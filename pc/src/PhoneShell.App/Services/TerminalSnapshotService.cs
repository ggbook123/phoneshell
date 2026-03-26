using System.Text.Json;
using PhoneShell.Core.Terminals;

namespace PhoneShell.Services;

public sealed class TerminalSnapshotService
{
    private Func<string, Task<string>>? _executeScript;

    public void SetScriptExecutor(Func<string, Task<string>> executor)
    {
        _executeScript = executor;
    }

    public async Task<TerminalSnapshot?> CaptureAsync()
    {
        if (_executeScript is null)
            return null;

        // WebView2's ExecuteScriptAsync returns double-JSON-encoded results:
        // JS returns a JSON string, then WebView2 wraps it in another JSON string.
        var outerJson = await _executeScript("window.getScreenSnapshot()");

        if (string.IsNullOrEmpty(outerJson) || outerJson == "null")
            return null;

        // First deserialization: unwrap the WebView2 JSON string wrapper
        var innerJson = JsonSerializer.Deserialize<string>(outerJson);
        if (string.IsNullOrEmpty(innerJson))
            return null;

        // Second deserialization: parse the actual snapshot data
        using var doc = JsonDocument.Parse(innerJson);
        var root = doc.RootElement;

        var bufferType = root.GetProperty("type").GetString() ?? "normal";
        var cursorX = root.GetProperty("cursorX").GetInt32();
        var cursorY = root.GetProperty("cursorY").GetInt32();
        var cols = root.GetProperty("cols").GetInt32();
        var rows = root.GetProperty("rows").GetInt32();

        var linesArray = root.GetProperty("lines");
        var lines = new string[linesArray.GetArrayLength()];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = linesArray[i].GetString() ?? string.Empty;
        }

        return new TerminalSnapshot(bufferType, cursorX, cursorY, cols, rows, lines);
    }
}
