using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using PhoneShell.Core.Protocol;

namespace PhoneShell.Core.Networking;

/// <summary>
/// WebSocket relay server that accepts connections from PC clients and mobile clients.
/// Maintains a registry of connected devices and forwards terminal I/O between them.
/// </summary>
public sealed class RelayServer : IDisposable
{
    private HttpListener? _httpListener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, ConnectedDevice> _devices = new();
    private readonly ConcurrentDictionary<string, ConnectedClient> _clients = new();
    private readonly List<string> _listenPrefixes = new();
    private readonly List<string> _reachableWebSocketUrls = new();
    private int _clientIdCounter;
    private bool _disposed;

    public event Action<string>? Log;
    public event Action<List<DeviceInfo>>? DeviceListChanged;
    public Func<Task<string>>? LocalTerminalSnapshotProvider { get; set; }
    public Func<(int Cols, int Rows)>? LocalTerminalSizeProvider { get; set; }

    public bool IsRunning => _httpListener?.IsListening == true;
    public IReadOnlyList<string> ListenPrefixes => _listenPrefixes;
    public IReadOnlyList<string> ReachableWebSocketUrls => _reachableWebSocketUrls;

    public Task StartAsync(int port, CancellationToken ct = default)
    {
        if (_httpListener is not null) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _httpListener = StartListenerWithFallback(port);

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
    /// </summary>
    public void RegisterLocalDevice(string deviceId, string displayName, List<string> availableShells)
    {
        var device = new ConnectedDevice
        {
            DeviceId = deviceId,
            DisplayName = displayName,
            Os = "Windows",
            AvailableShells = availableShells,
            IsLocal = true
        };
        _devices[deviceId] = device;
        NotifyDeviceListChanged();
        Log?.Invoke($"Local device registered: {displayName} ({deviceId})");
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
    public event Action? LocalTerminalSessionEnded;

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
                if (context.Request.IsWebSocketRequest)
                {
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
                    context.Response.StatusCode = 400;
                    context.Response.Close();
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
                _devices.TryGetValue(client.SubscribedDeviceId, out var subscribedDevice) &&
                subscribedDevice.IsLocal)
            {
                try
                {
                    LocalTerminalSessionEnded?.Invoke();
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

            case TerminalInputMessage input:
                // Route to the target device
                if (_devices.TryGetValue(input.DeviceId, out var targetDevice))
                {
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
                    if (c.ClientId != client.ClientId && c.SubscribedDeviceId == output.DeviceId)
                    {
                        await SendAsync(c, json);
                    }
                }
                break;

            case TerminalOpenMessage open:
                // Forward to the target device's PC client
                if (_devices.TryGetValue(open.DeviceId, out var openTarget))
                {
                    client.SubscribedDeviceId = open.DeviceId;
                    if (openTarget.IsLocal)
                    {
                        var cols = 120;
                        var rows = 30;
                        if (LocalTerminalSizeProvider is not null)
                        {
                            try
                            {
                                var size = LocalTerminalSizeProvider();
                                if (size.Cols > 0)
                                    cols = size.Cols;
                                if (size.Rows > 0)
                                    rows = size.Rows;
                            }
                            catch (Exception ex)
                            {
                                Log?.Invoke($"Local terminal size lookup failed: {ex.Message}");
                            }
                        }

                        // Local device - reply with terminal.opened so mobile can proceed
                        var opened = new TerminalOpenedMessage
                        {
                            DeviceId = open.DeviceId,
                            SessionId = "local",
                            Cols = cols,
                            Rows = rows
                        };
                        await SendAsync(client, MessageSerializer.Serialize(opened));

                        string? snapshot = null;
                        if (LocalTerminalSnapshotProvider is not null)
                        {
                            try
                            {
                                snapshot = await LocalTerminalSnapshotProvider();
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
                                SessionId = opened.SessionId,
                                Data = snapshot
                            };
                            await SendAsync(client, MessageSerializer.Serialize(initialOutput));
                        }

                        Log?.Invoke($"Local terminal opened for client {client.ClientId}");
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
                    if (closeTarget.IsLocal)
                    {
                        // Local device - reply with terminal.closed
                        var closed = new TerminalClosedMessage
                        {
                            DeviceId = close.DeviceId,
                            SessionId = close.SessionId
                        };
                        await SendAsync(client, MessageSerializer.Serialize(closed));
                        client.SubscribedDeviceId = null;
                        InvokeLocalTerminalSessionEnded(client, close);
                        Log?.Invoke($"Local terminal closed for client {client.ClientId}");
                    }
                    else if (closeTarget.ClientId is not null &&
                             _clients.TryGetValue(closeTarget.ClientId, out var closeClient))
                    {
                        await SendAsync(closeClient, json);
                    }
                }
                break;

            case TerminalOpenedMessage:
            case TerminalClosedMessage:
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
            LocalTerminalSessionEnded?.Invoke();
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
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }
}

