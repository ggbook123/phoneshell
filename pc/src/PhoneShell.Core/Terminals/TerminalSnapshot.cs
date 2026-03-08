using System.Text;

namespace PhoneShell.Core.Terminals;

public sealed record TerminalSnapshot(
    string BufferType,
    int CursorX,
    int CursorY,
    int Cols,
    int Rows,
    string[] Lines)
{
    public bool IsTuiActive => string.Equals(BufferType, "alternate", StringComparison.OrdinalIgnoreCase);

    public string GetScreenText()
    {
        return string.Join('\n', Lines);
    }

    public string GetNumberedScreenText()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Lines.Length; i++)
        {
            sb.AppendLine($"{i + 1,3}| {Lines[i]}");
        }
        return sb.ToString();
    }
}
