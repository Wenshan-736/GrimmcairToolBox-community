using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop;

public partial class MainWindow
{
    private SaveArchiveService? _archives;
    private ReplayArchiveCoordinator? _replays;
    private DesktopScreenshotHotkey? _screenshotHotkey;
    private ArchiveNotificationService? _archiveNotifications;
    private DispatcherTimer? _replayTimer;
    private readonly SemaphoreSlim _archiveConfigurationGate = new(1, 1);
    private readonly object _archiveEventGate = new();
    private EventHandler<SaveArchiveScanResult>? _archiveScanHandler;
    private int _archiveGeneration;
    private bool _replayTickRunning, _closed, _closing, _closeReady;

    private void InitializeWindowLifetime()
    {
        Closed += (_, _) =>
        {
            _closed = true; _replayTimer?.Stop(); _screenshotHotkey?.Dispose(); DisposeGameHotkeys(); _archiveNotifications?.Dispose(); _archives?.Dispose();
        };
        Closing += async (_, e) =>
        {
            if (_closeReady) return;
            e.Cancel = true;
            if (SettingsPage.IsDataImportRunning)
            { StatusText.Text = "旧数据正在复制和校验，请等待完成后再关闭窗口。"; return; }
            if (_closing) return;
            _closing = true;
            DesktopDiagnostics.Mark(DesktopDiagnosticStage.ShutdownRequested);
            try { await StopAiChatAsync(); }
            catch (IOException error)
            {
                MainPages.SelectedIndex = 6; StatusText.Text = error.Message;
                if (MessageBox.Show(this,
                    "AI 对话仍未能保存，可能是磁盘写入失败或另一窗口修改了这份对话。\n\n选择“否”保留窗口并复制回答。只有确认已复制需要的内容后，才选择“是”放弃未保存部分并退出；已保存的记录保持原样。",
                    "放弃未保存部分并退出？", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                { _closing = false; return; }
                AiChatPage.DiscardUnsavedForClose();
                await SettingsPage.AiSettings.StopAsync();
            }
            _replayTimer?.Stop();
            await StopGameInputAsync();
            await _archiveConfigurationGate.WaitAsync();
            try { if (_archives is not null) await _archives.StopWatchingAsync(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                StatusText.Text = "关闭前归档未完成：" + error.Message; _closing = false; _replayTimer?.Start();
                AiChatPage.ResumeAfterCloseFailure(); SettingsPage.AiSettings.ResumeAfterCloseFailure();
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (_closed || _closing) return;
                    var settings = SettingsPage.CurrentSettings;
                    _screenshotHotkey?.Configure(settings.ScreenshotHotkey);
                    ConfigureGameHotkeys(settings);
                });
                return;
            }
            finally { _archiveConfigurationGate.Release(); }
            await StopDesktopApiAsync();
            _closeReady = true;
            _ = Dispatcher.BeginInvoke(Close);
        };
    }

    private async Task InitializeArchivesAsync()
    {
        if (_closed || _closing || _decoder is null || _simulation is null) return;
        var stateDirectory = Path.GetDirectoryName(_simulationPath)!;
        // The decoder owns a lazily populated object cache; archive reads have their own instance.
        var archiveDecoder = new SaveDecoder();
        _archives = new SaveArchiveService(Path.Combine(stateDirectory, "archive-manifest.json"),
            (bytes, name) => archiveDecoder.ParseBytesForImport(bytes, name));
        _archives.ShouldIgnoreSource = (path, account, parsed) => _simulation?.IsIgnoredSource(path, account, parsed) == true;
        _archives.SeedVerifiedSnapshots(_simulation.State.Saves);
        _archives.SourceRenamed += (_, e) =>
        {
            try { _simulation?.TrackIgnoredSourceRename(e.OldFullPath, e.FullPath); }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            { DesktopDiagnostics.ReportException(DesktopDiagnosticStage.LocalStateLoading, error); }
        };
        _archiveNotifications = new ArchiveNotificationService(Dispatcher);
        try
        {
            _replays = new ReplayArchiveCoordinator(Path.Combine(stateDirectory, "replay-pending.json"), _archives.ArchiveMedia);
            _replays.RemovePendingSources(_simulation.State.Saves.Where(s => s.IsIgnored)
                .SelectMany(s => s.IgnoredSourceKeys.Append(s.SourceKey)).ToHashSet(StringComparer.Ordinal));
            _screenshotHotkey = new DesktopScreenshotHotkey(this, CaptureCurrentSaveScreenshot);
            InitializeGameHotkeys();
            SettingsPage.SettingsSaved += async (_, e) => await ConfigureArchivesAsync(e.NewSettings);
            await ConfigureArchivesAsync(SettingsPage.CurrentSettings);
            if (_closed || _closing) return;
            _replayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _replayTimer.Tick += async (_, _) => await PollReplaysAsync();
            _replayTimer.Start();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        { StatusText.Text = "归档初始化失败，原始文件保持不变：" + error.Message; }

    }

    private async Task ConfigureArchivesAsync(LocalSettings settings)
    {
        await _archiveConfigurationGate.WaitAsync();
        try
        {
            if (_archives is null || _closed || _closing) return;
            // Drain old notifications with their original replay source before reconfiguring.
            await _archives.StopWatchingAsync();
            lock (_archiveEventGate)
            {
                if (_archiveScanHandler is not null) _archives.ScanCompleted -= _archiveScanHandler;
                var generation = ++_archiveGeneration;
                _replays?.Configure(settings);
                _archiveScanHandler = (_, result) =>
                {
                    lock (_archiveEventGate)
                    {
                        if (generation != _archiveGeneration) return;
                        // Persist the association before posting UI work, including while closing.
                        try
                        {
                            foreach (var entry in result.Changes.Where(e => e.Parsed["kind"]?.ToString() == "character"))
                                if (_simulation?.IsIgnoredSource(entry.SourcePath, entry.GameAccount, entry.Parsed) != true) _replays?.Queue(entry);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                        { result.Issues.Add("录像关联未能记录：" + error.Message); }
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (!_closed && generation == _archiveGeneration) ApplyArchiveScan(result, false);
                        });
                    }
                };
                _archives.ScanCompleted += _archiveScanHandler;
            }
            var hotkeyIssue = _screenshotHotkey?.Configure(settings.ScreenshotHotkey);
            ConfigureGameHotkeys(settings);
            var result = await _archives.ScanAsync(settings);
            if (_closed) return;
            ApplyArchiveScan(result, false);
            _archives.StartWatching(settings);
            if (hotkeyIssue is not null) StatusText.Text = hotkeyIssue;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { StatusText.Text = "归档设置未能启用：" + error.Message; }
        finally { _archiveConfigurationGate.Release(); }
    }

    private void ApplyArchiveScan(SaveArchiveScanResult result, bool live)
    {
        var selectedId = (SaveList.SelectedItem as SavedDocument)?.SimulationSaveId;
        var selectedKey = (SaveList.SelectedItem as SavedDocument)?.SourceKey;
        var issues = result.Issues.ToList();
        foreach (var entry in result.Entries)
        {
            if (_simulation?.IsIgnoredSource(entry.SourcePath, entry.GameAccount, entry.Parsed) == true) continue;
            try
            {
                _importSourcePaths.Add(Path.GetFullPath(entry.SourcePath));
                var accepted = _simulation?.State.Saves.FirstOrDefault(s => s.OwnerId == CloudSimulation.PreviewOwnerId
                    && !s.IsIgnored && !s.SourceDetached && s.SourceKey == entry.LogicalSourceKey
                    && s.AcceptedSourceHash == entry.SourceHash && s.ParsedResult is not null);
                if (accepted is not null)
                {
                    // The library already holds this accepted source (possibly with a later local/cloud restore).
                    // Avoid N whole-state JSON transactions on every startup/flush for unchanged files.
                    var cachedDocument = _saves.FirstOrDefault(s => s.SimulationSaveId == accepted.Id);
                    if (cachedDocument is not null) cachedDocument.ChangeSummary = entry.ChangeSummary;
                    continue;
                }
                var doc = AcceptParsedSave(entry.Parsed, string.IsNullOrEmpty(entry.ArchivePath) ? entry.SourcePath : entry.ArchivePath,
                    sourceKey: entry.LogicalSourceKey, saveName: entry.SaveName, changeSummary: entry.ChangeSummary);
                // Only live file changes join the game awaiting its future completed replay.
                if (live && entry.HasChanged && !entry.IsInitial && doc.Result["kind"]?.ToString() == "character")
                    _replays?.Queue(entry);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            { issues.Add($"{entry.SaveName}：{error.Message}"); }
        }
        RefreshSimulationContext();
        var selected = _saves.FirstOrDefault(s => selectedId is not null && s.SimulationSaveId == selectedId)
            ?? _saves.FirstOrDefault(s => selectedKey is not null && s.SourceKey == selectedKey)
            ?? SaveList.Items.Cast<SavedDocument>().FirstOrDefault();
        if (selected is not null) SelectSaveDocument(selected);
        StatusText.Text = issues.Count > 0 ? $"存档扫描：{issues.Count} 项提示 · {issues[0]}"
            : $"已读取 {result.Entries.Count} 份存档 · 新归档 {result.Entries.Count(e => e.IsNewArchive)} 份 · 原文件保持不变";
        StatusText.ToolTip = issues.Count > 0 ? string.Join(Environment.NewLine, issues) : StatusText.Text;
    }

    private async Task PollReplaysAsync()
    {
        if (_replays is null || _replayTickRunning || _closed) return;
        _replayTickRunning = true;
        try
        {
            var result = await _replays.TickAsync();
            if (_closed) return;
            if (result.Archives.Count > 0)
            {
                StatusText.Text = $"新录像已归档到 {result.Archives.Count} 个存档目录";
                if (!_closing) _archiveNotifications?.ShowReplays(result.Archives);
            }
            else if (result.Issues.Count > 0) StatusText.Text = result.Issues[0];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { if (!_closed) StatusText.Text = "录像归档等待重试：" + error.Message; }
        finally { _replayTickRunning = false; }
    }

    private void ShowDesktopNotice(string title, string message) => _archiveNotifications?.Show(title, message);

    private void CaptureScreenshot_Click(object sender, RoutedEventArgs e) => CaptureCurrentSaveScreenshot();

    private void CaptureCurrentSaveScreenshot()
    {
        if (!DesktopFeaturesAllowed) return;
        try
        {
            if (_archives is null || SaveList.SelectedItem is not SavedDocument doc)
                throw new InvalidOperationException("请先在我的存档中选择截图要归属的存档。");
            _archiveNotifications?.PrepareForScreenshot();
            var bytes = DesktopCapture.CaptureForegroundPngBytes();
            var path = _archives.ArchiveMedia(bytes, ArchiveMediaKind.Screenshot, doc.SaveName, doc.ChangeSummary,
                SettingsPage.CurrentSettings, ".png");
            StatusText.Text = "截图已归档：" + path;
            if (!_closing && !_closed) _archiveNotifications?.ShowScreenshot(doc.SaveName, path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception)
        { StatusText.Text = "截图未保存：" + error.Message; }
    }
}
