using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Grimmcair.Desktop.Services;

public sealed record MaterialNeedGoal(string ItemId, string Name, long Quantity);
public sealed class MaterialUnresolvedGroup
{
    public string Id { get; init; } = "pending-combination";
    public string Title { get; init; } = "部分材料尚未算完";
    public string Reason { get; init; } = "";
    public List<MaterialNeedGoal> Goals { get; init; } = [];
    public List<PlannerRecipe> Recipes { get; init; } = [];
}

/// <summary>Versioned public demand document; deliberately excludes save codes and raw parse data.</summary>
public sealed class MaterialNeedsDocument
{
    public int SchemaVersion { get; init; } = 1;
    public string Kind { get; init; } = "grimmcair.material-needs";
    public string Status { get; set; } = "ineligible";
    public bool IsExact { get; set; }
    public string MapVersion { get; init; } = "";
    public string MapSha256 { get; init; } = "";
    public string RecipeSetSha256 { get; init; } = "";
    public string SaveId { get; init; } = "";
    public long SaveRevision { get; init; }
    public string GoalsRevision { get; init; } = "";
    public string OwnerId { get; init; } = "";
    public string GameAccount { get; init; } = "";
    public string Profession { get; init; } = "";
    public string ProfessionId { get; init; } = "";
    public string SnapshotKind { get; init; } = "local";
    public DateTimeOffset? SnapshotAt { get; init; }
    public DateTimeOffset CalculatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<MaterialNeedGoal> Goals { get; init; } = [];
    public List<MaterialNeed> OrdinaryNeeds { get; set; } = [];
    public List<MaterialPlanGroup> OrGroups { get; set; } = [];
    public List<MaterialQuantityRange> QuantityRanges { get; set; } = [];
    public List<MaterialUnresolvedGroup> UnresolvedGroups { get; set; } = [];
    public List<MaterialPlanIssue> Issues { get; set; } = [];
    public string Semantics { get; init; } = MaterialNeedsExporter.ReportingSemantics;
}

public static class MaterialNeedsExporter
{
    public const string CurrentMapSha256 = "59459e3eb9dd4a47a2c820a7cd211c00569b067ef5800c2edee70a11d0336d6d";
    public const int MinimumReportedStageRank = 8; // Current catalog: 爵土马判=8, 大天使=9; 君主=7.
    public const string ReportingScopeNote = "仅上报爵及以上材料，低阶段及魔石/结晶保留本地计算";
    public const string ReportingSemantics = ReportingScopeNote
        + "。目标装备单独完整上报，未标阶段材料不猜入上报范围。ordinaryNeeds全部需要；每个orGroups只选择一个原完整方案，该option.needs全部需要。"
        + "普通需求与所选组合相加；不同选项不能相加或拆拼。空option表示该原方案在本上报范围内没有材料缺口，不代表完整配方材料已足够。"
        + "quantityRanges仅是各材料可能区间，不能分别取最小值。unresolvedGroups仅说明本地尚未完成的部分，不上传配方供服务器或网页计算。";
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static bool CanAnalyze(CloudSave? save) => save?.IsMain == true && CanAnalyzeLocally(save);

    public static bool CanAnalyzeLocally(CloudSave? save) => save is { IsDemo: false, ParsedResult: JsonObject parsed }
        && parsed["kind"]?.ToString() == "character"
        && parsed["map_profile"] is JsonObject profile
        && profile["script_sha256"]?.ToString() == SaveProfile.ScriptSha256;

    public static Dictionary<string, long> Inventory(CloudSave save) => save.Inventory
        .GroupBy(x => x.ItemId, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Sum(x => (long)x.Quantity), StringComparer.Ordinal);

    public static HashSet<string> Wardrobe(CloudSave save)
    {
        var wardrobe = new HashSet<string>(StringComparer.Ordinal);
        if (save.ParsedResult?["wardrobe"] is JsonArray categories)
            foreach (var category in categories)
                if (category?["items"] is JsonArray items)
                    foreach (var item in items)
                        if (item?["rawcode"]?.ToString() is { Length: > 0 } id) wardrobe.Add(id);
        return wardrobe;
    }

    public static MaterialNeedsDocument Create(MaterialPlanner planner, CloudSave snapshot, IEnumerable<CloudGoal>? goals = null, string professionId = "")
    {
        var goalList = (goals ?? snapshot.Targets).Select(x => new MaterialNeedGoal(planner.Canonical(x.ItemId),
            planner.GetItem(x.ItemId)?.Name ?? "未收录目标", x.Quantity)).OrderBy(x => x.ItemId, StringComparer.Ordinal).ThenBy(x => x.Quantity).ToList();
        var goalRevision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(goalList, JsonOptions)))).ToLowerInvariant();
        var document = new MaterialNeedsDocument
        {
            MapVersion = planner.MapVersion, MapSha256 = planner.MapSha256, RecipeSetSha256 = planner.RecipeSetSha256,
            SaveId = snapshot is CloudSnapshot synced ? synced.SaveId : snapshot.Id, SaveRevision = snapshot.Revision,
            GoalsRevision = goalRevision, OwnerId = snapshot.OwnerId, GameAccount = snapshot.GameAccount, Profession = snapshot.Profession, ProfessionId = professionId,
            SnapshotKind = snapshot is CloudSnapshot ? "synced" : "local", SnapshotAt = snapshot is CloudSnapshot s ? s.SyncedAt : null,
            Goals = goalList
        };
        if (!CanAnalyze(snapshot) || planner.MapSha256 != CurrentMapSha256)
        {
            document.Issues.Add(new("ineligible_save", "仅上报当前地图的真实角色主档需求；副档目标与材料缺口只在本地计算。演示档或版本不符时不输出确定需求。"));
            return document;
        }
        var stock = Inventory(snapshot);
        var wardrobe = Wardrobe(snapshot);
        var result = planner.Calculate(goalList.Select(x => new MaterialGoal(x.ItemId, x.Quantity)), stock, wardrobe);
        document.Status = result.IsExact ? "calculated" : "partial";
        document.IsExact = result.IsExact;
        // Apply the public scope only after the full plan has spent shared inventory
        // and built correlated alternatives. Never restrict the planner's inputs.
        document.OrdinaryNeeds = result.OrdinaryNeeds.Where(need => IsReportableMaterial(planner, need.ItemId)).ToList();
        document.OrGroups = result.OrGroups.Select(group => new MaterialPlanGroup
        {
            Id = group.Id, Title = group.Title, Choose = group.Choose,
            Options = group.Options.Select(option =>
            {
                var needs = option.Needs.Where(need => IsReportableMaterial(planner, need.ItemId)).ToList();
                return new MaterialPlanOption
                {
                    Id = option.Id, Label = option.Label + (needs.Count == 0 ? " · 本上报范围无缺口" : ""),
                    Needs = needs, Restrictions = [.. option.Restrictions]
                };
            }).ToList()
        }).ToList();
        document.QuantityRanges = result.QuantityRanges.Where(range => range.Maximum > 0 && IsReportableMaterial(planner, range.ItemId)).ToList();
        document.Issues = result.Issues.Where(issue => issue.ItemId.Length == 0 || IsReportableMaterial(planner, issue.ItemId)
            || goalList.Any(goal => goal.ItemId == issue.ItemId)).ToList();
        if (result.UnexpandedRecipes.Count > 0)
        {
            document.UnresolvedGroups.Add(new() { Goals = goalList,
                Reason = string.Join("；", result.Issues.Where(x => x.Code != "unrecorded_prerequisite").Select(x => x.Message))
                    + "；这部分尚未在本地算出材料缺口，未上传配方，也不能按零缺口处理。" });
        }
        return document;
    }

    public static bool IsReportableMaterial(MaterialPlanner planner, string itemId)
    {
        var item = planner.GetItem(itemId);
        // Names come from the pinned canonical catalog, never from an uploaded name.
        // Magic stones and crystals are separate rewards outside this demand scope.
        return item is not null && item.StageRank >= MinimumReportedStageRank && !string.IsNullOrWhiteSpace(item.Stage)
            && !item.Name.EndsWith("魔石", StringComparison.Ordinal) && !item.Name.EndsWith("结晶", StringComparison.Ordinal);
    }

    public static string Serialize(MaterialNeedsDocument document) => JsonSerializer.Serialize(document, JsonOptions);
}
