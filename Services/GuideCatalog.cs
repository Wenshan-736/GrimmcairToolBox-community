using System.IO;
using System.Text.Json;

namespace Grimmcair.Desktop.Services;

/// <summary>Published guide entries derived from this exact map revision.</summary>
public sealed class GuideCatalog
{
    public string MapVersion { get; init; } = "";
    public string MapSha256 { get; init; } = "";
    public List<GuideHero> Heroes { get; init; } = [];
    public List<GuideBoss> Bosses { get; init; } = [];
    public List<GuideBadgeBonus> BadgeBonuses { get; init; } = [];

    public static GuideCatalog Load()
    {
        using var catalog = JsonDocument.Parse(ResourcePack.ReadText("catalog.json"));
        return FromJson(ResourcePack.ReadText("guide.json"),
            catalog.RootElement.GetProperty("mapVersion").GetString() ?? "",
            catalog.RootElement.GetProperty("sourceSha256").GetString() ?? "");
    }

    public static GuideCatalog FromJson(string json, string expectedMapVersion, string expectedMapSha256)
    {
        var guide = JsonSerializer.Deserialize<GuideCatalog>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidDataException("英雄与 BOSS 资料为空。");
        if (string.IsNullOrWhiteSpace(guide.MapSha256) ||
            !string.Equals(guide.MapSha256, expectedMapSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(guide.MapVersion, expectedMapVersion, StringComparison.Ordinal))
            throw new InvalidDataException("英雄与 BOSS 资料的地图版本不匹配，请重新构建或更新程序。");
        if (guide.Heroes.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != guide.Heroes.Count ||
            guide.Bosses.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != guide.Bosses.Count)
            throw new InvalidDataException("英雄与 BOSS 资料包含重复条目。");
        return guide;
    }

    public IEnumerable<GuideDropSource> FindDrops(string itemId) =>
        Bosses.SelectMany(boss => boss.Drops.Where(drop => drop.ItemId == itemId)
            .Select(drop => new GuideDropSource(boss, drop)));
}

public sealed class GuideHero
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Icon { get; init; } = "";
    public List<GuideUnit> Units { get; init; } = [];
}

public sealed class GuideUnit
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Icon { get; init; } = "";
    public string AttributesText { get; init; } = "";
    public string OriginText { get; init; } = "";
    public List<GuideSkill> Skills { get; init; } = [];
}

public sealed class GuideSkill
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Key { get; init; } = "";
    public string BasicText { get; init; } = "";
    public List<GuideTextRun> BasicRuns { get; init; } = [];
    public string RangeText { get; init; } = "";
    public string DetailsText { get; init; } = "";
    public string OverviewText { get; init; } = "";
    public List<GuideReadingSection> ReadingSections { get; init; } = [];
    public List<GuideFormula> Formulas { get; init; } = [];
    public List<GuideDiagram> Diagrams { get; init; } = [];
    public string KeyLabel => string.IsNullOrWhiteSpace(Key) ? "—" : Key;
}

public sealed class GuideTextRun
{
    public string Text { get; init; } = "";
    public string Color { get; init; } = "";
}

public sealed class GuideReadingSection
{
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public List<string> FormulaIds { get; init; } = [];
}

public sealed class GuideFormula
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Expression { get; init; } = "";
    public string VariablesText { get; init; } = "";
    public string ConditionsText { get; init; } = "";
    public List<GuideFormulaExample> Examples { get; init; } = [];
}

public sealed class GuideFormulaExample
{
    public string InputsText { get; init; } = "";
    public string ResultText { get; init; } = "";
}

public sealed class GuideDiagram
{
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public string Caption { get; init; } = "";
    public string RangeText { get; init; } = "";
    public string CenterLabel { get; init; } = "";
    public double AngleDegrees { get; init; } = 90;
    public int Count { get; init; }
}

public sealed class GuideBoss
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Stage { get; init; } = "";
    public bool InformationOnly { get; init; }
    public string AttributesText { get; init; } = "";
    public List<GuideSkill> Skills { get; init; } = [];
    public List<GuideUnit> Units { get; init; } = [];
    public List<GuideDrop> Drops { get; init; } = [];
    public string DropSummaryText { get; init; } = "";
    public string PlayersText { get; init; } = "";
}

public sealed class GuideDrop
{
    public string ItemId { get; init; } = "";
    public string ItemName { get; init; } = "";
    public string BaseChanceText { get; init; } = "";
    public string Notes { get; init; } = "";
    public string DetailsText { get; init; } = "";
    public string Icon => $"Icons/{ItemId}.png";
}

public sealed class GuideBadgeBonus
{
    public string ItemId { get; init; } = "";
    public string Text { get; init; } = "";
}

public sealed record GuideDropSource(GuideBoss Boss, GuideDrop Drop);
