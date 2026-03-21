export interface TerminalHistoryPage {
    data: string;
    nextBeforeSeq: number;
    hasMore: boolean;
}
export declare class TerminalHistoryStore {
    private readonly historyDirectory;
    private readonly maxChars;
    private readonly buffers;
    private readonly pendingTrimPaths;
    private trimTimer;
    constructor(baseDirectory: string, maxChars?: number);
    append(deviceId: string, sessionId: string, data: string): void;
    getPage(deviceId: string, sessionId: string, beforeSeq: number, maxChars: number): TerminalHistoryPage;
    readAll(deviceId: string, sessionId: string): string;
    removeSession(deviceId: string, sessionId: string): void;
    removeDevice(deviceId: string): void;
    private trimIfNeeded;
    private ensureTrimTimer;
    private getOrCreateBuffer;
    private flushSession;
    private flushBuffer;
    private clearBuffer;
    private tryReadRecordBackward;
    private tryReadInt32;
    private writeStringRecords;
    private writeRecord;
    private getSessionPath;
    private getDeviceDirectory;
    private sanitizeKey;
}
