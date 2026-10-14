using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop.Views;

public partial class DropCalculatorView : UserControl
{
    private bool changing;
    private readonly List<DropParticipantInput> roster = [];
    private List<DropCalculatorProfile> profiles = [];
    private static readonly (string? Id, string Name)[] Badges = [(null, "未启用增掉率徽章"),
        ("I0MD", "永恒·世界大咖徽章"), ("I02T", "世界传奇徽章"), ("I03X", "不朽者徽章")];
    public DropCalculatorView() => InitializeComponent();
    public DropCalculatorProfile? SelectedProfile => BossBox.SelectedItem as DropCalculatorProfile;
    public DropProbabilityResult? LastResult { get; private set; }
    public string VisibleResultText => string.Join("\n", ResultCards.Children.OfType<Border>()
        .SelectMany(card => ((StackPanel)card.Child).Children.OfType<TextBlock>()).Select(text => text.Text));
    public string ValidationMessage => ValidationText.Visibility == Visibility.Visible ? ValidationText.Text : "";

    public void LoadProfiles(List<DropCalculatorProfile> data)
    {
        changing = true;
        profiles = data;
        BossBox.ItemsSource = profiles;
        BossBox.SelectedItem = profiles.FirstOrDefault();
        changing = false;
        ConfigureBoss();
    }

    public bool SetBoss(string id)
    {
        var profile = profiles.FirstOrDefault(profile => profile.Id == id);
        if (profile is null) return false;
        BossBox.SelectedItem = profile;
        return true;
    }

    public void SetCounts(int participants, int? population = null)
    {
        changing = true;
        ParticipantCountBox.Text = participants.ToString(CultureInfo.InvariantCulture);
        PopulationCountBox.Text = (population ?? participants).ToString(CultureInfo.InvariantCulture);
        changing = false;
        ResizeRoster();
    }

    public void SetPopulationMultiplier(double? multiplier)
    {
        changing = true;
        ManualPopulationBox.IsChecked = multiplier.HasValue;
        PopulationMultiplierBox.Text = (multiplier ?? 1).ToString(CultureInfo.InvariantCulture);
        changing = false;
        Recalculate();
    }

    public bool SetScope(string scope)
    {
        var item = ScopeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag as string == scope);
        if (item is null) return false;
        ScopeBox.SelectedItem = item;
        return true;
    }

    public void SetParticipant(int index, int forfeitTarget = -1, string? wishItemId = null, string? badgeId = null)
    {
        roster[index] = new() { ForfeitTarget = forfeitTarget, WishItemId = wishItemId, BadgeId = badgeId };
        RenderRoster();
        Recalculate();
    }

    private void BossBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!changing && RosterCards is not null) ConfigureBoss(); }
    private void ParticipantCountBox_TextChanged(object sender, TextChangedEventArgs e)
    { if (!changing && RosterCards is not null) ResizeRoster(); }
    private void Input_Changed(object sender, RoutedEventArgs e)
    { if (!changing && ResultCards is not null) Recalculate(); }

    private void ConfigureBoss()
    {
        if (SelectedProfile is not { } profile) { ClearResults(); return; }
        changing = true;
        ParticipantCountBox.Text = profile.Id == "h09V" ? "6" : "10";
        PopulationCountBox.Text = "10";
        PopulationField.Visibility = profile.PopulationFromParticipants ? Visibility.Collapsed : Visibility.Visible;
        ManualPopulationBox.IsChecked = false;
        roster.Clear();
        RosterExpander.IsExpanded = false;
        changing = false;
        ResizeRoster();
    }

    private void ResizeRoster()
    {
        if (!TryCount(ParticipantCountBox.Text, 1, out var count))
        { Invalid("累计参战人数请填写1～10的整数。"); return; }
        while (roster.Count < count) roster.Add(new());
        if (roster.Count > count) roster.RemoveRange(count, roster.Count - count);
        for (var i = 0; i < roster.Count; i++)
            if (roster[i].ForfeitTarget >= count && roster[i].ForfeitTarget != 10)
                ChangeMember(i, forfeitTarget: -1);
        RenderRoster();
        Recalculate();
    }

    private void RenderRoster()
    {
        changing = true;
        RosterCards.Children.Clear();
        var profile = SelectedProfile;
        var personal = profile?.Mode is 1 or 2;
        RosterExpander.Header = $"逐人设置许愿、放手和徽章（{roster.Count}人）";
        RosterNote.Text = personal ? "每人单独选择；默认无人许愿、无人放手、未启用徽章。玩家1作为本人。徽章须已启用；给未参战者放手不产生新的抽取资格。"
            : "判、天是全队共享一次抽取，不读取个人许愿、放手或徽章。这里的个人选择不会改变共享概率。";
        for (var i = 0; i < roster.Count; i++)
        {
            var index = i;
            var member = roster[i];
            var stack = new StackPanel();
            stack.Children.Add(Label(PlayerName(i), true));
            var wish = Choice("愿望", profile?.AllowsWish == true && personal);
            wish.Items.Add(new ComboBoxItem { Content = "不许愿", Tag = "" });
            foreach (var row in profile?.Rows.Where(row => !row.Special).DistinctBy(row => row.ItemId) ?? [])
                wish.Items.Add(new ComboBoxItem { Content = row.Name, Tag = row.ItemId });
            SelectTag(wish, member.WishItemId ?? "");
            wish.SelectionChanged += (_, _) => { if (!changing) { ChangeMember(index, wishItem: SelectedTag(wish) as string, changeWish: true); Recalculate(); } };
            stack.Children.Add(Label("许愿")); stack.Children.Add(wish);
            var forfeit = Choice("放手对象", profile?.AllowsWish == true && personal);
            forfeit.Items.Add(new ComboBoxItem { Content = "不放手", Tag = -1 });
            forfeit.Items.Add(new ComboBoxItem { Content = "放手给所有人", Tag = 10 });
            for (var j = 0; j < roster.Count; j++) if (j != i)
                forfeit.Items.Add(new ComboBoxItem { Content = "放手给" + PlayerName(j), Tag = j });
            SelectTag(forfeit, member.ForfeitTarget);
            forfeit.SelectionChanged += (_, _) => { if (!changing) { ChangeMember(index, forfeitTarget: (int)(SelectedTag(forfeit) ?? -1)); Recalculate(); } };
            stack.Children.Add(Label("放手")); stack.Children.Add(forfeit);
            var badge = Choice("已启用徽章", personal);
            foreach (var option in Badges) badge.Items.Add(new ComboBoxItem { Content = option.Name, Tag = option.Id ?? "" });
            SelectTag(badge, member.BadgeId ?? "");
            badge.SelectionChanged += (_, _) => { if (!changing) { ChangeMember(index, badgeId: SelectedTag(badge) as string, changeBadge: true); Recalculate(); } };
            stack.Children.Add(Label("已启用徽章")); stack.Children.Add(badge);
            RosterCards.Children.Add(new Border { Child = stack, Width = 205, Padding = new Thickness(9), Margin = new Thickness(0, 0, 8, 8),
                BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) });
        }
        changing = false;
    }

    private void ChangeMember(int index, int? forfeitTarget = null, string? wishItem = null, bool changeWish = false,
        string? badgeId = null, bool changeBadge = false)
    {
        var previous = roster[index];
        roster[index] = new() { ForfeitTarget = forfeitTarget ?? previous.ForfeitTarget,
            WishItemId = changeWish ? (string.IsNullOrEmpty(wishItem) ? null : wishItem) : previous.WishItemId,
            BadgeId = changeBadge ? (string.IsNullOrEmpty(badgeId) ? null : badgeId) : previous.BadgeId };
    }

    private static object? SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag;
    private static void SelectTag(ComboBox box, object tag) => box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, tag)) ?? box.Items[0];
    private ComboBox Choice(string name, bool enabled)
    {
        var box = new ComboBox { IsEnabled = enabled, MinHeight = 34 };
        AutomationProperties.SetName(box, name);
        return box;
    }
    private TextBlock Label(string text, bool title = false) => new() { Text = text, TextWrapping = TextWrapping.Wrap,
        FontWeight = title ? FontWeights.SemiBold : FontWeights.Normal, FontSize = title ? 14 : 12,
        Margin = new Thickness(0, title ? 0 : 5, 0, 4), Foreground = (Brush)FindResource("InkBrush") };
    private static string PlayerName(int index) => index == 0 ? "玩家1（本人）" : $"玩家{index + 1}";
    private static bool TryCount(string text, int minimum, out int count) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out count) && count >= minimum && count <= 10;
    private static string Percent(double probability)
    {
        var percent = probability * 100;
        return (percent is > 0 and < .00000001 ? percent.ToString("0.###E+0", CultureInfo.InvariantCulture)
            : percent.ToString("0.########", CultureInfo.InvariantCulture)) + "%";
    }

    private void Recalculate()
    {
        if (SelectedProfile is not { } profile) { ClearResults(); return; }
        PopulationMultiplierBox.IsEnabled = ManualPopulationBox.IsChecked == true;
        if (!TryCount(ParticipantCountBox.Text, 1, out var count) || count != roster.Count)
        { Invalid("累计参战人数请填写1～10的整数。"); return; }
        var population = count;
        if (!profile.PopulationFromParticipants && !TryCount(PopulationCountBox.Text, 0, out population))
        { Invalid("全图符合条件人数请填写0～10的整数。"); return; }
        var steps = population < profile.PopulationLowerCount ? 0 : Math.Min(population, profile.PopulationUpperCount) - profile.PopulationLowerCount + 1;
        var defaultMultiplier = 1 + steps * profile.DropPerStepPercent / 100;
        PopulationDefaultText.Text = $"本版自动值：×{defaultMultiplier.ToString("0.####", CultureInfo.InvariantCulture)}";
        double? overridePercent = null;
        if (ManualPopulationBox.IsChecked == true)
        {
            if (!double.TryParse(PopulationMultiplierBox.Text.Trim(), NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var multiplier) || !double.IsFinite(multiplier) || multiplier < 0 || multiplier > 100)
            { Invalid("人数倍率请填写0～100的数值；例如1.5表示提高50%。"); return; }
            overridePercent = (multiplier - 1) * 100;
        }
        else
        {
            changing = true;
            PopulationMultiplierBox.Text = defaultMultiplier.ToString("0.####", CultureInfo.InvariantCulture);
            changing = false;
        }
        var scope = SelectedTag(ScopeBox) as string ?? "ordinary";
        ScopeNote.Text = scope switch { "equipment" => "按当前目录的穿戴分类统计装备；材料、徽章及另算掉物不计入件数。目录分类不代替徽章资格判定。",
            "allRegistered" => "统计完整登记列表，包括另算魔石、结晶等；一件是结果中的一种物品，不是物品叠加数量。",
            _ => "统计普通列表中的物品，包含装备与材料；另算的魔石、结晶等不计入件数。同一份结果中同名物品只计一项，不按叠加数量。" };
        try
        {
            var result = DropProbabilityCalculator.Calculate(profile, new DropCalculationInput { Roster = roster,
                ReceiverIndex = 0, PopulationCount = population, PopulationBonusOverridePercent = overridePercent, Scope = scope });
            LastResult = result;
            ValidationText.Visibility = Visibility.Collapsed;
            ResultCards.Children.Clear(); ProbabilityDetails.Children.Clear();
            var personal = profile.Mode is 1 or 2;
            ResultCards.Children.Add(DistributionCard(personal ? "本人这份结果" : "全队共享这份结果", result.ReceiverDistribution));
            if (personal) ResultCards.Children.Add(DistributionCard($"全队合计（{count}人）", result.TeamDistribution));
            ConditionSummary.Text = personal
                ? $"普通模式 · {roster.Count(member => member.ForfeitTarget != -1)}人放手，{roster.Count(member => member.WishItemId is not null)}人许愿，{roster.Count(member => member.BadgeId is not null)}人启用徽章。人数乘区×{(1 + result.PopulationBonusPercent / 100).ToString("0.####", CultureInfo.InvariantCulture)}；综合掉率倍率×{result.DropContainerMultiplier.ToString("0.####", CultureInfo.InvariantCulture)}。"
                : $"普通模式 · 共享结果只抽一次；个人徽章、许愿、放手不参与。人数乘区×{(1 + result.PopulationBonusPercent / 100).ToString("0.####", CultureInfo.InvariantCulture)}。";
            ResultNote.Text = "以上是每次击杀的无条件概率；‘恰好1／2／3件’排除其他数量。全队合计保留不同玩家各自抽到的同名物品；共享结果只计一次。概率按已核脚本、固定输入与独立连续随机模型计算，尚无游戏实测。";
            for (var i = 0; i < (personal ? result.ParticipantCompensationFactors.Count : 1); i++)
                ProbabilityDetails.Children.Add(Label((personal ? PlayerName(i) : "共享结果") + $"：少人／放手补偿×{result.ParticipantCompensationFactors[i].ToString("0.####", CultureInfo.InvariantCulture)}"));
            foreach (var row in result.RowDetails)
                ProbabilityDetails.Children.Add(Label((personal ? PlayerName(row.PlayerIndex) + " · " : "") + row.Name + "："
                    + (!row.Resolved ? "所需分类尚未确认" : row.Eligible ? Percent(row.Probability) : "本次不参加抽取") + (row.Counted ? "" : "（不计入当前统计）")
                    + (row.BadgeMultiplier > 1 ? $" · 徽章×{row.BadgeMultiplier.ToString("0.#", CultureInfo.InvariantCulture)}" : "")));
            foreach (var note in result.BoundaryNotes) ProbabilityDetails.Children.Add(Label(note));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.IO.InvalidDataException)
        { Invalid("当前条件不能完整计算：" + error.Message); }
    }

    private Border DistributionCard(string title, DropCountDistribution distribution)
    {
        var stack = new StackPanel();
        stack.Children.Add(Label(title, true));
        foreach (var (name, probability) in new[] { ("没有掉落", distribution.ProbabilityOf(0)), ("恰好1件", distribution.ProbabilityOf(1)),
            ("恰好2件", distribution.ProbabilityOf(2)), ("恰好3件", distribution.ProbabilityOf(3)),
            ("4件及以上", distribution.AtLeastFourProbability), ("至少1件", 1 - distribution.ProbabilityOf(0)) })
            stack.Children.Add(Label(name + "：" + Percent(probability)));
        stack.Children.Add(Label($"最多{distribution.MaximumCount}件 · 理论平均{distribution.ExpectedCount.ToString("0.######", CultureInfo.InvariantCulture)}件"));
        return new Border { Child = stack, Width = 260, Padding = new Thickness(10), Margin = new Thickness(0, 0, 10, 8),
            Background = (Brush)FindResource("PaperBrush"), BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
    }
    private void ClearResults() { LastResult = null; ResultCards.Children.Clear(); ProbabilityDetails.Children.Clear(); }
    private void Invalid(string message) { ClearResults(); ValidationText.Text = message; ValidationText.Visibility = Visibility.Visible; ConditionSummary.Text = ""; }
}
