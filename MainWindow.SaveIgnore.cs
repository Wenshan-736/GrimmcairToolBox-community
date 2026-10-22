using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop;

public partial class MainWindow
{
    private bool _saveIgnoreBusy;
    private sealed record IgnoredSaveChoice(string Id, string OwnerId, string Label);

    private async void IgnoreSave_Click(object sender, RoutedEventArgs e)
    {
        if (_saveIgnoreBusy || _closing || _closed || _simulation is null) return;
        if (SaveList.SelectedItem is not SavedDocument document || FindLinkedSave(document) is not { IsDemo: false, IsIgnored: false } save)
        { StatusText.Text = "请先选择要忽略的角色存档。"; return; }
        _saveIgnoreBusy = true;
        StatusText.Text = "正在停止这份存档的同步和自动归档……";
        try
        {
            // Finish already running writes first. New actions cannot pass this gate until opt-out is durable.
            await _desktopWorkGate.WaitAsync();
            try
            {
                var cloudPath = Path.Combine(Path.GetDirectoryName(_simulationPath)!, "desktop-cloud-state.json");
                if (DesktopEdition.SupportsCloud && File.Exists(cloudPath))
                {
                    _desktopStateStore ??= new DesktopCloudStateStore(cloudPath);
                    _desktopStateStore.StopLocalSaveSync(save.Id);
                }
                void Ignore()
                {
                    CloseMaterialAnalysisWindows(save.OwnerId, save.Id);
                    lock (_archiveEventGate)
                    {
                        // Drain an already active replay write before the durable opt-out boundary;
                        // a delayed scan notification then observes IsIgnored and cannot re-queue it.
                        var current = _simulation.FindSave(save.OwnerId, save.Id)!;
                        void PersistIgnore()
                        {
                            _simulation.SetIgnored(save.OwnerId, save.Id, true);
                            _replays?.RemovePendingSources(current.IgnoredSourceKeys.Append(current.SourceKey).ToHashSet(StringComparer.Ordinal));
                        }
                        if (_replays is not null) _replays.RunWithProcessingPaused(PersistIgnore);
                        else PersistIgnore();
                    }
                }
                if (_archives is not null) await _archives.RunBetweenScansAsync(Ignore);
                else Ignore();
                _manualTargetId = null;
                RefreshSimulationContext(false);
                if (SaveList.SelectedItem is SavedDocument remaining) ShowSelectedSave(remaining);
                else
                {
                    SaveTitle.Text = "暂无活动存档";
                    SaveSummary.Text = "可从“已忽略的存档”恢复，或导入另一份存档。";
                    SaveNote.Text = "已忽略存档的原文件、历史、目标和媒体仍然保留。";
                    _saveRows = [];
                    FilterSaveItems();
                    RefreshImportedSaveControls();
                }
                StatusText.Text = $"已忽略“{save.Name}”并停止同步；原存档、历史和云端内容保留。可从“已忽略的存档”恢复。";
                if (DesktopOnlineMode && _desktopSession?.IsAllowed == true)
                {
                    if (_desktopSync is null) await ReadDesktopCloudAsync();
                    await FinishIgnoredCloudStopsAsync();
                    if (_desktopSync?.GetBinding(save.Id).LastError.Length > 0)
                        StatusText.Text = "存档已忽略，本机已停止同步；云端停止状态待连接恢复后确认，云端内容保留。";
                }
            }
            finally { _desktopWorkGate.Release(); }
        }
        catch (Exception error) when (IsDesktopError(error))
        { StatusText.Text = "存档忽略处理：" + error.Message; }
        finally { _saveIgnoreBusy = false; }
    }

    private void ManageIgnoredSaves_Click(object sender, RoutedEventArgs e)
    {
        if (_simulation is null || _closing || _closed) return;
        var panel = new DockPanel { Margin = new Thickness(20) };
        var explanation = new TextBlock
        {
            Text = "这些存档已停止同步和自动归档。恢复后重新接收游戏文件更新；云同步和主档需要手动开启。已有历史、目标和媒体保留。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16)
        };
        DockPanel.SetDock(explanation, Dock.Top); panel.Children.Add(explanation);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var restore = new Button { Content = "恢复所选存档", MinWidth = 130, Padding = new Thickness(12, 7, 12, 7), IsEnabled = false };
        var close = new Button { Content = "关闭", MinWidth = 76, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(10, 0, 0, 0) };
        actions.Children.Add(restore); actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
        var list = new ListBox { DisplayMemberPath = nameof(IgnoredSaveChoice.Label) };
        panel.Children.Add(list);
        var dialog = new Window { Owner = this, Title = "已忽略的存档", Width = 680, Height = 400, MinWidth = 480, MinHeight = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel, Background = new SolidColorBrush(Color.FromRgb(249, 239, 214)) };
        void Refresh()
        {
            list.ItemsSource = _simulation.State.Saves.Where(x => x.OwnerId == CloudSimulation.PreviewOwnerId && x.IsIgnored)
                .Select(x => new IgnoredSaveChoice(x.Id, x.OwnerId, $"{x.GameAccount} · {x.Profession} · {x.Name} · 停止同步")).ToList();
            restore.IsEnabled = list.SelectedItem is IgnoredSaveChoice;
            if (list.Items.Count == 0) explanation.Text = "暂无已忽略的存档。";
        }
        list.SelectionChanged += (_, _) => restore.IsEnabled = list.SelectedItem is IgnoredSaveChoice;
        restore.Click += async (_, _) =>
        {
            if (list.SelectedItem is not IgnoredSaveChoice choice) return;
            restore.IsEnabled = false;
            try
            {
                _simulation.SetIgnored(choice.OwnerId, choice.Id, false);
                Refresh();
                if (_archives is not null)
                {
                    var scan = await _archives.ScanAsync(SettingsPage.CurrentSettings);
                    if (!_closed) ApplyArchiveScan(scan, false);
                }
                RefreshSimulationContext(false);
                var restored = _saves.FirstOrDefault(x => x.SimulationSaveId == choice.Id);
                if (restored is not null) SelectSaveDocument(restored);
                StatusText.Text = "存档已恢复显示和自动更新；云同步保持关闭，主档由你手动选择。";
            }
            catch (Exception error) when (IsDesktopError(error)) { explanation.Text = error.Message; }
            finally { restore.IsEnabled = list.SelectedItem is IgnoredSaveChoice; }
        };
        close.Click += (_, _) => dialog.Close();
        Refresh();
        dialog.ShowDialog();
    }
}
