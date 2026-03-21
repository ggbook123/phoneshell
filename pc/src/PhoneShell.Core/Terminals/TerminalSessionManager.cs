using System.Text;

namespace PhoneShell.Core.Terminals;

public sealed class TerminalSessionManager : IDisposable
{
    private ITerminalSession? _session;
    private bool _disposed;

    public event Action<string>? OutputReceived;

    public bool IsRunning => _session is not null;

    public void Start(ITerminalSession session, ShellInfo shell, int cols, int rows)
    {
        if (_session is not null) return;

        _session = session;
        _session.OutputReady += OnOutputReady;
        _session.Start(shell.ExecutablePath, shell.Arguments, cols, rows);
    }

    public void WriteInput(string data)
    {
        if (_session is null) return;
        var bytes = Encoding.UTF8.GetBytes(data);
        _session.Write(bytes);
    }

    public void Resize(int cols, int rows)
    {
        _session?.Resize(cols, rows);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_session is not null)
            _session.OutputReady -= OnOutputReady;
        _session?.Dispose();
        _session = null;
    }

    private void OnOutputReady(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        OutputReceived?.Invoke(text);
    }
}
