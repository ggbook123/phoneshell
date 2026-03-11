using System.Collections.Concurrent;
using System.Text;
using PhoneShell.Core.Networking;
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
    private readonly IShellLocator _shellLocator;
    private readonly DeviceIdentityStore _identityStore;

    // Networking — conditionally initialized based on modules
    private RelayServer? _relayServer;
    private RelayClient? _relayClient;

    // Multi-session terminal management
    private readonly ConcurrentDictionary<string, ManagedSession> _sessions = new();
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

    public HeadlessHost(HeadlessConfig config, string baseDirectory)
    {
        _config = config;
        _baseDirectory = baseDirectory;
        _shellLocator = TerminalPlatformFactory.CreateShellLocator();
        _identityStore = new DeviceIdentityStore(baseDirectory);
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

            if (_config.Modules.Terminal)
            {
                _relayServer.RegisterLocalDevice(DeviceId, displayName, os, shells);
            }

            await _relayServer.StartAsync(_config.Port, _baseDirectory, _cts.Token);

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

            // Handle server migration: this device is selected as the new server
            _relayClient.ServerChangeRequested += async (groupId, groupSecret) =>
            {
                Log($"[relay-client] Server migration: this device selected as new server");
                try
                {
                    // Disconnect client
                    _relayClient?.Disconnect();

                    // Start relay server
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

                    var allShells = _config.Modules.Terminal
                        ? _shellLocator.GetAvailableShells().Select(s => s.DisplayName).ToList()
                        : new List<string>();
                    var identity = _identityStore.LoadOrCreate();
                    _relayServer.RegisterLocalDevice(DeviceId,
                        string.IsNullOrWhiteSpace(_config.DisplayName) ? identity.DisplayName : _config.DisplayName,
                        TerminalPlatformFactory.GetOsIdentifier(), allShells);

                    await _relayServer.StartAsync(_config.Port, _baseDirectory, _cts?.Token ?? CancellationToken.None);
                    Log($"[relay-server] Server role activated on port {_config.Port}");

                    // Send prepare response back via relay (the old server will forward commit)
                    var newServerUrl = _relayServer.ReachableWebSocketUrls.FirstOrDefault() ?? $"ws://localhost:{_config.Port}/ws/";
                    // Note: the old server receives the prepare message and broadcasts commit
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
                    _relayClient?.Disconnect();
                    _relayClient!.GroupSecret = newSecret;
                    await _relayClient.ConnectAsync(newUrl, _cts?.Token ?? CancellationToken.None);
                });
            };

            // Handle secret rotation
            _relayClient.GroupSecretRotated += newSecret =>
            {
                _config.GroupSecret = newSecret;
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

        _relayClient?.Dispose();
        _relayClient = null;

        _relayServer?.Dispose();
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

    private async Task<(string SessionId, int Cols, int Rows)> OnLocalTerminalOpenRequested(
        string deviceId, string shellId)
    {
        var (sessionId, manager) = CreateTerminalSession(shellId);

        // Wire output to relay broadcast
        manager.OutputReceived += data =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_relayServer is not null)
                        await _relayServer.BroadcastLocalTerminalOutputAsync(deviceId, sessionId, data);
                }
                catch (Exception ex)
                {
                    Log($"Error broadcasting output for {sessionId}: {ex.Message}");
                }
            });
        };

        return (sessionId, _config.DefaultCols, _config.DefaultRows);
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
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_relayClient is not null)
                            await _relayClient.SendTerminalOutputAsync(DeviceId, sessionId, data);
                    }
                    catch (Exception ex)
                    {
                        Log($"Error sending output for {sessionId}: {ex.Message}");
                    }
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
            Log($"Terminal session closed by remote: {sessionId}");
        }
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
            Path = "/panel"
        };
        return builder.Uri.AbsoluteUri;
    }
}
