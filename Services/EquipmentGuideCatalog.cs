using System.IO;
using System.Text.Json;

namespace Grimmcair.Desktop.Services;

/// <summary>Published equipment explanations for the application's exact map.</summary>
public sealed class EquipmentGuideCatalog
{
    public string MapVersion { get; init; } = "";
    public string MapSha256 { get; init; } = "";
    public string ScriptSha256 { get; init; } = "";
    public List<EquipmentGuideItem> Items { get; init; } = [];

    public static EquipmentGuideCatalog Load()
    {
        using var catalog = JsonDocument.Parse(ResourcePack.ReadText("catalog.json"));
        return FromJson(ResourcePack.ReadText("equipment-guide.json"),
            catalog.RootElement.GetProperty("mapVersion").GetString() ?? "",
            catalog.RootElement.GetProperty("sourceSha256").GetString() ?? "");
    }

    public static EquipmentGuideCatalog FromJson(string json, string expectedMapVersion,
        string expectedMapSha256, string? expectedScriptSha256 = null)
    {
        var guide = JsonSerializer.Deserialize<EquipmentGuideCatalog>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("装备说明为空。");
        if (string.IsNullOrWhiteSpace(guide.MapSha256) ||
            !string.Equals(guide.MapVersion, expectedMapVersion, StringComparison.Ordinal) ||
            !string.Equals(guide.MapSha256, expectedMapSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(guide.ScriptSha256, expectedScriptSha256 ?? SaveProfile.ScriptSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("装备说明的地图版本不匹配，请重新构建或更新程序。");
        if (guide.Items is null || guide.Items.Count == 0 || guide.Items.Any(item => item is null ||
                string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name) ||
                item.Sections is null || item.Sections.Count == 0 || item.Sections.Any(section => section is null ||
                    string.IsNullOrWhiteSpace(section.Title) || string.IsNullOrWhiteSpace(section.Text))) ||
            guide.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != guide.Items.Count)
            throw new InvalidDataException("装备说明包含空白或重复内容。");
        return guide;
    }
}

public sealed class EquipmentGuideItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public List<ConceptParagraph> Sections { get; init; } = [];
}
