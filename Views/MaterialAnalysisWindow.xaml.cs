using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop.Views;

public partial class MaterialAnalysisWindow : Window
{
    private readonly MaterialPlanner planner;
    private readonly GuideCatalog guide;
    private readonly Func<CloudSave?> source;
    private MaterialPlanResult plan = new();
    private MaterialSynthesisResult synthesis = new();
    private string view = "materials";
    private bool refreshing;
    public event Action<string>? ItemRequested;
    public MaterialPlanResult CurrentPlan => plan;
    internal IReadOnlyList<MaterialPlanRow> VisibleRows => MaterialRows.Items.OfType<DisplayRow>().Select(x => x.Row).ToList();
    internal string SummaryText => PlanSummary.Text;
    internal string DetailText => RowDetails.Text;
    internal string? SelectedStage => StageFilter.SelectedItem as string;
    internal bool CanOpenSelectedItem => OpenItemButton.IsEnabled;
    internal bool SummaryShownInDetails => MaterialSummaryColumn.Visibility == Visibility.Collapsed;
    internal bool RoutesExpanded => BranchExpander.IsExpanded;
    internal int RouteSelectorCount => Descendants<ComboBox>(BranchRows).Count();
    internal string ChoiceHeading => BranchExpander.Header?.ToString() ?? "";
    internal MaterialSynthesisResult CurrentSynthesisTree => synthesis;
    internal bool SynthesisVisible => SynthesisPanel.IsVisible;
    internal int SynthesisSchemeCount => synthesis.Schemes.Count;
    internal int SelectedSynthesisScheme => TreeSchemeBox.SelectedIndex;
    internal string SynthesisExplanation => TreeExplanation.Text;
    internal string? SelectedTreeItemId => (SynthesisRoots.SelectedItem as TreeViewItem)?.Tag is MaterialSynthesisNode node ? node.ItemId : null;
    internal bool TreeSchemeSelectorVisible => TreeSchemeSelector.IsVisible;
    internal IReadOnlyList<MaterialSynthesisNode> VisibleTreeTargets => SynthesisRoots.Items.OfType<TreeViewItem>()
        .Select(x => x.Tag).OfType<MaterialSynthesisNode>().ToList();
    internal IReadOnlyList<MaterialSynthesisNode> RealizedTreeNodes => Descendants<TreeViewItem>(SynthesisRoots)
        .Select(x => x.Tag).OfType<MaterialSynthesisNode>().ToList();
    internal IReadOnlyList<TextBlock> VisibleNumericTextBlocks => Descendants<TextBlock>(MaterialRows)
        .Where(x => x.IsVisible && System.Text.RegularExpressions.Regex.IsMatch(x.Text, "^[0-9]+(～[0-9]+)?$")).ToList();
    internal bool ChoicesInsideMaterialCard => BranchExpander.Parent is Grid panel && ReferenceEquals(panel, MaterialRows.Parent) && panel.Parent is Border;
    internal int ChoiceImageCount => Descendants<Image>(BranchRows).Count(x => x.Source is not null && x.ActualWidth > 0 && x.ActualHeight > 0);
    internal int EnabledChoiceButtonCount => Descendants<Button>(BranchRows).Count(x => x.IsEnabled && x.IsVisible);
    internal static bool CanAnalyze(CloudSave? save) => MaterialNeedsExporter.CanAnalyzeLocally(save);
    internal double HeaderHeight => HeaderPanel.ActualHeight;
    internal double TargetListHeight => TargetScroller.ActualHeight;
    internal double RowViewportHeight => Descendants<ScrollContentPresenter>(MaterialRows).FirstOrDefault()?.ActualHeight ?? 0;
    internal int FullyVisibleRowCount
    {
        get
        {
            var viewport = Descendants<ScrollContentPresenter>(MaterialRows).FirstOrDefault();
            if (viewport is null) return 0;
            return Descendants<DataGridRow>(MaterialRows).Count(row =>
            {
                var top = row.TranslatePoint(new Point(), viewport).Y;
                return row.ActualHeight > 0 && top >= -0.5 && top + row.ActualHeight <= viewport.ActualHeight + 0.5;
            });
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    public MaterialAnalysisWindow(MaterialPlanner planner, GuideCatalog guide, Func<CloudSave?> source)
    {
        this.planner = planner; this.guide = guide; this.source = source;
        InitializeComponent(); RefreshPlan();
    }

    public void RefreshPlan()
    {
        refreshing = true;
        try
        {
            var save = source();
            if (!CanAnalyze(save))
            {
                plan = new(); synthesis = new(); TargetItems.Children.Clear(); BranchRows.Children.Clear(); BranchExpander.Visibility = Visibility.Collapsed;
                TreeSchemeBox.ItemsSource = null; SynthesisRoots.Items.Clear();
                TreeExplanation.Text = "请选择当前地图版本的有效存档；未展示旧合成树。";
                BranchExpander.IsExpanded = false;
                SaveTitle.Text = save is null ? "该存档已不存在，请关闭后重新选择。" :
                    save.IsDemo ? "演示存档不参与真实配方分析。" : "该存档的解析结果或地图版本不适用于当前材料分析。";
                PlanSummary.Text = "未计算真实配方，请选择当前地图版本的有效游戏存档。";
                StageFilter.ItemsSource = new[] { "全部阶段" }; StageFilter.SelectedIndex = 0;
                MaterialRows.ItemsSource = null; return;
            }
            if (save is null) return;
            SaveTitle.Text = $"{save.GameAccount} · {save.Profession} · {save.Name}（{(save.IsMain ? "主档" : "副档")}）  /  {DateTime.Now:HH:mm:ss} 读取";
            TargetItems.Children.Clear();
            foreach (var goal in save.Targets)
            {
                var item = planner.GetItem(goal.ItemId);
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new Image { Source = ResourceImage.Load(item?.Icon ?? ""), Width = 24, Height = 24, Margin = new Thickness(0,0,7,0) });
                var label = $"{item?.Name ?? goal.ItemName} × {goal.Quantity}";
                content.Children.Add(new TextBlock { Text = label, ToolTip = label, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var button = new Button { Content = content, Margin = new Thickness(0,0,6,4), Padding = new Thickness(7,3,7,3), MinHeight = 30 };
                button.ToolTip = "点击目标展开合成树；选中树节点后可查看物品信息。";
                button.Click += (_, _) => ExpandTargetTree(goal.ItemId); TargetItems.Children.Add(button);
            }
            var goals = save.Targets.Select(x => new MaterialGoal(x.ItemId, x.Quantity)).ToList();
            var inventory = MaterialNeedsExporter.Inventory(save); var wardrobe = MaterialNeedsExporter.Wardrobe(save);
            plan = planner.Calculate(goals, inventory, wardrobe);
            synthesis = MaterialSynthesisTree.Build(planner, goals, inventory, wardrobe, plan, preferRecipeView: true);
            TreeSchemeBox.ItemsSource = synthesis.Schemes; TreeSchemeBox.SelectedIndex = synthesis.Schemes.Count > 0 ? 0 : -1;
            TreeExplanation.Text = synthesis.Message;
            BuildBranches();
            var oldStage = StageFilter.SelectedItem as string;
            var stages = plan.Materials.Concat(plan.Steps).Select(x => (x.Stage, x.StageRank)).Distinct()
                .OrderByDescending(x => x.StageRank).Select(x => string.IsNullOrEmpty(x.Stage) ? "未标注阶段" : x.Stage).Distinct().ToList();
            stages.Insert(0, "全部阶段"); StageFilter.ItemsSource = stages;
            StageFilter.SelectedItem = stages.Contains(oldStage ?? "") ? oldStage : stages[0];
            var unrecorded = plan.Issues.Count(x => x.Code == "unrecorded_prerequisite");
            var otherIssues = plan.Issues.Where(x => x.Code != "unrecorded_prerequisite").Select(x => x.Message).Distinct().ToList();
            PlanSummary.Text = save.Targets.Count == 0 ? "还没有装备目标，请先在物品信息中添加。" :
                $"全部 {save.Targets.Count} 项目标的总体缺口 · {plan.OrdinaryNeeds.Count} 种共同必需材料" +
                (plan.OrGroups.Count > 0 ? plan.OrGroups.Count > 1 ? $" · {plan.OrGroups.Count} 组可选材料，每组选一个方案。" :
                    $" · {plan.OrGroups.Sum(x => x.Options.Count)} " +
                    (plan.OrGroups.All(x => x.Options.All(o => o.Needs.Count <= 1)) ? "项任选其一。" : "个完整组合任选其一。") :
                    plan.IsExact ? " · 已按全部已核配方共用库存计算。" : " · 尚未形成确定总单。") +
                (plan.OrGroups.Count > 0 ? "\n表中共同需求全部需要，再从下方每组选一个完整方案。每组只取一项，方案内的材料全部需要；不同方案不能拆取或相加。" : "") +
                (unrecorded > 0 ? $"\n{unrecorded} 项未收录更早前置，获取方式仍待补充。" : "") +
                (otherIssues.Count > 0 ? "\n" + string.Join("；", otherIssues.Take(3)) + (otherIssues.Count > 3 ? "；另有其他计算问题。" : "") : "");
        }
        finally { refreshing = false; RenderRows(); }
    }

    private void BuildBranches()
    {
        BranchRows.Children.Clear();
        if (plan.OrGroups.Count > 0)
            BranchRows.Children.Add(new TextBlock { Text = "共同需求已在材料表中列出一次。以下每组选一个方案，再加上共同需求。每组只选一项，方案内的材料全部需要；同一组的不同方案不能拆开混选。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,5) });
        Button MaterialButton(MaterialNeed need)
        {
            var label = need.Name + " × " + need.Quantity + (need.IsReusable ? "（衣柜解锁）" : "");
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Image { Source = ResourceImage.Load(planner.GetItem(need.ItemId)?.Icon ?? ""), Width = 25, Height = 25, Margin = new Thickness(0,0,6,0) });
            content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var item = new Button { Content = content, Padding = new Thickness(6,2,6,2), MinHeight = 29, Margin = new Thickness(0,0,5,2),
                ToolTip = "点击查看物品与获取信息。" };
            item.Click += (_, _) => RequestItem(need.ItemId); return item;
        }
        foreach (var group in plan.OrGroups)
        {
            var simple = group.Options.All(option => option.Needs.Count <= 1);
            if (plan.OrGroups.Count > 1)
                BranchRows.Children.Add(new TextBlock { Text = "第 " + (plan.OrGroups.IndexOf(group) + 1) + " 组 · " +
                    (simple ? (group.Options.Count == 2 ? "二选一" : "任选一项") : "任选一个完整方案"), FontWeight = FontWeights.SemiBold });
            if (simple)
            {
                var row = new WrapPanel();
                foreach (var option in group.Options)
                {
                    if (row.Children.Count > 0) row.Children.Add(new TextBlock { Text = "或", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3,0,8,2) });
                    if (option.Needs.Count == 0)
                    {
                        row.Children.Add(new TextBlock { Text = "无需额外材料", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4,0,5,2),
                            ToolTip = "这一项无需普通需求以外的新材料；普通需求仍全部需要。" +
                                (option.Restrictions.Count > 0 ? "\n" + string.Join("\n", option.Restrictions) : "") });
                    }
                    else
                    {
                        var item = MaterialButton(option.Needs[0]);
                        if (option.Restrictions.Count > 0) item.ToolTip = string.Join("\n", option.Restrictions) + "\n点击查看物品。";
                        row.Children.Add(item);
                    }
                }
                BranchRows.Children.Add(row);
                continue;
            }
            foreach (var option in group.Options)
            {
                var row = new WrapPanel { Margin = new Thickness(0,2,0,2) };
                row.Children.Add(new TextBlock { Text = option.Label + "（全部需要）：", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,7,0) });
                if (option.Needs.Count == 0) row.Children.Add(new TextBlock { Text = "除普通需求外，无需新增材料（已有库存可抵扣）。", TextWrapping = TextWrapping.Wrap });
                foreach (var need in option.Needs) row.Children.Add(MaterialButton(need));
                if (option.Restrictions.Count > 0) row.ToolTip = string.Join("\n", option.Restrictions);
                BranchRows.Children.Add(row);
            }
        }
        if (!plan.IsExact && plan.UnexpandedRecipes.Count > 0)
        {
            BranchRows.Children.Add(new TextBlock { Text = "这些路线还没有算出扣除库存后的材料数量。库存只能抵扣一次，下列可选路线不能相加。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,8) });
            foreach (var output in plan.UnexpandedRecipes.GroupBy(x => x.OutputId))
            {
                var name = planner.GetItem(output.Key)?.Name ?? "合成物品";
                var alternatives = output.Select(x => string.Join(" ＋ ", x.Ingredients.Select(i => (planner.GetItem(i.ItemId)?.Name ?? i.Name) + " × " + i.Quantity + (i.IsReusable ? "（可复用）" : ""))));
                BranchRows.Children.Add(new TextBlock { Text = name + "：" + string.Join(" 或 ", alternatives), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,3,0,3) });
            }
        }
        BranchExpander.Visibility = BranchRows.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var singleGroup = plan.OrGroups.Count == 1 ? plan.OrGroups[0] : null;
        BranchExpander.Header = !plan.IsExact ? "配方关系（缺口数量待计算）" :
            singleGroup is not null && singleGroup.Options.All(x => x.Needs.Count <= 1) ?
                singleGroup.Options.Count == 2 ? "二选一" : "任选一项" :
            singleGroup?.Options.Count == 2 ? "组合二选一（每个组合内全部需要）" :
                plan.OrGroups.Count > 1 ? "可选材料（每组选一个方案）" : "完整组合任选其一";
        BranchExpander.IsExpanded = false;
    }

    private void RenderRows()
    {
        if (MaterialRows is null || refreshing) return;
        BranchExpander.Visibility = view == "materials" && BranchRows.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SynthesisPanel.Visibility = view == "steps" ? Visibility.Visible : Visibility.Collapsed;
        MaterialRows.Visibility = view == "steps" ? Visibility.Collapsed : Visibility.Visible;
        var targetIds = synthesis.Schemes.FirstOrDefault()?.Targets.Select(x => x.ItemId).ToHashSet(StringComparer.Ordinal) ?? [];
        IEnumerable<MaterialPlanRow> rows = view == "steps" ? plan.Steps.Where(x => targetIds.Contains(x.ItemId)) : view == "allocations" ? plan.Allocations : plan.Materials;
        if (MissingOnly.IsChecked == true && view == "materials") rows = rows.Where(x => x.Missing > 0);
        if (StageFilter.SelectedItem is string stage && stage != "全部阶段") rows = rows.Where(x => (string.IsNullOrEmpty(x.Stage) ? "未标注阶段" : x.Stage) == stage);
        MaterialRows.ItemsSource = rows.OrderByDescending(x => x.StageRank).ThenByDescending(x => x.Missing).ThenBy(x => x.Name).Select(x => new DisplayRow(x, planner)).ToList();
        MissingOnly.IsEnabled = view == "materials";
        if (view == "steps") RenderSynthesisTree();
        foreach (var button in ViewButtons.Children.OfType<Button>()) button.Style = (Style)FindResource((string?)button.Tag == view ? "PrimaryButton" : typeof(Button));
        RowDetails.Text = view == "steps" ? "点击目标展开合成树；选中节点可查看当前档抵扣、配方限制与物品信息。" : "选择材料可查看用途路径、获取信息与合成限制。"; OpenItemButton.IsEnabled = false;
    }

    private void MaterialRows_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MaterialRows.SelectedItem is not DisplayRow row) return;
        var detail = row.Row;
        var drops = guide.FindDrops(detail.ItemId).Select(x => x.Boss.Name + "：" + x.Drop.BaseChanceText);
        RowDetails.Text = string.Join("\n", new[] { detail.MissingMaximum != detail.Missing ? "此数量为不同完整组合的可能区间；不能与其他材料分别取最小值拼成新组合。" : "", detail.Status, string.Join("；", detail.Paths), detail.RestrictionsText, string.Join("；", drops) }.Where(x => !string.IsNullOrWhiteSpace(x)));
        OpenItemButton.IsEnabled = planner.GetItem(detail.ItemId) is { IsCosmetic: false };
    }
    private void Filter_Changed(object sender, RoutedEventArgs e) => RenderRows();
    private void AnalysisWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (MaterialSummaryColumn is not null)
            MaterialSummaryColumn.Visibility = ActualWidth < 940 ? Visibility.Collapsed : Visibility.Visible;
    }
    internal void ShowView(string targetView)
    {
        if (targetView is not ("materials" or "steps" or "allocations")) throw new ArgumentOutOfRangeException(nameof(targetView));
        if (targetView == "steps" && view != targetView) StageFilter.SelectedIndex = 0;
        view = targetView; RenderRows();
    }
    internal void ExpandChoices(bool value = true) => BranchExpander.IsExpanded = value;
    internal bool SelectSynthesisScheme(int index)
    {
        if (index < 0 || index >= synthesis.Schemes.Count) return false;
        TreeSchemeBox.SelectedIndex = index; return true;
    }
    internal bool ExpandTargetTree(string itemId)
    {
        StageFilter.SelectedIndex = 0;
        ShowView("steps");
        var id = planner.Canonical(itemId);
        var target = SynthesisRoots.Items.OfType<TreeViewItem>().FirstOrDefault(x => x.Tag is MaterialSynthesisNode node && node.ItemId == id);
        if (target is null) return false;
        target.IsExpanded = true; target.IsSelected = true; BringSynthesisTargetIntoView(target); return true;
    }
    private void BringSynthesisTargetIntoView(TreeViewItem item)
    {
        item.BringIntoView();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (!item.IsVisible) return;
            item.UpdateLayout();
            var scroll = Descendants<ScrollViewer>(SynthesisRoots).FirstOrDefault();
            var viewport = Descendants<ScrollContentPresenter>(SynthesisRoots).FirstOrDefault();
            if (scroll is null || viewport is null || viewport.ActualHeight <= 0) return;
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + item.TranslatePoint(new Point(), viewport).Y);
        }));
    }
    public void OpenSynthesis(string? targetItemId = null)
    {
        ShowView("steps");
        if (string.IsNullOrEmpty(targetItemId)) return;
        ExpandTargetTree(targetItemId);
        if (!IsLoaded)
        {
            RoutedEventHandler? loaded = null;
            loaded = (_, _) =>
            {
                Loaded -= loaded;
                ExpandTargetTree(targetItemId);
            };
            Loaded += loaded;
        }
    }
    internal bool ExpandSynthesisPath(string itemId)
    {
        if (TreeSchemeBox.SelectedItem is not MaterialSynthesisScheme scheme) return false;
        var id = planner.Canonical(itemId);
        List<MaterialSynthesisNode>? Find(MaterialSynthesisNode node)
        {
            if (node.ItemId == id) return [node];
            node.EnsureExpanded();
            foreach (var child in node.Children)
                if (Find(child) is { } path) { path.Insert(0, node); return path; }
            return null;
        }
        foreach (var root in scheme.Targets)
        {
            if (Find(root) is not { } path) continue;
            var items = SynthesisRoots.Items;
            TreeViewItem? selected = null;
            foreach (var node in path)
            {
                selected = items.OfType<TreeViewItem>().FirstOrDefault(x => x.Tag is MaterialSynthesisNode value && value.Key == node.Key);
                if (selected is null) return false;
                selected.IsExpanded = true; items = selected.Items;
            }
            if (selected is null) return false;
            selected.IsSelected = true; selected.BringIntoView(); return true;
        }
        return false;
    }
    private void TreeScheme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!refreshing && view == "steps") RenderSynthesisTree();
    }
    private void RenderSynthesisTree()
    {
        SynthesisRoots.Items.Clear();
        TreeSchemeSelector.Visibility = !synthesis.IsRecipeView && synthesis.Schemes.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (TreeSchemeBox.SelectedItem is not MaterialSynthesisScheme scheme) return;
        var targets = scheme.Targets.AsEnumerable();
        if (StageFilter.SelectedItem is string stage && stage != "全部阶段")
            targets = targets.Where(x => (string.IsNullOrEmpty(x.Stage) ? "未标注阶段" : x.Stage) == stage);
        foreach (var target in targets) SynthesisRoots.Items.Add(CreateSynthesisItem(target));
    }
    private TreeViewItem CreateSynthesisItem(MaterialSynthesisNode node)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = node.IsGoal ? new Thickness(1,4,4,4) : new Thickness(1,2,4,2) };
        if (!node.IsRecipe) row.Children.Add(new Image { Source = ResourceImage.Load(node.Icon), Width = 29, Height = 29, Margin = new Thickness(0,0,8,0) });
        var text = new StackPanel { MaxWidth = 650 };
        var nameRow = new WrapPanel();
        nameRow.Children.Add(new TextBlock { Text = node.IsRecipe ? node.Name : node.Name + " × " + node.Required,
            FontWeight = node.IsGoal || node.IsRecipe ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(Color.FromRgb(71,44,26)), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(node.ChoiceLabel))
            nameRow.Children.Add(new Border
            {
                Margin = new Thickness(8,0,0,0), Padding = new Thickness(6,1,6,1), CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromRgb(228,207,163)),
                Child = new TextBlock { Text = node.ChoiceLabel, Foreground = new SolidColorBrush(Color.FromRgb(117,54,24)),
                    FontWeight = FontWeights.SemiBold, FontSize = 11 },
                ToolTip = node.IsRecipe ? "同组完整组合任选一个；组合内的材料全部需要，不能拆开混选。" : "同组材料任选一项；其他共同材料仍需要。"
            });
        text.Children.Add(nameRow);
        string SavedText()
        {
            var saved = node.IsReusable ? node.Owned > 0 ? "当前档衣柜已解锁，可复用" : "当前档衣柜尚未解锁" :
                $"当前档已保存 {node.Owned}" + (node.AllocationKnown ? $" · {(synthesis.IsRecipeView ? "此处" : "本组合")}抵扣 {node.Allocated}" : " · 共用库存，实际抵扣看总表");
            return saved + (node.AllocationKnown && node.Remaining > 0 && !node.IsSharedReference ?
                $" · {(node.CanExpand ? "尚需合成" : "尚需获取")} {node.Remaining}" : "");
        }
        TextBlock? savedLine = null, statusLine = null;
        if (!node.IsRecipe)
        {
            savedLine = new TextBlock { Text = SavedText(), TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(node.AllocationKnown && node.Remaining == 0 ? Color.FromRgb(47,103,55) : Color.FromRgb(141,73,37)) };
            statusLine = new TextBlock { Text = node.Status, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(111,83,56)), FontSize = 11 };
            text.Children.Add(savedLine);
            if (node.IsGoal) text.Children.Add(statusLine);
        }
        row.Children.Add(text);
        var item = new TreeViewItem { Tag = node, IsExpanded = false,
            ToolTip = string.IsNullOrWhiteSpace(node.RestrictionsText) ? node.Status : node.Status + "\n" + node.RestrictionsText };
        var toggle = new Button { Content = row, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(2,0,2,0), HorizontalContentAlignment = HorizontalAlignment.Left,
            ToolTip = node.CanExpand ? "点击展开或收起这项前置；选中后可查看物品信息。" : "选择此项查看当前档库存与合成限制。" };
        System.Windows.Automation.AutomationProperties.SetName(toggle, node.Name + (node.CanExpand ? "，展开或收起合成前置" : "，查看库存状态"));
        toggle.Click += (_, _) =>
        {
            item.IsSelected = true;
            if (node.CanExpand) item.IsExpanded = !item.IsExpanded;
            if (item.IsExpanded && node.IsGoal) BringSynthesisTargetIntoView(item);
        };
        item.Header = toggle;
        // Only the selected scheme's roots and one visible level are realized.
        // Other complete combinations and collapsed subtrees stay inert models.
        if (node.CanExpand)
        {
            item.Items.Add(new TreeViewItem { Header = "展开查看已核前置…" });
            RoutedEventHandler? expand = null;
            expand = (sender, args) =>
            {
                if (!ReferenceEquals(sender, args.OriginalSource)) return;
                item.Expanded -= expand; item.Items.Clear();
                node.EnsureExpanded();
                item.ToolTip = string.IsNullOrWhiteSpace(node.RestrictionsText) ? node.Status : node.Status + "\n" + node.RestrictionsText;
                if (savedLine is not null) savedLine.Text = SavedText();
                if (statusLine is not null) statusLine.Text = node.Status;
                foreach (var child in node.Children) item.Items.Add(CreateSynthesisItem(child));
            };
            item.Expanded += expand;
        }
        if (node.IsRecipe && node.CanExpand) item.IsExpanded = true;
        return item;
    }
    private void SynthesisSelection_Changed(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem { Tag: MaterialSynthesisNode node }) return;
        RowDetails.Text = node.IsRecipe ? node.Name + (node.RestrictionsText.Length > 0 ? "\n" + node.RestrictionsText : "") :
            node.Name + "：" + node.Status + "\n" + (!node.AllocationKnown ? "此处只标出当前档持有数量，不重复抵扣共用库存。全部目标的实际缺口请看材料缺口表。" : node.IsReusable ? "衣柜解锁只需一次，可供多个目标共用。" :
                $"此处抵扣 {node.Allocated}／这条路线用量 {node.Required}。" + (synthesis.IsRecipeView ? "全部目标的实际缺口请看材料缺口表。" : "此完整组合共用一次当前档库存。")) +
            (node.RestrictionsText.Length > 0 ? "\n" + node.RestrictionsText : "");
        OpenItemButton.IsEnabled = !node.IsRecipe && planner.GetItem(node.ItemId) is { IsCosmetic: false };
    }
    internal bool SelectMaterial(string itemId)
    {
        var row = MaterialRows.Items.OfType<DisplayRow>().FirstOrDefault(x => x.Row.ItemId == itemId);
        if (row is null) return false;
        MaterialRows.SelectedItem = row; MaterialRows.ScrollIntoView(row); return true;
    }
    internal void SetMissingOnly(bool value) => MissingOnly.IsChecked = value;
    internal bool SelectStage(string stage)
    {
        if (!StageFilter.Items.Contains(stage)) return false;
        StageFilter.SelectedItem = stage; return true;
    }
    private void View_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string tag }) ShowView(tag); }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshPlan();
    private void OpenItem_Click(object sender, RoutedEventArgs e)
    {
        if (view == "steps" && SynthesisRoots.SelectedItem is TreeViewItem { Tag: MaterialSynthesisNode { IsRecipe: false } node }) RequestItem(node.ItemId);
        else if (MaterialRows.SelectedItem is DisplayRow row) RequestItem(row.Row.ItemId);
    }
    private void RequestItem(string id) { Close(); ItemRequested?.Invoke(id); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private sealed class DisplayRow(MaterialPlanRow row, MaterialPlanner planner)
    {
        public MaterialPlanRow Row => row;
        public string Name => row.Name;
        public string Icon => row.Icon;
        public string Stage => string.IsNullOrEmpty(row.Stage) ? "未标注阶段" : row.Stage;
        public string Required => Range(row.Required, row.RequiredMaximum);
        public long Owned => row.Owned;
        public string Allocated => Range(row.Allocated, row.AllocatedMaximum);
        public string Missing => Range(row.Missing, row.MissingMaximum);
        private static string Range(long minimum, long maximum) => minimum == maximum ? minimum.ToString() : $"{minimum}～{maximum}";
        public string Summary => (row.IsReusable ? "衣柜前置 · 可复用\n" : "") + row.Status + "\n" + string.Join("、", row.GoalIds.Select(id => planner.GetItem(id)?.Name ?? "未收录目标"));
    }
}
