using System.IO;
using System.Windows;
using System.Windows.Controls;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop.Views;

public partial class CloudSimulationView : UserControl
{
    private CloudSimulation? _service;
    private List<CatalogItem> _catalog = [];
    private bool _refreshing;
    private string? _selectedSaveId;

    public string SelectedOwnerId => CloudSimulation.PreviewOwnerId;
    public string? SelectedSaveId => _selectedSaveId;
    public event EventHandler? ContextChanged;

    public CloudSimulationView()
    {
        InitializeComponent();
        BadgeBox.ItemsSource = Enumerable.Range(1, 13).Select(level => new BadgeChoice(level,
            level == 1 ? "1 级 · 黑铁" : level == 13 ? "13 级 · 日耀" : $"{level} 级")).ToList();
    }

    public void Initialize(CloudSimulation service, IEnumerable<CatalogItem> catalog)
    {
        if (_service is not null) _service.Changed -= Service_Changed;
        _service = service;
        _service.EnsurePreviewAccount();
        _catalog = catalog.Where(item => item.Category == "装备").ToList();
        _service.Changed += Service_Changed;
        RefreshAll();
    }

    public void OpenSave(string id)
    {
        var save = _service?.State.Saves.FirstOrDefault(item => item.Id == id);
        if (save is null || save.OwnerId != SelectedOwnerId) return;
        _selectedSaveId = save.Id;
        RefreshAll();
        ContextChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool AddCatalogTarget(CatalogItem item)
    {
        var save = SelectedSave();
        if (_service is null || save is null || !save.IsMain)
        {
            StatusText.Text = "请先选择一个主档，再为它设置装备目标。";
            return false;
        }
        return Run(() => _service.AddTarget(save.OwnerId, save.Id, item.Id, item.Name, 1),
            "已添加主档装备目标；成功同步后才会更新云端快照。尚未核对的配方会单独标记。");
    }

    // Allows the existing preview smoke runner to inspect an injected temporary simulation.
    public object InspectSimulationUi() => new
    {
        SelectedOwnerId, SelectedSaveId, Capacity = CapacityText.Text,
        Slots = SlotList.Items.Count, MainTargetEditorEnabled = TargetEditor.IsEnabled,
        CanDownloadCloudCopy = DownloadCloudButton.IsEnabled, Status = StatusText.Text
    };

    private CloudSave? SelectedSave() => _service?.State.Saves.FirstOrDefault(item =>
        item.Id == _selectedSaveId && item.OwnerId == SelectedOwnerId);

    private void Service_Changed(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(RefreshAll); return; }
        RefreshAll();
    }

    private void RefreshAll()
    {
        if (_service is null) return;
        _refreshing = true;
        try
        {
            var account = _service.State.Accounts.FirstOrDefault(item => item.Id == SelectedOwnerId);
            BadgeBox.IsEnabled = account is not null;
            BadgeBox.SelectedValue = account?.BadgeLevel;
            SyncAccountButton.IsEnabled = account is not null;
            var used = account is null ? 0 : _service.State.Snapshots.Count(item => item.OwnerId == account.Id);
            CapacityText.Text = account is null ? "尚未初始化" : $"{used} / {account.Capacity} 格已占用";
            var saves = _service.State.Saves.Where(item => item.OwnerId == account?.Id)
                .OrderBy(item => item.GameAccount, StringComparer.CurrentCulture)
                .ThenBy(item => item.Profession, StringComparer.CurrentCulture)
                .ThenByDescending(item => item.IsMain).Select(item => new SlotRow(item,
                    _service.GetSyncStatus(item.OwnerId, item.Id))).ToList();
            SlotList.ItemsSource = saves;
            SlotList.SelectedItem = saves.FirstOrDefault(item => item.Id == _selectedSaveId) ?? saves.FirstOrDefault();
            _selectedSaveId = (SlotList.SelectedItem as SlotRow)?.Id;
            SaveCountText.Text = $"我的本地分档 · {saves.Count} 份";
            RefreshDetail();
        }
        finally { _refreshing = false; }
    }

    private void RefreshDetail()
    {
        var save = SelectedSave();
        SaveDetailScroll.Visibility = save is null ? Visibility.Collapsed : Visibility.Visible;
        EmptySaveText.Visibility = save is null ? Visibility.Visible : Visibility.Collapsed;
        TargetEditor.IsEnabled = save?.IsMain == true;
        DeleteCloudButton.IsEnabled = false;
        DownloadCloudButton.IsEnabled = false;
        if (_service is null || save is null) return;
        var hasCloudCopy = _service.State.Snapshots.Any(item => item.OwnerId == save.OwnerId && item.SaveId == save.Id);
        DetailCharacterText.Text = $"{save.GameAccount} · {save.Profession}";
        DetailSaveText.Text = $"{save.Name} · {(save.IsMain ? "主档" : "副档")} · {(save.IsDemo ? "演示数据" : "导入存档")} · {save.MapVersion}";
        var selectionLabel = save.SyncEnabled ? "已选择云同步" : hasCloudCopy ? "已停止同步，云副本保留" : "仅保留本地";
        DetailSyncText.Text = $"{selectionLabel} · {_service.GetSyncStatus(save.OwnerId, save.Id)}" +
            (save.LastSyncAt is { } time ? $" · 最近成功：{time.ToLocalTime():MM-dd HH:mm:ss}" : " · 尚未成功同步");
        MakeMainButton.IsEnabled = !save.IsMain;
        SyncEnabledBox.IsChecked = save.SyncEnabled;
        DeleteCloudButton.IsEnabled = hasCloudCopy;
        DownloadCloudButton.IsEnabled = hasCloudCopy;
        TargetEditor.IsEnabled = save.IsMain;
        TargetEditor.Visibility = save.IsMain ? Visibility.Visible : Visibility.Collapsed;
        TargetHintText.Text = save.IsMain
            ? save.IsDemo ? "演示目标使用假定配方，可体验共享库存抵扣。" : "为此主档设置目标。真实地图配方尚未核对时，只记录目标并标记待核对。"
            : "副档不设置装备目标，也不参与材料需求汇总。";
        var selectedTargetId = (TargetBox.SelectedItem as ItemChoice)?.Id;
        var choices = save.IsDemo
            ? _service.DemoEquipment.Select(item => new ItemChoice(item.ItemId, item.ItemName)).ToList()
            : _catalog.Select(item => new ItemChoice(item.Id, item.Name)).ToList();
        TargetBox.ItemsSource = choices;
        TargetBox.SelectedItem = choices.FirstOrDefault(item => item.Id == selectedTargetId) ?? choices.FirstOrDefault();
        GoalList.ItemsSource = save.IsMain ? save.Targets.Select(item => new GoalRow(item.ItemId, $"{item.ItemName} × {item.Quantity}")).ToList() : [];
        var report = save.IsMain ? _service.GetNeeds(save.OwnerId, save.Id) : null;
        NeedsGrid.ItemsSource = report?.Needs;
        NeedsHintText.Text = !save.IsMain ? "副档没有材料申请。" : save.Targets.Count == 0 ? "添加目标后查看需求。同一主档的库存只抵扣一次。"
            : "依据本地当前内容计算；成功同步后才更新云端需求快照。";
        PendingText.Text = report is null || report.PendingTargets.Count == 0 ? "" : "配方待核对：" + string.Join("、", report.PendingTargets);
        DemoInventoryPanel.Visibility = save.IsDemo ? Visibility.Visible : Visibility.Collapsed;
        var selectedMaterialId = (InventoryBox.SelectedItem as ItemChoice)?.Id;
        var materials = _service.DemoEquipment.SelectMany(item => item.Materials).GroupBy(item => item.MaterialId)
            .Select(group => new ItemChoice(group.Key, group.First().MaterialName)).ToList();
        InventoryBox.ItemsSource = materials;
        InventoryBox.SelectedItem = materials.FirstOrDefault(item => item.Id == selectedMaterialId) ?? materials.FirstOrDefault();
        RefreshInventoryQuantity();
        InventoryText.Text = save.IsDemo ? "演示库存：" + string.Join("；", save.Inventory.Where(item => item.Quantity > 0).Select(item => $"{item.ItemName} × {item.Quantity}"))
            : $"已读取 {save.Inventory.Count} 项存档库存。真实导入档的库存只通过重新解析存档更新。";
    }

    private bool Run(Action action, string success)
    {
        try { action(); StatusText.Text = success; return true; }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = error.Message;
            RefreshAll();
            return false;
        }
    }

    private void BadgeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || _service is null || BadgeBox.SelectedItem is not BadgeChoice badge) return;
        Run(() => _service.SetBadgeLevel(SelectedOwnerId, badge.Level), "已更新模拟徽章等级。等级变化只调整云槽位额度，不删除已有存档。");
    }

    private void SlotList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || _service is null) return;
        _selectedSaveId = (SlotList.SelectedItem as SlotRow)?.Id;
        _refreshing = true;
        try { RefreshDetail(); } finally { _refreshing = false; }
        ContextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SyncAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        var offline = OfflineBox.IsChecked == true;
        var count = 0;
        if (Run(() => count = _service.Sync(SelectedOwnerId, offline), ""))
            StatusText.Text = offline ? "模拟离线：上传未成功，模拟云副本仍保留上次成功同步的内容。" : $"本地模拟同步完成：成功 {count} 份。已更新我的云端快照。";
    }

    private void DuplicateSave_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || SelectedSave() is not { } save) return;
        Run(() => OpenSave(_service.DuplicateSave(save.OwnerId, save.Id).Id), "已复制为本地副档。副档不继承装备目标，勾选云同步后再同步才占用云槽位。");
    }

    private void MakeMain_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || SelectedSave() is not { } save) return;
        Run(() => _service.SetMain(save.OwnerId, save.Id), "已设为本角色主档，装备目标随角色转交。成功同步后才更新云端快照。");
    }

    private void SyncEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _service is null || SelectedSave() is not { } save) return;
        var enabled = SyncEnabledBox.IsChecked == true;
        Run(() => _service.SetSyncEnabled(save.OwnerId, save.Id, enabled), enabled
            ? "已选择云同步；点击“同步我的存档”后才会上传到本机模拟云端。"
            : "已停止自动选择同步。已有模拟云副本仍保留，需要移除时点击“删除本机模拟云副本”。");
    }

    private void AddTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || SelectedSave() is not { IsMain: true } save || TargetBox.SelectedItem is not ItemChoice item) return;
        if (!int.TryParse(TargetQuantityBox.Text, out var quantity) || quantity < 1)
        { StatusText.Text = "目标数量需要是大于 0 的整数。"; return; }
        Run(() => _service.AddTarget(save.OwnerId, save.Id, item.Id, item.Name, quantity), "已更新主档装备目标。成功同步后才更新云端需求快照。");
    }

    private void RemoveTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || SelectedSave() is not { IsMain: true } save || sender is not Button { Tag: string id }) return;
        Run(() => _service.RemoveTarget(save.OwnerId, save.Id, id), "已移除主档目标。成功同步后更新云端需求快照。");
    }

    private void InventoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing) RefreshInventoryQuantity();
    }

    private void RefreshInventoryQuantity()
    {
        var save = SelectedSave();
        var item = InventoryBox.SelectedItem as ItemChoice;
        InventoryQuantityBox.Text = (save?.Inventory.FirstOrDefault(entry => entry.ItemId == item?.Id)?.Quantity ?? 0).ToString();
    }

    private void UpdateInventory_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || SelectedSave() is not { IsDemo: true } save || InventoryBox.SelectedItem is not ItemChoice item) return;
        if (!int.TryParse(InventoryQuantityBox.Text, out var quantity) || quantity < 0)
        { StatusText.Text = "演示库存需要是 0 或更大的整数。"; return; }
        Run(() => _service.SetDemoInventory(save.OwnerId, save.Id, item.Id, quantity), "已调整演示库存，并重新计算本地主档缺口；同步后更新云端快照。");
    }

    private void DeleteCloudCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || SelectedSave() is not { } save) return;
        if (OfflineBox.IsChecked == true)
        {
            StatusText.Text = "模拟离线：无法删除模拟云副本，本地存档和模拟云副本保持不变。";
            return;
        }
        Run(() => _service.DeleteCloudCopy(save.OwnerId, save.Id), "已删除该档的本机模拟云副本并停止同步，本地存档和游戏文件仍保留。");
    }

    private void DownloadCloudCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || SelectedSave() is not { } save) return;
        if (OfflineBox.IsChecked == true)
        {
            StatusText.Text = "模拟离线：无法从模拟云取回存档，已有本地存档保持不变。";
            return;
        }
        Run(() => OpenSave(_service.DownloadCloudCopy(SelectedOwnerId, save.Id).Id),
            "已从本机模拟云取回为新的本地副档。现有主档和目标保持不变，可按需要将取回档设为主档。");
    }


    private sealed record BadgeChoice(int Level, string Label);
    private sealed record ItemChoice(string Id, string Name);
    private sealed record GoalRow(string ItemId, string Label);
    private sealed class SlotRow(CloudSave save, string status)
    {
        public string Id => save.Id;
        public string CharacterLabel => $"{save.GameAccount} · {save.Profession}";
        public string Name => save.Name;
        public string Badges => $"[{(save.IsMain ? "主档" : "副档")}]" + (save.SyncEnabled ? "  [云同步]" : "") + (save.IsDemo ? "  [演示]" : "");
        public string SyncLabel => status + (save.LastSyncAt is { } time ? $" · {time.ToLocalTime():MM-dd HH:mm}" : "");
    }
}
