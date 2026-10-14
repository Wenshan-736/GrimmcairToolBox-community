using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Grimmcair.Desktop.Services;

public sealed record DesktopDataImportResult(string DataDirectory, int FileCount, long Bytes, int UpdatedPaths);
public sealed record DesktopDataSwitchState(string CurrentDirectory, string NextDirectory, bool CanRevert, bool Pending);

/// <summary>Explicit whole-directory import. Source and existing datasets are never changed or merged.</summary>
public static class DesktopDataImport
{
    private const int MaximumFiles = 100_000;
    private const long MaximumFileBytes = 4L * 1024 * 1024 * 1024;
    private const long MaximumTotalBytes = 32L * 1024 * 1024 * 1024;
    private sealed record FileEntry(string Path, long Bytes, string Sha256, DateTime LastWriteUtc);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static DesktopDataSwitchState GetSwitchState(DesktopDataLocation location)
    {
        if (!location.IsInstalled || location.IsIsolated) return new(location.DataDirectory, location.DataDirectory, false, false);
        var choice = DesktopDataPaths.ReadChoice(location.StorageRoot);
        var currentName = Path.GetFileName(location.DataDirectory);
        var pending = choice.ActiveDirectory != currentName;
        var revertName = pending ? currentName : choice.PreviousDirectory;
        var canRevert = revertName is not null && revertName != choice.ActiveDirectory
            && Directory.Exists(Path.Combine(location.StorageRoot, revertName));
        return new(location.DataDirectory, Path.Combine(location.StorageRoot, choice.ActiveDirectory), canRevert, pending);
    }

    public static void RevertForNextStart(DesktopDataLocation location)
    {
        RequireInstalled(location);
        using var lease = Acquire(location.StorageRoot);
        var choice = DesktopDataPaths.ReadChoice(location.StorageRoot);
        var currentName = Path.GetFileName(location.DataDirectory);
        var target = choice.ActiveDirectory != currentName ? currentName : choice.PreviousDirectory;
        if (target is null || target == choice.ActiveDirectory) throw new InvalidDataException("没有可切回的数据目录。");
        DesktopDataPaths.WriteChoice(location, target, choice.ActiveDirectory);
    }

    public static DesktopDataImportResult ImportForNextStart(string selectedUserData, DesktopDataLocation location,
        CancellationToken cancellationToken = default) => ImportCore(selectedUserData, location, cancellationToken);

    internal static DesktopDataImportResult ImportCore(string selectedUserData, DesktopDataLocation location,
        CancellationToken cancellationToken = default, Action? afterCopy = null, string? dataSetName = null)
    {
        RequireInstalled(location);
        if (!Path.IsPathFullyQualified(selectedUserData)) throw new InvalidDataException("请选择完整的旧 UserData 文件夹。");
        var source = Path.GetFullPath(selectedUserData).TrimEnd(Path.DirectorySeparatorChar);
        DesktopDataPaths.RequirePlainPath(source);
        DesktopDataPaths.RequirePlainPath(location.StorageRoot);
        if (!Directory.Exists(source)) throw new InvalidDataException("所选 UserData 文件夹不存在。");
        if (DesktopDataPaths.IsWithin(source, location.StorageRoot) || DesktopDataPaths.IsWithin(location.StorageRoot, source))
            throw new InvalidDataException("旧数据与当前安装版数据目录不能相同或互相包含。");
        if (!new[] { "settings.json", "cloud-simulation.json", "archive-manifest.json", "account-documents.json" }
                .Any(name => File.Exists(Path.Combine(source, name))) && !Directory.Exists(Path.Combine(source, "ai")))
            throw new InvalidDataException("这个文件夹没有可识别的工具箱数据，请选择旧程序旁的 UserData 文件夹。");
        using var lease = Acquire(location.StorageRoot);
        if (GetSwitchState(location).Pending) throw new InvalidDataException("已有等待重启的切换，请先重启或取消切换。");
        var files = Snapshot(source, cancellationToken);
        if (files.Count == 0) throw new InvalidDataException("所选数据文件夹为空。");
        Directory.CreateDirectory(location.StorageRoot);
        var name = dataSetName ?? "data-" + Guid.NewGuid().ToString("N")[..12];
        if (!DesktopDataPaths.IsDataSetName(name) || name == "data") throw new InvalidDataException("导入数据目录名称无效。");
        var target = Path.Combine(location.StorageRoot, name);
        var staging = Path.Combine(location.StorageRoot, ".import-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(target) || File.Exists(target) || Directory.Exists(staging) || File.Exists(staging))
            throw new IOException("导入目标已存在，未覆盖任何文件，请重试。");
        Directory.CreateDirectory(staging);
        // A failed staging tree is never selected. Retaining it also avoids deleting data after a disk/IO failure.
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = Path.Combine(source, file.Path);
            var copy = Path.Combine(staging, file.Path);
            DesktopDataPaths.RequirePlainPath(original);
            DesktopDataPaths.RequirePlainPath(copy);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            using (var input = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            if (new FileInfo(copy).Length != file.Bytes || Hash(copy) != file.Sha256)
                throw new InvalidDataException("复制校验失败，未启用导入数据，原数据保持不变。");
            File.SetLastWriteTimeUtc(copy, file.LastWriteUtc);
        }
        afterCopy?.Invoke();
        var finalSource = Snapshot(source, cancellationToken);
        if (!files.SequenceEqual(finalSource)) throw new InvalidDataException("导入期间旧数据发生变化，请关闭旧程序后重新导入。原数据保持不变。");
        if (!files.SequenceEqual(Snapshot(staging, cancellationToken))) throw new InvalidDataException("导入副本发生变化，未切换数据。");
        var updatedPaths = RebaseLocalPaths(staging, source, target);
        var finalCopy = Snapshot(staging, cancellationToken);
        if (finalCopy.Count != files.Count) throw new InvalidDataException("导入副本不完整，未切换。");
        cancellationToken.ThrowIfCancellationRequested();
        DesktopDataPaths.RequirePlainPath(staging);
        DesktopDataPaths.RequirePlainPath(target);
        Directory.Move(staging, target); // No overwrite and no change to any existing dataset.
        var receipts = Path.Combine(location.StorageRoot, "imports");
        DesktopDataPaths.RequirePlainPath(receipts);
        Directory.CreateDirectory(receipts);
        using (var receipt = new FileStream(Path.Combine(receipts, name + ".json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(receipt, new { schemaVersion = 1, sourceDirectory = source, dataDirectory = name,
                copiedUtc = DateTimeOffset.UtcNow, updatedPaths, sourceFiles = files, importedFiles = finalCopy }, Json);
            receipt.Flush(true);
        }
        DesktopDataPaths.WriteChoice(location, name, Path.GetFileName(location.DataDirectory));
        return new(target, files.Count, files.Sum(file => file.Bytes), updatedPaths);
    }

    private static List<FileEntry> Snapshot(string directory, CancellationToken cancellationToken)
    {
        DesktopDataPaths.RequirePlainPath(directory);
        var result = new List<FileEntry>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((directory, 0));
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            if (current.Depth > 48) throw new InvalidDataException("旧数据目录层级过深，未导入。");
            DesktopDataPaths.RequirePlainPath(current.Path);
            foreach (var path in Directory.EnumerateFileSystemEntries(current.Path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DesktopDataPaths.RequirePlainPath(path);
                var relative = Path.GetRelativePath(directory, path);
                if (!DesktopDataPaths.IsWithin(path, directory) || !names.Add(relative) || names.Count > MaximumFiles * 2)
                    throw new InvalidDataException("数据目录包含重复或越界路径，未导入。");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push((path, current.Depth + 1)); continue; }
                var length = new FileInfo(path).Length;
                total += length;
                if (result.Count >= MaximumFiles || length > MaximumFileBytes || total > MaximumTotalBytes)
                    throw new InvalidDataException("旧数据超过导入容量限制（十万文件、单文件4GB、总计32GB），未导入。");
                result.Add(new(relative, length, Hash(path), File.GetLastWriteTimeUtc(path)));
            }
        }
        return result.OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static int RebaseLocalPaths(string staging, string source, string target)
    {
        var count = 0;
        // Only live path configuration is rebased. Save/history bodies and encrypted records keep their bytes.
        foreach (var name in new[] { "settings.json", "archive-manifest.json", "replay-pending.json" })
        {
            var path = Path.Combine(staging, name);
            if (!File.Exists(path)) continue;
            if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("路径配置文件过大，未启用导入。");
            JsonNode root;
            try { root = JsonNode.Parse(File.ReadAllBytes(path)) ?? throw new InvalidDataException("旧路径配置为空。"); }
            catch (JsonException error) { throw new InvalidDataException("旧路径配置无法读取，未启用导入。", error); }
            var before = count;
            void Walk(JsonNode? node, string? field = null, bool archiveValues = false)
            {
                if (node is JsonObject obj)
                {
                    foreach (var pair in obj.ToArray())
                    {
                        var pathField = pair.Key.EndsWith("Directory", StringComparison.OrdinalIgnoreCase)
                            || pair.Key.Equals("path", StringComparison.OrdinalIgnoreCase) || archiveValues;
                        if (pathField && pair.Value is JsonValue value && value.TryGetValue<string>(out var text)
                            && Path.IsPathFullyQualified(text) && DesktopDataPaths.IsWithin(text, source))
                        {
                            obj[pair.Key] = Path.Combine(target, Path.GetRelativePath(source, text)); count++;
                        }
                        else Walk(pair.Value, pair.Key, pair.Key.Equals("archives", StringComparison.OrdinalIgnoreCase));
                    }
                }
                else if (node is JsonArray array) foreach (var child in array) Walk(child, field);
            }
            Walk(root);
            if (count != before) File.WriteAllText(path, root.ToJsonString(Json));
        }
        return count;
    }

    private static void RequireInstalled(DesktopDataLocation location)
    {
        if (!location.IsInstalled || location.IsIsolated) throw new InvalidDataException("请在正常运行的安装版中导入旧数据。");
        DesktopDataPaths.RequirePlainPath(location.StorageRoot);
    }

    private static IDisposable Acquire(string root)
    {
        var mutex = new Mutex(false, "Local\\Grimmcair.DataImport." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant()))));
        try
        {
            try { if (!mutex.WaitOne(0)) throw new IOException("另一个窗口正在导入或切换数据，请稍后重试。"); }
            catch (AbandonedMutexException) { }
            return new Lease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }
    private sealed class Lease(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
