/**
 * Ring buffer that keeps the most recent raw terminal output (with ANSI codes)
 * so clients re-subscribing to a session can receive a snapshot.
 */
export declare class OutputBuffer {
    private readonly chunks;
    private totalLength;
    private readonly maxLength;
    constructor(maxLength?: number);
    append(data: string): void;
    getSnapshot(): string;
    clear(): void;
}
