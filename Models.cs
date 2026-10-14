using System.IO;
using System.Text.Json.Nodes;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop;

public sealed class CatalogItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public string Subcategory { get; init; } = "";
    public string DisplayId { get; init; } = "";
    public string EffectsText { get; init; } = "";
    public string EffectsSource { get; init; } = "";
    public List<GuideTextRun> DescriptionRuns { get; init; } = [];
    public List<GuideTextRun> EffectsRuns { get; init; } = [];
    public string Stage { get; init; } = "";
    public int StageRank { get; init; }
    public string StageSource { get; init; } = "";
    public string CategoryLabel => string.IsNullOrEmpty(Subcategory) ? Category : Subcategory;
    public string StageNote => string.IsNullOrEmpty(Stage)
        ? "尚无阶段标注，排在已知阶段之后。" : $"物品阶段：{Stage}";
    public string CatalogLabel => string.IsNullOrEmpty(Stage) ? CategoryLabel : $"{CategoryLabel} · {Stage}";
    public string Description { get; init; } = "";
    public string ShortDescription { get; init; } = "";
    public string IconPath { get; init; } = "";
    public string SearchText { get; init; } = "";
    public List<CatalogRecipe> Recipes { get; init; } = [];
    public List<CatalogRecipePlan> RecipePlans { get; init; } = [];
    public List<CatalogUse> UsedIn { get; init; } = [];
}

public sealed class SavedDocument : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public void NotifyDisplayChanged() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(null));
    public required JsonObject Result { get; set; }
    public required string SourcePath { get; set; }
    public string GameAccount { get; set; } = "";
    public string SaveName { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public bool IsMain { get; set; }
    public bool IsFavorite { get; set; }
    public bool SyncEnabled { get; set; }
    public bool IsDemo { get; set; }
    public string ChangeSummary { get; set; } = "暂无物品变化";
    public string SelectionColor => IsMain ? "#347344" : "#356A9C";
    public string RoleName => Result["kind"]?.ToString() == "account" ? "账号徽章" : Result["hero"]?["name"]?.ToString() ?? "职业待识别";
    public string PickerLabel => $"{GameAccount} · {RoleName}";
    public string PickerSubtitle => $"{(IsMain ? "主档" : Result["kind"]?.ToString() == "account" ? "账号共有" : "副档")} · {SaveName}";
    public string? ImportUsernameFallback { get; init; }
    public string? SimulationSaveId { get; set; }
    public string SimulationBadgeText { get; set; } = "";
    public string Hash => Result["source"]?["sha256"]?.ToString() ?? "";
    public string DisplayName => Result["hero"] is JsonObject h
        ? h["level"] is null ? $"{h["name"]}" : $"{h["name"]} · {h["level"]} 级" : "账号徽章与收藏";
    public string Subtitle => $"{Path.GetFileName(SourcePath)} · 版本 {Result["internal_version"]}";
}

public sealed class SaveItemRow
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Group { get; init; } = "";
    public string QuantityLabel { get; init; } = "";
    public string State { get; init; } = "";
    public string IconPath { get; init; } = "";
}
