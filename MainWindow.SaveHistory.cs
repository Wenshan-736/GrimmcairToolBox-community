using System.IO;
using System.Text.Json;
using System.Windows;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop;

public partial class MainWindow
{
    private void LocalSaveHistory_Click(object sender, RoutedEventArgs e)
    {
        if (SaveList.SelectedItem is not SavedDocument document || FindLinkedSave(document) is not { } save || _simulation is null)
        {
            StatusText.Text = "请先选择一份保存在本地存档库中的角色存档。";
            return;
        }
        try
        {
            var history = _simulation.ListSaveHistory(save.OwnerId, save.Id);
            var window = new Views.LocalSaveHistoryWindow($"{save.GameAccount} · {save.Profession} · {save.Name}", history,
                historyId => _simulation.ReadSaveHistory(save.OwnerId, save.Id, historyId),
                historyId => RestoreSelectedSaveHistory(save.OwnerId, save.Id, historyId),
                syncEnabled: DesktopEdition.SupportsCloud && (DesktopOnlineMode ? RemoteSyncEnabled(save.Id) : save.SyncEnabled)) { Owner = this };
            window.ShowDialog();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
        {
            StatusText.Text = "本地历史暂时无法读取；当前存档与原文件保持不变。";
            if (App.AutomationMode) throw;
            MessageBox.Show(this, StatusText.Text, "本地存档历史", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void RestoreSelectedSaveHistory(string ownerId, string saveId, string historyId)
    {
        if (_simulation is null) throw new InvalidOperationException("本地存档库未就绪。");
        var wasRefreshing = _refreshingSimulation;
        _refreshingSimulation = true;
        try { _simulation.RestoreSaveHistory(ownerId, saveId, historyId); }
        finally { _refreshingSimulation = wasRefreshing; }
        RefreshSimulationContext(syncPending: false);
        var document = _saves.FirstOrDefault(save => save.SimulationSaveId == saveId)
            ?? throw new InvalidOperationException("恢复版本已保留，请重新打开存档页查看。");
        document.ChangeSummary = "已从本地历史恢复为新的当前版本";
        SelectSaveDocument(document);
        StatusText.Text = "已恢复为新的当前版本，已有本地历史继续保留，原游戏TXT未改写。";
    }
}
