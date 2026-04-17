using System.Windows;
using System.Windows.Threading;
using PhoneShell.ViewModels;

namespace PhoneShell;

public partial class ProbeWindow : Window
{
    private readonly ProbeWindowViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;
    private bool _refreshInFlight;

    public ProbeWindow(ProbeWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        _refreshTimer = new DispatcherTimer();
        _refreshTimer.Tick += RefreshTimer_Tick;

        InitializeComponent();
        DataContext = _viewModel;
    }

    public void Configure(ProbeTargetDescriptor descriptor, bool isEnglish)
    {
        _viewModel.SetLanguage(isEnglish);
        _viewModel.SetTarget(descriptor);
        Title = isEnglish ? "Device Probe" : "设备探针";
        UpdateRefreshInterval(descriptor.IsRemote);

        if (IsLoaded)
            _ = RefreshNowAsync();
    }

    public void RefreshLanguage(bool isEnglish)
    {
        _viewModel.SetLanguage(isEnglish);
        Title = isEnglish ? "Device Probe" : "设备探针";
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.TargetDeviceId.Length == 0)
            return;

        UpdateRefreshInterval(_viewModel.IsRemoteTarget);
        _refreshTimer.Start();
        await RefreshNowAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshNowAsync();
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshNowAsync();
    }

    private void NetworkSortDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetNetworkSortMode(ProbeNetworkSortMode.Download);
    }

    private void NetworkSortUploadButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetNetworkSortMode(ProbeNetworkSortMode.Upload);
    }

    private async Task RefreshNowAsync()
    {
        if (_refreshInFlight)
            return;

        _refreshInFlight = true;
        try
        {
            await _viewModel.RefreshAsync();
            UpdateRefreshInterval(_viewModel.IsRemoteTarget);
        }
        finally
        {
            _refreshInFlight = false;
        }
    }

    private void UpdateRefreshInterval(bool isRemoteTarget)
    {
        _refreshTimer.Interval = isRemoteTarget
            ? TimeSpan.FromSeconds(1.6d)
            : TimeSpan.FromSeconds(1d);
    }
}
