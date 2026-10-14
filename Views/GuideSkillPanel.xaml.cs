using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop.Views;

public partial class GuideSkillPanel : UserControl
{
    private readonly Dictionary<string, FrameworkElement> formulaAnchors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> formulaLabels = new(StringComparer.Ordinal);
    private string visibleDetails = "";
    private GuideTextRenderResult basicRendering = new(false, 0, 0, 0, 0, []);
    private Window? layoutWindow;
    private static readonly DependencyPropertyKey IsCompactPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsCompact), typeof(bool), typeof(GuideSkillPanel), new PropertyMetadata(false));
    public static readonly DependencyProperty IsCompactProperty = IsCompactPropertyKey.DependencyProperty;
    public bool IsCompact => (bool)GetValue(IsCompactProperty);
    public double VisibleDetailViewportHeight => DetailScroll.ViewportHeight;

    public GuideSkillPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var window = Window.GetWindow(this);
            if (layoutWindow != window)
            {
                if (layoutWindow is not null) layoutWindow.SizeChanged -= LayoutWindow_SizeChanged;
                layoutWindow = window;
                if (layoutWindow is not null) layoutWindow.SizeChanged += LayoutWindow_SizeChanged;
            }
            UpdateCompactMode();
        };
        Unloaded += (_, _) =>
        {
            if (layoutWindow is not null) layoutWindow.SizeChanged -= LayoutWindow_SizeChanged;
            layoutWindow = null;
        };
    }

    private void LayoutWindow_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCompactMode();
    private void UpdateCompactMode() => SetValue(IsCompactPropertyKey, layoutWindow is { ActualHeight: > 0 and <= 800 });
    public string? SelectedSkillId => (SkillList.SelectedItem as GuideSkill)?.Id;
    public int SkillCount => SkillList.Items.Count;
    public string VisibleBasicText => GuideTextRendering.ReadText(BasicText);
    public string VisibleRangeText => RangeText.Text;
    public bool RangeSummaryVisible => RangeSummary.Visibility == Visibility.Visible;
    public bool BasicRunsApplied => basicRendering.UsedRuns;
    public int BasicColoredRunCount => basicRendering.ColoredRunCount;
    public int BasicAdjustedColorCount => basicRendering.AdjustedColorCount;
    public int BasicRejectedColorCount => basicRendering.RejectedColorCount;
    public double BasicMinimumContrast => basicRendering.MinimumContrast;
    public IReadOnlyList<string> BasicRenderedColors => basicRendering.RenderedColors;
    public string VisibleDetailsText => visibleDetails;
    public string VisibleOverviewText => OverviewText.Text;
    public string VisibleExamplesText { get; private set; } = "";
    public string VisibleFormulaText { get; private set; } = "";
    public int ReadingSectionCount => ReadingSectionsPanel.Children.Count;
    public int FormulaCount => formulaAnchors.Count;
    public int ExampleCount { get; private set; }
    public int DiagramCount => DiagramCards.Children.Count;
    public bool FormulasExpanded => FormulaExpander.IsExpanded;
    public string? LastFormulaId { get; private set; }
    public bool UsesReadableDetails { get; private set; }
    public bool SelectedSkillIconMissing => SkillList.SelectedItem is GuideSkill skill && ResourceImage.Load(skill.Icon) is null;

    public void Load(IEnumerable<GuideSkill> skills)
    {
        SkillList.ItemsSource = skills.ToList();
        SkillList.SelectedIndex = SkillList.Items.Count > 0 ? 0 : -1;
        EmptyText.Visibility = SkillList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowSelectedSkill();
    }

    public bool SelectSkill(string skillId)
    {
        var skill = SkillList.Items.OfType<GuideSkill>().FirstOrDefault(x => x.Id == skillId);
        if (skill is null) return false;
        SkillList.SelectedItem = skill;
        SkillList.ScrollIntoView(skill);
        return true;
    }

    public bool JumpToFormula(string formulaId)
    {
        if (!formulaAnchors.TryGetValue(formulaId, out var anchor)) return false;
        FormulaExpander.IsExpanded = true;
        DetailScroll.UpdateLayout();
        DetailScroll.ScrollToVerticalOffset(Math.Max(0, anchor.TranslatePoint(new Point(), DetailPanel).Y - 8));
        LastFormulaId = formulaId;
        return true;
    }

    public bool JumpToExamples() => ScrollToSection(ExamplesPanel);
    public bool JumpToDiagrams() => ScrollToSection(DiagramsPanel);

    private bool ScrollToSection(FrameworkElement section)
    {
        if (section.Visibility != Visibility.Visible) return false;
        DetailScroll.UpdateLayout();
        DetailScroll.ScrollToVerticalOffset(Math.Max(0, section.TranslatePoint(new Point(), DetailPanel).Y - 8));
        return true;
    }

    private void SkillList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelectedSkill();
    private void DiagramsJump_Click(object sender, RoutedEventArgs e) => JumpToDiagrams();
    private void ExamplesJump_Click(object sender, RoutedEventArgs e) => JumpToExamples();
    private void FormulaJump_Click(object sender, RoutedEventArgs e)
    {
        if (formulaAnchors.Keys.FirstOrDefault() is { } id) JumpToFormula(id);
    }
    private void DetailsJump_Click(object sender, RoutedEventArgs e)
    {
        DetailScroll.UpdateLayout();
        DetailScroll.ScrollToVerticalOffset(DetailsHeading.TranslatePoint(new Point(), DetailPanel).Y);
    }

    private TextBlock Paragraph(string text, double fontSize = 13, bool muted = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = fontSize,
        LineHeight = fontSize >= 13 ? 24 : 20,
        Foreground = (Brush)FindResource(muted ? "MutedBrush" : "InkBrush")
    };

    private Border Card(UIElement content, Thickness? margin = null) => new()
    {
        Child = content, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(8),
        BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(1),
        Background = (Brush)FindResource("PaperLightBrush"), Margin = margin ?? new Thickness(0, 0, 0, 10)
    };

    private Button FormulaLink(GuideFormula formula, bool includeTitle = false)
    {
        var marker = formulaLabels[formula.Id];
        var button = new Button
        {
            Content = includeTitle ? $"{marker} 查看公式 ↓" : marker,
            Padding = new Thickness(8, 3, 8, 3), MinHeight = 27, FontSize = 11,
            Margin = new Thickness(7, 0, 0, 4), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"查看 {marker}：{formula.Title}"
        };
        AutomationProperties.SetName(button, $"查看公式 {marker} {formula.Title}");
        button.Click += (_, _) => JumpToFormula(formula.Id);
        return button;
    }

    private void ShowSelectedSkill()
    {
        if (DetailPanel is null) return;
        var skill = SkillList.SelectedItem as GuideSkill;
        DetailPanel.Visibility = skill is null ? Visibility.Collapsed : Visibility.Visible;
        SkillNameText.Text = skill?.Name ?? "";
        var paperColors = ((GradientBrush)FindResource("ParchmentBrush")).GradientStops.Select(stop => stop.Color)
            .Append(((SolidColorBrush)FindResource("PaperLightBrush")).Color).ToArray();
        basicRendering = GuideTextRendering.Apply(BasicText, skill?.BasicText ?? "", skill?.BasicRuns,
            ((SolidColorBrush)FindResource("InkBrush")).Color, paperColors);
        RangeText.Text = skill?.RangeText ?? "";
        RangeSummary.Visibility = string.IsNullOrWhiteSpace(RangeText.Text) ? Visibility.Collapsed : Visibility.Visible;
        OverviewText.Text = skill?.OverviewText ?? "";
        OverviewCard.Visibility = string.IsNullOrWhiteSpace(OverviewText.Text) ? Visibility.Collapsed : Visibility.Visible;
        ReadingSectionsPanel.Children.Clear(); ExampleCards.Children.Clear(); DiagramCards.Children.Clear(); FormulaCards.Children.Clear();
        formulaAnchors.Clear(); formulaLabels.Clear(); FormulaExpander.IsExpanded = false;
        ExampleCount = 0; LastFormulaId = null; VisibleExamplesText = ""; VisibleFormulaText = "";
        UsesReadableDetails = skill is not null && (!string.IsNullOrWhiteSpace(skill.OverviewText) || skill.ReadingSections.Count > 0);
        DetailsText.Text = UsesReadableDetails ? "" : skill?.DetailsText ?? "";
        DetailsText.Visibility = string.IsNullOrWhiteSpace(DetailsText.Text) ? Visibility.Collapsed : Visibility.Visible;
        visibleDetails = UsesReadableDetails && skill is not null
            ? string.Join("\n\n", new[] { skill.OverviewText }.Concat(skill.ReadingSections.Select(x =>
                string.Join("\n", new[] { x.Title, x.Text }.Where(value => !string.IsNullOrWhiteSpace(value)))))
                .Where(value => !string.IsNullOrWhiteSpace(value)))
            : DetailsText.Text;

        if (skill is not null)
        {
            var formulas = skill.Formulas.Where(x => !string.IsNullOrWhiteSpace(x.Id)).DistinctBy(x => x.Id).ToList();
            for (var index = 0; index < formulas.Count; index++) formulaLabels[formulas[index].Id] = $"F{index + 1}";
            foreach (var section in skill.ReadingSections)
            {
                var content = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
                var title = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
                var titleText = Paragraph(section.Title, 14); titleText.FontWeight = FontWeights.SemiBold;
                titleText.Margin = new Thickness(0, 0, 0, 4); title.Children.Add(titleText);
                foreach (var formula in section.FormulaIds.Distinct().Select(id => formulas.FirstOrDefault(x => x.Id == id)).OfType<GuideFormula>())
                    title.Children.Add(FormulaLink(formula));
                content.Children.Add(title); content.Children.Add(Paragraph(section.Text));
                ReadingSectionsPanel.Children.Add(content);
            }
            foreach (var diagram in skill.Diagrams.Where(x => SkillMechanicDiagram.Supports(x.Kind)))
                DiagramCards.Children.Add(new SkillMechanicDiagram(diagram) { Width = 342, Margin = new Thickness(0, 0, 12, 10) });

            var exampleTexts = new List<string>(); var formulaTexts = new List<string>();
            foreach (var formula in formulas)
            {
                var label = formulaLabels[formula.Id];
                if (formula.Examples.Count > 0)
                {
                    var content = new StackPanel();
                    var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
                    var link = FormulaLink(formula, includeTitle: true); DockPanel.SetDock(link, Dock.Right); heading.Children.Add(link);
                    var title = Paragraph(formula.Title); title.FontWeight = FontWeights.SemiBold; heading.Children.Add(title);
                    content.Children.Add(heading);
                    var columns = new WrapPanel();
                    foreach (var example in formula.Examples)
                    {
                        var column = new StackPanel { Width = 220, Margin = new Thickness(0, 3, 14, 10) };
                        column.Children.Add(Paragraph(example.InputsText, 12, muted: true));
                        var result = Paragraph(example.ResultText, 16); result.FontWeight = FontWeights.SemiBold;
                        result.Foreground = new SolidColorBrush(Color.FromRgb(0x36, 0x68, 0x46)); result.Margin = new Thickness(0, 5, 0, 0);
                        column.Children.Add(result); columns.Children.Add(column); ExampleCount++;
                        exampleTexts.Add($"{formula.Title}\n{example.InputsText}\n{example.ResultText}");
                    }
                    content.Children.Add(columns); ExampleCards.Children.Add(Card(content));
                }

                var body = new StackPanel();
                var formulaTitle = Paragraph($"{label} · {formula.Title}", 14); formulaTitle.FontWeight = FontWeights.SemiBold;
                body.Children.Add(formulaTitle);
                var expression = Paragraph(formula.Expression, 14); expression.Margin = new Thickness(0, 7, 0, 7);
                body.Children.Add(expression);
                if (!string.IsNullOrWhiteSpace(formula.VariablesText)) body.Children.Add(Paragraph("各项含义\n" + formula.VariablesText, 12, muted: true));
                if (!string.IsNullOrWhiteSpace(formula.ConditionsText))
                {
                    var conditions = Paragraph("计算条件\n" + formula.ConditionsText, 12, muted: true);
                    conditions.Margin = new Thickness(0, 7, 0, 0); body.Children.Add(conditions);
                }
                var card = Card(body); FormulaCards.Children.Add(card); formulaAnchors[formula.Id] = card;
                formulaTexts.Add(string.Join("\n", new[] { label + " · " + formula.Title, formula.Expression, formula.VariablesText, formula.ConditionsText }
                    .Where(value => !string.IsNullOrWhiteSpace(value))));
            }
            VisibleExamplesText = string.Join("\n\n", exampleTexts); VisibleFormulaText = string.Join("\n\n", formulaTexts);
        }
        DiagramsPanel.Visibility = DiagramCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExamplesPanel.Visibility = ExampleCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        FormulaExpander.Visibility = FormulaCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        DiagramsJump.Visibility = DiagramsPanel.Visibility;
        ExamplesJump.Visibility = ExamplesPanel.Visibility;
        FormulaJump.Visibility = FormulaExpander.Visibility;
        ReadingNavigation.Visibility = DiagramCount > 0 || ExampleCount > 0 || FormulaCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailsJump.Visibility = string.IsNullOrWhiteSpace(visibleDetails) && DiagramCount == 0 && ExampleCount == 0 && FormulaCount == 0
            ? Visibility.Collapsed : Visibility.Visible;
        DetailScroll.ScrollToTop();
    }
}
