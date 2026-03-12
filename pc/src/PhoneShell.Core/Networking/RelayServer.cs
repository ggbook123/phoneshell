using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhoneShell.Core.Models;
using PhoneShell.Core.Protocol;
using PhoneShell.Core.Services;

namespace PhoneShell.Core.Networking;

/// <summary>
/// WebSocket relay server that accepts connections from PC clients and mobile clients.
/// Maintains a registry of connected devices and forwards terminal I/O between them.
/// Manages group membership and authentication via shared group secret.
/// </summary>
public sealed class RelayServer : IDisposable
{
    private HttpListener? _httpListener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, ConnectedDevice> _devices = new();
    private readonly ConcurrentDictionary<string, ConnectedClient> _clients = new();
    private readonly ConcurrentDictionary<string, PendingAuth> _pendingAuths = new();
    private readonly ConcurrentDictionary<string, PanelLoginSession> _panelLoginSessions = new();
    private readonly ConcurrentDictionary<string, PanelAccessToken> _panelAccessTokens = new();
    private readonly TimeSpan _panelLoginTtl = TimeSpan.FromMinutes(10);
    private readonly TimeSpan _panelTokenTtl = TimeSpan.FromDays(365);
    private readonly List<string> _listenPrefixes = new();
    private readonly List<string> _reachableWebSocketUrls = new();
    private int _clientIdCounter;
    private bool _disposed;
    private DateTimeOffset _startedAtUtc;
    private WebPanelModule? _webPanelModule;
    private GroupInfo? _group;
    private GroupStore? _groupStore;

    public event Action<string>? Log;
    public event Action<List<DeviceInfo>>? DeviceListChanged;
    public event Action<List<GroupMemberInfo>>? GroupMemberListChanged;
    public Func<string, Task<string>>? LocalTerminalSnapshotProvider { get; set; } // sessionId -> snapshot
    public Func<string, (int Cols, int Rows)>? LocalTerminalSizeProvider { get; set; } // sessionId -> size
    public string AuthToken { get; set; } = string.Empty;
    public bool WebPanelEnabled { get; set; }

    /// <summary>Current group info (null if no group created yet).</summary>
    public GroupInfo? Group => _group;

    public bool IsRunning => _httpListener?.IsListening == true;
    public IReadOnlyList<string> ListenPrefixes => _listenPrefixes;
    public IReadOnlyList<string> ReachableWebSocketUrls => _reachableWebSocketUrls;

    public Task StartAsync(int port, CancellationToken ct = default)
    {
        return StartAsync(port, null, ct);
    }

    public Task StartAsync(int port, string? baseDirectory, CancellationToken ct = default)
    {
        if (_httpListener is not null) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _httpListener = StartListenerWithFallback(port);
        _startedAtUtc = DateTimeOffset.UtcNow;

        // Initialize group store if a base directory is provided
        if (!string.IsNullOrEmpty(baseDirectory))
        {
            _groupStore = new GroupStore(baseDirectory);
        }

        _listenPrefixes.Clear();
        _listenPrefixes.AddRange(_httpListener.Prefixes.Cast<string>());

        _reachableWebSocketUrls.Clear();
        if (_listenPrefixes.Any(prefix =>
                !prefix.Contains("localhost", StringComparison.OrdinalIgnoreCase)))
        {
            _reachableWebSocketUrls.AddRange(RelayAddressHelper.GetReachableWebSocketUrls(port));
        }
        else
        {
            _reachableWebSocketUrls.Add(RelayAddressHelper.GetLocalhostWebSocketUrl(port));
        }

        Log?.Invoke($"Relay server started on port {port}");
        foreach (var url in _reachableWebSocketUrls)
            Log?.Invoke($"Relay server reachable at {url}");

        if (WebPanelEnabled)
        {
            _webPanelModule = new WebPanelModule();
            Log?.Invoke("Web panel enabled — serving at /panel");
        }

        _ = AcceptClientsLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public void Stop()
    {
        _cts?.Cancel();

        foreach (var client in _clients.Values)
        {
            try { client.WebSocket.Abort(); } catch { }
        }
        _clients.Clear();
        _devices.Clear();
        _listenPrefixes.Clear();
        _reachableWebSocketUrls.Clear();

        try { _httpListener?.Stop(); } catch { }
        _httpListener = null;

        Log?.Invoke("Relay server stopped");
        NotifyDeviceListChanged();
    }

    /// <summary>
    /// Register a local device (the server PC itself) without a WebSocket connection.
    /// Terminal I/O for this device is handled through direct delegates.
    /// Also initializes or loads the group, registering this device as the Server member.
    /// </summary>
    public void RegisterLocalDevice(string deviceId, string displayName, string os, List<string> availableShells)
    {
        var device = new ConnectedDevice
        {
            DeviceId = deviceId,
            DisplayName = displayName,
            Os = os,
            AvailableShells = availableShells,
            IsLocal = true
        };
        _devices[deviceId] = device;

        // Initialize or load group
        InitializeGroup(deviceId, displayName, os, availableShells);

        NotifyDeviceListChanged();
        Log?.Invoke($"Local device registered: {displayName} ({deviceId})");
    }

    /// <summary>
    /// Initialize or load the group, ensuring the server device is a member.
    /// If AuthToken is set and no group exists, uses AuthToken as GroupSecret for backward compat.
    /// </summary>
    private void InitializeGroup(string deviceId, string displayName, string os, List<string> availableShells)
    {
        if (_groupStore is not null)
        {
            _group = _groupStore.LoadGroup();
        }

        if (_group is null)
        {
            _group = new GroupInfo
            {
                GroupId = Guid.NewGuid().ToString("N"),
                GroupSecret = !string.IsNullOrWhiteSpace(AuthToken)
                    ? AuthToken
                    : GenerateGroupSecret(),
                ServerDeviceId = deviceId,
                CreatedAt = DateTimeOffset.UtcNow
            };
        }

        // Ensure server is in the member list
        _group.ServerDeviceId = deviceId;
        var serverMember = _group.Members.FirstOrDefault(m => m.DeviceId == deviceId);
        if (serverMember is null)
        {
            _group.Members.Add(new GroupMember
            {
                DeviceId = deviceId,
                DisplayName = displayName,
                Os = os,
                Role = MemberRole.Server,
                JoinedAt = DateTimeOffset.UtcNow,
                AvailableShells = availableShells
            });
        }
        else
        {
            serverMember.DisplayName = displayName;
            serverMember.Os = os;
            serverMember.Role = MemberRole.Server;
            serverMember.AvailableShells = availableShells;
        }

        // Use GroupSecret as the AuthToken for WebSocket authentication
        if (string.IsNullOrWhiteSpace(AuthToken))
            AuthToken = _group.GroupSecret;

        _groupStore?.SaveGroup(_group);

        Log?.Invoke($"Group initialized: {_group.GroupId} (secret: {_group.GroupSecret[..8]}...)");
    }

    /// <summary>Get current group info for external access (e.g. Web Panel API).</summary>
    public GroupInfo? GetGroupInfo() => _group;

    private static string GenerateGroupSecret()
    {
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>
    /// Forward terminal output from local device to subscribed clients.
    /// </summary>
    public async Task BroadcastLocalTerminalOutputAsync(string deviceId, string sessionId, string data)
    {
        var msg = MessageSerializer.Serialize(new TerminalOutputMessage
        {
            DeviceId = deviceId,
            SessionId = sessionId,
            Data = data
        });

        foreach (var client in _clients.Values)
        {
            if (client.SubscribedDeviceId == deviceId &&
                string.Equals(client.SubscribedSessionId, sessionId, StringComparison.Ordinal))
            {
                await SendAsync(client, msg);
            }
        }
    }

    /// <summary>
    /// Notify subscribed clients that a local terminal session was closed by the PC.
    /// </summary>
    public async Task BroadcastLocalTerminalClosedAsync(string deviceId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(sessionId))
            return;

        var msg = MessageSerializer.Serialize(new TerminalClosedMessage
        {
            DeviceId = deviceId,
            SessionId = sessionId
        });

        foreach (var client in _clients.Values)
        {
            if (client.SubscribedDeviceId == deviceId &&
                string.Equals(client.SubscribedSessionId, sessionId, StringComparison.Ordinal))
            {
                client.SubscribedSessionId = null;
                await SendAsync(client, msg);
            }
        }
    }

    /// <summary>
    /// Push the latest local session list to clients that are viewing this device.
    /// </summary>
    public async Task BroadcastLocalSessionListChangedAsync(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return;

        var msg = MessageSerializer.Serialize(new SessionListMessage
        {
            DeviceId = deviceId,
            Sessions = LocalSessionListProvider?.Invoke() ?? new List<Protocol.SessionInfo>()
        });

        foreach (var client in _clients.Values)
        {
            if (client.SubscribedDeviceId == deviceId)
            {
                await SendAsync(client, msg);
            }
        }
    }

    /// <summary>
    /// Event raised when a remote client sends terminal input to the local device.
    /// </summary>
    public event Action<string, string>? LocalTerminalInputReceived; // sessionId, data

    /// <summary>
    /// Event raised when a remote client resizes the local device terminal.
    /// </summary>
    public event Action<string, int, int>? LocalTerminalResizeReceived; // sessionId, cols, rows

    /// <summary>
    /// Event raised when a remote client ends its local terminal viewing session.
    /// </summary>
    public event Action<string>? LocalTerminalSessionEnded; // sessionId

    /// <summary>
    /// Event raised when a remote client requests to open a new terminal session on the local device.
    /// </summary>
    public event Func<string, string, Task<(string SessionId, int Cols, int Rows)>>? LocalTerminalOpenRequested; // deviceId, shellId -> (sessionId, cols, rows)

    /// <summary>
    /// Returns active session list for local device.
    /// </summary>
    public Func<List<Protocol.SessionInfo>>? LocalSessionListProvider { get; set; }

    /// <summary>
    /// Event raised when a remote device has opened a terminal in response to our request.
    /// Used by server-mode PC to create a remote tab.
    /// </summary>
    public event Action<string, string, int, int>? RemoteTerminalOpenedReceived; // deviceId, sessionId, cols, rows

    /// <summary>
    /// Event raised when a remote device sends terminal output back.
    /// Used by server-mode PC to display remote terminal output.
    /// </summary>
    public event Action<string, string, string>? RemoteTerminalOutputReceived; // deviceId, sessionId, data

    /// <summary>
    /// Event raised when a remote device terminal is closed.
    /// </summary>
    public event Action<string, string>? RemoteTerminalClosedReceived; // deviceId, sessionId

    /// <summary>
    /// Forward terminal input from server PC to a remote device.
    /// </summary>
    public async Task ForwardTerminalInputToDevice(string deviceId, string sessionId, string data)
    {
        if (!_devices.TryGetValue(deviceId, out var device)) return;
        if (device.ClientId is null) return;
        if (!_clients.TryGetValue(device.ClientId, out var client)) return;

        var msg = MessageSerializer.Serialize(new TerminalInputMessage
        {
            DeviceId = deviceId,
            SessionId = sessionId,
            Data = data
        });
        await SendAsync(client, msg);
    }

    /// <summary>
    /// Send a terminal.open request from the server PC to a remote device.
    /// </summary>
    public async Task SendTerminalOpenToDevice(string targetDeviceId, string shellId, string requesterDeviceId)
    {
        if (!_devices.TryGetValue(targetDeviceId, out var device)) return;
        if (device.ClientId is null) return;
        if (!_clients.TryGetValue(device.ClientId, out var client)) return;

        var msg = MessageSerializer.Serialize(new TerminalOpenMessage
        {
            DeviceId = targetDeviceId,
            ShellId = shellId
        });
        await SendAsync(client, msg);
        Log?.Invoke($"Sent terminal.open to device {targetDeviceId} (shell={shellId}) requested by {requesterDeviceId}");
    }

    public List<DeviceInfo> GetDeviceList()
    {
        return _devices.Values.Select(d => new DeviceInfo
        {
            DeviceId = d.DeviceId,
            DisplayName = d.DisplayName,
            Os = d.Os,
            IsOnline = true,
            AvailableShells = d.AvailableShells
        }).ToList();
    }

    private List<SessionInfo>? GetSessionsForDevice(string deviceId)
    {
        if (!_devices.TryGetValue(deviceId, out var device))
            return null;

        if (device.IsLocal)
            return LocalSessionListProvider?.Invoke() ?? new List<SessionInfo>();

        // For remote devices, we don't have direct session data via REST
        return new List<SessionInfo>();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts?.Dispose();
    }

    private async Task AcceptClientsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _httpListener is not null)
        {
            try
            {
                var context = await _httpListener.GetContextAsync();
                var path = NormalizeRequestPath(context.Request.Url?.AbsolutePath);
                if (context.Request.IsWebSocketRequest)
                {
                    if (!IsWebSocketPath(path))
                    {
                        await WriteJsonResponseAsync(
                            context.Response,
                            HttpStatusCode.NotFound,
                            new ErrorMessage { Code = "not_found", Message = "Unknown WebSocket endpoint." });
                        continue;
                    }

                    if (!IsAuthorized(context.Request))
                    {
                        Log?.Invoke($"Unauthorized WebSocket request from {context.Request.RemoteEndPoint}");
                        await WriteJsonResponseAsync(
                            context.Response,
                            HttpStatusCode.Unauthorized,
                            new ErrorMessage { Code = "unauthorized", Message = "Missing or invalid relay token." });
                        continue;
                    }

                    var wsContext = await context.AcceptWebSocketAsync(null);
                    var clientId = $"client-{Interlocked.Increment(ref _clientIdCounter)}";
                    var client = new ConnectedClient
                    {
                        ClientId = clientId,
                        WebSocket = wsContext.WebSocket
                    };
                    _clients[clientId] = client;
                    Log?.Invoke($"Client connected: {clientId}");
                    _ = HandleClientAsync(client, ct);
                }
                else
                {
                    await HandleHttpRequestAsync(context, path);
                }
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex)
            {
                Log?.Invoke($"Accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(ConnectedClient client, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuffer = new MemoryStream();
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pingTask = RunPingLoopAsync(client, pingCts.Token);
        try
        {
            while (client.WebSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await client.WebSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log?.Invoke(
                        $"Client {client.ClientId} requested close: " +
                        $"{result.CloseStatus} {result.CloseStatusDescription}");
                    await client.WebSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    messageBuffer.Write(buffer, 0, result.Count);

                    if (!result.EndOfMessage)
                        continue;

                    var json = Encoding.UTF8.GetString(
                        messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                    messageBuffer.SetLength(0);

                    try
                    {
                        await HandleMessageAsync(client, json, ct);
                    }
                    catch (Exception ex)
                    {
                        Log?.Invoke(
                            $"Client {client.ClientId} message handling failed: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            Log?.Invoke($"Client {client.ClientId} websocket error: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Client {client.ClientId} error: {ex.Message}");
        }
        finally
        {
            pingCts.Cancel();
            try { await pingTask; } catch { }
            if (client.SubscribedDeviceId is not null &&
                client.SubscribedSessionId is not null &&
                _devices.TryGetValue(client.SubscribedDeviceId, out var subscribedDevice) &&
                subscribedDevice.IsLocal)
            {
                try
                {
                    LocalTerminalSessionEnded?.Invoke(client.SubscribedSessionId);
                }
                catch (Exception ex)
                {
                    Log?.Invoke(
                        $"Local terminal session end handler failed for {client.ClientId}: {ex.Message}");
                }
            }

            // Clean up device registration if this client registered one
            if (client.RegisteredDeviceId is not null)
            {
                _devices.TryRemove(client.RegisteredDeviceId, out _);
                NotifyDeviceListChanged();
                Log?.Invoke($"Device unregistered: {client.RegisteredDeviceId}");

                // If the disconnecting client is the bound mobile, invalidate all panel tokens
                if (_group is not null && client.RegisteredDeviceId == _group.BoundMobileId)
                {
                    _panelAccessTokens.Clear();
                    _panelLoginSessions.Clear();
                    Log?.Invoke("Bound mobile disconnected — all panel tokens invalidated");
                }

                // Broadcast group member left (device goes offline, stays in persistent list)
                if (_group is not null)
                {
                    var leftMsg = MessageSerializer.Serialize(new GroupMemberLeftMessage
                    {
                        DeviceId = client.RegisteredDeviceId
                    });
                    _ = BroadcastToOthersAsync(client, leftMsg);
                    GroupMemberListChanged?.Invoke(BuildGroupMemberInfoList());
                }
            }

            _clients.TryRemove(client.ClientId, out _);
            Log?.Invoke($"Client disconnected: {client.ClientId}");
        }
    }

    private async Task HandleMessageAsync(ConnectedClient client, string json, CancellationToken ct)
    {
        var message = MessageSerializer.DeserializeMessage(json);
        if (message is null)
        {
            Log?.Invoke($"Client {client.ClientId} sent unrecognized message");
            return;
        }

        Log?.Invoke($"Client {client.ClientId} -> {message.GetType().Name}");

        switch (message)
        {
            case GroupJoinRequestMessage joinReq:
                await HandleGroupJoinRequest(client, joinReq);
                break;

            case GroupKickMessage kick:
                await HandleGroupKick(client, kick);
                break;

            case MobileBindRequestMessage bindReq:
                await HandleMobileBindRequest(client, bindReq);
                break;

            case MobileUnbindMessage unbind:
                await HandleMobileUnbind(client, unbind);
                break;

            case AuthResponseMessage authResp:
                HandleAuthResponse(client, authResp);
                break;

            case PanelLoginScanMessage loginScan:
                await HandlePanelLoginScan(client, loginScan);
                break;

            case DeviceRegisterMessage reg:
                var device = new ConnectedDevice
                {
                    DeviceId = reg.DeviceId,
                    DisplayName = reg.DisplayName,
                    Os = reg.Os,
                    AvailableShells = reg.AvailableShells,
                    ClientId = client.ClientId,
                    IsLocal = false
                };
                _devices[reg.DeviceId] = device;
                client.RegisteredDeviceId = reg.DeviceId;
                NotifyDeviceListChanged();
                Log?.Invoke($"Device registered: {reg.DisplayName} ({reg.DeviceId}) from {client.ClientId}");
                break;

            case DeviceListRequestMessage:
                var list = new DeviceListMessage { Devices = GetDeviceList() };
                await SendAsync(client, MessageSerializer.Serialize(list));
                break;

            case SessionListRequestMessage sessionReq:
                if (_devices.TryGetValue(sessionReq.DeviceId, out var sessionDevice))
                {
                    client.SubscribedDeviceId = sessionReq.DeviceId;
                    client.SubscribedSessionId = null;

                    List<Protocol.SessionInfo> sessions;
                    if (sessionDevice.IsLocal)
                    {
                        sessions = LocalSessionListProvider?.Invoke() ?? new List<Protocol.SessionInfo>();
                    }
                    else
                    {
                        // Forward to remote device
                        if (sessionDevice.ClientId is not null &&
                            _clients.TryGetValue(sessionDevice.ClientId, out var sessionClient))
                        {
                            await SendAsync(sessionClient, json);
                        }
                        break;
                    }
                    var sessionList = new SessionListMessage
                    {
                        DeviceId = sessionReq.DeviceId,
                        Sessions = sessions
                    };
                    await SendAsync(client, MessageSerializer.Serialize(sessionList));
                }
                break;

            case TerminalInputMessage input:
                // Route to the target device
                if (_devices.TryGetValue(input.DeviceId, out var targetDevice))
                {
                    client.SubscribedDeviceId = input.DeviceId;
                    client.SubscribedSessionId = input.SessionId;

                    if (targetDevice.IsLocal)
                    {
                        // Local device - invoke direct handler
                        InvokeLocalTerminalInput(client, input);
                    }
                    else if (targetDevice.ClientId is not null &&
                             _clients.TryGetValue(targetDevice.ClientId, out var deviceClient))
                    {
                        // Forward to remote PC
                        await SendAsync(deviceClient, json);
                    }
                }
                break;

            case TerminalOutputMessage output:
                // Forward output to all clients subscribed to this device
                client.SubscribedDeviceId ??= output.DeviceId;
                foreach (var c in _clients.Values)
                {
                    if (c.ClientId != client.ClientId &&
                        c.SubscribedDeviceId == output.DeviceId &&
                        string.Equals(c.SubscribedSessionId, output.SessionId, StringComparison.Ordinal))
                    {
                        await SendAsync(c, json);
                    }
                }
                // Also notify the server PC if it requested this remote terminal
                RemoteTerminalOutputReceived?.Invoke(output.DeviceId, output.SessionId, output.Data);
                break;

            case TerminalOpenMessage open:
                // Forward to the target device's PC client
                if (_devices.TryGetValue(open.DeviceId, out var openTarget))
                {
                    client.SubscribedDeviceId = open.DeviceId;
                    client.SubscribedSessionId = null;
                    if (openTarget.IsLocal)
                    {
                        if (LocalTerminalOpenRequested is not null)
                        {
                            try
                            {
                                var (sessionId, openCols, openRows) =
                                    await LocalTerminalOpenRequested.Invoke(open.DeviceId, open.ShellId);
                                client.SubscribedSessionId = sessionId;

                                var opened = new TerminalOpenedMessage
                                {
                                    DeviceId = open.DeviceId,
                                    SessionId = sessionId,
                                    Cols = openCols,
                                    Rows = openRows
                                };
                                await SendAsync(client, MessageSerializer.Serialize(opened));

                                string? snapshot = null;
                                if (LocalTerminalSnapshotProvider is not null)
                                {
                                    try
                                    {
                                        snapshot = await LocalTerminalSnapshotProvider(sessionId);
                                    }
                                    catch (Exception ex)
                                    {
                                        Log?.Invoke($"Local terminal snapshot failed: {ex.Message}");
                                    }
                                }

                                if (!string.IsNullOrWhiteSpace(snapshot))
                                {
                                    var initialOutput = new TerminalOutputMessage
                                    {
                                        DeviceId = open.DeviceId,
                                        SessionId = sessionId,
                                        Data = snapshot
                                    };
                                    await SendAsync(client, MessageSerializer.Serialize(initialOutput));
                                }

                                Log?.Invoke($"Local terminal opened for client {client.ClientId}, session={sessionId}");
                            }
                            catch (Exception ex)
                            {
                                Log?.Invoke($"Local terminal open failed: {ex.Message}");
                                var error = new ErrorMessage
                                {
                                    Code = "terminal.open.failed",
                                    Message = ex.Message
                                };
                                await SendAsync(client, MessageSerializer.Serialize(error));
                            }
                        }
                        else
                        {
                            // Fallback: no handler registered
                            var cols = 120;
                            var rows = 30;
                            if (LocalTerminalSizeProvider is not null)
                            {
                                try
                                {
                                    var size = LocalTerminalSizeProvider("local");
                                    if (size.Cols > 0) cols = size.Cols;
                                    if (size.Rows > 0) rows = size.Rows;
                                }
                                catch (Exception ex)
                                {
                                    Log?.Invoke($"Local terminal size lookup failed: {ex.Message}");
                                }
                            }

                            var opened = new TerminalOpenedMessage
                            {
                                DeviceId = open.DeviceId,
                                SessionId = "local",
                                Cols = cols,
                                Rows = rows
                            };
                            await SendAsync(client, MessageSerializer.Serialize(opened));
                            Log?.Invoke($"Local terminal opened (legacy) for client {client.ClientId}");
                        }
                    }
                    else if (openTarget.ClientId is not null &&
                             _clients.TryGetValue(openTarget.ClientId, out var openClient))
                    {
                        await SendAsync(openClient, json);
                    }
                }
                break;

            case TerminalResizeMessage resize:
                if (_devices.TryGetValue(resize.DeviceId, out var resizeTarget))
                {
                    client.SubscribedDeviceId = resize.DeviceId;
                    client.SubscribedSessionId = resize.SessionId;

                    if (resizeTarget.IsLocal)
                    {
                        InvokeLocalTerminalResize(client, resize);
                    }
                    else if (resizeTarget.ClientId is not null &&
                             _clients.TryGetValue(resizeTarget.ClientId, out var resizeClient))
                    {
                        await SendAsync(resizeClient, json);
                    }
                }
                break;

            case TerminalCloseMessage close:
                if (_devices.TryGetValue(close.DeviceId, out var closeTarget))
                {
                    client.SubscribedDeviceId = close.DeviceId;
                    client.SubscribedSessionId = close.SessionId;

                    if (closeTarget.IsLocal)
                    {
                        // Local device - reply with terminal.closed
                        var closed = new TerminalClosedMessage
                        {
                            DeviceId = close.DeviceId,
                            SessionId = close.SessionId
                        };
                        await SendAsync(client, MessageSerializer.Serialize(closed));
                        client.SubscribedSessionId = null;
                        InvokeLocalTerminalSessionEnded(client, close);
                        Log?.Invoke($"Local terminal closed for client {client.ClientId}, session={close.SessionId}");
                    }
                    else if (closeTarget.ClientId is not null &&
                             _clients.TryGetValue(closeTarget.ClientId, out var closeClient))
                    {
                        await SendAsync(closeClient, json);
                    }
                }
                break;

            case TerminalOpenedMessage opened:
                // Forward to subscribed clients
                foreach (var c in _clients.Values)
                {
                    if (c.ClientId != client.ClientId)
                    {
                        await SendAsync(c, json);
                    }
                }
                // Notify the server PC
                RemoteTerminalOpenedReceived?.Invoke(opened.DeviceId, opened.SessionId, opened.Cols, opened.Rows);
                break;

            case TerminalClosedMessage closed:
                // Forward to subscribed clients
                foreach (var c in _clients.Values)
                {
                    if (c.ClientId != client.ClientId)
                    {
                        await SendAsync(c, json);
                    }
                }
                // Notify the server PC
                RemoteTerminalClosedReceived?.Invoke(closed.DeviceId, closed.SessionId);
                break;

            case SessionListMessage:
                // Forward to subscribed clients
                foreach (var c in _clients.Values)
                {
                    if (c.ClientId != client.ClientId)
                    {
                        await SendAsync(c, json);
                    }
                }
                break;

            case ControlForceDisconnectMessage:
            case ControlRequestMessage:
            case ControlGrantMessage:
                // Broadcast control messages to all relevant clients
                foreach (var c in _clients.Values)
                {
                    if (c.ClientId != client.ClientId)
                    {
                        await SendAsync(c, json);
                    }
                }
                break;

            case GroupServerChangeRequestMessage changeReq:
                await HandleServerChangeRequest(client, changeReq);
                break;

            case GroupServerChangePrepareMessage prepare:
                // A target device reports it's ready as the new server
                await HandleServerChangePrepare(client, prepare);
                break;

            case GroupSecretRotateRequestMessage rotateReq:
                await HandleSecretRotateRequest(client, rotateReq);
                break;

            case GroupSecretRotateDoneMessage rotated:
                // Forward to all clients (usually sent by server, not expected from client)
                await BroadcastToOthersAsync(client, json);
                break;
        }
    }

    private static async Task RunPingLoopAsync(ConnectedClient client, CancellationToken ct)
    {
        var pingInterval = TimeSpan.FromSeconds(30);
        var pingPayload = new byte[] { 0x70, 0x69, 0x6E, 0x67 }; // "ping"
        try
        {
            while (!ct.IsCancellationRequested && client.WebSocket.State == WebSocketState.Open)
            {
                await Task.Delay(pingInterval, ct);
                if (client.WebSocket.State != WebSocketState.Open) break;
                await client.SendLock.WaitAsync(ct);
                try
                {
                    if (client.WebSocket.State != WebSocketState.Open) break;
                    await client.WebSocket.SendAsync(
                        new ArraySegment<byte>(pingPayload),
                        WebSocketMessageType.Binary,
                        true,
                        ct);
                }
                finally
                {
                    client.SendLock.Release();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task SendAsync(ConnectedClient client, string message)
    {
        if (client.WebSocket.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(message);
        await client.SendLock.WaitAsync();
        try
        {
            if (client.WebSocket.State != WebSocketState.Open) return;
            await client.WebSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
        }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            client.SendLock.Release();
        }
    }

    private void InvokeLocalTerminalInput(ConnectedClient client, TerminalInputMessage input)
    {
        try
        {
            Log?.Invoke(
                $"Local terminal input from {client.ClientId}: " +
                $"session={input.SessionId}, len={input.Data.Length}");
            LocalTerminalInputReceived?.Invoke(input.SessionId, input.Data);
        }
        catch (Exception ex)
        {
            Log?.Invoke(
                $"Local terminal input handler failed for {client.ClientId}: {ex.Message}");
        }
    }

    private void InvokeLocalTerminalResize(ConnectedClient client, TerminalResizeMessage resize)
    {
        try
        {
            Log?.Invoke(
                $"Local terminal resize from {client.ClientId}: " +
                $"session={resize.SessionId}, size={resize.Cols}x{resize.Rows}");
            LocalTerminalResizeReceived?.Invoke(
                resize.SessionId,
                resize.Cols,
                resize.Rows);
        }
        catch (Exception ex)
        {
            Log?.Invoke(
                $"Local terminal resize handler failed for {client.ClientId}: {ex.Message}");
        }
    }

    private void InvokeLocalTerminalSessionEnded(ConnectedClient client, TerminalCloseMessage close)
    {
        try
        {
            Log?.Invoke(
                $"Local terminal session ended by {client.ClientId}: session={close.SessionId}");
            LocalTerminalSessionEnded?.Invoke(close.SessionId);
        }
        catch (Exception ex)
        {
            Log?.Invoke(
                $"Local terminal close handler failed for {client.ClientId}: {ex.Message}");
        }
    }

    private void NotifyDeviceListChanged()
    {
        DeviceListChanged?.Invoke(GetDeviceList());
    }

    // --- Group management handlers ---

    private async Task HandleGroupJoinRequest(ConnectedClient client, GroupJoinRequestMessage req)
    {
        if (_group is null)
        {
            await SendAsync(client, MessageSerializer.Serialize(new GroupJoinRejectedMessage
            {
                Reason = "No group exists on this server."
            }));
            return;
        }

        if (!TokensEqual(req.GroupSecret, _group.GroupSecret))
        {
            Log?.Invoke($"Group join rejected for {req.DisplayName} ({req.DeviceId}): invalid secret");
            await SendAsync(client, MessageSerializer.Serialize(new GroupJoinRejectedMessage
            {
                Reason = "Invalid group secret."
            }));
            return;
        }

        // Determine role
        var role = MemberRole.Member;

        // Add or update member in group
        var existing = _group.Members.FirstOrDefault(m => m.DeviceId == req.DeviceId);
        if (existing is not null)
        {
            existing.DisplayName = req.DisplayName;
            existing.Os = req.Os;
            existing.AvailableShells = req.AvailableShells;
        }
        else
        {
            _group.Members.Add(new GroupMember
            {
                DeviceId = req.DeviceId,
                DisplayName = req.DisplayName,
                Os = req.Os,
                Role = role,
                JoinedAt = DateTimeOffset.UtcNow,
                AvailableShells = req.AvailableShells
            });
        }

        // Register device
        var device = new ConnectedDevice
        {
            DeviceId = req.DeviceId,
            DisplayName = req.DisplayName,
            Os = req.Os,
            AvailableShells = req.AvailableShells,
            ClientId = client.ClientId,
            IsLocal = false
        };
        _devices[req.DeviceId] = device;
        client.RegisteredDeviceId = req.DeviceId;
        client.MemberRole = role;

        var autoBindMobile = string.IsNullOrWhiteSpace(_group.BoundMobileId) &&
                             IsLikelyMobileOs(req.Os);
        if (autoBindMobile)
        {
            _group.BoundMobileId = req.DeviceId;
            var member = _group.Members.FirstOrDefault(m => m.DeviceId == req.DeviceId);
            if (member is not null)
                member.Role = MemberRole.Mobile;
            client.MemberRole = MemberRole.Mobile;
        }

        _groupStore?.SaveGroup(_group);
        NotifyDeviceListChanged();

        // Reply with accepted + full member list
        var memberList = BuildGroupMemberInfoList();
        await SendAsync(client, MessageSerializer.Serialize(new GroupJoinAcceptedMessage
        {
            GroupId = _group.GroupId,
            Members = memberList,
            ServerDeviceId = _group.ServerDeviceId,
            BoundMobileId = _group.BoundMobileId
        }));

        // Broadcast member joined to all other clients
        var joinedMember = memberList.FirstOrDefault(m => m.DeviceId == req.DeviceId);
        if (joinedMember is not null)
        {
            var broadcastMsg = MessageSerializer.Serialize(new GroupMemberJoinedMessage
            {
                Member = joinedMember
            });
            await BroadcastToOthersAsync(client, broadcastMsg);
        }

        GroupMemberListChanged?.Invoke(memberList);
        Log?.Invoke($"Group member joined: {req.DisplayName} ({req.DeviceId})");

        if (autoBindMobile)
        {
            Log?.Invoke($"Mobile auto-bound: {req.DisplayName} ({req.DeviceId})");
            TryDispatchPendingPanelLogins();
        }
    }

    private async Task HandleGroupKick(ConnectedClient client, GroupKickMessage kick)
    {
        if (_group is null) return;

        // Only bound mobile can kick
        if (client.MemberRole != MemberRole.Mobile)
        {
            await SendAsync(client, MessageSerializer.Serialize(new ErrorMessage
            {
                Code = "permission_denied",
                Message = "Only the bound mobile can kick members."
            }));
            return;
        }

        // Can't kick the server or yourself
        if (kick.DeviceId == _group.ServerDeviceId)
        {
            await SendAsync(client, MessageSerializer.Serialize(new ErrorMessage
            {
                Code = "cannot_kick_server",
                Message = "Cannot kick the server device."
            }));
            return;
        }

        _group.Members.RemoveAll(m => m.DeviceId == kick.DeviceId);
        _groupStore?.SaveGroup(_group);

        // Disconnect the kicked device
        var kickedClient = _clients.Values.FirstOrDefault(c => c.RegisteredDeviceId == kick.DeviceId);
        if (kickedClient is not null)
        {
            await SendAsync(kickedClient, MessageSerializer.Serialize(new GroupJoinRejectedMessage
            {
                Reason = "You have been removed from the group."
            }));
            try { kickedClient.WebSocket.Abort(); } catch { }
        }

        // Broadcast member left
        await BroadcastToAllAsync(MessageSerializer.Serialize(new GroupMemberLeftMessage
        {
            DeviceId = kick.DeviceId
        }));

        GroupMemberListChanged?.Invoke(BuildGroupMemberInfoList());
        Log?.Invoke($"Group member kicked: {kick.DeviceId}");
    }

    private async Task HandleMobileBindRequest(ConnectedClient client, MobileBindRequestMessage req)
    {
        if (_group is null) return;

        if (!string.IsNullOrEmpty(_group.BoundMobileId) && _group.BoundMobileId != req.MobileDeviceId)
        {
            await SendAsync(client, MessageSerializer.Serialize(new MobileBindRejectedMessage
            {
                Reason = "Another mobile device is already bound to this group."
            }));
            return;
        }

        _group.BoundMobileId = req.MobileDeviceId;

        // Ensure mobile is in the member list
        var mobileMember = _group.Members.FirstOrDefault(m => m.DeviceId == req.MobileDeviceId);
        if (mobileMember is null)
        {
            _group.Members.Add(new GroupMember
            {
                DeviceId = req.MobileDeviceId,
                DisplayName = req.MobileDisplayName,
                Os = "HarmonyOS",
                Role = MemberRole.Mobile,
                JoinedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            mobileMember.Role = MemberRole.Mobile;
            mobileMember.DisplayName = req.MobileDisplayName;
        }

        client.MemberRole = MemberRole.Mobile;
        client.RegisteredDeviceId = req.MobileDeviceId;
        _groupStore?.SaveGroup(_group);

        await SendAsync(client, MessageSerializer.Serialize(new MobileBindAcceptedMessage
        {
            GroupId = _group.GroupId,
            MobileDeviceId = req.MobileDeviceId
        }));

        // Broadcast updated member list
        var memberList = BuildGroupMemberInfoList();
        await BroadcastToAllAsync(MessageSerializer.Serialize(new GroupMemberListMessage
        {
            Members = memberList
        }));

        GroupMemberListChanged?.Invoke(memberList);
        Log?.Invoke($"Mobile bound: {req.MobileDisplayName} ({req.MobileDeviceId})");

        TryDispatchPendingPanelLogins();
    }

    private async Task HandleMobileUnbind(ConnectedClient client, MobileUnbindMessage unbind)
    {
        if (_group is null) return;

        // Only the bound mobile or server can unbind
        if (client.MemberRole != MemberRole.Mobile && client.RegisteredDeviceId != _group.ServerDeviceId)
        {
            await SendAsync(client, MessageSerializer.Serialize(new ErrorMessage
            {
                Code = "permission_denied",
                Message = "Only the bound mobile or server can unbind."
            }));
            return;
        }

        _group.BoundMobileId = null;
        // Change mobile member role back to Member
        var mobileMember = _group.Members.FirstOrDefault(m => m.Role == MemberRole.Mobile);
        if (mobileMember is not null)
            mobileMember.Role = MemberRole.Member;

        _groupStore?.SaveGroup(_group);

        // Broadcast updated member list
        var memberList = BuildGroupMemberInfoList();
        await BroadcastToAllAsync(MessageSerializer.Serialize(new GroupMemberListMessage
        {
            Members = memberList
        }));

        GroupMemberListChanged?.Invoke(memberList);
        Log?.Invoke("Mobile unbound from group");
    }

    private void HandleAuthResponse(ConnectedClient client, AuthResponseMessage resp)
    {
        if (!_pendingAuths.TryRemove(resp.RequestId, out var pending)) return;

        if (resp.Approved)
        {
            Log?.Invoke($"Auth request {resp.RequestId} approved");
            pending.Approved?.Invoke();
        }
        else
        {
            Log?.Invoke($"Auth request {resp.RequestId} rejected");
            pending.Rejected?.Invoke();
        }
    }

    /// <summary>
    /// Handle a server migration request (from mobile admin).
    /// </summary>
    private async Task HandleServerChangeRequest(ConnectedClient client, GroupServerChangeRequestMessage req)
    {
        if (_group is null) return;

        // Only bound mobile can initiate server change
        if (client.MemberRole != MemberRole.Mobile)
        {
            await SendAsync(client, MessageSerializer.Serialize(new ErrorMessage
            {
                Code = "permission_denied",
                Message = "Only the bound mobile can initiate server migration."
            }));
            return;
        }

        // Target device must exist and be online
        if (!_devices.TryGetValue(req.NewServerDeviceId, out var targetDevice))
        {
            await SendAsync(client, MessageSerializer.Serialize(new ErrorMessage
            {
                Code = "device_not_found",
                Message = "Target device is not online."
            }));
            return;
        }

        if (targetDevice.ClientId is null || !_clients.TryGetValue(targetDevice.ClientId, out var targetClient))
        {
            await SendAsync(client, MessageSerializer.Serialize(new ErrorMessage
            {
                Code = "device_not_connected",
                Message = "Target device is not connected."
            }));
            return;
        }

        // Send prepare message to the target device
        var prepareMsg = MessageSerializer.Serialize(new GroupServerChangePrepareMessage
        {
            GroupId = _group.GroupId,
            GroupSecret = _group.GroupSecret,
            NewServerUrl = "" // Target device fills this in when it starts its server
        });
        await SendAsync(targetClient, prepareMsg);
        Log?.Invoke($"Server migration: sent prepare to {req.NewServerDeviceId}");
    }

    /// <summary>
    /// Handle a server change prepare response (target device confirms it has started a server).
    /// </summary>
    private async Task HandleServerChangePrepare(ConnectedClient client, GroupServerChangePrepareMessage prepare)
    {
        if (_group is null) return;
        if (string.IsNullOrWhiteSpace(prepare.NewServerUrl)) return;

        // Broadcast commit to all clients so they switch to the new server
        var commitMsg = MessageSerializer.Serialize(new GroupServerChangeCommitMessage
        {
            NewServerUrl = prepare.NewServerUrl,
            GroupId = _group.GroupId,
            GroupSecret = _group.GroupSecret
        });
        await BroadcastToAllAsync(commitMsg);
        Log?.Invoke($"Server migration: commit broadcast, new server={prepare.NewServerUrl}");

        // The old server can now stop
        ServerMigrationCommitted?.Invoke(prepare.NewServerUrl, _group.GroupSecret);
    }

    /// <summary>
    /// Handle a group secret rotation request (from mobile admin).
    /// </summary>
    private async Task HandleSecretRotateRequest(ConnectedClient client, GroupSecretRotateRequestMessage req)
    {
        if (_group is null) return;

        // Only bound mobile can rotate
        if (client.MemberRole != MemberRole.Mobile)
        {
            await SendAsync(client, MessageSerializer.Serialize(new ErrorMessage
            {
                Code = "permission_denied",
                Message = "Only the bound mobile can rotate the group secret."
            }));
            return;
        }

        // Generate new secret
        var newSecret = GenerateGroupSecret();
        _group.GroupSecret = newSecret;
        AuthToken = newSecret;
        _groupStore?.SaveGroup(_group);

        // Broadcast to all online members
        var doneMsg = MessageSerializer.Serialize(new GroupSecretRotateDoneMessage
        {
            NewSecret = newSecret
        });
        await BroadcastToAllAsync(doneMsg);
        Log?.Invoke($"Group secret rotated to {newSecret[..8]}...");
    }

    /// <summary>Event raised when a server migration commit is broadcast.</summary>
    public event Action<string, string>? ServerMigrationCommitted; // newUrl, groupSecret

    /// <summary>
    /// Build the group member info list with online status.
    /// </summary>
    public List<GroupMemberInfo> BuildGroupMemberInfoList()
    {
        if (_group is null) return new List<GroupMemberInfo>();

        return _group.Members.Select(m => new GroupMemberInfo
        {
            DeviceId = m.DeviceId,
            DisplayName = m.DisplayName,
            Os = m.Os,
            Role = m.Role.ToString(),
            IsOnline = _devices.ContainsKey(m.DeviceId),
            AvailableShells = m.AvailableShells
        }).ToList();
    }

    private async Task BroadcastToOthersAsync(ConnectedClient sender, string message)
    {
        foreach (var c in _clients.Values)
        {
            if (c.ClientId != sender.ClientId)
                await SendAsync(c, message);
        }
    }

    private async Task BroadcastToAllAsync(string message)
    {
        foreach (var c in _clients.Values)
        {
            await SendAsync(c, message);
        }
    }

    /// <summary>
    /// Send an auth request to the bound mobile and execute callbacks on response.
    /// Returns false if no mobile is bound.
    /// </summary>
    public async Task<bool> RequestMobileAuthAsync(
        string action, string requesterId, string requesterName,
        string? targetDeviceId, string description,
        Action onApproved, Action onRejected, int timeoutSeconds = 60)
    {
        if (_group?.BoundMobileId is null)
            return false;

        var mobileClient = _clients.Values.FirstOrDefault(c =>
            c.RegisteredDeviceId == _group.BoundMobileId);
        if (mobileClient is null)
            return false;

        var requestId = Guid.NewGuid().ToString("N");
        var pending = new PendingAuth
        {
            RequestId = requestId,
            Approved = onApproved,
            Rejected = onRejected
        };
        _pendingAuths[requestId] = pending;

        await SendAsync(mobileClient, MessageSerializer.Serialize(new AuthRequestMessage
        {
            RequestId = requestId,
            Action = action,
            RequesterId = requesterId,
            RequesterName = requesterName,
            TargetDeviceId = targetDeviceId,
            Description = description
        }));

        // Timeout cleanup
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
            if (_pendingAuths.TryRemove(requestId, out var timedOut))
            {
                Log?.Invoke($"Auth request {requestId} timed out");
                timedOut.Rejected?.Invoke();
            }
        });

        return true;
    }

    private static HttpListener CreateListener(IEnumerable<string> prefixes)
    {
        var listener = new HttpListener();
        foreach (var prefix in prefixes.Distinct(StringComparer.OrdinalIgnoreCase))
            listener.Prefixes.Add(prefix);
        return listener;
    }

    private HttpListener StartListenerWithFallback(int port)
    {
        var attempts = new (string Description, IReadOnlyList<string> Prefixes)[]
        {
            ("LAN addresses", RelayAddressHelper.GetBindableHttpPrefixes(port)),
            ("wildcard host", new[] { RelayAddressHelper.GetWildcardHttpPrefix(port) }),
            ("localhost only", new[] { RelayAddressHelper.GetLocalhostHttpPrefix(port) })
        };

        HttpListenerException? lastException = null;

        foreach (var attempt in attempts)
        {
            var listener = CreateListener(attempt.Prefixes);
            try
            {
                listener.Start();
                if (attempt.Description != "LAN addresses")
                    Log?.Invoke($"Relay server fallback binding active: {attempt.Description}");
                return listener;
            }
            catch (HttpListenerException ex)
            {
                lastException = ex;
                Log?.Invoke($"Relay server failed to bind via {attempt.Description}: {ex.Message}");
                listener.Close();
            }
        }

        throw new HttpListenerException(lastException?.ErrorCode ?? 0,
            lastException?.Message ?? "Unable to bind relay server listener.");
    }

    private async Task HandleHttpRequestAsync(HttpListenerContext context, string path)
    {
        // Web panel routes (served before relay API routes)
        if (_webPanelModule is not null && _webPanelModule.CanHandle(path))
        {
            var handled = await _webPanelModule.HandleAsync(
                context,
                path,
                isAuthorized: () => IsAuthorized(context.Request),
                buildStatusPayload: BuildStatusPayload,
                getDeviceList: GetDeviceList,
                getSessionsForDevice: GetSessionsForDevice,
                getPanelPairingPayload: BuildPanelPairingPayload,
                getPanelQrPayload: GetPanelQrPayload,
                startPanelLogin: StartPanelLoginAsync,
                getPanelLoginStatus: GetPanelLoginStatusPayload,
                getGroupInfo: GetGroupInfo,
                getGroupMembers: BuildGroupMemberInfoList);
            if (handled) return;
        }

        if (path == "/ws/healthz")
        {
            await WriteJsonResponseAsync(context.Response, HttpStatusCode.OK, new
            {
                status = "ok",
                startedAtUtc = _startedAtUtc,
                uptimeSeconds = Math.Max(0, (int)(DateTimeOffset.UtcNow - _startedAtUtc).TotalSeconds)
            });
            return;
        }

        if (path == "/ws/status")
        {
            if (!IsAuthorized(context.Request))
            {
                await WriteJsonResponseAsync(
                    context.Response,
                    HttpStatusCode.Unauthorized,
                    new ErrorMessage { Code = "unauthorized", Message = "Missing or invalid relay token." });
                return;
            }

            await WriteJsonResponseAsync(context.Response, HttpStatusCode.OK, BuildStatusPayload());
            return;
        }

        if (path == "/ws/" || path == "/ws")
        {
            await WriteJsonResponseAsync(context.Response, HttpStatusCode.OK, new
            {
                service = "PhoneShell Relay",
                websocketPath = "/ws/",
                healthPath = "/ws/healthz",
                statusPath = "/ws/status",
                authenticationRequired = !string.IsNullOrWhiteSpace(AuthToken)
            });
            return;
        }

        await WriteJsonResponseAsync(
            context.Response,
            HttpStatusCode.NotFound,
            new ErrorMessage { Code = "not_found", Message = "Unknown relay endpoint." });
    }

    private object BuildStatusPayload()
    {
        var devices = _devices.Values
            .OrderBy(device => device.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.DeviceId, StringComparer.Ordinal)
            .Select(device => new
            {
                device.DeviceId,
                device.DisplayName,
                device.Os,
                device.IsLocal,
                AvailableShells = device.AvailableShells
            })
            .ToList();

        return new
        {
            status = "ok",
            startedAtUtc = _startedAtUtc,
            uptimeSeconds = Math.Max(0, (int)(DateTimeOffset.UtcNow - _startedAtUtc).TotalSeconds),
            connectedClientCount = _clients.Count,
            registeredDeviceCount = _devices.Count,
            listenPrefixes = _listenPrefixes.ToArray(),
            reachableWebSocketUrls = _reachableWebSocketUrls.ToArray(),
            devices
        };
    }

    private object BuildPanelPairingPayload()
    {
        // Every fresh page load calls this — invalidate all previous panel tokens
        // to enforce "scan every time" requirement
        _panelAccessTokens.Clear();

        var group = _group;
        var serverUrl = _reachableWebSocketUrls.FirstOrDefault() ?? string.Empty;
        var boundMobileId = group?.BoundMobileId;
        var hasBoundMobile = !string.IsNullOrWhiteSpace(boundMobileId);
        var boundMobileOnline = false;
        if (hasBoundMobile)
        {
            boundMobileOnline = _clients.Values.Any(c => c.RegisteredDeviceId == boundMobileId);
        }

        // Only return bind QR when no mobile is bound yet
        var qrPayload = hasBoundMobile ? string.Empty : (GetPanelQrPayload() ?? string.Empty);

        return new
        {
            requiresAuth = !string.IsNullOrWhiteSpace(AuthToken),
            hasGroup = group is not null,
            groupId = group?.GroupId ?? string.Empty,
            serverUrl,
            qrPayload,
            hasBoundMobile,
            boundMobileOnline
        };
    }

    private string? GetPanelQrPayload()
    {
        if (_group is null)
            return null;

        var serverUrl = _reachableWebSocketUrls.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(serverUrl))
            return null;

        var builder = new QrPayloadBuilder();
        return builder.BuildGroupBind(
            serverUrl,
            _group.GroupId,
            _group.GroupSecret,
            _group.ServerDeviceId);
    }

    private Task<object> StartPanelLoginAsync(HttpListenerRequest request)
    {
        CleanupExpiredPanelAuth();

        // Invalidate ALL existing panel access tokens — every page load must re-scan
        _panelAccessTokens.Clear();

        var requestId = Guid.NewGuid().ToString("N");
        var token = GeneratePanelToken();
        var requesterAddress = request.RemoteEndPoint?.Address?.ToString();

        var hasBoundMobile = !string.IsNullOrWhiteSpace(_group?.BoundMobileId);

        var session = new PanelLoginSession
        {
            RequestId = requestId,
            Token = token,
            Status = hasBoundMobile ? PanelLoginState.AwaitingScan : PanelLoginState.AwaitingMobile,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.Add(_panelLoginTtl),
            RequesterAddress = requesterAddress,
            Message = hasBoundMobile ? "Waiting for mobile scan." : "Waiting for mobile binding."
        };
        _panelLoginSessions[requestId] = session;

        // Build login QR payload for already-bound case
        string? loginQrPayload = null;
        if (hasBoundMobile && _group is not null)
        {
            var serverUrl = _reachableWebSocketUrls.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(serverUrl))
            {
                var builder = new QrPayloadBuilder();
                loginQrPayload = builder.BuildPanelLogin(serverUrl, _group.GroupId, requestId);
            }
        }

        // For unbound case, don't dispatch immediately — wait for bind flow
        // For bound case, don't dispatch immediately — wait for mobile to scan the login QR

        return Task.FromResult<object>(new
        {
            requestId,
            status = PanelLoginStateToString(session.Status),
            message = session.Message ?? string.Empty,
            expiresAtUtc = session.ExpiresAtUtc,
            loginQrPayload = loginQrPayload ?? string.Empty
        });
    }

    private object? GetPanelLoginStatusPayload(string requestId)
    {
        CleanupExpiredPanelAuth();

        if (!_panelLoginSessions.TryGetValue(requestId, out var session))
            return null;

        if (session.Status != PanelLoginState.Approved &&
            DateTimeOffset.UtcNow > session.ExpiresAtUtc)
        {
            session.Status = PanelLoginState.Expired;
            session.Message ??= "Request expired.";
        }

        // Include loginQrPayload for AwaitingScan state so frontend can display the QR
        string? loginQrPayload = null;
        if (session.Status == PanelLoginState.AwaitingScan && _group is not null)
        {
            var serverUrl = _reachableWebSocketUrls.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(serverUrl))
            {
                var builder = new QrPayloadBuilder();
                loginQrPayload = builder.BuildPanelLogin(serverUrl, _group.GroupId, session.RequestId);
            }
        }

        return new
        {
            requestId = session.RequestId,
            status = PanelLoginStateToString(session.Status),
            message = session.Message ?? string.Empty,
            token = session.Status == PanelLoginState.Approved ? session.Token : null,
            expiresAtUtc = session.ExpiresAtUtc,
            loginQrPayload = loginQrPayload ?? string.Empty
        };
    }

    private async Task TryDispatchPanelLoginSessionAsync(PanelLoginSession session)
    {
        if (session.Status is PanelLoginState.Approved or PanelLoginState.Rejected or PanelLoginState.Expired)
            return;

        // AwaitingScan means we're waiting for mobile to scan the login QR — don't auto-dispatch
        if (session.Status == PanelLoginState.AwaitingScan)
            return;

        if (_group?.BoundMobileId is null)
        {
            session.Status = PanelLoginState.AwaitingMobile;
            session.Message = "Waiting for mobile binding.";
            return;
        }

        var mobileClient = _clients.Values.FirstOrDefault(c => c.RegisteredDeviceId == _group.BoundMobileId);
        if (mobileClient is null)
        {
            // Mobile is bound but offline — keep waiting, do NOT auto-approve
            session.Status = PanelLoginState.AwaitingScan;
            session.Message = "Waiting for mobile scan.";
            return;
        }

        if (_pendingAuths.ContainsKey(session.RequestId))
        {
            session.Status = PanelLoginState.AwaitingApproval;
            session.Message = "Waiting for mobile approval.";
            return;
        }

        session.Status = PanelLoginState.AwaitingApproval;
        session.Message = "Waiting for mobile approval.";

        _pendingAuths[session.RequestId] = new PendingAuth
        {
            RequestId = session.RequestId,
            Approved = () => ApprovePanelLogin(session),
            Rejected = () => RejectPanelLogin(session, "Rejected by mobile.")
        };

        var description = string.IsNullOrWhiteSpace(session.RequesterAddress)
            ? "Web panel login request."
            : $"Web panel login request from {session.RequesterAddress}.";

        await SendAsync(mobileClient, MessageSerializer.Serialize(new AuthRequestMessage
        {
            RequestId = session.RequestId,
            Action = "panel.login",
            RequesterId = "web-panel",
            RequesterName = "Web Panel",
            TargetDeviceId = null,
            Description = description
        }));

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(60));
            if (_pendingAuths.TryRemove(session.RequestId, out var timedOut))
            {
                Log?.Invoke($"Panel login request {session.RequestId} timed out");
                timedOut.Rejected?.Invoke();
            }
        });
    }

    private void ApprovePanelLogin(PanelLoginSession session)
    {
        session.Status = PanelLoginState.Approved;
        session.Message = "Approved.";
        _panelAccessTokens[session.Token] = new PanelAccessToken
        {
            Token = session.Token,
            ExpiresAtUtc = DateTimeOffset.UtcNow.Add(_panelTokenTtl)
        };
    }

    private void RejectPanelLogin(PanelLoginSession session, string message)
    {
        session.Status = PanelLoginState.Rejected;
        session.Message = message;
    }

    private void TryDispatchPendingPanelLogins()
    {
        foreach (var session in _panelLoginSessions.Values)
        {
            if (session.Status == PanelLoginState.AwaitingMobile)
            {
                // First bind just completed — auto-approve pending sessions
                Log?.Invoke($"Panel login auto-approved (first bind completed, requestId={session.RequestId})");
                ApprovePanelLogin(session);
            }
        }
    }

    /// <summary>
    /// Handle a panel login scan message from a mobile device.
    /// Verifies the sender is the bound mobile, then sends an auth request for confirmation.
    /// </summary>
    private async Task HandlePanelLoginScan(ConnectedClient client, PanelLoginScanMessage loginScan)
    {
        if (_group is null)
        {
            await SendAsync(client, MessageSerializer.Serialize(new ErrorMessage
            {
                Code = "no_group",
                Message = "No group exists on this server."
            }));
            return;
        }

        // Verify sender is the bound mobile
        if (string.IsNullOrEmpty(_group.BoundMobileId) ||
            client.RegisteredDeviceId != _group.BoundMobileId)
        {
            Log?.Invoke($"Panel login scan rejected: {client.RegisteredDeviceId} is not the bound mobile");
            await SendAsync(client, MessageSerializer.Serialize(new ErrorMessage
            {
                Code = "not_bound_mobile",
                Message = "Only the bound mobile can scan login QR codes."
            }));
            return;
        }

        // Find the corresponding login session
        if (!_panelLoginSessions.TryGetValue(loginScan.RequestId, out var session))
        {
            await SendAsync(client, MessageSerializer.Serialize(new ErrorMessage
            {
                Code = "login_session_not_found",
                Message = "Login session not found or expired."
            }));
            return;
        }

        if (session.Status is PanelLoginState.Approved or PanelLoginState.Rejected or PanelLoginState.Expired)
        {
            await SendAsync(client, MessageSerializer.Serialize(new ErrorMessage
            {
                Code = "login_session_closed",
                Message = "Login session already resolved."
            }));
            return;
        }

        if (DateTimeOffset.UtcNow > session.ExpiresAtUtc)
        {
            session.Status = PanelLoginState.Expired;
            session.Message = "Request expired.";
            await SendAsync(client, MessageSerializer.Serialize(new ErrorMessage
            {
                Code = "login_session_expired",
                Message = "Login session expired."
            }));
            return;
        }

        // Scan verified — now send auth request to mobile for confirmation
        session.Status = PanelLoginState.AwaitingApproval;
        session.Message = "Waiting for mobile approval.";

        _pendingAuths[session.RequestId] = new PendingAuth
        {
            RequestId = session.RequestId,
            Approved = () => ApprovePanelLogin(session),
            Rejected = () => RejectPanelLogin(session, "Rejected by mobile.")
        };

        var description = string.IsNullOrWhiteSpace(session.RequesterAddress)
            ? "Web panel login request."
            : $"Web panel login request from {session.RequesterAddress}.";

        await SendAsync(client, MessageSerializer.Serialize(new AuthRequestMessage
        {
            RequestId = session.RequestId,
            Action = "panel.login",
            RequesterId = "web-panel",
            RequesterName = "Web Panel",
            TargetDeviceId = null,
            Description = description
        }));
        Log?.Invoke($"Panel login scan accepted, auth request sent to mobile (requestId={loginScan.RequestId})");

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(60));
            if (_pendingAuths.TryRemove(session.RequestId, out var timedOut))
            {
                Log?.Invoke($"Panel login request {session.RequestId} timed out");
                timedOut.Rejected?.Invoke();
            }
        });
    }

    private void CleanupExpiredPanelAuth()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _panelAccessTokens)
        {
            if (kvp.Value.ExpiresAtUtc <= now)
                _panelAccessTokens.TryRemove(kvp.Key, out _);
        }

        foreach (var kvp in _panelLoginSessions)
        {
            if (kvp.Value.Status == PanelLoginState.Approved)
                continue;

            if (kvp.Value.ExpiresAtUtc <= now)
            {
                kvp.Value.Status = PanelLoginState.Expired;
                kvp.Value.Message ??= "Request expired.";
            }
        }
    }

    private bool IsPanelTokenAuthorized(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (_panelAccessTokens.TryGetValue(token, out var entry))
        {
            if (entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
                return true;

            _panelAccessTokens.TryRemove(token, out _);
        }

        return false;
    }

    private static string PanelLoginStateToString(PanelLoginState state) => state switch
    {
        PanelLoginState.AwaitingScan => "awaiting_scan",
        PanelLoginState.AwaitingMobile => "awaiting_mobile",
        PanelLoginState.AwaitingApproval => "awaiting_approval",
        PanelLoginState.Approved => "approved",
        PanelLoginState.Rejected => "rejected",
        PanelLoginState.Expired => "expired",
        _ => "unknown"
    };

    private static string GeneratePanelToken()
    {
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        if (string.IsNullOrWhiteSpace(AuthToken))
            return true;

        var bearerHeader = request.Headers["Authorization"];
        if (!string.IsNullOrWhiteSpace(bearerHeader) &&
            bearerHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
            (TokensEqual(bearerHeader["Bearer ".Length..].Trim(), AuthToken) ||
             IsPanelTokenAuthorized(bearerHeader["Bearer ".Length..].Trim())))
        {
            return true;
        }

        var tokenHeader = request.Headers["X-PhoneShell-Token"];
        if (!string.IsNullOrWhiteSpace(tokenHeader))
        {
            var trimmed = tokenHeader.Trim();
            if (TokensEqual(trimmed, AuthToken) || IsPanelTokenAuthorized(trimmed))
                return true;
        }

        // Support token via query string (for browser WebSocket connections)
        var queryToken = request.QueryString["token"];
        if (!string.IsNullOrWhiteSpace(queryToken))
        {
            var trimmed = queryToken.Trim();
            return TokensEqual(trimmed, AuthToken) || IsPanelTokenAuthorized(trimmed);
        }

        return false;
    }

    private static bool TokensEqual(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool IsLikelyMobileOs(string os)
    {
        if (string.IsNullOrWhiteSpace(os))
            return false;
        var value = os.Trim().ToLowerInvariant();
        return value.Contains("android") ||
               value.Contains("ios") ||
               value.Contains("iphone") ||
               value.Contains("ipad") ||
               value.Contains("harmony");
    }

    private static bool IsWebSocketPath(string path) =>
        path == "/ws/" || path == "/ws";

    private static string NormalizeRequestPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        var normalized = path.Trim();
        if (normalized.Length > 1 && normalized.EndsWith('/'))
            normalized = normalized.TrimEnd('/');

        return normalized switch
        {
            "/ws" => "/ws",
            "/ws/healthz" => "/ws/healthz",
            "/ws/status" => "/ws/status",
            "/" => "/",
            "/panel" => "/panel",
            _ when normalized.StartsWith("/panel/", StringComparison.Ordinal) => normalized,
            _ when normalized.StartsWith("/api/", StringComparison.Ordinal) => normalized,
            _ when path.EndsWith('/') => normalized + "/",
            _ => normalized
        };
    }

    private static async Task WriteJsonResponseAsync(HttpListenerResponse response, HttpStatusCode statusCode, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        response.Close();
    }

    private sealed class ConnectedDevice
    {
        public string DeviceId { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Os { get; init; } = string.Empty;
        public List<string> AvailableShells { get; init; } = new();
        public string? ClientId { get; init; }
        public bool IsLocal { get; init; }
    }

    private sealed class ConnectedClient
    {
        public string ClientId { get; init; } = string.Empty;
        public WebSocket WebSocket { get; init; } = null!;
        public string? RegisteredDeviceId { get; set; }
        public string? SubscribedDeviceId { get; set; }
        public string? SubscribedSessionId { get; set; }
        public MemberRole MemberRole { get; set; } = MemberRole.Member;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private sealed class PendingAuth
    {
        public string RequestId { get; init; } = string.Empty;
        public Action? Approved { get; init; }
        public Action? Rejected { get; init; }
    }

    private sealed class PanelLoginSession
    {
        public string RequestId { get; init; } = string.Empty;
        public string Token { get; init; } = string.Empty;
        public PanelLoginState Status { get; set; }
        public DateTimeOffset CreatedAtUtc { get; init; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
        public string? RequesterAddress { get; init; }
        public string? Message { get; set; }
    }

    private sealed class PanelAccessToken
    {
        public string Token { get; init; } = string.Empty;
        public DateTimeOffset ExpiresAtUtc { get; init; }
    }

    private enum PanelLoginState
    {
        AwaitingScan,
        AwaitingMobile,
        AwaitingApproval,
        Approved,
        Rejected,
        Expired
    }
}

