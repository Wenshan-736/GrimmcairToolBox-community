using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop.Views;

public partial class ConceptsView : UserControl
{
    private ConceptCatalog? catalog;
    private ConceptSection? section;
    private bool changingSection;
    private sealed record GuideSourceLink(string SourceId, string Title, string Author, string? Version, string Introduction, string Url);
    private IReadOnlyList<GuideSourceLink> guideSources = [];
    private IReadOnlyList<GuideSourceLink> visibleGuideSources = [];
    // Canonical links verified from the already-authorized source index. The beginner sheet
    // is the user-provided desktop entry point; it does not change the underlying research data.
    private static readonly IReadOnlyDictionary<string, (string Url, string Introduction)> GuideSourceLinks =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["tianting-xiaobudian"] = ("https://docs.qq.com/sheet/DWFZqUmhZdWxES1JC", "面向新人的常用操作、角色与徽章存读档、背包仓库及组队入门。"),
            ["qqdoc-DYVR2Vm5QaVRMY1V1"] = ("https://docs.qq.com/doc/DYVR2Vm5QaVRMY1V1", "圣光十字军的技能与术语、力锤输出、智锤辅助、阶段装备和文内 Boss 应对举例。"),
            ["tieba-11020661839"] = ("https://tieba.baidu.com/p/11020661839", "火系魔导师的技能、灼烧伤害、输出配装与 Boss 场景。"),
            ["qqdoc-DYXZOaFNFcmFzRVZI"] = ("https://docs.qq.com/doc/DYXZOaFNFcmFzRVZI", "各职业的技能属性与定位简介，便于初步了解职业差异。"),
            ["qqdoc-DSXVqQkxSdUhpb1pB"] = ("https://docs.qq.com/doc/DSXVqQkxSdUhpb1pB", "炼金术士的机制与主要技能、爆发续航、阶段出装和文内 Boss 应对举例；正文另含0.93a说明。"),
            ["tieba-10816026078"] = ("https://tieba.baidu.com/p/10816026078", "炼金术士在线攻略的帖子入口，包含技能操作与输出补充。"),
            ["tieba-10662858926"] = ("https://tieba.baidu.com/p/10662858926", "女巫的辅助与输出定位、技能用法、出装和文内 Boss 应对举例。"),
            ["tieba-10864031821"] = ("https://tieba.baidu.com/p/10864031821", "牧师辅助定位、阶段配装、技能与天使操作，以及文内 Boss 应对举例；部分配图标注0.92b。"),
            ["tieba-10296394570"] = ("https://tieba.baidu.com/p/10296394570", "炼金版本变化后的技能、输出和装备心得。"),
            ["tieba-8844086041"] = ("https://tieba.baidu.com/p/8844086041", "火系魔导师的技能与伤害分析，包含旧版伤害样本。"),
            ["tieba-9092252548"] = ("https://tieba.baidu.com/p/9092252548", "2024年的圣光十字军攻略，作为历史版本参考入口。")
        };

    public ConceptsView() => InitializeComponent();

    public string? SelectedSectionId => section?.Id;
    public string? SelectedEntryId => (EntryList.SelectedItem as ConceptEntry)?.Id;
    public int VisibleEntryCount => section?.Id == "player-guides" ? visibleGuideSources.Count : EntryList.Items.Count;
    public int VisibleTableRowCount => TableSection.Visibility == Visibility.Visible ? Math.Max(0, TableGrid.RowDefinitions.Count - 1) : 0;
    public int VisibleEffectRowCount => (EntryList.SelectedItem as ConceptEntry)?.Effects.Count > 0 ? VisibleTableRowCount : 0;
    public int VisibleSourceIconCount => Descendants(TableGrid).OfType<Image>().Count(x => x.Source is not null);
    public int VisibleExampleCount => ExampleCards.Children.Count;
    public int FormulaCount => FormulaCards.Children.Count;
    public bool FormulaExpanded => FormulaExpander.Visibility == Visibility.Visible && FormulaExpander.IsExpanded;
    public bool LeadFormulaVisible => LeadFormulaCard.Visibility == Visibility.Visible;
    public string VisibleLeadFormulaText => LeadFormulaText.Text;
    public double DetailViewportHeight => DetailScroll.ViewportHeight;
    public double TableTopOffset => TableSection.Visibility == Visibility.Visible
        ? TableSection.TranslatePoint(new Point(), DetailPanel).Y : double.NaN;
    public double TableViewportHeight => TableSection.Visibility == Visibility.Visible
        ? Math.Max(0, Math.Min(TableGrid.ActualHeight, DetailScroll.ViewportHeight - TableTopOffset + DetailScroll.VerticalOffset)) : 0;
    public bool ExamplesExpanded => ExamplesSection.Visibility == Visibility.Visible && ExamplesSection.IsExpanded;
    public string VisibleEffectText => VisibleEffectRowCount > 0 ? ReadText(TableGrid) : "";
    public string VisibleBodyText => section?.Id == "player-guides" ? ReadText(GuideSourceCards) : string.Join("\n", new[] { EntryTitle.Text, EntrySummary.Text, LeadFormulaText.Text,
        PlayerSourceText.Text, ReadText(KnowledgeImages), ReadText(ExampleCards), ReadText(ParagraphCards), ReadText(TableGrid) }.Where(x => x.Length > 0));
    public string VisibleFormulaText => ReadText(FormulaCards);
    public string FilterText => SearchBox.Text;
    public string SelectedSourceFilter => (SourceFilterBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
    public bool SourceFilterVisible => SourceFilterBox.Visibility == Visibility.Visible;
    public bool PlayerSourceVisible => PlayerSourceCard.Visibility == Visibility.Visible;
    public int VisibleKnowledgeImageCount => Descendants(KnowledgeImages).OfType<Image>().Count(image => image.Source is not null);
    public IReadOnlyList<string> VisibleGuideSourceIds => visibleGuideSources.Select(source => source.SourceId).ToArray();
    public IReadOnlyList<string> VisibleGuideSourceUrls => visibleGuideSources.Select(source => source.Url).ToArray();
    public bool GuideSourcesVisible => GuideSourcesPanel.Visibility == Visibility.Visible;
    public int VisibleGuideSourceLinkCount => Descendants(GuideSourceCards).OfType<TextBlock>()
        .Sum(text => text.Inlines.OfType<Hyperlink>().Count());
    public bool GuideBodyContentVisible => section?.Id == "player-guides" && (ConceptContentGrid.Visibility == Visibility.Visible
        || KnowledgeImages.Children.Count > 0 || ParagraphCards.Children.Count > 0);

    public bool SetSourceFilter(string source)
    {
        var item = SourceFilterBox.Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Tag as string == source);
        if (item is null) return false;
        SourceFilterBox.SelectedItem = item;
        return true;
    }

    public void Load(ConceptCatalog concepts)
    {
        catalog = concepts;
        guideSources = (concepts.Sections.FirstOrDefault(item => item.Id == "player-guides")?.Entries ?? [])
            .Where(entry => entry.PlayerGuideSource is { } source && GuideSourceLinks.ContainsKey(source.SourceId))
            .GroupBy(entry => entry.PlayerGuideSource!.SourceId, StringComparer.Ordinal)
            .Where(group => group.Key != "tianting-xiaobudian" || group.Any(entry => entry.Id == "player-guide-tianting-beginner"))
            .Select(group =>
            {
                var entry = group.First();
                var (url, introduction) = GuideSourceLinks[group.Key];
                return new GuideSourceLink(group.Key, group.Key == "tianting-xiaobudian" ? "天庭小不点 · 萌新指南" : entry.SourceName,
                    group.Key == "tianting-xiaobudian" ? "天庭小不点" : entry.Author, entry.SourceMapVersion, introduction, url);
            }).OrderBy(source => source.SourceId == "tianting-xiaobudian" ? 0 : 1).ToArray();
        DropCalculator.LoadProfiles(concepts.CalculatorProfiles);
        SectionButtons.Children.Clear();
        foreach (var item in concepts.Sections)
        {
            var button = new Button { Content = item.Id == "basics" ? "入门说明" : item.Title, Tag = item.Id, Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(11, 6, 11, 6) };
            AutomationProperties.SetName(button, "基础概念：" + item.Title);
            button.Click += (_, _) => SelectSection(item.Id);
            SectionButtons.Children.Add(button);
        }
        if (concepts.Sections.FirstOrDefault() is { } first) SelectSection(first.Id);
    }

    public bool SelectSection(string sectionId)
    {
        var selected = catalog?.Sections.FirstOrDefault(x => x.Id == sectionId);
        if (selected is null) return false;
        changingSection = true;
        section = selected;
        SearchBox.Text = "";
        changingSection = false;
        SectionTitle.Text = selected.Title;
        SectionSummary.Text = selected.Id switch
        {
            "buff" => "按效果分类，查角色与装备",
            "drop" => "从抽签到领取，逐项查看",
            "damage" => "从伤害基数到实际扣血",
            "basics" => "先理解术语，再查具体效果",
            "player-guides" => "按来源查看简介与原始链接",
            _ => selected.Summary
        };
        SectionSummary.ToolTip = selected.Summary;
        SearchHint.Text = selected.Id switch { "buff" => "搜索角色、装备或效果…", "player-guides" => "搜索职业、作者或玩法…", _ => "搜索规则、条件或说明…" };
        SourceFilterBox.Visibility = selected.Id == "buff" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in SectionButtons.Children.OfType<Button>())
            button.Style = (Style)FindResource((string)button.Tag == selected.Id ? "PrimaryButton" : typeof(Button));
        ApplyFilter();
        return true;
    }

    public bool SelectEntry(string entryId)
    {
        if (section?.Id == "player-guides") return false;
        var entry = EntryList.Items.OfType<ConceptEntry>().FirstOrDefault(x => x.Id == entryId);
        if (entry is null) return false;
        EntryList.SelectedItem = entry;
        EntryList.ScrollIntoView(entry);
        return true;
    }

    public void SetFilter(string query) => SearchBox.Text = query;

    public bool SetExamplesExpanded(bool expanded)
    {
        if (ExamplesSection.Visibility != Visibility.Visible) return false;
        ExamplesSection.IsExpanded = expanded;
        if (expanded)
        {
            DetailScroll.UpdateLayout();
            DetailScroll.ScrollToVerticalOffset(Math.Max(0, ExamplesSection.TranslatePoint(new Point(), DetailPanel).Y - 8));
        }
        return true;
    }

    public bool SetFormulasExpanded(bool expanded)
    {
        if (FormulaExpander.Visibility != Visibility.Visible) return false;
        FormulaExpander.IsExpanded = expanded;
        if (expanded)
        {
            DetailScroll.UpdateLayout();
            DetailScroll.ScrollToVerticalOffset(Math.Max(0, FormulaExpander.TranslatePoint(new Point(), DetailPanel).Y - 8));
        }
        return true;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!changingSection && EntryList is not null) ApplyFilter();
    }
    private void ClearSearch_Click(object sender, RoutedEventArgs e) => SetFilter("");
    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderEntry();
    private void SourceFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!changingSection && EntryList is not null && SearchBox is not null) ApplyFilter();
    }

    private bool MatchesSource(ConceptEffect effect) => section?.Id != "buff" || SelectedSourceFilter == "all"
        || effect.SourceKind == SelectedSourceFilter || effect.LinkedSourceKinds.Contains(SelectedSourceFilter);

    private static bool Matches(string text, string query) => query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .All(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    private static string Searchable(ConceptEffect effect) => string.Join(" ", effect.HeroName, effect.SkillName, effect.Key,
        effect.ValueText, effect.TargetText, effect.ApplicationText, effect.StackingDisplayText, effect.ScalingText,
        effect.EquipmentFamilyName, effect.SourceNoticeText, effect.SourceDifferenceText, effect.StackingTooltip, effect.SearchTags);
    private static string Searchable(ConceptEntry entry) => string.Join("\n", new[] { entry.Title, entry.Summary, entry.SearchTags, entry.LeadFormula,
        entry.Author, entry.SourceName, entry.SourceMapVersion ?? "来源版本未注明", entry.SourceVersionNotice }
        .Concat(entry.Paragraphs.Select(x => x.Title + " " + x.Text))
        .Concat(entry.Examples.Select(x => x.InputsText + " " + x.ResultText + " " + x.ConditionsText))
        .Concat(entry.Formulas.Select(x => x.Title + " " + x.Expression + " " + x.VariablesText + " " + x.ConditionsText))
        .Concat(entry.Table?.Rows.Select(x => string.Join(" ", x)) ?? [])
        .Concat(entry.Effects.Select(Searchable)));

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        SearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var sourceLinks = section?.Id == "player-guides";
        ConceptContentGrid.Visibility = sourceLinks ? Visibility.Collapsed : Visibility.Visible;
        GuideSourcesPanel.Visibility = sourceLinks ? Visibility.Visible : Visibility.Collapsed;
        if (sourceLinks)
        {
            EntryList.ItemsSource = Array.Empty<ConceptEntry>();
            RenderEntry();
            RenderGuideSources(query);
            return;
        }
        visibleGuideSources = [];
        GuideSourceCards.Children.Clear();
        var previous = SelectedEntryId;
        var entries = section?.Entries.Where(x => x.Effects.Count > 0
            ? x.Effects.Any(effect => MatchesSource(effect) && (query.Length == 0 || Matches(x.Title + " " + Searchable(effect), query)))
            : (section?.Id != "buff" || SelectedSourceFilter == "all" || x.SourceKind.Length == 0 || x.SourceKind == SelectedSourceFilter)
                && (query.Length == 0 || Matches(Searchable(x), query))).ToList() ?? [];
        EntryList.ItemsSource = entries;
        EntryList.SelectedItem = entries.FirstOrDefault(x => x.Id == previous) ?? entries.FirstOrDefault();
        RenderEntry();
    }

    private void RenderGuideSources(string query)
    {
        visibleGuideSources = guideSources.Where(source => query.Length == 0 || Matches(string.Join(" ", source.Title,
            source.Author, source.Version ?? "版本未注明", source.Introduction), query)).ToArray();
        GuideSourcesCount.Text = query.Length > 0 ? $"匹配 {visibleGuideSources.Count} / {guideSources.Count} 个来源" : $"共 {guideSources.Count} 个来源";
        GuideSourceCards.Children.Clear();
        foreach (var source in visibleGuideSources)
        {
            var stack = new StackPanel();
            var title = Paragraph(source.Title, size: 15); title.FontWeight = FontWeights.SemiBold;
            stack.Children.Add(title);
            var metadata = Paragraph($"作者：{source.Author}  ·  来源版本：{(string.IsNullOrWhiteSpace(source.Version) ? "未注明" : source.Version)}", muted: true, size: 12);
            metadata.Margin = new Thickness(0, 5, 0, 0); stack.Children.Add(metadata);
            var introduction = Paragraph(source.Introduction); introduction.Margin = new Thickness(0, 7, 0, 5); stack.Children.Add(introduction);
            var linkText = Paragraph("");
            if (TryGuideSourceUri(source.Url, out var uri))
            {
                var link = new Hyperlink(new Run(source.Url)) { NavigateUri = uri, ToolTip = "在浏览器中打开原始来源" };
                link.RequestNavigate += OpenGuideSource;
                AutomationProperties.SetName(link, "打开攻略来源：" + source.Title);
                linkText.Inlines.Add(link);
            }
            stack.Children.Add(linkText);
            GuideSourceCards.Children.Add(Card(stack));
        }
        if (visibleGuideSources.Count == 0) GuideSourceCards.Children.Add(Paragraph("没有匹配的攻略来源，可换一个作者、职业或版本试试。", muted: true));
    }

    private static bool TryGuideSourceUri(string value, out Uri? uri)
    {
        uri = null;
        return value.Length <= 4096 && !value.Any(char.IsControl) && GuideSourceLinks.Values.Any(source => source.Url == value)
            && Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 && uri.UserInfo.Length == 0;
    }

    private static void OpenGuideSource(object sender, RequestNavigateEventArgs args)
    {
        args.Handled = true;
        if (!TryGuideSourceUri(args.Uri.AbsoluteUri, out var uri)) return;
        try { Process.Start(new ProcessStartInfo(uri!.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or System.Security.SecurityException)
        { if (sender is Hyperlink link) link.ToolTip = "无法打开浏览器，可复制链接：" + uri!.AbsoluteUri; }
    }

    private TextBlock Paragraph(string text, bool muted = false, double size = 13) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, LineHeight = size >= 13 ? 23 : 20,
        Foreground = (Brush)FindResource(muted ? "MutedBrush" : "InkBrush"), FontWeight = FontWeights.Normal
    };

    private Border Card(UIElement content, Thickness? margin = null) => new()
    {
        Child = content, Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(6),
        BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(1),
        Background = (Brush)FindResource("PaperLightBrush"), Margin = margin ?? new Thickness(0, 0, 0, 9)
    };

    private void RenderEntry()
    {
        if (DetailPanel is null) return;
        var entry = section?.Id == "player-guides" ? null : EntryList.SelectedItem as ConceptEntry;
        EntryTitle.Text = entry?.Title ?? "";
        var playerGuide = entry?.SourceKind == "player_guide";
        var generatedSourceSummary = playerGuide
            ? $"玩家攻略。作者／提供者：{entry!.Author}。来源：{entry.SourceName}。来源版本：{(string.IsNullOrWhiteSpace(entry.SourceMapVersion) ? "未注明" : entry.SourceMapVersion)}。{entry.SourceVersionNotice}"
            : null;
        // Only collapse the exact generated metadata summary. A separately written
        // gameplay summary remains intact; source details appear in the card below.
        EntrySummary.Text = playerGuide && entry!.Summary == generatedSourceSummary
            ? entry.SourceVersionNotice : entry?.Summary ?? "";
        PlayerSourceCard.Visibility = playerGuide ? Visibility.Visible : Visibility.Collapsed;
        PlayerSourceText.Text = playerGuide ? $"作者／提供者：{entry!.Author}\n来源：{entry.SourceName}\n来源版本：{entry.SourceMapVersion ?? "未注明"}" : "";
        KnowledgeImages.Children.Clear();
        LeadFormulaText.Text = entry?.LeadFormula ?? "";
        LeadFormulaCard.Visibility = string.IsNullOrWhiteSpace(LeadFormulaText.Text) ? Visibility.Collapsed : Visibility.Visible;
        ExampleCards.Children.Clear();
        ParagraphCards.Children.Clear();
        FormulaCards.Children.Clear();
        TableGrid.Children.Clear();
        TableGrid.RowDefinitions.Clear();
        TableGrid.ColumnDefinitions.Clear();
        FormulaExpander.IsExpanded = false;
        ExamplesSection.IsExpanded = false;
        RulesExpander.IsExpanded = entry?.Effects.Count == 0;
        RulesExpander.Header = section?.Id switch { "damage" => "计算讲解", "player-guides" => "攻略正文", "basics" => "概念说明", _ => "规则说明" };
        RulesExpander.Visibility = entry?.Paragraphs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = SearchBox.Text.Trim().Length > 0 ? "没有匹配的角色、装备或概念；可换一个词试试。" : "选择左侧分类，查看效果与生效条件。";
        EmptyText.Visibility = entry is null ? Visibility.Visible : Visibility.Collapsed;
        DetailScroll.Visibility = entry is null ? Visibility.Collapsed : Visibility.Visible;
        TableSection.Visibility = Visibility.Collapsed;
        EffectLegendText.Visibility = Visibility.Collapsed;
        DropCalculator.Visibility = entry?.Id == "drop-multiple-loot" && catalog?.CalculatorProfiles.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        TableFilterText.Text = "";
        if (entry is null) return;
        if (playerGuide)
        {
            try
            {
                var images = KnowledgeImageCatalog.Load();
                foreach (var reference in images.ReferencesForEntry("concept:" + entry.Id))
                    KnowledgeImages.Children.Add(KnowledgeImageViewer.CreateCard(reference, images));
            }
            catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or InvalidOperationException
                or ArgumentException or System.Text.Json.JsonException)
            { KnowledgeImages.Children.Add(Paragraph("当前配图未能读取，攻略文字仍可正常查看。", muted: true)); }
        }
        ExamplesSection.Visibility = entry.Examples.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var example in entry.Examples)
        {
            var stack = new StackPanel();
            stack.Children.Add(Paragraph(example.InputsText, muted: true));
            var result = Paragraph(example.ResultText, size: 16);
            result.FontWeight = FontWeights.SemiBold;
            result.Margin = new Thickness(0, 7, 0, 0);
            stack.Children.Add(result);
            if (!string.IsNullOrWhiteSpace(example.ConditionsText))
            {
                var conditions = Paragraph(example.ConditionsText, muted: true, size: 12);
                conditions.Margin = new Thickness(0, 7, 0, 0);
                stack.Children.Add(conditions);
            }
            var card = Card(stack, new Thickness(0, 0, 10, 9));
            card.Width = 235;
            ExampleCards.Children.Add(card);
        }
        foreach (var paragraph in entry.Paragraphs)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 5, 0, 8) };
            if (!string.IsNullOrWhiteSpace(paragraph.Title))
            {
                var title = Paragraph(paragraph.Title);
                title.FontWeight = FontWeights.SemiBold;
                title.Margin = new Thickness(0, 0, 0, 4);
                stack.Children.Add(title);
            }
            stack.Children.Add(Paragraph(paragraph.Text));
            ParagraphCards.Children.Add(stack);
        }
        RenderTable(entry);
        FormulaExpander.Visibility = entry.Formulas.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var formula in entry.Formulas)
        {
            var stack = new StackPanel();
            if (!string.IsNullOrWhiteSpace(formula.Title))
            {
                var title = Paragraph(formula.Title);
                title.FontWeight = FontWeights.SemiBold;
                title.Margin = new Thickness(0, 0, 0, 6);
                stack.Children.Add(title);
            }
            stack.Children.Add(Paragraph(formula.Expression));
            if (!string.IsNullOrWhiteSpace(formula.VariablesText))
            {
                var variables = Paragraph(formula.VariablesText, muted: true);
                variables.Margin = new Thickness(0, 7, 0, 0);
                stack.Children.Add(variables);
            }
            if (!string.IsNullOrWhiteSpace(formula.ConditionsText))
            {
                var conditions = Paragraph(formula.ConditionsText, muted: true);
                conditions.Margin = new Thickness(0, 7, 0, 0);
                stack.Children.Add(conditions);
            }
            FormulaCards.Children.Add(Card(stack));
        }
        DetailScroll.ScrollToTop();
    }

    private void RenderTable(ConceptEntry entry)
    {
        if (entry.Effects.Count > 0)
        {
            RenderEffects(entry);
            return;
        }
        TableSection.Visibility = entry.Table is null ? Visibility.Collapsed : Visibility.Visible;
        if (entry.Table is not { } table) return;
        var query = SearchBox.Text.Trim();
        // Entry text keeps a matching topic discoverable; table filtering still
        // applies to the rows themselves, even when its title also matches.
        var filterRows = query.Length > 0;
        var rows = table.Rows.Where(row => !filterRows || Matches(string.Join(" ", row), query)).ToList();
        TableFilterText.Text = filterRows ? $"匹配 {rows.Count} / {table.Rows.Count} 项 · 清除搜索查看全部" : $"共 {rows.Count} 项 · 按行对照条件与结果";
        if (entry.Id == "drop-multiple-loot") TableFilterText.Text = "默认表：4人普通模式，无许愿／放手／徽章或额外加成；普通物品包括材料，不计魔石与结晶。爵土马为每人，判天为共享一份。";
        for (var column = 0; column < table.Columns.Count; column++)
            TableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(column == 0 ? 1.1 : 1, GridUnitType.Star) });
        AddTableRow(table.Columns, 0, true);
        for (var row = 0; row < rows.Count; row++) AddTableRow(rows[row], row + 1, false);
    }

    private void RenderEffects(ConceptEntry entry)
    {
        TableSection.Visibility = Visibility.Visible;
        EffectLegendText.Visibility = Visibility.Visible;
        var query = SearchBox.Text.Trim();
        var sourceEffects = entry.Effects.Where(MatchesSource).ToList();
        var effects = sourceEffects.Where(effect => query.Length == 0 || Matches(entry.Title + " " + Searchable(effect), query)).ToList();
        var paperColors = ((GradientBrush)FindResource("ParchmentBrush")).GradientStops.Select(stop => stop.Color)
            .Append(((SolidColorBrush)FindResource("PaperLightBrush")).Color).ToArray();
        EffectLegendText.Inlines.Clear();
        EffectLegendText.Text = "悬停数值看属性加成，悬停叠加规则看比较范围。";
        var sourceLabel = SelectedSourceFilter switch { "hero" => "英雄与召唤物", "equipment" => "装备", _ => "全部来源" };
        TableFilterText.Text = query.Length == 0 ? $"{effects.Count} 项效果 · {sourceLabel}"
            : $"匹配 {effects.Count} / {sourceEffects.Count} 项 · {sourceLabel}";
        foreach (var width in new[] { 1.6, 1.15, .95, 2.45, .65 })
            TableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
        AddTableRow(["角色 / 装备 / 技能", "效果数值", "作用对象", "生效条件", "叠加规则"], 0, true);
        for (var index = 0; index < effects.Count; index++)
        {
            var effect = effects[index];
            var row = index + 1;
            TableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var source = new StackPanel();
            source.Children.Add(SourceLine(effect.HeroName, effect.HeroIcon, 24, true));
            var skillName = effect.SkillName + (string.IsNullOrWhiteSpace(effect.Key) ? "" : " · " + effect.Key);
            var skill = effect.SourceKind == "equipment" ? Paragraph(skillName, size: 12)
                : SourceLine(skillName, effect.SkillIcon, 22, false);
            skill.Margin = new Thickness(0, 5, 0, 0);
            source.Children.Add(skill);
            if (!string.IsNullOrWhiteSpace(effect.LinkedEquipmentText))
            {
                var linked = Paragraph("关联装备：" + effect.LinkedEquipmentText, muted: true, size: 11);
                linked.Margin = new Thickness(0, 5, 0, 0);
                source.Children.Add(linked);
            }
            if (!string.IsNullOrWhiteSpace(effect.SourceDisplayText))
            {
                var notice = Paragraph(effect.SourceDisplayText, muted: true, size: 11);
                notice.Margin = new Thickness(0, 5, 0, 0);
                notice.ToolTip = effect.SourceTooltip;
                source.Children.Add(notice);
            }
            else if (!string.IsNullOrWhiteSpace(effect.SourceTooltip)) source.ToolTip = effect.SourceTooltip;
            AddCell(source, row, 0, 5);
            var value = Paragraph(effect.ValueText, size: 13);
            value.FontWeight = FontWeights.SemiBold;
            var rendering = ConceptEffectRendering.Apply(value, effect.ValueText, effect.ValueRuns,
                ((SolidColorBrush)FindResource("InkBrush")).Color, paperColors);
            value.Tag = rendering;
            if (!string.IsNullOrWhiteSpace(effect.ScalingText)) value.ToolTip = effect.ScalingText;
            AddCell(value, row, 1, 5);
            AddCell(Paragraph(Display(effect.TargetText), size: 12), row, 2, 5);
            AddCell(Paragraph(Display(effect.ApplicationText), size: 12), row, 3, 5);
            var stacking = Paragraph(effect.StackingDisplayText, size: 12);
            if (!string.IsNullOrWhiteSpace(effect.StackingTooltip)) stacking.ToolTip = effect.StackingTooltip;
            AddCell(stacking, row, 4, 5);
        }
    }

    private static string Display(string value) => string.IsNullOrWhiteSpace(value) ? "未注明" : value;

    private FrameworkElement SourceLine(string name, string icon, double size, bool hero)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var picture = ResourceImage.Load(icon);
        var iconFrame = new Border
        {
            Width = size, Height = size, BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("LineBrush"), Background = (Brush)FindResource("PaperBrush"),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 6, 0),
            Child = picture is null
                ? new TextBlock { Text = "◇", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                : new Image { Source = picture, Width = size - 2, Height = size - 2, Stretch = Stretch.Uniform }
        };
        AutomationProperties.SetName(iconFrame, name + (picture is null ? "（图标待补）" : "图标"));
        grid.Children.Add(iconFrame);
        var label = Paragraph(name, size: 12);
        label.FontWeight = hero ? FontWeights.SemiBold : FontWeights.Normal;
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        return grid;
    }

    private void AddCell(UIElement content, int row, int column, int columns, bool header = false)
    {
        var cell = new Border
        {
            Child = content, Padding = new Thickness(8, header ? 8 : 9, 8, header ? 8 : 9),
            Background = header ? new SolidColorBrush(Color.FromRgb(230, 209, 169))
                : row % 2 == 1 ? (Brush)FindResource("PaperLightBrush") : Brushes.Transparent,
            BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(0, 0, column == columns - 1 ? 0 : 1, 1)
        };
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        TableGrid.Children.Add(cell);
    }

    private void AddTableRow(IReadOnlyList<string> values, int row, bool header)
    {
        TableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var column = 0; column < values.Count; column++)
        {
            var text = Paragraph(values[column], size: 12);
            if (header) text.FontWeight = FontWeights.SemiBold;
            AddCell(text, row, column, values.Count, header);
        }
    }

    private void View_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IndexColumn is not null) IndexColumn.Width = new GridLength(e.NewSize.Width < 1000 ? 170 : 205);
        if (SearchField is not null) SearchField.Width = e.NewSize.Width < 1000 ? 195 : 245;
        if (FilterFields is not null && SourceFilterBox is not null)
        {
            var compact = e.NewSize.Width < 1200;
            FilterFields.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
            SourceFilterBox.Margin = new Thickness(12, 0, 0, compact ? 6 : 0);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject element)
    {
        foreach (var index in Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(element)))
        {
            var child = VisualTreeHelper.GetChild(element, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static string ReadText(DependencyObject element)
    {
        if (element is TextBlock text) return GuideTextRendering.ReadText(text);
        return string.Join("\n", Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(element))
            .Select(index => ReadText(VisualTreeHelper.GetChild(element, index))).Where(x => x.Length > 0));
    }
}
