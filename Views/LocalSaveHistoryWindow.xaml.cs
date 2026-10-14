using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop.Views;

public partial class LocalSaveHistoryWindow : Window
{
    private readonly Func<string, LocalSaveHistorySnapshot> readHistory;
    private readonly Action<string> restoreHistory;
    private readonly bool syncEnabled;
    private bool reading;
    private LocalSaveHistoryDisplayRow? selected;

    public LocalSaveHistoryWindow(string saveLabel, IReadOnlyList<LocalSaveHistoryEntry> history,
        Func<string, LocalSaveHistorySnapshot> readHistory, Action<string> restoreHistory, bool syncEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(history);
        this.readHistory = readHistory ?? throw new ArgumentNullException(nameof(readHistory));
        this.restoreHistory = restoreHistory ?? throw new ArgumentNullException(nameof(restoreHistory));
        this.syncEnabled = syncEnabled;
        InitializeComponent();
        SaveHeading.Text = saveLabel;
        var displayedTimes = new Dictionary<string, int>(StringComparer.Ordinal);
        HistoryRows = history.OrderByDescending(entry => entry.CreatedAtUtc).Select(entry =>
        {
            var row = LocalSaveHistoryDisplayRow.FromEntry(entry);
            var sequence = displayedTimes.GetValueOrDefault(row.BeijingTimeText) + 1;
            displayedTimes[row.BeijingTimeText] = sequence;
            return sequence == 1 ? row : row with { BeijingTimeText = row.BeijingTimeText + $"（{sequence}）" };
        }).ToList();
        HistoryCount.Text = HistoryRows.Count == 0 ? "此存档还没有历史记录，后续更新会在本地保留。" : $"共 {HistoryRows.Count} 个版本 · 最新在前";
        HistoryGrid.ItemsSource = HistoryRows;
        if (HistoryRows.Count > 0) HistoryGrid.SelectedIndex = 0;
    }

    public IReadOnlyList<LocalSaveHistoryDisplayRow> HistoryRows { get; }
    public string? SelectedHistoryId => selected?.Id;
    public string DetailSummaryText => SelectionSummary.Text;
    public int VisibleInventoryCount => SnapshotItems.Items.Count;
    public string GoalsSummaryText => GoalSummary.Text;
    public string ErrorText => DetailError.Visibility == Visibility.Visible ? DetailError.Text : "";
    public string? RestoredHistoryId { get; private set; }

    public bool SelectHistory(string historyId)
    {
        var row = HistoryRows.FirstOrDefault(row => row.Id == historyId);
        if (row is null) return false;
        HistoryGrid.SelectedItem = row;
        return true;
    }

    public bool RestoreSelectedForAutomation(bool confirmed)
        => RestoreSelection(confirmed);

    internal bool RestoreSelection(bool confirmation)
    {
        if (!App.AutomationMode) throw new InvalidOperationException("历史恢复测试接口仅用于隔离自动验证。");
        if (!confirmation) return false;
        return RestoreSelected(closeDialog: false);
    }

    public string RestoreConfirmationText(LocalSaveHistoryDisplayRow row) =>
        $"将“{row.SaveName}”在{row.BeijingTimeText}（北京时间）的内容恢复为当前版本？\n\n"
        + "恢复会生成新的当前版本，保留已有全部历史。当前主副档身份、收藏、来源和同步设置保留；副档不恢复装备目标。原游戏TXT保持不变。"
        + (syncEnabled ? "\n\n这份存档已开启同步，后续云端槽位会更新为恢复后的最新内容。" : "");

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var work = SystemParameters.WorkArea;
        MaxWidth = Math.Max(320, work.Width - 36);
        MaxHeight = Math.Max(320, work.Height - 36);
        MinWidth = Math.Min(MinWidth, MaxWidth);
        MinHeight = Math.Min(MinHeight, MaxHeight);
        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);
        UpdateHistoryColumns();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateHistoryColumns();

    private void UpdateHistoryColumns()
    {
        if (HistoryRoleColumn is null || HistoryTargetColumn is null) return;
        // Compact views keep the time, save name and source; role and goal count
        // remain in the selected-version summary rather than being squeezed.
        var visibility = (ActualWidth > 0 ? ActualWidth : Width) < 1000 ? Visibility.Collapsed : Visibility.Visible;
        HistoryRoleColumn.Visibility = visibility;
        HistoryTargetColumn.Visibility = visibility;
    }

    private void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SnapshotItems is null || reading) return;
        selected = HistoryGrid.SelectedItem as LocalSaveHistoryDisplayRow;
        RestoreButton.IsEnabled = false;
        SnapshotItems.ItemsSource = null;
        EmptyInventoryText.Visibility = Visibility.Collapsed;
        GoalSummary.Text = "";
        DetailError.Visibility = Visibility.Collapsed;
        SelectionState.Text = "";
        if (selected is null)
        {
            SelectionSummary.Text = "选择一个历史版本，查看等级、物品和目标摘要。";
            return;
        }
        reading = true;
        try
        {
            var snapshot = readHistory(selected.Id);
            var save = snapshot.Save;
            var level = save.ParsedResult?["hero"]?["level"]?.ToString();
            var levelText = string.IsNullOrWhiteSpace(level) ? selected.LevelText : $"{level}级";
            SelectionSummary.Text = $"{save.GameAccount} · {save.Profession}\n{selected.BeijingTimeText}（北京时间）\n"
                + $"{levelText} · {selected.RoleText} · {save.Inventory.Count}项持有记录 · {save.Targets.Count}项目标";
            SelectionState.Text = selected.IsCandidate ? "这是保留在本地的云端候选版本，尚未应用到当前存档。" : "这里只查看历史摘要，不显示完整存档代码。";
            var items = save.Inventory.Select(item => new LocalSaveHistoryItemRow(
                DisplayItemName(item.ItemName, item.ItemId), "×" + item.Quantity.ToString(CultureInfo.InvariantCulture))).ToList();
            SnapshotItems.ItemsSource = items;
            EmptyInventoryText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            GoalSummary.Text = save.Targets.Count == 0 ? "此版本没有装备目标。" : string.Join("\n", save.Targets.Select(goal =>
                DisplayItemName(goal.ItemName, goal.ItemId) + " ×" + goal.Quantity.ToString(CultureInfo.InvariantCulture)));
            RestoreButton.IsEnabled = true;
        }
        catch (Exception error) when (IsHistoryError(error))
        {
            SelectionSummary.Text = "此历史版本暂时无法读取。";
            DetailError.Text = "未修改当前存档。请关闭窗口后重新读取历史；若仍不可用，请保留本地历史文件。";
            DetailError.Visibility = Visibility.Visible;
        }
        finally { reading = false; }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (selected is not { } row || !RestoreButton.IsEnabled) return;
        if (MessageBox.Show(this, RestoreConfirmationText(row), "确认恢复本地历史", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        RestoreSelected(closeDialog: true);
    }

    private bool RestoreSelected(bool closeDialog)
    {
        if (selected is not { } row || !RestoreButton.IsEnabled) return false;
        RestoreButton.IsEnabled = false;
        try
        {
            restoreHistory(row.Id);
            RestoredHistoryId = row.Id;
            if (closeDialog) DialogResult = true;
            return true;
        }
        catch (Exception error) when (IsHistoryError(error))
        {
            DetailError.Text = "恢复未能完成。请重新打开本地历史并检查当前版本，再决定是否重试。";
            DetailError.Visibility = Visibility.Visible;
            return false;
        }
    }

    private static bool IsHistoryError(Exception error) => error is IOException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException or JsonException;
    private static string DisplayItemName(string? name, string id) => string.IsNullOrWhiteSpace(name) || name == id ? "未命名物品" : name;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed record LocalSaveHistoryItemRow(string Name, string QuantityText);

// This public list projection deliberately exposes no load codes, source paths,
// inventory IDs or private payload. Complete snapshot contents stay in the service.
public sealed record LocalSaveHistoryDisplayRow(string Id, string BeijingTimeText, string SaveName, string RoleText,
    int TargetCount, string LevelText, string SourceText, bool IsCandidate)
{
    public static LocalSaveHistoryDisplayRow FromEntry(LocalSaveHistoryEntry entry) => new(entry.Id,
        entry.CreatedAtUtc.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        entry.SaveName, entry.IsMain ? "主档" : "副档", entry.TargetCount,
        string.IsNullOrWhiteSpace(entry.LevelText) ? "等级摘要未保存" : entry.LevelText,
        entry.IsCandidate ? "云端候选" : "本地历史", entry.IsCandidate);
}
