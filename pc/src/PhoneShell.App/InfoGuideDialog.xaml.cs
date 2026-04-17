using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;

namespace PhoneShell;

public partial class InfoGuideDialog : Window
{
    public InfoGuideDialog()
    {
        InitializeComponent();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore browser launch failures.
        }

        e.Handled = true;
    }
}
