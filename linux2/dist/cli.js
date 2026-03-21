#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import https from 'node:https';
import { spawn, spawnSync } from 'node:child_process';
import readline from 'node:readline/promises';
import { EventEmitter } from 'node:events';
import { fileURLToPath } from 'node:url';
import WebSocket from 'ws';
import { serialize, deserialize } from './protocol/serializer.js';
const DEFAULT_CONFIG_PATH = '/etc/phoneshell/config.json';
const DEFAULT_BASE_DIR = '/etc/phoneshell';
const DEFAULT_PORT = 19090;
function showHelp() {
    console.log(`
PhoneShell CLI

Usage:
  phoneshell [local] [options]
  phoneshell attach [options]
  phoneshell list [options]
  phoneshell group reset [options]
  phoneshell install [options]

Options (local):
  --server <wsUrl>       WebSocket URL (ws:// or wss://, default: ws://127.0.0.1:19090/ws/)
  --token <token>        Auth token (defaults to group secret if available)
  --config <path>        Config file path (default: /etc/phoneshell/config.json)
  --device <deviceId>    Target device ID (default: local device)
  --shell <shellId>      Shell ID for new session (default: bash/zsh/sh)
  --attach [sessionId]   Attach to existing session (prompt if omitted)
  --list                 List sessions and exit
  --help                 Show help

Install:
  phoneshell install     Run interactive installer (systemd + config)

Group:
  phoneshell group reset Clear group data (group.json + group-membership.json)
`);
}
function showGroupHelp() {
    console.log(`
Group Commands

Usage:
  phoneshell group reset [options]

Options:
  --config <path>        Config file path (default: /etc/phoneshell/config.json)
  --restart             Restart phoneshell service after reset
  --help                 Show help
`);
}
function parseLocalArgs(args) {
    const opts = {};
    for (let i = 0; i < args.length; i++) {
        const arg = args[i];
        if (arg.startsWith('--server=')) {
            opts.serverUrl = arg.slice('--server='.length);
            continue;
        }
        if (arg === '--server' && i + 1 < args.length) {
            opts.serverUrl = args[++i];
            continue;
        }
        if (arg.startsWith('--token=')) {
            opts.token = arg.slice('--token='.length);
            continue;
        }
        if (arg === '--token' && i + 1 < args.length) {
            opts.token = args[++i];
            continue;
        }
        if (arg.startsWith('--config=')) {
            opts.configPath = arg.slice('--config='.length);
            continue;
        }
        if (arg === '--config' && i + 1 < args.length) {
            opts.configPath = args[++i];
            continue;
        }
        if (arg.startsWith('--device=')) {
            opts.deviceId = arg.slice('--device='.length);
            continue;
        }
        if (arg === '--device' && i + 1 < args.length) {
            opts.deviceId = args[++i];
            continue;
        }
        if (arg.startsWith('--shell=')) {
            opts.shellId = arg.slice('--shell='.length);
            continue;
        }
        if (arg === '--shell' && i + 1 < args.length) {
            opts.shellId = args[++i];
            continue;
        }
        if (arg === '--list') {
            opts.listOnly = true;
            continue;
        }
        if (arg.startsWith('--attach=')) {
            opts.attach = true;
            opts.attachSessionId = arg.slice('--attach='.length) || undefined;
            continue;
        }
        if (arg === '--attach') {
            opts.attach = true;
            const next = args[i + 1];
            if (next && !next.startsWith('-')) {
                opts.attachSessionId = next;
                i++;
            }
            continue;
        }
    }
    return opts;
}
function parseGroupResetArgs(args) {
    const opts = {};
    for (let i = 0; i < args.length; i++) {
        const arg = args[i];
        if (arg.startsWith('--config=')) {
            opts.configPath = arg.slice('--config='.length);
            continue;
        }
        if (arg === '--config' && i + 1 < args.length) {
            opts.configPath = args[++i];
            continue;
        }
        if (arg === '--restart') {
            opts.restart = true;
            continue;
        }
    }
    return opts;
}
function loadConfigJson(configPath) {
    try {
        if (fs.existsSync(configPath)) {
            const raw = fs.readFileSync(configPath, 'utf-8');
            const parsed = JSON.parse(raw);
            return parsed;
        }
    }
    catch {
        // ignore
    }
    return null;
}
function resolveBaseDirectory(configPath) {
    const cfg = loadConfigJson(configPath);
    const base = cfg?.baseDirectory;
    if (typeof base === 'string' && base.trim())
        return base.trim();
    return DEFAULT_BASE_DIR;
}
function resolvePort(configPath) {
    const cfg = loadConfigJson(configPath);
    const port = cfg?.port;
    if (typeof port === 'number' && port >= 1 && port <= 65535)
        return port;
    return DEFAULT_PORT;
}
function clearFileIfExists(filePath) {
    try {
        if (fs.existsSync(filePath)) {
            fs.unlinkSync(filePath);
            return true;
        }
    }
    catch {
        // ignore
    }
    return false;
}
function runGroupReset(options) {
    const configPath = options.configPath || DEFAULT_CONFIG_PATH;
    const baseDir = resolveBaseDirectory(configPath);
    const dataDir = path.join(baseDir, 'data');
    const groupPath = path.join(dataDir, 'group.json');
    const membershipPath = path.join(dataDir, 'group-membership.json');
    const clearedGroup = clearFileIfExists(groupPath);
    const clearedMembership = clearFileIfExists(membershipPath);
    const cleared = [];
    if (clearedGroup)
        cleared.push('group.json');
    if (clearedMembership)
        cleared.push('group-membership.json');
    if (cleared.length === 0) {
        console.log('[psh] no group data found to clear');
    }
    else {
        console.log(`[psh] cleared: ${cleared.join(', ')}`);
    }
    console.log(`[psh] base directory: ${baseDir}`);
    if (options.restart) {
        if (typeof process.getuid === 'function' && process.getuid() !== 0) {
            console.warn('[psh] restart may require sudo');
        }
        console.log('[psh] restarting phoneshell service...');
        const result = spawnSync('systemctl', ['restart', 'phoneshell'], { stdio: 'inherit' });
        if (result.error || result.status !== 0) {
            const detail = result.error ? result.error.message : `exit code ${result.status}`;
            console.error(`[psh] failed to restart service (${detail}).`);
            console.error('[psh] try: sudo systemctl restart phoneshell');
        }
    }
    else {
        console.log('[psh] restart the service to re-initialize the group');
    }
}
function normalizeWsUrl(raw) {
    let url = raw.trim();
    if (!url)
        return '';
    if (!url.startsWith('ws://') && !url.startsWith('wss://')) {
        url = `ws://${url}`;
    }
    if (url.endsWith('/ws'))
        return `${url}/`;
    if (!url.endsWith('/ws/')) {
        url = url.replace(/\/+$/, '');
        url = `${url}/ws/`;
    }
    return url;
}
function wsToHttpBase(wsUrl) {
    let url = wsUrl;
    if (url.startsWith('wss://'))
        url = `https://${url.slice('wss://'.length)}`;
    else if (url.startsWith('ws://'))
        url = `http://${url.slice('ws://'.length)}`;
    url = url.replace(/\/ws\/?$/, '');
    return url.replace(/\/+$/, '');
}
function readGroupSecret(baseDir) {
    try {
        const groupPath = path.join(baseDir, 'data', 'group.json');
        if (!fs.existsSync(groupPath))
            return '';
        const json = JSON.parse(fs.readFileSync(groupPath, 'utf-8'));
        return (json.groupSecret || '').trim();
    }
    catch {
        return '';
    }
}
function httpGetJson(url) {
    return new Promise((resolve, reject) => {
        const client = url.startsWith('https://') ? https : http;
        const req = client.get(url, (res) => {
            let body = '';
            res.on('data', (chunk) => { body += chunk.toString(); });
            res.on('end', () => {
                try {
                    const parsed = JSON.parse(body);
                    resolve(parsed);
                }
                catch (err) {
                    reject(err);
                }
            });
        });
        req.on('error', reject);
    });
}
async function fetchStandaloneInfo(httpBase) {
    try {
        const url = `${httpBase}/api/standalone/info`;
        const info = await httpGetJson(url);
        if (info && info.deviceId)
            return info;
    }
    catch {
        // ignore
    }
    return null;
}
async function promptSelection(items, render, prompt) {
    if (items.length === 0)
        return null;
    for (let i = 0; i < items.length; i++) {
        console.log(`  ${i + 1}) ${render(items[i], i)}`);
    }
    const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
    try {
        while (true) {
            const answer = (await rl.question(prompt)).trim();
            if (answer.toLowerCase() === 'q' || answer.toLowerCase() === 'quit')
                return null;
            const idx = Number.parseInt(answer, 10);
            if (!Number.isNaN(idx) && idx >= 1 && idx <= items.length) {
                return items[idx - 1];
            }
            console.log(`Invalid selection. Enter 1-${items.length}, or 'q' to quit.`);
        }
    }
    finally {
        rl.close();
    }
}
async function runLocal(options) {
    const configPath = options.configPath || DEFAULT_CONFIG_PATH;
    const baseDir = resolveBaseDirectory(configPath);
    const port = resolvePort(configPath);
    const wsUrl = normalizeWsUrl(options.serverUrl || `ws://127.0.0.1:${port}/ws/`);
    if (!wsUrl) {
        console.error('Invalid server URL.');
        process.exit(1);
    }
    let token = (options.token || process.env.PHONESHELL_GROUP_SECRET || process.env.PHONESHELL_RELAY_TOKEN || '').trim();
    if (!token)
        token = readGroupSecret(baseDir);
    if (!token) {
        console.warn('[psh] warning: no auth token found, connection may fail');
    }
    const httpBase = wsToHttpBase(wsUrl);
    const standalone = await fetchStandaloneInfo(httpBase);
    const localDeviceId = standalone?.deviceId;
    const headers = token ? { Authorization: `Bearer ${token}` } : undefined;
    const ws = new WebSocket(wsUrl, headers ? { headers } : undefined);
    const bus = new EventEmitter();
    let activeDeviceId = options.deviceId || localDeviceId || '';
    let activeSessionId = '';
    ws.on('message', (data) => {
        const json = data.toString('utf-8');
        const msg = deserialize(json);
        if (!msg)
            return;
        bus.emit(msg.type, msg);
        bus.emit('*', msg);
    });
    ws.on('close', () => {
        process.stdout.write('\n[psh] connection closed\n');
        process.exit(0);
    });
    ws.on('error', (err) => {
        console.error(`[psh] ws error: ${err.message}`);
    });
    const opened = await waitForOpen(ws, 5000);
    if (!opened) {
        console.error('[psh] failed to connect');
        process.exit(1);
    }
    if (!activeDeviceId) {
        ws.send(serialize({ type: 'device.list.request' }));
        const deviceList = await waitForMessage(bus, 'device.list', 5000);
        if (!deviceList || deviceList.devices.length === 0) {
            console.error('[psh] no devices found');
            process.exit(1);
        }
        if (deviceList.devices.length === 1) {
            activeDeviceId = deviceList.devices[0].deviceId;
        }
        else {
            console.log('Available devices:');
            const selected = await promptSelection(deviceList.devices, (d) => `${d.displayName} (${d.os}) - ${d.deviceId}`, 'Select device: ');
            if (!selected)
                process.exit(0);
            activeDeviceId = selected.deviceId;
        }
    }
    const sessions = await fetchSessions(ws, bus, activeDeviceId);
    if (options.listOnly) {
        if (sessions.length === 0) {
            console.log('No active sessions.');
        }
        else {
            console.log('Active sessions:');
            sessions.forEach((s, i) => {
                console.log(`  ${i + 1}) ${formatSession(s)}`);
            });
        }
        process.exit(0);
    }
    const autoAttachAllowed = !options.attach && !options.shellId;
    if (autoAttachAllowed && sessions.length > 0) {
        if (sessions.length === 1) {
            activeSessionId = sessions[0].sessionId;
            console.log(`[psh] attaching to existing session ${formatSession(sessions[0])}`);
        }
        else {
            console.log('Select a session to attach:');
            const selected = await promptSelection(sessions, (s) => formatSession(s), 'Select session: ');
            if (!selected)
                process.exit(0);
            activeSessionId = selected.sessionId;
        }
    }
    else if (options.attach) {
        if (options.attachSessionId) {
            if (!sessions.some((s) => s.sessionId === options.attachSessionId)) {
                console.warn('[psh] warning: session not found in list, trying to attach anyway');
            }
            activeSessionId = options.attachSessionId;
        }
        else {
            if (sessions.length === 0) {
                console.error('[psh] no active sessions to attach');
                process.exit(1);
            }
            console.log('Select a session to attach:');
            const selected = await promptSelection(sessions, (s) => formatSession(s), 'Select session: ');
            if (!selected)
                process.exit(0);
            activeSessionId = selected.sessionId;
        }
    }
    else {
        // Create a new session
        const shellId = options.shellId || '';
        ws.send(serialize({ type: 'terminal.open', deviceId: activeDeviceId, shellId }));
        const opened = await waitForMessage(bus, 'terminal.opened', 5000);
        if (!opened) {
            console.error('[psh] terminal.open timed out');
            process.exit(1);
        }
        activeSessionId = opened.sessionId;
    }
    if (!activeSessionId) {
        console.error('[psh] no session selected');
        process.exit(1);
    }
    attachToSession(ws, bus, activeDeviceId, activeSessionId);
}
function formatSession(session) {
    const title = session.title || session.shellId || 'shell';
    return `${title} [${session.sessionId}]`;
}
async function fetchSessions(ws, bus, deviceId) {
    ws.send(serialize({ type: 'session.list.request', deviceId }));
    const list = await waitForMessage(bus, 'session.list', 5000);
    if (!list || list.deviceId !== deviceId)
        return [];
    return Array.isArray(list.sessions) ? list.sessions : [];
}
function attachToSession(ws, bus, deviceId, sessionId) {
    const resize = () => {
        const cols = process.stdout.columns || 120;
        const rows = process.stdout.rows || 30;
        ws.send(serialize({ type: 'terminal.resize', deviceId, sessionId, cols, rows }));
    };
    resize();
    process.stdout.on('resize', () => resize());
    bus.on('terminal.output', (msg) => {
        if (msg.deviceId !== deviceId || msg.sessionId !== sessionId)
            return;
        process.stdout.write(msg.data);
    });
    bus.on('terminal.closed', (msg) => {
        if (msg.deviceId !== deviceId || msg.sessionId !== sessionId)
            return;
        process.stdout.write('\n[psh] session closed\n');
        process.exit(0);
    });
    if (process.stdin.isTTY) {
        process.stdin.setRawMode(true);
    }
    process.stdin.resume();
    process.stdin.on('data', (chunk) => {
        ws.send(serialize({
            type: 'terminal.input',
            deviceId,
            sessionId,
            data: chunk.toString('utf-8'),
        }));
    });
}
function waitForOpen(ws, timeoutMs) {
    return new Promise((resolve, reject) => {
        const cleanup = () => {
            ws.off('open', onOpen);
            ws.off('error', onError);
            ws.off('close', onClose);
            clearTimeout(timer);
        };
        const onOpen = () => {
            cleanup();
            resolve(true);
        };
        const onClose = () => {
            cleanup();
            resolve(false);
        };
        const onError = (err) => {
            cleanup();
            reject(err);
        };
        const timer = setTimeout(() => {
            cleanup();
            resolve(false);
        }, timeoutMs);
        timer.unref?.();
        ws.on('open', onOpen);
        ws.on('error', onError);
        ws.on('close', onClose);
    });
}
function waitForMessage(bus, type, timeoutMs) {
    return new Promise((resolve) => {
        const handler = (msg) => {
            clearTimeout(timer);
            bus.off(type, handler);
            resolve(msg);
        };
        const timer = setTimeout(() => {
            bus.off(type, handler);
            resolve(null);
        }, timeoutMs);
        timer.unref?.();
        bus.on(type, handler);
    });
}
async function main() {
    const [cmd, ...rest] = process.argv.slice(2);
    if (!cmd || cmd === 'local') {
        const opts = parseLocalArgs(rest);
        if (rest.includes('--help') || rest.includes('-h')) {
            showHelp();
            return;
        }
        await runLocal(opts);
        return;
    }
    if (cmd === 'attach') {
        const opts = parseLocalArgs(rest);
        opts.attach = true;
        await runLocal(opts);
        return;
    }
    if (cmd === 'list') {
        const opts = parseLocalArgs(rest);
        opts.listOnly = true;
        await runLocal(opts);
        return;
    }
    if (cmd === 'install') {
        await runInstall(rest);
        return;
    }
    if (cmd === 'group') {
        const [sub, ...groupArgs] = rest;
        if (!sub || sub === 'help' || sub === '--help' || sub === '-h') {
            showGroupHelp();
            return;
        }
        if (sub === 'reset') {
            if (groupArgs.includes('--help') || groupArgs.includes('-h')) {
                showGroupHelp();
                return;
            }
            const opts = parseGroupResetArgs(groupArgs);
            runGroupReset(opts);
            return;
        }
        console.error(`Unknown group command: ${sub}`);
        showGroupHelp();
        process.exit(1);
    }
    if (cmd === 'help' || cmd === '--help' || cmd === '-h') {
        showHelp();
        return;
    }
    console.error(`Unknown command: ${cmd}`);
    showHelp();
    process.exit(1);
}
main().catch((err) => {
    console.error(`[psh] fatal: ${err.message}`);
    process.exit(1);
});
async function runInstall(args) {
    const root = getPackageRoot();
    const wrapper = path.join(root, 'deploy', 'phoneshell');
    const installer = path.join(root, 'deploy', 'install.sh');
    const command = 'bash';
    let target = wrapper;
    let argsWithInstall = ['install', ...args];
    if (!fs.existsSync(wrapper)) {
        target = installer;
        argsWithInstall = [...args];
    }
    if (!fs.existsSync(target)) {
        console.error('[psh] install script not found in package');
        process.exit(1);
    }
    const child = spawn(command, [target, ...argsWithInstall], { stdio: 'inherit' });
    const code = await new Promise((resolve) => {
        child.on('exit', (c) => resolve(c ?? 0));
    });
    process.exit(code);
}
function getPackageRoot() {
    const filePath = fileURLToPath(import.meta.url);
    return path.resolve(path.dirname(filePath), '..');
}
//# sourceMappingURL=cli.js.map