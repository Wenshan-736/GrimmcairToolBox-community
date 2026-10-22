using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop;

public partial class MainWindow
{
    private const string EquipmentTargetDragFormat = "Grimmcair.EquipmentTarget";
    private sealed record TargetDrag(string OwnerId, string SaveId, string ItemId);
    private Point _targetDragStart;
    private Button? _targetDragButton;
    private bool _draggingTarget;
    private static List<GuideTextRun> ReadItemRuns(System.Text.Json.Nodes.JsonNode? node) =>
        node is System.Text.Json.Nodes.JsonArray runs ? runs.Select(run => new GuideTextRun
            { Text = run?["text"]?.ToString() ?? "", Color = run?["color"]?.ToString() ?? "" }).ToList() : [];

    private void RenderEquipmentTargets()
    {
        if (EquipmentTargetItems is null) return;
        EquipmentTargetItems.Children.Clear();
        var choice = TargetSaveBox.SelectedItem as TargetSaveChoice;
        var save = choice is null ? null : _simulation?.FindSave(choice.OwnerId, choice.Id);
        EquipmentTargetHint.Text = save is not null
            ? $"{save.GameAccount} · {save.Profession} · {save.Name}（{(save.IsMain ? "主档" : "副档")}） · {save.Targets.Count} 项目标"
            : "请先选择目标所属存档";
        EquipmentTargetHint.ToolTip = EquipmentTargetHint.Text + "\n右键装备添加目标，右键目标取消；点击目标查看装备。";
        if (save is not null)
        {
            foreach (var goal in save.Targets)
            {
                var item = _byId.GetValueOrDefault(goal.ItemId);
                var label = $"{item?.Name ?? goal.ItemName} × {goal.Quantity}";
                var button = new Button { Content = new Image { Source = LoadImage(item?.IconPath ?? ""), Width = 34, Height = 34 },
                    Tag = goal.ItemId, Padding = new Thickness(3), Width = 42, Height = 42,
                    Margin = new Thickness(0, 0, 5, 3), ToolTip = label + "\n左键查看 · 右键取消 · 拖动排序" };
                System.Windows.Automation.AutomationProperties.SetName(button, label);
                button.Click += (_, _) => { if (!_draggingTarget) OpenRelatedItem(goal.ItemId); };
                button.PreviewMouseRightButtonDown += (_, e) => { e.Handled = true; ChangeEquipmentTarget(goal.ItemId, false, save.Id); };
                button.PreviewMouseLeftButtonDown += (_, e) => { _targetDragStart = e.GetPosition(EquipmentTargetItems); _targetDragButton = button; };
                button.PreviewMouseLeftButtonUp += (_, _) => { if (!_draggingTarget) _targetDragButton = null; };
                button.LostMouseCapture += (_, _) => { if (!_draggingTarget) _targetDragButton = null; };
                button.PreviewMouseMove += (_, e) =>
                {
                    if (_targetDragButton != button || e.LeftButton != MouseButtonState.Pressed || _draggingTarget) return;
                    var delta = e.GetPosition(EquipmentTargetItems) - _targetDragStart;
                    if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                    _draggingTarget = true;
                    button.ReleaseMouseCapture();
                    try { DragDrop.DoDragDrop(button, new DataObject(EquipmentTargetDragFormat, new TargetDrag(save.OwnerId, save.Id, goal.ItemId)), DragDropEffects.Move); }
                    finally { _targetDragButton = null; _draggingTarget = false; }
                    e.Handled = true;
                };
                EquipmentTargetItems.Children.Add(button);
            }
        }
        if (EquipmentTargetItems.Children.Count == 0)
            EquipmentTargetItems.Children.Add(new TextBlock { Text = save is not null
                    ? "右键目录中的装备，添加到这里。" : "每份存档可独立设置目标，材料只按该档库存计算。",
                Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1, 4, 0, 4) });
        RefreshEquipmentContextMenu();
    }

    private void RefreshEquipmentContextMenu()
    {
        EquipmentList.ContextMenu = null;
    }

    private void EquipmentList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(EquipmentList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (row is null) { e.Handled = true; return; }
        row.IsSelected = true;
        row.Focus();
        if (row.DataContext is CatalogItem item && TargetSaveBox.SelectedItem is TargetSaveChoice choice &&
            _simulation?.FindSave(choice.OwnerId, choice.Id) is { } save)
            ChangeEquipmentTarget(item.Id, !save.Targets.Any(goal => goal.ItemId == item.Id), save.Id);
        e.Handled = true;
    }

    private void EquipmentList_ContextMenuOpening(object sender, ContextMenuEventArgs e) => e.Handled = true;

    private void EquipmentTargets_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(EquipmentTargetDragFormat) is TargetDrag drag && TargetSaveBox.SelectedItem is TargetSaveChoice choice
            && drag.OwnerId == choice.OwnerId && drag.SaveId == choice.Id ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void EquipmentTargets_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(EquipmentTargetDragFormat) is not TargetDrag drag || TargetSaveBox.SelectedItem is not TargetSaveChoice choice
            || drag.OwnerId != choice.OwnerId || drag.SaveId != choice.Id || _simulation?.FindSave(choice.OwnerId, choice.Id) is not { } save) return;
        var position = e.GetPosition(EquipmentTargetItems);
        var before = EquipmentTargetItems.Children.OfType<Button>().FirstOrDefault(button =>
        {
            var top = button.TranslatePoint(new Point(), EquipmentTargetItems);
            return position.Y <= top.Y + button.ActualHeight && (position.Y < top.Y || position.X < top.X + button.ActualWidth / 2);
        })?.Tag as string;
        if (before == drag.ItemId) return;
        var order = save.Targets.Select(goal => goal.ItemId).Where(id => id != drag.ItemId).ToList();
        var index = before is null ? order.Count : order.IndexOf(before);
        order.Insert(index < 0 ? order.Count : index, drag.ItemId);
        RunSimulationAction(() => _simulation.ReorderTargets(choice.OwnerId, choice.Id, order));
    }

    private void ChangeEquipmentTarget(string itemId, bool add, string? expectedSaveId = null)
    {
        if (TargetSaveBox.SelectedItem is not TargetSaveChoice choice || _simulation is null ||
            expectedSaveId is not null && expectedSaveId != choice.Id ||
            _simulation.FindSave(choice.OwnerId, choice.Id) is not { } save) return;
        if (add == save.Targets.Any(goal => goal.ItemId == itemId)) return;
        var item = _byId.GetValueOrDefault(itemId);
        if (add && item is not { Category: "装备" }) return;
        RunSimulationAction(() =>
        {
            if (add) _simulation.AddTarget(choice.OwnerId, choice.Id, item!.Id, item.Name);
            else _simulation.RemoveTarget(choice.OwnerId, choice.Id, itemId);
            StatusText.Text = add ? "已添加到所选存档目标" : "已取消所选存档目标";
        });
    }
}
