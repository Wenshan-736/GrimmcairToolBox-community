using System.Windows;

namespace Grimmcair.Desktop.Views;

public partial class CloudSyncNoticeWindow : Window
{
    public CloudSyncNoticeWindow() => InitializeComponent();
    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
