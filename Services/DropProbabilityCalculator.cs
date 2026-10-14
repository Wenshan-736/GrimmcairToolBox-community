namespace Grimmcair.Desktop.Services;

// Typed projections of the fixed map's drop models. IDs are internal; the UI displays names.
public sealed class DropCalculatorProfile
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string StageAlias { get; init; } = "";
    public int Mode { get; init; } = 2;
    public int Group { get; init; } = 5;
    public bool AllowsWish { get; init; }
    public int OrdinaryRowCount { get; init; }
    public int OrdinarySuccessCap { get; init; }
    public int PopulationLowerCount { get; init; }
    public int PopulationUpperCount { get; init; }
    public double DropPerStepPercent { get; init; }
    public bool PopulationFromParticipants { get; init; } = true;
    public bool AllowsHard { get; init; }
    public bool AllowsPractice { get; init; }
    public double HardDropBonusPercent { get; init; }
    public bool ImmortalBadgeException { get; init; }
    public List<DropCalculatorRow> Rows { get; init; } = [];
}

public sealed class DropCalculatorRow
{
    public int RegistrationIndex { get; init; } = -1;
    public string ItemId { get; init; } = "";
    public string Name { get; init; } = "";
    // Percentage threshold points: 0.6 means 0.6%, not 60%.
    public double BasePercent { get; init; }
    public bool Special { get; init; }
    public int? ItemCategory { get; init; }
    public int? ItemTier { get; init; }
    public bool? IsEquipment { get; init; }
}

public sealed class DropParticipantInput
{
    // Roster index: -1 means unset, 10 means everyone. A target outside this roster grants no new eligibility.
    public int ForfeitTarget { get; init; } = -1;
    public string? WishItemId { get; init; }
    // Exactly one equipped badge: null, I0MD, I02T, or I03X. Merely carrying one has no effect.
    public string? BadgeId { get; init; }
}

public sealed class DropCalculationInput
{
    public List<DropParticipantInput> Roster { get; init; } = [];
    public int ReceiverIndex { get; init; }
    // For global-population profiles this is the current level-100 global count, separate from the retained roster.
    public int? PopulationCount { get; init; }
    // Replaces only the population node; e.g. multiplier 1.5 is +50, and multiplier zero is -100.
    public double? PopulationBonusOverridePercent { get; init; }
    public List<double> ExtraDropBonusesPercent { get; init; } = [];
    public string Mode { get; init; } = "normal";
    public string Scope { get; init; } = "ordinary";
}

public sealed class DropCountDistribution
{
    public List<double> Probabilities { get; init; } = [1];
    public int MaximumCount => Math.Max(0, Probabilities.FindLastIndex(probability => probability > 0));
    public double ExpectedCount => Probabilities.Select((probability, count) => probability * count).Sum();
    public double AtLeastFourProbability => Probabilities.Skip(4).Sum();
    public double ProbabilityOf(int count) => count >= 0 && count < Probabilities.Count ? Probabilities[count] : 0;
}

public sealed class DropRowProbability
{
    // -1 identifies the one shared roll, not a particular recipient.
    public int PlayerIndex { get; init; }
    public int RegistrationIndex { get; init; }
    public string ItemId { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Special { get; init; }
    public bool Eligible { get; init; }
    public bool Counted { get; init; }
    public bool Resolved { get; init; } = true;
    public double ThresholdPercent { get; init; }
    public double Probability { get; init; }
    public double BadgeMultiplier { get; init; } = 1;
}

public sealed class DropProbabilityResult
{
    public bool Shared { get; init; }
    public DropCountDistribution ReceiverDistribution { get; init; } = new();
    public DropCountDistribution TeamDistribution { get; init; } = new();
    public List<DropCountDistribution> ParticipantDistributions { get; init; } = [];
    public List<double> ParticipantCompensationFactors { get; init; } = [];
    public List<DropRowProbability> RowDetails { get; init; } = [];
    public int PopulationCount { get; init; }
    public int PopulationSteps { get; init; }
    public double AutomaticPopulationBonusPercent { get; init; }
    public double PopulationBonusPercent { get; init; }
    public double DropContainerMultiplier { get; init; }
    public List<string> BoundaryNotes { get; init; } = [];
}

/// <summary>
/// Conditional exact distribution for MP1/2 private result sets and MP3 shared sets.
/// Uses fixed-state, independent uniform continuous draws, not measured game RNG or eventual receipt.
/// Executes typed coefficients only and performs no I/O, random sampling, or expression evaluation.
/// </summary>
public static class DropProbabilityCalculator
{
    public static void ValidateProfile(DropCalculatorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Name)
            || profile.Rows is null || profile.Rows.Count is < 1 or > 128)
            throw new ArgumentException("掉落配置缺少有效身份或登记条目。");
        if (profile.Mode is < 1 or > 3)
            throw new ArgumentException("当前计算器只支持个人或共用战利品箱；地面限件抽取尚未适配。");
        if (profile.Group < 0 || profile.OrdinarySuccessCap < 0
            || profile.PopulationLowerCount is < 0 or > 10 || profile.PopulationUpperCount < profile.PopulationLowerCount
            || profile.PopulationUpperCount > 10 || !FiniteAtLeast(profile.DropPerStepPercent, -100)
            || !FiniteAtLeast(profile.HardDropBonusPercent, -100))
            throw new ArgumentException("掉落配置的人数、模式或乘区参数不完整。");
        if (profile.Rows.Any(row => row is null || string.IsNullOrWhiteSpace(row.ItemId)
                || string.IsNullOrWhiteSpace(row.Name) || !FiniteAtLeast(row.BasePercent, 0)
                || row.RegistrationIndex < -1 || row.ItemCategory < 0 || row.ItemTier < 0)
            || profile.OrdinaryRowCount != profile.Rows.Count(row => !row.Special))
            throw new ArgumentException("掉落登记数、基础阈值或物品资料不一致。");
        var indices = profile.Rows.Where(row => row.RegistrationIndex >= 0).Select(row => row.RegistrationIndex).ToList();
        if (indices.Distinct().Count() != indices.Count)
            throw new ArgumentException("掉落配置包含重复的登记行编号。");
        foreach (var rows in profile.Rows.GroupBy(row => row.ItemId, StringComparer.Ordinal))
        {
            if (rows.Where(row => row.ItemCategory.HasValue).Select(row => row.ItemCategory).Distinct().Count() > 1
                || rows.Where(row => row.ItemTier.HasValue).Select(row => row.ItemTier).Distinct().Count() > 1
                || rows.Where(row => row.IsEquipment.HasValue).Select(row => row.IsEquipment).Distinct().Count() > 1)
                throw new ArgumentException("同一掉落物品的类型资料存在冲突。");
        }
    }

    public static DropProbabilityResult Calculate(DropCalculatorProfile profile, DropCalculationInput input)
    {
        ValidateProfile(profile);
        ArgumentNullException.ThrowIfNull(input);
        ValidateInput(profile, input);
        var notes = new List<string>
        {
            "这是固定名单、菜单及掉率节点状态下的数学估计：假定各次随机抽样独立且在0～100连续均匀分布，尚无游戏实测。",
            "统计的是抽签后的结果条目，不代表已经领取、入库或成功保存；兑换产物和固定保底不计入登记条目。"
        };
        var shared = profile.Mode == 3;
        var populationCount = profile.PopulationFromParticipants ? input.Roster.Count : input.PopulationCount ?? input.Roster.Count;
        if (profile.PopulationFromParticipants && input.PopulationCount is { } specified && specified != input.Roster.Count)
            notes.Add("该首领人数节点读取累计参战名单，另填的全图人数不参与当前计算。");
        if (!profile.PopulationFromParticipants && input.PopulationCount is null)
            notes.Add("当前将参战人数作为全图100级人数的条件输入；实际全图人数与累计参战名单可能不同。");
        var steps = populationCount < profile.PopulationLowerCount ? 0
            : Math.Clamp(populationCount, profile.PopulationLowerCount, profile.PopulationUpperCount) - profile.PopulationLowerCount + 1;
        var automaticBonus = steps * profile.DropPerStepPercent;
        var populationBonus = input.PopulationBonusOverridePercent ?? automaticBonus;
        if (input.PopulationBonusOverridePercent is not null)
            notes.Add("人数乘区由手动输入替换；少人及放手补偿仍使用地图基准与累计参战名单。");
        // Type-3 group-0 nodes multiply before iu96i clamps the aggregate to a multiplier in [0,7.5].
        var factors = new List<double> { 1 + populationBonus / 100 };
        factors.AddRange(input.ExtraDropBonusesPercent.Select(bonus => 1 + bonus / 100));
        if (input.Mode == "hard") factors.Add(1 + profile.HardDropBonusPercent / 100);
        if (input.Mode == "practice") factors.Add(0);
        var dropMultiplier = BoundedProduct(factors);
        var counted = CountedItems(profile, input.Scope);
        var rowDetails = new List<DropRowProbability>();
        var distributions = new List<DropCountDistribution>();
        var compensationFactors = new List<double>();
        if (shared)
        {
            notes.Add("共用模式只抽一次，个人与全队显示同一份结果；不读取个人徽章、许愿或放手，领取先后不能按人数均分。");
            var rows = CalculateRows(profile, input, -1, 1, dropMultiplier, counted, notes);
            rowDetails.AddRange(rows);
            var distribution = ResultSetDistribution(rows, counted);
            distributions.AddRange(input.Roster.Select(_ => distribution));
            compensationFactors.AddRange(input.Roster.Select(_ => 1.0));
            return Result(distribution, distribution);
        }
        notes.Add("个人模式每位登记成员各抽一份；同物品在同一份结果内去重，全队不同人的同物品仍分别计数。普通登记数不是每人件数上限。" );
        for (var player = 0; player < input.Roster.Count; player++)
        {
            var compensation = Compensation(profile, input.Roster, player);
            compensationFactors.Add(compensation);
            var rows = CalculateRows(profile, input, player, compensation, dropMultiplier, counted, notes);
            rowDetails.AddRange(rows);
            distributions.Add(ResultSetDistribution(rows, counted));
        }
        var team = new DropCountDistribution();
        foreach (var distribution in distributions) team = Convolve(team, distribution);
        return Result(distributions[input.ReceiverIndex], team);

        DropProbabilityResult Result(DropCountDistribution receiver, DropCountDistribution team) => new()
        {
            Shared = shared, ReceiverDistribution = receiver, TeamDistribution = team,
            ParticipantDistributions = distributions, ParticipantCompensationFactors = compensationFactors,
            RowDetails = rowDetails, PopulationCount = populationCount, PopulationSteps = steps,
            AutomaticPopulationBonusPercent = automaticBonus, PopulationBonusPercent = populationBonus,
            DropContainerMultiplier = dropMultiplier, BoundaryNotes = notes.Distinct(StringComparer.Ordinal).ToList()
        };
    }

    private static void ValidateInput(DropCalculatorProfile profile, DropCalculationInput input)
    {
        if (input.Roster is null || input.Roster.Count is < 1 or > 10 || input.Roster.Any(player => player is null)
            || input.ReceiverIndex < 0 || input.ReceiverIndex >= input.Roster.Count || input.PopulationCount is < 0 or > 10)
            throw new ArgumentException("参战名单需有1～10人，且当前查看的成员及全图人数必须有效。");
        if (input.Scope is not ("ordinary" or "allRegistered" or "equipment"))
            throw new ArgumentException("请选择可许愿普通掉落、全部登记掉落或已确认装备。");
        if (input.Mode is not ("normal" or "hard" or "practice")
            || input.Mode == "hard" && !profile.AllowsHard || input.Mode == "practice" && !profile.AllowsPractice)
            throw new ArgumentException("该首领当前配置没有所选难度模式。");
        if (input.PopulationBonusOverridePercent is { } bonus && !FiniteAtLeast(bonus, -100)
            || input.ExtraDropBonusesPercent is null || input.ExtraDropBonusesPercent.Any(value => !FiniteAtLeast(value, -100)))
            throw new ArgumentException("掉率乘区必须为有限数值，且不能低于-100%。");
        for (var player = 0; player < input.Roster.Count; player++)
        {
            var member = input.Roster[player];
            if (member.ForfeitTarget is < -1 or > 10 || member.ForfeitTarget == player)
                throw new ArgumentException("放手只能取消、给所有人或给其他成员。");
            if (member.BadgeId is not (null or "" or "I0MD" or "I02T" or "I03X"))
                throw new ArgumentException("请选择已有资料确认的已启用徽章。");
            if (profile.Mode != 3 && !string.IsNullOrEmpty(member.WishItemId)
                && (!profile.AllowsWish || !profile.Rows.Any(row => !row.Special && row.ItemId == member.WishItemId)))
                throw new ArgumentException("愿望只能选择该首领允许许愿的普通登记物品。");
        }
    }

    private static HashSet<string> CountedItems(DropCalculatorProfile profile, string scope)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in profile.Rows.GroupBy(row => row.ItemId, StringComparer.Ordinal))
        {
            if (scope == "equipment" && !group.Any(row => row.IsEquipment.HasValue))
                throw new InvalidOperationException("部分登记物品的装备分类尚未确认，无法完整计算装备件数；可改用普通或全部登记掉落。");
            if (scope == "allRegistered" || scope == "ordinary" && group.Any(row => !row.Special)
                || scope == "equipment" && group.Any(row => row.IsEquipment == true)) result.Add(group.Key);
        }
        return result;
    }

    private static double Compensation(DropCalculatorProfile profile, IReadOnlyList<DropParticipantInput> roster, int receiver)
    {
        if (!profile.AllowsWish || profile.Group is not (4 or 5) || roster[receiver].ForfeitTarget != -1) return 1;
        var missing = Math.Max(profile.PopulationLowerCount - 1 - roster.Count, 0);
        var directed = roster.Count(member => member.ForfeitTarget == receiver);
        var everyone = roster.Count(member => member.ForfeitTarget == 10);
        var unForfeited = roster.Count(member => member.ForfeitTarget == -1);
        return Math.Clamp(1 + .8 * directed + .8 * (missing + everyone) / Math.Max(unForfeited, 1), 1, 3);
    }

    private static List<DropRowProbability> CalculateRows(DropCalculatorProfile profile, DropCalculationInput input,
        int playerIndex, double compensation, double dropMultiplier, HashSet<string> counted, List<string> notes)
    {
        var shared = playerIndex < 0;
        var member = shared ? new DropParticipantInput() : input.Roster[playerIndex];
        var wish = profile.AllowsWish ? member.WishItemId : null;
        var forfeited = profile.AllowsWish && profile.Group is 4 or 5 && member.ForfeitTarget != -1;
        var result = new List<DropRowProbability>();
        for (var index = 0; index < profile.Rows.Count; index++)
        {
            var row = profile.Rows[index];
            var eligible = row.Special || !forfeited && (string.IsNullOrEmpty(wish) || wish == row.ItemId);
            var badgeMultiplier = 1.0;
            var resolved = true;
            if (eligible && !shared && !string.IsNullOrEmpty(member.BadgeId))
            {
                var badge = BadgeMultiplier(profile, row, member.BadgeId);
                if (badge is null)
                {
                    if (counted.Contains(row.ItemId))
                        throw new InvalidOperationException($"{row.Name}的徽章资格所需类别或阶级尚未确认，无法给出完整概率；请取消徽章或更换统计范围。");
                    resolved = false;
                    notes.Add("未纳入统计的部分物品缺少徽章资格资料，其条目概率保留未确认，不影响所选范围的结果。" );
                }
                else badgeMultiplier = badge.Value;
            }
            var threshold = 0.0;
            if (eligible && resolved)
            {
                threshold = row.BasePercent * dropMultiplier;
                if (profile.Group is 4 or 5)
                {
                    if (profile.AllowsWish && !row.Special)
                    {
                        threshold *= compensation;
                        if (string.IsNullOrEmpty(wish)) threshold *= 1.15 / Math.Max(profile.OrdinaryRowCount, 1);
                    }
                }
                else if (!string.IsNullOrEmpty(wish) && wish == row.ItemId) threshold *= 2.5;
                threshold *= badgeMultiplier;
                if (!double.IsFinite(threshold)) throw new ArgumentException("掉落阈值超出可计算范围，请减小手动输入。");
                if (threshold >= 100)
                    notes.Add("概率已按连续均匀抽样模型限制在0～100%；源码最终阈值不显式夹限，随机端点行为未实测。" );
            }
            result.Add(new DropRowProbability
            {
                PlayerIndex = playerIndex, RegistrationIndex = row.RegistrationIndex >= 0 ? row.RegistrationIndex : index,
                ItemId = row.ItemId, Name = row.Name, Special = row.Special, Eligible = eligible,
                Counted = counted.Contains(row.ItemId), Resolved = resolved, ThresholdPercent = threshold,
                Probability = Math.Clamp(threshold / 100, 0, 1), BadgeMultiplier = badgeMultiplier
            });
        }
        return result;
    }

    private static double? BadgeMultiplier(DropCalculatorProfile profile, DropCalculatorRow row, string badgeId)
    {
        if (row.ItemCategory is null) return null;
        if (row.ItemCategory is not (>= 1 and <= 5 or 7)) return 1;
        if (badgeId == "I03X" && profile.ImmortalBadgeException) return 1.5;
        if (row.ItemTier is null) return null;
        var upper = badgeId == "I0MD" ? 6 : 7;
        return row.ItemTier >= 4 && row.ItemTier <= upper ? 1.5 : 1;
    }

    private static DropCountDistribution ResultSetDistribution(IReadOnlyList<DropRowProbability> rows, HashSet<string> counted)
    {
        var distribution = new DropCountDistribution();
        foreach (var group in rows.GroupBy(row => row.ItemId, StringComparer.Ordinal).Where(group => counted.Contains(group.Key)))
        {
            // Separate registration attempts may target the same rawcode, but O6Io retains it only once.
            var occurrence = 0.0;
            foreach (var row in group) occurrence += (1 - occurrence) * row.Probability;
            if (occurrence <= 0) continue;
            distribution = Convolve(distribution, new DropCountDistribution { Probabilities = [1 - occurrence, occurrence] });
        }
        return distribution;
    }

    private static DropCountDistribution Convolve(DropCountDistribution first, DropCountDistribution second)
    {
        var values = new double[first.Probabilities.Count + second.Probabilities.Count - 1];
        for (var a = 0; a < first.Probabilities.Count; a++)
            for (var b = 0; b < second.Probabilities.Count; b++) values[a + b] += first.Probabilities[a] * second.Probabilities[b];
        return new() { Probabilities = values.ToList() };
    }

    private static bool FiniteAtLeast(double value, double lower) => double.IsFinite(value) && value >= lower;

    private static double BoundedProduct(IReadOnlyList<double> factors)
    {
        // Check zero first: practice remains exactly zero even if another finite factor is huge.
        if (factors.Any(factor => factor == 0)) return 0;
        var logarithm = factors.Sum(Math.Log);
        return logarithm >= Math.Log(7.5) ? 7.5 : Math.Exp(logarithm);
    }
}
