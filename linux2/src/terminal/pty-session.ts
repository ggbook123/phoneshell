import * as pty from 'node-pty';
import { EventEmitter } from 'node:events';

export interface PtySessionEvents {
  data: (data: string) => void;
  exit: (code: number, signal: number) => void;
}

export class PtySession extends EventEmitter {
  private ptyProcess: pty.IPty | null = null;
  private _exited = false;

  get exited(): boolean {
    return this._exited;
  }

  start(shell: string, cols: number, rows: number, env?: Record<string, string>): void {
    const mergedEnv = { ...process.env, ...env, TERM: 'xterm-256color' } as Record<string, string>;

    this.ptyProcess = pty.spawn(shell, [], {
      name: 'xterm-256color',
      cols,
      rows,
      cwd: process.env.HOME || '/',
      env: mergedEnv,
    });

    this.ptyProcess.onData((data: string) => {
      this.emit('data', data);
    });

    this.ptyProcess.onExit(({ exitCode, signal }) => {
      this._exited = true;
      this.emit('exit', exitCode, signal);
    });
  }

  write(data: string): void {
    if (this.ptyProcess && !this._exited) {
      this.ptyProcess.write(data);
    }
  }

  resize(cols: number, rows: number): void {
    if (this.ptyProcess && !this._exited) {
      try {
        this.ptyProcess.resize(cols, rows);
      } catch {
        // ignore resize errors on dead process
      }
    }
  }

  kill(): void {
    if (this.ptyProcess && !this._exited) {
      try {
        this.ptyProcess.kill();
      } catch {
        // already dead
      }
    }
    this.ptyProcess = null;
    this.removeAllListeners();
  }
}
