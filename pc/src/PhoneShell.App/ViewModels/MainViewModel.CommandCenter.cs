using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PhoneShell.ViewModels;

public sealed class QuickCommandItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string CommandText { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class FileExplorerNode
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public bool IsPlaceholder { get; set; }
    public bool HasLoadedChildren { get; set; }
    public ObservableCollection<FileExplorerNode> Children { get; } = new();
}

public sealed partial class MainViewModel
{
    private const int MaxRecentInputCount = 20;

    private readonly Dictionary<string, StringBuilder> _terminalInputDrafts = new(StringComparer.Ordinal);

    private string _explorerRootPath = string.Empty;
    private string _explorerStatus = string.Empty;

    private QuickCommandItem? _selectedQuickCommand;
    private string _quickCommandEditName = string.Empty;
    private string _quickCommandEditCommand = string.Empty;
    private string _quickCommandEditDescription = string.Empty;

    public ObservableCollection<FileExplorerNode> ExplorerRootNodes { get; } = new();
    public ObservableCollection<QuickCommandItem> QuickCommands { get; } = new();
    public ObservableCollection<string> RecentInputs { get; } = new();

    public bool HasActiveSessionInputTarget => ActiveTab is not null;

    public string ExplorerRootPath
    {
        get => _explorerRootPath;
        set => SetProperty(ref _explorerRootPath, value);
    }

    public string ExplorerStatus
    {
        get => _explorerStatus;
        private set => SetProperty(ref _explorerStatus, value);
    }

    public QuickCommandItem? SelectedQuickCommand
    {
        get => _selectedQuickCommand;
        set
        {
            if (!SetProperty(ref _selectedQuickCommand, value))
                return;

            if (value is null)
            {
                QuickCommandEditName = string.Empty;
                QuickCommandEditCommand = string.Empty;
                QuickCommandEditDescription = string.Empty;
            }
            else
            {
                QuickCommandEditName = value.Name;
                QuickCommandEditCommand = value.CommandText;
                QuickCommandEditDescription = value.Description;
            }

            OnPropertyChanged(nameof(IsQuickCommandSelected));
            OnPropertyChanged(nameof(QuickCommandEditorTitle));
        }
    }

    public bool IsQuickCommandSelected => SelectedQuickCommand is not null;

    public string QuickCommandEditorTitle => IsQuickCommandSelected ? "编辑快捷指令" : "新增快捷指令";

    public string QuickCommandEditName
    {
        get => _quickCommandEditName;
        set => SetProperty(ref _quickCommandEditName, value);
    }

    public string QuickCommandEditCommand
    {
        get => _quickCommandEditCommand;
        set => SetProperty(ref _quickCommandEditCommand, value);
    }

    public string QuickCommandEditDescription
    {
        get => _quickCommandEditDescription;
        set => SetProperty(ref _quickCommandEditDescription, value);
    }

    private void InitializeDesktopCommandCenter()
    {
        ExplorerRootPath = ResolveDefaultExplorerRootPath();
        LoadExplorerRoot();
        LoadQuickCommands();
        LoadRecentInputs();
    }

    private void OnActiveSessionTargetChanged()
    {
        // Placeholder for future command-state updates.
    }

    private void RemoveTerminalInputDraft(string tabId)
    {
        if (string.IsNullOrWhiteSpace(tabId))
            return;

        _terminalInputDrafts.Remove(tabId);
    }

    public void TrackTerminalUserInput(string data)
    {
        if (string.IsNullOrEmpty(data) || ActiveTab is null)
            return;

        TrackInputChunk(ActiveTab, data);
    }

    public bool TryInsertTextIntoActiveSession(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        var tab = ActiveTab;
        if (tab is null)
        {
            SessionStatus = "请先选择会话";
            return false;
        }

        WriteTabInput(tab, text);
        TrackInputChunk(tab, text);
        return true;
    }

    public bool TryRunCommandInActiveSession(string commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
            return false;

        var normalized = commandText.TrimEnd('\r', '\n');
        return TryInsertTextIntoActiveSession(normalized + "\r");
    }

    public bool TryInsertPathsIntoActiveSession(IEnumerable<string> paths)
    {
        if (paths is null)
            return false;

        var normalized = NormalizePaths(paths);
        if (normalized.Count == 0)
            return false;

        var payload = string.Join(" ", normalized.Select(FormatPathForTerminal));
        return TryInsertTextIntoActiveSession(payload);
    }

    public void LoadExplorerRoot()
    {
        ExplorerRootNodes.Clear();

        var rawRoot = ExplorerRootPath?.Trim();
        if (string.IsNullOrWhiteSpace(rawRoot))
        {
            ExplorerStatus = "请输入目录路径";
            return;
        }

        string rootPath;
        try
        {
            rootPath = Path.GetFullPath(rawRoot);
        }
        catch
        {
            ExplorerStatus = "目录路径无效";
            return;
        }

        if (!Directory.Exists(rootPath))
        {
            ExplorerStatus = "目录不存在";
            return;
        }

        ExplorerRootPath = rootPath;

        var rootNode = CreateDirectoryNode(rootPath);
        ExplorerRootNodes.Add(rootNode);
        EnsureExplorerNodeChildren(rootNode);
        ExplorerStatus = rootPath;
    }

    public void EnsureExplorerNodeChildren(FileExplorerNode? node)
    {
        if (node is null || !node.IsDirectory || node.HasLoadedChildren)
            return;

        node.Children.Clear();

        try
        {
            var directories = Directory.EnumerateDirectories(node.FullPath)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
            foreach (var directoryPath in directories)
                node.Children.Add(CreateDirectoryNode(directoryPath));

            var files = Directory.EnumerateFiles(node.FullPath)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
            foreach (var filePath in files)
            {
                node.Children.Add(new FileExplorerNode
                {
                    Name = Path.GetFileName(filePath),
                    FullPath = filePath,
                    IsDirectory = false,
                    IsPlaceholder = false,
                    HasLoadedChildren = true
                });
            }
        }
        catch (UnauthorizedAccessException)
        {
            node.Children.Add(new FileExplorerNode
            {
                Name = "[无权限访问]",
                FullPath = node.FullPath,
                IsDirectory = false,
                IsPlaceholder = true,
                HasLoadedChildren = true
            });
        }
        catch (IOException ex)
        {
            node.Children.Add(new FileExplorerNode
            {
                Name = $"[读取失败] {ex.Message}",
                FullPath = node.FullPath,
                IsDirectory = false,
                IsPlaceholder = true,
                HasLoadedChildren = true
            });
        }

        node.HasLoadedChildren = true;
    }

    public void BeginNewQuickCommandEdit()
    {
        SelectedQuickCommand = null;
    }

    public bool SaveQuickCommandDraft()
    {
        var name = QuickCommandEditName?.Trim() ?? string.Empty;
        var command = QuickCommandEditCommand?.Trim() ?? string.Empty;
        var description = QuickCommandEditDescription?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(command))
            return false;

        if (SelectedQuickCommand is null)
        {
            var created = new QuickCommandItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                CommandText = command,
                Description = description
            };

            QuickCommands.Insert(0, created);
            SelectedQuickCommand = created;
        }
        else
        {
            var index = QuickCommands.IndexOf(SelectedQuickCommand);
            if (index < 0)
                index = 0;

            var updated = new QuickCommandItem
            {
                Id = SelectedQuickCommand.Id,
                Name = name,
                CommandText = command,
                Description = description
            };

            if (QuickCommands.Count > index)
                QuickCommands[index] = updated;
            else
                QuickCommands.Add(updated);

            SelectedQuickCommand = updated;
        }

        SaveQuickCommands();
        return true;
    }

    public bool DeleteSelectedQuickCommand()
    {
        if (SelectedQuickCommand is null)
            return false;

        var removed = QuickCommands.Remove(SelectedQuickCommand);
        if (!removed)
            return false;

        SaveQuickCommands();
        SelectedQuickCommand = null;
        return true;
    }

    public bool TryInsertSelectedQuickCommand(bool executeImmediately)
    {
        if (SelectedQuickCommand is null)
            return false;

        return executeImmediately
            ? TryRunCommandInActiveSession(SelectedQuickCommand.CommandText)
            : TryInsertTextIntoActiveSession(SelectedQuickCommand.CommandText);
    }

    public bool TryInsertRecentInput(string? input, bool executeImmediately)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        return executeImmediately
            ? TryRunCommandInActiveSession(input)
            : TryInsertTextIntoActiveSession(input);
    }

    public void ClearRecentInputs()
    {
        RecentInputs.Clear();
        SaveRecentInputs();
    }

    private void TrackInputChunk(TerminalTab tab, string data)
    {
        if (string.IsNullOrEmpty(data))
            return;

        if (!_terminalInputDrafts.TryGetValue(tab.TabId, out var draft))
        {
            draft = new StringBuilder();
            _terminalInputDrafts[tab.TabId] = draft;
        }

        for (var i = 0; i < data.Length; i++)
        {
            var ch = data[i];
            if (ch == '\x1b')
            {
                if (i + 1 >= data.Length)
                    continue;

                var next = data[i + 1];
                if (next == '[')
                {
                    // CSI sequence: ESC [ ... final-byte
                    i += 2;
                    while (i < data.Length && (data[i] < '@' || data[i] > '~'))
                        i++;
                }
                else if (next == 'O')
                {
                    // SS3 sequence: ESC O X
                    i = Math.Min(i + 2, data.Length - 1);
                }
                else
                {
                    // Alt-modified key or 2-byte escape sequence.
                    i = Math.Min(i + 1, data.Length - 1);
                }

                continue;
            }

            switch (ch)
            {
                case '\r':
                case '\n':
                    CommitInputDraft(draft);
                    break;
                case '\b':
                case '\x7f':
                    if (draft.Length > 0)
                        draft.Length--;
                    break;
                default:
                    if (!char.IsControl(ch) || ch == '\t')
                        draft.Append(ch);
                    break;
            }
        }
    }

    private void CommitInputDraft(StringBuilder draft)
    {
        var text = draft.ToString().Trim();
        draft.Clear();

        if (string.IsNullOrWhiteSpace(text))
            return;

        AddRecentInput(text);
    }

    private void AddRecentInput(string input)
    {
        var normalized = input.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var existingIndex = RecentInputs
            .Select((item, index) => new { item, index })
            .FirstOrDefault(pair => string.Equals(pair.item, normalized, StringComparison.Ordinal))?.index;

        if (existingIndex.HasValue)
            RecentInputs.RemoveAt(existingIndex.Value);

        RecentInputs.Insert(0, normalized);

        while (RecentInputs.Count > MaxRecentInputCount)
            RecentInputs.RemoveAt(RecentInputs.Count - 1);

        SaveRecentInputs();
    }

    private FileExplorerNode CreateDirectoryNode(string directoryPath)
    {
        var name = Path.GetFileName(directoryPath);
        if (string.IsNullOrWhiteSpace(name))
            name = directoryPath;

        var node = new FileExplorerNode
        {
            Name = name,
            FullPath = directoryPath,
            IsDirectory = true,
            IsPlaceholder = false,
            HasLoadedChildren = false
        };

        if (HasAnyChildEntry(directoryPath))
        {
            node.Children.Add(new FileExplorerNode
            {
                Name = "...",
                FullPath = directoryPath,
                IsDirectory = false,
                IsPlaceholder = true,
                HasLoadedChildren = true
            });
        }
        else
        {
            node.HasLoadedChildren = true;
        }

        return node;
    }

    private static bool HasAnyChildEntry(string directoryPath)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directoryPath).Take(1).Any();
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveDefaultExplorerRootPath()
    {
        var current = Directory.GetCurrentDirectory();
        var candidate = current;

        while (!string.IsNullOrWhiteSpace(candidate))
        {
            var solutionPath = Path.Combine(candidate, "PhoneShell.sln");
            if (File.Exists(solutionPath))
                return candidate;

            var parent = Directory.GetParent(candidate);
            if (parent is null)
                break;

            candidate = parent.FullName;
        }

        return current;
    }

    private void LoadQuickCommands()
    {
        QuickCommands.Clear();

        try
        {
            var filePath = GetQuickCommandsFilePath();
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                var loaded = JsonSerializer.Deserialize<List<QuickCommandItem>>(json);
                if (loaded is not null)
                {
                    foreach (var command in loaded.Where(IsValidQuickCommand))
                        QuickCommands.Add(command);
                }
            }
        }
        catch
        {
            // Ignore malformed file and fall back to defaults.
        }

        if (QuickCommands.Count == 0)
        {
            foreach (var command in BuildDefaultQuickCommands())
                QuickCommands.Add(command);
            SaveQuickCommands();
        }
    }

    private void SaveQuickCommands()
    {
        try
        {
            var path = GetQuickCommandsFilePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(QuickCommands.ToList(), new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(path, json);
        }
        catch
        {
            // Best effort persistence.
        }
    }

    private void LoadRecentInputs()
    {
        RecentInputs.Clear();

        try
        {
            var path = GetRecentInputsFilePath();
            if (!File.Exists(path))
                return;

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<List<string>>(json);
            if (loaded is null)
                return;

            foreach (var item in loaded
                         .Where(item => !string.IsNullOrWhiteSpace(item))
                         .Select(item => item.Trim())
                         .Distinct(StringComparer.Ordinal)
                         .Take(MaxRecentInputCount))
            {
                RecentInputs.Add(item);
            }
        }
        catch
        {
            // Best effort load.
        }
    }

    private void SaveRecentInputs()
    {
        try
        {
            var path = GetRecentInputsFilePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(RecentInputs.ToList(), new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(path, json);
        }
        catch
        {
            // Best effort persistence.
        }
    }

    private static bool IsValidQuickCommand(QuickCommandItem? item)
    {
        return item is not null &&
               !string.IsNullOrWhiteSpace(item.Name) &&
               !string.IsNullOrWhiteSpace(item.CommandText);
    }

    private static List<QuickCommandItem> BuildDefaultQuickCommands()
    {
        return
        [
            CreateQuickCommand("pwd", "pwd", "显示当前目录"),
            CreateQuickCommand("ls -Force", "Get-ChildItem -Force", "显示当前目录文件（含隐藏）"),
            CreateQuickCommand("cd ..", "Set-Location ..", "返回上一级目录"),
            CreateQuickCommand("find cs", "Get-ChildItem -Recurse -Filter *.cs", "递归查找 C# 文件"),
            CreateQuickCommand("rg files", "rg --files", "快速列出仓库文件"),
            CreateQuickCommand("find TODO", "Get-ChildItem -Recurse | Select-String -Pattern 'TODO'", "检索 TODO"),
            CreateQuickCommand("git status", "git status -sb", "查看仓库状态"),
            CreateQuickCommand("git fetch", "git fetch --all --prune", "同步远端引用"),
            CreateQuickCommand("git pull", "git pull --ff-only", "快进拉取"),
            CreateQuickCommand("git diff names", "git diff --name-only", "查看变更文件"),
            CreateQuickCommand("git add", "git add -A", "暂存全部修改"),
            CreateQuickCommand("git log", "git log --oneline -20", "最近 20 条提交"),
            CreateQuickCommand("dotnet restore", "dotnet restore", "恢复依赖"),
            CreateQuickCommand("dotnet build", "dotnet build pc/PhoneShell.sln", "构建桌面端"),
            CreateQuickCommand("dotnet test", "dotnet test pc/tests/PhoneShell.Core.Tests/PhoneShell.Core.Tests.csproj", "运行核心测试"),
            CreateQuickCommand("run app", "dotnet run --project pc/src/PhoneShell.App/PhoneShell.App.csproj", "启动桌面端"),
            CreateQuickCommand("check port", "Get-NetTCPConnection -LocalPort 9090", "查看 9090 端口占用")
        ];
    }

    private static QuickCommandItem CreateQuickCommand(string name, string commandText, string description)
    {
        return new QuickCommandItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            CommandText = commandText,
            Description = description
        };
    }

    private static string GetQuickCommandsFilePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "data", "quick-commands.json");
    }

    private static string GetRecentInputsFilePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "data", "recent-inputs.json");
    }

    private static List<string> NormalizePaths(IEnumerable<string> paths)
    {
        var normalized = new List<string>();
        foreach (var rawPath in paths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;

            try
            {
                normalized.Add(Path.GetFullPath(rawPath.Trim()));
            }
            catch
            {
                // Ignore invalid path entries.
            }
        }

        return normalized
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string FormatPathForTerminal(string path)
    {
        if (path.Any(char.IsWhiteSpace))
            return $"\"{path.Replace("\"", "\\\"")}\"";
        return path;
    }
}
