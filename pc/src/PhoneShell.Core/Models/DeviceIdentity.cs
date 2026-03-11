namespace PhoneShell.Core.Models;

public sealed class DeviceIdentity
{
    public string DeviceId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public static DeviceIdentity Create(string? displayName = null)
    {
        return new DeviceIdentity
        {
            DeviceId = Guid.NewGuid().ToString("N"),
            DisplayName = displayName ?? Environment.MachineName,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}
