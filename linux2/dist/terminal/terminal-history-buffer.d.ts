export interface TerminalHistoryPage {
    data: string;
    nextBeforeSeq: number;
    hasMore: boolean;
}
export declare class TerminalHistoryBuffer {
    private readonly chunks;
    private totalChars;
    private nextSeq;
    private readonly maxChars;
    constructor(maxChars?: number);
    append(data: string): void;
    getPage(beforeSeq: number, maxChars: number): TerminalHistoryPage;
    clear(): void;
    private trimIfNeeded;
}
