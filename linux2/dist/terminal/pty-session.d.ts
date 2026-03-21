import { EventEmitter } from 'node:events';
export interface PtySessionEvents {
    data: (data: string) => void;
    exit: (code: number, signal: number) => void;
}
export declare class PtySession extends EventEmitter {
    private ptyProcess;
    private _exited;
    get exited(): boolean;
    start(shell: string, cols: number, rows: number, env?: Record<string, string>): void;
    write(data: string): void;
    resize(cols: number, rows: number): void;
    kill(): void;
}
