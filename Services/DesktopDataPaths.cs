using System.IO;
using System.Text.Json;

namespace Grimmcair.Desktop.Services;

public sealed record DesktopDataLocation(string BaseDirectory, string StorageRoot, string DataDirectory,
    string Edition, bool IsInstalled, bool IsIsolated, string KnowledgeSelectionPath);

internal sealed record DesktopDataChoice(int SchemaVersion, string ActiveDirectory, string? PreviousDirectory);

/// <summary>Resolves a process-fixed location. Resolution never creates or migrates user data.</summary>
public static class DesktopDataPaths
{
    public const string InstallationMarkerName = "grimmcair.install.json";
    internal const string ChoiceFileName = "data-choice.json";
    private static readonly object Gate = new();
    private static DesktopDataLocation? _current;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static DesktopDataLocation Current
    {
        get { Initialize(Environment.GetCommandLineArgs().Skip(1).ToArray()); return _current!; }
    }

    public static void Initialize(string[] arguments)
    {
        lock (Gate)
        {
            if (_current is not null) return;
#if DESKTOP_COMMUNITY
            const string edition = "Community";
#else
            const string edition = "Dedicated";
#endif
            const bool preview = false;
            _current = Resolve(AppContext.BaseDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), edition, preview,
                FindIsolatedDirectory(arguments, AppContext.BaseDirectory));
        }
    }

    internal static string? FindIsolatedDirectory(string[] arguments, string baseDirectory)
    {
        for (var i = 0; i < arguments.Length; i++)
        {
            if (arguments[i] == "--ai-preview") return Path.Combine(baseDirectory, "AiPreviewData");
            if (arguments[i] == "--knowledge-preview") return Path.Combine(baseDirectory, "KnowledgePreviewData");
            if (arguments[i] == "--parse" && i + 2 < arguments.Length) return Path.GetDirectoryName(Path.GetFullPath(arguments[i + 2]));
            if (arguments[i] is "--ai-chat-smoke" or "--knowledge-smoke" or "--smoke" or "--cloud-smoke"
                or "--guide-smoke" or "--concepts-smoke" or "--concepts-reading-smoke" or "--desktop-api-smoke"
                or "--equipment-ux-smoke" or "--history-sync-smoke")
            {
                if (i + 1 >= arguments.Length) throw new ArgumentException("自动化检查需要独立输出目录。");
                return Path.GetFullPath(arguments[i + 1]);
            }
        }
        return null;
    }

    internal static DesktopDataLocation Resolve(string baseDirectory, string localAppData, string edition,
        bool preview, string? isolatedDirectory = null)
    {
        if (edition is not ("Community" or "Dedicated")) throw new InvalidDataException("应用版本标识无效。");
        var app = Path.GetFullPath(baseDirectory);
        if (isolatedDirectory is not null)
        {
            var isolated = Path.GetFullPath(isolatedDirectory);
            return new(app, isolated, Path.Combine(isolated, "simulation"), edition, false, true,
                Path.Combine(isolated, "knowledge-selection.json"));
        }
        var marker = Path.Combine(app, InstallationMarkerName);
        if (!preview && Directory.Exists(marker)) throw new InvalidDataException("安装标识不是文件，未切换数据目录。");
        if (preview || !File.Exists(marker))
            return new(app, Path.Combine(app, "UserData"), Path.Combine(app, "UserData"), edition, false, preview,
                Path.Combine(app, "knowledge-selection.json"));
        RequirePlainPath(marker);
        if (new FileInfo(marker).Length is < 1 or > 4096) throw new InvalidDataException("安装标识无效，未切换数据目录。");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(marker));
            var value = document.RootElement;
            if (value.GetProperty("schemaVersion").GetInt32() != 1 || value.GetProperty("edition").GetString() != edition
                || value.GetProperty("dataMode").GetString() != "installed") throw new InvalidDataException("安装标识与应用版本不符。");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("安装标识无效，未切换数据目录。", error); }
        if (!Path.IsPathFullyQualified(localAppData)) throw new InvalidDataException("无法确定当前 Windows 用户的数据目录。");
        var root = Path.Combine(Path.GetFullPath(localAppData), "Grimmcair", edition);
        RequirePlainPath(root);
        var choice = ReadChoice(root);
        var data = Path.Combine(root, choice.ActiveDirectory);
        RequirePlainPath(data);
        if (choice.ActiveDirectory != "data" && !Directory.Exists(data))
            throw new InvalidDataException("所选用户数据目录缺失，未创建替代记录。请恢复该目录后重试。");
        return new(app, root, data, edition, true, false, Path.Combine(root, "knowledge-selection.json"));
    }

    internal static DesktopDataChoice ReadChoice(string storageRoot)
    {
        var path = Path.Combine(storageRoot, ChoiceFileName);
        RequirePlainPath(path);
        if (!File.Exists(path)) return new(1, "data", null);
        if (new FileInfo(path).Length is < 1 or > 4096) throw new InvalidDataException("数据目录选择记录无效，原文件已保留。");
        try
        {
            var choice = JsonSerializer.Deserialize<DesktopDataChoice>(File.ReadAllBytes(path), Json);
            if (choice is null || choice.SchemaVersion != 1 || !IsDataSetName(choice.ActiveDirectory)
                || choice.PreviousDirectory is not null && !IsDataSetName(choice.PreviousDirectory))
                throw new InvalidDataException("数据目录选择记录无效，原文件已保留。");
            return choice;
        }
        catch (JsonException error) { throw new InvalidDataException("数据目录选择记录无法读取，原文件已保留。", error); }
    }

    internal static bool IsDataSetName(string? name) => name == "data" || name is { Length: 17 }
        && name.StartsWith("data-", StringComparison.Ordinal) && name.AsSpan(5).ToArray().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static void WriteChoice(DesktopDataLocation location, string activeDirectory, string? previousDirectory)
    {
        if (!location.IsInstalled || location.IsIsolated || !IsDataSetName(activeDirectory)
            || previousDirectory is not null && !IsDataSetName(previousDirectory)) throw new InvalidDataException("当前模式不支持切换安装版数据。");
        var active = Path.Combine(location.StorageRoot, activeDirectory);
        RequirePlainPath(active);
        if (!Directory.Exists(active)) throw new InvalidDataException("将要使用的数据目录不存在，未切换。");
        RequirePlainPath(location.StorageRoot);
        Directory.CreateDirectory(location.StorageRoot);
        var path = Path.Combine(location.StorageRoot, ChoiceFileName);
        RequirePlainPath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, new DesktopDataChoice(1, activeDirectory, previousDirectory), Json); stream.Flush(true); }
            RequirePlainPath(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static bool IsWithin(string path, string root) => string.Equals(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void RequirePlainPath(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("数据目录不能包含符号链接或目录联接。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
