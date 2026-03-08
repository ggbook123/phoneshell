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

// Union type for all incoming messages (for reference; individual types used in practice)
export type ServerMessage =
  DeviceListMessage |
  TerminalOpenedMessage |
  TerminalOutputMessage |
  TerminalClosedMessage |
  ControlGrantMessage |
  ControlForceDisconnectMessage |
  ErrorMessage;
