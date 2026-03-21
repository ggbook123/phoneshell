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

let clientIdCounter = 0;

export function createClientConnection(ws: WebSocket): ClientConnection {
  return {
    clientId: `client-${++clientIdCounter}`,
    ws,
    memberRole: 'Member',
    sendQueue: Promise.resolve(),
    isPanelClient: false,
  };
}

export function sendToClient(client: ClientConnection, message: string, logger?: (msg: string) => void): Promise<void> {
  const promise = new Promise<void>((resolve) => {
    client.sendQueue = client.sendQueue.then(() => {
      return new Promise<void>((done) => {
        if (client.ws.readyState !== WebSocket.OPEN) {
          resolve();
          done();
          return;
        }
        client.ws.send(message, (err) => {
          if (err) {
            logger?.(`Failed to send message to client ${client.clientId}: ${err.message}`);
          }
          resolve();
          done();
        });
      });
    }).catch((err) => {
      logger?.(`Send queue error for client ${client.clientId}: ${err.message}`);
    });
  });
  return promise;
}
