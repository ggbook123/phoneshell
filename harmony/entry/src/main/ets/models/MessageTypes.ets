// Protocol message types aligned with PC-side PhoneShell.Core.Protocol.Messages
// All JSON fields use camelCase naming (matching C# JsonNamingPolicy.CamelCase)

export interface BaseMessage {
  type: string;
}

// --- Device management ---

export interface DeviceRegisterMessage extends BaseMessage {
  type: 'device.register';
  deviceId: string;
  displayName: string;
  os: string;
  availableShells: Array<string>;
}

export interface DeviceUnregisterMessage extends BaseMessage {
  type: 'device.unregister';
  deviceId: string;
}

export interface DeviceListRequestMessage extends BaseMessage {
  type: 'device.list.request';
}

export interface DeviceListMessage extends BaseMessage {
  type: 'device.list';
  devices: Array<DeviceInfo>;
}

export interface DeviceInfo {
  deviceId: string;
  displayName: string;
  os: string;
  isOnline: boolean;
  availableShells: Array<string>;
}

// --- Session management ---

export interface SessionListRequestMessage extends BaseMessage {
  type: 'session.list.request';
  deviceId: string;
}

export interface SessionListMessage extends BaseMessage {
  type: 'session.list';
  deviceId: string;
  sessions: Array<SessionInfo>;
}

export interface SessionInfo {
  sessionId: string;
  shellId: string;
  title: string;
}

// --- Terminal session management ---

export interface TerminalOpenMessage extends BaseMessage {
  type: 'terminal.open';
  deviceId: string;
  shellId: string;
}

export interface TerminalOpenedMessage extends BaseMessage {
  type: 'terminal.opened';
  deviceId: string;
  sessionId: string;
  cols: number;
  rows: number;
}

export interface TerminalInputMessage extends BaseMessage {
  type: 'terminal.input';
  deviceId: string;
  sessionId: string;
  data: string;
}

export interface TerminalOutputMessage extends BaseMessage {
  type: 'terminal.output';
  deviceId: string;
  sessionId: string;
  data: string;
}

export interface TerminalResizeMessage extends BaseMessage {
  type: 'terminal.resize';
  deviceId: string;
  sessionId: string;
  cols: number;
  rows: number;
}

export interface TerminalCloseMessage extends BaseMessage {
  type: 'terminal.close';
  deviceId: string;
  sessionId: string;
}

export interface TerminalClosedMessage extends BaseMessage {
  type: 'terminal.closed';
  deviceId: string;
  sessionId: string;
}

// --- Control ownership ---

export interface ControlRequestMessage extends BaseMessage {
  type: 'control.request';
  deviceId: string;
  requesterId: string;
}

export interface ControlGrantMessage extends BaseMessage {
  type: 'control.grant';
  deviceId: string;
  ownerId: string;
}

export interface ControlForceDisconnectMessage extends BaseMessage {
  type: 'control.force_disconnect';
  deviceId: string;
}

// --- Error ---

export interface ErrorMessage extends BaseMessage {
  type: 'error';
  code: string;
  message: string;
}

// --- Group management ---

export interface GroupJoinRequestMessage extends BaseMessage {
  type: 'group.join.request';
  groupSecret: string;
  deviceId: string;
  displayName: string;
  os: string;
  availableShells: Array<string>;
}

export interface GroupJoinAcceptedMessage extends BaseMessage {
  type: 'group.join.accepted';
  groupId: string;
  members: Array<GroupMemberInfo>;
  serverDeviceId: string;
  boundMobileId: string | null;
}

export interface GroupJoinRejectedMessage extends BaseMessage {
  type: 'group.join.rejected';
  reason: string;
}

export interface GroupMemberJoinedMessage extends BaseMessage {
  type: 'group.member.joined';
  member: GroupMemberInfo;
}

export interface GroupMemberLeftMessage extends BaseMessage {
  type: 'group.member.left';
  deviceId: string;
}

export interface GroupMemberListMessage extends BaseMessage {
  type: 'group.member.list';
  members: Array<GroupMemberInfo>;
}

export interface GroupKickMessage extends BaseMessage {
  type: 'group.kick';
  deviceId: string;
}

export interface GroupMemberInfo {
  deviceId: string;
  displayName: string;
  os: string;
  role: string; // "Server", "Member", "Mobile"
  isOnline: boolean;
  availableShells: Array<string>;
}

// --- Mobile binding ---

export interface MobileBindRequestMessage extends BaseMessage {
  type: 'mobile.bind.request';
  groupId: string;
  mobileDeviceId: string;
  mobileDisplayName: string;
}

export interface MobileBindAcceptedMessage extends BaseMessage {
  type: 'mobile.bind.accepted';
  groupId: string;
  mobileDeviceId: string;
}

export interface MobileBindRejectedMessage extends BaseMessage {
  type: 'mobile.bind.rejected';
  reason: string;
}

export interface MobileUnbindMessage extends BaseMessage {
  type: 'mobile.unbind';
  groupId: string;
}

// --- Authorization ---

export interface AuthRequestMessage extends BaseMessage {
  type: 'auth.request';
  requestId: string;
  action: string;
  requesterId: string;
  requesterName: string;
  targetDeviceId: string | null;
  description: string;
}

export interface AuthResponseMessage extends BaseMessage {
  type: 'auth.response';
  requestId: string;
  approved: boolean;
}

// Union type for all incoming messages (for reference; individual types used in practice)
export type ServerMessage =
  DeviceListMessage |
  TerminalOpenedMessage |
  TerminalOutputMessage |
  TerminalClosedMessage |
  ControlGrantMessage |
  ControlForceDisconnectMessage |
  GroupJoinAcceptedMessage |
  GroupJoinRejectedMessage |
  GroupMemberJoinedMessage |
  GroupMemberLeftMessage |
  GroupMemberListMessage |
  MobileBindAcceptedMessage |
  MobileBindRejectedMessage |
  AuthRequestMessage |
  ErrorMessage;
