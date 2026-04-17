namespace PhoneShell.ViewModels;

public sealed record ProbeProcessDetailItemViewModel
{
    public int Rank { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string ValueText { get; init; } = string.Empty;
    public string MetaText { get; init; } = string.Empty;
}
