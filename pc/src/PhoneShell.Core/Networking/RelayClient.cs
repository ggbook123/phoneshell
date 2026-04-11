using System.Net.WebSockets;
using System.Text;
using PhoneShell.Core.Models;
using PhoneShell.Core.Protocol;

namespace PhoneShell.Core.Networking;

/// <summary>
/// WebSocket client that connects a PC to a relay server.
/// Registers this PC's terminal session and forwards I/O.
/// Supports group-based authentication via GroupSecret.
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
    public string AuthToken { get; set; } = string.Empty;

    /// <summary>Group secret used for group-based authentication (replaces AuthToken when set).</summary>
    public string GroupSecret { get; set; } = string.Empty;

    /// <summary>Invite code for one-time group join (used when joining via invite instead of group secret).</summary>
    public string InviteCode { get; set; } = string.Empty;

    /// <summary>Group ID received after successful join.</summary>
    public string GroupId { get; private set; } = string.Empty;

    /// <summary>Current group members (updated by server).</summary>
    public List<GroupMemberInfo> GroupMembers { get; private set; } = new();

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

    /// <summary>Raised when the server indicates a mobile client detached from a session.</summary>
    public event Action<string>? TerminalDetachRequested; // sessionId

    /// <summary>Raised when this device is unbound by the server.</summary>
    public event Action? DeviceUnbound;

    /// <summary>Raised when group join is accepted.</summary>
    public event Action<GroupJoinAcceptedMessage>? GroupJoined;

    /// <summary>Raised when group join is rejected.</summary>
    public event Action<string>? GroupJoinRejected; // reason

    /// <summary>Raised when the group member list changes.</summary>
    public event Action<List<GroupMemberInfo>>? GroupMemberChanged;

    /// <summary>Raised when a remote terminal is opened (for PC-to-PC remote terminal).</summary>
    public event Action<string, string, int, int>? TerminalOpenedReceived; // deviceId, sessionId, cols, rows

    /// <summary>Raised when a device session list is received.</summary>
    public event Action<string, List<SessionInfo>>? SessionListReceived; // deviceId, sessions

    /// <summary>Raised when the server requests a local session rename.</summary>
    public event Action<string, string, string>? SessionRenameRequested; // deviceId, sessionId, title

    /// <summary>Raised when terminal output is received from a remote device.</summary>
    public event Action<string, string, string, long>? TerminalOutputReceived; // deviceId, sessionId, data, outputSeq

    /// <summary>Raised when a terminal buffer page is received from the relay server.</summary>
    public event Action<string, string, string, string, long, string, bool, string>? TerminalBufferReceived;
    // deviceId, sessionId, mode, data, snapshotOutputSeq, nextBeforeCursor, hasMore, requestId

    /// <summary>
    /// Compatibility-only event. PC remote terminal loading now uses terminal.buffer.response.
    /// </summary>
    public event Action<string, string, string, long, bool>? TerminalHistoryReceived; // deviceId, sessionId, data, nextBeforeSeq, hasMore

    /// <summary>Raised when a remote terminal is closed.</summary>
    public event Action<string, string>? TerminalClosedReceived; // deviceId, sessionId

    /// <summary>Raised when the server instructs all clients to switch to a new server.</summary>
    public event Action<string, string>? ServerChanged; // newUrl, groupSecret

    /// <summary>Raised when this device is chosen as the new server (prepare message received).</summary>
    public event Action<string, string>? ServerChangeRequested; // groupId, groupSecret

    /// <summary>Raised when the group secret has been rotated.</summary>
    public event Action<string>? GroupSecretRotated; // newSecret

    /// <summary>Raised when this device receives a relay.designated response.</summary>
    public event Action<string, string>? RelayDesignated; // relayUrl, groupId

    /// <summary>Raised when an invite code is created in response to invite.create.request.</summary>
    public event Action<string, string>? InviteCreated; // inviteCode, relayUrl

    /// <summary>Raised when a device's display name is updated.</summary>
    public event Action<string, string>? DeviceSettingsUpdated; // deviceId, displayName

    /// <summary>Raised when this device is kicked from the group.</summary>
    public event Action<string>? DeviceKicked; // reason

    /// <summary>Raised when the group is dissolved.</summary>
    public event Action<string>? GroupDissolved; // reason

    /// <summary>Provides local session list when a remote client requests it.</summary>
    public Func<List<SessionInfo>>? LocalSessionListProvider { get; set; }

    /// <summary>Provides quick panel snapshot when a remote client requests it.</summary>
    public Func<string, QuickPanelSyncMessage>? LocalQuickPanelSyncProvider { get; set; }

    /// <summary>Raised when a quick panel sync payload is received.</summary>
    public event Action<QuickPanelSyncMessage>? QuickPanelSyncReceived;

    /// <summary>Raised when a remote client appends one recent-input item on this device.</summary>
    public Action<string>? LocalRecentInputAppendRequested;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    /// <summary>
    /// Send a server migration prepare response (from the new server back to the old server).
    /// </summary>
    public async Task SendServerChangePrepareAsync(string groupId, string groupSecret, string newServerUrl)
    {
        if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(groupSecret) ||
            string.IsNullOrWhiteSpace(newServerUrl))
            return;

        var msg = MessageSerializer.Serialize(new GroupServerChangePrepareMessage
        {
            GroupId = groupId,
            GroupSecret = groupSecret,
            NewServerUrl = newServerUrl
        });
        await SendAsync(msg);
    }

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

    /// <summary>
    /// Notify the server that a terminal session was closed.
    /// </summary>
    public async Task SendTerminalClosedAsync(string deviceId, string sessionId)
    {
        if (_ws?.State != WebSocketState.Open) return;
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(sessionId))
            return;

        var msg = MessageSerializer.Serialize(new TerminalClosedMessage
        {
            DeviceId = deviceId,
            SessionId = sessionId
        });
        await SendAsync(msg);
    }

    /// <summary>
    /// Request a terminal buffer page for a session from the relay server.
    /// </summary>
    public async Task SendTerminalBufferRequestAsync(
        string deviceId,
        string sessionId,
        string beforeCursor,
        int maxChars,
        string requestId)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var msg = MessageSerializer.Serialize(new TerminalBufferRequestMessage
        {
            DeviceId = deviceId,
            SessionId = sessionId,
            RequestId = requestId,
            BeforeCursor = beforeCursor,
            MaxChars = maxChars
        });

        await SendAsync(msg);
    }

    /// <summary>
    /// Compatibility-only request path. PC remote terminal loading now uses terminal.buffer.request.
    /// </summary>
    public async Task SendTerminalHistoryRequestAsync(string deviceId, string sessionId, long beforeSeq, int maxChars)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var msg = MessageSerializer.Serialize(new TerminalHistoryRequestMessage
        {
            DeviceId = deviceId,
            SessionId = sessionId,
            BeforeSeq = beforeSeq,
            MaxChars = maxChars
        });

        await SendAsync(msg);
    }

    /// <summary>
    /// Send the current local session list to the relay server.
    /// </summary>
    public async Task SendSessionListAsync(string deviceId, List<SessionInfo> sessions)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var msg = MessageSerializer.Serialize(new SessionListMessage
        {
            DeviceId = deviceId,
            Sessions = sessions ?? new List<SessionInfo>()
        });
        await SendAsync(msg);
    }

    /// <summary>Request the current session list from a device in the group.</summary>
    public async Task SendSessionListRequestAsync(string deviceId)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var msg = MessageSerializer.Serialize(new SessionListRequestMessage
        {
            DeviceId = deviceId
        });
        await SendAsync(msg);
    }

    /// <summary>Request a session rename on a target device.</summary>
    public async Task SendSessionRenameAsync(string deviceId, string sessionId, string title)
    {
        if (_ws?.State != WebSocketState.Open) return;
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(sessionId))
            return;

        var trimmedTitle = title?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedTitle))
            return;

        var msg = MessageSerializer.Serialize(new SessionRenameMessage
        {
            DeviceId = deviceId,
            SessionId = sessionId,
            Title = trimmedTitle
        });
        await SendAsync(msg);
    }

    /// <summary>Push a quick panel snapshot to the relay server.</summary>
    public async Task SendQuickPanelSyncAsync(QuickPanelSyncMessage snapshot)
    {
        if (_ws?.State != WebSocketState.Open) return;
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.DeviceId))
            return;

        await SendAsync(MessageSerializer.Serialize(snapshot));
    }

    /// <summary>Append one recent-input item to a target device via relay.</summary>
    public async Task SendQuickPanelRecentAppendAsync(string deviceId, string input)
    {
        if (_ws?.State != WebSocketState.Open) return;
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(input))
            return;

        var msg = MessageSerializer.Serialize(new QuickPanelRecentAppendMessage
        {
            DeviceId = deviceId,
            Input = input
        });
        await SendAsync(msg);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        _cts?.Dispose();
        _sendLock.Dispose();
    }

    private async Task ConnectInternalAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

                // Use GroupSecret as auth token when available, fall back to AuthToken
                var effectiveToken = !string.IsNullOrWhiteSpace(GroupSecret) ? GroupSecret : AuthToken;
                if (!string.IsNullOrWhiteSpace(effectiveToken))
                {
                    _ws.Options.SetRequestHeader("Authorization", $"Bearer {effectiveToken}");
                    _ws.Options.SetRequestHeader("X-PhoneShell-Token", effectiveToken);
                }

                // Build URI — append invite code as query param for WebSocket auth when no token
                var connectUrl = _serverUrl;
                if (string.IsNullOrWhiteSpace(effectiveToken) && !string.IsNullOrWhiteSpace(InviteCode))
                {
                    var separator = connectUrl.Contains('?') ? "&" : "?";
                    connectUrl = $"{connectUrl}{separator}invite={Uri.EscapeDataString(InviteCode)}";
                }

                var uri = new Uri(connectUrl);
                Log?.Invoke($"Connecting to {uri}...");

                await _ws.ConnectAsync(uri, ct);
                ConnectionStateChanged?.Invoke(true);
                Log?.Invoke("Connected to relay server");

                // Determine which registration message to send after connect
                if (!string.IsNullOrWhiteSpace(InviteCode))
                {
                    // Joining via invite code — send group.join.request with invite code
                    var joinMsg = MessageSerializer.Serialize(new GroupJoinRequestMessage
                    {
                        InviteCode = InviteCode,
                        DeviceId = DeviceId,
                        DisplayName = DisplayName,
                        Os = Os,
                        AvailableShells = AvailableShells
                    });
                    await SendAsync(joinMsg);
                    Log?.Invoke("Group join request sent (via invite code)");
                    // Clear invite code after first use (it's one-time)
                    InviteCode = string.Empty;
                }
                else if (!string.IsNullOrWhiteSpace(GroupSecret))
                {
                    var joinMsg = MessageSerializer.Serialize(new GroupJoinRequestMessage
                    {
                        GroupSecret = GroupSecret,
                        DeviceId = DeviceId,
                        DisplayName = DisplayName,
                        Os = Os,
                        AvailableShells = AvailableShells
                    });
                    await SendAsync(joinMsg);
                    Log?.Invoke("Group join request sent");
                }
                else
                {
                    var regMsg = MessageSerializer.Serialize(new DeviceRegisterMessage
                    {
                        DeviceId = DeviceId,
                        DisplayName = DisplayName,
                        Os = Os,
                        AvailableShells = AvailableShells
                    });
                    await SendAsync(regMsg);
                    Log?.Invoke("Device registered with relay server");
                }

                // Start receiving
                await ReceiveLoopAsync(ct);
                ConnectionStateChanged?.Invoke(false);
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

            if (result.MessageType == WebSocketMessageType.Text)
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
            case GroupJoinAcceptedMessage accepted:
                GroupId = accepted.GroupId;
                GroupMembers = accepted.Members;
                // Save group secret from invite-based join for reconnect auth
                if (!string.IsNullOrWhiteSpace(accepted.GroupSecret))
                    GroupSecret = accepted.GroupSecret;
                GroupJoined?.Invoke(accepted);
                GroupMemberChanged?.Invoke(accepted.Members);
                Log?.Invoke($"Joined group {accepted.GroupId} ({accepted.Members.Count} members)");
                break;

            case GroupJoinRejectedMessage rejected:
                GroupJoinRejected?.Invoke(rejected.Reason);
                Log?.Invoke($"Group join rejected: {rejected.Reason}");
                _cts?.Cancel(); // Stop reconnect loop — credentials are invalid
                break;

            case GroupMemberJoinedMessage memberJoined:
                var existingJoined = GroupMembers.FirstOrDefault(m => m.DeviceId == memberJoined.Member.DeviceId);
                if (existingJoined is not null)
                    GroupMembers.Remove(existingJoined);
                GroupMembers.Add(memberJoined.Member);
                GroupMemberChanged?.Invoke(GroupMembers);
                break;

            case GroupMemberLeftMessage memberLeft:
                var leftMember = GroupMembers.FirstOrDefault(m => m.DeviceId == memberLeft.DeviceId);
                if (leftMember is not null)
                {
                    // Mark as offline rather than removing
                    GroupMembers.Remove(leftMember);
                    GroupMembers.Add(new GroupMemberInfo
                    {
                        DeviceId = leftMember.DeviceId,
                        DisplayName = leftMember.DisplayName,
                        Os = leftMember.Os,
                        Role = leftMember.Role,
                        IsOnline = false,
                        AvailableShells = leftMember.AvailableShells
                    });
                }
                GroupMemberChanged?.Invoke(GroupMembers);
                break;

            case GroupMemberListMessage memberList:
                GroupMembers = memberList.Members;
                GroupMemberChanged?.Invoke(memberList.Members);
                break;

            case TerminalInputMessage input:
                TerminalInputReceived?.Invoke(input.SessionId, input.Data);
                break;

            case TerminalOpenMessage open:
                TerminalOpenRequested?.Invoke(open.DeviceId, open.ShellId);
                break;

            case TerminalOpenedMessage opened:
                TerminalOpenedReceived?.Invoke(opened.DeviceId, opened.SessionId, opened.Cols, opened.Rows);
                break;

            case TerminalOutputMessage output:
                TerminalOutputReceived?.Invoke(output.DeviceId, output.SessionId, output.Data, output.OutputSeq);
                break;

            case TerminalBufferResponseMessage buffer:
                TerminalBufferReceived?.Invoke(
                    buffer.DeviceId,
                    buffer.SessionId,
                    buffer.Mode,
                    buffer.Data,
                    buffer.SnapshotOutputSeq,
                    buffer.NextBeforeCursor,
                    buffer.HasMore,
                    buffer.RequestId);
                break;

            case TerminalHistoryResponseMessage history:
                TerminalHistoryReceived?.Invoke(history.DeviceId, history.SessionId, history.Data,
                    history.NextBeforeSeq, history.HasMore);
                break;

            case TerminalResizeMessage resize:
                TerminalResizeRequested?.Invoke(resize.SessionId, resize.Cols, resize.Rows);
                break;

            case TerminalCloseMessage close:
                TerminalCloseRequested?.Invoke(close.SessionId);
                break;

            case TerminalDetachMessage detach:
                TerminalDetachRequested?.Invoke(detach.SessionId);
                break;

            case TerminalClosedMessage closed:
                TerminalClosedReceived?.Invoke(closed.DeviceId, closed.SessionId);
                break;

            case SessionListMessage sessionList:
                SessionListReceived?.Invoke(sessionList.DeviceId, sessionList.Sessions);
                break;

            case SessionRenameMessage rename:
                SessionRenameRequested?.Invoke(rename.DeviceId, rename.SessionId, rename.Title);
                break;

            case SessionListRequestMessage sessionReq:
                if (string.Equals(sessionReq.DeviceId, DeviceId, StringComparison.Ordinal))
                {
                    var sessions = LocalSessionListProvider?.Invoke() ?? new List<SessionInfo>();
                    _ = SendSessionListAsync(DeviceId, sessions);
                }
                break;

            case QuickPanelSyncRequestMessage syncReq:
                if (string.Equals(syncReq.DeviceId, DeviceId, StringComparison.Ordinal))
                {
                    var snapshot = LocalQuickPanelSyncProvider?.Invoke(syncReq.ExplorerPath ?? string.Empty);
                    if (snapshot is not null)
                    {
                        var response = new QuickPanelSyncMessage
                        {
                            DeviceId = DeviceId,
                            ExplorerPath = snapshot.ExplorerPath,
                            ExplorerVirtualRoot = snapshot.ExplorerVirtualRoot,
                            ExplorerEntries = snapshot.ExplorerEntries ?? new List<QuickPanelExplorerEntry>(),
                            QuickCommandFolders = snapshot.QuickCommandFolders ?? new List<QuickPanelFolderInfo>(),
                            QuickCommands = snapshot.QuickCommands ?? new List<QuickPanelCommandInfo>(),
                            RecentInputs = snapshot.RecentInputs ?? new List<string>(),
                            UpdatedAtUnixMs = snapshot.UpdatedAtUnixMs
                        };
                        _ = SendQuickPanelSyncAsync(response);
                    }
                }
                break;

            case QuickPanelSyncMessage sync:
                QuickPanelSyncReceived?.Invoke(sync);
                break;

            case QuickPanelRecentAppendMessage appendRecent:
                if (string.Equals(appendRecent.DeviceId, DeviceId, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(appendRecent.Input))
                {
                    LocalRecentInputAppendRequested?.Invoke(appendRecent.Input);
                }
                break;

            case DeviceUnregisterMessage unregistered:
                if (string.Equals(unregistered.DeviceId, DeviceId, StringComparison.Ordinal))
                {
                    Log?.Invoke("Device unbound by server");
                    DeviceUnbound?.Invoke();
                }
                break;

            case ControlForceDisconnectMessage:
                Log?.Invoke("Control force disconnected by server");
                break;

            case ErrorMessage error:
                Log?.Invoke($"Server error: [{error.Code}] {error.Message}");
                break;

            case GroupServerChangeCommitMessage commit:
                Log?.Invoke($"Server migration: switching to {commit.NewServerUrl}");
                GroupSecret = commit.GroupSecret;
                ServerChanged?.Invoke(commit.NewServerUrl, commit.GroupSecret);
                break;

            case GroupServerChangePrepareMessage prepare:
                Log?.Invoke($"Server migration: this device selected as new server");
                ServerChangeRequested?.Invoke(prepare.GroupId, prepare.GroupSecret);
                break;

            case GroupSecretRotateDoneMessage rotated:
                GroupSecret = rotated.NewSecret;
                Log?.Invoke($"Group secret rotated to {rotated.NewSecret[..Math.Min(8, rotated.NewSecret.Length)]}...");
                GroupSecretRotated?.Invoke(rotated.NewSecret);
                break;

            case RelayDesignatedMessage designated:
                Log?.Invoke($"Relay designated: url={designated.RelayUrl} groupId={designated.GroupId}");
                RelayDesignated?.Invoke(designated.RelayUrl, designated.GroupId);
                break;

            case InviteCreateResponseMessage inviteResponse:
                Log?.Invoke($"Invite code created: {inviteResponse.InviteCode}");
                InviteCreated?.Invoke(inviteResponse.InviteCode, inviteResponse.RelayUrl);
                break;

            case DeviceSettingsUpdatedMessage settingsUpdated:
                Log?.Invoke($"Device settings updated: {settingsUpdated.DeviceId} → {settingsUpdated.DisplayName}");
                DeviceSettingsUpdated?.Invoke(settingsUpdated.DeviceId, settingsUpdated.DisplayName);
                break;

            case DeviceKickedMessage kicked:
                Log?.Invoke($"Device kicked from group: {kicked.Reason}");
                _cts?.Cancel(); // Stop reconnect loop
                DeviceKicked?.Invoke(kicked.Reason);
                break;

            case GroupDissolvedMessage dissolved:
                Log?.Invoke($"Group dissolved: {dissolved.Reason}");
                _cts?.Cancel(); // Stop reconnect loop
                GroupDissolved?.Invoke(dissolved.Reason);
                break;

        }
    }

    // --- Remote terminal methods (for PC-to-PC) ---

    /// <summary>Send a request to open a terminal on a remote device.</summary>
    public async Task SendTerminalOpenAsync(string deviceId, string shellId)
    {
        var msg = MessageSerializer.Serialize(new TerminalOpenMessage
        {
            DeviceId = deviceId,
            ShellId = shellId
        });
        await SendAsync(msg);
    }

    /// <summary>Send terminal input to a remote device's session.</summary>
    public async Task SendTerminalInputAsync(string deviceId, string sessionId, string data)
    {
        var msg = MessageSerializer.Serialize(new TerminalInputMessage
        {
            DeviceId = deviceId,
            SessionId = sessionId,
            Data = data
        });
        await SendAsync(msg);
    }

    /// <summary>Send a resize request to a remote device's session.</summary>
    public async Task SendTerminalResizeAsync(string deviceId, string sessionId, int cols, int rows)
    {
        var msg = MessageSerializer.Serialize(new TerminalResizeMessage
        {
            DeviceId = deviceId,
            SessionId = sessionId,
            Cols = cols,
            Rows = rows
        });
        await SendAsync(msg);
    }

    /// <summary>Send a close request for a remote device's session.</summary>
    public async Task SendTerminalCloseAsync(string deviceId, string sessionId)
    {
        var msg = MessageSerializer.Serialize(new TerminalCloseMessage
        {
            DeviceId = deviceId,
            SessionId = sessionId
        });
        await SendAsync(msg);
    }

    /// <summary>Send a detach request for a remote device's session without closing it.</summary>
    public async Task SendTerminalDetachAsync(string deviceId, string sessionId)
    {
        var msg = MessageSerializer.Serialize(new TerminalDetachMessage
        {
            DeviceId = deviceId,
            SessionId = sessionId
        });
        await SendAsync(msg);
    }

    /// <summary>Request an invite code from the relay server.</summary>
    public async Task SendInviteCreateRequestAsync()
    {
        var msg = MessageSerializer.Serialize(new InviteCreateRequestMessage());
        await SendAsync(msg);
    }

    /// <summary>Update a device's display name via the relay server.</summary>
    public async Task SendDeviceSettingsUpdateAsync(string deviceId, string displayName)
    {
        var msg = MessageSerializer.Serialize(new DeviceSettingsUpdateMessage
        {
            DeviceId = deviceId,
            DisplayName = displayName
        });
        await SendAsync(msg);
    }

    /// <summary>Dissolve the current group.</summary>
    public async Task SendGroupDissolveAsync()
    {
        var msg = MessageSerializer.Serialize(new GroupDissolveMessage());
        await SendAsync(msg);
    }

    /// <summary>Join a group using an invite code instead of a group secret.</summary>
    public async Task SendGroupJoinWithInviteAsync(string inviteCode)
    {
        var msg = MessageSerializer.Serialize(new GroupJoinRequestMessage
        {
            InviteCode = inviteCode,
            DeviceId = DeviceId,
            DisplayName = DisplayName,
            Os = Os,
            AvailableShells = AvailableShells
        });
        await SendAsync(msg);
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
