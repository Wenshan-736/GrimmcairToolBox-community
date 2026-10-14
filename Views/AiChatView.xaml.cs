using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop.Views;

public partial class AiChatView : UserControl
{
    private AiChatStore? _store;
    private AiConnection? _connection;
    private AiConversation _current = new();
    private bool _selecting, _busy, _closed, _hasUnsaved, _updatingThinking;
    private CancellationTokenSource? _cancellation;
    private Task _work = Task.CompletedTask;
    private readonly Dictionary<AiStoredMessage, StackPanel> _imageGalleries = [];
    public event EventHandler? SettingsRequested;
    public AiChatView() => InitializeComponent();

    public void Initialize(AiChatStore store)
    {
        _store = store;
        ReloadConnection();
        ReloadHistory(selectLatest: true);
        RenderMessages();
    }

    public void ReloadConnection()
    {
        try { _connection = _store?.LoadConnection(); }
        catch { _connection = null; Status.Text = "AI 设置无法读取，请在设置中重新连接。"; }
        ConfigureBanner.Visibility = _connection is null ? Visibility.Visible : Visibility.Collapsed;
        RefreshButtons();
    }

    private void ReloadHistory(bool selectLatest = false)
    {
        if (_store is null) return;
        _selecting = true;
        try
        {
            var history = _store.ListConversations().OrderByDescending(x => x.UpdatedUtc).ToList();
            if (selectLatest && history.Count > 0) _current = history[0];
            HistoryList.ItemsSource = history;
            HistoryList.SelectedItem = history.FirstOrDefault(x => x.Id == _current.Id);
            if (_store.LastReadErrors.Count > 0) Status.Text = "部分旧对话无法读取，原文件已保留。其他对话可以继续查看。";
        }
        catch (Exception error) { Status.Text = FriendlyError(error); }
        finally { _selecting = false; }
        ConversationTitle.Text = _current.Title;
    }

    private void RenderMessages()
    {
        MessageStack.Children.Clear();
        _imageGalleries.Clear();
        foreach (var message in _current.Messages)
        {
            AddMessage(message, out var source);
            source.Text = DescribeSource(message);
        }
        Welcome.Visibility = _current.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MessagesScroll.Visibility = _current.Messages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ConversationTitle.Text = _current.Title;
        MessagesScroll.ScrollToEnd();
        RefreshButtons();
    }

    private AiMessageBody AddMessage(AiStoredMessage message, out TextBlock sources)
    {
        var user = message.Role == "user";
        var stack = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var copy = new Button { Content = user ? "复制" : "复制 Markdown", FontSize = 11, Padding = new Thickness(8, 2, 8, 2),
            MinHeight = 24, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Right };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(message.Content); Status.Text = "已复制消息原文。"; }
            catch { Status.Text = "暂时无法写入剪贴板，请选中文字后复制。"; }
        };
        DockPanel.SetDock(copy, Dock.Right); header.Children.Add(copy);
        header.Children.Add(new TextBlock { Text = user ? "你" : "知识库助手", FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("MutedBrush"), VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(header);
        var body = new AiMessageBody(markdown: !user);
        body.SetContent(message.Content, immediate: true);
        System.Windows.Automation.AutomationProperties.SetName(body, user ? "用户消息" : "AI 回答");
        stack.Children.Add(body);
        if (!user)
        {
            var gallery = new StackPanel();
            _imageGalleries[message] = gallery;
            stack.Children.Add(gallery);
            RefreshImages(message);
        }
        sources = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 9, 0, 0) };
        stack.Children.Add(sources);
        MessageStack.Children.Add(new Border { Child = stack, Padding = new Thickness(17, 14, 17, 12), CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(user ? Color.FromRgb(238, 224, 194) : Color.FromRgb(255, 250, 239)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(225, 210, 180)), BorderThickness = new Thickness(1),
            Margin = new Thickness(user ? 38 : 0, 0, user ? 0 : 18, 15) });
        return body;
    }

    private void RefreshImages(AiStoredMessage message)
    {
        if (!_imageGalleries.TryGetValue(message, out var gallery)) return;
        gallery.Children.Clear();
        foreach (var reference in message.Images.Take(KnowledgeImageCatalog.MaximumMessageImages))
            gallery.Children.Add(KnowledgeImageViewer.CreateCard(reference));
    }

    private static string DescribeSource(AiStoredMessage message)
    {
        if (message.Role == "user") return "";
        var state = message.Status switch { "streaming" => "回答尚未完成", "stopped" => "已停止生成", "error" => "本次回答未完成", _ => "" };
        var citations = message.Sources.Count > 0 ? "本地资料 · " + string.Join(" / ", message.Sources) : "";
        var thinking = message.Status == "complete" && message.ContextMessages.Any(item => !string.IsNullOrEmpty(item.ReasoningContent)) ? "已完成思考" : "";
        return string.Join(" · ", new[] { state, thinking, citations }.Where(x => x.Length > 0));
    }

    private void RefreshButtons()
    {
        if (SendButton is null) return;
        var modeReady = !_current.ThinkingEnabled || _connection?.ThinkingVerified == true;
        SendButton.IsEnabled = !_closed && !_busy && _connection is not null && modeReady && !string.IsNullOrWhiteSpace(Composer.Text);
        NewButton.IsEnabled = HistoryList.IsEnabled = !_busy && !_closed && !_hasUnsaved;
        DeleteButton.IsEnabled = !_busy && !_closed && !_hasUnsaved && _current.Revision > 0;
        Composer.IsReadOnly = _busy || _closed;
        StopButton.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        RefreshThinkingControl();
    }

    private void RefreshThinkingControl()
    {
        if (ConversationThinkingBox is null || ThinkingNote is null || ModelLabel is null) return;
        _updatingThinking = true;
        try { ConversationThinkingBox.IsChecked = _current.ThinkingEnabled; }
        finally { _updatingThinking = false; }
        var verified = _connection?.ThinkingVerified == true;
        ConversationThinkingBox.IsEnabled = !_busy && !_closed && !_hasUnsaved && _store is not null
            && (verified || _current.ThinkingEnabled);
        ThinkingNote.Text = verified ? (_current.ThinkingEnabled ? "仅当前对话 · 下一条消息使用深度思考" : "仅当前对话 · 普通回答")
            : _current.ThinkingEnabled ? "当前模型尚未验证此能力，请测试连接或关闭深度思考。"
            : _connection is null ? "连接模型后可按对话选择。"
            : AiChatClient.CanControlThinking(_connection) ? "先在连接设置中测试深度思考能力。" : "此接口暂未接入手动思考开关。";
        ModelLabel.Text = _connection is null ? "尚未连接模型" : _connection.Model + (_current.ThinkingEnabled ? " · 深度思考" : "");
    }

    private void ConversationThinking_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingThinking || _busy || _closed || _hasUnsaved || _store is null) return;
        var enabled = ConversationThinkingBox.IsChecked == true;
        if (enabled == _current.ThinkingEnabled) return;
        if (enabled && _connection?.ThinkingVerified != true)
        {
            RefreshThinkingControl();
            Status.Text = "请先在连接设置中测试当前模型的深度思考能力。";
            return;
        }
        var previous = _current.ThinkingEnabled;
        try
        {
            _current.ThinkingEnabled = enabled;
            _store.SaveConversation(_current);
            ReloadHistory();
            Status.Text = enabled ? "已为当前对话开启深度思考，下次发送时生效。" : "当前对话已切换为普通回答。";
        }
        catch (Exception error)
        {
            _current.ThinkingEnabled = previous;
            Status.Text = "此对话的模式未能保存，已保留原选择。" + FriendlyError(error);
        }
        RefreshButtons();
    }

    private AiConnection CreateRequestConnection()
    {
        if (_connection is null) throw new InvalidOperationException("请先连接模型。");
        if (_current.ThinkingEnabled && !_connection.ThinkingVerified)
            throw new InvalidOperationException("当前模型的深度思考能力尚未验证，请测试连接或关闭此对话的深度思考。");
        return _connection with { ThinkingEnabled = _current.ThinkingEnabled };
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Composer.Text = (sender as Button)?.Tag?.ToString() ?? "";
        Composer.Focus(); Composer.CaretIndex = Composer.Text.Length;
    }
    private void Composer_Changed(object sender, TextChangedEventArgs e) => RefreshButtons();
    private void Composer_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        if (SendButton.IsEnabled) Send_Click(sender, e);
    }
    private void History_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_selecting || _busy || HistoryList.SelectedItem is not AiConversation conversation) return;
        _current = conversation;
        RenderMessages();
        Status.Text = "已读取本机对话，可以继续提问。";
    }
    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closed || _store is null) return;
        try
        {
            var conversation = new AiConversation();
            _store.SaveConversation(conversation);
            _current = conversation;
            Composer.Clear(); ReloadHistory(); RenderMessages();
            Status.Text = "新对话已建立。";
        }
        catch (Exception error) { Status.Text = FriendlyError(error); }
    }
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closed || _store is null || _current.Revision == 0) return;
        if (MessageBox.Show(Window.GetWindow(this), $"删除「{_current.Title}」及其本机聊天记录？", "删除对话", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            _store.DeleteConversation(_current.Id);
            _current = new AiConversation(); ReloadHistory(selectLatest: true); RenderMessages();
            Status.Text = "对话已从本机删除。";
        }
        catch (Exception error) { Status.Text = FriendlyError(error); }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closed || _connection is null || _store is null || string.IsNullOrWhiteSpace(Composer.Text)) return;
        AiConnection requestConnection;
        try { requestConnection = CreateRequestConnection(); }
        catch (Exception error) { Status.Text = FriendlyError(error); return; }
        _busy = true;
        _work = SendCoreAsync(requestConnection, Composer.Text.Trim());
        await _work;
    }

    private async Task SendCoreAsync(AiConnection connection, string question)
    {
        var conversation = _current;
        var response = new AiStoredMessage { Role = "assistant", Status = "streaming" };
        var questionMessage = new AiStoredMessage { Role = "user", Content = question };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _cancellation = cancellation;
        TextBlock? sources = null;
        AiMessageBody? body = null;
        var started = false;
        var originalTitle = conversation.Title;
        try
        {
            if (conversation.Messages.Count == 0) conversation.Title = question.Length > 22 ? question[..22] + "…" : question;
            conversation.Messages.Add(questionMessage);
            var context = AiChatWorkflow.BuildContext(conversation.Messages, out var limited, connection);
            conversation.Messages.Add(response);
            _store!.SaveConversation(conversation);
            started = true;
            _hasUnsaved = false;
            Composer.Clear();
            MessageStack.Children.Clear();
            _imageGalleries.Clear();
            foreach (var message in conversation.Messages.Take(conversation.Messages.Count - 1))
            { AddMessage(message, out var previousSources); previousSources.Text = DescribeSource(message); }
            Welcome.Visibility = Visibility.Collapsed;
            MessagesScroll.Visibility = Visibility.Visible;
            body = AddMessage(response, out sources);
            var followOutput = true;
            body.DocumentUpdating += (_, _) => followOutput = MessagesScroll.ScrollableHeight - MessagesScroll.VerticalOffset < 70;
            body.DocumentUpdated += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => { if (followOutput) MessagesScroll.ScrollToEnd(); }));
            RefreshButtons(); ReloadHistory();
            var saveClock = Stopwatch.StartNew();
            var thinkingClock = new Stopwatch();
            var imageCatalog = KnowledgeImageCatalog.Load();
            var protocol = await new AiChatWorkflow().RunAsync(connection, context,
                delta => Dispatcher.Invoke(() =>
                {
                    var follow = MessagesScroll.ScrollableHeight - MessagesScroll.VerticalOffset < 70;
                    response.Content += delta; body.SetContent(response.Content);
                    if (follow) MessagesScroll.ScrollToEnd();
                    if (saveClock.ElapsedMilliseconds >= 2000)
                    { _store.SaveConversation(conversation); saveClock.Restart(); }
                }),
                status => Dispatcher.Invoke(() => Status.Text = status + (limited ? " · 本次仅携带最近对话，完整记录仍在本机" : "")),
                source => Dispatcher.Invoke(() => { if (!response.Sources.Contains(source)) response.Sources.Add(source); sources.Text = DescribeSource(response); }), cancellation.Token,
                onReasoning: _ => Dispatcher.Invoke(() =>
                {
                    if (!thinkingClock.IsRunning) thinkingClock.Start();
                    Status.Text = $"模型正在深度思考……已用 {Math.Max(1, (int)thinkingClock.Elapsed.TotalSeconds)} 秒";
                }), onKnowledgeResult: (tool, result) => Dispatcher.Invoke(() =>
                {
                    var references = imageCatalog.ReferencesFromToolResult(tool, result);
                    var added = false;
                    foreach (var reference in references)
                        if (response.Images.Count < KnowledgeImageCatalog.MaximumMessageImages && !response.Images.Any(existing =>
                            existing.ImageId == reference.ImageId && existing.KnowledgeRevision == reference.KnowledgeRevision))
                        { response.Images.Add(reference); added = true; }
                    if (added) RefreshImages(response);
                }));
            response.ContextOwner = AiChatWorkflow.GetContextOwner(connection);
            response.ContextMessages = protocol.ToList();
            response.Status = "complete";
        }
        catch (OperationCanceledException)
        {
            response.Status = "stopped";
            Status.Text = "已停止生成，收到的内容已保留。";
        }
        catch (Exception error)
        {
            response.Status = "error";
            Status.Text = FriendlyError(error);
        }
        finally
        {
            body?.Flush();
            if (started)
            {
                try
                {
                    _store!.SaveConversation(conversation); _hasUnsaved = false;
                    if (response.Status == "complete") Status.Text = "回答已完成 · 已保存在本机";
                }
                catch { _hasUnsaved = true; Status.Text = "本次记录未能保存。请先复制需要的回答，并检查磁盘空间或文件权限。"; }
            }
            else if (conversation.Messages.Contains(questionMessage))
            {
                // Keep the user's draft when the initial local write fails.
                Composer.Text = question;
                conversation.Messages.Remove(questionMessage);
                conversation.Messages.Remove(response);
                conversation.Title = originalTitle;
            }
            if (sources is not null) sources.Text = DescribeSource(response);
            _cancellation = null; _busy = false;
            ReloadHistory(); RefreshButtons();
        }
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
    public void ResumeAfterCloseFailure() { _closed = false; RefreshButtons(); }
    public void DiscardUnsavedForClose() { _hasUnsaved = false; _closed = true; }
    public async Task StopAsync()
    {
        _closed = true; _cancellation?.Cancel();
        await _work;
        if (_hasUnsaved)
        {
            try { _store!.SaveConversation(_current); _hasUnsaved = false; }
            catch
            {
                _closed = false; RefreshButtons();
                throw new IOException("AI 对话尚未保存，请复制回答或恢复文件写入后再关闭。");
            }
        }
    }
    internal static string FriendlyError(Exception error) => error switch
    {
        AiChatException => error.Message,
        InvalidOperationException => error.Message,
        ArgumentException => "请检查 API 地址、密钥和所选模型。",
        IOException or UnauthorizedAccessException => "本地记录或知识库暂时不可用，请检查文件权限和磁盘空间。",
        _ => "这次请求未能完成，请检查连接设置后重试。"
    };
}
