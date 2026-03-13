import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { URL } from 'node:url';
import { WebSocketServer } from 'ws';
import { RelayServer } from '../relay/relay-server.js';
import { TerminalManager } from '../terminal/terminal-manager.js';
import { DeviceStore } from '../store/device-store.js';
import { GroupStore } from '../store/group-store.js';
import { generateQrPng } from '../auth/qr-service.js';
function log(msg) {
    const ts = new Date().toLocaleTimeString('en-US', { hour12: false });
    console.log(`[${ts}] ${msg}`);
}
export function createApp(config) {
    const deviceStore = new DeviceStore(config.baseDirectory);
    const groupStore = new GroupStore(config.baseDirectory);
    const identity = deviceStore.loadOrCreate();
    const deviceId = identity.deviceId;
    const displayName = config.displayName || identity.displayName;
    const os = `Linux ${process.arch}`;
    const terminalManager = new TerminalManager(config.defaultCols, config.defaultRows);
    const availableShells = config.modules.terminal
        ? terminalManager.getAvailableShells().map(s => s.displayName)
        : [];
    const relay = new RelayServer();
    relay.setLogger((msg) => log(`[relay] ${msg}`));
    // Output ordering: per-session promise chain
    const outputChains = new Map();
    function enqueueOutput(sessionId, fn) {
        const chain = (outputChains.get(sessionId) || Promise.resolve())
            .then(() => fn().catch(() => { }));
        outputChains.set(sessionId, chain);
    }
    // Wire terminal callbacks
    relay.setCallbacks({
        onLocalTerminalInput: (sessionId, data) => terminalManager.writeInput(sessionId, data),
        onLocalTerminalResize: (sessionId, cols, rows) => terminalManager.resize(sessionId, cols, rows),
        onLocalTerminalSessionEnded: (sessionId) => {
            terminalManager.closeSession(sessionId);
            outputChains.delete(sessionId);
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
    // Wire terminal output to relay broadcast
    terminalManager.onOutput = (sessionId, data) => {
        enqueueOutput(sessionId, () => relay.broadcastLocalTerminalOutput(deviceId, sessionId, data));
    };
    terminalManager.onExit = (sessionId) => {
        relay.broadcastLocalTerminalClosed(deviceId, sessionId);
        relay.broadcastLocalSessionListChanged(deviceId);
        outputChains.delete(sessionId);
    };
    // Set auth token
    const effectiveToken = config.groupSecret || config.relayAuthToken;
    relay.setAuthToken(effectiveToken);
    // Init group
    relay.initGroup(groupStore, deviceId, displayName, os, availableShells);
    relay.registerLocalDevice(deviceId, displayName, os, availableShells);
    relay.start();
    // Resolve server URL from request headers (reverse proxy support)
    function resolveServerUrl(req) {
        const proto = req.headers['x-forwarded-proto']?.split(',')[0]?.trim();
        let host = req.headers['x-forwarded-host']?.split(',')[0]?.trim();
        const port = req.headers['x-forwarded-port']?.split(',')[0]?.trim();
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
        if (port && host && !host.includes(':'))
            host = `${host}:${port}`;
        if (!host) {
            if (config.publicHost) {
                const ph = config.publicHost.includes(':') ? config.publicHost : `${config.publicHost}:${config.port}`;
                return `ws://${ph}/ws/`;
            }
            return `ws://localhost:${config.port}/ws/`;
        }
        const scheme = proto?.toLowerCase();
        const wsScheme = scheme === 'https' || scheme === 'wss' ? 'wss' : 'ws';
        return `${wsScheme}://${host}/ws/`;
    }
    // Serve panel HTML (inline singlefile)
    let panelHtml = null;
    function getPanelHtml() {
        if (panelHtml)
            return panelHtml;
        // Try loading from web/dist/index.html first, then fallback
        const webDistPath = path.resolve(path.dirname(new URL(import.meta.url).pathname), '../../web/dist/index.html');
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
    const server = http.createServer(async (req, res) => {
        const url = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`);
        const pathname = url.pathname.replace(/\/+$/, '') || '/';
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
        // --- Panel HTML ---
        if (pathname === '/' || pathname === '/panel') {
            const html = getPanelHtml();
            res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-cache' });
            res.end(html);
            return;
        }
        // --- Panel static assets from web/dist ---
        if (pathname.startsWith('/panel/')) {
            const assetPath = pathname.slice('/panel/'.length);
            const webDistDir = path.resolve(path.dirname(new URL(import.meta.url).pathname), '../../web/dist');
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
            writeJson(res, 200, { valid: false });
            return;
        }
        if (pathname === '/api/panel/pairing') {
            const serverUrl = resolveServerUrl(req);
            writeJson(res, 200, relay.getPanelPairingPayload(serverUrl));
            return;
        }
        if (pathname === '/api/panel/qr.png') {
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
            const serverUrl = resolveServerUrl(req);
            const requesterAddress = req.socket.remoteAddress;
            const payload = relay.startPanelLogin(requesterAddress, serverUrl);
            writeJson(res, 200, payload);
            return;
        }
        if (pathname.startsWith('/api/panel/login/status/')) {
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
    });
    // WebSocket server
    const wss = new WebSocketServer({ noServer: true });
    server.on('upgrade', (req, socket, head) => {
        const url = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`);
        const pathname = url.pathname.replace(/\/+$/, '') || '/';
        if (pathname !== '/ws' && pathname !== '/ws/') {
            socket.destroy();
            return;
        }
        const token = extractToken(req) || url.searchParams.get('token') || undefined;
        if (!relay.isAuthorized(token)) {
            socket.write('HTTP/1.1 401 Unauthorized\r\n\r\n');
            socket.destroy();
            return;
        }
        wss.handleUpgrade(req, socket, head, (ws) => {
            relay.handleConnection(ws);
        });
    });
    return {
        start() {
            server.listen(config.port, '0.0.0.0', () => {
                log(`PhoneShell server listening on port ${config.port}`);
                log(`  Device: ${displayName} (${deviceId})`);
                log(`  OS: ${os}`);
                log(`  Available shells: ${availableShells.join(', ')}`);
                const group = relay.getGroup();
                if (group) {
                    log(`  Group ID: ${group.groupId}`);
                    log(`  Group Secret: ${group.groupSecret}`);
                }
                if (config.publicHost) {
                    const ph = config.publicHost.includes(':') ? config.publicHost : `${config.publicHost}:${config.port}`;
                    log(`  Public: http://${ph}/panel/`);
                }
                log(`  Health: http://localhost:${config.port}/ws/healthz`);
                log(`  Panel: http://localhost:${config.port}/panel/`);
            });
        },
        stop() {
            relay.stop();
            terminalManager.disposeAll();
            wss.close();
            server.close();
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
    const url = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`);
    const queryToken = url.searchParams.get('token');
    if (queryToken)
        return queryToken.trim();
    return undefined;
}
//# sourceMappingURL=app.js.map