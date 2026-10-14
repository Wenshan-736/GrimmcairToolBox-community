using System.IO;
using System.Windows;
using System.Windows.Controls;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop.Views;

public partial class AiSettingsView : UserControl
{
    private AiChatStore? _store;
    private AiConnection? _tested;
    private CancellationTokenSource? _cancellation;
    private Task _work = Task.CompletedTask;
    private bool _loading, _closed;
    internal AiConnectionTester ConnectionTester { get; set; } = new();
    public event EventHandler? ConnectionSaved;
    public AiSettingsView() => InitializeComponent();

    public void Initialize(AiChatStore store)
    {
        _store = store;
        try
        {
            var connection = store.LoadConnection();
            if (connection is null) return;
            _loading = true;
            UrlBox.Text = connection.BaseUrl;
            UpdateProviderNote();
            KeyBox.Password = connection.ApiKey;
            ModelBox.ItemsSource = new[] { connection.Model };
            ModelBox.SelectedIndex = 0;
            Status.Text = "已读取保存的连接。修改地址、密钥或模型后需要重新测试。";
        }
        catch { Status.Text = "AI 设置暂时无法读取，原文件已保留。请检查当前 Windows 用户与文件权限。"; }
        finally { _loading = false; }
    }

    private AiConnection ReadConnection(bool requireModel = true)
    {
        var url = AiChatClient.NormalizeBaseUrl(UrlBox.Text.Trim());
        var key = KeyBox.Password.Trim();
        var model = ModelBox.SelectedItem as string ?? "";
        if (key.Length == 0) throw new InvalidOperationException("请填写 API 密钥。");
        if (requireModel && model.Length == 0) throw new InvalidOperationException("请先获取并选择模型。");
        return new AiConnection(url, key, model);
    }

    private void EditorChanged(object sender, RoutedEventArgs e)
    {
        if (ProviderNote is not null) UpdateProviderNote();
        if (_loading || SaveButton is null) return;
        _tested = null;
        SaveButton.IsEnabled = false;
        Status.Text = "配置已修改，请测试成功后保存。";
        // A model list belongs to one URL/key pair.
        if (ReferenceEquals(sender, UrlBox) || ReferenceEquals(sender, KeyBox))
        {
            _loading = true;
            ModelBox.ItemsSource = null;
            _loading = false;
        }
    }

    private void UpdateProviderNote()
    {
        ProviderNote.Visibility = AiChatClient.CanControlThinking(UrlBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Models_Click(object sender, RoutedEventArgs e)
    {
        if (!_work.IsCompleted || _closed) return;
        _work = WithBusyAsync(async token =>
        {
            var connection = ReadConnection(false);
            Status.Text = "正在获取可用模型……";
            using var client = new AiChatClient();
            var models = await client.ListModelsAsync(connection, token);
            ModelBox.ItemsSource = models;
            ModelBox.SelectedIndex = models.Count > 0 ? 0 : -1;
            Status.Text = models.Count > 0 ? $"已获取 {models.Count} 个模型。请选择模型，再测试连接。" : "服务未返回可用模型。";
        });
        await _work;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!_work.IsCompleted || _closed) return;
        _tested = null;
        _work = WithBusyAsync(async token =>
        {
            var connection = ReadConnection();
            var result = await ConnectionTester.TestAsync(connection, status => Dispatcher.Invoke(() => Status.Text = status), token);
            _tested = result.Connection;
            Status.Text = result.Status;
        });
        await _work;
    }

    private async Task WithBusyAsync(Func<CancellationToken, Task> work)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _cancellation = cancellation;
        Editor.IsEnabled = false;
        TestButton.IsEnabled = SaveButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        try { await work(cancellation.Token); }
        catch (OperationCanceledException) { Status.Text = "测试已停止或超时，未保存修改。"; }
        catch (Exception error) { Status.Text = AiChatView.FriendlyError(error); }
        finally
        {
            _cancellation = null;
            Editor.IsEnabled = !_closed;
            TestButton.IsEnabled = !_closed;
            SaveButton.IsEnabled = !_closed && _tested is not null;
            CancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var current = ReadConnection();
            if (_store is null || _tested is null || (_tested with { ThinkingVerified = false }) != current)
                throw new InvalidOperationException("请先测试当前配置。");
            _store.SaveConnection(_tested);
            Status.Text = "AI 设置已保存，可以前往左侧「AI 对话」开始聊天。";
            ConnectionSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error) { Status.Text = AiChatView.FriendlyError(error); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
    public void ResumeAfterCloseFailure() { _closed = false; Editor.IsEnabled = TestButton.IsEnabled = true; }
    public async Task StopAsync()
    {
        _closed = true;
        _cancellation?.Cancel();
        await _work;
        KeyBox.Clear();
    }
}
