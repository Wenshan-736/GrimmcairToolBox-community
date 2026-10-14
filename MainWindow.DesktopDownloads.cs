using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop;

public partial class MainWindow
{
    private CloudSave CurrentLocalSave(string localId) => _simulation?.FindSave(CloudSimulation.PreviewOwnerId, localId)
        ?? throw new InvalidOperationException("本地存档已变化，未应用云端更新。");

    private async Task TakeRemoteUpdateAsync(string localId, bool keepAsConflict)
    {
        var sync = _desktopSync ?? throw new InvalidOperationException("云同步尚未就绪。");
        var local = CurrentLocalSave(localId);
        var fingerprint = sync.GetFingerprint(local);
        var listedCloud = RemoteSave(localId)?.DeepClone().AsObject()
            ?? throw new DesktopCloudConflictException("对应云档已变化，请刷新后处理。");
        var listedCharacter = RemoteCharacter(local)?.DeepClone().AsObject()
            ?? throw new DesktopCloudConflictException("对应云端角色已变化，请刷新后处理。");
        var content = await SendDesktopAsync("GET", $"/api/desktop/cloud-saves/{Guid.Parse(sync.GetBinding(localId).SaveId):D}/content",
            null, _desktopLifetime.Token);
        await ReadDesktopCloudAsync();
        var current = CurrentLocalSave(localId);
        var localChangedDuringRead = sync.GetFingerprint(current) != fingerprint;
        var snapshot = sync.ValidateDownloadedSnapshot(current, listedCloud, listedCharacter, content,
            RemoteSave(localId) ?? throw new DesktopCloudConflictException("读取期间云档已删除，未替换本地存档。"),
            RemoteCharacter(current) ?? throw new DesktopCloudConflictException("读取期间云端角色已变化。"),
            allowConflict: keepAsConflict || localChangedDuringRead);
        var import = await DecodeDesktopSnapshotAsync(snapshot);
        if (_closing || _closed || !DesktopFeaturesAllowed || _desktopSync != sync)
            throw new OperationCanceledException("同步已暂停，本地存档保持不变。");
        current = CurrentLocalSave(localId);
        if (keepAsConflict)
        {
            _simulation!.RecordRemoteHistoryCandidate(CloudSimulation.PreviewOwnerId, localId, import, snapshot.Goals, snapshot.IsMain);
            return;
        }
        if (localChangedDuringRead || sync.GetFingerprint(current) != fingerprint)
        {
            _simulation!.RecordRemoteHistoryCandidate(CloudSimulation.PreviewOwnerId, localId, import, snapshot.Goals, snapshot.IsMain);
            throw new DesktopCloudConflictException("下载期间本地存档也有更新，双方版本已保留在本地历史中。");
        }
        sync.ValidateDownloadedSnapshotForApply(current, snapshot);
        // This updates the existing library branch. The source TXT remains read-only.
        var applied = _simulation!.ApplyRemoteSnapshot(CloudSimulation.PreviewOwnerId, localId, import,
            snapshot.LocalMainAsCloudSecondary ? null : snapshot.Goals);
        sync.AcceptDownloadedSnapshot(applied, snapshot);
        RefreshSimulationContext(false);
        if ((SaveList.SelectedItem as SavedDocument)?.SimulationSaveId == localId)
            ShowSelectedSave(_saves.Single(document => document.SimulationSaveId == localId));
        StatusText.Text = "已取回云端更新，旧版本保留在本地历史中。";
    }

    private async Task<CloudImportSave> DecodeDesktopSnapshotAsync(DesktopCloudDownloadedSnapshot snapshot)
    {
        var result = await Task.Run(() => new SaveDecoder().ParseBytesForImport(
            Encoding.UTF8.GetBytes(string.Join('\n', snapshot.Codes)), "云端存档", snapshot.GameAccount));
        var profession = result["hero"]?["name"]?.ToString() ?? "";
        if (result["kind"]?.ToString() != "character" || profession != RemoteProfessionName(snapshot.ProfessionId)
            || result["map_profile"]?["script_sha256"]?.ToString() != SaveProfile.ScriptSha256)
            throw new InvalidDataException("云存档的角色或地图与代码不一致，未修改本地存档。");
        var inventory = new List<CloudInventoryItem>();
        foreach (var group in new[] { "inventory", "bag", "storage" })
            if (result["inventories"]?[group] is JsonArray items)
                inventory.AddRange(items.Select(item => new CloudInventoryItem { ItemId = item!["rawcode"]!.ToString(),
                    ItemName = item["name"]?.ToString() ?? "未命名物品", Quantity = item["quantity"]?.GetValue<int>() ?? 1 }));
        return new CloudImportSave
        {
            Name = snapshot.SaveName, GameAccount = snapshot.GameAccount, Profession = profession,
            MapVersion = result["map_profile"]!["name"]!.ToString(), SourceHash = result["source"]!["sha256"]!.ToString(),
            ParsedResult = result, LoadCodes = snapshot.Codes, Inventory = inventory
        };
    }
}
