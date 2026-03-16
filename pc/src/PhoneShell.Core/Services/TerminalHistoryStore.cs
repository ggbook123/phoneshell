using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace PhoneShell.Core.Services;

/// <summary>
/// Persistent terminal history store (raw VT/ANSI output) backed by disk.
/// Record format: [int32 length][payload bytes][int32 length].
/// Uses the end offset of a record as paging cursor (beforeSeq).
/// </summary>
public sealed class TerminalHistoryStore : IDisposable
{
    private const int DefaultPageChars = 20_000;
    private const int DefaultMaxChars = 5_000_000;
    private const int MaxRecordBytes = 8 * 1024 * 1024;

    private readonly string _historyDirectory;
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);
    private readonly Encoding _encoding = new UTF8Encoding(false);
    private readonly int _maxChars;

    public TerminalHistoryStore(string baseDirectory, int maxChars = DefaultMaxChars)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            baseDirectory = AppContext.BaseDirectory;

        _historyDirectory = Path.Combine(baseDirectory, "data", "history");
        Directory.CreateDirectory(_historyDirectory);
        _maxChars = Math.Max(0, maxChars);
    }

    public void Append(string deviceId, string sessionId, string data)
    {
        if (string.IsNullOrWhiteSpace(deviceId) ||
            string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrEmpty(data))
            return;

        try
        {
            var path = GetSessionPath(deviceId, sessionId);
            var sync = GetLock(deviceId, sessionId);
            lock (sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    fs.Seek(0, SeekOrigin.End);
                    WriteStringRecords(fs, data);
                    fs.Flush();
                }
                TrimIfNeeded(path);
            }
        }
        catch
        {
            // Best effort persistence.
        }
    }

    public TerminalHistoryPage GetPage(string deviceId, string sessionId, long beforeSeq, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(sessionId))
            return TerminalHistoryPage.Empty;

        try
        {
            var path = GetSessionPath(deviceId, sessionId);
            if (!File.Exists(path))
                return TerminalHistoryPage.Empty;

            var pageLimit = maxChars <= 0 ? DefaultPageChars : maxChars;
            if (_maxChars > 0)
                pageLimit = Math.Min(pageLimit, _maxChars);
            var sync = GetLock(deviceId, sessionId);

            lock (sync)
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length == 0)
                    return TerminalHistoryPage.Empty;

                var currentEnd = beforeSeq <= 0 || beforeSeq > fs.Length
                    ? fs.Length
                    : beforeSeq;

                var parts = new List<string>();
                var used = 0;
                var earliestStart = currentEnd;

                while (currentEnd > 0 && used < pageLimit)
                {
                    if (!TryReadRecordBackward(fs, currentEnd, out var data, out var recordStart))
                    {
                        currentEnd--;
                        continue;
                    }

                    if (used + data.Length > pageLimit)
                    {
                        if (used == 0)
                        {
                            var take = pageLimit;
                            if (data.Length > take)
                                data = data[^take..];
                            parts.Add(data);
                            used += data.Length;
                            earliestStart = recordStart;
                        }
                        break;
                    }

                    parts.Add(data);
                    used += data.Length;
                    earliestStart = recordStart;
                    currentEnd = recordStart;
                }

                if (parts.Count == 0)
                    return TerminalHistoryPage.Empty;

                parts.Reverse();
                var payload = string.Concat(parts);
                var hasMore = earliestStart > 0;
                var nextBeforeSeq = hasMore ? earliestStart : 0;
                return new TerminalHistoryPage(payload, nextBeforeSeq, hasMore);
            }
        }
        catch
        {
            return TerminalHistoryPage.Empty;
        }
    }

    public string ReadAll(string deviceId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(sessionId))
            return string.Empty;

        try
        {
            var path = GetSessionPath(deviceId, sessionId);
            if (!File.Exists(path))
                return string.Empty;

            var sync = GetLock(deviceId, sessionId);
            lock (sync)
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length == 0)
                    return string.Empty;

                var sb = new StringBuilder();
                while (fs.Position + 8 <= fs.Length)
                {
                    if (!TryReadInt32(fs, fs.Position, out var len))
                        break;

                    if (len <= 0 || len > MaxRecordBytes)
                        break;

                    if (fs.Position + len + 4 > fs.Length)
                        break;

                    var buffer = new byte[len];
                    var read = fs.Read(buffer, 0, len);
                    if (read != len)
                        break;

                    if (!TryReadInt32(fs, fs.Position, out var suffix) || suffix != len)
                        break;

                    sb.Append(_encoding.GetString(buffer));
                }

                return sb.ToString();
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    public void RemoveSession(string deviceId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(sessionId))
            return;

        try
        {
            var path = GetSessionPath(deviceId, sessionId);
            var sync = GetLock(deviceId, sessionId);
            lock (sync)
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    public void RemoveDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return;

        try
        {
            var dir = GetDeviceDirectory(deviceId);
            if (!Directory.Exists(dir))
                return;

            Directory.Delete(dir, true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    public void Dispose()
    {
        _locks.Clear();
    }

    private void TrimIfNeeded(string path)
    {
        if (_maxChars <= 0)
            return;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= _maxChars)
                return;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length == 0)
                return;

            var parts = new List<string>();
            var used = 0;
            var currentEnd = fs.Length;
            var earliestStart = currentEnd;

            while (currentEnd > 0 && used < _maxChars)
            {
                if (!TryReadRecordBackward(fs, currentEnd, out var data, out var recordStart))
                {
                    currentEnd--;
                    continue;
                }

                if (used + data.Length > _maxChars)
                {
                    if (used == 0)
                    {
                        var take = _maxChars;
                        if (data.Length > take)
                            data = data[^take..];
                        parts.Add(data);
                        used += data.Length;
                        earliestStart = recordStart;
                    }
                    break;
                }

                parts.Add(data);
                used += data.Length;
                earliestStart = recordStart;
                currentEnd = recordStart;
            }

            if (parts.Count == 0 || earliestStart <= 0)
                return;

            parts.Reverse();
            var tempPath = path + ".tmp";
            using (var outFs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (var part in parts)
                {
                    WriteStringRecords(outFs, part);
                }
                outFs.Flush(true);
            }

            File.Move(tempPath, path, true);
        }
        catch
        {
            // Best effort trim.
        }
    }

    private static void WriteRecord(FileStream fs, byte[] bytes)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lenBuf, bytes.Length);
        fs.Write(lenBuf);
        fs.Write(bytes, 0, bytes.Length);
        fs.Write(lenBuf);
    }

    private void WriteStringRecords(FileStream fs, string data)
    {
        if (string.IsNullOrEmpty(data))
            return;

        var maxCharsPerRecord = Math.Max(1, MaxRecordBytes / 4);
        for (var offset = 0; offset < data.Length; offset += maxCharsPerRecord)
        {
            var length = Math.Min(maxCharsPerRecord, data.Length - offset);
            var slice = data.Substring(offset, length);
            var bytes = _encoding.GetBytes(slice);
            WriteRecord(fs, bytes);
        }
    }

    private bool TryReadRecordBackward(FileStream fs, long recordEnd, out string data, out long recordStart)
    {
        data = string.Empty;
        recordStart = 0;

        if (recordEnd < 8)
            return false;

        if (!TryReadInt32(fs, recordEnd - 4, out var len))
            return false;

        if (len <= 0 || len > MaxRecordBytes)
            return false;

        var start = recordEnd - 4 - len - 4;
        if (start < 0)
            return false;

        if (!TryReadInt32(fs, start, out var prefix) || prefix != len)
            return false;

        if (start + 4 + len > fs.Length)
            return false;

        fs.Position = start + 4;
        var buffer = new byte[len];
        var read = fs.Read(buffer, 0, len);
        if (read != len)
            return false;

        data = _encoding.GetString(buffer);
        recordStart = start;
        return true;
    }

    private static bool TryReadInt32(FileStream fs, long position, out int value)
    {
        value = 0;
        if (position < 0 || position + 4 > fs.Length)
            return false;

        Span<byte> buf = stackalloc byte[4];
        fs.Position = position;
        var read = fs.Read(buf);
        if (read != 4)
            return false;

        value = BinaryPrimitives.ReadInt32LittleEndian(buf);
        return true;
    }

    private object GetLock(string deviceId, string sessionId) =>
        _locks.GetOrAdd(BuildKey(deviceId, sessionId), _ => new object());

    private string GetSessionPath(string deviceId, string sessionId)
    {
        var dir = GetDeviceDirectory(deviceId);
        var session = SanitizeKey(sessionId);
        return Path.Combine(dir, $"{session}.vth");
    }

    private string GetDeviceDirectory(string deviceId)
    {
        var device = SanitizeKey(deviceId);
        return Path.Combine(_historyDirectory, device);
    }

    private static string BuildKey(string deviceId, string sessionId) =>
        $"{deviceId}::{sessionId}";

    private static string SanitizeKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if ((ch >= 'a' && ch <= 'z') ||
                (ch >= 'A' && ch <= 'Z') ||
                (ch >= '0' && ch <= '9') ||
                ch == '-' || ch == '_' || ch == '.')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('_');
            }
        }
        return sb.ToString();
    }
}
