namespace PhoneShell.Core.Services;

/// <summary>
/// Detects when terminal output has stabilized (no new output for a debounce period).
/// Each call to WaitForStableOutputAsync is independent and self-contained.
/// </summary>
public sealed class TerminalOutputStabilizer : IDisposable
{
    private readonly int _debounceMs;
    private long _lastOutputTicks;
    private int _disposed;

    public TerminalOutputStabilizer(int debounceMs = 2000)
    {
        _debounceMs = debounceMs;
        _lastOutputTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>
    /// Call this every time the terminal produces output.
    /// </summary>
    public void NotifyOutputReceived()
    {
        Interlocked.Exchange(ref _lastOutputTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// Polls until no output has been received for the debounce interval.
    /// Simple, no locks, no Timer, no TCS — just polling with Task.Delay.
    /// </summary>
    public async Task WaitForStableOutputAsync(CancellationToken ct = default)
    {
        // Mark current time as "output received" so we wait at least one full interval
        NotifyOutputReceived();

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct);

            var lastTicks = Interlocked.Read(ref _lastOutputTicks);
            var elapsed = DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc);
            if (elapsed.TotalMilliseconds >= _debounceMs)
                return;
        }

        ct.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }
}
