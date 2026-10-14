using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Grimmcair.Desktop.Services;

/// <summary>
/// An offline business preview. The supplied JSON file holds BOTH local saves and
/// independent simulated cloud snapshots; this service has no network transport.
/// Callers must use its owner-aware operations rather than mutate State directly.
/// </summary>
public sealed class CloudSimulation
{
    public const string PreviewOwnerId = "local:preview:owner";
    // A real server must use its own consent version; preview acknowledgement is not upload consent.
    public const string SharingNoticeVersion = "local-preview:cloud-material-sharing:v1";
    public const string DemoMapVersion = "纯虚构演示配方 v1";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string FilePath { get; }
    public CloudSimulationState State { get; private set; }
    public LocalSaveHistory History { get; }
    private readonly object mutationGate = new();
    public event EventHandler? Changed;
    public IReadOnlyList<CloudAccount> Accounts => State.Accounts;
    public IReadOnlyList<CloudSave> Saves => State.Saves;
    public IReadOnlyList<CloudSnapshot> Snapshots => State.Snapshots;

    public IReadOnlyList<CloudRecipe> DemoEquipment { get; } =
    [
        new() { ItemId = "demo:equip:staff", ItemName = "示范·星火法杖", Materials =
        [
            new() { Stage = "爵", MaterialId = "demo:mat:jue", MaterialName = "示范·爵阶碎片", Quantity = 4 },
            new() { Stage = "土", MaterialId = "demo:mat:tu", MaterialName = "示范·土阶结晶", Quantity = 2 },
            new() { Stage = "马", MaterialId = "demo:mat:ma", MaterialName = "示范·马阶印记", Quantity = 1 }
        ] },
        new() { ItemId = "demo:equip:crown", ItemName = "示范·黎明冠冕", Materials =
        [
            new() { Stage = "爵", MaterialId = "demo:mat:jue", MaterialName = "示范·爵阶碎片", Quantity = 3 },
            new() { Stage = "判", MaterialId = "demo:mat:pan", MaterialName = "示范·判阶精华", Quantity = 2 },
            new() { Stage = "天", MaterialId = "demo:mat:tian", MaterialName = "示范·天阶辉光", Quantity = 1 }
        ] },
        new() { ItemId = "demo:equip:wings", ItemName = "示范·远行羽翼", Materials =
        [
            new() { Stage = "土", MaterialId = "demo:mat:tu", MaterialName = "示范·土阶结晶", Quantity = 3 },
            new() { Stage = "马", MaterialId = "demo:mat:ma", MaterialName = "示范·马阶印记", Quantity = 2 },
            new() { Stage = "天", MaterialId = "demo:mat:tian", MaterialName = "示范·天阶辉光", Quantity = 2 }
        ] }
    ];

    public CloudSimulation(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
        State = Load();
        History = new LocalSaveHistory(FilePath);
        History.Initialize();
        var metadataChanged = false;
        foreach (var save in State.Saves.Where(x => !x.IsDemo))
        {
            if (string.IsNullOrEmpty(save.AcceptedSourceHash) && !save.SourceDetached && !string.IsNullOrEmpty(save.SourceKey))
            { save.AcceptedSourceHash = save.SourceHash; metadataChanged = true; }
            var entry = History.RecordCurrent(save, "初始本地版本");
            if (save.LocalHistoryId != entry.Id) { save.LocalHistoryId = entry.Id; metadataChanged = true; }
        }
        if (metadataChanged) PersistState(State);
        var names = SaveNamePolicy.MigrationNames(State.Saves);
        if (names.Count > 0)
            Mutate(state =>
            {
                foreach (var save in state.Saves)
                    if (names.TryGetValue(save.Id, out var name)) { save.Name = name; MarkChanged(save); }
                return true;
            }, "区分同角色存档名称");
    }

    public CloudAccount CreateAccount(string name, int badgeLevel = 1)
    {
        RequireText(name, "模拟成员名称不能为空。");
        RequireLevel(badgeLevel);
        return Mutate(state =>
        {
            var account = new CloudAccount { Id = NewId(), Name = name.Trim(), BadgeLevel = badgeLevel };
            state.Accounts.Add(account);
            return account;
        });
    }

    public CloudAccount EnsurePreviewAccount()
    {
        var existing = State.Accounts.FirstOrDefault(x => x.Id == PreviewOwnerId);
        if (existing is not null) return existing;
        return Mutate(state =>
        {
            var account = new CloudAccount { Id = PreviewOwnerId, Name = "本地模拟账户", BadgeLevel = 1 };
            state.Accounts.Add(account);
            return account;
        });
    }

    public void SetBadgeLevel(string ownerId, int level)
    {
        RequireLevel(level);
        Mutate(state => { GetAccount(state, ownerId).BadgeLevel = level; return true; });
    }

    public bool HasAcknowledgedSharingNotice(string ownerId) =>
        GetAccount(State, ownerId).CloudSharingNoticeVersion == SharingNoticeVersion;

    public void AcknowledgeSharingNotice(string ownerId) => Mutate(state =>
    {
        GetAccount(state, ownerId).CloudSharingNoticeVersion = SharingNoticeVersion;
        return true;
    });

    public CloudSave ImportSave(string ownerId, CloudImportSave import)
    {
        return ImportOrUpdateSave(ownerId, import);
    }

    /// <summary>
    /// A game save is identified by owner, original account, profession and save
    /// name. A path from another computer updates the same record. SourceKey is
    /// only a fallback for an existing tracked source whose display name changed.
    /// </summary>
    public CloudSave ImportOrUpdateSave(string ownerId, CloudImportSave import)
    {
        ValidateImport(import);
        GetAccount(State, ownerId);
        var importedName = ImportedName(import);
        var existing = State.Saves.FirstOrDefault(x => !x.IsDemo
            && SameRole(x, ownerId, import.GameAccount, import.Profession)
            && string.Equals(x.Name.Trim(), importedName, StringComparison.OrdinalIgnoreCase));
        if (existing is null && !string.IsNullOrWhiteSpace(import.SourceKey))
            existing = State.Saves.FirstOrDefault(x => !x.IsDemo && !x.SourceDetached
                && SameRole(x, ownerId, import.GameAccount, import.Profession) && x.SourceKey == import.SourceKey);
        if (existing is null) return Mutate(state => CreateImportedSave(state, ownerId, import));
        var saveId = existing.Id;
        return Mutate(state =>
        {
            var save = GetSave(state, ownerId, saveId);
            var unchangedSource = !string.IsNullOrEmpty(import.SourceHash) && save.AcceptedSourceHash == import.SourceHash;
            if (unchangedSource && !(save.ParsedResult is null && import.ParsedResult is not null && save.LoadCodes.SequenceEqual(import.LoadCodes)))
            {
                // Refresh path coordinates, not a cloud/restored character with
                // the same old source TXT that the watcher has already accepted.
                if (!string.IsNullOrWhiteSpace(import.SourceKey))
                {
                    save.SourceKey = import.SourceKey; save.SourcePath = import.SourcePath ?? "";
                    save.SourceDetached = false;
                }
                return save;
            }
            var parsed = import.ParsedResult is null && save.SourceHash == import.SourceHash
                ? save.ParsedResult : import.ParsedResult;
            var contentChanged = save.SourceHash != (import.SourceHash ?? "") || save.MapVersion != (import.MapVersion ?? "")
                || !save.LoadCodes.SequenceEqual(import.LoadCodes)
                || !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(save.Inventory, JsonOptions), JsonSerializer.SerializeToNode(import.Inventory, JsonOptions))
                || !JsonNode.DeepEquals(save.ParsedResult, parsed);
            if (!string.IsNullOrWhiteSpace(import.SourceKey))
            {
                save.SourceKey = import.SourceKey;
                save.SourceDetached = false;
            }
            save.SourceHash = import.SourceHash ?? "";
            save.AcceptedSourceHash = import.SourceHash ?? "";
            if (!string.IsNullOrWhiteSpace(import.SourcePath)) save.SourcePath = import.SourcePath;
            save.MapVersion = import.MapVersion ?? "";
            save.LoadCodes = [.. import.LoadCodes];
            save.Inventory = Clone(import.Inventory);
            save.ParsedResult = parsed?.DeepClone().AsObject();
            if (contentChanged) MarkChanged(save);
            return save;
        }, "游戏来源更新");
    }

    public IReadOnlyList<LocalSaveHistoryEntry> ListSaveHistory(string ownerId, string localSaveId)
    {
        GetSave(State, ownerId, localSaveId);
        return History.List(ownerId, localSaveId);
    }

    public LocalSaveHistorySnapshot ReadSaveHistory(string ownerId, string localSaveId, string historyId)
    {
        var current = GetSave(State, ownerId, localSaveId);
        var snapshot = History.Read(ownerId, localSaveId, historyId);
        if (!SameRole(current, snapshot.Save)) throw new InvalidOperationException("历史与当前角色身份不匹配，未应用。");
        return snapshot;
    }

    public CloudSave RestoreSaveHistory(string ownerId, string localSaveId, string historyId)
    {
        var snapshot = ReadSaveHistory(ownerId, localSaveId, historyId);
        return Mutate(state =>
        {
            var current = GetSave(state, ownerId, localSaveId);
            current.Name = SaveNamePolicy.UniqueName(state.Saves, ownerId, current.GameAccount, current.Profession,
                snapshot.Save.Name, current.IsMain, current.Id);
            current.MapVersion = snapshot.Save.MapVersion;
            current.SourceHash = snapshot.Save.SourceHash;
            current.LoadCodes = [.. snapshot.Save.LoadCodes]; current.Inventory = Clone(snapshot.Save.Inventory);
            current.ParsedResult = snapshot.Save.ParsedResult?.DeepClone().AsObject();
            current.Targets = Clone(snapshot.Save.Targets);
            // Id, role/main selection, favorite, source coordinates and accepted
            // source hash, sync configuration and external binding all stay current.
            MarkChanged(current);
            return current;
        }, "恢复本地历史");
    }

    public CloudSave ApplyRemoteSnapshot(string ownerId, string localSaveId, CloudImportSave import, IReadOnlyList<CloudGoal>? goals = null)
    {
        ValidateRemoteImport(ownerId, localSaveId, import, goals);
        return Mutate(state =>
        {
            var save = GetSave(state, ownerId, localSaveId);
            save.Name = SaveNamePolicy.UniqueName(state.Saves, ownerId, save.GameAccount, save.Profession,
                string.IsNullOrWhiteSpace(import.Name) ? save.Name : import.Name.Trim(), save.IsMain, save.Id);
            save.MapVersion = import.MapVersion ?? ""; save.SourceHash = import.SourceHash ?? "";
            save.LoadCodes = [.. import.LoadCodes]; save.Inventory = Clone(import.Inventory);
            save.ParsedResult = import.ParsedResult?.DeepClone().AsObject();
            if (save.IsMain && goals is not null) save.Targets = Clone(goals.ToList());
            if (!LocalSaveHistory.SameContent(GetSave(State, ownerId, localSaveId), save)) MarkChanged(save);
            return save;
        }, "云端最新版本");
    }

    public LocalSaveHistoryEntry RecordRemoteHistoryCandidate(string ownerId, string localSaveId, CloudImportSave import,
        IReadOnlyList<CloudGoal>? goals, bool isMain, string reason = "云端冲突版本")
    {
        lock (mutationGate)
        {
            ValidateRemoteImport(ownerId, localSaveId, import, goals);
            return History.RecordCandidate(GetSave(State, ownerId, localSaveId), import, goals, isMain, reason);
        }
    }

    private void ValidateRemoteImport(string ownerId, string localSaveId, CloudImportSave import, IReadOnlyList<CloudGoal>? goals)
    {
        ValidateImport(import);
        var save = GetSave(State, ownerId, localSaveId);
        if (!SameRole(save, ownerId, import.GameAccount, import.Profession))
            throw new InvalidOperationException("云内容与当前角色身份不匹配，未应用。");
        if (goals is not null && (goals.Any(x => x is null || string.IsNullOrWhiteSpace(x.ItemId) || string.IsNullOrWhiteSpace(x.ItemName) || x.Quantity <= 0)
            || goals.Select(x => x.ItemId).Distinct(StringComparer.Ordinal).Count() != goals.Count))
            throw new InvalidOperationException("云目标内容不正确或重复，未应用。");
    }

    public void RenameSave(string ownerId, string saveId, string name)
    {
        name = SaveNamePolicy.ValidateManualName(name);
        Mutate(state =>
        {
            var save = GetSave(state, ownerId, saveId);
            if (!SaveNamePolicy.IsAvailable(state.Saves, save, name))
                throw new InvalidOperationException("同一角色已有同名存档，请使用不同名称。");
            if (save.Name != name.Trim())
            {
                save.Name = name.Trim();
                MarkChanged(save);
            }
            return true;
        });
    }

    private static void ValidateImport(CloudImportSave import)
    {
        ArgumentNullException.ThrowIfNull(import);
        RequireText(import.GameAccount, "缺少原始游戏账号名，不能归属角色。");
        RequireText(import.Profession, "缺少已解码职业，不能归属角色。");
        if (import.LoadCodes is null || import.LoadCodes.Count == 0 || import.LoadCodes.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("需要保留完整的 load 代码组后才能加入本地模拟。");
        ValidateInventory(import.Inventory);
    }

    private static CloudSave CreateImportedSave(CloudSimulationState state, string ownerId, CloudImportSave import)
    {
        var isMain = !state.Saves.Any(x => SameRole(x, ownerId, import.GameAccount, import.Profession));
        var save = new CloudSave
        {
            Id = NewId(), OwnerId = ownerId,
            Name = SaveNamePolicy.UniqueName(state.Saves, ownerId, import.GameAccount, import.Profession,
                ImportedName(import), isMain),
            GameAccount = import.GameAccount, Profession = import.Profession,
            MapVersion = import.MapVersion ?? "", SourceHash = import.SourceHash ?? "", SourcePath = import.SourcePath ?? "",
            AcceptedSourceHash = string.IsNullOrEmpty(import.SourceKey) ? "" : import.SourceHash ?? "",
            SourceKey = import.SourceKey ?? "", ParsedResult = import.ParsedResult?.DeepClone().AsObject(),
            LoadCodes = [.. import.LoadCodes], Inventory = Clone(import.Inventory),
            IsMain = isMain,
            IsDemo = false, SyncEnabled = false
        };
        state.Saves.Add(save);
        return save;
    }

    private static string ImportedName(CloudImportSave import) => SaveNamePolicy.NewNameBase(
        string.IsNullOrWhiteSpace(import.Name) ? $"{import.GameAccount}_{import.Profession}" : import.Name.Trim());

    public CloudSave? FindSave(string ownerId, string saveId)
    {
        GetAccount(State, ownerId);
        return State.Saves.FirstOrDefault(x => x.OwnerId == ownerId && x.Id == saveId);
    }

    public CloudSave? FindSaveBySourceHash(string ownerId, string? sourceHash)
    {
        GetAccount(State, ownerId);
        if (string.IsNullOrWhiteSpace(sourceHash)) return null;
        return State.Saves.FirstOrDefault(x => x.OwnerId == ownerId && (x.SourceHash == sourceHash || !x.SourceDetached && x.AcceptedSourceHash == sourceHash));
    }

    public CloudSave DuplicateSave(string ownerId, string saveId, string? displayName = null)
    {
        return Mutate(state =>
        {
            var original = GetSave(state, ownerId, saveId);
            var copy = Clone(original);
            copy.Id = NewId();
            copy.Name = SaveNamePolicy.UniqueName(state.Saves, ownerId, original.GameAccount, original.Profession,
                string.IsNullOrWhiteSpace(displayName) ? SaveNamePolicy.WithSuffix(original.Name, "_副档")
                    : SaveNamePolicy.NewNameBase(displayName.Trim()), isMain: false);
            copy.IsMain = false;
            copy.IsFavorite = false;
            copy.SyncEnabled = false;
            copy.SourceKey = "";
            copy.SourceDetached = true;
            copy.AcceptedSourceHash = ""; copy.LocalHistoryId = "";
            copy.Targets.Clear();
            copy.Revision = 1;
            copy.LastSyncStatus = "未启用";
            copy.LastSyncError = "";
            copy.LastSyncAt = null;
            state.Saves.Add(copy);
            return copy;
        });
    }

    /// <summary>A cloud download is always an independent local branch, never a source update.</summary>
    public CloudSave ImportCloudContent(string ownerId, CloudImportSave import)
    {
        ValidateImport(import);
        return Mutate(state =>
        {
            GetAccount(state, ownerId);
            var copy = CreateImportedSave(state, ownerId, import);
            copy.SourceDetached = true;
            copy.SourceKey = "";
            copy.SourcePath = "";
            copy.AcceptedSourceHash = "";
            // With no local character yet, the sole local branch is its local main.
            // This does not claim the cloud main or import its goals/sync preference.
            return copy;
        });
    }

    /// <summary>Apply an accepted remote main selection and its goals; other branches keep their local goals.</summary>
    public void ApplyRemoteMain(string ownerId, string saveId, List<CloudGoal> goals)
    {
        Mutate(state =>
        {
            var selected = GetSave(state, ownerId, saveId);
            foreach (var save in state.Saves.Where(x => SameRole(x, selected)))
            {
                var isMain = save.Id == saveId;
                var sameGoals = !isMain || JsonSerializer.Serialize(save.Targets, JsonOptions) == JsonSerializer.Serialize(goals, JsonOptions);
                if (save.IsMain != isMain || !sameGoals)
                {
                    save.IsMain = isMain;
                    if (isMain) save.Targets = Clone(goals);
                    MarkChanged(save);
                }
            }
            return true;
        });
    }

    public void SetMain(string ownerId, string saveId)
    {
        Mutate(state =>
        {
            var selected = GetSave(state, ownerId, saveId);
            if (selected.IsMain) return true;
            var roleSaves = state.Saves.Where(x => SameRole(x, selected)).ToList();
            foreach (var save in roleSaves)
            {
                if (save.IsMain || save.Id == saveId)
                {
                    save.IsMain = save.Id == saveId;
                    MarkChanged(save);
                }
            }
            return true;
        });
    }

    public void SetFavorite(string ownerId, string saveId, bool favorite)
    {
        if (GetSave(State, ownerId, saveId).IsFavorite == favorite) return;
        Mutate(state =>
        {
            GetSave(state, ownerId, saveId).IsFavorite = favorite;
            return true;
        });
    }

    public void SetSyncEnabled(string ownerId, string saveId, bool enabled)
    {
        Mutate(state =>
        {
            var save = GetSave(state, ownerId, saveId);
            save.SyncEnabled = enabled;
            save.LastSyncStatus = enabled ? (IsDirty(state, save) ? "待同步" : "已同步") : "未启用";
            save.LastSyncError = "";
            return true;
        });
    }

    /// <summary>Add this save's local target, or replace its requested quantity if already present.</summary>
    public void AddTarget(string ownerId, string saveId, string itemId, string name, int quantity = 1)
    {
        RequireText(itemId, "请选择目标装备。");
        RequireText(name, "目标装备名称不能为空。");
        if (quantity <= 0) throw new InvalidOperationException("目标数量必须大于零。");
        Mutate(state =>
        {
            var save = GetSave(state, ownerId, saveId);
            var target = save.Targets.FirstOrDefault(x => x.ItemId == itemId);
            if (target is null) save.Targets.Add(new CloudGoal { ItemId = itemId, ItemName = name, Quantity = quantity });
            else { target.Quantity = quantity; target.ItemName = name; }
            MarkChanged(save);
            return true;
        });
    }

    public void RemoveTarget(string ownerId, string saveId, string itemId)
    {
        Mutate(state =>
        {
            var save = GetSave(state, ownerId, saveId);
            if (save.Targets.RemoveAll(x => x.ItemId == itemId) > 0) MarkChanged(save);
            return true;
        });
    }

    public void SetDemoInventory(string ownerId, string saveId, string itemId, int quantity)
    {
        if (quantity < 0) throw new InvalidOperationException("库存数量不能小于零。");
        var names = DemoEquipment.SelectMany(x => x.Materials).Select(x => (Id: x.MaterialId, Name: x.MaterialName))
            .Concat(DemoEquipment.Select(x => (Id: x.ItemId, Name: x.ItemName)));
        var name = names.FirstOrDefault(x => x.Id == itemId).Name;
        if (name is null) throw new InvalidOperationException("这里只能修改明确标注为示范的材料或装备库存。");
        Mutate(state =>
        {
            var save = GetSave(state, ownerId, saveId);
            if (!save.IsDemo) throw new InvalidOperationException("真实导入的存档库存只读，请重新导入新的存档。");
            var item = save.Inventory.FirstOrDefault(x => x.ItemId == itemId);
            if (item is null) save.Inventory.Add(new CloudInventoryItem { ItemId = itemId, ItemName = name, Quantity = quantity });
            else item.Quantity = quantity;
            MarkChanged(save);
            return true;
        });
    }

    public string GetSyncStatus(string ownerId, string saveId)
    {
        var save = GetSave(State, ownerId, saveId);
        if (!save.SyncEnabled) return "未启用";
        if (save.LastSyncStatus == "失败") return "失败";
        return IsDirty(State, save) ? "待同步" : "已同步";
    }

    public bool IsDirty(string ownerId, string saveId) => IsDirty(State, GetSave(State, ownerId, saveId));

    /// <summary>
    /// Atomically replace all selected slots for one owner. A full cloud rejects a
    /// batch containing new slots; already existing slots remain independently updatable.
    /// </summary>
    public int Sync(string ownerId, bool simulateOffline = false)
    {
        GetAccount(State, ownerId);
        var selected = State.Saves.Where(x => x.OwnerId == ownerId && x.SyncEnabled).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("请先为需要更新的存档开启云同步。");
        return SyncSelected(ownerId, selected.Select(s => s.Id).ToHashSet(), simulateOffline);
    }

    /// <summary>Update only one enabled, changed slot. Clean successful slots are a no-op.</summary>
    public bool SyncSave(string ownerId, string saveId, bool simulateOffline = false)
    {
        var save = GetSave(State, ownerId, saveId);
        if (!save.SyncEnabled) throw new InvalidOperationException("请先为该存档开启云同步。");
        if (!IsDirty(State, save) && save.LastSyncStatus != "失败") return false;
        SyncSelected(ownerId, [saveId], simulateOffline);
        return true;
    }

    /// <summary>Automatic local preview updates skip unchanged slots and previous failures until a retry is requested.</summary>
    public (int Updated, int Failed) SyncPending(string ownerId, bool retryFailures = false, bool simulateOffline = false)
    {
        GetAccount(State, ownerId);
        var pending = State.Saves.Where(s => s.OwnerId == ownerId && s.SyncEnabled
                && (retryFailures || s.LastSyncStatus != "失败")
                && (IsDirty(State, s) || s.LastSyncStatus == "失败"))
            .Select(s => s.Id).ToList();
        var updated = 0; var failed = 0;
        foreach (var saveId in pending)
        {
            try { if (SyncSave(ownerId, saveId, simulateOffline)) updated++; }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
            { failed++; }
        }
        return (updated, failed);
    }

    private int SyncSelected(string ownerId, HashSet<string> selectedIds, bool simulateOffline)
    {
        var owner = GetAccount(State, ownerId);
        var selected = State.Saves.Where(x => x.OwnerId == ownerId && selectedIds.Contains(x.Id)).ToList();
        try
        {
            if (simulateOffline)
                throw new InvalidOperationException("模拟离线：本次未更新云端存档，仍保留上次成功结果。");
            var current = State.Snapshots.Where(x => x.OwnerId == ownerId).ToList();
            var newCount = selected.Count(x => current.All(s => s.SaveId != x.Id));
            if (newCount > 0 && current.Count + newCount > owner.Capacity)
                throw new InvalidOperationException($"模拟云槽位不足：已占 {current.Count} / {owner.Capacity}，本次还需新增 {newCount} 格。此次更新未写入，已有云端存档仍保留。");
            return Mutate(state =>
            {
                var now = DateTimeOffset.UtcNow;
                var saves = state.Saves.Where(x => x.OwnerId == ownerId && selectedIds.Contains(x.Id)).ToList();
                foreach (var save in saves)
                {
                    var snapshot = JsonSerializer.Deserialize<CloudSnapshot>(JsonSerializer.Serialize(save, JsonOptions), JsonOptions)!;
                    snapshot.IsFavorite = false;
                    snapshot.SaveId = save.Id;
                    snapshot.SyncedAt = now;
                    snapshot.LastSyncAt = now;
                    snapshot.LastSyncStatus = "已同步";
                    snapshot.LastSyncError = "";
                    state.Snapshots.RemoveAll(x => x.OwnerId == ownerId && x.SaveId == save.Id);
                    state.Snapshots.Add(snapshot);
                    save.LastSyncAt = now;
                    save.LastSyncStatus = "已同步";
                    save.LastSyncError = "";
                }
                // A primary selection belongs to the role. Update its cloud metadata as
                // part of the SAME successful transaction, even for an unselected old slot.
                foreach (var role in saves.GroupBy(x => (x.GameAccount, x.Profession)))
                {
                    var main = state.Saves.Single(x => x.OwnerId == ownerId && x.GameAccount == role.Key.GameAccount
                        && x.Profession == role.Key.Profession && x.IsMain);
                    foreach (var snapshot in state.Snapshots.Where(x => SameRole(x, main)))
                    {
                        var isMain = snapshot.SaveId == main.Id;
                        if (snapshot.IsMain != isMain)
                        {
                            snapshot.IsMain = isMain;
                            snapshot.SyncedAt = now;
                            snapshot.LastSyncAt = now;
                            // Do not promote a stale snapshot with unsynced local targets.
                            // A current main only becomes a cloud main when included above.
                            if (isMain && !selectedIds.Contains(main.Id)) snapshot.IsMain = false;
                        }
                        if (!snapshot.IsMain) snapshot.Targets.Clear();
                    }
                }
                return saves.Count;
            });
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            RecordSyncFailure(ownerId, selectedIds, error.Message);
            throw;
        }
    }

    private void RecordSyncFailure(string ownerId, HashSet<string> selectedIds, string message)
    {
        void Record(CloudSimulationState state)
        {
            foreach (var save in state.Saves.Where(s => s.OwnerId == ownerId && selectedIds.Contains(s.Id)))
            { save.LastSyncStatus = "失败"; save.LastSyncError = message; }
        }
        try { Mutate(state => { Record(state); return true; }); }
        catch (InvalidOperationException)
        {
            // A full or read-only disk may also prevent persisting the failure marker.
            // Preserve the last cloud snapshot and still expose this session's failure in the UI.
            Record(State);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void DeleteCloudCopy(string ownerId, string saveId)
    {
        Mutate(state =>
        {
            var save = GetSave(state, ownerId, saveId);
            state.Snapshots.RemoveAll(x => x.OwnerId == ownerId && x.SaveId == saveId);
            save.SyncEnabled = false;
            save.LastSyncStatus = "未启用";
            save.LastSyncError = "";
            return true;
        });
    }

    /// <summary>Retrieve the last successful cloud content into a new local branch.</summary>
    public CloudSave DownloadCloudCopy(string ownerId, string saveId)
    {
        return Mutate(state =>
        {
            GetSave(state, ownerId, saveId);
            var snapshot = state.Snapshots.FirstOrDefault(x => x.OwnerId == ownerId && x.SaveId == saveId)
                ?? throw new InvalidOperationException("当前档位没有已成功同步的模拟云副本，暂时无法取回。");
            var copy = JsonSerializer.Deserialize<CloudSave>(JsonSerializer.Serialize(snapshot, JsonOptions), JsonOptions)!;
            copy.Id = NewId();
            copy.Name = SaveNamePolicy.UniqueName(state.Saves, ownerId, snapshot.GameAccount, snapshot.Profession,
                SaveNamePolicy.WithSuffix(snapshot.Name, "_云端取回"), isMain: false);
            copy.IsMain = false;
            copy.IsFavorite = false;
            copy.SyncEnabled = false;
            copy.SourceKey = "";
            copy.SourceDetached = true;
            copy.AcceptedSourceHash = ""; copy.LocalHistoryId = "";
            copy.Targets.Clear();
            copy.Revision = 1;
            copy.LastSyncStatus = "未启用";
            copy.LastSyncError = "";
            copy.LastSyncAt = null;
            state.Saves.Add(copy);
            return copy;
        });
    }

    public CloudNeedsReport GetNeeds(string ownerId, string saveId) => CalculateNeeds(GetSave(State, ownerId, saveId));

    public List<CloudSyncedNeed> GetSyncedNeeds(string ownerId)
    {
        GetAccount(State, ownerId);
        return State.Snapshots.Where(x => x.IsMain && x.OwnerId == ownerId)
            .Select(x => new CloudSyncedNeed
            {
                OwnerId = x.OwnerId, OwnerName = GetAccount(State, x.OwnerId).Name,
                GameAccount = x.GameAccount, Profession = x.Profession, SaveName = x.Name,
                SyncedAt = x.SyncedAt, Targets = Clone(x.Targets), Report = CalculateNeeds(x)
            }).OrderBy(x => x.OwnerName).ThenBy(x => x.GameAccount).ThenBy(x => x.Profession).ToList();
    }

    public void LoadDemoData()
    {
        EnsurePreviewAccount();
        const string firstMainSeed = "demo:seed:preview:account-a:main";
        const string branchSeed = "demo:seed:preview:account-a:branch";
        const string secondMainSeed = "demo:seed:preview:account-b:main";
        bool HasSeed(CloudSimulationState state, string seed) =>
            state.Saves.Any(x => x.OwnerId == PreviewOwnerId && x.IsDemo && x.SourceHash == seed);
        if (HasSeed(State, firstMainSeed) && HasSeed(State, branchSeed) && HasSeed(State, secondMainSeed)) return;
        Mutate(state =>
        {
            if (!HasSeed(state, firstMainSeed))
            {
                var main = !state.Saves.Any(x => SameRole(x, PreviewOwnerId, "示范账号A", "巫术师"));
                var primary = NewDemo(PreviewOwnerId, "示范账号A", "巫术师", "示范·账号A 养成主档", main, 5, 1, 0, 0, 0);
                primary.SourceHash = firstMainSeed;
                if (main) primary.Targets = [Goal("demo:equip:staff"), Goal("demo:equip:crown")];
                state.Saves.Add(primary);
            }
            if (!HasSeed(state, branchSeed))
            {
                var branch = NewDemo(PreviewOwnerId, "示范账号A", "巫术师", "示范·账号A 攻略副档", false, 99, 99, 99, 99, 99);
                branch.SourceHash = branchSeed;
                state.Saves.Add(branch);
            }
            if (!HasSeed(state, secondMainSeed))
            {
                var main = !state.Saves.Any(x => SameRole(x, PreviewOwnerId, "示范账号B", "巫术师"));
                var second = NewDemo(PreviewOwnerId, "示范账号B", "巫术师", "示范·账号B 独立角色", main, 0, 1, 0, 0, 0);
                second.SourceHash = secondMainSeed;
                if (main) second.Targets = [Goal("demo:equip:wings")];
                state.Saves.Add(second);
            }
            return true;
        });
    }

    /// <summary>Remove only explicitly marked fixture saves, never source files or real imports.</summary>
    public int RemoveDemoSaves()
    {
        var ids = State.Saves.Where(x => x.IsDemo).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) return 0;
        return Mutate(state =>
        {
            state.Snapshots.RemoveAll(x => x.IsDemo || ids.Contains(x.SaveId));
            state.Saves.RemoveAll(x => ids.Contains(x.Id));
            foreach (var role in state.Saves.GroupBy(x => (x.OwnerId, x.GameAccount, x.Profession)))
                if (!role.Any(x => x.IsMain)) role.First().IsMain = true;
            return ids.Count;
        });
    }

    private CloudGoal Goal(string id)
    {
        var recipe = DemoEquipment.Single(x => x.ItemId == id);
        return new CloudGoal { ItemId = id, ItemName = recipe.ItemName, Quantity = 1 };
    }

    private CloudSave NewDemo(string owner, string gameAccount, string profession, string name, bool main, params int[] quantities)
    {
        var materialOrder = new[] { "爵", "土", "马", "判", "天" };
        var materials = DemoEquipment.SelectMany(x => x.Materials).DistinctBy(x => x.MaterialId)
            .OrderBy(x => Array.IndexOf(materialOrder, x.Stage)).ToList();
        return new CloudSave
        {
            Id = NewId(), OwnerId = owner, Name = name, GameAccount = gameAccount, Profession = profession,
            IsDemo = true, IsMain = main, MapVersion = DemoMapVersion, SyncEnabled = false,
            SourceHash = "demo:" + NewId(), SourcePath = "纯虚构演示，不对应任何玩家文件",
            LoadCodes = ["示范代码（不可用于游戏）"],
            Inventory = materials.Select((x, i) => new CloudInventoryItem
                { ItemId = x.MaterialId, ItemName = x.MaterialName, Quantity = quantities[i] }).ToList()
        };
    }

    private CloudNeedsReport CalculateNeeds(CloudSave save)
    {
        var report = new CloudNeedsReport();
        try
        {
            var inventory = save.Inventory.GroupBy(x => x.ItemId)
                .ToDictionary(x => x.Key, x => x.Sum(i => i.Quantity), StringComparer.Ordinal);
            var required = new Dictionary<string, CloudMaterialNeed>(StringComparer.Ordinal);
            foreach (var goal in save.Targets)
            {
                var recipe = save.IsDemo && save.MapVersion == DemoMapVersion
                    ? DemoEquipment.FirstOrDefault(x => x.ItemId == goal.ItemId) : null;
                if (recipe is null)
                {
                    report.PendingTargets.Add($"{goal.ItemName} × {goal.Quantity}：配方待核对，暂不能计算材料缺口");
                    continue;
                }
                var ownedEquipment = inventory.GetValueOrDefault(goal.ItemId);
                var consumedEquipment = Math.Min(ownedEquipment, goal.Quantity);
                inventory[goal.ItemId] = ownedEquipment - consumedEquipment;
                var remaining = goal.Quantity - consumedEquipment;
                foreach (var ingredient in recipe.Materials)
                {
                    if (!required.TryGetValue(ingredient.MaterialId, out var need))
                    {
                        need = new CloudMaterialNeed { Stage = ingredient.Stage, MaterialId = ingredient.MaterialId, MaterialName = ingredient.MaterialName };
                        required.Add(ingredient.MaterialId, need);
                    }
                    need.Required = checked(need.Required + checked(ingredient.Quantity * remaining));
                }
            }
            foreach (var need in required.Values)
            {
                need.Owned = inventory.GetValueOrDefault(need.MaterialId);
                need.Missing = Math.Max(0, need.Required - need.Owned);
            }
            var stages = new[] { "爵", "土", "马", "判", "天" };
            report.Needs = required.Values.OrderByDescending(x => Array.IndexOf(stages, x.Stage)).ThenBy(x => x.MaterialName).ToList();
            return report;
        }
        catch (OverflowException)
        {
            throw new InvalidOperationException("目标或库存数量超出当前模拟支持的范围，请减少数量。");
        }
    }

    private CloudSimulationState Load()
    {
        if (!File.Exists(FilePath)) return new CloudSimulationState();
        try
        {
            var json = File.ReadAllText(FilePath);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("marker", out var marker)
                || marker.GetString() != "Grimmcair.LocalCloudSimulation"
                || !document.RootElement.TryGetProperty("formatVersion", out var version) || version.GetInt32() != 1
                || !document.RootElement.TryGetProperty("isSimulation", out var simulation) || simulation.ValueKind != JsonValueKind.True
                || !document.RootElement.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array
                || !document.RootElement.TryGetProperty("saves", out var saves) || saves.ValueKind != JsonValueKind.Array
                || !document.RootElement.TryGetProperty("snapshots", out var snapshots) || snapshots.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("文件不属于当前版本的本地云存档模拟。");
            var state = JsonSerializer.Deserialize<CloudSimulationState>(json, JsonOptions)
                ?? throw new InvalidOperationException("模拟文件内容为空。");
            // Existing same-named branches are migrated after their history baseline is safe.
            ValidateState(state, enforceUniqueNames: false);
            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidOperationException($"无法读取本地云存档模拟文件，原文件保持不变。文件：{FilePath}。原因：{ex.Message}", ex);
        }
    }

    private T Mutate<T>(Func<CloudSimulationState, T> operation, string reason = "本地内容更新")
    {
        T result;
        lock (mutationGate)
        {
            var next = Clone(State);
            try
            {
                result = operation(next);
                ValidateState(next);
                foreach (var save in next.Saves.Where(x => !x.IsDemo))
                {
                    var old = State.Saves.FirstOrDefault(x => x.Id == save.Id && x.OwnerId == save.OwnerId);
                    if (old is not null && LocalSaveHistory.SameContent(old, save))
                    { save.LocalHistoryId = old.LocalHistoryId; continue; }
                    if (old is not null) save.LocalHistoryId = History.RecordCurrent(old, "更新前本地版本").Id;
                    save.LocalHistoryId = History.RecordCurrent(save, old is null ? "新本地存档" : reason).Id;
                }
                PersistState(next);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or OverflowException)
            {
                throw new InvalidOperationException("本地存档未能安全保存，本次操作未生效。", ex);
            }
            State = next;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private void PersistState(CloudSimulationState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, JsonOptions);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporaryPath = FilePath + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { throw new InvalidOperationException("本地存档状态未能安全保存，原状态文件保持不变。", error); }
    }

    private static void ValidateState(CloudSimulationState state, bool enforceUniqueNames = true)
    {
        if (state.FormatVersion != 1 || state.Marker != "Grimmcair.LocalCloudSimulation" || !state.IsSimulation
            || state.Accounts is null || state.Saves is null || state.Snapshots is null)
            throw new InvalidOperationException("模拟文件格式或模拟标识不正确。");
        if (state.Accounts.Any(x => x is null || string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Name))
            || state.Accounts.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != state.Accounts.Count)
            throw new InvalidOperationException("模拟成员信息缺失或重复。");
        foreach (var account in state.Accounts) RequireLevel(account.BadgeLevel);
        if (state.Saves.Any(x => x is null) || state.Snapshots.Any(x => x is null)
            || state.Saves.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != state.Saves.Count
            || state.Snapshots.Select(x => (x.OwnerId, x.SaveId)).Distinct().Count() != state.Snapshots.Count)
            throw new InvalidOperationException("模拟档位为空或重复。");
        foreach (var save in state.Saves.Cast<CloudSave>().Concat(state.Snapshots))
        {
            if (string.IsNullOrWhiteSpace(save.Id) || string.IsNullOrWhiteSpace(save.Name)
                || string.IsNullOrWhiteSpace(save.GameAccount) || string.IsNullOrWhiteSpace(save.Profession)
                || save.Revision < 1 || save.Targets is null || save.LoadCodes is null || save.LoadCodes.Any(x => x is null))
                throw new InvalidOperationException("模拟存档的身份、目标或代码信息不完整。");
            GetAccount(state, save.OwnerId);
            ValidateInventory(save.Inventory);
            if (save.Targets.Any(x => x is null || string.IsNullOrWhiteSpace(x.ItemId) || string.IsNullOrWhiteSpace(x.ItemName) || x.Quantity <= 0)
                || save.Targets.Select(x => x.ItemId).Distinct(StringComparer.Ordinal).Count() != save.Targets.Count)
                throw new InvalidOperationException("目标装备内容不正确或重复。");
            if (save is CloudSnapshot && !save.IsMain && save.Targets.Count > 0)
                throw new InvalidOperationException("云端副档不能持有公开装备目标。");
            if (save.IsDemo && save.MapVersion != DemoMapVersion) throw new InvalidOperationException("示范存档不能使用真实地图配方版本。");
        }
        if (state.Saves.GroupBy(x => (x.OwnerId, x.GameAccount, x.Profession)).Any(x => x.Count(s => s.IsMain) != 1))
            throw new InvalidOperationException("每个本地角色必须且只能有一个主档。");
        if (enforceUniqueNames && !SaveNamePolicy.AreUnique(state.Saves))
            throw new InvalidOperationException("同一角色的主副档名称必须能够区分。");
        if (state.Snapshots.GroupBy(x => (x.OwnerId, x.GameAccount, x.Profession)).Any(x => x.Count(s => s.IsMain) > 1))
            throw new InvalidOperationException("同一个云端角色不能有多个主档。");
        foreach (var snapshot in state.Snapshots)
        {
            var local = GetSave(state, snapshot.OwnerId, snapshot.SaveId);
            if (snapshot.Id != snapshot.SaveId || !SameRole(local, snapshot) || snapshot.SyncedAt == default)
                throw new InvalidOperationException("模拟云副本的档位归属或成功时间不正确。");
        }
    }

    private static void ValidateInventory(List<CloudInventoryItem>? inventory)
    {
        if (inventory is null || inventory.Any(x => x is null || string.IsNullOrWhiteSpace(x.ItemId) || string.IsNullOrWhiteSpace(x.ItemName) || x.Quantity < 0))
            throw new InvalidOperationException("存档库存的物品标识、名称或数量不正确。");
    }

    private static CloudAccount GetAccount(CloudSimulationState state, string ownerId) =>
        state.Accounts.FirstOrDefault(x => x.Id == ownerId)
        ?? throw new InvalidOperationException("找不到该模拟成员，请先选择一个成员。");

    private static CloudSave GetSave(CloudSimulationState state, string ownerId, string saveId)
    {
        GetAccount(state, ownerId);
        return state.Saves.FirstOrDefault(x => x.Id == saveId && x.OwnerId == ownerId)
            ?? throw new InvalidOperationException("找不到当前成员的这份存档，不能操作其他成员的档位。");
    }

    private static bool SameRole(CloudSave first, CloudSave second) => SameRole(first, second.OwnerId, second.GameAccount, second.Profession);
    private static bool SameRole(CloudSave save, string ownerId, string gameAccount, string profession) =>
        save.OwnerId == ownerId && save.GameAccount == gameAccount && save.Profession == profession;

    private static bool IsDirty(CloudSimulationState state, CloudSave save)
    {
        var snapshot = state.Snapshots.FirstOrDefault(x => x.OwnerId == save.OwnerId && x.SaveId == save.Id);
        return snapshot is null || snapshot.Revision != save.Revision || snapshot.IsMain != save.IsMain;
    }

    private static void MarkChanged(CloudSave save)
    {
        save.Revision = checked(save.Revision + 1);
        save.LastSyncStatus = save.SyncEnabled ? "待同步" : "未启用";
        save.LastSyncError = "";
    }

    private static void RequireLevel(int level)
    {
        if (level is < 1 or > 13) throw new InvalidOperationException("模拟冒险家徽章等级应为 1 到 13 级。");
    }

    private static void RequireText(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(message);
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;
}
