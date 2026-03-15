import type { SessionInfo } from '../protocol/messages.js';
type LogFn = (msg: string) => void;
export interface RelayClientCallbacks {
    onLocalTerminalInput?: (sessionId: string, data: string) => void;
    onLocalTerminalResize?: (sessionId: string, cols: number, rows: number) => void;
    onLocalTerminalOpen?: (deviceId: string, shellId: string) => Promise<{
        sessionId: string;
        cols: number;
        rows: number;
    }>;
    onLocalTerminalSessionEnded?: (sessionId: string) => void;
    getLocalSessionList?: () => SessionInfo[];
    getLocalTerminalSnapshot?: (sessionId: string) => string;
    onTerminalOutput?: (deviceId: string, sessionId: string, data: string) => void;
    onKicked?: (reason: string) => void;
    onGroupDissolved?: (reason: string) => void;
    onGroupJoined?: (groupId: string, groupSecret?: string) => void;
}
export declare class RelayClient {
    private ws;
    private relayUrl;
    private deviceId;
    private displayName;
    private os;
    private availableShells;
    private inviteCode;
    private groupSecret;
    private reconnectTimer;
    private reconnectAttempts;
    private readonly maxReconnectAttempts;
    private shouldReconnect;
    private connected;
    private log;
    private callbacks;
    setLogger(fn: LogFn): void;
    setCallbacks(cb: RelayClientCallbacks): void;
    isConnected(): boolean;
    connect(relayUrl: string, deviceId: string, displayName: string, os: string, availableShells: string[], inviteCode: string, groupSecret?: string): void;
    disconnect(): void;
    send(json: string): void;
    private doConnect;
    private handleMessage;
    private handleTerminalOpen;
    /** Send terminal output back to relay for broadcast */
    sendTerminalOutput(deviceId: string, sessionId: string, data: string): void;
    sendTerminalClosed(deviceId: string, sessionId: string): void;
    sendSessionList(deviceId: string, sessions: SessionInfo[]): void;
    private scheduleReconnect;
    private clearReconnectTimer;
    private buildConnectUrl;
}
export {};
