using System.Net.WebSockets;
using System.Text;
using PhoneShell.Core.Protocol;

namespace PhoneShell.Core.Networking;

/// <summary>
/// WebSocket client that connects a PC to a relay server.
/// Registers this PC's terminal session and forwards I/O.
/// </summary>
public sealed class RelayClient : IDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;
    private string _serverUrl = string.Empty;
    private int _reconnectDelayMs = 3000;

    // Device info to send on registration
    public string DeviceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Os { get; set; } = "Windows";
    public List<string> AvailableShells { get; set; } = new();

    public event Action<string>? Log;
    public event Action<bool>? ConnectionStateChanged;

    /// <summary>Raised when the server sends terminal input to this PC.</summary>
    public event Action<string, string>? TerminalInputReceived; // sessionId, data

    /// <summary>Raised when the server sends a terminal open request.</summary>
    public event Action<string, string>? TerminalOpenRequested; // deviceId, shellId

    /// <summary>Raised when the server sends a terminal resize request.</summary>
    public event Action<string, int, int>? TerminalResizeRequested; // sessionId, cols, rows

    /// <summary>Raised when the server sends a terminal close request.</summary>
    public event Action<string>? TerminalCloseRequested; // sessionId

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public async Task ConnectAsync(string serverUrl, CancellationToken ct = default)
    {
        _serverUrl = serverUrl;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await ConnectInternalAsync(_cts.Token);
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        try { _ws?.Abort(); } catch { }
        _ws?.Dispose();
        _ws = null;
        ConnectionStateChanged?.Invoke(false);
    }

    /// <summary>
    /// Send terminal output from this PC to the relay server.
    /// </summary>
    public async Task SendTerminalOutputAsync(string deviceId, string sessionId, string data)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var msg = MessageSerializer.Serialize(new TerminalOutputMessage
        {
            DeviceId = deviceId,
            SessionId = sessionId,
            Data = data
        });

        await SendAsync(msg);
    }

    /// <summary>
    /// Notify the server that a terminal session was opened.
    /// </summary>
    public async Task SendTerminalOpenedAsync(string deviceId, string sessionId, int cols, int rows)
    {
        var msg = MessageSerializer.Serialize(new TerminalOpenedMessage
        {
            DeviceId = deviceId,
            SessionId = sessionId,
            Cols = cols,
            Rows = rows
        });
        await SendAsync(msg);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        _cts?.Dispose();
    }

    private async Task ConnectInternalAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _ws?.Dispose();
                _ws = new ClientWebSocket();

                var uri = new Uri(_serverUrl);
                Log?.Invoke($"Connecting to {uri}...");

                await _ws.ConnectAsync(uri, ct);
                ConnectionStateChanged?.Invoke(true);
                Log?.Invoke("Connected to relay server");

                // Register this device
                var regMsg = MessageSerializer.Serialize(new DeviceRegisterMessage
                {
                    DeviceId = DeviceId,
                    DisplayName = DisplayName,
                    Os = Os,
                    AvailableShells = AvailableShells
                });
                await SendAsync(regMsg);
                Log?.Invoke("Device registered with relay server");

                // Start receiving
                await ReceiveLoopAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Connection error: {ex.Message}");
                ConnectionStateChanged?.Invoke(false);
            }

            if (!ct.IsCancellationRequested)
            {
                Log?.Invoke($"Reconnecting in {_reconnectDelayMs}ms...");
                try { await Task.Delay(_reconnectDelayMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        ConnectionStateChanged?.Invoke(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var messageBuffer = new MemoryStream();

        while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                Log?.Invoke("Server closed connection");
                break;
            }

            if (result.MessageType == WebSocketMessageType.Text ||
                (result.MessageType == WebSocketMessageType.Binary && messageBuffer.Length > 0))
            {
                messageBuffer.Write(buffer, 0, result.Count);

                if (!result.EndOfMessage)
                    continue;

                var json = Encoding.UTF8.GetString(
                    messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                messageBuffer.SetLength(0);

                HandleMessage(json);
            }
        }
    }

    private void HandleMessage(string json)
    {
        var message = MessageSerializer.DeserializeMessage(json);
        if (message is null) return;

        switch (message)
        {
            case TerminalInputMessage input:
                TerminalInputReceived?.Invoke(input.SessionId, input.Data);
                break;

            case TerminalOpenMessage open:
                TerminalOpenRequested?.Invoke(open.DeviceId, open.ShellId);
                break;

            case TerminalResizeMessage resize:
                TerminalResizeRequested?.Invoke(resize.SessionId, resize.Cols, resize.Rows);
                break;

            case TerminalCloseMessage close:
                TerminalCloseRequested?.Invoke(close.SessionId);
                break;

            case ControlForceDisconnectMessage:
                Log?.Invoke("Control force disconnected by server");
                break;
        }
    }

    private async Task SendAsync(string message)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(message);
        await _sendLock.WaitAsync();
        try
        {
            if (_ws?.State != WebSocketState.Open) return;
            await _ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
        }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            _sendLock.Release();
        }
    }
}
