import * as pty from 'node-pty';
import { EventEmitter } from 'node:events';
export class PtySession extends EventEmitter {
    ptyProcess = null;
    _exited = false;
    get exited() {
        return this._exited;
    }
    start(shell, cols, rows, env) {
        const mergedEnv = { ...process.env, ...env, TERM: 'xterm-256color' };
        this.ptyProcess = pty.spawn(shell, [], {
            name: 'xterm-256color',
            cols,
            rows,
            cwd: process.env.HOME || '/',
            env: mergedEnv,
        });
        this.ptyProcess.onData((data) => {
            this.emit('data', data);
        });
        this.ptyProcess.onExit(({ exitCode, signal }) => {
            this._exited = true;
            this.emit('exit', exitCode, signal);
        });
    }
    write(data) {
        if (this.ptyProcess && !this._exited) {
            this.ptyProcess.write(data);
        }
    }
    resize(cols, rows) {
        if (this.ptyProcess && !this._exited) {
            try {
                this.ptyProcess.resize(cols, rows);
            }
            catch {
                // ignore resize errors on dead process
            }
        }
    }
    kill() {
        if (this.ptyProcess && !this._exited) {
            try {
                this.ptyProcess.kill();
            }
            catch {
                // already dead
            }
        }
        this.ptyProcess = null;
        this.removeAllListeners();
    }
}
//# sourceMappingURL=pty-session.js.map