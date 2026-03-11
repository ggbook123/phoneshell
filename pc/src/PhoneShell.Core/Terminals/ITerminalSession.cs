namespace PhoneShell.Core.Terminals;

public interface ITerminalSession : IDisposable
{
    event Action<byte[]>? OutputReady;
    void Start(string executable, string arguments, int cols, int rows);
    void Write(byte[] data);
    void Resize(int cols, int rows);
}
