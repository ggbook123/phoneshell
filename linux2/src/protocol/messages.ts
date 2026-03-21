// Protocol message types — must be wire-compatible with C# PhoneShell.Core.Protocol.Messages
// All field names are camelCase (matches C# JsonNamingPolicy.CamelCase serialization).

// --- Device management ---

export interface DeviceRegisterMessage {
  type: 'device.register';
  deviceId: string;
  displayName: string;
  os: string;
  availableShells: string[];
  mode?: 'standalone' | 'relay' | 'client';
}

export interface DeviceUnregisterMessage {
  type: 'device.unregister';
  deviceId: string;
}

export interface DeviceListRequestMessage {
  type: 'device.list.request';
}

export interface DeviceListMessage {
  type: 'device.list';
  devices: DeviceInfo[];
}

export interface DeviceInfo {
  deviceId: string;
  displayName: string;
  os: string;
  isOnline: boolean;
  availableShells: string[];
}

// --- Session management ---

export interface SessionListRequestMessage {
  type: 'session.list.request';
  deviceId: string;
}

export interface SessionListMessage {
  type: 'session.list';
  deviceId: string;
  sessions: SessionInfo[];
}

export interface SessionInfo {
  sessionId: string;
  shellId: string;
  title: string;
}

// --- Terminal session management ---

export interface TerminalOpenMessage {
  type: 'terminal.open';
  deviceId: string;
  shellId: string;
}

export interface TerminalOpenedMessage {
  type: 'terminal.opened';
  deviceId: string;
  sessionId: string;
  cols: number;
  rows: number;
}

export interface TerminalInputMessage {
  type: 'terminal.input';
  deviceId: string;
  sessionId: string;
  data: string;
}

export interface TerminalOutputMessage {
  type: 'terminal.output';
  deviceId: string;
  sessionId: string;
  data: string;
}

export interface TerminalHistoryRequestMessage {
  type: 'terminal.history.request';
  deviceId: string;
  sessionId: string;
  beforeSeq: number;
  maxChars: number;
}

export interface TerminalHistoryResponseMessage {
  type: 'terminal.history.response';
  deviceId: string;
  sessionId: string;
  data: string;
  nextBeforeSeq: number;
  hasMore: boolean;
}

export interface TerminalResizeMessage {
  type: 'terminal.resize';
  deviceId: string;
  sessionId: string;
  cols: number;
  rows: number;
}

export interface TerminalCloseMessage {
  type: 'terminal.close';
  deviceId: string;
  sessionId: string;
}

export interface TerminalClosedMessage {
  type: 'terminal.closed';
  deviceId: string;
  sessionId: string;
}

// --- Control ownership ---

export interface ControlRequestMessage {
  type: 'control.request';
  deviceId: string;
  requesterId: string;
}

export interface ControlGrantMessage {
  type: 'control.grant';
  deviceId: string;
  ownerId: string;
}

export interface ControlForceDisconnectMessage {
  type: 'control.force_disconnect';
  deviceId: string;
}

// --- Relay designation ---

export interface RelayDesignateMessage {
  type: 'relay.designate';
}

export interface RelayDesignatedMessage {
  type: 'relay.designated';
  relayUrl: string;
  groupId: string;
  groupSecret: string;
}

// --- Invite system ---

export interface InviteCreateRequestMessage {
  type: 'invite.create.request';
}

export interface InviteCreateResponseMessage {
  type: 'invite.create.response';
  inviteCode: string;
  relayUrl: string;
  expiresAt: string;
}

// --- Device settings ---

export interface DeviceSettingsUpdateMessage {
  type: 'device.settings.update';
  deviceId: string;
  displayName?: string;
}

export interface DeviceSettingsUpdatedMessage {
  type: 'device.settings.updated';
  deviceId: string;
  displayName: string;
}

// --- Device kick (new style) ---

export interface DeviceKickMessage {
  type: 'device.kick';
  deviceId: string;
}

export interface DeviceKickedMessage {
  type: 'device.kicked';
  reason: string;
}

// --- Group dissolve ---

export interface GroupDissolveMessage {
  type: 'group.dissolve';
}

export interface GroupDissolvedMessage {
  type: 'group.dissolved';
  reason: string;
}

// --- Panel disconnect notification ---

export interface PanelDisconnectedMessage {
  type: 'panel.disconnected';
  clientId: string;
}

// --- Group management ---

export interface GroupJoinRequestMessage {
  type: 'group.join.request';
  groupSecret?: string;
  inviteCode?: string;
  deviceId: string;
  displayName: string;
  os: string;
  availableShells: string[];
}

export interface GroupJoinAcceptedMessage {
  type: 'group.join.accepted';
  groupId: string;
  members: GroupMemberInfo[];
  serverDeviceId: string;
  boundMobileId?: string | null;
  groupSecret?: string;
}

export interface GroupJoinRejectedMessage {
  type: 'group.join.rejected';
  reason: string;
}

export interface GroupMemberJoinedMessage {
  type: 'group.member.joined';
  member: GroupMemberInfo;
}

export interface GroupMemberLeftMessage {
  type: 'group.member.left';
  deviceId: string;
}

export interface GroupMemberListMessage {
  type: 'group.member.list';
  members: GroupMemberInfo[];
}

export interface GroupKickMessage {
  type: 'group.kick';
  deviceId: string;
}

export interface GroupMemberInfo {
  deviceId: string;
  displayName: string;
  os: string;
  role: string; // "Server" | "Member" | "Mobile"
  isOnline: boolean;
  availableShells: string[];
}

// --- Mobile binding ---

export interface MobileBindRequestMessage {
  type: 'mobile.bind.request';
  groupId: string;
  mobileDeviceId: string;
  mobileDisplayName: string;
}

export interface MobileBindAcceptedMessage {
  type: 'mobile.bind.accepted';
  groupId: string;
  mobileDeviceId: string;
}

export interface MobileBindRejectedMessage {
  type: 'mobile.bind.rejected';
  reason: string;
}

export interface MobileUnbindMessage {
  type: 'mobile.unbind';
  groupId: string;
}

// --- Authorization ---

export interface AuthRequestMessage {
  type: 'auth.request';
  requestId: string;
  action: string;
  requesterId: string;
  requesterName: string;
  targetDeviceId?: string | null;
  description: string;
}

export interface AuthResponseMessage {
  type: 'auth.response';
  requestId: string;
  approved: boolean;
}

// --- Panel login scan ---

export interface PanelLoginScanMessage {
  type: 'panel.login.scan';
  requestId: string;
  mobileDeviceId: string;
}

// --- Error ---

export interface ErrorMessage {
  type: 'error';
  code: string;
  message: string;
}

// --- Server migration ---

export interface GroupServerChangeRequestMessage {
  type: 'group.server.change.request';
  newServerDeviceId: string;
  requesterId: string;
}

export interface GroupServerChangePrepareMessage {
  type: 'group.server.change.prepare';
  newServerUrl: string;
  groupId: string;
  groupSecret: string;
}

export interface GroupServerChangeCommitMessage {
  type: 'group.server.change.commit';
  newServerUrl: string;
  groupId: string;
  groupSecret: string;
}

// --- Group secret rotation ---

export interface GroupSecretRotateRequestMessage {
  type: 'group.secret.rotate.request';
  requesterId: string;
}

export interface GroupSecretRotateDoneMessage {
  type: 'group.secret.rotate.done';
  newSecret: string;
}

// --- Data models ---

export type MemberRole = 'Server' | 'Member' | 'Mobile';

export interface GroupInfo {
  groupId: string;
  groupSecret: string;
  serverDeviceId: string;
  boundMobileId?: string | null;
  createdAt: string;
  members: GroupMember[];
}

export interface GroupMember {
  deviceId: string;
  displayName: string;
  os: string;
  role: MemberRole;
  joinedAt: string;
  availableShells: string[];
}

export interface DeviceIdentity {
  deviceId: string;
  displayName: string;
  createdAt: string;
}

// Union type for all messages
export type Message =
  | DeviceRegisterMessage
  | DeviceUnregisterMessage
  | DeviceListRequestMessage
  | DeviceListMessage
  | SessionListRequestMessage
  | SessionListMessage
  | TerminalOpenMessage
  | TerminalOpenedMessage
  | TerminalInputMessage
  | TerminalOutputMessage
  | TerminalHistoryRequestMessage
  | TerminalHistoryResponseMessage
  | TerminalResizeMessage
  | TerminalCloseMessage
  | TerminalClosedMessage
  | ControlRequestMessage
  | ControlGrantMessage
  | ControlForceDisconnectMessage
  | GroupJoinRequestMessage
  | GroupJoinAcceptedMessage
  | GroupJoinRejectedMessage
  | GroupMemberJoinedMessage
  | GroupMemberLeftMessage
  | GroupMemberListMessage
  | GroupKickMessage
  | MobileBindRequestMessage
  | MobileBindAcceptedMessage
  | MobileBindRejectedMessage
  | MobileUnbindMessage
  | AuthRequestMessage
  | AuthResponseMessage
  | PanelLoginScanMessage
  | ErrorMessage
  | GroupServerChangeRequestMessage
  | GroupServerChangePrepareMessage
  | GroupServerChangeCommitMessage
  | GroupSecretRotateRequestMessage
  | GroupSecretRotateDoneMessage
  | RelayDesignateMessage
  | RelayDesignatedMessage
  | InviteCreateRequestMessage
  | InviteCreateResponseMessage
  | DeviceSettingsUpdateMessage
  | DeviceSettingsUpdatedMessage
  | DeviceKickMessage
  | DeviceKickedMessage
  | GroupDissolveMessage
  | GroupDissolvedMessage
  | PanelDisconnectedMessage;
