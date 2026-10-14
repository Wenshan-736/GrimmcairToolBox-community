namespace Grimmcair.Desktop.Services;

public sealed class MaterialSynthesisNode
{
    public string Key { get; internal set; } = "";
    public string ItemId { get; internal set; } = "";
    public string GoalId { get; internal set; } = "";
    public string Name { get; internal set; } = "";
    public string Icon { get; internal set; } = "";
    public string Stage { get; internal set; } = "";
    public long Required { get; internal set; }
    public long Owned { get; internal set; }
    public long Allocated { get; internal set; }
    public long Remaining { get; internal set; }
    public bool AllocationKnown { get; internal set; } = true;
    public bool IsGoal { get; internal set; }
    public bool IsReusable { get; internal set; }
    public bool IsRecipe { get; internal set; }
    public string ChoiceLabel { get; internal set; } = "";
    public string ChoiceGroup { get; internal set; } = "";
    public bool IsSharedReference { get; internal set; }
    public int Depth { get; internal set; }
    public string Status { get; internal set; } = "";
    public string RestrictionsText { get; internal set; } = "";
    public List<MaterialSynthesisNode> Children { get; } = [];
    internal Action? ExpandChildren { get; set; }
    public bool CanExpand => Children.Count > 0 || ExpandChildren is not null;
    public void EnsureExpanded()
    {
        var expand = ExpandChildren;
        if (expand is null) return;
        ExpandChildren = null;
        expand();
    }
}

public sealed class MaterialSynthesisScheme
{
    public string Label { get; internal set; } = "";
    public List<MaterialSynthesisNode> Targets { get; } = [];
}

public sealed class MaterialSynthesisResult
{
    public bool IsComplete { get; internal set; }
    public bool IsRecipeView { get; internal set; }
    public string Message { get; internal set; } = "";
    public List<MaterialSynthesisScheme> Schemes { get; } = [];
}

/// <summary>
/// A display-only forest of coherent recipe combinations. It annotates the
/// planner's pinned recipes with one shared inventory allocation per scheme;
/// it never replaces the planner's total needs or changes cloud requirements.
/// </summary>
public static class MaterialSynthesisTree
{
    private const int MaxGoals = 128, MaxNodes = 4096, MaxDepth = 64, MaxSchemes = 256;
    private const long MaxQuantity = 1_000_000_000;
    private const int MaxStoredNodes = 131072;
    private sealed class DisplayLimitException(string message) : Exception(message) { }
    private sealed class Forest
    {
        public List<MaterialSynthesisNode> Targets { get; } = [];
        public Dictionary<string, MaterialSynthesisNode> Nodes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<string>> Pending { get; } = new(StringComparer.Ordinal);
        public int NextKey { get; set; }
        public Forest Copy()
        {
            var copy = new Forest { NextKey = NextKey };
            MaterialSynthesisNode Clone(MaterialSynthesisNode node)
            {
                var value = new MaterialSynthesisNode
                {
                    Key = node.Key, ItemId = node.ItemId, GoalId = node.GoalId, Name = node.Name, Icon = node.Icon,
                    Stage = node.Stage, Required = node.Required, Owned = node.Owned, Allocated = node.Allocated,
                    Remaining = node.Remaining, IsGoal = node.IsGoal, IsReusable = node.IsReusable, IsRecipe = node.IsRecipe,
                    ChoiceLabel = node.ChoiceLabel, ChoiceGroup = node.ChoiceGroup,
                    AllocationKnown = node.AllocationKnown,
                    IsSharedReference = node.IsSharedReference, Depth = node.Depth, Status = node.Status,
                    RestrictionsText = node.RestrictionsText
                };
                copy.Nodes.Add(value.Key, value);
                value.Children.AddRange(node.Children.Select(Clone));
                return value;
            }
            copy.Targets.AddRange(Targets.Select(Clone));
            foreach (var (id, keys) in Pending) copy.Pending[id] = [.. keys];
            return copy;
        }
    }

    private static long Add(long a, long b)
    {
        var value = checked(a + b);
        if (value > MaxQuantity) throw new OverflowException();
        return value;
    }
    private static long Multiply(long a, long b)
    {
        var value = checked(a * b);
        if (value > MaxQuantity) throw new OverflowException();
        return value;
    }

    public static MaterialSynthesisResult Build(MaterialPlanner planner, IEnumerable<MaterialGoal> goals,
        IReadOnlyDictionary<string, long> inventory, IReadOnlySet<string> wardrobe, MaterialPlanResult plan, bool preferRecipeView = false)
    {
        var result = new MaterialSynthesisResult();
        var requested = goals.Take(MaxGoals + 1).ToList();
        if (requested.Count == 0) { result.IsComplete = true; result.Message = "还没有装备目标，请先添加目标。"; return result; }
        if (requested.Count > MaxGoals || requested.Any(x => x.Quantity is <= 0 or > MaxQuantity))
        { result.Message = "目标数量超出合成树展示范围。"; return result; }
        var stock = new Dictionary<string, long>(StringComparer.Ordinal);
        var unlocked = wardrobe.Select(planner.Canonical).ToHashSet(StringComparer.Ordinal);
        var initial = new Forest();
        try
        {
            foreach (var (id, amount) in inventory)
            {
                if (amount is < 0 or > MaxQuantity) throw new OverflowException();
                var key = planner.Canonical(id); stock[key] = Add(stock.GetValueOrDefault(key), amount);
            }
            var goalList = requested.GroupBy(x => planner.Canonical(x.ItemId), StringComparer.Ordinal)
                .Where(x => ShowAsTarget(planner.GetItem(x.Key)))
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new MaterialGoal(x.Key, x.Aggregate(0L, (sum, goal) => Add(sum, goal.Quantity)))).ToList();
            MaterialSynthesisNode ItemNode(Forest forest, string id, long quantity, string goal, int depth, bool isGoal = false)
            {
                id = planner.Canonical(id);
                var item = planner.GetItem(id);
                if (forest.Nodes.Count >= MaxNodes || depth > MaxDepth)
                    throw new DisplayLimitException("合成树超过节点或深度上限，未展示部分组合。");
                var node = new MaterialSynthesisNode
                {
                    Key = "node-" + forest.NextKey++, ItemId = id, GoalId = goal, Name = item?.Name ?? "未收录物品",
                    Icon = item?.Icon ?? "", Stage = item?.Stage ?? "", Required = item?.IsReusable == true ? 1 : quantity,
                    Remaining = item?.IsReusable == true ? 1 : quantity, IsGoal = isGoal, IsReusable = item?.IsReusable == true,
                    Depth = depth, RestrictionsText = item?.RestrictionsText ?? ""
                };
                forest.Nodes.Add(node.Key, node);
                if (!forest.Pending.TryGetValue(id, out var pending)) forest.Pending[id] = pending = [];
                pending.Add(node.Key);
                return node;
            }
            foreach (var goal in goalList) initial.Targets.Add(ItemNode(initial, goal.ItemId, goal.Quantity, goal.ItemId, 0, true));
            if (preferRecipeView || !plan.IsExact || plan.OrGroups.Count > 1)
            {
                var recipeView = BuildRecipeView(planner, requested, stock, unlocked);
                if (recipeView is not null) return recipeView;
            }
            if (!plan.IsExact)
                throw new DisplayLimitException("总体配方尚未形成确定组合；合成树仅保留目标入口，请查看可选配方关系。");

            // Union topology ensures that every potential consumer is processed
            // before its common ingredient, including targets that are also an
            // ingredient of another target. Direct targets reserve stock first.
            var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            void Visit(string id, int depth)
            {
                if (visiting.Contains(id)) throw new DisplayLimitException("合成关系有循环，合成树未展开。");
                if (edges.ContainsKey(id)) return;
                if (depth > MaxDepth || edges.Count >= MaxNodes) throw new DisplayLimitException("合成树超过节点或深度上限。");
                visiting.Add(id); edges[id] = new(StringComparer.Ordinal);
                foreach (var recipe in planner.Recipes.GetValueOrDefault(id) ?? [])
                    foreach (var ingredient in recipe.Ingredients)
                    {
                        var child = planner.Canonical(ingredient.ItemId); edges[id].Add(child); Visit(child, depth + 1);
                    }
                visiting.Remove(id);
            }
            foreach (var goal in goalList) Visit(goal.ItemId, 0);
            var incoming = edges.Keys.ToDictionary(x => x, _ => 0, StringComparer.Ordinal);
            foreach (var children in edges.Values) foreach (var child in children) incoming[child]++;
            var ready = new SortedSet<string>(incoming.Where(x => x.Value == 0).Select(x => x.Key), StringComparer.Ordinal);
            var order = new List<string>();
            while (ready.Count > 0)
            {
                var id = ready.Min!; ready.Remove(id); order.Add(id);
                foreach (var child in edges[id]) if (--incoming[child] == 0) ready.Add(child);
            }
            var forests = new List<Forest> { initial };
            foreach (var id in order)
            {
                var next = new List<Forest>();
                foreach (var forest in forests)
                {
                    if (!forest.Pending.Remove(id, out var pending)) { next.Add(forest); continue; }
                    var nodes = pending.Select(key => forest.Nodes[key]).OrderBy(x => x.IsGoal ? 0 : 1)
                        .ThenBy(x => x.GoalId, StringComparer.Ordinal).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
                    var reusable = nodes[0].IsReusable;
                    var required = reusable ? 1 : nodes.Aggregate(0L, (sum, node) => Add(sum, node.Required));
                    var owned = reusable ? (unlocked.Contains(id) ? 1L : 0L) : stock.GetValueOrDefault(id);
                    var available = Math.Min(required, owned);
                    var recipes = planner.Recipes.GetValueOrDefault(id) ?? [];
                    foreach (var node in nodes)
                    {
                        node.Owned = owned;
                        node.Allocated = reusable ? available : Math.Min(node.Required, available);
                        if (!reusable) available -= node.Allocated;
                        node.Remaining = node.Required - node.Allocated;
                        node.Status = node.Remaining == 0 ? reusable ? "当前档衣柜已解锁，可复用；停止展开" : "当前档已保存库存已抵扣；停止展开" :
                            recipes.Count == 0 ? "还需获取；更早前置未收录" : "仅为未满足数量展开已核配方";
                    }
                    var unmet = nodes.Where(x => x.Remaining > 0).ToList();
                    if (reusable && unmet.Count > 1)
                    {
                        foreach (var reference in unmet.Skip(1))
                        {
                            reference.IsSharedReference = true;
                            reference.Status = "可复用解锁前置；材料已在“" + (planner.GetItem(unmet[0].GoalId)?.Name ?? "另一目标") + "”合成树列出一次";
                        }
                        unmet = [unmet[0]];
                    }
                    var missing = unmet.Aggregate(0L, (sum, node) => Add(sum, node.Remaining));
                    if (missing == 0 || recipes.Count == 0) { next.Add(forest); continue; }
                    var unmetKeys = unmet.Select(x => x.Key).ToArray();
                    void Expand(Forest target, long[] counts)
                    {
                        var nodeIndex = 0; long used = 0;
                        for (var recipeIndex = 0; recipeIndex < counts.Length; recipeIndex++)
                        {
                            var remaining = counts[recipeIndex];
                            while (remaining > 0)
                            {
                                var parent = target.Nodes[unmetKeys[nodeIndex]];
                                var take = Math.Min(remaining, parent.Remaining - used);
                                if (target.Nodes.Count >= MaxNodes) throw new DisplayLimitException("合成树超过节点上限。");
                                var recipe = recipes[recipeIndex];
                                var group = new MaterialSynthesisNode
                                {
                                    Key = "node-" + target.NextKey++, GoalId = parent.GoalId, IsRecipe = true,
                                    Name = recipes.Count == 1 ? "合成 " + take + " 件 · 以下材料全部需要" :
                                        "已核路线 " + (recipeIndex + 1) + " · 合成 " + take + " 件 · 以下材料全部需要",
                                    Required = take, Depth = parent.Depth, RestrictionsText = recipe.RestrictionsText
                                };
                                target.Nodes.Add(group.Key, group); parent.Children.Add(group);
                                foreach (var ingredient in recipe.Ingredients)
                                    group.Children.Add(ItemNode(target, ingredient.ItemId, ingredient.IsReusable ? 1 : Multiply(take, ingredient.Quantity),
                                        parent.GoalId, parent.Depth + 1));
                                remaining -= take; used += take;
                                if (used == parent.Remaining) { nodeIndex++; used = 0; }
                            }
                        }
                    }
                    if (recipes.Count == 1) { Expand(forest, [missing]); next.Add(forest); }
                    else
                    {
                        var counts = new long[recipes.Count];
                        void Distribute(int index, long remaining)
                        {
                            if (index == counts.Length - 1)
                            {
                                if (next.Count >= MaxSchemes) throw new DisplayLimitException("合成树组合超过256组，未展示部分组合。");
                                counts[index] = remaining; var branch = forest.Copy(); Expand(branch, counts); next.Add(branch); return;
                            }
                            for (long n = 0; n <= remaining; n++) { counts[index] = n; Distribute(index + 1, remaining - n); }
                        }
                        Distribute(0, missing);
                    }
                    if (next.Count > MaxSchemes || next.Sum(x => (long)x.Nodes.Count) > MaxStoredNodes)
                        throw new DisplayLimitException("合成树展示量超过安全上限，未展示部分组合。");
                }
                forests = next;
            }
            foreach (var (forest, index) in forests.Select((value, index) => (value, index)))
            {
                var scheme = new MaterialSynthesisScheme { Label = "完整组合 " + (index + 1) };
                scheme.Targets.AddRange(forest.Targets); result.Schemes.Add(scheme);
            }
            result.IsComplete = true;
            result.Message = result.Schemes.Count > 1 ?
                $"可查看 {result.Schemes.Count} 个不同的已核路线完整组合；每组内全部目标共用一次当前档库存。切换只改变树示意，总体缺口仍包含所有可选组合。" :
                "点击目标展开合成树；只展开尚未满足的数量，当前档已保存的前置成品停止展开。总体缺口仍以“全部目标材料缺口”为准。";
        }
        catch (Exception error) when (error is DisplayLimitException or OverflowException)
        {
            if (error is DisplayLimitException && (error.Message.Contains("组合") || error.Message.Contains("展示量")))
            {
                var recipeView = BuildRecipeView(planner, requested, stock, unlocked);
                if (recipeView is not null) return recipeView;
            }
            result.Message = error is DisplayLimitException ? error.Message : "合成树数量超出安全范围，未展示部分数量。";
            result.Message += " 总体缺口请看材料分析；此处不输出新的确定总单。";
            var fallback = new MaterialSynthesisScheme { Label = "目标入口（合成树未完整展开）" };
            foreach (var node in initial.Targets)
            {
                // Initial may already have been explored in a single-path
                // forest. Preserve only target identity in the partial view.
                fallback.Targets.Add(new MaterialSynthesisNode { Key = node.Key, ItemId = node.ItemId, GoalId = node.GoalId,
                    Name = node.Name, Icon = node.Icon, Stage = node.Stage, Required = node.Required,
                    Owned = node.IsReusable ? unlocked.Contains(node.ItemId) ? 1 : 0 : stock.GetValueOrDefault(node.ItemId),
                    Remaining = node.Required, AllocationKnown = false, IsGoal = true, IsReusable = node.IsReusable,
                    Status = "树未完整展开；请查看物品配方与总体缺口" });
            }
            if (fallback.Targets.Count > 0) result.Schemes.Add(fallback);
        }
        return result;
    }

    private static bool ShowAsTarget(PlannerItem? item) => item is null ||
        !(item.Name.EndsWith("魔石", StringComparison.Ordinal) || item.Name.EndsWith("结晶", StringComparison.Ordinal));

    /// <summary>
    /// Recipe alternatives are a small AND/OR graph, even when their global
    /// Cartesian product is enormous. This projection keeps that graph and
    /// realizes only the branches opened by the user. It does not turn stock
    /// into independent per-target deficit claims.
    /// </summary>
    private static MaterialSynthesisResult? BuildRecipeView(MaterialPlanner planner, List<MaterialGoal> requested,
        IReadOnlyDictionary<string, long> stock, IReadOnlySet<string> unlocked)
    {
        var result = new MaterialSynthesisResult { IsRecipeView = true };
        var goals = requested.GroupBy(x => planner.Canonical(x.ItemId), StringComparer.Ordinal)
            .Where(x => ShowAsTarget(planner.GetItem(x.Key))).OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new MaterialGoal(x.Key, x.Aggregate(0L, (sum, goal) => Add(sum, goal.Quantity)))).ToList();
        var paths = new Dictionary<string, int>(StringComparer.Ordinal);
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            void Visit(string id, int depth)
            {
                if (visiting.Contains(id) || depth > MaxDepth || edges.Count >= MaxNodes) throw new DisplayLimitException("配方结构超出展示范围。");
                if (edges.ContainsKey(id)) return;
                visiting.Add(id);
                edges[id] = (planner.Recipes.GetValueOrDefault(id) ?? []).SelectMany(x => x.Ingredients)
                    .Select(x => planner.Canonical(x.ItemId)).ToList();
                foreach (var child in edges[id]) Visit(child, depth + 1);
                visiting.Remove(id);
            }
            foreach (var goal in goals) Visit(goal.ItemId, 0);
            var incoming = edges.Keys.ToDictionary(x => x, _ => 0, StringComparer.Ordinal);
            foreach (var children in edges.Values) foreach (var child in children) incoming[child]++;
            var ready = new SortedSet<string>(incoming.Where(x => x.Value == 0).Select(x => x.Key), StringComparer.Ordinal);
            foreach (var goal in goals) paths[goal.ItemId] = Math.Min(2, paths.GetValueOrDefault(goal.ItemId) + 1);
            while (ready.Count > 0)
            {
                var id = ready.Min!; ready.Remove(id);
                foreach (var child in edges[id])
                {
                    paths[child] = Math.Min(2, paths.GetValueOrDefault(child) + paths.GetValueOrDefault(id));
                    if (--incoming[child] == 0) ready.Add(child);
                }
            }
        }
        catch (DisplayLimitException) { return null; }

        // Direct finished targets reserve stock before any route can use the
        // same product as an ingredient. Other ambiguous consumers show saved
        // stock only; they cannot each claim the same allocation.
        var goalReservations = goals.ToDictionary(x => x.ItemId, x => Math.Min(x.Quantity, stock.GetValueOrDefault(x.ItemId)), StringComparer.Ordinal);
        var nextKey = 0;
        MaterialSynthesisNode ItemNode(string id, long quantity, string goal, int depth, bool isGoal = false)
        {
            id = planner.Canonical(id);
            var item = planner.GetItem(id);
            var reusable = item?.IsReusable == true;
            var required = reusable ? 1 : quantity;
            var owned = reusable ? unlocked.Contains(id) ? 1L : 0L : stock.GetValueOrDefault(id);
            var available = reusable || isGoal ? owned : Math.Max(0, owned - goalReservations.GetValueOrDefault(id));
            var known = reusable || isGoal || available == 0 || paths.GetValueOrDefault(id) == 1;
            var allocated = known ? Math.Min(required, available) : 0;
            var node = new MaterialSynthesisNode
            {
                Key = "recipe-node-" + nextKey++, ItemId = id, GoalId = goal, Name = item?.Name ?? "未收录物品",
                Icon = item?.Icon ?? "", Stage = item?.Stage ?? "", Required = required, Owned = owned,
                Allocated = allocated, Remaining = required - allocated, AllocationKnown = known,
                IsGoal = isGoal, IsReusable = reusable, Depth = depth, RestrictionsText = item?.RestrictionsText ?? ""
            };
            var recipes = planner.Recipes.GetValueOrDefault(id) ?? [];
            node.Status = node.Remaining == 0 ? reusable ? "衣柜已解锁，可复用；无需展开前置" : "已有成品满足此处用量；无需展开前置" :
                !known ? "这份库存可能被其他目标或备选材料使用；实际缺口请看材料缺口表" :
                recipes.Count > 1 ? "公共材料全部需要，标出的备选材料任选一项；完整组合不能拆开混选" +
                    (node.Remaining > 1 ? "。多件装备可以各自选用不同配方" : "") :
                recipes.Count == 1 ? "点击查看所需材料" : "更早前置未收录，请查看获取来源";
            if (node.Remaining == 0 || recipes.Count == 0) return node;
            node.ExpandChildren = () =>
            {
                var children = new List<MaterialSynthesisNode>();
                try
                {
                    if (depth >= MaxDepth || nextKey >= MaxStoredNodes) throw new DisplayLimitException("这一支前置过深，请查看物品配方。");
                    var parts = recipes.Select(recipe =>
                    {
                        var quantities = new Dictionary<(string ItemId, bool IsReusable), long>();
                        foreach (var ingredient in recipe.Ingredients)
                        {
                            var ingredientId = planner.Canonical(ingredient.ItemId);
                            var key = (ingredientId, ingredient.IsReusable || planner.GetItem(ingredientId)?.IsReusable == true);
                            quantities[key] = key.Item2 ? 1 : Add(quantities.GetValueOrDefault(key), ingredient.Quantity);
                        }
                        return quantities;
                    }).ToList();
                    var common = parts[0].Where(part => parts.All(option => option.ContainsKey(part.Key)))
                        .ToDictionary(part => part.Key, part => parts.Min(option => option[part.Key]));
                    MaterialSynthesisNode Ingredient((string ItemId, bool IsReusable) key, long count) =>
                        ItemNode(key.ItemId, key.IsReusable ? 1 : Multiply(node.Remaining, count), goal, depth + 1);
                    foreach (var (key, count) in common) children.Add(Ingredient(key, count));
                    if (recipes.Count == 1)
                    {
                        node.RestrictionsText = JoinText(node.RestrictionsText, recipes[0].RestrictionsText);
                    }
                    else
                    {
                        var choiceLabel = recipes.Count == 2 ? "二选一" : recipes.Count + "选一";
                        var choiceGroup = node.Key + "-choice";
                        for (var index = 0; index < recipes.Count; index++)
                        {
                            var remainder = parts[index].Select(part => (part.Key, Count: part.Value - common.GetValueOrDefault(part.Key)))
                                .Where(part => part.Count > 0).ToList();
                            var restrictions = JoinText(recipes[index].RestrictionsText, node.Remaining > 1 ?
                                "此项用量按全部 " + node.Remaining + " 件采用同一配方显示；不同件可分别使用不同配方。实际缺口以材料缺口表为准。" : "");
                            if (remainder.Count == 1)
                            {
                                var option = Ingredient(remainder[0].Key, remainder[0].Count);
                                option.ChoiceLabel = choiceLabel; option.ChoiceGroup = choiceGroup;
                                option.RestrictionsText = JoinText(option.RestrictionsText, restrictions);
                                children.Add(option);
                            }
                            else
                            {
                                var group = new MaterialSynthesisNode
                                {
                                    Key = "recipe-node-" + nextKey++, GoalId = goal, IsRecipe = true, Required = node.Remaining,
                                    ChoiceLabel = choiceLabel, ChoiceGroup = choiceGroup, Depth = depth,
                                    Name = remainder.Count == 0 ? "无需额外材料" : "组合 " + (index + 1) + "（组合内全部需要）",
                                    RestrictionsText = restrictions
                                };
                                foreach (var part in remainder) group.Children.Add(Ingredient(part.Key, part.Count));
                                children.Add(group);
                            }
                        }
                    }
                    node.Children.AddRange(children);
                }
                catch (Exception error) when (error is DisplayLimitException or OverflowException)
                {
                    result.IsComplete = false; node.AllocationKnown = false;
                    node.Status = error is DisplayLimitException ? error.Message : "这一支数量过大，未展示不确定的数量。";
                }
            };
            return node;
        }
        var scheme = new MaterialSynthesisScheme { Label = "逐项查看合成配方" };
        foreach (var goal in goals) scheme.Targets.Add(ItemNode(goal.ItemId, goal.Quantity, goal.ItemId, 0, true));
        result.Schemes.Add(scheme); result.IsComplete = true;
        result.Message = "点击装备即可查看材料。公共材料全部需要，标出的二选一材料任选一项；完整组合不能拆开混选。已有成品满足此处用量时停止展开。全部目标共用库存后的实际缺口请看材料缺口表。";
        return result;
    }

    private static string JoinText(params string[] values) => string.Join("\n", values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal));
}
