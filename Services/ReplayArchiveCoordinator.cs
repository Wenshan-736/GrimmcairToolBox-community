using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Grimmcair.Desktop.Services;

/// <summary>
/// Associates saves changed during a game with the replay written after that game.
/// Existing replay files are baselines, never a substitute for a newly finished replay.
/// </summary>
public sealed class ReplayArchiveCoordinator
{
    private readonly object _gate = new();
    private readonly string _statePath;
    private readonly Func<string, ArchiveMediaKind, string, string, LocalSettings, string> _archiveMedia;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _settlingDelay;
    private readonly Dictionary<string, ReplayObservation> _observations = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private ReplayArchiveState _state;
    private LocalSettings _settings = LocalSettings.Suggested();
    private string _currentDirectory = "";

    public ReplayArchiveCoordinator(string statePath,
        Func<string, ArchiveMediaKind, string, string, LocalSettings, string> archiveMedia,
        Func<DateTimeOffset>? clock = null, TimeSpan? settlingDelay = null)
    {
        _statePath = Path.GetFullPath(statePath);
        _archiveMedia = archiveMedia;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _settlingDelay = settlingDelay ?? TimeSpan.FromSeconds(2);
        _state = File.Exists(_statePath)
            ? JsonSerializer.Deserialize<ReplayArchiveState>(File.ReadAllText(_statePath), _json)
                ?? throw new InvalidDataException("录像归档关联记录为空。")
            : new ReplayArchiveState();
        if (_state.FormatVersion != 1 || _state.Sources is null || _state.Pending is null)
            throw new InvalidDataException("录像归档关联记录格式不受支持。");
    }

    public int PendingCount { get { lock (_gate) return _state.Pending.Count; } }

    /// <summary>Call after loading or explicitly saving settings. Pending games survive app restarts.</summary>
    public void Configure(LocalSettings settings)
    {
        lock (_gate)
        {
            _settings = settings;
            var directory = settings.ResolveReplaySourceDirectory();
            _currentDirectory = string.IsNullOrWhiteSpace(directory) ? "" : Path.GetFullPath(directory);
            _observations.Clear();
            if (_currentDirectory.Length == 0) return;
            var source = _state.Sources.FirstOrDefault(s => SamePath(s.Directory, _currentDirectory));
            if (source is null)
            {
                source = new ReplaySourceState { Directory = _currentDirectory };
                _state.Sources.Add(source);
            }
            // With no unfinished game, recordings made while this tool was closed are historical.
            // With a pending game, retain the old hash so an exit recorded while closed can be found.
            if (!_state.Pending.Any(p => SamePath(p.ReplayDirectory, _currentDirectory)))
                source.Baselines = ReadBaselines(_currentDirectory);
            SaveState();
        }
    }

    /// <summary>Only call for live watcher changes, not a manual initial folder scan.</summary>
    public bool Queue(SaveArchiveChange change, DateTimeOffset? observedAt = null)
    {
        if (!change.HasChanged || change.IsInitial || string.IsNullOrWhiteSpace(change.SaveName)) return false;
        lock (_gate)
        {
            if (_currentDirectory.Length == 0) return false;
            var sourceKey = string.IsNullOrWhiteSpace(change.LogicalSourceKey) ? change.SourcePath : change.LogicalSourceKey;
            if (string.IsNullOrWhiteSpace(sourceKey)) return false;
            var now = _clock().ToUniversalTime();
            var timestamp = observedAt?.ToUniversalTime()
                ?? (change.SourceWrittenAt != default ? change.SourceWrittenAt.ToUniversalTime() : LastWriteOrNow(change.SourcePath, now));
            var pending = _state.Pending.FirstOrDefault(p => p.SourceKey == sourceKey && SamePath(p.ReplayDirectory, _currentDirectory));
            if (pending is null)
            {
                pending = new PendingReplaySave { SourceKey = sourceKey, SaveName = change.SaveName, ReplayDirectory = _currentDirectory };
                _state.Pending.Add(pending);
            }
            pending.SaveName = change.SaveName;
            // Repeated watcher notifications for identical content must not duplicate the session label.
            if (pending.Changes.LastOrDefault()?.SourceHash != change.SourceHash)
                pending.Changes.Add(new PendingReplayChange
                {
                    SourceHash = change.SourceHash,
                    ObservedAt = timestamp,
                    Summary = string.IsNullOrWhiteSpace(change.ChangeSummary) ? "物品无变化" : change.ChangeSummary
                });
            SaveState();
            return true;
        }
    }

    /// <summary>Poll every two seconds. IO runs off the WPF dispatcher; overlapping ticks are serialized.</summary>
    public Task<ReplayArchiveTickResult> TickAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return TickCore(cancellationToken);
    }, cancellationToken);

    private ReplayArchiveTickResult TickCore(CancellationToken cancellationToken)
    {
        var result = new ReplayArchiveTickResult();
        if (_state.Pending.Count == 0) return result;
        if (string.IsNullOrWhiteSpace(_settings.ReplayArchiveDirectory))
        {
            result.Issues.Add("已记录本局存档变化；请在设置中选择录像归档目录。");
            result.PendingCount = _state.Pending.Count;
            return result;
        }
        foreach (var directory in _state.Pending.Select(p => p.ReplayDirectory).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory))
            {
                result.Issues.Add($"正在等待录像来源文件夹：{directory}");
                continue;
            }
            var source = _state.Sources.Single(s => SamePath(s.Directory, directory));
            string[] files;
            try { files = ReadReplayPaths(directory, _settings.ReplayArchiveDirectory).OrderBy(File.GetLastWriteTimeUtc).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { result.Issues.Add("无法读取录像来源：" + ex.Message); continue; }
            foreach (var path in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryArchive(path, source, result);
            }
        }
        result.PendingCount = _state.Pending.Count;
        return result;
    }

    private void TryArchive(string path, ReplaySourceState source, ReplayArchiveTickResult result)
    {
        var finishedReading = false;
        try
        {
            var info = new FileInfo(path);
            var stamp = new ReplayStamp { Length = info.Length, LastWriteUtc = info.LastWriteTimeUtc };
            if (stamp.Length == 0) return;
            var baseline = source.Baselines.FirstOrDefault(b => SamePath(b.Path, path));
            if (baseline is not null && baseline.Length == stamp.Length && baseline.LastWriteUtc == stamp.LastWriteUtc) return;
            var eligible = _state.Pending.Where(p => SamePath(p.ReplayDirectory, source.Directory)
                && p.Changes.Any(c => c.ObservedAt.UtcDateTime <= stamp.LastWriteUtc)).ToArray();
            if (eligible.Length == 0) return;
            var now = _clock().ToUniversalTime();
            if (!_observations.TryGetValue(path, out var observation) || observation.Length != stamp.Length || observation.LastWriteUtc != stamp.LastWriteUtc)
            {
                _observations[path] = new ReplayObservation(stamp.Length, stamp.LastWriteUtc, now);
                return;
            }
            if (now - observation.FirstSeenAt < _settlingDelay || now.UtcDateTime - stamp.LastWriteUtc < _settlingDelay) return;

            // A writer still holding the replay causes this open to fail. Keep this read lock while
            // copying to every save, so LastReplay.w3g cannot change between the destination copies.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            info.Refresh();
            if (info.Length != stamp.Length || info.LastWriteTimeUtc != stamp.LastWriteUtc) return;
            finishedReading = true;
            // Platforms can publish the same recording as LastReplay and a dated
            // copy, sometimes after the next game has already saved. Its known
            // bytes must not consume that next game's pending save association.
            if (source.Baselines.Any(b => b.Hash == hash))
            {
                UpdateBaseline(source, path, stamp, hash);
                SaveState();
                return;
            }
            var allCopied = true;
            foreach (var pending in eligible)
            {
                var changes = pending.Changes.Where(c => c.ObservedAt.UtcDateTime <= stamp.LastWriteUtc).ToArray();
                var summary = string.Join('；', changes.Select(c => c.Summary).Distinct());
                var removed = false;
                try
                {
                    var archivePath = _archiveMedia(path, ArchiveMediaKind.Replay, pending.SaveName, summary, _settings);
                    result.Archives.Add(new ReplayArchiveResult(path, archivePath, pending.SaveName, summary));
                    foreach (var change in changes) pending.Changes.Remove(change);
                    if (pending.Changes.Count == 0) _state.Pending.Remove(pending);
                    removed = true;
                    SaveState(); // A partial fan-out resumes without repeating successful destinations.
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    if (removed)
                    {
                        pending.Changes.AddRange(changes);
                        if (!_state.Pending.Contains(pending)) _state.Pending.Add(pending);
                    }
                    allCopied = false;
                    result.Issues.Add($"{pending.SaveName} 的录像归档失败：{ex.Message}");
                }
            }
            if (allCopied)
            {
                UpdateBaseline(source, path, stamp, hash);
                _observations.Remove(path);
                SaveState();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Expected while Warcraft is still completing the file. Retry on the next poll.
            if (finishedReading) result.Issues.Add("录像归档关联记录保存失败：" + ex.Message);
        }
    }

    private List<ReplayBaseline> ReadBaselines(string directory)
    {
        var baselines = new List<ReplayBaseline>();
        if (!Directory.Exists(directory)) return baselines;
        foreach (var path in ReadReplayPaths(directory, _settings.ReplayArchiveDirectory))
        {
            try
            {
                var info = new FileInfo(path);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                baselines.Add(new ReplayBaseline { Path = path, Length = info.Length, LastWriteUtc = info.LastWriteTimeUtc, Hash = Convert.ToHexString(SHA256.HashData(stream)) });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Retain its timestamp even if a historical file is currently open by another app.
                var info = new FileInfo(path);
                if (info.Exists) baselines.Add(new ReplayBaseline { Path = path, Length = info.Length, LastWriteUtc = info.LastWriteTimeUtc });
            }
        }
        return baselines;
    }

    // Warcraft recordings use .w3g; the KK platform uses .nwg and also keeps dated
    // copies below Replay. Baselines and live discovery must cover the same files.
    private static IEnumerable<string> ReadReplayPaths(string directory, string archiveDirectory)
    {
        var archiveRoot = string.IsNullOrWhiteSpace(archiveDirectory) ? "" : Path.GetFullPath(archiveDirectory);
        // Only a separate output subtree can be excluded. If output is the
        // source itself or an ancestor, skipping it would hide every replay.
        var excludeArchiveTree = archiveRoot.Length > 0 && !SamePath(archiveRoot, directory)
            && IsInDirectory(archiveRoot, directory);
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        var directories = new Stack<string>();
        directories.Push(directory);
        while (directories.TryPop(out var current))
        {
            if (excludeArchiveTree && IsInDirectory(current, archiveRoot)) continue;
            foreach (var path in Directory.EnumerateFiles(current, "*", options))
            {
                var extension = Path.GetExtension(path);
                if ((extension.Equals(".w3g", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".nwg", StringComparison.OrdinalIgnoreCase)))
                    yield return path;
            }
            foreach (var child in Directory.EnumerateDirectories(current, "*", options))
                directories.Push(child);
        }
    }

    private static bool IsInDirectory(string path, string directory) => SamePath(path, directory)
        || path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset LastWriteOrNow(string path, DateTimeOffset now)
    {
        try
        {
            if (File.Exists(path))
            {
                var timestamp = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
                return timestamp <= now ? timestamp : now;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return now;
    }

    private static bool SamePath(string left, string right) => StringComparer.OrdinalIgnoreCase.Equals(left.TrimEnd(Path.DirectorySeparatorChar), right.TrimEnd(Path.DirectorySeparatorChar));

    private static void UpdateBaseline(ReplaySourceState source, string path, ReplayStamp stamp, string hash)
    {
        source.Baselines.RemoveAll(b => SamePath(b.Path, path));
        source.Baselines.Add(new ReplayBaseline { Path = path, Length = stamp.Length, LastWriteUtc = stamp.LastWriteUtc, Hash = hash });
    }

    private void SaveState()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temporaryPath = _statePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_state, _json));
        File.Move(temporaryPath, _statePath, overwrite: true);
    }

    private sealed record ReplayObservation(long Length, DateTime LastWriteUtc, DateTimeOffset FirstSeenAt);
}

public sealed class ReplayArchiveTickResult
{
    public List<ReplayArchiveResult> Archives { get; } = [];
    public List<string> Issues { get; } = [];
    public int PendingCount { get; internal set; }
}

public sealed record ReplayArchiveResult(string SourcePath, string ArchivePath, string SaveName, string ChangeSummary);

public sealed class ReplayArchiveState
{
    public int FormatVersion { get; set; } = 1;
    public List<ReplaySourceState> Sources { get; set; } = [];
    public List<PendingReplaySave> Pending { get; set; } = [];
}

public sealed class ReplaySourceState
{
    public string Directory { get; set; } = "";
    public List<ReplayBaseline> Baselines { get; set; } = [];
}

public class ReplayStamp
{
    public long Length { get; set; }
    public DateTime LastWriteUtc { get; set; }
}

public sealed class ReplayBaseline : ReplayStamp
{
    public string Path { get; set; } = "";
    public string Hash { get; set; } = "";
}

public sealed class PendingReplaySave
{
    public string SourceKey { get; set; } = "";
    public string SaveName { get; set; } = "";
    public string ReplayDirectory { get; set; } = "";
    public List<PendingReplayChange> Changes { get; set; } = [];
}

public sealed class PendingReplayChange
{
    public string SourceHash { get; set; } = "";
    public DateTimeOffset ObservedAt { get; set; }
    public string Summary { get; set; } = "";
}
