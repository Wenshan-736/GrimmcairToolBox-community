using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Grimmcair.Desktop;

public sealed record CatalogIngredient(string Id, string Name, int Quantity, bool Available);
public sealed record CatalogRecipe(List<CatalogIngredient> Ingredients);
public sealed record CatalogRecipeChoice(List<CatalogIngredient> Options);
public sealed record CatalogRecipePlan(List<CatalogIngredient> Required, List<CatalogRecipeChoice> Choices);
public sealed record CatalogUse(string Id, string Name, bool Available, string Stage, int StageRank);

public partial class MainWindow
{
    private static List<CatalogIngredient> ReadRecipeIngredients(JsonNode? node) => (node as JsonArray)?
        .Select(i => new CatalogIngredient(i!["id"]!.ToString(), i["name"]!.ToString(),
            i["quantity"]!.GetValue<int>(), i["available"]!.GetValue<bool>())).ToList() ?? [];

    private static List<CatalogRecipePlan> ReadRecipePlans(JsonNode? node) => (node as JsonArray)?
        .Select(plan => new CatalogRecipePlan(ReadRecipeIngredients(plan?["required"]),
            (plan?["choices"] as JsonArray)?.Select(choice => new CatalogRecipeChoice(
                ReadRecipeIngredients(choice?["options"]))).ToList() ?? []))
        .ToList() ?? [];

    private static List<CatalogRecipe> ReadRecipes(JsonNode? node) => (node as JsonArray)?
        .Select(recipe => new CatalogRecipe((recipe?["ingredients"] as JsonArray)?
            .Select(i => new CatalogIngredient(i!["id"]!.ToString(), i["name"]!.ToString(),
                i["quantity"]!.GetValue<int>(), i["available"]!.GetValue<bool>())).ToList() ?? []))
        .ToList() ?? [];

    private static List<CatalogUse> ReadUses(JsonNode? node) => (node as JsonArray)?
        .Select(i => new CatalogUse(i!["id"]!.ToString(), i["name"]!.ToString(),
            i["available"]!.GetValue<bool>(), i["stage"]?.ToString() ?? "", i["stageRank"]?.GetValue<int>() ?? 0))
        .OrderByDescending(i => i.StageRank).ThenBy(i => i.Name, StringComparer.CurrentCulture).ToList() ?? [];

    private void DetailTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DetailScroll is null || RecipeScroll is null) return;
        DetailScroll.Visibility = DetailTabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecipeScroll.Visibility = DetailTabs.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private TextBlock RecipeLabel(string text, bool heading = false) => new()
    {
        Text = text, Style = (Style)FindResource(heading ? "SectionLabel" : "MutedText"),
        TextWrapping = TextWrapping.Wrap
    };

    private void RenderRecipes(CatalogItem item)
    {
        RecipeContent.Children.Clear();
        RecipeTab.Content = "合成与用途";
        RecipeContent.Children.Add(RecipeLabel("获取来源", true));
        var drops = _guide.FindDrops(item.Id).ToList();
        if (drops.Count > 0)
        {
            RecipeContent.Children.Add(RecipeLabel("BOSS 掉落 · 点击查看首领掉落物"));
            foreach (var source in drops)
            {
                var content = new Grid();
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(43) });
                content.ColumnDefinitions.Add(new ColumnDefinition());
                var icon = LoadImage(source.Boss.Icon);
                if (icon is not null) content.Children.Add(new Image { Source = icon, Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Left });
                var labels = new StackPanel();
                labels.Children.Add(new TextBlock { Text = source.Boss.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                labels.Children.Add(RecipeLabel("基础掉落概率 · " + source.Drop.BaseChanceText));
                if (!string.IsNullOrEmpty(source.Drop.Notes)) labels.Children.Add(RecipeLabel(source.Drop.Notes));
                Grid.SetColumn(labels, 1); content.Children.Add(labels);
                var button = new Button
                {
                    Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(10), Margin = new Thickness(0, 6, 0, 5),
                    Tag = "boss:" + source.Boss.Id, ToolTip = "查看" + source.Boss.Name + "的掉落物"
                };
                button.Click += (_, _) => OpenBossDrops(source.Boss.Id, item.Id);
                RecipeContent.Children.Add(button);
            }
        }
        RecipeContent.Children.Add(RecipeLabel("合成 / 兑换", true));
        if (item.RecipePlans.Count > 0)
        {
            RecipeContent.Children.Add(RecipeLabel(item.RecipePlans.Any(plan => plan.Choices.Count > 0)
                ? "共同材料全部需要；框内可选材料只需其中一种，数量见各物品下方。"
                : "同一方案内的物品全部需要，数量见各物品下方。"));
            for (var index = 0; index < item.RecipePlans.Count; index++)
            {
                var plan = item.RecipePlans[index];
                var panel = new StackPanel();
                if (item.RecipePlans.Count > 1)
                    panel.Children.Add(RecipeLabel($"方案 {index + 1}（任选一种方案）"));
                if (plan.Required.Count > 0)
                {
                    if (plan.Choices.Count > 0) panel.Children.Add(RecipeLabel("共同材料（全部需要）"));
                    panel.Children.Add(RecipeIngredientsPanel(plan.Required, "recipe-required"));
                }
                foreach (var choice in plan.Choices)
                {
                    var choicePanel = new StackPanel();
                    var label = choice.Options.Count == 2 ? "二选一" : choice.Options.Count == 3 ? "三选一" : $"{choice.Options.Count} 选一";
                    choicePanel.Children.Add(RecipeLabel(label, true));
                    choicePanel.Children.Add(RecipeIngredientsPanel(choice.Options, "recipe-choice-options"));
                    panel.Children.Add(new Border
                    {
                        Tag = "recipe-choice", Child = choicePanel,
                        BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(5), Padding = new Thickness(9, 5, 9, 0),
                        Margin = new Thickness(0, 7, 0, 4)
                    });
                }
                RecipeContent.Children.Add(RecipePanelBorder(panel));
            }
        }
        else if (item.Recipes.Count == 0)
            RecipeContent.Children.Add(RecipeLabel("资料中暂未收录该物品的合成配方。"));
        else
            RecipeContent.Children.Add(RecipeLabel("同一方案内的物品全部需要，数量见各物品下方。"));
        // Older packs still show intact full alternatives. Do not guess local
        // independence by factoring correlated whole-recipe routes in the UI.
        for (var index = 0; item.RecipePlans.Count == 0 && index < item.Recipes.Count; index++)
        {
            var panel = new StackPanel();
            if (item.Recipes.Count > 1)
                panel.Children.Add(RecipeLabel($"方案 {index + 1}（任选一种方案）"));
            panel.Children.Add(RecipeIngredientsPanel(item.Recipes[index].Ingredients, "recipe-legacy"));
            RecipeContent.Children.Add(RecipePanelBorder(panel));
        }
        RecipeContent.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("LineBrush"), Margin = new Thickness(0, 16, 0, 17) });
        RecipeContent.Children.Add(RecipeLabel("可合成物品", true));
        if (item.UsedIn.Count == 0)
            RecipeContent.Children.Add(RecipeLabel("资料中暂未收录使用该物品的后续合成。"));
        else
        {
            RecipeContent.Children.Add(RecipeLabel("选择产物可查看完整配方，按阶段由高到低排列。"));
            var uses = new WrapPanel { Margin = new Thickness(0, 8, -6, 0) };
            foreach (var use in item.UsedIn)
                uses.Children.Add(ItemLink(use.Id, use.Name, use.Stage, use.Available));
            RecipeContent.Children.Add(uses);
        }
    }

    private WrapPanel RecipeIngredientsPanel(IEnumerable<CatalogIngredient> ingredients, string tag)
    {
        var panel = new WrapPanel { Tag = tag, Margin = new Thickness(0, 5, -6, 0) };
        foreach (var ingredient in ingredients)
            panel.Children.Add(ItemLink(ingredient.Id, ingredient.Name, $"× {ingredient.Quantity}", ingredient.Available));
        return panel;
    }

    private Border RecipePanelBorder(StackPanel panel) => new()
    {
        Tag = "recipe-plan", Child = panel, Background = (Brush)FindResource("PaperLightBrush"),
        BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 7, 10, 4), Margin = new Thickness(0, 0, 0, 8)
    };

    private Button ItemLink(string id, string name, string annotation, bool available)
    {
        var item = _byId.GetValueOrDefault(id);
        var canOpen = available && item is not null && _catalog.Any(i => i.Id == item.DisplayId);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(33) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = LoadImage(item?.IconPath ?? "");
        if (icon is not null)
            row.Children.Add(new Image { Source = icon, Width = 27, Height = 27, HorizontalAlignment = HorizontalAlignment.Left });
        else
            row.Children.Add(new TextBlock { Text = "◇", FontSize = 22, VerticalAlignment = VerticalAlignment.Center });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = name, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        labels.Children.Add(new TextBlock { Text = annotation, FontSize = 10, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 3, 0, 0) });
        Grid.SetColumn(labels, 1);
        row.Children.Add(labels);
        var button = new Button
        {
            Content = row, Tag = id, MaxWidth = 245, Padding = new Thickness(8, 6, 8, 6),
            HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 6, 6),
            IsEnabled = canOpen, ToolTip = canOpen ? $"查看{name}" : "当前物品目录尚未收录该物品的详情"
        };
        button.Click += (_, _) => OpenRelatedItem(id);
        return button;
    }

    private void OpenRelatedItem(string id)
    {
        SearchBox.Text = "";
        CategoryBox.SelectedIndex = 0;
        SelectEquipment(id);
        DetailTabs.SelectedIndex = 1;
        MainPages.SelectedIndex = 0;
    }
}
