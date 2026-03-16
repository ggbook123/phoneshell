namespace PhoneShell.Core.Services;

public sealed class TerminalHistoryBuffer
{
    private sealed record Chunk(long Seq, string Data);

    private readonly LinkedList<Chunk> _chunks = new();
    private readonly object _lock = new();
    private readonly long _maxChars;
    private long _nextSeq = 1;
    private long _totalChars;
    private const int DefaultPageChars = 20_000;

    public TerminalHistoryBuffer(int maxChars = 5_000_000)
    {
        _maxChars = Math.Max(0, maxChars);
    }

    public void Append(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        lock (_lock)
        {
            _chunks.AddLast(new Chunk(_nextSeq++, data));
            _totalChars += data.Length;
            TrimIfNeeded();
        }
    }

    public TerminalHistoryPage GetPage(long beforeSeq, int maxChars)
    {
        lock (_lock)
        {
            if (_chunks.Count == 0)
                return TerminalHistoryPage.Empty;

            var pageLimit = maxChars <= 0 ? DefaultPageChars : maxChars;
            if (_maxChars > 0)
                pageLimit = (int)Math.Min(pageLimit, _maxChars);
            LinkedListNode<Chunk>? node = null;

            if (beforeSeq <= 0)
            {
                node = _chunks.Last;
            }
            else
            {
                for (var cur = _chunks.Last; cur is not null; cur = cur.Previous)
                {
                    if (cur.Value.Seq < beforeSeq)
                    {
                        node = cur;
                        break;
                    }
                }
            }

            if (node is null)
                return TerminalHistoryPage.Empty;

            var parts = new List<string>();
            var used = 0;
            var oldestSeq = node.Value.Seq;

            for (var cur = node; cur is not null; cur = cur.Previous)
            {
                var data = cur.Value.Data;
                if (string.IsNullOrEmpty(data))
                {
                    oldestSeq = cur.Value.Seq;
                    continue;
                }

                if (used + data.Length > pageLimit)
                {
                    if (used == 0)
                    {
                        // Return the tail of a large chunk.
                        parts.Add(data[^pageLimit..]);
                        oldestSeq = cur.Value.Seq;
                        used = pageLimit;
                    }
                    break;
                }

                parts.Add(data);
                used += data.Length;
                oldestSeq = cur.Value.Seq;

                if (used >= pageLimit)
                    break;
            }

            if (parts.Count == 0)
                return TerminalHistoryPage.Empty;

            parts.Reverse();
            var payload = string.Concat(parts);
            var hasMore = _chunks.First!.Value.Seq < oldestSeq;
            var nextBeforeSeq = hasMore ? oldestSeq : 0;
            return new TerminalHistoryPage(payload, nextBeforeSeq, hasMore);
        }
    }

    private void TrimIfNeeded()
    {
        if (_maxChars <= 0)
            return;

        while (_totalChars > _maxChars && _chunks.Count > 0)
        {
            var first = _chunks.First!;
            _chunks.RemoveFirst();
            _totalChars -= first.Value.Data.Length;
        }
    }
}

public readonly record struct TerminalHistoryPage(string Data, long NextBeforeSeq, bool HasMore)
{
    public static TerminalHistoryPage Empty => new(string.Empty, 0, false);
}
