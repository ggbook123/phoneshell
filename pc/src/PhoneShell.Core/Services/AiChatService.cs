using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PhoneShell.Core.Models;

namespace PhoneShell.Core.Services;

public sealed record AiChatResult(string Content, string RequestJson, string ResponseJson);

public sealed class AiChatService : IDisposable
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private const string SystemPrompt = """
        You are a terminal assistant embedded in PhoneShell, a PC terminal application.
        The terminal may be running PowerShell, CMD, WSL (bash/zsh), or other shells.
        Detect the current shell from the terminal output and use appropriate syntax.

        For PowerShell: use `Set-Location` or `cd`, `Get-ChildItem` or `ls`/`dir`, etc.
        For bash/zsh (WSL/Linux): use standard Unix commands.
        For CMD: use `cd /d`, `dir`, `type`, etc.
        Never mix shell syntaxes.

        You can see the recent terminal output provided as context. Use it to understand the current state.

        IMPORTANT: All user requests are instructions for you to directly operate the terminal.
        When the user says "窗口" (window), they mean the terminal.
        When the user says "输入xxx", "执行xxx", "运行xxx", "打开xxx", or similar phrases,
        they mean you should execute that as a terminal command. For example:
        - "输入claude" means execute the command `claude` in the terminal.
        - "输入exit" means execute `exit` in the terminal.
        - "打开claude" means run `claude` as a command.
        You are controlling the terminal directly. Always translate user intent into terminal commands.
        Never say you cannot operate the terminal or click buttons — you ARE the terminal controller.

        When you want to execute a command in the terminal, wrap it in a ```command block like this:

        ```command
        ls
        ```

        Each ```command block should contain exactly ONE command per line.
        Respond concisely. Use the user's language.

        ## Special Key Tokens for TUI Applications

        You can send special keys inside ```command blocks using {KEY} syntax.
        Available keys:
        - {ENTER}, {ESC}, {TAB}, {BACKSPACE}, {DELETE}, {SPACE}
        - {CTRL+C}, {CTRL+D}, {CTRL+Z}, {CTRL+L}, {CTRL+A}, {CTRL+E}, {CTRL+K}, {CTRL+U}, {CTRL+W}, {CTRL+R}, {CTRL+X}
        - {UP}, {DOWN}, {LEFT}, {RIGHT}, {HOME}, {END}, {PAGEUP}, {PAGEDOWN}, {INSERT}
        - {F1} through {F12}

        Rules for key tokens:
        - A line containing {KEY} tokens will NOT have Enter appended automatically.
        - You can mix text and tokens on the same line: `Hello{ENTER}` types "Hello" then presses Enter.
        - A line with ONLY a token like `{CTRL+C}` sends just that key.
        - Lines WITHOUT any {KEY} tokens behave as before (text + Enter).

        Examples:
        - Exit a TUI app: ```command
          {CTRL+C}
          ```
        - Type text and submit in a TUI: ```command
          Hello world{ENTER}
          ```
        - Navigate a menu: ```command
          {DOWN}{DOWN}{ENTER}
          ```
        - Press Escape to close a dialog: ```command
          {ESC}
          ```

        ## Detecting TUI vs Shell

        Look at the terminal output to determine the current state:
        - If you see a PS prompt like `PS C:\>`, you are in a normal PowerShell shell.
        - If you see a $ prompt, you are in bash/zsh.
        - If you see a C:\> prompt, you are in CMD.
        - If you see box-drawing characters (─, │, ┌, ┐, └, ┘, etc.), menus, or full-screen layouts, a TUI application is running.
        - When in a TUI, use {KEY} tokens to interact. Do NOT type shell commands — they will be sent as keystrokes to the TUI.
        - To exit most TUI apps, try {CTRL+C}, {ESC}, or type q{ENTER} depending on the application.
        """;

    private const string SingleStepAddendum = """

        IMPORTANT: Only include ONE ```command block per response. If the user asks for multiple steps,
        do the FIRST step only. After the command executes, the user will see the terminal output
        and can ask you to continue. You cannot see command output until the next conversation turn.
        Always explain what you're doing and what you'll do next.
        """;

    private const string AutoExecAddendum = """

        You are in AUTO-EXEC mode. The system will automatically execute your commands and feed the
        terminal output back to you. You can continue issuing commands step by step until the task is done.

        CRITICAL RULES — you MUST follow these strictly:
        1. If the task is NOT yet complete, you MUST include exactly ONE ```command block in your response.
           Do NOT reply with only text/explanation when there are still steps remaining.
           Even if you are just describing what happened, ALWAYS append the next ```command block.
        2. ONLY omit the ```command block when the ENTIRE task requested by the user is fully complete.
        3. After each command, you will receive the updated terminal output in the next turn.
        4. Keep explanations very brief (one short sentence). Focus on the command.

        WRONG example (task not done but no command block):
          "已切换到目录X。接下来将切换到目录Y。"
        CORRECT example (task not done, includes command block):
          "已切换到目录X，接下来切换到目录Y。

          ```command
          Set-Location "D:\Y"
          ```"
        """;

    private const string TuiModeAddendum = """

        [TUI MODE ACTIVE]: A full-screen TUI application is currently running (alternate screen buffer detected).
        The terminal screen content shown below is the TUI interface, NOT a shell prompt.
        You MUST use {KEY} tokens to interact with the TUI. Do NOT type shell commands.
        Common TUI interactions:
        - Navigate: {UP}, {DOWN}, {LEFT}, {RIGHT}, {TAB}
        - Select/confirm: {ENTER}
        - Cancel/back: {ESC}
        - Quit: {CTRL+C} or q{ENTER} (depends on the app)
        - Scroll: {PAGEUP}, {PAGEDOWN}
        """;

    public async Task<AiChatResult> SendAsync(
        AiSettings settings,
        List<ChatMessage> history,
        string userMessage,
        string terminalContext,
        bool isAutoExec = false,
        bool isTuiActive = false,
        CancellationToken ct = default)
    {
        var messages = new List<object>();

        // System prompt with terminal context
        var systemContent = SystemPrompt + (isAutoExec ? AutoExecAddendum : SingleStepAddendum);
        if (isTuiActive)
            systemContent += TuiModeAddendum;

        if (!string.IsNullOrWhiteSpace(terminalContext))
        {
            if (isTuiActive)
            {
                systemContent += $"\n\n--- Terminal Screen (TUI) ---\n{terminalContext}\n--- End Terminal Screen ---";
            }
            else
            {
                systemContent += $"\n\n--- Recent Terminal Output ---\n{terminalContext}\n--- End Terminal Output ---";
            }
        }
        messages.Add(new { role = "system", content = systemContent });

        // Conversation history (last 20, already includes current user message)
        var startIndex = Math.Max(0, history.Count - 20);
        for (int i = startIndex; i < history.Count; i++)
        {
            messages.Add(new { role = history[i].Role, content = history[i].Content });
        }

        var requestBody = new
        {
            model = settings.ModelName,
            messages,
            temperature = 0.7,
            max_tokens = 2048
        };

        var json = JsonSerializer.Serialize(requestBody, PrettyJson);
        var request = new HttpRequestMessage(HttpMethod.Post, settings.ApiEndpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Authorization", $"Bearer {settings.ApiKey}");

        var response = await _httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            return new AiChatResult($"[API Error {(int)response.StatusCode}]: {responseBody}", json, responseBody);
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) &&
                choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var content))
                {
                    return new AiChatResult(content.GetString() ?? string.Empty, json, responseBody);
                }
            }

            return new AiChatResult("[Error]: Unexpected API response format.", json, responseBody);
        }
        catch (JsonException)
        {
            return new AiChatResult($"[Error]: API returned truncated/invalid JSON response.", json, responseBody);
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
