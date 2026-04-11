using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using PhoneShell.Core.Terminals;
using PhoneShell.Core.Protocol;
using PhoneShell.ViewModels;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

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
    private bool _isToolsPanelVisible;
    private const string CompactModeIcon = "\U0001F4F1";
    private const string ExpandModeIcon = "\U0001F4BB";
    private const string ExplorerVirtualRootEn = "This PC";
    private const string ExplorerVirtualRootZh = "此电脑";
    private Point _explorerDragStartPoint;
    private WebViewDropTarget? _webViewDropTarget;
    private readonly List<IntPtr> _webViewDropTargetHandles = new();
    private string? _explorerForwardPath;
    private bool _isExplorerNavigating;
    private bool _isFolderScrollDragging;
    private bool _isFolderScrollDragArmed;
    private Point _folderScrollDragStart;
    private double _folderScrollStartOffset;
    private QuickCommandFolder? _folderSelectedOnMouseDown;
    private readonly ContextMenu _quickCommandFolderContextMenu = new();
    private readonly ContextMenu _quickCommandContextMenu = new();
    private readonly MenuItem _folderContextEditMenuItem = new();
    private readonly MenuItem _folderContextDeleteMenuItem = new();
    private readonly MenuItem _commandContextEditMenuItem = new();
    private readonly MenuItem _commandContextDeleteMenuItem = new();

    private enum RightSidebarSection
    {
        Explorer,
        QuickCommands,
        RecentInputs
    }

    public MainWindow()
    {
        _viewModel = new MainViewModel();
        InitializeComponent();
        DataContext = _viewModel;

        InitializeQuickCommandContextMenus();

        _viewModel.TerminalOutputForwarded += OnTerminalOutputForwarded;
        _viewModel.TerminalBufferReplaceRequested += OnTerminalBufferReplaceRequested;
        _viewModel.ActiveTabChanged += OnActiveTabChanged;
        _viewModel.TerminalViewportLockRequested += OnTerminalViewportLockRequested;
        _viewModel.TerminalViewportAutoFitRequested += OnTerminalViewportAutoFitRequested;
        _viewModel.ChatMessages.CollectionChanged += ChatMessages_CollectionChanged;
        _viewModel.Tabs.CollectionChanged += Tabs_CollectionChanged;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;

        TerminalWebView.Loaded += TerminalWebView_Loaded;
        TerminalWebView.CoreWebView2InitializationCompleted += TerminalWebView_CoreWebView2InitializationCompleted;
    }

    private void InitializeQuickCommandContextMenus()
    {
        _folderContextEditMenuItem.Header = "编辑";
        _folderContextEditMenuItem.Click += QuickCommandFolderContextEdit_Click;
        _folderContextDeleteMenuItem.Header = "删除";
        _folderContextDeleteMenuItem.Click += QuickCommandFolderContextDelete_Click;
        _quickCommandFolderContextMenu.Items.Add(_folderContextEditMenuItem);
        _quickCommandFolderContextMenu.Items.Add(_folderContextDeleteMenuItem);

        _commandContextEditMenuItem.Header = "编辑";
        _commandContextEditMenuItem.Click += QuickCommandContextEdit_Click;
        _commandContextDeleteMenuItem.Header = "删除";
        _commandContextDeleteMenuItem.Click += QuickCommandContextDelete_Click;
        _quickCommandContextMenu.Items.Add(_commandContextEditMenuItem);
        _quickCommandContextMenu.Items.Add(_commandContextDeleteMenuItem);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (_viewModel.QuickCommandFolders.Any(folder => folder.IsEditing) &&
            !IsDescendantOfFolderEditBox(e.OriginalSource as DependencyObject))
        {
            _viewModel.CommitInlineQuickCommandFolderEdits();
        }

        base.OnPreviewMouseDown(e);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await TerminalWebView.EnsureCoreWebView2Async();
        TerminalWebView.DefaultBackgroundColor =
            System.Drawing.Color.FromArgb(255, 0x0A, 0x0E, 0x14);
        ConfigureWebViewDropBehavior();
        // Let terminal.html handle copy/paste/select shortcuts instead of WebView2 browser accelerators.
        TerminalWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
        TerminalWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        TerminalWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        TerminalWebView.CoreWebView2.NavigationStarting += TerminalWebView_NavigationStarting;

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
        SetRightSidebarSection(RightSidebarSection.Explorer);
        LoadLanguagePreference();
        UpdateSideRailButtonStates();
        UpdateExplorerNavButtons();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        UnregisterWebViewDropTarget();
    }

    private void TerminalWebView_Loaded(object sender, RoutedEventArgs e)
    {
        RegisterWebViewDropTarget();
    }

    private void TerminalWebView_CoreWebView2InitializationCompleted(
        object? sender,
        CoreWebView2InitializationCompletedEventArgs e)
    {
        RegisterWebViewDropTarget();
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
        var dialog = new InfoGuideDialog
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.ShowDialog();
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
                {
                    _viewModel.WriteTabInput(_viewModel.ActiveTab, data);
                    _viewModel.TrackTerminalUserInput(data);
                }
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
            case "clipboard.write":
                if (root.TryGetProperty("text", out var clipboardWriteElement))
                {
                    var clipboardWriteText = clipboardWriteElement.GetString();
                    if (!string.IsNullOrEmpty(clipboardWriteText))
                    {
                        try
                        {
                            Clipboard.SetText(clipboardWriteText);
                        }
                        catch
                        {
                        }
                    }
                }
                break;
            case "clipboard.read":
                var requestId = root.TryGetProperty("requestId", out var requestIdElement)
                    ? requestIdElement.GetString()
                    : null;
                if (!string.IsNullOrEmpty(requestId))
                {
                    var clipboardReadText = string.Empty;
                    try
                    {
                        if (Clipboard.ContainsText())
                        {
                            clipboardReadText = Clipboard.GetText();
                        }
                    }
                    catch
                    {
                    }

                    var requestIdJson = JsonSerializer.Serialize(requestId);
                    var clipboardTextJson = JsonSerializer.Serialize(clipboardReadText);
                    TerminalWebView.CoreWebView2.ExecuteScriptAsync(
                        $"window.handleHostClipboardReadResult && window.handleHostClipboardReadResult({requestIdJson}, {clipboardTextJson})");
                }
                break;
        }
    }

    private void TerminalWebView_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Uri))
            return;

        if (IsAllowedTerminalUri(e.Uri))
            return;

        // Prevent WebView2 from navigating away (e.g., file drops opening about:blank#blocked).
        e.Cancel = true;
    }

    private static bool IsAllowedTerminalUri(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return false;

        return parsed.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
               parsed.Host.Equals("app.local", StringComparison.OrdinalIgnoreCase);
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
        var showWelcome = !hasVisibleTabs || _forceWelcomeVisible;
        if (WelcomePanel is not null)
            WelcomePanel.Visibility = showWelcome ? Visibility.Visible : Visibility.Collapsed;
        if (TerminalWebView is not null)
            TerminalWebView.Visibility = !showWelcome ? Visibility.Visible : Visibility.Collapsed;
        if (TabBarHost is not null)
            TabBarHost.Visibility = hasVisibleTabs ? Visibility.Visible : Visibility.Collapsed;
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
        if (e.PropertyName == nameof(MainViewModel.ExplorerRootPath))
        {
            Dispatcher.InvokeAsync(UpdateExplorerNavButtons);
        }
        else if (e.PropertyName == nameof(MainViewModel.ExplorerCurrentPath))
        {
            Dispatcher.InvokeAsync(UpdateExplorerNavButtons);
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
        _viewModel.CloseAllTabs();
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
            if (ToolsSidebarContent is not null) ToolsSidebarContent.Visibility = Visibility.Collapsed;
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

            if (ToolsSidebarContent is not null)
                ToolsSidebarContent.Visibility = _isToolsPanelVisible ? Visibility.Visible : Visibility.Collapsed;
            if (SidebarContent is not null)
                SidebarContent.Visibility = _isToolsPanelVisible ? Visibility.Collapsed : Visibility.Visible;
            if (SidebarSplitter is not null) SidebarSplitter.Visibility = Visibility.Visible;
            if (SidebarToggleButton is not null)
            {
                SidebarToggleButton.Content = "\u276E";
                SidebarToggleButton.ToolTip = "Collapse sidebar";
            }

            _isSidebarCollapsed = false;
        }

        UpdateSideRailButtonStates();
    }

    // --- Right Sidebar ---

    private void RightSidebarToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSidebarCollapsed)
        {
            SidebarToggleButton_Click(sender, e);
        }

        SetToolsPanelVisible(true);
    }

    private void SettingsSidebarToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSidebarCollapsed)
        {
            SidebarToggleButton_Click(sender, e);
        }

        SetToolsPanelVisible(false);
    }

    private void RightExplorerButton_Click(object sender, RoutedEventArgs e)
    {
        SetRightSidebarSection(RightSidebarSection.Explorer);
    }

    private void RightQuickCommandsButton_Click(object sender, RoutedEventArgs e)
    {
        SetRightSidebarSection(RightSidebarSection.QuickCommands);
    }

    private void RightRecentInputsButton_Click(object sender, RoutedEventArgs e)
    {
        SetRightSidebarSection(RightSidebarSection.RecentInputs);
    }

    private void SetRightSidebarSection(RightSidebarSection section)
    {
        if (ExplorerPanel is not null)
            ExplorerPanel.Visibility = section == RightSidebarSection.Explorer
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (QuickCommandsPanel is not null)
            QuickCommandsPanel.Visibility = section == RightSidebarSection.QuickCommands
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (RecentInputsPanel is not null)
            RecentInputsPanel.Visibility = section == RightSidebarSection.RecentInputs
                ? Visibility.Visible
                : Visibility.Collapsed;

        SetRightSidebarSectionButtonStyle(RightExplorerButton, section == RightSidebarSection.Explorer);
        SetRightSidebarSectionButtonStyle(RightQuickCommandsButton, section == RightSidebarSection.QuickCommands);
        SetRightSidebarSectionButtonStyle(RightRecentInputsButton, section == RightSidebarSection.RecentInputs);
    }

    private void SetRightSidebarSectionButtonStyle(Button? button, bool isActive)
    {
        if (button is null) return;

        button.Background = isActive
            ? (Brush)FindResource("AccentSubtleBrush")
            : Brushes.Transparent;
        button.Foreground = isActive
            ? (Brush)FindResource("Text1Brush")
            : (Brush)FindResource("Text2Brush");
        button.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void SetToolsPanelVisible(bool visible)
    {
        _isToolsPanelVisible = visible;

        if (ToolsSidebarContent is not null)
            ToolsSidebarContent.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (SidebarContent is not null)
            SidebarContent.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;

        UpdateToolsToggleToolTip();
        UpdateSideRailButtonStates();
    }

    private void UpdateToolsToggleToolTip()
    {
        var isEnglish = LangEnRadio?.IsChecked == true;
        if (RightSidebarToggleButton is not null)
            RightSidebarToggleButton.ToolTip = isEnglish ? "Open tools" : "打开工具面板";
        if (SettingsSidebarToggleButton is not null)
            SettingsSidebarToggleButton.ToolTip = isEnglish ? "Open settings" : "打开设置面板";
    }

    private void UpdateSideRailButtonStates()
    {
        var activeBrush = TryFindResource("Text1Brush") as Brush ?? Brushes.White;
        var inactiveBrush = TryFindResource("AccentBrush") as Brush ?? Brushes.LimeGreen;

        if (RightSidebarToggleButton is not null)
        {
            RightSidebarToggleButton.Foreground = (!_isSidebarCollapsed && _isToolsPanelVisible)
                ? activeBrush
                : inactiveBrush;
        }

        if (SettingsSidebarToggleButton is not null)
        {
            SettingsSidebarToggleButton.Foreground = (!_isSidebarCollapsed && !_isToolsPanelVisible)
                ? activeBrush
                : inactiveBrush;
        }

        if (SidebarToggleButton is not null)
        {
            SidebarToggleButton.Foreground = _isSidebarCollapsed
                ? activeBrush
                : inactiveBrush;
        }
    }

    private void ConfigureWebViewDropBehavior()
    {
        if (TerminalWebView is null)
            return;

        try
        {
            TerminalWebView.AllowExternalDrop = false;
        }
        catch
        {
            // Best effort; if unavailable, drag/drop may still be handled by WebView2.
        }

        RegisterWebViewDropTarget();
    }

    private void RegisterWebViewDropTarget()
    {
        if (TerminalWebView is null)
            return;

        var handle = TerminalWebView.Handle;
        if (handle == IntPtr.Zero)
        {
            Dispatcher.BeginInvoke(RegisterWebViewDropTarget, System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }

        _webViewDropTarget ??= new WebViewDropTarget(
            files => _viewModel.TryInsertPathsIntoActiveSession(files),
            text => _viewModel.TryInsertTextIntoActiveSession(text));

        var handles = NativeDropTarget.CollectDropTargetHandles(handle);
        if (handles.Count == 0)
            return;

        UnregisterWebViewDropTarget();

        foreach (var hwnd in handles)
        {
            if (NativeDropTarget.TryRegisterDropTarget(hwnd, _webViewDropTarget))
                _webViewDropTargetHandles.Add(hwnd);
        }
    }

    private void UnregisterWebViewDropTarget()
    {
        if (_webViewDropTargetHandles.Count > 0)
        {
            foreach (var hwnd in _webViewDropTargetHandles)
                NativeDropTarget.RevokeDragDrop(hwnd);

            _webViewDropTargetHandles.Clear();
        }
    }

    // --- Drag & Drop to Active Session ---

    private void SessionDropHost_PreviewDragOver(object sender, DragEventArgs e)
    {
        var hasSupportedPayload = e.Data.GetDataPresent(DataFormats.FileDrop) ||
                                  e.Data.GetDataPresent(DataFormats.Text) ||
                                  e.Data.GetDataPresent(DataFormats.UnicodeText);

        e.Effects = hasSupportedPayload && _viewModel.HasActiveSessionInputTarget
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void SessionDropHost_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] fileDrop &&
                    fileDrop.Length > 0)
                {
                    _viewModel.TryInsertPathsIntoActiveSession(fileDrop);
                }
            }
            else if (e.Data.GetDataPresent(DataFormats.Text))
            {
                if (e.Data.GetData(DataFormats.Text) is string text &&
                    !string.IsNullOrWhiteSpace(text))
                {
                    _viewModel.TryInsertTextIntoActiveSession(text);
                }
            }
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                if (e.Data.GetData(DataFormats.UnicodeText) is string text &&
                    !string.IsNullOrWhiteSpace(text))
                {
                    _viewModel.TryInsertTextIntoActiveSession(text);
                }
            }
        }
        finally
        {
            e.Handled = true;
        }
    }

    // --- WebView2 Drop Target (native) ---

    [ComVisible(true)]
    private sealed class WebViewDropTarget : IDropTarget
    {
        private readonly Action<string[]> _onFiles;
        private readonly Action<string> _onText;
        private bool _hasSupportedData;

        public WebViewDropTarget(Action<string[]> onFiles, Action<string> onText)
        {
            _onFiles = onFiles;
            _onText = onText;
        }

        public int DragEnter(ComIDataObject pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect)
        {
            _hasSupportedData = NativeDropTarget.HasFileDrop(pDataObj) || NativeDropTarget.HasText(pDataObj);
            pdwEffect = _hasSupportedData ? NativeDropTarget.DROPEFFECT_COPY : NativeDropTarget.DROPEFFECT_NONE;
            return NativeDropTarget.S_OK;
        }

        public int DragOver(int grfKeyState, POINTL pt, ref int pdwEffect)
        {
            pdwEffect = _hasSupportedData
                ? NativeDropTarget.DROPEFFECT_COPY
                : NativeDropTarget.DROPEFFECT_NONE;
            return NativeDropTarget.S_OK;
        }

        public int DragLeave()
        {
            _hasSupportedData = false;
            return NativeDropTarget.S_OK;
        }

        public int Drop(ComIDataObject pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect)
        {
            pdwEffect = NativeDropTarget.DROPEFFECT_NONE;

            try
            {
                if (pDataObj is not null && NativeDropTarget.TryGetFileList(pDataObj, out var files))
                {
                    _onFiles(files);
                    pdwEffect = NativeDropTarget.DROPEFFECT_COPY;
                    return NativeDropTarget.S_OK;
                }

                if (pDataObj is not null && NativeDropTarget.TryGetText(pDataObj, out var text))
                {
                    var paths = NativeDropTarget.ExtractExistingPaths(text);
                    if (paths.Length > 0)
                        _onFiles(paths);
                    else
                        _onText(text);

                    pdwEffect = NativeDropTarget.DROPEFFECT_COPY;
                }
            }
            catch
            {
                pdwEffect = NativeDropTarget.DROPEFFECT_NONE;
            }

            return NativeDropTarget.S_OK;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTL
    {
        public int X;
        public int Y;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("00000122-0000-0000-C000-000000000046")]
    private interface IDropTarget
    {
        [PreserveSig]
        int DragEnter(ComIDataObject pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect);

        [PreserveSig]
        int DragOver(int grfKeyState, POINTL pt, ref int pdwEffect);

        [PreserveSig]
        int DragLeave();

        [PreserveSig]
        int Drop(ComIDataObject pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect);
    }

    private static class NativeDropTarget
    {
        public const int S_OK = 0;
        public const int DROPEFFECT_NONE = 0;
        public const int DROPEFFECT_COPY = 1;
        private const int DRAGDROP_E_ALREADYREGISTERED = unchecked((int)0x80040101);
        private const short CF_TEXT = 1;
        private const short CF_UNICODETEXT = 13;
        private const short CF_HDROP = 15;

        [DllImport("ole32.dll")]
        public static extern int RegisterDragDrop(IntPtr hwnd, IDropTarget pDropTarget);

        [DllImport("ole32.dll")]
        public static extern int RevokeDragDrop(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        public static List<IntPtr> CollectDropTargetHandles(IntPtr hostHandle)
        {
            var handles = new List<IntPtr> { hostHandle };
            EnumChildWindows(hostHandle, (hwnd, _) =>
            {
                if (hwnd != IntPtr.Zero)
                    handles.Add(hwnd);
                return true;
            }, IntPtr.Zero);

            return handles.Distinct().ToList();
        }

        public static bool TryRegisterDropTarget(IntPtr hwnd, IDropTarget target)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            var hr = RegisterDragDrop(hwnd, target);
            if (hr == DRAGDROP_E_ALREADYREGISTERED)
            {
                RevokeDragDrop(hwnd);
                hr = RegisterDragDrop(hwnd, target);
            }

            return hr == S_OK;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

        [DllImport("ole32.dll")]
        private static extern void ReleaseStgMedium(ref STGMEDIUM pmedium);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr hMem);

        public static bool HasFileDrop(ComIDataObject dataObj)
        {
            var format = CreateFormat(CF_HDROP);
            return dataObj.QueryGetData(ref format) == S_OK;
        }

        public static bool HasText(ComIDataObject dataObj)
        {
            var format = CreateFormat(CF_UNICODETEXT);
            return dataObj.QueryGetData(ref format) == S_OK;
        }

        public static bool TryGetFileList(ComIDataObject dataObj, out string[] files)
        {
            files = Array.Empty<string>();
            var format = CreateFormat(CF_HDROP);
            if (dataObj.QueryGetData(ref format) != S_OK)
                return false;

            dataObj.GetData(ref format, out var medium);
            try
            {
                var hDrop = medium.unionmember;
                var count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                if (count == 0)
                    return false;

                var list = new string[count];
                for (uint i = 0; i < count; i++)
                {
                    var length = DragQueryFile(hDrop, i, null, 0);
                    var sb = new StringBuilder((int)length + 1);
                    DragQueryFile(hDrop, i, sb, (uint)sb.Capacity);
                    list[i] = sb.ToString();
                }

                files = list;
                return true;
            }
            finally
            {
                ReleaseStgMedium(ref medium);
            }
        }

        public static bool TryGetText(ComIDataObject dataObj, out string text)
        {
            if (TryGetString(dataObj, CF_UNICODETEXT, out text))
                return !string.IsNullOrWhiteSpace(text);

            if (TryGetString(dataObj, CF_TEXT, out text))
                return !string.IsNullOrWhiteSpace(text);

            text = string.Empty;
            return false;
        }

        public static string[] ExtractExistingPaths(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<string>();

            var lines = text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();

            if (lines.Length == 0)
                return Array.Empty<string>();

            var existing = lines
                .Where(line => File.Exists(line) || Directory.Exists(line))
                .ToArray();

            return existing;
        }

        private static bool TryGetString(ComIDataObject dataObj, short formatId, out string text)
        {
            text = string.Empty;
            var format = CreateFormat(formatId);
            if (dataObj.QueryGetData(ref format) != S_OK)
                return false;

            dataObj.GetData(ref format, out var medium);
            try
            {
                var dataPtr = GlobalLock(medium.unionmember);
                if (dataPtr == IntPtr.Zero)
                    return false;

                try
                {
                    text = formatId == CF_UNICODETEXT
                        ? Marshal.PtrToStringUni(dataPtr) ?? string.Empty
                        : Marshal.PtrToStringAnsi(dataPtr) ?? string.Empty;
                }
                finally
                {
                    GlobalUnlock(medium.unionmember);
                }

                return true;
            }
            finally
            {
                ReleaseStgMedium(ref medium);
            }
        }

        private static FORMATETC CreateFormat(short format)
        {
            return new FORMATETC
            {
                cfFormat = format,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = TYMED.TYMED_HGLOBAL
            };
        }
    }

    // --- Explorer ---

    private void ExplorerRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        var before = _viewModel.ExplorerRootPath;
        _viewModel.LoadExplorerRoot();
        if (!string.Equals(before, _viewModel.ExplorerRootPath, StringComparison.OrdinalIgnoreCase))
            _explorerForwardPath = null;

        UpdateExplorerNavButtons();
    }

    private void ExplorerUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetExplorerParent(out var parent))
            return;

        _explorerForwardPath = GetExplorerActivePath();

        if (IsExplorerVirtualRootPath(_viewModel.ExplorerRootPath))
        {
            _viewModel.ExplorerCurrentPath = parent;
            UpdateExplorerNavButtons();
            return;
        }

        NavigateExplorerToPath(parent, preserveForward: true);
    }

    private void ExplorerForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_explorerForwardPath))
            return;

        var target = _explorerForwardPath;
        _explorerForwardPath = null;
        if (IsExplorerVirtualRootPath(_viewModel.ExplorerRootPath))
        {
            _viewModel.ExplorerCurrentPath = target;
            UpdateExplorerNavButtons();
            return;
        }

        NavigateExplorerToPath(target, preserveForward: true);
    }

    private void UpdateExplorerNavButtons()
    {
        if (ExplorerForwardButton is not null)
            ExplorerForwardButton.IsEnabled = !string.IsNullOrWhiteSpace(_explorerForwardPath);

        if (ExplorerUpButton is not null)
            ExplorerUpButton.IsEnabled = TryGetExplorerParent(out _);
    }

    private bool TryGetExplorerParent(out string parentPath)
    {
        parentPath = string.Empty;
        var current = GetExplorerActivePath()?.Trim();
        if (string.IsNullOrWhiteSpace(current))
            return false;

        if (IsExplorerVirtualRootPath(current))
            return false;

        try
        {
            var full = Path.GetFullPath(current);
            var parent = Directory.GetParent(full);
            if (parent is null)
            {
                parentPath = GetExplorerVirtualRootDisplay();
                return true;
            }

            if (!Directory.Exists(parent.FullName))
                return false;

            parentPath = parent.FullName;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void NavigateExplorerToPath(string path, bool preserveForward)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!preserveForward)
            _explorerForwardPath = null;

        _viewModel.ExplorerRootPath = path;
        _viewModel.ExplorerCurrentPath = path;
        _viewModel.LoadExplorerRoot();
        UpdateExplorerNavButtons();
    }

    private void ExplorerTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_isExplorerNavigating)
            return;

        if (e.NewValue is not FileExplorerNode node || node.IsPlaceholder || !node.IsDirectory)
            return;

        var target = node.FullPath;
        if (string.IsNullOrWhiteSpace(target))
            return;

        if (string.Equals(_viewModel.ExplorerRootPath, target, StringComparison.OrdinalIgnoreCase))
            return;

        _isExplorerNavigating = true;
        try
        {
            _explorerForwardPath = null;
            if (IsExplorerVirtualRootPath(_viewModel.ExplorerRootPath))
            {
                _viewModel.ExplorerCurrentPath = target;
                UpdateExplorerNavButtons();
            }
            else
            {
                NavigateExplorerToPath(target, preserveForward: true);
            }
        }
        finally
        {
            _isExplorerNavigating = false;
        }
    }

    private static bool IsExplorerVirtualRootPath(string? path)
    {
        return string.Equals(path, ExplorerVirtualRootEn, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(path, ExplorerVirtualRootZh, StringComparison.OrdinalIgnoreCase);
    }

    private string GetExplorerVirtualRootDisplay()
    {
        var isEnglish = LangEnRadio?.IsChecked == true;
        return isEnglish ? ExplorerVirtualRootEn : ExplorerVirtualRootZh;
    }

    private string GetExplorerActivePath()
    {
        if (IsExplorerVirtualRootPath(_viewModel.ExplorerRootPath))
            return _viewModel.ExplorerCurrentPath;

        return _viewModel.ExplorerRootPath;
    }

    private void ExplorerTreeItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is FileExplorerNode node)
        {
            _viewModel.EnsureExplorerNodeChildren(node);
        }
    }

    private void ExplorerTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _explorerDragStartPoint = e.GetPosition(null);
    }

    private void ExplorerTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _explorerDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _explorerDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (!TryGetSelectedExplorerNode(out var node) || node.IsPlaceholder)
            return;

        var data = new DataObject();
        data.SetData(DataFormats.Text, node.FullPath);
        data.SetData(DataFormats.UnicodeText, node.FullPath);

        DragDrop.DoDragDrop(ExplorerTree, data, DragDropEffects.Copy);
    }

    private void ExplorerTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!TryGetSelectedExplorerNode(out var node) || node.IsPlaceholder)
            return;

        if (node.IsDirectory)
        {
            _viewModel.EnsureExplorerNodeChildren(node);
            return;
        }

        OpenFileSessionWindow(node.FullPath);
    }

    private bool TryGetSelectedExplorerNode(out FileExplorerNode node)
    {
        if (ExplorerTree?.SelectedItem is FileExplorerNode selected)
        {
            node = selected;
            return true;
        }

        node = null!;
        return false;
    }

    private void OpenFileSessionWindow(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        var session = new FileSessionWindow(filePath)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        session.InsertPathRequested += path =>
        {
            _viewModel.TryInsertPathsIntoActiveSession(new[] { path });
        };
        session.Show();
    }

    // --- Quick Commands ---

    private void QuickCommandsPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.QuickCommandFolders.Any(folder => folder.IsEditing))
            return;

        if (IsDescendantOfFolderEditBox(e.OriginalSource as DependencyObject))
            return;

        _viewModel.CommitInlineQuickCommandFolderEdits();
    }

    private void QuickCommandFolderListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not QuickCommandFolder folder)
            return;

        listBox.SelectedItem = folder;
        _quickCommandFolderContextMenu.DataContext = folder;
        _quickCommandFolderContextMenu.PlacementTarget = container;
        _quickCommandFolderContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void QuickCommandFolderListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        _folderSelectedOnMouseDown = null;
        _isFolderScrollDragging = false;
        _isFolderScrollDragArmed = false;

        if (IsDescendantOfFolderEditBox(e.OriginalSource as DependencyObject))
            return;

        var clickedContainer = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        var clickedFolder = clickedContainer?.DataContext as QuickCommandFolder;
        if (clickedFolder is not null &&
            ReferenceEquals(listBox.SelectedItem, clickedFolder))
        {
            _folderSelectedOnMouseDown = clickedFolder;
        }

        var scrollViewer = FindDescendantScrollViewer(listBox);
        if (scrollViewer is null)
            return;

        _folderScrollDragStart = e.GetPosition(scrollViewer);
        _folderScrollStartOffset = scrollViewer.HorizontalOffset;
        _isFolderScrollDragArmed = true;
    }

    private void QuickCommandFolderListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ListBox listBox || e.LeftButton != MouseButtonState.Pressed)
            return;

        if (!_isFolderScrollDragArmed)
            return;

        var scrollViewer = FindDescendantScrollViewer(listBox);
        if (scrollViewer is null)
            return;

        var current = e.GetPosition(scrollViewer);
        var delta = current.X - _folderScrollDragStart.X;
        if (!_isFolderScrollDragging)
        {
            if (Math.Abs(delta) < SystemParameters.MinimumHorizontalDragDistance)
                return;

            _isFolderScrollDragging = true;
            _folderSelectedOnMouseDown = null;
            listBox.CaptureMouse();
        }

        scrollViewer.ScrollToHorizontalOffset(_folderScrollStartOffset - delta);
        e.Handled = true;
    }

    private void QuickCommandFolderListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        if (_isFolderScrollDragging)
            e.Handled = true;

        if (listBox.IsMouseCaptured)
            listBox.ReleaseMouseCapture();

        _isFolderScrollDragging = false;
        _isFolderScrollDragArmed = false;
    }

    private void QuickCommandFolderListBox_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        if (listBox.IsMouseCaptured)
            listBox.ReleaseMouseCapture();

        _isFolderScrollDragging = false;
        _isFolderScrollDragArmed = false;
    }

    private void QuickCommandFolderAddButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = _viewModel.BeginInlineQuickCommandFolderCreate();
        FocusQuickCommandFolderEditor(folder);
    }

    private void QuickCommandFolderName_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isFolderScrollDragging)
            return;

        if (sender is not FrameworkElement element || element.DataContext is not QuickCommandFolder folder)
            return;

        if (folder.IsEditing)
            return;

        if (!ReferenceEquals(_folderSelectedOnMouseDown, folder))
            return;

        _folderSelectedOnMouseDown = null;

        if (QuickCommandFolderListBox is not null)
            QuickCommandFolderListBox.SelectedItem = folder;

        _viewModel.BeginInlineQuickCommandFolderEdit(folder);
        FocusQuickCommandFolderEditor(folder);
        e.Handled = true;
    }

    private void QuickCommandFolderContextEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.DataContext is not QuickCommandFolder folder)
            return;

        if (QuickCommandFolderListBox is not null)
            QuickCommandFolderListBox.SelectedItem = folder;

        _viewModel.BeginInlineQuickCommandFolderEdit(folder);
        FocusQuickCommandFolderEditor(folder);
    }

    private void QuickCommandFolderContextDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.DataContext is not QuickCommandFolder folder)
            return;

        _viewModel.DeleteQuickCommandFolder(folder);
    }

    private void QuickCommandFolderEditBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not QuickCommandFolder folder)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (element.IsKeyboardFocusWithin)
                return;

            _viewModel.CommitInlineQuickCommandFolderEdit(folder);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void QuickCommandFolderEditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not QuickCommandFolder folder)
            return;

        if (e.Key == Key.Enter)
        {
            _viewModel.CommitInlineQuickCommandFolderEdit(folder);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.CancelInlineQuickCommandFolderEdit(folder);
            e.Handled = true;
        }
    }

    private void QuickCommandSaveButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SaveQuickCommandDraft();
    }

    private void QuickCommandNewButton_Click(object sender, RoutedEventArgs e)
    {
        var item = _viewModel.BeginInlineQuickCommandCreate();
        FocusQuickCommandEditor(item);
    }

    private void QuickCommandDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.DeleteSelectedQuickCommand();
    }

    private void QuickCommandItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item || item.DataContext is not QuickCommandItem)
            return;

        if (item.DataContext is QuickCommandItem quickCommand && quickCommand.IsEditing)
            return;

        if (e.ClickCount > 1)
            return;

        if (IsDescendantOfButton(e.OriginalSource as DependencyObject))
            return;

        _viewModel.CommitInlineQuickCommandEdits();

        if (QuickCommandsListBox is not null)
            QuickCommandsListBox.SelectedItem = item.DataContext;

        _viewModel.TryInsertSelectedQuickCommand(executeImmediately: false);
        e.Handled = true;
    }

    private void QuickCommandEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is QuickCommandItem item)
        {
            if (QuickCommandsListBox is not null)
                QuickCommandsListBox.SelectedItem = item;
            _viewModel.BeginInlineQuickCommandEdit(item);
            FocusQuickCommandEditor(item);
        }
    }

    private void QuickCommandsListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not QuickCommandItem item)
            return;

        listBox.SelectedItem = item;
        _quickCommandContextMenu.DataContext = item;
        _quickCommandContextMenu.PlacementTarget = container;
        _quickCommandContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void QuickCommandContextEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.DataContext is not QuickCommandItem item)
            return;

        if (QuickCommandsListBox is not null)
            QuickCommandsListBox.SelectedItem = item;

        _viewModel.BeginInlineQuickCommandEdit(item);
        FocusQuickCommandEditor(item);
    }

    private void QuickCommandContextDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.DataContext is not QuickCommandItem item)
            return;

        _viewModel.DeleteQuickCommand(item);
    }

    private void QuickCommandEditPanel_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not FrameworkElement panel || panel.DataContext is not QuickCommandItem item)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (panel.IsKeyboardFocusWithin)
                return;

            _viewModel.CommitInlineQuickCommandEdit(item);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void FocusQuickCommandFolderEditor(QuickCommandFolder folder)
    {
        if (QuickCommandFolderListBox is null)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            QuickCommandFolderListBox.ScrollIntoView(folder);
            QuickCommandFolderListBox.UpdateLayout();

            if (QuickCommandFolderListBox.ItemContainerGenerator.ContainerFromItem(folder) is not ListBoxItem container)
                return;

            EnsureFolderChipFullyVisible(container);
            QuickCommandFolderListBox.UpdateLayout();

            var textBox = FindVisualChild<TextBox>(container);
            if (textBox is null)
                return;

            textBox.Focus();
            textBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void EnsureFolderChipFullyVisible(ListBoxItem container)
    {
        if (QuickCommandFolderListBox is null)
            return;

        var scrollViewer = FindDescendantScrollViewer(QuickCommandFolderListBox);
        if (scrollViewer is null || scrollViewer.ViewportWidth <= 0)
            return;

        Rect bounds;
        try
        {
            bounds = container.TransformToAncestor(scrollViewer)
                .TransformBounds(new Rect(new Point(0, 0), container.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return;
        }

        const double edgePadding = 8;
        if (bounds.Right > scrollViewer.ViewportWidth)
        {
            var shiftRight = bounds.Right - scrollViewer.ViewportWidth + edgePadding;
            scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset + shiftRight);
        }
        else if (bounds.Left < 0)
        {
            var target = Math.Max(0, scrollViewer.HorizontalOffset + bounds.Left - edgePadding);
            scrollViewer.ScrollToHorizontalOffset(target);
        }
    }

    private void FocusQuickCommandEditor(QuickCommandItem item)
    {
        if (QuickCommandsListBox is null)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            QuickCommandsListBox.ScrollIntoView(item);
            QuickCommandsListBox.UpdateLayout();

            if (QuickCommandsListBox.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container)
                return;

            var textBox = FindVisualChild<TextBox>(container);
            if (textBox is null)
                return;

            textBox.Focus();
            textBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
                return typed;

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
                return descendant;
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T typed)
                return typed;
            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject parent)
    {
        return FindVisualChild<ScrollViewer>(parent);
    }

    private static bool IsDescendantOfFolderEditBox(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TextBox textBox && textBox.DataContext is QuickCommandFolder)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static bool IsDescendantOfButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    // --- Recent Inputs ---

    private void RecentInputItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item || item.DataContext is not string recentInput)
            return;

        if (e.ClickCount > 1)
            return;

        if (IsDescendantOfButton(e.OriginalSource as DependencyObject))
            return;

        if (RecentInputsListBox is not null)
            RecentInputsListBox.SelectedItem = recentInput;

        _viewModel.TryInsertRecentInput(recentInput, executeImmediately: false);
        e.Handled = true;
    }

    private void RecentInputClearButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearRecentInputs();
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
            if (RightExplorerButton is not null) RightExplorerButton.Content = "Explorer";
            if (RightQuickCommandsButton is not null) RightQuickCommandsButton.Content = "Quick Commands";
            if (RightRecentInputsButton is not null) RightRecentInputsButton.Content = "Recent Commands";
            if (ExplorerRefreshButton is not null) ExplorerRefreshButton.ToolTip = "Refresh";
            if (ExplorerUpButton is not null) ExplorerUpButton.ToolTip = "Up";
            if (ExplorerForwardButton is not null) ExplorerForwardButton.ToolTip = "Forward";
            if (RightSidebarTitleText is not null) RightSidebarTitleText.Text = "Quick Panel";
            if (ExplorerHintText is not null) ExplorerHintText.Text = "Drag files into the session terminal to recognize as paths";
            if (QuickCommandHintText is not null) QuickCommandHintText.Text = "Click an item to insert into the active session";
            if (QuickCommandFolderAddButton is not null) QuickCommandFolderAddButton.ToolTip = "New Folder";
            _folderContextEditMenuItem.Header = "Edit";
            _folderContextDeleteMenuItem.Header = "Delete";
            _commandContextEditMenuItem.Header = "Edit";
            _commandContextDeleteMenuItem.Header = "Delete";
            if (QuickCommandNewButton is not null) QuickCommandNewButton.Content = "Add";
            if (RecentInputsHintText is not null) RecentInputsHintText.Text = "Keep the latest 18 commands and click an item to insert";
            if (RecentInputClearButton is not null) RecentInputClearButton.Content = "Clear";
            UpdateToolsToggleToolTip();
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
            if (RightExplorerButton is not null) RightExplorerButton.Content = "资源管理器";
            if (RightQuickCommandsButton is not null) RightQuickCommandsButton.Content = "快捷指令";
            if (RightRecentInputsButton is not null) RightRecentInputsButton.Content = "历史指令";
            if (ExplorerRefreshButton is not null) ExplorerRefreshButton.ToolTip = "刷新";
            if (ExplorerUpButton is not null) ExplorerUpButton.ToolTip = "上级";
            if (ExplorerForwardButton is not null) ExplorerForwardButton.ToolTip = "前进";
            if (RightSidebarTitleText is not null) RightSidebarTitleText.Text = "快捷面板";
            if (ExplorerHintText is not null) ExplorerHintText.Text = "文件拖拽进会话终端可识别为路径";
            if (QuickCommandHintText is not null) QuickCommandHintText.Text = "点击条目直接插入到当前会话";
            if (QuickCommandFolderAddButton is not null) QuickCommandFolderAddButton.ToolTip = "新增文件夹";
            _folderContextEditMenuItem.Header = "编辑";
            _folderContextDeleteMenuItem.Header = "删除";
            _commandContextEditMenuItem.Header = "编辑";
            _commandContextDeleteMenuItem.Header = "删除";
            if (QuickCommandNewButton is not null) QuickCommandNewButton.Content = "新增";
            if (RecentInputsHintText is not null) RecentInputsHintText.Text = "保存最近18条输入，点击条目直接插入到当前会话";
            if (RecentInputClearButton is not null) RecentInputClearButton.Content = "清空";
            UpdateToolsToggleToolTip();
        }

        var desiredRoot = isEnglish ? ExplorerVirtualRootEn : ExplorerVirtualRootZh;
        if (IsExplorerVirtualRootPath(_viewModel.ExplorerRootPath) &&
            !string.Equals(_viewModel.ExplorerRootPath, desiredRoot, StringComparison.OrdinalIgnoreCase))
        {
            _viewModel.ExplorerRootPath = desiredRoot;
            _viewModel.LoadExplorerRoot();
            UpdateExplorerNavButtons();
        }

        if (IsExplorerVirtualRootPath(_viewModel.ExplorerCurrentPath) &&
            !string.Equals(_viewModel.ExplorerCurrentPath, desiredRoot, StringComparison.OrdinalIgnoreCase))
        {
            _viewModel.ExplorerCurrentPath = desiredRoot;
            UpdateExplorerNavButtons();
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
