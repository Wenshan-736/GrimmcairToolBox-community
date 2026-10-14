using System.IO;
using System.Windows;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop;

public partial class MainWindow
{
    private void InitializeAiChat()
    {
        var store = new AiChatStore(Path.Combine(Path.GetDirectoryName(_simulationPath)!, "ai"));
        AiChatPage.Initialize(store);
        SettingsPage.AiSettings.Initialize(store);
        SettingsPage.AiSettings.ConnectionSaved += (_, _) => AiChatPage.ReloadConnection();
        AiChatPage.SettingsRequested += (_, _) => MainPages.SelectedIndex = 4;
    }

    private void AiNav_Click(object sender, RoutedEventArgs e) => OpenAiChat();
    public void OpenAiChat()
    {
        MainPages.SelectedIndex = 6;
        if (App.AiPreviewMode)
        {
            Title = "格林姆迦尔的妙妙工具箱 · AI 对话预览";
            StatusText.Text = "AI 对话预览 · 独立本机记录 · 桌面已收录资料";
            DesktopAccountPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async Task StopAiChatAsync()
    {
        await AiChatPage.StopAsync();
        await SettingsPage.AiSettings.StopAsync();
    }
}
