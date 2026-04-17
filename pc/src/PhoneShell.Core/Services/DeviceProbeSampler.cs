using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using PhoneShell.Core.Models;

namespace PhoneShell.Core.Services;

public sealed class DeviceProbeSampler
{
    private const int TopProcessCount = 10;
    private static readonly TimeSpan ProcessDetailRefreshInterval = TimeSpan.FromSeconds(3);
    private readonly object _syncRoot = new();
    private CpuTimesSnapshot? _previousCpu;
    private NetworkTotalsSnapshot? _previousNetwork;
    private Dictionary<int, ProcessCpuSnapshot> _previousProcessCpu = new();
    private Dictionary<int, ProcessIoSnapshot> _previousProcessIo = new();
    private DateTimeOffset? _lastProcessSampleAtUtc;
    private ProcessDetailSnapshot _cachedProcessDetails = ProcessDetailSnapshot.Empty;

    public ProbeSnapshot CaptureSnapshot(string deviceId, int sessionCount)
    {
        lock (_syncRoot)
        {
            var capturedAt = DateTimeOffset.UtcNow;
            var cpuUsagePercent = ReadCpuUsagePercent();
            var (memoryTotalBytes, memoryUsedBytes, memoryUsagePercent) = ReadMemoryUsage();
            var network = ReadNetworkUsage(capturedAt);
            var processDetails = ReadProcessDetails(capturedAt);

            return new ProbeSnapshot
            {
                DeviceId = deviceId,
                CapturedAtUnixMs = capturedAt.ToUnixTimeMilliseconds(),
                SessionCount = Math.Max(0, sessionCount),
                LogicalProcessorCount = Environment.ProcessorCount,
                UptimeSeconds = Math.Max(0, Environment.TickCount64 / 1000),
                CpuUsagePercent = cpuUsagePercent,
                MemoryUsagePercent = memoryUsagePercent,
                MemoryUsedBytes = memoryUsedBytes,
                MemoryTotalBytes = memoryTotalBytes,
                NetworkActivityPercent = network.ActivityPercent,
                NetworkReceiveBytesPerSecond = network.ReceiveBytesPerSecond,
                NetworkSendBytesPerSecond = network.SendBytesPerSecond,
                NetworkTotalBytesPerSecond = network.TotalBytesPerSecond,
                NetworkLinkSpeedBitsPerSecond = network.LinkSpeedBitsPerSecond,
                TopCpuProcesses = processDetails.TopCpuProcesses,
                TopMemoryProcesses = processDetails.TopMemoryProcesses,
                TopNetworkProcesses = processDetails.TopNetworkProcesses
            };
        }
    }

    private double ReadCpuUsagePercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return 0d;

        var current = new CpuTimesSnapshot(
            ToUInt64(idle),
            ToUInt64(kernel),
            ToUInt64(user));

        if (_previousCpu is null)
        {
            _previousCpu = current;
            return 0d;
        }

        var idleDelta = current.IdleTicks >= _previousCpu.Value.IdleTicks
            ? current.IdleTicks - _previousCpu.Value.IdleTicks
            : 0UL;
        var kernelDelta = current.KernelTicks >= _previousCpu.Value.KernelTicks
            ? current.KernelTicks - _previousCpu.Value.KernelTicks
            : 0UL;
        var userDelta = current.UserTicks >= _previousCpu.Value.UserTicks
            ? current.UserTicks - _previousCpu.Value.UserTicks
            : 0UL;

        _previousCpu = current;

        var totalDelta = kernelDelta + userDelta;
        if (totalDelta == 0 || totalDelta < idleDelta)
            return 0d;

        var busyDelta = totalDelta - idleDelta;
        return Math.Clamp(busyDelta * 100d / totalDelta, 0d, 100d);
    }

    private static (ulong TotalBytes, ulong UsedBytes, double UsagePercent) ReadMemoryUsage()
    {
        var status = new MemoryStatusEx();
        status.Length = (uint)Marshal.SizeOf<MemoryStatusEx>();

        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
            return (0UL, 0UL, 0d);

        var usedBytes = status.TotalPhys > status.AvailPhys
            ? status.TotalPhys - status.AvailPhys
            : 0UL;

        return (
            status.TotalPhys,
            usedBytes,
            Math.Clamp(usedBytes * 100d / status.TotalPhys, 0d, 100d));
    }

    private NetworkUsageSnapshot ReadNetworkUsage(DateTimeOffset capturedAt)
    {
        long totalReceivedBytes = 0;
        long totalSentBytes = 0;
        long totalLinkSpeedBitsPerSecond = 0;

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!IsEligibleInterface(networkInterface))
                continue;

            try
            {
                var statistics = networkInterface.GetIPv4Statistics();
                totalReceivedBytes += statistics.BytesReceived;
                totalSentBytes += statistics.BytesSent;
                if (networkInterface.Speed > 0)
                    totalLinkSpeedBitsPerSecond += networkInterface.Speed;
            }
            catch (NetworkInformationException)
            {
            }
        }

        var current = new NetworkTotalsSnapshot(capturedAt, totalReceivedBytes, totalSentBytes, totalLinkSpeedBitsPerSecond);
        if (_previousNetwork is null)
        {
            _previousNetwork = current;
            return new NetworkUsageSnapshot(0L, 0L, 0L, 0L, 0d);
        }

        var intervalSeconds = Math.Max((current.CapturedAtUtc - _previousNetwork.Value.CapturedAtUtc).TotalSeconds, 0.25d);

        var receiveDelta = current.ReceivedBytes >= _previousNetwork.Value.ReceivedBytes
            ? current.ReceivedBytes - _previousNetwork.Value.ReceivedBytes
            : 0L;
        var sendDelta = current.SentBytes >= _previousNetwork.Value.SentBytes
            ? current.SentBytes - _previousNetwork.Value.SentBytes
            : 0L;

        _previousNetwork = current;

        var receiveBytesPerSecond = (long)Math.Round(receiveDelta / intervalSeconds);
        var sendBytesPerSecond = (long)Math.Round(sendDelta / intervalSeconds);
        var totalBytesPerSecond = Math.Max(0L, receiveBytesPerSecond + sendBytesPerSecond);

        var linkUtilizationPercent = current.LinkSpeedBitsPerSecond > 0
            ? Math.Clamp(totalBytesPerSecond * 8d * 100d / current.LinkSpeedBitsPerSecond, 0d, 100d)
            : 0d;

        var totalMegabitsPerSecond = totalBytesPerSecond * 8d / 1_000_000d;
        var activityFromThroughput = Math.Clamp(
            Math.Log10(1d + totalMegabitsPerSecond) / Math.Log10(101d) * 100d,
            0d,
            100d);

        return new NetworkUsageSnapshot(
            receiveBytesPerSecond,
            sendBytesPerSecond,
            totalBytesPerSecond,
            current.LinkSpeedBitsPerSecond,
            Math.Max(linkUtilizationPercent, activityFromThroughput));
    }

    private ProcessDetailSnapshot ReadProcessDetails(DateTimeOffset capturedAt)
    {
        if (_lastProcessSampleAtUtc is not null
            && capturedAt - _lastProcessSampleAtUtc.Value < ProcessDetailRefreshInterval)
        {
            return _cachedProcessDetails;
        }

        var currentCpuSamples = new Dictionary<int, ProcessCpuSnapshot>();
        var currentIoSamples = new Dictionary<int, ProcessIoSnapshot>();
        var cpuCandidates = new List<ProbeProcessUsage>();
        var memoryCandidates = new List<ProbeProcessUsage>();
        var networkCandidates = new List<ProbeProcessUsage>();
        var tcpConnectionCounts = GetActiveTcpConnectionCountsByPid();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var processId = process.Id;
                    var processName = process.ProcessName;
                    var totalProcessorTime = process.TotalProcessorTime;
                    var workingSetBytes = (ulong)Math.Max(process.WorkingSet64, 0L);
                    currentCpuSamples[processId] = new ProcessCpuSnapshot(totalProcessorTime, capturedAt);
                    var ioCounters = ReadProcessIoCounters(process);
                    if (ioCounters is not null)
                    {
                        currentIoSamples[processId] = new ProcessIoSnapshot(
                            ioCounters.Value.ReadTransferCount,
                            ioCounters.Value.WriteTransferCount,
                            capturedAt);
                    }

                    var cpuPercent = 0d;
                    if (_previousProcessCpu.TryGetValue(processId, out var previousCpu))
                    {
                        var elapsedMilliseconds = (capturedAt - previousCpu.CapturedAtUtc).TotalMilliseconds;
                        var cpuMilliseconds = (totalProcessorTime - previousCpu.TotalProcessorTime).TotalMilliseconds;
                        if (elapsedMilliseconds > 0d && cpuMilliseconds > 0d)
                        {
                            cpuPercent = Math.Clamp(
                                cpuMilliseconds / (elapsedMilliseconds * Environment.ProcessorCount) * 100d,
                                0d,
                                100d);
                        }
                    }

                    if (cpuPercent > 0.05d)
                    {
                        cpuCandidates.Add(new ProbeProcessUsage
                        {
                            ProcessName = processName,
                            ProcessId = processId,
                            CpuUsagePercent = cpuPercent
                        });
                    }

                    if (workingSetBytes > 0UL)
                    {
                        memoryCandidates.Add(new ProbeProcessUsage
                        {
                            ProcessName = processName,
                            ProcessId = processId,
                            MemoryBytes = workingSetBytes
                        });
                    }

                    if (tcpConnectionCounts.TryGetValue(processId, out var activeConnections) && activeConnections > 0)
                    {
                        var receiveBytesPerSecond = 0L;
                        var sendBytesPerSecond = 0L;
                        if (currentIoSamples.TryGetValue(processId, out var currentIo)
                            && _previousProcessIo.TryGetValue(processId, out var previousIo))
                        {
                            var elapsedSeconds = Math.Max((currentIo.CapturedAtUtc - previousIo.CapturedAtUtc).TotalSeconds, 0.25d);
                            var readDelta = currentIo.ReadTransferBytes >= previousIo.ReadTransferBytes
                                ? currentIo.ReadTransferBytes - previousIo.ReadTransferBytes
                                : 0UL;
                            var writeDelta = currentIo.WriteTransferBytes >= previousIo.WriteTransferBytes
                                ? currentIo.WriteTransferBytes - previousIo.WriteTransferBytes
                                : 0UL;
                            receiveBytesPerSecond = (long)Math.Round(readDelta / elapsedSeconds);
                            sendBytesPerSecond = (long)Math.Round(writeDelta / elapsedSeconds);
                        }

                        networkCandidates.Add(new ProbeProcessUsage
                        {
                            ProcessName = processName,
                            ProcessId = processId,
                            ActiveTcpConnectionCount = activeConnections,
                            NetworkReceiveBytesPerSecond = Math.Max(0L, receiveBytesPerSecond),
                            NetworkSendBytesPerSecond = Math.Max(0L, sendBytesPerSecond)
                        });
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
                catch (NotSupportedException)
                {
                }
            }
        }

        _previousProcessCpu = currentCpuSamples;
        _previousProcessIo = currentIoSamples;
        _lastProcessSampleAtUtc = capturedAt;
        var topNetworkByReceive = networkCandidates
            .OrderByDescending(item => item.NetworkReceiveBytesPerSecond)
            .ThenByDescending(item => item.NetworkSendBytesPerSecond)
            .ThenBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Take(TopProcessCount)
            .ToList();
        var topNetworkBySend = networkCandidates
            .OrderByDescending(item => item.NetworkSendBytesPerSecond)
            .ThenByDescending(item => item.NetworkReceiveBytesPerSecond)
            .ThenBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Take(TopProcessCount)
            .ToList();
        _cachedProcessDetails = new ProcessDetailSnapshot(
            cpuCandidates
                .OrderByDescending(item => item.CpuUsagePercent)
                .ThenBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
                .Take(TopProcessCount)
                .ToList(),
            memoryCandidates
                .OrderByDescending(item => item.MemoryBytes)
                .ThenBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
                .Take(TopProcessCount)
                .ToList(),
            topNetworkByReceive
                .Concat(topNetworkBySend)
                .GroupBy(item => item.ProcessId)
                .Select(group => group.First())
                .ToList());

        return _cachedProcessDetails;
    }

    private static IoCounters? ReadProcessIoCounters(Process process)
    {
        try
        {
            return GetProcessIoCounters(process.Handle, out var ioCounters)
                ? ioCounters
                : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static Dictionary<int, int> GetActiveTcpConnectionCountsByPid()
    {
        var counts = new Dictionary<int, int>();
        AccumulateActiveTcpConnectionCounts(AddressFamilyInterNetwork, counts, isIpv6: false);
        AccumulateActiveTcpConnectionCounts(AddressFamilyInterNetworkV6, counts, isIpv6: true);
        return counts;
    }

    private static void AccumulateActiveTcpConnectionCounts(int addressFamily, Dictionary<int, int> counts, bool isIpv6)
    {
        var bufferLength = 0;
        _ = GetExtendedTcpTable(
            IntPtr.Zero,
            ref bufferLength,
            true,
            addressFamily,
            TcpTableClass.TcpTableOwnerPidAll,
            0u);

        if (bufferLength <= 0)
            return;

        var buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            var result = GetExtendedTcpTable(
                buffer,
                ref bufferLength,
                true,
                addressFamily,
                TcpTableClass.TcpTableOwnerPidAll,
                0u);

            if (result != 0)
                return;

            var rowCount = Marshal.ReadInt32(buffer);
            var rowPtr = IntPtr.Add(buffer, sizeof(int));
            if (isIpv6)
            {
                var rowSize = Marshal.SizeOf<MibTcp6RowOwnerPid>();
                for (var index = 0; index < rowCount; index++)
                {
                    var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(rowPtr);
                    CountTcpRowIfActive((TcpState)row.State, unchecked((int)row.OwningPid), counts);
                    rowPtr = IntPtr.Add(rowPtr, rowSize);
                }
            }
            else
            {
                var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                for (var index = 0; index < rowCount; index++)
                {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                    CountTcpRowIfActive((TcpState)row.State, unchecked((int)row.OwningPid), counts);
                    rowPtr = IntPtr.Add(rowPtr, rowSize);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void CountTcpRowIfActive(TcpState state, int processId, Dictionary<int, int> counts)
    {
        if (processId <= 0)
            return;

        if (state is TcpState.Listen or TcpState.Closed or TcpState.DeleteTcb)
            return;

        counts[processId] = counts.TryGetValue(processId, out var currentCount)
            ? currentCount + 1
            : 1;
    }

    private static bool IsEligibleInterface(NetworkInterface networkInterface)
    {
        if (networkInterface.OperationalStatus != OperationalStatus.Up)
            return false;

        return networkInterface.NetworkInterfaceType is not NetworkInterfaceType.Loopback
            and not NetworkInterfaceType.Tunnel;
    }

    private static ulong ToUInt64(FILETIME fileTime)
    {
        return ((ulong)(uint)fileTime.dwHighDateTime << 32) | (uint)fileTime.dwLowDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(
        out FILETIME idleTime,
        out FILETIME kernelTime,
        out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int sizePointer,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int ipVersion,
        TcpTableClass tableClass,
        uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(
        IntPtr hProcess,
        out IoCounters ioCounters);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    private enum TcpTableClass
    {
        TcpTableBasicListener,
        TcpTableBasicConnections,
        TcpTableBasicAll,
        TcpTableOwnerPidListener,
        TcpTableOwnerPidConnections,
        TcpTableOwnerPidAll,
        TcpTableOwnerModuleListener,
        TcpTableOwnerModuleConnections,
        TcpTableOwnerModuleAll
    }

    private readonly record struct CpuTimesSnapshot(ulong IdleTicks, ulong KernelTicks, ulong UserTicks);

    private readonly record struct NetworkTotalsSnapshot(
        DateTimeOffset CapturedAtUtc,
        long ReceivedBytes,
        long SentBytes,
        long LinkSpeedBitsPerSecond);

    private readonly record struct NetworkUsageSnapshot(
        long ReceiveBytesPerSecond,
        long SendBytesPerSecond,
        long TotalBytesPerSecond,
        long LinkSpeedBitsPerSecond,
        double ActivityPercent);

    private readonly record struct ProcessCpuSnapshot(
        TimeSpan TotalProcessorTime,
        DateTimeOffset CapturedAtUtc);

    private readonly record struct ProcessIoSnapshot(
        ulong ReadTransferBytes,
        ulong WriteTransferBytes,
        DateTimeOffset CapturedAtUtc);

    private readonly record struct ProcessDetailSnapshot(
        List<ProbeProcessUsage> TopCpuProcesses,
        List<ProbeProcessUsage> TopMemoryProcesses,
        List<ProbeProcessUsage> TopNetworkProcesses)
    {
        public static ProcessDetailSnapshot Empty { get; } = new(new(), new(), new());
    }

    private const int AddressFamilyInterNetwork = 2;
    private const int AddressFamilyInterNetworkV6 = 23;
}
