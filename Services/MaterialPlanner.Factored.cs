using System.Text;

namespace Grimmcair.Desktop.Services;

public sealed partial class MaterialPlanner
{
    // Keep independent choices as a product of small tables. Join tables only
    // where a held item or a reusable unlock actually couples their demands.
    // All numerical inventory subtraction happens here, on the desktop.
    private sealed class Factor
    {
        public SortedSet<string> Goals { get; } = new(StringComparer.Ordinal);
        public List<Scenario> States { get; set; } = [];
    }

    private static long DemandQuantity(Scenario state, string id, bool reusable)
    {
        if (!state.Demand.TryGetValue(id, out var slices)) return 0;
        return reusable ? 1 : slices.Values.Aggregate(0L, (sum, slice) => Add(sum, slice.Quantity));
    }

    private static MaterialPlanRow SumRows(IEnumerable<MaterialPlanRow> source)
    {
        var rows = source.ToList(); var sample = rows[0];
        long Total(Func<MaterialPlanRow, long> select) => sample.IsReusable ? rows.Max(select) : rows.Aggregate(0L, (sum, row) => Add(sum, select(row)));
        return new MaterialPlanRow
        {
            ItemId = sample.ItemId, Name = sample.Name, Icon = sample.Icon, Stage = sample.Stage, StageRank = sample.StageRank,
            Required = Total(x => x.Required), RequiredMaximum = Total(x => x.RequiredMaximum),
            Owned = rows.Max(x => x.Owned), Allocated = Total(x => x.Allocated), AllocatedMaximum = Total(x => x.AllocatedMaximum),
            Missing = Total(x => x.Missing), MissingMaximum = Total(x => x.MissingMaximum), IsReusable = sample.IsReusable,
            Status = rows.Any(x => x.Status == "数量随可选组合变化") ? "数量随可选组合变化" : sample.Status,
            RecipeId = rows.Select(x => x.RecipeId).Distinct(StringComparer.Ordinal).Count() == 1 ? sample.RecipeId : "",
            RestrictionsText = string.Join("；", rows.Select(x => x.RestrictionsText).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal)),
            GoalIds = rows.SelectMany(x => x.GoalIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            Paths = rows.SelectMany(x => x.Paths).Distinct(StringComparer.Ordinal).Take(128).ToArray()
        };
    }

    private static string StateKey(Scenario state)
    {
        var key = new StringBuilder();
        foreach (var (id, slices) in state.Demand.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var amount = slices.Values.Aggregate(0L, (sum, slice) => Add(sum, slice.Quantity));
            // Preserve direct-target priority when held intermediate equipment
            // is also requested as a finished target.
            var direct = slices.Values.Where(x => x.Goals.Length == 1 && x.Goals[0] == id)
                .Aggregate(0L, (sum, slice) => Add(sum, slice.Quantity));
            key.Append(id).Append(':').Append(amount).Append('/').Append(direct).Append(';');
        }
        void Rows(char kind, IEnumerable<MaterialPlanRow> rows)
        {
            key.Append(kind);
            foreach (var row in rows.GroupBy(x => x.ItemId, StringComparer.Ordinal).Select(SumRows).OrderBy(x => x.ItemId, StringComparer.Ordinal))
                key.Append(row.ItemId).Append(':').Append(row.Required).Append('/').Append(row.Allocated).Append('/').Append(row.Missing).Append(';');
        }
        Rows('S', state.Steps); Rows('M', state.Materials);
        foreach (var restriction in state.Restrictions.Order(StringComparer.Ordinal)) key.Append('R').Append(restriction.Length).Append(':').Append(restriction);
        return key.ToString();
    }

    private static void AddState(Dictionary<string, Scenario> states, Scenario state)
    {
        var key = StateKey(state);
        if (states.TryGetValue(key, out var existing))
        {
            // Equal numeric states may have different target provenance. Keep
            // the union without adding their quantities a second time.
            foreach (var (id, slices) in existing.Demand.ToList())
            {
                var other = state.Demand[id];
                var all = slices.Values.Concat(other.Values).ToList();
                var direct = slices.Values.Where(x => x.Goals.Length == 1 && x.Goals[0] == id)
                    .Aggregate(0L, (sum, slice) => Add(sum, slice.Quantity));
                var total = slices.Values.Aggregate(0L, (sum, slice) => Add(sum, slice.Quantity));
                existing.Demand.Remove(id);
                if (direct > 0) Need(existing, id, direct, [id], all.Where(x => x.Goals.Length == 1 && x.Goals[0] == id).SelectMany(x => x.Paths));
                var rest = all.Where(x => x.Goals.Length != 1 || x.Goals[0] != id).ToList();
                if (total > direct) Need(existing, id, total - direct, rest.SelectMany(x => x.Goals).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), rest.SelectMany(x => x.Paths));
            }
            foreach (var group in state.Steps.GroupBy(x => x.ItemId, StringComparer.Ordinal))
            {
                var previous = existing.Steps.Where(x => x.ItemId == group.Key).ToList();
                if (previous.Count == 0) continue;
                var row = SumRows(previous);
                existing.Steps.RemoveAll(x => x.ItemId == group.Key);
                existing.Steps.Add(WithProvenance(row, previous.Concat(group)));
            }
            foreach (var group in state.Materials.GroupBy(x => x.ItemId, StringComparer.Ordinal))
            {
                var previous = existing.Materials.Where(x => x.ItemId == group.Key).ToList();
                if (previous.Count == 0) continue;
                var row = SumRows(previous);
                existing.Materials.RemoveAll(x => x.ItemId == group.Key);
                existing.Materials.Add(WithProvenance(row, previous.Concat(group)));
            }
            return;
        }
        if (states.Count >= MaxScenarios) throw new ExpansionLimitException();
        states.Add(key, state);
    }

    private static MaterialPlanRow WithProvenance(MaterialPlanRow row, IEnumerable<MaterialPlanRow> provenance) => new()
    {
        ItemId = row.ItemId, Name = row.Name, Icon = row.Icon, Stage = row.Stage, StageRank = row.StageRank,
        Required = row.Required, RequiredMaximum = row.RequiredMaximum, Owned = row.Owned,
        Allocated = row.Allocated, AllocatedMaximum = row.AllocatedMaximum, Missing = row.Missing, MissingMaximum = row.MissingMaximum,
        IsReusable = row.IsReusable, Status = row.Status, RecipeId = row.RecipeId, RestrictionsText = row.RestrictionsText,
        GoalIds = provenance.SelectMany(x => x.GoalIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        Paths = provenance.SelectMany(x => x.Paths).Distinct(StringComparer.Ordinal).Take(128).ToArray()
    };

    private static Factor JoinFactors(IReadOnlyList<Factor> factors)
    {
        var joined = new Factor { States = [new Scenario()] };
        foreach (var factor in factors)
        {
            joined.Goals.UnionWith(factor.Goals);
            var next = new Dictionary<string, Scenario>(StringComparer.Ordinal);
            foreach (var left in joined.States)
                foreach (var right in factor.States)
                {
                    var state = left.Copy();
                    foreach (var (id, slices) in right.Demand)
                        foreach (var slice in slices.Values) Need(state, id, slice.Quantity, slice.Goals, slice.Paths);
                    state.Steps.AddRange(right.Steps); state.Materials.AddRange(right.Materials); state.Restrictions.UnionWith(right.Restrictions);
                    AddState(next, state);
                }
            joined.States = next.Values.ToList();
        }
        return joined;
    }

    private bool TryCalculateFactored(IReadOnlyList<MaterialGoal> requested, IReadOnlyDictionary<string, long> stock,
        IReadOnlySet<string> unlocked, IReadOnlyList<string> order, MaterialPlanResult result)
    {
        var factors = new List<Factor>();
        foreach (var goal in requested.GroupBy(x => Canonical(x.ItemId), StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var initial = new Scenario();
            Need(initial, goal.Key, goal.Aggregate(0L, (sum, value) => Add(sum, value.Quantity)), [goal.Key], [GetItem(goal.Key)?.Name ?? "未收录物品"]);
            var factor = new Factor { States = [initial] }; factor.Goals.Add(goal.Key); factors.Add(factor);
        }
        try
        {
            void Expand(Factor factor, string id, long owned, long quota, bool reusableReference)
            {
                var quantities = factor.States.Select(state => DemandQuantity(state, id, GetItem(id)?.IsReusable == true)).ToList();
                // A fixed sibling prerequisite is independent even if it is
                // inside the same goal. Extract its choice before multiplying
                // it by the other siblings' choices.
                if (!reusableReference && quantities.Min() > 0 && quantities.Min() == quantities.Max()
                    && (recipes.GetValueOrDefault(id)?.Count ?? 0) > 1
                    && (factor.States.Count > 1 || factor.States[0].Demand.Count > 1))
                {
                    var seed = new Scenario();
                    foreach (var slice in factor.States[0].Demand[id].Values) Need(seed, id, slice.Quantity, slice.Goals, slice.Paths);
                    var seeds = new Dictionary<string, Scenario>(StringComparer.Ordinal); AddState(seeds, seed);
                    foreach (var state in factor.States.Skip(1))
                    {
                        var equivalent = new Scenario();
                        foreach (var slice in state.Demand[id].Values) Need(equivalent, id, slice.Quantity, slice.Goals, slice.Paths);
                        AddState(seeds, equivalent);
                    }
                    // Direct-target and total amounts are preserved in the
                    // key; extraction is valid only for one numerical seed.
                    if (seeds.Count == 1)
                    {
                        var branch = new Factor { States = seeds.Values.ToList() }; branch.Goals.UnionWith(factor.Goals);
                        foreach (var state in factor.States) state.Demand.Remove(id);
                        factors.Add(branch); ExpandFactor(branch, id, owned, quota, false); return;
                    }
                }
                ExpandFactor(factor, id, owned, quota, reusableReference);
            }
            foreach (var id in order)
            {
                var reusable = GetItem(id)?.IsReusable == true;
                var relevant = factors.Where(x => x.States.Any(state => state.Demand.ContainsKey(id)))
                    .OrderByDescending(x => x.Goals.Contains(id)).ThenBy(x => string.Join("|", x.Goals), StringComparer.Ordinal).ToList();
                if (relevant.Count == 0) continue;
                var owned = reusable ? (unlocked.Contains(id) ? 1L : 0L) : stock.GetValueOrDefault(id);
                long Min(Factor factor) => factor.States.Min(state => DemandQuantity(state, id, reusable));
                long Max(Factor factor) => factor.States.Max(state => DemandQuantity(state, id, reusable));
                void Join(IReadOnlyList<Factor> selected)
                {
                    var combined = JoinFactors(selected);
                    foreach (var factor in selected) { factors.Remove(factor); relevant.Remove(factor); }
                    factors.Add(combined); relevant.Add(combined);
                    relevant = relevant.OrderByDescending(x => x.Goals.Contains(id)).ThenBy(x => string.Join("|", x.Goals), StringComparer.Ordinal).ToList();
                }

                if (reusable)
                {
                    // A missing reusable unlock is crafted once. If one factor
                    // always needs it, the other factors can reference it. If
                    // every use is conditional, join those conditions first.
                    if (owned == 0 && relevant.Count > 1 && relevant.All(x => Min(x) == 0)) Join(relevant.ToList());
                    var producer = owned > 0 ? null : relevant.FirstOrDefault(x => Min(x) > 0) ?? relevant[0];
                    foreach (var factor in relevant)
                        Expand(factor, id, stock.GetValueOrDefault(id), factor == producer ? 0 : 1, reusableReference: owned == 0 && factor != producer);
                    continue;
                }

                var minimum = relevant.Aggregate(0L, (sum, factor) => Add(sum, Min(factor)));
                var maximum = relevant.Aggregate(0L, (sum, factor) => Add(sum, Max(factor)));
                if (owned > minimum && owned < maximum && relevant.Count > 1)
                {
                    // Constants use their fixed stock first. Only the varying
                    // factors compete for the remaining amount.
                    var varying = relevant.Where(x => Min(x) != Max(x)).ToList();
                    if (varying.Count > 1) Join(varying);
                    minimum = relevant.Aggregate(0L, (sum, factor) => Add(sum, Min(factor)));
                    maximum = relevant.Aggregate(0L, (sum, factor) => Add(sum, Max(factor)));
                }
                var remainingStock = owned;
                if (owned >= maximum)
                {
                    foreach (var factor in relevant) Expand(factor, id, owned, Max(factor), false);
                }
                else if (owned <= minimum)
                {
                    foreach (var factor in relevant)
                    {
                        var quota = Math.Min(remainingStock, Min(factor)); remainingStock -= quota;
                        Expand(factor, id, owned, quota, false);
                    }
                }
                else
                {
                    var varying = relevant.Single(x => Min(x) != Max(x));
                    foreach (var factor in relevant.Where(x => x != varying))
                    {
                        var quota = Math.Min(remainingStock, Min(factor)); remainingStock -= quota;
                        Expand(factor, id, owned, quota, false);
                    }
                    Expand(varying, id, owned, remainingStock, false);
                }
            }
            AddFactoredResult(factors, result);
            result.IsExact = true; result.IsFactored = true;
            result.ScenarioCount = factors.Sum(x => x.States.Count);
            return true;
        }
        catch (ExpansionLimitException) { return false; }
    }

    private void ExpandFactor(Factor factor, string id, long owned, long quota, bool reusableReference)
    {
        var next = new Dictionary<string, Scenario>(StringComparer.Ordinal);
        foreach (var state in factor.States)
        {
            if (!state.Demand.Remove(id, out var byGoal)) { AddState(next, state); continue; }
            var item = GetItem(id) ?? new PlannerItem { Id = id, Name = "未收录物品" };
            var slices = byGoal.Values.OrderBy(x => x.Goals.Length == 1 && x.Goals[0] == id ? 0 : 1)
                .ThenBy(x => string.Join("|", x.Goals), StringComparer.Ordinal).ToList();
            var targetIds = slices.SelectMany(x => x.Goals).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var paths = slices.SelectMany(x => x.Paths).Distinct(StringComparer.Ordinal).Take(128).ToArray();
            var required = item.IsReusable ? 1 : slices.Aggregate(0L, (sum, slice) => Add(sum, slice.Quantity));
            var allocated = Math.Min(required, quota); var missing = required - allocated;
            var options = recipes.GetValueOrDefault(id) ?? [];
            var row = new MaterialPlanRow
            {
                ItemId = id, Name = item.Name, Icon = item.Icon, Stage = item.Stage, StageRank = item.StageRank,
                Required = reusableReference ? 0 : required, RequiredMaximum = reusableReference ? 0 : required,
                Owned = item.IsReusable ? (reusableReference ? 0 : quota) : owned,
                Allocated = reusableReference ? 0 : allocated, AllocatedMaximum = reusableReference ? 0 : allocated,
                Missing = missing, MissingMaximum = missing, IsReusable = item.IsReusable, GoalIds = targetIds, Paths = paths,
                RecipeId = options.Count == 1 ? options[0].Id : "",
                RestrictionsText = string.Join("；", options.Select(x => x.RestrictionsText).Append(item.RestrictionsText).Where(x => x.Length > 0).Distinct()),
                Status = reusableReference ? "可复用前置已在另一目标中计算一次" : missing == 0 ? "当前档物品已满足" :
                    options.Count == 0 ? "未收录前置" : options.Count > 1 ? "保留可选组合" : "已展开前置"
            };
            state.Steps.Add(row);
            if (options.Count == 0 && !reusableReference) state.Materials.Add(row);
            if (missing == 0 || options.Count == 0) { AddState(next, state); continue; }
            var unmet = new List<DemandSlice>();
            if (item.IsReusable)
            {
                var slice = new DemandSlice { Goals = targetIds, Quantity = 1 }; slice.Paths.UnionWith(paths); unmet.Add(slice);
            }
            else
            {
                var available = allocated;
                foreach (var slice in slices)
                {
                    var used = Math.Min(available, slice.Quantity); available -= used;
                    if (slice.Quantity > used) { var rest = slice.Copy(); rest.Quantity -= used; unmet.Add(rest); }
                }
            }
            void Expand(Scenario target, long[] counts)
            {
                var sliceIndex = 0; long sliceUsed = 0;
                for (var optionIndex = 0; optionIndex < counts.Length; optionIndex++)
                {
                    var quantity = counts[optionIndex];
                    if (quantity > 0 && options[optionIndex].RestrictionsText.Length > 0) target.Restrictions.Add(options[optionIndex].RestrictionsText);
                    while (quantity > 0)
                    {
                        var slice = unmet[sliceIndex]; var take = Math.Min(quantity, slice.Quantity - sliceUsed);
                        foreach (var ingredient in options[optionIndex].Ingredients)
                            Need(target, Canonical(ingredient.ItemId), ingredient.IsReusable ? 1 : Multiply(take, ingredient.Quantity),
                                slice.Goals, slice.Paths.Select(path => path + " → " + (GetItem(ingredient.ItemId)?.Name ?? ingredient.Name)));
                        quantity -= take; sliceUsed += take;
                        if (sliceUsed == slice.Quantity) { sliceIndex++; sliceUsed = 0; }
                    }
                }
            }
            if (options.Count == 1) { Expand(state, [missing]); AddState(next, state); }
            else
            {
                var counts = new long[options.Count];
                void Distribute(int index, long remaining)
                {
                    if (index == counts.Length - 1)
                    {
                        counts[index] = remaining; var branch = state.Copy(); Expand(branch, counts); AddState(next, branch); return;
                    }
                    for (long n = 0; n <= remaining; n++) { counts[index] = n; Distribute(index + 1, remaining - n); }
                }
                Distribute(0, missing);
            }
        }
        factor.States = next.Values.ToList();
    }

    private void AddFactoredResult(IReadOnlyList<Factor> factors, MaterialPlanResult result)
    {
        List<MaterialPlanRow> Merge(Factor factor, Func<Scenario, List<MaterialPlanRow>> select)
        {
            var states = factor.States.Select(state => select(state).GroupBy(x => x.ItemId, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, SumRows, StringComparer.Ordinal)).ToList();
            return states.SelectMany(x => x.Keys).Distinct(StringComparer.Ordinal).Select(id =>
            {
                var rows = states.Select(x => x.GetValueOrDefault(id)).ToList(); var sample = rows.First(x => x is not null)!;
                var minimum = rows.Min(x => x?.Required ?? 0); var maximum = rows.Max(x => x?.Required ?? 0);
                return new MaterialPlanRow
                {
                    ItemId = id, Name = sample.Name, Icon = sample.Icon, Stage = sample.Stage, StageRank = sample.StageRank,
                    Required = minimum, RequiredMaximum = maximum, Owned = rows.Max(x => x?.Owned ?? 0),
                    Allocated = rows.Min(x => x?.Allocated ?? 0), AllocatedMaximum = rows.Max(x => x?.Allocated ?? 0),
                    Missing = rows.Min(x => x?.Missing ?? 0), MissingMaximum = rows.Max(x => x?.Missing ?? 0), IsReusable = sample.IsReusable,
                    Status = minimum != maximum ? "数量随可选组合变化" : sample.Status, RecipeId = sample.RecipeId,
                    RestrictionsText = sample.RestrictionsText,
                    GoalIds = rows.Where(x => x is not null).SelectMany(x => x!.GoalIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    Paths = rows.Where(x => x is not null).SelectMany(x => x!.Paths).Distinct(StringComparer.Ordinal).Take(128).ToArray()
                };
            }).ToList();
        }
        var componentMaterials = factors.Select(factor => Merge(factor, x => x.Materials)).ToList();
        result.Steps.AddRange(factors.SelectMany(factor => Merge(factor, x => x.Steps)).GroupBy(x => x.ItemId, StringComparer.Ordinal)
            .Select(SumRows).OrderByDescending(x => x.StageRank).ThenBy(x => x.Name, StringComparer.Ordinal));
        result.Materials.AddRange(componentMaterials.SelectMany(x => x).GroupBy(x => x.ItemId, StringComparer.Ordinal)
            .Select(SumRows).OrderByDescending(x => x.StageRank).ThenBy(x => x.Name, StringComparer.Ordinal));
        result.Allocations.AddRange(result.Steps.Where(x => x.AllocatedMaximum > 0));
        MaterialNeed NeedRow(MaterialPlanRow row, long quantity) => new()
        { ItemId = row.ItemId, Name = row.Name, Stage = row.Stage, Quantity = quantity, IsReusable = row.IsReusable, GoalIds = row.GoalIds };
        foreach (var row in result.Materials)
        {
            if (row.Missing > 0) result.OrdinaryNeeds.Add(NeedRow(row, row.Missing));
            result.QuantityRanges.Add(new(row.ItemId, row.Name, row.Stage, row.Missing, row.MissingMaximum, row.IsReusable));
            if (row.MissingMaximum > 0) result.Issues.Add(new("unrecorded_prerequisite", row.Name + "：未收录更早前置，获取方式仍待补充。", row.ItemId));
        }
        for (var index = 0; index < factors.Count; index++)
        {
            var factor = factors[index];
            var common = componentMaterials[index].ToDictionary(x => x.ItemId, x => x.Missing, StringComparer.Ordinal);
            var combinations = new SortedDictionary<string, MaterialPlanOption>(StringComparer.Ordinal);
            foreach (var state in factor.States)
            {
                var rest = state.Materials.GroupBy(x => x.ItemId, StringComparer.Ordinal).Select(SumRows)
                    .Where(x => x.Missing > common.GetValueOrDefault(x.ItemId)).OrderBy(x => x.ItemId, StringComparer.Ordinal)
                    .Select(x => NeedRow(WithProvenance(x, componentMaterials[index].Where(row => row.ItemId == x.ItemId)), x.Missing - common.GetValueOrDefault(x.ItemId))).ToList();
                var restrictions = state.Restrictions.Order(StringComparer.Ordinal).ToList();
                var key = string.Join("|", rest.Select(x => x.ItemId + ":" + x.Quantity)) + "#" + string.Join("|", restrictions);
                combinations.TryAdd(key, new() { Needs = rest, Restrictions = restrictions });
            }
            if (combinations.Count > 1)
            {
                var title = string.Join("、", factor.Goals.Select(id => GetItem(id)?.Name ?? "目标装备"));
                // The API caps titles at 500 characters, regardless of a long
                // list of correlated targets.
                if (title.Length > 400) title = title[..400] + "等目标";
                result.OrGroups.Add(new() { Id = "combination-" + (result.OrGroups.Count + 1),
                    Title = title + "：任选一个完整方案（另加共同材料）",
                    Options = combinations.Values.Select((option, i) => new MaterialPlanOption
                    { Id = "option-" + (i + 1), Label = "方案 " + (i + 1), Needs = option.Needs, Restrictions = option.Restrictions }).ToList() });
            }
        }
    }
}
