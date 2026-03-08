import { webSocket } from '@kit.NetworkKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { ConnectionState } from '../common/Constants';
import { DeviceInfo } from '../models/MessageTypes';

const DOMAIN = 0x0000;
const NET_TAG = 'PhoneShellNet';

// Message classes for protocol - must be explicit classes for ArkTS strict mode
class DeviceListRequestMsg {
  type: string = 'device.list.request';
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

  // Callbacks
  private messageCallbackWrappers: Array<MessageCallbackWrapper> = [];
  private stateCallbackWrappers: Array<StateCallbackWrapper> = [];
  private nextCallbackId: number = 1;

  // Current state
  private connectionState: ConnectionState = ConnectionState.DISCONNECTED;

  // Cached device list
  private cachedDevices: Array<DeviceInfo> = [];

  // Active terminal session
  private activeSessionId: string = '';
  private activeDeviceId: string = '';
  private bufferedTerminalOutput: string = '';
  private maxBufferedTerminalOutput: number = 50000;

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
    this.bufferedTerminalOutput = '';
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
    const payload: Record<string, Object> =
      JSON.parse(JSON.stringify({ type: 'error', message: message })) as Record<string, Object>;
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
    this.activeDeviceId = this.normalizeString(deviceId);
    this.activeSessionId = this.normalizeString(sessionId);
  }

  getActiveDeviceId(): string {
    return this.normalizeString(this.activeDeviceId);
  }

  getBufferedTerminalOutput(): string {
    return this.normalizeString(this.bufferedTerminalOutput);
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

  private doConnect(): void {
    this.updateState(ConnectionState.CONNECTING);
    hilog.info(DOMAIN, NET_TAG, 'doConnect start');

    this.ws = webSocket.createWebSocket();

    this.ws.on('open', (err: BusinessError, value: Object) => {
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
      if (err) {
        return;
      }
      if (typeof value === 'string') {
        hilog.info(DOMAIN, NET_TAG, 'message received len=%{public}d', value.length);
        this.handleMessage(value);
      }
    });

    this.ws.on('close', (err: BusinessError, value: webSocket.CloseResult) => {
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
    this.clearReconnectTimer();
    this.resetActiveSession();

    const activeSocket: webSocket.WebSocket | null = this.ws;
    this.ws = null;

    if (activeSocket !== null) {
      activeSocket.close().catch((err: BusinessError) => {
        hilog.error(DOMAIN, NET_TAG, 'disconnect close error %{public}s', this.describeError(err));
      });
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

  openTerminal(deviceId: string, shellId: string): void {
    const normalizedDeviceId: string = this.normalizeString(deviceId);
    if (normalizedDeviceId.length === 0) {
      return;
    }

    hilog.info(DOMAIN, NET_TAG, 'openTerminal device=%{public}s shell=%{public}s',
      normalizedDeviceId, this.normalizeString(shellId));
    this.activeDeviceId = normalizedDeviceId;
    this.bufferedTerminalOutput = '';
    let msg: TerminalOpenMsg = new TerminalOpenMsg();
    msg.type = 'terminal.open';
    msg.deviceId = normalizedDeviceId;
    msg.shellId = this.normalizeString(shellId);
    this.send(JSON.stringify(msg));
  }

  sendTerminalInput(data: string): void {
    const sessionId: string = this.getActiveSessionId();
    const payload: string = this.normalizeString(data);
    if (sessionId.length > 0 && payload.length > 0) {
      hilog.info(DOMAIN, NET_TAG, 'sendTerminalInput session=%{public}s len=%{public}d',
        sessionId, payload.length);
      let msg: TerminalInputMsg = new TerminalInputMsg();
      msg.type = 'terminal.input';
      msg.deviceId = this.getActiveDeviceId();
      msg.sessionId = sessionId;
      msg.data = payload;
      this.send(JSON.stringify(msg));
    }
  }

  resizeTerminal(cols: number, rows: number): void {
    const sessionId: string = this.getActiveSessionId();
    const deviceId: string = this.getActiveDeviceId();
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

  closeTerminal(): void {
    const sessionId: string = this.getActiveSessionId();
    const deviceId: string = this.getActiveDeviceId();
    hilog.warn(DOMAIN, NET_TAG, 'closeTerminal session=%{public}s device=%{public}s', sessionId, deviceId);
    if (sessionId.length > 0 && deviceId.length > 0) {
      let msg: TerminalCloseMsg = new TerminalCloseMsg();
      msg.type = 'terminal.close';
      msg.deviceId = deviceId;
      msg.sessionId = sessionId;
      this.send(JSON.stringify(msg));
    }
    this.resetActiveSession();
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
      } else if (msgType === 'terminal.output') {
        const outputData: string = this.normalizeString(parsed['data']);
        if (outputData.length > 0) {
          this.bufferedTerminalOutput += outputData;
          if (this.bufferedTerminalOutput.length > this.maxBufferedTerminalOutput) {
            this.bufferedTerminalOutput =
              this.bufferedTerminalOutput.substring(
                this.bufferedTerminalOutput.length - this.maxBufferedTerminalOutput
              );
          }
        }
      } else if (msgType === 'terminal.opened') {
        const sessionId: string = this.normalizeString(parsed['sessionId']);
        if (sessionId.length > 0) {
          this.activeSessionId = sessionId;
        }
        this.activeDeviceId = this.normalizeString(parsed['deviceId'], this.activeDeviceId);
      } else if (msgType === 'terminal.closed') {
        this.activeSessionId = '';
        this.bufferedTerminalOutput = '';
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
