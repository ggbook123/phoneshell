using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhoneShell.Core.Models;
using PhoneShell.Core.Networking;
using PhoneShell.Core.Protocol;
using PhoneShell.Core.Services;
using PhoneShell.Core.Terminals;
using PhoneShell.Core.Terminals.Windows;
using PhoneShell.Services;
using PhoneShell.Utilities;

namespace PhoneShell.ViewModels;

internal readonly record struct PendingRemoteOutput(long OutputSeq, string Data);

/// <summary>
/// Represents a single terminal tab with its own session, buffers, and state.
/// </summary>
public sealed class TerminalTab : ObservableObject, IDisposable
{
    private string _title;
    private bool _isCompactMode;
    private int _cols;
    private int _rows;
    private int _desktopCols;
    private int _desktopRows;
    private int _mobileCols;
    private int _mobileRows;
    private bool _hasMobileSession;
    private bool _isMobileControlPaused;
    private bool _isMobileViewportLocked;
    private bool _isActive;
    private int _tabNumber;
    private bool _isRemote;
    private string _remoteDeviceId = string.Empty;
    private string _remoteDeviceName = string.Empty;
    private string _remoteSessionId = string.Empty;
    private long _remoteBufferBeforeSeq;
    private long _latestBufferSnapshotSeq;
    private bool _latestBufferLoading;
    private bool _latestBufferLoaded;
    private bool _olderBufferLoading;
    private string _latestBufferRequestId = string.Empty;
    private string _olderBufferRequestId = string.Empty;

    public TerminalTab(string tabId, ShellInfo shell, int tabNumber)
    {
        TabId = tabId;
        Shell = shell;
        _tabNumber = tabNumber;
        _title = $"{shell.DisplayName} #{tabNumber}";
        SessionManager = new TerminalSessionManager();
        OutputBuffer = new TerminalOutputBuffer();
        VirtualScreen = new VirtualScreen();
        OutputStabilizer = new TerminalOutputStabilizer();
    }

    /// <summary>Constructor for remote tabs (PC-to-PC remote terminal).</summary>
    public TerminalTab(string tabId, string remoteDeviceId, string remoteDeviceName,
                       string shellDisplayName, int tabNumber)
    {
        TabId = tabId;
        Shell = new ShellInfo(shellDisplayName, shellDisplayName, "", "");
        _tabNumber = tabNumber;
        _isRemote = true;
        _remoteDeviceId = remoteDeviceId;
        _remoteDeviceName = remoteDeviceName;
        _title = $"[{remoteDeviceName}] {shellDisplayName} #{tabNumber}";
        SessionManager = new TerminalSessionManager();
        OutputBuffer = new TerminalOutputBuffer();
        VirtualScreen = new VirtualScreen();
        OutputStabilizer = new TerminalOutputStabilizer();
    }

    public string TabId { get; }
    public ShellInfo Shell { get; }
    public TerminalSessionManager SessionManager { get; }
    public TerminalOutputBuffer OutputBuffer { get; }
    public VirtualScreen VirtualScreen { get; }
    public TerminalOutputStabilizer OutputStabilizer { get; }

    internal List<PendingRemoteOutput> PendingRemoteOutputs { get; } = new();
    internal StringBuilder RemoteBufferCache { get; } = new();

    internal long RemoteBufferBeforeSeq
    {
        get => _remoteBufferBeforeSeq;
        set => _remoteBufferBeforeSeq = value;
    }

    internal long LatestBufferSnapshotSeq
    {
        get => _latestBufferSnapshotSeq;
        set => _latestBufferSnapshotSeq = value;
    }

    internal bool LatestBufferLoading
    {
        get => _latestBufferLoading;
        set => _latestBufferLoading = value;
    }

    internal bool LatestBufferLoaded
    {
        get => _latestBufferLoaded;
        set => _latestBufferLoaded = value;
    }

    internal bool OlderBufferLoading
    {
        get => _olderBufferLoading;
        set => _olderBufferLoading = value;
    }

    internal string LatestBufferRequestId
    {
        get => _latestBufferRequestId;
        set => _latestBufferRequestId = value;
    }

    internal string OlderBufferRequestId
    {
        get => _olderBufferRequestId;
        set => _olderBufferRequestId = value;
    }

    /// <summary>Cached delegate for OutputReceived subscription (allows proper unsubscribe).</summary>
    internal Action<string>? OutputHandler;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public bool IsCompactMode
    {
        get => _isCompactMode;
        set => SetProperty(ref _isCompactMode, value);
    }

    public int Cols
    {
        get => _cols;
        set => SetProperty(ref _cols, value);
    }

    public int Rows
    {
        get => _rows;
        set => SetProperty(ref _rows, value);
    }

    public int DesktopCols
    {
        get => _desktopCols;
        set => SetProperty(ref _desktopCols, value);
    }

    public int DesktopRows
    {
        get => _desktopRows;
        set => SetProperty(ref _desktopRows, value);
    }

    public int MobileCols
    {
        get => _mobileCols;
        set => SetProperty(ref _mobileCols, value);
    }

    public int MobileRows
    {
        get => _mobileRows;
        set => SetProperty(ref _mobileRows, value);
    }

    public bool HasMobileSession
    {
        get => _hasMobileSession;
        set => SetProperty(ref _hasMobileSession, value);
    }

    public bool IsMobileControlPaused
    {
        get => _isMobileControlPaused;
        set => SetProperty(ref _isMobileControlPaused, value);
    }

    public bool IsMobileViewportLocked
    {
        get => _isMobileViewportLocked;
        set => SetProperty(ref _isMobileViewportLocked, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public bool IsRemote => _isRemote;
    public string RemoteDeviceId => _remoteDeviceId;
    public string RemoteDeviceName => _remoteDeviceName;

    public string RemoteSessionId
    {
        get => _remoteSessionId;
        set => SetProperty(ref _remoteSessionId, value);
    }

    public void Dispose()
    {
        OutputStabilizer.Dispose();
        SessionManager.Dispose();
    }
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    // Key token mapping: {TOKEN} -> VT sequence
    private static readonly Dictionary<string, string> KeyTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENTER"] = "\r",
        ["ESC"] = "\x1b",
        ["TAB"] = "\t",
        ["BACKSPACE"] = "\x7f",
        ["DELETE"] = "\x1b[3~",
        ["SPACE"] = " ",
        ["CTRL+C"] = "\x03",
        ["CTRL+D"] = "\x04",
        ["CTRL+Z"] = "\x1a",
        ["CTRL+L"] = "\x0c",
        ["CTRL+A"] = "\x01",
        ["CTRL+E"] = "\x05",
        ["CTRL+K"] = "\x0b",
        ["CTRL+U"] = "\x15",
        ["CTRL+W"] = "\x17",
        ["CTRL+R"] = "\x12",
        ["CTRL+X"] = "\x18",
        ["UP"] = "\x1b[A",
        ["DOWN"] = "\x1b[B",
        ["RIGHT"] = "\x1b[C",
        ["LEFT"] = "\x1b[D",
        ["HOME"] = "\x1b[H",
        ["END"] = "\x1b[F",
        ["PAGEUP"] = "\x1b[5~",
        ["PAGEDOWN"] = "\x1b[6~",
        ["INSERT"] = "\x1b[2~",
        ["F1"] = "\x1bOP",
        ["F2"] = "\x1bOQ",
        ["F3"] = "\x1bOR",
        ["F4"] = "\x1bOS",
        ["F5"] = "\x1b[15~",
        ["F6"] = "\x1b[17~",
        ["F7"] = "\x1b[18~",
        ["F8"] = "\x1b[19~",
        ["F9"] = "\x1b[20~",
        ["F10"] = "\x1b[21~",
        ["F11"] = "\x1b[23~",
        ["F12"] = "\x1b[24~",
    };

    private static readonly Regex KeyTokenRegex = new(@"\{([A-Za-z0-9+_]+)\}", RegexOptions.Compiled);
    private const string WindowClosePreferenceAsk = "ask";
    private const string WindowClosePreferenceMinimizeToTray = "minimize_to_tray";
    private const string WindowClosePreferenceExit = "exit";
    private const string DefaultLocalSessionTargetZh = "PC端";
    private const string DefaultLocalSessionTargetEn = "This PC";

    private readonly DeviceIdentityStore _identityStore;
    private readonly QrCodeService _qrCodeService;
    private readonly QrPayloadBuilder _payloadBuilder;
    private readonly AiSettingsStore _aiSettingsStore;
    private readonly AiChatService _aiChatService;
    private readonly ServerSettingsStore _serverSettingsStore;
    private readonly GroupStore _groupStore;
    private readonly TerminalHistoryStore _historyStore;
    private readonly Dispatcher _dispatcher;
    private DeviceIdentity _identity = new();
    private string _deviceId = string.Empty;
    private string _qrPayload = string.Empty;
    private BitmapImage? _qrImage;
    private string _connectionStatus = "Initializing";
    private ControlOwner _controlOwner = ControlOwner.Pc;
    private bool _isMobileConnected;
    private string _sessionStatus = "No session";

    // AI fields
    private AiSettings _aiSettings;
    private string _aiEndpoint = string.Empty;
    private string _aiApiKey = string.Empty;
    private string _aiModelName = string.Empty;
    private string _chatInput = string.Empty;
    private bool _isAiLoading;

    // Auto-exec fields
    private CancellationTokenSource? _autoExecCts;
    private int _autoExecGeneration;
    private bool _isAutoExecuting;
    private int _autoExecStep;
    private const int AutoExecMaxSteps = 10;
    private const int MaxDebugLogEntries = 100;
    private static readonly bool EnableDebugLog = true;
    private static readonly string NetLogFile =
        System.IO.Path.Combine(AppContext.BaseDirectory, "data", "net-debug.log");
    private const int RemoteLatestBufferPageChars = 240000;
    private const int RemoteOlderBufferPageChars = 20000;
    private string _autoExecStatus = string.Empty;

    // Server/Client fields
    private ServerSettings _serverSettings;
    private bool _isAutoMode;
    private bool _isRelayServer;
    private bool _relayModeUserOverride;
    private bool _suppressRelayModeOverride;
    private int _serverPort = 9090;
    private string _relayServerAddress = string.Empty;
    private string _windowClosePreference = WindowClosePreferenceAsk;
    private string _localSessionTargetLabel = DefaultLocalSessionTargetZh;
    private bool _isServerRunning;
    private string _serverStatus = "Stopped";
    private string _primaryRelayAddress = string.Empty;
    private string _relayReachableAddresses = string.Empty;
    private RelayServer? _relayServer;
    private RelayClient? _relayClient;
    private bool _isStandaloneMode;

    // Group fields
    private string _groupSecret = string.Empty;
    private string _groupId = string.Empty;
    private bool _isGroupJoined;
    private string _groupStatus = string.Empty;
    private string? _selectedGroupDeviceId;

    // Shell selection fields
    private readonly IShellLocator _shellLocator;
    private ObservableCollection<ShellInfo> _availableShells = new();
    private ShellInfo? _selectedShell;

    // Multi-tab fields
    private TerminalTab? _activeTab;
    private int _tabCounter;

    public MainViewModel()
    {
        _identityStore = new DeviceIdentityStore(AppContext.BaseDirectory);
        _qrCodeService = new QrCodeService();
        _payloadBuilder = new QrPayloadBuilder();
        _aiSettingsStore = new AiSettingsStore(AppContext.BaseDirectory);
        _aiChatService = new AiChatService();
        _serverSettingsStore = new ServerSettingsStore(AppContext.BaseDirectory);
        _groupStore = new GroupStore(AppContext.BaseDirectory);
        _historyStore = new TerminalHistoryStore(AppContext.BaseDirectory);
        _shellLocator = new WindowsShellLocator();
        _dispatcher = Application.Current.Dispatcher;
        _aiSettings = _aiSettingsStore.Load();
        _aiEndpoint = _aiSettings.ApiEndpoint;
        _aiApiKey = _aiSettings.ApiKey;
        _aiModelName = _aiSettings.ModelName;

        _serverSettings = _serverSettingsStore.Load();
        _isAutoMode = _serverSettings.AutoMode;
        _isRelayServer = _serverSettings.IsRelayServer;
        _serverPort = _serverSettings.Port;
        _relayServerAddress = _serverSettings.RelayServerAddress ?? string.Empty;
        _groupSecret = _serverSettings.GroupSecret ?? string.Empty;
        _windowClosePreference = NormalizeWindowClosePreference(_serverSettings.WindowClosePreference);

        RefreshQrCommand = new RelayCommand(RefreshQr);
        ForceDisconnectCommand = new RelayCommand(ForceDisconnect, () => IsMobileConnected);
        SendMessageCommand = new AsyncRelayCommand(SendMessageAsync);
        SaveAiSettingsCommand = new RelayCommand(SaveAiSettings);
        StopAutoExecCommand = new RelayCommand(StopAutoExec);
        SaveServerSettingsCommand = new RelayCommand(SaveServerSettings);
        CopyGroupSecretCommand = new RelayCommand(() => CopyToClipboard(GroupSecret),
            () => !string.IsNullOrWhiteSpace(GroupSecret));
        CopyGroupIdCommand = new RelayCommand(() => CopyToClipboard(GroupId),
            () => !string.IsNullOrWhiteSpace(GroupId));
        RefreshShellsCommand = new RelayCommand(RefreshShells);
        StartNetworkCommand = new AsyncRelayCommand(StartNetworkAsync, () => !IsServerRunning);
        StopNetworkCommand = new RelayCommand(StopNetwork, () => IsServerRunning);
        InitializeGroupCommand = new RelayCommand(InitializeGroupData);
        NewSessionCommand = new AsyncRelayCommand(CreateNewSessionAsync, CanCreateNewSession);
        CloseTabCommand = new RelayCommand<string>(CloseTab);
        SwitchTabCommand = new RelayCommand<string>(SwitchTab);
        ToggleCompactModeCommand = new RelayCommand<string>(ToggleCompactMode);

        InitializeDesktopCommandCenter();
        Initialize();
    }

    // --- Events ---

    /// <summary>
    /// Raised when a tab switch occurs. MainWindow subscribes to reset xterm.js and replay output.
    /// Carries the new active tab (null if no tabs).
    /// </summary>
    public event Action<TerminalTab?>? ActiveTabChanged;

    /// <summary>
    /// Raised when terminal output is received for the active tab. MainWindow pushes to WebView2.
    /// </summary>
    public event Action<string>? TerminalOutputForwarded;

    /// <summary>
    /// Raised when the terminal buffer should be replaced with a full replay (e.g. history load).
    /// </summary>
    public event Action<string>? TerminalBufferReplaceRequested;

    /// <summary>
    /// Raised when the desktop terminal viewport should lock to explicit terminal geometry.
    /// </summary>
    public event Action<int, int>? TerminalViewportLockRequested;

    /// <summary>
    /// Raised when the desktop terminal viewport should return to auto-fit mode.
    /// </summary>
    public event Action? TerminalViewportAutoFitRequested;

    // --- Properties ---

    public string DeviceId
    {
        get => _deviceId;
        private set => SetProperty(ref _deviceId, value);
    }

    public string QrPayload
    {
        get => _qrPayload;
        private set => SetProperty(ref _qrPayload, value);
    }

    public BitmapImage? QrImage
    {
        get => _qrImage;
        private set => SetProperty(ref _qrImage, value);
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    public ControlOwner ControlOwner
    {
        get => _controlOwner;
        private set
        {
            if (SetProperty(ref _controlOwner, value))
                OnPropertyChanged(nameof(ControlOwnerText));
        }
    }

    public string ControlOwnerText => ControlOwner switch
    {
        ControlOwner.Pc => "PC (local)",
        ControlOwner.Mobile => "Mobile",
        _ => "None"
    };

    public bool IsMobileConnected
    {
        get => _isMobileConnected;
        private set
        {
            if (SetProperty(ref _isMobileConnected, value))
                ForceDisconnectCommand.RaiseCanExecuteChanged();
        }
    }

    public string SessionStatus
    {
        get => _sessionStatus;
        set => SetProperty(ref _sessionStatus, value);
    }

    // AI properties
    public string AiEndpoint
    {
        get => _aiEndpoint;
        set => SetProperty(ref _aiEndpoint, value);
    }

    public string AiApiKey
    {
        get => _aiApiKey;
        set => SetProperty(ref _aiApiKey, value);
    }

    public string AiModelName
    {
        get => _aiModelName;
        set => SetProperty(ref _aiModelName, value);
    }

    public string ChatInput
    {
        get => _chatInput;
        set => SetProperty(ref _chatInput, value);
    }

    public bool IsAiLoading
    {
        get => _isAiLoading;
        set => SetProperty(ref _isAiLoading, value);
    }

    public bool IsAutoExecuting
    {
        get => _isAutoExecuting;
        private set
        {
            if (SetProperty(ref _isAutoExecuting, value))
                OnPropertyChanged(nameof(IsNotAutoExecuting));
        }
    }

    public bool IsNotAutoExecuting => !IsAutoExecuting;

    public string AutoExecStatus
    {
        get => _autoExecStatus;
        private set => SetProperty(ref _autoExecStatus, value);
    }

    // Server properties
    public bool IsAutoMode
    {
        get => _isAutoMode;
        set
        {
            if (SetProperty(ref _isAutoMode, value))
            {
                _relayModeUserOverride = false;
                if (_isAutoMode)
                    ApplyAutoRelayMode();
                UpdateRelayAddressPreview();
            }
        }
    }

    public bool IsRelayServer
    {
        get => _isRelayServer;
        set
        {
            if (SetProperty(ref _isRelayServer, value))
            {
                if (IsAutoMode && !_suppressRelayModeOverride)
                    _relayModeUserOverride = true;
                UpdateRelayAddressPreview();
            }
        }
    }

    public int ServerPort
    {
        get => _serverPort;
        set
        {
            if (SetProperty(ref _serverPort, value))
                UpdateRelayAddressPreview();
        }
    }

    public string RelayServerAddress
    {
        get => _relayServerAddress;
        set => SetProperty(ref _relayServerAddress, value);
    }

    public string WindowClosePreference
    {
        get => _windowClosePreference;
        set
        {
            var normalized = NormalizeWindowClosePreference(value);
            if (!SetProperty(ref _windowClosePreference, normalized))
                return;

            _serverSettings.WindowClosePreference = normalized;
            _serverSettingsStore.Save(_serverSettings);
        }
    }

    public bool ShouldAskBeforeClose =>
        string.Equals(_windowClosePreference, WindowClosePreferenceAsk, StringComparison.Ordinal);

    public bool ShouldMinimizeToTrayOnClose =>
        string.Equals(_windowClosePreference, WindowClosePreferenceMinimizeToTray, StringComparison.Ordinal);

    public bool IsServerRunning
    {
        get => _isServerRunning;
        private set
        {
            if (SetProperty(ref _isServerRunning, value))
            {
                OnPropertyChanged(nameof(IsServerStopped));
                StartNetworkCommand.RaiseCanExecuteChanged();
                StopNetworkCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsServerStopped => !IsServerRunning;

    public string ServerStatus
    {
        get => _serverStatus;
        private set => SetProperty(ref _serverStatus, value);
    }

    public string PrimaryRelayAddress
    {
        get => _primaryRelayAddress;
        private set => SetProperty(ref _primaryRelayAddress, value);
    }

    public string RelayReachableAddresses
    {
        get => _relayReachableAddresses;
        private set => SetProperty(ref _relayReachableAddresses, value);
    }

    // Group properties
    public string GroupSecret
    {
        get => _groupSecret;
        set
        {
            if (SetProperty(ref _groupSecret, value))
                CopyGroupSecretCommand.RaiseCanExecuteChanged();
        }
    }

    public string GroupId
    {
        get => _groupId;
        private set
        {
            if (SetProperty(ref _groupId, value))
                CopyGroupIdCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsGroupJoined
    {
        get => _isGroupJoined;
        private set => SetProperty(ref _isGroupJoined, value);
    }

    public string GroupStatus
    {
        get => _groupStatus;
        private set => SetProperty(ref _groupStatus, value);
    }

    public ObservableCollection<GroupDeviceItem> GroupMembers { get; } = new();
    public ObservableCollection<ShellInfo> SessionTargetShells { get; } = new();

    // Shell selection properties
    public ObservableCollection<ShellInfo> AvailableShells
    {
        get => _availableShells;
        private set => SetProperty(ref _availableShells, value);
    }

    public ShellInfo? SelectedShell
    {
        get => _selectedShell;
        set
        {
            if (SetProperty(ref _selectedShell, value))
                NewSessionCommand.RaiseCanExecuteChanged();
        }
    }

    // Multi-tab properties
    public ObservableCollection<TerminalTab> Tabs { get; } = new();
    public ObservableCollection<TerminalTab> VisibleTabs { get; } = new();

    public TerminalTab? ActiveTab
    {
        get => _activeTab;
        private set
        {
            if (SetProperty(ref _activeTab, value))
            {
                OnPropertyChanged(nameof(HasTabs));
                OnPropertyChanged(nameof(HasActiveSessionInputTarget));
                UpdateExecuteTerminalCommand();
                OnActiveSessionTargetChanged();
            }
        }
    }

    public bool HasTabs => Tabs.Count > 0;
    public bool HasVisibleTabs => VisibleTabs.Count > 0;
    public GroupDeviceItem? SelectedGroupDevice => GroupMembers.FirstOrDefault(m => m.IsSelected);
    public string CurrentSessionTargetDeviceId => SelectedGroupDevice?.DeviceId ?? _identity.DeviceId;
    public string CurrentSessionTargetDisplayName =>
        SelectedGroupDevice?.DisplayName
        ?? (!string.IsNullOrWhiteSpace(_identity.DisplayName) ? _identity.DisplayName : _localSessionTargetLabel);
    public string NewSessionTargetButtonText => SelectedGroupDevice?.DisplayName ?? _localSessionTargetLabel;
    public bool IsCurrentSessionTargetRemote =>
        !string.IsNullOrWhiteSpace(SelectedGroupDevice?.DeviceId) &&
        !string.Equals(SelectedGroupDevice.DeviceId, _identity.DeviceId, StringComparison.Ordinal);

    public ObservableCollection<ChatMessage> ChatMessages { get; } = new();
    public ObservableCollection<string> DebugLogs { get; } = new();
    public TerminalSnapshotService SnapshotService { get; } = new();
    public Action<string>? ExecuteTerminalCommand { get; set; }

    // Commands
    public RelayCommand RefreshQrCommand { get; }
    public RelayCommand ForceDisconnectCommand { get; }
    public AsyncRelayCommand SendMessageCommand { get; }
    public RelayCommand SaveAiSettingsCommand { get; }
    public RelayCommand StopAutoExecCommand { get; }
    public RelayCommand SaveServerSettingsCommand { get; }
    public RelayCommand CopyGroupSecretCommand { get; }
    public RelayCommand CopyGroupIdCommand { get; }
    public RelayCommand RefreshShellsCommand { get; }
    public AsyncRelayCommand StartNetworkCommand { get; }
    public RelayCommand StopNetworkCommand { get; }
    public RelayCommand InitializeGroupCommand { get; }
    public AsyncRelayCommand NewSessionCommand { get; }
    public RelayCommand<string> CloseTabCommand { get; }
    public RelayCommand<string> SwitchTabCommand { get; }
    public RelayCommand<string> ToggleCompactModeCommand { get; }

    public void Dispose()
    {
        _autoExecCts?.Cancel();
        _autoExecCts?.Dispose();
        _aiChatService.Dispose();
        _historyStore.Dispose();

        foreach (var tab in Tabs)
            tab.Dispose();
        Tabs.Clear();

        _relayServer?.Dispose();
        _relayClient?.Dispose();
    }

    // --- Multi-Tab Management ---

    public async Task CreateNewSessionAsync()
    {
        var shell = ResolveSessionTargetShell();
        if (shell is null)
            return;

        if (IsCurrentSessionTargetRemote)
        {
            await OpenRemoteTerminalAsync(CurrentSessionTargetDeviceId, shell.Id);
            return;
        }

        CreateTab(shell, 0, 0, activate: true);
    }

    public TerminalTab CreateTab(ShellInfo shell, int cols, int rows, bool activate)
    {
        _tabCounter++;
        var tabId = Guid.NewGuid().ToString("N")[..8];
        var tab = new TerminalTab(tabId, shell, _tabCounter);

        tab.OutputHandler = text => OnTabOutput(tab, text);
        tab.SessionManager.OutputReceived += tab.OutputHandler;

        Tabs.Add(tab);
        OnPropertyChanged(nameof(HasTabs));

        RefreshVisibleTabs(activate ? tab : null);
        NotifyLocalSessionListChanged();

        if (cols > 0 && rows > 0)
        {
            StartTabTerminal(tab, cols, rows);
        }

        return tab;
    }

    public TerminalTab CreateRemoteTab(string remoteDeviceId, string remoteDeviceName,
                                        string shellId, string remoteSessionId, int cols, int rows,
                                        bool activate, string? titleOverride = null)
    {
        _tabCounter++;
        var tabId = Guid.NewGuid().ToString("N")[..8];
        var tab = new TerminalTab(tabId, remoteDeviceId, remoteDeviceName, shellId, _tabCounter);
        tab.RemoteSessionId = remoteSessionId;
        tab.Cols = cols;
        tab.Rows = rows;
        tab.VirtualScreen.Resize(cols, rows);
        if (!string.IsNullOrWhiteSpace(titleOverride))
            tab.Title = titleOverride;

        Tabs.Add(tab);
        OnPropertyChanged(nameof(HasTabs));
        RefreshVisibleTabs(activate ? tab : null);

        return tab;
    }

    /// <summary>
    /// Write input to a tab — routes to local ConPTY or remote device as appropriate.
    /// </summary>
    public void WriteTabInput(TerminalTab tab, string data)
    {
        if (tab.IsRemote)
        {
            if (_relayClient is not null && _relayClient.IsConnected)
                _ = _relayClient.SendTerminalInputAsync(tab.RemoteDeviceId, tab.RemoteSessionId, data);
            else if (_relayServer is not null && _relayServer.IsRunning)
                _ = _relayServer.ForwardTerminalInputToDevice(tab.RemoteDeviceId, tab.RemoteSessionId, data);
        }
        else
        {
            tab.SessionManager.WriteInput(data);
        }
    }

    /// <summary>
    /// Request to open a terminal on a remote device.
    /// </summary>
    public async Task OpenRemoteTerminalAsync(string deviceId, string shellId)
    {
        if (_relayClient is not null && _relayClient.IsConnected)
        {
            await _relayClient.SendTerminalOpenAsync(deviceId, shellId);
            OnNetworkLog($"Sent terminal.open to device={deviceId}, shell={shellId}");
        }
        else if (_relayServer is not null && _relayServer.IsRunning)
        {
            await _relayServer.SendTerminalOpenToDevice(deviceId, shellId, _identity.DeviceId);
        }
    }

    private void StartTabTerminal(TerminalTab tab, int cols, int rows)
    {
        if (tab.SessionManager.IsRunning) return;

        tab.Cols = cols;
        tab.Rows = rows;
        tab.VirtualScreen.Resize(cols, rows);

        var session = new ConPtySession();
        tab.SessionManager.Start(session, tab.Shell, cols, rows);
        SessionStatus = $"Running ({tab.Shell.DisplayName})";
    }

    public void StartActiveTabTerminal(int cols, int rows)
    {
        if (ActiveTab is null) return;
        StartTabTerminal(ActiveTab, cols, rows);
    }

    public async Task SelectGroupDeviceAsync(string? deviceId)
    {
        SetSelectedGroupDevice(deviceId);
        RefreshVisibleTabs();

        if (!IsCurrentSessionTargetRemote)
            return;

        await RequestRemoteSessionListAsync(CurrentSessionTargetDeviceId);
    }

    public async Task OpenSessionOnCurrentTargetAsync(string shellId)
    {
        if (string.IsNullOrWhiteSpace(shellId))
            return;

        var shell = ResolveSessionTargetShell(shellId);
        if (shell is null)
            return;

        if (IsCurrentSessionTargetRemote)
        {
            await OpenRemoteTerminalAsync(CurrentSessionTargetDeviceId, shell.Id);
            return;
        }

        CreateTab(shell, 0, 0, activate: true);
    }

    private void SetSelectedGroupDevice(string? deviceId)
    {
        var normalizedId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        _selectedGroupDeviceId = normalizedId;

        foreach (var member in GroupMembers)
            member.IsSelected = string.Equals(member.DeviceId, normalizedId, StringComparison.Ordinal);

        RefreshSessionTargetShells();
        OnPropertyChanged(nameof(SelectedGroupDevice));
        OnPropertyChanged(nameof(CurrentSessionTargetDeviceId));
        OnPropertyChanged(nameof(CurrentSessionTargetDisplayName));
        OnPropertyChanged(nameof(NewSessionTargetButtonText));
        OnPropertyChanged(nameof(IsCurrentSessionTargetRemote));
    }

    private void RefreshSessionTargetShells()
    {
        SessionTargetShells.Clear();

        IEnumerable<ShellInfo> shells = SelectedGroupDevice is not null
            ? SelectedGroupDevice.AvailableShells
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(ResolveShellInfo)
            : AvailableShells;

        foreach (var shell in shells)
            SessionTargetShells.Add(shell);

        NewSessionCommand.RaiseCanExecuteChanged();
    }

    private ShellInfo ResolveShellInfo(string shellId)
    {
        return AvailableShells.FirstOrDefault(s =>
                   string.Equals(s.Id, shellId, StringComparison.OrdinalIgnoreCase))
               ?? new ShellInfo(shellId, shellId, string.Empty, string.Empty);
    }

    private ShellInfo? ResolveSessionTargetShell(string? requestedShellId = null)
    {
        if (IsCurrentSessionTargetRemote)
        {
            if (!string.IsNullOrWhiteSpace(requestedShellId))
            {
                return SessionTargetShells.FirstOrDefault(s =>
                    string.Equals(s.Id, requestedShellId, StringComparison.OrdinalIgnoreCase));
            }

            if (SelectedShell is not null)
            {
                var matchingSelected = SessionTargetShells.FirstOrDefault(s =>
                    string.Equals(s.Id, SelectedShell.Id, StringComparison.OrdinalIgnoreCase));
                if (matchingSelected is not null)
                    return matchingSelected;
            }

            return SessionTargetShells.FirstOrDefault();
        }

        if (!string.IsNullOrWhiteSpace(requestedShellId))
        {
            return AvailableShells.FirstOrDefault(s =>
                string.Equals(s.Id, requestedShellId, StringComparison.OrdinalIgnoreCase));
        }

        return SelectedShell ?? AvailableShells.FirstOrDefault() ?? _shellLocator.GetDefaultShell();
    }

    private bool CanCreateNewSession()
    {
        return ResolveSessionTargetShell() is not null;
    }

    private bool IsTabInCurrentTarget(TerminalTab tab)
    {
        if (IsCurrentSessionTargetRemote)
        {
            return tab.IsRemote &&
                   string.Equals(tab.RemoteDeviceId, CurrentSessionTargetDeviceId, StringComparison.Ordinal);
        }

        return !tab.IsRemote;
    }

    private void RefreshVisibleTabs(TerminalTab? preferredActiveTab = null)
    {
        var nextVisibleTabs = Tabs.Where(IsTabInCurrentTarget).ToList();

        VisibleTabs.Clear();
        foreach (var tab in nextVisibleTabs)
            VisibleTabs.Add(tab);

        OnPropertyChanged(nameof(HasVisibleTabs));

        TerminalTab? nextActiveTab = null;
        if (preferredActiveTab is not null && nextVisibleTabs.Contains(preferredActiveTab))
        {
            nextActiveTab = preferredActiveTab;
        }
        else if (ActiveTab is not null && nextVisibleTabs.Contains(ActiveTab))
        {
            nextActiveTab = ActiveTab;
        }
        else if (nextVisibleTabs.Count > 0)
        {
            nextActiveTab = nextVisibleTabs[0];
        }

        if (!ReferenceEquals(ActiveTab, nextActiveTab))
            SetActiveTab(nextActiveTab);
    }

    private async Task RequestRemoteSessionListAsync(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return;

        try
        {
            if (_relayClient is not null && _relayClient.IsConnected)
            {
                await _relayClient.SendSessionListRequestAsync(deviceId);
                return;
            }

            if (_relayServer is not null && _relayServer.IsRunning)
            {
                await _relayServer.RequestSessionListFromDeviceAsync(deviceId);
            }
        }
        catch (Exception ex)
        {
            OnNetworkLog($"Session list request failed for {deviceId}: {ex.Message}");
        }
    }

    private void ApplyRemoteSessionList(string deviceId, IReadOnlyCollection<SessionInfo> sessions)
    {
        var member = GroupMembers.FirstOrDefault(m => string.Equals(m.DeviceId, deviceId, StringComparison.Ordinal));
        var deviceName = member?.DisplayName ?? deviceId[..Math.Min(8, deviceId.Length)];
        var sessionsById = sessions.ToDictionary(s => s.SessionId, StringComparer.Ordinal);

        foreach (var staleTab in Tabs.Where(t =>
                     t.IsRemote &&
                     string.Equals(t.RemoteDeviceId, deviceId, StringComparison.Ordinal) &&
                     !sessionsById.ContainsKey(t.RemoteSessionId)).ToList())
        {
            RemoveTab(staleTab, requestRemoteClose: false);
        }

        foreach (var session in sessions)
        {
            var existingTab = Tabs.FirstOrDefault(t =>
                t.IsRemote &&
                string.Equals(t.RemoteDeviceId, deviceId, StringComparison.Ordinal) &&
                string.Equals(t.RemoteSessionId, session.SessionId, StringComparison.Ordinal));

            var desiredTitle = string.IsNullOrWhiteSpace(session.Title)
                ? $"[{deviceName}] {session.ShellId}"
                : $"[{deviceName}] {session.Title}";

            if (existingTab is not null)
            {
                existingTab.Title = desiredTitle;
                continue;
            }

            var tab = CreateRemoteTab(deviceId, deviceName, session.ShellId, session.SessionId,
                cols: 120, rows: 30, activate: false, titleOverride: desiredTitle);
            StartRemoteBufferSync(tab);
        }

        if (string.Equals(CurrentSessionTargetDeviceId, deviceId, StringComparison.Ordinal))
            RefreshVisibleTabs();
    }

    private void UpdateGroupMembers(IEnumerable<GroupMemberInfo> members)
    {
        var memberList = members.ToList();
        var previous = GroupMembers.ToDictionary(m => m.DeviceId, StringComparer.Ordinal);

        GroupMembers.Clear();
        foreach (var member in memberList)
        {
            if (!previous.TryGetValue(member.DeviceId, out var item))
                item = new GroupDeviceItem(member);
            else
                item.Update(member);

            item.IsSelected = string.Equals(member.DeviceId, _selectedGroupDeviceId, StringComparison.Ordinal);
            GroupMembers.Add(item);
        }

        if (!string.IsNullOrWhiteSpace(_selectedGroupDeviceId) &&
            GroupMembers.All(m => !string.Equals(m.DeviceId, _selectedGroupDeviceId, StringComparison.Ordinal)))
        {
            _selectedGroupDeviceId = null;
        }

        RefreshSessionTargetShells();
        OnPropertyChanged(nameof(SelectedGroupDevice));
        OnPropertyChanged(nameof(CurrentSessionTargetDeviceId));
        OnPropertyChanged(nameof(CurrentSessionTargetDisplayName));
        OnPropertyChanged(nameof(NewSessionTargetButtonText));
        OnPropertyChanged(nameof(IsCurrentSessionTargetRemote));
        RefreshVisibleTabs();
    }

    private void SetActiveTab(TerminalTab? tab)
    {
        foreach (var t in Tabs)
            t.IsActive = t == tab;

        ActiveTab = tab;
        UpdateMobileConnectionState();
        ActiveTabChanged?.Invoke(tab);
    }

    private void SwitchTab(string? tabId)
    {
        if (string.IsNullOrEmpty(tabId)) return;
        var tab = VisibleTabs.FirstOrDefault(t => t.TabId == tabId);
        if (tab is null || tab == ActiveTab) return;
        SetActiveTab(tab);
    }

    private void CloseTab(string? tabId)
    {
        if (string.IsNullOrEmpty(tabId)) return;
        var tab = Tabs.FirstOrDefault(t => t.TabId == tabId);
        if (tab is null) return;
        RemoveTab(tab, requestRemoteClose: true);
    }

    public void CloseAllTabs()
    {
        var tabsToClose = Tabs.ToList();
        if (tabsToClose.Count == 0)
            return;

        foreach (var tab in tabsToClose)
        {
            RemoveTab(tab, requestRemoteClose: true);
        }
    }

    public string GetEditableSessionTitle(TerminalTab tab)
    {
        if (tab is null) return string.Empty;
        return tab.IsRemote ? ExtractRemoteTitle(tab.Title) : tab.Title;
    }

    public void RenameTab(TerminalTab tab, string newTitle)
    {
        if (tab is null) return;
        var trimmed = newTitle?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed)) return;

        if (tab.IsRemote)
        {
            var prefix = ExtractRemoteTitlePrefix(tab.Title);
            if (prefix.Length == 0 && !string.IsNullOrWhiteSpace(tab.RemoteDeviceName))
            {
                prefix = $"[{tab.RemoteDeviceName}] ";
            }
            tab.Title = $"{prefix}{trimmed}";

            if (_relayClient is not null && _relayClient.IsConnected)
            {
                _ = _relayClient.SendSessionRenameAsync(tab.RemoteDeviceId, tab.RemoteSessionId, trimmed);
            }
            else if (_relayServer is not null && _relayServer.IsRunning)
            {
                _ = _relayServer.ForwardSessionRenameToDeviceAsync(tab.RemoteDeviceId, tab.RemoteSessionId, trimmed);
            }

            return;
        }

        tab.Title = trimmed;
        NotifyLocalSessionListChanged();
    }

    private static string ExtractRemoteTitle(string title)
    {
        var prefix = ExtractRemoteTitlePrefix(title);
        return prefix.Length > 0 ? title[prefix.Length..] : title;
    }

    private static string ExtractRemoteTitlePrefix(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        if (title[0] == '[')
        {
            var idx = title.IndexOf("] ", StringComparison.Ordinal);
            if (idx > 0)
                return title[..(idx + 2)];
        }
        return string.Empty;
    }

    private void RemoveTab(TerminalTab tab, bool requestRemoteClose)
    {
        var visibleTabsSnapshot = VisibleTabs.ToList();
        var currentVisibleIndex = visibleTabsSnapshot.IndexOf(tab);

        if (requestRemoteClose && tab.IsRemote && !string.IsNullOrEmpty(tab.RemoteSessionId))
        {
            if (_relayClient is not null && _relayClient.IsConnected)
                _ = _relayClient.SendTerminalCloseAsync(tab.RemoteDeviceId, tab.RemoteSessionId);
        }

        if (!tab.IsRemote)
            NotifyLocalTerminalClosed(tab);

        tab.SessionManager.OutputReceived -= tab.OutputHandler;

        TerminalTab? nextActiveTab = null;
        if (ActiveTab == tab)
        {
            if (currentVisibleIndex >= 0)
            {
                var nextIndex = Math.Min(currentVisibleIndex, visibleTabsSnapshot.Count - 2);
                if (nextIndex >= 0)
                {
                    nextActiveTab = visibleTabsSnapshot
                        .Where(t => t != tab)
                        .ElementAtOrDefault(nextIndex);
                }
            }
        }

        tab.Dispose();
        RemoveTerminalInputDraft(tab.TabId);
        Tabs.Remove(tab);
        OnPropertyChanged(nameof(HasTabs));
        RefreshVisibleTabs(nextActiveTab);

        if (ActiveTab is null)
            SessionStatus = "No session";

        if (!tab.IsRemote)
            NotifyLocalSessionListChanged();

        UpdateMobileConnectionState();
    }

    private void ToggleCompactMode(string? tabId)
    {
        if (string.IsNullOrEmpty(tabId)) return;
        var tab = Tabs.FirstOrDefault(t => t.TabId == tabId);
        if (tab is null) return;

        if (TryGetMobileViewport(tab, out var mobileCols, out var mobileRows))
        {
            if (tab.IsCompactMode && !tab.IsMobileControlPaused)
            {
                PauseMobileViewport(tab);
            }
            else
            {
                ShowMobileViewport(tab, mobileCols, mobileRows);
            }

            return;
        }

        tab.IsCompactMode = !tab.IsCompactMode;
        tab.IsMobileControlPaused = false;

        if (tab == ActiveTab)
        {
            if (tab.IsCompactMode)
            {
                // Switch to mobile-size viewport (e.g. 80x24)
                TerminalViewportLockRequested?.Invoke(80, 24);
                ApplyTabTerminalSize(tab, 80, 24);
            }
            else
            {
                // Restore to desktop size
                // Clear mobile lock so HandleLocalViewportResize will apply the new size
                tab.IsMobileViewportLocked = false;

                if (tab.DesktopCols > 0 && tab.DesktopRows > 0)
                {
                    ApplyTabTerminalSize(tab, tab.DesktopCols, tab.DesktopRows);
                }

                TerminalViewportAutoFitRequested?.Invoke();
            }
        }

        UpdateMobileConnectionState();
    }

    private bool TryGetMobileViewport(TerminalTab tab, out int cols, out int rows)
    {
        cols = tab.MobileCols;
        rows = tab.MobileRows;
        return tab.HasMobileSession && cols > 0 && rows > 0;
    }

    private void ShowMobileViewport(TerminalTab tab, int cols, int rows)
    {
        tab.HasMobileSession = true;
        tab.MobileCols = cols;
        tab.MobileRows = rows;
        tab.IsCompactMode = true;
        tab.IsMobileViewportLocked = true;
        tab.IsMobileControlPaused = false;

        if (tab == ActiveTab)
        {
            TerminalViewportLockRequested?.Invoke(cols, rows);
        }

        ApplyTabTerminalSize(tab, cols, rows);
        UpdateMobileConnectionState();
        SessionStatus = $"Running ({cols}x{rows})";
    }

    private void PauseMobileViewport(TerminalTab tab)
    {
        if (tab.DesktopCols <= 0 || tab.DesktopRows <= 0)
        {
            tab.DesktopCols = 120;
            tab.DesktopRows = 30;
        }

        tab.IsCompactMode = false;
        tab.IsMobileViewportLocked = false;
        tab.IsMobileControlPaused = true;

        if (tab.DesktopCols > 0 && tab.DesktopRows > 0)
        {
            ApplyTabTerminalSize(tab, tab.DesktopCols, tab.DesktopRows);
        }

        if (tab == ActiveTab)
        {
            TerminalViewportAutoFitRequested?.Invoke();
        }

        UpdateMobileConnectionState();
        SessionStatus = "Running (mobile paused)";
    }

    private void ClearMobileViewport(TerminalTab tab)
    {
        var hadMobileSession = tab.HasMobileSession || tab.IsMobileViewportLocked || tab.IsMobileControlPaused;

        tab.IsCompactMode = false;
        tab.IsMobileViewportLocked = false;
        tab.IsMobileControlPaused = false;
        tab.HasMobileSession = false;
        tab.MobileCols = 0;
        tab.MobileRows = 0;

        if (hadMobileSession && tab.DesktopCols > 0 && tab.DesktopRows > 0)
        {
            ApplyTabTerminalSize(tab, tab.DesktopCols, tab.DesktopRows);
        }

        if (hadMobileSession && tab == ActiveTab)
        {
            TerminalViewportAutoFitRequested?.Invoke();
            SessionStatus = "Running";
        }

        UpdateMobileConnectionState();
    }

    private void UpdateMobileConnectionState()
    {
        var mobileTab = ActiveTab?.HasMobileSession == true
            ? ActiveTab
            : Tabs.FirstOrDefault(t => t.HasMobileSession);

        if (mobileTab is null)
        {
            IsMobileConnected = false;
            ControlOwner = ControlOwner.Pc;
            return;
        }

        IsMobileConnected = true;
        ControlOwner = mobileTab.IsMobileControlPaused ? ControlOwner.Pc : ControlOwner.Mobile;
    }

    private void UpdateExecuteTerminalCommand()
    {
        if (ActiveTab is not null)
        {
            if (ActiveTab.IsRemote)
                ExecuteTerminalCommand = cmd => WriteTabInput(ActiveTab, cmd);
            else
                ExecuteTerminalCommand = cmd => ActiveTab.SessionManager.WriteInput(cmd);
        }
        else
            ExecuteTerminalCommand = null;
    }

    private void OnTabOutput(TerminalTab tab, string text)
    {
        tab.OutputBuffer.Append(text);
        tab.VirtualScreen.Write(text);
        tab.OutputStabilizer.NotifyOutputReceived();

        if (ShouldAppendLocalHistory())
        {
            _historyStore.Append(_identity.DeviceId, tab.TabId, text);
        }

        // Forward to network
        ForwardTabOutputToNetwork(tab, text);

        // Only forward to UI if this is the active tab
        if (tab == ActiveTab)
            TerminalOutputForwarded?.Invoke(text);
    }

    private bool ShouldAppendLocalHistory()
    {
        if (_relayServer is null || !_relayServer.IsRunning)
            return true;

        if (!ReferenceEquals(_relayServer.HistoryStore, _historyStore))
        {
            _relayServer.HistoryStore = _historyStore;
        }

        return false;
    }

    private void ForwardTabOutputToNetwork(TerminalTab tab, string text)
    {
        if (_relayServer is not null && _relayServer.IsRunning)
        {
            _ = _relayServer.BroadcastLocalTerminalOutputAsync(_identity.DeviceId, tab.TabId, text);
        }
        else if (_relayClient is not null && _relayClient.IsConnected)
        {
            _ = _relayClient.SendTerminalOutputAsync(_identity.DeviceId, tab.TabId, text);
        }
    }

    /// <summary>
    /// Get the buffered output for a tab to replay when switching tabs.
    /// Returns raw VT data suitable for replaying into xterm.js.
    /// </summary>
    public string GetTabReplayData(TerminalTab tab)
    {
        if (tab.IsRemote)
            return tab.RemoteBufferCache.ToString();

        return _historyStore.ReadAll(_identity.DeviceId, tab.TabId);
    }

    // --- Viewport resize handling ---

    public void HandleLocalViewportResize(int cols, int rows, bool force = false)
    {
        if (cols <= 0 || rows <= 0) return;

        if (ActiveTab is not null)
        {
            // Always save desktop dimensions unless in compact mode
            if (force || !ActiveTab.IsCompactMode)
            {
                ActiveTab.DesktopCols = cols;
                ActiveTab.DesktopRows = rows;
            }

            // Don't apply resize if compact mode or mobile viewport is locked
            if (!force && (ActiveTab.IsCompactMode || ActiveTab.IsMobileViewportLocked)) return;

            if (force)
            {
                // Force desktop restoration even if stale compact/mobile lock state lingers.
                ActiveTab.IsCompactMode = false;
                ActiveTab.IsMobileViewportLocked = false;
            }

            ApplyTabTerminalSize(ActiveTab, cols, rows);
            SessionStatus = "Running";
        }
    }

    private void ApplyTabTerminalSize(TerminalTab tab, int cols, int rows)
    {
        if (cols <= 0 || rows <= 0) return;

        tab.Cols = cols;
        tab.Rows = rows;

        if (!tab.SessionManager.IsRunning)
        {
            StartTabTerminal(tab, cols, rows);
            return;
        }

        tab.SessionManager.Resize(cols, rows);
        tab.VirtualScreen.Resize(cols, rows);
    }

    // --- Backward compatibility for AI ---

    /// <summary>Active tab's VirtualScreen, or a fallback empty one for AI context.</summary>
    public VirtualScreen VirtualScreen => ActiveTab?.VirtualScreen ?? _fallbackVirtualScreen;
    private readonly VirtualScreen _fallbackVirtualScreen = new();

    /// <summary>Active tab's OutputBuffer, or a fallback empty one for AI context.</summary>
    public TerminalOutputBuffer TerminalOutputBuffer => ActiveTab?.OutputBuffer ?? _fallbackOutputBuffer;
    private readonly TerminalOutputBuffer _fallbackOutputBuffer = new();

    /// <summary>Active tab's OutputStabilizer, or a fallback for AI auto-exec.</summary>
    public TerminalOutputStabilizer OutputStabilizer => ActiveTab?.OutputStabilizer ?? _fallbackOutputStabilizer;
    private readonly TerminalOutputStabilizer _fallbackOutputStabilizer = new();

    private void Initialize()
    {
        _identity = _identityStore.LoadOrCreate();
        DeviceId = _identity.DeviceId;
        ApplyAutoRelayMode();
        RefreshQr();
        RefreshShells();
        UpdateRelayAddressPreview();

        ControlOwner = ControlOwner.Pc;
        IsMobileConnected = false;
        ConnectionStatus = "Local only";
    }

    private void RefreshQr()
    {
        // In server mode with a group, generate a binding QR code
        if (IsRelayServer && _relayServer?.Group is not null && !string.IsNullOrEmpty(PrimaryRelayAddress))
        {
            var group = _relayServer.Group;
            var serverDeviceId = !string.IsNullOrWhiteSpace(group.ServerDeviceId)
                ? group.ServerDeviceId
                : _identity.DeviceId;
            QrPayload = _payloadBuilder.BuildGroupBind(
                PrimaryRelayAddress, group.GroupId, group.GroupSecret, serverDeviceId);
        }
        else if (_isStandaloneMode && _relayServer is not null && !string.IsNullOrEmpty(PrimaryRelayAddress))
        {
            var baseWsUrl = PrimaryRelayAddress;
            var httpUrl = BuildHttpUrlFromWebSocketUrl(baseWsUrl);
            var wsUrl = AppendOrReplaceTokenQuery(baseWsUrl, _relayServer.AuthToken);
            QrPayload = _payloadBuilder.BuildStandalone(httpUrl, wsUrl, _identity.DeviceId, _identity.DisplayName);
        }
        else
        {
            QrPayload = _payloadBuilder.Build(_identity);
        }
        QrImage = _qrCodeService.Generate(QrPayload);
    }

    private static string BuildHttpUrlFromWebSocketUrl(string wsUrl)
    {
        if (string.IsNullOrWhiteSpace(wsUrl))
            return string.Empty;

        var withoutQuery = wsUrl.Split('?', 2)[0];
        return withoutQuery.Replace("ws://", "http://").Replace("wss://", "https://")
            .TrimEnd('/').Replace("/ws", "");
    }

    private static string AppendOrReplaceTokenQuery(string wsUrl, string? token)
    {
        var trimmedUrl = wsUrl?.Trim() ?? string.Empty;
        var trimmedToken = token?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedUrl) || string.IsNullOrWhiteSpace(trimmedToken))
            return trimmedUrl;

        var encodedToken = Uri.EscapeDataString(trimmedToken);
        if (trimmedUrl.Contains("token=", StringComparison.OrdinalIgnoreCase))
            return Regex.Replace(trimmedUrl, @"token=[^&]*", $"token={encodedToken}", RegexOptions.IgnoreCase);

        var separator = trimmedUrl.Contains('?') ? "&" : "?";
        return $"{trimmedUrl}{separator}token={encodedToken}";
    }

    private static bool IsLoopbackRelayUrl(string? relayUrl)
    {
        if (string.IsNullOrWhiteSpace(relayUrl))
            return true;

        try
        {
            var uri = new Uri(relayUrl, UriKind.Absolute);
            return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private string GetMigrationRelayAddress()
    {
        var runningServerUrl = _relayServer?.ReachableWebSocketUrls
            .FirstOrDefault(url => !IsLoopbackRelayUrl(url));
        if (!string.IsNullOrWhiteSpace(runningServerUrl))
            return runningServerUrl;

        return RelayAddressHelper.GetReachableWebSocketUrls(ServerPort)
            .FirstOrDefault(url => !IsLoopbackRelayUrl(url)) ?? string.Empty;
    }

    private void ForceDisconnect()
    {
        // Reserved for a future "unbind current mobile without stopping relay" flow.
        // The UI entry is intentionally hidden until protocol-side behavior is completed.
        RestoreDesktopTerminalViewport();
        IsMobileConnected = false;
        ControlOwner = ControlOwner.Pc;
        ConnectionStatus = "Mobile disconnected by PC";
    }

    private void SaveAiSettings()
    {
        _aiSettings.ApiEndpoint = AiEndpoint;
        _aiSettings.ApiKey = AiApiKey;
        _aiSettings.ModelName = AiModelName;
        _aiSettingsStore.Save(_aiSettings);
    }

    private void SaveServerSettings()
    {
        if (!IsRelayServer && !string.IsNullOrWhiteSpace(RelayServerAddress))
        {
            try
            {
                RelayServerAddress = RelayAddressHelper.NormalizeClientWebSocketUrl(RelayServerAddress, ServerPort);
            }
            catch (Exception ex)
            {
                ServerStatus = $"Invalid relay address: {ex.Message}";
                return;
            }
        }

        _serverSettings.AutoMode = IsAutoMode;
        _serverSettings.IsRelayServer = IsRelayServer;
        _serverSettings.Port = ServerPort;
        _serverSettings.RelayServerAddress = string.IsNullOrWhiteSpace(RelayServerAddress) ? null : RelayServerAddress;
        _serverSettings.GroupSecret = string.IsNullOrWhiteSpace(GroupSecret) ? null : GroupSecret;
        _serverSettings.WindowClosePreference = NormalizeWindowClosePreference(WindowClosePreference);
        _serverSettingsStore.Save(_serverSettings);
        UpdateRelayAddressPreview();
    }

    private static string NormalizeWindowClosePreference(string? value)
    {
        return value switch
        {
            WindowClosePreferenceMinimizeToTray => WindowClosePreferenceMinimizeToTray,
            WindowClosePreferenceExit => WindowClosePreferenceExit,
            _ => WindowClosePreferenceAsk
        };
    }

    // --- Language Preference ---

    private static string GetLanguageFilePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "data", "language.json");
    }

    public void SaveLanguagePreference(string langCode)
    {
        try
        {
            var dir = Path.GetDirectoryName(GetLanguageFilePath())!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(GetLanguageFilePath(),
                System.Text.Json.JsonSerializer.Serialize(new { language = langCode }));
        }
        catch
        {
            // Best effort
        }
    }

    public string LoadLanguagePreference()
    {
        try
        {
            var path = GetLanguageFilePath();
            if (!File.Exists(path)) return "zh";
            var json = File.ReadAllText(path);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("language").GetString() ?? "zh";
        }
        catch
        {
            return "zh";
        }
    }

    private void ClearPersistedGroupIfRequested()
    {
        if (!IsRelayServer || !string.IsNullOrWhiteSpace(GroupSecret))
            return;
        ClearServerGroupFiles();
    }

    private void SaveGroupMembership(string groupId, string groupSecret, string? serverUrl = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(groupSecret))
                return;

            var normalizedServerUrl = string.Empty;
            var effectiveServerUrl = string.IsNullOrWhiteSpace(serverUrl) ? RelayServerAddress : serverUrl;
            if (!string.IsNullOrWhiteSpace(effectiveServerUrl))
            {
                try
                {
                    normalizedServerUrl = RelayAddressHelper.NormalizeClientWebSocketUrl(effectiveServerUrl, ServerPort);
                }
                catch (Exception ex)
                {
                    OnNetworkLog($"Save membership skipped invalid relay address: {ex.Message}");
                }
            }

            _groupStore.SaveMembership(new GroupMembership
            {
                GroupId = groupId,
                GroupSecret = groupSecret,
                ServerUrl = normalizedServerUrl
            });
        }
        catch (Exception ex)
        {
            OnNetworkLog($"Save membership failed: {ex.Message}");
        }
    }

    private bool TryRestoreRelayAddressFromMembership(GroupMembership? membership)
    {
        var candidate = !string.IsNullOrWhiteSpace(RelayServerAddress)
            ? RelayServerAddress
            : membership?.ServerUrl;
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        try
        {
            RelayServerAddress = RelayAddressHelper.NormalizeClientWebSocketUrl(candidate, ServerPort);
            return true;
        }
        catch (Exception ex)
        {
            OnNetworkLog($"[AutoMode] Ignoring invalid relay server address: {ex.Message}");
            return false;
        }
    }

    private bool CanStartStandaloneMode(GroupMembership? membership)
    {
        if (IsRelayServer || !string.IsNullOrWhiteSpace(RelayServerAddress))
            return false;

        return membership is null && string.IsNullOrWhiteSpace(GroupSecret);
    }

    private bool ShouldStartLocalServer(GroupMembership? membership)
    {
        if (IsRelayServer)
            return true;

        return CanStartStandaloneMode(membership);
    }

    private void ClearClientMembershipState(bool clearRelayAddress = true)
    {
        GroupId = string.Empty;
        GroupSecret = string.Empty;

        if (clearRelayAddress)
            RelayServerAddress = string.Empty;

        try
        {
            _groupStore.ClearMembership();
        }
        catch (Exception ex)
        {
            OnNetworkLog($"Clear membership failed: {ex.Message}");
        }

        SaveServerSettings();
    }

    private void ClearServerGroupFiles()
    {
        try
        {
            _groupStore.ClearGroup();
        }
        catch (Exception ex)
        {
            OnNetworkLog($"Clear group failed: {ex.Message}");
        }
    }

    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            _dispatcher.Invoke(() => Clipboard.SetText(text));
        }
        catch (Exception ex)
        {
            ServerStatus = $"Copy failed: {ex.Message}";
        }
    }

    private void RefreshShells()
    {
        var shells = _shellLocator.GetAvailableShells();
        AvailableShells = new ObservableCollection<ShellInfo>(shells);
        if (SelectedShell is null && shells.Count > 0)
            SelectedShell = shells[0];
        RefreshSessionTargetShells();
    }

    private void ApplyAutoRelayMode()
    {
        if (!IsAutoMode || IsServerRunning)
            return;

        if (_relayModeUserOverride)
        {
            OnNetworkLog("[AutoMode] User override active - keeping relay preference");
            return;
        }

        var existingGroup = _groupStore.LoadGroup();
        var membership = _groupStore.LoadMembership();

        // Priority 1: If we have a membership file, we're a client
        if (membership is not null)
        {
            SetRelayServerMode(false);
            if (string.IsNullOrWhiteSpace(GroupSecret))
                GroupSecret = membership.GroupSecret;
            var hasRelayAddress = TryRestoreRelayAddressFromMembership(membership);
            OnNetworkLog(hasRelayAddress
                ? "[AutoMode] Detected membership file - starting as client"
                : "[AutoMode] Membership file found but relay server address is missing - waiting for invite or manual configuration");
            return;
        }

        // Priority 2: If we have a local group file, we're a server
        if (existingGroup is not null)
        {
            SetRelayServerMode(true);
            RelayServerAddress = string.Empty;
            if (string.IsNullOrWhiteSpace(GroupSecret))
                GroupSecret = existingGroup.GroupSecret;
            OnNetworkLog("[AutoMode] Detected group file - starting as server");
            return;
        }

        // Priority 3: If RelayServerAddress is configured, we're a client
        if (!string.IsNullOrWhiteSpace(RelayServerAddress))
        {
            SetRelayServerMode(false);
            OnNetworkLog("[AutoMode] Relay server address configured - starting as client");
            return;
        }

        // Priority 4: If GroupSecret is set but no server address, wait for invite
        if (!string.IsNullOrWhiteSpace(GroupSecret))
        {
            SetRelayServerMode(false);
            OnNetworkLog("[AutoMode] Group secret configured but no server address - waiting for invite or manual configuration");
            return;
        }

        // Default: No configuration found. Keep user preference (server/client).
        OnNetworkLog("[AutoMode] No configuration found - using current relay preference");
    }

    private void SetRelayServerMode(bool value)
    {
        _suppressRelayModeOverride = true;
        try
        {
            IsRelayServer = value;
        }
        finally
        {
            _suppressRelayModeOverride = false;
        }
    }

    // --- Network Start/Stop ---

    private async Task StartNetworkAsync()
    {
        if (IsAutoMode)
            ApplyAutoRelayMode();

        GroupMembership? clientMembership = null;
        if (!IsRelayServer)
        {
            clientMembership = _groupStore.LoadMembership();
            TryRestoreRelayAddressFromMembership(clientMembership);
        }

        if (ShouldStartLocalServer(clientMembership))
            TryRegisterUrlAcl(ServerPort);

        if (!IsRelayServer && !string.IsNullOrWhiteSpace(RelayServerAddress))
        {
            try
            {
                RelayServerAddress = RelayAddressHelper.NormalizeClientWebSocketUrl(RelayServerAddress, ServerPort);
            }
            catch (Exception ex)
            {
                ServerStatus = $"Invalid relay address: {ex.Message}";
                return;
            }
        }

        SaveServerSettings();
        ClearPersistedGroupIfRequested();

        try
        {
            if (IsRelayServer)
            {
                _relayServer = new RelayServer();
                _relayServer.HistoryStore = _historyStore;
                _relayServer.PreserveTerminalHistoryOnClose = true;
                _relayServer.Log += OnNetworkLog;
                _relayServer.LocalTerminalInputReceived += OnRemoteTerminalInput;
                _relayServer.LocalTerminalResizeReceived += OnRemoteTerminalResize;
                _relayServer.LocalTerminalSessionEnded += OnRemoteTerminalSessionEnded;
                _relayServer.LocalTerminalCloseRequested += OnLocalTerminalCloseRequested;
                _relayServer.LocalSessionRenameRequested += OnLocalSessionRenameRequested;
                _relayServer.LocalTerminalOpenRequested += OnRemoteTerminalOpenRequested;
                _relayServer.LocalTerminalSnapshotProvider = CaptureTabTerminalViewAsync;
                _relayServer.LocalTerminalSizeProvider = GetTabTerminalSize;
                _relayServer.LocalSessionListProvider = GetLocalSessionList;
                _relayServer.LocalQuickPanelSyncProvider = BuildQuickPanelSyncSnapshot;
                _relayServer.LocalRecentInputAppendRequested += AppendRecentInputFromMobile;
                _relayServer.GroupMemberListChanged += OnGroupMemberListChanged;
                _relayServer.RemoteSessionListReceived += OnRemoteSessionListReceived;
                _relayServer.RemoteTerminalOpenedReceived += OnRemoteTerminalOpenedReceived;
                _relayServer.RemoteTerminalOutputReceived += OnRemoteTerminalOutputReceived;
                _relayServer.RemoteTerminalClosedReceived += OnRemoteTerminalClosedReceived;
                _relayServer.ServerMigrationCommitted += OnServerMigrationCommitted;

                // Use GroupSecret as AuthToken if provided
                if (!string.IsNullOrWhiteSpace(GroupSecret))
                    _relayServer.AuthToken = GroupSecret;

                // Allow relay server to accept invite requests (for joining another group)
                _relayServer.CustomHttpHandler = (context, path) =>
                    HandleInviteHttpAsync(context, path);

                await _relayServer.StartAsync(ServerPort, AppContext.BaseDirectory);

                // Patch HttpListener to add /api/ prefixes so invite endpoint is reachable
                PatchHttpListenerApiPrefixes(_relayServer);

                // Register local device
                var shellIds = AvailableShells.Select(s => s.Id).ToList();
                _relayServer.RegisterLocalDevice(_identity.DeviceId, _identity.DisplayName, "Windows", shellIds);

                // Update UI with group info
                if (_relayServer.Group is not null)
                {
                    GroupId = _relayServer.Group.GroupId;
                    GroupSecret = _relayServer.Group.GroupSecret;
                    IsGroupJoined = true;
                    GroupStatus = $"Group created: {GroupId}";

                    // Update GroupMembers from server
                    var members = _relayServer.BuildGroupMemberInfoList();
                    if (_dispatcher.CheckAccess())
                    {
                        UpdateGroupMembers(members);
                    }
                    else
                    {
                        await _dispatcher.InvokeAsync(() => UpdateGroupMembers(members));
                    }

                    // Save the GroupSecret back to settings
                    _serverSettings.GroupSecret = GroupSecret;
                    _serverSettingsStore.Save(_serverSettings);
                }

                // Server role clears any client membership
                _groupStore.ClearMembership();

                IsServerRunning = true;
                var reachableUrls = _relayServer.ReachableWebSocketUrls;
                PrimaryRelayAddress = reachableUrls.FirstOrDefault() ??
                                      RelayAddressHelper.GetLocalhostWebSocketUrl(ServerPort);
                RelayReachableAddresses = string.Join(Environment.NewLine, reachableUrls);

                // Generate QR with group info for mobile binding (must be after PrimaryRelayAddress is set)
                RefreshQr();

                ServerStatus = $"Relay server running. Mobile can connect to {PrimaryRelayAddress}";
                ConnectionStatus = $"Listening at {PrimaryRelayAddress}";
            }
            else if (!string.IsNullOrWhiteSpace(RelayServerAddress))
            {
                var url = RelayAddressHelper.NormalizeClientWebSocketUrl(RelayServerAddress, ServerPort);
                RelayServerAddress = url;

                _relayClient = new RelayClient
                {
                    DeviceId = _identity.DeviceId,
                    DisplayName = _identity.DisplayName,
                    Os = "Windows",
                    AvailableShells = AvailableShells.Select(s => s.Id).ToList(),
                    GroupSecret = GroupSecret,
                    LocalSessionListProvider = GetLocalSessionList,
                    LocalQuickPanelSyncProvider = BuildQuickPanelSyncSnapshot,
                    LocalRecentInputAppendRequested = AppendRecentInputFromMobile
                };
                _relayClient.Log += OnNetworkLog;
                _relayClient.ConnectionStateChanged += OnClientConnectionStateChanged;
                _relayClient.TerminalInputReceived += OnRemoteTerminalInput;
                _relayClient.TerminalOpenRequested += OnRelayClientTerminalOpenRequested;
                _relayClient.TerminalResizeRequested += OnRemoteTerminalResize;
                _relayClient.TerminalCloseRequested += OnRemoteTerminalCloseRequested;
                _relayClient.TerminalDetachRequested += OnRemoteTerminalSessionEnded;
                _relayClient.GroupJoined += OnGroupJoined;
                _relayClient.GroupJoinRejected += OnGroupJoinRejected;
                _relayClient.GroupMemberChanged += OnGroupMemberListChanged;
                _relayClient.SessionListReceived += OnRemoteSessionListReceived;
                _relayClient.SessionRenameRequested += OnSessionRenameRequested;
                _relayClient.ServerChanged += OnServerChanged;
                _relayClient.ServerChangeRequested += OnServerChangeRequested;
                _relayClient.GroupSecretRotated += OnGroupSecretRotated;
                _relayClient.DeviceUnbound += OnDeviceUnbound;
                _relayClient.TerminalOpenedReceived += OnRemoteTerminalOpenedReceived;
                _relayClient.TerminalOutputReceived += OnRemoteTerminalOutputReceived;
                _relayClient.TerminalBufferReceived += OnRemoteTerminalBufferReceived;
                _relayClient.TerminalClosedReceived += OnRemoteTerminalClosedReceived;
                _relayClient.RelayDesignated += OnRelayDesignated;
                _relayClient.InviteCreated += OnInviteCreated;
                _relayClient.DeviceSettingsUpdated += OnDeviceSettingsUpdated;
                _relayClient.DeviceKicked += OnDeviceKicked;
                _relayClient.GroupDissolved += OnGroupDissolved;

                _ = _relayClient.ConnectAsync(url);

                IsServerRunning = true;
                ServerStatus = $"Connecting to {url}...";
                ConnectionStatus = $"Connecting to relay...";
            }
            else
            {
                if (!CanStartStandaloneMode(clientMembership))
                {
                    _isStandaloneMode = false;
                    RefreshQr();
                    if (clientMembership is not null)
                    {
                        ServerStatus = "Client membership found, but relay server address is missing.";
                        ConnectionStatus = "Relay address required";
                        OnNetworkLog("[StartNetwork] Client membership found without relay address - not entering standalone mode.");
                    }
                    else
                    {
                        ServerStatus = "Waiting for relay server invite or manual address.";
                        ConnectionStatus = "Waiting for relay invite";
                        OnNetworkLog("[StartNetwork] Group secret present without relay address - waiting for invite or manual configuration.");
                    }

                    return;
                }

                // No relay server address configured - start in standalone mode
                // Standalone mode: start local WS server without group secret, show connect QR
                _isStandaloneMode = true;
                _relayServer = new RelayServer();
                _relayServer.HistoryStore = _historyStore;
                _relayServer.PreserveTerminalHistoryOnClose = true;
                _relayServer.Log += OnNetworkLog;
                _relayServer.LocalTerminalInputReceived += OnRemoteTerminalInput;
                _relayServer.LocalTerminalResizeReceived += OnRemoteTerminalResize;
                _relayServer.LocalTerminalSessionEnded += OnRemoteTerminalSessionEnded;
                _relayServer.LocalTerminalCloseRequested += OnLocalTerminalCloseRequested;
                _relayServer.LocalSessionRenameRequested += OnLocalSessionRenameRequested;
                _relayServer.LocalTerminalOpenRequested += OnRemoteTerminalOpenRequested;
                _relayServer.LocalTerminalSnapshotProvider = CaptureTabTerminalViewAsync;
                _relayServer.LocalTerminalSizeProvider = GetTabTerminalSize;
                _relayServer.LocalSessionListProvider = GetLocalSessionList;
                _relayServer.LocalQuickPanelSyncProvider = BuildQuickPanelSyncSnapshot;
                _relayServer.LocalRecentInputAppendRequested += AppendRecentInputFromMobile;
                _relayServer.GroupMemberListChanged += OnGroupMemberListChanged;
                _relayServer.RemoteSessionListReceived += OnRemoteSessionListReceived;
                _relayServer.RemoteTerminalOpenedReceived += OnRemoteTerminalOpenedReceived;
                _relayServer.RemoteTerminalOutputReceived += OnRemoteTerminalOutputReceived;
                _relayServer.RemoteTerminalClosedReceived += OnRemoteTerminalClosedReceived;
                _relayServer.ServerMigrationCommitted += OnServerMigrationCommitted;

                // No AuthToken — standalone mode does not require authentication
                _relayServer.CustomHttpHandler = (context, path) =>
                    HandleStandaloneHttpAsync(context, path);

                await _relayServer.StartAsync(ServerPort, AppContext.BaseDirectory);

                // Patch HttpListener to add /api/ prefixes so standalone endpoints are reachable
                PatchHttpListenerApiPrefixes(_relayServer);

                var shellIds = AvailableShells.Select(s => s.Id).ToList();
                _relayServer.RegisterLocalDevice(_identity.DeviceId, _identity.DisplayName, "Windows", shellIds);

                PrimaryRelayAddress = _relayServer.ReachableWebSocketUrls.FirstOrDefault() ??
                                      RelayAddressHelper.GetLocalhostWebSocketUrl(ServerPort);
                RefreshQr();

                IsServerRunning = true;
                ServerStatus = "Standalone mode. Scan QR to connect or wait for invite.";
                ConnectionStatus = "Waiting for phone...";
            }
        }
        catch (Exception ex)
        {
            ServerStatus = $"Error: {ex.Message}";
            OnNetworkLog($"Start error: {ex.Message}");
        }
    }

    private void TryRegisterUrlAcl(int port)
    {
        if (!IsRunningAsAdmin())
            return;

        var prefixes = new[]
        {
            $"http://+:{port}/ws/",
            $"http://+:{port}/api/"
        };

        foreach (var prefix in prefixes)
        {
            TryAddUrlAcl(prefix, "Everyone");
        }
    }

    private static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void TryAddUrlAcl(string prefix, string user)
    {
        try
        {
            var startInfo = new ProcessStartInfo("netsh", $"http add urlacl url={prefix} user={user}")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
                return;

            process.WaitForExit(5000);
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            var combined = $"{output}\n{error}";

            if (process.ExitCode != 0 &&
                !combined.Contains("already exists", StringComparison.OrdinalIgnoreCase) &&
                !combined.Contains("已存在", StringComparison.OrdinalIgnoreCase))
            {
                OnNetworkLog($"[urlacl] add failed: {prefix} ({process.ExitCode}) {combined.Trim()}");
            }
        }
        catch (Exception ex)
        {
            OnNetworkLog($"[urlacl] add failed: {prefix} ({ex.Message})");
        }
    }

    private void StopNetwork()
    {
        StopNetwork("unknown");
    }

    private void StopNetwork(string reason)
    {
        OnNetworkLog($"StopNetwork: {reason}");
        if (_relayServer is not null)
        {
            _relayServer.Log -= OnNetworkLog;
            _relayServer.LocalTerminalInputReceived -= OnRemoteTerminalInput;
            _relayServer.LocalTerminalResizeReceived -= OnRemoteTerminalResize;
            _relayServer.LocalTerminalSessionEnded -= OnRemoteTerminalSessionEnded;
            _relayServer.LocalTerminalCloseRequested -= OnLocalTerminalCloseRequested;
            _relayServer.LocalSessionRenameRequested -= OnLocalSessionRenameRequested;
            _relayServer.LocalTerminalOpenRequested -= OnRemoteTerminalOpenRequested;
            _relayServer.LocalTerminalSnapshotProvider = null;
            _relayServer.LocalTerminalSizeProvider = null;
            _relayServer.LocalSessionListProvider = null;
            _relayServer.LocalQuickPanelSyncProvider = null;
            _relayServer.LocalRecentInputAppendRequested -= AppendRecentInputFromMobile;
            _relayServer.GroupMemberListChanged -= OnGroupMemberListChanged;
            _relayServer.RemoteSessionListReceived -= OnRemoteSessionListReceived;
            _relayServer.RemoteTerminalOpenedReceived -= OnRemoteTerminalOpenedReceived;
            _relayServer.RemoteTerminalOutputReceived -= OnRemoteTerminalOutputReceived;
            _relayServer.RemoteTerminalClosedReceived -= OnRemoteTerminalClosedReceived;
            _relayServer.ServerMigrationCommitted -= OnServerMigrationCommitted;
            _relayServer.Dispose();
            _relayServer = null;
        }

        if (_relayClient is not null)
        {
            _relayClient.Log -= OnNetworkLog;
            _relayClient.ConnectionStateChanged -= OnClientConnectionStateChanged;
            _relayClient.TerminalInputReceived -= OnRemoteTerminalInput;
            _relayClient.TerminalOpenRequested -= OnRelayClientTerminalOpenRequested;
            _relayClient.TerminalResizeRequested -= OnRemoteTerminalResize;
            _relayClient.TerminalCloseRequested -= OnRemoteTerminalCloseRequested;
            _relayClient.TerminalDetachRequested -= OnRemoteTerminalSessionEnded;
            _relayClient.GroupJoined -= OnGroupJoined;
            _relayClient.GroupJoinRejected -= OnGroupJoinRejected;
            _relayClient.GroupMemberChanged -= OnGroupMemberListChanged;
            _relayClient.SessionListReceived -= OnRemoteSessionListReceived;
            _relayClient.SessionRenameRequested -= OnSessionRenameRequested;
            _relayClient.ServerChanged -= OnServerChanged;
            _relayClient.ServerChangeRequested -= OnServerChangeRequested;
            _relayClient.GroupSecretRotated -= OnGroupSecretRotated;
            _relayClient.DeviceUnbound -= OnDeviceUnbound;
            _relayClient.TerminalOpenedReceived -= OnRemoteTerminalOpenedReceived;
            _relayClient.TerminalOutputReceived -= OnRemoteTerminalOutputReceived;
            _relayClient.TerminalBufferReceived -= OnRemoteTerminalBufferReceived;
            _relayClient.TerminalClosedReceived -= OnRemoteTerminalClosedReceived;
            _relayClient.RelayDesignated -= OnRelayDesignated;
            _relayClient.InviteCreated -= OnInviteCreated;
            _relayClient.DeviceSettingsUpdated -= OnDeviceSettingsUpdated;
            _relayClient.DeviceKicked -= OnDeviceKicked;
            _relayClient.GroupDissolved -= OnGroupDissolved;
            _relayClient.LocalQuickPanelSyncProvider = null;
            _relayClient.LocalRecentInputAppendRequested = null;
            _relayClient.Dispose();
            _relayClient = null;
        }

        RestoreDesktopTerminalViewport();
        _isStandaloneMode = false;
        IsServerRunning = false;
        IsGroupJoined = false;
        SetSelectedGroupDevice(null);
        GroupMembers.Clear();
        RefreshSessionTargetShells();
        RefreshVisibleTabs();
        GroupStatus = string.Empty;
        UpdateRelayAddressPreview();
        ConnectionStatus = "Local only";
    }

    private void InitializeGroupData()
    {
        StopNetwork("initialize_group_data");

        try
        {
            _groupStore.ClearMembership();
        }
        catch (Exception ex)
        {
            OnNetworkLog($"Clear membership failed: {ex.Message}");
        }

        try
        {
            _groupStore.ClearGroup();
        }
        catch (Exception ex)
        {
            OnNetworkLog($"Clear group failed: {ex.Message}");
        }

        GroupId = string.Empty;
        GroupSecret = string.Empty;

        var wasRelayServer = IsRelayServer;
        if (wasRelayServer)
        {
            var group = _groupStore.CreateGroup(_identity.DeviceId);
            GroupId = group.GroupId;
            GroupSecret = group.GroupSecret;
            OnNetworkLog($"Group initialized: {group.GroupId}");
        }

        IsAutoMode = true;
        SetRelayServerMode(true);
        RelayServerAddress = string.Empty;

        SaveServerSettings();
        _serverSettings.GroupSecret = string.IsNullOrWhiteSpace(GroupSecret) ? null : GroupSecret;
        _serverSettingsStore.Save(_serverSettings);
        RefreshQr();
    }

    private void OnNetworkLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var entry = $"[{timestamp}] [NET] {message}";
        try
        {
            var dir = System.IO.Path.GetDirectoryName(NetLogFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(NetLogFile, entry + Environment.NewLine);
        }
        catch
        {
            // Ignore logging failures
        }

        if (!EnableDebugLog)
            return;
        _dispatcher.InvokeAsync(() =>
        {
            while (DebugLogs.Count >= MaxDebugLogEntries)
                DebugLogs.RemoveAt(0);
            DebugLogs.Add(entry);
        });
    }

    private void OnClientConnectionStateChanged(bool connected)
    {
        _dispatcher.InvokeAsync(() =>
        {
            if (connected)
            {
                ServerStatus = $"Connected to relay server";
                ConnectionStatus = $"Connected to relay";
            }
            else
            {
                ServerStatus = "Disconnected from relay (reconnecting...)";
                ConnectionStatus = "Reconnecting...";
            }
        });
    }

    private void OnGroupJoined(GroupJoinAcceptedMessage accepted)
    {
        _dispatcher.InvokeAsync(() =>
        {
            GroupId = accepted.GroupId;
            IsGroupJoined = true;
            GroupStatus = $"Joined group ({accepted.Members.Count} members)";

            // Save group secret from invite-based join for reconnect
            if (!string.IsNullOrWhiteSpace(accepted.GroupSecret))
            {
                GroupSecret = accepted.GroupSecret;
                _serverSettings.GroupSecret = GroupSecret;
                _serverSettingsStore.Save(_serverSettings);
            }

            UpdateGroupMembers(accepted.Members);

            SaveGroupMembership(accepted.GroupId, GroupSecret, RelayServerAddress);
        });
    }

    private void OnGroupJoinRejected(string reason)
    {
        _dispatcher.InvokeAsync(() =>
        {
            IsGroupJoined = false;
            GroupStatus = $"Join rejected: {reason}";
            ServerStatus = $"Group join rejected: {reason}";
        });
    }

    private void OnDeviceUnbound()
    {
        _dispatcher.InvokeAsync(() =>
        {
            GroupSecret = string.Empty;
            SaveServerSettings();

            try
            {
                _groupStore.ClearMembership();
            }
            catch (Exception ex)
            {
                OnNetworkLog($"Clear membership failed: {ex.Message}");
            }

            StopNetwork("device_unbound");
            GroupStatus = "Device unbound. Please rebind.";
            ServerStatus = "Device unbound by mobile.";
        });
    }

    private void OnGroupMemberListChanged(List<GroupMemberInfo> members)
    {
        _dispatcher.InvokeAsync(() =>
        {
            UpdateGroupMembers(members);
            GroupStatus = $"Group: {members.Count} member(s)";
        });
    }

    private void OnRemoteSessionListReceived(string deviceId, List<SessionInfo> sessions)
    {
        _dispatcher.InvokeAsync(() =>
        {
            ApplyRemoteSessionList(deviceId, sessions);
        });
    }

    private void OnLocalSessionRenameRequested(string sessionId, string title)
    {
        _dispatcher.InvokeAsync(() =>
        {
            ApplyLocalSessionRename(sessionId, title);
        });
    }

    private void OnSessionRenameRequested(string deviceId, string sessionId, string title)
    {
        if (!string.Equals(deviceId, _identity.DeviceId, StringComparison.Ordinal))
            return;

        _dispatcher.InvokeAsync(() =>
        {
            ApplyLocalSessionRename(sessionId, title);
        });
    }

    private void ApplyLocalSessionRename(string sessionId, string title)
    {
        var trimmed = title?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed) || string.IsNullOrWhiteSpace(sessionId))
            return;

        var tab = FindTabBySessionId(sessionId);
        if (tab is null) return;

        tab.Title = trimmed;
        NotifyLocalSessionListChanged();
    }

    private void OnGroupSecretRotated(string newSecret)
    {
        _dispatcher.InvokeAsync(() =>
        {
            GroupSecret = newSecret;
            _serverSettings.GroupSecret = newSecret;
            _serverSettingsStore.Save(_serverSettings);

            if (!string.IsNullOrWhiteSpace(GroupId))
                SaveGroupMembership(GroupId, newSecret, RelayServerAddress);
        });
    }

    private void OnServerChanged(string newUrl, string newSecret)
    {
        _dispatcher.InvokeAsync(async () =>
        {
            // If this device is already running as the new server, just drop the client connection.
            if (_relayServer is not null && _relayServer.IsRunning)
            {
                _relayClient?.Disconnect();
                _relayClient?.Dispose();
                _relayClient = null;
                RelayServerAddress = string.Empty;
                GroupSecret = newSecret;
                SaveServerSettings();
                return;
            }

            await SwitchToClientAsync(newUrl, newSecret, _relayClient?.GroupId);
        });
    }

    private void OnServerChangeRequested(string groupId, string groupSecret)
    {
        _dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await StartRelayServerForMigrationAsync(groupId, groupSecret);
            }
            catch (Exception ex)
            {
                OnNetworkLog($"Server migration (prepare) failed: {ex.Message}");
            }
        });
    }

    private void OnServerMigrationCommitted(string newUrl, string newSecret)
    {
        _dispatcher.InvokeAsync(async () =>
        {
            await SwitchToClientAsync(newUrl, newSecret, _relayServer?.Group?.GroupId);
        });
    }

    // --- New multi-device architecture event handlers ---

    private void OnRelayDesignated(string relayUrl, string groupId)
    {
        _dispatcher.InvokeAsync(() =>
        {
            OnNetworkLog($"This device designated as relay server: url={relayUrl}, groupId={groupId}");
            GroupStatus = "Designated as relay server";
        });
    }

    private void OnInviteCreated(string inviteCode, string relayUrl)
    {
        _dispatcher.InvokeAsync(() =>
        {
            OnNetworkLog($"Invite code created: {inviteCode} for relay={relayUrl}");
        });
    }

    private void OnDeviceSettingsUpdated(string deviceId, string displayName)
    {
        _dispatcher.InvokeAsync(() =>
        {
            var member = GroupMembers.FirstOrDefault(m => string.Equals(m.DeviceId, deviceId, StringComparison.Ordinal));
            if (member is not null)
            {
                member.Update(new GroupMemberInfo
                {
                    DeviceId = member.DeviceId,
                    DisplayName = displayName,
                    Os = member.Os,
                    Role = member.Role,
                    IsOnline = member.IsOnline,
                    AvailableShells = member.AvailableShells.ToList(),
                });
                OnPropertyChanged(nameof(CurrentSessionTargetDisplayName));
                OnPropertyChanged(nameof(NewSessionTargetButtonText));
            }
            OnNetworkLog($"Device settings updated: {deviceId} → {displayName}");
        });
    }

    private void OnDeviceKicked(string reason)
    {
        _dispatcher.InvokeAsync(() =>
        {
            OnNetworkLog($"Kicked from group: {reason}");
            StopNetwork("device_kicked");
            ClearClientMembershipState();
            GroupStatus = $"Kicked: {reason}";
            ServerStatus = "Kicked from group. Client state cleared.";
        });
    }

    private void OnGroupDissolved(string reason)
    {
        _dispatcher.InvokeAsync(() =>
        {
            OnNetworkLog($"Group dissolved: {reason}");
            StopNetwork("group_dissolved");
            ClearClientMembershipState();
            GroupStatus = $"Group dissolved: {reason}";
            ServerStatus = "Group dissolved. Client state cleared.";
        });
    }

    // --- Remote terminal event handlers (for PC-to-PC remote terminals) ---

    private void OnRemoteTerminalOpenedReceived(string deviceId, string sessionId, int cols, int rows)
    {
        _dispatcher.InvokeAsync(() =>
        {
            var member = GroupMembers.FirstOrDefault(m => string.Equals(m.DeviceId, deviceId, StringComparison.Ordinal));
            var deviceName = member?.DisplayName ?? deviceId[..Math.Min(8, deviceId.Length)];
            var existingTab = Tabs.FirstOrDefault(t =>
                t.IsRemote &&
                string.Equals(t.RemoteDeviceId, deviceId, StringComparison.Ordinal) &&
                string.Equals(t.RemoteSessionId, sessionId, StringComparison.Ordinal));

            if (existingTab is not null)
            {
                existingTab.Cols = cols;
                existingTab.Rows = rows;
                existingTab.VirtualScreen.Resize(cols, rows);
                RefreshVisibleTabs(existingTab);
            }
            else
            {
                var tab = CreateRemoteTab(deviceId, deviceName, "remote", sessionId, cols, rows,
                    activate: string.Equals(CurrentSessionTargetDeviceId, deviceId, StringComparison.Ordinal));
                StartRemoteBufferSync(tab);
                OnNetworkLog($"Remote tab created: [{deviceName}] session={sessionId}");
            }

            _ = RequestRemoteSessionListAsync(deviceId);
        });
    }

    private static string BuildRemoteBufferRequestId()
    {
        return Guid.NewGuid().ToString("N");
    }

    private static string BuildRemoteBufferCursor(long beforeSeq)
    {
        return beforeSeq > 0
            ? beforeSeq.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static long ParseRemoteBufferCursor(string? beforeCursor)
    {
        if (string.IsNullOrWhiteSpace(beforeCursor))
            return 0;

        return long.TryParse(beforeCursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var beforeSeq)
            ? Math.Max(0, beforeSeq)
            : 0;
    }

    private void OnRemoteTerminalOutputReceived(string deviceId, string sessionId, string data, long outputSeq)
    {
        _dispatcher.InvokeAsync(() =>
        {
            var tab = Tabs.FirstOrDefault(t =>
                t.IsRemote &&
                string.Equals(t.RemoteDeviceId, deviceId, StringComparison.Ordinal) &&
                string.Equals(t.RemoteSessionId, sessionId, StringComparison.Ordinal));
            if (tab is null) return;

            if (tab.LatestBufferLoaded && outputSeq > 0 && outputSeq <= tab.LatestBufferSnapshotSeq)
                return;

            tab.OutputBuffer.Append(data);
            tab.VirtualScreen.Write(data);

            if (!tab.LatestBufferLoaded)
            {
                tab.PendingRemoteOutputs.Add(new PendingRemoteOutput(outputSeq, data));
                return;
            }

            tab.RemoteBufferCache.Append(data);
            if (outputSeq > 0)
                tab.LatestBufferSnapshotSeq = Math.Max(tab.LatestBufferSnapshotSeq, outputSeq);

            if (tab == ActiveTab)
                TerminalOutputForwarded?.Invoke(data);
        });
    }

    private void OnRemoteTerminalClosedReceived(string deviceId, string sessionId)
    {
        _dispatcher.InvokeAsync(() =>
        {
            var tab = Tabs.FirstOrDefault(t =>
                t.IsRemote &&
                string.Equals(t.RemoteDeviceId, deviceId, StringComparison.Ordinal) &&
                string.Equals(t.RemoteSessionId, sessionId, StringComparison.Ordinal));
            if (tab is not null)
            {
                RemoveTab(tab, requestRemoteClose: false);
                OnNetworkLog($"Remote terminal closed: device={deviceId}, session={sessionId}");
            }
        });
    }

    private void OnRemoteTerminalBufferReceived(
        string deviceId,
        string sessionId,
        string mode,
        string data,
        long snapshotOutputSeq,
        string nextBeforeCursor,
        bool hasMore,
        string requestId)
    {
        _dispatcher.InvokeAsync(() =>
        {
            HandleRemoteBufferResponse(
                deviceId,
                sessionId,
                mode,
                requestId,
                data,
                snapshotOutputSeq,
                nextBeforeCursor,
                hasMore);
        });
    }

    private void StartRemoteBufferSync(TerminalTab tab)
    {
        if (!tab.IsRemote) return;
        ResetRemoteBufferState(tab);
        RequestRemoteLatestBufferPage(tab);
    }

    private void ResetRemoteBufferState(TerminalTab tab)
    {
        tab.LatestBufferLoading = false;
        tab.LatestBufferLoaded = false;
        tab.OlderBufferLoading = false;
        tab.LatestBufferRequestId = string.Empty;
        tab.OlderBufferRequestId = string.Empty;
        tab.RemoteBufferBeforeSeq = 0;
        tab.LatestBufferSnapshotSeq = 0;
        tab.PendingRemoteOutputs.Clear();
        tab.RemoteBufferCache.Clear();
    }

    private void RequestRemoteLatestBufferPage(TerminalTab tab)
    {
        if (tab.LatestBufferLoading || tab.LatestBufferLoaded) return;
        if (string.IsNullOrWhiteSpace(tab.RemoteSessionId) || string.IsNullOrWhiteSpace(tab.RemoteDeviceId))
            return;

        tab.LatestBufferLoading = true;
        tab.LatestBufferRequestId = BuildRemoteBufferRequestId();

        if (_relayClient is not null && _relayClient.IsConnected)
        {
            _ = _relayClient.SendTerminalBufferRequestAsync(
                tab.RemoteDeviceId,
                tab.RemoteSessionId,
                string.Empty,
                RemoteLatestBufferPageChars,
                tab.LatestBufferRequestId);
            return;
        }

        if (_relayServer is not null && _relayServer.IsRunning)
        {
            var page = _relayServer.GetTerminalBufferPage(
                tab.RemoteDeviceId, tab.RemoteSessionId, string.Empty, RemoteLatestBufferPageChars);
            HandleRemoteBufferResponse(
                tab.RemoteDeviceId,
                tab.RemoteSessionId,
                page.Mode,
                tab.LatestBufferRequestId,
                page.Data,
                page.SnapshotOutputSeq,
                page.NextBeforeCursor,
                page.HasMore);
            return;
        }

        tab.LatestBufferLoading = false;
        tab.LatestBufferLoaded = true;
        if (tab.PendingRemoteOutputs.Count > 0)
        {
            foreach (var pending in tab.PendingRemoteOutputs)
            {
                if (!string.IsNullOrEmpty(pending.Data))
                    tab.RemoteBufferCache.Append(pending.Data);
            }
            tab.PendingRemoteOutputs.Clear();
            if (tab == ActiveTab)
                TerminalBufferReplaceRequested?.Invoke(tab.RemoteBufferCache.ToString());
        }
    }

    private void RequestRemoteOlderBufferPage(TerminalTab tab)
    {
        if (tab.OlderBufferLoading || tab.RemoteBufferBeforeSeq <= 0) return;
        if (string.IsNullOrWhiteSpace(tab.RemoteSessionId) || string.IsNullOrWhiteSpace(tab.RemoteDeviceId))
            return;

        tab.OlderBufferLoading = true;
        tab.OlderBufferRequestId = BuildRemoteBufferRequestId();
        var beforeCursor = BuildRemoteBufferCursor(tab.RemoteBufferBeforeSeq);

        if (_relayClient is not null && _relayClient.IsConnected)
        {
            _ = _relayClient.SendTerminalBufferRequestAsync(
                tab.RemoteDeviceId,
                tab.RemoteSessionId,
                beforeCursor,
                RemoteOlderBufferPageChars,
                tab.OlderBufferRequestId);
            return;
        }

        if (_relayServer is not null && _relayServer.IsRunning)
        {
            var page = _relayServer.GetTerminalBufferPage(
                tab.RemoteDeviceId, tab.RemoteSessionId, beforeCursor, RemoteOlderBufferPageChars);
            HandleRemoteBufferResponse(
                tab.RemoteDeviceId,
                tab.RemoteSessionId,
                page.Mode,
                tab.OlderBufferRequestId,
                page.Data,
                page.SnapshotOutputSeq,
                page.NextBeforeCursor,
                page.HasMore);
            return;
        }

        tab.OlderBufferLoading = false;
    }

    private void HandleRemoteBufferResponse(
        string deviceId,
        string sessionId,
        string mode,
        string requestId,
        string data,
        long snapshotOutputSeq,
        string nextBeforeCursor,
        bool hasMore)
    {
        var tab = Tabs.FirstOrDefault(t =>
            t.IsRemote &&
            string.Equals(t.RemoteDeviceId, deviceId, StringComparison.Ordinal) &&
            string.Equals(t.RemoteSessionId, sessionId, StringComparison.Ordinal));
        if (tab is null) return;

        var older = string.Equals(mode, "older", StringComparison.OrdinalIgnoreCase);
        if (older)
        {
            if (!string.IsNullOrEmpty(tab.OlderBufferRequestId) &&
                !string.IsNullOrEmpty(requestId) &&
                !string.Equals(tab.OlderBufferRequestId, requestId, StringComparison.Ordinal))
            {
                return;
            }

            tab.OlderBufferLoading = false;
            tab.OlderBufferRequestId = string.Empty;
            tab.RemoteBufferBeforeSeq = ParseRemoteBufferCursor(nextBeforeCursor);

            if (!string.IsNullOrEmpty(data))
            {
                tab.RemoteBufferCache.Insert(0, data);
                if (tab == ActiveTab)
                    TerminalBufferReplaceRequested?.Invoke(tab.RemoteBufferCache.ToString());
            }

            if (hasMore && tab.RemoteBufferBeforeSeq > 0)
                RequestRemoteOlderBufferPage(tab);
            return;
        }

        if (!string.IsNullOrEmpty(tab.LatestBufferRequestId) &&
            !string.IsNullOrEmpty(requestId) &&
            !string.Equals(tab.LatestBufferRequestId, requestId, StringComparison.Ordinal))
        {
            return;
        }

        tab.LatestBufferLoading = false;
        tab.LatestBufferLoaded = true;
        tab.LatestBufferRequestId = string.Empty;
        tab.LatestBufferSnapshotSeq = Math.Max(0, snapshotOutputSeq);
        tab.RemoteBufferBeforeSeq = ParseRemoteBufferCursor(nextBeforeCursor);
        tab.RemoteBufferCache.Clear();
        if (!string.IsNullOrEmpty(data))
            tab.RemoteBufferCache.Append(data);

        foreach (var pending in tab.PendingRemoteOutputs)
        {
            if (string.IsNullOrEmpty(pending.Data))
                continue;

            if (pending.OutputSeq > 0 && pending.OutputSeq <= tab.LatestBufferSnapshotSeq)
                continue;

            tab.RemoteBufferCache.Append(pending.Data);
            if (pending.OutputSeq > 0)
                tab.LatestBufferSnapshotSeq = Math.Max(tab.LatestBufferSnapshotSeq, pending.OutputSeq);
        }
        tab.PendingRemoteOutputs.Clear();

        if (tab == ActiveTab)
            TerminalBufferReplaceRequested?.Invoke(tab.RemoteBufferCache.ToString());

        if (hasMore && tab.RemoteBufferBeforeSeq > 0)
            RequestRemoteOlderBufferPage(tab);
    }

    private void UpdateRelayAddressPreview()
    {
        if (!IsRelayServer)
        {
            PrimaryRelayAddress = string.Empty;
            RelayReachableAddresses = string.Empty;
            if (!IsServerRunning)
                ServerStatus = string.IsNullOrWhiteSpace(RelayServerAddress)
                    ? "Enter a relay server address or enable relay mode"
                    : "Ready to connect to relay server";
            return;
        }

        var reachableUrls = RelayAddressHelper.GetReachableWebSocketUrls(ServerPort);
        PrimaryRelayAddress = reachableUrls.FirstOrDefault() ??
                              RelayAddressHelper.GetLocalhostWebSocketUrl(ServerPort);
        RelayReachableAddresses = string.Join(Environment.NewLine, reachableUrls);

        if (!IsServerRunning)
            ServerStatus = $"Ready to start. Mobile can connect to {PrimaryRelayAddress}";
    }

    private async Task StartRelayServerForMigrationAsync(string groupId, string groupSecret)
    {
        if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(groupSecret))
            return;

        var migrationRelayUrl = GetMigrationRelayAddress();
        if (string.IsNullOrWhiteSpace(migrationRelayUrl))
        {
            OnNetworkLog("Server migration aborted: no reachable non-localhost relay URL available.");
            ServerStatus = "Server migration aborted: relay URL is localhost-only.";
            return;
        }

        // Persist group info so the new server keeps the existing group ID/secret.
        _groupStore.SaveGroup(new GroupInfo
        {
            GroupId = groupId,
            GroupSecret = groupSecret,
            ServerDeviceId = _identity.DeviceId,
            CreatedAt = DateTimeOffset.UtcNow
        });

        SetRelayServerMode(true);
        GroupSecret = groupSecret;
        RelayServerAddress = string.Empty;
        SaveServerSettings();
        _groupStore.ClearMembership();

        if (_relayServer is null || !_relayServer.IsRunning)
        {
            await StartNetworkAsync();
        }

        var newServerUrl = GetMigrationRelayAddress();
        if (_relayClient is not null && !string.IsNullOrWhiteSpace(newServerUrl))
        {
            await _relayClient.SendServerChangePrepareAsync(groupId, groupSecret, newServerUrl);
            OnNetworkLog($"Server migration: prepare sent ({newServerUrl})");
        }
    }

    private async Task SwitchToClientAsync(string newUrl, string newSecret, string? groupId)
    {
        if (string.IsNullOrWhiteSpace(newUrl) || string.IsNullOrWhiteSpace(newSecret))
            return;

        var effectiveGroupId = !string.IsNullOrWhiteSpace(groupId)
            ? groupId
            : _relayServer?.Group?.GroupId ?? _groupStore.LoadGroup()?.GroupId;
        if (!string.IsNullOrWhiteSpace(effectiveGroupId))
            SaveGroupMembership(effectiveGroupId, newSecret, newUrl);

        StopNetwork("switch_to_client");

        ClearServerGroupFiles();
        SetRelayServerMode(false);
        RelayServerAddress = newUrl;
        GroupSecret = newSecret;
        SaveServerSettings();

        await StartNetworkAsync();
    }

    private void OnRemoteTerminalInput(string sessionId, string data)
    {
        try
        {
            var tab = FindTabBySessionId(sessionId);
            if (tab is null)
            {
                OnNetworkLog($"Remote terminal input: no tab for session={sessionId}");
                return;
            }

            if (!tab.SessionManager.IsRunning)
            {
                OnNetworkLog($"Remote terminal input ignored, terminal not running. session={sessionId}");
                return;
            }

            if (tab.HasMobileSession && tab.IsMobileControlPaused &&
                TryGetMobileViewport(tab, out var mobileCols, out var mobileRows))
            {
                _dispatcher.Invoke(() => ShowMobileViewport(tab, mobileCols, mobileRows));
            }

            tab.SessionManager.WriteInput(data);
        }
        catch (Exception ex)
        {
            OnNetworkLog($"Remote terminal input failed. session={sessionId}, error={ex.Message}");
        }
    }

    private void OnRemoteTerminalResize(string sessionId, int cols, int rows)
    {
        if (cols <= 0 || rows <= 0) return;

        _dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var tab = FindTabBySessionId(sessionId);
                if (tab is null) return;

                var isFirstMobileAttach = !tab.HasMobileSession;
                if (isFirstMobileAttach && (tab.DesktopCols <= 0 || tab.DesktopRows <= 0))
                {
                    tab.DesktopCols = tab.Cols > 0 ? tab.Cols : 120;
                    tab.DesktopRows = tab.Rows > 0 ? tab.Rows : 30;
                }

                if (isFirstMobileAttach)
                {
                    _ = SendTabSnapshotToNetworkAsync(tab);
                }

                ShowMobileViewport(tab, cols, rows);
            }
            catch (Exception ex)
            {
                OnNetworkLog($"Remote terminal resize failed. session={sessionId}, error={ex.Message}");
            }
        });
    }

    private async Task SendTabSnapshotToNetworkAsync(TerminalTab tab)
    {
        try
        {
            var snapshot = await CaptureTabTerminalViewAsync(tab.TabId);
            if (!string.IsNullOrWhiteSpace(snapshot))
            {
                ForwardTabOutputToNetwork(tab, snapshot);
            }
        }
        catch (Exception ex)
        {
            OnNetworkLog($"Send tab snapshot failed. session={tab.TabId}, error={ex.Message}");
        }
    }

    private void OnRemoteTerminalSessionEnded(string sessionId)
    {
        _dispatcher.InvokeAsync(() =>
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            var tab = FindTabBySessionId(sessionId);
            if (tab is not null)
            {
                ClearMobileViewport(tab);
            }
        });
    }

    private void OnRemoteTerminalCloseRequested(string sessionId)
    {
        _dispatcher.InvokeAsync(() =>
        {
            var tab = FindTabBySessionId(sessionId);
            if (tab is not null)
            {
                RemoveTab(tab, requestRemoteClose: false);
            }
            else
            {
                RestoreDesktopTerminalViewport();
            }
        });
    }

    private void OnLocalTerminalCloseRequested(string sessionId)
    {
        _dispatcher.InvokeAsync(() =>
        {
            var tab = FindTabBySessionId(sessionId);
            if (tab is not null)
            {
                RemoveTab(tab, requestRemoteClose: false);
            }
        });
    }

    private void OnRelayClientTerminalOpenRequested(string deviceId, string shellId)
    {
        _ = HandleRelayClientTerminalOpenAsync(deviceId, shellId);
    }

    private async Task HandleRelayClientTerminalOpenAsync(string deviceId, string shellId)
    {
        try
        {
            var (sessionId, cols, rows) = await OnRemoteTerminalOpenRequested(deviceId, shellId);

            if (_relayClient is null || !_relayClient.IsConnected)
                return;

            await _relayClient.SendTerminalOpenedAsync(deviceId, sessionId, cols, rows);

            var snapshot = await CaptureTabTerminalViewAsync(sessionId);
            if (!string.IsNullOrWhiteSpace(snapshot))
            {
                await _relayClient.SendTerminalOutputAsync(deviceId, sessionId, snapshot);
            }
        }
        catch (Exception ex)
        {
            OnNetworkLog($"Relay client terminal.open handling failed: {ex.Message}");
        }
    }

    private async Task<(string SessionId, int Cols, int Rows)> OnRemoteTerminalOpenRequested(
        string deviceId, string shellId)
    {
        var tcs = new TaskCompletionSource<(string, int, int)>();

        await _dispatcher.InvokeAsync(() =>
        {
            try
            {
                var shell = AvailableShells.FirstOrDefault(s =>
                    string.Equals(s.Id, shellId, StringComparison.OrdinalIgnoreCase));
                shell ??= SelectedShell ?? _shellLocator.GetDefaultShell();

                var tab = CreateTab(shell, 0, 0, activate: !IsCurrentSessionTargetRemote);

                // Use default size if no size has been reported yet
                var cols = tab.Cols > 0 ? tab.Cols : 120;
                var rows = tab.Rows > 0 ? tab.Rows : 30;

                if (!tab.SessionManager.IsRunning)
                {
                    StartTabTerminal(tab, cols, rows);
                }

                tcs.TrySetResult((tab.TabId, cols, rows));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return await tcs.Task;
    }

    private List<SessionInfo> GetLocalSessionList()
    {
        if (_dispatcher.CheckAccess())
        {
            return Tabs.Where(t => !t.IsRemote).Select(t => new SessionInfo
            {
                SessionId = t.TabId,
                ShellId = t.Shell.Id,
                Title = t.Title
            }).ToList();
        }

        return _dispatcher.Invoke(() =>
            Tabs.Where(t => !t.IsRemote).Select(t => new SessionInfo
            {
                SessionId = t.TabId,
                ShellId = t.Shell.Id,
                Title = t.Title
            }).ToList()
        );
    }

    private void NotifyLocalSessionListChanged()
    {
        if (_relayServer is not null && _relayServer.IsRunning)
        {
            _ = _relayServer.BroadcastLocalSessionListChangedAsync(_identity.DeviceId);
        }

        if (_relayClient is not null && _relayClient.IsConnected)
        {
            var sessions = GetLocalSessionList();
            _ = _relayClient.SendSessionListAsync(_identity.DeviceId, sessions);
        }
    }

    private void NotifyLocalTerminalClosed(TerminalTab tab)
    {
        if (_relayServer is not null && _relayServer.IsRunning)
        {
            _ = _relayServer.BroadcastLocalTerminalClosedAsync(_identity.DeviceId, tab.TabId);
        }

        if (_relayClient is not null && _relayClient.IsConnected)
        {
            _ = _relayClient.SendTerminalClosedAsync(_identity.DeviceId, tab.TabId);
        }
    }

    private async Task<string> CaptureTabTerminalViewAsync(string sessionId)
    {
        var tab = FindTabBySessionId(sessionId);
        if (tab is null) return string.Empty;

        // Only try xterm snapshot if this is the active tab
        if (tab == ActiveTab)
        {
            try
            {
                var snapshot = await CaptureTerminalSnapshotOnUiThreadAsync();
                if (snapshot is not null)
                {
                    var bootstrap = BuildBootstrapSequence(snapshot);
                    if (!string.IsNullOrWhiteSpace(bootstrap))
                        return bootstrap;
                }
            }
            catch (Exception ex)
            {
                OnNetworkLog($"Snapshot capture error: {ex.Message}");
            }
        }

        var virtualScreenSnapshot = tab.VirtualScreen.GetSnapshot();
        if (!string.IsNullOrWhiteSpace(virtualScreenSnapshot))
        {
            var lines = virtualScreenSnapshot.Split('\n');
            return BuildPlainTextBootstrapSequence(
                lines,
                isAlternateBuffer: false,
                cursorX: 0,
                cursorY: Math.Max(0, lines.Length - 1));
        }

        return tab.OutputBuffer.GetRecentRaw();
    }

    private (int Cols, int Rows) GetTabTerminalSize(string sessionId)
    {
        var tab = FindTabBySessionId(sessionId);
        if (tab is not null && tab.Cols > 0 && tab.Rows > 0)
            return (tab.Cols, tab.Rows);
        return (120, 30);
    }

    private TerminalTab? FindTabBySessionId(string sessionId)
    {
        if (_dispatcher.CheckAccess())
            return Tabs.FirstOrDefault(t => t.TabId == sessionId);

        return _dispatcher.Invoke(() =>
            Tabs.FirstOrDefault(t => t.TabId == sessionId));
    }

    private void RestoreDesktopTerminalViewport()
    {
        if (Tabs.Count > 0)
        {
            foreach (var tab in Tabs.ToList())
            {
                ClearMobileViewport(tab);
            }
        }
        else
        {
            IsMobileConnected = false;
            ControlOwner = ControlOwner.Pc;
        }
    }

    private void RestoreTabDesktopViewport(TerminalTab tab)
    {
        if (!tab.IsMobileViewportLocked && !tab.HasMobileSession)
            return;

        ClearMobileViewport(tab);
    }

    // --- Bootstrap Sequence Helpers ---

    private static string BuildBootstrapSequence(TerminalSnapshot snapshot)
    {
        return BuildPlainTextBootstrapSequence(
            snapshot.Lines,
            snapshot.IsTuiActive,
            snapshot.CursorX,
            snapshot.CursorY);
    }

    private static string BuildPlainTextBootstrapSequence(
        IReadOnlyList<string> lines,
        bool isAlternateBuffer,
        int cursorX,
        int cursorY)
    {
        var sb = new StringBuilder();

        if (isAlternateBuffer)
            sb.Append("\x1b[?1049h");

        sb.Append("\x1b[0m");
        sb.Append("\x1b[H\x1b[2J");

        for (var row = 0; row < lines.Count; row++)
        {
            var line = SanitizeSnapshotLine(lines[row]);
            if (line.Length == 0)
                continue;

            sb.Append($"\x1b[{row + 1};1H");
            sb.Append(line);
        }

        sb.Append($"\x1b[{Math.Max(1, cursorY + 1)};{Math.Max(1, cursorX + 1)}H");
        sb.Append("\x1b[?25h");
        return sb.ToString();
    }

    private static string SanitizeSnapshotLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return string.Empty;

        var sb = new StringBuilder(line.Length);
        foreach (var ch in line)
        {
            if (ch == '\x1b' || char.IsControl(ch))
                continue;

            sb.Append(ch);
        }

        return sb.ToString();
    }

    // --- AI Chat ---

    private async Task<(string context, bool isTui)> GetTerminalContextAsync()
    {
        const int maxLength = 6000;
        try
        {
            var snapshot = await CaptureTerminalSnapshotOnUiThreadAsync();

            if (snapshot is not null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Snapshot] type={snapshot.BufferType}, cursor=({snapshot.CursorX},{snapshot.CursorY}), " +
                    $"size={snapshot.Cols}x{snapshot.Rows}, lines={snapshot.Lines.Length}, isTui={snapshot.IsTuiActive}");

                var text = snapshot.IsTuiActive
                    ? snapshot.GetNumberedScreenText()
                    : snapshot.GetScreenText();

                if (text.Length > maxLength)
                    text = text[..maxLength];

                if (snapshot.IsTuiActive)
                    text += $"\n[Cursor: row {snapshot.CursorY + 1}, col {snapshot.CursorX + 1}]";

                return (text, snapshot.IsTuiActive);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SnapshotService] CaptureAsync failed: {ex.Message}");
        }

        var fallback = VirtualScreen.GetSnapshot();
        if (fallback.Length > maxLength)
            fallback = fallback[..maxLength];
        return (fallback, false);
    }

    private async Task<TerminalSnapshot?> CaptureTerminalSnapshotOnUiThreadAsync()
    {
        var tcs = new TaskCompletionSource<TerminalSnapshot?>();
        await _dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var result = await SnapshotService.CaptureAsync();
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return await tcs.Task;
    }

    private async Task SendMessageAsync()
    {
        var input = ChatInput?.Trim();
        if (string.IsNullOrEmpty(input)) return;

        ChatMessages.Add(new ChatMessage { Role = "user", Content = input });
        ChatInput = string.Empty;

        _autoExecCts?.Cancel();
        _autoExecCts = new CancellationTokenSource();
        var ct = _autoExecCts.Token;
        var gen = ++_autoExecGeneration;

        IsAutoExecuting = true;
        _autoExecStep = 0;

        try
        {
            IsAiLoading = true;
            AutoExecStatus = "AI is thinking...";

            var (terminalContext, isTui) = await GetTerminalContextAsync();
            var history = new List<ChatMessage>(ChatMessages);
            var result = await _aiChatService.SendAsync(
                _aiSettings, history, input, terminalContext,
                isAutoExec: false, isTuiActive: isTui, ct: ct);

            await _dispatcher.InvokeAsync(() =>
            {
                IsAiLoading = false;
                LogDebug(result.RequestJson, result.ResponseJson);
                ChatMessages.Add(new ChatMessage { Role = "assistant", Content = result.Content });
            });

            if (result.Content.StartsWith("[Error]") || result.Content.StartsWith("[API Error"))
            {
                await _dispatcher.InvokeAsync(() => AutoExecStatus = string.Empty);
                return;
            }

            var commands = ParseCommandBlocks(result.Content);
            if (commands.Count == 0)
            {
                await _dispatcher.InvokeAsync(() => AutoExecStatus = string.Empty);
                return;
            }

            await _dispatcher.InvokeAsync(() => ExecuteCommands(commands));

            await RunAutoExecLoopAsync(string.Join("\n", commands), ct);
        }
        catch (OperationCanceledException)
        {
            await _dispatcher.InvokeAsync(() => AutoExecStatus = "Stopped by user.");
        }
        catch (Exception ex)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                ChatMessages.Add(new ChatMessage { Role = "assistant", Content = $"[Error]: {ex.Message}" });
                AutoExecStatus = "Error.";
            });
        }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                IsAiLoading = false;
                if (gen == _autoExecGeneration)
                    IsAutoExecuting = false;
            });
        }
    }

    private async Task RunAutoExecLoopAsync(string initialCommandBlock, CancellationToken ct)
    {
        string? previousCommandBlock = initialCommandBlock;

        while (!ct.IsCancellationRequested && _autoExecStep < AutoExecMaxSteps)
        {
            _autoExecStep++;

            await _dispatcher.InvokeAsync(() =>
                AutoExecStatus = $"Waiting for terminal output (step {_autoExecStep}/{AutoExecMaxSteps})...");

            await OutputStabilizer.WaitForStableOutputAsync(ct);

            ct.ThrowIfCancellationRequested();

            await _dispatcher.InvokeAsync(() =>
            {
                AutoExecStatus = $"AI auto-executing (step {_autoExecStep}/{AutoExecMaxSteps})...";
                IsAiLoading = true;
            });

            var (terminalContext, isTui) = await GetTerminalContextAsync();
            var history = await _dispatcher.InvokeAsync(() => new List<ChatMessage>(ChatMessages));
            var result = await _aiChatService.SendAsync(
                _aiSettings, history, string.Empty, terminalContext,
                isAutoExec: true, isTuiActive: isTui, ct: ct);

            await _dispatcher.InvokeAsync(() =>
            {
                IsAiLoading = false;
                LogDebug(result.RequestJson, result.ResponseJson);
            });

            if (result.Content.StartsWith("[Error]") || result.Content.StartsWith("[API Error"))
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    ChatMessages.Add(new ChatMessage { Role = "assistant", Content = result.Content });
                    AutoExecStatus = "Auto-exec stopped due to error.";
                });
                return;
            }

            var commands = ParseCommandBlocks(result.Content);

            await _dispatcher.InvokeAsync(() =>
            {
                ChatMessages.Add(new ChatMessage { Role = "assistant", Content = result.Content });
            });

            if (commands.Count == 0)
            {
                await _dispatcher.InvokeAsync(() => AutoExecStatus = "Auto-exec completed.");
                return;
            }

            var currentCommandBlock = string.Join("\n", commands);
            if (string.Equals(currentCommandBlock, previousCommandBlock, StringComparison.Ordinal))
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    AutoExecStatus = "Auto-exec stopped: repeated command detected.";
                    ChatMessages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content = "[Auto-exec stopped: AI repeated the same command]"
                    });
                });
                return;
            }
            previousCommandBlock = currentCommandBlock;

            await _dispatcher.InvokeAsync(() => ExecuteCommands(commands));
        }

        if (_autoExecStep >= AutoExecMaxSteps && !ct.IsCancellationRequested)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                AutoExecStatus = $"Reached max steps ({AutoExecMaxSteps}). Stopped.";
                ChatMessages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = $"[Auto-exec stopped: reached maximum {AutoExecMaxSteps} steps]"
                });
            });
        }
    }

    private void StopAutoExec()
    {
        _autoExecCts?.Cancel();
    }

    private void ExecuteCommands(List<string> commands)
    {
        foreach (var cmd in commands)
        {
            var lines = cmd.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.TrimEnd('\r', ' ');
                if (string.IsNullOrEmpty(trimmed)) continue;

                if (KeyTokenRegex.IsMatch(trimmed))
                    ExecuteLineWithTokens(trimmed);
                else
                    ExecuteTerminalCommand?.Invoke(trimmed + "\r");
            }
        }
    }

    private void ExecuteLineWithTokens(string line)
    {
        var lastIndex = 0;
        foreach (Match match in KeyTokenRegex.Matches(line))
        {
            if (match.Index > lastIndex)
            {
                var text = line[lastIndex..match.Index];
                ExecuteTerminalCommand?.Invoke(text);
            }

            var tokenName = match.Groups[1].Value;
            if (KeyTokens.TryGetValue(tokenName, out var vt))
                ExecuteTerminalCommand?.Invoke(vt);
            else
                ExecuteTerminalCommand?.Invoke(match.Value);

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < line.Length)
        {
            var tail = line[lastIndex..];
            ExecuteTerminalCommand?.Invoke(tail);
        }
    }

    private void LogDebug(string requestJson, string responseJson)
    {
        if (!EnableDebugLog)
            return;

        var timestamp = DateTime.Now.ToString("HH:mm:ss");

        void AddEntries()
        {
            while (DebugLogs.Count >= MaxDebugLogEntries - 1)
                DebugLogs.RemoveAt(0);

            DebugLogs.Add($"=== [{timestamp}] REQUEST ===\n{requestJson}");
            DebugLogs.Add($"=== [{timestamp}] RESPONSE ===\n{responseJson}");
        }

        if (_dispatcher.CheckAccess())
            AddEntries();
        else
            _dispatcher.InvokeAsync(AddEntries);

        try
        {
            var logDir = System.IO.Path.Combine(AppContext.BaseDirectory, "data");
            System.IO.Directory.CreateDirectory(logDir);
            var logFile = System.IO.Path.Combine(logDir, "ai-debug.log");
            var logEntry = $"=== [{timestamp}] REQUEST ===\n{requestJson}\n\n=== [{timestamp}] RESPONSE ===\n{responseJson}\n\n{"".PadRight(80, '=')}\n\n";
            System.IO.File.AppendAllText(logFile, logEntry);
        }
        catch
        {
            // Ignore file write errors
        }
    }

    // --- Standalone mode ---

    private static readonly System.Text.Json.JsonSerializerOptions _standaloneJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed class InvitePayload
    {
        public string RelayUrl { get; set; } = string.Empty;
        public string InviteCode { get; set; } = string.Empty;
        public string? GroupId { get; set; }
    }

    /// <summary>
    /// Patch the internal HttpListener to add /api/ prefixes so standalone endpoints
    /// (/api/invite, /api/standalone/info) are reachable.
    /// </summary>
    private void PatchHttpListenerApiPrefixes(RelayServer server)
    {
        try
        {
            var listenerField = typeof(RelayServer).GetField("_httpListener",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (listenerField?.GetValue(server) is HttpListener listener)
            {
                var existingPrefixes = listener.Prefixes.ToList();
                foreach (var prefix in existingPrefixes)
                {
                    var apiPrefix = prefix.Replace("/ws/", "/api/");
                    if (!listener.Prefixes.Contains(apiPrefix))
                    {
                        listener.Prefixes.Add(apiPrefix);
                        OnNetworkLog($"[standalone] Added HTTP prefix: {apiPrefix}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            OnNetworkLog($"[standalone] Warning: failed to patch HTTP prefixes: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles /api/invite endpoint for both relay server and standalone modes.
    /// When this device receives an invite, it transitions to client mode and joins the specified group.
    /// </summary>
    private async Task<bool> HandleInviteHttpAsync(HttpListenerContext context, string path)
    {
        if (path == "/api/invite" && context.Request.HttpMethod == "POST")
        {
            try
            {
                using var reader = new System.IO.StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                var invite = System.Text.Json.JsonSerializer.Deserialize<InvitePayload>(body, _standaloneJsonOptions);

                if (invite is null || string.IsNullOrWhiteSpace(invite.RelayUrl) ||
                    string.IsNullOrWhiteSpace(invite.InviteCode))
                {
                    await WriteStandaloneJsonAsync(context.Response, 400, new
                    {
                        type = "error", code = "bad_request",
                        message = "relayUrl and inviteCode are required."
                    });
                    return true;
                }

                OnNetworkLog($"[invite] Received invite: relay={invite.RelayUrl} code={invite.InviteCode}");

                var relayUrl = invite.RelayUrl;
                var inviteCode = invite.InviteCode;
                var groupId = invite.GroupId ?? "";

                // Send response first, then transition (transition disposes the HTTP listener)
                await WriteStandaloneJsonAsync(context.Response, 200, new
                {
                    status = "accepted", relayUrl = invite.RelayUrl
                });

                _ = Task.Run(async () =>
                {
                    // Give HttpListener a brief window to flush the response before the
                    // standalone listener is torn down during client transition.
                    await Task.Delay(200);
                    await _dispatcher.InvokeAsync(() => TransitionToClientAsync(relayUrl, inviteCode, groupId));
                });
            }
            catch (Exception ex)
            {
                OnNetworkLog($"[invite] Error: {ex.Message}");
                await WriteStandaloneJsonAsync(context.Response, 400, new
                {
                    type = "error", code = "bad_request", message = "Invalid JSON body."
                });
            }
            return true;
        }

        // Handle OPTIONS for CORS preflight
        if (path == "/api/invite" && context.Request.HttpMethod == "OPTIONS")
        {
            context.Response.AddHeader("Access-Control-Allow-Origin", "*");
            context.Response.AddHeader("Access-Control-Allow-Methods", "POST, OPTIONS");
            context.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
            context.Response.StatusCode = 204;
            context.Response.Close();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Handles /api/invite and /api/standalone/info endpoints for standalone mode.
    /// </summary>
    private async Task<bool> HandleStandaloneHttpAsync(HttpListenerContext context, string path)
    {
        // Delegate /api/invite to the shared invite handler
        if (await HandleInviteHttpAsync(context, path))
            return true;

        // GET /api/standalone/info — return device info for phone connection
        if (path == "/api/standalone/info" && context.Request.HttpMethod == "GET")
        {
            var baseWsUrl = _relayServer?.ReachableWebSocketUrls.FirstOrDefault() ?? "";
            var wsUrl = AppendOrReplaceTokenQuery(baseWsUrl, _relayServer?.AuthToken);
            var httpUrl = BuildHttpUrlFromWebSocketUrl(baseWsUrl);

            await WriteStandaloneJsonAsync(context.Response, 200, new
            {
                deviceId = _identity.DeviceId,
                displayName = _identity.DisplayName,
                os = "Windows",
                availableShells = AvailableShells.Select(s => s.Id).ToList(),
                httpUrl,
                wsUrl
            });
            return true;
        }

        return false;
    }

    private static async Task WriteStandaloneJsonAsync(HttpListenerResponse response, int statusCode, object data)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(data, _standaloneJsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.AddHeader("Access-Control-Allow-Origin", "*");
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    /// <summary>
    /// Transition from standalone mode to client mode after receiving an invite.
    /// Stops the local RelayServer and connects as a RelayClient to the specified relay.
    /// Must be called on the UI thread.
    /// </summary>
    private async Task TransitionToClientAsync(string relayUrl, string inviteCode, string groupId)
    {
        try
        {
            OnNetworkLog($"[standalone] Transitioning to client mode: {relayUrl}");

            // Stop current relay server (partial StopNetwork — keep UI running)
            if (_relayServer is not null)
            {
                _relayServer.Log -= OnNetworkLog;
                _relayServer.LocalTerminalInputReceived -= OnRemoteTerminalInput;
                _relayServer.LocalTerminalResizeReceived -= OnRemoteTerminalResize;
                _relayServer.LocalTerminalSessionEnded -= OnRemoteTerminalSessionEnded;
                _relayServer.LocalTerminalCloseRequested -= OnLocalTerminalCloseRequested;
                _relayServer.LocalSessionRenameRequested -= OnLocalSessionRenameRequested;
                _relayServer.LocalTerminalOpenRequested -= OnRemoteTerminalOpenRequested;
                _relayServer.LocalTerminalSnapshotProvider = null;
                _relayServer.LocalTerminalSizeProvider = null;
                _relayServer.LocalSessionListProvider = null;
                _relayServer.LocalQuickPanelSyncProvider = null;
                _relayServer.LocalRecentInputAppendRequested -= AppendRecentInputFromMobile;
                _relayServer.GroupMemberListChanged -= OnGroupMemberListChanged;
                _relayServer.RemoteTerminalOpenedReceived -= OnRemoteTerminalOpenedReceived;
                _relayServer.RemoteTerminalOutputReceived -= OnRemoteTerminalOutputReceived;
                _relayServer.RemoteTerminalClosedReceived -= OnRemoteTerminalClosedReceived;
                _relayServer.ServerMigrationCommitted -= OnServerMigrationCommitted;
                _relayServer.Dispose();
                _relayServer = null;
            }

            _isStandaloneMode = false;

            // Clear old group data so AutoMode won't restore relay server mode on next start
            _groupStore.ClearGroup();

            // Create relay client
            _relayClient = new RelayClient
            {
                DeviceId = _identity.DeviceId,
                DisplayName = _identity.DisplayName,
                Os = "Windows",
                AvailableShells = AvailableShells.Select(s => s.Id).ToList(),
                InviteCode = inviteCode,
                LocalSessionListProvider = GetLocalSessionList,
                LocalQuickPanelSyncProvider = BuildQuickPanelSyncSnapshot,
                LocalRecentInputAppendRequested = AppendRecentInputFromMobile
            };
            _relayClient.Log += OnNetworkLog;
            _relayClient.ConnectionStateChanged += OnClientConnectionStateChanged;
            _relayClient.TerminalInputReceived += OnRemoteTerminalInput;
            _relayClient.TerminalOpenRequested += OnRelayClientTerminalOpenRequested;
            _relayClient.TerminalResizeRequested += OnRemoteTerminalResize;
            _relayClient.TerminalCloseRequested += OnRemoteTerminalCloseRequested;
            _relayClient.TerminalDetachRequested += OnRemoteTerminalSessionEnded;
            _relayClient.GroupJoined += OnGroupJoined;
            _relayClient.GroupJoinRejected += OnGroupJoinRejected;
            _relayClient.GroupMemberChanged += OnGroupMemberListChanged;
            _relayClient.SessionListReceived += OnRemoteSessionListReceived;
            _relayClient.SessionRenameRequested += OnSessionRenameRequested;
            _relayClient.ServerChanged += OnServerChanged;
            _relayClient.ServerChangeRequested += OnServerChangeRequested;
            _relayClient.GroupSecretRotated += OnGroupSecretRotated;
            _relayClient.DeviceUnbound += OnDeviceUnbound;
            _relayClient.TerminalOpenedReceived += OnRemoteTerminalOpenedReceived;
            _relayClient.TerminalOutputReceived += OnRemoteTerminalOutputReceived;
            _relayClient.TerminalBufferReceived += OnRemoteTerminalBufferReceived;
            _relayClient.TerminalClosedReceived += OnRemoteTerminalClosedReceived;
            _relayClient.RelayDesignated += OnRelayDesignated;
            _relayClient.InviteCreated += OnInviteCreated;
            _relayClient.DeviceSettingsUpdated += OnDeviceSettingsUpdated;
            _relayClient.DeviceKicked += OnStandaloneClientKicked;
            _relayClient.GroupDissolved += OnStandaloneClientDissolved;

            // Update settings to reflect client mode
            RelayServerAddress = relayUrl;
            SetRelayServerMode(false);
            SaveServerSettings();

            // Fire-and-forget: ConnectAsync blocks in its receive loop.
            // The InviteCode is set on the client, so ConnectInternalAsync will
            // automatically send group.join.request with the invite code after connecting.
            _ = _relayClient.ConnectAsync(relayUrl);

            ServerStatus = $"Transitioning to relay client: {relayUrl}";
            ConnectionStatus = "Joining group via invite...";

            OnNetworkLog("[standalone] Transition to client complete");
        }
        catch (Exception ex)
        {
            OnNetworkLog($"[standalone] Transition to client failed: {ex.Message}");
            ServerStatus = $"Transition failed: {ex.Message}. Restarting standalone...";

            // Clean up failed client
            if (_relayClient is not null)
            {
                _relayClient.Dispose();
                _relayClient = null;
            }

            // Restart in standalone mode
            RelayServerAddress = string.Empty;
            SetRelayServerMode(false);
            IsServerRunning = false;
            SaveServerSettings();
            await StartNetworkAsync();
        }
    }

    private void OnStandaloneClientKicked(string reason)
    {
        _dispatcher.InvokeAsync(async () =>
        {
            OnNetworkLog($"[standalone] Kicked from group: {reason}");
            StopNetwork("standalone_client_kicked");
            ClearClientMembershipState();
            GroupStatus = $"Kicked: {reason}";
            ServerStatus = "Kicked from group. Restarting standalone...";
            SetRelayServerMode(false);
            await StartNetworkAsync();
        });
    }

    private void OnStandaloneClientDissolved(string reason)
    {
        _dispatcher.InvokeAsync(async () =>
        {
            OnNetworkLog($"[standalone] Group dissolved: {reason}");
            StopNetwork("standalone_client_dissolved");
            ClearClientMembershipState();
            GroupStatus = $"Group dissolved: {reason}";
            ServerStatus = "Group dissolved. Restarting standalone...";
            SetRelayServerMode(false);
            await StartNetworkAsync();
        });
    }

    public void SetUiLanguage(bool isEnglish)
    {
        var nextLabel = isEnglish ? DefaultLocalSessionTargetEn : DefaultLocalSessionTargetZh;
        if (string.Equals(_localSessionTargetLabel, nextLabel, StringComparison.Ordinal))
            return;

        _localSessionTargetLabel = nextLabel;
        OnPropertyChanged(nameof(CurrentSessionTargetDisplayName));
        OnPropertyChanged(nameof(NewSessionTargetButtonText));
    }

    private static List<string> ParseCommandBlocks(string response)
    {
        var commands = new List<string>();
        var matches = Regex.Matches(response, @"```command\s*\n(.*?)```", RegexOptions.Singleline);
        foreach (Match match in matches)
        {
            var cmd = match.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(cmd))
                commands.Add(cmd);
        }
        return commands;
    }
}
