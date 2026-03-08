namespace PhoneShell.Core.Protocol;

/// <summary>
/// All WebSocket message types exchanged between PC clients, relay server, and mobile clients.
/// Each message is serialized as JSON with a "type" discriminator field.
/// </summary>

// --- Device management ---

public sealed class DeviceRegisterMessage
{
    public string Type => "device.register";
    public string DeviceId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Os { get; init; } = string.Empty;
    public List<string> AvailableShells { get; init; } = new();
}

public sealed class DeviceUnregisterMessage
{
    public string Type => "device.unregister";
    public string DeviceId { get; init; } = string.Empty;
}

public sealed class DeviceListRequestMessage
{
    public string Type => "device.list.request";
}

public sealed class DeviceListMessage
{
    public string Type => "device.list";
    public List<DeviceInfo> Devices { get; init; } = new();
}

public sealed class DeviceInfo
{
    public string DeviceId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Os { get; init; } = string.Empty;
    public bool IsOnline { get; init; }
    public List<string> AvailableShells { get; init; } = new();
}

// --- Session management ---

public sealed class SessionListRequestMessage
{
    public string Type => "session.list.request";
    public string DeviceId { get; init; } = string.Empty;
}

public sealed class SessionListMessage
{
    public string Type => "session.list";
    public string DeviceId { get; init; } = string.Empty;
    public List<SessionInfo> Sessions { get; init; } = new();
}

public sealed class SessionInfo
{
    public string SessionId { get; init; } = string.Empty;
    public string ShellId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
}

// --- Terminal session management ---

public sealed class TerminalOpenMessage
{
    public string Type => "terminal.open";
    public string DeviceId { get; init; } = string.Empty;
    public string ShellId { get; init; } = string.Empty;
}

public sealed class TerminalOpenedMessage
{
    public string Type => "terminal.opened";
    public string DeviceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public int Cols { get; init; }
    public int Rows { get; init; }
}

public sealed class TerminalInputMessage
{
    public string Type => "terminal.input";
    public string DeviceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string Data { get; init; } = string.Empty;
}

public sealed class TerminalOutputMessage
{
    public string Type => "terminal.output";
    public string DeviceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string Data { get; init; } = string.Empty;
}

public sealed class TerminalResizeMessage
{
    public string Type => "terminal.resize";
    public string DeviceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public int Cols { get; init; }
    public int Rows { get; init; }
}

public sealed class TerminalCloseMessage
{
    public string Type => "terminal.close";
    public string DeviceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
}

public sealed class TerminalClosedMessage
{
    public string Type => "terminal.closed";
    public string DeviceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
}

// --- Control ownership ---

public sealed class ControlRequestMessage
{
    public string Type => "control.request";
    public string DeviceId { get; init; } = string.Empty;
    public string RequesterId { get; init; } = string.Empty;
}

public sealed class ControlGrantMessage
{
    public string Type => "control.grant";
    public string DeviceId { get; init; } = string.Empty;
    public string OwnerId { get; init; } = string.Empty;
}

public sealed class ControlForceDisconnectMessage
{
    public string Type => "control.force_disconnect";
    public string DeviceId { get; init; } = string.Empty;
}

// --- Error ---

public sealed class ErrorMessage
{
    public string Type => "error";
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
