using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Grimmcair.Desktop.Services;

public enum ArchiveMediaKind { Screenshot, Replay }

public sealed record SaveArchiveItemChange(string ItemId, string ItemName, int Before, int After)
{
    public int Delta => After - Before;
}

public sealed class SaveArchiveChange
{
    public string SourcePath { get; init; } = "";
    public string SourceHash { get; init; } = "";
    public DateTimeOffset SourceWrittenAt { get; init; }
    public string ArchivePath { get; init; } = "";
    public string SaveName { get; init; } = "";
    public string LogicalSourceKey { get; init; } = "";
    public JsonObject Parsed { get; init; } = new();
    public string ChangeSummary { get; init; } = "";
    public IReadOnlyList<SaveArchiveItemChange> ItemChanges { get; init; } = [];
    public string GameAccount { get; init; } = "";
    public bool IsInitial { get; init; }
    public bool IsNewArchive { get; init; }
    public bool HasChanged { get; init; }
}

public sealed class SaveArchiveScanResult : EventArgs
{
    public List<SaveArchiveChange> Entries { get; } = [];
    public List<SaveArchiveChange> Changes { get; } = [];
    public List<string> Issues { get; } = [];
}

/// <summary>Copies successfully decoded stable snapshots; it never writes to the game's source files.</summary>
public sealed class SaveArchiveService : IDisposable
{
    private sealed class ArchiveManifest
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, ArchiveHead> Heads { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class ArchiveHead
    {
        public string Hash { get; set; } = "";
        public string Summary { get; set; } = "";
        public Dictionary<string, ArchiveItem> Items { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Archives { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class ArchiveItem
    {
        public string Name { get; set; } = "";
        public int Quantity { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _manifestPath;
    private readonly Func<byte[], string, JsonObject> _parse;
    private readonly Func<DateTime> _localClock;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _watchGate = new();
    private readonly object _mediaGate = new();
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _watchLifetime;
    private CancellationTokenSource? _debounce;
    private readonly HashSet<Task> _watchTasks = [];
    private LocalSettings? _watchSettings;
    private Task? _watchDrainTask;
    private bool _acceptWatchEvents, _resumeAfterFlush;
    private bool _disposed;
    private readonly Dictionary<string, JsonObject> _verifiedParsed = new(StringComparer.Ordinal);
    public Func<string, string, JsonObject, bool>? ShouldIgnoreSource { get; set; }
    public event EventHandler<RenamedEventArgs>? SourceRenamed;

    public event EventHandler<SaveArchiveScanResult>? ScanCompleted;

    public async Task RunBetweenScansAsync(Action update)
    {
        await _scanGate.WaitAsync();
        try { update(); }
        finally { _scanGate.Release(); }
    }

    public SaveArchiveService(string manifestPath, Func<byte[], string, JsonObject> parser, Func<DateTime>? localClock = null)
    {
        _manifestPath = Path.GetFullPath(manifestPath);
        _parse = parser ?? throw new ArgumentNullException(nameof(parser));
        _localClock = localClock ?? (() => DateTime.Now);
    }

    public Task<SaveArchiveScanResult> ScanAsync(LocalSettings settings, CancellationToken cancellationToken = default) =>
        Task.Run(() => ScanCoreAsync(settings.Normalize(), false, cancellationToken), cancellationToken);

    /// <summary>Reuse only the parsed version whose complete source hash was previously accepted.</summary>
    public void SeedVerifiedSnapshots(IEnumerable<CloudSave> saves)
    {
        foreach (var save in saves.Where(x => !x.IsDemo && !x.SourceDetached && x.ParsedResult is not null
                     && x.SourceHash.Length == 64 && x.SourceHash == x.AcceptedSourceHash
                     && x.ParsedResult["source"]?["sha256"]?.ToString() == x.SourceHash))
            try
            {
                var key = JsonSerializer.Deserialize<string[]>(save.SourceKey);
                if (key is { Length: 4 }) _verifiedParsed[key[0] + "\n" + save.SourceHash] = save.ParsedResult!.DeepClone().AsObject();
            }
            catch (JsonException) { }
    }

    private async Task<SaveArchiveScanResult> ScanCoreAsync(LocalSettings settings, bool fromWatcher, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new SaveArchiveScanResult();
            if (string.IsNullOrWhiteSpace(settings.SaveSourceDirectory)) return result;
            if (!Directory.Exists(settings.SaveSourceDirectory))
            {
                result.Issues.Add("存档来源文件夹不存在，请在设置中重新选择。");
                return result;
            }
            ArchiveManifest manifest;
            try { manifest = LoadManifest(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                result.Issues.Add($"归档记录无法读取，未覆盖原记录：{error.Message}");
                return result;
            }
            var dirty = false;
            foreach (var sourcePath in Directory.EnumerateFiles(settings.SaveSourceDirectory, "*.txt", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var sourceName = Path.GetFileName(sourcePath);
                    var snapshot = await ReadStableSnapshotAsync(sourcePath, cancellationToken, knownHash =>
                        _verifiedParsed.ContainsKey(sourceName.ToUpperInvariant() + "\n" + knownHash)).ConfigureAwait(false);
                    var raw = snapshot.Bytes;
                    var hash = Convert.ToHexStringLower(SHA256.HashData(raw));
                    var cacheKey = sourceName.ToUpperInvariant() + "\n" + hash;
                    if (!_verifiedParsed.TryGetValue(cacheKey, out var verified))
                    {
                        verified = _parse(raw, sourceName);
                        _verifiedParsed[cacheKey] = verified.DeepClone().AsObject();
                    }
                    var parsed = verified.DeepClone().AsObject();
                    var input = SaveInput.Parse(raw, sourceName, preferEmbeddedUsername: true);
                    if (ShouldIgnoreSource?.Invoke(sourcePath, input.Username, parsed) == true) continue;
                    var kind = parsed["kind"]?.GetValue<string>() ?? throw new InvalidDataException("解析结果缺少存档类型。");
                    var key = BuildLogicalSourceKey(sourcePath, input.Username, parsed);
                    var saveName = SanitizeName(Path.GetFileNameWithoutExtension(sourceName));
                    var items = ReadItems(parsed);
                    manifest.Heads.TryGetValue(key, out var previous);
                    var changed = previous is null || previous.Hash != hash;
                    var differences = previous is null ? new List<SaveArchiveItemChange>() : CompareItems(previous.Items, items);
                    var summary = changed ? previous is null ? "首次记录" : Summarize(differences) : previous!.Summary;
                    var archivePath = "";
                    var copied = false;
                    var head = previous ?? new ArchiveHead();
                    if (!string.IsNullOrWhiteSpace(settings.SaveArchiveDirectory))
                    {
                        var archiveDirectory = Path.Combine(Path.GetFullPath(settings.SaveArchiveDirectory), saveName);
                        if (head.Archives.TryGetValue(hash, out var existing) && IsInDirectory(existing, archiveDirectory)
                            && File.Exists(existing) && HashFile(existing) == hash)
                            archivePath = existing;
                        else
                        {
                            Directory.CreateDirectory(archiveDirectory);
                            archivePath = WriteUniqueSnapshot(archiveDirectory, saveName, raw);
                            head.Archives[hash] = archivePath;
                            copied = true;
                        }
                    }
                    head.Hash = hash;
                    head.Summary = summary;
                    head.Items = items;
                    manifest.Heads[key] = head;
                    dirty |= changed || copied;
                    var entry = new SaveArchiveChange
                    {
                        SourcePath = Path.GetFullPath(sourcePath), SourceHash = hash, ArchivePath = archivePath,
                        SourceWrittenAt = snapshot.WrittenAt,
                        SaveName = saveName, LogicalSourceKey = key, Parsed = parsed, GameAccount = input.Username,
                        ChangeSummary = summary, ItemChanges = differences, IsInitial = previous is null && !fromWatcher,
                        IsNewArchive = copied, HasChanged = changed
                    };
                    result.Entries.Add(entry);
                    if (changed || copied) result.Changes.Add(entry);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
                { result.Issues.Add($"{Path.GetFileName(sourcePath)}：{error.Message}"); }
            }
            if (dirty) SaveManifest(manifest);
            return result;
        }
        finally { _scanGate.Release(); }
    }

    public void StartWatching(LocalSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        settings = settings.Normalize();
        lock (_watchGate)
            if (_watchDrainTask is { IsCompleted: false }) throw new InvalidOperationException("请等待旧来源完成归档后再启动新的监听。");
        StopWatching();
        if (string.IsNullOrWhiteSpace(settings.SaveSourceDirectory) || !Directory.Exists(settings.SaveSourceDirectory)) return;
        lock (_watchGate)
        {
            _watchLifetime = new CancellationTokenSource();
            var generation = _watchLifetime;
            _watchSettings = settings;
            _acceptWatchEvents = true;
            _watchDrainTask = null;
            _watcher = new FileSystemWatcher(settings.SaveSourceDirectory, "*.txt")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            _watcher.Changed += (_, _) => ScheduleScan(settings, generation);
            _watcher.Created += (_, _) => ScheduleScan(settings, generation);
            _watcher.Renamed += (_, e) =>
            {
                SourceRenamed?.Invoke(this, e);
                ScheduleScan(settings, generation);
            };
            _watcher.Error += (_, _) => ScheduleScan(settings, generation);
            _watcher.EnableRaisingEvents = true;
        }
    }

    private void ScheduleScan(LocalSettings settings, CancellationTokenSource generation)
    {
        CancellationToken token;
        CancellationToken lifetime;
        lock (_watchGate)
        {
            if (!_acceptWatchEvents || _watchLifetime != generation || _disposed) return;
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = CancellationTokenSource.CreateLinkedTokenSource(generation.Token);
            token = _debounce.Token;
            lifetime = generation.Token;
            var task = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(450, token).ConfigureAwait(false);
                    // Later writes schedule another pass. Graceful stopping waits for
                    // these notifications rather than cancelling an in-flight commit.
                    var result = await ScanCoreAsync(settings, true, lifetime).ConfigureAwait(false);
                    if (!lifetime.IsCancellationRequested) ScanCompleted?.Invoke(this, result);
                }
                catch (OperationCanceledException) { }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    if (!lifetime.IsCancellationRequested)
                    {
                        var result = new SaveArchiveScanResult();
                        result.Issues.Add($"自动归档失败：{error.Message}");
                        ScanCompleted?.Invoke(this, result);
                    }
                }
            });
            _watchTasks.Add(task);
            _ = task.ContinueWith(finished => { lock (_watchGate) _watchTasks.Remove(finished); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>Stops new events, flushes the last observed source bytes, and waits for completed notifications.</summary>
    public Task StopWatchingAsync() => DrainWatchingAsync(resume: false);

    /// <summary>Temporarily pauses events, flushes observed changes, then resumes the same source watcher.</summary>
    public Task FlushWatchingAsync() => DrainWatchingAsync(resume: true);

    private Task DrainWatchingAsync(bool resume)
    {
        lock (_watchGate)
        {
            if (_watchDrainTask is { IsCompleted: false })
            {
                if (!resume)
                {
                    _resumeAfterFlush = false;
                    _watcher?.Dispose(); _watcher = null;
                }
                return _watchDrainTask;
            }
            _acceptWatchEvents = false;
            _resumeAfterFlush = resume;
            // The unconditional final pass covers queued events; only cancel their debounce wait.
            // An in-flight decode/commit uses the lifetime token and is still fully drained.
            _debounce?.Cancel();
            if (_watcher is not null) _watcher.EnableRaisingEvents = false;
            if (!resume) { _watcher?.Dispose(); _watcher = null; }
            var generation = _watchLifetime;
            var settings = _watchSettings;
            var queued = _watchTasks.ToArray();
            _watchDrainTask = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(queued).ConfigureAwait(false);
                    // An FS callback may already be waiting for _watchGate. The
                    // final pass covers it even though new scheduling is stopped.
                    if (settings is not null && generation is not null && !generation.IsCancellationRequested)
                    {
                        var result = await ScanCoreAsync(settings, true, generation.Token).ConfigureAwait(false);
                        if (!generation.IsCancellationRequested) ScanCompleted?.Invoke(this, result);
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    lock (_watchGate)
                    {
                        if (_watchLifetime == generation)
                        {
                            _debounce?.Dispose(); _debounce = null;
                            if (_resumeAfterFlush && !_disposed && _watcher is not null && generation?.IsCancellationRequested == false)
                            {
                                _acceptWatchEvents = true;
                                _watcher.EnableRaisingEvents = true;
                            }
                            else
                            {
                                generation?.Dispose(); _watchLifetime = null;
                                _watchSettings = null;
                            }
                        }
                    }
                }
            });
            return _watchDrainTask;
        }
    }

    public void StopWatching()
    {
        lock (_watchGate)
        {
            _acceptWatchEvents = false; _resumeAfterFlush = false; _watchSettings = null;
            _watcher?.Dispose(); _watcher = null;
            _debounce?.Cancel(); _debounce?.Dispose(); _debounce = null;
            _watchLifetime?.Cancel(); _watchLifetime?.Dispose(); _watchLifetime = null;
        }
    }

    public string ArchiveMedia(string sourcePath, ArchiveMediaKind kind, string saveName, string changeSummary, LocalSettings settings) =>
        ArchiveMedia(File.ReadAllBytes(sourcePath), kind, saveName, changeSummary, settings, Path.GetExtension(sourcePath));

    public string ArchiveMedia(byte[] content, ArchiveMediaKind kind, string saveName, string changeSummary, LocalSettings settings, string extension)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var root = kind == ArchiveMediaKind.Screenshot ? settings.ScreenshotDirectory : settings.ReplayArchiveDirectory;
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException(kind == ArchiveMediaKind.Screenshot ? "请先设置截图归档位置。" : "请先设置录像归档位置。");
        if (extension.Length < 2 || extension[0] != '.' || extension.Skip(1).Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("媒体文件扩展名无效。", nameof(extension));
        var directory = Path.Combine(Path.GetFullPath(root), SanitizeName(saveName));
        var summary = SanitizeName(string.IsNullOrWhiteSpace(changeSummary) ? "物品无变化" : changeSummary);
        lock (_mediaGate)
        {
            Directory.CreateDirectory(directory);
            if (kind == ArchiveMediaKind.Screenshot)
            {
                var stamp = _localClock().ToString("yyyyMMdd_HHmmss_fff", System.Globalization.CultureInfo.InvariantCulture);
                for (var copy = 1; ; copy++)
                {
                    var suffix = copy == 1 ? "" : "_" + copy.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var path = Path.Combine(directory, stamp + suffix + extension.ToLowerInvariant());
                    // Atomic destination creation also protects against another
                    // process saving an image in the same millisecond.
                    if (TryWriteNew(path, content)) return path;
                }
            }
            var serial = Directory.EnumerateFiles(directory).Select(path => Path.GetFileName(path).Split('-')[0])
                .Select(part => int.TryParse(part, out var number) ? number : 0).DefaultIfEmpty(0).Max() + 1;
            while (true)
            {
                var path = Path.Combine(directory, $"{serial:D4}-{summary}{extension.ToLowerInvariant()}");
                if (TryWriteNew(path, content)) return path;
                serial++;
            }
        }
    }

    public static string SanitizeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(result) || result is "." or "..") result = "未命名存档";
        var device = result.Split('.')[0].ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" || device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && device[3] is >= '1' and <= '9') result = "_" + result;
        return result.Length > 100 ? result[..100].TrimEnd('.', ' ') : result;
    }

    /// <summary>Moving the source folder keeps identity; filenames and actual game roles keep branches separate.</summary>
    public static string BuildLogicalSourceKey(string sourcePath, string gameAccount, JsonObject parsed)
    {
        var kind = parsed["kind"]?.GetValue<string>() ?? throw new InvalidDataException("解析结果缺少存档类型。");
        var profession = kind == "character" ? parsed["hero"]?["rawcode"]?.GetValue<string>() ?? "" : "";
        return JsonSerializer.Serialize(new[] { Path.GetFileName(sourcePath).ToUpperInvariant(), gameAccount, kind, profession });
    }

    private sealed record StableSnapshot(byte[] Bytes, DateTimeOffset WrittenAt);

    private static async Task<StableSnapshot> ReadStableSnapshotAsync(string path, CancellationToken token, Func<string, bool>? isVerified = null)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                var first = ReadSnapshot(path);
                // Always read and hash actual bytes, even when timestamps and size are unchanged.
                // A complete previously decoded snapshot needs no second settling delay or parse.
                if (isVerified?.Invoke(Convert.ToHexStringLower(SHA256.HashData(first.Bytes))) == true) return first;
                await Task.Delay(120, token).ConfigureAwait(false);
                var second = ReadSnapshot(path);
                if (first.WrittenAt == second.WrittenAt && first.Bytes.AsSpan().SequenceEqual(second.Bytes)) return second;
            }
            // Game save writers may briefly hold an exclusive handle. Retry the
            // same event rather than requiring another file-system event later.
            catch (IOException) when (attempt < 3) { }
            await Task.Delay(150, token).ConfigureAwait(false);
        }
        throw new IOException("文件仍在写入，稍后将再次检测。");
    }

    private static StableSnapshot ReadSnapshot(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > 2 * 1024 * 1024) throw new InvalidDataException("存档文件超过 2 MiB 上限。");
        using var memory = new MemoryStream();
        input.CopyTo(memory);
        if (memory.Length > 2 * 1024 * 1024) throw new InvalidDataException("存档文件超过 2 MiB 上限。");
        return new StableSnapshot(memory.ToArray(), new DateTimeOffset(File.GetLastWriteTimeUtc(input.SafeFileHandle), TimeSpan.Zero));
    }

    private static Dictionary<string, ArchiveItem> ReadItems(JsonObject parsed)
    {
        var result = new Dictionary<string, ArchiveItem>(StringComparer.Ordinal);
        foreach (var section in new[] { "inventory", "bag", "storage" })
            foreach (var node in parsed["inventories"]?[section]?.AsArray() ?? [])
            {
                var id = node?["rawcode"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!result.TryGetValue(id, out var item)) result[id] = item = new ArchiveItem { Name = node?["name"]?.GetValue<string>() ?? "未知物品" };
                item.Quantity = checked(item.Quantity + (node?["quantity"]?.GetValue<int>() ?? 1));
            }
        return result;
    }

    private static List<SaveArchiveItemChange> CompareItems(Dictionary<string, ArchiveItem> before, Dictionary<string, ArchiveItem> after) =>
        before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal).Select(id =>
            new SaveArchiveItemChange(id, after.GetValueOrDefault(id)?.Name ?? before[id].Name,
                before.GetValueOrDefault(id)?.Quantity ?? 0, after.GetValueOrDefault(id)?.Quantity ?? 0))
            .Where(item => item.Delta != 0).OrderBy(item => item.ItemName, StringComparer.CurrentCulture).ToList();

    private static string Summarize(IReadOnlyList<SaveArchiveItemChange> changes) => changes.Count == 0 ? "物品无变化" :
        string.Join("，", changes.Select(item => $"{item.ItemName}{(item.Delta > 0 ? "+" : "")}{item.Delta}"));

    private ArchiveManifest LoadManifest()
    {
        if (!File.Exists(_manifestPath)) return new ArchiveManifest();
        var result = JsonSerializer.Deserialize<ArchiveManifest>(File.ReadAllBytes(_manifestPath), JsonOptions)
            ?? throw new InvalidDataException("归档记录为空。");
        if (result.Version != 1 || result.Heads is null || result.Heads.Values.Any(head => head is null || head.Items is null || head.Archives is null))
            throw new InvalidDataException("归档记录格式无效。");
        return result;
    }

    private void SaveManifest(ArchiveManifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_manifestPath)!);
        var temporary = _manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
        File.Move(temporary, _manifestPath, true);
    }

    private static string WriteUniqueSnapshot(string directory, string saveName, byte[] raw)
    {
        while (true)
        {
            var path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{saveName}.txt");
            if (TryWriteNew(path, raw)) return path;
            Thread.Sleep(1);
        }
    }

    private static bool TryWriteNew(string path, byte[] content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temporary, content);
        try { File.Move(temporary, path, false); return true; }
        catch (IOException) when (File.Exists(path)) { File.Delete(temporary); return false; }
    }

    private static bool IsInDirectory(string path, string directory) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopWatching();
        _disposed = true;
    }
}
