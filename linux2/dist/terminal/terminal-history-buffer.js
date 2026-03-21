const DEFAULT_PAGE_CHARS = 20000;
export class TerminalHistoryBuffer {
    chunks = [];
    totalChars = 0;
    nextSeq = 1;
    maxChars;
    constructor(maxChars = 5000000) {
        this.maxChars = Math.max(0, maxChars);
    }
    append(data) {
        if (!data)
            return;
        this.chunks.push({ seq: this.nextSeq++, data });
        this.totalChars += data.length;
        this.trimIfNeeded();
    }
    getPage(beforeSeq, maxChars) {
        if (this.chunks.length === 0) {
            return { data: '', nextBeforeSeq: 0, hasMore: false };
        }
        const limit = maxChars > 0 ? maxChars : DEFAULT_PAGE_CHARS;
        let idx = -1;
        if (beforeSeq <= 0) {
            idx = this.chunks.length - 1;
        }
        else {
            for (let i = this.chunks.length - 1; i >= 0; i--) {
                if (this.chunks[i].seq < beforeSeq) {
                    idx = i;
                    break;
                }
            }
        }
        if (idx < 0) {
            return { data: '', nextBeforeSeq: 0, hasMore: false };
        }
        const parts = [];
        let used = 0;
        let oldestSeq = this.chunks[idx].seq;
        for (let i = idx; i >= 0; i--) {
            const chunk = this.chunks[i];
            const data = chunk.data;
            if (!data) {
                oldestSeq = chunk.seq;
                continue;
            }
            if (used + data.length > limit) {
                if (used === 0) {
                    parts.push(data.slice(data.length - limit));
                    oldestSeq = chunk.seq;
                    used = limit;
                }
                break;
            }
            parts.push(data);
            used += data.length;
            oldestSeq = chunk.seq;
            if (used >= limit)
                break;
        }
        if (parts.length === 0) {
            return { data: '', nextBeforeSeq: 0, hasMore: false };
        }
        parts.reverse();
        const payload = parts.join('');
        const hasMore = this.chunks[0].seq < oldestSeq;
        const nextBeforeSeq = hasMore ? oldestSeq : 0;
        return { data: payload, nextBeforeSeq, hasMore };
    }
    clear() {
        this.chunks.length = 0;
        this.totalChars = 0;
        this.nextSeq = 1;
    }
    trimIfNeeded() {
        if (this.maxChars <= 0)
            return;
        while (this.totalChars > this.maxChars && this.chunks.length > 0) {
            const first = this.chunks.shift();
            if (first)
                this.totalChars -= first.data.length;
        }
    }
}
//# sourceMappingURL=terminal-history-buffer.js.map