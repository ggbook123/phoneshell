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
}
