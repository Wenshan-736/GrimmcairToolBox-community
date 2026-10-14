using System.IO;
using System.Text.Json;

namespace Grimmcair.Desktop.Services;

public sealed record MaterialGoal(string ItemId, long Quantity = 1);

public sealed class PlannerItem
{
    public string Id { get; init; } = "";
    public string ItemId => Id;
    public string Name { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Stage { get; init; } = "";
    public int StageRank { get; init; }
    public bool IsReusable { get; init; }
    public bool IsCosmetic { get; init; }
    public string RestrictionsText { get; init; } = "";
}

public sealed class PlannerIngredient
{
    public string ItemId { get; init; } = "";
    public string Name { get; init; } = "";
    public long Quantity { get; init; }
    public bool IsReusable { get; init; }
}

public sealed class PlannerRecipe
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string OutputId { get; init; } = "";
    public long OutputQuantity { get; init; } = 1;
    public List<PlannerIngredient> Ingredients { get; init; } = [];
    public string IngredientsSummary { get; init; } = "";
    public string RestrictionsText { get; init; } = "";
}

public sealed class MaterialPlanRow
{
    public string ItemId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Stage { get; init; } = "";
    public int StageRank { get; init; }
    public long Required { get; init; }
    public long Owned { get; init; }
    public long Allocated { get; init; }
    public long Missing { get; init; }
    public long RequiredMaximum { get; init; }
    public long AllocatedMaximum { get; init; }
    public long MissingMaximum { get; init; }
    public bool IsReusable { get; init; }
    public string Status { get; init; } = "";
    public string RecipeId { get; init; } = "";
    public string RestrictionsText { get; init; } = "";
    public IReadOnlyList<string> GoalIds { get; init; } = [];
    public IReadOnlyList<string> Targets => GoalIds;
    public IReadOnlyList<string> Paths { get; init; } = [];
}

public sealed class MaterialPlanBranch
{
    public string ItemId { get; init; } = "";
    public string Name { get; init; } = "";
    public long Required { get; init; }
    public IReadOnlyList<PlannerRecipe> Options { get; init; } = [];
    public IReadOnlyList<string> GoalIds { get; init; } = [];
    public IReadOnlyList<string> Paths { get; init; } = [];
}

public sealed record MaterialPlanIssue(string Code, string Message, string ItemId = "");

public sealed class MaterialNeed
{
    public string ItemId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Stage { get; init; } = "";
    public long Quantity { get; init; }
    public bool IsReusable { get; init; }
    public string AcquisitionStatus { get; init; } = "unrecorded_prerequisite";
    public IReadOnlyList<string> GoalIds { get; init; } = [];
}
public sealed class MaterialPlanOption
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public List<MaterialNeed> Needs { get; init; } = [];
    public List<string> Restrictions { get; init; } = [];
}
public sealed class MaterialPlanGroup
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public int Choose { get; init; } = 1;
    public List<MaterialPlanOption> Options { get; init; } = [];
}
public sealed record MaterialQuantityRange(string ItemId, string Name, string Stage, long Minimum, long Maximum, bool IsReusable);

public sealed class MaterialPlanResult
{
    public List<MaterialPlanRow> Materials { get; } = [];
    public List<MaterialPlanRow> Allocations { get; } = [];
    public List<MaterialPlanRow> Steps { get; } = [];
    public List<MaterialPlanBranch> Branches { get; } = [];
    public List<MaterialPlanIssue> Issues { get; } = [];
    public List<MaterialNeed> OrdinaryNeeds { get; } = [];
    public List<MaterialPlanGroup> OrGroups { get; } = [];
    public List<MaterialQuantityRange> QuantityRanges { get; } = [];
    public List<PlannerRecipe> UnexpandedRecipes { get; } = [];
    public bool IsExact { get; internal set; }
    public bool IsFactored { get; internal set; }
    public int ScenarioCount { get; internal set; }
    public bool IsComplete => IsExact && Issues.Count == 0;
    public string Notes { get; init; } = "仅按当前存档物品与衣柜计算；多目标共同抵扣一次。普通需求加上每组选一个完整组合，不能把组选项相加。各材料区间不能分别挑最小值拼成新组合。未收录前置不等于确定掉落；标准配方仅静态核对，结果不是游戏内可合成判定。";
}

/// <summary>Finite, local-only material expansion for the pinned map's standard recipes.</summary>
public sealed partial class MaterialPlanner
{
    private const int MaxGoals = 128;
    private const int MaxNodes = 4096;
    private const int MaxDepth = 64;
    private const long MaxQuantity = 1_000_000_000;
    private readonly Dictionary<string, PlannerItem> items;
    private readonly Dictionary<string, List<PlannerRecipe>> recipes;
    private readonly Dictionary<string, string> aliases;
    public IReadOnlyDictionary<string, PlannerItem> Items => items;
    public IReadOnlyDictionary<string, List<PlannerRecipe>> Recipes => recipes;
    public string MapVersion { get; }
    public string MapSha256 { get; }
    public string RecipeSetSha256 { get; private set; } = "";

    private sealed class Document
    {
        public string MapVersion { get; init; } = "";
        public string MapSha256 { get; init; } = "";
        public List<PlannerItem> Items { get; init; } = [];
        public List<PlannerRecipe> Recipes { get; init; } = [];
        public Dictionary<string, string> Aliases { get; init; } = [];
    }

    private MaterialPlanner(Document document)
    {
        MapVersion = document.MapVersion;
        MapSha256 = document.MapSha256;
        items = document.Items.ToDictionary(x => x.Id, StringComparer.Ordinal);
        recipes = document.Recipes.GroupBy(x => x.OutputId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Id, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        aliases = document.Aliases;
    }

    public static MaterialPlanner Load()
    {
        using var catalog = JsonDocument.Parse(ResourcePack.ReadText("catalog.json"));
        return FromJson(ResourcePack.ReadText("planner.json"),
            catalog.RootElement.GetProperty("mapVersion").GetString() ?? "",
            catalog.RootElement.GetProperty("sourceSha256").GetString() ?? "");
    }

    public static MaterialPlanner FromJson(string json, string expectedMapVersion, string expectedMapSha256)
    {
        var document = JsonSerializer.Deserialize<Document>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("材料分析资料为空。");
        if (string.IsNullOrWhiteSpace(document.MapSha256) || document.MapSha256 != expectedMapSha256 || document.MapVersion != expectedMapVersion)
            throw new InvalidDataException("材料分析资料的地图版本不匹配。");
        if (document.Items.Count > MaxNodes || document.Items.Any(x => string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Name)) ||
            document.Items.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != document.Items.Count ||
            document.Recipes.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != document.Recipes.Count)
            throw new InvalidDataException("材料分析资料包含重复或无效条目。");
        var itemIndex = document.Items.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var recipe in document.Recipes)
        {
            if (!itemIndex.ContainsKey(recipe.OutputId) || recipe.OutputQuantity != 1 || recipe.Ingredients.Count == 0 ||
                recipe.Ingredients.Any(x => !itemIndex.ContainsKey(x.ItemId) || x.Quantity is <= 0 or > MaxQuantity || x.IsReusable != itemIndex[x.ItemId].IsReusable) ||
                recipe.Ingredients.Select(x => x.ItemId).Distinct(StringComparer.Ordinal).Count() != recipe.Ingredients.Count)
                throw new InvalidDataException("标准配方包含不受支持的产量、材料或复用规则。");
        }
        if (document.Aliases.Any(x => !itemIndex.ContainsKey(x.Value) || document.Aliases.ContainsKey(x.Value)))
            throw new InvalidDataException("材料分析资料包含无效的物品别名。");
        return new MaterialPlanner(document) { RecipeSetSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant() };
    }

    public PlannerItem? GetItem(string itemId) => items.GetValueOrDefault(Canonical(itemId));
    public string Canonical(string itemId) => aliases.GetValueOrDefault(itemId, itemId);

    private const int MaxScenarios = 256;
    private sealed class ExpansionLimitException : Exception { }
    private sealed class DemandSlice
    {
        public required string[] Goals { get; init; }
        public long Quantity { get; set; }
        public HashSet<string> Paths { get; } = new(StringComparer.Ordinal);
        public DemandSlice Copy()
        {
            var copy = new DemandSlice { Goals = Goals, Quantity = Quantity };
            copy.Paths.UnionWith(Paths); return copy;
        }
    }
    private sealed class Scenario
    {
        public Dictionary<string, Dictionary<string, DemandSlice>> Demand { get; } = new(StringComparer.Ordinal);
        public List<MaterialPlanRow> Steps { get; } = [];
        public List<MaterialPlanRow> Materials { get; } = [];
        public HashSet<string> Restrictions { get; } = new(StringComparer.Ordinal);
        public Scenario Copy()
        {
            var copy = new Scenario();
            foreach (var (id, slices) in Demand)
                copy.Demand[id] = slices.ToDictionary(x => x.Key, x => x.Value.Copy(), StringComparer.Ordinal);
            copy.Steps.AddRange(Steps); copy.Materials.AddRange(Materials); copy.Restrictions.UnionWith(Restrictions); return copy;
        }
    }

    private static long Add(long a, long b)
    {
        var n = checked(a + b); if (n > MaxQuantity) throw new OverflowException(); return n;
    }
    private static long Multiply(long a, long b)
    {
        var n = checked(a * b); if (n > MaxQuantity) throw new OverflowException(); return n;
    }
    private static void Need(Scenario state, string id, long quantity, string[] targets, IEnumerable<string> paths)
    {
        if (quantity == 0) return;
        if (!state.Demand.TryGetValue(id, out var slices)) state.Demand[id] = slices = new(StringComparer.Ordinal);
        var key = string.Join("|", targets.Order(StringComparer.Ordinal));
        if (!slices.TryGetValue(key, out var slice)) slices[key] = slice = new() { Goals = targets };
        slice.Quantity = Add(slice.Quantity, quantity);
        foreach (var path in paths) if (slice.Paths.Count < 128) slice.Paths.Add(path);
    }

    // The compatibility parameter is deliberately ignored: older saved manual
    // choices may not silently narrow the user's now-unconditional OR report.
    public MaterialPlanResult Calculate(IEnumerable<MaterialGoal> goals, IReadOnlyDictionary<string, long> inventory,
        IReadOnlySet<string> wardrobe, IReadOnlyDictionary<string, string>? choices = null)
    {
        var result = new MaterialPlanResult();
        try { CalculateCore(goals, inventory, wardrobe, result); }
        catch (OverflowException)
        {
            ClearCalculated(result);
            result.Issues.Add(new("quantity_limit", "材料数量超出安全计算范围，未输出部分总单。"));
        }
        return result;
    }

    private static void ClearCalculated(MaterialPlanResult result)
    {
        result.Materials.Clear(); result.Allocations.Clear(); result.Steps.Clear(); result.Branches.Clear();
        result.OrdinaryNeeds.Clear(); result.OrGroups.Clear(); result.QuantityRanges.Clear();
        result.IsExact = false; result.ScenarioCount = 0;
    }

    private void CalculateCore(IEnumerable<MaterialGoal> goals, IReadOnlyDictionary<string, long> inventory,
        IReadOnlySet<string> wardrobe, MaterialPlanResult result)
    {
        var goalList = goals.Take(MaxGoals + 1).OrderBy(x => Canonical(x.ItemId), StringComparer.Ordinal).ToList();
        if (goalList.Count > MaxGoals || goalList.Any(x => x.Quantity is <= 0 or > MaxQuantity))
        { result.Issues.Add(new("invalid_goals", "目标数量无效或目标条目超过计算上限。")); return; }
        var stock = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (key, amount) in inventory)
        {
            if (amount < 0 || amount > MaxQuantity) { result.Issues.Add(new("invalid_inventory", "当前档物品数量无效。")); return; }
            var id = Canonical(key); stock[id] = Add(stock.GetValueOrDefault(id), amount);
        }
        var unlocked = wardrobe.Select(Canonical).ToHashSet(StringComparer.Ordinal);
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string id, int depth)
        {
            if (visiting.Contains(id)) { result.Issues.Add(new("cycle", "合成关系存在循环，未输出确定总单。", id)); return false; }
            if (edges.ContainsKey(id)) return true;
            if (depth > MaxDepth || edges.Count >= MaxNodes) { result.Issues.Add(new("depth_limit", "合成关系超过展开上限，未输出确定总单。", id)); return false; }
            edges[id] = new(StringComparer.Ordinal); visiting.Add(id);
            // The union graph orders every potential consumer before its shared
            // prerequisite, even when that consumer belongs to another route.
            foreach (var option in recipes.GetValueOrDefault(id) ?? [])
                foreach (var ingredient in option.Ingredients)
                {
                    var child = Canonical(ingredient.ItemId); edges[id].Add(child);
                    if (!Visit(child, depth + 1)) return false;
                }
            visiting.Remove(id); return true;
        }
        void KeepUnexpanded()
        {
            result.UnexpandedRecipes.Clear();
            result.UnexpandedRecipes.AddRange(edges.Keys.SelectMany(id => recipes.GetValueOrDefault(id) ?? []).OrderBy(x => x.Id, StringComparer.Ordinal));
            foreach (var (id, options) in recipes.Where(x => edges.ContainsKey(x.Key) && x.Value.Count > 1))
                result.Branches.Add(new() { ItemId = id, Name = GetItem(id)?.Name ?? "合成物品", Options = options });
        }
        foreach (var goal in goalList)
            if (!Visit(Canonical(goal.ItemId), 0)) { KeepUnexpanded(); return; }
        var incoming = edges.Keys.ToDictionary(x => x, _ => 0, StringComparer.Ordinal);
        foreach (var children in edges.Values) foreach (var child in children) incoming[child]++;
        var ready = new SortedSet<string>(incoming.Where(x => x.Value == 0).Select(x => x.Key), StringComparer.Ordinal);
        var order = new List<string>();
        while (ready.Count > 0)
        {
            var id = ready.Min!; ready.Remove(id); order.Add(id);
            foreach (var child in edges[id]) if (--incoming[child] == 0) ready.Add(child);
        }
        var initial = new Scenario();
        foreach (var goal in goalList)
        {
            var id = Canonical(goal.ItemId); Need(initial, id, goal.Quantity, [id], [GetItem(id)?.Name ?? "未收录物品"]);
        }
        var states = new List<Scenario> { initial };
        try
        {
            foreach (var id in order)
            {
                var next = new List<Scenario>();
                foreach (var state in states)
                {
                    if (next.Count >= MaxScenarios) throw new ExpansionLimitException();
                    if (!state.Demand.Remove(id, out var byGoal)) { next.Add(state); continue; }
                    var item = GetItem(id) ?? new PlannerItem { Id = id, Name = "未收录物品" };
                    var slices = byGoal.Values.OrderBy(x => x.Goals.Length == 1 && x.Goals[0] == id ? 0 : 1)
                        .ThenBy(x => string.Join("|", x.Goals), StringComparer.Ordinal).ToList();
                    var targetIds = slices.SelectMany(x => x.Goals).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                    var paths = slices.SelectMany(x => x.Paths).Distinct(StringComparer.Ordinal).Take(128).ToArray();
                    var required = item.IsReusable ? 1 : slices.Aggregate(0L, (sum, x) => Add(sum, x.Quantity));
                    var owned = item.IsReusable ? (unlocked.Contains(id) ? 1L : 0L) : stock.GetValueOrDefault(id);
                    var allocated = Math.Min(required, owned); var missing = required - allocated;
                    var options = recipes.GetValueOrDefault(id) ?? [];
                    var row = new MaterialPlanRow
                    {
                        ItemId = id, Name = item.Name, Icon = item.Icon, Stage = item.Stage, StageRank = item.StageRank,
                        Required = required, RequiredMaximum = required, Owned = owned, Allocated = allocated, AllocatedMaximum = allocated,
                        Missing = missing, MissingMaximum = missing, IsReusable = item.IsReusable, GoalIds = targetIds, Paths = paths,
                        RecipeId = options.Count == 1 ? options[0].Id : "",
                        RestrictionsText = string.Join("；", options.Select(x => x.RestrictionsText).Append(item.RestrictionsText).Where(x => x.Length > 0).Distinct()),
                        Status = missing == 0 ? (item.IsReusable ? "当前档已解锁，可复用" : "当前档物品已满足") : options.Count == 0 ? "未收录前置" : options.Count > 1 ? "保留可选组合" : "已展开前置"
                    };
                    state.Steps.Add(row);
                    // A craftable wardrobe unlock is a prerequisite step, not
                    // an additional acquisition alongside its crafting inputs.
                    if (options.Count == 0) state.Materials.Add(row);
                    if (missing == 0 || options.Count == 0) { next.Add(state); continue; }
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
                            if (quantity > 0 && options[optionIndex].RestrictionsText.Length > 0)
                                target.Restrictions.Add(options[optionIndex].RestrictionsText);
                            while (quantity > 0)
                            {
                                var slice = unmet[sliceIndex]; var take = Math.Min(quantity, slice.Quantity - sliceUsed);
                                foreach (var ingredient in options[optionIndex].Ingredients)
                                {
                                    var child = Canonical(ingredient.ItemId);
                                    Need(target, child, ingredient.IsReusable ? 1 : Multiply(take, ingredient.Quantity), slice.Goals,
                                        slice.Paths.Select(path => path + " → " + (GetItem(child)?.Name ?? ingredient.Name)));
                                }
                                quantity -= take; sliceUsed += take;
                                if (sliceUsed == slice.Quantity) { sliceIndex++; sliceUsed = 0; }
                            }
                        }
                    }
                    if (options.Count == 1) { Expand(state, [missing]); next.Add(state); }
                    else
                    {
                        var counts = new long[options.Count];
                        void Distribute(int index, long remaining)
                        {
                            if (index == counts.Length - 1)
                            {
                                if (next.Count >= MaxScenarios) throw new ExpansionLimitException();
                                counts[index] = remaining; var branch = state.Copy(); Expand(branch, counts); next.Add(branch); return;
                            }
                            // Each missing copy can use a different recipe.
                            // Enumerating compositions retains mixed routes.
                            for (long n = 0; n <= remaining; n++) { counts[index] = n; Distribute(index + 1, remaining - n); }
                        }
                        Distribute(0, missing);
                    }
                    if (next.Count > MaxScenarios) throw new ExpansionLimitException();
                }
                states = next;
            }
        }
        catch (ExpansionLimitException)
        {
            ClearCalculated(result);
            if (TryCalculateFactored(goalList, stock, unlocked, order, result)) return;
            ClearCalculated(result); KeepUnexpanded();
            result.Issues.Add(new("combination_limit", "关联组合较多，已保留完整配方关系与共享库存约束；尚未约化为确定缺口，不能将任意选项相加。"));
            return;
        }
        result.IsExact = true; result.ScenarioCount = states.Count;
        List<MaterialPlanRow> Merge(Func<Scenario, List<MaterialPlanRow>> select)
        {
            var perState = states.Select(s => select(s).ToDictionary(x => x.ItemId, StringComparer.Ordinal)).ToList();
            var ids = perState.SelectMany(x => x.Keys).Distinct(StringComparer.Ordinal);
            return ids.Select(id =>
            {
                var rows = perState.Select(x => x.GetValueOrDefault(id)).ToList(); var sample = rows.First(x => x is not null)!;
                var minRequired = rows.Min(x => x?.Required ?? 0); var maxRequired = rows.Max(x => x?.Required ?? 0);
                var minMissing = rows.Min(x => x?.Missing ?? 0); var maxMissing = rows.Max(x => x?.Missing ?? 0);
                return new MaterialPlanRow
                {
                    ItemId = id, Name = sample.Name, Icon = sample.Icon, Stage = sample.Stage, StageRank = sample.StageRank,
                    Required = minRequired, RequiredMaximum = maxRequired, Owned = sample.Owned,
                    Allocated = rows.Min(x => x?.Allocated ?? 0), AllocatedMaximum = rows.Max(x => x?.Allocated ?? 0),
                    Missing = minMissing, MissingMaximum = maxMissing, IsReusable = sample.IsReusable,
                    Status = minRequired != maxRequired ? "数量随可选组合变化" : sample.Status,
                    RecipeId = sample.RecipeId, RestrictionsText = sample.RestrictionsText,
                    GoalIds = rows.Where(x => x is not null).SelectMany(x => x!.GoalIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    Paths = rows.Where(x => x is not null).SelectMany(x => x!.Paths).Distinct(StringComparer.Ordinal).Take(128).ToArray()
                };
            }).OrderByDescending(x => x.StageRank).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
        }
        result.Steps.AddRange(Merge(x => x.Steps));
        result.Materials.AddRange(Merge(x => x.Materials));
        result.Allocations.AddRange(result.Steps.Where(x => x.AllocatedMaximum > 0));
        MaterialNeed NeedRow(MaterialPlanRow row, long quantity) => new()
        { ItemId = row.ItemId, Name = row.Name, Stage = row.Stage, Quantity = quantity, IsReusable = row.IsReusable, GoalIds = row.GoalIds };
        foreach (var row in result.Materials)
        {
            if (row.Missing > 0) result.OrdinaryNeeds.Add(NeedRow(row, row.Missing));
            result.QuantityRanges.Add(new(row.ItemId, row.Name, row.Stage, row.Missing, row.MissingMaximum, row.IsReusable));
            if (row.MissingMaximum > 0) result.Issues.Add(new("unrecorded_prerequisite", row.Name + "：未收录更早前置，获取方式仍待补充。", row.ItemId));
        }
        var common = result.Materials.ToDictionary(x => x.ItemId, x => x.Missing, StringComparer.Ordinal);
        var combinations = new SortedDictionary<string, MaterialPlanOption>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            var rest = state.Materials.Where(x => x.Missing > common.GetValueOrDefault(x.ItemId))
                .OrderBy(x => x.ItemId, StringComparer.Ordinal).Select(x => NeedRow(x, x.Missing - common.GetValueOrDefault(x.ItemId))).ToList();
            var restrictions = state.Restrictions.Order(StringComparer.Ordinal).ToList();
            var key = string.Join("|", rest.Select(x => x.ItemId + ":" + x.Quantity)) + "#" + string.Join("|", restrictions);
            if (!combinations.ContainsKey(key)) combinations[key] = new()
            {
                Needs = rest, Restrictions = restrictions
            };
        }
        if (combinations.Count > 1)
        {
            var options = combinations.Values.Select((x, i) => new MaterialPlanOption
            { Id = "option-" + (i + 1), Label = "组合 " + (i + 1), Needs = x.Needs, Restrictions = x.Restrictions }).ToList();
            result.OrGroups.Add(new() { Id = "combination-1", Title = "以下完整组合任选其一（另加普通需求）", Options = options });
        }
    }
}
