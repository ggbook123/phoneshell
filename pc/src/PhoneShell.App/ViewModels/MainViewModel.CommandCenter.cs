using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Data;

namespace PhoneShell.ViewModels;

public sealed class QuickCommandItem : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _categoryId = string.Empty;
    private string _name = string.Empty;
    private string _commandText = string.Empty;
    private string _description = string.Empty;
    private bool _isEditing;
    private bool _isNew;
    private string _originalName = string.Empty;
    private string _originalCommandText = string.Empty;

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string CategoryId
    {
        get => _categoryId;
        set => SetField(ref _categoryId, value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string CommandText
    {
        get => _commandText;
        set => SetField(ref _commandText, value);
    }

    public string Description
    {
        get => _description;
        set => SetField(ref _description, value);
    }

    [JsonIgnore]
    public bool IsEditing
    {
        get => _isEditing;
        set => SetField(ref _isEditing, value);
    }

    [JsonIgnore]
    public bool IsNew
    {
        get => _isNew;
        set => SetField(ref _isNew, value);
    }

    [JsonIgnore]
    public string OriginalName
    {
        get => _originalName;
        set => SetField(ref _originalName, value);
    }

    [JsonIgnore]
    public string OriginalCommandText
    {
        get => _originalCommandText;
        set => SetField(ref _originalCommandText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class QuickCommandFolder : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = string.Empty;
    private bool _isEditing;
    private bool _isNew;
    private string _originalName = string.Empty;

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    [JsonIgnore]
    public bool IsEditing
    {
        get => _isEditing;
        set => SetField(ref _isEditing, value);
    }

    [JsonIgnore]
    public bool IsNew
    {
        get => _isNew;
        set => SetField(ref _isNew, value);
    }

    [JsonIgnore]
    public string OriginalName
    {
        get => _originalName;
        set => SetField(ref _originalName, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class QuickCommandStore
{
    public List<QuickCommandFolder> Folders { get; set; } = new();
    public List<QuickCommandItem> Commands { get; set; } = new();
    public int? ShellDefaultsVersion { get; set; }
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
    private const int MaxRecentInputCount = 18;
    private const string ExplorerVirtualRootEn = "This PC";
    private const string ExplorerVirtualRootZh = "此电脑";
    private const string QuickCommandFolderShellId = "shell";
    private const string QuickCommandFolderGitId = "git";
    private const string QuickCommandFolderClaudeId = "claude";
    private const string QuickCommandFolderCodexId = "codex";
    private const string NewQuickCommandFolderDraftNameZh = "新建文件夹";
    private const string NewQuickCommandFolderDraftNameEn = "New Folder";
    private const int ShellDefaultsVersion = 1;

    private static readonly JsonSerializerOptions _quickCommandJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, StringBuilder> _terminalInputDrafts = new(StringComparer.Ordinal);

    private string _explorerRootPath = string.Empty;
    private string _explorerStatus = string.Empty;
    private string _explorerCurrentPath = string.Empty;

    private QuickCommandItem? _selectedQuickCommand;
    private QuickCommandFolder? _selectedQuickCommandFolder;
    private ICollectionView? _quickCommandsView;
    private string _quickCommandEditName = string.Empty;
    private string _quickCommandEditCommand = string.Empty;
    private string _quickCommandEditDescription = string.Empty;

    public ObservableCollection<FileExplorerNode> ExplorerRootNodes { get; } = new();
    public ObservableCollection<QuickCommandFolder> QuickCommandFolders { get; } = new();
    public ObservableCollection<QuickCommandItem> QuickCommands { get; } = new();
    public ObservableCollection<string> RecentInputs { get; } = new();

    public bool HasActiveSessionInputTarget => ActiveTab is not null;

    public string ExplorerRootPath
    {
        get => _explorerRootPath;
        set => SetProperty(ref _explorerRootPath, value);
    }

    public string ExplorerCurrentPath
    {
        get => _explorerCurrentPath;
        set => SetProperty(ref _explorerCurrentPath, value);
    }

    public string ExplorerStatus
    {
        get => _explorerStatus;
        private set => SetProperty(ref _explorerStatus, value);
    }

    public ICollectionView QuickCommandsView
    {
        get
        {
            if (_quickCommandsView is null)
            {
                _quickCommandsView = CollectionViewSource.GetDefaultView(QuickCommands);
                _quickCommandsView.Filter = FilterQuickCommandByFolder;
            }

            return _quickCommandsView;
        }
    }

    public QuickCommandFolder? SelectedQuickCommandFolder
    {
        get => _selectedQuickCommandFolder;
        set
        {
            if (!SetProperty(ref _selectedQuickCommandFolder, value))
                return;

            CommitInlineQuickCommandEdits();
            CommitInlineQuickCommandFolderEdits(except: value);

            QuickCommandsView.Refresh();

            if (_selectedQuickCommand is not null && value is not null &&
                !IsQuickCommandInFolder(_selectedQuickCommand, value))
            {
                SelectedQuickCommand = null;
            }
        }
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
        ExplorerCurrentPath = ExplorerRootPath;
        LoadExplorerRoot();
        _ = QuickCommandsView;
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

        if (IsExplorerVirtualRoot(rawRoot))
        {
            LoadExplorerVirtualRoot();
            ExplorerStatus = rawRoot;
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

    private void LoadExplorerVirtualRoot()
    {
        try
        {
            var drives = DriveInfo.GetDrives()
                .OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var drive in drives)
            {
                ExplorerRootNodes.Add(CreateDriveNode(drive));
            }
        }
        catch
        {
            // Ignore drive enumeration failures.
        }
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

    public QuickCommandFolder BeginInlineQuickCommandFolderCreate()
    {
        CommitInlineQuickCommandFolderEdits();

        var folder = new QuickCommandFolder
        {
            Id = string.Empty,
            Name = NewQuickCommandFolderDraftNameZh,
            IsEditing = true,
            IsNew = true
        };

        QuickCommandFolders.Add(folder);
        SelectedQuickCommandFolder = folder;
        return folder;
    }

    public void BeginInlineQuickCommandFolderEdit(QuickCommandFolder folder)
    {
        if (folder is null)
            return;

        CommitInlineQuickCommandFolderEdits(except: folder);
        folder.OriginalName = folder.Name;
        folder.IsEditing = true;
        folder.IsNew = false;
        SelectedQuickCommandFolder = folder;
    }

    public void CommitInlineQuickCommandFolderEdit(QuickCommandFolder folder)
    {
        if (folder is null)
            return;

        var name = folder.Name?.Trim() ?? string.Empty;
        if (folder.IsNew && IsDraftQuickCommandFolderName(name))
        {
            QuickCommandFolders.Remove(folder);
            if (SelectedQuickCommandFolder == folder)
                SelectedQuickCommandFolder = QuickCommandFolders.FirstOrDefault();

            folder.IsEditing = false;
            folder.IsNew = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            if (folder.IsNew)
            {
                QuickCommandFolders.Remove(folder);
                if (SelectedQuickCommandFolder == folder)
                    SelectedQuickCommandFolder = QuickCommandFolders.FirstOrDefault();
            }
            else
            {
                folder.Name = folder.OriginalName ?? folder.Name ?? string.Empty;
            }

            folder.IsEditing = false;
            folder.IsNew = false;
            return;
        }

        folder.Name = name;
        if (string.IsNullOrWhiteSpace(folder.Id))
            folder.Id = Guid.NewGuid().ToString("N");

        folder.IsEditing = false;
        folder.IsNew = false;
        SaveQuickCommands();
    }

    public void CommitInlineQuickCommandFolderEdits(QuickCommandFolder? except = null)
    {
        var pending = QuickCommandFolders.Where(folder => folder.IsEditing && folder != except).ToList();
        foreach (var folder in pending)
            CommitInlineQuickCommandFolderEdit(folder);
    }

    public void CancelInlineQuickCommandFolderEdit(QuickCommandFolder folder)
    {
        if (folder is null)
            return;

        if (folder.IsNew)
        {
            QuickCommandFolders.Remove(folder);
            if (SelectedQuickCommandFolder == folder)
                SelectedQuickCommandFolder = QuickCommandFolders.FirstOrDefault();
        }
        else
        {
            folder.Name = folder.OriginalName ?? folder.Name;
        }

        folder.IsEditing = false;
        folder.IsNew = false;
    }

    public QuickCommandItem BeginInlineQuickCommandCreate()
    {
        CommitInlineQuickCommandEdits();

        var categoryId = SelectedQuickCommandFolder?.Id ?? GetDefaultQuickCommandFolderId();
        var item = new QuickCommandItem
        {
            Id = string.Empty,
            CategoryId = categoryId,
            Name = string.Empty,
            CommandText = string.Empty,
            IsEditing = true,
            IsNew = true
        };

        QuickCommands.Insert(0, item);
        SelectedQuickCommand = item;
        return item;
    }

    public void BeginInlineQuickCommandEdit(QuickCommandItem item)
    {
        if (item is null)
            return;

        CommitInlineQuickCommandEdits(except: item);

        item.OriginalName = item.Name;
        item.OriginalCommandText = item.CommandText;
        item.IsEditing = true;
        item.IsNew = false;
        SelectedQuickCommand = item;
    }

    public void CommitInlineQuickCommandEdit(QuickCommandItem item)
    {
        if (item is null)
            return;

        var name = item.Name?.Trim() ?? string.Empty;
        var command = item.CommandText?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(command))
        {
            if (item.IsNew)
            {
                QuickCommands.Remove(item);
                if (SelectedQuickCommand == item)
                    SelectedQuickCommand = null;
            }
            else
            {
                item.Name = item.OriginalName ?? item.Name ?? string.Empty;
                item.CommandText = item.OriginalCommandText ?? item.CommandText ?? string.Empty;
            }

            item.IsEditing = false;
            item.IsNew = false;
            return;
        }

        item.Name = name;
        item.CommandText = command;
        if (string.IsNullOrWhiteSpace(item.CategoryId))
            item.CategoryId = GetDefaultQuickCommandFolderId();
        if (string.IsNullOrWhiteSpace(item.Id))
            item.Id = Guid.NewGuid().ToString("N");

        item.IsEditing = false;
        item.IsNew = false;
        SaveQuickCommands();
    }

    public void CommitInlineQuickCommandEdits(QuickCommandItem? except = null)
    {
        var pending = QuickCommands.Where(cmd => cmd.IsEditing && cmd != except).ToList();
        foreach (var item in pending)
            CommitInlineQuickCommandEdit(item);
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
            var categoryId = SelectedQuickCommandFolder?.Id ?? GetDefaultQuickCommandFolderId();
            var created = new QuickCommandItem
            {
                Id = Guid.NewGuid().ToString("N"),
                CategoryId = categoryId,
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
                CategoryId = string.IsNullOrWhiteSpace(SelectedQuickCommand.CategoryId)
                    ? GetDefaultQuickCommandFolderId()
                    : SelectedQuickCommand.CategoryId,
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

    public void DeleteQuickCommand(QuickCommandItem item)
    {
        if (item is null)
            return;

        var removed = QuickCommands.Remove(item);
        if (!removed)
            return;

        if (SelectedQuickCommand == item)
            SelectedQuickCommand = null;

        SaveQuickCommands();
    }

    public void DeleteQuickCommandFolder(QuickCommandFolder folder)
    {
        if (folder is null)
            return;

        var folderId = folder.Id;
        var wasSelected = SelectedQuickCommandFolder == folder;
        var removed = QuickCommandFolders.Remove(folder);
        if (!removed)
            return;

        for (var i = QuickCommands.Count - 1; i >= 0; i--)
        {
            if (string.Equals(QuickCommands[i].CategoryId, folderId, StringComparison.OrdinalIgnoreCase))
                QuickCommands.RemoveAt(i);
        }

        if (wasSelected)
            SelectedQuickCommandFolder = QuickCommandFolders.FirstOrDefault();

        if (SelectedQuickCommand is not null &&
            string.Equals(SelectedQuickCommand.CategoryId, folderId, StringComparison.OrdinalIgnoreCase))
        {
            SelectedQuickCommand = null;
        }

        if (QuickCommandFolders.Count == 0)
        {
            foreach (var defaultFolder in BuildDefaultQuickCommandFolders())
                QuickCommandFolders.Add(defaultFolder);
            SelectedQuickCommandFolder = QuickCommandFolders.FirstOrDefault();
        }

        SaveQuickCommands();
        QuickCommandsView.Refresh();
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
        return ExplorerVirtualRootEn;
    }

    private static bool IsExplorerVirtualRoot(string? path)
    {
        return string.Equals(path, ExplorerVirtualRootEn, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(path, ExplorerVirtualRootZh, StringComparison.OrdinalIgnoreCase);
    }

    private static FileExplorerNode CreateDriveNode(DriveInfo drive)
    {
        var rootPath = drive.RootDirectory.FullName;
        var label = drive.IsReady ? drive.VolumeLabel : string.Empty;
        var driveName = drive.Name.TrimEnd('\\');
        var display = string.IsNullOrWhiteSpace(label)
            ? driveName
            : $"{label} ({driveName})";

        var node = new FileExplorerNode
        {
            Name = display,
            FullPath = rootPath,
            IsDirectory = true,
            IsPlaceholder = false,
            HasLoadedChildren = false
        };

        if (HasAnyChildEntry(rootPath))
        {
            node.Children.Add(new FileExplorerNode
            {
                Name = "...",
                FullPath = rootPath,
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

    private void LoadQuickCommands()
    {
        QuickCommands.Clear();
        QuickCommandFolders.Clear();

        var store = LoadQuickCommandStore();
        if (store is not null)
            ApplyQuickCommandStore(store);

        var cleanedDraftFolders = CleanupOrphanDraftQuickCommandFolders();
        var addedFolders = EnsureDefaultQuickCommandFolders();
        var migratedShellDefaults = UpdateShellDefaultsIfNeeded(store);
        var addedCommands = EnsureDefaultQuickCommandsForMissingCategories();
        if (cleanedDraftFolders || addedFolders || migratedShellDefaults || addedCommands)
            SaveQuickCommands();

        EnsureQuickCommandFolderSelection();
        QuickCommandsView.Refresh();
    }

    private void SaveQuickCommands()
    {
        try
        {
            var path = GetQuickCommandsFilePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var store = new QuickCommandStore
            {
                Folders = QuickCommandFolders.ToList(),
                Commands = QuickCommands.ToList(),
                ShellDefaultsVersion = ShellDefaultsVersion
            };
            var json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
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

    private QuickCommandStore? LoadQuickCommandStore()
    {
        try
        {
            var filePath = GetQuickCommandsFilePath();
            if (!File.Exists(filePath))
                return null;

            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.Array => new QuickCommandStore
                {
                    Commands = JsonSerializer.Deserialize<List<QuickCommandItem>>(json, _quickCommandJsonOptions) ?? new()
                },
                JsonValueKind.Object => JsonSerializer.Deserialize<QuickCommandStore>(json, _quickCommandJsonOptions),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private void ApplyQuickCommandStore(QuickCommandStore store)
    {
        if (store.Folders is not null)
        {
            foreach (var folder in store.Folders.Where(IsValidQuickCommandFolder))
            {
                NormalizeQuickCommandFolder(folder);
                QuickCommandFolders.Add(folder);
            }
        }

        var validFolderIds = new HashSet<string>(
            QuickCommandFolders.Select(folder => folder.Id),
            StringComparer.OrdinalIgnoreCase);

        if (store.Commands is not null)
        {
            foreach (var command in store.Commands.Where(IsValidQuickCommand))
            {
                NormalizeQuickCommand(command);
                if (string.IsNullOrWhiteSpace(command.CategoryId))
                {
                    command.CategoryId = GetDefaultQuickCommandFolderId();
                }
                else if (!validFolderIds.Contains(command.CategoryId) &&
                         !IsKnownQuickCommandFolderId(command.CategoryId))
                {
                    command.CategoryId = GetDefaultQuickCommandFolderId();
                }
                QuickCommands.Add(command);
            }
        }
    }

    private void EnsureQuickCommandFolderSelection()
    {
        if (SelectedQuickCommandFolder is null)
            SelectedQuickCommandFolder = QuickCommandFolders.FirstOrDefault();
    }

    private bool EnsureDefaultQuickCommandFolders()
    {
        if (QuickCommandFolders.Count > 0)
            return false;

        foreach (var folder in BuildDefaultQuickCommandFolders())
            QuickCommandFolders.Add(folder);
        return true;
    }

    private bool EnsureDefaultQuickCommandsForMissingCategories()
    {
        var defaults = BuildDefaultQuickCommands();
        var folderIds = new HashSet<string>(
            QuickCommandFolders.Select(folder => folder.Id),
            StringComparer.OrdinalIgnoreCase);
        var categoriesWithCommands = new HashSet<string>(
            QuickCommands.Select(cmd => cmd.CategoryId),
            StringComparer.OrdinalIgnoreCase);
        var existingKeys = new HashSet<string>(
            QuickCommands.Select(cmd => $"{cmd.CategoryId}|{cmd.Name}|{cmd.CommandText}"),
            StringComparer.OrdinalIgnoreCase);

        var added = false;
        foreach (var command in defaults)
        {
            if (!folderIds.Contains(command.CategoryId))
                continue;
            if (categoriesWithCommands.Contains(command.CategoryId))
                continue;

            var key = $"{command.CategoryId}|{command.Name}|{command.CommandText}";
            if (existingKeys.Contains(key))
                continue;

            QuickCommands.Add(command);
            existingKeys.Add(key);
            added = true;
        }

        return added;
    }

    private bool UpdateShellDefaultsIfNeeded(QuickCommandStore? store)
    {
        var version = store?.ShellDefaultsVersion ?? 0;
        if (version >= ShellDefaultsVersion)
            return false;

        if (!QuickCommandFolders.Any(folder =>
                string.Equals(folder.Id, QuickCommandFolderShellId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        for (var i = QuickCommands.Count - 1; i >= 0; i--)
        {
            if (string.Equals(QuickCommands[i].CategoryId, QuickCommandFolderShellId, StringComparison.OrdinalIgnoreCase))
                QuickCommands.RemoveAt(i);
        }

        foreach (var command in BuildShellQuickCommands())
            QuickCommands.Add(command);

        return true;
    }

    private bool FilterQuickCommandByFolder(object obj)
    {
        if (obj is not QuickCommandItem item)
            return false;

        var folder = SelectedQuickCommandFolder;
        if (folder is null)
            return true;

        return string.Equals(item.CategoryId, folder.Id, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsQuickCommandInFolder(QuickCommandItem item, QuickCommandFolder folder)
    {
        return string.Equals(item.CategoryId, folder.Id, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownQuickCommandFolderId(string? folderId)
    {
        if (string.IsNullOrWhiteSpace(folderId))
            return false;

        return string.Equals(folderId, QuickCommandFolderShellId, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(folderId, QuickCommandFolderGitId, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(folderId, QuickCommandFolderClaudeId, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(folderId, QuickCommandFolderCodexId, StringComparison.OrdinalIgnoreCase);
    }

    private string GetDefaultQuickCommandFolderId()
    {
        if (QuickCommandFolders.Count == 0)
            return QuickCommandFolderShellId;

        return QuickCommandFolders[0].Id;
    }

    private static bool IsValidQuickCommand(QuickCommandItem? item)
    {
        return item is not null &&
               !string.IsNullOrWhiteSpace(item.Name) &&
               !string.IsNullOrWhiteSpace(item.CommandText);
    }

    private static bool IsValidQuickCommandFolder(QuickCommandFolder? folder)
    {
        return folder is not null && !string.IsNullOrWhiteSpace(folder.Name);
    }

    private bool CleanupOrphanDraftQuickCommandFolders()
    {
        if (QuickCommandFolders.Count == 0)
            return false;

        var folderIdsWithCommands = new HashSet<string>(
            QuickCommands
                .Where(command => !string.IsNullOrWhiteSpace(command.CategoryId))
                .Select(command => command.CategoryId),
            StringComparer.OrdinalIgnoreCase);

        var removed = false;
        for (var i = QuickCommandFolders.Count - 1; i >= 0; i--)
        {
            var folder = QuickCommandFolders[i];
            if (!IsDraftQuickCommandFolderName(folder.Name))
                continue;

            if (!string.IsNullOrWhiteSpace(folder.Id) &&
                folderIdsWithCommands.Contains(folder.Id))
            {
                continue;
            }

            if (SelectedQuickCommandFolder == folder)
                SelectedQuickCommandFolder = null;

            QuickCommandFolders.RemoveAt(i);
            removed = true;
        }

        return removed;
    }

    private static bool IsDraftQuickCommandFolderName(string? name)
    {
        var normalized = name?.Trim();
        return string.Equals(normalized, NewQuickCommandFolderDraftNameZh, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, NewQuickCommandFolderDraftNameEn, StringComparison.OrdinalIgnoreCase);
    }

    private static List<QuickCommandFolder> BuildDefaultQuickCommandFolders()
    {
        return
        [
            CreateQuickCommandFolder(QuickCommandFolderShellId, "命令行"),
            CreateQuickCommandFolder(QuickCommandFolderGitId, "git"),
            CreateQuickCommandFolder(QuickCommandFolderClaudeId, "claude"),
            CreateQuickCommandFolder(QuickCommandFolderCodexId, "codex")
        ];
    }

    private static QuickCommandFolder CreateQuickCommandFolder(string id, string name)
    {
        return new QuickCommandFolder
        {
            Id = id,
            Name = name
        };
    }

    private static List<QuickCommandItem> BuildDefaultQuickCommands()
    {
        var commands = new List<QuickCommandItem>();
        commands.AddRange(BuildShellQuickCommands());
        commands.AddRange(BuildGitQuickCommands());
        commands.AddRange(BuildClaudeQuickCommands());
        commands.AddRange(BuildCodexQuickCommands());
        return commands;
    }

    private static List<QuickCommandItem> BuildShellQuickCommands()
    {
        return
        [
            CreateQuickCommand("获取命令帮助信息", "Get-Help", QuickCommandFolderShellId),
            CreateQuickCommand("列出可用命令或查找命令", "Get-Command", QuickCommandFolderShellId),
            CreateQuickCommand("查看对象的属性和方法", "Get-Member", QuickCommandFolderShellId),
            CreateQuickCommand("查看当前运行的进程", "Get-Process", QuickCommandFolderShellId),
            CreateQuickCommand("结束指定进程", "Stop-Process", QuickCommandFolderShellId),
            CreateQuickCommand("查看系统服务", "Get-Service", QuickCommandFolderShellId),
            CreateQuickCommand("启动服务", "Start-Service", QuickCommandFolderShellId),
            CreateQuickCommand("停止服务", "Stop-Service", QuickCommandFolderShellId),
            CreateQuickCommand("查看事件日志", "Get-EventLog", QuickCommandFolderShellId),
            CreateQuickCommand("清屏", "Clear-Host", QuickCommandFolderShellId)
        ];
    }

    private static List<QuickCommandItem> BuildGitQuickCommands()
    {
        return
        [
            CreateQuickCommand("查看状态", "git status -sb", QuickCommandFolderGitId),
            CreateQuickCommand("同步远端引用", "git fetch --all --prune", QuickCommandFolderGitId),
            CreateQuickCommand("快进拉取", "git pull --ff-only", QuickCommandFolderGitId),
            CreateQuickCommand("查看变更", "git diff", QuickCommandFolderGitId),
            CreateQuickCommand("查看变更文件", "git diff --name-only", QuickCommandFolderGitId),
            CreateQuickCommand("暂存全部修改", "git add -A", QuickCommandFolderGitId),
            CreateQuickCommand("提交修改", "git commit -m \"\"", QuickCommandFolderGitId),
            CreateQuickCommand("最近 20 条提交", "git log --oneline -20", QuickCommandFolderGitId),
            CreateQuickCommand("切换分支", "git switch", QuickCommandFolderGitId),
            CreateQuickCommand("推送到远端", "git push", QuickCommandFolderGitId)
        ];
    }

    private static List<QuickCommandItem> BuildClaudeQuickCommands()
    {
        return
        [
            CreateQuickCommand("清空并新会话", "/clear", QuickCommandFolderClaudeId),
            CreateQuickCommand("压缩上下文", "/compact", QuickCommandFolderClaudeId),
            CreateQuickCommand("切换模型", "/model", QuickCommandFolderClaudeId),
            CreateQuickCommand("权限管理", "/permissions", QuickCommandFolderClaudeId),
            CreateQuickCommand("计划模式", "/plan", QuickCommandFolderClaudeId),
            CreateQuickCommand("查看状态", "/status", QuickCommandFolderClaudeId),
            CreateQuickCommand("查看 diff", "/diff", QuickCommandFolderClaudeId),
            CreateQuickCommand("继续会话", "/resume", QuickCommandFolderClaudeId),
            CreateQuickCommand("初始化项目", "/init", QuickCommandFolderClaudeId),
            CreateQuickCommand("帮助", "/help", QuickCommandFolderClaudeId),
            CreateQuickCommand("退出", "/exit", QuickCommandFolderClaudeId)
        ];
    }

    private static List<QuickCommandItem> BuildCodexQuickCommands()
    {
        return
        [
            CreateQuickCommand("新会话", "/new", QuickCommandFolderCodexId),
            CreateQuickCommand("清空并新会话", "/clear", QuickCommandFolderCodexId),
            CreateQuickCommand("切换模型", "/model", QuickCommandFolderCodexId),
            CreateQuickCommand("权限/审批", "/permissions", QuickCommandFolderCodexId),
            CreateQuickCommand("压缩上下文", "/compact", QuickCommandFolderCodexId),
            CreateQuickCommand("查看状态", "/status", QuickCommandFolderCodexId),
            CreateQuickCommand("代码审查", "/review", QuickCommandFolderCodexId),
            CreateQuickCommand("恢复会话", "/resume", QuickCommandFolderCodexId),
            CreateQuickCommand("分叉会话", "/fork", QuickCommandFolderCodexId),
            CreateQuickCommand("MCP 列表", "/mcp", QuickCommandFolderCodexId),
            CreateQuickCommand("初始化项目", "/init", QuickCommandFolderCodexId),
            CreateQuickCommand("指定文件", "/mention", QuickCommandFolderCodexId),
            CreateQuickCommand("复制输出", "/copy", QuickCommandFolderCodexId),
            CreateQuickCommand("登出", "/logout", QuickCommandFolderCodexId)
        ];
    }

    private static QuickCommandItem CreateQuickCommand(string name, string commandText, string categoryId)
    {
        return new QuickCommandItem
        {
            Id = Guid.NewGuid().ToString("N"),
            CategoryId = categoryId,
            Name = name,
            CommandText = commandText,
            Description = string.Empty
        };
    }

    private static void NormalizeQuickCommandFolder(QuickCommandFolder folder)
    {
        if (string.IsNullOrWhiteSpace(folder.Id))
            folder.Id = Guid.NewGuid().ToString("N");

        folder.Name = folder.Name?.Trim() ?? string.Empty;
    }

    private void NormalizeQuickCommand(QuickCommandItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Description))
        {
            item.Name = item.Description.Trim();
            item.Description = string.Empty;
        }

        item.Name = item.Name?.Trim() ?? string.Empty;
        item.CommandText = item.CommandText?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(item.CategoryId))
        {
            var command = item.CommandText?.TrimStart() ?? string.Empty;
            if (command.StartsWith("git ", StringComparison.OrdinalIgnoreCase))
                item.CategoryId = QuickCommandFolderGitId;
            else
                item.CategoryId = GetDefaultQuickCommandFolderId();
        }
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
