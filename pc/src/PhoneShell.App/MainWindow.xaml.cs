using System;
using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
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

        _viewModel.ExecuteTerminalCommand = cmd => _viewModel.TerminalManager.WriteInput(cmd);
        _viewModel.TerminalOutputForwarded += OnTerminalOutputForwarded;
        _viewModel.TerminalRestarted += OnTerminalRestarted;
        _viewModel.TerminalViewportLockRequested += OnTerminalViewportLockRequested;
        _viewModel.TerminalViewportAutoFitRequested += OnTerminalViewportAutoFitRequested;
        _viewModel.ChatMessages.CollectionChanged += ChatMessages_CollectionChanged;

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
        _viewModel.SessionStatus = "Terminal ready";

        ApiKeyBox.Password = _viewModel.AiApiKey;
    }

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
                if (data is not null)
                    _viewModel.TerminalManager.WriteInput(data);
                break;
            case "resize":
                var cols = root.GetProperty("cols").GetInt32();
                var rows = root.GetProperty("rows").GetInt32();
                if (cols > 0 && rows > 0)
                {
                    _viewModel.HandleLocalViewportResize(cols, rows);
                }
                break;
        }
    }

    private void OnTerminalViewportLockRequested(int cols, int rows)
    {
        if (!_webViewReady) return;

        Dispatcher.InvokeAsync(() =>
        {
            TerminalWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.setTerminalGeometry && window.setTerminalGeometry({cols}, {rows})");
        });
    }

    private void OnTerminalViewportAutoFitRequested()
    {
        if (!_webViewReady) return;

        Dispatcher.InvokeAsync(() =>
        {
            TerminalWebView.CoreWebView2.ExecuteScriptAsync(
                "window.fitTerminalToContainer && window.fitTerminalToContainer()");
        });
    }

    /// <summary>
    /// Called by ViewModel when terminal output has been processed (buffer, stabilizer, network).
    /// We just need to push it to WebView2.
    /// </summary>
    private void OnTerminalOutputForwarded(string text)
    {
        if (!_webViewReady) return;
        Dispatcher.InvokeAsync(() =>
        {
            var escaped = JsonSerializer.Serialize(text);
            TerminalWebView.CoreWebView2.ExecuteScriptAsync($"window.writeTerminal({escaped})");
        });
    }

    /// <summary>
    /// Called by ViewModel when terminal is restarted (shell change). Reset xterm.js.
    /// </summary>
    private void OnTerminalRestarted()
    {
        if (!_webViewReady) return;
        Dispatcher.InvokeAsync(() =>
        {
            // Reset the xterm.js terminal
            TerminalWebView.CoreWebView2.ExecuteScriptAsync("window.resetTerminal && window.resetTerminal()");

            // Re-wire the command delegate to the new TerminalManager
            _viewModel.ExecuteTerminalCommand = cmd => _viewModel.TerminalManager.WriteInput(cmd);

            // Re-set the snapshot executor
            _viewModel.SnapshotService.SetScriptExecutor(
                script => TerminalWebView.CoreWebView2.ExecuteScriptAsync(script));
        });
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _viewModel.AiApiKey = ApiKeyBox.Password;
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
        _viewModel.TerminalRestarted -= OnTerminalRestarted;
        _viewModel.TerminalViewportLockRequested -= OnTerminalViewportLockRequested;
        _viewModel.TerminalViewportAutoFitRequested -= OnTerminalViewportAutoFitRequested;
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
