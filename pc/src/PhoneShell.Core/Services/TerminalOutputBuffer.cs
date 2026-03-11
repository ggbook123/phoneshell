using System.Text;
using System.Text.RegularExpressions;

namespace PhoneShell.Core.Services;

public sealed class TerminalOutputBuffer
{
    private readonly char[] _buffer;
    private int _position;
    private int _length;
    private readonly object _lock = new();

    // Matches ANSI escape sequences: ESC[ ... final_byte, ESC] ... ST, ESC(X, etc.
    private static readonly Regex AnsiRegex = new(
        @"\x1b\[[0-9;?]*[A-Za-z]|\x1b\][^\x07]*(?:\x07|\x1b\\)|\x1b[()][A-Z0-9]|\x1b[>=]",
        RegexOptions.Compiled);

    // Collapse 3+ consecutive blank lines into 1
    private static readonly Regex MultiBlankLines = new(
        @"(\r?\n\s*){3,}", RegexOptions.Compiled);

    public TerminalOutputBuffer(int capacity = 8000)
    {
        _buffer = new char[capacity];
    }

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        lock (_lock)
        {
            foreach (var ch in text)
            {
                _buffer[_position] = ch;
                _position = (_position + 1) % _buffer.Length;
                if (_length < _buffer.Length)
                    _length++;
            }
        }
    }

    public string GetRecent()
    {
        lock (_lock)
        {
            if (_length == 0) return string.Empty;

            var result = new char[_length];
            var start = (_position - _length + _buffer.Length) % _buffer.Length;

            for (int i = 0; i < _length; i++)
            {
                result[i] = _buffer[(start + i) % _buffer.Length];
            }

            var raw = new string(result);

            // 1. Strip ANSI escape sequences
            raw = AnsiRegex.Replace(raw, string.Empty);

            // 2. Process backspaces (char + \b = delete previous char)
            raw = ProcessBackspaces(raw);

            // 3. Strip remaining control chars except \n and \t
            var sb = new StringBuilder(raw.Length);
            foreach (var ch in raw)
            {
                if (ch == '\n' || ch == '\t' || !char.IsControl(ch))
                    sb.Append(ch);
            }
            raw = sb.ToString();

            // 4. Remove box-drawing / block element unicode noise
            raw = StripBoxDrawing(raw);

            // 5. Collapse excessive blank lines
            raw = MultiBlankLines.Replace(raw, "\n\n");

            return raw.Trim();
        }
    }

    private static string ProcessBackspaces(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch == '\b')
            {
                if (sb.Length > 0) sb.Length--;
            }
            else
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Like GetRecent() but preserves box-drawing characters (U+2500-U+259F) and non-breaking spaces.
    /// Use this when feeding terminal output to AI so TUI layouts remain readable.
    /// </summary>
    public string GetRecentRaw()
    {
        lock (_lock)
        {
            if (_length == 0) return string.Empty;

            var result = new char[_length];
            var start = (_position - _length + _buffer.Length) % _buffer.Length;

            for (int i = 0; i < _length; i++)
            {
                result[i] = _buffer[(start + i) % _buffer.Length];
            }

            var raw = new string(result);

            // 1. Strip ANSI escape sequences
            raw = AnsiRegex.Replace(raw, string.Empty);

            // 2. Process backspaces
            raw = ProcessBackspaces(raw);

            // 3. Strip remaining control chars except \n and \t
            var sb = new StringBuilder(raw.Length);
            foreach (var ch in raw)
            {
                if (ch == '\n' || ch == '\t' || !char.IsControl(ch))
                    sb.Append(ch);
            }
            raw = sb.ToString();

            // NOTE: No StripBoxDrawing — keep box-drawing chars for TUI readability

            // 4. Collapse excessive blank lines
            raw = MultiBlankLines.Replace(raw, "\n\n");

            return raw.Trim();
        }
    }

    private static string StripBoxDrawing(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            // Box Drawing: U+2500-U+257F
            // Block Elements: U+2580-U+259F
            // Skip non-breaking space U+00A0 too
            if ((ch >= '\u2500' && ch <= '\u259F') || ch == '\u00A0')
                continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
