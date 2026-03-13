import { webSocket } from '@kit.NetworkKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { ConnectionState } from '../common/Constants';

const DOMAIN = 0x0000;
const TAG = 'DeviceConnection';

// Callback wrapper classes for ArkTS strict mode
class MessageCallbackWrapper {
  id: number = -1;
  callback: (type: string, data: Record<string, Object>) => void = (_t: string, _d: Record<string, Object>) => {};
}

class StateCallbackWrapper {
  id: number = -1;
  callback: (state: ConnectionState) => void = (_s: ConnectionState) => {};
}

export class DeviceConnection {
  private ws: webSocket.WebSocket | null = null;
  private url: string = '';
  private _deviceId: string = '';
  private _displayName: string = '';
  private reconnectTimer: number = -1;
  private reconnectAttempts: number = 0;
  private maxReconnectAttempts: number = 10;
  private reconnectBaseDelay: number = 1000;
  private shouldReconnect: boolean = false;
  private connectionGeneration: number = 0;
  private _connectionState: ConnectionState = ConnectionState.DISCONNECTED;

  private messageCallbackWrappers: Array<MessageCallbackWrapper> = [];
  private stateCallbackWrappers: Array<StateCallbackWrapper> = [];
  private nextCallbackId: number = 1;

  constructor(deviceId: string, displayName: string) {
    this._deviceId = deviceId;
    this._displayName = displayName;
  }

  get deviceId(): string { return this._deviceId; }
  get displayName(): string { return this._displayName; }
  set displayName(name: string) { this._displayName = name; }
  get connectionState(): ConnectionState { return this._connectionState; }
  get serverUrl(): string { return this.url; }

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

  connect(url: string): void {
    if (this._connectionState === ConnectionState.CONNECTING ||
        this._connectionState === ConnectionState.CONNECTED) {
      this.disconnect();
    }

    this.url = url.trim();
    if (this.url.length === 0) {
      this.updateState(ConnectionState.DISCONNECTED);
      return;
    }
    hilog.info(DOMAIN, TAG, 'connect device=%{public}s url=%{public}s', this._deviceId, this.url);
    this.shouldReconnect = true;
    this.reconnectAttempts = 0;
    this.doConnect();
  }

  disconnect(): void {
    hilog.info(DOMAIN, TAG, 'disconnect device=%{public}s', this._deviceId);
    this.shouldReconnect = false;
    this.connectionGeneration++;
    this.clearReconnectTimer();

    const activeSocket: webSocket.WebSocket | null = this.ws;
    this.ws = null;

    if (activeSocket !== null) {
      this.cleanupSocket(activeSocket);
    }
    this.updateState(ConnectionState.DISCONNECTED);
  }

  send(json: string): void {
    if (json.length === 0) return;
    if (this.ws !== null && this._connectionState === ConnectionState.CONNECTED) {
      this.ws.send(json).catch((err: BusinessError) => {
        hilog.error(DOMAIN, TAG, 'send error device=%{public}s %{public}s', this._deviceId, err.message ?? '');
      });
    }
  }

  private cleanupSocket(socket: webSocket.WebSocket): void {
    try {
      socket.off('open');
      socket.off('message');
      socket.off('close');
      socket.off('error');
    } catch (_e) { /* ignore */ }
    socket.close().catch((_err: BusinessError) => { /* ignore */ });
  }

  private doConnect(): void {
    this.connectionGeneration++;
    const generation: number = this.connectionGeneration;

    if (this.ws !== null) {
      this.cleanupSocket(this.ws);
      this.ws = null;
    }

    this.updateState(ConnectionState.CONNECTING);
    this.ws = webSocket.createWebSocket();

    this.ws.on('open', (err: BusinessError, _value: Object) => {
      if (generation !== this.connectionGeneration) return;
      if (err) {
        this.updateState(ConnectionState.DISCONNECTED);
        if (this.shouldReconnect) this.scheduleReconnect();
        return;
      }
      this.reconnectAttempts = 0;
      this.updateState(ConnectionState.CONNECTED);
    });

    this.ws.on('message', (err: BusinessError, value: string | ArrayBuffer) => {
      if (generation !== this.connectionGeneration) return;
      if (err) return;
      if (typeof value === 'string') {
        this.handleMessage(value);
      }
    });

    this.ws.on('close', (err: BusinessError, _value: webSocket.CloseResult) => {
      if (generation !== this.connectionGeneration) return;
      this.updateState(ConnectionState.DISCONNECTED);
      if (this.shouldReconnect) this.scheduleReconnect();
    });

    this.ws.on('error', (err: BusinessError) => {
      if (generation !== this.connectionGeneration) return;
      hilog.error(DOMAIN, TAG, 'ws error device=%{public}s %{public}s', this._deviceId, JSON.stringify(err));
      this.updateState(ConnectionState.DISCONNECTED);
      if (this.shouldReconnect) this.scheduleReconnect();
    });

    this.ws.connect(this.url).catch((err: BusinessError) => {
      if (generation !== this.connectionGeneration) return;
      this.updateState(ConnectionState.DISCONNECTED);
      if (this.shouldReconnect) this.scheduleReconnect();
    });
  }

  private handleMessage(raw: string): void {
    try {
      if (raw.length === 0) return;
      const parsed: Record<string, Object> = JSON.parse(raw) as Record<string, Object>;
      const typeValue: Object | undefined = parsed['type'];
      if (typeof typeValue !== 'string' || typeValue.length === 0) return;
      const msgType: string = typeValue as string;
      this.emitMessage(msgType, parsed);
    } catch (err) {
      hilog.error(DOMAIN, TAG, 'handleMessage error %{public}s', (err as Error).message ?? '');
    }
  }

  private updateState(state: ConnectionState): void {
    this._connectionState = state;
    let listeners: Array<StateCallbackWrapper> = this.stateCallbackWrappers.slice();
    for (let i: number = 0; i < listeners.length; i++) {
      try { listeners[i].callback(state); } catch (_e) { /* ignore */ }
    }
  }

  private emitMessage(type: string, data: Record<string, Object>): void {
    let listeners: Array<MessageCallbackWrapper> = this.messageCallbackWrappers.slice();
    for (let i: number = 0; i < listeners.length; i++) {
      try { listeners[i].callback(type, data); } catch (_e) { /* ignore */ }
    }
  }

  private scheduleReconnect(): void {
    if (this.reconnectAttempts >= this.maxReconnectAttempts) {
      this.shouldReconnect = false;
      return;
    }
    this.clearReconnectTimer();
    const delay: number = Math.min(this.reconnectBaseDelay * Math.pow(2, this.reconnectAttempts), 30000);
    this.reconnectAttempts++;
    this.reconnectTimer = setTimeout(() => {
      if (this.shouldReconnect) this.doConnect();
    }, delay) as number;
  }

  private clearReconnectTimer(): void {
    if (this.reconnectTimer !== -1) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = -1;
    }
  }
}
