using System.Collections.Concurrent;
using System.Net;
using System.Text;
using PhoneShell.Core.Networking;
using PhoneShell.Core.Models;
using PhoneShell.Core.Protocol;
using PhoneShell.Core.Services;
using PhoneShell.Core.Terminals;

namespace PhoneShell.Headless;

/// <summary>
/// Core orchestrator for the headless PhoneShell service.
/// Manages terminal sessions and relay networking without any GUI.
/// Each feature is conditionally started based on module config.
/// </summary>
public sealed class HeadlessHost : IDisposable
{
    private readonly HeadlessConfig _config;
    private readonly string _baseDirectory;
    private readonly string? _configPath;
    private readonly object _configSync = new();
    private readonly IShellLocator _shellLocator;
    private readonly DeviceIdentityStore _identityStore;
    private readonly GroupStore _groupStore;

    // Networking — conditionally initialized based on modules
    private RelayServer? _relayServer;
    private RelayClient? _relayClient;

    // Multi-session terminal management
    private readonly ConcurrentDictionary<string, ManagedSession> _sessions = new();
    private readonly ConcurrentDictionary<string, Task> _outputSendChains = new();
    private readonly ConcurrentDictionary<string, RawOutputBuffer> _rawOutputBuffers = new();
    private int _sessionCounter;

    /// <summary>
    /// Tracks a terminal session together with its shell metadata,
    /// so session lists returned to clients contain meaningful info.
    /// </summary>
    private sealed record ManagedSession(
        TerminalSessionManager Manager,
        string ShellId,
        string ShellDisplayName);

    private CancellationTokenSource? _cts;
    private bool _disposed;

    public string DeviceId { get; private set; } = string.Empty;

    public HeadlessHost(HeadlessConfig config, string baseDirectory, string? configPath = null)
    {
        _config = config;
        _baseDirectory = baseDirectory;
        _configPath = string.IsNullOrWhiteSpace(configPath) ? null : configPath;
        _shellLocator = TerminalPlatformFactory.CreateShellLocator();
        _identityStore = new DeviceIdentityStore(baseDirectory);
        _groupStore = new GroupStore(baseDirectory);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Load or create device identity
        var identity = _identityStore.LoadOrCreate();
        DeviceId = identity.DeviceId;
        var displayName = string.IsNullOrWhiteSpace(_config.DisplayName)
            ? identity.DisplayName
            : _config.DisplayName;

        var os = TerminalPlatformFactory.GetOsIdentifier();
        var shells = _config.Modules.Terminal
            ? _shellLocator.GetAvailableShells().Select(s => s.DisplayName).ToList()
            : new List<string>();

        Log($"PhoneShell Headless starting...");
        Log($"  Device: {displayName} ({DeviceId})");
        Log($"  OS: {os}");
        Log($"  Modules: {GetEnabledModulesString()}");
        Log($"  Relay authentication: {(string.IsNullOrWhiteSpace(_config.RelayAuthToken) && string.IsNullOrWhiteSpace(_config.GroupSecret) ? "disabled" : "enabled")}");
        if (!string.IsNullOrWhiteSpace(_config.GroupSecret))
            Log($"  Group secret: {_config.GroupSecret[..Math.Min(8, _config.GroupSecret.Length)]}...");

        if (_config.Modules.Terminal)
        {
            Log($"  Available shells: {string.Join(", ", shells)}");
            Log($"  Default shell: {_shellLocator.GetDefaultShell().DisplayName}");
        }

        // [MODULE: relay-server] — Start WebSocket relay hub
        if (_config.Modules.RelayServer)
        {
            // GroupSecret takes precedence over RelayAuthToken
            var effectiveToken = !string.IsNullOrWhiteSpace(_config.GroupSecret)
                ? _config.GroupSecret
                : _config.RelayAuthToken;

            _relayServer = new RelayServer
            {
                AuthToken = effectiveToken,
                WebPanelEnabled = _config.Modules.WebPanel
            };
            _relayServer.Log += msg => Log($"[relay-server] {msg}");
            _relayServer.LocalTerminalInputReceived += OnLocalTerminalInput;
            _relayServer.LocalTerminalResizeReceived += OnLocalTerminalResize;
            _relayServer.LocalTerminalSessionEnded += OnLocalTerminalSessionEnded;
            _relayServer.LocalTerminalOpenRequested += OnLocalTerminalOpenRequested;
            _relayServer.LocalSessionListProvider += GetSessionList;
            _relayServer.LocalTerminalSnapshotProvider = GetTerminalSnapshot;
            _relayServer.ServerMigrationCommitted += OnServerMigrationCommitted;

            // Register custom HTTP handlers for standalone mode APIs
            _relayServer.CustomHttpHandler = (context, path) =>
                HandleStandaloneHttpAsync(context, path, DeviceId, displayName, os, shells);

            await _relayServer.StartAsync(_config.Port, _baseDirectory, _cts.Token);

            // Always register the local device so the group is initialized,
            // even when running in relay-only mode.
            _relayServer.RegisterLocalDevice(DeviceId, displayName, os, shells);

            // Patch: add /panel/ and /api/ prefixes to HttpListener
            // The core DLL only registers /ws/ prefix, so /panel and /api paths
            // are rejected by HttpListener before reaching the WebPanelModule.
            if (_config.Modules.WebPanel)
            {
                try
                {
                    var listenerField = typeof(RelayServer).GetField("_httpListener",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (listenerField?.GetValue(_relayServer) is System.Net.HttpListener listener)
                    {
                        var existingPrefixes = listener.Prefixes.ToList();
                        foreach (var prefix in existingPrefixes)
                        {
                            // For each /ws/ prefix, add corresponding /panel/ and /api/ prefixes
                            var panelPrefix = prefix.Replace("/ws/", "/panel/");
                            var apiPrefix = prefix.Replace("/ws/", "/api/");
                            var rootPrefix = prefix.Replace("/ws/", "/");
                            listener.Prefixes.Add(panelPrefix);
                            listener.Prefixes.Add(apiPrefix);
                            Log($"[web-panel] Added HTTP prefix: {panelPrefix}");
                            Log($"[web-panel] Added HTTP prefix: {apiPrefix}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[web-panel] Warning: failed to patch HTTP prefixes: {ex.Message}");
                }
            }

            // Patch: override ReachableWebSocketUrls with public host when behind NAT
            if (!string.IsNullOrWhiteSpace(_config.PublicHost))
            {
                try
                {
                    PatchReachableUrls(_relayServer, _config.PublicHost, _config.Port);
                    Log($"[relay-server] Public host override: {_config.PublicHost}");
                }
                catch (Exception ex)
                {
                    Log($"[relay-server] Warning: failed to patch public host: {ex.Message}");
                }
            }

            Log($"Relay server listening on port {_config.Port}");
            if (string.IsNullOrWhiteSpace(effectiveToken))
                Log("WARNING: Relay server is running without a shared auth token or group secret.");

            // Log group info
            if (_relayServer.Group is not null)
            {
                Log($"Group ID: {_relayServer.Group.GroupId}");
                Log($"Group Secret: {_relayServer.Group.GroupSecret}");
                Log($"Share this Group Secret with other PCs to join the group.");
            }

            foreach (var wsUrl in _relayServer.ReachableWebSocketUrls)
            {
                Log($"Relay WebSocket: {wsUrl}");
                Log($"Relay health: {BuildHttpEndpointUrl(wsUrl, "healthz")}");
                Log($"Relay status: {BuildHttpEndpointUrl(wsUrl, "status")}");
            }

            // Standalone mode: display QR URL for phone to scan directly
            var isStandalone = string.IsNullOrWhiteSpace(_config.GroupSecret)
                            && string.IsNullOrWhiteSpace(_config.RelayUrl);
            if (isStandalone && _relayServer.ReachableWebSocketUrls.Any())
            {
                var wsUrl = _relayServer.ReachableWebSocketUrls.First();
                var httpUrl = wsUrl.Replace("ws://", "http://").Replace("wss://", "https://")
                    .TrimEnd('/').Replace("/ws", "");
                var qrBuilder = new QrPayloadBuilder();
                var standaloneQr = qrBuilder.BuildStandalone(httpUrl, wsUrl, DeviceId, displayName);
                Log("");
                Log("=== STANDALONE MODE ===");
                Log($"Scan this QR code with PhoneShell mobile app:");
                Log($"  {standaloneQr}");
                Log($"  API: {httpUrl}/api/standalone/info");
                Log("========================");
                Log("");
            }
        }

        // [MODULE: relay-client] — Connect to existing relay server
        if (_config.Modules.RelayClient && !string.IsNullOrWhiteSpace(_config.RelayUrl))
        {
            // GroupSecret takes precedence over RelayAuthToken
            var clientEffectiveToken = !string.IsNullOrWhiteSpace(_config.GroupSecret)
                ? _config.GroupSecret
                : _config.RelayAuthToken;

            _relayClient = new RelayClient
            {
                DeviceId = DeviceId,
                DisplayName = displayName,
                Os = os,
                AvailableShells = shells,
                AuthToken = clientEffectiveToken,
                GroupSecret = _config.GroupSecret
            };
            _relayClient.Log += msg => Log($"[relay-client] {msg}");
            _relayClient.TerminalInputReceived += OnRelayClientTerminalInput;
            _relayClient.TerminalOpenRequested += OnRelayClientTerminalOpen;
            _relayClient.TerminalResizeRequested += OnRelayClientTerminalResize;
            _relayClient.TerminalCloseRequested += OnRelayClientTerminalClose;
            _relayClient.GroupJoined += accepted =>
                Log($"[relay-client] Joined group {accepted.GroupId} ({accepted.Members.Count} members)");
            _relayClient.GroupJoinRejected += reason =>
                Log($"[relay-client] Group join rejected: {reason}");
            _relayClient.DeviceUnbound += OnRelayClientDeviceUnbound;

            // Handle server migration: this device is selected as the new server
            _relayClient.ServerChangeRequested += async (groupId, groupSecret) =>
            {
                Log($"[relay-client] Server migration: this device selected as new server");
                try
                {
                    // Persist group info so the new server keeps existing group ID/secret.
                    _groupStore.SaveGroup(new GroupInfo
                    {
                        GroupId = groupId,
                        GroupSecret = groupSecret,
                        ServerDeviceId = DeviceId,
                        CreatedAt = DateTimeOffset.UtcNow
                    });

                    // Start relay server (keep client connected until commit)
                    if (_relayServer is null || !_relayServer.IsRunning)
                    {
                        _relayServer = new RelayServer
                        {
                            AuthToken = groupSecret,
                            WebPanelEnabled = _config.Modules.WebPanel
                        };
                        _relayServer.Log += msg => Log($"[relay-server] {msg}");
                        _relayServer.LocalTerminalInputReceived += OnLocalTerminalInput;
                        _relayServer.LocalTerminalResizeReceived += OnLocalTerminalResize;
                        _relayServer.LocalTerminalSessionEnded += OnLocalTerminalSessionEnded;
                        _relayServer.LocalTerminalOpenRequested += OnLocalTerminalOpenRequested;
                        _relayServer.LocalSessionListProvider += GetSessionList;
                        _relayServer.ServerMigrationCommitted += OnServerMigrationCommitted;

                        var allShells = _config.Modules.Terminal
                            ? _shellLocator.GetAvailableShells().Select(s => s.DisplayName).ToList()
                            : new List<string>();
                        var identity = _identityStore.LoadOrCreate();
                        _relayServer.RegisterLocalDevice(DeviceId,
                            string.IsNullOrWhiteSpace(_config.DisplayName) ? identity.DisplayName : _config.DisplayName,
                            TerminalPlatformFactory.GetOsIdentifier(), allShells);

                        await _relayServer.StartAsync(_config.Port, _baseDirectory, _cts?.Token ?? CancellationToken.None);
                        Log($"[relay-server] Server role activated on port {_config.Port}");
                    }

                    UpdateConfig(cfg =>
                    {
                        cfg.Modules.RelayServer = true;
                        cfg.Modules.RelayClient = false;
                        cfg.GroupSecret = groupSecret;
                        cfg.RelayUrl = string.Empty;
                    });

                    var newServerUrl = _relayServer.ReachableWebSocketUrls.FirstOrDefault() ??
                                       $"ws://localhost:{_config.Port}/ws/";
                    await _relayClient.SendServerChangePrepareAsync(groupId, groupSecret, newServerUrl);
                    Log($"[relay-client] Server migration prepare sent: {newServerUrl}");
                }
                catch (Exception ex)
                {
                    Log($"[relay-client] Server migration failed: {ex.Message}");
                }
            };

            // Handle server changed: reconnect to new server
            _relayClient.ServerChanged += (newUrl, newSecret) =>
            {
                Log($"[relay-client] Server changed, reconnecting to {newUrl}");
                _ = Task.Run(async () =>
                {
                    // If we are the new server, drop client connection.
                    if (_relayServer is not null && _relayServer.IsRunning)
                    {
                        _relayClient?.Disconnect();
                        _relayClient?.Dispose();
                        _relayClient = null;
                        UpdateConfig(cfg =>
                        {
                            cfg.Modules.RelayServer = true;
                            cfg.Modules.RelayClient = false;
                            cfg.GroupSecret = newSecret;
                            cfg.RelayUrl = string.Empty;
                        });
                        return;
                    }

                    _relayClient?.Disconnect();
                    UpdateConfig(cfg =>
                    {
                        cfg.Modules.RelayServer = false;
                        cfg.Modules.RelayClient = true;
                        cfg.RelayUrl = newUrl;
                        cfg.GroupSecret = newSecret;
                    });
                    _relayClient!.GroupSecret = newSecret;
                    await _relayClient.ConnectAsync(newUrl, _cts?.Token ?? CancellationToken.None);
                });
            };

            // Handle secret rotation
            _relayClient.GroupSecretRotated += newSecret =>
            {
                UpdateConfig(cfg => cfg.GroupSecret = newSecret);
                Log($"[relay-client] Group secret updated to {newSecret[..Math.Min(8, newSecret.Length)]}...");
            };

            if (string.IsNullOrWhiteSpace(clientEffectiveToken))
                Log("WARNING: Relay client is connecting without a shared auth token or group secret.");

            await _relayClient.ConnectAsync(_config.RelayUrl, _cts.Token);
            Log($"Connected to relay server at {_config.RelayUrl}");
        }

        // [MODULE: web-panel] — Web management UI
        if (_config.Modules.WebPanel)
        {
            if (_config.Modules.RelayServer && _relayServer is not null)
            {
                foreach (var wsUrl in _relayServer.ReachableWebSocketUrls)
                {
                    var panelUrl = BuildPanelUrl(wsUrl);
                    Log($"[web-panel] Web panel: {panelUrl}");
                }
            }
            else
            {
                Log("[web-panel] Module enabled but relay-server is not active. Web panel requires relay-server module.");
            }
        }

        // [MODULE: ai-chat] — TODO: Future implementation
        if (_config.Modules.AiChat)
        {
            Log("[ai-chat] Module enabled but not yet implemented. Will provide AI terminal assistant in future version.");
        }

        Log("PhoneShell Headless started. Press Ctrl+C to stop.");
    }

    public void Stop()
    {
        Log("Stopping PhoneShell Headless...");
        _cts?.Cancel();

        // Dispose all terminal sessions
        foreach (var (sessionId, managed) in _sessions)
        {
            try { managed.Manager.Dispose(); }
            catch (Exception ex) { Log($"Error disposing session {sessionId}: {ex.Message}"); }
        }
        _sessions.Clear();
        _outputSendChains.Clear();
        _rawOutputBuffers.Clear();

        _relayClient?.Dispose();
        _relayClient = null;

        if (_relayServer is not null)
        {
            _relayServer.ServerMigrationCommitted -= OnServerMigrationCommitted;
            _relayServer.Dispose();
        }
        _relayServer = null;

        Log("PhoneShell Headless stopped.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts?.Dispose();
    }

    // --- Terminal session management ---

    private (string SessionId, TerminalSessionManager Manager) CreateTerminalSession(string shellId)
    {
        if (!_config.Modules.Terminal)
            throw new InvalidOperationException("Terminal module is disabled.");

        var shell = FindShell(shellId);
        var session = TerminalPlatformFactory.CreateSession();
        var manager = new TerminalSessionManager();
        var sessionId = $"session-{Interlocked.Increment(ref _sessionCounter)}";

        manager.Start(session, shell, _config.DefaultCols, _config.DefaultRows);
        _sessions[sessionId] = new ManagedSession(manager, shell.Id, shell.DisplayName);

        Log($"Terminal session created: {sessionId} (shell: {shell.DisplayName})");
        return (sessionId, manager);
    }

    private ShellInfo FindShell(string shellId)
    {
        if (string.IsNullOrWhiteSpace(shellId))
            return _shellLocator.GetDefaultShell();

        var shells = _shellLocator.GetAvailableShells();
        return shells.FirstOrDefault(s =>
            string.Equals(s.Id, shellId, StringComparison.OrdinalIgnoreCase)) ??
            _shellLocator.GetDefaultShell();
    }

    private List<SessionInfo> GetSessionList()
    {
        return _sessions.Select(kvp => new SessionInfo
        {
            SessionId = kvp.Key,
            ShellId = kvp.Value.ShellId,
            Title = kvp.Value.ShellDisplayName
        }).ToList();
    }

    // --- Relay Server event handlers (when this node is the hub) ---

    private Task<(string SessionId, int Cols, int Rows)> OnLocalTerminalOpenRequested(
        string deviceId, string shellId)
    {
        var (sessionId, manager) = CreateTerminalSession(shellId);

        // Create raw output buffer for snapshot support
        var rawBuffer = new RawOutputBuffer();
        _rawOutputBuffers[sessionId] = rawBuffer;

        // Wire output to buffer + relay broadcast
        manager.OutputReceived += data =>
        {
            rawBuffer.Append(data);
            EnqueueOutput(sessionId, async () =>
            {
                if (_relayServer is not null)
                    await _relayServer.BroadcastLocalTerminalOutputAsync(deviceId, sessionId, data);
            });
        };

        return Task.FromResult((sessionId, _config.DefaultCols, _config.DefaultRows));
    }

    private void OnLocalTerminalInput(string sessionId, string data)
    {
        if (_sessions.TryGetValue(sessionId, out var managed))
            managed.Manager.WriteInput(data);
    }

    private void OnLocalTerminalResize(string sessionId, int cols, int rows)
    {
        if (_sessions.TryGetValue(sessionId, out var managed))
            managed.Manager.Resize(cols, rows);
    }

    private void OnLocalTerminalSessionEnded(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var managed))
        {
            managed.Manager.Dispose();
            RemoveOutputChain(sessionId);
            _rawOutputBuffers.TryRemove(sessionId, out _);
            Log($"Terminal session ended: {sessionId}");
        }
    }

    // --- Relay Client event handlers (when this node connects to a hub) ---

    private void OnRelayClientTerminalOpen(string deviceId, string shellId)
    {
        try
        {
            var (sessionId, manager) = CreateTerminalSession(shellId);

            // Wire output to relay client
            manager.OutputReceived += data =>
            {
                EnqueueOutput(sessionId, async () =>
                {
                    if (_relayClient is not null)
                        await _relayClient.SendTerminalOutputAsync(DeviceId, sessionId, data);
                });
            };

            // Notify relay that session is opened
            _ = Task.Run(async () =>
            {
                if (_relayClient is not null)
                    await _relayClient.SendTerminalOpenedAsync(
                        DeviceId, sessionId, _config.DefaultCols, _config.DefaultRows);
            });
        }
        catch (Exception ex)
        {
            Log($"Failed to open terminal: {ex.Message}");
        }
    }

    private void OnRelayClientTerminalInput(string sessionId, string data)
    {
        if (_sessions.TryGetValue(sessionId, out var managed))
            managed.Manager.WriteInput(data);
    }

    private void OnRelayClientTerminalResize(string sessionId, int cols, int rows)
    {
        if (_sessions.TryGetValue(sessionId, out var managed))
            managed.Manager.Resize(cols, rows);
    }

    private void OnRelayClientTerminalClose(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var managed))
        {
            managed.Manager.Dispose();
            RemoveOutputChain(sessionId);
            Log($"Terminal session closed by remote: {sessionId}");
        }
    }

    private void OnRelayClientDeviceUnbound()
    {
        Log("[relay-client] Device unbound by mobile. Clearing group secret and disconnecting.");

        try
        {
            _groupStore.ClearMembership();
        }
        catch (Exception ex)
        {
            Log($"[relay-client] Clear membership failed: {ex.Message}");
        }

        UpdateConfig(cfg => cfg.GroupSecret = string.Empty);
        _relayClient?.Disconnect();
    }

    private void OnServerMigrationCommitted(string newUrl, string newSecret)
    {
        Log($"[relay-server] Server migration committed. Switching to client: {newUrl}");
        _ = Task.Run(async () =>
        {
            try
            {
                if (_relayServer is not null)
                {
                    _relayServer.ServerMigrationCommitted -= OnServerMigrationCommitted;
                    _relayServer.Dispose();
                    _relayServer = null;
                }

                _groupStore.ClearGroup();

                UpdateConfig(cfg =>
                {
                    cfg.Modules.RelayServer = false;
                    cfg.Modules.RelayClient = true;
                    cfg.RelayUrl = newUrl;
                    cfg.GroupSecret = newSecret;
                });

                var shells = _config.Modules.Terminal
                    ? _shellLocator.GetAvailableShells().Select(s => s.DisplayName).ToList()
                    : new List<string>();

                _relayClient?.Dispose();
                _relayClient = new RelayClient
                {
                    DeviceId = DeviceId,
                    DisplayName = string.IsNullOrWhiteSpace(_config.DisplayName)
                        ? _identityStore.LoadOrCreate().DisplayName
                        : _config.DisplayName,
                    Os = TerminalPlatformFactory.GetOsIdentifier(),
                    AvailableShells = shells,
                    GroupSecret = newSecret
                };
                _relayClient.Log += msg => Log($"[relay-client] {msg}");
                _relayClient.TerminalInputReceived += OnRelayClientTerminalInput;
                _relayClient.TerminalOpenRequested += OnRelayClientTerminalOpen;
                _relayClient.TerminalResizeRequested += OnRelayClientTerminalResize;
                _relayClient.TerminalCloseRequested += OnRelayClientTerminalClose;
                _relayClient.GroupJoined += accepted =>
                    Log($"[relay-client] Joined group {accepted.GroupId} ({accepted.Members.Count} members)");
                _relayClient.GroupJoinRejected += reason =>
                    Log($"[relay-client] Group join rejected: {reason}");
                _relayClient.DeviceUnbound += OnRelayClientDeviceUnbound;
                _relayClient.GroupSecretRotated += secret =>
                {
                    UpdateConfig(cfg => cfg.GroupSecret = secret);
                    Log($"[relay-client] Group secret updated to {secret[..Math.Min(8, secret.Length)]}...");
                };

                _relayClient.ServerChangeRequested += async (groupId, groupSecret) =>
                {
                    Log($"[relay-client] Server migration: this device selected as new server");
                    try
                    {
                        _groupStore.SaveGroup(new GroupInfo
                        {
                            GroupId = groupId,
                            GroupSecret = groupSecret,
                            ServerDeviceId = DeviceId,
                            CreatedAt = DateTimeOffset.UtcNow
                        });

                        if (_relayServer is null || !_relayServer.IsRunning)
                        {
                            _relayServer = new RelayServer
                            {
                                AuthToken = groupSecret,
                                WebPanelEnabled = _config.Modules.WebPanel
                            };
                            _relayServer.Log += msg => Log($"[relay-server] {msg}");
                            _relayServer.LocalTerminalInputReceived += OnLocalTerminalInput;
                            _relayServer.LocalTerminalResizeReceived += OnLocalTerminalResize;
                            _relayServer.LocalTerminalSessionEnded += OnLocalTerminalSessionEnded;
                            _relayServer.LocalTerminalOpenRequested += OnLocalTerminalOpenRequested;
                            _relayServer.LocalSessionListProvider += GetSessionList;
                            _relayServer.ServerMigrationCommitted += OnServerMigrationCommitted;

                            var allShells = _config.Modules.Terminal
                                ? _shellLocator.GetAvailableShells().Select(s => s.DisplayName).ToList()
                                : new List<string>();
                            var identity = _identityStore.LoadOrCreate();
                            _relayServer.RegisterLocalDevice(DeviceId,
                                string.IsNullOrWhiteSpace(_config.DisplayName) ? identity.DisplayName : _config.DisplayName,
                                TerminalPlatformFactory.GetOsIdentifier(), allShells);

                            await _relayServer.StartAsync(_config.Port, _baseDirectory, _cts?.Token ?? CancellationToken.None);
                            Log($"[relay-server] Server role activated on port {_config.Port}");
                        }

                        UpdateConfig(cfg =>
                        {
                            cfg.Modules.RelayServer = true;
                            cfg.Modules.RelayClient = false;
                            cfg.GroupSecret = groupSecret;
                            cfg.RelayUrl = string.Empty;
                        });

                        var newServerUrl = _relayServer.ReachableWebSocketUrls.FirstOrDefault() ??
                                           $"ws://localhost:{_config.Port}/ws/";
                        await _relayClient.SendServerChangePrepareAsync(groupId, groupSecret, newServerUrl);
                        Log($"[relay-client] Server migration prepare sent: {newServerUrl}");
                    }
                    catch (Exception ex)
                    {
                        Log($"[relay-client] Server migration failed: {ex.Message}");
                    }
                };

                _relayClient.ServerChanged += (url, secret) =>
                {
                    Log($"[relay-client] Server changed, reconnecting to {url}");
                    _ = Task.Run(async () =>
                    {
                        if (_relayServer is not null && _relayServer.IsRunning)
                        {
                            _relayClient?.Disconnect();
                            _relayClient?.Dispose();
                            _relayClient = null;
                            UpdateConfig(cfg =>
                            {
                                cfg.Modules.RelayServer = true;
                                cfg.Modules.RelayClient = false;
                                cfg.GroupSecret = secret;
                                cfg.RelayUrl = string.Empty;
                            });
                            return;
                        }

                        _relayClient?.Disconnect();
                        UpdateConfig(cfg =>
                        {
                            cfg.Modules.RelayServer = false;
                            cfg.Modules.RelayClient = true;
                            cfg.RelayUrl = url;
                            cfg.GroupSecret = secret;
                        });
                        _relayClient!.GroupSecret = secret;
                        await _relayClient.ConnectAsync(url, _cts?.Token ?? CancellationToken.None);
                    });
                };

                await _relayClient.ConnectAsync(newUrl, _cts?.Token ?? CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log($"[relay-server] Migration switch failed: {ex.Message}");
            }
        });
    }

    // --- Config persistence ---

    private void UpdateConfig(Action<HeadlessConfig> update)
    {
        if (_configPath is null)
        {
            update(_config);
            return;
        }

        lock (_configSync)
        {
            update(_config);
            try
            {
                _config.Save(_configPath);
            }
            catch (Exception ex)
            {
                Log($"[config] Warning: failed to save config: {ex.Message}");
            }
        }
    }

    // --- Output ordering ---

    private void EnqueueOutput(string sessionId, Func<Task> sendAsync)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        _outputSendChains.AddOrUpdate(sessionId,
            _ => SafeSendAsync(sendAsync),
            (_, previous) => previous.ContinueWith(_ => SafeSendAsync(sendAsync), TaskScheduler.Default).Unwrap());
    }

    private async Task SafeSendAsync(Func<Task> sendAsync)
    {
        try
        {
            await sendAsync();
        }
        catch (Exception ex)
        {
            Log($"Output send failed: {ex.Message}");
        }
    }

    private void RemoveOutputChain(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        _outputSendChains.TryRemove(sessionId, out _);
    }

    // --- Logging ---

    private string GetEnabledModulesString()
    {
        var modules = new List<string>();
        if (_config.Modules.Terminal) modules.Add("terminal");
        if (_config.Modules.RelayServer) modules.Add("relay-server");
        if (_config.Modules.RelayClient) modules.Add("relay-client");
        if (_config.Modules.WebPanel) modules.Add("web-panel");
        if (_config.Modules.AiChat) modules.Add("ai-chat");
        return modules.Count > 0 ? string.Join(", ", modules) : "(none)";
    }

    private static void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        Console.WriteLine($"[{timestamp}] {message}");
    }

    private static string BuildHttpEndpointUrl(string webSocketUrl, string endpointName)
    {
        var uri = new Uri(webSocketUrl);
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ? "https" : "http",
            Path = $"/ws/{endpointName.TrimStart('/')}"
        };
        return builder.Uri.AbsoluteUri;
    }

    private static string BuildPanelUrl(string webSocketUrl)
    {
        var uri = new Uri(webSocketUrl);
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ? "https" : "http",
            Path = "/panel/"
        };
        return builder.Uri.AbsoluteUri;
    }

    private static string BuildPublicWebSocketUrl(string publicHost, int port)
    {
        var trimmed = publicHost.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return string.Empty;

        // If caller provided a full URI, normalize to ws/wss and force /ws/ path.
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
        {
            var scheme = absolute.Scheme.ToLowerInvariant();
            var wsScheme = scheme switch
            {
                "http" => "ws",
                "https" => "wss",
                "ws" => "ws",
                "wss" => "wss",
                _ => "ws"
            };

            var builder = new UriBuilder(absolute)
            {
                Scheme = wsScheme,
                Path = "/ws/",
                Query = string.Empty,
                Fragment = string.Empty
            };
            return builder.Uri.AbsoluteUri;
        }

        // Treat as host[:port] (supports IPv6). If port not provided, use config port.
        var host = NormalizeHostForWebSocket(trimmed, port);
        return $"ws://{host}/ws/";
    }

    private static string NormalizeHostForWebSocket(string host, int port)
    {
        var trimmed = host.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return host;

        var hasPort = HostHasPort(trimmed);
        var normalized = NormalizeBracketedHost(trimmed);
        if (!hasPort)
            return $"{normalized}:{port}";

        return normalized;
    }

    private static bool HostHasPort(string host)
    {
        if (host.StartsWith("["))
        {
            var end = host.IndexOf(']');
            return end >= 0 && end + 1 < host.Length && host[end + 1] == ':';
        }

        var firstColon = host.IndexOf(':');
        if (firstColon < 0)
            return false;

        // Single colon => host:port, multiple colons => IPv6 without brackets.
        return host.IndexOf(':', firstColon + 1) < 0;
    }

    private static string NormalizeBracketedHost(string host)
    {
        if (host.StartsWith("["))
            return host;

        var firstColon = host.IndexOf(':');
        if (firstColon < 0)
            return host;

        // Multiple colons => IPv6, wrap in brackets.
        return host.IndexOf(':', firstColon + 1) >= 0 ? $"[{host}]" : host;
    }

    /// <summary>
    /// Use reflection to replace auto-detected internal URLs with public host address.
    /// This fixes QR codes and advertised URLs when running behind NAT.
    /// </summary>
    private static void PatchReachableUrls(RelayServer server, string publicHost, int port)
    {
        var publicUrl = BuildPublicWebSocketUrl(publicHost, port);
        if (string.IsNullOrWhiteSpace(publicUrl))
            return;

        // Try to find and replace the backing list/field for ReachableWebSocketUrls
        var serverType = typeof(RelayServer);
        var prop = serverType.GetProperty("ReachableWebSocketUrls");
        if (prop is null) return;

        var currentUrls = prop.GetValue(server);
        if (currentUrls is IList<string> urlList)
        {
            urlList.Clear();
            urlList.Add(publicUrl);
            return;
        }

        // Fallback: try to find and replace the backing field directly
        var fields = serverType.GetFields(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        foreach (var field in fields)
        {
            if (!field.Name.Contains("reachableWebSocketUrl", StringComparison.OrdinalIgnoreCase) &&
                !field.Name.Contains("ReachableWebSocketUrl", StringComparison.OrdinalIgnoreCase))
                continue;

            var val = field.GetValue(server);
            if (val is IList<string> list)
            {
                list.Clear();
                list.Add(publicUrl);
                return;
            }

            if (field.FieldType == typeof(IReadOnlyList<string>) ||
                field.FieldType == typeof(IEnumerable<string>) ||
                field.FieldType == typeof(List<string>))
            {
                field.SetValue(server, new List<string> { publicUrl });
                return;
            }
        }
    }

    // --- Terminal snapshot support ---

    private Task<string> GetTerminalSnapshot(string sessionId)
    {
        if (_rawOutputBuffers.TryGetValue(sessionId, out var buffer))
            return Task.FromResult(buffer.GetSnapshot());
        return Task.FromResult(string.Empty);
    }

    // --- Standalone mode HTTP handlers ---

    /// <summary>
    /// Handles /api/invite and /api/standalone/* endpoints for standalone mode.
    /// </summary>
    private async Task<bool> HandleStandaloneHttpAsync(
        HttpListenerContext context, string path,
        string deviceId, string displayName, string os, List<string> shells)
    {
        // POST /api/invite — receive invite to join a group (standalone → client transition)
        if (path == "/api/invite" && context.Request.HttpMethod == "POST")
        {
            try
            {
                using var reader = new System.IO.StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                var invite = System.Text.Json.JsonSerializer.Deserialize<InvitePayload>(body, _jsonOptions);

                if (invite is null || string.IsNullOrWhiteSpace(invite.RelayUrl) ||
                    string.IsNullOrWhiteSpace(invite.InviteCode))
                {
                    await WriteStandaloneJsonAsync(context.Response, 400, new
                    {
                        type = "error", code = "bad_request",
                        message = "relayUrl and inviteCode are required."
                    });
                    return true;
                }

                Log($"[standalone] Received invite: relay={invite.RelayUrl} code={invite.InviteCode}");

                // Transition from standalone to client mode
                _ = Task.Run(() => TransitionToClient(invite.RelayUrl, invite.InviteCode,
                    invite.GroupId ?? "", deviceId, displayName, os, shells));

                await WriteStandaloneJsonAsync(context.Response, 200, new
                {
                    status = "accepted", relayUrl = invite.RelayUrl
                });
            }
            catch (Exception ex)
            {
                Log($"[standalone] Invite error: {ex.Message}");
                await WriteStandaloneJsonAsync(context.Response, 400, new
                {
                    type = "error", code = "bad_request", message = "Invalid JSON body."
                });
            }
            return true;
        }

        // GET /api/standalone/info — return device info for phone connection
        if (path == "/api/standalone/info" && context.Request.HttpMethod == "GET")
        {
            var wsUrl = _relayServer?.ReachableWebSocketUrls.FirstOrDefault() ?? "";
            var httpUrl = wsUrl.Replace("ws://", "http://").Replace("wss://", "https://")
                .TrimEnd('/').Replace("/ws", "");

            await WriteStandaloneJsonAsync(context.Response, 200, new
            {
                deviceId, displayName, os, availableShells = shells,
                httpUrl, wsUrl
            });
            return true;
        }

        return false;
    }

    /// <summary>
    /// Transition this device from standalone/server mode to client mode.
    /// Stops the relay server and connects to the specified relay URL.
    /// </summary>
    private async void TransitionToClient(
        string relayUrl, string inviteCode, string groupId,
        string deviceId, string displayName, string os, List<string> shells)
    {
        try
        {
            Log($"[standalone] Transitioning to client mode: {relayUrl}");

            // Stop current relay server
            if (_relayServer is not null)
            {
                _relayServer.ServerMigrationCommitted -= OnServerMigrationCommitted;
                _relayServer.Dispose();
                _relayServer = null;
            }

            // Update config
            UpdateConfig(cfg =>
            {
                cfg.Modules.RelayServer = false;
                cfg.Modules.RelayClient = true;
                cfg.RelayUrl = relayUrl;
            });

            // Create relay client and join with invite code
            _relayClient?.Dispose();
            _relayClient = new RelayClient
            {
                DeviceId = deviceId,
                DisplayName = displayName,
                Os = os,
                AvailableShells = shells
            };
            _relayClient.Log += msg => Log($"[relay-client] {msg}");
            _relayClient.TerminalInputReceived += OnRelayClientTerminalInput;
            _relayClient.TerminalOpenRequested += OnRelayClientTerminalOpen;
            _relayClient.TerminalResizeRequested += OnRelayClientTerminalResize;
            _relayClient.TerminalCloseRequested += OnRelayClientTerminalClose;
            _relayClient.GroupJoined += accepted =>
            {
                Log($"[relay-client] Joined group {accepted.GroupId} via invite");
                UpdateConfig(cfg => cfg.GroupSecret = _relayClient.GroupSecret);
            };
            _relayClient.GroupJoinRejected += reason =>
                Log($"[relay-client] Group join rejected: {reason}");
            _relayClient.DeviceUnbound += OnRelayClientDeviceUnbound;
            _relayClient.DeviceKicked += reason =>
            {
                Log($"[relay-client] Kicked from group: {reason}");
                TransitionBackToStandalone(deviceId, displayName, os, shells);
            };
            _relayClient.GroupDissolved += reason =>
            {
                Log($"[relay-client] Group dissolved: {reason}");
                TransitionBackToStandalone(deviceId, displayName, os, shells);
            };

            await _relayClient.ConnectAsync(relayUrl, _cts?.Token ?? CancellationToken.None);

            // Send group join with invite code
            await _relayClient.SendGroupJoinWithInviteAsync(inviteCode);

            Log($"[standalone] Transition to client complete");
        }
        catch (Exception ex)
        {
            Log($"[standalone] Transition to client failed: {ex.Message}");
            // Try to recover back to standalone
            TransitionBackToStandalone(deviceId, displayName, os, shells);
        }
    }

    /// <summary>
    /// Transition back to standalone mode after being kicked/dissolved/error.
    /// </summary>
    private async void TransitionBackToStandalone(
        string deviceId, string displayName, string os, List<string> shells)
    {
        try
        {
            Log("[standalone] Transitioning back to standalone mode");

            _relayClient?.Dispose();
            _relayClient = null;

            UpdateConfig(cfg =>
            {
                cfg.Modules.RelayServer = true;
                cfg.Modules.RelayClient = false;
                cfg.RelayUrl = string.Empty;
                cfg.GroupSecret = string.Empty;
            });

            // Restart relay server
            var effectiveToken = !string.IsNullOrWhiteSpace(_config.GroupSecret)
                ? _config.GroupSecret
                : _config.RelayAuthToken;

            _relayServer = new RelayServer
            {
                AuthToken = effectiveToken,
                WebPanelEnabled = _config.Modules.WebPanel
            };
            _relayServer.Log += msg => Log($"[relay-server] {msg}");
            _relayServer.LocalTerminalInputReceived += OnLocalTerminalInput;
            _relayServer.LocalTerminalResizeReceived += OnLocalTerminalResize;
            _relayServer.LocalTerminalSessionEnded += OnLocalTerminalSessionEnded;
            _relayServer.LocalTerminalOpenRequested += OnLocalTerminalOpenRequested;
            _relayServer.LocalSessionListProvider += GetSessionList;
            _relayServer.LocalTerminalSnapshotProvider = GetTerminalSnapshot;
            _relayServer.ServerMigrationCommitted += OnServerMigrationCommitted;
            _relayServer.CustomHttpHandler = (context, path) =>
                HandleStandaloneHttpAsync(context, path, deviceId, displayName, os, shells);
            _relayServer.RegisterLocalDevice(deviceId, displayName, os, shells);

            await _relayServer.StartAsync(_config.Port, _baseDirectory, _cts?.Token ?? CancellationToken.None);
            Log($"[standalone] Relay server restarted on port {_config.Port}");
        }
        catch (Exception ex)
        {
            Log($"[standalone] Failed to restart in standalone mode: {ex.Message}");
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed class InvitePayload
    {
        public string RelayUrl { get; set; } = string.Empty;
        public string InviteCode { get; set; } = string.Empty;
        public string? GroupId { get; set; }
    }

    private static async Task WriteStandaloneJsonAsync(HttpListenerResponse response, int statusCode, object data)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(data, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.AddHeader("Access-Control-Allow-Origin", "*");
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    /// <summary>
    /// Ring buffer that keeps the most recent raw terminal output (with ANSI codes)
    /// so that clients re-subscribing to a session can receive a snapshot.
    /// </summary>
    private sealed class RawOutputBuffer
    {
        private readonly object _lock = new();
        private readonly Queue<string> _chunks = new();
        private int _totalLength;
        private const int MaxLength = 65536; // 64 KB

        public void Append(string data)
        {
            if (string.IsNullOrEmpty(data)) return;
            lock (_lock)
            {
                _chunks.Enqueue(data);
                _totalLength += data.Length;
                while (_totalLength > MaxLength && _chunks.Count > 1)
                {
                    var old = _chunks.Dequeue();
                    _totalLength -= old.Length;
                }
            }
        }

        public string GetSnapshot()
        {
            lock (_lock)
            {
                if (_chunks.Count == 0) return string.Empty;
                return string.Concat(_chunks);
            }
        }
    }
}
