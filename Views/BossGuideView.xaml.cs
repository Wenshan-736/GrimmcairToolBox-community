using System.Windows;
using System.Windows.Controls;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop.Views;

public partial class BossGuideView : UserControl
{
    private const string FullDropScope = "基础值尚未计入人数、许愿等适用修正；查看掉率说明可了解抽签和领取条件。";
    private const string InformationDropScope = "显示已收录的基础掉落信息，实际掉落还受游戏条件影响。";

    public event Action<string>? ItemRequested;
    public event Action? DropRulesRequested;
    public void OpenDropRules() => DropRulesRequested?.Invoke();
    public string? SelectedBossId => (BossPicker.SelectedItem as GuideBoss)?.Id;
    public string? SelectedUnitId => (UnitList.SelectedItem as GuideUnit)?.Id;
    public string? SelectedDropItemId => (DropList.SelectedItem as GuideDrop)?.ItemId;
    public string? SelectedSkillId => SkillPanel.SelectedSkillId;
    public string SelectedSection { get; private set; } = "attributes";
    internal bool IsInformationOnly => BossPicker.SelectedItem is GuideBoss { InformationOnly: true };
    internal bool SkillsTabVisible => SkillsButton.Visibility == Visibility.Visible;
    internal bool PlayersTabVisible => PlayersButton.Visibility == Visibility.Visible;

    public BossGuideView()
    {
        InitializeComponent();
        ShowSection("attributes");
    }

    public void Load(GuideCatalog guide)
    {
        BossPicker.ItemsSource = guide.Bosses;
        BossPicker.SelectedIndex = guide.Bosses.Count > 0 ? 0 : -1;
    }

    public bool SelectBoss(string bossId)
    {
        var boss = BossPicker.Items.OfType<GuideBoss>().FirstOrDefault(x => x.Id == bossId);
        if (boss is null) return false;
        BossPicker.SelectedItem = boss;
        return true;
    }

    public bool ShowDrops(string bossId, string? itemId = null)
    {
        if (!SelectBoss(bossId)) return false;
        UnitList.SelectedIndex = 0;
        ShowSection("drops");
        var drop = DropList.Items.OfType<GuideDrop>().FirstOrDefault(x => x.ItemId == itemId);
        DropList.SelectedItem = drop;
        if (drop is not null)
        {
            DropList.UpdateLayout();
            DropList.ScrollIntoView(drop);
        }
        return true;
    }

    public void ShowSection(string section)
    {
        if (section is not ("attributes" or "skills" or "drops" or "players"))
            throw new ArgumentOutOfRangeException(nameof(section));
        if (IsInformationOnly && section is ("skills" or "players")) section = "drops";
        SelectedSection = section;
        AttributesPanel.Visibility = section == "attributes" ? Visibility.Visible : Visibility.Collapsed;
        SkillPanel.Visibility = section == "skills" ? Visibility.Visible : Visibility.Collapsed;
        DropsPanel.Visibility = section == "drops" ? Visibility.Visible : Visibility.Collapsed;
        PlayersPanel.Visibility = section == "players" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in SectionButtons.Children.OfType<Button>())
            button.Style = (Style)FindResource((string?)button.Tag == section ? "PrimaryButton" : typeof(Button));
    }

    public bool SelectSkill(string skillId) => !IsInformationOnly && SkillPanel.SelectSkill(skillId);

    public bool SelectUnit(string unitId)
    {
        var unit = UnitList.Items.OfType<GuideUnit>().FirstOrDefault(x => x.Id == unitId);
        if (unit is null) return false;
        UnitList.SelectedItem = unit;
        return true;
    }

    public bool OpenSelectedDrop()
    {
        if (DropList.SelectedItem is not GuideDrop drop) return false;
        ItemRequested?.Invoke(drop.ItemId);
        return true;
    }

    public object InspectGuideUi() => new
    {
        SelectedBossId, SelectedUnitId, SelectedSection, SelectedDropItemId, SelectedSkillId,
        InformationOnly = IsInformationOnly, SkillsTabVisible, PlayersTabVisible,
        Units = UnitList.Items.Count,
        Bosses = BossPicker.Items.Count, Drops = DropList.Items.Count,
        Skills = SkillPanel.SkillCount, AttributesText = AttributesText.Text,
        PlayersText = PlayersText.Text, BasicText = SkillPanel.VisibleBasicText,
        DetailsText = SkillPanel.VisibleDetailsText, IconPlaceholderVisible = SkillPanel.SelectedSkillIconMissing,
        DropScopeText = DropScopeText.Text
    };

    private void BossPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BossPicker.SelectedItem is not GuideBoss boss) return;
        BossPortrait.Source = ResourceImage.Load(boss.Icon);
        BossNameText.Text = boss.Name;
        BossStageText.Text = boss.Stage;
        SkillsButton.Visibility = boss.InformationOnly ? Visibility.Collapsed : Visibility.Visible;
        PlayersButton.Visibility = boss.InformationOnly ? Visibility.Collapsed : Visibility.Visible;
        PlayersText.Text = boss.InformationOnly ? "" : boss.PlayersText;
        DropScopeText.Text = !string.IsNullOrWhiteSpace(boss.DropSummaryText) ? boss.DropSummaryText
            : boss.InformationOnly ? InformationDropScope : FullDropScope;
        SkillPanel.Load([]);
        UnitList.ItemsSource = boss.Units.Count > 0 ? boss.Units :
            new List<GuideUnit> { new() { Id = boss.Id, Name = boss.Name, Icon = boss.Icon,
                AttributesText = boss.AttributesText, Skills = boss.Skills, OriginText = "BOSS 本体" } };
        UnitList.SelectedIndex = 0;
        DropList.ItemsSource = boss.Drops;
        DropsEmptyText.Visibility = boss.Drops.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowSection(SelectedSection);
        AttributesPanel.ScrollToTop();
        PlayersPanel.ScrollToTop();
    }

    private void UnitList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UnitList.SelectedItem is not GuideUnit unit)
        {
            AttributeHeading.Text = "基础属性";
            AttributesText.Text = "";
            UnitOriginText.Text = "";
            SkillPanel.Load([]);
            return;
        }
        AttributeHeading.Text = unit.Name + " · 基础属性";
        AttributesText.Text = string.IsNullOrWhiteSpace(unit.AttributesText) ? "属性资料待补充。" : unit.AttributesText;
        UnitOriginText.Text = unit.OriginText;
        var isBody = unit.Id == SelectedBossId;
        AttributesButton.Content = isBody ? "BOSS 属性" : "单位属性";
        SkillsButton.Content = isBody ? "BOSS 技能" : "单位技能";
        SkillPanel.Load(IsInformationOnly ? Array.Empty<GuideSkill>() : unit.Skills);
        AttributesPanel.ScrollToTop();
    }

    private void Section_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section }) ShowSection(section);
    }

    private void DropItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string itemId }) ItemRequested?.Invoke(itemId);
    }
    private void DropRules_Click(object sender, RoutedEventArgs e) => OpenDropRules();
}
