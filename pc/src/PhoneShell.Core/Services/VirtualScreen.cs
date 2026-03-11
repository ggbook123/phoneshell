using System.Text;

namespace PhoneShell.Core.Services;

public sealed class VirtualScreen
{
    private char[,] _grid;
    private int _cols;
    private int _rows;
    private int _cursorRow;
    private int _cursorCol;
    private readonly object _lock = new();

    // Parser state
    private enum ParseState { Normal, Escape, Csi, Osc }
    private ParseState _state;
    private readonly StringBuilder _csiParams = new();

    public VirtualScreen(int cols = 80, int rows = 24)
    {
        _cols = cols;
        _rows = rows;
        _grid = new char[rows, cols];
        ClearGrid(0, 0, rows - 1, cols - 1);
    }

    public void Resize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0) return;
        lock (_lock)
        {
            var newGrid = new char[rows, cols];
            // Fill with spaces
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    newGrid[r, c] = ' ';
            // Copy existing content
            var copyRows = Math.Min(_rows, rows);
            var copyCols = Math.Min(_cols, cols);
            for (int r = 0; r < copyRows; r++)
                for (int c = 0; c < copyCols; c++)
                    newGrid[r, c] = _grid[r, c];
            _grid = newGrid;
            _rows = rows;
            _cols = cols;
            _cursorRow = Math.Min(_cursorRow, rows - 1);
            _cursorCol = Math.Min(_cursorCol, cols - 1);
        }
    }

    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_lock)
        {
            foreach (var ch in text)
                ProcessChar(ch);
        }
    }

    private void ProcessChar(char ch)
    {
        switch (_state)
        {
            case ParseState.Normal:
                if (ch == '\x1b')
                {
                    _state = ParseState.Escape;
                }
                else if (ch == '\r')
                {
                    _cursorCol = 0;
                }
                else if (ch == '\n')
                {
                    LineFeed();
                }
                else if (ch == '\b')
                {
                    if (_cursorCol > 0) _cursorCol--;
                }
                else if (ch == '\t')
                {
                    _cursorCol = Math.Min((_cursorCol / 8 + 1) * 8, _cols - 1);
                }
                else if (!char.IsControl(ch))
                {
                    PutChar(ch);
                }
                break;

            case ParseState.Escape:
                if (ch == '[')
                {
                    _state = ParseState.Csi;
                    _csiParams.Clear();
                }
                else if (ch == ']')
                {
                    _state = ParseState.Osc;
                }
                else
                {
                    _state = ParseState.Normal;
                }
                break;

            case ParseState.Csi:
                if (ch >= '0' && ch <= '9' || ch == ';' || ch == '?')
                {
                    _csiParams.Append(ch);
                }
                else
                {
                    ExecuteCsi(ch);
                    _state = ParseState.Normal;
                }
                break;

            case ParseState.Osc:
                if (ch == '\x07')
                {
                    _state = ParseState.Normal;
                }
                else if (ch == '\x1b')
                {
                    _state = ParseState.Escape;
                }
                break;
        }
    }

    private void ExecuteCsi(char cmd)
    {
        var paramStr = _csiParams.ToString();
        if (paramStr.StartsWith('?'))
            paramStr = paramStr[1..];

        var parts = paramStr.Split(';', StringSplitOptions.None);

        switch (cmd)
        {
            case 'H':
            case 'f':
                var row = ParseParam(parts, 0, 1) - 1;
                var col = ParseParam(parts, 1, 1) - 1;
                _cursorRow = Clamp(row, 0, _rows - 1);
                _cursorCol = Clamp(col, 0, _cols - 1);
                break;
            case 'A':
                _cursorRow = Math.Max(0, _cursorRow - ParseParam(parts, 0, 1));
                break;
            case 'B':
                _cursorRow = Math.Min(_rows - 1, _cursorRow + ParseParam(parts, 0, 1));
                break;
            case 'C':
                _cursorCol = Math.Min(_cols - 1, _cursorCol + ParseParam(parts, 0, 1));
                break;
            case 'D':
                _cursorCol = Math.Max(0, _cursorCol - ParseParam(parts, 0, 1));
                break;
            case 'G':
                _cursorCol = Clamp(ParseParam(parts, 0, 1) - 1, 0, _cols - 1);
                break;
            case 'd':
                _cursorRow = Clamp(ParseParam(parts, 0, 1) - 1, 0, _rows - 1);
                break;
            case 'J':
                EraseInDisplay(ParseParam(parts, 0, 0));
                break;
            case 'K':
                EraseInLine(ParseParam(parts, 0, 0));
                break;
            case 'm':
            case 'h':
            case 'l':
            case 'r':
            case 's':
            case 'u':
            case 'n':
            case 't':
                break;
        }
    }

    private void PutChar(char ch)
    {
        if (_cursorCol >= _cols)
        {
            _cursorCol = 0;
            LineFeed();
        }
        _grid[_cursorRow, _cursorCol] = ch;
        _cursorCol++;
    }

    private void LineFeed()
    {
        if (_cursorRow < _rows - 1)
        {
            _cursorRow++;
        }
        else
        {
            ScrollUp();
        }
    }

    private void ScrollUp()
    {
        for (int r = 0; r < _rows - 1; r++)
            for (int c = 0; c < _cols; c++)
                _grid[r, c] = _grid[r + 1, c];
        for (int c = 0; c < _cols; c++)
            _grid[_rows - 1, c] = ' ';
    }

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                ClearGrid(_cursorRow, _cursorCol, _cursorRow, _cols - 1);
                if (_cursorRow + 1 < _rows)
                    ClearGrid(_cursorRow + 1, 0, _rows - 1, _cols - 1);
                break;
            case 1:
                if (_cursorRow > 0)
                    ClearGrid(0, 0, _cursorRow - 1, _cols - 1);
                ClearGrid(_cursorRow, 0, _cursorRow, _cursorCol);
                break;
            case 2:
            case 3:
                ClearGrid(0, 0, _rows - 1, _cols - 1);
                break;
        }
    }

    private void EraseInLine(int mode)
    {
        switch (mode)
        {
            case 0:
                ClearGrid(_cursorRow, _cursorCol, _cursorRow, _cols - 1);
                break;
            case 1:
                ClearGrid(_cursorRow, 0, _cursorRow, _cursorCol);
                break;
            case 2:
                ClearGrid(_cursorRow, 0, _cursorRow, _cols - 1);
                break;
        }
    }

    private void ClearGrid(int r1, int c1, int r2, int c2)
    {
        for (int r = r1; r <= r2; r++)
            for (int c = (r == r1 ? c1 : 0); c <= (r == r2 ? c2 : _cols - 1); c++)
                _grid[r, c] = ' ';
    }

    private static int ParseParam(string[] parts, int index, int defaultValue)
    {
        if (index < parts.Length && int.TryParse(parts[index], out var val) && val > 0)
            return val;
        return defaultValue;
    }

    private static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;

    public string GetSnapshot()
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            int lastNonEmptyRow = -1;

            for (int r = _rows - 1; r >= 0; r--)
            {
                for (int c = 0; c < _cols; c++)
                {
                    if (_grid[r, c] != ' ')
                    {
                        lastNonEmptyRow = r;
                        goto found;
                    }
                }
            }
            found:

            if (lastNonEmptyRow < 0)
                return string.Empty;

            for (int r = 0; r <= lastNonEmptyRow; r++)
            {
                if (r > 0) sb.Append('\n');
                var rowChars = new char[_cols];
                for (int c = 0; c < _cols; c++)
                    rowChars[c] = _grid[r, c];
                var line = new string(rowChars).TrimEnd();
                sb.Append(line);
            }

            return sb.ToString();
        }
    }
}
