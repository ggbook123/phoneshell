using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhoneShell.Core.Protocol;

public static class MessageSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string Serialize(object message)
    {
        return JsonSerializer.Serialize(message, message.GetType(), Options);
    }

    public static string? GetMessageType(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("type", out var typeProp))
                return typeProp.GetString();
        }
        catch (JsonException)
        {
        }
        return null;
    }

    public static T? Deserialize<T>(string json) where T : class
    {
        return JsonSerializer.Deserialize<T>(json, Options);
    }

    /// <summary>
    /// Deserialize a message based on its "type" field.
    /// Returns null if the type is unknown or parsing fails.
    /// </summary>
    public static object? DeserializeMessage(string json)
    {
        var type = GetMessageType(json);
        if (type is null) return null;

        return type switch
        {
            "device.register" => Deserialize<DeviceRegisterMessage>(json),
            "device.unregister" => Deserialize<DeviceUnregisterMessage>(json),
            "device.list.request" => Deserialize<DeviceListRequestMessage>(json),
            "device.list" => Deserialize<DeviceListMessage>(json),
            "session.list.request" => Deserialize<SessionListRequestMessage>(json),
            "session.list" => Deserialize<SessionListMessage>(json),
            "terminal.open" => Deserialize<TerminalOpenMessage>(json),
            "terminal.opened" => Deserialize<TerminalOpenedMessage>(json),
            "terminal.input" => Deserialize<TerminalInputMessage>(json),
            "terminal.output" => Deserialize<TerminalOutputMessage>(json),
            "terminal.resize" => Deserialize<TerminalResizeMessage>(json),
            "terminal.close" => Deserialize<TerminalCloseMessage>(json),
            "terminal.closed" => Deserialize<TerminalClosedMessage>(json),
            "control.request" => Deserialize<ControlRequestMessage>(json),
            "control.grant" => Deserialize<ControlGrantMessage>(json),
            "control.force_disconnect" => Deserialize<ControlForceDisconnectMessage>(json),
            "error" => Deserialize<ErrorMessage>(json),
            _ => null
        };
    }
}
