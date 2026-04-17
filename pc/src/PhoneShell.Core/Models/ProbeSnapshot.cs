namespace PhoneShell.Core.Models;

public sealed class ProbeSnapshot
{
    public string DeviceId { get; init; } = string.Empty;
    public long CapturedAtUnixMs { get; init; }
    public int SessionCount { get; init; }
    public int LogicalProcessorCount { get; init; }
    public long UptimeSeconds { get; init; }
    public double CpuUsagePercent { get; init; }
    public double MemoryUsagePercent { get; init; }
    public ulong MemoryUsedBytes { get; init; }
    public ulong MemoryTotalBytes { get; init; }
    public double NetworkActivityPercent { get; init; }
    public long NetworkReceiveBytesPerSecond { get; init; }
    public long NetworkSendBytesPerSecond { get; init; }
    public long NetworkTotalBytesPerSecond { get; init; }
    public long NetworkLinkSpeedBitsPerSecond { get; init; }
    public List<ProbeProcessUsage> TopCpuProcesses { get; init; } = new();
    public List<ProbeProcessUsage> TopMemoryProcesses { get; init; } = new();
    public List<ProbeProcessUsage> TopNetworkProcesses { get; init; } = new();
}

public sealed class ProbeProcessUsage
{
    public string ProcessName { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public double CpuUsagePercent { get; init; }
    public ulong MemoryBytes { get; init; }
    public int ActiveTcpConnectionCount { get; init; }
    public long NetworkReceiveBytesPerSecond { get; init; }
    public long NetworkSendBytesPerSecond { get; init; }
}
