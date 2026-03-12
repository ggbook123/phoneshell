using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

public sealed class MainViewModel : ObservableObject, IDisposable
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

    private readonly DeviceIdentityStore _identityStore;
    private readonly QrCodeService _qrCodeService;
    private readonly QrPayloadBuilder _payloadBuilder;
    private readonly AiSettingsStore _aiSettingsStore;
    private readonly AiChatService _aiChatService;
    private readonly ServerSettingsStore _serverSettingsStore;
    private readonly GroupStore _groupStore;
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
    private bool _isAutoExecuting;
    private int _autoExecStep;
    private const int AutoExecMaxSteps = 10;
    private const int MaxDebugLogEntries = 100;
    private string _autoExecStatus = string.Empty;

    // Server/Client fields
    private ServerSettings _serverSettings;
    private bool _isAutoMode;
    private bool _isRelayServer;
    private int _serverPort = 9090;
    private string _relayServerAddress = string.Empty;
    private bool _isServerRunning;
    private string _serverStatus = "Stopped";
    private string _primaryRelayAddress = string.Empty;
    private string _relayReachableAddresses = string.Empty;
    private RelayServer? _relayServer;
    private RelayClient? _relayClient;

    // Group fields
    private string _groupSecret = string.Empty;
    private string _groupId = string.Empty;
    private bool _isGroupJoined;
    private string _groupStatus = string.Empty;

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
        NewSessionCommand = new RelayCommand(CreateNewSession);
        CloseTabCommand = new RelayCommand<string>(CloseTab);
        SwitchTabCommand = new RelayCommand<string>(SwitchTab);
        ToggleCompactModeCommand = new RelayCommand<string>(ToggleCompactMode);

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
                UpdateRelayAddressPreview();
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

    public ObservableCollection<GroupMemberInfo> GroupMembers { get; } = new();

    // Shell selection properties
    public ObservableCollection<ShellInfo> AvailableShells
    {
        get => _availableShells;
        private set => SetProperty(ref _availableShells, value);
    }

    public ShellInfo? SelectedShell
    {
        get => _selectedShell;
        set => SetProperty(ref _selectedShell, value);
    }

    // Multi-tab properties
    public ObservableCollection<TerminalTab> Tabs { get; } = new();

    public TerminalTab? ActiveTab
    {
        get => _activeTab;
        private set
        {
            if (SetProperty(ref _activeTab, value))
            {
                OnPropertyChanged(nameof(HasTabs));
                UpdateExecuteTerminalCommand();
            }
        }
    }

    public bool HasTabs => Tabs.Count > 0;

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
    public RelayCommand NewSessionCommand { get; }
    public RelayCommand<string> CloseTabCommand { get; }
    public RelayCommand<string> SwitchTabCommand { get; }
    public RelayCommand<string> ToggleCompactModeCommand { get; }

    public void Dispose()
    {
        _autoExecCts?.Cancel();
        _autoExecCts?.Dispose();
        _aiChatService.Dispose();

        foreach (var tab in Tabs)
            tab.Dispose();
        Tabs.Clear();

        _relayServer?.Dispose();
        _relayClient?.Dispose();
    }

    // --- Multi-Tab Management ---

    public void CreateNewSession()
    {
        var shell = SelectedShell ?? _shellLocator.GetDefaultShell();
        CreateTab(shell, 0, 0);
    }

    public TerminalTab CreateTab(ShellInfo shell, int cols, int rows)
    {
        _tabCounter++;
        var tabId = Guid.NewGuid().ToString("N")[..8];
        var tab = new TerminalTab(tabId, shell, _tabCounter);

        tab.SessionManager.OutputReceived += text => OnTabOutput(tab, text);

        Tabs.Add(tab);
        OnPropertyChanged(nameof(HasTabs));

        SetActiveTab(tab);
        NotifyLocalSessionListChanged();

        if (cols > 0 && rows > 0)
        {
            StartTabTerminal(tab, cols, rows);
        }

        return tab;
    }

    public TerminalTab CreateRemoteTab(string remoteDeviceId, string remoteDeviceName,
                                        string shellId, string remoteSessionId, int cols, int rows)
    {
        _tabCounter++;
        var tabId = Guid.NewGuid().ToString("N")[..8];
        var tab = new TerminalTab(tabId, remoteDeviceId, remoteDeviceName, shellId, _tabCounter);
        tab.RemoteSessionId = remoteSessionId;
        tab.Cols = cols;
        tab.Rows = rows;
        tab.VirtualScreen.Resize(cols, rows);

        Tabs.Add(tab);
        OnPropertyChanged(nameof(HasTabs));
        SetActiveTab(tab);

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
        var tab = Tabs.FirstOrDefault(t => t.TabId == tabId);
        if (tab is null || tab == ActiveTab) return;
        SetActiveTab(tab);
    }

    private void CloseTab(string? tabId)
    {
        if (string.IsNullOrEmpty(tabId)) return;
        var tab = Tabs.FirstOrDefault(t => t.TabId == tabId);
        if (tab is null) return;

        // Remote tab: notify remote device to close
        if (tab.IsRemote && !string.IsNullOrEmpty(tab.RemoteSessionId))
        {
            if (_relayClient is not null && _relayClient.IsConnected)
                _ = _relayClient.SendTerminalCloseAsync(tab.RemoteDeviceId, tab.RemoteSessionId);
        }

        var index = Tabs.IndexOf(tab);
        if (!tab.IsRemote)
            NotifyLocalTerminalClosed(tab);
        tab.SessionManager.OutputReceived -= text => OnTabOutput(tab, text);
        tab.Dispose();
        Tabs.Remove(tab);
        OnPropertyChanged(nameof(HasTabs));
        NotifyLocalSessionListChanged();
        UpdateMobileConnectionState();

        if (ActiveTab == tab)
        {
            if (Tabs.Count > 0)
            {
                var newIndex = Math.Min(index, Tabs.Count - 1);
                SetActiveTab(Tabs[newIndex]);
            }
            else
            {
                SetActiveTab(null);
                SessionStatus = "No session";
            }
        }
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
                TerminalViewportAutoFitRequested?.Invoke();

                // If we have saved desktop dimensions, apply them immediately
                if (tab.DesktopCols > 0 && tab.DesktopRows > 0)
                {
                    ApplyTabTerminalSize(tab, tab.DesktopCols, tab.DesktopRows);
                }
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

        if (tab == ActiveTab)
        {
            TerminalViewportAutoFitRequested?.Invoke();
        }

        if (tab.DesktopCols > 0 && tab.DesktopRows > 0)
        {
            ApplyTabTerminalSize(tab, tab.DesktopCols, tab.DesktopRows);
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

        // Forward to network
        ForwardTabOutputToNetwork(tab, text);

        // Only forward to UI if this is the active tab
        if (tab == ActiveTab)
            TerminalOutputForwarded?.Invoke(text);
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
        return tab.OutputBuffer.GetRecentRaw();
    }

    // --- Viewport resize handling ---

    public void HandleLocalViewportResize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0) return;

        if (ActiveTab is not null)
        {
            // Always save desktop dimensions unless in compact mode
            if (!ActiveTab.IsCompactMode)
            {
                ActiveTab.DesktopCols = cols;
                ActiveTab.DesktopRows = rows;
            }

            // Don't apply resize if compact mode or mobile viewport is locked
            if (ActiveTab.IsCompactMode || ActiveTab.IsMobileViewportLocked) return;

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
        else
        {
            QrPayload = _payloadBuilder.Build(_identity);
        }
        QrImage = _qrCodeService.Generate(QrPayload);
    }

    private void ForceDisconnect()
    {
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
        _serverSettingsStore.Save(_serverSettings);
        UpdateRelayAddressPreview();
    }

    private void ClearPersistedGroupIfRequested()
    {
        if (!IsRelayServer || !string.IsNullOrWhiteSpace(GroupSecret))
            return;
        ClearServerGroupFiles();
    }

    private void SaveGroupMembership(string groupId, string groupSecret)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(groupSecret))
                return;

            _groupStore.SaveMembership(new GroupMembership
            {
                GroupId = groupId,
                GroupSecret = groupSecret
            });
        }
        catch (Exception ex)
        {
            OnNetworkLog($"Save membership failed: {ex.Message}");
        }
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
    }

    private void ApplyAutoRelayMode()
    {
        if (!IsAutoMode || IsServerRunning)
            return;

        var existingGroup = _groupStore.LoadGroup();
        var membership = _groupStore.LoadMembership();

        if (existingGroup is not null)
        {
            IsRelayServer = true;
            RelayServerAddress = string.Empty;
            if (string.IsNullOrWhiteSpace(GroupSecret))
                GroupSecret = existingGroup.GroupSecret;
            return;
        }

        if (membership is not null)
        {
            IsRelayServer = false;
            if (string.IsNullOrWhiteSpace(GroupSecret))
                GroupSecret = membership.GroupSecret;
            return;
        }

        if (!string.IsNullOrWhiteSpace(RelayServerAddress))
        {
            IsRelayServer = false;
            return;
        }

        // First run default: act as relay server so QR is available
        IsRelayServer = true;
        RelayServerAddress = string.Empty;
    }

    // --- Network Start/Stop ---

    private async Task StartNetworkAsync()
    {
        if (IsAutoMode)
            ApplyAutoRelayMode();

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
                _relayServer.Log += OnNetworkLog;
                _relayServer.LocalTerminalInputReceived += OnRemoteTerminalInput;
                _relayServer.LocalTerminalResizeReceived += OnRemoteTerminalResize;
                _relayServer.LocalTerminalSessionEnded += OnRemoteTerminalSessionEnded;
                _relayServer.LocalTerminalOpenRequested += OnRemoteTerminalOpenRequested;
                _relayServer.LocalTerminalSnapshotProvider = CaptureTabTerminalViewAsync;
                _relayServer.LocalTerminalSizeProvider = GetTabTerminalSize;
                _relayServer.LocalSessionListProvider = GetLocalSessionList;
                _relayServer.GroupMemberListChanged += OnGroupMemberListChanged;
                _relayServer.RemoteTerminalOpenedReceived += OnRemoteTerminalOpenedReceived;
                _relayServer.RemoteTerminalOutputReceived += OnRemoteTerminalOutputReceived;
                _relayServer.RemoteTerminalClosedReceived += OnRemoteTerminalClosedReceived;
                _relayServer.ServerMigrationCommitted += OnServerMigrationCommitted;
                _relayServer.GroupMergeRequested += OnGroupMergeRequested;

                // Use GroupSecret as AuthToken if provided
                if (!string.IsNullOrWhiteSpace(GroupSecret))
                    _relayServer.AuthToken = GroupSecret;

                await _relayServer.StartAsync(ServerPort, AppContext.BaseDirectory);

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
                    _dispatcher.InvokeAsync(() =>
                    {
                        GroupMembers.Clear();
                        foreach (var m in members)
                            GroupMembers.Add(m);
                    });

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
                    GroupSecret = GroupSecret
                };
                _relayClient.Log += OnNetworkLog;
                _relayClient.ConnectionStateChanged += OnClientConnectionStateChanged;
                _relayClient.TerminalInputReceived += OnRemoteTerminalInput;
                _relayClient.TerminalResizeRequested += OnRemoteTerminalResize;
                _relayClient.TerminalCloseRequested += OnRemoteTerminalCloseRequested;
                _relayClient.GroupJoined += OnGroupJoined;
                _relayClient.GroupJoinRejected += OnGroupJoinRejected;
                _relayClient.GroupMemberChanged += OnGroupMemberListChanged;
                _relayClient.ServerChanged += OnServerChanged;
                _relayClient.ServerChangeRequested += OnServerChangeRequested;
                _relayClient.GroupSecretRotated += OnGroupSecretRotated;
                _relayClient.TerminalOpenedReceived += OnRemoteTerminalOpenedReceived;
                _relayClient.TerminalOutputReceived += OnRemoteTerminalOutputReceived;
                _relayClient.TerminalClosedReceived += OnRemoteTerminalClosedReceived;

                _ = _relayClient.ConnectAsync(url);

                IsServerRunning = true;
                ServerStatus = $"Connecting to {url}...";
                ConnectionStatus = $"Connecting to relay...";
            }
            else
            {
                ServerStatus = "Enter a relay server address or enable relay mode";
            }
        }
        catch (Exception ex)
        {
            ServerStatus = $"Error: {ex.Message}";
            OnNetworkLog($"Start error: {ex.Message}");
        }
    }

    private void StopNetwork()
    {
        if (_relayServer is not null)
        {
            _relayServer.Log -= OnNetworkLog;
            _relayServer.LocalTerminalInputReceived -= OnRemoteTerminalInput;
            _relayServer.LocalTerminalResizeReceived -= OnRemoteTerminalResize;
            _relayServer.LocalTerminalSessionEnded -= OnRemoteTerminalSessionEnded;
            _relayServer.LocalTerminalOpenRequested -= OnRemoteTerminalOpenRequested;
            _relayServer.LocalTerminalSnapshotProvider = null;
            _relayServer.LocalTerminalSizeProvider = null;
            _relayServer.LocalSessionListProvider = null;
            _relayServer.GroupMemberListChanged -= OnGroupMemberListChanged;
            _relayServer.RemoteTerminalOpenedReceived -= OnRemoteTerminalOpenedReceived;
            _relayServer.RemoteTerminalOutputReceived -= OnRemoteTerminalOutputReceived;
            _relayServer.RemoteTerminalClosedReceived -= OnRemoteTerminalClosedReceived;
            _relayServer.ServerMigrationCommitted -= OnServerMigrationCommitted;
            _relayServer.GroupMergeRequested -= OnGroupMergeRequested;
            _relayServer.Dispose();
            _relayServer = null;
        }

        if (_relayClient is not null)
        {
            _relayClient.Log -= OnNetworkLog;
            _relayClient.ConnectionStateChanged -= OnClientConnectionStateChanged;
            _relayClient.TerminalInputReceived -= OnRemoteTerminalInput;
            _relayClient.TerminalResizeRequested -= OnRemoteTerminalResize;
            _relayClient.TerminalCloseRequested -= OnRemoteTerminalCloseRequested;
            _relayClient.GroupJoined -= OnGroupJoined;
            _relayClient.GroupJoinRejected -= OnGroupJoinRejected;
            _relayClient.GroupMemberChanged -= OnGroupMemberListChanged;
            _relayClient.ServerChanged -= OnServerChanged;
            _relayClient.ServerChangeRequested -= OnServerChangeRequested;
            _relayClient.GroupSecretRotated -= OnGroupSecretRotated;
            _relayClient.TerminalOpenedReceived -= OnRemoteTerminalOpenedReceived;
            _relayClient.TerminalOutputReceived -= OnRemoteTerminalOutputReceived;
            _relayClient.TerminalClosedReceived -= OnRemoteTerminalClosedReceived;
            _relayClient.Dispose();
            _relayClient = null;
        }

        RestoreDesktopTerminalViewport();
        IsServerRunning = false;
        IsGroupJoined = false;
        GroupMembers.Clear();
        GroupStatus = string.Empty;
        UpdateRelayAddressPreview();
        ConnectionStatus = "Local only";
    }

    private void OnNetworkLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var entry = $"[{timestamp}] [NET] {message}";
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

            GroupMembers.Clear();
            foreach (var m in accepted.Members)
                GroupMembers.Add(m);

            SaveGroupMembership(accepted.GroupId, GroupSecret);
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

    private void OnGroupMemberListChanged(List<GroupMemberInfo> members)
    {
        _dispatcher.InvokeAsync(() =>
        {
            GroupMembers.Clear();
            foreach (var m in members)
                GroupMembers.Add(m);
            GroupStatus = $"Group: {members.Count} member(s)";
        });
    }

    private void OnGroupSecretRotated(string newSecret)
    {
        _dispatcher.InvokeAsync(() =>
        {
            GroupSecret = newSecret;
            _serverSettings.GroupSecret = newSecret;
            _serverSettingsStore.Save(_serverSettings);
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

    private void OnGroupMergeRequested(string targetServerUrl, string targetGroupSecret, string targetGroupId)
    {
        _dispatcher.InvokeAsync(async () =>
        {
            OnNetworkLog($"Group merge: switching to client, connecting to {targetServerUrl}");
            await SwitchToClientAsync(targetServerUrl, targetGroupSecret, targetGroupId);
        });
    }

    // --- Remote terminal event handlers (for PC-to-PC remote terminals) ---

    private void OnRemoteTerminalOpenedReceived(string deviceId, string sessionId, int cols, int rows)
    {
        _dispatcher.InvokeAsync(() =>
        {
            var member = GroupMembers.FirstOrDefault(m => m.DeviceId == deviceId);
            var deviceName = member?.DisplayName ?? deviceId[..Math.Min(8, deviceId.Length)];

            var tab = CreateRemoteTab(deviceId, deviceName, "remote", sessionId, cols, rows);
            OnNetworkLog($"Remote tab created: [{deviceName}] session={sessionId}");
        });
    }

    private void OnRemoteTerminalOutputReceived(string deviceId, string sessionId, string data)
    {
        _dispatcher.InvokeAsync(() =>
        {
            var tab = Tabs.FirstOrDefault(t => t.IsRemote && t.RemoteSessionId == sessionId);
            if (tab is null) return;

            tab.OutputBuffer.Append(data);
            tab.VirtualScreen.Write(data);

            if (tab == ActiveTab)
                TerminalOutputForwarded?.Invoke(data);
        });
    }

    private void OnRemoteTerminalClosedReceived(string deviceId, string sessionId)
    {
        _dispatcher.InvokeAsync(() =>
        {
            var tab = Tabs.FirstOrDefault(t => t.IsRemote && t.RemoteSessionId == sessionId);
            if (tab is not null)
            {
                CloseTab(tab.TabId);
                OnNetworkLog($"Remote terminal closed: device={deviceId}, session={sessionId}");
            }
        });
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

        // Persist group info so the new server keeps the existing group ID/secret.
        _groupStore.SaveGroup(new GroupInfo
        {
            GroupId = groupId,
            GroupSecret = groupSecret,
            ServerDeviceId = _identity.DeviceId,
            CreatedAt = DateTimeOffset.UtcNow
        });

        IsRelayServer = true;
        GroupSecret = groupSecret;
        RelayServerAddress = string.Empty;
        SaveServerSettings();
        _groupStore.ClearMembership();

        if (_relayServer is null || !_relayServer.IsRunning)
        {
            await StartNetworkAsync();
        }

        var newServerUrl = PrimaryRelayAddress;
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

        StopNetwork();

        ClearServerGroupFiles();
        IsRelayServer = false;
        RelayServerAddress = newUrl;
        GroupSecret = newSecret;
        SaveServerSettings();

        if (!string.IsNullOrWhiteSpace(groupId))
            SaveGroupMembership(groupId, newSecret);

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
                ClearMobileViewport(tab);
            }
            else
            {
                RestoreDesktopTerminalViewport();
            }
        });
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

                var tab = CreateTab(shell, 0, 0);

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
            return Tabs.Select(t => new SessionInfo
            {
                SessionId = t.TabId,
                ShellId = t.Shell.Id,
                Title = t.Title
            }).ToList();
        }

        return _dispatcher.Invoke(() =>
            Tabs.Select(t => new SessionInfo
            {
                SessionId = t.TabId,
                ShellId = t.Shell.Id,
                Title = t.Title
            }).ToList()
        );
    }

    private void NotifyLocalSessionListChanged()
    {
        if (_relayServer is null || !_relayServer.IsRunning) return;

        _ = _relayServer.BroadcastLocalSessionListChangedAsync(_identity.DeviceId);
    }

    private void NotifyLocalTerminalClosed(TerminalTab tab)
    {
        if (_relayServer is null || !_relayServer.IsRunning) return;

        _ = _relayServer.BroadcastLocalTerminalClosedAsync(_identity.DeviceId, tab.TabId);
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
