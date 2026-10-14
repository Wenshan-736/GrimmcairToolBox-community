using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Grimmcair.Desktop.Services;

public sealed class LocalSaveHistoryEntry
{
    public string Id { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; }
    public string BeijingTimeName { get; init; } = "";
    public string SaveName { get; init; } = "";
    public bool IsMain { get; init; }
    public int TargetCount { get; init; }
    public string LevelText { get; init; } = "";
    public int InventoryItemCount { get; init; }
    public string Reason { get; init; } = "";
    public bool IsCandidate { get; init; }
}

public sealed class LocalSaveHistorySnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public string Marker { get; init; } = "Grimmcair.LocalSaveHistory";
    public LocalSaveHistoryEntry Entry { get; init; } = new();
    public string OwnerId { get; init; } = "";
    public string LocalSaveId { get; init; } = "";
    public string ParentHistoryId { get; init; } = "";
    public string ContentFingerprint { get; init; } = "";
    public string SnapshotSha256 { get; init; } = "";
    public CloudSave Save { get; init; } = new();
}

/// <summary>
/// Append-only private local snapshots. Each configuration/owner/local-save
/// branch is isolated; remote candidates never become a current dedupe head.
/// Callers commit LocalHistoryId together with their current-state file after
/// these snapshots have been flushed. No history body is written to logs.
/// </summary>
public sealed class LocalSaveHistory
{
    private static readonly JsonSerializerOptions Options = new()
    { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object gate = new();
    private readonly Func<DateTimeOffset> clock;
    private readonly string legacyRoot;
    private readonly string stateHash;
    private readonly string mutexName;
    public string DirectoryPath { get; }

    public LocalSaveHistory(string stateFilePath, Func<DateTimeOffset>? clock = null)
    {
        var fullPath = Path.GetFullPath(stateFilePath);
        var stateDirectory = Path.GetDirectoryName(fullPath)!;
        legacyRoot = Path.Combine(stateDirectory, "save-history");
        stateHash = Hash(Path.GetFileName(fullPath));
        DirectoryPath = Path.Combine(stateDirectory, "save-history-v2");
        mutexName = "Grimmcair.LocalHistory." + Hash(stateDirectory.ToUpperInvariant());
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Initialize() => WithHistoryLock(() => true);

    public IReadOnlyList<LocalSaveHistoryEntry> List(string ownerId, string localSaveId)
    {
        return WithHistoryLock(() => ReadBranch(ownerId, localSaveId).Select(x => x.Entry)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id, StringComparer.Ordinal).ToList());
    }

    public LocalSaveHistorySnapshot Read(string ownerId, string localSaveId, string historyId)
    {
        return WithHistoryLock(() =>
        {
            ValidateId(historyId);
            return ReadFile(Path.Combine(Branch(ownerId, localSaveId), historyId + ".json"), ownerId, localSaveId);
        });
    }

    public LocalSaveHistoryEntry RecordCurrent(CloudSave save, string reason)
    {
        return WithHistoryLock(() =>
        {
            ValidateSave(save);
            var fingerprint = Fingerprint(save);
            if (!string.IsNullOrEmpty(save.LocalHistoryId))
            {
                ValidateId(save.LocalHistoryId);
                var headPath = Path.Combine(Branch(save.OwnerId, save.Id), save.LocalHistoryId + ".json");
                if (File.Exists(headPath))
                {
                    var head = ReadFile(headPath, save.OwnerId, save.Id);
                    if (!head.Entry.IsCandidate && head.ContentFingerprint == fingerprint) return head.Entry;
                }
            }
            // A state-file write may fail after the snapshot was safely written.
            // Reuse that same proposed child, rather than growing repeated copies.
            var pending = ReadBranch(save.OwnerId, save.Id).Where(x => !x.Entry.IsCandidate
                && x.ParentHistoryId == save.LocalHistoryId && x.ContentFingerprint == fingerprint)
                .OrderByDescending(x => x.Entry.CreatedAtUtc).FirstOrDefault();
            return pending?.Entry ?? Append(save, fingerprint, reason, false);
        });
    }

    public LocalSaveHistoryEntry RecordCandidate(CloudSave branch, CloudImportSave remote,
        IReadOnlyList<CloudGoal>? goals, bool isMain, string reason = "云端冲突版本")
    {
        return WithHistoryLock(() =>
        {
            ValidateSave(branch);
            if (remote is null || remote.GameAccount != branch.GameAccount || remote.Profession != branch.Profession
                || remote.LoadCodes is null || remote.LoadCodes.Count == 0 || remote.LoadCodes.Any(string.IsNullOrWhiteSpace)
                || remote.Inventory is null)
                throw new InvalidOperationException("云候选内容或角色身份不正确，未保存。");
            var candidate = Copy(branch);
            candidate.Name = string.IsNullOrWhiteSpace(remote.Name) ? branch.Name : remote.Name.Trim();
            candidate.MapVersion = remote.MapVersion ?? "";
            candidate.SourceHash = remote.SourceHash ?? "";
            candidate.LoadCodes = [.. remote.LoadCodes]; candidate.Inventory = Copy(remote.Inventory);
            candidate.ParsedResult = remote.ParsedResult?.DeepClone().AsObject();
            candidate.IsMain = isMain;
            // Remote secondary saves carry no public goals. Their code-only versions
            // retain this branch's private local goals when offered for restoration.
            if (isMain && goals is not null) candidate.Targets = Copy(goals.ToList());
            ValidateSave(candidate);
            var fingerprint = Fingerprint(candidate);
            var existing = ReadBranch(branch.OwnerId, branch.Id).Where(x => x.Entry.IsCandidate
                && x.ParentHistoryId == branch.LocalHistoryId && x.ContentFingerprint == fingerprint
                && x.Save.IsMain == isMain).OrderByDescending(x => x.Entry.CreatedAtUtc).FirstOrDefault();
            return existing?.Entry ?? Append(candidate, fingerprint, reason, true);
        });
    }

    public static bool SameContent(CloudSave first, CloudSave second) => Fingerprint(first) == Fingerprint(second);
    public static string Fingerprint(CloudSave save)
    {
        var parsed = save.ParsedResult?.DeepClone().AsObject();
        // Decoder presentation/source coordinates are not character content.
        foreach (var key in new[] { "source", "code_groups", "plaintext_comparison", "limitations" }) parsed?.Remove(key);
        var content = JsonSerializer.SerializeToNode(new
        {
            save.GameAccount, save.Profession, save.Name, save.MapVersion, save.LoadCodes,
            Inventory = save.Inventory.OrderBy(x => x.ItemId, StringComparer.Ordinal).ThenBy(x => x.ItemName, StringComparer.Ordinal).ThenBy(x => x.Quantity),
            Targets = save.Targets.OrderBy(x => x.ItemId, StringComparer.Ordinal), ParsedResult = parsed
        }, Options)!;
        return Hash(Canonical(content).ToJsonString());
    }

    private LocalSaveHistoryEntry Append(CloudSave save, string fingerprint, string reason, bool candidate)
    {
        try
        {
            var branch = Branch(save.OwnerId, save.Id);
            var created = clock().ToUniversalTime();
            var latest = ReadBranch(save.OwnerId, save.Id).Select(x => x.Entry.CreatedAtUtc).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
            if (created <= latest) created = latest.AddTicks(1);
            var id = created.UtcTicks.ToString("D19", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            var snapshotSave = Copy(save); snapshotSave.LocalHistoryId = id;
            var entry = new LocalSaveHistoryEntry
            {
                Id = id, CreatedAtUtc = created,
                BeijingTimeName = created.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
                SaveName = save.Name, IsMain = save.IsMain, TargetCount = save.Targets.Count,
                LevelText = Level(save),
                InventoryItemCount = save.Inventory.Count(x => x.Quantity > 0),
                Reason = string.IsNullOrWhiteSpace(reason) ? "本地内容更新" : reason.Trim(), IsCandidate = candidate
            };
            var snapshot = new LocalSaveHistorySnapshot
            {
                Entry = entry, OwnerId = save.OwnerId, LocalSaveId = save.Id, ParentHistoryId = save.LocalHistoryId,
                ContentFingerprint = fingerprint, Save = snapshotSave, SnapshotSha256 = Hash(JsonSerializer.Serialize(snapshotSave, Options))
            };
            Directory.CreateDirectory(branch);
            var file = Path.Combine(branch, id + ".json");
            var temporary = file + ".tmp";
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, Options));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temporary, file);
            return entry;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { throw new InvalidOperationException("本地历史未能安全保存，当前存档保持不变。", error); }
    }

    private List<LocalSaveHistorySnapshot> ReadBranch(string ownerId, string localSaveId)
    {
        var branch = Branch(ownerId, localSaveId);
        if (!Directory.Exists(branch)) return [];
        return Directory.EnumerateFiles(branch, "*.json", SearchOption.TopDirectoryOnly)
            .Select(file => ReadFile(file, ownerId, localSaveId)).ToList();
    }

    private LocalSaveHistorySnapshot ReadFile(string file, string ownerId, string localSaveId)
    {
        try
        {
            RequireNoLink(file);
            var value = JsonSerializer.Deserialize<LocalSaveHistorySnapshot>(File.ReadAllText(file), Options)
                ?? throw new InvalidDataException();
            if (value.Entry is null || value.Save is null) throw new InvalidDataException();
            ValidateSave(value.Save);
            ValidateId(value.Entry.Id);
            if (value.SchemaVersion != 1 || value.Marker != "Grimmcair.LocalSaveHistory" || value.OwnerId != ownerId
                || value.LocalSaveId != localSaveId || value.Save.OwnerId != ownerId || value.Save.Id != localSaveId
                || value.Entry.Id != Path.GetFileNameWithoutExtension(file) || value.Save.LocalHistoryId != value.Entry.Id
                || value.Entry.CreatedAtUtc == default || value.ContentFingerprint != Fingerprint(value.Save)
                || value.SnapshotSha256 != Hash(JsonSerializer.Serialize(value.Save, Options))
                || value.Entry.SaveName != value.Save.Name || value.Entry.IsMain != value.Save.IsMain
                || value.Entry.TargetCount != value.Save.Targets.Count || value.Entry.LevelText != Level(value.Save)
                || value.Entry.InventoryItemCount != value.Save.Inventory.Count(x => x.Quantity > 0)
                || value.Entry.CreatedAtUtc.UtcTicks.ToString("D19", CultureInfo.InvariantCulture) != value.Entry.Id[..19]
                || value.Entry.BeijingTimeName != value.Entry.CreatedAtUtc.ToOffset(TimeSpan.FromHours(8))
                    .ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture))
                throw new InvalidDataException();
            return value;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { throw new InvalidOperationException("本地历史无法读取或校验，未应用这份历史。", error); }
    }

    private string Branch(string ownerId, string localSaveId)
    {
        if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(localSaveId)) throw new InvalidOperationException("缺少本地历史分支身份。");
        var key = BranchKey(stateHash, Hash(ownerId), Hash(localSaveId));
        var path = Path.Combine(DirectoryPath, key);
        RequireNoLink(DirectoryPath);
        RequireNoLink(path);
        var conflicts = Path.Combine(DirectoryPath, "conflicts", key);
        RequireNoLink(Path.GetDirectoryName(conflicts)!);
        RequireNoLink(conflicts);
        if (Directory.Exists(conflicts) && Directory.EnumerateFileSystemEntries(conflicts).Any())
            throw new InvalidOperationException("本地历史存在同名内容冲突；两个版本均已保留，暂不能使用这份存档历史。");
        return path;
    }

    private static string BranchKey(string configuration, string owner, string save) => Hash(configuration + "/" + owner + "/" + save);

    private T WithHistoryLock<T>(Func<T> action)
    {
        lock (gate)
        {
            using var mutex = new Mutex(false, mutexName);
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new InvalidOperationException("本地历史正在处理，请稍后重试。");
                try
                {
                    RequireNoLinkInPath(Path.GetDirectoryName(DirectoryPath)!);
                    MigrateLegacyHistories();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { throw new InvalidOperationException("本地历史目录迁移未完成，原有文件已保留，请稍后重试。", error); }
                return action();
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
    }

    // Combine the original complete SHA-256 identifiers instead of truncating
    // them. Moving bytes preserves old history IDs, checksums and state heads.
    // Atomic moves plus remaining source entries are the restart checkpoint.
    private void MigrateLegacyHistories()
    {
        RequireNoLink(legacyRoot);
        if (!Directory.Exists(legacyRoot)) return;
        RequireNoLink(DirectoryPath);
        Directory.CreateDirectory(DirectoryPath);
        foreach (var configuration in Directory.EnumerateDirectories(legacyRoot).ToArray())
        {
            if (!IsHashDirectory(configuration)) continue;
            RequireNoLink(configuration);
            foreach (var owner in Directory.EnumerateDirectories(configuration).ToArray())
            {
                if (!IsHashDirectory(owner)) continue;
                RequireNoLink(owner);
                foreach (var save in Directory.EnumerateDirectories(owner).ToArray())
                {
                    if (!IsHashDirectory(save)) continue;
                    RequirePlainTree(save);
                    var key = BranchKey(Path.GetFileName(configuration).ToLowerInvariant(),
                        Path.GetFileName(owner).ToLowerInvariant(), Path.GetFileName(save).ToLowerInvariant());
                    MergeLegacyDirectory(save, Path.Combine(DirectoryPath, key), Path.Combine(DirectoryPath, "conflicts", key));
                }
                RemoveEmptyDirectory(owner);
            }
            RemoveEmptyDirectory(configuration);
        }
        RemoveEmptyDirectory(legacyRoot);
    }

    private static bool IsHashDirectory(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length == 64 && name.All(char.IsAsciiHexDigit);
    }

    private static void RequireNoLink(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("历史目录包含文件系统链接，已保留原有数据并停止迁移。");
    }

    private static void RequireNoLinkInPath(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            RequireNoLink(current);
    }

    private static void RequirePlainTree(string root)
    {
        RequireNoLink(root);
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            RequireNoLink(path);
            if (Directory.Exists(path)) RequirePlainTree(path);
        }
    }

    private void MergeLegacyDirectory(string source, string destination, string conflicts)
    {
        RequireNoLink(destination);
        RequireNoLink(Path.GetDirectoryName(conflicts)!);
        RequireNoLink(conflicts);
        if (File.Exists(destination))
        {
            PreserveConflictDirectory(source, conflicts);
            return;
        }
        if (!Directory.Exists(destination))
        {
            Directory.Move(source, destination);
            return;
        }
        foreach (var file in Directory.EnumerateFiles(source).ToArray())
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            RequireNoLink(target);
            if (File.Exists(target) && SameBytes(file, target)) File.Delete(file);
            else if (File.Exists(target) || Directory.Exists(target)) PreserveConflict(file, conflicts);
            else File.Move(file, target);
        }
        foreach (var child in Directory.EnumerateDirectories(source).ToArray())
            MergeLegacyDirectory(child, Path.Combine(destination, Path.GetFileName(child)), Path.Combine(conflicts, Path.GetFileName(child)));
        RemoveEmptyDirectory(source);
    }

    private static bool SameBytes(string first, string second)
    {
        using var left = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var right = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (left.Length != right.Length) return false;
        var leftBuffer = new byte[64 * 1024];
        var rightBuffer = new byte[leftBuffer.Length];
        while (true)
        {
            var count = left.ReadAtLeast(leftBuffer, leftBuffer.Length, throwOnEndOfStream: false);
            right.ReadExactly(rightBuffer.AsSpan(0, count));
            if (!leftBuffer.AsSpan(0, count).SequenceEqual(rightBuffer.AsSpan(0, count))) return false;
            if (count == 0) return true;
        }
    }

    private static void PreserveConflict(string source, string directory)
    {
        Directory.CreateDirectory(directory);
        var name = Path.GetFileName(source);
        for (var number = 0; ; number++)
        {
            var target = Path.Combine(directory, name + (number == 0 ? "" : "." + number.ToString(CultureInfo.InvariantCulture)));
            RequireNoLink(target);
            if (File.Exists(target) && SameBytes(source, target)) { File.Delete(source); return; }
            if (File.Exists(target) || Directory.Exists(target)) continue;
            File.Move(source, target);
            return;
        }
    }

    private static void PreserveConflictDirectory(string source, string directory)
    {
        Directory.CreateDirectory(directory);
        for (var number = 0; ; number++)
        {
            var target = Path.Combine(directory, "directory" + (number == 0 ? "" : "." + number.ToString(CultureInfo.InvariantCulture)));
            RequireNoLink(target);
            if (File.Exists(target) || Directory.Exists(target)) continue;
            Directory.Move(source, target);
            return;
        }
    }

    private static void RemoveEmptyDirectory(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path, recursive: false);
    }
    private static void ValidateId(string id)
    {
        if (id is null || id.Length != 52 || id[19] != '-' || !id[..19].All(char.IsAsciiDigit) || !id[20..].All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("本地历史版本标识不正确。");
    }
    private static void ValidateSave(CloudSave save)
    {
        if (save is null || string.IsNullOrWhiteSpace(save.OwnerId) || string.IsNullOrWhiteSpace(save.Id)
            || string.IsNullOrWhiteSpace(save.Name) || string.IsNullOrWhiteSpace(save.GameAccount)
            || string.IsNullOrWhiteSpace(save.Profession) || save.Revision < 1
            || save.LocalHistoryId is null || save.LoadCodes is null || save.LoadCodes.Count == 0
            || save.LoadCodes.Any(string.IsNullOrWhiteSpace) || save.Inventory is null || save.Targets is null
            || save.Inventory.Any(x => x is null || string.IsNullOrWhiteSpace(x.ItemId) || string.IsNullOrWhiteSpace(x.ItemName) || x.Quantity < 0)
            || save.Targets.Any(x => x is null || string.IsNullOrWhiteSpace(x.ItemId) || string.IsNullOrWhiteSpace(x.ItemName) || x.Quantity <= 0)
            || save.Targets.Select(x => x.ItemId).Distinct(StringComparer.Ordinal).Count() != save.Targets.Count)
            throw new InvalidOperationException("本地历史的存档内容或分支身份不正确。");
    }
    private static string Level(CloudSave save) => save.ParsedResult?["hero"] is JsonObject hero
        ? hero["level"]?.ToString() ?? "—" : "—";
    private static JsonNode Canonical(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => KeyValuePair.Create(x.Key, x.Value is null ? null : Canonical(x.Value)))),
        JsonArray array => new JsonArray(array.Select(x => x is null ? null : Canonical(x)).ToArray()),
        _ => node.DeepClone()
    };
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}
