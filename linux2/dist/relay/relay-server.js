import crypto from 'node:crypto';
import WebSocket from 'ws';
import { serialize, deserialize } from '../protocol/serializer.js';
import { createClientConnection, sendToClient } from './client-connection.js';
import { TokenManager } from '../auth/token-manager.js';
import { buildGroupBindPayload, buildPanelLoginPayload } from '../auth/qr-service.js';
export class RelayServer {
    devices = new Map();
    clients = new Map();
    pendingAuths = new Map();
    tokenManager = new TokenManager();
    outputChains = new Map();
    group = null;
    groupStore = null;
    authToken = '';
    startedAtUtc = new Date();
    log = () => { };
    callbacks = {};
    setLogger(fn) { this.log = fn; }
    setCallbacks(cb) { this.callbacks = cb; }
    setAuthToken(token) { this.authToken = token; }
    getGroup() { return this.group; }
    initGroup(groupStore, deviceId, displayName, os, availableShells) {
        this.groupStore = groupStore;
        this.group = groupStore.loadGroup();
        if (!this.group) {
            this.group = {
                groupId: crypto.randomUUID().replace(/-/g, ''),
                groupSecret: this.authToken || crypto.randomBytes(32).toString('base64url'),
                serverDeviceId: deviceId,
                createdAt: new Date().toISOString(),
                members: [],
            };
        }
        this.group.serverDeviceId = deviceId;
        const existing = this.group.members.find(m => m.deviceId === deviceId);
        if (!existing) {
            this.group.members.push({
                deviceId, displayName, os,
                role: 'Server',
                joinedAt: new Date().toISOString(),
                availableShells,
            });
        }
        else {
            existing.displayName = displayName;
            existing.os = os;
            existing.role = 'Server';
            existing.availableShells = availableShells;
        }
        if (!this.authToken)
            this.authToken = this.group.groupSecret;
        groupStore.saveGroup(this.group);
        this.log(`Group initialized: ${this.group.groupId} (secret: ${this.group.groupSecret.slice(0, 8)}...)`);
    }
    registerLocalDevice(deviceId, displayName, os, availableShells) {
        this.devices.set(deviceId, { deviceId, displayName, os, availableShells, isLocal: true });
        this.log(`Local device registered: ${displayName} (${deviceId})`);
    }
    start() {
        this.startedAtUtc = new Date();
    }
    getDeviceList() {
        return Array.from(this.devices.values()).map(d => ({
            deviceId: d.deviceId,
            displayName: d.displayName,
            os: d.os,
            isOnline: true,
            availableShells: d.availableShells,
        }));
    }
    buildGroupMemberInfoList() {
        if (!this.group)
            return [];
        return this.group.members.map(m => ({
            deviceId: m.deviceId,
            displayName: m.displayName,
            os: m.os,
            role: m.role,
            isOnline: this.devices.has(m.deviceId),
            availableShells: m.availableShells,
        }));
    }
    // --- Auth ---
    isAuthorized(token) {
        if (!this.authToken)
            return true;
        if (!token)
            return false;
        const trimmed = token.trim();
        return this.tokenManager.tokensEqual(trimmed, this.authToken) ||
            this.tokenManager.isPanelTokenValid(trimmed);
    }
    extractToken(headers, query) {
        const auth = headers['authorization'];
        if (auth?.startsWith('Bearer '))
            return auth.slice(7).trim();
        const xToken = headers['x-phoneshell-token'];
        if (xToken)
            return xToken.trim();
        if (query)
            return query.trim();
        return undefined;
    }
    // --- WebSocket ---
    handleConnection(ws) {
        const client = createClientConnection(ws);
        this.clients.set(client.clientId, client);
        this.log(`Client connected: ${client.clientId}`);
        // Ping loop
        const pingInterval = setInterval(() => {
            if (ws.readyState === WebSocket.OPEN) {
                ws.ping();
            }
            else {
                clearInterval(pingInterval);
            }
        }, 30000);
        ws.on('message', async (data) => {
            try {
                const json = data.toString('utf-8');
                await this.handleMessage(client, json);
            }
            catch (err) {
                this.log(`Client ${client.clientId} message error: ${err.message}`);
            }
        });
        ws.on('close', () => {
            clearInterval(pingInterval);
            this.handleClientDisconnect(client);
        });
        ws.on('error', (err) => {
            this.log(`Client ${client.clientId} ws error: ${err.message}`);
        });
    }
    handleClientDisconnect(client) {
        // Clean up terminal subscription
        if (client.subscribedDeviceId && client.subscribedSessionId) {
            const device = this.devices.get(client.subscribedDeviceId);
            if (device?.isLocal) {
                try {
                    this.callbacks.onLocalTerminalSessionEnded?.(client.subscribedSessionId);
                }
                catch { }
            }
        }
        // Clean up device registration
        if (client.registeredDeviceId) {
            this.devices.delete(client.registeredDeviceId);
            this.log(`Device unregistered: ${client.registeredDeviceId}`);
            // Bug1 fix: mobile disconnect preserves BOTH login sessions and access tokens.
            // Login sessions have their own expiry — clearing them here would break the
            // web panel login flow when the phone briefly disconnects before scanning.
            // Only explicit unbind (mobile.unbind) calls tokenManager.clearAll().
            if (this.group && client.registeredDeviceId === this.group.boundMobileId) {
                this.log('Bound mobile disconnected — tokens and login sessions preserved');
            }
            // Broadcast group member left
            if (this.group) {
                const leftMsg = serialize({ type: 'group.member.left', deviceId: client.registeredDeviceId });
                this.broadcastToOthers(client, leftMsg);
            }
        }
        this.clients.delete(client.clientId);
        this.log(`Client disconnected: ${client.clientId}`);
    }
    async handleMessage(client, json) {
        const message = deserialize(json);
        if (!message) {
            this.log(`Client ${client.clientId} sent unrecognized message`);
            return;
        }
        this.log(`Client ${client.clientId} -> ${message.type}`);
        switch (message.type) {
            case 'group.join.request':
                await this.handleGroupJoinRequest(client, message);
                break;
            case 'group.kick':
                await this.handleGroupKick(client, message);
                break;
            case 'device.unregister':
                await this.handleDeviceUnregister(client, message);
                break;
            case 'mobile.bind.request':
                await this.handleMobileBindRequest(client, message);
                break;
            case 'mobile.unbind':
                await this.handleMobileUnbind(client, message);
                break;
            case 'auth.response':
                this.handleAuthResponse(message);
                break;
            case 'panel.login.scan':
                await this.handlePanelLoginScan(client, message);
                break;
            case 'device.register': {
                const reg = message;
                this.devices.set(reg.deviceId, {
                    deviceId: reg.deviceId, displayName: reg.displayName,
                    os: reg.os, availableShells: reg.availableShells,
                    clientId: client.clientId, isLocal: false,
                });
                client.registeredDeviceId = reg.deviceId;
                this.log(`Device registered: ${reg.displayName} (${reg.deviceId})`);
                break;
            }
            case 'device.list.request': {
                const list = serialize({ type: 'device.list', devices: this.getDeviceList() });
                await sendToClient(client, list);
                break;
            }
            case 'session.list.request': {
                const req = message;
                const device = this.devices.get(req.deviceId);
                if (device) {
                    client.subscribedDeviceId = req.deviceId;
                    client.subscribedSessionId = undefined;
                    if (device.isLocal) {
                        const sessions = this.callbacks.getLocalSessionList?.() || [];
                        await sendToClient(client, serialize({
                            type: 'session.list', deviceId: req.deviceId, sessions,
                        }));
                    }
                    else if (device.clientId) {
                        const deviceClient = this.clients.get(device.clientId);
                        if (deviceClient)
                            await sendToClient(deviceClient, json);
                    }
                }
                break;
            }
            case 'terminal.input': {
                const input = message;
                const device = this.devices.get(input.deviceId);
                if (device) {
                    client.subscribedDeviceId = input.deviceId;
                    client.subscribedSessionId = input.sessionId;
                    if (device.isLocal) {
                        this.callbacks.onLocalTerminalInput?.(input.sessionId, input.data);
                    }
                    else if (device.clientId) {
                        const dc = this.clients.get(device.clientId);
                        if (dc)
                            await sendToClient(dc, json);
                    }
                }
                break;
            }
            case 'terminal.output': {
                const output = message;
                client.subscribedDeviceId ??= output.deviceId;
                for (const c of this.clients.values()) {
                    if (c.clientId !== client.clientId &&
                        c.subscribedDeviceId === output.deviceId &&
                        c.subscribedSessionId === output.sessionId) {
                        await sendToClient(c, json);
                    }
                }
                break;
            }
            case 'terminal.open': {
                const open = message;
                const target = this.devices.get(open.deviceId);
                if (target) {
                    client.subscribedDeviceId = open.deviceId;
                    client.subscribedSessionId = undefined;
                    if (target.isLocal) {
                        await this.handleLocalTerminalOpen(client, open);
                    }
                    else if (target.clientId) {
                        const dc = this.clients.get(target.clientId);
                        if (dc)
                            await sendToClient(dc, json);
                    }
                }
                break;
            }
            case 'terminal.resize': {
                const resize = message;
                const target = this.devices.get(resize.deviceId);
                if (target) {
                    const prevSession = client.subscribedSessionId;
                    client.subscribedDeviceId = resize.deviceId;
                    client.subscribedSessionId = resize.sessionId;
                    if (target.isLocal) {
                        this.callbacks.onLocalTerminalResize?.(resize.sessionId, resize.cols, resize.rows);
                        // Bug3 fix: send snapshot when client re-subscribes to a session
                        if (prevSession !== resize.sessionId) {
                            const snapshot = this.callbacks.getLocalTerminalSnapshot?.(resize.sessionId);
                            if (snapshot) {
                                await sendToClient(client, serialize({
                                    type: 'terminal.output',
                                    deviceId: resize.deviceId, sessionId: resize.sessionId, data: snapshot,
                                }));
                            }
                        }
                    }
                    else if (target.clientId) {
                        const dc = this.clients.get(target.clientId);
                        if (dc)
                            await sendToClient(dc, json);
                    }
                }
                break;
            }
            case 'terminal.close': {
                const close = message;
                const target = this.devices.get(close.deviceId);
                if (target) {
                    client.subscribedDeviceId = close.deviceId;
                    client.subscribedSessionId = close.sessionId;
                    if (target.isLocal) {
                        await sendToClient(client, serialize({
                            type: 'terminal.closed', deviceId: close.deviceId, sessionId: close.sessionId,
                        }));
                        client.subscribedSessionId = undefined;
                        this.callbacks.onLocalTerminalSessionEnded?.(close.sessionId);
                    }
                    else if (target.clientId) {
                        const dc = this.clients.get(target.clientId);
                        if (dc)
                            await sendToClient(dc, json);
                    }
                }
                break;
            }
            case 'terminal.opened':
            case 'terminal.closed':
            case 'session.list':
                // Forward to all other clients
                for (const c of this.clients.values()) {
                    if (c.clientId !== client.clientId)
                        await sendToClient(c, json);
                }
                break;
            case 'control.force_disconnect':
            case 'control.request':
            case 'control.grant':
                for (const c of this.clients.values()) {
                    if (c.clientId !== client.clientId)
                        await sendToClient(c, json);
                }
                break;
            case 'group.server.change.request':
                await this.handleServerChangeRequest(client, message);
                break;
            case 'group.server.change.prepare':
                await this.handleServerChangePrepare(client, message);
                break;
            case 'group.secret.rotate.request':
                await this.handleSecretRotateRequest(client, message);
                break;
            case 'group.secret.rotate.done':
                this.broadcastToOthers(client, json);
                break;
        }
    }
    // --- Local terminal open ---
    async handleLocalTerminalOpen(client, open) {
        if (!this.callbacks.onLocalTerminalOpen) {
            await sendToClient(client, serialize({
                type: 'error', code: 'terminal.open.failed', message: 'Terminal not available',
            }));
            return;
        }
        try {
            const { sessionId, cols, rows } = await this.callbacks.onLocalTerminalOpen(open.deviceId, open.shellId);
            client.subscribedSessionId = sessionId;
            await sendToClient(client, serialize({
                type: 'terminal.opened',
                deviceId: open.deviceId, sessionId, cols, rows,
            }));
            // Send initial snapshot
            const snapshot = this.callbacks.getLocalTerminalSnapshot?.(sessionId);
            if (snapshot) {
                await sendToClient(client, serialize({
                    type: 'terminal.output',
                    deviceId: open.deviceId, sessionId, data: snapshot,
                }));
            }
            this.log(`Local terminal opened for ${client.clientId}, session=${sessionId}`);
        }
        catch (err) {
            await sendToClient(client, serialize({
                type: 'error', code: 'terminal.open.failed', message: err.message,
            }));
        }
    }
    // --- Broadcast terminal output to subscribed clients ---
    async broadcastLocalTerminalOutput(deviceId, sessionId, data) {
        const msg = serialize({ type: 'terminal.output', deviceId, sessionId, data });
        const promises = [];
        for (const client of this.clients.values()) {
            if (client.subscribedDeviceId === deviceId && client.subscribedSessionId === sessionId) {
                promises.push(sendToClient(client, msg));
            }
        }
        await Promise.all(promises);
    }
    async broadcastLocalTerminalClosed(deviceId, sessionId) {
        const msg = serialize({ type: 'terminal.closed', deviceId, sessionId });
        for (const client of this.clients.values()) {
            if (client.subscribedDeviceId === deviceId && client.subscribedSessionId === sessionId) {
                client.subscribedSessionId = undefined;
                await sendToClient(client, msg);
            }
        }
    }
    async broadcastLocalSessionListChanged(deviceId) {
        const sessions = this.callbacks.getLocalSessionList?.() || [];
        const msg = serialize({ type: 'session.list', deviceId, sessions });
        for (const client of this.clients.values()) {
            if (client.subscribedDeviceId === deviceId) {
                await sendToClient(client, msg);
            }
        }
    }
    // --- Group handlers ---
    async handleGroupJoinRequest(client, req) {
        if (!this.group) {
            await sendToClient(client, serialize({ type: 'group.join.rejected', reason: 'No group exists on this server.' }));
            return;
        }
        if (!this.tokenManager.tokensEqual(req.groupSecret, this.group.groupSecret)) {
            this.log(`Group join rejected for ${req.displayName} (${req.deviceId}): invalid secret`);
            await sendToClient(client, serialize({ type: 'group.join.rejected', reason: 'Invalid group secret.' }));
            return;
        }
        let role = 'Member';
        const existing = this.group.members.find(m => m.deviceId === req.deviceId);
        if (existing) {
            existing.displayName = req.displayName;
            existing.os = req.os;
            existing.availableShells = req.availableShells;
        }
        else {
            this.group.members.push({
                deviceId: req.deviceId, displayName: req.displayName, os: req.os,
                role, joinedAt: new Date().toISOString(), availableShells: req.availableShells,
            });
        }
        this.devices.set(req.deviceId, {
            deviceId: req.deviceId, displayName: req.displayName, os: req.os,
            availableShells: req.availableShells, clientId: client.clientId, isLocal: false,
        });
        client.registeredDeviceId = req.deviceId;
        client.memberRole = role;
        // Auto-bind mobile
        const autoBindMobile = !this.group.boundMobileId && this.isLikelyMobileOs(req.os);
        if (autoBindMobile) {
            this.group.boundMobileId = req.deviceId;
            const member = this.group.members.find(m => m.deviceId === req.deviceId);
            if (member)
                member.role = 'Mobile';
            client.memberRole = 'Mobile';
        }
        this.groupStore?.saveGroup(this.group);
        const memberList = this.buildGroupMemberInfoList();
        await sendToClient(client, serialize({
            type: 'group.join.accepted',
            groupId: this.group.groupId, members: memberList,
            serverDeviceId: this.group.serverDeviceId,
            boundMobileId: this.group.boundMobileId,
        }));
        const joinedMember = memberList.find(m => m.deviceId === req.deviceId);
        if (joinedMember) {
            this.broadcastToOthers(client, serialize({ type: 'group.member.joined', member: joinedMember }));
        }
        this.log(`Group member joined: ${req.displayName} (${req.deviceId})`);
        if (autoBindMobile) {
            this.log(`Mobile auto-bound: ${req.displayName} (${req.deviceId})`);
            this.tryDispatchPendingPanelLogins();
        }
    }
    async handleGroupKick(client, kick) {
        if (!this.group)
            return;
        if (client.memberRole !== 'Mobile') {
            await sendToClient(client, serialize({ type: 'error', code: 'permission_denied', message: 'Only the bound mobile can kick members.' }));
            return;
        }
        if (kick.deviceId === this.group.serverDeviceId) {
            await sendToClient(client, serialize({ type: 'error', code: 'cannot_kick_server', message: 'Cannot kick the server device.' }));
            return;
        }
        this.group.members = this.group.members.filter(m => m.deviceId !== kick.deviceId);
        this.groupStore?.saveGroup(this.group);
        const kickedClient = this.findClientByDeviceId(kick.deviceId);
        if (kickedClient) {
            await sendToClient(kickedClient, serialize({ type: 'group.join.rejected', reason: 'You have been removed from the group.' }));
            kickedClient.ws.close();
        }
        this.broadcastToAll(serialize({ type: 'group.member.left', deviceId: kick.deviceId }));
        this.log(`Group member kicked: ${kick.deviceId}`);
    }
    async handleDeviceUnregister(client, unreg) {
        const targetDeviceId = unreg.deviceId?.trim();
        if (!targetDeviceId)
            return;
        if (!this.group) {
            if (client.registeredDeviceId !== targetDeviceId) {
                await sendToClient(client, serialize({ type: 'error', code: 'permission_denied', message: 'Only the device itself can unregister.' }));
                return;
            }
            this.devices.delete(targetDeviceId);
            return;
        }
        const isSelf = client.registeredDeviceId === targetDeviceId;
        const isMobile = client.memberRole === 'Mobile';
        const isServer = client.registeredDeviceId === this.group.serverDeviceId;
        if (!isSelf && !isMobile && !isServer) {
            await sendToClient(client, serialize({ type: 'error', code: 'permission_denied', message: 'Only the bound mobile or server can unbind devices.' }));
            return;
        }
        if (targetDeviceId === this.group.serverDeviceId) {
            if (!isMobile) {
                await sendToClient(client, serialize({ type: 'error', code: 'permission_denied', message: 'Only the bound mobile can unbind the server device.' }));
                return;
            }
            await this.handleMobileUnbind(client, { type: 'mobile.unbind', groupId: this.group.groupId });
            return;
        }
        if (this.group.boundMobileId && targetDeviceId === this.group.boundMobileId) {
            await this.handleMobileUnbind(client, { type: 'mobile.unbind', groupId: this.group.groupId });
            return;
        }
        const removed = this.group.members.some(m => m.deviceId === targetDeviceId);
        this.group.members = this.group.members.filter(m => m.deviceId !== targetDeviceId);
        this.groupStore?.saveGroup(this.group);
        const targetClient = this.findClientByDeviceId(targetDeviceId);
        if (targetClient) {
            await sendToClient(targetClient, serialize({ type: 'device.unregister', deviceId: targetDeviceId }));
            targetClient.ws.close();
        }
        this.devices.delete(targetDeviceId);
        if (removed) {
            this.broadcastToAll(serialize({ type: 'group.member.left', deviceId: targetDeviceId }));
            this.broadcastToAll(serialize({ type: 'group.member.list', members: this.buildGroupMemberInfoList() }));
        }
    }
    async handleMobileBindRequest(client, req) {
        if (!this.group)
            return;
        if (this.group.boundMobileId && this.group.boundMobileId !== req.mobileDeviceId) {
            await sendToClient(client, serialize({ type: 'mobile.bind.rejected', reason: 'Another mobile device is already bound to this group.' }));
            return;
        }
        this.group.boundMobileId = req.mobileDeviceId;
        const mobileMember = this.group.members.find(m => m.deviceId === req.mobileDeviceId);
        if (!mobileMember) {
            this.group.members.push({
                deviceId: req.mobileDeviceId, displayName: req.mobileDisplayName,
                os: 'HarmonyOS', role: 'Mobile',
                joinedAt: new Date().toISOString(), availableShells: [],
            });
        }
        else {
            mobileMember.role = 'Mobile';
            mobileMember.displayName = req.mobileDisplayName;
        }
        client.memberRole = 'Mobile';
        client.registeredDeviceId = req.mobileDeviceId;
        this.groupStore?.saveGroup(this.group);
        await sendToClient(client, serialize({
            type: 'mobile.bind.accepted',
            groupId: this.group.groupId, mobileDeviceId: req.mobileDeviceId,
        }));
        this.broadcastToAll(serialize({ type: 'group.member.list', members: this.buildGroupMemberInfoList() }));
        this.log(`Mobile bound: ${req.mobileDisplayName} (${req.mobileDeviceId})`);
        this.tryDispatchPendingPanelLogins();
    }
    async handleMobileUnbind(client, _unbind) {
        if (!this.group)
            return;
        const boundMobileId = this.group.boundMobileId;
        if (!boundMobileId)
            return;
        if (client.memberRole !== 'Mobile' && client.registeredDeviceId !== this.group.serverDeviceId) {
            await sendToClient(client, serialize({ type: 'error', code: 'permission_denied', message: 'Only the bound mobile or server can unbind.' }));
            return;
        }
        this.group.boundMobileId = null;
        this.group.members = this.group.members.filter(m => m.deviceId !== boundMobileId);
        this.groupStore?.saveGroup(this.group);
        // Explicit unbind: clear both tokens and sessions
        this.tokenManager.clearAll();
        const boundClient = this.findClientByDeviceId(boundMobileId);
        if (boundClient) {
            await sendToClient(boundClient, serialize({ type: 'device.unregister', deviceId: boundMobileId }));
            boundClient.ws.close();
        }
        this.devices.delete(boundMobileId);
        this.broadcastToAll(serialize({ type: 'group.member.left', deviceId: boundMobileId }));
        this.broadcastToAll(serialize({ type: 'group.member.list', members: this.buildGroupMemberInfoList() }));
        this.log(`Mobile unbound from group: ${boundMobileId}`);
    }
    handleAuthResponse(resp) {
        const pending = this.pendingAuths.get(resp.requestId);
        if (!pending)
            return;
        this.pendingAuths.delete(resp.requestId);
        if (resp.approved) {
            this.log(`Auth request ${resp.requestId} approved`);
            pending.onApproved?.();
        }
        else {
            this.log(`Auth request ${resp.requestId} rejected`);
            pending.onRejected?.();
        }
    }
    async handlePanelLoginScan(client, loginScan) {
        if (!this.group) {
            await sendToClient(client, serialize({ type: 'error', code: 'no_group', message: 'No group exists on this server.' }));
            return;
        }
        if (!this.group.boundMobileId || client.registeredDeviceId !== this.group.boundMobileId) {
            await sendToClient(client, serialize({ type: 'error', code: 'not_bound_mobile', message: 'Only the bound mobile can scan login QR codes.' }));
            return;
        }
        const session = this.tokenManager.getLoginSession(loginScan.requestId);
        if (!session) {
            await sendToClient(client, serialize({ type: 'error', code: 'login_session_not_found', message: 'Login session not found or expired.' }));
            return;
        }
        if (session.status === 'approved' || session.status === 'rejected' || session.status === 'expired') {
            await sendToClient(client, serialize({ type: 'error', code: 'login_session_closed', message: 'Login session already resolved.' }));
            return;
        }
        if (new Date() > session.expiresAtUtc) {
            session.status = 'expired';
            session.message = 'Request expired.';
            await sendToClient(client, serialize({ type: 'error', code: 'login_session_expired', message: 'Login session expired.' }));
            return;
        }
        // Bound mobile scanning login QR IS the authorization — auto-approve
        this.tokenManager.approveLogin(session);
        this.log(`Panel login auto-approved by mobile scan (requestId=${loginScan.requestId})`);
    }
    async handleServerChangeRequest(client, req) {
        if (!this.group)
            return;
        if (client.memberRole !== 'Mobile') {
            await sendToClient(client, serialize({ type: 'error', code: 'permission_denied', message: 'Only the bound mobile can initiate server migration.' }));
            return;
        }
        const targetDevice = this.devices.get(req.newServerDeviceId);
        if (!targetDevice || !targetDevice.clientId) {
            await sendToClient(client, serialize({ type: 'error', code: 'device_not_found', message: 'Target device is not online.' }));
            return;
        }
        const targetClient = this.clients.get(targetDevice.clientId);
        if (!targetClient)
            return;
        await sendToClient(targetClient, serialize({
            type: 'group.server.change.prepare',
            groupId: this.group.groupId, groupSecret: this.group.groupSecret, newServerUrl: '',
        }));
        this.log(`Server migration: sent prepare to ${req.newServerDeviceId}`);
    }
    async handleServerChangePrepare(_client, prepare) {
        if (!this.group || !prepare.newServerUrl)
            return;
        this.broadcastToAll(serialize({
            type: 'group.server.change.commit',
            newServerUrl: prepare.newServerUrl, groupId: this.group.groupId, groupSecret: this.group.groupSecret,
        }));
        this.log(`Server migration: commit broadcast, new server=${prepare.newServerUrl}`);
    }
    async handleSecretRotateRequest(client, _req) {
        if (!this.group)
            return;
        if (client.memberRole !== 'Mobile') {
            await sendToClient(client, serialize({ type: 'error', code: 'permission_denied', message: 'Only the bound mobile can rotate the group secret.' }));
            return;
        }
        const newSecret = crypto.randomBytes(32).toString('base64url');
        this.group.groupSecret = newSecret;
        this.authToken = newSecret;
        this.groupStore?.saveGroup(this.group);
        this.broadcastToAll(serialize({ type: 'group.secret.rotate.done', newSecret }));
        this.log(`Group secret rotated to ${newSecret.slice(0, 8)}...`);
    }
    // --- Panel login flow ---
    startPanelLogin(requesterAddress, serverUrl) {
        const hasBoundMobile = !!this.group?.boundMobileId;
        const session = this.tokenManager.createLoginSession(requesterAddress, serverUrl);
        if (hasBoundMobile) {
            session.status = 'awaiting_scan';
            session.message = 'Waiting for mobile scan.';
        }
        else {
            session.status = 'awaiting_mobile';
            session.message = 'Waiting for mobile binding.';
        }
        let loginQrPayload = '';
        if (hasBoundMobile && this.group && serverUrl) {
            loginQrPayload = buildPanelLoginPayload(serverUrl, this.group.groupId, session.requestId);
            session.loginQrPayload = loginQrPayload;
        }
        return {
            requestId: session.requestId,
            status: session.status,
            message: session.message || '',
            expiresAtUtc: session.expiresAtUtc.toISOString(),
            loginQrPayload,
        };
    }
    getPanelLoginStatus(requestId) {
        const session = this.tokenManager.getLoginSession(requestId);
        if (!session)
            return null;
        let loginQrPayload = '';
        if (session.status === 'awaiting_scan' && this.group) {
            if (!session.loginQrPayload) {
                const serverUrl = session.serverUrl;
                if (serverUrl) {
                    session.loginQrPayload = buildPanelLoginPayload(serverUrl, this.group.groupId, session.requestId);
                }
            }
            loginQrPayload = session.loginQrPayload || '';
        }
        return {
            requestId: session.requestId,
            status: session.status,
            message: session.message || '',
            token: session.status === 'approved' ? session.token : undefined,
            expiresAtUtc: session.expiresAtUtc.toISOString(),
            loginQrPayload,
        };
    }
    getPanelPairingPayload(serverUrl) {
        const hasBoundMobile = !!this.group?.boundMobileId;
        let boundMobileOnline = false;
        if (hasBoundMobile && this.group?.boundMobileId) {
            boundMobileOnline = !!this.findClientByDeviceId(this.group.boundMobileId);
        }
        const qrPayload = hasBoundMobile ? '' : this.getBindQrPayload(serverUrl);
        return {
            requiresAuth: !!this.authToken,
            hasGroup: !!this.group,
            groupId: this.group?.groupId || '',
            serverUrl,
            qrPayload,
            hasBoundMobile,
            boundMobileOnline,
        };
    }
    getBindQrPayload(serverUrl) {
        if (!this.group || !serverUrl)
            return '';
        return buildGroupBindPayload(serverUrl, this.group.groupId, this.group.groupSecret, this.group.serverDeviceId);
    }
    buildStatusPayload() {
        return {
            status: 'ok',
            startedAtUtc: this.startedAtUtc.toISOString(),
            uptimeSeconds: Math.max(0, Math.floor((Date.now() - this.startedAtUtc.getTime()) / 1000)),
            connectedClientCount: this.clients.size,
            registeredDeviceCount: this.devices.size,
            devices: Array.from(this.devices.values()).map(d => ({
                deviceId: d.deviceId, displayName: d.displayName, os: d.os,
                isLocal: d.isLocal, availableShells: d.availableShells,
            })),
        };
    }
    // --- Output ordering ---
    enqueueOutput(sessionId, sendFn) {
        const chain = (this.outputChains.get(sessionId) || Promise.resolve())
            .then(() => sendFn().catch(() => { }));
        this.outputChains.set(sessionId, chain);
    }
    removeOutputChain(sessionId) {
        this.outputChains.delete(sessionId);
    }
    // --- Helpers ---
    isLikelyMobileOs(os) {
        if (!os)
            return false;
        const v = os.toLowerCase();
        return v.includes('android') || v.includes('ios') || v.includes('iphone') ||
            v.includes('ipad') || v.includes('harmony');
    }
    findClientByDeviceId(deviceId) {
        for (const c of this.clients.values()) {
            if (c.registeredDeviceId === deviceId)
                return c;
        }
        return undefined;
    }
    broadcastToOthers(sender, message) {
        for (const c of this.clients.values()) {
            if (c.clientId !== sender.clientId)
                sendToClient(c, message);
        }
    }
    broadcastToAll(message) {
        for (const c of this.clients.values()) {
            sendToClient(c, message);
        }
    }
    tryDispatchPendingPanelLogins() {
        for (const session of this.tokenManager.getAllLoginSessions()) {
            if (session.status === 'awaiting_mobile') {
                this.log(`Panel login auto-approved (first bind completed, requestId=${session.requestId})`);
                this.tokenManager.approveLogin(session);
            }
        }
    }
    dispatchAuthForSession(mobileClient, session) {
        this.pendingAuths.set(session.requestId, {
            requestId: session.requestId,
            onApproved: () => this.tokenManager.approveLogin(session),
            onRejected: () => this.tokenManager.rejectLogin(session, 'Rejected by mobile.'),
        });
        const description = session.requesterAddress
            ? `Web panel login request from ${session.requesterAddress}.`
            : 'Web panel login request.';
        sendToClient(mobileClient, serialize({
            type: 'auth.request',
            requestId: session.requestId,
            action: 'panel.login',
            requesterId: 'web-panel',
            requesterName: 'Web Panel',
            targetDeviceId: null,
            description,
        }));
        // Timeout
        setTimeout(() => {
            const pending = this.pendingAuths.get(session.requestId);
            if (pending) {
                this.pendingAuths.delete(session.requestId);
                this.log(`Panel login request ${session.requestId} timed out`);
                pending.onRejected?.();
            }
        }, 60000);
    }
    stop() {
        for (const client of this.clients.values()) {
            try {
                client.ws.close();
            }
            catch { }
        }
        this.clients.clear();
        this.devices.clear();
        this.outputChains.clear();
    }
}
//# sourceMappingURL=relay-server.js.map