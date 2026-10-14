using System.IO;

namespace Grimmcair.Desktop.Services;

public sealed record ArchiveNotice(string Title, string Message);

/// <summary>Success-only display text; does not inspect or modify any source file.</summary>
public static class ArchiveNotificationText
{
    public static ArchiveNotice Screenshot(string saveName, string archivePath) =>
        new("截图已保存", saveName + "\n" + Path.GetFileName(archivePath));

    public static ArchiveNotice? Replays(IReadOnlyList<ReplayArchiveResult> archives)
    {
        if (archives.Count == 0) return null;
        var replayCount = archives.Select(x => x.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var folders = archives.Select(x => Path.GetDirectoryName(x.ArchivePath) ?? x.ArchivePath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var names = archives.Select(x => x.SaveName).Distinct(StringComparer.Ordinal).ToList();
        var namesText = string.Join("、", names.Take(3)) + (names.Count > 3 ? $" 等 {names.Count} 份存档" : "");
        return new("新录像已归档", $"{replayCount} 份录像 · {folders} 个存档目录\n{namesText}");
    }
}
