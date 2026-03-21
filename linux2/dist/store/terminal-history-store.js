import fs from 'node:fs';
import path from 'node:path';
const DEFAULT_PAGE_CHARS = 20000;
const DEFAULT_MAX_CHARS = 5000000;
const MAX_RECORD_BYTES = 8 * 1024 * 1024;
const FLUSH_INTERVAL_MS = 50;
const FLUSH_MAX_BYTES = 256 * 1024;
const TRIM_INTERVAL_MS = 30000;
export class TerminalHistoryStore {
    historyDirectory;
    maxChars;
    buffers = new Map();
    pendingTrimPaths = new Set();
    trimTimer = null;
    constructor(baseDirectory, maxChars = DEFAULT_MAX_CHARS) {
        const base = baseDirectory && baseDirectory.trim().length > 0
            ? baseDirectory.trim()
            : process.cwd();
        this.historyDirectory = path.join(base, 'data', 'history');
        this.maxChars = Math.max(0, maxChars);
        fs.mkdirSync(this.historyDirectory, { recursive: true });
    }
    append(deviceId, sessionId, data) {
        if (!deviceId || !sessionId || !data)
            return;
        const filePath = this.getSessionPath(deviceId, sessionId);
        const buffer = this.getOrCreateBuffer(filePath);
        buffer.chunks.push(data);
        buffer.byteLength += Buffer.byteLength(data, 'utf8');
        if (buffer.byteLength >= FLUSH_MAX_BYTES) {
            this.flushBuffer(filePath);
        }
        else if (!buffer.flushTimer) {
            buffer.flushTimer = setTimeout(() => {
                buffer.flushTimer = null;
                this.flushBuffer(filePath);
            }, FLUSH_INTERVAL_MS);
            buffer.flushTimer.unref?.();
        }
        this.pendingTrimPaths.add(filePath);
        this.ensureTrimTimer();
    }
    getPage(deviceId, sessionId, beforeSeq, maxChars) {
        if (!deviceId || !sessionId)
            return { data: '', nextBeforeSeq: 0, hasMore: false };
        try {
            this.flushSession(deviceId, sessionId);
            const filePath = this.getSessionPath(deviceId, sessionId);
            if (!fs.existsSync(filePath))
                return { data: '', nextBeforeSeq: 0, hasMore: false };
            let pageLimit = maxChars > 0 ? maxChars : DEFAULT_PAGE_CHARS;
            if (this.maxChars > 0)
                pageLimit = Math.min(pageLimit, this.maxChars);
            const fd = fs.openSync(filePath, 'r');
            try {
                const stat = fs.fstatSync(fd);
                if (stat.size === 0)
                    return { data: '', nextBeforeSeq: 0, hasMore: false };
                let currentEnd = beforeSeq > 0 && beforeSeq <= stat.size ? beforeSeq : stat.size;
                const parts = [];
                let used = 0;
                let earliestStart = currentEnd;
                while (currentEnd > 0 && used < pageLimit) {
                    const record = this.tryReadRecordBackward(fd, currentEnd);
                    if (!record) {
                        currentEnd -= 1;
                        continue;
                    }
                    let data = record.data;
                    if (used + data.length > pageLimit) {
                        if (used === 0) {
                            const take = pageLimit;
                            if (data.length > take)
                                data = data.slice(data.length - take);
                            parts.push(data);
                            used += data.length;
                            earliestStart = record.recordStart;
                        }
                        break;
                    }
                    parts.push(data);
                    used += data.length;
                    earliestStart = record.recordStart;
                    currentEnd = record.recordStart;
                }
                if (parts.length === 0)
                    return { data: '', nextBeforeSeq: 0, hasMore: false };
                parts.reverse();
                const payload = parts.join('');
                const hasMore = earliestStart > 0;
                const nextBeforeSeq = hasMore ? earliestStart : 0;
                return { data: payload, nextBeforeSeq, hasMore };
            }
            finally {
                fs.closeSync(fd);
            }
        }
        catch {
            return { data: '', nextBeforeSeq: 0, hasMore: false };
        }
    }
    readAll(deviceId, sessionId) {
        if (!deviceId || !sessionId)
            return '';
        try {
            this.flushSession(deviceId, sessionId);
            const filePath = this.getSessionPath(deviceId, sessionId);
            if (!fs.existsSync(filePath))
                return '';
            const fd = fs.openSync(filePath, 'r');
            try {
                const stat = fs.fstatSync(fd);
                if (stat.size === 0)
                    return '';
                const parts = [];
                let position = 0;
                while (position + 8 <= stat.size) {
                    const len = this.tryReadInt32(fd, position);
                    if (len === null || len <= 0 || len > MAX_RECORD_BYTES)
                        break;
                    if (position + 4 + len + 4 > stat.size)
                        break;
                    const buffer = Buffer.alloc(len);
                    const read = fs.readSync(fd, buffer, 0, len, position + 4);
                    if (read !== len)
                        break;
                    const suffix = this.tryReadInt32(fd, position + 4 + len);
                    if (suffix !== len)
                        break;
                    parts.push(buffer.toString('utf8'));
                    position += 4 + len + 4;
                }
                return parts.join('');
            }
            finally {
                fs.closeSync(fd);
            }
        }
        catch {
            return '';
        }
    }
    removeSession(deviceId, sessionId) {
        if (!deviceId || !sessionId)
            return;
        try {
            const filePath = this.getSessionPath(deviceId, sessionId);
            this.clearBuffer(filePath);
            if (fs.existsSync(filePath))
                fs.unlinkSync(filePath);
        }
        catch {
            // Best effort cleanup.
        }
    }
    removeDevice(deviceId) {
        if (!deviceId)
            return;
        try {
            const deviceDir = this.getDeviceDirectory(deviceId);
            const prefix = deviceDir.endsWith(path.sep) ? deviceDir : deviceDir + path.sep;
            for (const filePath of this.buffers.keys()) {
                if (filePath.startsWith(prefix)) {
                    this.clearBuffer(filePath);
                }
            }
            if (this.pendingTrimPaths.size > 0) {
                for (const filePath of Array.from(this.pendingTrimPaths)) {
                    if (filePath.startsWith(prefix)) {
                        this.pendingTrimPaths.delete(filePath);
                    }
                }
            }
            if (fs.existsSync(deviceDir)) {
                fs.rmSync(deviceDir, { recursive: true, force: true });
            }
        }
        catch {
            // Best effort cleanup.
        }
    }
    trimIfNeeded(filePath) {
        if (this.maxChars <= 0)
            return;
        try {
            const stat = fs.statSync(filePath);
            if (stat.size <= this.maxChars)
                return;
            const fd = fs.openSync(filePath, 'r');
            try {
                if (stat.size === 0)
                    return;
                const parts = [];
                let used = 0;
                let currentEnd = stat.size;
                let earliestStart = currentEnd;
                while (currentEnd > 0 && used < this.maxChars) {
                    const record = this.tryReadRecordBackward(fd, currentEnd);
                    if (!record) {
                        currentEnd -= 1;
                        continue;
                    }
                    let data = record.data;
                    if (used + data.length > this.maxChars) {
                        if (used === 0) {
                            const take = this.maxChars;
                            if (data.length > take)
                                data = data.slice(data.length - take);
                            parts.push(data);
                            used += data.length;
                            earliestStart = record.recordStart;
                        }
                        break;
                    }
                    parts.push(data);
                    used += data.length;
                    earliestStart = record.recordStart;
                    currentEnd = record.recordStart;
                }
                if (parts.length === 0 || earliestStart <= 0)
                    return;
                parts.reverse();
                const tempPath = filePath + '.tmp';
                const outFd = fs.openSync(tempPath, 'w');
                try {
                    for (const part of parts) {
                        this.writeStringRecords(outFd, part);
                    }
                }
                finally {
                    fs.closeSync(outFd);
                }
                fs.renameSync(tempPath, filePath);
            }
            finally {
                fs.closeSync(fd);
            }
        }
        catch {
            // Best effort trim.
        }
    }
    ensureTrimTimer() {
        if (this.trimTimer)
            return;
        this.trimTimer = setInterval(() => {
            if (this.pendingTrimPaths.size === 0)
                return;
            const targets = Array.from(this.pendingTrimPaths);
            this.pendingTrimPaths.clear();
            for (const filePath of targets) {
                this.trimIfNeeded(filePath);
            }
        }, TRIM_INTERVAL_MS);
        this.trimTimer.unref?.();
    }
    getOrCreateBuffer(filePath) {
        const existing = this.buffers.get(filePath);
        if (existing)
            return existing;
        const buffer = { chunks: [], byteLength: 0, flushTimer: null };
        this.buffers.set(filePath, buffer);
        return buffer;
    }
    flushSession(deviceId, sessionId) {
        if (!deviceId || !sessionId)
            return;
        const filePath = this.getSessionPath(deviceId, sessionId);
        this.flushBuffer(filePath);
    }
    flushBuffer(filePath) {
        const buffer = this.buffers.get(filePath);
        if (!buffer || buffer.chunks.length === 0)
            return;
        if (buffer.flushTimer) {
            clearTimeout(buffer.flushTimer);
            buffer.flushTimer = null;
        }
        const payload = buffer.chunks.join('');
        buffer.chunks = [];
        buffer.byteLength = 0;
        if (!payload)
            return;
        try {
            fs.mkdirSync(path.dirname(filePath), { recursive: true });
            const fd = fs.openSync(filePath, 'a');
            try {
                this.writeStringRecords(fd, payload);
            }
            finally {
                fs.closeSync(fd);
            }
        }
        catch {
            // Best effort persistence.
        }
    }
    clearBuffer(filePath) {
        const buffer = this.buffers.get(filePath);
        if (!buffer)
            return;
        if (buffer.flushTimer) {
            clearTimeout(buffer.flushTimer);
            buffer.flushTimer = null;
        }
        this.buffers.delete(filePath);
        this.pendingTrimPaths.delete(filePath);
    }
    tryReadRecordBackward(fd, recordEnd) {
        if (recordEnd < 8)
            return null;
        const len = this.tryReadInt32(fd, recordEnd - 4);
        if (len === null || len <= 0 || len > MAX_RECORD_BYTES)
            return null;
        const start = recordEnd - 4 - len - 4;
        if (start < 0)
            return null;
        const prefix = this.tryReadInt32(fd, start);
        if (prefix !== len)
            return null;
        if (start + 4 + len > recordEnd)
            return null;
        const buffer = Buffer.alloc(len);
        const read = fs.readSync(fd, buffer, 0, len, start + 4);
        if (read !== len)
            return null;
        return { data: buffer.toString('utf8'), recordStart: start };
    }
    tryReadInt32(fd, position) {
        if (position < 0)
            return null;
        const buffer = Buffer.alloc(4);
        const read = fs.readSync(fd, buffer, 0, 4, position);
        if (read !== 4)
            return null;
        return buffer.readInt32LE(0);
    }
    writeStringRecords(fd, data) {
        if (!data)
            return;
        const maxCharsPerRecord = Math.max(1, Math.floor(MAX_RECORD_BYTES / 4));
        for (let offset = 0; offset < data.length; offset += maxCharsPerRecord) {
            const slice = data.slice(offset, offset + maxCharsPerRecord);
            if (!slice)
                continue;
            const bytes = Buffer.from(slice, 'utf8');
            this.writeRecord(fd, bytes);
        }
    }
    writeRecord(fd, bytes) {
        if (bytes.length === 0)
            return;
        const lenBuf = Buffer.alloc(4);
        lenBuf.writeInt32LE(bytes.length, 0);
        fs.writeSync(fd, lenBuf, 0, 4);
        fs.writeSync(fd, bytes, 0, bytes.length);
        fs.writeSync(fd, lenBuf, 0, 4);
    }
    getSessionPath(deviceId, sessionId) {
        const dir = this.getDeviceDirectory(deviceId);
        const session = this.sanitizeKey(sessionId);
        return path.join(dir, `${session}.vth`);
    }
    getDeviceDirectory(deviceId) {
        const device = this.sanitizeKey(deviceId);
        return path.join(this.historyDirectory, device);
    }
    sanitizeKey(value) {
        if (!value || !value.trim())
            return 'unknown';
        let out = '';
        for (const ch of value) {
            if ((ch >= 'a' && ch <= 'z') ||
                (ch >= 'A' && ch <= 'Z') ||
                (ch >= '0' && ch <= '9') ||
                ch === '-' || ch === '_' || ch === '.') {
                out += ch;
            }
            else {
                out += '_';
            }
        }
        return out;
    }
}
//# sourceMappingURL=terminal-history-store.js.map