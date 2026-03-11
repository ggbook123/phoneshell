using System;
using System.Collections.Specialized;
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

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        _viewModel.TerminalOutputForwarded += OnTerminalOutputForwarded;
        _viewModel.ActiveTabChanged += OnActiveTabChanged;
        _viewModel.TerminalViewportLockRequested += OnTerminalViewportLockRequested;
        _viewModel.TerminalViewportAutoFitRequested += OnTerminalViewportAutoFitRequested;
        _viewModel.ChatMessages.CollectionChanged += ChatMessages_CollectionChanged;
        _viewModel.Tabs.CollectionChanged += Tabs_CollectionChanged;

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await TerminalWebView.EnsureCoreWebView2Async();
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
            if (_viewModel.ActiveTab is not null)
            {
                CompactModeButton.Content = _viewModel.ActiveTab.IsCompactMode ? "Expand" : "Compact";
            }

            TerminalWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.setTerminalGeometry && window.setTerminalGeometry({cols}, {rows})");
        });
    }

    private void OnTerminalViewportAutoFitRequested()
    {
        if (!_webViewReady) return;

        Dispatcher.InvokeAsync(() =>
        {
            if (_viewModel.ActiveTab is not null)
            {
                CompactModeButton.Content = _viewModel.ActiveTab.IsCompactMode ? "Expand" : "Compact";
            }

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

    private void OnActiveTabChanged(TerminalTab? tab)
    {
        Dispatcher.InvokeAsync(() =>
        {
            UpdatePanelVisibility();

            if (tab is null)
            {
                return;
            }

            // Update compact mode button text
            CompactModeButton.Content = tab.IsCompactMode ? "Expand" : "Compact";

            // Reset xterm.js and replay the new tab's buffered output
            if (_webViewReady)
            {
                TerminalWebView.CoreWebView2.ExecuteScriptAsync(
                    "window.resetTerminal && window.resetTerminal()");

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
        var hasTabs = _viewModel.Tabs.Count > 0;
        WelcomePanel.Visibility = hasTabs ? Visibility.Collapsed : Visibility.Visible;
        TabContainer.Visibility = hasTabs ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTabHighlighting()
    {
        if (_viewModel.ActiveTab is not null)
        {
            CompactModeButton.Content = _viewModel.ActiveTab.IsCompactMode ? "Expand" : "Compact";
        }
    }

    private void Tabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(UpdatePanelVisibility);
    }

    // --- Tab UI Event Handlers ---

    private void ShellCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ShellInfo shell)
        {
            _viewModel.SelectedShell = shell;
            _viewModel.CreateNewSession();
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

    private void CompactModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ActiveTab is not null)
        {
            _viewModel.ToggleCompactModeCommand.Execute(_viewModel.ActiveTab.TabId);
            CompactModeButton.Content = _viewModel.ActiveTab.IsCompactMode ? "Expand" : "Compact";
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
            // Navigate up to the parent DataContext which is the GroupMemberInfo
            var parent = btn.Tag as GroupMemberInfo;
            if (parent is not null)
            {
                await _viewModel.OpenRemoteTerminalAsync(parent.DeviceId, shellId);
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
        _viewModel.ActiveTabChanged -= OnActiveTabChanged;
        _viewModel.TerminalViewportLockRequested -= OnTerminalViewportLockRequested;
        _viewModel.TerminalViewportAutoFitRequested -= OnTerminalViewportAutoFitRequested;
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
