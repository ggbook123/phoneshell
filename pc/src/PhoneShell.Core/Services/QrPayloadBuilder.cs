using PhoneShell.Core.Models;

namespace PhoneShell.Core.Services;

public sealed class QrPayloadBuilder
{
    public string Build(DeviceIdentity identity)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var deviceId = Uri.EscapeDataString(identity.DeviceId);
        var name = Uri.EscapeDataString(identity.DisplayName);
        return $"phoneshell://bind?deviceId={deviceId}&name={name}&nonce={nonce}";
    }

    /// <summary>
    /// Build a QR payload for mobile binding that includes server address and group secret.
    /// Scanning this QR code allows the phone to connect + join group + bind as mobile admin.
    /// </summary>
    public string BuildGroupBind(string serverWsUrl, string groupId, string groupSecret, string? serverDeviceId = null)
    {
        var server = Uri.EscapeDataString(serverWsUrl);
        var gid = Uri.EscapeDataString(groupId);
        var secret = Uri.EscapeDataString(groupSecret);
        var nonce = Guid.NewGuid().ToString("N");
        if (!string.IsNullOrWhiteSpace(serverDeviceId))
        {
            var deviceId = Uri.EscapeDataString(serverDeviceId);
            return $"phoneshell://bind?server={server}&groupId={gid}&groupSecret={secret}&serverDeviceId={deviceId}&nonce={nonce}";
        }
        return $"phoneshell://bind?server={server}&groupId={gid}&groupSecret={secret}&nonce={nonce}";
    }

    /// <summary>
    /// Build a QR payload for panel login (already-bound mobile scans to authenticate).
    /// Does NOT include groupSecret — safe even if leaked.
    /// </summary>
    public string BuildPanelLogin(string serverWsUrl, string groupId, string requestId)
    {
        var server = Uri.EscapeDataString(serverWsUrl);
        var gid = Uri.EscapeDataString(groupId);
        var rid = Uri.EscapeDataString(requestId);
        var nonce = Guid.NewGuid().ToString("N");
        return $"phoneshell://login?server={server}&groupId={gid}&requestId={rid}&nonce={nonce}";
    }
}
