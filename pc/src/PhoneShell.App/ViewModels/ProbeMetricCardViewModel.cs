using System.Windows.Media;
using PhoneShell.Utilities;

namespace PhoneShell.ViewModels;

public sealed class ProbeMetricCardViewModel : ObservableObject
{
    private const int HistoryCapacity = 24;
    private const double RingSize = 132d;
    private const double RingPadding = 12d;
    private const double SparklineWidth = 256d;
    private const double SparklineHeight = 54d;
    private const double SparklineHorizontalPadding = 6d;
    private const double SparklineVerticalPadding = 6d;

    private readonly List<double> _history = new();
    private string _title = string.Empty;
    private string _valueText = string.Empty;
    private string _captionText = string.Empty;
    private string _detailText = string.Empty;
    private string _scoreText = "0%";
    private string _centerValueText = "0";
    private double _scorePercent;
    private Geometry _ringGeometry = Geometry.Empty;
    private Geometry _trendGeometry = Geometry.Empty;

    public ProbeMetricCardViewModel(string id, string glyph, Brush accentBrush)
    {
        Id = id;
        Glyph = glyph;
        AccentBrush = accentBrush;
        AccentSurfaceBrush = CreateAlphaBrush(accentBrush, 0x26);
    }

    public string Id { get; }

    public string Glyph { get; }

    public Brush AccentBrush { get; }

    public Brush AccentSurfaceBrush { get; }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string ValueText
    {
        get => _valueText;
        private set => SetProperty(ref _valueText, value);
    }

    public string CaptionText
    {
        get => _captionText;
        private set => SetProperty(ref _captionText, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    public string ScoreText
    {
        get => _scoreText;
        private set => SetProperty(ref _scoreText, value);
    }

    public string CenterValueText
    {
        get => _centerValueText;
        private set => SetProperty(ref _centerValueText, value);
    }

    public double ScorePercent
    {
        get => _scorePercent;
        private set => SetProperty(ref _scorePercent, value);
    }

    public Geometry RingGeometry
    {
        get => _ringGeometry;
        private set => SetProperty(ref _ringGeometry, value);
    }

    public Geometry TrendGeometry
    {
        get => _trendGeometry;
        private set => SetProperty(ref _trendGeometry, value);
    }

    public void Apply(
        string title,
        string valueText,
        string captionText,
        string detailText,
        double scorePercent)
    {
        Title = title;
        ValueText = valueText;
        CaptionText = captionText;
        DetailText = detailText;

        var clampedScore = Math.Clamp(scorePercent, 0d, 100d);
        ScorePercent = clampedScore;
        ScoreText = $"{clampedScore:0}%";
        CenterValueText = $"{clampedScore:0}";

        _history.Add(clampedScore);
        if (_history.Count > HistoryCapacity)
            _history.RemoveAt(0);

        RingGeometry = ProbeChartGeometryBuilder.CreateRingArcGeometry(
            clampedScore,
            RingSize,
            RingSize,
            RingPadding);
        TrendGeometry = ProbeChartGeometryBuilder.CreateSparklineGeometry(
            _history,
            SparklineWidth,
            SparklineHeight,
            SparklineHorizontalPadding,
            SparklineVerticalPadding);
    }

    public void Reset()
    {
        _history.Clear();
        ValueText = "--";
        CaptionText = string.Empty;
        DetailText = string.Empty;
        ScoreText = "0%";
        CenterValueText = "0";
        ScorePercent = 0d;
        RingGeometry = Geometry.Empty;
        TrendGeometry = Geometry.Empty;
    }

    private static Brush CreateAlphaBrush(Brush sourceBrush, byte alpha)
    {
        if (sourceBrush is SolidColorBrush solidColorBrush)
        {
            var color = solidColorBrush.Color;
            var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }

        return sourceBrush.Clone();
    }
}
