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
            "session.rename" => Deserialize<SessionRenameMessage>(json),
            "quickpanel.sync.request" => Deserialize<QuickPanelSyncRequestMessage>(json),
            "quickpanel.sync" => Deserialize<QuickPanelSyncMessage>(json),
            "quickpanel.recent.append" => Deserialize<QuickPanelRecentAppendMessage>(json),
            "terminal.open" => Deserialize<TerminalOpenMessage>(json),
            "terminal.opened" => Deserialize<TerminalOpenedMessage>(json),
            "terminal.input" => Deserialize<TerminalInputMessage>(json),
            "terminal.output" => Deserialize<TerminalOutputMessage>(json),
            "terminal.history.request" => Deserialize<TerminalHistoryRequestMessage>(json),
            "terminal.history.response" => Deserialize<TerminalHistoryResponseMessage>(json),
            "terminal.resize" => Deserialize<TerminalResizeMessage>(json),
            "terminal.close" => Deserialize<TerminalCloseMessage>(json),
            "terminal.closed" => Deserialize<TerminalClosedMessage>(json),
            "control.request" => Deserialize<ControlRequestMessage>(json),
            "control.grant" => Deserialize<ControlGrantMessage>(json),
            "control.force_disconnect" => Deserialize<ControlForceDisconnectMessage>(json),
            "group.join.request" => Deserialize<GroupJoinRequestMessage>(json),
            "group.join.accepted" => Deserialize<GroupJoinAcceptedMessage>(json),
            "group.join.rejected" => Deserialize<GroupJoinRejectedMessage>(json),
            "group.member.joined" => Deserialize<GroupMemberJoinedMessage>(json),
            "group.member.left" => Deserialize<GroupMemberLeftMessage>(json),
            "group.member.list" => Deserialize<GroupMemberListMessage>(json),
            "group.kick" => Deserialize<GroupKickMessage>(json),
            "mobile.bind.request" => Deserialize<MobileBindRequestMessage>(json),
            "mobile.bind.accepted" => Deserialize<MobileBindAcceptedMessage>(json),
            "mobile.bind.rejected" => Deserialize<MobileBindRejectedMessage>(json),
            "mobile.unbind" => Deserialize<MobileUnbindMessage>(json),
            "auth.request" => Deserialize<AuthRequestMessage>(json),
            "auth.response" => Deserialize<AuthResponseMessage>(json),
            "group.server.change.request" => Deserialize<GroupServerChangeRequestMessage>(json),
            "group.server.change.prepare" => Deserialize<GroupServerChangePrepareMessage>(json),
            "group.server.change.commit" => Deserialize<GroupServerChangeCommitMessage>(json),
            "group.secret.rotate.request" => Deserialize<GroupSecretRotateRequestMessage>(json),
            "group.secret.rotate.done" => Deserialize<GroupSecretRotateDoneMessage>(json),
            "panel.login.scan" => Deserialize<PanelLoginScanMessage>(json),
            "error" => Deserialize<ErrorMessage>(json),
            "relay.designate" => Deserialize<RelayDesignateMessage>(json),
            "relay.designated" => Deserialize<RelayDesignatedMessage>(json),
            "invite.create.request" => Deserialize<InviteCreateRequestMessage>(json),
            "invite.create.response" => Deserialize<InviteCreateResponseMessage>(json),
            "device.settings.update" => Deserialize<DeviceSettingsUpdateMessage>(json),
            "device.settings.updated" => Deserialize<DeviceSettingsUpdatedMessage>(json),
            "device.kick" => Deserialize<DeviceKickMessage>(json),
            "device.kicked" => Deserialize<DeviceKickedMessage>(json),
            "group.dissolve" => Deserialize<GroupDissolveMessage>(json),
            "group.dissolved" => Deserialize<GroupDissolvedMessage>(json),
            "panel.disconnected" => Deserialize<PanelDisconnectedMessage>(json),
            _ => null
        };
    }
}
