namespace PhoneShell.Core.Models;

public sealed class ServerSettings
{
    public bool IsRelayServer { get; set; }
    public int Port { get; set; } = 9090;
    public string? RelayServerAddress { get; set; }

    /// <summary>Group secret for joining an existing group (client mode) or initializing a new group (server mode).</summary>
    public string? GroupSecret { get; set; }
}
