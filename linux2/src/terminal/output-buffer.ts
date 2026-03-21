/**
 * Ring buffer that keeps the most recent raw terminal output (with ANSI codes)
 * so clients re-subscribing to a session can receive a snapshot.
 */
export class OutputBuffer {
  private readonly chunks: string[] = [];
  private totalLength = 0;
  private readonly maxLength: number;

  constructor(maxLength = 65536) {
    this.maxLength = maxLength;
  }

  append(data: string): void {
    if (!data) return;
    this.chunks.push(data);
    this.totalLength += data.length;
    while (this.totalLength > this.maxLength && this.chunks.length > 1) {
      const old = this.chunks.shift()!;
      this.totalLength -= old.length;
    }
  }

  getSnapshot(): string {
    return this.chunks.join('');
  }

  clear(): void {
    this.chunks.length = 0;
    this.totalLength = 0;
  }
}
