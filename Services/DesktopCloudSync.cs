using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Grimmcair.Desktop.Services;

public sealed class DesktopCloudConflictException(string message) : InvalidOperationException(message);

public enum DesktopCloudReconciliationAction { None, Upload, Download, Conflict, Disabled, Stopped, Deleted, RecoverPending }

public sealed record DesktopCloudReconciliationDecision(DesktopCloudReconciliationAction Action, string Message,
    bool LocalChanged = false, bool RemoteChanged = false);

/// <summary>Validated private read, bound to the creating authenticated account and the unchanged local role.</summary>
public sealed class DesktopCloudDownloadedSnapshot
{
    internal DesktopCloudDownloadedSnapshot() { }
    internal DesktopCloudAccountState Account { get; init; } = null!;
    internal string BeforeLocalFingerprint { get; init; } = "";
    internal string ContentFingerprint { get; init; } = "";
    internal string GoalFingerprint { get; init; } = "";
    internal bool CanAutoApply { get; init; }
    internal bool ObservedPublicationWithdrawal { get; init; }
    internal string BeforeBindingSaveId { get; init; } = "";
    internal List<string> PrivateCodes { get; init; } = [];
    internal List<CloudGoal> PrivateGoals { get; init; } = [];
    public string LocalSaveId { get; internal init; } = "";
    public string SaveId { get; internal init; } = "";
    public string CharacterId { get; internal init; } = "";
    public string GameAccount { get; internal init; } = "";
    public string ProfessionId { get; internal init; } = "";
    public string SaveName { get; internal init; } = "";
    public string GoalsRevision { get; internal init; } = "";
    public long CloudRevision { get; internal init; }
    public long ClientRevision { get; internal init; }
    public long CharacterRevision { get; internal init; }
    public bool IsMain { get; internal init; }
    public bool IsExplicitLink { get; internal init; }
    public bool IsBindingReplacement { get; internal init; }
    public bool LocalMainAsCloudSecondary { get; internal init; }
    public bool SyncEnabled { get; internal init; } = true;
    public DateTimeOffset SyncedAt { get; internal init; }
    // Return copies so callers cannot change the validated payload before acceptance.
    public List<string> Codes => PrivateCodes.ToList();
    public List<CloudGoal> Goals => PrivateGoals.Select(x => new CloudGoal { ItemId = x.ItemId, ItemName = x.ItemName, Quantity = x.Quantity }).ToList();
    public override string ToString() => "Validated cloud snapshot (private content omitted)";
}

/// <summary>Builds private uploads and journals each operation before transport. Never modifies CloudSave.</summary>
public sealed class DesktopCloudSync
{
    private readonly MaterialPlanner planner;
    private readonly DesktopCloudStateStore store;
    private readonly Dictionary<string, string> professionIds = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim execution = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public DesktopCloudAccountState Account { get; }

    public DesktopCloudSync(MaterialPlanner planner, string guideJson, string referenceJson,
        DesktopCloudStateStore stateStore, string baseUrl, long userId)
    {
        this.planner = planner; store = stateStore;
        var guide = Parse(guideJson); var reference = Parse(referenceJson);
        foreach (var document in new[] { guide, reference })
            if (Text(document, "mapVersion") != planner.MapVersion || Text(document, "mapSha256") != planner.MapSha256)
                throw new InvalidDataException("服务器与本地地图资料不一致，未上传。");
        if (Text(reference, "recipeSetSha256") != planner.RecipeSetSha256)
            throw new InvalidDataException("服务器与本地配方资料不一致，未上传。");
        var referenceProfessions = Array(reference, "professions").Select(Object).ToDictionary(x => Text(x, "id"), x => Text(x, "name"), StringComparer.Ordinal);
        foreach (var hero in Array(guide, "heroes").Select(Object))
        {
            var id = Text(hero, "id"); var name = Text(hero, "name");
            if (!referenceProfessions.TryGetValue(id, out var serverName) || serverName != name || !professionIds.TryAdd(name, id))
                throw new InvalidDataException("服务器与本地职业字典不一致，未猜测职业编号。");
        }
        if (professionIds.Count == 0 || professionIds.Count != referenceProfessions.Count)
            throw new InvalidDataException("服务器与本地职业字典不完整。");
        Account = store.ForAccount(baseUrl, userId);
    }

    public DesktopCloudBinding GetBinding(string localSaveId)
    {
        if (string.IsNullOrEmpty(localSaveId)) throw new ArgumentException("本地存档标识为空。", nameof(localSaveId));
        var binding = Account.Bindings.SingleOrDefault(x => x.LocalSaveId == localSaveId);
        if (binding is not null) return binding;
        // Server save IDs are globally unique, including across authenticated accounts.
        binding = new() { LocalSaveId = localSaveId, SaveId = Guid.NewGuid().ToString("D") };
        Account.Bindings.Add(binding); store.Save();
        return binding;
    }
    public void Save() => store.Save();
    public void SetEnabled(string localSaveId, bool enabled) => SetEnabled(GetBinding(localSaveId), enabled);
    public void SetEnabled(DesktopCloudBinding binding, bool enabled)
    {
        RequireBinding(binding);
        if (binding.Deleted && enabled) throw new InvalidOperationException("这个云档已删除，请先读取云列表并重新建立关联。本地存档与历史仍保留。");
        binding.Enabled = enabled;
        if (enabled) binding.StopRequestedByIgnore = false;
        if (!Account.PendingOperations.Any(x => x.LocalSaveId == binding.LocalSaveId))
            binding.Status = enabled ? "pending" : "disabled";
        store.Save();
    }
    public void RecordFailure(string localSaveId, string message)
    {
        var binding = GetBinding(localSaveId);
        binding.LastError = message; binding.Status = "failed"; store.Save();
    }

    public void StopForIgnoredSave(string localSaveId) => store.StopLocalSaveSync(localSaveId);

    /// <summary>Recover evidence only; never resend a content/main/delete request after local opt-out.</summary>
    public async Task<bool> RecoverIgnoredOperationAsync(DesktopCloudOperation operation,
        Func<string, string, string?, CancellationToken, Task<JsonObject>> sendAsync, CancellationToken cancellation = default)
    {
        await execution.WaitAsync(cancellation);
        try
        {
            ValidateOperation(operation);
            var binding = GetBinding(operation.LocalSaveId);
            if (!binding.StopRequestedByIgnore) throw new InvalidOperationException("这份存档尚未标记停止同步。");
            if (operation.MustQueryReceipt)
            {
                JsonObject receipt;
                try { receipt = await sendAsync("GET", $"/api/desktop/operations/{operation.RequestId}", null, cancellation); }
                catch (DesktopApiException error) when (error.StatusCode == 404 && error.Code == "operation_not_found")
                {
                    // A previous write might still finish late. Retain the journal and retry receipt-only later.
                    binding.LastError = "本机已停止同步；旧请求结果仍待确认，未重发内容。";
                    store.Save();
                    return false;
                }
                ApplyReceipt(operation, receipt);
                if (operation.Kind == "main-switch") CompleteLocalMainTransfer(operation.RequestId);
                return true;
            }
            var before = JsonSerializer.Serialize(Account, Json);
            Account.PendingOperations.Remove(operation);
            operation.Status = "cancelled";
            Account.RetiredFailedOperations.Add(operation);
            try { store.Save(); } catch { RestoreAccount(before); throw; }
            return true;
        }
        finally { execution.Release(); }
    }
    public string ProfessionId(CloudSave save) => professionIds.TryGetValue(save.Profession, out var id) ? id
        : throw new InvalidDataException("存档职业与当前职业字典没有精确对应，未上传。");

    /// <summary>
    /// Find identity in the authenticated account's complete active-save list. This is only a metadata match;
    /// the caller must still perform the stable content read and explicit link validation before applying it.
    /// Raw game identity is exact; display names follow SaveNamePolicy's trimmed, case-insensitive rule.
    /// </summary>
    public JsonObject? FindUniqueCloudSaveByIdentity(CloudSave local, IEnumerable<JsonObject> allCloudSaves)
    {
        ArgumentNullException.ThrowIfNull(allCloudSaves);
        var name = IdentityName(local.Name); var profession = ProfessionId(local);
        Limited(local.GameAccount, 1, 120, "游戏账号");
        var matches = allCloudSaves.Where(cloud => Text(cloud, "gameAccount") == local.GameAccount
            && Text(cloud, "professionId") == profession
            && string.Equals(IdentityName(Text(cloud, "saveName")), name, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var cloud in matches) Uuid(Text(cloud, "saveId"));
        if (matches.Count > 1)
            throw new DesktopCloudConflictException("这个角色和存档名有多个同名云记录，请明确选择要关联的云档；未猜测主档或版本。");
        return matches.SingleOrDefault()?.DeepClone().AsObject();
    }

    /// <summary>
    /// Select a stable new-slot UUID only before any operation has been prepared for the placeholder.
    /// New callers must provide the complete authenticated save list; an existing same-name slot must be linked instead.
    /// No server write or content overwrite is performed here. Two devices preparing the same new identity use one UUID.
    /// </summary>
    public DesktopCloudBinding PrepareNewIdentityBinding(CloudSave local, IEnumerable<JsonObject> allCloudSaves)
    {
        ArgumentNullException.ThrowIfNull(allCloudSaves);
        if (string.IsNullOrEmpty(local.Id)) throw new ArgumentException("本地存档标识为空。", nameof(local));
        var name = IdentityName(local.Name); var profession = ProfessionId(local);
        Limited(local.GameAccount, 1, 120, "游戏账号");
        var binding = Account.Bindings.SingleOrDefault(candidate => candidate.LocalSaveId == local.Id);
        if (binding?.Deleted == true)
            throw new DesktopCloudConflictException("这个本地档的云标识已删除，未按同名身份复活原云档。");
        // An established slot remains usable even if an older client left other same-name historical slots.
        if (binding is not null && HasUploadedBinding(binding)) return binding;
        var reviewBinding = binding ?? new DesktopCloudBinding { LocalSaveId = local.Id };
        _ = EnsureNoPending(reviewBinding, local.GameAccount, profession, acceptRemoteChanges: false);
        var match = FindUniqueCloudSaveByIdentity(local, allCloudSaves);
        if (match is not null)
        {
            if (binding is not null && Uuid(Text(match, "saveId")) == binding.SaveId) return binding;
            throw new DesktopCloudConflictException("已有同游戏账号、职业和存档名的云档，请先关联该云档；未新建重复槽位。");
        }
        // Terminal bodies also prove that a UUID was already committed to a request. Never rewrite their identity.
        if (binding is not null && Account.RetiredFailedOperations.Any(operation => operation.LocalSaveId == local.Id || operation.SaveId == binding.SaveId))
            return binding;
        // An explicitly recreated lifecycle is already assigned. Never regress it to a deleted initial UUID.
        if (binding is not null && binding.PredecessorSaveId.Length > 0) return binding;
        var saveId = InitialIdentitySaveId(local, profession, name);
        if (Account.Bindings.Any(candidate => candidate.LocalSaveId != local.Id && candidate.SaveId == saveId))
            throw new DesktopCloudConflictException("这个存档身份已经关联另一份本地记录，请直接使用该记录；未创建重复绑定。");
        if (Account.RetiredBindings.Any(candidate => candidate.SaveId == saveId))
            throw new DesktopCloudConflictException("这个存档身份已有旧关联记录，请重新读取原云档后明确关联；未复用历史云标识。");
        if (binding?.SaveId == saveId) return binding;
        var previousId = binding?.SaveId;
        if (binding is null) { binding = new DesktopCloudBinding { LocalSaveId = local.Id, SaveId = saveId }; Account.Bindings.Add(binding); }
        else binding.SaveId = saveId;
        try { store.Save(); }
        catch { if (previousId is null) Account.Bindings.Remove(binding); else binding.SaveId = previousId; throw; }
        return binding;
    }

    /// <summary>
    /// Explicit enable/retry only, after the authenticated complete active list and original receipts were read.
    /// Deleted slots receive a new deterministic lifecycle UUID; no old request body or local save is changed.
    /// </summary>
    public DesktopCloudBinding PrepareCloudReenable(CloudSave local, IEnumerable<JsonObject> allCloudSaves,
        IReadOnlySet<string> liveLocalIds, bool confirmedDeletedByServer = false)
    {
        ArgumentNullException.ThrowIfNull(allCloudSaves); ArgumentNullException.ThrowIfNull(liveLocalIds);
        ValidateSave(local);
        if (!liveLocalIds.Contains(local.Id)) throw new DesktopCloudConflictException("这份本地存档已不在当前列表中，请重新选择。");
        var clouds = allCloudSaves.ToList();
        foreach (var cloud in clouds) Uuid(Text(cloud, "saveId"));
        var profession = ProfessionId(local); var name = IdentityName(local.Name);
        var current = Account.Bindings.SingleOrDefault(binding => binding.LocalSaveId == local.Id);
        var initialId = InitialIdentitySaveId(local, profession, name);
        var lineage = IdentityLineage(initialId);
        if (current is not null && (current.Deleted || HasUploadedBinding(current) || current.PredecessorSaveId.Length > 0 || confirmedDeletedByServer)
            && !lineage.Contains(current.SaveId))
            lineage.AddRange(IdentityLineage(current.SaveId).Where(id => !lineage.Contains(id)));
        var orphaned = Account.Bindings.Where(binding => binding.LocalSaveId != local.Id
            && lineage.Contains(binding.SaveId)).ToList();
        if (orphaned.Any(binding => liveLocalIds.Contains(binding.LocalSaveId)))
            throw new DesktopCloudConflictException("这个存档身份已经关联另一份本地记录，请直接使用该记录；未创建重复绑定。");
        var retired = ReviewReenableOperations(local, orphaned.Select(binding => binding.LocalSaveId));
        var sameIdentity = FindUniqueCloudSaveByIdentity(local, clouds);
        var currentRemote = current is null ? null : clouds.SingleOrDefault(cloud => Text(cloud, "saveId") == current.SaveId);
        if (sameIdentity is not null && (current is null || Text(sameIdentity, "saveId") != current.SaveId))
            throw new DesktopCloudConflictException("已有同游戏账号、职业和存档名的云档，请先关联该云档；未新建重复槽位。");
        if (currentRemote is not null)
        {
            if (Text(currentRemote, "gameAccount") != local.GameAccount || Text(currentRemote, "professionId") != profession)
                throw new InvalidDataException("云档身份与当前本地绑定不一致，未重建。");
            if (current!.Deleted || confirmedDeletedByServer)
                throw new DesktopCloudConflictException("云列表仍有这份存档，请重新读取后关联；未根据过期删除状态重建。");
            // A renamed or stopped existing slot remains the same lifecycle.
            if (retired.Count == 0) return current;
            var beforeExisting = JsonSerializer.Serialize(Account, Json);
            RetireReenableOperations(retired);
            try { store.Save(); } catch { RestoreAccount(beforeExisting); throw; }
            return current;
        }
        var knownLineage = Account.Bindings.Concat(Account.RetiredBindings)
            .Where(binding => lineage.Contains(binding.SaveId)).ToList();
        var predecessor = current is not null && (current.Deleted || HasUploadedBinding(current) || confirmedDeletedByServer)
            ? IdentityLineage(current.SaveId)[^1] : knownLineage.LastOrDefault(binding => binding.SaveId == lineage.Last()
                && (binding.Deleted || HasUploadedBinding(binding) || Account.RetiredBindings.Contains(binding)))?.SaveId;
        // A confirmed server tombstone is sufficient even when all local history had been removed.
        if (confirmedDeletedByServer && predecessor is null) predecessor = current?.SaveId ?? initialId;
        var nextId = predecessor is null ? current?.SaveId ?? initialId : RecreatedIdentitySaveId(predecessor);
        if (predecessor is null && current is not null && !HasUploadedBinding(current)
            && current.PredecessorSaveId.Length == 0
            && !Account.RetiredFailedOperations.Any(operation => operation.LocalSaveId == local.Id || operation.SaveId == current.SaveId))
            nextId = initialId;
        if (clouds.Any(cloud => Text(cloud, "saveId") == nextId) && currentRemote is null)
            throw new DesktopCloudConflictException("这份新云槽已出现在云列表中，请先读取并关联；未覆盖现存内容。");
        if (Account.Bindings.Any(binding => binding.LocalSaveId != local.Id && binding.SaveId == nextId && !orphaned.Contains(binding)))
            throw new DesktopCloudConflictException("这个新云槽已关联另一份本地记录，请直接使用该记录。");
        var replace = current is null || current.SaveId != nextId;
        if (!replace && orphaned.Count == 0 && retired.Count == 0) return current!;
        var before = JsonSerializer.Serialize(Account, Json);
        var result = replace ? new DesktopCloudBinding { LocalSaveId = local.Id, SaveId = nextId,
            PredecessorSaveId = predecessor ?? "", Enabled = current?.Enabled == true, Status = current?.Enabled == true ? "pending" : "disabled" } : current!;
        foreach (var binding in orphaned.Concat(replace && current is not null ? [current] : []).Distinct())
            ArchiveBinding(binding);
        if (replace) Account.Bindings.Add(result);
        RetireReenableOperations(retired);
        try { store.Save(); } catch { RestoreAccount(before); throw; }
        return result;
    }

    /// <summary>Explicit linking only. An absent local record may release its association, never a live record.</summary>
    public void RetireOrphanedBinding(CloudSave local, string targetSaveId, IReadOnlySet<string> liveLocalIds)
    {
        ArgumentNullException.ThrowIfNull(liveLocalIds); ValidateSave(local); Uuid(targetSaveId);
        if (!liveLocalIds.Contains(local.Id)) throw new DesktopCloudConflictException("这份本地存档已不在当前列表中，请重新选择。");
        var orphan = Account.Bindings.SingleOrDefault(binding => binding.SaveId == targetSaveId && binding.LocalSaveId != local.Id);
        if (orphan is null) return;
        if (liveLocalIds.Contains(orphan.LocalSaveId))
            throw new DesktopCloudConflictException("这个云槽位已经关联另一份本地存档，请直接使用那份存档；未重复创建关联。");
        var retired = ReviewReenableOperations(local, [orphan.LocalSaveId]);
        var before = JsonSerializer.Serialize(Account, Json);
        ArchiveBinding(orphan); RetireReenableOperations(retired);
        try { store.Save(); } catch { RestoreAccount(before); throw; }
    }

    private string InitialIdentitySaveId(CloudSave local, string profession, string name) => StableUuid(JsonSerializer.Serialize(new {
        kind = "grimmcair.desktop-save-identity.v1", websiteUserId = Account.UserId,
        gameAccount = local.GameAccount, professionId = profession, saveName = name.ToUpperInvariant() }, Json));
    private string RecreatedIdentitySaveId(string predecessor) => StableUuid(JsonSerializer.Serialize(new {
        kind = "grimmcair.desktop-save-lifecycle.v1", websiteUserId = Account.UserId, predecessorSaveId = Uuid(predecessor) }, Json));
    private static string StableUuid(string identity)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identity))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x80); bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString("D");
    }
    private List<string> IdentityLineage(string initialId)
    {
        var ids = new List<string> { initialId };
        while (true)
        {
            var descendants = Account.Bindings.Concat(Account.RetiredBindings)
                .Where(binding => binding.PredecessorSaveId == ids[^1]).Select(binding => binding.SaveId).Distinct(StringComparer.Ordinal).ToList();
            if (descendants.Count == 0) return ids;
            if (descendants.Count != 1 || ids.Contains(descendants[0]))
                throw new InvalidDataException("旧云槽生命周期记录冲突，未猜测新的云标识。");
            ids.Add(descendants[0]);
        }
    }
    private List<DesktopCloudOperation> ReviewReenableOperations(CloudSave local, IEnumerable<string> additionalLocalIds)
    {
        var ids = additionalLocalIds.Append(local.Id).ToHashSet(StringComparer.Ordinal); var profession = ProfessionId(local);
        bool Related(DesktopCloudOperation operation) => ids.Contains(operation.LocalSaveId)
            || operation.GameAccount == local.GameAccount && operation.ProfessionId == profession;
        if (Account.CommittedMainTransfers.Any(Related))
            throw new DesktopCloudConflictException("云端主档已经转换，请先完成本地主档与目标的接管。");
        var operations = Account.PendingOperations.Where(Related).ToList();
        if (operations.Any(operation => !IsKnownFailedOperation(operation) || operation.MustQueryReceipt))
            throw new DesktopCloudConflictException("原云操作仍须查询回执，请先恢复原请求；未放弃任何待办或改写正文。");
        return operations;
    }
    private void ArchiveBinding(DesktopCloudBinding binding)
    {
        var archived = JsonSerializer.Deserialize<DesktopCloudBinding>(JsonSerializer.Serialize(binding, Json), Json)!;
        archived.Enabled = false; Account.RetiredBindings.Add(archived); Account.Bindings.Remove(binding);
    }
    private void RetireReenableOperations(IEnumerable<DesktopCloudOperation> operations)
    {
        foreach (var operation in operations)
        {
            Account.PendingOperations.Remove(operation); Account.RetiredFailedOperations.Add(operation);
            var binding = Account.Bindings.SingleOrDefault(candidate => candidate.LocalSaveId == operation.LocalSaveId);
            if (binding is not null) { binding.LastError = ""; binding.Status = binding.Enabled ? "pending" : "disabled"; }
        }
    }

    private static string IdentityName(string name)
    {
        var trimmed = Limited(name.Trim(), 1, SaveNamePolicy.MaximumApiNameLength, "存档名");
        if (string.IsNullOrWhiteSpace(trimmed)) throw new InvalidDataException("存档名不能为空。");
        return trimmed;
    }

    public string GetFingerprint(CloudSave save) => Fingerprint(save, save.IsMain, NormalizeGoals(save.IsMain ? save.Targets : []));
    private string Fingerprint(CloudSave save, bool isMain, IReadOnlyList<CloudGoal> goals) => DesktopCloudStateStore.Hash(
        JsonSerializer.Serialize(new { save.Name, save.GameAccount, professionId = ProfessionId(save),
            isMain, content = ContentFingerprint(save), goals = goals.Select(x => new { x.ItemId, x.Quantity }) }, Json));
    private string GoalFingerprint(IEnumerable<CloudGoal> goals) => DesktopCloudStateStore.Hash(
        JsonSerializer.Serialize(NormalizeGoals(goals).Select(x => new { x.ItemId, x.Quantity }), Json));
    private string ContentFingerprint(CloudSave save) => DesktopCloudStateStore.Hash(JsonSerializer.Serialize(new {
        planner.MapVersion, planner.MapSha256, planner.RecipeSetSha256, codes = save.LoadCodes }, Json));
    private string LocalFingerprintWithName(CloudSave local, string name)
    {
        var projected = CopyForCalculation(local, local.IsMain, NormalizeGoals(local.IsMain ? local.Targets : []));
        projected.Name = name; projected.LoadCodes = local.LoadCodes.ToList();
        return GetFingerprint(projected);
    }

    public DesktopCloudReconciliationDecision GetReconciliationDecision(CloudSave local, JsonObject? cloud, JsonObject? character)
    {
        ValidateSave(local);
        var binding = GetBinding(local.Id);
        if (binding.Deleted) return new(DesktopCloudReconciliationAction.Deleted, "该云档已删除，本地存档与历史仍保留。");
        if (HasPendingRole(local)) return new(DesktopCloudReconciliationAction.RecoverPending, "这个角色仍有待确认操作，请先恢复原回执或处理冲突。");
        if (!binding.Enabled) return new(DesktopCloudReconciliationAction.Disabled, "这个本地档位未开启正式同步。");
        if (cloud is null)
        {
            if (binding.CloudRevision > 0) return new(DesktopCloudReconciliationAction.Deleted, "绑定云档已不在本人最新列表中，自动同步已停止。");
            if (character is not null)
            {
                ValidateCharacter(local, binding, character);
                var main = OptionalText(character, "mainSaveId");
                if (local.IsMain && main != binding.SaveId || !local.IsMain && main == binding.SaveId)
                    return new(DesktopCloudReconciliationAction.Conflict, "云端主档关系不同，请显式处理主档转换。", true, true);
            }
            return new(DesktopCloudReconciliationAction.Upload, "新本地档等待首次上传。", true);
        }
        ValidateRemoteSnapshotIdentity(local, binding, cloud, character);
        if (!Bool(cloud, "syncEnabled")) return new(DesktopCloudReconciliationAction.Stopped, "云端已停止这个档位的同步，本地内容与历史仍保留。");
        var localChanged = GetFingerprint(local) != binding.AcceptedLocalFingerprint;
        var cloudMain = Bool(cloud, "isMain");
        var publicationWithdrawn = IsPublicationWithdrawal(binding, cloud, character!);
        var publicationReenabled = IsPublicationReenable(local, binding, cloud, character!);
        var localMainBoundary = (binding.LocalMainAsCloudSecondary || publicationWithdrawn) && local.IsMain && !cloudMain;
        if (cloudMain != binding.AcceptedIsMain && !publicationWithdrawn && !publicationReenabled || cloudMain != local.IsMain && !localMainBoundary)
            return new(DesktopCloudReconciliationAction.Conflict, "云端主副档关系已变化，请显式处理；未自动迁移本地主副档。", localChanged, true);
        if (binding.AcceptedLocalFingerprint.Length == 0 || binding.AcceptedContentFingerprint.Length == 0)
            return new(DesktopCloudReconciliationAction.Conflict, "缺少这个绑定的成功同步基线，请先明确确认两份存档。", localChanged, true);
        // Older state files have no explicit name baseline. A clean local fingerprint still proves its accepted name.
        var knownName = binding.AcceptedSaveName.Length > 0 ? binding.AcceptedSaveName : !localChanged ? local.Name : null;
        var remoteChanged = Integer(cloud, "clientRevision", 1) != binding.AcceptedClientRevision
            || Text(cloud, "goalsRevision") != binding.AcceptedGoalsRevision
            || knownName is null || Text(cloud, "saveName") != knownName;
        if (localChanged && remoteChanged)
            return new(DesktopCloudReconciliationAction.Conflict, "本地与云端都已变化，保留双方并等待选择，未静默覆盖。", true, true);
        if (remoteChanged) return new(DesktopCloudReconciliationAction.Download, "仅云端变化，可以更新这个绑定的本地副本。", false, true);
        if (localChanged) return new(DesktopCloudReconciliationAction.Upload, "仅本地变化，可以上传这个绑定的最新内容。", true);
        if (local.IsMain && cloudMain && OptionalText(cloud, "needsStatus") == "partial"
            && MaterialNeedsExporter.Create(planner, local).IsExact)
            return new(DesktopCloudReconciliationAction.Upload, "本地已算出完整材料缺口，将补全这份云档的材料清单。", true);
        return new(DesktopCloudReconciliationAction.None, "当前本地和云端已一致。");
    }

    /// <summary>Absorb metadata-only or other-slot character revisions without changing the accepted content baseline.</summary>
    public void AcceptObservedMetadata(CloudSave local, JsonObject cloud, JsonObject character)
    {
        var decision = GetReconciliationDecision(local, cloud, character);
        if (decision.Action is not (DesktopCloudReconciliationAction.None or DesktopCloudReconciliationAction.Upload))
            throw new DesktopCloudConflictException("云端内容尚未接管，不能只推进版本标记。");
        var binding = GetBinding(local.Id);
        var previous = JsonSerializer.Serialize(Account, Json);
        if (IsPublicationWithdrawal(binding, cloud, character) || IsPublicationReenable(local, binding, cloud, character))
        { binding.AcceptedIsMain = Bool(cloud, "isMain"); binding.LocalMainAsCloudSecondary = local.IsMain && !binding.AcceptedIsMain; }
        binding.CloudRevision = Integer(cloud, "cloudRevision", 1);
        Account.CharacterRevisions[Uuid(Text(character, "characterId"))] = Integer(character, "characterRevision", 1);
        if (binding.AcceptedSaveName.Length == 0) binding.AcceptedSaveName = Text(cloud, "saveName");
        try { store.Save(); } catch { RestoreAccount(previous); throw; }
    }

    /// <summary>Observe withdrawal or re-enabling of this same local main; never accept code/goal content or change local roles.</summary>
    public bool AcceptObservedPublicationState(CloudSave local, JsonObject cloud, JsonObject character)
    {
        ValidateSave(local);
        var binding = Account.Bindings.SingleOrDefault(candidate => candidate.LocalSaveId == local.Id)
            ?? throw new DesktopCloudConflictException("尚无该云档的本机关联，未吸收主档状态。");
        if (binding.Deleted || !binding.Enabled || HasPendingRole(local))
            throw new DesktopCloudConflictException("同步未开启或角色仍有待办，未吸收云主档状态。");
        ValidateRemoteSnapshotIdentity(local, binding, cloud, character);
        if (!Bool(cloud, "syncEnabled")) throw new DesktopCloudConflictException("云档已停止同步，未继续吸收云主档状态。");
        if (!IsPublicationWithdrawal(binding, cloud, character) && !IsPublicationReenable(local, binding, cloud, character)) return false;
        var previous = JsonSerializer.Serialize(Account, Json);
        binding.AcceptedIsMain = Bool(cloud, "isMain"); binding.LocalMainAsCloudSecondary = local.IsMain && !binding.AcceptedIsMain;
        try { store.Save(); } catch { RestoreAccount(previous); throw; }
        return true;
    }

    private static bool IsPublicationWithdrawal(DesktopCloudBinding binding, JsonObject cloud, JsonObject character) =>
        binding.AcceptedIsMain && !Bool(cloud, "isMain") && OptionalText(character, "mainSaveId") is null;

    private static bool IsPublicationReenable(CloudSave local, DesktopCloudBinding binding, JsonObject cloud, JsonObject character) =>
        !binding.AcceptedIsMain && binding.LocalMainAsCloudSecondary && local.IsMain && Bool(cloud, "isMain")
        && OptionalText(character, "mainSaveId") == binding.SaveId;

    public void MarkRemoteDeleted(string localSaveId)
    {
        var binding = GetBinding(localSaveId);
        if (Account.PendingOperations.Any(x => x.LocalSaveId == localSaveId))
            throw new DesktopCloudConflictException("仍有待确认的原操作，未将云档记为删除。");
        var previous = JsonSerializer.Serialize(Account, Json);
        binding.Deleted = true; binding.Enabled = false; binding.AcceptedSyncEnabled = false;
        binding.AcceptedIsMain = false; binding.Status = "deleted";
        binding.LastError = "云档已删除，本地存档和历史保留。";
        try { store.Save(); } catch { RestoreAccount(previous); throw; }
    }

    public DesktopCloudDownloadedSnapshot ValidateDownloadedSnapshot(CloudSave local, JsonObject listedCloud,
        JsonObject listedCharacter, JsonObject content, JsonObject latestCloud, JsonObject latestCharacter, bool allowConflict = false)
    {
        var decision = GetReconciliationDecision(local, listedCloud, listedCharacter);
        if (decision.Action != DesktopCloudReconciliationAction.Download
            && !(allowConflict && (decision.Action == DesktopCloudReconciliationAction.Conflict
                || decision.Action == DesktopCloudReconciliationAction.RecoverPending && CanReadKnownConflictCandidate(local))))
            throw new DesktopCloudConflictException("当前状态不允许自动接管云档，请先处理本地变化或待办。");
        var binding = GetBinding(local.Id);
        return ReadStableSnapshot(local, binding, listedCloud, listedCharacter, content, latestCloud, latestCharacter,
            canAutoApply: decision.Action == DesktopCloudReconciliationAction.Download);
    }

    private DesktopCloudDownloadedSnapshot ReadStableSnapshot(CloudSave local, DesktopCloudBinding binding, JsonObject listedCloud,
        JsonObject listedCharacter, JsonObject content, JsonObject latestCloud, JsonObject latestCharacter,
        bool canAutoApply, bool explicitLink = false, string beforeBindingSaveId = "", bool bindingReplacement = false)
    {
        ValidateRemoteSnapshotIdentity(local, binding, listedCloud, listedCharacter);
        ValidateRemoteSnapshotIdentity(local, binding, content, listedCharacter);
        ValidateRemoteSnapshotIdentity(local, binding, latestCloud, latestCharacter);
        RequireSameRemoteRead(listedCloud, content);
        RequireSameRemoteRead(listedCloud, latestCloud);
        RequireSameCharacterRead(listedCharacter, latestCharacter);
        if (Text(content, "importAs") != "secondary" || !explicitLink && (!Bool(content, "syncEnabled") || !Bool(latestCloud, "syncEnabled")))
            throw new InvalidDataException("云端读取语义或同步状态无效，未应用。");
        var codes = Array(content, "codes").Select(x => x?.GetValue<string>() ?? throw new InvalidDataException("云代码组无效。")).ToList();
        var remoteMain = Bool(content, "isMain");
        var goals = remoteMain ? ReadTransferredGoals(Array(listedCharacter, "mainGoals")) : [];
        if (remoteMain && Text(listedCharacter, "mainGoalsRevision") != Text(listedCloud, "goalsRevision"))
            throw new DesktopCloudConflictException("读取期间主档目标版本变化，未应用。");
        // Keep the original target ordering/names for the opaque server digest, but validate canonical IDs and quantities too.
        var normalized = NormalizeGoals(goals);
        if (normalized.Count != goals.Count || normalized.Any(x => planner.GetItem(x.ItemId) is null)
            || !normalized.Select(x => x.ItemId).Order(StringComparer.Ordinal)
            .SequenceEqual(goals.Select(x => x.ItemId).Order(StringComparer.Ordinal)))
            throw new InvalidDataException("云端目标不是当前资料的唯一标准物品，未应用。");
        var projected = CopyForCalculation(local, remoteMain, goals);
        projected.Name = Text(content, "saveName"); projected.LoadCodes = codes;
        ValidateSave(projected);
        return new() {
            Account = Account, LocalSaveId = local.Id, SaveId = binding.SaveId, CharacterId = Uuid(Text(content, "characterId")),
            GameAccount = local.GameAccount, ProfessionId = ProfessionId(local), SaveName = projected.Name,
            CloudRevision = Integer(content, "cloudRevision", 1), ClientRevision = Integer(content, "clientRevision", 1),
            CharacterRevision = Integer(listedCharacter, "characterRevision", 1), GoalsRevision = Text(content, "goalsRevision"),
            IsMain = remoteMain, CanAutoApply = canAutoApply, IsExplicitLink = explicitLink, IsBindingReplacement = bindingReplacement,
            LocalMainAsCloudSecondary = local.IsMain && !remoteMain && (explicitLink || binding.LocalMainAsCloudSecondary
                || IsPublicationWithdrawal(binding, listedCloud, listedCharacter)),
            ObservedPublicationWithdrawal = !explicitLink && IsPublicationWithdrawal(binding, listedCloud, listedCharacter),
            SyncEnabled = Bool(content, "syncEnabled"), BeforeBindingSaveId = beforeBindingSaveId,
            SyncedAt = ServerTime(content, "syncedAt"), PrivateCodes = codes, PrivateGoals = goals,
            BeforeLocalFingerprint = GetFingerprint(local), ContentFingerprint = ContentFingerprint(projected), GoalFingerprint = GoalFingerprint(goals)
        };
    }

    /// <summary>Explicit cloud-content choice. Validates a stable read without changing bindings or retiring a journal.</summary>
    public DesktopCloudDownloadedSnapshot PrepareLinkSnapshot(CloudSave local, JsonObject listedCloud, JsonObject listedCharacter,
        JsonObject content, JsonObject latestCloud, JsonObject latestCharacter, bool replaceExistingBinding = false)
    {
        ValidateSave(local);
        var target = Uuid(Text(listedCloud, "saveId"));
        var current = Account.Bindings.SingleOrDefault(binding => binding.LocalSaveId == local.Id);
        ValidateLinkBinding(local, current, target, replaceExistingBinding);
        var placeholder = current ?? new DesktopCloudBinding { LocalSaveId = local.Id, SaveId = target };
        _ = EnsureNoPending(placeholder, local.GameAccount, ProfessionId(local), acceptRemoteChanges: true);
        var validationBinding = new DesktopCloudBinding { LocalSaveId = local.Id, SaveId = target,
            CharacterId = current?.SaveId == target ? current.CharacterId : "",
            CloudRevision = current?.SaveId == target ? current.CloudRevision : 0,
            AcceptedClientRevision = current?.SaveId == target ? current.AcceptedClientRevision : 0 };
        return ReadStableSnapshot(local, validationBinding, listedCloud, listedCharacter, content, latestCloud, latestCharacter,
            canAutoApply: false, explicitLink: true, beforeBindingSaveId: current?.SaveId ?? "",
            bindingReplacement: current is not null && current.SaveId != target && HasUploadedBinding(current));
    }

    public void ValidateLinkedSnapshotForApply(CloudSave local, DesktopCloudDownloadedSnapshot snapshot)
    {
        ValidateLinkedSnapshotAccount(local, snapshot);
        if (GetFingerprint(local) != snapshot.BeforeLocalFingerprint)
            throw new DesktopCloudConflictException("关联期间本地存档或目标变化，未覆盖本地内容；请重新选择云版本。");
    }

    public void AcceptLinkedSnapshot(CloudSave appliedLocal, DesktopCloudDownloadedSnapshot snapshot)
    {
        var current = ValidateLinkedSnapshotAccount(appliedLocal, snapshot);
        ValidateSave(appliedLocal);
        if (snapshot.IsMain && !appliedLocal.IsMain
            || ContentFingerprint(appliedLocal) != snapshot.ContentFingerprint
            || GoalFingerprint(snapshot.IsMain ? appliedLocal.Targets : []) != snapshot.GoalFingerprint)
            throw new InvalidDataException("本地尚未完整接管这次云代码、目标或主副档选择，未关联成功。");
        var binding = current ?? new DesktopCloudBinding { LocalSaveId = appliedLocal.Id, SaveId = snapshot.SaveId };
        var retired = EnsureNoPending(binding, appliedLocal.GameAccount, ProfessionId(appliedLocal), acceptRemoteChanges: true);
        var previous = JsonSerializer.Serialize(Account, Json);
        if (snapshot.IsBindingReplacement)
        {
            var oldBinding = JsonSerializer.Deserialize<DesktopCloudBinding>(JsonSerializer.Serialize(binding, Json), Json)!;
            oldBinding.Enabled = false;
            Account.RetiredBindings.Add(oldBinding);
        }
        if (current is null) Account.Bindings.Add(binding);
        foreach (var terminal in retired)
        {
            Account.PendingOperations.Remove(terminal); Account.RetiredFailedOperations.Add(terminal);
            var oldBinding = Account.Bindings.SingleOrDefault(candidate => candidate.LocalSaveId == terminal.LocalSaveId);
            if (oldBinding is not null) { oldBinding.Status = oldBinding.Enabled ? "pending" : "disabled"; oldBinding.LastError = ""; }
        }
        if (binding.SaveId != snapshot.SaveId) binding.PredecessorSaveId = "";
        binding.SaveId = snapshot.SaveId; binding.CharacterId = snapshot.CharacterId;
        binding.CloudRevision = snapshot.CloudRevision; binding.AcceptedClientRevision = snapshot.ClientRevision;
        binding.AcceptedGoalsRevision = snapshot.GoalsRevision; binding.AcceptedContentFingerprint = snapshot.ContentFingerprint;
        binding.AcceptedGoalFingerprint = snapshot.GoalFingerprint; binding.AcceptedSaveName = snapshot.SaveName;
        binding.AcceptedIsMain = snapshot.IsMain; binding.AcceptedSyncEnabled = snapshot.SyncEnabled;
        binding.LocalMainAsCloudSecondary = appliedLocal.IsMain && !snapshot.IsMain;
        binding.AcceptedLocalFingerprint = LocalFingerprintWithName(appliedLocal, snapshot.SaveName);
        binding.Enabled = snapshot.SyncEnabled; binding.Deleted = false; binding.LastSyncAt = snapshot.SyncedAt;
        binding.Status = binding.Enabled ? "synced" : "disabled"; binding.LastError = "";
        Account.CharacterRevisions[snapshot.CharacterId] = snapshot.CharacterRevision;
        try { store.Save(); } catch { RestoreAccount(previous); throw; }
    }

    private DesktopCloudBinding? ValidateLinkedSnapshotAccount(CloudSave local, DesktopCloudDownloadedSnapshot snapshot)
    {
        if (!snapshot.IsExplicitLink || !ReferenceEquals(snapshot.Account, Account) || snapshot.LocalSaveId != local.Id
            || snapshot.GameAccount != local.GameAccount || snapshot.ProfessionId != ProfessionId(local))
            throw new DesktopCloudConflictException("这次关联不属于当前认证账户或角色，未应用。");
        var current = Account.Bindings.SingleOrDefault(binding => binding.LocalSaveId == local.Id);
        if ((current?.SaveId ?? "") != snapshot.BeforeBindingSaveId)
            throw new DesktopCloudConflictException("关联期间本地绑定已变化，请重新读取云档。");
        ValidateLinkBinding(local, current, snapshot.SaveId, snapshot.IsBindingReplacement);
        if (snapshot.IsBindingReplacement && (current is null || current.SaveId == snapshot.SaveId))
            throw new DesktopCloudConflictException("这次明确替换的旧绑定已变化，请重新选择云档。");
        _ = EnsureNoPending(current ?? new DesktopCloudBinding { LocalSaveId = local.Id, SaveId = snapshot.SaveId },
            local.GameAccount, ProfessionId(local), acceptRemoteChanges: true);
        if (current?.SaveId == snapshot.SaveId && current.CloudRevision > snapshot.CloudRevision)
            throw new DesktopCloudConflictException("这次云读取已早于本机接受的版本，未关联旧内容。");
        return current;
    }

    private static bool HasUploadedBinding(DesktopCloudBinding binding) => binding.CloudRevision > 0 || binding.CharacterId.Length > 0
        || binding.AcceptedLocalFingerprint.Length > 0 || binding.AcceptedContentFingerprint.Length > 0;

    private void ValidateLinkBinding(CloudSave local, DesktopCloudBinding? current, string target, bool replaceExistingBinding = false)
    {
        Uuid(target);
        if (current?.Deleted == true && !replaceExistingBinding)
            throw new DesktopCloudConflictException("这个本地档的原云标识已删除，请获取独立副本后关联；未复活旧标识。");
        if (current is not null && current.SaveId != target && HasUploadedBinding(current) && !replaceExistingBinding)
            throw new DesktopCloudConflictException("这份本地档已关联另一云槽位，请获取独立副本后关联；未替换原云绑定。");
        if (Account.Bindings.Any(binding => binding.LocalSaveId != local.Id && binding.SaveId == target))
            throw new DesktopCloudConflictException("这个云槽位已经关联另一份本地存档，请直接使用那份存档；未重复创建关联。");
    }

    /// <summary>Call immediately before a local atomic apply; a local source/goal change invalidates the prepared read.</summary>
    public void ValidateDownloadedSnapshotForApply(CloudSave local, DesktopCloudDownloadedSnapshot snapshot)
    {
        ValidateSnapshotAccount(local, snapshot);
        if (GetFingerprint(local) != snapshot.BeforeLocalFingerprint)
            throw new DesktopCloudConflictException("下载期间本地存档或目标变化，已保留双方，未覆盖。");
    }

    public void AcceptDownloadedSnapshot(CloudSave appliedLocal, DesktopCloudDownloadedSnapshot snapshot)
    {
        ValidateSnapshotAccount(appliedLocal, snapshot);
        ValidateSave(appliedLocal);
        if (ContentFingerprint(appliedLocal) != snapshot.ContentFingerprint
            || GoalFingerprint(snapshot.IsMain ? appliedLocal.Targets : []) != snapshot.GoalFingerprint)
            throw new InvalidDataException("本地尚未完整应用这次云代码和目标，未标为同步成功。");
        var binding = GetBinding(appliedLocal.Id);
        var previous = JsonSerializer.Serialize(Account, Json);
        binding.CharacterId = snapshot.CharacterId; binding.CloudRevision = snapshot.CloudRevision;
        binding.AcceptedClientRevision = snapshot.ClientRevision; binding.AcceptedGoalsRevision = snapshot.GoalsRevision;
        binding.AcceptedContentFingerprint = snapshot.ContentFingerprint; binding.AcceptedGoalFingerprint = snapshot.GoalFingerprint;
        binding.AcceptedLocalFingerprint = LocalFingerprintWithName(appliedLocal, snapshot.SaveName); binding.AcceptedSaveName = snapshot.SaveName;
        binding.AcceptedIsMain = snapshot.IsMain; binding.AcceptedSyncEnabled = true;
        binding.LocalMainAsCloudSecondary = snapshot.LocalMainAsCloudSecondary && appliedLocal.IsMain;
        binding.LastSyncAt = snapshot.SyncedAt; binding.Status = "synced"; binding.LastError = "";
        Account.CharacterRevisions[snapshot.CharacterId] = snapshot.CharacterRevision;
        try { store.Save(); } catch { RestoreAccount(previous); throw; }
    }

    private bool HasPendingRole(CloudSave local) => Account.PendingOperations.Concat(Account.CommittedMainTransfers)
        .Any(x => x.LocalSaveId == local.Id || x.GameAccount == local.GameAccount && x.ProfessionId == ProfessionId(local));

    private bool CanReadKnownConflictCandidate(CloudSave local)
    {
        var profession = ProfessionId(local);
        bool Related(DesktopCloudOperation operation) => operation.LocalSaveId == local.Id
            || operation.GameAccount == local.GameAccount && operation.ProfessionId == profession;
        if (Account.CommittedMainTransfers.Any(Related)) return false;
        var pending = Account.PendingOperations.Where(Related).ToList();
        // A terminal rejection may be inspected for history only. Never consume an unknown result,
        // advance its baseline, or bypass another local slot's operation for this same character.
        return pending.Count > 0 && pending.All(operation => operation.LocalSaveId == local.Id
            && operation.Status is "failed" or "conflict");
    }

    private void ValidateSnapshotAccount(CloudSave local, DesktopCloudDownloadedSnapshot snapshot)
    {
        var binding = Account.Bindings.SingleOrDefault(candidate => candidate.LocalSaveId == local.Id);
        if (binding is null || !snapshot.CanAutoApply || !ReferenceEquals(snapshot.Account, Account) || snapshot.LocalSaveId != local.Id || snapshot.SaveId != binding.SaveId
            || !binding.Enabled || binding.Deleted || snapshot.GameAccount != local.GameAccount || snapshot.ProfessionId != ProfessionId(local)
            || snapshot.IsMain != local.IsMain && !((binding.LocalMainAsCloudSecondary
                || binding.AcceptedIsMain && snapshot.ObservedPublicationWithdrawal) && snapshot.LocalMainAsCloudSecondary && local.IsMain && !snapshot.IsMain)
            || HasPendingRole(local) || binding.CloudRevision >= snapshot.CloudRevision)
            throw new DesktopCloudConflictException("这次下载不再属于当前账户、绑定或角色状态，未自动应用。");
    }

    private void ValidateRemoteSnapshotIdentity(CloudSave local, DesktopCloudBinding binding, JsonObject cloud, JsonObject? character)
    {
        if (Uuid(Text(cloud, "saveId")) != binding.SaveId || Text(cloud, "gameAccount") != local.GameAccount
            || Text(cloud, "professionId") != ProfessionId(local) || character is null)
            throw new InvalidDataException("云档不属于当前本地绑定或角色，未应用。");
        ValidateCharacter(local, binding, character);
        var characterId = Uuid(Text(character, "characterId"));
        if (Uuid(Text(cloud, "characterId")) != characterId
            || Integer(cloud, "characterRevision", 1) != Integer(character, "characterRevision", 1)
            || Bool(cloud, "isMain") != (OptionalText(character, "mainSaveId") == binding.SaveId))
            throw new DesktopCloudConflictException("云档与角色列表不是同次稳定版本，未应用。");
        if (Text(cloud, "mapVersion") != planner.MapVersion || Text(cloud, "mapSha256") != planner.MapSha256
            || Text(cloud, "recipeSetSha256") != planner.RecipeSetSha256)
            throw new InvalidDataException("云档地图或配方资料与当前本地版本不同，未应用。");
        Limited(Text(cloud, "saveName"), 1, 160, "云存档名"); Sha(Text(cloud, "goalsRevision"), "云目标版本");
        if (Integer(cloud, "cloudRevision", 1) < binding.CloudRevision
            || Integer(cloud, "clientRevision", 1) < binding.AcceptedClientRevision)
            throw new InvalidDataException("服务器存档版本回退，未应用。");
        _ = Bool(cloud, "syncEnabled"); _ = ServerTime(cloud, "syncedAt");
    }

    private void ValidateCharacter(CloudSave local, DesktopCloudBinding binding, JsonObject character)
    {
        var id = Uuid(Text(character, "characterId"));
        if (Text(character, "gameAccount") != local.GameAccount || Text(character, "professionId") != ProfessionId(local)
            || binding.CharacterId.Length > 0 && binding.CharacterId != id
            || Account.CharacterRevisions.TryGetValue(id, out var known) && Integer(character, "characterRevision", 1) < known)
            throw new InvalidDataException("云角色身份或版本与当前绑定不一致。");
        if (OptionalText(character, "mainSaveId") is { } main) Uuid(main);
        _ = Integer(character, "characterRevision", 1);
    }

    private static void RequireSameRemoteRead(JsonObject first, JsonObject second)
    {
        foreach (var key in new[] { "saveId", "characterId", "saveName", "gameAccount", "professionId", "cloudRevision", "characterRevision",
            "clientRevision", "goalsRevision", "isMain", "syncEnabled", "mapVersion", "mapSha256", "recipeSetSha256", "syncedAt" })
            if (!JsonNode.DeepEquals(first[key], second[key]))
                throw new DesktopCloudConflictException("读取期间云档版本或状态变化，请稍后重新读取；未应用旧内容。");
    }

    private static void RequireSameCharacterRead(JsonObject first, JsonObject second)
    {
        foreach (var key in new[] { "characterId", "gameAccount", "professionId", "characterRevision", "mainSaveId", "mainGoals", "mainGoalsRevision" })
            if (!JsonNode.DeepEquals(first[key], second[key]))
                throw new DesktopCloudConflictException("读取期间角色主档或目标变化，未应用旧内容。");
    }

    private void RestoreAccount(string json)
    {
        var previous = JsonSerializer.Deserialize<DesktopCloudAccountState>(json, Json)!;
        Account.Bindings = previous.Bindings; Account.CharacterRevisions = previous.CharacterRevisions;
        Account.RetiredBindings = previous.RetiredBindings;
        Account.PendingOperations = previous.PendingOperations; Account.CommittedMainTransfers = previous.CommittedMainTransfers;
        Account.RetiredFailedOperations = previous.RetiredFailedOperations;
    }

    // allCloudSaves is optional for compatibility with old callers. New synchronization calls must pass the complete list.
    public DesktopCloudOperation PrepareUpload(CloudSave save, JsonObject? cloudSave, JsonObject? character,
        bool acceptRemoteChanges = false, IEnumerable<JsonObject>? allCloudSaves = null)
    {
        if (allCloudSaves is not null) PrepareNewIdentityBinding(save, allCloudSaves);
        var binding = GetBinding(save.Id);
        if (!binding.Enabled) throw new InvalidOperationException("请先明确开启这个档位的正式云同步。");
        var newSlot = allCloudSaves is not null && cloudSave is null
            && !HasUploadedBinding(binding) && !Account.PendingOperations.Concat(Account.RetiredFailedOperations)
                .Concat(Account.CommittedMainTransfers).Any(operation => operation.LocalSaveId == save.Id || operation.SaveId == binding.SaveId);
        var newSecondaryBoundary = newSlot && save.IsMain && character is not null
            && OptionalText(character, "mainSaveId") is string existingMain
            && existingMain.Length > 0 && existingMain != binding.SaveId;
        if (character is not null)
        {
            var main = OptionalText(character, "mainSaveId");
            var cloudMain = save.IsMain && !binding.LocalMainAsCloudSecondary && !newSecondaryBoundary;
            if (cloudMain && main != binding.SaveId && !(newSlot && string.IsNullOrEmpty(main)) || !cloudMain && main == binding.SaveId)
                throw new InvalidOperationException("请使用显式主档转换，不能通过普通更新改变云端主档。");
        }
        return PrepareSnapshot(save, cloudSave, character, switchMain: false, acceptRemoteChanges, newSecondaryBoundary);
    }

    public DesktopCloudOperation PrepareMainSwitch(CloudSave successor, JsonObject character, JsonObject? cloudSave,
        bool acceptRemoteChanges = false, IEnumerable<JsonObject>? allCloudSaves = null)
    {
        if (allCloudSaves is not null) PrepareNewIdentityBinding(successor, allCloudSaves);
        if (OptionalText(character, "mainSaveId") == GetBinding(successor.Id).SaveId)
            throw new InvalidOperationException("这份存档已经是云端主档，请使用普通更新。");
        return PrepareSnapshot(successor, cloudSave, character, switchMain: true, acceptRemoteChanges);
    }

    private DesktopCloudOperation PrepareSnapshot(CloudSave save, JsonObject? cloudSave, JsonObject? character, bool switchMain,
        bool acceptRemoteChanges, bool newSecondaryBoundary = false)
    {
        ValidateSave(save);
        var binding = GetBinding(save.Id); var profession = ProfessionId(save);
        CheckRemote(binding, save.GameAccount, profession, cloudSave, character, acceptRemoteChanges);
        var retired = EnsureNoPending(binding, save.GameAccount, profession, acceptRemoteChanges);
        var isMain = switchMain || save.IsMain && !binding.LocalMainAsCloudSecondary && !newSecondaryBoundary;
        var transfer = switchMain && !string.IsNullOrEmpty(OptionalText(character!, "mainSaveId"));
        var goals = transfer ? ReadTransferredGoals(Array(character!, "mainGoals")) : NormalizeGoals(isMain ? save.Targets : []);
        var calculationSave = CopyForCalculation(save, isMain, goals);
        var needs = isMain ? MaterialNeedsExporter.Create(planner, calculationSave, goals, profession) : null;
        var goalFingerprint = GoalFingerprint(goals);
        var goalRevision = transfer ? Text(character!, "mainGoalsRevision")
            : binding.AcceptedIsMain == isMain && binding.AcceptedGoalFingerprint == goalFingerprint && binding.AcceptedGoalsRevision.Length > 0
                ? binding.AcceptedGoalsRevision : (needs?.GoalsRevision ?? GoalRevision(goals));
        Sha(goalRevision, "目标版本");
        var content = ContentFingerprint(save);
        var remoteClientRevision = cloudSave is null ? binding.AcceptedClientRevision : Integer(cloudSave, "clientRevision", 1);
        var clientRevision = Math.Max(save.Revision, Math.Max(1, remoteClientRevision));
        if (remoteClientRevision > 0 && content != binding.AcceptedContentFingerprint)
            clientRevision = Math.Max(clientRevision, checked(remoteClientRevision + 1));
        var requestId = Guid.NewGuid().ToString("D");
        var body = new JsonObject {
            ["requestId"] = requestId, ["expectedCloudRevision"] = cloudSave is null ? 0 : Integer(cloudSave, "cloudRevision", 1),
            ["expectedCharacterRevision"] = character is null ? 0 : Integer(character, "characterRevision", 1),
            ["clientRevision"] = clientRevision, ["saveName"] = Limited(save.Name, 1, 160, "存档名"),
            ["gameAccount"] = Limited(save.GameAccount, 1, 120, "游戏账号"), ["professionId"] = profession,
            ["mapVersion"] = planner.MapVersion, ["mapSha256"] = planner.MapSha256,
            ["recipeSetSha256"] = planner.RecipeSetSha256, ["goalsRevision"] = goalRevision,
            ["isMain"] = isMain, ["syncEnabled"] = binding.Enabled,
            ["codes"] = Strings(save.LoadCodes), ["goals"] = ProjectGoals(goals),
            ["needs"] = needs is null ? null : ProjectNeeds(needs, binding.SaveId, clientRevision, goalRevision, profession, goals)
        };
        var characterId = character is null ? "" : Uuid(Text(character, "characterId"));
        if (switchMain) body["saveId"] = binding.SaveId;
        var operation = new DesktopCloudOperation { RequestId = requestId, LocalSaveId = save.Id, SaveId = binding.SaveId,
            CharacterId = characterId, GameAccount = save.GameAccount, ProfessionId = profession,
            Kind = switchMain ? "main-switch" : "upload", Method = "PUT",
            Path = switchMain ? $"/api/desktop/characters/{characterId}/main-save" : $"/api/desktop/cloud-saves/{binding.SaveId}",
            BodyJson = body.ToJsonString(Json), BodySha256 = DesktopCloudStateStore.Hash(body.ToJsonString(Json)),
            ContentFingerprint = content, GoalFingerprint = goalFingerprint,
            LocalFingerprint = switchMain ? Fingerprint(save, isMain, NormalizeGoals(goals)) : GetFingerprint(save) };
        var previousBoundary = binding.LocalMainAsCloudSecondary;
        if (newSecondaryBoundary) binding.LocalMainAsCloudSecondary = true;
        try { return Journal(binding, operation, retired); }
        catch { binding.LocalMainAsCloudSecondary = previousBoundary; throw; }
    }

    public DesktopCloudOperation PrepareSyncState(string localSaveId, JsonObject cloudSave, bool enabled, bool acceptRemoteChanges = false)
        => PrepareMutation(localSaveId, cloudSave, "sync-state", enabled, acceptRemoteChanges);

    /// <summary>Withdraw public main goals without disabling sync, changing local roles, or uploading a replacement snapshot.</summary>
    public DesktopCloudOperation PrepareMainUnpublish(CloudSave local, JsonObject cloudSave, JsonObject character,
        bool acceptRemoteChanges = false)
    {
        ValidateSave(local);
        var binding = Account.Bindings.SingleOrDefault(candidate => candidate.LocalSaveId == local.Id)
            ?? throw new DesktopCloudConflictException("请先关联并同步这份云档，再关闭云主档状态。");
        if (!binding.Enabled || !Bool(cloudSave, "syncEnabled"))
            throw new InvalidOperationException("仅开启云同步的档位可以关闭云主档状态。");
        CheckRemote(binding, local.GameAccount, ProfessionId(local), cloudSave, character, acceptRemoteChanges);
        ValidateRemoteSnapshotIdentity(local, binding, cloudSave, character);
        var retired = EnsureNoPending(binding, local.GameAccount, ProfessionId(local), acceptRemoteChanges);
        if (!Bool(cloudSave, "isMain") || OptionalText(character, "mainSaveId") != binding.SaveId)
            throw new DesktopCloudConflictException("这份云档已经不是主档，请刷新状态；未重复发送关闭请求。");
        var characterId = Uuid(Text(character, "characterId"));
        if (Uuid(Text(cloudSave, "characterId")) != characterId)
            throw new InvalidDataException("云档与角色身份不一致，未关闭主档状态。");
        var requestId = Guid.NewGuid().ToString("D");
        var body = new JsonObject { ["requestId"] = requestId,
            ["expectedCloudRevision"] = Integer(cloudSave, "cloudRevision", 1),
            ["expectedCharacterRevision"] = Integer(character, "characterRevision", 1), ["enabled"] = false };
        return Journal(binding, new() { RequestId = requestId, LocalSaveId = local.Id, SaveId = binding.SaveId,
            CharacterId = characterId, GameAccount = local.GameAccount, ProfessionId = ProfessionId(local),
            Kind = "main-state", Method = "PATCH", Path = $"/api/desktop/cloud-saves/{binding.SaveId}/main-state",
            BodyJson = body.ToJsonString(Json), BodySha256 = DesktopCloudStateStore.Hash(body.ToJsonString(Json)),
            LocalFingerprint = GetFingerprint(local), ContentFingerprint = binding.AcceptedContentFingerprint,
            GoalFingerprint = binding.AcceptedGoalFingerprint, LocalWasMain = local.IsMain,
            ExpectedClientRevision = Integer(cloudSave, "clientRevision", 1),
            ExpectedGoalsRevision = Sha(Text(cloudSave, "goalsRevision"), "云目标版本"), ExpectedSyncEnabled = Bool(cloudSave, "syncEnabled") }, retired);
    }

    public DesktopCloudOperation PrepareDelete(string localSaveId, JsonObject cloudSave, bool acceptRemoteChanges = false)
        => PrepareMutation(localSaveId, cloudSave, "delete", null, acceptRemoteChanges);
    public DesktopCloudOperation PrepareDelete(JsonObject cloudSave, bool acceptRemoteChanges = false)
    {
        var saveId = Uuid(Text(cloudSave, "saveId"));
        var binding = Account.Bindings.SingleOrDefault(x => x.SaveId == saveId);
        if (binding is null)
        {
            binding = new() { LocalSaveId = "remote:" + saveId, SaveId = saveId,
                CharacterId = Uuid(Text(cloudSave, "characterId")), CloudRevision = Integer(cloudSave, "cloudRevision", 1) };
            Account.Bindings.Add(binding); store.Save();
        }
        return PrepareDelete(binding.LocalSaveId, cloudSave, acceptRemoteChanges);
    }
    private DesktopCloudOperation PrepareMutation(string localSaveId, JsonObject cloudSave, string kind, bool? enabled, bool acceptRemoteChanges)
    {
        var binding = GetBinding(localSaveId);
        var gameAccount = Text(cloudSave, "gameAccount"); var profession = Text(cloudSave, "professionId");
        CheckRemote(binding, gameAccount, profession, cloudSave, null, acceptRemoteChanges);
        var retired = EnsureNoPending(binding, gameAccount, profession, acceptRemoteChanges);
        var requestId = Guid.NewGuid().ToString("D");
        var body = new JsonObject { ["requestId"] = requestId, ["expectedCloudRevision"] = Integer(cloudSave, "cloudRevision", 1) };
        if (enabled.HasValue) body["enabled"] = enabled.Value;
        return Journal(binding, new() { RequestId = requestId, LocalSaveId = localSaveId, SaveId = binding.SaveId,
            CharacterId = Uuid(Text(cloudSave, "characterId")), GameAccount = gameAccount, ProfessionId = profession,
            Kind = kind, Method = kind == "delete" ? "DELETE" : "PATCH",
            Path = $"/api/desktop/cloud-saves/{binding.SaveId}" + (kind == "sync-state" ? "/sync-state" : ""),
            BodyJson = body.ToJsonString(Json), BodySha256 = DesktopCloudStateStore.Hash(body.ToJsonString(Json)) }, retired);
    }

    private DesktopCloudOperation Journal(DesktopCloudBinding binding, DesktopCloudOperation operation,
        IReadOnlyList<DesktopCloudOperation>? retired = null)
    {
        if (Encoding.UTF8.GetByteCount(operation.BodyJson) > 2 * 1024 * 1024)
            throw new InvalidDataException("上传内容超过服务器的 2 MiB 上限，未拆分或提交。");
        var previousPending = Account.PendingOperations.ToList();
        var previousRetired = Account.RetiredFailedOperations.ToList();
        var affected = Account.Bindings.Where(candidate => ReferenceEquals(candidate, binding)
            || (retired ?? []).Any(terminal => terminal.LocalSaveId == candidate.LocalSaveId))
            .Select(candidate => (Binding: candidate, Status: candidate.Status, Error: candidate.LastError)).ToList();
        foreach (var terminal in retired ?? [])
        { Account.PendingOperations.Remove(terminal); Account.RetiredFailedOperations.Add(terminal); }
        foreach (var item in affected)
        { item.Binding.Status = item.Binding.Enabled ? "pending" : "disabled"; item.Binding.LastError = ""; }
        Account.PendingOperations.Add(operation); binding.Status = "pending"; binding.LastError = "";
        try { store.Save(); } // No caller receives a transportable operation until this succeeds.
        catch
        {
            Account.PendingOperations = previousPending; Account.RetiredFailedOperations = previousRetired;
            foreach (var item in affected) { item.Binding.Status = item.Status; item.Binding.LastError = item.Error; }
            throw;
        }
        return operation;
    }

    public async Task<JsonObject> ExecuteAsync(DesktopCloudOperation operation,
        Func<string, string, string?, CancellationToken, Task<JsonObject>> sendAsync, CancellationToken cancellation = default,
        bool recoverTerminalReceipt = false)
    {
        await execution.WaitAsync(cancellation);
        var queryingReceipt = false;
        try
        {
            ValidateOperation(operation);
            if (GetBinding(operation.LocalSaveId).StopRequestedByIgnore
                && !(operation.Kind == "sync-state" && Parse(operation.BodyJson)["enabled"]?.GetValue<bool>() == false))
                throw new DesktopCloudConflictException("这份存档已停止同步，旧请求只能查询回执，不能重新发送。");
            if (IsKnownFailedOperation(operation) && !(recoverTerminalReceipt && operation.MustQueryReceipt))
                throw new DesktopCloudConflictException("这次操作需要先处理失败或冲突，未自动重试。");
            cancellation.ThrowIfCancellationRequested();
            var queryReceipt = operation.MustQueryReceipt;
            operation.MustQueryReceipt = true; operation.Status = "unknown"; store.Save();
            JsonObject? receipt = null;
            if (queryReceipt)
            {
                queryingReceipt = true;
                try { receipt = await sendAsync("GET", $"/api/desktop/operations/{operation.RequestId}", null, cancellation); }
                catch (DesktopApiException error) when (error.StatusCode == 404 && error.Code == "operation_not_found") { }
                queryingReceipt = false;
            }
            receipt ??= await sendAsync(operation.Method, operation.Path, operation.BodyJson, cancellation);
            ApplyReceipt(operation, receipt);
            return receipt;
        }
        catch (DesktopApiException error)
        {
            var binding = GetBinding(operation.LocalSaveId);
            if (queryingReceipt || error.Code == "idempotency_conflict" || error.OutcomeUnknown) operation.Status = "unknown";
            else if (error.StatusCode == 409) operation.Status = "conflict";
            else if (!error.OutcomeUnknown && error.StatusCode is >= 400 and < 500 && error.StatusCode != 429)
                operation.Status = "failed";
            else operation.Status = "unknown";
            // Old versions left this flag on even after a definite rejection. Such evidence can now
            // be resolved by querying/retrying the unchanged request; unknown outcomes keep the flag.
            operation.MustQueryReceipt = operation.Status == "unknown";
            binding.Status = operation.Status; binding.LastError = error.Message; store.Save();
            throw;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or JsonException or HttpRequestException)
        {
            var binding = GetBinding(operation.LocalSaveId);
            binding.Status = "unknown"; binding.LastError = "同步结果待确认，恢复连接后会先查询这次操作。";
            operation.Status = "unknown"; store.Save();
            throw;
        }
        finally { execution.Release(); }
    }

    public void ApplyReceipt(DesktopCloudOperation operation, JsonObject receipt)
    {
        ValidateOperation(operation);
        var body = Parse(operation.BodyJson);
        if (receipt["ok"]?.GetValue<bool>() != true || Text(receipt, "requestId") != operation.RequestId || Text(receipt, "saveId") != operation.SaveId)
            throw new InvalidDataException("服务器回执与本次操作不一致，尚未标为成功。");
        var revision = Integer(receipt, "cloudRevision", 1);
        if (revision <= Integer(body, "expectedCloudRevision", 0)) throw new InvalidDataException("服务器回执版本无效。");
        var binding = GetBinding(operation.LocalSaveId);
        var characterId = operation.CharacterId;
        long? characterRevision = null;
        DateTimeOffset? syncedAt = null;
        if (operation.Kind is "upload" or "main-switch")
        {
            characterId = Uuid(Text(receipt, "characterId"));
            if (operation.CharacterId.Length > 0 && characterId != operation.CharacterId)
                throw new InvalidDataException("服务器回执角色不一致。");
            characterRevision = Integer(receipt, "characterRevision", 1);
            if (characterRevision <= Integer(body, "expectedCharacterRevision", 0)
                || Integer(receipt, "acceptedClientRevision", 1) != Integer(body, "clientRevision", 1)
                || Text(receipt, "acceptedGoalsRevision") != Text(body, "goalsRevision")
                || Bool(receipt, "isMain") != Bool(body, "isMain") || Bool(receipt, "syncEnabled") != Bool(body, "syncEnabled"))
                throw new InvalidDataException("服务器回执没有确认本次完整快照。");
            syncedAt = ServerTime(receipt, "syncedAt");
        }
        else if (operation.Kind == "main-state")
        {
            characterId = Uuid(Text(receipt, "characterId"));
            characterRevision = Integer(receipt, "characterRevision", 1);
            if (characterId != operation.CharacterId || characterRevision <= Integer(body, "expectedCharacterRevision", 1)
                || Bool(body, "enabled") || Bool(receipt, "isMain")
                || Bool(receipt, "syncEnabled") != operation.ExpectedSyncEnabled
                || Integer(receipt, "acceptedClientRevision", 1) != operation.ExpectedClientRevision
                || Text(receipt, "acceptedGoalsRevision") != operation.ExpectedGoalsRevision)
                throw new InvalidDataException("服务器回执没有确认本次主档关闭或保留原内容，未应用。");
            _ = ServerTime(receipt, "syncedAt");
        }
        else if (operation.Kind == "delete")
        { characterRevision = Integer(receipt, "characterRevision", 1); _ = ServerTime(receipt, "deletedAt"); }
        else if (Bool(receipt, "syncEnabled") != Bool(body, "enabled"))
            throw new InvalidDataException("服务器回执同步开关不一致。");

        // Keep a rollback copy: disk failure must not lose the retry journal in this process.
        var before = JsonSerializer.Serialize(Account, Json);
        binding.CloudRevision = revision; binding.LastError = "";
        if (characterRevision.HasValue) Account.CharacterRevisions[characterId] = characterRevision.Value;
        if (operation.Kind is "upload" or "main-switch")
        {
            binding.CharacterId = characterId; binding.LastSyncAt = syncedAt;
            binding.AcceptedClientRevision = Integer(receipt, "acceptedClientRevision", 1);
            binding.AcceptedGoalsRevision = Text(receipt, "acceptedGoalsRevision");
            binding.AcceptedGoalFingerprint = operation.GoalFingerprint;
            binding.AcceptedSaveName = Text(body, "saveName");
            binding.AcceptedIsMain = Bool(receipt, "isMain"); binding.AcceptedSyncEnabled = Bool(receipt, "syncEnabled");
            if (operation.Kind == "main-switch" || binding.AcceptedIsMain) binding.LocalMainAsCloudSecondary = false;
            binding.AcceptedLocalFingerprint = operation.LocalFingerprint; binding.AcceptedContentFingerprint = operation.ContentFingerprint;
            if (binding.AcceptedIsMain)
                foreach (var old in Account.Bindings.Where(x => x != binding && x.CharacterId == characterId)) old.AcceptedIsMain = false;
            binding.Status = binding.Enabled ? "synced" : "disabled";
        }
        else if (operation.Kind == "main-state")
        {
            binding.AcceptedIsMain = false; binding.LocalMainAsCloudSecondary = operation.LocalWasMain;
            binding.AcceptedSyncEnabled = Bool(receipt, "syncEnabled");
            // A metadata receipt does not prove acceptance of current local or newer cloud content.
            binding.Status = !binding.Enabled ? "disabled" : operation.LocalFingerprint == binding.AcceptedLocalFingerprint ? "synced" : "pending";
        }
        else if (operation.Kind == "delete")
        { binding.Deleted = true; binding.Enabled = false; binding.Status = "deleted"; binding.AcceptedIsMain = false; binding.AcceptedSyncEnabled = false; }
        else
        { binding.AcceptedSyncEnabled = Bool(receipt, "syncEnabled"); binding.Status = binding.Enabled ? "pending" : "disabled"; }
        Account.PendingOperations.Remove(operation);
        if (operation.Kind == "main-switch") Account.CommittedMainTransfers.Add(operation);
        try { store.Save(); }
        catch
        {
            var previous = JsonSerializer.Deserialize<DesktopCloudAccountState>(before, Json)!;
            // Preserve pending object identity so the current caller can safely retry it.
            Account.Bindings = previous.Bindings;
            Account.RetiredBindings = previous.RetiredBindings;
            Account.CharacterRevisions = previous.CharacterRevisions;
            Account.CommittedMainTransfers = previous.CommittedMainTransfers;
            Account.RetiredFailedOperations = previous.RetiredFailedOperations;
            Account.PendingOperations = previous.PendingOperations.Select(x => x.RequestId == operation.RequestId ? operation : x).ToList();
            throw;
        }
    }

    public void CompleteLocalMainTransfer(string requestId)
    {
        var operation = Account.CommittedMainTransfers.SingleOrDefault(x => x.RequestId == requestId);
        if (operation is null) return;
        Account.CommittedMainTransfers.Remove(operation);
        try { store.Save(); }
        catch { Account.CommittedMainTransfers.Add(operation); throw; }
    }

    /// <summary>
    /// Completes the local role change without adopting the old main's transition goals.
    /// A true result means the newly selected save still has its own snapshot to publish.
    /// The accepted receipt remains the baseline until that separate upload succeeds.
    /// </summary>
    public bool CompleteLocalMainTransfer(string requestId, CloudSave appliedLocal)
    {
        var operation = Account.CommittedMainTransfers.SingleOrDefault(x => x.RequestId == requestId);
        if (operation is null) return false;
        ValidateSave(appliedLocal);
        var binding = Account.Bindings.SingleOrDefault(x => x.LocalSaveId == operation.LocalSaveId);
        if (operation.Kind != "main-switch" || appliedLocal.Id != operation.LocalSaveId || !appliedLocal.IsMain
            || appliedLocal.GameAccount != operation.GameAccount || ProfessionId(appliedLocal) != operation.ProfessionId
            || binding is null || binding.SaveId != operation.SaveId || !binding.AcceptedIsMain)
            throw new InvalidOperationException("本地主档尚未完整切换，已确认的云端切换回执仍保留，未覆盖本地目标。");
        var needsUpload = GetFingerprint(appliedLocal) != binding.AcceptedLocalFingerprint;
        var previousStatus = binding.Status;
        binding.Status = binding.Enabled ? needsUpload ? "pending" : "synced" : "disabled";
        Account.CommittedMainTransfers.Remove(operation);
        try { store.Save(); }
        catch
        {
            binding.Status = previousStatus;
            Account.CommittedMainTransfers.Add(operation);
            throw;
        }
        return needsUpload;
    }

    public void DiscardFailedOperation(string requestId)
    {
        var operation = Account.PendingOperations.Single(x => x.RequestId == requestId);
        if (!IsKnownFailedOperation(operation) || operation.MustQueryReceipt)
            throw new InvalidOperationException("结果未知的操作必须先查询回执，不能丢弃或换编号。");
        RetireOperations([operation]);
    }

    public static bool IsKnownFailedOperation(DesktopCloudOperation operation) => operation.Status is "failed" or "conflict" or "rejected";

    /// <summary>Explicit user resolution only. Retains private terminal bodies locally; never retires an unknown receipt.</summary>
    public int RetireKnownFailedOperations(CloudSave roleSave)
    {
        var binding = GetBinding(roleSave.Id);
        var pending = EnsureNoPending(binding, roleSave.GameAccount, ProfessionId(roleSave), acceptRemoteChanges: true);
        if (pending.Count > 0) RetireOperations(pending);
        return pending.Count;
    }

    private void RetireOperations(IReadOnlyList<DesktopCloudOperation> operations)
    {
        var previousPending = Account.PendingOperations.ToList();
        var previousRetired = Account.RetiredFailedOperations.ToList();
        var affected = Account.Bindings.Where(binding => operations.Any(operation => operation.LocalSaveId == binding.LocalSaveId))
            .Select(binding => (Binding: binding, Status: binding.Status, Error: binding.LastError)).ToList();
        foreach (var terminal in operations)
        { Account.PendingOperations.Remove(terminal); Account.RetiredFailedOperations.Add(terminal); }
        foreach (var item in affected)
        { item.Binding.Status = item.Binding.Enabled ? "pending" : "disabled"; item.Binding.LastError = ""; }
        try { store.Save(); }
        catch
        {
            Account.PendingOperations = previousPending; Account.RetiredFailedOperations = previousRetired;
            foreach (var item in affected) { item.Binding.Status = item.Status; item.Binding.LastError = item.Error; }
            throw;
        }
    }

    private List<DesktopCloudOperation> EnsureNoPending(DesktopCloudBinding binding, string gameAccount, string profession, bool acceptRemoteChanges)
    {
        if (Account.CommittedMainTransfers.Any(x => x.LocalSaveId == binding.LocalSaveId || x.GameAccount == gameAccount && x.ProfessionId == profession))
            throw new DesktopCloudConflictException("云端主档已经转换，请先完成本地主档与目标的接管。");
        var pending = Account.PendingOperations.Where(x => x.LocalSaveId == binding.LocalSaveId
            || x.GameAccount == gameAccount && x.ProfessionId == profession).ToList();
        // Validate the complete role before changing any collection. An earlier terminal entry must
        // not disappear merely because a later unknown/prepared entry blocked the action.
        if (pending.Any(operation => !IsKnownFailedOperation(operation) || operation.MustQueryReceipt))
            throw new DesktopCloudConflictException("这个角色仍有结果未知或尚未提交的原操作，请先查询原回执；未放弃任何待办。");
        if (pending.Count > 0 && !acceptRemoteChanges)
            throw new DesktopCloudConflictException("这个角色有已知失败或冲突操作，请明确处理后再继续；本地内容仍保留。");
        return pending;
    }
    private void CheckRemote(DesktopCloudBinding binding, string gameAccount, string profession, JsonObject? cloud, JsonObject? character, bool accepted)
    {
        if (binding.Deleted) throw new InvalidOperationException("云档标识已删除，不能复用。");
        if (cloud is null && binding.CloudRevision > 0)
            throw new DesktopCloudConflictException("已绑定云档不在最新列表中，请先处理云端删除或账户变化。");
        if (cloud is not null)
        {
            if (Uuid(Text(cloud, "saveId")) != binding.SaveId || Text(cloud, "gameAccount") != gameAccount || Text(cloud, "professionId") != profession)
                throw new InvalidDataException("云档身份与本地绑定不一致，未上传。");
            if (!accepted && Integer(cloud, "cloudRevision", 1) != binding.CloudRevision)
                throw new DesktopCloudConflictException("云端已有其他设备更新，请先获取副本或明确处理冲突。");
        }
        if (character is not null)
        {
            var id = Uuid(Text(character, "characterId"));
            if (Text(character, "gameAccount") != gameAccount || Text(character, "professionId") != profession
                || binding.CharacterId.Length > 0 && binding.CharacterId != id)
                throw new InvalidDataException("云端角色身份与存档不一致，未上传。");
            if (!accepted && Account.CharacterRevisions.TryGetValue(id, out var known) && Integer(character, "characterRevision", 1) != known)
                throw new DesktopCloudConflictException("云端角色已被其他操作更新，请先确认新的主档和目标。");
        }
    }
    private void RequireBinding(DesktopCloudBinding binding)
    { if (!Account.Bindings.Contains(binding)) throw new InvalidOperationException("不能操作其他账户分区的存档绑定。"); }
    private void ValidateOperation(DesktopCloudOperation operation)
    {
        if (!Account.PendingOperations.Contains(operation) || DesktopCloudStateStore.Hash(operation.BodyJson) != operation.BodySha256)
            throw new InvalidDataException("待确认操作不属于当前账户，或内容已变化。");
        var binding = GetBinding(operation.LocalSaveId);
        Uuid(operation.RequestId); Uuid(operation.SaveId);
        var path = operation.Kind == "main-switch" ? $"/api/desktop/characters/{Uuid(operation.CharacterId)}/main-save"
            : $"/api/desktop/cloud-saves/{operation.SaveId}" + (operation.Kind switch { "sync-state" => "/sync-state", "main-state" => "/main-state", _ => "" });
        var method = operation.Kind switch { "upload" or "main-switch" => "PUT", "sync-state" or "main-state" => "PATCH", "delete" => "DELETE", _ => "" };
        if (binding.SaveId != operation.SaveId || operation.Path != path || operation.Method != method || method.Length == 0
            || Text(Parse(operation.BodyJson), "requestId") != operation.RequestId)
            throw new InvalidDataException("待确认操作的目标或类型无效。");
        if (operation.Kind == "main-state")
        {
            Uuid(operation.CharacterId); Quantity(operation.ExpectedClientRevision, 1, long.MaxValue);
            Sha(operation.ExpectedGoalsRevision, "原云目标版本");
            if (Bool(Parse(operation.BodyJson), "enabled")) throw new InvalidDataException("重新设为主档须使用显式转换，未重放无效关闭请求。");
        }
    }

    private void ValidateSave(CloudSave save)
    {
        if (save.IsIgnored) throw new InvalidOperationException("请先恢复已忽略的存档，再执行云操作。");
        if (save.IsDemo || save.ParsedResult?["kind"]?.ToString() != "character"
            || save.ParsedResult?["map_profile"]?["script_sha256"]?.ToString() != SaveProfile.ScriptSha256)
            throw new InvalidDataException("仅支持当前地图的真实角色存档，账号徽章档不能上传。");
        if (save.Revision < 1 || save.LoadCodes.Count is < 1 or > 32
            || save.LoadCodes.Any(x => x.Length is < 7 or > 262144 || !x.StartsWith("-load ", StringComparison.Ordinal)
                || x[6..].Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            || save.LoadCodes.Sum(x => (long)Encoding.UTF8.GetByteCount(x)) > 1024 * 1024)
            throw new InvalidDataException("完整角色存档代码组不符合上传格式或大小限制。");
    }
    private List<CloudGoal> NormalizeGoals(IEnumerable<CloudGoal> goals)
    {
        var source = goals.ToList();
        foreach (var goal in source) { Limited(goal.ItemId, 1, 64, "目标物品编号"); Quantity(goal.Quantity, 1, 10000); }
        var result = source.GroupBy(x => planner.Canonical(x.ItemId), StringComparer.Ordinal).Select(group => new CloudGoal {
            ItemId = group.Key, ItemName = planner.GetItem(group.Key)?.Name ?? "未收录目标", Quantity = checked(group.Sum(x => x.Quantity))
        }).OrderBy(x => x.ItemId, StringComparer.Ordinal).ThenBy(x => x.Quantity).ToList();
        ValidateGoals(result); return result;
    }
    private static List<CloudGoal> ReadTransferredGoals(JsonArray goals)
    {
        var result = goals.Select(Object).Select(x => new CloudGoal { ItemId = Text(x, "itemId"), ItemName = Text(x, "name"),
            Quantity = checked((int)Integer(x, "quantity", 1)) }).ToList();
        ValidateGoals(result); return result;
    }
    private static void ValidateGoals(IReadOnlyList<CloudGoal> goals)
    {
        if (goals.Count > 100 || goals.Select(x => x.ItemId).Distinct(StringComparer.Ordinal).Count() != goals.Count)
            throw new InvalidDataException("目标数量超限或物品重复。");
        foreach (var goal in goals) { Limited(goal.ItemId, 1, 64, "目标物品编号"); Limited(goal.ItemName, 0, 160, "目标名称"); Quantity(goal.Quantity, 1, 10000); }
    }
    private static string GoalRevision(IReadOnlyList<CloudGoal> goals) => DesktopCloudStateStore.Hash(JsonSerializer.Serialize(
        goals.Select(x => new MaterialNeedGoal(x.ItemId, x.ItemName, x.Quantity)).ToList(), MaterialNeedsExporter.JsonOptions));
    private static CloudSave CopyForCalculation(CloudSave source, bool main, List<CloudGoal> goals) => new() {
        Id = source.Id, Name = source.Name, GameAccount = source.GameAccount, Profession = source.Profession,
        IsMain = main, IsDemo = source.IsDemo, MapVersion = source.MapVersion, ParsedResult = source.ParsedResult?.DeepClone().AsObject(),
        Inventory = source.Inventory.Select(x => new CloudInventoryItem { ItemId = x.ItemId, ItemName = x.ItemName, Quantity = x.Quantity }).ToList(),
        Targets = goals, Revision = source.Revision
    };

    public static JsonObject ProjectNeeds(MaterialNeedsDocument document, string saveId, long clientRevision, string goalsRevision,
        string professionId, IReadOnlyList<CloudGoal> goals)
    {
        // Explicit projection avoids owner/path/inventory and computed helper properties.
        var result = new JsonObject { ["schemaVersion"] = 1, ["kind"] = "grimmcair.material-needs",
            ["status"] = document.Status, ["isExact"] = document.IsExact, ["mapVersion"] = document.MapVersion,
            ["mapSha256"] = document.MapSha256, ["recipeSetSha256"] = document.RecipeSetSha256,
            ["saveId"] = Uuid(saveId), ["saveRevision"] = clientRevision, ["goalsRevision"] = Sha(goalsRevision, "目标版本"),
            ["gameAccount"] = document.GameAccount, ["professionId"] = professionId, ["profession"] = document.Profession,
            ["goals"] = ProjectGoals(goals), ["ordinaryNeeds"] = Nodes(document.OrdinaryNeeds.Select(ProjectNeed), 1200),
            ["orGroups"] = Nodes(document.OrGroups.Select(group => new JsonObject {
                ["id"] = Limited(group.Id, 1, 96, "任选组编号"), ["title"] = Limited(group.Title, 0, 500, "任选组说明"), ["choose"] = group.Choose == 1 ? 1 : throw new InvalidDataException("任选组必须选择一个完整方案。"),
                ["options"] = Nodes(group.Options.Select(option => new JsonObject {
                    ["id"] = Limited(option.Id, 1, 96, "选项编号"), ["label"] = Limited(option.Label, 0, 500, "选项名称"),
                    ["needs"] = Nodes(option.Needs.Select(ProjectNeed), 1200),
                    ["restrictions"] = Nodes(option.Restrictions.Select(x => (JsonNode?)JsonValue.Create(Limited(x, 0, 500, "方案条件"))), 50)
                }), 256, 2) }), 100),
            ["quantityRanges"] = Nodes(document.QuantityRanges.Select(x => new JsonObject {
                ["itemId"] = Limited(x.ItemId, 1, 96, "物品编号"), ["name"] = Limited(x.Name, 0, 500, "物品名"), ["stage"] = Limited(x.Stage, 0, 500, "阶段"),
                ["minimum"] = Quantity(x.Minimum, 0, 1000000), ["maximum"] = Quantity(x.Maximum, x.Minimum, 1000000), ["isReusable"] = x.IsReusable }), 1200),
            ["unresolvedGroups"] = Nodes(document.UnresolvedGroups.Select(group => new JsonObject {
                ["id"] = Limited(group.Id, 1, 96, "待计算组编号"), ["title"] = Limited(group.Title, 0, 500, "待计算组名称"), ["reason"] = Limited(group.Reason, 0, 4000, "待计算原因"),
                ["goals"] = Nodes(group.Goals.Select(x => new JsonObject { ["itemId"] = x.ItemId, ["name"] = x.Name, ["quantity"] = Quantity(x.Quantity, 1, 1000000) }), 100),
                ["recipes"] = Nodes(group.Recipes.Select(recipe => new JsonObject {
                    ["id"] = Limited(recipe.Id, 1, 96, "配方编号"), ["name"] = Limited(recipe.Name, 0, 500, "配方名称"), ["outputId"] = Limited(recipe.OutputId, 1, 96, "产物编号"),
                    ["outputQuantity"] = Quantity(recipe.OutputQuantity, 1, 1000000),
                    ["ingredientsSummary"] = Limited(recipe.IngredientsSummary, 0, 500, "配方说明"), ["restrictionsText"] = Limited(recipe.RestrictionsText, 0, 500, "配方条件"),
                    ["ingredients"] = Nodes(recipe.Ingredients.Select(x => new JsonObject { ["itemId"] = Limited(x.ItemId, 1, 96, "材料编号"),
                        ["name"] = Limited(x.Name, 0, 500, "材料名称"), ["quantity"] = Quantity(x.Quantity, 1, 1000000), ["isReusable"] = x.IsReusable }), 100)
                }), 600) }), 100),
            ["issues"] = Nodes(document.Issues.Select(x => new JsonObject { ["code"] = Limited(x.Code, 1, 96, "提示编号"),
                ["message"] = Limited(x.Message, 0, 500, "需求提示"), ["itemId"] = Limited(x.ItemId, 0, 96, "提示物品编号") }), 200),
            ["semantics"] = Limited(document.Semantics, 0, 1500, "需求说明") };
        return result;
    }
    private static JsonObject ProjectNeed(MaterialNeed x) => new() { ["itemId"] = Limited(x.ItemId, 1, 96, "材料编号"),
        ["name"] = Limited(x.Name, 0, 500, "材料名称"), ["quantity"] = Quantity(x.Quantity, 1, 1000000), ["stage"] = Limited(x.Stage, 0, 500, "材料阶段"),
        ["isReusable"] = x.IsReusable, ["acquisitionStatus"] = Limited(x.AcquisitionStatus, 0, 500, "材料来源说明"),
        ["goalIds"] = Nodes(x.GoalIds.Select(id => (JsonNode?)JsonValue.Create(Limited(id, 1, 96, "关联目标编号"))), 100) };
    private static JsonArray ProjectGoals(IReadOnlyList<CloudGoal> goals) => Nodes(goals.Select(x => new JsonObject {
        ["itemId"] = x.ItemId, ["name"] = x.ItemName, ["quantity"] = x.Quantity }), 100);
    private static JsonArray Nodes(IEnumerable<JsonNode?> nodes, int maximum, int minimum = 0)
    { var array = nodes.ToArray(); if (array.Length < minimum || array.Length > maximum) throw new InvalidDataException("上传需求结构超过服务器允许范围。"); return new(array); }
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
    private static string Limited(string value, int minimum, int maximum, string label)
    { if (value.Length < minimum || value.Length > maximum) throw new InvalidDataException(label + "长度超出接口允许范围。"); return value; }
    private static long Quantity(long value, long minimum, long maximum)
    { if (value < minimum || value > maximum) throw new InvalidDataException("需求数量超出接口允许范围。"); return value; }
    private static string Sha(string value, string label)
    { if (value.Length != 64 || value.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c))) throw new InvalidDataException(label + "摘要无效。"); return value; }
    private static DateTimeOffset ServerTime(JsonObject value, string key)
    { if (!DateTimeOffset.TryParse(Text(value, key), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) throw new InvalidDataException("服务器成功时间无效。"); return time; }
    private static string Uuid(string value) => DesktopCloudStateStore.RequireUuid(value);
    private static JsonObject Parse(string text) => JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("云接口对象为空。");
    private static JsonObject Object(JsonNode? value) => value as JsonObject ?? throw new InvalidDataException("云接口对象格式错误。");
    private static JsonArray Array(JsonObject value, string key) => value[key] as JsonArray ?? throw new InvalidDataException("云接口缺少所需列表。");
    private static string Text(JsonObject value, string key) => value[key]?.GetValue<string>() ?? throw new InvalidDataException("云接口缺少所需文字字段。");
    private static string? OptionalText(JsonObject value, string key) => value[key]?.GetValue<string>();
    private static long Integer(JsonObject value, string key, long minimum)
    {
        if (value[key] is not JsonValue number) throw new InvalidDataException("云接口缺少所需整数。");
        // JsonNode parsed from HTTP uses JsonElement, while typed projections can contain Int32.
        // Accept both integer representations, never strings or floating-point guesses.
        if (number.TryGetValue<long>(out var wide)) return Quantity(wide, minimum, long.MaxValue);
        if (number.TryGetValue<int>(out var narrow)) return Quantity(narrow, minimum, long.MaxValue);
        throw new InvalidDataException("云接口整数格式无效。");
    }
    private static bool Bool(JsonObject value, string key) => value[key]?.GetValue<bool>() ?? throw new InvalidDataException("云接口缺少所需状态。");
}
