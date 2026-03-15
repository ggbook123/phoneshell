import type { SessionInfo } from '../protocol/messages.js';
export interface ShellInfo {
    id: string;
    displayName: string;
    path: string;
}
export declare class TerminalManager {
    private readonly sessions;
    private readonly defaultCols;
    private readonly defaultRows;
    /** Callback for terminal output */
    onOutput?: (sessionId: string, data: string) => void;
    /** Callback for terminal exit */
    onExit?: (sessionId: string) => void;
    constructor(defaultCols?: number, defaultRows?: number);
    /** Detect available shells from /etc/shells */
    getAvailableShells(): ShellInfo[];
    getDefaultShell(): ShellInfo;
    findShell(shellId: string): ShellInfo;
    createSession(shellId: string): {
        sessionId: string;
        cols: number;
        rows: number;
    };
    writeInput(sessionId: string, data: string): void;
    resize(sessionId: string, cols: number, rows: number): void;
    getSnapshot(sessionId: string): string;
    closeSession(sessionId: string): void;
    getSessionList(): SessionInfo[];
    disposeAll(): void;
    hasSession(sessionId: string): boolean;
}
