using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Title = "关于" + DesktopEdition.WindowTitle;
        AboutTitleText.Text = DesktopEdition.WindowTitle;
        AboutVersionText.Text = "应用 0.1.8 · 地图 TWRPG 0.94a";
    }
    private void Reference_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, "无法打开浏览器，请复制仓库地址后访问。", "参考与致谢", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
