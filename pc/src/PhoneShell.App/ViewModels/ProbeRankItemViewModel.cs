using System.Windows.Media;

namespace PhoneShell.ViewModels;

public sealed record ProbeRankItemViewModel
{
    public int Rank { get; init; }
    public string Glyph { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string ValueText { get; init; } = string.Empty;
    public string DetailText { get; init; } = string.Empty;
    public string LevelText { get; init; } = string.Empty;
    public double ScorePercent { get; init; }
    public Brush AccentBrush { get; init; } = Brushes.White;
    public Brush AccentSurfaceBrush { get; init; } = Brushes.Transparent;
}
