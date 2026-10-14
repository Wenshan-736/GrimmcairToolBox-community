using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop;

public partial class MainWindow
{
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
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new Image { Source = LoadImage(item?.IconPath ?? ""), Width = 23, Height = 23,
                    Margin = new Thickness(0, 0, 6, 0) });
                content.Children.Add(new TextBlock { Text = label, MaxWidth = 155, TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center });
                var button = new Button { Content = content, Tag = goal.ItemId, Padding = new Thickness(6, 3, 6, 3),
                    MinHeight = 29, Margin = new Thickness(0, 0, 5, 3), ToolTip = label + "\n点击查看装备，右键取消目标。" };
                button.Click += (_, _) => OpenRelatedItem(goal.ItemId);
                var menu = new ContextMenu();
                var remove = new MenuItem { Header = "取消目标", Tag = goal.ItemId };
                remove.Click += (_, _) => ChangeEquipmentTarget(goal.ItemId, false, save.Id);
                menu.Items.Add(remove); button.ContextMenu = menu;
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
        EquipmentList.ContextMenu ??= new ContextMenu();
        var menu = EquipmentList.ContextMenu;
        menu.Items.Clear();
        var item = EquipmentList.SelectedItem as CatalogItem;
        var choice = TargetSaveBox.SelectedItem as TargetSaveChoice;
        var save = choice is null ? null : _simulation?.FindSave(choice.OwnerId, choice.Id);
        var already = item is not null && save?.Targets.Any(goal => goal.ItemId == item.Id) == true;
        var action = new MenuItem { Header = already ? "取消目标" : "加入当前档目标", Tag = item?.Id,
            IsEnabled = item is { Category: "装备" } && save is not null,
            ToolTip = save is null ? "请先选择目标所属存档。" : item?.Category != "装备" ? "目标只接受装备成品。" : null };
        if (item is not null && save is not null)
            action.Click += (_, _) => ChangeEquipmentTarget(item.Id, !already, save.Id);
        menu.Items.Add(action);
    }

    private void EquipmentList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(EquipmentList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (row is null) { e.Handled = true; return; }
        row.IsSelected = true;
        row.Focus();
        RefreshEquipmentContextMenu();
    }

    private void EquipmentList_ContextMenuOpening(object sender, ContextMenuEventArgs e) => RefreshEquipmentContextMenu();

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
