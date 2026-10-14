using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Grimmcair.Desktop.Services;
using Microsoft.Win32;

namespace Grimmcair.Desktop.Views;

public partial class KnowledgeSettingsView : UserControl
{
    public KnowledgeSettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        var current = ResourcePack.Current;
        CurrentSummary.Text = $"{current.MapVersion} · 修订 {current.Revision}";
        CurrentSource.Text = current.Source == "embedded" ? "当前使用随程序提供的内置资料。" : "当前资料包：" + current.PackagePath;
        if (string.IsNullOrEmpty(SelectionStatus.Text))
            SelectionStatus.Text = KnowledgePackSelection.StartupWarning ?? "新资料包校验通过后会记住选择，下次启动生效。当前对话与桌面资料保持同一版本。";
    }

    private async void Choose_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择公开知识库资料包", Filter = "知识库资料包 (*.zip)|*.zip", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        ChooseButton.IsEnabled = false;
        SelectionStatus.Text = "正在校验资料版本、完整性与配图……";
        try
        {
            var info = await Task.Run(() => KnowledgePackSelection.SelectForNextStart(dialog.FileName));
            SelectionStatus.Text = $"已通过校验：{info.Revision}。下次启动后使用；当前窗口继续使用原资料。";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        { SelectionStatus.Text = "未更换资料包：" + error.Message; }
        finally { ChooseButton.IsEnabled = true; }
    }

    private void Builtin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            KnowledgePackSelection.SelectBuiltinForNextStart();
            SelectionStatus.Text = "已记住选择，下次启动使用内置资料；当前窗口中的资料不会变动。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { SelectionStatus.Text = "未能保存选择：" + error.Message; }
    }

    private void Images_Click(object sender, RoutedEventArgs e) => KnowledgeImageViewer.ShowGallery(Window.GetWindow(this));

    private void CopyMcp_Click(object sender, RoutedEventArgs e)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Grimmcair.KnowledgeMcp.exe");
        if (!File.Exists(executable))
        {
            SelectionStatus.Text = "此目录尚未提供独立 MCP 启动器，请使用包含 Grimmcair.KnowledgeMcp.exe 的预览目录。";
            return;
        }
        try
        {
            Clipboard.SetText(CreateMcpConfiguration(executable, ResourcePack.Current,
                Environment.GetEnvironmentVariable("DOTNET_ROOT")));
            SelectionStatus.Text = "MCP 配置已复制，可粘贴到支持 stdio MCP 的客户端。可以单独查询公开资料，无需打开工具箱或登录。";
        }
        catch (System.Runtime.InteropServices.COMException) { SelectionStatus.Text = "剪贴板暂时不可用，请稍后重试。"; }
    }

    internal static string CreateMcpConfiguration(string executable, ResourcePackInfo current, string? runtime)
    {
        var arguments = current.PackagePath is { } path
            ? new[] { "--data-package", path, "--data-package-sha256", current.PackageSha256 }
            : new[] { "--builtin-knowledge" };
        var service = new Dictionary<string, object> { ["command"] = executable, ["args"] = arguments };
        // Development previews use the repository's runtime. A self-contained delivery
        // does not need or include this entry.
        if (!string.IsNullOrWhiteSpace(runtime) && Directory.Exists(runtime))
            service["env"] = new Dictionary<string, string> { ["DOTNET_ROOT"] = runtime, ["DOTNET_ROOT_X64"] = runtime };
        // Keep the configuration ASCII-only, including paths containing Chinese;
        // JSON Unicode escapes preserve the original path when a client reads it.
        return JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object> { ["grimmcair"] = service } },
            new JsonSerializerOptions { WriteIndented = true });
    }
}
