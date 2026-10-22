using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop;

public partial class MainWindow
{
    private EquipmentGuideCatalog _equipmentGuide = new();
    private Dictionary<string, EquipmentGuideItem> _equipmentExplanations = [];
    private Dictionary<string, string> _equipmentExplanationSearch = [];

    private void InitializeEquipmentGuide(EquipmentGuideCatalog? guide = null)
    {
        _equipmentGuide = guide ?? EquipmentGuideCatalog.Load();
        if (_equipmentGuide.Items.Any(item => !_byId.TryGetValue(item.Id, out var catalogItem) ||
                catalogItem.Category != "装备"))
            throw new InvalidDataException("装备说明与物品目录不匹配。");
        _equipmentExplanations = _equipmentGuide.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        _equipmentExplanationSearch = _equipmentGuide.Items.ToDictionary(item => item.Id,
            item => string.Join(" ", item.Sections.Select(section => section.Title + " " + section.Text)), StringComparer.Ordinal);
    }

    private void RenderEquipmentExplanation(CatalogItem item)
    {
        DetailResearchContent.Children.Clear();
        DetailResearchExpander.IsExpanded = false;
        var available = _equipmentExplanations.TryGetValue(item.Id, out var explanation);
        DetailResearchPanel.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        DetailOriginalDescription.Header = "属性与技能";
        DetailOriginalDescription.IsExpanded = true;
        if (!available) return;
        foreach (var section in explanation!.Sections)
        {
            var body = new StackPanel();
            body.Children.Add(new TextBlock
            {
                Text = section.Title, FontSize = 14, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7)
            });
            body.Children.Add(new TextBlock
            {
                Text = section.Text, FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 24
            });
            DetailResearchContent.Children.Add(new Border
            {
                Child = body, Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 10),
                Background = new SolidColorBrush(Color.FromRgb(0xFC, 0xF4, 0xE2)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xAF, 0x80)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5)
            });
        }
    }
}
