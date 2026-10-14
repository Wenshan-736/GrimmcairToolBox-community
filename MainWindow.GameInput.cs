using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop;

public partial class MainWindow
{
    private DesktopChatHotkeys? _gameHotkeys;
    private CancellationTokenSource? _chatInputCancellation;
    private Task _chatInputTask = Task.CompletedTask;
    private bool _chatInputRunning;
    private void InitializeGameHotkeys()
    {
        _gameHotkeys = new DesktopChatHotkeys(this, StartGameCommand);
    }
    private void StartGameCommand(bool load)
    {
        if (_chatInputRunning || _closed || _closing || !DesktopFeaturesAllowed) return;
        _chatInputTask = SendGameCommandAsync(load);
    }
    private async Task StopGameInputAsync()
    {
        _gameHotkeys?.Suspend();
        _chatInputCancellation?.Cancel();
        // Keep the dispatcher alive until the transport restores our clipboard and releases resources.
        await _chatInputTask;
    }
    private void ConfigureGameHotkeys(LocalSettings settings)
    {
        _chatInputCancellation?.Cancel();
        if (_chatInputRunning || _closing || _closed) return;
        var error = _gameHotkeys?.Configure(settings.LoadHotkey, settings.SaveHotkey);
        if (error is not null) { StatusText.Text = error; ShowDesktopNotice("快捷键未启用", error); }
    }
    private void DisposeGameHotkeys()
    { _chatInputCancellation?.Cancel(); _gameHotkeys?.Dispose(); _gameHotkeys = null; }

    private IReadOnlyList<string> SelectedGameCommands(bool load)
    {
        var doc = SaveList.SelectedItem as SavedDocument;
        if (!load) return GameChatCommands.Save(doc?.SaveName, doc?.Result["kind"]?.ToString() == "account");
        if (doc is null || doc.IsDemo) throw new InvalidOperationException("请先在我的存档中选择要下档的存档。");
        if (FindLinkedSave(doc) is { IsDemo: false } linked && linked.ParsedResult is not null)
            return GameChatCommands.Load(linked.LoadCodes);
        var source = SaveInput.ReadGameExport(doc.SourcePath);
        if (source.Sha256 != doc.Hash) throw new InvalidOperationException("存档来源已更新，请等待读取完成后再下档。");
        return GameChatCommands.Load(source.Groups.Select(g => $"-{g.Command} {g.Payload}"));
    }
    private async Task SendGameCommandAsync(bool load)
    {
        if (_chatInputRunning || _closed || _closing) return;
        _chatInputRunning = true;
        _chatInputCancellation = new CancellationTokenSource();
        try
        {
            var lines = SelectedGameCommands(load);
            using var input = new WindowsGameChatTransport(new WindowInteropHelper(this).Handle);
            _gameHotkeys?.Suspend();
            _screenshotHotkey?.Configure(null);
            var hotkey = load ? SettingsPage.CurrentSettings.LoadHotkey : SettingsPage.CurrentSettings.SaveHotkey;
            await WindowsGameChatTransport.WaitForHotkeyReleaseAsync(hotkey, _chatInputCancellation.Token);
            var count = await GameChatSequence.SendAsync(lines, input, _chatInputCancellation.Token);
            if (!_closed && !_closing)
            {
                StatusText.Text = load ? $"已发送 {count} 段载入输入，请查看游戏中的结果。" : "保存命令已发送，请查看游戏中的结果。";
                ShowDesktopNotice(load ? "载入输入已发送" : "保存命令已发送", "请查看游戏中的结果。");
            }
        }
        catch (OperationCanceledException) { if (!_closed) StatusText.Text = "本次游戏输入已停止。"; }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or Win32Exception or ExternalException)
        {
            if (!_closed) { StatusText.Text = error.Message; ShowDesktopNotice("游戏输入未完成", error.Message); }
        }
        finally
        {
            _chatInputRunning = false;
            _chatInputCancellation?.Dispose(); _chatInputCancellation = null;
            if (!_closed && !_closing)
            {
                var settings = SettingsPage.CurrentSettings;
                var screenshotError = _screenshotHotkey?.Configure(settings.ScreenshotHotkey);
                if (screenshotError is not null) StatusText.Text = screenshotError;
                ConfigureGameHotkeys(settings);
            }
        }
    }
}
