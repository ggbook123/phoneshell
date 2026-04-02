using System.Windows;
using System.Windows.Input;

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
}
