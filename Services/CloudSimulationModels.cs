using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace Grimmcair.Desktop.Services;

public sealed class CloudSimulationState
{
    public int FormatVersion { get; set; } = 1;
    public string Marker { get; set; } = "Grimmcair.LocalCloudSimulation";
    public bool IsSimulation { get; set; } = true;
    public List<CloudAccount> Accounts { get; set; } = [];
    public List<CloudSave> Saves { get; set; } = [];
    public List<CloudSnapshot> Snapshots { get; set; } = [];
}

public sealed class CloudAccount
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int BadgeLevel { get; set; } = 1;
    public string CloudSharingNoticeVersion { get; set; } = "";
    [JsonIgnore] public int Capacity => 24 + BadgeLevel * 2;
}

public class CloudSave
{
    public string Id { get; set; } = "";
    public string OwnerId { get; set; } = "";
    // Unique within the exact owner/account/profession role; case-insensitive display comparison only.
    public string Name { get; set; } = "";
    // Preserve the original account name. Case folding could invalidate a load code.
    public string GameAccount { get; set; } = "";
    public string Profession { get; set; } = "";
    public bool IsMain { get; set; }
    // A local picker preference, independent of cloud content and its revision.
    public bool IsFavorite { get; set; }
    // Local opt-out: retain history/media/targets but do not ingest or synchronize this branch.
    public bool IsIgnored { get; set; }
    public List<string> IgnoredSourceKeys { get; set; } = [];
    public bool SyncEnabled { get; set; }
    public bool IsDemo { get; set; }
    public string MapVersion { get; set; } = "";
    public string SourceHash { get; set; } = "";
    // Last actual tracked TXT version accepted, independent of current cloud or
    // restored content. An unchanged source must not overwrite that content.
    public string AcceptedSourceHash { get; set; } = "";
    // Current private local history head, committed with this state file.
    public string LocalHistoryId { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public bool SourceDetached { get; set; }
    public JsonObject? ParsedResult { get; set; }
    public List<string> LoadCodes { get; set; } = [];
    public List<CloudInventoryItem> Inventory { get; set; } = [];
    public List<CloudGoal> Targets { get; set; } = [];
    public long Revision { get; set; } = 1;
    public string LastSyncStatus { get; set; } = "未启用";
    public string LastSyncError { get; set; } = "";
    public DateTimeOffset? LastSyncAt { get; set; }
}

public sealed class CloudSnapshot : CloudSave
{
    public string SaveId { get; set; } = "";
    public DateTimeOffset SyncedAt { get; set; }
}

public sealed class CloudImportSave
{
    public string Name { get; set; } = "";
    public string GameAccount { get; set; } = "";
    public string Profession { get; set; } = "";
    public string MapVersion { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public JsonObject? ParsedResult { get; set; }
    public List<string> LoadCodes { get; set; } = [];
    public List<CloudInventoryItem> Inventory { get; set; } = [];
}

public sealed class CloudInventoryItem
{
    public string ItemId { get; set; } = "";
    public string ItemName { get; set; } = "";
    public int Quantity { get; set; }
}

public sealed class CloudGoal
{
    public string ItemId { get; set; } = "";
    public string ItemName { get; set; } = "";
    public int Quantity { get; set; } = 1;
}

public sealed class CloudRecipe
{
    public string ItemId { get; set; } = "";
    public string ItemName { get; set; } = "";
    public List<CloudRecipeMaterial> Materials { get; set; } = [];
}

public sealed class CloudRecipeMaterial
{
    public string Stage { get; set; } = "";
    public string MaterialId { get; set; } = "";
    public string MaterialName { get; set; } = "";
    public int Quantity { get; set; }
}

public sealed class CloudNeedsReport
{
    public List<CloudMaterialNeed> Needs { get; set; } = [];
    public List<string> PendingTargets { get; set; } = [];
    [JsonIgnore] public bool IsComplete => PendingTargets.Count == 0;
}

public sealed class CloudMaterialNeed
{
    public string Stage { get; set; } = "";
    public string MaterialId { get; set; } = "";
    public string MaterialName { get; set; } = "";
    public int Required { get; set; }
    public int Owned { get; set; }
    public int Missing { get; set; }
}

// Deliberately contains no load codes, source path, source hash or raw save inventory.
public sealed class CloudSyncedNeed
{
    public string OwnerId { get; set; } = "";
    public string OwnerName { get; set; } = "";
    public string GameAccount { get; set; } = "";
    public string Profession { get; set; } = "";
    public string SaveName { get; set; } = "";
    public DateTimeOffset SyncedAt { get; set; }
    public List<CloudGoal> Targets { get; set; } = [];
    public CloudNeedsReport Report { get; set; } = new();
}
