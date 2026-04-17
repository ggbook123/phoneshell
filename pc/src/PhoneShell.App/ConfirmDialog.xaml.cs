using System.Windows;
using System.Windows.Input;

namespace PhoneShell;

public enum ConfirmDialogAction
{
    Cancel,
    Alternate,
    Confirm
}

public partial class ConfirmDialog : Window
{
    public ConfirmDialogAction SelectedAction { get; private set; } = ConfirmDialogAction.Cancel;

    public ConfirmDialog(
        string titleText,
        string messageText,
        string confirmText,
        string cancelText,
        string? alternateText = null,
        bool destructive = false)
    {
        InitializeComponent();

        Title = titleText;
        HeaderTextBlock.Text = titleText;
        MessageTextBlock.Text = messageText;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
        ConfigureAlternateButton(alternateText);

        var confirmStyleKey = destructive ? "DangerButton" : "AccentButton";
        if (FindResource(confirmStyleKey) is Style confirmStyle)
            ConfirmButton.Style = confirmStyle;
    }

    private void ConfigureAlternateButton(string? alternateText)
    {
        if (string.IsNullOrWhiteSpace(alternateText))
        {
            AlternateButton.Visibility = Visibility.Collapsed;
            return;
        }

        AlternateButton.Content = alternateText;
        AlternateButton.Visibility = Visibility.Visible;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = ConfirmDialogAction.Cancel;
        DialogResult = false;
    }

    private void AlternateButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = ConfirmDialogAction.Alternate;
        DialogResult = false;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = ConfirmDialogAction.Confirm;
        DialogResult = true;
    }
}
