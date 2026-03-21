namespace PhoneShell.Core.Models;

public sealed class ServerSettings
{
    /// <summary>
    /// Auto mode: decide relay role based on local data (group.json, relay url, etc.).
    /// </summary>
    public bool AutoMode { get; set; } = true;
    public bool IsRelayServer { get; set; } = true;
    public int Port { get; set; } = 9090;
    public string? RelayServerAddress { get; set; }

    /// <summary>Group secret for joining an existing group (client mode) or initializing a new group (server mode).</summary>
    public string? GroupSecret { get; set; }
}
