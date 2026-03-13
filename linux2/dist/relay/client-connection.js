import WebSocket from 'ws';
let clientIdCounter = 0;
export function createClientConnection(ws) {
    return {
        clientId: `client-${++clientIdCounter}`,
        ws,
        memberRole: 'Member',
        sendQueue: Promise.resolve(),
        isPanelClient: false,
    };
}
export function sendToClient(client, message) {
    const promise = new Promise((resolve) => {
        client.sendQueue = client.sendQueue.then(() => {
            return new Promise((done) => {
                if (client.ws.readyState !== WebSocket.OPEN) {
                    resolve();
                    done();
                    return;
                }
                client.ws.send(message, (err) => {
                    if (err) { /* ignore send errors */ }
                    resolve();
                    done();
                });
            });
        });
    });
    return promise;
}
//# sourceMappingURL=client-connection.js.map