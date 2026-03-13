import WebSocket from 'ws';
import { serialize, deserialize } from '../protocol/serializer.js';
import type { SessionInfo, Message } from '../protocol/messages.js';

type LogFn = (msg: string) => void;

export interface RelayClientCallbacks {
  onLocalTerminalInput?: (sessionId: string, data: string) => void;
  onLocalTerminalResize?: (sessionId: string, cols: number, rows: number) => void;
  onLocalTerminalOpen?: (deviceId: string, shellId: string) => Promise<{ sessionId: string; cols: number; rows: number }>;
  onLocalTerminalSessionEnded?: (sessionId: string) => void;
  getLocalSessionList?: () => SessionInfo[];
  getLocalTerminalSnapshot?: (sessionId: string) => string;
  onTerminalOutput?: (deviceId: string, sessionId: string, data: string) => void;
  onKicked?: (reason: string) => void;
  onGroupDissolved?: (reason: string) => void;
}

export class RelayClient {
  private ws: WebSocket | null = null;
  private relayUrl: string = '';
  private deviceId: string = '';
  private displayName: string = '';
  private os: string = '';
  private availableShells: string[] = [];
  private inviteCode: string = '';
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private reconnectAttempts = 0;
  private readonly maxReconnectAttempts = 10;
  private shouldReconnect = false;
  private connected = false;
  private log: LogFn = () => {};
  private callbacks: RelayClientCallbacks = {};

  setLogger(fn: LogFn): void { this.log = fn; }
  setCallbacks(cb: RelayClientCallbacks): void { this.callbacks = cb; }
  isConnected(): boolean { return this.connected; }

  connect(
    relayUrl: string,
    deviceId: string,
    displayName: string,
    os: string,
    availableShells: string[],
    inviteCode: string,
  ): void {
    this.relayUrl = relayUrl;
    this.deviceId = deviceId;
    this.displayName = displayName;
    this.os = os;
    this.availableShells = availableShells;
    this.inviteCode = inviteCode;
    this.shouldReconnect = true;
    this.reconnectAttempts = 0;
    this.doConnect();
  }

  disconnect(): void {
    this.shouldReconnect = false;
    this.clearReconnectTimer();
    if (this.ws) {
      try { this.ws.close(); } catch {}
      this.ws = null;
    }
    this.connected = false;
  }

  send(json: string): void {
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(json);
    }
  }

  private doConnect(): void {
    if (this.ws) {
      try { this.ws.close(); } catch {}
      this.ws = null;
    }

    this.log(`Connecting to relay: ${this.relayUrl}`);
    this.ws = new WebSocket(this.relayUrl);

    this.ws.on('open', () => {
      this.connected = true;
      this.reconnectAttempts = 0;
      this.log('Connected to relay server');

      // Register device
      this.send(serialize({
        type: 'device.register' as const,
        deviceId: this.deviceId,
        displayName: this.displayName,
        os: this.os,
        availableShells: this.availableShells,
        mode: 'client' as const,
      }));

      // Join group with invite code
      this.send(serialize({
        type: 'group.join.request' as const,
        inviteCode: this.inviteCode,
        deviceId: this.deviceId,
        displayName: this.displayName,
        os: this.os,
        availableShells: this.availableShells,
      }));
    });

    this.ws.on('message', (data: WebSocket.RawData) => {
      try {
        const json = data.toString('utf-8');
        this.handleMessage(json);
      } catch (err) {
        this.log(`Message error: ${(err as Error).message}`);
      }
    });

    this.ws.on('close', () => {
      this.connected = false;
      this.log('Disconnected from relay server');
      if (this.shouldReconnect) {
        this.scheduleReconnect();
      }
    });

    this.ws.on('error', (err) => {
      this.log(`WebSocket error: ${err.message}`);
    });
  }

  private handleMessage(json: string): void {
    const message = deserialize(json);
    if (!message) return;

    this.log(`Relay → ${message.type}`);

    switch (message.type) {
      case 'group.join.accepted':
        this.log('Joined group successfully');
        break;
      case 'group.join.rejected':
        this.log(`Group join rejected: ${(message as { reason: string }).reason}`);
        this.shouldReconnect = false;
        this.disconnect();
        this.callbacks.onKicked?.('Group join rejected');
        break;
      case 'terminal.open': {
        const open = message as { deviceId: string; shellId: string };
        if (open.deviceId === this.deviceId) {
          this.handleTerminalOpen(open.deviceId, open.shellId);
        }
        break;
      }
      case 'terminal.input': {
        const input = message as { deviceId: string; sessionId: string; data: string };
        if (input.deviceId === this.deviceId) {
          this.callbacks.onLocalTerminalInput?.(input.sessionId, input.data);
        }
        break;
      }
      case 'terminal.resize': {
        const resize = message as { deviceId: string; sessionId: string; cols: number; rows: number };
        if (resize.deviceId === this.deviceId) {
          this.callbacks.onLocalTerminalResize?.(resize.sessionId, resize.cols, resize.rows);
        }
        break;
      }
      case 'terminal.close': {
        const close = message as { deviceId: string; sessionId: string };
        if (close.deviceId === this.deviceId) {
          this.callbacks.onLocalTerminalSessionEnded?.(close.sessionId);
          this.send(serialize({
            type: 'terminal.closed' as const,
            deviceId: close.deviceId,
            sessionId: close.sessionId,
          }));
        }
        break;
      }
      case 'session.list.request': {
        const req = message as { deviceId: string };
        if (req.deviceId === this.deviceId) {
          const sessions = this.callbacks.getLocalSessionList?.() || [];
          this.send(serialize({
            type: 'session.list' as const,
            deviceId: this.deviceId,
            sessions,
          }));
        }
        break;
      }
      case 'device.kicked': {
        const kicked = message as { reason: string };
        this.log(`Kicked from group: ${kicked.reason}`);
        this.shouldReconnect = false;
        this.disconnect();
        this.callbacks.onKicked?.(kicked.reason);
        break;
      }
      case 'group.dissolved': {
        const dissolved = message as { reason: string };
        this.log(`Group dissolved: ${dissolved.reason}`);
        this.shouldReconnect = false;
        this.disconnect();
        this.callbacks.onGroupDissolved?.(dissolved.reason);
        break;
      }
    }
  }

  private async handleTerminalOpen(deviceId: string, shellId: string): Promise<void> {
    if (!this.callbacks.onLocalTerminalOpen) {
      this.send(serialize({
        type: 'error' as const,
        code: 'terminal.open.failed',
        message: 'Terminal not available',
      }));
      return;
    }
    try {
      const { sessionId, cols, rows } = await this.callbacks.onLocalTerminalOpen(deviceId, shellId);
      this.send(serialize({
        type: 'terminal.opened' as const,
        deviceId,
        sessionId,
        cols,
        rows,
      }));
      // Send initial snapshot
      const snapshot = this.callbacks.getLocalTerminalSnapshot?.(sessionId);
      if (snapshot) {
        this.send(serialize({
          type: 'terminal.output' as const,
          deviceId,
          sessionId,
          data: snapshot,
        }));
      }
    } catch (err) {
      this.send(serialize({
        type: 'error' as const,
        code: 'terminal.open.failed',
        message: (err as Error).message,
      }));
    }
  }

  /** Send terminal output back to relay for broadcast */
  sendTerminalOutput(deviceId: string, sessionId: string, data: string): void {
    this.send(serialize({
      type: 'terminal.output' as const,
      deviceId,
      sessionId,
      data,
    }));
  }

  sendTerminalClosed(deviceId: string, sessionId: string): void {
    this.send(serialize({
      type: 'terminal.closed' as const,
      deviceId,
      sessionId,
    }));
  }

  sendSessionList(deviceId: string, sessions: SessionInfo[]): void {
    this.send(serialize({
      type: 'session.list' as const,
      deviceId,
      sessions,
    }));
  }

  private scheduleReconnect(): void {
    if (this.reconnectAttempts >= this.maxReconnectAttempts) {
      this.log('Max reconnect attempts reached');
      this.shouldReconnect = false;
      return;
    }

    this.clearReconnectTimer();
    const delay = Math.min(1000 * Math.pow(2, this.reconnectAttempts), 30000);
    this.reconnectAttempts++;
    this.log(`Reconnecting in ${delay}ms (attempt ${this.reconnectAttempts})`);

    this.reconnectTimer = setTimeout(() => {
      if (this.shouldReconnect) {
        this.doConnect();
      }
    }, delay);
  }

  private clearReconnectTimer(): void {
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
  }
}
