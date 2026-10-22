using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop.Views;

public partial class MaterialAnalysisWindow
{
    private readonly MaterialRouteDiagram routeDiagram = new();
    private IReadOnlyDictionary<string, long> diagramInventory = new Dictionary<string, long>();
    private string diagramTargetId = "";
    private double diagramHorizontalOffset, diagramVerticalOffset;
    internal bool MissingOnlyVisible => MissingOnly.Visibility == Visibility.Visible;
    internal bool DiagramMode => RouteModeToggle.IsChecked == true;
    internal bool RouteModeVisible => RouteModePicker.Visibility == Visibility.Visible;
    internal MaterialRouteDiagram CurrentDiagram => routeDiagram;
    internal string PinnedSaveTitle => SaveTitle.Text;

    private void InitializeDiagram()
    {
        DiagramHost.Content = routeDiagram;
        routeDiagram.ItemRequested += RequestItem;
        routeDiagram.LayoutChanging += () =>
        {
            diagramHorizontalOffset = DiagramScroll.HorizontalOffset;
            diagramVerticalOffset = DiagramScroll.VerticalOffset;
        };
        routeDiagram.LayoutChanged += () => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            DiagramScroll.ScrollToHorizontalOffset(diagramHorizontalOffset);
            DiagramScroll.ScrollToVerticalOffset(diagramVerticalOffset);
        }));
    }

    internal void SetDiagramMode(bool enabled) => RouteModeToggle.IsChecked = enabled;

    private void RouteMode_Changed(object sender, RoutedEventArgs e)
    {
        if (DiagramHost is null || refreshing) return;
        RenderRows();
    }

    private void RenderRoutes()
    {
        var graph = RouteModeToggle.IsChecked == true;
        SynthesisRoots.Visibility = graph ? Visibility.Collapsed : Visibility.Visible;
        DiagramScroll.Visibility = graph ? Visibility.Visible : Visibility.Collapsed;
        // Keep the inventory explanation in the table/list. The graph has a
        // compact legend and only physical saved quantities on item nodes.
        PlanSummary.Visibility = graph ? Visibility.Collapsed : Visibility.Visible;
        TreeSchemeSelector.Visibility = !synthesis.IsRecipeView && synthesis.Schemes.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        TreeExplanation.Text = graph ? "选择上方目标查看路线。连线连接共同材料；金色框内任选一个完整方案。所有数量均来自本窗口固定的存档。" : synthesis.Message;
        if (!graph) { RenderSynthesisTree(); return; }
        var targets = DiagramTargets().ToList();
        var target = targets.FirstOrDefault(x => x.ItemId == diagramTargetId) ?? targets.FirstOrDefault();
        diagramTargetId = target?.ItemId ?? "";
        routeDiagram.SetRoot(target, diagramInventory);
    }

    private IEnumerable<MaterialSynthesisNode> DiagramTargets()
    {
        if (TreeSchemeBox.SelectedItem is not MaterialSynthesisScheme scheme) return [];
        var targets = scheme.Targets.AsEnumerable();
        if (StageFilter.SelectedItem is string stage && stage != "全部阶段")
            targets = targets.Where(x => (string.IsNullOrEmpty(x.Stage) ? "未标注阶段" : x.Stage) == stage);
        return targets;
    }

    internal bool SelectDiagramTarget(string itemId)
    {
        var id = planner.Canonical(itemId);
        if (!DiagramTargets().Any(x => x.ItemId == id)) return false;
        var changed = diagramTargetId != id;
        diagramTargetId = id; RenderRoutes();
        if (changed)
        {
            diagramHorizontalOffset = 0; diagramVerticalOffset = 0;
            DiagramScroll.ScrollToHome();
        }
        return true;
    }
}
