import WebSocket from 'ws';
import { serialize, deserialize } from '../protocol/serializer.js';
export class RelayClient {
    ws = null;
    relayUrl = '';
    deviceId = '';
    displayName = '';
    os = '';
    availableShells = [];
    inviteCode = '';
    groupSecret = '';
    reconnectTimer = null;
    reconnectAttempts = 0;
    maxReconnectAttempts = 10;
    shouldReconnect = false;
    connected = false;
    log = () => { };
    callbacks = {};
    setLogger(fn) { this.log = fn; }
    setCallbacks(cb) { this.callbacks = cb; }
    isConnected() { return this.connected; }
    connect(relayUrl, deviceId, displayName, os, availableShells, inviteCode, groupSecret = '') {
        this.relayUrl = relayUrl;
        this.deviceId = deviceId;
        this.displayName = displayName;
        this.os = os;
        this.availableShells = availableShells;
        this.inviteCode = inviteCode;
        this.groupSecret = groupSecret;
        this.shouldReconnect = true;
        this.reconnectAttempts = 0;
        this.doConnect();
    }
    disconnect() {
        this.shouldReconnect = false;
        this.clearReconnectTimer();
        if (this.ws) {
            const wsToClose = this.ws;
            this.ws = null;
            try {
                if (wsToClose.readyState === WebSocket.OPEN || wsToClose.readyState === WebSocket.CONNECTING) {
                    wsToClose.close();
                }
            }
            catch (err) {
                this.log(`Error closing WebSocket: ${err.message}`);
            }
        }
        this.connected = false;
    }
    send(json) {
        if (this.ws && this.ws.readyState === WebSocket.OPEN) {
            this.ws.send(json);
        }
    }
    doConnect() {
        // Clean up existing connection first
        if (this.ws) {
            const oldWs = this.ws;
            this.ws = null;
            try {
                if (oldWs.readyState === WebSocket.OPEN || oldWs.readyState === WebSocket.CONNECTING) {
                    oldWs.close();
                }
            }
            catch (err) {
                this.log(`Error closing old WebSocket: ${err.message}`);
            }
        }
        const connectUrl = this.buildConnectUrl();
        this.log(`Connecting to relay: ${connectUrl}`);
        this.ws = new WebSocket(connectUrl);
        this.ws.on('open', () => {
            this.connected = true;
            this.reconnectAttempts = 0;
            this.log('Connected to relay server');
            // Register device
            this.send(serialize({
                type: 'device.register',
                deviceId: this.deviceId,
                displayName: this.displayName,
                os: this.os,
                availableShells: this.availableShells,
                mode: 'client',
            }));
            // Join group with group secret or invite code
            const joinPayload = {
                type: 'group.join.request',
                deviceId: this.deviceId,
                displayName: this.displayName,
                os: this.os,
                availableShells: this.availableShells,
                ...(this.groupSecret ? { groupSecret: this.groupSecret } : {}),
                ...(this.inviteCode ? { inviteCode: this.inviteCode } : {}),
            };
            this.send(serialize(joinPayload));
        });
        this.ws.on('message', (data) => {
            try {
                const json = data.toString('utf-8');
                this.handleMessage(json);
            }
            catch (err) {
                this.log(`Message error: ${err.message}`);
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
    handleMessage(json) {
        const message = deserialize(json);
        if (!message)
            return;
        this.log(`Relay → ${message.type}`);
        switch (message.type) {
            case 'group.join.accepted':
                {
                    const accepted = message;
                    const acceptedSecret = (accepted.groupSecret || '').trim();
                    if (acceptedSecret) {
                        this.groupSecret = acceptedSecret;
                        this.inviteCode = '';
                    }
                    this.callbacks.onGroupJoined?.(accepted.groupId, this.groupSecret);
                    this.log('Joined group successfully');
                }
                break;
            case 'group.join.rejected':
                this.log(`Group join rejected: ${message.reason}`);
                this.shouldReconnect = false;
                this.disconnect();
                this.callbacks.onKicked?.('Group join rejected');
                break;
            case 'group.server.change.prepare': {
                const prepare = message;
                const groupId = (prepare.groupId || '').trim();
                const groupSecret = (prepare.groupSecret || '').trim();
                if (groupId && groupSecret) {
                    this.callbacks.onServerChangeRequested?.(groupId, groupSecret);
                }
                else {
                    this.log('Server change prepare missing group info');
                }
                break;
            }
            case 'group.server.change.commit': {
                const commit = message;
                const newUrl = (commit.newServerUrl || '').trim();
                const newSecret = (commit.groupSecret || '').trim();
                const newGroupId = (commit.groupId || '').trim();
                if (newSecret) {
                    this.groupSecret = newSecret;
                }
                if (newUrl && newSecret) {
                    this.callbacks.onServerChanged?.(newUrl, newSecret, newGroupId);
                }
                else {
                    this.log('Server change commit missing new server info');
                }
                break;
            }
            case 'terminal.open': {
                const open = message;
                if (open.deviceId === this.deviceId) {
                    this.handleTerminalOpen(open.deviceId, open.shellId);
                }
                break;
            }
            case 'terminal.input': {
                const input = message;
                if (input.deviceId === this.deviceId) {
                    this.callbacks.onLocalTerminalInput?.(input.sessionId, input.data);
                }
                break;
            }
            case 'terminal.resize': {
                const resize = message;
                if (resize.deviceId === this.deviceId) {
                    this.callbacks.onLocalTerminalResize?.(resize.sessionId, resize.cols, resize.rows);
                }
                break;
            }
            case 'terminal.close': {
                const close = message;
                if (close.deviceId === this.deviceId) {
                    this.callbacks.onLocalTerminalSessionEnded?.(close.sessionId);
                    this.send(serialize({
                        type: 'terminal.closed',
                        deviceId: close.deviceId,
                        sessionId: close.sessionId,
                    }));
                }
                break;
            }
            case 'session.list.request': {
                const req = message;
                if (req.deviceId === this.deviceId) {
                    const sessions = this.callbacks.getLocalSessionList?.() || [];
                    this.send(serialize({
                        type: 'session.list',
                        deviceId: this.deviceId,
                        sessions,
                    }));
                }
                break;
            }
            case 'device.kicked': {
                const kicked = message;
                this.log(`Kicked from group: ${kicked.reason}`);
                this.shouldReconnect = false;
                this.disconnect();
                this.callbacks.onKicked?.(kicked.reason);
                break;
            }
            case 'group.dissolved': {
                const dissolved = message;
                this.log(`Group dissolved: ${dissolved.reason}`);
                this.shouldReconnect = false;
                this.disconnect();
                this.callbacks.onGroupDissolved?.(dissolved.reason);
                break;
            }
        }
    }
    async handleTerminalOpen(deviceId, shellId) {
        if (!this.callbacks.onLocalTerminalOpen) {
            this.send(serialize({
                type: 'error',
                code: 'terminal.open.failed',
                message: 'Terminal not available',
            }));
            return;
        }
        try {
            const { sessionId, cols, rows } = await this.callbacks.onLocalTerminalOpen(deviceId, shellId);
            this.send(serialize({
                type: 'terminal.opened',
                deviceId,
                sessionId,
                cols,
                rows,
            }));
            // Send initial snapshot
            const snapshot = this.callbacks.getLocalTerminalSnapshot?.(sessionId);
            if (snapshot) {
                this.send(serialize({
                    type: 'terminal.output',
                    deviceId,
                    sessionId,
                    data: snapshot,
                }));
            }
        }
        catch (err) {
            this.send(serialize({
                type: 'error',
                code: 'terminal.open.failed',
                message: err.message,
            }));
        }
    }
    /** Send terminal output back to relay for broadcast */
    sendTerminalOutput(deviceId, sessionId, data) {
        this.send(serialize({
            type: 'terminal.output',
            deviceId,
            sessionId,
            data,
        }));
    }
    sendTerminalClosed(deviceId, sessionId) {
        this.send(serialize({
            type: 'terminal.closed',
            deviceId,
            sessionId,
        }));
    }
    sendSessionList(deviceId, sessions) {
        this.send(serialize({
            type: 'session.list',
            deviceId,
            sessions,
        }));
    }
    scheduleReconnect() {
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
    clearReconnectTimer() {
        if (this.reconnectTimer) {
            clearTimeout(this.reconnectTimer);
            this.reconnectTimer = null;
        }
    }
    buildConnectUrl() {
        try {
            const url = new URL(this.relayUrl);
            if (this.groupSecret) {
                if (!url.searchParams.get('token')) {
                    url.searchParams.set('token', this.groupSecret);
                }
            }
            else if (this.inviteCode) {
                if (!url.searchParams.get('invite') && !url.searchParams.get('token')) {
                    url.searchParams.set('invite', this.inviteCode);
                }
            }
            return url.toString();
        }
        catch {
            // Fallback for invalid URL parsing
            if (!this.inviteCode && !this.groupSecret)
                return this.relayUrl;
            const hasQuery = this.relayUrl.includes('?');
            const tokenParam = this.groupSecret ? `token=${encodeURIComponent(this.groupSecret)}` : '';
            const inviteParam = this.inviteCode ? `invite=${encodeURIComponent(this.inviteCode)}` : '';
            const param = tokenParam || inviteParam;
            return this.relayUrl + (hasQuery ? '&' : '?') + param;
        }
    }
}
//# sourceMappingURL=relay-client.js.map