using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using PhoneShell.Core.Models;
using PhoneShell.Utilities;

namespace PhoneShell.ViewModels;

public sealed class ProbeWindowViewModel : ObservableObject, IDisposable
{
    private readonly Func<string, ProbeTargetDescriptor> _targetResolver;
    private readonly Func<string, CancellationToken, Task<ProbeSnapshot?>> _snapshotProvider;
    private ProbeSnapshot? _lastSnapshot;
    private CancellationTokenSource? _refreshCts;
    private string _targetDeviceId = string.Empty;
    private string _targetDisplayName = string.Empty;
    private string _pageTitle = "设备探针";
    private string _pageSubtitle = "轻量级性能总览";
    private string _targetKindText = "本机";
    private string _connectionStateText = "在线";
    private string _refreshCadenceText = "每 1.0 秒刷新";
    private string _lastUpdatedText = "等待首个样本";
    private string _statusText = "正在等待实时数据";
    private string _sessionsLabel = "活动会话";
    private string _uptimeLabel = "运行时间";
    private string _coresLabel = "逻辑核心";
    private string _sessionsValue = "--";
    private string _uptimeValue = "--";
    private string _coresValue = "--";
    private string _rankingTitle = "压力排序";
    private string _rankingSubtitle = "按当前占用水平从高到低排列";
    private string _emptyStateTitle = "等待探针数据";
    private string _emptyStateMessage = "窗口打开后将自动拉取当前设备的实时指标。";
    private string _refreshButtonText = "立即刷新";
    private bool _isEnglish;
    private bool _hasSnapshot;
    private bool _isRefreshing;
    private ProbeNetworkSortMode _networkSortMode = ProbeNetworkSortMode.Download;

    public ProbeWindowViewModel(
        Func<string, ProbeTargetDescriptor> targetResolver,
        Func<string, CancellationToken, Task<ProbeSnapshot?>> snapshotProvider)
    {
        _targetResolver = targetResolver;
        _snapshotProvider = snapshotProvider;

        MetricCards.Add(new ProbeMetricCardViewModel("cpu", "CPU", CreateBrush("#FFBE0B")));
        MetricCards.Add(new ProbeMetricCardViewModel("memory", "MEM", CreateBrush("#4DA6FF")));
        MetricCards.Add(new ProbeMetricCardViewModel("network", "NET", CreateBrush("#00D4AA")));

        ProcessDetailCards.Add(new ProbeProcessDetailCardViewModel("cpu", "CPU", MetricCards[0].AccentBrush));
        ProcessDetailCards.Add(new ProbeProcessDetailCardViewModel("memory", "MEM", MetricCards[1].AccentBrush));
        ProcessDetailCards.Add(new ProbeProcessDetailCardViewModel("network", "NET", MetricCards[2].AccentBrush));
    }

    public ObservableCollection<ProbeMetricCardViewModel> MetricCards { get; } = new();

    public ObservableCollection<ProbeProcessDetailCardViewModel> ProcessDetailCards { get; } = new();

    public ObservableCollection<ProbeRankItemViewModel> RankedMetrics { get; } = new();

    public string TargetDeviceId
    {
        get => _targetDeviceId;
        private set => SetProperty(ref _targetDeviceId, value);
    }

    public string TargetDisplayName
    {
        get => _targetDisplayName;
        private set => SetProperty(ref _targetDisplayName, value);
    }

    public string PageTitle
    {
        get => _pageTitle;
        private set => SetProperty(ref _pageTitle, value);
    }

    public string PageSubtitle
    {
        get => _pageSubtitle;
        private set => SetProperty(ref _pageSubtitle, value);
    }

    public string TargetKindText
    {
        get => _targetKindText;
        private set => SetProperty(ref _targetKindText, value);
    }

    public string ConnectionStateText
    {
        get => _connectionStateText;
        private set => SetProperty(ref _connectionStateText, value);
    }

    public string RefreshCadenceText
    {
        get => _refreshCadenceText;
        private set => SetProperty(ref _refreshCadenceText, value);
    }

    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        private set => SetProperty(ref _lastUpdatedText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SessionsLabel
    {
        get => _sessionsLabel;
        private set => SetProperty(ref _sessionsLabel, value);
    }

    public string UptimeLabel
    {
        get => _uptimeLabel;
        private set => SetProperty(ref _uptimeLabel, value);
    }

    public string CoresLabel
    {
        get => _coresLabel;
        private set => SetProperty(ref _coresLabel, value);
    }

    public string SessionsValue
    {
        get => _sessionsValue;
        private set => SetProperty(ref _sessionsValue, value);
    }

    public string UptimeValue
    {
        get => _uptimeValue;
        private set => SetProperty(ref _uptimeValue, value);
    }

    public string CoresValue
    {
        get => _coresValue;
        private set => SetProperty(ref _coresValue, value);
    }

    public string RankingTitle
    {
        get => _rankingTitle;
        private set => SetProperty(ref _rankingTitle, value);
    }

    public string RankingSubtitle
    {
        get => _rankingSubtitle;
        private set => SetProperty(ref _rankingSubtitle, value);
    }

    public string EmptyStateTitle
    {
        get => _emptyStateTitle;
        private set => SetProperty(ref _emptyStateTitle, value);
    }

    public string EmptyStateMessage
    {
        get => _emptyStateMessage;
        private set => SetProperty(ref _emptyStateMessage, value);
    }

    public string RefreshButtonText
    {
        get => _refreshButtonText;
        private set => SetProperty(ref _refreshButtonText, value);
    }

    public bool HasSnapshot
    {
        get => _hasSnapshot;
        private set => SetProperty(ref _hasSnapshot, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetProperty(ref _isRefreshing, value);
    }

    public bool IsRemoteTarget { get; private set; }

    public void SetNetworkSortMode(ProbeNetworkSortMode sortMode)
    {
        if (_networkSortMode == sortMode)
            return;

        _networkSortMode = sortMode;
        if (_lastSnapshot is not null)
            ApplyProcessDetails(_lastSnapshot);
    }

    public void SetLanguage(bool isEnglish)
    {
        _isEnglish = isEnglish;
        PageTitle = isEnglish ? "Device Probe" : "设备探针";
        PageSubtitle = isEnglish ? "Lightweight live telemetry surface" : "轻量级实时指标面板";
        SessionsLabel = isEnglish ? "Live Sessions" : "活动会话";
        UptimeLabel = isEnglish ? "Uptime" : "运行时间";
        CoresLabel = isEnglish ? "Logical Cores" : "逻辑核心";
        RankingTitle = isEnglish ? "Pressure Ranking" : "压力排序";
        RankingSubtitle = isEnglish
            ? "Sorted from highest to lowest current utilization"
            : "按当前占用水平从高到低排列";
        EmptyStateTitle = isEnglish ? "Waiting For Probe Data" : "等待探针数据";
        EmptyStateMessage = isEnglish
            ? "The window will automatically pull live metrics for the selected device."
            : "窗口打开后会自动拉取当前设备的实时指标。";
        RefreshButtonText = isEnglish ? "Refresh Now" : "立即刷新";

        ApplyTargetDescriptor(_targetResolver(TargetDeviceId.Length == 0 ? string.Empty : TargetDeviceId));
        if (_lastSnapshot is not null)
        {
            ApplySnapshot(_targetResolver(TargetDeviceId), _lastSnapshot);
        }
        else
        {
            ResetDetailCards();
        }
    }

    public void SetTarget(ProbeTargetDescriptor descriptor)
    {
        CancelRefresh();
        TargetDeviceId = descriptor.DeviceId;
        _lastSnapshot = null;
        HasSnapshot = false;
        EmptyStateTitle = _isEnglish ? "Waiting For Probe Data" : "等待探针数据";
        EmptyStateMessage = _isEnglish
            ? "The window will automatically pull live metrics for the selected device."
            : "窗口打开后会自动拉取当前设备的实时指标。";
        ApplyTargetDescriptor(descriptor);
        ResetCards();
        ResetDetailCards();
        RankedMetrics.Clear();
        SessionsValue = "--";
        UptimeValue = "--";
        CoresValue = "--";
        LastUpdatedText = _isEnglish ? "Waiting for first sample" : "等待首个样本";
        StatusText = _isEnglish ? "Preparing live telemetry" : "正在准备实时数据";
    }

    public async Task RefreshAsync()
    {
        if (TargetDeviceId.Length == 0)
            return;

        CancelRefresh();
        _refreshCts = new CancellationTokenSource();
        var cancellationToken = _refreshCts.Token;
        var descriptor = _targetResolver(TargetDeviceId);
        ApplyTargetDescriptor(descriptor);

        if (descriptor.IsRemote && !descriptor.IsOnline)
        {
            MarkUnavailable(
                _isEnglish ? "Remote target is offline" : "远程设备当前离线",
                _isEnglish ? "Bring the device online and the chart will resume automatically." : "待设备重新上线后，图表会自动恢复。");
            return;
        }

        IsRefreshing = true;
        StatusText = _isEnglish ? "Collecting live metrics" : "正在采集实时指标";

        try
        {
            var snapshot = await _snapshotProvider(TargetDeviceId, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;

            if (snapshot is null)
            {
                MarkUnavailable(
                    _isEnglish ? "No live sample returned" : "未收到实时样本",
                    _isEnglish ? "Check network connectivity or wait for the target device to respond." : "请检查网络连接，或等待目标设备响应。");
                return;
            }

            _lastSnapshot = snapshot;
            ApplySnapshot(descriptor, snapshot);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
                IsRefreshing = false;
        }
    }

    public void Dispose()
    {
        CancelRefresh();
    }

    private void ApplyTargetDescriptor(ProbeTargetDescriptor descriptor)
    {
        if (descriptor.DeviceId.Length == 0)
            return;

        TargetDeviceId = descriptor.DeviceId;
        TargetDisplayName = descriptor.DisplayName;
        IsRemoteTarget = descriptor.IsRemote;
        TargetKindText = descriptor.IsRemote
            ? (_isEnglish ? "REMOTE TARGET" : "远程设备")
            : (_isEnglish ? "LOCAL DEVICE" : "本机设备");
        ConnectionStateText = descriptor.IsOnline
            ? (_isEnglish ? "ONLINE" : "在线")
            : (_isEnglish ? "OFFLINE" : "离线");
        RefreshCadenceText = descriptor.IsRemote
            ? (_isEnglish ? "Auto refresh every 1.6s" : "每 1.6 秒自动刷新")
            : (_isEnglish ? "Auto refresh every 1.0s" : "每 1.0 秒自动刷新");
    }

    private void ApplySnapshot(ProbeTargetDescriptor descriptor, ProbeSnapshot snapshot)
    {
        HasSnapshot = true;
        ApplyTargetDescriptor(descriptor);

        SessionsValue = snapshot.SessionCount.ToString();
        UptimeValue = FormatDuration(snapshot.UptimeSeconds);
        CoresValue = snapshot.LogicalProcessorCount.ToString();
        LastUpdatedText = _isEnglish
            ? $"Updated {DateTimeOffset.FromUnixTimeMilliseconds(snapshot.CapturedAtUnixMs):HH:mm:ss}"
            : $"最近刷新 {DateTimeOffset.FromUnixTimeMilliseconds(snapshot.CapturedAtUnixMs):HH:mm:ss}";
        StatusText = descriptor.IsRemote
            ? (_isEnglish ? "Streaming from selected remote device" : "正在查看所选远程设备的实时指标")
            : (_isEnglish ? "Streaming from this device" : "正在查看本机的实时指标");

        ApplyMetricCards(snapshot);
        ApplyProcessDetails(snapshot);
    }

    private void ApplyMetricCards(ProbeSnapshot snapshot)
    {
        MetricCards[0].Apply(
            _isEnglish ? "CPU Pressure" : "CPU 压力",
            $"{snapshot.CpuUsagePercent:0}%",
            _isEnglish ? "system load" : "系统负载",
            _isEnglish
                ? $"{snapshot.LogicalProcessorCount} logical cores"
                : $"{snapshot.LogicalProcessorCount} 个逻辑核心",
            snapshot.CpuUsagePercent);

        MetricCards[1].Apply(
            _isEnglish ? "Memory Footprint" : "内存占用",
            $"{FormatBytes(snapshot.MemoryUsedBytes)} / {FormatBytes(snapshot.MemoryTotalBytes)}",
            _isEnglish ? "resident usage" : "驻留内存",
            _isEnglish
                ? $"{snapshot.MemoryUsagePercent:0}% occupied"
                : $"{snapshot.MemoryUsagePercent:0}% 已占用",
            snapshot.MemoryUsagePercent);

        MetricCards[2].Apply(
            _isEnglish ? "Network Activity" : "网络活跃度",
            FormatRate(snapshot.NetworkTotalBytesPerSecond),
            _isEnglish ? "combined throughput" : "总吞吐速率",
            _isEnglish
                ? $"Down {FormatRate(snapshot.NetworkReceiveBytesPerSecond)}  Up {FormatRate(snapshot.NetworkSendBytesPerSecond)}"
                : $"下行 {FormatRate(snapshot.NetworkReceiveBytesPerSecond)}  上行 {FormatRate(snapshot.NetworkSendBytesPerSecond)}",
            snapshot.NetworkActivityPercent);
    }

    private void ApplyProcessDetails(ProbeSnapshot snapshot)
    {
        ProcessDetailCards[0].Apply(
            _isEnglish ? "CPU Processes" : "CPU 进程明细",
            _isEnglish ? "Top 10 by current CPU usage" : "按当前 CPU 占用排序的前 10 个进程",
            _isEnglish ? "Waiting for the next CPU process sample." : "等待下一轮 CPU 进程采样。",
            snapshot.TopCpuProcesses
                .Take(10)
                .Select((item, index) => new ProbeProcessDetailItemViewModel
                {
                    Rank = index + 1,
                    ProcessName = item.ProcessName,
                    ValueText = $"{item.CpuUsagePercent:0.#}%",
                    MetaText = $"PID {item.ProcessId}"
                }));
        ProcessDetailCards[0].HideSortToggle();

        ProcessDetailCards[1].Apply(
            _isEnglish ? "Memory Processes" : "内存进程明细",
            _isEnglish ? "Top 10 by resident memory" : "按驻留内存排序的前 10 个进程",
            _isEnglish ? "Waiting for the next memory process sample." : "等待下一轮内存进程采样。",
            snapshot.TopMemoryProcesses
                .Take(10)
                .Select((item, index) => new ProbeProcessDetailItemViewModel
                {
                    Rank = index + 1,
                    ProcessName = item.ProcessName,
                    ValueText = FormatBytes(item.MemoryBytes),
                    MetaText = $"PID {item.ProcessId}"
                }));
        ProcessDetailCards[1].HideSortToggle();

        var isDownloadSort = _networkSortMode == ProbeNetworkSortMode.Download;
        var orderedNetworkProcesses = snapshot.TopNetworkProcesses
            .OrderByDescending(item => isDownloadSort ? item.NetworkReceiveBytesPerSecond : item.NetworkSendBytesPerSecond)
            .ThenByDescending(item => isDownloadSort ? item.NetworkSendBytesPerSecond : item.NetworkReceiveBytesPerSecond)
            .ThenBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Take(10);
        ProcessDetailCards[2].Apply(
            _isEnglish ? "Network Processes" : "网络进程明细",
            _isEnglish ? "Top 10 by process traffic" : "按进程流量排序的前 10 个进程",
            _isEnglish ? "Waiting for the next network process sample." : "等待下一轮网络进程采样。",
            orderedNetworkProcesses
                .Select((item, index) => new ProbeProcessDetailItemViewModel
                {
                    Rank = index + 1,
                    ProcessName = item.ProcessName,
                    ValueText = $"{(_isEnglish ? "D" : "↓")} {FormatRate(item.NetworkReceiveBytesPerSecond)}   {(_isEnglish ? "U" : "↑")} {FormatRate(item.NetworkSendBytesPerSecond)}",
                    MetaText = $"PID {item.ProcessId}"
                }));
        ProcessDetailCards[2].ConfigureSortToggle(
            _isEnglish ? "Down" : "下行",
            _isEnglish ? "Up" : "上行",
            isDownloadSort);
    }

    private void MarkUnavailable(string title, string message)
    {
        HasSnapshot = false;
        EmptyStateTitle = title;
        EmptyStateMessage = message;
        StatusText = message;
        LastUpdatedText = _isEnglish ? "Awaiting live response" : "等待实时响应";
        ResetCards();
        ResetDetailCards();
        RankedMetrics.Clear();
    }

    private void ResetCards()
    {
        foreach (var metricCard in MetricCards)
            metricCard.Reset();
    }

    private void ResetDetailCards()
    {
        ProcessDetailCards[0].Reset(
            _isEnglish ? "CPU Processes" : "CPU 进程明细",
            _isEnglish ? "Top 10 by current CPU usage" : "按当前 CPU 占用排序的前 10 个进程",
            _isEnglish ? "Waiting for the next CPU process sample." : "等待下一轮 CPU 进程采样。");
        ProcessDetailCards[0].HideSortToggle();
        ProcessDetailCards[1].Reset(
            _isEnglish ? "Memory Processes" : "内存进程明细",
            _isEnglish ? "Top 10 by resident memory" : "按驻留内存排序的前 10 个进程",
            _isEnglish ? "Waiting for the next memory process sample." : "等待下一轮内存进程采样。");
        ProcessDetailCards[1].HideSortToggle();
        ProcessDetailCards[2].Reset(
            _isEnglish ? "Network Processes" : "网络进程明细",
            _isEnglish ? "Top 10 by process traffic" : "按进程流量排序的前 10 个进程",
            _isEnglish ? "Waiting for the next network process sample." : "等待下一轮网络进程采样。");
        ProcessDetailCards[2].ConfigureSortToggle(
            _isEnglish ? "Down" : "下行",
            _isEnglish ? "Up" : "上行",
            _networkSortMode == ProbeNetworkSortMode.Download);
    }

    private void CancelRefresh()
    {
        if (_refreshCts is null)
            return;

        try
        {
            _refreshCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _refreshCts.Dispose();
            _refreshCts = null;
        }
    }

    private string DescribeLevel(double percent)
    {
        return percent switch
        {
            < 18d => _isEnglish ? "Idle" : "空闲",
            < 42d => _isEnglish ? "Steady" : "平稳",
            < 68d => _isEnglish ? "Elevated" : "偏高",
            < 85d => _isEnglish ? "High" : "高压",
            _ => _isEnglish ? "Critical" : "临界"
        };
    }

    private string FormatDuration(long totalSeconds)
    {
        var safeSeconds = Math.Max(0L, totalSeconds);
        var timeSpan = TimeSpan.FromSeconds(safeSeconds);
        if (_isEnglish)
        {
            if (timeSpan.TotalDays >= 1d)
                return $"{(int)timeSpan.TotalDays}d {timeSpan:hh\\:mm}";
            if (timeSpan.TotalHours >= 1d)
                return $"{(int)timeSpan.TotalHours}h {timeSpan:mm\\:ss}";
            return $"{(int)timeSpan.TotalMinutes}m {timeSpan:ss}s";
        }

        if (timeSpan.TotalDays >= 1d)
            return $"{(int)timeSpan.TotalDays}天 {timeSpan:hh\\:mm}";
        if (timeSpan.TotalHours >= 1d)
            return $"{(int)timeSpan.TotalHours}小时 {timeSpan:mm\\:ss}";
        return $"{(int)timeSpan.TotalMinutes}分 {timeSpan:ss}秒";
    }

    private static string FormatBytes(ulong bytes)
    {
        const double scale = 1024d;
        if (bytes < scale)
            return $"{bytes:0} B";

        var units = new[] { "KB", "MB", "GB", "TB" };
        var value = bytes / scale;
        var unitIndex = 0;
        while (value >= scale && unitIndex < units.Length - 1)
        {
            value /= scale;
            unitIndex++;
        }

        return $"{value:0.#} {units[unitIndex]}";
    }

    private static string FormatRate(long bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
            return "0 B/s";

        const double scale = 1024d;
        var value = (double)bytesPerSecond;
        var units = new[] { "B/s", "KB/s", "MB/s", "GB/s" };
        var unitIndex = 0;
        while (value >= scale && unitIndex < units.Length - 1)
        {
            value /= scale;
            unitIndex++;
        }

        return $"{value:0.#} {units[unitIndex]}";
    }

    private static SolidColorBrush CreateBrush(string colorHex)
    {
        var color = (Color)ColorConverter.ConvertFromString(colorHex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Brush CreateAlphaBrush(Brush sourceBrush, byte alpha)
    {
        if (sourceBrush is SolidColorBrush solidColorBrush)
        {
            var color = solidColorBrush.Color;
            var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }

        return sourceBrush.Clone();
    }
}

public enum ProbeNetworkSortMode
{
    Download,
    Upload
}
