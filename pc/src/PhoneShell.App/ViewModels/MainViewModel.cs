using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhoneShell.Core.Models;
using PhoneShell.Core.Networking;
using PhoneShell.Core.Services;
using PhoneShell.Core.Terminals;
using PhoneShell.Core.Terminals.Windows;
using PhoneShell.Services;
using PhoneShell.Utilities;

namespace PhoneShell.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    // Key token mapping: {TOKEN} â†?VT sequence
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
    private readonly Dispatcher _dispatcher;

    private DeviceIdentity _identity = new();
    private string _deviceId = string.Empty;
    private string _qrPayload = string.Empty;
    private BitmapImage? _qrImage;
    private string _connectionStatus = "Initializing";
    private ControlOwner _controlOwner = ControlOwner.Pc;
    private bool _isMobileConnected;
    private string _sessionStatus = "Starting...";

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
    private bool _isRelayServer;
    private int _serverPort = 9090;
    private string _relayServerAddress = string.Empty;
    private bool _isServerRunning;
    private string _serverStatus = "Stopped";
    private string _primaryRelayAddress = string.Empty;
    private string _relayReachableAddresses = string.Empty;
    private RelayServer? _relayServer;
    private RelayClient? _relayClient;
    private const string LocalSessionId = "local";

    // Shell selection fields
    private readonly IShellLocator _shellLocator;
    private ObservableCollection<ShellInfo> _availableShells = new();
    private ShellInfo? _selectedShell;
    private int _terminalCols;
    private int _terminalRows;

    public MainViewModel()
    {
        _identityStore = new DeviceIdentityStore(AppContext.BaseDirectory);
        _qrCodeService = new QrCodeService();
        _payloadBuilder = new QrPayloadBuilder();
        _aiSettingsStore = new AiSettingsStore(AppContext.BaseDirectory);
        _aiChatService = new AiChatService();
        _serverSettingsStore = new ServerSettingsStore(AppContext.BaseDirectory);
        _shellLocator = new WindowsShellLocator();
        _dispatcher = Application.Current.Dispatcher;
        TerminalOutputBuffer = new TerminalOutputBuffer();
        OutputStabilizer = new TerminalOutputStabilizer();
        VirtualScreen = new VirtualScreen();
        TerminalManager = new TerminalSessionManager();

        _aiSettings = _aiSettingsStore.Load();
        _aiEndpoint = _aiSettings.ApiEndpoint;
        _aiApiKey = _aiSettings.ApiKey;
        _aiModelName = _aiSettings.ModelName;

        _serverSettings = _serverSettingsStore.Load();
        _isRelayServer = _serverSettings.IsRelayServer;
        _serverPort = _serverSettings.Port;
        _relayServerAddress = _serverSettings.RelayServerAddress ?? string.Empty;

        RefreshQrCommand = new RelayCommand(RefreshQr);
        ForceDisconnectCommand = new RelayCommand(ForceDisconnect, () => IsMobileConnected);
        SendMessageCommand = new AsyncRelayCommand(SendMessageAsync);
        SaveAiSettingsCommand = new RelayCommand(SaveAiSettings);
        StopAutoExecCommand = new RelayCommand(StopAutoExec);
        SaveServerSettingsCommand = new RelayCommand(SaveServerSettings);
        RefreshShellsCommand = new RelayCommand(RefreshShells);
        StartNetworkCommand = new AsyncRelayCommand(StartNetworkAsync, () => !IsServerRunning);
        StopNetworkCommand = new RelayCommand(StopNetwork, () => IsServerRunning);
        RestartTerminalCommand = new RelayCommand(RequestTerminalRestart);

        Initialize();
    }

    // --- Events ---

    /// <summary>
    /// Raised when the terminal needs to be restarted. MainWindow subscribes to handle WebView2 reset.
    /// Carries (cols, rows) of the current terminal for the new session.
    /// </summary>
    public event Action? TerminalRestarted;

    /// <summary>
    /// Raised when terminal output is received. MainWindow subscribes to push to WebView2.
    /// </summary>
    public event Action<string>? TerminalOutputForwarded;

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

    public ObservableCollection<ChatMessage> ChatMessages { get; } = new();
    public ObservableCollection<string> DebugLogs { get; } = new();
    public TerminalOutputBuffer TerminalOutputBuffer { get; }
    public TerminalOutputStabilizer OutputStabilizer { get; }
    public VirtualScreen VirtualScreen { get; }
    public TerminalSessionManager TerminalManager { get; private set; }
    public TerminalSnapshotService SnapshotService { get; } = new();
    public Action<string>? ExecuteTerminalCommand { get; set; }

    // Commands
    public RelayCommand RefreshQrCommand { get; }
    public RelayCommand ForceDisconnectCommand { get; }
    public AsyncRelayCommand SendMessageCommand { get; }
    public RelayCommand SaveAiSettingsCommand { get; }
    public RelayCommand StopAutoExecCommand { get; }
    public RelayCommand SaveServerSettingsCommand { get; }
    public RelayCommand RefreshShellsCommand { get; }
    public AsyncRelayCommand StartNetworkCommand { get; }
    public RelayCommand StopNetworkCommand { get; }
    public RelayCommand RestartTerminalCommand { get; }

    public void Dispose()
    {
        _autoExecCts?.Cancel();
        _autoExecCts?.Dispose();
        OutputStabilizer.Dispose();
        _aiChatService.Dispose();
        TerminalManager.Dispose();
        _relayServer?.Dispose();
        _relayClient?.Dispose();
    }

    private void Initialize()
    {
        _identity = _identityStore.LoadOrCreate();
        DeviceId = _identity.DeviceId;
        RefreshQr();
        RefreshShells();
        UpdateRelayAddressPreview();

        ControlOwner = ControlOwner.Pc;
        IsMobileConnected = false;
        ConnectionStatus = "Local only";
    }

    private void RefreshQr()
    {
        QrPayload = _payloadBuilder.Build(_identity);
        QrImage = _qrCodeService.Generate(QrPayload);
    }

    private void ForceDisconnect()
    {
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

        _serverSettings.IsRelayServer = IsRelayServer;
        _serverSettings.Port = ServerPort;
        _serverSettings.RelayServerAddress = string.IsNullOrWhiteSpace(RelayServerAddress) ? null : RelayServerAddress;
        _serverSettingsStore.Save(_serverSettings);
        UpdateRelayAddressPreview();
    }

    private void RefreshShells()
    {
        var shells = _shellLocator.GetAvailableShells();
        AvailableShells = new ObservableCollection<ShellInfo>(shells);
        if (SelectedShell is null && shells.Count > 0)
            SelectedShell = shells[0];
    }

    // --- Network Start/Stop ---

    private async Task StartNetworkAsync()
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

        SaveServerSettings();

        try
        {
            if (IsRelayServer)
            {
                _relayServer = new RelayServer();
                _relayServer.Log += OnNetworkLog;
                _relayServer.LocalTerminalInputReceived += OnRemoteTerminalInput;
                _relayServer.LocalTerminalResizeReceived += OnRemoteTerminalResize;
                _relayServer.LocalTerminalSnapshotProvider = CaptureCurrentTerminalViewAsync;
                _relayServer.LocalTerminalSizeProvider = GetCurrentTerminalSize;

                await _relayServer.StartAsync(ServerPort);

                // Register local device
                var shellIds = AvailableShells.Select(s => s.Id).ToList();
                _relayServer.RegisterLocalDevice(_identity.DeviceId, _identity.DisplayName, shellIds);

                IsServerRunning = true;
                var reachableUrls = _relayServer.ReachableWebSocketUrls;
                PrimaryRelayAddress = reachableUrls.FirstOrDefault() ??
                                      RelayAddressHelper.GetLocalhostWebSocketUrl(ServerPort);
                RelayReachableAddresses = string.Join(Environment.NewLine, reachableUrls);
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
                    AvailableShells = AvailableShells.Select(s => s.Id).ToList()
                };
                _relayClient.Log += OnNetworkLog;
                _relayClient.ConnectionStateChanged += OnClientConnectionStateChanged;
                _relayClient.TerminalInputReceived += OnRemoteTerminalInput;
                _relayClient.TerminalResizeRequested += OnRemoteTerminalResize;

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
            _relayServer.LocalTerminalSnapshotProvider = null;
            _relayServer.LocalTerminalSizeProvider = null;
            _relayServer.Dispose();
            _relayServer = null;
        }

        if (_relayClient is not null)
        {
            _relayClient.Log -= OnNetworkLog;
            _relayClient.ConnectionStateChanged -= OnClientConnectionStateChanged;
            _relayClient.TerminalInputReceived -= OnRemoteTerminalInput;
            _relayClient.TerminalResizeRequested -= OnRemoteTerminalResize;
            _relayClient.Dispose();
            _relayClient = null;
        }

        IsServerRunning = false;
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

    private void OnRemoteTerminalInput(string sessionId, string data)
    {
        // Write remote input into local terminal
        TerminalManager.WriteInput(data);
    }

    private void OnRemoteTerminalResize(string sessionId, int cols, int rows)
    {
        if (cols <= 0 || rows <= 0)
            return;

        _dispatcher.InvokeAsync(() =>
        {
            ApplyTerminalSize(cols, rows, startIfNeeded: true);
            SessionStatus = $"Running ({cols}x{rows})";
        });
    }

    private (int Cols, int Rows) GetCurrentTerminalSize()
    {
        if (_terminalCols > 0 && _terminalRows > 0)
            return (_terminalCols, _terminalRows);

        return (120, 30);
    }

    private async Task<string> CaptureCurrentTerminalViewAsync()
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

        var virtualScreenSnapshot = VirtualScreen.GetSnapshot();
        if (!string.IsNullOrWhiteSpace(virtualScreenSnapshot))
        {
            var lines = virtualScreenSnapshot.Split('\n');
            return BuildPlainTextBootstrapSequence(
                lines,
                isAlternateBuffer: false,
                cursorX: 0,
                cursorY: Math.Max(0, lines.Length - 1));
        }

        return TerminalOutputBuffer.GetRecentRaw();
    }

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

    // --- Terminal Output Forwarding ---

    /// <summary>
    /// Called by MainWindow when terminal produces output. Handles buffer, stabilizer, and network forwarding.
    /// </summary>
    public void HandleTerminalOutput(string text)
    {
        TerminalOutputBuffer.Append(text);
        VirtualScreen.Write(text);
        OutputStabilizer.NotifyOutputReceived();

        // Forward to network if active
        ForwardTerminalOutputToNetwork(text);

        // Notify UI
        TerminalOutputForwarded?.Invoke(text);
    }

    private void ForwardTerminalOutputToNetwork(string text)
    {
        if (_relayServer is not null && _relayServer.IsRunning)
        {
            _ = _relayServer.BroadcastLocalTerminalOutputAsync(_identity.DeviceId, LocalSessionId, text);
        }
        else if (_relayClient is not null && _relayClient.IsConnected)
        {
            _ = _relayClient.SendTerminalOutputAsync(_identity.DeviceId, LocalSessionId, text);
        }
    }

    // --- Terminal Start / Restart ---

    /// <summary>
    /// Start the terminal using the selected shell. Called from MainWindow after WebView2 reports size.
    /// </summary>
    public void StartTerminal(int cols, int rows)
    {
        if (TerminalManager.IsRunning) return;

        _terminalCols = cols;
        _terminalRows = rows;
        VirtualScreen.Resize(cols, rows);

        var shell = SelectedShell ?? _shellLocator.GetDefaultShell();
        var session = new ConPtySession();
        TerminalManager.OutputReceived += OnTerminalManagerOutput;
        TerminalManager.Start(session, shell, cols, rows);
    }

    public void ApplyTerminalSize(int cols, int rows, bool startIfNeeded)
    {
        if (cols <= 0 || rows <= 0)
            return;

        _terminalCols = cols;
        _terminalRows = rows;

        if (!TerminalManager.IsRunning)
        {
            if (!startIfNeeded)
                return;

            StartTerminal(cols, rows);
            return;
        }

        TerminalManager.Resize(cols, rows);
        VirtualScreen.Resize(cols, rows);
    }

    private void RequestTerminalRestart()
    {
        if (_terminalCols <= 0 || _terminalRows <= 0) return;

        // Dispose old terminal
        TerminalManager.OutputReceived -= OnTerminalManagerOutput;
        TerminalManager.Dispose();

        // Create new manager and start
        TerminalManager = new TerminalSessionManager();

        var shell = SelectedShell ?? _shellLocator.GetDefaultShell();
        var session = new ConPtySession();
        TerminalManager.OutputReceived += OnTerminalManagerOutput;
        TerminalManager.Start(session, shell, _terminalCols, _terminalRows);

        // Update the command delegate
        ExecuteTerminalCommand = cmd => TerminalManager.WriteInput(cmd);

        SessionStatus = $"Running ({shell.DisplayName})";

        // Notify MainWindow to reset WebView2 terminal
        OnPropertyChanged(nameof(TerminalManager));
        TerminalRestarted?.Invoke();
    }

    private void OnTerminalManagerOutput(string text)
    {
        HandleTerminalOutput(text);
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

