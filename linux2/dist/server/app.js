import http from 'node:http';
import https from 'node:https';
import fs from 'node:fs';
import path from 'node:path';
import net from 'node:net';
import { URL, fileURLToPath } from 'node:url';
import { WebSocketServer } from 'ws';
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
    // Set auth token (server mode only)
    let effectiveToken = config.groupSecret || config.relayAuthToken;
    // Mode manager for standalone ↔ client transitions
    const modeManager = new ModeManager();
    modeManager.setLogger((msg) => log(`[mode] ${msg}`));
    let relayClient = null;
    let pendingServerMigration = null;
    let resolvedPublicHost = '';
    let panelAccessCache = panelAccessDefault;
    let panelAccessCacheAt = 0;
    const wsScheme = (useTls) => (useTls ? 'wss' : 'ws');
    const httpScheme = (useTls) => (useTls ? 'https' : 'http');
    const buildWsUrl = (host, port, useTls) => `${wsScheme(useTls)}://${normalizeHostWithPort(host, port)}/ws/`;
    function startRelayServer(tokenOverride) {
        if (tokenOverride)
            effectiveToken = tokenOverride;
        relay.setAuthToken(effectiveToken);
        relay.initGroup(groupStore, deviceId, displayName, os, availableShells);
        relay.registerLocalDevice(deviceId, displayName, os, availableShells);
        relay.start();
        wireTerminalOutputToRelay();
    }
    function startRelayClient(relayUrl, inviteCode, groupSecret, options) {
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
            onGroupJoined: (groupId, newSecret) => {
                if (!newSecret)
                    return;
                membershipStore.save({
                    groupId,
                    groupSecret: newSecret,
                    relayUrl,
                    updatedAtUtc: new Date().toISOString(),
                });
            },
            onKicked: (reason) => {
                log(`Kicked from group: ${reason}`);
                membershipStore.clear();
                transitionBackToStandalone();
            },
            onGroupDissolved: (reason) => {
                log(`Group dissolved: ${reason}`);
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
            onServerChanged: (newUrl, newSecret) => {
                handleServerChangeCommit(newUrl, newSecret);
            },
        });
        if (!preserveServer) {
            wireTerminalOutputToClient();
        }
        relayClient.connect(relayUrl, deviceId, displayName, os, availableShells, inviteCode, groupSecret);
    }
    function getRuntimePublicHost() {
        return config.publicHost || resolvedPublicHost;
    }
    function buildRelayUrlFromConfig() {
        const publicHost = getRuntimePublicHost();
        if (publicHost) {
            const port = tlsRuntime.enabled ? tlsPort : primaryPort;
            return buildWsUrl(publicHost, port, tlsRuntime.enabled);
        }
        const port = tlsRuntime.enabled ? tlsPort : primaryPort;
        return buildWsUrl('localhost', port, tlsRuntime.enabled);
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
        const newServerUrl = lastResolvedServerUrl || buildRelayUrlFromConfig();
        if (!config.publicHost && !resolvedPublicHost && !lastResolvedServerUrl) {
            log('[mode] Server migration: publicHost not set, using localhost for new server URL');
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
        }
        else if (modeManager.isStandalone()) {
            modeManager.transitionToRelay();
        }
        relay.stop();
        startRelayServer(groupSecret);
        return newServerUrl;
    }
    function handleServerChangeCommit(newUrl, newSecret) {
        if (!newUrl || !newSecret)
            return;
        if (pendingServerMigration && pendingServerMigration.newServerUrl === newUrl) {
            log('Server migration committed: staying as relay server');
            relayClient?.disconnect();
            relayClient = null;
            pendingServerMigration = null;
            return;
        }
        if (modeManager.isRelay()) {
            groupStore.clearGroup();
            modeManager.transitionToClientFromRelay(newUrl);
        }
        startRelayClient(newUrl, '', newSecret);
    }
    function transitionBackToStandalone() {
        if (relayClient) {
            relayClient.disconnect();
            relayClient = null;
        }
        pendingServerMigration = null;
        modeManager.transitionToStandalone();
        startRelayServer();
        log('Transitioned back to standalone mode');
    }
    const savedMembership = membershipStore.load();
    const shouldStartAsClient = config.mode === 'client' ||
        (config.mode === 'standalone' && !!savedMembership);
    if (shouldStartAsClient && savedMembership) {
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
        if (host) {
            const { host: hostOnly } = splitHostPort(host);
            if (hostOnly && isLocalHost(hostOnly) && publicHost) {
                host = normalizeHostWithPort(publicHost, portForHost);
            }
        }
        else if (publicHost) {
            host = normalizeHostWithPort(publicHost, portForHost);
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
            req.on('end', () => {
                try {
                    const invite = JSON.parse(body);
                    if (!invite.relayUrl || !invite.inviteCode) {
                        writeJson(res, 400, { type: 'error', code: 'bad_request', message: 'relayUrl and inviteCode are required.' });
                        return;
                    }
                    log(`[invite] Received invite: relay=${invite.relayUrl} code=${invite.inviteCode}`);
                    if (modeManager.isClient()) {
                        startRelayClient(invite.relayUrl, invite.inviteCode, '');
                        writeJson(res, 200, { status: 'accepted', relayUrl: invite.relayUrl, mode: 'client' });
                        return;
                    }
                    log('[invite] Keeping server mode; starting relay client in background');
                    startRelayClient(invite.relayUrl, invite.inviteCode, '', { preserveServer: true });
                    writeJson(res, 200, { status: 'accepted', relayUrl: invite.relayUrl, mode: 'relay' });
                }
                catch {
                    writeJson(res, 400, { type: 'error', code: 'bad_request', message: 'Invalid JSON body.' });
                }
            });
            return;
        }
        // --- Standalone QR code endpoint ---
        if (pathname === '/api/standalone/qr.png') {
            const serverUrl = resolveServerUrl(req);
            const httpUrl = serverUrl.replace(/^ws/, 'http').replace(/\/ws\/?$/, '');
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
            const serverUrl = resolveServerUrl(req);
            const httpUrl = serverUrl.replace(/^ws/, 'http').replace(/\/ws\/?$/, '');
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
                writeJson(res, 200, relay.buildStatusPayload());
                return;
            }
            if (pathname === '/api/devices') {
                writeJson(res, 200, relay.getDeviceList());
                return;
            }
            if (pathname === '/api/group') {
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
    const handleUpgrade = (req, socket, head) => {
        const url = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`);
        const pathname = url.pathname.replace(/\/+$/, '') || '/';
        if (pathname !== '/ws' && pathname !== '/ws/') {
            socket.destroy();
            return;
        }
        // Reject WS connections when not in standalone/relay mode
        if (modeManager.isClient()) {
            socket.write('HTTP/1.1 503 Service Unavailable\r\n\r\n');
            socket.destroy();
            return;
        }
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
                    applyPublicHostToRelay(result.host);
                    if (webPanelEnabled) {
                        const panelHostPort = panelPort || (tlsRuntime.enabled ? tlsPort : primaryPort);
                        const panelHost = normalizeHostWithPort(result.host, panelHostPort);
                        const panelScheme = tlsRuntime.enabled ? 'https' : 'http';
                        log(`  Public: ${panelScheme}://${panelHost}/panel/`);
                    }
                });
            }
        },
        stop() {
            relay.stop();
            terminalManager.disposeAll();
            wss.close();
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