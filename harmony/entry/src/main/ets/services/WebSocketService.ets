import { webSocket } from '@kit.NetworkKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { ConnectionState } from '../common/Constants';
import { DeviceInfo } from '../models/MessageTypes';

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

  private emitError(message: string): void {
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
    return this.cachedDevices;
  }

  getActiveSessionId(): string {
    return this.activeSessionId;
  }

  setActiveSession(deviceId: string, sessionId: string): void {
    this.activeDeviceId = deviceId;
    this.activeSessionId = sessionId;
  }

  getActiveDeviceId(): string {
    return this.activeDeviceId;
  }

  getBufferedTerminalOutput(): string {
    return this.bufferedTerminalOutput;
  }

  connect(url: string): void {
    if (this.connectionState === ConnectionState.CONNECTING ||
        this.connectionState === ConnectionState.CONNECTED) {
      this.disconnect();
    }

    this.serverUrl = url;
    this.shouldReconnect = true;
    this.reconnectAttempts = 0;
    this.doConnect();
  }

  private doConnect(): void {
    this.updateState(ConnectionState.CONNECTING);

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
      this.reconnectAttempts = 0;
      this.updateState(ConnectionState.CONNECTED);
    });

    this.ws.on('message', (err: BusinessError, value: string | ArrayBuffer) => {
      if (err) {
        return;
      }
      if (typeof value === 'string') {
        this.handleMessage(value);
      }
    });

    this.ws.on('close', (err: BusinessError, value: webSocket.CloseResult) => {
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
    this.shouldReconnect = false;
    this.clearReconnectTimer();
    this.activeSessionId = '';
    this.activeDeviceId = '';
    this.bufferedTerminalOutput = '';

    if (this.ws !== null) {
      this.ws.close().catch((_err: BusinessError) => {
        // Ignore close errors
      });
      this.ws = null;
    }
    this.updateState(ConnectionState.DISCONNECTED);
  }

  send(json: string): void {
    if (this.ws !== null && this.connectionState === ConnectionState.CONNECTED) {
      this.ws.send(json).catch((_err: BusinessError) => {
        // Ignore send errors
      });
    }
  }

  // --- High-level protocol methods ---

  requestDeviceList(): void {
    let msg: DeviceListRequestMsg = new DeviceListRequestMsg();
    this.send(JSON.stringify(msg));
  }

  openTerminal(deviceId: string, shellId: string): void {
    this.activeDeviceId = deviceId;
    this.bufferedTerminalOutput = '';
    let msg: TerminalOpenMsg = new TerminalOpenMsg();
    msg.type = 'terminal.open';
    msg.deviceId = deviceId;
    msg.shellId = shellId;
    this.send(JSON.stringify(msg));
  }

  sendTerminalInput(data: string): void {
    if (this.activeSessionId.length > 0) {
      let msg: TerminalInputMsg = new TerminalInputMsg();
      msg.type = 'terminal.input';
      msg.deviceId = this.activeDeviceId;
      msg.sessionId = this.activeSessionId;
      msg.data = data;
      this.send(JSON.stringify(msg));
    }
  }

  resizeTerminal(cols: number, rows: number): void {
    if (this.activeSessionId.length > 0) {
      let msg: TerminalResizeMsg = new TerminalResizeMsg();
      msg.type = 'terminal.resize';
      msg.deviceId = this.activeDeviceId;
      msg.sessionId = this.activeSessionId;
      msg.cols = cols;
      msg.rows = rows;
      this.send(JSON.stringify(msg));
    }
  }

  closeTerminal(): void {
    if (this.activeSessionId.length > 0) {
      let msg: TerminalCloseMsg = new TerminalCloseMsg();
      msg.type = 'terminal.close';
      msg.deviceId = this.activeDeviceId;
      msg.sessionId = this.activeSessionId;
      this.send(JSON.stringify(msg));
      this.activeSessionId = '';
      this.activeDeviceId = '';
      this.bufferedTerminalOutput = '';
    }
  }

  // --- Internal methods ---

  private handleMessage(raw: string): void {
    try {
      const parsed: Record<string, Object> = JSON.parse(raw) as Record<string, Object>;
      const typeValue: Object | undefined = parsed['type'];
      if (typeValue === undefined || typeValue === null) {
        return;
      }
      const msgType: string = typeValue as string;

      // Update cached data for specific message types
      if (msgType === 'device.list') {
        const devices: Object | undefined = parsed['devices'];
        if (devices !== undefined && devices !== null) {
          this.cachedDevices = devices as Array<DeviceInfo>;
        }
      } else if (msgType === 'terminal.output') {
        const outputData: Object | undefined = parsed['data'];
        if (outputData !== undefined && outputData !== null) {
          this.bufferedTerminalOutput += outputData as string;
          if (this.bufferedTerminalOutput.length > this.maxBufferedTerminalOutput) {
            this.bufferedTerminalOutput =
              this.bufferedTerminalOutput.substring(
                this.bufferedTerminalOutput.length - this.maxBufferedTerminalOutput
              );
          }
        }
      } else if (msgType === 'terminal.opened') {
        const sessionId: Object | undefined = parsed['sessionId'];
        if (sessionId !== undefined && sessionId !== null) {
          this.activeSessionId = sessionId as string;
        }
      } else if (msgType === 'terminal.closed') {
        this.activeSessionId = '';
        this.bufferedTerminalOutput = '';
      }

      this.emitMessage(msgType, parsed);
    } catch (e) {
      // Ignore parse errors
    }
  }

  private updateState(state: ConnectionState): void {
    this.connectionState = state;
    let listeners: Array<StateCallbackWrapper> = [];
    for (let i: number = 0; i < this.stateCallbackWrappers.length; i++) {
      listeners.push(this.stateCallbackWrappers[i]);
    }
    for (let i: number = 0; i < listeners.length; i++) {
      listeners[i].callback(state);
    }
  }

  private emitMessage(type: string, data: Record<string, Object>): void {
    let listeners: Array<MessageCallbackWrapper> = [];
    for (let i: number = 0; i < this.messageCallbackWrappers.length; i++) {
      listeners.push(this.messageCallbackWrappers[i]);
    }
    for (let i: number = 0; i < listeners.length; i++) {
      listeners[i].callback(type, data);
    }
  }

  private scheduleReconnect(): void {
    if (this.reconnectAttempts >= this.maxReconnectAttempts) {
      this.shouldReconnect = false;
      return;
    }

    this.clearReconnectTimer();
    const delay: number = Math.min(
      this.reconnectBaseDelay * Math.pow(2, this.reconnectAttempts),
      30000
    );
    this.reconnectAttempts++;

    this.reconnectTimer = setTimeout(() => {
      if (this.shouldReconnect) {
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
