using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop;

public partial class MainWindow : Window
{
    private readonly string _base = AppContext.BaseDirectory;
    private readonly ObservableCollection<SavedDocument> _saves = [];
    private readonly HashSet<string> _targets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _importSourcePaths = new(StringComparer.OrdinalIgnoreCase);
    private List<CatalogItem> _catalog = [];
    private Dictionary<string, CatalogItem> _byId = [];
    private List<SaveItemRow> _saveRows = [];
    private SaveDecoder? _decoder;
    private bool _ready;
    private bool _importing;
    private bool _changingSections;
    private Task? _initialization;

    public MainWindow(string? simulationPath = null)
    {
        _simulationPath = simulationPath ?? Path.Combine(DesktopDataPaths.Current.DataDirectory, "cloud-simulation.json");
        InitializeComponent();
        InitializeWindowLifetime();
        InitializeAiChat();
        Title = DesktopEdition.WindowTitle;
        EditionTitleText.Text = DesktopEdition.SidebarTitle;
        System.Windows.Automation.AutomationProperties.SetName(AboutButton, "关于" + DesktopEdition.WindowTitle);
        ApplicationVersionText.Text = "应用版本 · 0.1.8";
        if (DesktopEdition.IsCommunity)
        {
            DesktopAccountPanel.Visibility = Visibility.Collapsed;
            SaveSyncBox.Visibility = Visibility.Collapsed;
            SaveSyncStatus.Visibility = Visibility.Collapsed;
            SaveManagement.Visibility = Visibility.Collapsed;
            CommunityLocalSaveHistoryButton.Visibility = Visibility.Visible;
            System.Windows.Automation.AutomationProperties.SetName(MainSaveBox, "设为本地主档");
        }
        LoadDesktopConnectionSettings();
        DesktopDiagnostics.Mark(DesktopDiagnosticStage.SettingsLoaded);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        SaveList.ItemsSource = _saves;
        // Automation explicitly awaits initialization in App; a second async-void observer
        // would rethrow the same failure after App has already recorded it and requested exit.
        Loaded += async (_, _) => { if (!App.AutomationMode) await InitializeAsync(); };
        AllowDrop = true;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += async (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
                await ImportFilesAsync(paths.Where(p => Path.GetExtension(p).Equals(".txt", StringComparison.OrdinalIgnoreCase)));
        };
    }

    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        if (_ready) return;
        try
        {
            DesktopDiagnostics.Mark(DesktopDiagnosticStage.InitializationStarted);
            StatusText.Text = "正在读取本地图鉴……";
            var data = await Task.Run(() => ResourcePack.ReadText("catalog.json"));
            var root = JsonNode.Parse(data)!.AsObject();
            CatalogItem ReadItem(JsonNode? n)
            {
                var icon = n!["icon"]?.ToString() ?? "";
                return new CatalogItem
                {
                    Id = n["id"]!.ToString(), Name = n["name"]!.ToString(),
                    Category = n["category"]?.ToString() ?? "其他",
                    Subcategory = n["subcategory"]?.ToString() ?? "",
                    DisplayId = n["displayId"]?.ToString() ?? n["id"]!.ToString(),
                    EffectsText = n["effectsText"]?.ToString() ?? "",
                    EffectsSource = n["effectsSource"]?.ToString() ?? "",
                    DescriptionRuns = ReadItemRuns(n["descriptionRuns"]),
                    EffectsRuns = ReadItemRuns(n["effectsRuns"]),
                    Stage = n["stage"]?.ToString() ?? "",
                    StageRank = n["stageRank"]?.GetValue<int>() ?? 0,
                    StageSource = n["stageSource"]?.ToString() ?? "",
                    Recipes = ReadRecipes(n["recipes"]),
                    RecipePlans = ReadRecipePlans(n["recipePlans"]),
                    UsedIn = ReadUses(n["usedIn"]),
                    Description = n["description"]?.ToString() ?? "暂无说明",
                    ShortDescription = n["shortDescription"]?.ToString() ?? "",
                    SearchText = n["searchText"]?.ToString() ?? "",
                    IconPath = icon
                };
            }
            _catalog = root["items"]!.AsArray().Select(ReadItem).OrderByDescending(i => i.StageRank).ThenBy(i => i.Name, StringComparer.CurrentCulture).ToList();
            _byId = (root["lookupItems"] ?? root["items"])!.AsArray().Select(ReadItem).ToDictionary(i => i.Id, StringComparer.Ordinal);
            InitializeEquipmentGuide();
            InitializeGuides();
            _decoder = await Task.Run(() => new SaveDecoder());
            DesktopDiagnostics.Mark(DesktopDiagnosticStage.ResourcesLoaded);
            _ready = true;
            InitializeSimulation();
            FilterEquipment();
            SelectEquipment("I0ET");
            if (_simulation is not null) StatusText.Text = $"{DesktopEdition.Name} · 已载入 {_catalog.Count:N0} 项地图资料 · 存档只读解析";
            if (!App.AutomationMode)
            {
                DesktopDiagnostics.Mark(DesktopDiagnosticStage.CloudInitializationStarted);
                await InitializeDesktopApiAsync();
                await InitializeArchivesAsync();
                DesktopDiagnostics.Mark(DesktopDiagnosticStage.ArchivesLoaded);
            }
            DesktopDiagnostics.Mark(DesktopDiagnosticStage.InitializationCompleted);
        }
        catch (Exception ex)
        {
            DesktopDiagnostics.ReportException(DesktopDiagnosticStage.InitializationStarted, ex);
            StatusText.Text = "内置资料加载失败，请重新构建或更新程序。";
            if (!App.AutomationMode) MessageBox.Show(this, ex.Message, "资料加载失败", MessageBoxButton.OK, MessageBoxImage.Error);
            else throw;
        }
    }

    private void FilterEquipment()
    {
        if (!_ready) return;
        var terms = SearchBox.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var category = CategoryBox.SelectedItem is ContentControl c ? c.Content?.ToString() : CategoryBox.SelectedItem?.ToString();
        var selected = (EquipmentList.SelectedItem as CatalogItem)?.Id;
        var items = _catalog.Where(i => (string.IsNullOrEmpty(category) || category == "全部" ||
            (category == "目标" ? _targets.Contains(i.Id) : i.Category == category || i.Subcategory == category))
            && terms.All(t => (i.Name + " " + i.Id + " " + i.SearchText + " " + i.Description + " " + i.EffectsText + " " + _badgeBonuses.GetValueOrDefault(i.Id)
                + " " + _equipmentExplanationSearch.GetValueOrDefault(i.Id)).Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
        EquipmentList.ItemsSource = items;
        CatalogCount.Text = $"{items.Count:N0} / {_catalog.Count:N0} 项";
        EquipmentList.SelectedItem = items.FirstOrDefault(i => i.Id == selected) ?? items.FirstOrDefault();
        if (items.Count == 0)
        {
            DetailName.Text = "没有找到匹配的物品";
            DetailSource.Text = ""; DetailCategory.Text = ""; DetailDescription.Text = "试试物品名称、材料名或属性关键词，也可以清空筛选条件。";
            DetailIcon.Source = null;
            RecipeContent.Children.Clear(); RecipeTab.Content = "合成与用途";
            DetailEffects.Text = ""; DetailEffectsPanel.Visibility = Visibility.Collapsed;
            DetailBadgeBonuses.Text = ""; DetailBadgePanel.Visibility = Visibility.Collapsed;
            DetailResearchContent.Children.Clear(); DetailResearchPanel.Visibility = Visibility.Collapsed;
            DetailResearchExpander.IsExpanded = false;
            DetailOriginalDescription.Visibility = Visibility.Visible;
            DetailOriginalDescription.Header = "搜索提示"; DetailOriginalDescription.IsExpanded = true;
            ShowEffectsButton.Visibility = Visibility.Collapsed;
            TargetButton.IsEnabled = false;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => FilterEquipment();
    private void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => FilterEquipment();
    private void EquipmentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EquipmentList.SelectedItem is not CatalogItem item) return;
        DetailName.Text = item.Name;
        DetailSource.Text = "TWRPG 0.94a";
        DetailCategory.Text = item.CatalogLabel;
        DetailCategory.ToolTip = item.StageNote;
        DetailDescription.Text = string.IsNullOrWhiteSpace(item.Description) ? "当前地图没有提供该物品的独立说明。" : item.Description;
        ItemDescriptionRendering.Apply(DetailDescription, DetailDescription.Text, item.DescriptionRuns,
            System.Windows.Media.Color.FromRgb(0x4F, 0x35, 0x23),
            [System.Windows.Media.Color.FromRgb(0xF1, 0xE2, 0xBD), System.Windows.Media.Color.FromRgb(0xFC, 0xF4, 0xE2)]);
        DetailIcon.Source = LoadImage(item.IconPath);
        DetailEffects.Text = item.EffectsText;
        ItemDescriptionRendering.Apply(DetailEffects, item.EffectsText, item.EffectsRuns,
            System.Windows.Media.Color.FromRgb(0x4F, 0x35, 0x23),
            [System.Windows.Media.Color.FromRgb(0xF1, 0xE2, 0xBD), System.Windows.Media.Color.FromRgb(0xFC, 0xF4, 0xE2)], distinguishSections: true);
        DetailDescription.ToolTip = ItemDescriptionRendering.PresentationNote;
        DetailEffects.ToolTip = item.EffectsSource + "\n" + ItemDescriptionRendering.PresentationNote;
        DetailEffectsSource.Text = item.EffectsSource;
        DetailBadgeBonuses.Text = _badgeBonuses.GetValueOrDefault(item.Id, "");
        DetailBadgePanel.Visibility = string.IsNullOrWhiteSpace(DetailBadgeBonuses.Text) ? Visibility.Collapsed : Visibility.Visible;
        DetailEffectsPanel.Visibility = string.IsNullOrWhiteSpace(item.EffectsText) ? Visibility.Collapsed : Visibility.Visible;
        DetailOriginalDescription.Visibility = Visibility.Visible;
        RenderEquipmentExplanation(item);
        ShowEffectsButton.Visibility = _equipmentExplanations.ContainsKey(item.Id) ? Visibility.Visible : DetailEffectsPanel.Visibility;
        DetailScroll.ScrollToTop();
        RenderRecipes(item);
        RecipeScroll.ScrollToTop();
        UpdateTargetControls();
    }

    private void ShowEffects_Click(object sender, RoutedEventArgs e)
    {
        DetailTabs.SelectedIndex = 0;
        if (DetailEffectsPanel.Visibility == Visibility.Visible)
        {
            DetailOriginalDescription.IsExpanded = true;
            UpdateLayout();
            DetailEffectsPanel.BringIntoView();
        }
        else if (DetailResearchPanel.Visibility == Visibility.Visible)
        {
            DetailResearchExpander.IsExpanded = true;
            UpdateLayout();
            DetailResearchPanel.BringIntoView();
        }
    }

    private static System.Windows.Media.ImageSource? LoadImage(string path) => ResourceImage.Load(path);

    private void EquipmentNav_Click(object sender, RoutedEventArgs e) => MainPages.SelectedIndex = 0;
    private void SavesNav_Click(object sender, RoutedEventArgs e)
    {
        MainPages.SelectedIndex = 1;
        if (DesktopEdition.SupportsCloud && !DesktopOnlineMode && _simulation?.Saves.Any(s => s.OwnerId == CloudSimulation.PreviewOwnerId && s.SyncEnabled) == true)
            RunSimulationAction(() => { ConfirmCloudSharing(CloudSimulation.PreviewOwnerId); RefreshImportedSaveControls(); });
    }
    private void HeroesNav_Click(object sender, RoutedEventArgs e) => MainPages.SelectedIndex = 2;
    private void BossesNav_Click(object sender, RoutedEventArgs e) => MainPages.SelectedIndex = 3;
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => MainPages.SelectedIndex = 4;
    private void ConceptsNav_Click(object sender, RoutedEventArgs e) => MainPages.SelectedIndex = 5;
    private async void ImportSave_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "选择游戏导出的角色或账号 TXT 存档", Filter = "游戏导出存档 (*.txt)|*.txt", Multiselect = true };
        if (picker.ShowDialog(this) == true) await ImportFilesAsync(picker.FileNames);
    }

    public async Task ImportFilesAsync(IEnumerable<string> paths, bool showErrors = true)
    {
        if (!_ready || _decoder is null || _importing || !DesktopFeaturesAllowed) return;
        _importing = true;
        var errors = new List<string>();
        try
        {
            foreach (var path in paths)
            {
                try
                {
                    StatusText.Text = $"正在导入 {Path.GetFileName(path)}……";
                    var result = await Task.Run(() => _decoder.ParseGameExport(path));
                    _importSourcePaths.Add(Path.GetFullPath(path));
                    var doc = AcceptParsedSave(result, Path.GetFullPath(path));
                    SelectSaveDocument(doc);
                }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}：{ex.Message}"); }
            }
            MainPages.SelectedIndex = 1;
            RefreshSimulationContext();
            StatusText.Text = errors.Count == 0 ? $"导入完成 · 当前导入 {_saves.Count} 份存档 · 原文件保持不变" : $"有 {errors.Count} 份文件未能导入，已成功导入的结果仍可查看";
            if (errors.Count > 0 && showErrors && !App.AutomationMode)
                MessageBox.Show(this, string.Join("\n\n", errors), "部分存档未能导入", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (errors.Count > 0 && App.AutomationMode) throw new InvalidDataException(string.Join("\n", errors));
        }
        finally { _importing = false; }
    }

    private async void ReparseSave_Click(object sender, RoutedEventArgs e)
    {
        if (SaveList.SelectedItem is SavedDocument doc) await ImportFilesAsync([doc.SourcePath]);
        else StatusText.Text = "请先导入并选择一份存档。";
    }

    private void SaveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingSavePicker || SaveList.SelectedItem is not SavedDocument doc) return;
        ShowSelectedSave(doc);
    }

    private void ShowSelectedSave(SavedDocument doc)
    {
        var result = doc.Result;
        SaveTitle.Text = doc.DisplayName;
        var hero = result["hero"] as JsonObject;
        if (hero is not null)
        {
            var attributes = hero["attributes_encoded"];
            SaveSummary.Text = attributes is null ? (doc.IsDemo ? "示例存档 · 用于体验主副档与云同步流程" : "旧记录摘要，重新导入或扫描来源后可查看完整属性")
                : $"金币 {hero["gold_restored"]}  ·  基础分配：力量 {attributes["str"]} / 敏捷 {attributes["agi"]} / 智力 {attributes["int"]}";
        }
        else SaveSummary.Text = "账号共有的徽章与收藏；持有记录独立于角色当前佩戴状态。";
        SaveNote.Text = DesktopEdition.IsCommunity ? "属性显示存档中的基础分配，本地记录保存在本机。"
            : DesktopOnlineMode ? "属性显示存档中的基础分配。" : "云端为本地模拟，尚未连接服务器。属性显示存档中的基础分配。";
        _saveRows = BuildSaveRows(result);
        _changingSections = true;
        SaveSectionBox.ItemsSource = new[] { "全部", "携带物品", "背包", "仓库", "翅膀藏品", "其他收藏" };
        SaveSectionBox.SelectedIndex = 0;
        _changingSections = false;
        FilterSaveItems();
        FollowSelectedSaveTarget();
        RefreshImportedSaveControls();
    }

    private List<SaveItemRow> BuildSaveRows(JsonObject result)
    {
        var rows = new List<SaveItemRow>();
        foreach (var (key, label) in new[] { ("inventory", "携带物品"), ("bag", "背包"), ("storage", "仓库") })
            if (result["inventories"]?[key] is JsonArray items)
                rows.AddRange(items.Select(i => MakeRow(i!, label, i!["quantity"]?.ToString() ?? "1", "")));
        if (result["wardrobe"] is JsonArray categories)
            foreach (var c in categories)
                foreach (var i in c!["items"]!.AsArray())
                    rows.Add(MakeRow(i!, c["category_name"]?.ToString() ?? $"收藏 {c["category_id"]}", "1",
                        i!["enabled"] is null ? "账号持有" : i["enabled"]!.GetValue<bool>() ? "已启用" : "已拥有 · 未启用"));
        return rows;
    }

    private SaveItemRow MakeRow(JsonNode item, string group, string quantity, string state)
    {
        var id = item["rawcode"]!.ToString();
        var name = item["name"]?.ToString();
        return new SaveItemRow { Id = id, Name = string.IsNullOrWhiteSpace(name) || name == id ? "未命名物品" : name, Group = group,
            QuantityLabel = quantity, State = state, IconPath = _byId.GetValueOrDefault(id)?.IconPath ?? "" };
    }

    private void SaveSectionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_changingSections) FilterSaveItems(); }
    private void FilterSaveItems()
    {
        if (SaveItemsList is null || SaveSectionBox is null) return;
        var selected = SaveSectionBox.SelectedItem?.ToString() ?? "全部";
        SaveItemsList.Visibility = Visibility.Visible;
        SaveGoalsPanel.Visibility = Visibility.Collapsed;
        SaveContentTitle.Text = "存档内物品";
        var items = _saveRows.Where(i => selected == "全部" || (selected == "翅膀藏品" ? i.Name.Contains("藏品") : selected == "其他收藏" ? i.Group is not ("携带物品" or "背包" or "仓库") && !i.Name.Contains("藏品") : i.Group == selected)).ToList();
        SaveItemsList.ItemsSource = items;
        SaveItemCount.Text = $"{items.Count} / {_saveRows.Count} 项 · 相同物品的独立记录保留";
    }

    private void SaveItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (SaveItemsList.SelectedItem is SaveItemRow row) StatusText.Text = $"{row.Name} · {row.Group}"; }
    private void LocateItem_Click(object sender, RoutedEventArgs e)
    {
        if (SaveItemsList.SelectedItem is not SaveItemRow row) { StatusText.Text = "请先选择一件物品。"; return; }
        var displayId = _byId.GetValueOrDefault(row.Id)?.DisplayId;
        if (displayId is null || !_catalog.Any(i => i.Id == displayId)) { StatusText.Text = "物品目录暂未收录该物品的独立说明，存档持有记录仍保留。"; return; }
        SearchBox.Text = ""; CategoryBox.SelectedIndex = 0; SelectEquipment(displayId); MainPages.SelectedIndex = 0;
    }

    private void SelectEquipment(string id)
    {
        var displayId = _byId.GetValueOrDefault(id)?.DisplayId ?? id;
        var item = _catalog.FirstOrDefault(i => i.Id == displayId);
        if (item is not null) { EquipmentList.SelectedItem = item; EquipmentList.ScrollIntoView(item); }
    }

    private void ExportSave_Click(object sender, RoutedEventArgs e)
    {
        if (SaveList.SelectedItem is not SavedDocument doc) { StatusText.Text = "请先选择一份已解析存档。"; return; }
        var picker = new SaveFileDialog { Title = "导出解析结果", FileName = Path.GetFileNameWithoutExtension(doc.SourcePath) + ".parsed.json", Filter = "结构化结果 (*.json)|*.json" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            ValidateExportPath(picker.FileName);
            File.WriteAllText(picker.FileName, doc.Result.ToJsonString(App.JsonOptions), new UTF8Encoding(false));
            StatusText.Text = "已导出 JSON；不包含用户名或载入原码。";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ValidateExportPath(string path)
    {
        if (_importSourcePaths.Contains(Path.GetFullPath(path)))
            throw new InvalidOperationException("解析结果不能覆盖导入的原始存档。请选择其他文件名。");
        if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("解析结果请使用 .json 后缀，不能保存为游戏存档 TXT。");
    }

    private void AddTarget_Click(object sender, RoutedEventArgs e)
    {
        if (EquipmentList.SelectedItem is CatalogItem item)
            ChangeEquipmentTarget(item.Id, !_targets.Contains(item.Id));
    }
}
