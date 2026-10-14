using System.Windows;

namespace Grimmcair.Desktop.Views;

public partial class DesktopConsentWindow : Window
{
    public DesktopConsentWindow(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        InitializeComponent();
        ServerMessage.Text = message;
    }

    internal string MessageText => ServerMessage.Text;

    // The caller submits the current server version only after an explicit true result.
    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
