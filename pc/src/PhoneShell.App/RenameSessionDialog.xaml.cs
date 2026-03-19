using System.Windows;

namespace PhoneShell;

public partial class RenameSessionDialog : Window
{
    public string SessionTitle { get; private set; }

    public RenameSessionDialog(string currentTitle)
    {
        InitializeComponent();
        SessionTitle = currentTitle ?? string.Empty;
        TitleBox.Text = SessionTitle;
        TitleBox.SelectAll();
        TitleBox.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var value = TitleBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            TitleBox.Focus();
            return;
        }

        SessionTitle = value;
        DialogResult = true;
    }
}
