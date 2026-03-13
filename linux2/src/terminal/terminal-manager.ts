import fs from 'node:fs';
import path from 'node:path';
import { PtySession } from './pty-session.js';
import { OutputBuffer } from './output-buffer.js';
import type { SessionInfo } from '../protocol/messages.js';

export interface ShellInfo {
  id: string;
  displayName: string;
  path: string;
}

interface ManagedSession {
  pty: PtySession;
  outputBuffer: OutputBuffer;
  shellId: string;
  shellDisplayName: string;
}

export class TerminalManager {
  private readonly sessions = new Map<string, ManagedSession>();
  private sessionCounter = 0;
  private readonly defaultCols: number;
  private readonly defaultRows: number;

  /** Callback for terminal output */
  onOutput?: (sessionId: string, data: string) => void;
  /** Callback for terminal exit */
  onExit?: (sessionId: string) => void;

  constructor(defaultCols = 120, defaultRows = 30) {
    this.defaultCols = defaultCols;
    this.defaultRows = defaultRows;
  }

  /** Detect available shells from /etc/shells */
  getAvailableShells(): ShellInfo[] {
    const shells: ShellInfo[] = [];
    const seen = new Set<string>();
    try {
      const content = fs.readFileSync('/etc/shells', 'utf-8');
      for (const line of content.split('\n')) {
        const trimmed = line.trim();
        if (!trimmed || trimmed.startsWith('#')) continue;
        if (fs.existsSync(trimmed)) {
          const name = path.basename(trimmed);
          if (seen.has(name)) continue;
          seen.add(name);
          shells.push({ id: name, displayName: name, path: trimmed });
        }
      }
    } catch {
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

  getDefaultShell(): ShellInfo {
    const shells = this.getAvailableShells();
    // Prefer bash, then zsh, then first available
    const preferred = shells.find(s => s.id === 'bash') ||
                      shells.find(s => s.id === 'zsh') ||
                      shells[0];
    return preferred || { id: 'sh', displayName: 'sh', path: '/bin/sh' };
  }

  findShell(shellId: string): ShellInfo {
    if (!shellId) return this.getDefaultShell();
    const shells = this.getAvailableShells();
    return shells.find(s => s.id.toLowerCase() === shellId.toLowerCase()) ||
           this.getDefaultShell();
  }

  createSession(shellId: string): { sessionId: string; cols: number; rows: number } {
    const shell = this.findShell(shellId);
    const sessionId = `session-${++this.sessionCounter}`;
    const ptySession = new PtySession();
    const outputBuffer = new OutputBuffer();

    const managed: ManagedSession = {
      pty: ptySession,
      outputBuffer,
      shellId: shell.id,
      shellDisplayName: shell.displayName,
    };

    this.sessions.set(sessionId, managed);

    ptySession.on('data', (data: string) => {
      outputBuffer.append(data);
      this.onOutput?.(sessionId, data);
    });

    ptySession.on('exit', () => {
      this.onExit?.(sessionId);
    });

    ptySession.start(shell.path, this.defaultCols, this.defaultRows);

    return { sessionId, cols: this.defaultCols, rows: this.defaultRows };
  }

  writeInput(sessionId: string, data: string): void {
    this.sessions.get(sessionId)?.pty.write(data);
  }

  resize(sessionId: string, cols: number, rows: number): void {
    this.sessions.get(sessionId)?.pty.resize(cols, rows);
  }

  getSnapshot(sessionId: string): string {
    return this.sessions.get(sessionId)?.outputBuffer.getSnapshot() || '';
  }

  closeSession(sessionId: string): void {
    const managed = this.sessions.get(sessionId);
    if (managed) {
      managed.pty.kill();
      this.sessions.delete(sessionId);
    }
  }

  getSessionList(): SessionInfo[] {
    return Array.from(this.sessions.entries()).map(([sessionId, managed]) => ({
      sessionId,
      shellId: managed.shellId,
      title: managed.shellDisplayName,
    }));
  }

  disposeAll(): void {
    for (const [, managed] of this.sessions) {
      managed.pty.kill();
    }
    this.sessions.clear();
  }

  hasSession(sessionId: string): boolean {
    return this.sessions.has(sessionId);
  }
}
