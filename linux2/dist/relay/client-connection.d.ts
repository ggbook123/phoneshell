import WebSocket from 'ws';
import type { MemberRole } from '../protocol/messages.js';
export interface ClientConnection {
    clientId: string;
    ws: WebSocket;
    registeredDeviceId?: string;
    subscribedDeviceId?: string;
    subscribedSessionId?: string;
    memberRole: MemberRole;
    sendQueue: Promise<void>;
    isPanelClient: boolean;
}
export declare function createClientConnection(ws: WebSocket): ClientConnection;
export declare function sendToClient(client: ClientConnection, message: string, logger?: (msg: string) => void): Promise<void>;
