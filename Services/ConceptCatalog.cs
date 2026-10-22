using System.IO;
using System.Text.Json;

namespace Grimmcair.Desktop.Services;

/// <summary>Player-facing explanations pinned to the same published map.</summary>
public sealed class ConceptCatalog
{
    public string MapVersion { get; init; } = "";
    public string MapSha256 { get; init; } = "";
    public string ScriptSha256 { get; init; } = "";
    public List<ConceptSection> Sections { get; init; } = [];
    public List<ConceptGuideSourceCard> GuideSources { get; init; } = [];
    public List<DropCalculatorProfile> CalculatorProfiles { get; init; } = [];

    public static ConceptCatalog Load()
    {
        using var catalog = JsonDocument.Parse(ResourcePack.ReadText("catalog.json"));
        return FromJson(ResourcePack.ReadText("concepts.json"),
            catalog.RootElement.GetProperty("mapVersion").GetString() ?? "",
            catalog.RootElement.GetProperty("sourceSha256").GetString() ?? "");
    }

    public static ConceptCatalog FromJson(string json, string expectedMapVersion,
        string expectedMapSha256, string? expectedScriptSha256 = null)
    {
        var concepts = JsonSerializer.Deserialize<ConceptCatalog>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidDataException("基础概念资料为空。");
        if (string.IsNullOrWhiteSpace(concepts.MapSha256) ||
            !string.Equals(concepts.MapVersion, expectedMapVersion, StringComparison.Ordinal) ||
            !string.Equals(concepts.MapSha256, expectedMapSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(concepts.ScriptSha256, expectedScriptSha256 ?? SaveProfile.ScriptSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("基础概念资料的地图版本不匹配，请重新构建或更新程序。");
        if (concepts.Sections is null || concepts.Sections.Any(x => x is null) ||
            concepts.Sections.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != concepts.Sections.Count ||
            concepts.Sections.Any(x => x.Id is not ("basics" or "buff" or "drop" or "damage" or "player-guides")) ||
            !new[] { "buff", "drop", "damage" }.All(id => concepts.Sections.Any(x => x.Id == id)))
            throw new InvalidDataException("基础概念资料的分类不完整或重复。");
        foreach (var section in concepts.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Title) || section.Entries is null ||
                section.Entries.Any(x => x is null || string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Title)
                    || x.SourceKind is not ("" or "hero" or "equipment" or "player_guide")
                    || (x.SourceKind == "player_guide") != (section.Id == "player-guides")) ||
                section.Entries.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != section.Entries.Count)
                throw new InvalidDataException("基础概念资料包含空白或重复条目。");
            foreach (var entry in section.Entries)
            {
                if (entry.SourceKind == "player_guide" && (string.IsNullOrWhiteSpace(entry.Author) ||
                    string.IsNullOrWhiteSpace(entry.SourceName) || string.IsNullOrWhiteSpace(entry.SourceVersionNotice) ||
                    entry.SourceStatus != "source_description" || entry.RuntimeTested ||
                    entry.PlayerGuideSource is null || string.IsNullOrWhiteSpace(entry.PlayerGuideSource.SourceId) ||
                    entry.PlayerGuideSource.CurrentMapApplicabilityVerified))
                    throw new InvalidDataException("玩家攻略缺少作者或版本边界，不能作为本版实测资料加载。");
                if (entry.SourceKind == "player_guide" && (entry.DocumentRole is not ("" or "player_reading" or
                        "boss_mechanics_and_tactics" or "illustration_notes" or "preparation" or
                        "navigation_and_summary" or "external_link_index") ||
                        entry.DocumentRole.Length > 0 && string.IsNullOrWhiteSpace(entry.DocumentRoleNotice)))
                    throw new InvalidDataException("玩家攻略缺少有效的文档角色或阅读范围说明。");
                if (entry.Paragraphs is null || entry.Examples is null || entry.Formulas is null || entry.Effects is null ||
                    entry.Paragraphs.Any(x => x is null) || entry.Examples.Any(x => x is null) || entry.Formulas.Any(x => x is null))
                    throw new InvalidDataException("基础概念资料包含无效内容。");
                if (entry.Effects.Any(x => x is null || string.IsNullOrWhiteSpace(x.Id) ||
                        string.IsNullOrWhiteSpace(x.HeroName) || string.IsNullOrWhiteSpace(x.SkillName) ||
                        string.IsNullOrWhiteSpace(x.ValueText) || x.ValueRuns is null ||
                        x.LinkedSourceKinds is null || x.LinkedSourceKinds.Any(kind => kind is not ("hero" or "equipment")) ||
                        x.DimensionStatus is null || x.SourceLinks is null || x.SourceLinks.Any(source => source is null) ||
                        x.SourceNoticeText is null || x.SourceDifferenceText is null || x.StackingTooltip is null ||
                        x.ValueRuns.Any(run => run is null || run.Text is null) ||
                        (x.ValueRuns.Count > 0 && string.Concat(x.ValueRuns.Select(run => run.Text)) != x.ValueText) ||
                        x.SourceKind is not ("hero" or "equipment")) ||
                    entry.Effects.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != entry.Effects.Count)
                    throw new InvalidDataException("增益与减益表包含空白来源、数值或重复条目。");
                if (entry.Table is { } table && (table.Columns is null || table.Rows is null ||
                    table.Columns.Count == 0 || table.Columns.Any(string.IsNullOrWhiteSpace) ||
                    table.Rows.Any(row => row is null || row.Count != table.Columns.Count || row.Any(cell => cell is null))))
                    throw new InvalidDataException("基础概念表格的列与内容不一致。");
            }
        }
        var allEntries = concepts.Sections.SelectMany(section => section.Entries).ToArray();
        if (allEntries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != allEntries.Length)
            throw new InvalidDataException("基础概念资料包含跨分类重复条目。");
        if (concepts.GuideSources is null || concepts.GuideSources.Any(card => card is null ||
                string.IsNullOrWhiteSpace(card.SourceId) || string.IsNullOrWhiteSpace(card.Title) ||
                string.IsNullOrWhiteSpace(card.Author) || string.IsNullOrWhiteSpace(card.Introduction) ||
                !ConceptGuideSourceCard.TrySourceUri(card.Url, out _) ||
                !allEntries.Any(entry => entry.PlayerGuideSource?.SourceId == card.SourceId)) ||
            concepts.GuideSources.Select(card => card.SourceId).Distinct(StringComparer.Ordinal).Count() != concepts.GuideSources.Count)
            throw new InvalidDataException("攻略来源清单包含重复来源、缺少简介或无效原文链接。");
        if (concepts.CalculatorProfiles is null || concepts.CalculatorProfiles.Any(profile => profile is null) ||
            concepts.CalculatorProfiles.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count() != concepts.CalculatorProfiles.Count)
            throw new InvalidDataException("掉落计算资料包含空白或重复首领。");
        if (concepts.Sections.SelectMany(section => section.Entries).Any(entry => entry.Id == "drop-multiple-loot"))
        {
            if (concepts.CalculatorProfiles.Count != 5)
                throw new InvalidDataException("多件掉落计算资料不完整。");
            try
            {
                foreach (var profile in concepts.CalculatorProfiles) DropProbabilityCalculator.ValidateProfile(profile);
            }
            catch (ArgumentException error) { throw new InvalidDataException("掉落计算资料不完整或需要更新。", error); }
        }
        return concepts;
    }
}

public sealed class ConceptSection
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public List<ConceptEntry> Entries { get; init; } = [];
}

public sealed class ConceptGuideSourceCard
{
    public string SourceId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string? SourceMapVersion { get; init; }
    public string Introduction { get; init; } = "";
    public string Url { get; init; } = "";

    public static bool TrySourceUri(string? value, out Uri? uri)
    {
        uri = null;
        return !string.IsNullOrWhiteSpace(value) && value.Length <= 4096 && !value.Any(char.IsControl)
            && Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.Length > 0 && uri.UserInfo.Length == 0;
    }
}

public sealed class ConceptEntry
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public string LeadFormula { get; init; } = "";
    public string SearchTags { get; init; } = "";
    public string SourceKind { get; init; } = "";
    public string Author { get; init; } = "";
    public string AuthorDisplayName { get; init; } = "";
    public string DocumentRole { get; init; } = "";
    public string DocumentRoleNotice { get; init; } = "";
    public string SourceName { get; init; } = "";
    public string? SourceMapVersion { get; init; }
    public string SourceStatus { get; init; } = "";
    public string SourceVersionNotice { get; init; } = "";
    public bool RuntimeTested { get; init; }
    public ConceptPlayerGuideSource? PlayerGuideSource { get; init; }
    public List<ConceptParagraph> Paragraphs { get; init; } = [];
    public List<ConceptExample> Examples { get; init; } = [];
    public List<ConceptFormula> Formulas { get; init; } = [];
    public ConceptTable? Table { get; init; }
    public List<ConceptEffect> Effects { get; init; } = [];
}

public sealed class ConceptPlayerGuideSource
{
    public string CollectionId { get; init; } = "";
    public string SourceId { get; init; } = "";
    public string Path { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string SourceIndex { get; init; } = "";
    public bool CurrentMapApplicabilityVerified { get; init; }
}

public sealed class ConceptEffect
{
    public string Id { get; init; } = "";
    public string HeroName { get; init; } = "";
    public string HeroIcon { get; init; } = "";
    public string SkillName { get; init; } = "";
    public string SkillIcon { get; init; } = "";
    public string Key { get; init; } = "";
    public string ValueText { get; init; } = "";
    public List<GuideTextRun> ValueRuns { get; init; } = [];
    public string ScalingText { get; init; } = "";
    public string ScalingKind { get; init; } = "";
    public List<string> ScalingAttributes { get; init; } = [];
    public string SourceKind { get; init; } = "hero";
    public string ItemId { get; init; } = "";
    public string EquipmentFamilyId { get; init; } = "";
    public string EquipmentFamilyName { get; init; } = "";
    public int EquipmentFamilyRank { get; init; }
    public string SourceStatus { get; init; } = "";
    public string SourceNoticeText { get; init; } = "";
    public string SourceDifferenceText { get; init; } = "";
    public List<string> LinkedSourceKinds { get; init; } = [];
    public Dictionary<string, string> DimensionStatus { get; init; } = [];
    public List<ConceptEffectSource> SourceLinks { get; init; } = [];
    public string StackingTooltip { get; init; } = "";
    public string TargetText { get; init; } = "";
    public string ApplicationText { get; init; } = "";
    public string StackingText { get; init; } = "";
    public string SearchTags { get; init; } = "";
    public string StackingDisplayText => string.IsNullOrWhiteSpace(StackingText) || StackingText.Trim() == "—"
        ? "未确认" : StackingText;
    public string SourceDisplayText => ScalingKind == "profession-attribute" ? "" : SourceStatus switch
    {
        "reference_description" => "参考说明",
        "source_description" => string.IsNullOrWhiteSpace(SourceDifferenceText) ? "" : "来源有差异",
        "conflict" => "来源有差异",
        "pending" => "待补证",
        _ => string.IsNullOrWhiteSpace(SourceDifferenceText) ? "" : "来源有差异"
    };
    public string SourceTooltip => ScalingKind == "profession-attribute" ? "" : string.Join("\n", new[]
        { SourceStatus == "reference_description" ? SourceNoticeText : "", SourceDifferenceText }
        .Where(text => !string.IsNullOrWhiteSpace(text)));
    public string LinkedEquipmentText => SourceKind == "hero"
        ? string.Join("、", SourceLinks.Where(source => source.SourceKind == "equipment")
            .Select(source => source.HeroName).Distinct()) : "";
}

public sealed class ConceptEffectSource
{
    public string SourceKind { get; init; } = "";
    public string HeroName { get; init; } = "";
    public string SkillName { get; init; } = "";
    public string ItemId { get; init; } = "";
}

public sealed class ConceptParagraph
{
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
}

public sealed class ConceptExample
{
    public string InputsText { get; init; } = "";
    public string ResultText { get; init; } = "";
    public string ConditionsText { get; init; } = "";
}

public sealed class ConceptFormula
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Expression { get; init; } = "";
    public string VariablesText { get; init; } = "";
    public string ConditionsText { get; init; } = "";
}

public sealed class ConceptTable
{
    public List<string> Columns { get; init; } = [];
    public List<List<string>> Rows { get; init; } = [];
}
