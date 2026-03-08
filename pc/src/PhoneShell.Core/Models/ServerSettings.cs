namespace PhoneShell.Core.Models;

public sealed class ServerSettings
{
    public bool IsRelayServer { get; set; }
    public int Port { get; set; } = 9090;
    public string? RelayServerAddress { get; set; }
}
