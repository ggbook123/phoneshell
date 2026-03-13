import fs from 'node:fs';
import path from 'node:path';
import { PtySession } from './pty-session.js';
import { OutputBuffer } from './output-buffer.js';
export class TerminalManager {
    sessions = new Map();
    sessionCounter = 0;
    defaultCols;
    defaultRows;
    /** Callback for terminal output */
    onOutput;
    /** Callback for terminal exit */
    onExit;
    constructor(defaultCols = 120, defaultRows = 30) {
        this.defaultCols = defaultCols;
        this.defaultRows = defaultRows;
    }
    /** Detect available shells from /etc/shells */
    getAvailableShells() {
        const shells = [];
        const seen = new Set();
        try {
            const content = fs.readFileSync('/etc/shells', 'utf-8');
            for (const line of content.split('\n')) {
                const trimmed = line.trim();
                if (!trimmed || trimmed.startsWith('#'))
                    continue;
                if (fs.existsSync(trimmed)) {
                    const name = path.basename(trimmed);
                    if (seen.has(name))
                        continue;
                    seen.add(name);
                    shells.push({ id: name, displayName: name, path: trimmed });
                }
            }
        }
        catch {
            // fallback
        }
        if (shells.length === 0) {
            // Fallback: try common shells
            for (const s of ['/bin/bash', '/bin/sh', '/bin/zsh']) {
                if (fs.existsSync(s)) {
                    const name = path.basename(s);
                    shells.push({ id: name, displayName: name, path: s });
                }
            }
        }
        return shells;
    }
    getDefaultShell() {
        const shells = this.getAvailableShells();
        // Prefer bash, then zsh, then first available
        const preferred = shells.find(s => s.id === 'bash') ||
            shells.find(s => s.id === 'zsh') ||
            shells[0];
        return preferred || { id: 'sh', displayName: 'sh', path: '/bin/sh' };
    }
    findShell(shellId) {
        if (!shellId)
            return this.getDefaultShell();
        const shells = this.getAvailableShells();
        return shells.find(s => s.id.toLowerCase() === shellId.toLowerCase()) ||
            this.getDefaultShell();
    }
    createSession(shellId) {
        const shell = this.findShell(shellId);
        const sessionId = `session-${++this.sessionCounter}`;
        const ptySession = new PtySession();
        const outputBuffer = new OutputBuffer();
        const managed = {
            pty: ptySession,
            outputBuffer,
            shellId: shell.id,
            shellDisplayName: shell.displayName,
        };
        this.sessions.set(sessionId, managed);
        ptySession.on('data', (data) => {
            outputBuffer.append(data);
            this.onOutput?.(sessionId, data);
        });
        ptySession.on('exit', () => {
            this.onExit?.(sessionId);
        });
        ptySession.start(shell.path, this.defaultCols, this.defaultRows);
        return { sessionId, cols: this.defaultCols, rows: this.defaultRows };
    }
    writeInput(sessionId, data) {
        this.sessions.get(sessionId)?.pty.write(data);
    }
    resize(sessionId, cols, rows) {
        this.sessions.get(sessionId)?.pty.resize(cols, rows);
    }
    getSnapshot(sessionId) {
        return this.sessions.get(sessionId)?.outputBuffer.getSnapshot() || '';
    }
    closeSession(sessionId) {
        const managed = this.sessions.get(sessionId);
        if (managed) {
            managed.pty.kill();
            this.sessions.delete(sessionId);
        }
    }
    getSessionList() {
        return Array.from(this.sessions.entries()).map(([sessionId, managed]) => ({
            sessionId,
            shellId: managed.shellId,
            title: managed.shellDisplayName,
        }));
    }
    disposeAll() {
        for (const [, managed] of this.sessions) {
            managed.pty.kill();
        }
        this.sessions.clear();
    }
    hasSession(sessionId) {
        return this.sessions.has(sessionId);
    }
}
//# sourceMappingURL=terminal-manager.js.map