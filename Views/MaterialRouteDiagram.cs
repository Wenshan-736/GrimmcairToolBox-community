using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop.Views;

/// <summary>
/// A lazy visual projection of the existing synthesis tree. It neither allocates
/// inventory nor expands recipes itself. Choice groups retain complete branches.
/// </summary>
internal sealed class MaterialRouteDiagram : Grid
{
    private readonly HashSet<string> expanded = new(StringComparer.Ordinal);
    private readonly List<MaterialSynthesisNode> visibleNodes = [];
    private readonly Dictionary<string, Button> itemButtons = new(StringComparer.Ordinal);
    private MaterialSynthesisNode? root;
    private IReadOnlyDictionary<string, long> inventory = new Dictionary<string, long>();
    private int realized;
    private const int VisibleNodeLimit = 240;
    private static readonly Brush ConnectorBrush = new SolidColorBrush(Color.FromRgb(105, 130, 70));
    public event Action<string>? ItemRequested;
    public event Action? LayoutChanging;
    public event Action? LayoutChanged;
    internal IReadOnlyList<MaterialSynthesisNode> VisibleNodes => visibleNodes;
    internal int ChoiceGroupCount { get; private set; }
    internal int CompleteGroupCount { get; private set; }
    internal bool WasLimited { get; private set; }
    internal string RootItemId => root?.ItemId ?? "";
    internal long DisplayedOwned(MaterialSynthesisNode node) => inventory.GetValueOrDefault(node.ItemId);

    public void SetRoot(MaterialSynthesisNode? value, IReadOnlyDictionary<string, long> savedInventory, bool reset = false)
    {
        if (reset || root?.ItemId != value?.ItemId) expanded.Clear();
        root = value; inventory = savedInventory;
        if (root is not null && expanded.Count == 0) expanded.Add(RootPath(root));
        Render();
    }

    internal bool RequestVisibleItem(string itemId)
    {
        if (!itemButtons.TryGetValue(itemId, out var button)) return false;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return true;
    }

    internal bool ToggleVisibleBranch(string itemId)
    {
        var node = visibleNodes.FirstOrDefault(x => x.ItemId == itemId && x.CanExpand);
        if (node is null) return false;
        var toggle = FindToggle(this, node);
        if (toggle is null) return false;
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return true;
    }

    private static Button? FindToggle(Panel panel, MaterialSynthesisNode node)
    {
        foreach (UIElement child in panel.Children)
        {
            if (child is Button button && ReferenceEquals(button.Tag, node)) return button;
            if (child is Panel nested && FindToggle(nested, node) is { } found) return found;
            if (child is Border { Child: Panel bordered } && FindToggle(bordered, node) is { } inside) return inside;
        }
        return null;
    }

    private static string RootPath(MaterialSynthesisNode node) => "root/" + node.ItemId;
    private static string ChildPath(string path, MaterialSynthesisNode node, int index) => path + "/" + index + ":" + (node.IsRecipe ? node.Name : node.ItemId);

    private void Render()
    {
        LayoutChanging?.Invoke();
        Children.Clear(); visibleNodes.Clear(); itemButtons.Clear(); realized = 0;
        ChoiceGroupCount = 0; CompleteGroupCount = 0; WasLimited = false;
        if (root is null)
            Children.Add(new TextBlock { Text = "当前筛选下没有可显示的目标。", Margin = new Thickness(20), Foreground = Brushes.SaddleBrown });
        else Children.Add(BuildNode(root, RootPath(root)));
        LayoutChanged?.Invoke();
    }

    private FrameworkElement BuildNode(MaterialSynthesisNode node, string path)
    {
        if (++realized > VisibleNodeLimit)
        {
            WasLimited = true;
            return new TextBlock { Text = "…", Margin = new Thickness(20), ToolTip = "当前展开内容较多，请收起部分分支后查看这一支。" };
        }
        if (node.IsRecipe)
        {
            node.EnsureExpanded(); CompleteGroupCount++;
            var group = new StackPanel { Margin = new Thickness(3), MinWidth = 90 };
            group.Children.Add(new TextBlock
            {
                Text = node.Children.Count == 0 ? "无需额外材料" : "组合内全部需要",
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(5, 2, 5, 8), FontSize = 11,
                ToolTip = node.Name + "\n" + node.RestrictionsText
            });
            if (node.Children.Count > 0) group.Children.Add(BuildChildren(node.Children, path));
            return new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(182, 147, 95)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(6), Margin = new Thickness(4, 0, 4, 0),
                Background = new SolidColorBrush(Color.FromArgb(45, 228, 207, 163)), Child = group
            };
        }

        visibleNodes.Add(node);
        var branch = new StackPanel { MinWidth = 88, Margin = new Thickness(4, 0, 4, 0) };
        var card = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 76 };
        var count = DisplayedOwned(node);
        var tooltip = node.Name + "\n当前档持有 " + count + " 件" +
            (node.IsReusable ? node.Owned > 0 ? "\n衣柜已解锁，可复用；解锁状态不计作物品件数。" : "\n衣柜尚未解锁；解锁状态不计作物品件数。" : "") +
            "\n点击图标查看物品信息。";
        var icon = new Button
        {
            Content = new Image { Source = ResourceImage.Load(node.Icon), Width = 46, Height = 46 },
            Width = 56, Height = 56, MinHeight = 0, Padding = new Thickness(4), ToolTip = tooltip,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(239, 220, 177))
        };
        AutomationProperties.SetName(icon, node.Name + "，当前档持有 " + count + " 件，查看物品信息");
        icon.Click += (_, _) => ItemRequested?.Invoke(node.ItemId);
        itemButtons.TryAdd(node.ItemId, icon); card.Children.Add(icon);
        card.Children.Add(new TextBlock
        {
            Text = count.ToString(), HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(count > 0 ? Color.FromRgb(51, 110, 61) : Color.FromRgb(124, 91, 61)),
            FontSize = 13, Margin = new Thickness(0, 2, 0, 1), ToolTip = tooltip
        });
        if (node.CanExpand)
        {
            var toggle = new Button
            {
                Content = expanded.Contains(path) ? "−" : "+", Tag = node, Width = 26, Height = 21, MinHeight = 0,
                Padding = new Thickness(0), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = expanded.Contains(path) ? "收起这项的合成材料" : "展开这项的合成材料"
            };
            AutomationProperties.SetName(toggle, (expanded.Contains(path) ? "收起" : "展开") + node.Name + "的合成材料");
            toggle.Click += (_, _) => { if (!expanded.Remove(path)) expanded.Add(path); Render(); };
            card.Children.Add(toggle);
        }
        branch.Children.Add(card);
        if (expanded.Contains(path) && node.CanExpand)
        {
            node.EnsureExpanded();
            if (node.Children.Count > 0) branch.Children.Add(BuildChildren(node.Children, path));
        }
        return branch;
    }

    private FrameworkElement BuildChildren(IReadOnlyList<MaterialSynthesisNode> children, string path)
    {
        // Group alternatives before drawing connectors: an OR box attaches once
        // to its parent, and every whole option stays inside that same box.
        var units = new List<FrameworkElement>();
        var choices = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index];
            if (string.IsNullOrEmpty(child.ChoiceGroup))
            {
                units.Add(BuildNode(child, ChildPath(path, child, index))); continue;
            }
            if (!choices.Add(child.ChoiceGroup)) continue;
            ChoiceGroupCount++;
            var options = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            for (var optionIndex = 0; optionIndex < children.Count; optionIndex++)
            {
                var option = children[optionIndex];
                if (option.ChoiceGroup != child.ChoiceGroup) continue;
                if (options.Children.Count > 0) options.Children.Add(new TextBlock
                {
                    Text = "或", Margin = new Thickness(7, 18, 7, 0), VerticalAlignment = VerticalAlignment.Top,
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 77, 40)), FontSize = 12
                });
                options.Children.Add(BuildNode(option, ChildPath(path, option, optionIndex)));
            }
            var group = new StackPanel();
            group.Children.Add(new TextBlock
            {
                Text = child.ChoiceLabel + " · 选择一个完整方案", FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(4, 0, 4, 9)
            });
            group.Children.Add(options);
            units.Add(new Border
            {
                Child = group, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.5),
                BorderBrush = new SolidColorBrush(Color.FromRgb(176, 124, 55)), Background = new SolidColorBrush(Color.FromRgb(243, 222, 179)),
                Padding = new Thickness(9), Margin = new Thickness(7, 0, 7, 0),
                ToolTip = "框内只选一个完整方案；方案内的材料全部需要，不同方案不能拆开混选。"
            });
        }
        return ConnectChildren(units);
    }

    private static FrameworkElement ConnectChildren(List<FrameworkElement> children)
    {
        var connected = new Grid();
        connected.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        connected.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var edges = new Canvas { Height = 28, IsHitTestVisible = false, ClipToBounds = true };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var child in children) { child.VerticalAlignment = VerticalAlignment.Top; row.Children.Add(child); }
        SetRow(row, 1); connected.Children.Add(edges); connected.Children.Add(row);
        void Draw()
        {
            edges.Children.Clear();
            if (children.Count == 0 || connected.ActualWidth <= 0) return;
            var centers = children.Select(child => child.TranslatePoint(new Point(child.ActualWidth / 2, 0), connected).X).ToList();
            void Line(double x1, double y1, double x2, double y2) => edges.Children.Add(new Line
            { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = ConnectorBrush, StrokeThickness = 1.5, SnapsToDevicePixels = true });
            var parent = connected.ActualWidth / 2;
            Line(parent, 0, parent, 13);
            Line(Math.Min(parent, centers.Min()), 13, Math.Max(parent, centers.Max()), 13);
            foreach (var center in centers) Line(center, 13, center, 28);
        }
        connected.Loaded += (_, _) => Draw(); connected.SizeChanged += (_, _) => Draw();
        return connected;
    }
}
