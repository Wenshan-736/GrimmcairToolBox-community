
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop;

public partial class MainWindow
{
    private readonly string _simulationPath;
    private CloudSimulation? _simulation;
    private bool _refreshingSimulation, _refreshingSavePicker;
    private string? _manualTargetId;
    private sealed record TargetSaveChoice(string Id, string OwnerId, string Label);
    private sealed record CloudCopyChoice(string Id, string Label);
    private string AccountDocumentsPath => Path.Combine(Path.GetDirectoryName(_simulationPath)!, "account-documents.json");

    private void InitializeSimulation()
    {
        try
        {
            DesktopDiagnostics.Mark(DesktopDiagnosticStage.LocalStateLoading);
            _simulation = new CloudSimulation(_simulationPath);
            if (!App.AutomationMode) _simulation.RemoveDemoSaves();
            _simulation.EnsurePreviewAccount();
            if (File.Exists(AccountDocumentsPath))
                foreach (var doc in JsonSerializer.Deserialize<List<SavedDocument>>(File.ReadAllText(AccountDocumentsPath)) ?? [])
                    if (doc.Result["kind"]?.ToString() == "account") _saves.Add(doc);
            _simulation.Changed += (_, _) =>
            {
                if (Dispatcher.CheckAccess()) RefreshSimulationContext();
                else Dispatcher.BeginInvoke(() => { if (!_closed) RefreshSimulationContext(); });
            };
            RefreshSimulationContext();
            if (SaveList.SelectedItem is SavedDocument selected) ShowSelectedSave(selected);
            DesktopDiagnostics.Mark(DesktopDiagnosticStage.LocalStateLoaded);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException)
        {
            DesktopDiagnostics.ReportException(DesktopDiagnosticStage.LocalStateLoading, error);
            _simulation = null;
            StatusText.Text = "本地记录无法读取，未覆盖原文件：" + error.Message;
            if (App.AutomationMode) throw;
        }
    }

    private static JsonObject MakeSummaryResult(CloudSave save) => new()
    {
        ["kind"] = "character", ["hero"] = new JsonObject { ["name"] = save.Profession },
        ["source"] = new JsonObject { ["sha256"] = save.SourceHash },
        ["inventories"] = new JsonObject { ["inventory"] = new JsonArray(save.Inventory.Select(i => (JsonNode)new JsonObject
            { ["rawcode"] = i.ItemId, ["name"] = i.ItemName, ["quantity"] = i.Quantity }).ToArray()) }
    };

    private void RefreshSimulationContext(bool syncPending = true)
    {
        if (_simulation is null || _refreshingSimulation) return;
        _refreshingSimulation = true;
        try
        {
            if (syncPending) SyncPendingSaves();
            foreach (var ignored in _saves.Where(d => d.SimulationSaveId is not null
                         && _simulation.FindSave(CloudSimulation.PreviewOwnerId, d.SimulationSaveId)?.IsIgnored == true).ToList()) _saves.Remove(ignored);
            foreach (var save in _simulation.State.Saves.Where(s => s.OwnerId == CloudSimulation.PreviewOwnerId && !s.IsIgnored))
            {
                var doc = _saves.FirstOrDefault(d => d.SimulationSaveId == save.Id);
                if (doc is null)
                {
                    doc = new SavedDocument { Result = save.ParsedResult ?? MakeSummaryResult(save), SourcePath = save.SourcePath, SimulationSaveId = save.Id };
                    _saves.Add(doc);
                }
                doc.Result = save.ParsedResult ?? doc.Result;
                doc.SourcePath = save.SourcePath; doc.SaveName = save.Name; doc.GameAccount = save.GameAccount;
                doc.IsMain = save.IsMain; doc.IsFavorite = save.IsFavorite; doc.SyncEnabled = DesktopEdition.IsCommunity ? false : DesktopOnlineMode ? RemoteSyncEnabled(save.Id) : save.SyncEnabled; doc.SourceKey = save.SourceKey; doc.IsDemo = save.IsDemo;
                doc.SimulationBadgeText = DesktopEdition.IsCommunity ? $"{(save.IsMain ? "主档" : "副档")} · 本地存档"
                    : DesktopOnlineMode ? $"{(save.IsMain ? "主档" : "副档")} · {(doc.SyncEnabled ? "已开启云同步" : "未开启云同步")}" : $"{(save.IsMain ? "主档" : "副档")} · {_simulation.GetSyncStatus(save.OwnerId, save.Id)}（模拟）";
                doc.NotifyDisplayChanged();
            }
            RefreshSavePicker();
            RefreshTargetChoices();
            if (DesktopEdition.SupportsCloud && !DesktopOnlineMode)
            {
            var previousCloud = (CloudCopiesBox.SelectedItem as CloudCopyChoice)?.Id;
            var copies = _simulation.State.Snapshots.Where(s => s.OwnerId == CloudSimulation.PreviewOwnerId)
                .Select(s => new CloudCopyChoice(s.SaveId, $"{s.GameAccount} · {s.Profession} · {s.Name}")).ToList();
            CloudCopiesBox.ItemsSource = copies;
            CloudCopiesBox.SelectedItem = copies.FirstOrDefault(c => c.Id == previousCloud) ?? copies.FirstOrDefault();
            var account = _simulation.State.Accounts.Single(a => a.Id == CloudSimulation.PreviewOwnerId);
            CloudCapacityText.Text = $"云槽位 {_simulation.State.Snapshots.Count(s => s.OwnerId == account.Id)} / {account.Capacity}";
            }
            else if (DesktopOnlineMode) RefreshRemoteCloudList();
        }
        finally { _refreshingSimulation = false; _refreshingSavePicker = false; }
        RefreshSelectedTargets();
        RefreshImportedSaveControls();
        RefreshMaterialAnalysisWindows();
    }

    private void RefreshSavePicker()
    {
        var selected = SaveList.SelectedItem as SavedDocument;
        var selectedId = selected?.SimulationSaveId;
        var wasRefreshing = _refreshingSavePicker;
        _refreshingSavePicker = true;
        try
        {
            SaveList.ItemsSource = _saves.OrderByDescending(s => s.IsFavorite).ThenByDescending(s => s.IsMain)
                .ThenBy(s => s.GameAccount).ThenBy(s => s.RoleName).ThenBy(s => s.SaveName).ToList();
            SaveList.SelectedItem = _saves.FirstOrDefault(d => selectedId is not null && d.SimulationSaveId == selectedId)
                ?? (selected is not null && _saves.Contains(selected) ? selected : null)
                ?? SaveList.Items.Cast<SavedDocument>().FirstOrDefault();
        }
        finally { _refreshingSavePicker = wasRefreshing; }
    }

    private void SetSaveFavorite(SavedDocument document, bool favorite)
    {
        if (!_saves.Contains(document)) throw new InvalidOperationException("所选存档已不在本地列表中，请重新选择。");
        if (FindLinkedSave(document) is CloudSave save)
        {
            // Local ordering must not cause Changed to start any pending cloud synchronization.
            var wasRefreshing = _refreshingSimulation;
            _refreshingSimulation = true;
            try { _simulation!.SetFavorite(save.OwnerId, save.Id, favorite); }
            finally { _refreshingSimulation = wasRefreshing; }
            document.IsFavorite = favorite;
        }
        else if (document.Result["kind"]?.ToString() == "account")
        {
            var previous = document.IsFavorite;
            document.IsFavorite = favorite;
            try { SaveAccountDocuments(); }
            catch
            {
                document.IsFavorite = previous;
                document.NotifyDisplayChanged();
                throw;
            }
        }
        else throw new InvalidOperationException("所选存档尚未保存在本地存档库中。");
        document.NotifyDisplayChanged();
        if (_simulation is not null) RefreshSimulationContext(syncPending: false);
        else RefreshSavePicker();
    }

    private void SaveAccountDocuments()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AccountDocumentsPath)!);
        var temporary = AccountDocumentsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_saves.Where(s => s.Result["kind"]?.ToString() == "account").ToList(), App.JsonOptions));
        File.Move(temporary, AccountDocumentsPath, true);
    }

    private (int Updated, int Failed) SyncPendingSaves(bool retryFailures = false)
    {
        if (!DesktopEdition.SupportsCloud) return (0, 0);
        if (DesktopOnlineMode) { QueueDesktopSync(); return (0, 0); }
        return _simulation?.HasAcknowledgedSharingNotice(CloudSimulation.PreviewOwnerId) == true
            ? _simulation.SyncPending(CloudSimulation.PreviewOwnerId, retryFailures) : (0, 0);
    }

    private void SelectSaveDocument(SavedDocument document)
    {
        RefreshSimulationContext();
        var doc = _saves.FirstOrDefault(s => document.SimulationSaveId is not null && s.SimulationSaveId == document.SimulationSaveId)
            ?? _saves.FirstOrDefault(s => s.SourceKey == document.SourceKey && s.Hash == document.Hash) ?? document;
        SaveList.SelectedItem = doc;
        ShowSelectedSave(doc);
    }

    private SavedDocument AcceptParsedSave(JsonObject result, string path, string? fallback = null, string? sourceKey = null, string? saveName = null, string? changeSummary = null)
    {
        if (_simulation is null) throw new InvalidOperationException("本地存档库未就绪，原文件保持不变。");
        var input = SaveInput.Read(path, fallback, preferEmbeddedUsername: true);
        if (input.Sha256 != result["source"]?["sha256"]?.ToString()) throw new IOException("存档在读取期间发生变化，请稍后重试。");
        var key = sourceKey ?? SaveArchiveService.BuildLogicalSourceKey(path, input.Username, result);
        var doc = new SavedDocument { Result = result, SourcePath = path, ImportUsernameFallback = fallback,
            GameAccount = input.Username, SaveName = saveName ?? Path.GetFileNameWithoutExtension(path), SourceKey = key,
            ChangeSummary = changeSummary ?? "手动导入" };
        if (result["kind"]?.ToString() == "character")
        {
            _refreshingSimulation = true;
            try { doc.SimulationSaveId = _simulation.ImportOrUpdateSave(CloudSimulation.PreviewOwnerId, CreateSimulationImport(doc)).Id; }
            finally { _refreshingSimulation = false; }
            var previous = _saves.FirstOrDefault(s => s.SimulationSaveId == doc.SimulationSaveId);
            if (previous is null) _saves.Add(doc);
            else { previous.Result = result; previous.SourcePath = path; previous.ChangeSummary = doc.ChangeSummary; doc = previous; }
        }
        else
        {
            var previous = _saves.FirstOrDefault(s => s.SourceKey == key || s.Hash == doc.Hash);
            if (previous is null) _saves.Add(doc);
            else { previous.Result = result; previous.SourcePath = path; previous.ChangeSummary = doc.ChangeSummary; doc = previous; }
            SaveAccountDocuments();
        }
        return doc;
    }

    private CloudSave? FindLinkedSave(SavedDocument doc) => _simulation?.State.Saves.FirstOrDefault(s => s.Id == doc.SimulationSaveId && s.OwnerId == CloudSimulation.PreviewOwnerId);

    private void RefreshTargetChoices()
    {
        var choices = _simulation!.State.Saves.Where(s => s.OwnerId == CloudSimulation.PreviewOwnerId && !s.IsIgnored)
            .Select(s => new TargetSaveChoice(s.Id, s.OwnerId, $"{s.GameAccount} · {s.Profession} · {s.Name}（{(s.IsMain ? "主档" : "副档")}）")).ToList();
        var current = SaveList.SelectedItem is SavedDocument doc ? FindLinkedSave(doc) : null;
        TargetSaveBox.ItemsSource = choices;
        TargetSaveBox.SelectedItem = choices.FirstOrDefault(c => c.Id == (_manualTargetId ?? current?.Id));
    }

    private void FollowSelectedSaveTarget()
    {
        _manualTargetId = null;
        if (_simulation is null) return;
        _refreshingSimulation = true;
        try { RefreshTargetChoices(); } finally { _refreshingSimulation = false; }
        RefreshSelectedTargets();
    }

    private void TargetSaveBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingSimulation) return;
        _manualTargetId = (TargetSaveBox.SelectedItem as TargetSaveChoice)?.Id;
        RefreshSelectedTargets();
    }

    private void RefreshSelectedTargets()
    {
        _targets.Clear();
        if (TargetSaveBox.SelectedItem is TargetSaveChoice choice && _simulation is not null)
        {
            var save = _simulation.FindSave(choice.OwnerId, choice.Id);
            if (save is not null) foreach (var goal in save.Targets) _targets.Add(goal.ItemId);
        }
        FilterEquipment(); UpdateTargetControls();
    }

    private void UpdateTargetControls()
    {
        if (TargetCount is null || TargetButton is null) return;
        var hasSave = TargetSaveBox.SelectedItem is TargetSaveChoice;
        TargetButton.IsEnabled = hasSave && EquipmentList.SelectedItem is CatalogItem { Category: "装备" };
        var analysisSave = TargetSaveBox.SelectedItem is TargetSaveChoice analysisChoice
            ? _simulation?.FindSave(analysisChoice.OwnerId, analysisChoice.Id) : null;
        AnalyzeTargetsButton.IsEnabled = Views.MaterialAnalysisWindow.CanAnalyze(analysisSave) && _targets.Count > 0;
        AnalyzeTargetsButton.ToolTip = "按所选存档自己的目标和库存分析材料；演示档和版本不符的档案不参与。";
        TargetCount.Text = hasSave ? $"当前档已设 {_targets.Count} 项目标" : "请在存档页选择角色存档，或在上方手动选择。";
        TargetButton.Content = EquipmentList.SelectedItem is CatalogItem item && _targets.Contains(item.Id) ? "取消目标" : "加入当前档目标";
        RenderEquipmentTargets();
    }

    private void RefreshImportedSaveControls()
    {
        if (SaveSyncBox is null) return;
        var doc = SaveList.SelectedItem as SavedDocument;
        var linked = doc is null ? null : FindLinkedSave(doc);
        if (DesktopEdition.IsCommunity)
        {
            SaveSyncBox.IsEnabled = false;
            SaveSyncBox.IsChecked = false;
            SaveSyncStatus.Visibility = Visibility.Collapsed;
            MainSaveBox.IsChecked = linked?.IsMain == true;
            MainSaveBox.IsEnabled = linked is not null && !linked.IsMain;
            MainSaveBox.ToolTip = "设为本地主档；每份存档仍独立保存目标，并按自己的库存计算材料。";
            SaveChangeText.Text = doc?.ChangeSummary ?? "暂无物品变化";
            SaveGoalsList.ItemsSource = linked?.Targets;
            RemoveSaveGoalButton.IsEnabled = linked is not null;
            AnalyzeSaveTargetsButton.IsEnabled = Views.MaterialAnalysisWindow.CanAnalyze(linked) && linked!.Targets.Count > 0;
            CommunityLocalSaveHistoryButton.IsEnabled = linked is not null;
            SaveGoalsHint.Text = linked is not null ? "每份存档独立保存目标；只使用当前档的库存计算材料缺口。" : "请先选择或导入角色存档。";
            return;
        }
        SaveSyncBox.IsEnabled = linked is not null; SaveSyncBox.IsChecked = linked?.SyncEnabled == true;
        MainSaveBox.IsChecked = linked?.IsMain == true;
        MainSaveBox.IsEnabled = linked is { IsIgnored: false, IsMain: false };
        MainSaveBox.ToolTip = "云同步开启后可设为主档，上报该档目标装备和爵及以上材料需求，供队长查看分配。每份存档独立保留目标，切换主档不会转交或清空目标。";
        SaveSyncBox.ToolTip = doc is null ? "请选择或导入一份存档" : linked is not null
            ? $"{_simulation!.GetSyncStatus(linked.OwnerId, linked.Id)}（本地模拟）\n开启后，本地内容更新会自动更新云端存档。云图标只表示已开启。"
            : "账号共有徽章与收藏暂不占角色云槽位。";
        var awaitingNotice = linked?.SyncEnabled == true && !_simulation!.HasAcknowledgedSharingNotice(linked.OwnerId);
        var syncStatus = linked is null ? "" : _simulation!.GetSyncStatus(linked.OwnerId, linked.Id);
        SaveSyncStatus.Visibility = linked?.SyncEnabled == true ? Visibility.Visible : Visibility.Collapsed;
        SaveSyncStatus.Text = awaitingNotice ? "待确认同步说明" : syncStatus == "失败" ? "更新失败" : syncStatus == "已同步" ? "已更新（模拟）" : "等待更新";
        SaveSyncStatus.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter()
            .ConvertFromString(syncStatus == "失败" ? "#B2372D" : syncStatus == "已同步" ? "#347344" : "#806247")!;
        SaveSyncStatus.ToolTip = syncStatus == "失败" ? linked!.LastSyncError + "\n原有云端存档保持不变，可点击“更新云端存档”重试。"
            : "当前仍为本地模拟，尚未连接真实服务器。";
        SaveChangeText.Text = doc?.ChangeSummary ?? "暂无物品变化";
        SaveGoalsList.ItemsSource = linked?.Targets;
        RemoveSaveGoalButton.IsEnabled = linked is not null;
        AnalyzeSaveTargetsButton.IsEnabled = Views.MaterialAnalysisWindow.CanAnalyze(linked) && linked!.Targets.Count > 0;
        SaveGoalsHint.Text = linked is not null ? "每份存档独立保存目标并按自己的库存计算；只有云端主档上传目标和材料缺口。" : "请先选择或导入角色存档。";
        if (DesktopOnlineMode) RefreshRemoteSaveControls(linked);
    }

    private void MainSave_Click(object sender, RoutedEventArgs e)
    {
        if (SaveList.SelectedItem is SavedDocument localDoc && FindLinkedSave(localDoc) is { IsIgnored: false } local
            && !local.IsMain && (!DesktopOnlineMode || !RemoteSyncEnabled(local.Id)))
        {
            RunSimulationAction(() => { _simulation!.SetMain(local.OwnerId, local.Id); FollowSelectedSaveTarget(); });
            StatusText.Text = "已设为本地主档；云同步保持关闭。";
            return;
        }
        if (DesktopOnlineMode)
        {
            var publish = MainSaveBox.IsChecked == true;
            _ = RunDesktopActionAsync(publish ? SetRemoteMainAsync : UnpublishRemoteMainAsync);
            return;
        }
        RunSimulationAction(() =>
    {
        if (SaveList.SelectedItem is SavedDocument doc && FindLinkedSave(doc) is CloudSave save)
        { _simulation!.SetMain(save.OwnerId, save.Id); FollowSelectedSaveTarget(); }
    });
    }
    private void SaveSync_Click(object sender, RoutedEventArgs e)
    {
        if (!DesktopEdition.SupportsCloud) return;
        if (DesktopOnlineMode) { var enabled = SaveSyncBox.IsChecked == true; _ = RunDesktopActionAsync(() => SetRemoteSyncAsync(enabled)); return; }
        RunSimulationAction(() =>
    {
        if (SaveList.SelectedItem is SavedDocument doc && FindLinkedSave(doc) is CloudSave save)
        {
            var enabled = SaveSyncBox.IsChecked == true;
            if (enabled && !ConfirmCloudSharing(save.OwnerId))
            { RefreshImportedSaveControls(); return; }
            _simulation!.SetSyncEnabled(save.OwnerId, save.Id, enabled);
        }
    });
    }
    private void SyncOwnSaves_Click(object sender, RoutedEventArgs e)
    {
        if (!DesktopEdition.SupportsCloud) return;
        if (DesktopOnlineMode) { _ = RunDesktopActionAsync(() => SyncDesktopSavesAsync(true, SelectedLocalSave().Id)); return; }
        RunSimulationAction(() =>
    {
        if (_simulation is null || !_simulation.State.Saves.Any(s => s.OwnerId == CloudSimulation.PreviewOwnerId && s.SyncEnabled))
            throw new InvalidOperationException("请先为需要更新的存档开启云同步。");
        if (!ConfirmCloudSharing(CloudSimulation.PreviewOwnerId)) return;
        (int Updated, int Failed) result;
        _refreshingSimulation = true;
        try { result = SyncPendingSaves(retryFailures: true); }
        finally { _refreshingSimulation = false; }
        RefreshSimulationContext();
        StatusText.Text = result.Failed > 0 ? $"已更新 {result.Updated} 份，{result.Failed} 份更新失败 · 本地模拟"
            : result.Updated > 0 ? $"已更新 {result.Updated} 份云端存档 · 本地模拟"
            : "云端存档已是最新 · 本地模拟";
    });
    }
    private bool ConfirmCloudSharing(string ownerId)
    {
        if (!DesktopEdition.SupportsCloud) return false;
        if (_simulation!.HasAcknowledgedSharingNotice(ownerId)) return true;
        if (new Views.CloudSyncNoticeWindow { Owner = this }.ShowDialog() != true) return false;
        _simulation.AcknowledgeSharingNotice(ownerId);
        return true;
    }
    private void DownloadCloud_Click(object sender, RoutedEventArgs e)
    {
        if (!DesktopEdition.SupportsCloud) return;
        if (DesktopOnlineMode) { _ = RunDesktopActionAsync(DownloadRemoteSaveAsync); return; }
        RunSimulationAction(() =>
    {
        if (CloudCopiesBox.SelectedItem is CloudCopyChoice choice)
        { var copy = _simulation!.DownloadCloudCopy(CloudSimulation.PreviewOwnerId, choice.Id); SelectSaveDocument(_saves.Single(s => s.SimulationSaveId == copy.Id)); }
    });
    }
    private void DeleteCloud_Click(object sender, RoutedEventArgs e)
    {
        if (!DesktopEdition.SupportsCloud) return;
        if (DesktopOnlineMode) { _ = RunDesktopActionAsync(() => DeleteRemoteSaveAsync()); return; }
        RunSimulationAction(() =>
    {
        if (CloudCopiesBox.SelectedItem is CloudCopyChoice choice) _simulation!.DeleteCloudCopy(CloudSimulation.PreviewOwnerId, choice.Id);
    });
    }
    private void SaveTargets_Click(object sender, RoutedEventArgs e)
    {
        SaveItemsList.Visibility = Visibility.Collapsed; SaveGoalsPanel.Visibility = Visibility.Visible;
        SaveContentTitle.Text = "目标装备"; SaveItemCount.Text = "当前存档"; RefreshImportedSaveControls();
    }
    private void RemoveSaveGoal_Click(object sender, RoutedEventArgs e) => RunSimulationAction(() =>
    {
        if (SaveList.SelectedItem is SavedDocument doc && FindLinkedSave(doc) is CloudSave save && SaveGoalsList.SelectedItem is CloudGoal goal)
            _simulation!.RemoveTarget(save.OwnerId, save.Id, goal.ItemId);
    });

    public void OpenLocalSimulationDemo()
    {
        // Old --cloud-demo shortcuts continue to open saves without reintroducing fixtures.
        MainPages.SelectedIndex = 1;
    }

    private static CloudImportSave CreateSimulationImport(SavedDocument doc)
    {
        if (doc.Result["kind"]?.ToString() != "character" || doc.Result["hero"] is not JsonObject hero)
            throw new InvalidOperationException("账号徽章档不占角色槽位。");
        var input = SaveInput.Read(doc.SourcePath, doc.ImportUsernameFallback, preferEmbeddedUsername: true);
        if (input.Sha256 != doc.Hash) throw new InvalidOperationException("原文件在解析后已变化，请重新解析。");
        var inventory = new List<CloudInventoryItem>();
        foreach (var key in new[] { "inventory", "bag", "storage" })
            if (doc.Result["inventories"]?[key] is JsonArray items)
                foreach (var item in items) inventory.Add(new CloudInventoryItem
                { ItemId = item!["rawcode"]!.ToString(), ItemName = item["name"]?.ToString() ?? "未命名物品", Quantity = item["quantity"]?.GetValue<int>() ?? 1 });
        return new CloudImportSave
        {
            Name = doc.SaveName, GameAccount = input.Username, Profession = hero["name"]?.ToString() ?? throw new InvalidDataException("职业未识别"),
            MapVersion = doc.Result["map_profile"]?["name"]?.ToString() ?? "待识别版本", SourceHash = doc.Hash,
            SourcePath = doc.SourcePath, SourceKey = doc.SourceKey, ParsedResult = (JsonObject)doc.Result.DeepClone(), Inventory = inventory,
            LoadCodes = input.Groups.Select(g => $"-{g.Command} {g.Payload}").ToList()
        };
    }

    private void RunSimulationAction(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text = error.Message;
            if (App.AutomationMode) throw;
            MessageBox.Show(this, error.Message, "存档管理", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshImportedSaveControls();
        }
    }
}
