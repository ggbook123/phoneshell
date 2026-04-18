import http from 'node:http';
import https from 'node:https';
import fs from 'node:fs';
import path from 'node:path';
import net from 'node:net';
import os from 'node:os';
import { URL, fileURLToPath } from 'node:url';
import WebSocket, { WebSocketServer } from 'ws';
import { RelayServer } from '../relay/relay-server.js';
import { RelayClient } from '../relay/relay-client.js';
import { ModeManager } from '../relay/mode-manager.js';
import { TerminalManager } from '../terminal/terminal-manager.js';
import { DeviceStore } from '../store/device-store.js';
import { GroupStore } from '../store/group-store.js';
import { GroupMembershipStore } from '../store/group-membership-store.js';
import { TerminalHistoryStore } from '../store/terminal-history-store.js';
import { generateQrPng, buildStandalonePayload } from '../auth/qr-service.js';
import { serialize } from '../protocol/serializer.js';
function log(msg) {
    const ts = new Date().toLocaleTimeString('en-US', { hour12: false });
    console.log(`[${ts}] ${msg}`);
}
function resolveTlsRuntime(config) {
    const tls = config.tls;
    if (!tls)
        return { enabled: false, port: 0 };
    const certPath = tls.certPath?.trim() || '';
    const keyPath = tls.keyPath?.trim() || '';
    const caPath = tls.caPath?.trim() || '';
    const passphrase = tls.passphrase?.trim() || '';
    const tlsConfigured = certPath.length > 0 && keyPath.length > 0;
    const tlsAllowed = tls.enabled !== false;
    if (!tlsConfigured || !tlsAllowed)
        return { enabled: false, port: 0 };
    const port = tls.port && tls.port >= 1 && tls.port <= 65535 ? tls.port : config.port;
    try {
        const options = {
            key: fs.readFileSync(keyPath),
            cert: fs.readFileSync(certPath),
            minVersion: 'TLSv1.2',
        };
        if (caPath)
            options.ca = fs.readFileSync(caPath);
        if (passphrase)
            options.passphrase = passphrase;
        return { enabled: true, port, options };
    }
    catch (err) {
        throw new Error(`[tls] failed to load cert/key: ${err.message}`);
    }
}
const publicIpProviders = [
    {
        name: 'ipify',
        url: 'https://api.ipify.org?format=json',
        parse: (body) => {
            try {
                const json = JSON.parse(body);
                const ip = typeof json?.ip === 'string' ? json.ip.trim() : '';
                return ip || null;
            }
            catch {
                return null;
            }
        },
    },
    {
        name: 'ifconfig',
        url: 'https://ifconfig.me/ip',
        parse: (body) => {
            const ip = body.trim();
            return ip || null;
        },
    },
    {
        name: 'aws-checkip',
        url: 'https://checkip.amazonaws.com',
        parse: (body) => {
            const ip = body.trim();
            return ip || null;
        },
    },
];
function isLocalHost(host) {
    const h = host.trim().toLowerCase();
    return h === 'localhost' || h === '127.0.0.1' || h === '::1';
}
function splitHostPort(host) {
    const trimmed = host.trim();
    if (!trimmed)
        return { host: '' };
    if (trimmed.startsWith('[')) {
        const end = trimmed.indexOf(']');
        if (end === -1)
            return { host: trimmed };
        const hostOnly = trimmed.slice(1, end);
        const rest = trimmed.slice(end + 1);
        if (rest.startsWith(':'))
            return { host: hostOnly, port: rest.slice(1) };
        return { host: hostOnly };
    }
    const firstColon = trimmed.indexOf(':');
    const lastColon = trimmed.lastIndexOf(':');
    if (firstColon !== -1 && firstColon === lastColon) {
        return { host: trimmed.slice(0, lastColon), port: trimmed.slice(lastColon + 1) };
    }
    return { host: trimmed };
}
function normalizeHostWithPort(host, port) {
    const trimmed = host.trim();
    if (!trimmed)
        return '';
    if (trimmed.startsWith('[')) {
        if (trimmed.includes(']:'))
            return trimmed;
        return `${trimmed}:${port}`;
    }
    const ipVersion = net.isIP(trimmed);
    if (ipVersion === 6)
        return `[${trimmed}]:${port}`;
    if (trimmed.includes(':'))
        return trimmed;
    return `${trimmed}:${port}`;
}
function isPrivateIpv4(address) {
    const parts = address.split('.').map((part) => parseInt(part, 10));
    if (parts.length !== 4 || parts.some((part) => Number.isNaN(part) || part < 0 || part > 255)) {
        return false;
    }
    if (parts[0] === 10)
        return true;
    if (parts[0] === 192 && parts[1] === 168)
        return true;
    if (parts[0] === 172 && parts[1] >= 16 && parts[1] <= 31)
        return true;
    return false;
}
function isLinkLocalIpv4(address) {
    return address.startsWith('169.254.');
}
function isBenchmarkIpv4(address) {
    const parts = address.split('.').map((part) => parseInt(part, 10));
    if (parts.length !== 4 || parts.some((part) => Number.isNaN(part)))
        return false;
    return parts[0] === 198 && (parts[1] === 18 || parts[1] === 19);
}
function scoreLanCandidate(interfaceName, address) {
    const name = interfaceName.trim().toLowerCase();
    let score = 0;
    if (isPrivateIpv4(address))
        score += 100;
    if (!isLinkLocalIpv4(address) && !isBenchmarkIpv4(address))
        score += 25;
    if (name.startsWith('eth') || name.startsWith('en') || name.startsWith('wl') || name.startsWith('wlan')) {
        score += 35;
    }
    if (/(docker|podman|cni|flannel|veth|virbr|br-|lxcbr|vmnet|vbox|tailscale|utun|tun|tap|wg|zerotier|vethernet)/i.test(name)) {
        score -= 120;
    }
    if (isLinkLocalIpv4(address))
        score -= 120;
    if (isBenchmarkIpv4(address))
        score -= 160;
    return score;
}
function detectLanHost() {
    const interfaces = os.networkInterfaces();
    const candidates = [];
    for (const name in interfaces) {
        const entries = interfaces[name];
        if (!entries)
            continue;
        for (const entry of entries) {
            const family = typeof entry.family === 'string' ? entry.family : String(entry.family);
            if (entry.internal || family !== 'IPv4')
                continue;
            const address = (entry.address || '').trim();
            if (address && net.isIP(address) === 4 && !isLocalHost(address)) {
                const score = scoreLanCandidate(name, address);
                candidates.push({ name, address, score });
            }
        }
    }
    candidates.sort((a, b) => b.score - a.score);
    const best = candidates[0];
    if (!best || best.score < 0)
        return '';
    return best.address;
}
function fetchText(url, timeoutMs) {
    return new Promise((resolve, reject) => {
        const req = https.get(url, { headers: { 'User-Agent': 'phoneshell' } }, (res) => {
            if (res.statusCode && res.statusCode >= 400) {
                res.resume();
                reject(new Error(`HTTP ${res.statusCode}`));
                return;
            }
            let data = '';
            res.setEncoding('utf8');
            res.on('data', (chunk) => { data += chunk; });
            res.on('end', () => resolve(data));
        });
        req.on('error', reject);
        req.setTimeout(timeoutMs, () => {
            req.destroy(new Error('timeout'));
        });
    });
}
async function detectPublicHost(logger) {
    for (const provider of publicIpProviders) {
        try {
            const body = await fetchText(provider.url, 4000);
            const ip = provider.parse(body);
            if (ip && net.isIP(ip) !== 0) {
                return { host: ip, source: provider.name };
            }
            logger(`[public] ${provider.name} returned invalid IP`);
        }
        catch (err) {
            logger(`[public] ${provider.name} failed: ${err.message}`);
        }
    }
    return null;
}
export function createApp(config) {
    const webPanelEnabled = true;
    const panelAccessDefault = config.modules.webPanel ? 'public' : 'private';
    const configPath = config.configPath || '/etc/phoneshell/config.json';
    const tlsRuntime = resolveTlsRuntime(config);
    const primaryPort = config.port;
    const tlsPort = tlsRuntime.enabled ? tlsRuntime.port : 0;
    const primaryUsesTls = tlsRuntime.enabled && tlsPort === primaryPort;
    const panelPortCandidate = config.panelPort && config.panelPort !== primaryPort ? config.panelPort : 0;
    const panelPort = panelPortCandidate && panelPortCandidate !== tlsPort ? panelPortCandidate : 0;
    const deviceStore = new DeviceStore(config.baseDirectory);
    const groupStore = new GroupStore(config.baseDirectory);
    const membershipStore = new GroupMembershipStore(config.baseDirectory);
    const identity = deviceStore.loadOrCreate();
    const deviceId = identity.deviceId;
    const displayName = config.displayName || identity.displayName;
    const os = `Linux ${process.arch}`;
    const terminalManager = new TerminalManager(config.defaultCols, config.defaultRows);
    const availableShells = config.modules.terminal
        ? terminalManager.getAvailableShells().map(s => s.displayName)
        : [];
    const relay = new RelayServer();
    const historyStore = new TerminalHistoryStore(config.baseDirectory);
    relay.setLogger((msg) => log(`[relay] ${msg}`));
    relay.setHistoryStore(historyStore);
    relay.setPreserveTerminalHistoryOnClose(true);
    // Output ordering: per-session promise chain
    const outputChains = new Map();
    function enqueueOutput(sessionId, fn) {
        const chain = (outputChains.get(sessionId) || Promise.resolve())
            .then(() => fn().catch((err) => {
            log(`[output-chain] Error sending output for session ${sessionId}: ${err.message}`);
        }));
        outputChains.set(sessionId, chain);
    }
    function cleanupOutputChain(sessionId) {
        outputChains.delete(sessionId);
    }
    // Wire terminal callbacks
    relay.setCallbacks({
        onLocalTerminalInput: (sessionId, data) => terminalManager.writeInput(sessionId, data),
        onLocalTerminalResize: (sessionId, cols, rows) => terminalManager.resize(sessionId, cols, rows),
        onLocalTerminalSessionEnded: (sessionId) => {
            terminalManager.closeSession(sessionId);
            cleanupOutputChain(sessionId);
            log(`Terminal session ended: ${sessionId}`);
        },
        onLocalTerminalOpen: async (_devId, shellId) => {
            const result = terminalManager.createSession(shellId);
            log(`Terminal session created: ${result.sessionId}`);
            return result;
        },
        getLocalSessionList: () => terminalManager.getSessionList(),
        getLocalTerminalSnapshot: (sessionId) => terminalManager.getSnapshot(sessionId),
        onServerMigrationCommitted: (newServerUrl, groupId, groupSecret) => {
            handleServerChangeCommit(newServerUrl, groupSecret, groupId);
        },
    });
    // Wire terminal output: relay (standalone) vs client mode
    function wireTerminalOutputToRelay() {
        terminalManager.onOutput = (sessionId, data) => {
            enqueueOutput(sessionId, () => relay.broadcastLocalTerminalOutput(deviceId, sessionId, data));
        };
        terminalManager.onExit = (sessionId) => {
            terminalManager.closeSession(sessionId);
            relay.broadcastLocalTerminalClosed(deviceId, sessionId);
            relay.broadcastLocalSessionListChanged(deviceId);
            cleanupOutputChain(sessionId);
        };
    }
    function wireTerminalOutputToClient() {
        terminalManager.onOutput = (sessionId, data) => {
            enqueueOutput(sessionId, async () => {
                relayClient?.sendTerminalOutput(deviceId, sessionId, data);
            });
        };
        terminalManager.onExit = (sessionId) => {
            terminalManager.closeSession(sessionId);
            relayClient?.sendTerminalClosed(deviceId, sessionId);
            relayClient?.sendSessionList(deviceId, terminalManager.getSessionList());
            cleanupOutputChain(sessionId);
        };
    }
    const defaultAuthToken = (config.groupSecret || config.relayAuthToken || '').trim();
    let effectiveToken = defaultAuthToken;
    // Mode manager for standalone ↔ client transitions
    const modeManager = new ModeManager();
    modeManager.setLogger((msg) => log(`[mode] ${msg}`));
    let relayClient = null;
    let pendingServerMigration = null;
    let pendingInviteJoin = null;
    let resolvedPublicHost = '';
    let panelAccessCache = panelAccessDefault;
    let panelAccessCacheAt = 0;
    let clientDevicesCache = [];
    let clientGroupCache = null;
    const clientPanelSockets = new Set();
    const wsScheme = (useTls) => (useTls ? 'wss' : 'ws');
    const httpScheme = (useTls) => (useTls ? 'https' : 'http');
    const buildWsUrl = (host, port, useTls) => `${wsScheme(useTls)}://${normalizeHostWithPort(host, port)}/ws/`;
    function cloneDeviceInfo(device) {
        return {
            deviceId: device.deviceId,
            displayName: device.displayName,
            os: device.os,
            isOnline: !!device.isOnline,
            availableShells: Array.isArray(device.availableShells) ? [...device.availableShells] : [],
        };
    }
    function cloneGroupMemberInfo(member) {
        return {
            deviceId: member.deviceId,
            displayName: member.displayName,
            os: member.os,
            role: member.role,
            isOnline: !!member.isOnline,
            availableShells: Array.isArray(member.availableShells) ? [...member.availableShells] : [],
        };
    }
    function syncLocalAuthToken(token) {
        effectiveToken = token.trim();
        relay.setAuthToken(effectiveToken);
    }
    function resetLocalAuthToken() {
        syncLocalAuthToken(defaultAuthToken);
    }
    function resolvePendingInviteJoin(result) {
        if (!pendingInviteJoin)
            return;
        const pending = pendingInviteJoin;
        pendingInviteJoin = null;
        clearTimeout(pending.timeout);
        pending.resolve(result);
    }
    function beginPendingInviteJoin(timeoutMs = 10000) {
        if (pendingInviteJoin) {
            return Promise.resolve({
                ok: false,
                reason: 'Another invite transition is already in progress.',
            });
        }
        return new Promise((resolve) => {
            const timeout = setTimeout(() => {
                if (!pendingInviteJoin)
                    return;
                pendingInviteJoin = null;
                resolve({
                    ok: false,
                    reason: 'Timed out waiting for invite join confirmation.',
                });
            }, timeoutMs);
            pendingInviteJoin = { timeout, resolve };
        });
    }
    function clearClientCaches() {
        clientDevicesCache = [];
        clientGroupCache = null;
    }
    function closeClientPanelSockets() {
        for (const ws of clientPanelSockets) {
            try {
                ws.close();
            }
            catch { }
        }
        clientPanelSockets.clear();
    }
    function getSavedMembership() {
        return membershipStore.load();
    }
    function getClientMembership() {
        if (!modeManager.isClient())
            return null;
        const membership = getSavedMembership();
        if (!membership)
            return null;
        const groupSecret = membership.groupSecret.trim();
        if (!groupSecret)
            return null;
        return membership;
    }
    function ensureClientGroupCache(groupId = '') {
        const membership = getSavedMembership();
        const effectiveGroupId = groupId.trim() || clientGroupCache?.groupId || membership?.groupId || '';
        if (!clientGroupCache) {
            clientGroupCache = {
                groupId: effectiveGroupId,
                serverDeviceId: '',
                boundMobileId: null,
                createdAt: membership?.updatedAtUtc || new Date().toISOString(),
                members: [],
            };
            return clientGroupCache;
        }
        if (effectiveGroupId && !clientGroupCache.groupId) {
            clientGroupCache.groupId = effectiveGroupId;
        }
        return clientGroupCache;
    }
    function mergeClientMemberWithDevice(member) {
        const device = clientDevicesCache.find((item) => item.deviceId === member.deviceId);
        return {
            deviceId: member.deviceId,
            displayName: device?.displayName || member.displayName,
            os: device?.os || member.os,
            role: member.role,
            isOnline: device?.isOnline ?? member.isOnline,
            availableShells: device?.availableShells ? [...device.availableShells] : [...(member.availableShells || [])],
        };
    }
    function syncClientGroupMembersWithDevices() {
        if (!clientGroupCache || clientDevicesCache.length === 0)
            return;
        const deviceMap = new Map(clientDevicesCache.map((device) => [device.deviceId, device]));
        clientGroupCache.members = clientGroupCache.members
            .filter((member) => deviceMap.has(member.deviceId))
            .map((member) => mergeClientMemberWithDevice(member));
        if (!clientGroupCache.boundMobileId) {
            clientGroupCache.boundMobileId = clientGroupCache.members.find((member) => member.role === 'Mobile')?.deviceId || null;
        }
        if (!clientGroupCache.serverDeviceId) {
            clientGroupCache.serverDeviceId = clientGroupCache.members.find((member) => member.role === 'Server')?.deviceId || '';
        }
    }
    function updateClientDevicesCache(devices) {
        clientDevicesCache = devices.map((device) => cloneDeviceInfo(device));
        syncClientGroupMembersWithDevices();
    }
    function setClientGroupMembers(members, groupId = '') {
        const cache = ensureClientGroupCache(groupId);
        cache.members = members.map((member) => mergeClientMemberWithDevice(cloneGroupMemberInfo(member)));
        cache.boundMobileId = cache.members.find((member) => member.role === 'Mobile')?.deviceId || cache.boundMobileId || null;
        cache.serverDeviceId = cache.members.find((member) => member.role === 'Server')?.deviceId || cache.serverDeviceId;
        syncClientGroupMembersWithDevices();
    }
    function upsertClientGroupMember(member) {
        const cache = ensureClientGroupCache();
        const nextMember = mergeClientMemberWithDevice(cloneGroupMemberInfo(member));
        const index = cache.members.findIndex((item) => item.deviceId === nextMember.deviceId);
        if (index >= 0) {
            cache.members[index] = nextMember;
        }
        else {
            cache.members.push(nextMember);
        }
        if (nextMember.role === 'Mobile') {
            cache.boundMobileId = nextMember.deviceId;
        }
        if (nextMember.role === 'Server') {
            cache.serverDeviceId = nextMember.deviceId;
        }
    }
    function markClientGroupMemberOffline(deviceIdToUpdate) {
        if (!clientGroupCache)
            return;
        const member = clientGroupCache.members.find((item) => item.deviceId === deviceIdToUpdate);
        if (!member)
            return;
        member.isOnline = false;
    }
    function updateClientGroupFromJoinAccepted(message) {
        const cache = ensureClientGroupCache((message.groupId || '').trim());
        if (message.serverDeviceId) {
            cache.serverDeviceId = message.serverDeviceId;
        }
        if (message.boundMobileId !== undefined) {
            cache.boundMobileId = message.boundMobileId || null;
        }
        setClientGroupMembers(Array.isArray(message.members) ? message.members : [], cache.groupId);
    }
    function buildClientDeviceList() {
        if (clientDevicesCache.length > 0) {
            return clientDevicesCache.map((device) => cloneDeviceInfo(device));
        }
        if (clientGroupCache?.members.length) {
            return clientGroupCache.members.map((member) => ({
                deviceId: member.deviceId,
                displayName: member.displayName,
                os: member.os,
                isOnline: member.isOnline,
                availableShells: [...member.availableShells],
            }));
        }
        return [];
    }
    function buildClientGroupPayload() {
        const membership = getSavedMembership();
        const cache = clientGroupCache || (membership
            ? {
                groupId: membership.groupId,
                serverDeviceId: '',
                boundMobileId: null,
                createdAt: membership.updatedAtUtc,
                members: [],
            }
            : null);
        if (!cache)
            return null;
        return {
            groupId: cache.groupId,
            serverDeviceId: cache.serverDeviceId,
            boundMobileId: cache.boundMobileId,
            createdAt: cache.createdAt,
            members: cache.members.map((member) => cloneGroupMemberInfo(member)),
        };
    }
    function broadcastClientPanelMessage(message) {
        if (clientPanelSockets.size === 0)
            return;
        const json = serialize(message);
        for (const ws of clientPanelSockets) {
            if (ws.readyState !== WebSocket.OPEN)
                continue;
            try {
                ws.send(json);
            }
            catch { }
        }
    }
    function sendInitialClientPanelSnapshot(ws) {
        try {
            const devices = buildClientDeviceList();
            if (devices.length > 0) {
                ws.send(serialize({ type: 'device.list', devices }));
            }
            const group = buildClientGroupPayload();
            if (group?.members && group.members.length > 0) {
                ws.send(serialize({ type: 'group.member.list', members: group.members }));
            }
        }
        catch { }
    }
    function requestClientDeviceList() {
        relayClient?.send(serialize({ type: 'device.list.request' }));
    }
    function handleRelayClientMessage(message) {
        switch (message.type) {
            case 'group.join.accepted': {
                const accepted = message;
                updateClientGroupFromJoinAccepted(accepted);
                break;
            }
            case 'device.list': {
                const list = message;
                updateClientDevicesCache(Array.isArray(list.devices) ? list.devices : []);
                break;
            }
            case 'group.member.list': {
                const list = message;
                setClientGroupMembers(Array.isArray(list.members) ? list.members : []);
                break;
            }
            case 'group.member.joined': {
                const joined = message;
                if (joined.member) {
                    upsertClientGroupMember(joined.member);
                }
                break;
            }
            case 'group.member.left': {
                const left = message;
                if (left.deviceId) {
                    markClientGroupMemberOffline(left.deviceId);
                }
                break;
            }
            case 'group.dissolved':
                clearClientCaches();
                break;
        }
        broadcastClientPanelMessage(message);
    }
    function saveClientMembership(groupId, groupSecret, relayUrl) {
        const normalizedGroupId = groupId.trim();
        const normalizedGroupSecret = groupSecret.trim();
        const normalizedRelayUrl = sanitizeRelayUrl(relayUrl);
        if (!normalizedGroupId || !normalizedGroupSecret || !normalizedRelayUrl)
            return;
        syncLocalAuthToken(normalizedGroupSecret);
        ensureClientGroupCache(normalizedGroupId);
        membershipStore.save({
            groupId: normalizedGroupId,
            groupSecret: normalizedGroupSecret,
            relayUrl: normalizedRelayUrl,
            updatedAtUtc: new Date().toISOString(),
        });
    }
    function startRelayServer(tokenOverride) {
        if (tokenOverride)
            effectiveToken = tokenOverride;
        relay.setAuthToken(effectiveToken);
        relay.initGroup(groupStore, deviceId, displayName, os, availableShells);
        const group = relay.getGroup();
        if (group?.groupSecret) {
            effectiveToken = group.groupSecret;
            relay.setAuthToken(group.groupSecret);
        }
        relay.registerLocalDevice(deviceId, displayName, os, availableShells);
        relay.start();
        wireTerminalOutputToRelay();
    }
    function sanitizeRelayUrl(relayUrl) {
        const trimmed = relayUrl.trim();
        if (!trimmed)
            return '';
        try {
            const parsed = new URL(trimmed);
            if (parsed.protocol !== 'ws:' && parsed.protocol !== 'wss:')
                return '';
            parsed.searchParams.delete('token');
            parsed.searchParams.delete('invite');
            if (!parsed.pathname || parsed.pathname === '/') {
                parsed.pathname = '/ws/';
            }
            else if (parsed.pathname === '/ws') {
                parsed.pathname = '/ws/';
            }
            return parsed.toString();
        }
        catch {
            let sanitized = trimmed.replace(/([?&])(token|invite)=[^&]*/gi, (_full, sep) => (sep === '?' ? '?' : ''));
            sanitized = sanitized.replaceAll('?&', '?').replaceAll('&&', '&');
            while (sanitized.endsWith('?') || sanitized.endsWith('&')) {
                sanitized = sanitized.slice(0, -1);
            }
            if (sanitized.endsWith('/ws')) {
                sanitized += '/';
            }
            return sanitized;
        }
    }
    function startRelayClient(relayUrl, inviteCode, groupSecret, options) {
        const normalizedRelayUrl = sanitizeRelayUrl(relayUrl);
        const normalizedInviteCode = inviteCode.trim();
        const normalizedGroupSecret = groupSecret.trim();
        if (!normalizedRelayUrl || (!normalizedInviteCode && !normalizedGroupSecret)) {
            log(`[invite] Invalid relay join parameters: relay=${relayUrl}`);
            resolvePendingInviteJoin({ ok: false, reason: 'Invalid relay URL or missing invite credentials.' });
            return;
        }
        const preserveServer = options?.preserveServer ?? false;
        if (!preserveServer) {
            relay.stop();
        }
        if (relayClient) {
            relayClient.disconnect();
            relayClient = null;
        }
        relayClient = new RelayClient();
        relayClient.setLogger((msg) => log(`[relay-client] ${msg}`));
        relayClient.setCallbacks({
            onLocalTerminalInput: (sessionId, data) => terminalManager.writeInput(sessionId, data),
            onLocalTerminalResize: (sessionId, cols, rows) => terminalManager.resize(sessionId, cols, rows),
            onLocalTerminalSessionEnded: (sessionId) => {
                terminalManager.closeSession(sessionId);
                cleanupOutputChain(sessionId);
            },
            onLocalTerminalOpen: async (_devId, shellId) => terminalManager.createSession(shellId),
            getLocalSessionList: () => terminalManager.getSessionList(),
            getLocalTerminalSnapshot: (sessionId) => terminalManager.getSnapshot(sessionId),
            onMessage: (message) => {
                handleRelayClientMessage(message);
            },
            onGroupJoined: (groupId, effectiveSecret) => {
                saveClientMembership(groupId, effectiveSecret, normalizedRelayUrl);
                resolvePendingInviteJoin({
                    ok: true,
                    groupId,
                    groupSecret: effectiveSecret,
                });
                requestClientDeviceList();
            },
            onKicked: (reason) => {
                log(`Kicked from group: ${reason}`);
                resolvePendingInviteJoin({ ok: false, reason: reason || 'Kicked while joining group.' });
                membershipStore.clear();
                transitionBackToStandalone();
            },
            onGroupDissolved: (reason) => {
                log(`Group dissolved: ${reason}`);
                resolvePendingInviteJoin({ ok: false, reason: reason || 'Group dissolved while joining.' });
                membershipStore.clear();
                transitionBackToStandalone();
            },
            onServerChangeRequested: (groupId, groupSecret) => {
                const newServerUrl = startRelayServerForMigration(groupId, groupSecret);
                if (relayClient && newServerUrl) {
                    const msg = serialize({
                        type: 'group.server.change.prepare',
                        newServerUrl,
                        groupId,
                        groupSecret,
                    });
                    relayClient.send(msg);
                    log(`Server migration: prepare sent (${newServerUrl})`);
                }
            },
            onServerChanged: (newUrl, newSecret, newGroupId) => {
                handleServerChangeCommit(newUrl, newSecret, newGroupId);
            },
        });
        if (!preserveServer) {
            wireTerminalOutputToClient();
        }
        if (normalizedGroupSecret) {
            syncLocalAuthToken(normalizedGroupSecret);
        }
        relayClient.connect(normalizedRelayUrl, deviceId, displayName, os, availableShells, normalizedInviteCode, normalizedGroupSecret);
    }
    function getRuntimePublicHost() {
        return (config.publicHost || '').trim();
    }
    function buildRelayUrlFromConfig() {
        const publicHost = getRuntimePublicHost();
        if (publicHost) {
            const port = tlsRuntime.enabled ? tlsPort : primaryPort;
            return buildWsUrl(publicHost, port, tlsRuntime.enabled);
        }
        const lanHost = detectLanHost();
        if (lanHost) {
            const port = tlsRuntime.enabled ? tlsPort : primaryPort;
            return buildWsUrl(lanHost, port, tlsRuntime.enabled);
        }
        const port = tlsRuntime.enabled ? tlsPort : primaryPort;
        return buildWsUrl('localhost', port, tlsRuntime.enabled);
    }
    function buildHttpUrlFromWebSocketUrl(wsUrl) {
        const withoutQuery = wsUrl.split('?', 1)[0] || wsUrl;
        return withoutQuery.replace(/^ws/, 'http').replace(/\/ws\/?$/, '');
    }
    function appendOrReplaceTokenQuery(wsUrl, token) {
        const trimmedUrl = wsUrl.trim();
        const trimmedToken = token.trim();
        if (!trimmedUrl || !trimmedToken)
            return trimmedUrl;
        const encodedToken = encodeURIComponent(trimmedToken);
        if (/token=/i.test(trimmedUrl)) {
            return trimmedUrl.replace(/token=[^&]*/i, `token=${encodedToken}`);
        }
        return trimmedUrl + (trimmedUrl.includes('?') ? '&' : '?') + `token=${encodedToken}`;
    }
    function isLocalRelayUrl(url) {
        try {
            const parsed = new URL(url);
            const hostOnly = splitHostPort(parsed.host).host;
            return !hostOnly || isLocalHost(hostOnly);
        }
        catch {
            return true;
        }
    }
    function buildRelayUrlForMigration() {
        if (lastResolvedServerUrl && !isLocalRelayUrl(lastResolvedServerUrl)) {
            return lastResolvedServerUrl;
        }
        const publicHost = getRuntimePublicHost();
        if (publicHost) {
            const port = tlsRuntime.enabled ? tlsPort : primaryPort;
            return buildWsUrl(publicHost, port, tlsRuntime.enabled);
        }
        const lanHost = detectLanHost();
        if (lanHost) {
            const port = tlsRuntime.enabled ? tlsPort : primaryPort;
            return buildWsUrl(lanHost, port, tlsRuntime.enabled);
        }
        return null;
    }
    function applyPublicHostToRelay(host) {
        const port = tlsRuntime.enabled ? tlsPort : primaryPort;
        const relayUrl = buildWsUrl(host, port, tlsRuntime.enabled);
        relay.setRelayUrl(relayUrl);
        lastResolvedServerUrl = relayUrl;
    }
    function readPanelAccessFromConfig() {
        if (!configPath)
            return panelAccessCache;
        try {
            if (!fs.existsSync(configPath))
                return panelAccessCache;
            const json = JSON.parse(fs.readFileSync(configPath, 'utf-8'));
            if (typeof json.panelAccess === 'string') {
                const v = json.panelAccess.trim().toLowerCase();
                if (v === 'private' || v === 'internal' || v === 'closed')
                    return 'private';
                if (v === 'public' || v === 'open')
                    return 'public';
            }
            if (json.modules?.webPanel === false)
                return 'private';
            if (json.modules?.webPanel === true)
                return 'public';
        }
        catch { }
        return panelAccessCache;
    }
    function getPanelAccessMode() {
        const now = Date.now();
        if (now - panelAccessCacheAt < 1500)
            return panelAccessCache;
        panelAccessCache = readPanelAccessFromConfig();
        panelAccessCacheAt = now;
        return panelAccessCache;
    }
    function isPanelAuthorized(req) {
        const token = extractToken(req);
        return relay.isAuthorized(token);
    }
    function buildPanelCookie(token, req) {
        const proto = req.headers['x-forwarded-proto']?.split(',')[0]?.trim().toLowerCase();
        const secure = proto === 'https' || proto === 'wss' || req.socket?.encrypted;
        const parts = [
            `ps_token=${encodeURIComponent(token)}`,
            'Path=/',
            'SameSite=Lax',
            'HttpOnly',
        ];
        if (secure)
            parts.push('Secure');
        return parts.join('; ');
    }
    function startRelayServerForMigration(groupId, groupSecret) {
        const newServerUrl = buildRelayUrlForMigration();
        if (!newServerUrl) {
            log('[mode] Server migration aborted: no reachable non-localhost relay URL available. Configure publicHost or ensure a LAN address is available.');
            return null;
        }
        pendingServerMigration = { groupId, groupSecret, newServerUrl };
        groupStore.saveGroup({
            groupId,
            groupSecret,
            serverDeviceId: deviceId,
            createdAt: new Date().toISOString(),
            members: [],
        });
        membershipStore.clear();
        if (modeManager.isClient()) {
            if (!modeManager.transitionToRelayFromClient()) {
                log('[mode] Server migration: failed to switch to relay mode (already relay?)');
            }
            closeClientPanelSockets();
            clearClientCaches();
        }
        else if (modeManager.isStandalone()) {
            modeManager.transitionToRelay();
        }
        relay.stop();
        startRelayServer(groupSecret);
        relay.setRelayUrl(newServerUrl);
        lastResolvedServerUrl = newServerUrl;
        return newServerUrl;
    }
    function handleServerChangeCommit(newUrl, newSecret, groupId) {
        if (!newUrl || !newSecret)
            return;
        if (pendingServerMigration && pendingServerMigration.newServerUrl === newUrl) {
            log('Server migration committed: staying as relay server');
            relayClient?.disconnect();
            relayClient = null;
            pendingServerMigration = null;
            closeClientPanelSockets();
            clearClientCaches();
            return;
        }
        const effectiveGroupId = (groupId || '').trim() ||
            pendingServerMigration?.groupId ||
            relay.getGroup()?.groupId ||
            groupStore.loadGroup()?.groupId ||
            membershipStore.load()?.groupId ||
            '';
        saveClientMembership(effectiveGroupId, newSecret, newUrl);
        pendingServerMigration = null;
        if (modeManager.isRelay()) {
            groupStore.clearGroup();
            modeManager.transitionToClientFromRelay(newUrl);
        }
        else if (modeManager.isStandalone() && relay.getGroup()) {
            groupStore.clearGroup();
            modeManager.transitionToClient(newUrl, '');
        }
        startRelayClient(newUrl, '', newSecret);
    }
    function transitionToClientFromInvite(relayUrl, inviteCode, groupSecret = '') {
        const normalizedRelayUrl = sanitizeRelayUrl(relayUrl);
        const normalizedInviteCode = inviteCode.trim();
        const normalizedGroupSecret = groupSecret.trim();
        if (!normalizedRelayUrl || (!normalizedInviteCode && !normalizedGroupSecret)) {
            return false;
        }
        log(`[invite] Transitioning to client mode: ${normalizedRelayUrl}`);
        pendingServerMigration = null;
        membershipStore.clear();
        clearClientCaches();
        if (modeManager.isStandalone()) {
            groupStore.clearGroup();
            if (!modeManager.transitionToClient(normalizedRelayUrl, normalizedInviteCode)) {
                log('[invite] Failed to transition standalone -> client before joining invite.');
                return false;
            }
        }
        else if (modeManager.isRelay()) {
            groupStore.clearGroup();
            if (!modeManager.transitionToClientFromRelay(normalizedRelayUrl)) {
                log('[invite] Failed to transition relay -> client before joining invite.');
                return false;
            }
        }
        startRelayClient(normalizedRelayUrl, normalizedInviteCode, normalizedGroupSecret);
        return true;
    }
    function transitionBackToStandalone() {
        if (relayClient) {
            relayClient.disconnect();
            relayClient = null;
        }
        resolvePendingInviteJoin({ ok: false, reason: 'Transitioned back to standalone mode before invite completed.' });
        pendingServerMigration = null;
        clearClientCaches();
        closeClientPanelSockets();
        resetLocalAuthToken();
        modeManager.transitionToStandalone();
        startRelayServer();
        log('Transitioned back to standalone mode');
    }
    const savedMembership = membershipStore.load();
    const shouldStartAsClient = config.mode === 'client' ||
        (config.mode === 'standalone' && !!savedMembership);
    if (shouldStartAsClient && savedMembership) {
        syncLocalAuthToken(savedMembership.groupSecret);
        ensureClientGroupCache(savedMembership.groupId);
        modeManager.initialize('client');
        startRelayClient(savedMembership.relayUrl, '', savedMembership.groupSecret);
    }
    else {
        if (config.mode === 'client' && !savedMembership) {
            log('[mode] Client mode requested but no membership found; falling back to standalone.');
        }
        modeManager.initialize('standalone');
        startRelayServer();
    }
    let lastResolvedServerUrl = '';
    // Resolve server URL from request headers (reverse proxy support)
    function resolveServerUrl(req) {
        const proto = req.headers['x-forwarded-proto']?.split(',')[0]?.trim();
        let host = req.headers['x-forwarded-host']?.split(',')[0]?.trim();
        const forwardedPort = req.headers['x-forwarded-port']?.split(',')[0]?.trim();
        const socketEncrypted = req.socket.encrypted === true;
        if (!host) {
            const origin = (req.headers['origin'] || req.headers['referer']);
            if (origin) {
                try {
                    const url = new URL(origin);
                    host = url.host;
                }
                catch { }
            }
        }
        host ??= req.headers['host'];
        if (forwardedPort && host && !host.includes(':'))
            host = `${host}:${forwardedPort}`;
        const publicHost = getRuntimePublicHost();
        const scheme = proto?.toLowerCase() || (socketEncrypted ? 'https' : 'http');
        const wsSchemeValue = scheme === 'https' || scheme === 'wss' ? 'wss' : 'ws';
        const portForHost = wsSchemeValue === 'wss'
            ? (tlsRuntime.enabled ? tlsPort : primaryPort)
            : primaryPort;
        const lanHost = detectLanHost();
        if (host) {
            const { host: hostOnly } = splitHostPort(host);
            if (hostOnly && isLocalHost(hostOnly) && publicHost) {
                host = normalizeHostWithPort(publicHost, portForHost);
            }
            else if (hostOnly && isLocalHost(hostOnly) && lanHost) {
                host = normalizeHostWithPort(lanHost, portForHost);
            }
        }
        else if (publicHost) {
            host = normalizeHostWithPort(publicHost, portForHost);
        }
        else if (lanHost) {
            host = normalizeHostWithPort(lanHost, portForHost);
        }
        if (!host) {
            const serverUrl = buildRelayUrlFromConfig();
            lastResolvedServerUrl = serverUrl;
            return serverUrl;
        }
        {
            const { host: hostOnly } = splitHostPort(host);
            if (hostOnly) {
                host = normalizeHostWithPort(hostOnly, portForHost);
            }
        }
        const serverUrl = `${wsSchemeValue}://${host}/ws/`;
        lastResolvedServerUrl = serverUrl;
        if (!config.publicHost) {
            const hostOnly = splitHostPort(host).host.toLowerCase() || '';
            if (hostOnly && hostOnly !== 'localhost' && hostOnly !== '127.0.0.1') {
                relay.setRelayUrl(serverUrl);
            }
        }
        return serverUrl;
    }
    // Serve panel HTML (inline singlefile)
    let panelHtml = null;
    function getPanelHtml() {
        if (panelHtml)
            return panelHtml;
        // Try loading from web/dist/index.html first, then fallback
        const currentFilePath = fileURLToPath(import.meta.url);
        const webDistPath = path.resolve(path.dirname(currentFilePath), '../../web/dist/index.html');
        try {
            if (fs.existsSync(webDistPath)) {
                panelHtml = fs.readFileSync(webDistPath);
                return panelHtml;
            }
        }
        catch { }
        panelHtml = Buffer.from('<!DOCTYPE html><html><body><h1>PhoneShell Panel</h1><p>Run <code>cd web && npm run build</code> to build the frontend.</p></body></html>');
        return panelHtml;
    }
    // QR PNG cache
    let cachedQrPayload = '';
    let cachedQrPng = null;
    const requestHandler = async (req, res) => {
        const url = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`);
        const pathname = url.pathname.replace(/\/+$/, '') || '/';
        const panelPrivate = getPanelAccessMode() === 'private';
        const panelAuthorized = isPanelAuthorized(req);
        // CORS
        res.setHeader('Access-Control-Allow-Origin', '*');
        res.setHeader('Access-Control-Allow-Headers', 'Authorization, X-PhoneShell-Token, Content-Type');
        if (req.method === 'OPTIONS') {
            res.writeHead(204).end();
            return;
        }
        // --- Health check (no auth) ---
        if (pathname === '/ws/healthz') {
            writeJson(res, 200, { status: 'ok', startedAtUtc: new Date().toISOString() });
            return;
        }
        // --- POST /api/invite — receive invite to join a group (standalone devices) ---
        if (pathname === '/api/invite' && req.method === 'POST') {
            let body = '';
            req.on('data', (chunk) => { body += chunk.toString(); });
            req.on('end', async () => {
                try {
                    const invite = JSON.parse(body);
                    const relayUrl = sanitizeRelayUrl(invite.relayUrl || '');
                    const inviteCode = (invite.inviteCode || '').trim();
                    const groupSecret = (invite.groupSecret || '').trim();
                    if (!relayUrl || (!inviteCode && !groupSecret)) {
                        writeJson(res, 400, {
                            type: 'error',
                            code: 'bad_request',
                            message: 'relayUrl and inviteCode/groupSecret are required.',
                        });
                        return;
                    }
                    if (pendingInviteJoin) {
                        writeJson(res, 409, {
                            type: 'error',
                            code: 'invite_busy',
                            message: 'Another invite transition is currently in progress.',
                        });
                        return;
                    }
                    log(`[invite] Received invite: relay=${relayUrl} code=${inviteCode || '-'} secret=${groupSecret ? 'yes' : 'no'}`);
                    const waitJoin = beginPendingInviteJoin(10000);
                    const started = modeManager.isClient()
                        ? (startRelayClient(relayUrl, inviteCode, groupSecret), true)
                        : transitionToClientFromInvite(relayUrl, inviteCode, groupSecret);
                    if (!started) {
                        resolvePendingInviteJoin({ ok: false, reason: 'Unable to transition to client mode.' });
                        writeJson(res, 409, {
                            type: 'error',
                            code: 'mode_transition_failed',
                            message: 'Unable to transition to client mode for invite.',
                        });
                        return;
                    }
                    const joinResult = await waitJoin;
                    if (!joinResult.ok) {
                        writeJson(res, 502, {
                            type: 'error',
                            code: 'invite_join_failed',
                            message: joinResult.reason || 'Invite accepted but join confirmation not received.',
                        });
                        return;
                    }
                    writeJson(res, 200, {
                        status: 'accepted',
                        relayUrl,
                        mode: 'client',
                        groupId: joinResult.groupId || '',
                    });
                }
                catch {
                    writeJson(res, 400, { type: 'error', code: 'bad_request', message: 'Invalid JSON body.' });
                }
            });
            return;
        }
        // --- Standalone QR code endpoint ---
        if (pathname === '/api/standalone/qr.png') {
            const baseServerUrl = resolveServerUrl(req);
            const serverUrl = appendOrReplaceTokenQuery(baseServerUrl, effectiveToken);
            const httpUrl = buildHttpUrlFromWebSocketUrl(baseServerUrl);
            const qrPayload = buildStandalonePayload(httpUrl, serverUrl, deviceId, displayName);
            try {
                const png = await generateQrPng(qrPayload);
                res.writeHead(200, { 'Content-Type': 'image/png', 'Cache-Control': 'no-cache' });
                res.end(png);
            }
            catch {
                writeJson(res, 500, { type: 'error', code: 'qr_error', message: 'QR generation failed.' });
            }
            return;
        }
        // --- Standalone device info ---
        if (pathname === '/api/standalone/info') {
            const baseServerUrl = resolveServerUrl(req);
            const serverUrl = appendOrReplaceTokenQuery(baseServerUrl, effectiveToken);
            const httpUrl = buildHttpUrlFromWebSocketUrl(baseServerUrl);
            writeJson(res, 200, {
                deviceId,
                displayName,
                os,
                availableShells,
                httpUrl,
                wsUrl: serverUrl,
            });
            return;
        }
        // --- Panel HTML ---
        if (pathname === '/' || pathname === '/panel') {
            if (panelPrivate && !panelAuthorized) {
                res.writeHead(404).end();
                return;
            }
            const tokenForPanel = panelAuthorized ? extractToken(req) : undefined;
            if (tokenForPanel) {
                res.setHeader('Set-Cookie', buildPanelCookie(tokenForPanel, req));
            }
            const html = getPanelHtml();
            res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-cache' });
            res.end(html);
            return;
        }
        // --- Panel static assets from web/dist ---
        if (pathname.startsWith('/panel/')) {
            if (panelPrivate && !panelAuthorized) {
                res.writeHead(404).end();
                return;
            }
            const assetPath = pathname.slice('/panel/'.length);
            const currentFilePath = fileURLToPath(import.meta.url);
            const webDistDir = path.resolve(path.dirname(currentFilePath), '../../web/dist');
            const filePath = path.join(webDistDir, assetPath);
            // Prevent directory traversal
            if (!filePath.startsWith(webDistDir)) {
                res.writeHead(403).end();
                return;
            }
            try {
                if (fs.existsSync(filePath) && fs.statSync(filePath).isFile()) {
                    const ext = path.extname(filePath).toLowerCase();
                    const mimeTypes = {
                        '.js': 'application/javascript', '.css': 'text/css', '.html': 'text/html',
                        '.png': 'image/png', '.svg': 'image/svg+xml', '.json': 'application/json',
                        '.woff': 'font/woff', '.woff2': 'font/woff2',
                    };
                    const contentType = mimeTypes[ext] || 'application/octet-stream';
                    const data = fs.readFileSync(filePath);
                    res.writeHead(200, { 'Content-Type': contentType, 'Cache-Control': 'public, max-age=86400' });
                    res.end(data);
                    return;
                }
            }
            catch { }
            // Fallback: serve index.html for SPA routing
            const html = getPanelHtml();
            res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-cache' });
            res.end(html);
            return;
        }
        // --- Panel API (no auth required for bootstrap) ---
        if (pathname === '/api/panel/verify') {
            if (panelPrivate && !panelAuthorized) {
                writeJson(res, 404, { type: 'error', code: 'not_found', message: 'Not found.' });
                return;
            }
            writeJson(res, 200, { valid: panelAuthorized });
            return;
        }
        if (pathname === '/api/panel/pairing') {
            if (panelPrivate && !panelAuthorized) {
                writeJson(res, 404, { type: 'error', code: 'not_found', message: 'Not found.' });
                return;
            }
            if (modeManager.isClient()) {
                const membership = getClientMembership();
                if (membership) {
                    const group = buildClientGroupPayload();
                    writeJson(res, 200, {
                        requiresAuth: !!effectiveToken,
                        hasGroup: true,
                        groupId: group?.groupId || membership.groupId,
                        serverUrl: membership.relayUrl,
                        qrPayload: '',
                        hasBoundMobile: true,
                        boundMobileOnline: !!group?.boundMobileId,
                    });
                    return;
                }
            }
            const serverUrl = resolveServerUrl(req);
            writeJson(res, 200, relay.getPanelPairingPayload(serverUrl));
            return;
        }
        if (pathname === '/api/panel/qr.png') {
            if (panelPrivate && !panelAuthorized) {
                writeJson(res, 404, { type: 'error', code: 'not_found', message: 'Not found.' });
                return;
            }
            const payload = url.searchParams.get('payload') || relay.getBindQrPayload(resolveServerUrl(req));
            if (!payload) {
                writeJson(res, 404, { type: 'error', code: 'not_found', message: 'QR payload not available.' });
                return;
            }
            if (cachedQrPayload === payload && cachedQrPng) {
                res.writeHead(200, { 'Content-Type': 'image/png', 'Cache-Control': 'no-cache' });
                res.end(cachedQrPng);
                return;
            }
            try {
                cachedQrPng = await generateQrPng(payload);
                cachedQrPayload = payload;
                res.writeHead(200, { 'Content-Type': 'image/png', 'Cache-Control': 'no-cache' });
                res.end(cachedQrPng);
            }
            catch {
                writeJson(res, 500, { type: 'error', code: 'qr_error', message: 'QR generation failed.' });
            }
            return;
        }
        if (pathname === '/api/panel/login/start') {
            if (panelPrivate && !panelAuthorized) {
                writeJson(res, 404, { type: 'error', code: 'not_found', message: 'Not found.' });
                return;
            }
            const serverUrl = resolveServerUrl(req);
            const requesterAddress = req.socket.remoteAddress;
            const payload = relay.startPanelLogin(requesterAddress, serverUrl);
            const membership = getClientMembership();
            if (membership) {
                writeJson(res, 200, {
                    requestId: payload.requestId,
                    status: 'approved',
                    message: 'Approved.',
                    token: membership.groupSecret,
                    expiresAtUtc: payload.expiresAtUtc,
                    loginQrPayload: '',
                });
                return;
            }
            writeJson(res, 200, payload);
            return;
        }
        if (pathname.startsWith('/api/panel/login/status/')) {
            if (panelPrivate && !panelAuthorized) {
                writeJson(res, 404, { type: 'error', code: 'not_found', message: 'Not found.' });
                return;
            }
            const requestId = pathname.slice('/api/panel/login/status/'.length);
            if (!requestId) {
                writeJson(res, 400, { type: 'error', code: 'bad_request', message: 'Request ID is required.' });
                return;
            }
            const membership = getClientMembership();
            if (membership) {
                writeJson(res, 200, {
                    requestId,
                    status: 'approved',
                    message: 'Approved.',
                    token: membership.groupSecret,
                    expiresAtUtc: new Date(Date.now() + 365 * 24 * 60 * 60 * 1000).toISOString(),
                    loginQrPayload: '',
                });
                return;
            }
            const status = relay.getPanelLoginStatus(requestId);
            if (!status) {
                writeJson(res, 404, { type: 'error', code: 'not_found', message: 'Login request not found.' });
                return;
            }
            writeJson(res, 200, status);
            return;
        }
        if (pathname === '/api/panel/login/qr.png') {
            if (panelPrivate && !panelAuthorized) {
                writeJson(res, 404, { type: 'error', code: 'not_found', message: 'Not found.' });
                return;
            }
            const payload = url.searchParams.get('payload');
            if (!payload) {
                writeJson(res, 400, { type: 'error', code: 'bad_request', message: 'Payload query parameter is required.' });
                return;
            }
            try {
                const png = await generateQrPng(payload);
                res.writeHead(200, { 'Content-Type': 'image/png', 'Cache-Control': 'no-cache' });
                res.end(png);
            }
            catch {
                writeJson(res, 500, { type: 'error', code: 'qr_error', message: 'QR generation failed.' });
            }
            return;
        }
        // --- Authed API ---
        if (pathname.startsWith('/api/')) {
            const token = extractToken(req);
            if (!relay.isAuthorized(token)) {
                writeJson(res, 401, { type: 'error', code: 'unauthorized', message: 'Missing or invalid relay token.' });
                return;
            }
            if (pathname === '/api/status') {
                if (modeManager.isClient()) {
                    const membership = getClientMembership();
                    writeJson(res, 200, {
                        status: 'ok',
                        mode: 'client',
                        relayConnected: !!relayClient?.isConnected(),
                        relayUrl: membership?.relayUrl || '',
                        groupId: clientGroupCache?.groupId || membership?.groupId || '',
                        registeredDeviceCount: buildClientDeviceList().length,
                        devices: buildClientDeviceList(),
                    });
                    return;
                }
                writeJson(res, 200, relay.buildStatusPayload());
                return;
            }
            if (pathname === '/api/devices') {
                if (modeManager.isClient()) {
                    writeJson(res, 200, buildClientDeviceList());
                    return;
                }
                writeJson(res, 200, relay.getDeviceList());
                return;
            }
            if (pathname === '/api/group') {
                if (modeManager.isClient()) {
                    const group = buildClientGroupPayload();
                    if (!group) {
                        writeJson(res, 404, { type: 'error', code: 'not_found', message: 'Group not initialized.' });
                        return;
                    }
                    writeJson(res, 200, group);
                    return;
                }
                const group = relay.getGroup();
                if (!group) {
                    writeJson(res, 404, { type: 'error', code: 'not_found', message: 'Group not initialized.' });
                    return;
                }
                writeJson(res, 200, {
                    groupId: group.groupId, serverDeviceId: group.serverDeviceId,
                    boundMobileId: group.boundMobileId, createdAt: group.createdAt,
                    members: relay.buildGroupMemberInfoList(),
                });
                return;
            }
            if (pathname.startsWith('/api/sessions/')) {
                const sesDeviceId = pathname.slice('/api/sessions/'.length);
                if (!sesDeviceId) {
                    writeJson(res, 400, { type: 'error', code: 'bad_request', message: 'Device ID is required.' });
                    return;
                }
                // Only local device sessions via REST
                const sessions = terminalManager.getSessionList();
                writeJson(res, 200, { deviceId: sesDeviceId, sessions });
                return;
            }
            writeJson(res, 404, { type: 'error', code: 'not_found', message: 'Unknown API endpoint.' });
            return;
        }
        // --- WS info ---
        if (pathname === '/ws' || pathname === '/ws/') {
            writeJson(res, 200, {
                service: 'PhoneShell Relay', websocketPath: '/ws/',
                healthPath: '/ws/healthz', statusPath: '/ws/status',
                authenticationRequired: !!effectiveToken,
            });
            return;
        }
        if (pathname === '/ws/status') {
            const token = extractToken(req);
            if (!relay.isAuthorized(token)) {
                writeJson(res, 401, { type: 'error', code: 'unauthorized', message: 'Missing or invalid relay token.' });
                return;
            }
            writeJson(res, 200, relay.buildStatusPayload());
            return;
        }
        writeJson(res, 404, { type: 'error', code: 'not_found', message: 'Unknown endpoint.' });
    };
    const createServer = (useTls) => useTls ? https.createServer(tlsRuntime.options, requestHandler) : http.createServer(requestHandler);
    const server = createServer(primaryUsesTls);
    const tlsServer = tlsRuntime.enabled && !primaryUsesTls ? createServer(true) : null;
    const panelServer = panelPort && webPanelEnabled ? createServer(tlsRuntime.enabled) : null;
    // WebSocket server
    const wss = new WebSocketServer({ noServer: true });
    const panelBridgeWss = new WebSocketServer({ noServer: true });
    panelBridgeWss.on('connection', (ws) => {
        clientPanelSockets.add(ws);
        sendInitialClientPanelSnapshot(ws);
        requestClientDeviceList();
        const pingInterval = setInterval(() => {
            if (ws.readyState === WebSocket.OPEN) {
                ws.ping();
            }
            else {
                clearInterval(pingInterval);
            }
        }, 30000);
        ws.on('message', (data) => {
            try {
                relayClient?.send(data.toString('utf-8'));
            }
            catch (err) {
                log(`[panel-bridge] WS proxy error: ${err.message}`);
            }
        });
        ws.on('close', () => {
            clearInterval(pingInterval);
            clientPanelSockets.delete(ws);
        });
        ws.on('error', (err) => {
            log(`[panel-bridge] Client WS error: ${err.message}`);
        });
    });
    const handleUpgrade = (req, socket, head) => {
        const url = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`);
        const pathname = url.pathname.replace(/\/+$/, '') || '/';
        if (pathname !== '/ws' && pathname !== '/ws/') {
            socket.destroy();
            return;
        }
        if (modeManager.isClient()) {
            const token = extractToken(req) || url.searchParams.get('token') || undefined;
            if (!relay.isAuthorized(token)) {
                socket.write('HTTP/1.1 401 Unauthorized\r\n\r\n');
                socket.destroy();
                return;
            }
            panelBridgeWss.handleUpgrade(req, socket, head, (ws) => {
                panelBridgeWss.emit('connection', ws, req);
            });
            return;
        }
        // Learn reachable WS host from real upgrade traffic so invite.create.response
        // does not keep returning stale localhost/external addresses.
        try {
            resolveServerUrl(req);
        }
        catch { }
        const token = extractToken(req) || url.searchParams.get('token') || undefined;
        const inviteCode = url.searchParams.get('invite') || undefined;
        // Check token authorization first
        const tokenAuthorized = relay.isAuthorized(token);
        // Check invite code (will be consumed on successful join)
        const inviteValid = inviteCode && relay.getInviteManager().isValidInviteCode(inviteCode);
        if (!tokenAuthorized && !inviteValid) {
            socket.write('HTTP/1.1 401 Unauthorized\r\n\r\n');
            socket.destroy();
            return;
        }
        wss.handleUpgrade(req, socket, head, (ws) => {
            relay.handleConnection(ws);
        });
    };
    server.on('upgrade', handleUpgrade);
    if (tlsServer)
        tlsServer.on('upgrade', handleUpgrade);
    if (panelServer)
        panelServer.on('upgrade', handleUpgrade);
    return {
        start() {
            server.listen(primaryPort, '0.0.0.0', () => {
                const primaryScheme = httpScheme(primaryUsesTls);
                log(`PhoneShell server listening on port ${primaryPort} (${primaryScheme.toUpperCase()})`);
                log(`  Device: ${displayName} (${deviceId})`);
                log(`  OS: ${os}`);
                log(`  Available shells: ${availableShells.join(', ')}`);
                // Set relay URL for invite system
                if (config.publicHost) {
                    applyPublicHostToRelay(config.publicHost);
                }
                else {
                    relay.setRelayUrl(buildRelayUrlFromConfig());
                }
                const group = relay.getGroup();
                if (group) {
                    log(`  Group ID: ${group.groupId}`);
                    log(`  Group Secret: ${group.groupSecret}`);
                }
                if (config.publicHost && webPanelEnabled) {
                    const panelHostPort = panelPort || (tlsRuntime.enabled ? tlsPort : primaryPort);
                    const panelHost = normalizeHostWithPort(config.publicHost, panelHostPort);
                    const panelScheme = tlsRuntime.enabled ? 'https' : 'http';
                    log(`  Public: ${panelScheme}://${panelHost}/panel/`);
                }
                log(`  Health: ${primaryScheme}://localhost:${primaryPort}/ws/healthz`);
                if (tlsServer) {
                    log(`  Health (TLS): https://localhost:${tlsPort}/ws/healthz`);
                }
                if (webPanelEnabled) {
                    if (panelPort) {
                        const panelScheme = tlsRuntime.enabled ? 'https' : 'http';
                        log(`  Panel: ${panelScheme}://localhost:${panelPort}/panel/`);
                    }
                    else {
                        log(`  Panel: ${primaryScheme}://localhost:${primaryPort}/panel/`);
                        if (tlsServer) {
                            log(`  Panel (TLS): https://localhost:${tlsPort}/panel/`);
                        }
                    }
                }
            });
            if (tlsServer) {
                tlsServer.listen(tlsPort, '0.0.0.0', () => {
                    log(`TLS server listening on port ${tlsPort}`);
                });
            }
            if (panelServer) {
                panelServer.listen(panelPort, '0.0.0.0', () => {
                    log(`Panel server listening on port ${panelPort}`);
                });
            }
            if (!config.publicHost) {
                void detectPublicHost((message) => log(message)).then((result) => {
                    if (!result) {
                        log('[public] Failed to resolve public host');
                        return;
                    }
                    resolvedPublicHost = result.host;
                    log(`[public] Resolved public host (${result.source}): ${result.host}`);
                    log('  Public host auto-detected for reference only. Set config.publicHost to force external relay URL.');
                    if (webPanelEnabled) {
                        const panelHostPort = panelPort || (tlsRuntime.enabled ? tlsPort : primaryPort);
                        const panelHost = normalizeHostWithPort(result.host, panelHostPort);
                        const panelScheme = tlsRuntime.enabled ? 'https' : 'http';
                        log(`  Public candidate: ${panelScheme}://${panelHost}/panel/`);
                    }
                });
            }
        },
        stop() {
            relay.stop();
            terminalManager.disposeAll();
            wss.close();
            panelBridgeWss.close();
            server.close();
            tlsServer?.close();
            panelServer?.close();
            log('PhoneShell server stopped.');
        },
    };
}
function writeJson(res, statusCode, data) {
    const json = JSON.stringify(data);
    res.writeHead(statusCode, {
        'Content-Type': 'application/json; charset=utf-8',
        'Access-Control-Allow-Origin': '*',
    });
    res.end(json);
}
function extractToken(req) {
    const auth = req.headers['authorization'];
    if (auth?.startsWith('Bearer '))
        return auth.slice(7).trim();
    const xToken = req.headers['x-phoneshell-token'];
    if (xToken)
        return xToken.trim();
    const cookie = req.headers['cookie'];
    if (cookie) {
        for (const part of cookie.split(';')) {
            const trimmed = part.trim();
            if (!trimmed)
                continue;
            if (trimmed.startsWith('ps_token=')) {
                const value = trimmed.slice('ps_token='.length);
                if (value) {
                    try {
                        return decodeURIComponent(value.trim());
                    }
                    catch {
                        return value.trim();
                    }
                }
            }
        }
    }
    const url = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`);
    const queryToken = url.searchParams.get('token');
    if (queryToken)
        return queryToken.trim();
    return undefined;
}
//# sourceMappingURL=app.js.map