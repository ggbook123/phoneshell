using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using PhoneShell.Core.Terminals;
using PhoneShell.Core.Protocol;
using PhoneShell.ViewModels;

namespace PhoneShell;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _webViewReady;
    private bool _forceWelcomeVisible;
    private bool _isSidebarCollapsed;
    private GridLength _sidebarExpandedWidth = new(320);
    private const double SidebarCollapsedWidth = 8;
    private const double SidebarExpandedMinWidth = 260;
    private const double SidebarExpandedMaxWidth = 440;
    private const string CompactModeIcon = "\U0001F4F1";
    private const string ExpandModeIcon = "\U0001F4BB";

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        _viewModel.TerminalOutputForwarded += OnTerminalOutputForwarded;
        _viewModel.TerminalBufferReplaceRequested += OnTerminalBufferReplaceRequested;
        _viewModel.ActiveTabChanged += OnActiveTabChanged;
        _viewModel.TerminalViewportLockRequested += OnTerminalViewportLockRequested;
        _viewModel.TerminalViewportAutoFitRequested += OnTerminalViewportAutoFitRequested;
        _viewModel.ChatMessages.CollectionChanged += ChatMessages_CollectionChanged;
        _viewModel.Tabs.CollectionChanged += Tabs_CollectionChanged;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await TerminalWebView.EnsureCoreWebView2Async();
        TerminalWebView.DefaultBackgroundColor =
            System.Drawing.Color.FromArgb(255, 0x0A, 0x0E, 0x14);
        TerminalWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        var assetsPath = Path.Combine(AppContext.BaseDirectory, "Assets");
        TerminalWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "app.local", assetsPath, CoreWebView2HostResourceAccessKind.Allow);
        TerminalWebView.CoreWebView2.Navigate("https://app.local/terminal.html");
        _webViewReady = true;
        _viewModel.SnapshotService.SetScriptExecutor(
            script => TerminalWebView.CoreWebView2.ExecuteScriptAsync(script));
        _viewModel.SessionStatus = "Ready";

        ApiKeyBox.Password = _viewModel.AiApiKey;

        UpdatePanelVisibility();
        LoadLanguagePreference();
    }

    // --- Custom Title Bar ---

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
        }
        else
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void InfoButton_Click(object sender, RoutedEventArgs e)
    {
        if (InfoOverlay is not null)
        {
            InfoOverlay.Visibility = Visibility.Visible;
        }
    }

    private void InfoCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (InfoOverlay is not null)
        {
            InfoOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void InfoOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (InfoOverlay is not null)
        {
            InfoOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void InfoDialog_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            MaximizeBtn.Content = "\u25A1"; // restore icon
        }
        else
        {
            WindowState = WindowState.Maximized;
            MaximizeBtn.Content = "\u29C9"; // overlapping squares
        }
    }

    // --- WebView2 ---

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var json = e.TryGetWebMessageAsString();
        if (json is null) return;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();

        switch (type)
        {
            case "input":
                var data = root.GetProperty("data").GetString();
                if (data is not null && _viewModel.ActiveTab is not null)
                    _viewModel.WriteTabInput(_viewModel.ActiveTab, data);
                break;
            case "resize":
                var cols = root.GetProperty("cols").GetInt32();
                var rows = root.GetProperty("rows").GetInt32();
                if (cols > 0 && rows > 0)
                {
                    _viewModel.HandleLocalViewportResize(cols, rows);

                    // If active tab exists but terminal not started yet, start it (local tabs only)
                    if (_viewModel.ActiveTab is not null && !_viewModel.ActiveTab.IsRemote &&
                        !_viewModel.ActiveTab.SessionManager.IsRunning)
                    {
                        _viewModel.StartActiveTabTerminal(cols, rows);
                    }
                }
                break;
        }
    }

    private void OnTerminalViewportLockRequested(int cols, int rows)
    {
        if (!_webViewReady) return;

        Dispatcher.InvokeAsync(() =>
        {
            UpdateCompactModeButton();

            TerminalWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.setTerminalGeometry && window.setTerminalGeometry({cols}, {rows})");
        });
    }

    private void OnTerminalViewportAutoFitRequested()
    {
        if (!_webViewReady) return;

        Dispatcher.InvokeAsync(() =>
        {
            UpdateCompactModeButton();

            TerminalWebView.CoreWebView2.ExecuteScriptAsync(
                "window.fitTerminalToContainer && window.fitTerminalToContainer()");
        });
    }

    private void OnTerminalOutputForwarded(string text)
    {
        if (!_webViewReady) return;
        Dispatcher.InvokeAsync(() =>
        {
            var escaped = JsonSerializer.Serialize(text);
            TerminalWebView.CoreWebView2.ExecuteScriptAsync($"window.writeTerminal({escaped})");
        });
    }

    private void OnTerminalBufferReplaceRequested(string text)
    {
        if (!_webViewReady) return;
        Dispatcher.InvokeAsync(() =>
        {
            var escaped = JsonSerializer.Serialize(text);
            TerminalWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.replaceTerminalBuffer && window.replaceTerminalBuffer({escaped})");
        });
    }

    private void OnActiveTabChanged(TerminalTab? tab)
    {
        Dispatcher.InvokeAsync(() =>
        {
            UpdatePanelVisibility();

            if (tab is null)
            {
                return;
            }

            // Update compact mode button icon
            UpdateCompactModeButton();

            // Reset xterm.js and replay the new tab's buffered output
            if (_webViewReady)
            {
                TerminalWebView.CoreWebView2.ExecuteScriptAsync(
                    "window.resetTerminal && window.resetTerminal()");

                var replay = _viewModel.GetTabReplayData(tab);
                if (!string.IsNullOrEmpty(replay))
                {
                    var escapedHistory = JsonSerializer.Serialize(replay);
                    TerminalWebView.CoreWebView2.ExecuteScriptAsync(
                        $"window.replaceTerminalBuffer && window.replaceTerminalBuffer({escapedHistory})");
                }
                else
                {
                    // Replay buffered output from VirtualScreen snapshot
                    var snapshot = tab.VirtualScreen.GetSnapshot();
                    if (!string.IsNullOrWhiteSpace(snapshot))
                    {
                        // Build a bootstrap sequence from the virtual screen
                        var lines = snapshot.Split('\n');
                        var sb = new System.Text.StringBuilder();
                        sb.Append("\x1b[0m\x1b[H\x1b[2J");
                        for (var row = 0; row < lines.Length; row++)
                        {
                            if (lines[row].Length == 0) continue;
                            sb.Append($"\x1b[{row + 1};1H");
                            sb.Append(lines[row]);
                        }
                        sb.Append($"\x1b[{Math.Max(1, lines.Length)};1H");

                        var escaped = JsonSerializer.Serialize(sb.ToString());
                        TerminalWebView.CoreWebView2.ExecuteScriptAsync(
                            $"window.writeTerminal({escaped})");
                    }
                }

                // Apply correct viewport mode
                if (tab.IsCompactMode)
                {
                    var cols = tab.MobileCols > 0 ? tab.MobileCols : 80;
                    var rows = tab.MobileRows > 0 ? tab.MobileRows : 24;
                    TerminalWebView.CoreWebView2.ExecuteScriptAsync(
                        $"window.setTerminalGeometry && window.setTerminalGeometry({cols}, {rows})");
                }
                else
                {
                    TerminalWebView.CoreWebView2.ExecuteScriptAsync(
                        "window.fitTerminalToContainer && window.fitTerminalToContainer()");
                }
            }

            // Re-set the snapshot executor
            _viewModel.SnapshotService.SetScriptExecutor(
                script => TerminalWebView.CoreWebView2.ExecuteScriptAsync(script));

            // Update tab highlighting
            UpdateTabHighlighting();
        });
    }

    private void UpdatePanelVisibility()
    {
        var hasVisibleTabs = _viewModel.HasVisibleTabs;
        var requiresCrossDeviceAuth = _viewModel.IsCrossDeviceAuthRequired;
        var showWelcome = !hasVisibleTabs || _forceWelcomeVisible;
        if (requiresCrossDeviceAuth)
            showWelcome = false;

        if (CrossDeviceGatePanel is not null)
            CrossDeviceGatePanel.Visibility = requiresCrossDeviceAuth ? Visibility.Visible : Visibility.Collapsed;
        if (WelcomePanel is not null)
            WelcomePanel.Visibility = (!requiresCrossDeviceAuth && showWelcome) ? Visibility.Visible : Visibility.Collapsed;
        if (TerminalWebView is not null)
            TerminalWebView.Visibility = (!requiresCrossDeviceAuth && !showWelcome) ? Visibility.Visible : Visibility.Collapsed;
        if (TabBarHost is not null)
            TabBarHost.Visibility = (!requiresCrossDeviceAuth && hasVisibleTabs) ? Visibility.Visible : Visibility.Collapsed;
        if (TabContainer is not null)
            TabContainer.Visibility = Visibility.Visible;
    }


    private void UpdateTabHighlighting()
    {
        UpdateCompactModeButton();
    }

    private void Tabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            _forceWelcomeVisible = false;
        }
        Dispatcher.InvokeAsync(UpdatePanelVisibility);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsCrossDeviceAuthRequired))
        {
            Dispatcher.InvokeAsync(UpdatePanelVisibility);
        }
    }

    // --- Tab UI Event Handlers ---

    private async void ShellCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ShellInfo shell)
        {
            _forceWelcomeVisible = false;
            await _viewModel.OpenSessionOnCurrentTargetAsync(shell.Id);
        }
    }

    private async void GroupDeviceCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is GroupDeviceItem device)
        {
            _forceWelcomeVisible = false;
            await _viewModel.SelectGroupDeviceAsync(device.DeviceId);
            UpdatePanelVisibility();
        }
    }

    private void TabItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TerminalTab tab)
        {
            _viewModel.SwitchTabCommand.Execute(tab.TabId);
        }
    }

    private void TabCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tabId)
        {
            _viewModel.CloseTabCommand.Execute(tabId);
        }
    }

    private void RenameTabMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.DataContext is not TerminalTab tab)
            return;

        var currentTitle = _viewModel.GetEditableSessionTitle(tab);
        var dialog = new RenameSessionDialog(currentTitle)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        if (dialog.ShowDialog() == true)
        {
            _viewModel.RenameTab(tab, dialog.SessionTitle);
        }
    }

    private void NewTabButton_Click(object sender, RoutedEventArgs e)
    {
        _forceWelcomeVisible = true;
        UpdatePanelVisibility();
    }

    private void CompactModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ActiveTab is not null)
        {
            _viewModel.ToggleCompactModeCommand.Execute(_viewModel.ActiveTab.TabId);
            UpdateCompactModeButton();
        }
    }

    private void TitleBarCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ActiveTab is not null)
        {
            _viewModel.CloseTabCommand.Execute(_viewModel.ActiveTab.TabId);
        }
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _viewModel.AiApiKey = ApiKeyBox.Password;
    }

    private async void RemoteShellButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            var shellId = btn.Content as string ?? "";
            var parent = btn.Tag as GroupDeviceItem;
            if (parent is not null)
            {
                _forceWelcomeVisible = false;
                await _viewModel.SelectGroupDeviceAsync(parent.DeviceId);
                await _viewModel.OpenSessionOnCurrentTargetAsync(shellId);
                UpdatePanelVisibility();
            }
        }
    }

    private void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.SendMessageCommand.CanExecute(null))
        {
            _viewModel.SendMessageCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ChatMessages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && ChatListBox.Items.Count > 0)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (ChatListBox.Items.Count > 0)
                    ChatListBox.ScrollIntoView(ChatListBox.Items[^1]);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.TerminalOutputForwarded -= OnTerminalOutputForwarded;
        _viewModel.TerminalBufferReplaceRequested -= OnTerminalBufferReplaceRequested;
        _viewModel.ActiveTabChanged -= OnActiveTabChanged;
        _viewModel.TerminalViewportLockRequested -= OnTerminalViewportLockRequested;
        _viewModel.TerminalViewportAutoFitRequested -= OnTerminalViewportAutoFitRequested;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    // --- Sidebar Accordion ---

    private Expander[]? _sidebarExpanders;

    private Expander[] GetSidebarExpanders()
    {
        if (_sidebarExpanders is not null)
            return _sidebarExpanders;

        Expander?[] expanders =
        {
            LanguageExpander, ServerSettingsExpander, ShellExpander,
            DeviceInfoExpander, GroupDevicesExpander, QrCodeExpander,
            AiSettingsExpander, DebugLogExpander
        };

        if (expanders.Any(expander => expander is null))
        {
            return expanders.Where(expander => expander is not null)
                .Select(expander => expander!)
                .ToArray();
        }

        _sidebarExpanders = expanders.Select(expander => expander!).ToArray();
        return _sidebarExpanders;
    }

    private void SidebarExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander expandedExpander) return;

        var expanders = GetSidebarExpanders();
        if (expanders.Length == 0) return;

        foreach (var expander in expanders)
        {
            if (expander != expandedExpander && expander.IsExpanded)
            {
                expander.IsExpanded = false;
            }
        }
    }

    private void SidebarToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (SidebarColumn is null) return;

        if (!_isSidebarCollapsed)
        {
            _sidebarExpandedWidth = SidebarColumn.Width;
            SidebarColumn.Width = new GridLength(SidebarCollapsedWidth);
            SidebarColumn.MinWidth = SidebarCollapsedWidth;
            SidebarColumn.MaxWidth = SidebarCollapsedWidth;

            if (SidebarContent is not null) SidebarContent.Visibility = Visibility.Collapsed;
            if (SidebarSplitter is not null) SidebarSplitter.Visibility = Visibility.Collapsed;
            if (SidebarToggleButton is not null)
            {
                SidebarToggleButton.Content = "\u276F";
                SidebarToggleButton.ToolTip = "Expand sidebar";
            }

            _isSidebarCollapsed = true;
        }
        else
        {
            SidebarColumn.Width = _sidebarExpandedWidth;
            SidebarColumn.MinWidth = SidebarExpandedMinWidth;
            SidebarColumn.MaxWidth = SidebarExpandedMaxWidth;

            if (SidebarContent is not null) SidebarContent.Visibility = Visibility.Visible;
            if (SidebarSplitter is not null) SidebarSplitter.Visibility = Visibility.Visible;
            if (SidebarToggleButton is not null)
            {
                SidebarToggleButton.Content = "\u276E";
                SidebarToggleButton.ToolTip = "Collapse sidebar";
            }

            _isSidebarCollapsed = false;
        }
    }

    // --- Language Switching ---

    private void LanguageRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (LangEnRadio is null || LangZhRadio is null) return;

        var isEnglish = LangEnRadio.IsChecked == true;
        ApplyLanguage(isEnglish);
        _viewModel?.SaveLanguagePreference(isEnglish ? "en" : "zh");
    }

    private void ApplyLanguage(bool isEnglish)
    {
        if (isEnglish)
        {
            if (LanguageExpander is not null) LanguageExpander.Header = "Language / 语言";
            if (ServerSettingsExpander is not null) ServerSettingsExpander.Header = "Server Settings";
            if (AutoModeCheckBox is not null) AutoModeCheckBox.Content = "Auto Mode (choose server/client)";
            if (EnableRelayServerCheckBox is not null) EnableRelayServerCheckBox.Content = "Enable Relay Server (this PC as hub)";
            if (ServerPortLabel is not null) ServerPortLabel.Text = "PORT";
            if (RelayServerAddressLabel is not null) RelayServerAddressLabel.Text = "RELAY SERVER ADDRESS";
            if (RelayReachableLabel is not null) RelayReachableLabel.Text = "LAN LISTEN ADDRESSES";
            if (RelayReachableHintText is not null) RelayReachableHintText.Text = "Use these addresses for phones on the same LAN";
            if (GroupSecretLabel is not null) GroupSecretLabel.Text = "GROUP SECRET";
            if (GroupSecretCopyButton is not null) GroupSecretCopyButton.Content = "Copy";
            if (GroupSecretHintText is not null) GroupSecretHintText.Text = "Share this key with other PCs to join the group";
            if (GroupIdLabel is not null) GroupIdLabel.Text = "GROUP ID";
            if (GroupIdCopyButton is not null) GroupIdCopyButton.Content = "Copy";
            if (ServerSettingsSaveButton is not null) ServerSettingsSaveButton.Content = "Save";
            if (ServerSettingsStartButton is not null) ServerSettingsStartButton.Content = "Start";
            if (ServerSettingsStopButton is not null) ServerSettingsStopButton.Content = "Stop";
            if (ServerSettingsInitButton is not null) ServerSettingsInitButton.Content = "Initialize";
            if (ShellExpander is not null) ShellExpander.Header = "Shell";
            if (DeviceInfoExpander is not null) DeviceInfoExpander.Header = "Device Info";
            if (GroupDevicesExpander is not null) GroupDevicesExpander.Header = "Group Devices";
            if (QrCodeExpander is not null) QrCodeExpander.Header = "QR Code";
            if (AiSettingsExpander is not null) AiSettingsExpander.Header = "AI Settings";
            if (DebugLogExpander is not null) DebugLogExpander.Header = "Debug Log";
            if (NewTabButtonInline is not null) NewTabButtonInline.ToolTip = "New Session";
            if (CrossDevicePromptText is not null) CrossDevicePromptText.Text = "Scan with your phone to continue cross-device access.";
        }
        else
        {
            if (LanguageExpander is not null) LanguageExpander.Header = "语言 / Language";
            if (ServerSettingsExpander is not null) ServerSettingsExpander.Header = "连接设置";
            if (AutoModeCheckBox is not null) AutoModeCheckBox.Content = "自动模式（自动选择服务端/客户端）";
            if (EnableRelayServerCheckBox is not null) EnableRelayServerCheckBox.Content = "本机设为服务器（局域网模式）";
            if (ServerPortLabel is not null) ServerPortLabel.Text = "端口";
            if (RelayServerAddressLabel is not null) RelayServerAddressLabel.Text = "中转服务器地址";
            if (RelayReachableLabel is not null) RelayReachableLabel.Text = "局域网监听地址";
            if (RelayReachableHintText is not null) RelayReachableHintText.Text = "同一局域网内手机可使用这些地址连接";
            if (GroupSecretLabel is not null) GroupSecretLabel.Text = "群组密钥";
            if (GroupSecretCopyButton is not null) GroupSecretCopyButton.Content = "复制";
            if (GroupSecretHintText is not null) GroupSecretHintText.Text = "把此密钥分享给其他 PC 以加入群组";
            if (GroupIdLabel is not null) GroupIdLabel.Text = "群组 ID";
            if (GroupIdCopyButton is not null) GroupIdCopyButton.Content = "复制";
            if (ServerSettingsSaveButton is not null) ServerSettingsSaveButton.Content = "保存";
            if (ServerSettingsStartButton is not null) ServerSettingsStartButton.Content = "启动";
            if (ServerSettingsStopButton is not null) ServerSettingsStopButton.Content = "停止";
            if (ServerSettingsInitButton is not null) ServerSettingsInitButton.Content = "初始化";
            if (ShellExpander is not null) ShellExpander.Header = "终端选择";
            if (DeviceInfoExpander is not null) DeviceInfoExpander.Header = "设备信息";
            if (GroupDevicesExpander is not null) GroupDevicesExpander.Header = "群组设备";
            if (QrCodeExpander is not null) QrCodeExpander.Header = "二维码";
            if (AiSettingsExpander is not null) AiSettingsExpander.Header = "AI 设置";
            if (DebugLogExpander is not null) DebugLogExpander.Header = "调试日志";
            if (NewTabButtonInline is not null) NewTabButtonInline.ToolTip = "新会话";
            if (CrossDevicePromptText is not null) CrossDevicePromptText.Text = "跨设备连接请先用手机扫码。";
        }
    }

    private void LoadLanguagePreference()
    {
        var lang = _viewModel.LoadLanguagePreference();
        var isEnglish = lang == "en";
        LangEnRadio.IsChecked = isEnglish;
        LangZhRadio.IsChecked = !isEnglish;
        ApplyLanguage(isEnglish);
    }

    private void UpdateCompactModeButton()
    {
        if (CompactModeButton is null || _viewModel.ActiveTab is null)
        {
            return;
        }

        CompactModeButton.Content = _viewModel.ActiveTab.IsCompactMode
            ? ExpandModeIcon
            : CompactModeIcon;
    }
}
