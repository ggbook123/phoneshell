import { hilog } from '@kit.PerformanceAnalysisKit';
import { http } from '@kit.NetworkKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { ConnectionState, DeviceMode, StorageKeys } from '../common/Constants';
import { DeviceConnection } from './DeviceConnection';
import { PreferencesUtil } from './PreferencesUtil';
import { DeviceInfo, GroupMemberInfo } from '../models/MessageTypes';

const DOMAIN = 0x0000;
const TAG = 'ConnectionManager';

// ArkTS strict-mode callback wrapper classes
class DeviceListCallbackWrapper {
  id: number = -1;
  callback: (devices: Array<DeviceInfo>) => void = (_d: Array<DeviceInfo>) => {};
}

class AuthRequestCallbackWrapper {
  id: number = -1;
  callback: (requestId: string, action: string, requesterName: string, description: string, targetDeviceId: string) => void =
    (_r: string, _a: string, _n: string, _d: string, _t: string) => {};
}

class ModeChangeCallbackWrapper {
  id: number = -1;
  callback: (mode: DeviceMode) => void = (_m: DeviceMode) => {};
}

// ArkTS strict-mode message classes
class DeviceListRequestMsg {
  type: string = 'device.list.request';
}

class SessionListRequestMsg {
  type: string = 'session.list.request';
  deviceId: string = '';
}

class SessionRenameMsg {
  type: string = 'session.rename';
  deviceId: string = '';
  sessionId: string = '';
  title: string = '';
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

class TerminalHistoryRequestMsg {
  type: string = 'terminal.history.request';
  deviceId: string = '';
  sessionId: string = '';
  beforeSeq: number = 0;
  maxChars: number = 20000;
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
  inviteCode: string = '';
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

class AuthResponseMsg {
  type: string = 'auth.response';
  requestId: string = '';
  approved: boolean = false;
}

class RelayDesignateMsg {
  type: string = 'relay.designate';
}

class InviteCreateRequestMsg {
  type: string = 'invite.create.request';
}

class DeviceSettingsUpdateMsg {
  type: string = 'device.settings.update';
  deviceId: string = '';
  displayName: string = '';
}

class DeviceKickMsg {
  type: string = 'device.kick';
  deviceId: string = '';
}

class GroupDissolveMsg {
  type: string = 'group.dissolve';
}

class GroupServerChangeRequestMsg {
  type: string = 'group.server.change.request';
  newServerDeviceId: string = '';
  requesterId: string = '';
}

class PanelLoginScanMsg {
  type: string = 'panel.login.scan';
  requestId: string = '';
  mobileDeviceId: string = '';
}

// Stored single-connection device record
export class SingleDeviceRecord {
  deviceId: string = '';
  displayName: string = '';
  wsUrl: string = '';
  httpUrl: string = '';
}

export class SessionState {
  sessionId: string = '';
  deviceId: string = '';
  bufferedOutput: string = '';
  shellId: string = '';
}

class ConnectionManagerClass {
  // Single-connection devices (standalone/single mode)
  private singleConnections: Map<string, DeviceConnection> = new Map();
  private singleDeviceInfos: Map<string, DeviceInfo> = new Map();
  private singleDeviceHttpUrls: Map<string, string> = new Map();
  private singleMessageCallbackIds: Map<string, number> = new Map();
  private singleStateCallbackIds: Map<string, number> = new Map();

  // Group connection (group mode)
  private groupConnection: DeviceConnection | null = null;
  private groupMessageCallbackId: number = -1;
  private groupStateCallbackId: number = -1;
  private groupDevices: Array<DeviceInfo> = [];
  private groupMembers: Array<GroupMemberInfo> = [];
  private groupId: string = '';
  private groupSecret: string = '';
  private isGroupJoined: boolean = false;

  // Current mode
  private _currentMode: DeviceMode = DeviceMode.STANDALONE;

  // Mobile identity
  private mobileDeviceId: string = '';

  // Session management
  private sessions: Map<string, SessionState> = new Map();
  private activeSessionId: string = '';
  private activeDeviceId: string = '';
  private maxBufferedTerminalOutput: number = 200000;

  // Callbacks
  private deviceListCallbackWrappers: Array<DeviceListCallbackWrapper> = [];
  private authRequestCallbackWrappers: Array<AuthRequestCallbackWrapper> = [];
  private modeChangeCallbackWrappers: Array<ModeChangeCallbackWrapper> = [];
  private nextCallbackId: number = 1;

  // Per-connection message listeners (pass through to pages)
  private messageCallbackWrappers: Array<MessageCallbackWrapperInner> = [];
  private stateCallbackWrappers: Array<StateCallbackWrapperInner> = [];

  get currentMode(): DeviceMode { return this._currentMode; }

  getActiveSessionId(): string { return this.activeSessionId; }
  getActiveDeviceId(): string { return this.activeDeviceId; }

  setMobileDeviceId(id: string): void { this.mobileDeviceId = id; }

  // --- Unified message/state callback registration (for pages) ---

  addOnMessage(callback: (type: string, data: Record<string, Object>) => void): number {
    let wrapper: MessageCallbackWrapperInner = new MessageCallbackWrapperInner();
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

  addOnStateChange(callback: (state: ConnectionState, deviceId: string) => void): number {
    let wrapper: StateCallbackWrapperInner = new StateCallbackWrapperInner();
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

  // --- Device list change callback ---

  addOnDeviceListChanged(callback: (devices: Array<DeviceInfo>) => void): number {
    let wrapper: DeviceListCallbackWrapper = new DeviceListCallbackWrapper();
    wrapper.id = this.nextCallbackId;
    this.nextCallbackId++;
    wrapper.callback = callback;
    this.deviceListCallbackWrappers.push(wrapper);
    return wrapper.id;
  }

  removeOnDeviceListChanged(callbackId: number): void {
    for (let i: number = 0; i < this.deviceListCallbackWrappers.length; i++) {
      if (this.deviceListCallbackWrappers[i].id === callbackId) {
        this.deviceListCallbackWrappers.splice(i, 1);
        break;
      }
    }
  }

  // --- Auth request callback ---

  addOnAuthRequest(callback: (requestId: string, action: string, requesterName: string, description: string, targetDeviceId: string) => void): number {
    let wrapper: AuthRequestCallbackWrapper = new AuthRequestCallbackWrapper();
    wrapper.id = this.nextCallbackId;
    this.nextCallbackId++;
    wrapper.callback = callback;
    this.authRequestCallbackWrappers.push(wrapper);
    return wrapper.id;
  }

  removeOnAuthRequest(callbackId: number): void {
    for (let i: number = 0; i < this.authRequestCallbackWrappers.length; i++) {
      if (this.authRequestCallbackWrappers[i].id === callbackId) {
        this.authRequestCallbackWrappers.splice(i, 1);
        break;
      }
    }
  }

  // --- Mode change callback ---

  addOnModeChange(callback: (mode: DeviceMode) => void): number {
    let wrapper: ModeChangeCallbackWrapper = new ModeChangeCallbackWrapper();
    wrapper.id = this.nextCallbackId;
    this.nextCallbackId++;
    wrapper.callback = callback;
    this.modeChangeCallbackWrappers.push(wrapper);
    return wrapper.id;
  }

  removeOnModeChange(callbackId: number): void {
    for (let i: number = 0; i < this.modeChangeCallbackWrappers.length; i++) {
      if (this.modeChangeCallbackWrappers[i].id === callbackId) {
        this.modeChangeCallbackWrappers.splice(i, 1);
        break;
      }
    }
  }

  // --- Single-connection management ---

  connectSingle(wsUrl: string, deviceId: string, displayName: string, httpUrl: string = ''): void {
    if (deviceId.length === 0 || wsUrl.length === 0) return;

    // If already connected to this device, disconnect first
    if (this.singleConnections.has(deviceId)) {
      this.disconnectDevice(deviceId);
    }

    hilog.info(DOMAIN, TAG, 'connectSingle device=%{public}s url=%{public}s', deviceId, wsUrl);

    const conn: DeviceConnection = new DeviceConnection(deviceId, displayName);

    // Register message handler
    const msgId: number = conn.addOnMessage((type: string, data: Record<string, Object>) => {
      this.handleSingleMessage(deviceId, type, data);
    });
    this.singleMessageCallbackIds.set(deviceId, msgId);

    // Register state handler
    const stateId: number = conn.addOnStateChange((state: ConnectionState) => {
      this.handleSingleStateChange(deviceId, state);
    });
    this.singleStateCallbackIds.set(deviceId, stateId);

    this.singleConnections.set(deviceId, conn);

    // Store device info
    const info: DeviceInfo = {
      deviceId: deviceId,
      displayName: displayName,
      os: '',
      isOnline: false,
      availableShells: []
    } as DeviceInfo;
    this.singleDeviceInfos.set(deviceId, info);
    this.singleDeviceHttpUrls.set(deviceId, httpUrl);

    // Persist single device record
    this.saveSingleDevice(deviceId, displayName, wsUrl, httpUrl);

    if (this._currentMode === DeviceMode.STANDALONE) {
      this._currentMode = DeviceMode.SINGLE;
      this.emitModeChange(DeviceMode.SINGLE);
    }

    // Notify UI immediately that a new device is available
    this.emitDeviceListChanged();

    conn.connect(wsUrl);
  }

  disconnectDevice(deviceId: string): void {
    if (deviceId.length === 0) return;

    const conn: DeviceConnection | undefined = this.singleConnections.get(deviceId);
    if (conn === undefined) return;

    hilog.info(DOMAIN, TAG, 'disconnectDevice device=%{public}s', deviceId);

    // Remove callback registrations
    const msgId: number | undefined = this.singleMessageCallbackIds.get(deviceId);
    if (msgId !== undefined) {
      conn.removeOnMessage(msgId);
      this.singleMessageCallbackIds.delete(deviceId);
    }
    const stateId: number | undefined = this.singleStateCallbackIds.get(deviceId);
    if (stateId !== undefined) {
      conn.removeOnStateChange(stateId);
      this.singleStateCallbackIds.delete(deviceId);
    }

    conn.disconnect();
    this.singleConnections.delete(deviceId);
    this.singleDeviceInfos.delete(deviceId);
    this.singleDeviceHttpUrls.delete(deviceId);

    // Remove persisted record
    this.removeSingleDevice(deviceId);

    // Clean up sessions for this device
    this.removeSessionsForDevice(deviceId);

    // If no more single connections, go back to standalone
    if (this.singleConnections.size === 0 && this._currentMode === DeviceMode.SINGLE) {
      this._currentMode = DeviceMode.STANDALONE;
      this.emitModeChange(DeviceMode.STANDALONE);
    }

    this.emitDeviceListChanged();
  }

  // --- Group connection management ---

  connectGroup(wsUrl: string, groupSecret: string, groupId: string): void {
    if (wsUrl.length === 0) return;

    this.disconnectGroup();

    const connectUrl: string = this.buildConnectUrlWithToken(wsUrl, groupSecret);
    hilog.info(DOMAIN, TAG, 'connectGroup url=%{public}s', connectUrl);

    this.groupId = groupId;
    this.groupSecret = groupSecret;
    const conn: DeviceConnection = new DeviceConnection('group', 'Group Connection');

    this.groupMessageCallbackId = conn.addOnMessage((type: string, data: Record<string, Object>) => {
      this.handleGroupMessage(type, data);
    });
    this.groupStateCallbackId = conn.addOnStateChange((state: ConnectionState) => {
      this.handleGroupStateChange(state);
    });

    this.groupConnection = conn;
    this._currentMode = DeviceMode.GROUP;
    this.emitModeChange(DeviceMode.GROUP);

    conn.connect(connectUrl);

    // Save group relay URL
    PreferencesUtil.setString(StorageKeys.GROUP_RELAY_URL, connectUrl);
    PreferencesUtil.setString(StorageKeys.GROUP_ID, groupId);
    PreferencesUtil.setString(StorageKeys.GROUP_SECRET, groupSecret);
  }

  disconnectGroup(): void {
    if (this.groupConnection === null) return;

    hilog.info(DOMAIN, TAG, 'disconnectGroup');

    if (this.groupMessageCallbackId !== -1) {
      this.groupConnection.removeOnMessage(this.groupMessageCallbackId);
      this.groupMessageCallbackId = -1;
    }
    if (this.groupStateCallbackId !== -1) {
      this.groupConnection.removeOnStateChange(this.groupStateCallbackId);
      this.groupStateCallbackId = -1;
    }

    this.groupConnection.disconnect();
    this.groupConnection = null;
    this.groupDevices = [];
    this.groupMembers = [];
    this.isGroupJoined = false;
    this.groupId = '';
    this.groupSecret = '';

    PreferencesUtil.setString(StorageKeys.GROUP_RELAY_URL, '');
    PreferencesUtil.setString(StorageKeys.GROUP_ID, '');
    PreferencesUtil.setString(StorageKeys.GROUP_SECRET, '');

    if (this._currentMode === DeviceMode.GROUP) {
      this._currentMode = this.singleConnections.size > 0 ? DeviceMode.SINGLE : DeviceMode.STANDALONE;
      this.emitModeChange(this._currentMode);
    }

    this.emitDeviceListChanged();
  }

  // --- Designate a single-connected device as relay server ---

  designateRelay(deviceId: string): void {
    const conn: DeviceConnection | undefined = this.singleConnections.get(deviceId);
    if (conn === undefined) {
      hilog.error(DOMAIN, TAG, 'designateRelay: device %{public}s not found', deviceId);
      return;
    }
    if (conn.connectionState !== ConnectionState.CONNECTED) {
      hilog.error(DOMAIN, TAG, 'designateRelay: device %{public}s not connected', deviceId);
      return;
    }

    hilog.info(DOMAIN, TAG, 'designateRelay device=%{public}s', deviceId);
    let msg: RelayDesignateMsg = new RelayDesignateMsg();
    conn.send(JSON.stringify(msg));
  }

  // --- Invite another device to group via HTTP POST ---

  inviteToGroup(httpUrl: string, inviteCode: string, relayUrl: string,
    onResult?: (success: boolean, message: string) => void): void {
    if (httpUrl.length === 0 || inviteCode.length === 0 || relayUrl.length === 0) {
      if (onResult !== undefined) onResult(false, 'Missing parameters');
      return;
    }

    hilog.info(DOMAIN, TAG, 'inviteToGroup target=%{public}s', httpUrl);

    const inviteUrl: string = httpUrl.endsWith('/') ? `${httpUrl}api/invite` : `${httpUrl}/api/invite`;

    let body: Record<string, Object> = {} as Record<string, Object>;
    body['relayUrl'] = relayUrl;
    body['inviteCode'] = inviteCode;
    body['groupId'] = this.groupId;

    const httpRequest: http.HttpRequest = http.createHttp();
    httpRequest.request(inviteUrl, {
      method: http.RequestMethod.POST,
      header: { 'Content-Type': 'application/json' } as Record<string, string>,
      extraData: JSON.stringify(body)
    }).then((resp: http.HttpResponse) => {
      const code: number = resp.responseCode;
      hilog.info(DOMAIN, TAG, 'inviteToGroup response code=%{public}d', code);
      if (code >= 200 && code < 300) {
        hilog.info(DOMAIN, TAG, 'inviteToGroup success');
        if (onResult !== undefined) onResult(true, '');
      } else {
        const msg: string = `HTTP ${code}`;
        hilog.error(DOMAIN, TAG, 'inviteToGroup failed: %{public}s', msg);
        if (onResult !== undefined) onResult(false, msg);
      }
      httpRequest.destroy();
    }).catch((err: BusinessError) => {
      hilog.error(DOMAIN, TAG, 'inviteToGroup error %{public}s', err.message ?? '');
      if (onResult !== undefined) onResult(false, err.message ?? 'Network error');
      httpRequest.destroy();
    });
  }

  // --- Request invite code from relay ---

  requestInviteCode(): void {
    if (this.groupConnection === null) return;
    if (this.groupConnection.connectionState !== ConnectionState.CONNECTED) return;
    let msg: InviteCreateRequestMsg = new InviteCreateRequestMsg();
    this.groupConnection.send(JSON.stringify(msg));
  }

  // --- Dissolve group ---

  dissolveGroup(): void {
    if (this.groupConnection === null) return;
    let msg: GroupDissolveMsg = new GroupDissolveMsg();
    this.groupConnection.send(JSON.stringify(msg));
  }

  // --- Group server change ---

  requestGroupServerChange(newServerDeviceId: string): void {
    if (newServerDeviceId.length === 0) return;
    if (this.groupConnection === null) return;
    if (this.groupConnection.connectionState !== ConnectionState.CONNECTED) return;
    if (this.mobileDeviceId.length === 0) return;
    hilog.info(DOMAIN, TAG, 'requestGroupServerChange newServer=%{public}s', newServerDeviceId);
    let msg: GroupServerChangeRequestMsg = new GroupServerChangeRequestMsg();
    msg.newServerDeviceId = newServerDeviceId;
    msg.requesterId = this.mobileDeviceId;
    this.groupConnection.send(JSON.stringify(msg));
  }

  // --- Device settings ---

  updateDeviceDisplayName(deviceId: string, newName: string): void {
    const conn: DeviceConnection | null = this.getConnectionForDevice(deviceId);
    if (conn === null) return;
    let msg: DeviceSettingsUpdateMsg = new DeviceSettingsUpdateMsg();
    msg.deviceId = deviceId;
    msg.displayName = newName;
    conn.send(JSON.stringify(msg));
  }

  kickDevice(deviceId: string): void {
    const conn: DeviceConnection | null = this.getConnectionForDevice(deviceId);
    if (conn === null) return;
    let msg: DeviceKickMsg = new DeviceKickMsg();
    msg.deviceId = deviceId;
    conn.send(JSON.stringify(msg));
  }

  // --- Auth response ---

  sendAuthResponse(requestId: string, approved: boolean): void {
    // Send to whichever connection the auth request came from
    // In group mode: groupConnection, in single mode: the active single connection
    let msg: AuthResponseMsg = new AuthResponseMsg();
    msg.requestId = requestId;
    msg.approved = approved;
    const json: string = JSON.stringify(msg);

    if (this.groupConnection !== null && this.groupConnection.connectionState === ConnectionState.CONNECTED) {
      this.groupConnection.send(json);
    } else {
      // Try sending to active single connection
      const conn: DeviceConnection | undefined = this.singleConnections.get(this.activeDeviceId);
      if (conn !== undefined) {
        conn.send(json);
      }
    }
  }

  // --- Panel login scan ---

  sendPanelLoginScan(requestId: string): void {
    let msg: PanelLoginScanMsg = new PanelLoginScanMsg();
    msg.requestId = requestId;
    msg.mobileDeviceId = this.mobileDeviceId;
    const json: string = JSON.stringify(msg);

    if (this.groupConnection !== null && this.groupConnection.connectionState === ConnectionState.CONNECTED) {
      this.groupConnection.send(json);
    } else {
      // Send to first connected single connection
      this.singleConnections.forEach((conn: DeviceConnection) => {
        if (conn.connectionState === ConnectionState.CONNECTED) {
          conn.send(json);
        }
      });
    }
  }

  // --- Terminal operations ---

  requestSessionList(deviceId: string): void {
    const conn: DeviceConnection | null = this.getConnectionForDevice(deviceId);
    if (conn === null) return;
    let msg: SessionListRequestMsg = new SessionListRequestMsg();
    msg.deviceId = deviceId;
    conn.send(JSON.stringify(msg));
  }

  renameSession(deviceId: string, sessionId: string, title: string): void {
    const trimmed: string = title.trim();
    if (deviceId.length === 0 || sessionId.length === 0 || trimmed.length === 0) return;
    const conn: DeviceConnection | null = this.getConnectionForDevice(deviceId);
    if (conn === null) return;
    let msg: SessionRenameMsg = new SessionRenameMsg();
    msg.deviceId = deviceId;
    msg.sessionId = sessionId;
    msg.title = trimmed;
    conn.send(JSON.stringify(msg));
  }

  openTerminal(deviceId: string, shellId: string): void {
    const conn: DeviceConnection | null = this.getConnectionForDevice(deviceId);
    if (conn === null) return;
    this.activeDeviceId = deviceId;
    let msg: TerminalOpenMsg = new TerminalOpenMsg();
    msg.deviceId = deviceId;
    msg.shellId = shellId;
    conn.send(JSON.stringify(msg));
  }

  sendTerminalInput(data: string, targetSessionId: string = ''): void {
    const sessionId: string = targetSessionId.length > 0 ? targetSessionId : this.activeSessionId;
    if (sessionId.length === 0 || data.length === 0) return;
    const session: SessionState | undefined = this.sessions.get(sessionId);
    const deviceId: string = session !== undefined ? session.deviceId : this.activeDeviceId;
    const conn: DeviceConnection | null = this.getConnectionForDevice(deviceId);
    if (conn === null) return;
    let msg: TerminalInputMsg = new TerminalInputMsg();
    msg.deviceId = deviceId;
    msg.sessionId = sessionId;
    msg.data = data;
    conn.send(JSON.stringify(msg));
  }

  requestTerminalHistory(deviceId: string, sessionId: string, beforeSeq: number, maxChars: number): void {
    if (deviceId.length === 0 || sessionId.length === 0) return;
    const conn: DeviceConnection | null = this.getConnectionForDevice(deviceId);
    if (conn === null) return;
    let msg: TerminalHistoryRequestMsg = new TerminalHistoryRequestMsg();
    msg.deviceId = deviceId;
    msg.sessionId = sessionId;
    msg.beforeSeq = beforeSeq;
    msg.maxChars = maxChars;
    conn.send(JSON.stringify(msg));
  }

  sendTerminalResize(cols: number, rows: number, targetSessionId: string = ''): void {
    const sessionId: string = targetSessionId.length > 0 ? targetSessionId : this.activeSessionId;
    if (sessionId.length === 0) return;
    const session: SessionState | undefined = this.sessions.get(sessionId);
    const deviceId: string = session !== undefined ? session.deviceId : this.activeDeviceId;
    const conn: DeviceConnection | null = this.getConnectionForDevice(deviceId);
    if (conn === null) return;
    let msg: TerminalResizeMsg = new TerminalResizeMsg();
    msg.deviceId = deviceId;
    msg.sessionId = sessionId;
    msg.cols = cols;
    msg.rows = rows;
    conn.send(JSON.stringify(msg));
  }

  closeTerminalSession(sessionId: string): void {
    if (sessionId.length === 0) return;
    const session: SessionState | undefined = this.sessions.get(sessionId);
    if (session === undefined) return;
    const conn: DeviceConnection | null = this.getConnectionForDevice(session.deviceId);
    if (conn === null) return;
    let msg: TerminalCloseMsg = new TerminalCloseMsg();
    msg.deviceId = session.deviceId;
    msg.sessionId = sessionId;
    conn.send(JSON.stringify(msg));
  }

  // --- Session state accessors ---

  setActiveSession(deviceId: string, sessionId: string): void {
    this.activeDeviceId = deviceId;
    this.activeSessionId = sessionId;
    if (sessionId.length > 0 && !this.sessions.has(sessionId)) {
      let session: SessionState = new SessionState();
      session.sessionId = sessionId;
      session.deviceId = deviceId;
      this.sessions.set(sessionId, session);
    }
  }

  getBufferedTerminalOutput(): string {
    const session: SessionState | undefined = this.sessions.get(this.activeSessionId);
    if (session !== undefined) return session.bufferedOutput;
    return '';
  }

  getSessionBufferedOutput(sessionId: string): string {
    const session: SessionState | undefined = this.sessions.get(sessionId);
    if (session !== undefined) return session.bufferedOutput;
    return '';
  }

  getSessionCount(): number { return this.sessions.size; }

  getSessionList(): Array<SessionState> {
    let result: Array<SessionState> = [];
    this.sessions.forEach((session: SessionState) => { result.push(session); });
    return result;
  }

  switchSession(sessionId: string): void {
    const session: SessionState | undefined = this.sessions.get(sessionId);
    if (session !== undefined) {
      this.activeSessionId = session.sessionId;
      this.activeDeviceId = session.deviceId;
    }
  }

  hasSession(sessionId: string): boolean { return this.sessions.has(sessionId); }

  // --- Unified device list ---

  getAllDevices(): Array<DeviceInfo> {
    let devices: Array<DeviceInfo> = [];

    if (this._currentMode === DeviceMode.GROUP) {
      // In group mode, return group device list
      for (let i: number = 0; i < this.groupDevices.length; i++) {
        devices.push(this.groupDevices[i]);
      }
    } else {
      // In single/standalone mode, return single connections
      this.singleDeviceInfos.forEach((info: DeviceInfo) => {
        devices.push(info);
      });
    }

    return devices;
  }

  getSingleDevices(): Array<DeviceInfo> {
    let devices: Array<DeviceInfo> = [];
    this.singleDeviceInfos.forEach((info: DeviceInfo) => {
      devices.push(info);
    });
    return devices;
  }

  getGroupDevices(): Array<DeviceInfo> {
    return this.groupDevices.slice();
  }

  getGroupMembers(): Array<GroupMemberInfo> {
    return this.groupMembers.slice();
  }

  getGroupId(): string { return this.groupId; }
  isInGroup(): boolean { return this.isGroupJoined; }

  getConnectionStateForDevice(deviceId: string): ConnectionState {
    const conn: DeviceConnection | undefined = this.singleConnections.get(deviceId);
    if (conn !== undefined) return conn.connectionState;
    if (this.groupConnection !== null) return this.groupConnection.connectionState;
    return ConnectionState.DISCONNECTED;
  }

  getSingleDeviceRecord(deviceId: string): SingleDeviceRecord | null {
    const conn: DeviceConnection | undefined = this.singleConnections.get(deviceId);
    if (conn === undefined) return null;
    let record: SingleDeviceRecord = new SingleDeviceRecord();
    record.deviceId = deviceId;
    record.displayName = conn.displayName;
    record.wsUrl = conn.serverUrl;
    record.httpUrl = this.singleDeviceHttpUrls.get(deviceId) ?? '';
    return record;
  }

  // --- Group join (for mobile binding after QR scan in group mode) ---

  sendGroupJoinRequest(groupSecret: string, inviteCode: string = ''): void {
    if (this.groupConnection === null) return;
    let msg: GroupJoinRequestMsg = new GroupJoinRequestMsg();
    msg.groupSecret = groupSecret;
    msg.inviteCode = inviteCode;
    msg.deviceId = this.mobileDeviceId;
    msg.displayName = 'HarmonyOS Phone';
    msg.os = 'HarmonyOS';
    this.groupConnection.send(JSON.stringify(msg));
  }

  sendMobileBindRequest(groupId: string): void {
    if (this.groupConnection === null) return;
    let msg: MobileBindRequestMsg = new MobileBindRequestMsg();
    msg.groupId = groupId;
    msg.mobileDeviceId = this.mobileDeviceId;
    msg.mobileDisplayName = 'HarmonyOS Phone';
    this.groupConnection.send(JSON.stringify(msg));
  }

  requestDeviceList(): void {
    if (this.groupConnection !== null && this.groupConnection.connectionState === ConnectionState.CONNECTED) {
      let msg: DeviceListRequestMsg = new DeviceListRequestMsg();
      this.groupConnection.send(JSON.stringify(msg));
    }
    // For single connections, the device info comes from the connection itself
  }

  // --- Restore saved connections ---

  async restoreSavedConnections(): Promise<void> {
    // Restore group connection first (if any)
    try {
      const groupRelayUrl: string = await PreferencesUtil.getString(StorageKeys.GROUP_RELAY_URL);
      const groupSecret: string = await PreferencesUtil.getString(StorageKeys.GROUP_SECRET);
      const groupId: string = await PreferencesUtil.getString(StorageKeys.GROUP_ID);
      if (groupRelayUrl.length > 0 && groupSecret.length > 0) {
        this.connectGroup(groupRelayUrl, groupSecret, groupId);
      }
    } catch (_e) {
      // Ignore restore errors
    }

    // Restore single-device connections
    try {
      const raw: string = await PreferencesUtil.getString(StorageKeys.SINGLE_DEVICES);
      if (raw.length === 0) return;
      const records: Array<Object> = JSON.parse(raw) as Array<Object>;
      for (let i: number = 0; i < records.length; i++) {
        const rec: Record<string, Object> = records[i] as Record<string, Object>;
        const deviceId: string = (rec['deviceId'] ?? '') as string;
        const displayName: string = (rec['displayName'] ?? '') as string;
        const wsUrl: string = (rec['wsUrl'] ?? '') as string;
        const httpUrl: string = (rec['httpUrl'] ?? '') as string;
        if (deviceId.length > 0 && wsUrl.length > 0) {
          this.connectSingle(wsUrl, deviceId, displayName, httpUrl);
        }
      }
    } catch (_e) {
      // Ignore restore errors
    }
  }

  // --- Private helpers ---

  private getConnectionForDevice(deviceId: string): DeviceConnection | null {
    // Check single connections first
    const single: DeviceConnection | undefined = this.singleConnections.get(deviceId);
    if (single !== undefined) return single;
    // Fall back to group connection
    if (this.groupConnection !== null) return this.groupConnection;
    return null;
  }

  private handleSingleMessage(deviceId: string, type: string, data: Record<string, Object>): void {
    if (type !== 'terminal.output') {
      hilog.info(DOMAIN, TAG, 'singleMessage device=%{public}s type=%{public}s', deviceId, type);
    }

    // Handle device info from standalone device
    if (type === 'device.list') {
      const devices: Array<DeviceInfo> = this.normalizeDeviceList(data['devices']);
      // Update the device info for this connection
      if (devices.length > 0) {
        this.singleDeviceInfos.set(deviceId, devices[0]);
      }
      this.emitDeviceListChanged();
    } else if (type === 'relay.designated') {
      // Device became a relay server - transition to group mode
      const relayUrl: string = (data['relayUrl'] ?? '') as string;
      const newGroupId: string = (data['groupId'] ?? '') as string;
      const secret: string = (data['groupSecret'] ?? '') as string;
      if (relayUrl.length > 0) {
        hilog.info(DOMAIN, TAG, 'Device %{public}s designated as relay, url=%{public}s', deviceId, relayUrl);
        // Store group secret for reconnect
        if (secret.length > 0) {
          this.groupSecret = secret;
          PreferencesUtil.setString(StorageKeys.GROUP_SECRET, secret);
        }
        // The single connection becomes the group connection
        this.promoteToGroupConnection(deviceId, relayUrl, newGroupId);
        // Join the group immediately so the phone becomes the bound mobile admin
        if (secret.length > 0) {
          this.sendGroupJoinRequest(secret);
        } else {
          hilog.error(DOMAIN, TAG, 'relay.designated missing groupSecret, cannot join group');
        }
      }
    } else if (type === 'auth.request') {
      this.handleAuthRequest(data);
    } else if (type === 'terminal.opened' || type === 'terminal.output' ||
               type === 'terminal.closed' || type === 'session.list') {
      this.handleTerminalMessage(type, data);
    }

    // Forward to all registered message listeners
    this.emitMessage(type, data);
  }

  private handleSingleStateChange(deviceId: string, state: ConnectionState): void {
    hilog.info(DOMAIN, TAG, 'singleStateChange device=%{public}s state=%{public}d', deviceId, state);

    // Update device info online status
    const info: DeviceInfo | undefined = this.singleDeviceInfos.get(deviceId);
    if (info !== undefined) {
      const updated: DeviceInfo = {
        deviceId: info.deviceId,
        displayName: info.displayName,
        os: info.os,
        isOnline: state === ConnectionState.CONNECTED,
        availableShells: info.availableShells
      } as DeviceInfo;
      this.singleDeviceInfos.set(deviceId, updated);
      this.emitDeviceListChanged();
    }

    // When connected, request device list to get full info
    if (state === ConnectionState.CONNECTED) {
      const conn: DeviceConnection | undefined = this.singleConnections.get(deviceId);
      if (conn !== undefined) {
        let msg: DeviceListRequestMsg = new DeviceListRequestMsg();
        conn.send(JSON.stringify(msg));
      }
    }

    this.emitStateChange(state, deviceId);
  }

  private handleGroupMessage(type: string, data: Record<string, Object>): void {
    if (type !== 'terminal.output') {
      hilog.info(DOMAIN, TAG, 'groupMessage type=%{public}s', type);
    }

    if (type === 'device.list') {
      this.groupDevices = this.normalizeDeviceList(data['devices']);
      this.emitDeviceListChanged();
    } else if (type === 'group.join.accepted') {
      this.isGroupJoined = true;
      this.groupId = ((data['groupId'] ?? '') as string);
      this.groupMembers = this.normalizeGroupMembers(data['members']);
      PreferencesUtil.setString(StorageKeys.GROUP_ID, this.groupId);
      const secret: string = (data['groupSecret'] ?? '') as string;
      if (secret.length > 0 && secret !== this.groupSecret) {
        this.groupSecret = secret;
        PreferencesUtil.setString(StorageKeys.GROUP_SECRET, secret);
      }
      // Refresh device list after successful join
      this.requestDeviceList();
    } else if (type === 'group.member.list') {
      this.groupMembers = this.normalizeGroupMembers(data['members']);
      this.requestDeviceList();
    } else if (type === 'group.member.joined') {
      const member: GroupMemberInfo | null = this.normalizeGroupMember(data['member']);
      if (member !== null) this.groupMembers.push(member);
      this.requestDeviceList();
    } else if (type === 'group.member.left') {
      const leftDeviceId: string = (data['deviceId'] ?? '') as string;
      this.groupMembers = this.groupMembers.filter((m: GroupMemberInfo) => m.deviceId !== leftDeviceId);
      this.requestDeviceList();
    } else if (type === 'group.server.change.commit') {
      const newUrl: string = (data['newServerUrl'] ?? '') as string;
      const newGroupId: string = (data['groupId'] ?? this.groupId) as string;
      const newSecret: string = (data['groupSecret'] ?? '') as string;
      const effectiveSecret: string = newSecret.length > 0 ? newSecret : this.groupSecret;
      if (newUrl.length === 0 || effectiveSecret.length === 0) {
        hilog.error(DOMAIN, TAG, 'server change commit missing url/secret');
        return;
      }
      hilog.info(DOMAIN, TAG, 'server change commit: newUrl=%{public}s', newUrl);
      const connectUrl: string = this.buildConnectUrlWithToken(newUrl, effectiveSecret);
      this.connectGroup(connectUrl, effectiveSecret, newGroupId);
    } else if (type === 'group.secret.rotate.done') {
      const newSecret: string = (data['newSecret'] ?? '') as string;
      if (newSecret.length > 0) {
        this.groupSecret = newSecret;
        PreferencesUtil.setString(StorageKeys.GROUP_SECRET, newSecret);
      }
    } else if (type === 'group.dissolved') {
      hilog.info(DOMAIN, TAG, 'Group dissolved');
      this.disconnectGroup();
    } else if (type === 'device.kicked') {
      hilog.info(DOMAIN, TAG, 'Kicked from group');
      this.disconnectGroup();
    } else if (type === 'device.settings.updated') {
      const targetId: string = (data['deviceId'] ?? '') as string;
      const newName: string = (data['displayName'] ?? '') as string;
      this.updateDeviceNameInLists(targetId, newName);
    } else if (type === 'invite.create.response') {
      // Forward to listeners — the UI will handle it
    } else if (type === 'auth.request') {
      this.handleAuthRequest(data);
    } else if (type === 'panel.disconnected') {
      // Forward to listeners
    } else if (type === 'terminal.opened' || type === 'terminal.output' ||
               type === 'terminal.closed' || type === 'session.list') {
      this.handleTerminalMessage(type, data);
    }

    this.emitMessage(type, data);
  }

  private handleGroupStateChange(state: ConnectionState): void {
    hilog.info(DOMAIN, TAG, 'groupStateChange state=%{public}d', state);

    if (state === ConnectionState.CONNECTED && this.groupConnection !== null) {
      // Auto re-join group on reconnect using stored secret
      if (this.groupSecret.length > 0) {
        hilog.info(DOMAIN, TAG, 'Auto re-joining group after reconnect');
        this.sendGroupJoinRequest(this.groupSecret);
      }
    }

    this.emitStateChange(state, 'group');
  }

  private handleTerminalMessage(type: string, data: Record<string, Object>): void {
    if (type === 'terminal.opened') {
      const sessionId: string = (data['sessionId'] ?? '') as string;
      const deviceId: string = (data['deviceId'] ?? '') as string;
      if (sessionId.length > 0) {
        let session: SessionState = new SessionState();
        session.sessionId = sessionId;
        session.deviceId = deviceId;
        this.sessions.set(sessionId, session);
        this.activeSessionId = sessionId;
        this.activeDeviceId = deviceId;
      }
    } else if (type === 'terminal.output') {
      const sessionId: string = (data['sessionId'] ?? '') as string;
      const outputData: string = (data['data'] ?? '') as string;
      const session: SessionState | undefined = this.sessions.get(sessionId);
      if (session !== undefined && outputData.length > 0) {
        session.bufferedOutput += outputData;
        if (session.bufferedOutput.length > this.maxBufferedTerminalOutput) {
          session.bufferedOutput = session.bufferedOutput.substring(
            session.bufferedOutput.length - this.maxBufferedTerminalOutput);
        }
      }
    } else if (type === 'terminal.closed') {
      const sessionId: string = (data['sessionId'] ?? '') as string;
      this.sessions.delete(sessionId);
      if (this.activeSessionId === sessionId) {
        this.activeSessionId = '';
      }
    } else if (type === 'session.list') {
      // Update sessions from server list
      const deviceId: string = (data['deviceId'] ?? '') as string;
      const rawSessions: Object | undefined = data['sessions'];
      if (Array.isArray(rawSessions)) {
        const sessionArray: Array<Object> = rawSessions as Array<Object>;
        for (let i: number = 0; i < sessionArray.length; i++) {
          const rawSession: Record<string, Object> = sessionArray[i] as Record<string, Object>;
          const sid: string = (rawSession['sessionId'] ?? '') as string;
          if (sid.length > 0 && !this.sessions.has(sid)) {
            let session: SessionState = new SessionState();
            session.sessionId = sid;
            session.deviceId = deviceId;
            session.shellId = (rawSession['shellId'] ?? '') as string;
            this.sessions.set(sid, session);
          }
        }
      }
    }
  }

  private handleAuthRequest(data: Record<string, Object>): void {
    const requestId: string = (data['requestId'] ?? '') as string;
    const action: string = (data['action'] ?? '') as string;
    const requesterName: string = (data['requesterName'] ?? '') as string;
    const description: string = (data['description'] ?? '') as string;
    const targetDeviceId: string = (data['targetDeviceId'] ?? '') as string;
    if (requestId.length === 0) return;

    let listeners: Array<AuthRequestCallbackWrapper> = this.authRequestCallbackWrappers.slice();
    for (let i: number = 0; i < listeners.length; i++) {
      try { listeners[i].callback(requestId, action, requesterName, description, targetDeviceId); } catch (_e) { /* ignore */ }
    }
  }

  private promoteToGroupConnection(deviceId: string, relayUrl: string, newGroupId: string): void {
    const conn: DeviceConnection | undefined = this.singleConnections.get(deviceId);
    if (conn === undefined) return;

    // Remove from single connections
    const msgId: number | undefined = this.singleMessageCallbackIds.get(deviceId);
    if (msgId !== undefined) {
      conn.removeOnMessage(msgId);
      this.singleMessageCallbackIds.delete(deviceId);
    }
    const stateId: number | undefined = this.singleStateCallbackIds.get(deviceId);
    if (stateId !== undefined) {
      conn.removeOnStateChange(stateId);
      this.singleStateCallbackIds.delete(deviceId);
    }
    this.singleConnections.delete(deviceId);
    this.singleDeviceInfos.delete(deviceId);

    // Set up as group connection (reuse same WS connection)
    this.groupConnection = conn;
    this.groupId = newGroupId;
    this._currentMode = DeviceMode.GROUP;

    this.groupMessageCallbackId = conn.addOnMessage((type: string, data: Record<string, Object>) => {
      this.handleGroupMessage(type, data);
    });
    this.groupStateCallbackId = conn.addOnStateChange((state: ConnectionState) => {
      this.handleGroupStateChange(state);
    });

    PreferencesUtil.setString(StorageKeys.GROUP_RELAY_URL, relayUrl);
    PreferencesUtil.setString(StorageKeys.GROUP_ID, newGroupId);
    this.emitModeChange(DeviceMode.GROUP);
  }

  private removeSessionsForDevice(deviceId: string): void {
    let toRemove: Array<string> = [];
    this.sessions.forEach((session: SessionState, key: string) => {
      if (session.deviceId === deviceId) {
        toRemove.push(key);
      }
    });
    for (let i: number = 0; i < toRemove.length; i++) {
      this.sessions.delete(toRemove[i]);
      if (this.activeSessionId === toRemove[i]) {
        this.activeSessionId = '';
        this.activeDeviceId = '';
      }
    }
  }

  private updateDeviceNameInLists(deviceId: string, newName: string): void {
    // Update single device info
    const info: DeviceInfo | undefined = this.singleDeviceInfos.get(deviceId);
    if (info !== undefined) {
      const updated: DeviceInfo = {
        deviceId: info.deviceId,
        displayName: newName,
        os: info.os,
        isOnline: info.isOnline,
        availableShells: info.availableShells
      } as DeviceInfo;
      this.singleDeviceInfos.set(deviceId, updated);
    }

    // Update group devices
    for (let i: number = 0; i < this.groupDevices.length; i++) {
      if (this.groupDevices[i].deviceId === deviceId) {
        const d: DeviceInfo = this.groupDevices[i];
        this.groupDevices[i] = {
          deviceId: d.deviceId,
          displayName: newName,
          os: d.os,
          isOnline: d.isOnline,
          availableShells: d.availableShells
        } as DeviceInfo;
      }
    }

    // Update group members
    for (let i: number = 0; i < this.groupMembers.length; i++) {
      if (this.groupMembers[i].deviceId === deviceId) {
        const m: GroupMemberInfo = this.groupMembers[i];
        this.groupMembers[i] = {
          deviceId: m.deviceId,
          displayName: newName,
          os: m.os,
          role: m.role,
          isOnline: m.isOnline,
          availableShells: m.availableShells
        } as GroupMemberInfo;
      }
    }

    // Update single connection display name
    const conn: DeviceConnection | undefined = this.singleConnections.get(deviceId);
    if (conn !== undefined) {
      conn.displayName = newName;
    }

    this.emitDeviceListChanged();
  }

  // --- Persistence helpers ---

  private saveSingleDevice(deviceId: string, displayName: string, wsUrl: string, httpUrl: string): void {
    PreferencesUtil.getString(StorageKeys.SINGLE_DEVICES).then((raw: string) => {
      let records: Array<SingleDeviceRecord> = [];
      if (raw.length > 0) {
        try { records = JSON.parse(raw) as Array<SingleDeviceRecord>; } catch (_e) { /* ignore */ }
      }
      // Remove existing entry for same deviceId
      records = records.filter((r: SingleDeviceRecord) => r.deviceId !== deviceId);
      let newRec: SingleDeviceRecord = new SingleDeviceRecord();
      newRec.deviceId = deviceId;
      newRec.displayName = displayName;
      newRec.wsUrl = wsUrl;
      newRec.httpUrl = httpUrl;
      records.push(newRec);
      PreferencesUtil.setString(StorageKeys.SINGLE_DEVICES, JSON.stringify(records));
    });
  }

  private removeSingleDevice(deviceId: string): void {
    PreferencesUtil.getString(StorageKeys.SINGLE_DEVICES).then((raw: string) => {
      if (raw.length === 0) return;
      try {
        let records: Array<SingleDeviceRecord> = JSON.parse(raw) as Array<SingleDeviceRecord>;
        records = records.filter((r: SingleDeviceRecord) => r.deviceId !== deviceId);
        PreferencesUtil.setString(StorageKeys.SINGLE_DEVICES, JSON.stringify(records));
      } catch (_e) { /* ignore */ }
    });
  }

  // --- Normalization helpers ---

  private normalizeDeviceList(value: Object | undefined): Array<DeviceInfo> {
    let normalized: Array<DeviceInfo> = [];
    if (!Array.isArray(value)) return normalized;
    const rawDevices: Array<Object> = value as Array<Object>;
    for (let i: number = 0; i < rawDevices.length; i++) {
      const rec: Record<string, Object> = rawDevices[i] as Record<string, Object>;
      const deviceId: string = ((rec['deviceId'] ?? '') as string);
      const info: DeviceInfo = {
        deviceId: deviceId,
        displayName: ((rec['displayName'] ?? deviceId) as string),
        os: ((rec['os'] ?? 'Unknown') as string),
        isOnline: ((rec['isOnline'] ?? true) as boolean),
        availableShells: this.normalizeStringArray(rec['availableShells'])
      } as DeviceInfo;
      normalized.push(info);
    }
    return normalized;
  }

  private normalizeGroupMembers(value: Object | undefined): Array<GroupMemberInfo> {
    let normalized: Array<GroupMemberInfo> = [];
    if (!Array.isArray(value)) return normalized;
    const rawMembers: Array<Object> = value as Array<Object>;
    for (let i: number = 0; i < rawMembers.length; i++) {
      const member: GroupMemberInfo | null = this.normalizeGroupMember(rawMembers[i]);
      if (member !== null) normalized.push(member);
    }
    return normalized;
  }

  private normalizeGroupMember(value: Object | undefined): GroupMemberInfo | null {
    if (value === undefined || value === null) return null;
    const rec: Record<string, Object> = value as Record<string, Object>;
    const memberInfo: GroupMemberInfo = {
      deviceId: ((rec['deviceId'] ?? '') as string),
      displayName: ((rec['displayName'] ?? '') as string),
      os: ((rec['os'] ?? '') as string),
      role: ((rec['role'] ?? 'Member') as string),
      isOnline: ((rec['isOnline'] ?? true) as boolean),
      availableShells: this.normalizeStringArray(rec['availableShells'])
    } as GroupMemberInfo;
    return memberInfo;
  }

  private normalizeStringArray(value: Object | undefined): Array<string> {
    let result: Array<string> = [];
    if (!Array.isArray(value)) return result;
    const items: Array<Object> = value as Array<Object>;
    for (let i: number = 0; i < items.length; i++) {
      if (typeof items[i] === 'string') result.push(items[i] as string);
    }
    return result;
  }

  // --- Event emission ---

  private emitMessage(type: string, data: Record<string, Object>): void {
    let listeners: Array<MessageCallbackWrapperInner> = this.messageCallbackWrappers.slice();
    for (let i: number = 0; i < listeners.length; i++) {
      try { listeners[i].callback(type, data); } catch (_e) { /* ignore */ }
    }
  }

  private emitStateChange(state: ConnectionState, deviceId: string): void {
    let listeners: Array<StateCallbackWrapperInner> = this.stateCallbackWrappers.slice();
    for (let i: number = 0; i < listeners.length; i++) {
      try { listeners[i].callback(state, deviceId); } catch (_e) { /* ignore */ }
    }
  }

  private emitDeviceListChanged(): void {
    const devices: Array<DeviceInfo> = this.getAllDevices();
    let listeners: Array<DeviceListCallbackWrapper> = this.deviceListCallbackWrappers.slice();
    for (let i: number = 0; i < listeners.length; i++) {
      try { listeners[i].callback(devices); } catch (_e) { /* ignore */ }
    }
  }

  private emitModeChange(mode: DeviceMode): void {
    let listeners: Array<ModeChangeCallbackWrapper> = this.modeChangeCallbackWrappers.slice();
    for (let i: number = 0; i < listeners.length; i++) {
      try { listeners[i].callback(mode); } catch (_e) { /* ignore */ }
    }
  }

  private buildConnectUrlWithToken(baseUrl: string, groupSecret: string): string {
    const trimmedSecret: string = groupSecret.trim();
    if (trimmedSecret.length === 0) return baseUrl;
    const token: string = encodeURIComponent(trimmedSecret);
    if (baseUrl.indexOf('token=') >= 0) {
      return baseUrl.replace(/token=[^&]*/g, 'token=' + token);
    }
    if (baseUrl.indexOf('?') >= 0) return baseUrl + '&token=' + token;
    return baseUrl + '?token=' + token;
  }
}

// Inner callback wrapper classes
class MessageCallbackWrapperInner {
  id: number = -1;
  callback: (type: string, data: Record<string, Object>) => void = (_t: string, _d: Record<string, Object>) => {};
}

class StateCallbackWrapperInner {
  id: number = -1;
  callback: (state: ConnectionState, deviceId: string) => void = (_s: ConnectionState, _d: string) => {};
}

// Singleton export
export const ConnectionManager: ConnectionManagerClass = new ConnectionManagerClass();
