namespace PhoneShell.ViewModels;

public sealed record ProbeTargetDescriptor(
    string DeviceId,
    string DisplayName,
    bool IsRemote,
    bool IsOnline);
