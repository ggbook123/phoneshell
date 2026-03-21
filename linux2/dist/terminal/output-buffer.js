/**
 * Ring buffer that keeps the most recent raw terminal output (with ANSI codes)
 * so clients re-subscribing to a session can receive a snapshot.
 */
export class OutputBuffer {
    chunks = [];
    totalLength = 0;
    maxLength;
    constructor(maxLength = 65536) {
        this.maxLength = maxLength;
    }
    append(data) {
        if (!data)
            return;
        this.chunks.push(data);
        this.totalLength += data.length;
        while (this.totalLength > this.maxLength && this.chunks.length > 1) {
            const old = this.chunks.shift();
            this.totalLength -= old.length;
        }
    }
    getSnapshot() {
        return this.chunks.join('');
    }
    clear() {
        this.chunks.length = 0;
        this.totalLength = 0;
    }
}
//# sourceMappingURL=output-buffer.js.map