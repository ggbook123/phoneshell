import { webSocket } from '@kit.NetworkKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { ConnectionState, StorageKeys } from '../common/Constants';
import { PreferencesUtil } from './PreferencesUtil';
import { DeviceInfo, GroupMemberInfo } from '../models/MessageTypes';

const DOMAIN = 0x0000;
const NET_TAG = 'PhoneShellNet';

// Message classes for protocol - must be explicit classes for ArkTS strict mode
class DeviceListRequestMsg {
  type: string = 'device.list.request';
}

class DeviceUnregisterMsg {
  type: string = 'device.unregister';
  deviceId: string = '';
}

class SessionListRequestMsg {
  type: string = 'session.list.request';
  deviceId: string = '';
}

class TerminalOpenMsg {
  type: string = 'terminal.open';
  deviceId: string = '';
  shellId: string = '';
}

class TerminalInputMsg {
  type: string = 'terminal.input';
  deviceId: string = '';
  sessionId: string = '';
  data: string = '';
}

class TerminalResizeMsg {
  type: string = 'terminal.resize';
  deviceId: string = '';
  sessionId: string = '';
  cols: number = 0;
  rows: number = 0;
}

class TerminalCloseMsg {
  type: string = 'terminal.close';
  deviceId: string = '';
  sessionId: string = '';
}

class GroupJoinRequestMsg {
  type: string = 'group.join.request';
  groupSecret: string = '';
  deviceId: string = '';
  displayName: string = '';
  os: string = 'HarmonyOS';
  availableShells: Array<string> = [];
}

class MobileBindRequestMsg {
  type: string = 'mobile.bind.request';
  groupId: string = '';
  mobileDeviceId: string = '';
  mobileDisplayName: string = '';
}

class MobileUnbindMsg {
  type: string = 'mobile.unbind';
  groupId: string = '';
}

class AuthResponseMsg {
  type: string = 'auth.response';
  requestId: string = '';
  approved: boolean = false;
}

class GroupKickMsg {
  type: string = 'group.kick';
  deviceId: string = '';
}

class GroupServerChangeRequestMsg {
  type: string = 'group.server.change.request';
  newServerDeviceId: string = '';
  requesterId: string = '';
}

class GroupSecretRotateRequestMsg {
  type: string = 'group.secret.rotate.request';
  requesterId: string = '';
}

class PanelLoginScanMsg {
  type: string = 'panel.login.scan';
  requestId: string = '';
  mobileDeviceId: string = '';
}

// Session state for multi-session management
export class SessionState {
  sessionId: string = '';
  deviceId: string = '';
  bufferedOutput: string = '';
  shellId: string = '';
}

// Callback wrapper classes for ArkTS (no call signatures allowed on interfaces)
class MessageCallbackWrapper {
  id: number = -1;
  callback: (type: string, data: Record<string, Object>) => void = (_t: string, _d: Record<string, Object>) => {};
}

class StateCallbackWrapper {
  id: number = -1;
  callback: (state: ConnectionState) => void = (_s: ConnectionState) => {};
}

class WebSocketServiceClass {
  private ws: webSocket.WebSocket | null = null;
  private serverUrl: string = '';
  private reconnectTimer: number = -1;
  private reconnectAttempts: number = 0;
  private maxReconnectAttempts: number = 10;
  private reconnectBaseDelay: number = 1000;
  private shouldReconnect: boolean = false;
  private connectionGeneration: number = 0;

  // Callbacks
  private messageCallbackWrappers: Array<MessageCallbackWrapper> = [];
  private stateCallbackWrappers: Array<StateCallbackWrapper> = [];
  private nextCallbackId: number = 1;

  // Current state
  private connectionState: ConnectionState = ConnectionState.DISCONNECTED;

  // Cached device list
  private cachedDevices: Array<DeviceInfo> = [];

  // Multi-session terminal state
  private sessions: Map<string, SessionState> = new Map();
  private activeSessionId: string = '';
  private activeDeviceId: string = '';
  private maxBufferedTerminalOutput: number = 50000;

  // Group state
  private groupId: string = '';
  private groupSecret: string = '';
  private cachedGroupMembers: Array<GroupMemberInfo> = [];
  private isGroupJoined: boolean = false;
  private mobileDeviceId: string = '';

  private normalizeString(value: Object | undefined | null, fallback: string = ''): string {
    if (typeof value === 'string') {
      return value;
    }
    return fallback;
  }

  private normalizeBoolean(value: Object | undefined | null, fallback: boolean): boolean {
    if (typeof value === 'boolean') {
      return value;
    }
    return fallback;
  }

  private normalizeStringArray(value: Object | undefined | null): Array<string> {
    let normalized: Array<string> = [];
    if (!Array.isArray(value)) {
      return normalized;
    }

    const rawItems: Array<Object> = value as Array<Object>;
    for (let i: number = 0; i < rawItems.length; i++) {
      const item: Object = rawItems[i];
      if (typeof item === 'string' && item.length > 0) {
        normalized.push(item as string);
      }
    }

    return normalized;
  }

  private normalizeDeviceList(value: Object | undefined | null): Array<DeviceInfo> {
    let normalized: Array<DeviceInfo> = [];
    if (!Array.isArray(value)) {
      return normalized;
    }

    const rawDevices: Array<Object> = value as Array<Object>;
    for (let i: number = 0; i < rawDevices.length; i++) {
      const rawDevice: Object = rawDevices[i];
      const deviceRecord: Record<string, Object> = rawDevice as Record<string, Object>;
      const deviceId: string = this.normalizeString(deviceRecord['deviceId']);
      const displayNameFallback: string = deviceId.length > 0 ? deviceId : 'Unknown device';
      const normalizedDevice: DeviceInfo = {
        deviceId: deviceId.length > 0 ? deviceId : displayNameFallback,
        displayName: this.normalizeString(deviceRecord['displayName'], displayNameFallback),
        os: this.normalizeString(deviceRecord['os'], 'Unknown'),
        isOnline: this.normalizeBoolean(deviceRecord['isOnline'], true),
        availableShells: this.normalizeStringArray(deviceRecord['availableShells'])
      } as DeviceInfo;
      normalized.push(normalizedDevice);
    }

    return normalized;
  }

  private resetActiveSession(): void {
    this.activeSessionId = '';
    this.activeDeviceId = '';
    this.sessions.clear();
  }

  private describeError(err: Object | undefined | null): string {
    if (err === undefined || err === null) {
      return 'unknown';
    }

    if (typeof err === 'string') {
      return err;
    }

    const errorRecord: Record<string, Object> = err as Record<string, Object>;
    const name: string = this.normalizeString(errorRecord['name']);
    const message: string = this.normalizeString(errorRecord['message']);
    if (name.length > 0 && message.length > 0) {
      return `${name}: ${message}`;
    }
    if (message.length > 0) {
      return message;
    }

    try {
      return JSON.stringify(err);
    } catch (_jsonErr) {
      return 'unknown';
    }
  }

  private emitError(message: string): void {
    hilog.error(DOMAIN, NET_TAG, 'emitError %{public}s', message);
    let payload: Record<string, Object> = {} as Record<string, Object>;
    payload['type'] = 'error';
    payload['message'] = message;
    this.emitMessage('error', payload);
  }

  private extractErrorMessage(err: BusinessError | null | undefined): string {
    if (err === undefined || err === null) {
      return '';
    }

    const rawMessage: Object | undefined = err.message as Object | undefined;
    if (typeof rawMessage === 'string' && rawMessage.length > 0) {
      return rawMessage;
    }

    const rawCode: Object | undefined = err.code as Object | undefined;
    if (typeof rawCode === 'number') {
      return `网络错误 (${rawCode})`;
    }

    return '';
  }

  addOnMessage(callback: (type: string, data: Record<string, Object>) => void): number {
    let wrapper: MessageCallbackWrapper = new MessageCallbackWrapper();
    wrapper.id = this.nextCallbackId;
    this.nextCallbackId++;
    wrapper.callback = callback;
    this.messageCallbackWrappers.push(wrapper);
    return wrapper.id;
  }

  removeOnMessage(callbackId: number): void {
    for (let i: number = 0; i < this.messageCallbackWrappers.length; i++) {
      if (this.messageCallbackWrappers[i].id === callbackId) {
        this.messageCallbackWrappers.splice(i, 1);
        break;
      }
    }
  }

  addOnStateChange(callback: (state: ConnectionState) => void): number {
    let wrapper: StateCallbackWrapper = new StateCallbackWrapper();
    wrapper.id = this.nextCallbackId;
    this.nextCallbackId++;
    wrapper.callback = callback;
    this.stateCallbackWrappers.push(wrapper);
    return wrapper.id;
  }

  removeOnStateChange(callbackId: number): void {
    for (let i: number = 0; i < this.stateCallbackWrappers.length; i++) {
      if (this.stateCallbackWrappers[i].id === callbackId) {
        this.stateCallbackWrappers.splice(i, 1);
        break;
      }
    }
  }

  getConnectionState(): ConnectionState {
    return this.connectionState;
  }

  getCachedDevices(): Array<DeviceInfo> {
    return this.cachedDevices.slice();
  }

  getActiveSessionId(): string {
    return this.normalizeString(this.activeSessionId);
  }

  setActiveSession(deviceId: string, sessionId: string): void {
    const normalizedDeviceId: string = this.normalizeString(deviceId);
    const normalizedSessionId: string = this.normalizeString(sessionId);
    this.activeDeviceId = normalizedDeviceId;
    this.activeSessionId = normalizedSessionId;
    // Ensure session exists in the map (e.g., reconnecting to existing session)
    if (normalizedSessionId.length > 0 && !this.sessions.has(normalizedSessionId)) {
      let session: SessionState = new SessionState();
      session.sessionId = normalizedSessionId;
      session.deviceId = normalizedDeviceId;
      session.bufferedOutput = '';
      this.sessions.set(normalizedSessionId, session);
    }
  }

  getActiveDeviceId(): string {
    return this.normalizeString(this.activeDeviceId);
  }

  getBufferedTerminalOutput(): string {
    const session: SessionState | undefined = this.sessions.get(this.activeSessionId);
    if (session !== undefined) {
      return this.normalizeString(session.bufferedOutput);
    }
    return '';
  }

  getSessionBufferedOutput(sessionId: string): string {
    const session: SessionState | undefined = this.sessions.get(sessionId);
    if (session !== undefined) {
      return this.normalizeString(session.bufferedOutput);
    }
    return '';
  }

  getSessionCount(): number {
    return this.sessions.size;
  }

  getSessionList(): Array<SessionState> {
    let result: Array<SessionState> = [];
    this.sessions.forEach((session: SessionState) => {
      result.push(session);
    });
    return result;
  }

  switchSession(sessionId: string): void {
    const session: SessionState | undefined = this.sessions.get(sessionId);
    if (session !== undefined) {
      this.activeSessionId = session.sessionId;
      this.activeDeviceId = session.deviceId;
      hilog.info(DOMAIN, NET_TAG, 'switchSession to %{public}s device=%{public}s',
        session.sessionId, session.deviceId);
    }
  }

  hasSession(sessionId: string): boolean {
    return this.sessions.has(sessionId);
  }

  connect(url: string): void {
    if (this.connectionState === ConnectionState.CONNECTING ||
        this.connectionState === ConnectionState.CONNECTED) {
      this.disconnect();
    }

    this.serverUrl = this.normalizeString(url).trim();
    if (this.serverUrl.length === 0) {
      this.emitError('连接服务器失败');
      this.updateState(ConnectionState.DISCONNECTED);
      return;
    }
    hilog.info(DOMAIN, NET_TAG, 'connect %{public}s', this.serverUrl);
    this.shouldReconnect = true;
    this.reconnectAttempts = 0;
    this.doConnect();
  }

  private cleanupSocket(socket: webSocket.WebSocket): void {
    try {
      socket.off('open');
      socket.off('message');
      socket.off('close');
      socket.off('error');
    } catch (_e) {
      // Ignore off() errors
    }
    socket.close().catch((closeErr: BusinessError) => {
      hilog.error(DOMAIN, NET_TAG, 'cleanupSocket close error %{public}s', this.describeError(closeErr));
    });
  }

  private doConnect(): void {
    this.connectionGeneration++;
    const generation: number = this.connectionGeneration;

    // Clean up any existing socket before creating a new one (Bug #4)
    if (this.ws !== null) {
      this.cleanupSocket(this.ws);
      this.ws = null;
    }

    this.updateState(ConnectionState.CONNECTING);
    hilog.info(DOMAIN, NET_TAG, 'doConnect start generation=%{public}d', generation);

    this.ws = webSocket.createWebSocket();

    this.ws.on('open', (err: BusinessError, value: Object) => {
      if (generation !== this.connectionGeneration) {
        return;
      }
      if (err) {
        const errorMessage: string = this.extractErrorMessage(err);
        if (errorMessage.length > 0) {
          this.emitError(errorMessage);
        } else {
          this.emitError('连接服务器失败');
        }
        this.updateState(ConnectionState.DISCONNECTED);
        if (this.shouldReconnect) {
          this.scheduleReconnect();
        }
        return;
      }
      hilog.info(DOMAIN, NET_TAG, 'websocket open');
      this.reconnectAttempts = 0;
      this.updateState(ConnectionState.CONNECTED);
    });

    this.ws.on('message', (err: BusinessError, value: string | ArrayBuffer) => {
      if (generation !== this.connectionGeneration) {
        return;
      }
      if (err) {
        return;
      }
      if (typeof value === 'string') {
        hilog.info(DOMAIN, NET_TAG, 'message received len=%{public}d', value.length);
        this.handleMessage(value);
      }
    });

    this.ws.on('close', (err: BusinessError, value: webSocket.CloseResult) => {
      if (generation !== this.connectionGeneration) {
        return;
      }
      hilog.warn(DOMAIN, NET_TAG, 'websocket close code=%{public}d reason=%{public}s',
        value.code, this.normalizeString(value.reason));
      const errorMessage: string = this.extractErrorMessage(err);
      if (errorMessage.length > 0) {
        this.emitError(errorMessage);
      }
      this.updateState(ConnectionState.DISCONNECTED);
      if (this.shouldReconnect) {
        this.scheduleReconnect();
      }
    });

    this.ws.on('error', (err: BusinessError) => {
      if (generation !== this.connectionGeneration) {
        return;
      }
      hilog.error(DOMAIN, NET_TAG, 'websocket error %{public}s', JSON.stringify(err));
      const errorMessage: string = this.extractErrorMessage(err);
      if (errorMessage.length > 0) {
        this.emitError(errorMessage);
      } else {
        this.emitError('网络错误');
      }
      this.updateState(ConnectionState.DISCONNECTED);
      if (this.shouldReconnect) {
        this.scheduleReconnect();
      }
    });

    this.ws.connect(this.serverUrl).catch((err: BusinessError) => {
      if (generation !== this.connectionGeneration) {
        return;
      }
      hilog.error(DOMAIN, NET_TAG, 'connect catch %{public}s', JSON.stringify(err));
      const errorMessage: string = this.extractErrorMessage(err);
      if (errorMessage.length > 0) {
        this.emitError(errorMessage);
      } else {
        this.emitError('连接服务器失败');
      }
      this.updateState(ConnectionState.DISCONNECTED);
      if (this.shouldReconnect) {
        this.scheduleReconnect();
      }
    });
  }

  disconnect(): void {
    hilog.info(DOMAIN, NET_TAG, 'disconnect called');
    this.shouldReconnect = false;
    this.connectionGeneration++;
    this.clearReconnectTimer();
    this.resetActiveSession();

    const activeSocket: webSocket.WebSocket | null = this.ws;
    this.ws = null;

    if (activeSocket !== null) {
      this.cleanupSocket(activeSocket);
    }
    this.updateState(ConnectionState.DISCONNECTED);
  }

  send(json: string): void {
    const payload: string = this.normalizeString(json);
    if (payload.length === 0) {
      return;
    }

    if (this.ws !== null && this.connectionState === ConnectionState.CONNECTED) {
      hilog.info(DOMAIN, NET_TAG, 'send len=%{public}d', payload.length);
      this.ws.send(payload).catch((err: BusinessError) => {
        hilog.error(DOMAIN, NET_TAG, 'send error %{public}s', this.describeError(err));
      });
    }
  }

  // --- High-level protocol methods ---

  requestDeviceList(): void {
    let msg: DeviceListRequestMsg = new DeviceListRequestMsg();
    this.send(JSON.stringify(msg));
  }

  unregisterDevice(deviceId: string): void {
    const normalizedDeviceId: string = this.normalizeString(deviceId);
    if (normalizedDeviceId.length === 0) {
      return;
    }
    hilog.info(DOMAIN, NET_TAG, 'unregisterDevice device=%{public}s', normalizedDeviceId);
    let msg: DeviceUnregisterMsg = new DeviceUnregisterMsg();
    msg.deviceId = normalizedDeviceId;
    this.send(JSON.stringify(msg));
  }

  requestSessionList(deviceId: string): void {
    const normalizedDeviceId: string = this.normalizeString(deviceId);
    if (normalizedDeviceId.length === 0) {
      return;
    }
    hilog.info(DOMAIN, NET_TAG, 'requestSessionList device=%{public}s', normalizedDeviceId);
    let msg: SessionListRequestMsg = new SessionListRequestMsg();
    msg.deviceId = normalizedDeviceId;
    this.send(JSON.stringify(msg));
  }

  openTerminal(deviceId: string, shellId: string): void {
    const normalizedDeviceId: string = this.normalizeString(deviceId);
    if (normalizedDeviceId.length === 0) {
      return;
    }

    hilog.info(DOMAIN, NET_TAG, 'openTerminal device=%{public}s shell=%{public}s',
      normalizedDeviceId, this.normalizeString(shellId));
    this.activeDeviceId = normalizedDeviceId;
    let msg: TerminalOpenMsg = new TerminalOpenMsg();
    msg.type = 'terminal.open';
    msg.deviceId = normalizedDeviceId;
    msg.shellId = this.normalizeString(shellId);
    this.send(JSON.stringify(msg));
  }

  sendTerminalInput(data: string, targetSessionId: string = ''): void {
    const sessionId: string = targetSessionId.length > 0 ? targetSessionId : this.getActiveSessionId();
    const payload: string = this.normalizeString(data);
    if (sessionId.length > 0 && payload.length > 0) {
      const session: SessionState | undefined = this.sessions.get(sessionId);
      const deviceId: string = session !== undefined ? session.deviceId : this.getActiveDeviceId();
      hilog.info(DOMAIN, NET_TAG, 'sendTerminalInput session=%{public}s len=%{public}d',
        sessionId, payload.length);
      let msg: TerminalInputMsg = new TerminalInputMsg();
      msg.type = 'terminal.input';
      msg.deviceId = deviceId;
      msg.sessionId = sessionId;
      msg.data = payload;
      this.send(JSON.stringify(msg));
    }
  }

  resizeTerminal(cols: number, rows: number, targetSessionId: string = ''): void {
    const sessionId: string = targetSessionId.length > 0 ? targetSessionId : this.getActiveSessionId();
    const session: SessionState | undefined = this.sessions.get(sessionId);
    const deviceId: string = session !== undefined ? session.deviceId : this.getActiveDeviceId();
    if (sessionId.length > 0 && deviceId.length > 0 && cols > 0 && rows > 0) {
      hilog.info(DOMAIN, NET_TAG, 'resizeTerminal session=%{public}s %d x %d', sessionId, cols, rows);
      let msg: TerminalResizeMsg = new TerminalResizeMsg();
      msg.type = 'terminal.resize';
      msg.deviceId = deviceId;
      msg.sessionId = sessionId;
      msg.cols = cols;
      msg.rows = rows;
      this.send(JSON.stringify(msg));
    }
  }

  closeTerminal(targetSessionId: string = ''): void {
    const sessionId: string = targetSessionId.length > 0 ? targetSessionId : this.getActiveSessionId();
    const session: SessionState | undefined = this.sessions.get(sessionId);
    const deviceId: string = session !== undefined ? session.deviceId : this.getActiveDeviceId();
    hilog.warn(DOMAIN, NET_TAG, 'closeTerminal session=%{public}s device=%{public}s', sessionId, deviceId);
    if (sessionId.length > 0 && deviceId.length > 0) {
      let msg: TerminalCloseMsg = new TerminalCloseMsg();
      msg.type = 'terminal.close';
      msg.deviceId = deviceId;
      msg.sessionId = sessionId;
      this.send(JSON.stringify(msg));
    }
    // Remove session from map
    this.sessions.delete(sessionId);
    // If we closed the active session, pick next one or clear
    if (sessionId === this.activeSessionId) {
      this.activeSessionId = '';
      this.activeDeviceId = '';
      this.sessions.forEach((s: SessionState) => {
        if (this.activeSessionId.length === 0) {
          this.activeSessionId = s.sessionId;
          this.activeDeviceId = s.deviceId;
        }
      });
    }
  }

  // --- Group methods ---

  joinGroup(groupSecret: string, deviceId: string, displayName: string): void {
    this.groupSecret = this.normalizeString(groupSecret);
    this.mobileDeviceId = this.normalizeString(deviceId);
    if (this.groupSecret.length === 0) {
      return;
    }
    hilog.info(DOMAIN, NET_TAG, 'joinGroup secret=%{public}s...', this.groupSecret.substring(0, 8));
    let msg: GroupJoinRequestMsg = new GroupJoinRequestMsg();
    msg.groupSecret = this.groupSecret;
    msg.deviceId = this.normalizeString(deviceId);
    msg.displayName = this.normalizeString(displayName, 'HarmonyOS Device');
    msg.os = 'HarmonyOS';
    this.send(JSON.stringify(msg));
  }

  bindMobile(groupId: string, mobileDeviceId: string, mobileDisplayName: string): void {
    hilog.info(DOMAIN, NET_TAG, 'bindMobile groupId=%{public}s', groupId);
    let msg: MobileBindRequestMsg = new MobileBindRequestMsg();
    msg.groupId = this.normalizeString(groupId);
    msg.mobileDeviceId = this.normalizeString(mobileDeviceId);
    msg.mobileDisplayName = this.normalizeString(mobileDisplayName, 'HarmonyOS Device');
    this.send(JSON.stringify(msg));
  }

  unbindMobile(groupId: string = ''): void {
    const resolvedGroupId: string = this.normalizeString(groupId, this.groupId);
    if (resolvedGroupId.length === 0) {
      return;
    }
    hilog.info(DOMAIN, NET_TAG, 'unbindMobile groupId=%{public}s', resolvedGroupId);
    let msg: MobileUnbindMsg = new MobileUnbindMsg();
    msg.groupId = resolvedGroupId;
    this.send(JSON.stringify(msg));
  }

  respondAuth(requestId: string, approved: boolean): void {
    hilog.info(DOMAIN, NET_TAG, 'respondAuth id=%{public}s approved=%{public}s', requestId, approved.toString());
    let msg: AuthResponseMsg = new AuthResponseMsg();
    msg.requestId = this.normalizeString(requestId);
    msg.approved = approved;
    this.send(JSON.stringify(msg));
  }

  kickMember(deviceId: string): void {
    hilog.info(DOMAIN, NET_TAG, 'kickMember device=%{public}s', deviceId);
    let msg: GroupKickMsg = new GroupKickMsg();
    msg.deviceId = this.normalizeString(deviceId);
    this.send(JSON.stringify(msg));
  }

  sendServerChangeRequest(newServerDeviceId: string): void {
    hilog.info(DOMAIN, NET_TAG, 'sendServerChangeRequest newServer=%{public}s', newServerDeviceId);
    let msg: GroupServerChangeRequestMsg = new GroupServerChangeRequestMsg();
    msg.newServerDeviceId = this.normalizeString(newServerDeviceId);
    msg.requesterId = this.normalizeString(this.mobileDeviceId);
    this.send(JSON.stringify(msg));
  }

  sendSecretRotateRequest(): void {
    hilog.info(DOMAIN, NET_TAG, 'sendSecretRotateRequest');
    let msg: GroupSecretRotateRequestMsg = new GroupSecretRotateRequestMsg();
    msg.requesterId = this.normalizeString(this.mobileDeviceId);
    this.send(JSON.stringify(msg));
  }

  sendPanelLoginScan(requestId: string, mobileDeviceId: string): void {
    hilog.info(DOMAIN, NET_TAG, 'sendPanelLoginScan requestId=%{public}s', requestId);
    let msg: PanelLoginScanMsg = new PanelLoginScanMsg();
    msg.requestId = this.normalizeString(requestId);
    msg.mobileDeviceId = this.normalizeString(mobileDeviceId);
    this.send(JSON.stringify(msg));
  }

  getGroupId(): string {
    return this.normalizeString(this.groupId);
  }

  getIsGroupJoined(): boolean {
    return this.isGroupJoined;
  }

  getCachedGroupMembers(): Array<GroupMemberInfo> {
    return this.cachedGroupMembers.slice();
  }

  getMobileDeviceId(): string {
    return this.normalizeString(this.mobileDeviceId);
  }

  resetGroupState(): void {
    this.groupId = '';
    this.groupSecret = '';
    this.isGroupJoined = false;
    this.cachedGroupMembers = [];
    this.cachedDevices = [];
  }

  private normalizeGroupMemberList(value: Object | undefined | null): Array<GroupMemberInfo> {
    let normalized: Array<GroupMemberInfo> = [];
    if (!Array.isArray(value)) {
      return normalized;
    }
    const rawMembers: Array<Object> = value as Array<Object>;
    for (let i: number = 0; i < rawMembers.length; i++) {
      const raw: Object = rawMembers[i];
      const record: Record<string, Object> = raw as Record<string, Object>;
      const member: GroupMemberInfo = {
        deviceId: this.normalizeString(record['deviceId']),
        displayName: this.normalizeString(record['displayName'], 'Unknown'),
        os: this.normalizeString(record['os'], 'Unknown'),
        role: this.normalizeString(record['role'], 'Member'),
        isOnline: this.normalizeBoolean(record['isOnline'], false),
        availableShells: this.normalizeStringArray(record['availableShells'])
      } as GroupMemberInfo;
      normalized.push(member);
    }
    return normalized;
  }

  // --- Internal methods ---

  private handleMessage(raw: string): void {
    try {
      const payload: string = this.normalizeString(raw);
      if (payload.length === 0) {
        return;
      }

      const parsed: Record<string, Object> = JSON.parse(payload) as Record<string, Object>;
      const typeValue: Object | undefined = parsed['type'];
      if (typeof typeValue !== 'string' || typeValue.length === 0) {
        return;
      }
      const msgType: string = typeValue as string;
      hilog.info(DOMAIN, NET_TAG, 'handleMessage type=%{public}s', msgType);

      // Update cached data for specific message types
      if (msgType === 'device.list') {
        const devices: Object | undefined = parsed['devices'];
        this.cachedDevices = this.normalizeDeviceList(devices);
      } else if (msgType === 'device.unregister') {
        const unboundDeviceId: string = this.normalizeString(parsed['deviceId']);
        if (unboundDeviceId.length > 0 && unboundDeviceId === this.mobileDeviceId) {
          hilog.warn(DOMAIN, NET_TAG, 'mobile unbound by server');
          this.resetGroupState();
          PreferencesUtil.setString(StorageKeys.GROUP_SECRET, '');
          this.disconnect();
        }
      } else if (msgType === 'group.join.accepted') {
        this.groupId = this.normalizeString(parsed['groupId']);
        this.cachedGroupMembers = this.normalizeGroupMemberList(parsed['members']);
        this.isGroupJoined = true;
        hilog.info(DOMAIN, NET_TAG, 'group joined: %{public}s members=%{public}d',
          this.groupId, this.cachedGroupMembers.length);
      } else if (msgType === 'group.join.rejected') {
        this.isGroupJoined = false;
        hilog.warn(DOMAIN, NET_TAG, 'group join rejected: %{public}s',
          this.normalizeString(parsed['reason']));
      } else if (msgType === 'group.member.joined') {
        const memberObj: Object | undefined = parsed['member'];
        if (memberObj !== undefined && memberObj !== null) {
          const record: Record<string, Object> = memberObj as Record<string, Object>;
          const newMember: GroupMemberInfo = {
            deviceId: this.normalizeString(record['deviceId']),
            displayName: this.normalizeString(record['displayName'], 'Unknown'),
            os: this.normalizeString(record['os'], 'Unknown'),
            role: this.normalizeString(record['role'], 'Member'),
            isOnline: this.normalizeBoolean(record['isOnline'], true),
            availableShells: this.normalizeStringArray(record['availableShells'])
          } as GroupMemberInfo;
          // Replace existing or add new
          let found: boolean = false;
          for (let i: number = 0; i < this.cachedGroupMembers.length; i++) {
            if (this.cachedGroupMembers[i].deviceId === newMember.deviceId) {
              this.cachedGroupMembers[i] = newMember;
              found = true;
              break;
            }
          }
          if (!found) {
            this.cachedGroupMembers.push(newMember);
          }
        }
      } else if (msgType === 'group.member.left') {
        const leftDeviceId: string = this.normalizeString(parsed['deviceId']);
        for (let i: number = 0; i < this.cachedGroupMembers.length; i++) {
          if (this.cachedGroupMembers[i].deviceId === leftDeviceId) {
            let offlineMember: GroupMemberInfo = {
              deviceId: this.cachedGroupMembers[i].deviceId,
              displayName: this.cachedGroupMembers[i].displayName,
              os: this.cachedGroupMembers[i].os,
              role: this.cachedGroupMembers[i].role,
              isOnline: false,
              availableShells: this.cachedGroupMembers[i].availableShells
            } as GroupMemberInfo;
            this.cachedGroupMembers[i] = offlineMember;
            break;
          }
        }
      } else if (msgType === 'group.member.list') {
        this.cachedGroupMembers = this.normalizeGroupMemberList(parsed['members']);
      } else if (msgType === 'mobile.bind.accepted') {
        hilog.info(DOMAIN, NET_TAG, 'mobile bind accepted');
      } else if (msgType === 'mobile.bind.rejected') {
        hilog.warn(DOMAIN, NET_TAG, 'mobile bind rejected: %{public}s',
          this.normalizeString(parsed['reason']));
      } else if (msgType === 'terminal.output') {
        const outputSessionId: string = this.normalizeString(parsed['sessionId']);
        const outputData: string = this.normalizeString(parsed['data']);
        if (outputData.length > 0 && outputSessionId.length > 0) {
          const session: SessionState | undefined = this.sessions.get(outputSessionId);
          if (session !== undefined) {
            session.bufferedOutput += outputData;
            if (session.bufferedOutput.length > this.maxBufferedTerminalOutput) {
              session.bufferedOutput =
                session.bufferedOutput.substring(
                  session.bufferedOutput.length - this.maxBufferedTerminalOutput
                );
            }
          }
        }
      } else if (msgType === 'terminal.opened') {
        const sessionId: string = this.normalizeString(parsed['sessionId']);
        const deviceId: string = this.normalizeString(parsed['deviceId'], this.activeDeviceId);
        const shellId: string = this.normalizeString(parsed['shellId']);
        if (sessionId.length > 0) {
          // Create new session entry in map
          let newSession: SessionState = new SessionState();
          newSession.sessionId = sessionId;
          newSession.deviceId = deviceId;
          newSession.shellId = shellId;
          newSession.bufferedOutput = '';
          this.sessions.set(sessionId, newSession);
          // Set as active
          this.activeSessionId = sessionId;
          this.activeDeviceId = deviceId;
          hilog.info(DOMAIN, NET_TAG, 'session created: %{public}s device=%{public}s total=%{public}d',
            sessionId, deviceId, this.sessions.size);
        }
      } else if (msgType === 'terminal.closed') {
        const closedSessionId: string = this.normalizeString(parsed['sessionId']);
        if (closedSessionId.length > 0) {
          this.sessions.delete(closedSessionId);
          hilog.info(DOMAIN, NET_TAG, 'session removed: %{public}s remaining=%{public}d',
            closedSessionId, this.sessions.size);
          // If the closed session was the active one, switch to next or clear
          if (closedSessionId === this.activeSessionId) {
            this.activeSessionId = '';
            this.activeDeviceId = '';
            // Pick the first remaining session
            this.sessions.forEach((s: SessionState) => {
              if (this.activeSessionId.length === 0) {
                this.activeSessionId = s.sessionId;
                this.activeDeviceId = s.deviceId;
              }
            });
          }
        } else {
          // No sessionId specified — clear all
          this.activeSessionId = '';
          this.activeDeviceId = '';
          this.sessions.clear();
        }
      } else if (msgType === 'group.server.change.commit') {
        const newServerUrl: string = this.normalizeString(parsed['newServerUrl']);
        const newGroupSecret: string = this.normalizeString(parsed['groupSecret']);
        if (newServerUrl.length > 0) {
          hilog.info(DOMAIN, NET_TAG, 'server migration: switching to %{public}s', newServerUrl);
          this.groupSecret = newGroupSecret;
          const savedMobileDeviceId: string = this.mobileDeviceId;
          const savedGroupSecret: string = this.groupSecret;
          // Reconnect to new server
          this.disconnect();
          this.connect(newServerUrl);
          // After connecting, rejoin group and rebind mobile
          if (savedGroupSecret.length > 0 && savedMobileDeviceId.length > 0) {
            const migrationStateListenerId: number = this.addOnStateChange((state: ConnectionState) => {
              if (state === ConnectionState.CONNECTED) {
                this.removeOnStateChange(migrationStateListenerId);
                this.joinGroup(savedGroupSecret, savedMobileDeviceId, 'HarmonyOS Device');
                const migrationMsgListenerId: number = this.addOnMessage((type: string, _data: Record<string, Object>) => {
                  if (type === 'group.join.accepted') {
                    this.removeOnMessage(migrationMsgListenerId);
                    const joinedGroupId: string = this.getGroupId();
                    this.bindMobile(joinedGroupId, savedMobileDeviceId, 'HarmonyOS Device');
                  } else if (type === 'group.join.rejected') {
                    this.removeOnMessage(migrationMsgListenerId);
                    hilog.warn(DOMAIN, NET_TAG, 'server migration: rejoin rejected');
                  }
                });
              }
            });
          }
        }
      } else if (msgType === 'group.secret.rotate.done') {
        const newSecret: string = this.normalizeString(parsed['newSecret']);
        if (newSecret.length > 0) {
          hilog.info(DOMAIN, NET_TAG, 'group secret rotated');
          this.groupSecret = newSecret;
        }
      }

      this.emitMessage(msgType, parsed);
    } catch (err) {
      hilog.error(DOMAIN, NET_TAG, 'handleMessage error %{public}s', this.describeError(err));
    }
  }

  private updateState(state: ConnectionState): void {
    this.connectionState = state;
    hilog.info(DOMAIN, NET_TAG, 'state=%{public}d', state);
    let listeners: Array<StateCallbackWrapper> = [];
    for (let i: number = 0; i < this.stateCallbackWrappers.length; i++) {
      listeners.push(this.stateCallbackWrappers[i]);
    }
    for (let i: number = 0; i < listeners.length; i++) {
      try {
        listeners[i].callback(state);
      } catch (err) {
        hilog.error(DOMAIN, NET_TAG,
          'state listener error id=%{public}d state=%{public}d err=%{public}s',
          listeners[i].id, state, this.describeError(err));
      }
    }
  }

  private emitMessage(type: string, data: Record<string, Object>): void {
    let listeners: Array<MessageCallbackWrapper> = [];
    for (let i: number = 0; i < this.messageCallbackWrappers.length; i++) {
      listeners.push(this.messageCallbackWrappers[i]);
    }
    for (let i: number = 0; i < listeners.length; i++) {
      try {
        listeners[i].callback(type, data);
      } catch (err) {
        hilog.error(DOMAIN, NET_TAG,
          'message listener error id=%{public}d type=%{public}s err=%{public}s',
          listeners[i].id, type, this.describeError(err));
      }
    }
  }

  private scheduleReconnect(): void {
    if (this.reconnectAttempts >= this.maxReconnectAttempts) {
      hilog.warn(DOMAIN, NET_TAG, 'scheduleReconnect stop at max attempts');
      this.shouldReconnect = false;
      return;
    }

    this.clearReconnectTimer();
    const delay: number = Math.min(
      this.reconnectBaseDelay * Math.pow(2, this.reconnectAttempts),
      30000
    );
    this.reconnectAttempts++;
    hilog.warn(DOMAIN, NET_TAG, 'scheduleReconnect attempt=%{public}d delay=%{public}d',
      this.reconnectAttempts, delay);

    this.reconnectTimer = setTimeout(() => {
      if (this.shouldReconnect) {
        hilog.info(DOMAIN, NET_TAG, 'reconnect timer fired');
        this.doConnect();
      }
    }, delay) as number;
  }

  private clearReconnectTimer(): void {
    if (this.reconnectTimer !== -1) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = -1;
    }
  }
}

// Singleton instance
export const WebSocketService: WebSocketServiceClass = new WebSocketServiceClass();
