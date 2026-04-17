using System.Collections.ObjectModel;
using System.Windows.Media;
using PhoneShell.Utilities;

namespace PhoneShell.ViewModels;

public sealed class ProbeProcessDetailCardViewModel : ObservableObject
{
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _emptyText = string.Empty;
    private bool _hasItems;
    private bool _showSortToggle;
    private string _primarySortText = string.Empty;
    private string _secondarySortText = string.Empty;
    private bool _isPrimarySortSelected;
    private bool _isSecondarySortSelected;

    public ProbeProcessDetailCardViewModel(string id, string glyph, Brush accentBrush)
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

    public ObservableCollection<ProbeProcessDetailItemViewModel> Items { get; } = new();

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        private set => SetProperty(ref _subtitle, value);
    }

    public string EmptyText
    {
        get => _emptyText;
        private set => SetProperty(ref _emptyText, value);
    }

    public bool HasItems
    {
        get => _hasItems;
        private set => SetProperty(ref _hasItems, value);
    }

    public bool ShowSortToggle
    {
        get => _showSortToggle;
        private set => SetProperty(ref _showSortToggle, value);
    }

    public string PrimarySortText
    {
        get => _primarySortText;
        private set => SetProperty(ref _primarySortText, value);
    }

    public string SecondarySortText
    {
        get => _secondarySortText;
        private set => SetProperty(ref _secondarySortText, value);
    }

    public bool IsPrimarySortSelected
    {
        get => _isPrimarySortSelected;
        private set => SetProperty(ref _isPrimarySortSelected, value);
    }

    public bool IsSecondarySortSelected
    {
        get => _isSecondarySortSelected;
        private set => SetProperty(ref _isSecondarySortSelected, value);
    }

    public void Apply(string title, string subtitle, string emptyText, IEnumerable<ProbeProcessDetailItemViewModel> items)
    {
        Title = title;
        Subtitle = subtitle;
        EmptyText = emptyText;

        Items.Clear();
        foreach (var item in items)
            Items.Add(item);

        HasItems = Items.Count > 0;
    }

    public void Reset(string title, string subtitle, string emptyText)
    {
        Title = title;
        Subtitle = subtitle;
        EmptyText = emptyText;
        Items.Clear();
        HasItems = false;
    }

    public void HideSortToggle()
    {
        ShowSortToggle = false;
        PrimarySortText = string.Empty;
        SecondarySortText = string.Empty;
        IsPrimarySortSelected = false;
        IsSecondarySortSelected = false;
    }

    public void ConfigureSortToggle(string primarySortText, string secondarySortText, bool isPrimarySelected)
    {
        ShowSortToggle = true;
        PrimarySortText = primarySortText;
        SecondarySortText = secondarySortText;
        IsPrimarySortSelected = isPrimarySelected;
        IsSecondarySortSelected = !isPrimarySelected;
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
