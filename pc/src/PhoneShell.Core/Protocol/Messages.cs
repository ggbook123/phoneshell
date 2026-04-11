namespace PhoneShell.Core.Protocol;

/// <summary>
/// All WebSocket message types exchanged between PC clients, relay server, and mobile clients.
/// Each message is serialized as JSON with a "type" discriminator field.
/// </summary>

// --- Device management ---

public sealed class DeviceRegisterMessage
{
    public string Type => "device.register";
    public string DeviceId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Os { get; init; } = string.Empty;
    public List<string> AvailableShells { get; init; } = new();
    public string? Mode { get; init; } = "standalone";
}

public sealed class DeviceUnregisterMessage
{
    public string Type => "device.unregister";
    public string DeviceId { get; init; } = string.Empty;
}

public sealed class DeviceListRequestMessage
{
    public string Type => "device.list.request";
}

public sealed class DeviceListMessage
{
    public string Type => "device.list";
    public List<DeviceInfo> Devices { get; init; } = new();
}

public sealed class DeviceInfo
{
    public string DeviceId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Os { get; init; } = string.Empty;
    public bool IsOnline { get; init; }
    public List<string> AvailableShells { get; init; } = new();
}

// --- Session management ---

public sealed class SessionListRequestMessage
{
    public string Type => "session.list.request";
    public string DeviceId { get; init; } = string.Empty;
}

public sealed class SessionListMessage
{
    public string Type => "session.list";
    public string DeviceId { get; init; } = string.Empty;
    public List<SessionInfo> Sessions { get; init; } = new();
}

public sealed class SessionInfo
{
    public string SessionId { get; init; } = string.Empty;
    public string ShellId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
}

public sealed class SessionRenameMessage
{
    public string Type => "session.rename";
    public string DeviceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
}

// --- Quick panel sync ---

public sealed class QuickPanelSyncRequestMessage
{
    public string Type => "quickpanel.sync.request";
    public string DeviceId { get; init; } = string.Empty;
    public string ExplorerPath { get; init; } = string.Empty;
}

public sealed class QuickPanelSyncMessage
{
    public string Type => "quickpanel.sync";
    public string DeviceId { get; init; } = string.Empty;
    public string ExplorerPath { get; init; } = string.Empty;
    public bool ExplorerVirtualRoot { get; init; }
    public List<QuickPanelExplorerEntry> ExplorerEntries { get; init; } = new();
    public List<QuickPanelFolderInfo> QuickCommandFolders { get; init; } = new();
    public List<QuickPanelCommandInfo> QuickCommands { get; init; } = new();
    public List<string> RecentInputs { get; init; } = new();
    public long UpdatedAtUnixMs { get; init; }
}

public sealed class QuickPanelExplorerEntry
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public bool IsParent { get; init; }
}

public sealed class QuickPanelFolderInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

public sealed class QuickPanelCommandInfo
{
    public string Id { get; init; } = string.Empty;
    public string FolderId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string CommandText { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}

public sealed class QuickPanelRecentAppendMessage
{
    public string Type => "quickpanel.recent.append";
    public string DeviceId { get; init; } = string.Empty;
    public string Input { get; init; } = string.Empty;
}

// --- Terminal session management ---

public sealed class TerminalOpenMessage
{
    public string Type => "terminal.open";
    public string DeviceId { get; init; } = string.Empty;
    public string ShellId { get; init; } = string.Empty;
}

public sealed class TerminalOpenedMessage
{
    public string Type => "terminal.opened";
    public string DeviceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public int Cols { get; init; }
    public int Rows { get; init; }
}

public sealed class TerminalInputMessage
{
    public string Type => "terminal.input";
    public string DeviceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string Data { get; init; } = string.Empty;
}

public sealed class TerminalOutputMessage
{
    public string Type => "terminal.output";
    public string DeviceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string Data { get; init; } = string.Empty;
    public long OutputSeq { get; init; }
}

public sealed class TerminalSnapshotRequestMessage
{
    public string Type => "terminal.snapshot.request";
    public string DeviceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public int MaxChars { get; init; } = 120_000;
}

public sealed class TerminalSnapshotResponseMessage
{
    public string Type => "terminal.snapshot.response";
    public string DeviceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public string Data { get; init; } = string.Empty;
    public long SnapshotSeq { get; init; }
    public long NextBeforeSeq { get; init; }
    public bool HasMore { get; init; }
}

public sealed class TerminalHistoryRequestMessage
{
    public string Type => "terminal.history.request";
    public string DeviceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public long BeforeSeq { get; init; }
    public int MaxChars { get; init; } = 20000;
}

public sealed class TerminalHistoryResponseMessage
{
    public string Type => "terminal.history.response";
    public string DeviceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string Data { get; init; } = string.Empty;
    public long NextBeforeSeq { get; init; }
    public bool HasMore { get; init; }
}

public sealed class TerminalResizeMessage
{
    public string Type => "terminal.resize";
    public string DeviceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public int Cols { get; init; }
    public int Rows { get; init; }
}

public sealed class TerminalCloseMessage
{
    public string Type => "terminal.close";
    public string DeviceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
}

public sealed class TerminalClosedMessage
{
    public string Type => "terminal.closed";
    public string DeviceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
}

// --- Control ownership ---

public sealed class ControlRequestMessage
{
    public string Type => "control.request";
    public string DeviceId { get; init; } = string.Empty;
    public string RequesterId { get; init; } = string.Empty;
}

public sealed class ControlGrantMessage
{
    public string Type => "control.grant";
    public string DeviceId { get; init; } = string.Empty;
    public string OwnerId { get; init; } = string.Empty;
}

public sealed class ControlForceDisconnectMessage
{
    public string Type => "control.force_disconnect";
    public string DeviceId { get; init; } = string.Empty;
}

// --- Group management ---

public sealed class GroupJoinRequestMessage
{
    public string Type => "group.join.request";
    public string? GroupSecret { get; init; }
    public string? InviteCode { get; init; }
    public string DeviceId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Os { get; init; } = string.Empty;
    public List<string> AvailableShells { get; init; } = new();
}

public sealed class GroupJoinAcceptedMessage
{
    public string Type => "group.join.accepted";
    public string GroupId { get; init; } = string.Empty;
    public List<GroupMemberInfo> Members { get; init; } = new();
    public string ServerDeviceId { get; init; } = string.Empty;
    public string? BoundMobileId { get; init; }
    /// <summary>Group secret returned to invite-based joiners for reconnect auth.</summary>
    public string? GroupSecret { get; init; }
}

public sealed class GroupJoinRejectedMessage
{
    public string Type => "group.join.rejected";
    public string Reason { get; init; } = string.Empty;
}

public sealed class GroupMemberJoinedMessage
{
    public string Type => "group.member.joined";
    public GroupMemberInfo Member { get; init; } = new();
}

public sealed class GroupMemberLeftMessage
{
    public string Type => "group.member.left";
    public string DeviceId { get; init; } = string.Empty;
}

public sealed class GroupMemberListMessage
{
    public string Type => "group.member.list";
    public List<GroupMemberInfo> Members { get; init; } = new();
}

public sealed class GroupKickMessage
{
    public string Type => "group.kick";
    public string DeviceId { get; init; } = string.Empty;
}

/// <summary>
/// Member info exchanged in group protocol messages.
/// </summary>
public sealed class GroupMemberInfo
{
    public string DeviceId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Os { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty; // "Server", "Member", "Mobile"
    public bool IsOnline { get; init; }
    public List<string> AvailableShells { get; init; } = new();
}

// --- Mobile binding ---

public sealed class MobileBindRequestMessage
{
    public string Type => "mobile.bind.request";
    public string GroupId { get; init; } = string.Empty;
    public string MobileDeviceId { get; init; } = string.Empty;
    public string MobileDisplayName { get; init; } = string.Empty;
}

public sealed class MobileBindAcceptedMessage
{
    public string Type => "mobile.bind.accepted";
    public string GroupId { get; init; } = string.Empty;
    public string MobileDeviceId { get; init; } = string.Empty;
}

public sealed class MobileBindRejectedMessage
{
    public string Type => "mobile.bind.rejected";
    public string Reason { get; init; } = string.Empty;
}

public sealed class MobileUnbindMessage
{
    public string Type => "mobile.unbind";
    public string GroupId { get; init; } = string.Empty;
}

// --- Authorization ---

public sealed class AuthRequestMessage
{
    public string Type => "auth.request";
    public string RequestId { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string RequesterId { get; init; } = string.Empty;
    public string RequesterName { get; init; } = string.Empty;
    public string? TargetDeviceId { get; init; }
    public string Description { get; init; } = string.Empty;
}

public sealed class AuthResponseMessage
{
    public string Type => "auth.response";
    public string RequestId { get; init; } = string.Empty;
    public bool Approved { get; init; }
}

// --- Panel login scan ---

public sealed class PanelLoginScanMessage
{
    public string Type => "panel.login.scan";
    public string RequestId { get; init; } = string.Empty;
    public string MobileDeviceId { get; init; } = string.Empty;
}

// --- Error ---

public sealed class ErrorMessage
{
    public string Type => "error";
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

// --- Relay designation ---

public sealed class RelayDesignateMessage
{
    public string Type => "relay.designate";
}

public sealed class RelayDesignatedMessage
{
    public string Type => "relay.designated";
    public string RelayUrl { get; init; } = string.Empty;
    public string GroupId { get; init; } = string.Empty;
    public string GroupSecret { get; init; } = string.Empty;
}

// --- Invite system ---

public sealed class InviteCreateRequestMessage
{
    public string Type => "invite.create.request";
}

public sealed class InviteCreateResponseMessage
{
    public string Type => "invite.create.response";
    public string InviteCode { get; init; } = string.Empty;
    public string RelayUrl { get; init; } = string.Empty;
    public string ExpiresAt { get; init; } = string.Empty;
}

// --- Device settings ---

public sealed class DeviceSettingsUpdateMessage
{
    public string Type => "device.settings.update";
    public string DeviceId { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
}

public sealed class DeviceSettingsUpdatedMessage
{
    public string Type => "device.settings.updated";
    public string DeviceId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

// --- Device kick (request from mobile to kick a member) ---

public sealed class DeviceKickMessage
{
    public string Type => "device.kick";
    public string DeviceId { get; init; } = string.Empty;
}

// --- Device kicked (notification sent to the kicked client) ---

public sealed class DeviceKickedMessage
{
    public string Type => "device.kicked";
    public string Reason { get; init; } = string.Empty;
}

// --- Group dissolve ---

public sealed class GroupDissolveMessage
{
    public string Type => "group.dissolve";
}

public sealed class GroupDissolvedMessage
{
    public string Type => "group.dissolved";
    public string Reason { get; init; } = string.Empty;
}

// --- Panel disconnect notification ---

public sealed class PanelDisconnectedMessage
{
    public string Type => "panel.disconnected";
    public string ClientId { get; init; } = string.Empty;
}

// --- Server migration ---

public sealed class GroupServerChangeRequestMessage
{
    public string Type => "group.server.change.request";
    public string NewServerDeviceId { get; init; } = string.Empty;
    public string RequesterId { get; init; } = string.Empty;
}

public sealed class GroupServerChangePrepareMessage
{
    public string Type => "group.server.change.prepare";
    public string NewServerUrl { get; init; } = string.Empty;
    public string GroupId { get; init; } = string.Empty;
    public string GroupSecret { get; init; } = string.Empty;
}

public sealed class GroupServerChangeCommitMessage
{
    public string Type => "group.server.change.commit";
    public string NewServerUrl { get; init; } = string.Empty;
    public string GroupId { get; init; } = string.Empty;
    public string GroupSecret { get; init; } = string.Empty;
}

// --- Group secret rotation ---

public sealed class GroupSecretRotateRequestMessage
{
    public string Type => "group.secret.rotate.request";
    public string RequesterId { get; init; } = string.Empty;
}

public sealed class GroupSecretRotateDoneMessage
{
    public string Type => "group.secret.rotate.done";
    public string NewSecret { get; init; } = string.Empty;
}
