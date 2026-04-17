using System.Collections.Concurrent;
using PhoneShell.Core.Models;
using PhoneShell.Core.Services;

namespace PhoneShell.ViewModels;

public sealed partial class MainViewModel
{
    private readonly DeviceProbeSampler _deviceProbeSampler = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ProbeSnapshot?>> _pendingProbeRequests =
        new(StringComparer.Ordinal);
    private bool _isEnglishUi;

    public bool IsEnglishUi
    {
        get => _isEnglishUi;
        private set => SetProperty(ref _isEnglishUi, value);
    }

    public ProbeTargetDescriptor GetCurrentProbeTargetDescriptor()
    {
        return ResolveProbeTargetDescriptor(CurrentSessionTargetDeviceId);
    }

    public ProbeTargetDescriptor ResolveProbeTargetDescriptor(string? deviceId)
    {
        var normalizedDeviceId = string.IsNullOrWhiteSpace(deviceId) ? _identity.DeviceId : deviceId.Trim();
        var isRemote = !string.Equals(normalizedDeviceId, _identity.DeviceId, StringComparison.Ordinal);
        if (!isRemote)
        {
            var displayName = !string.IsNullOrWhiteSpace(_identity.DisplayName)
                ? _identity.DisplayName
                : _localSessionTargetLabel;
            return new ProbeTargetDescriptor(_identity.DeviceId, displayName, false, true);
        }

        var member = GroupMembers.FirstOrDefault(m =>
            string.Equals(m.DeviceId, normalizedDeviceId, StringComparison.Ordinal));
        var fallbackName = normalizedDeviceId[..Math.Min(8, normalizedDeviceId.Length)];
        return new ProbeTargetDescriptor(
            normalizedDeviceId,
            member?.DisplayName ?? fallbackName,
            true,
            member?.IsOnline == true);
    }

    public async Task<ProbeSnapshot?> GetProbeSnapshotAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        var target = ResolveProbeTargetDescriptor(deviceId);
        if (!target.IsRemote)
            return CaptureLocalProbeSnapshot();

        if (!target.IsOnline)
            return null;

        var requestId = CreateProbeRequestId();
        var pending = new TaskCompletionSource<ProbeSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingProbeRequests.TryAdd(requestId, pending))
            return null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
        using var registration = timeoutCts.Token.Register(() => pending.TrySetCanceled(timeoutCts.Token));

        try
        {
            if (_relayClient is not null && _relayClient.IsConnected)
            {
                await _relayClient.SendProbeSnapshotRequestAsync(target.DeviceId, requestId);
            }
            else if (_relayServer is not null && _relayServer.IsRunning)
            {
                await _relayServer.RequestProbeSnapshotFromDeviceAsync(target.DeviceId, requestId);
            }
            else
            {
                return null;
            }

            return await pending.Task;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            OnNetworkLog($"Probe snapshot request failed for {target.DeviceId}: {ex.Message}");
            return null;
        }
        finally
        {
            _pendingProbeRequests.TryRemove(requestId, out _);
        }
    }

    private ProbeSnapshot CaptureLocalProbeSnapshot()
    {
        return _deviceProbeSampler.CaptureSnapshot(_identity.DeviceId, GetLocalSessionCount());
    }

    private int GetLocalSessionCount()
    {
        return Tabs.Count(tab => !tab.IsRemote);
    }

    private void OnProbeSnapshotReceived(string requestId, ProbeSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(requestId) || snapshot is null)
            return;

        if (_pendingProbeRequests.TryRemove(requestId, out var pending))
            pending.TrySetResult(snapshot);
    }

    private void CancelPendingProbeRequests()
    {
        foreach (var pair in _pendingProbeRequests.ToArray())
        {
            if (_pendingProbeRequests.TryRemove(pair.Key, out var pending))
                pending.TrySetResult(null);
        }
    }

    private static string CreateProbeRequestId()
    {
        return Guid.NewGuid().ToString("N");
    }
}
