using System.Windows;
using System.Windows.Controls;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop.Views;

public partial class HeroGuideView : UserControl
{
    private Window? layoutWindow;
    private static readonly DependencyPropertyKey IsCompactPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsCompact), typeof(bool), typeof(HeroGuideView), new PropertyMetadata(false));
    public static readonly DependencyProperty IsCompactProperty = IsCompactPropertyKey.DependencyProperty;
    public bool IsCompact => (bool)GetValue(IsCompactProperty);

    public HeroGuideView()
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
    public string? SelectedHeroId => (HeroPicker.SelectedItem as GuideHero)?.Id;
    public string? SelectedUnitId => (UnitList.SelectedItem as GuideUnit)?.Id;
    public string? SelectedSkillId => SkillPanel.SelectedSkillId;

    public void Load(GuideCatalog guide)
    {
        HeroPicker.ItemsSource = guide.Heroes;
        HeroPicker.SelectedIndex = guide.Heroes.Count > 0 ? 0 : -1;
    }

    public bool SelectHero(string heroId)
    {
        var hero = HeroPicker.Items.OfType<GuideHero>().FirstOrDefault(x => x.Id == heroId);
        if (hero is null) return false;
        HeroPicker.SelectedItem = hero;
        return true;
    }

    public bool SelectUnit(string unitId)
    {
        var unit = UnitList.Items.OfType<GuideUnit>().FirstOrDefault(x => x.Id == unitId);
        if (unit is null) return false;
        UnitList.SelectedItem = unit;
        return true;
    }

    public bool SelectSkill(string skillId) => SkillPanel.SelectSkill(skillId);

    public object InspectGuideUi() => new
    {
        SelectedHeroId, SelectedUnitId, SelectedSkillId,
        Heroes = HeroPicker.Items.Count, Units = UnitList.Items.Count,
        OriginText = (UnitList.SelectedItem as GuideUnit)?.OriginText ?? "",
        Skills = SkillPanel.SkillCount, BasicText = SkillPanel.VisibleBasicText,
        DetailsText = SkillPanel.VisibleDetailsText, IconPlaceholderVisible = SkillPanel.SelectedSkillIconMissing
    };

    private void HeroPicker_Click(object sender, RoutedEventArgs e) => HeroPickerPopup.IsOpen = !HeroPickerPopup.IsOpen;

    private void HeroPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HeroPicker.SelectedItem is not GuideHero hero) return;
        HeroNameText.Text = hero.Name;
        HeroPortrait.Source = ResourceImage.Load(hero.Icon);
        HeroPickerPopup.IsOpen = false;
        UnitList.ItemsSource = hero.Units;
        UnitList.SelectedIndex = hero.Units.Count > 0 ? 0 : -1;
        if (hero.Units.Count == 0) SkillPanel.Load([]);
    }

    private void UnitList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SkillPanel.Load((UnitList.SelectedItem as GuideUnit)?.Skills ?? []);
}
