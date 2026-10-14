using System.IO;
using System.Text.Json;

namespace Grimmcair.Desktop.Services;

/// <summary>Public resource selection only. Account settings and saves are never consulted.</summary>
public static class KnowledgePackSelection
{
    private sealed record Selection(int SchemaVersion, string PackagePath, string PackageSha256);
    public static string SettingsPath => DesktopDataPaths.Current.KnowledgeSelectionPath;
    public static string? StartupWarning { get; private set; }

    public static string[] Initialize(string[] arguments, bool ignoreSavedSelection = false)
    {
        string? package = null, expectedHash = null;
        var builtin = false;
        var remaining = new List<string>();
        for (var i = 0; i < arguments.Length; i++)
        {
            var argument = arguments[i];
            if (argument == "--builtin-knowledge")
            {
                if (builtin) throw new ArgumentException("内置资料参数重复。");
                builtin = true;
            }
            else if (argument is "--data-package" or "--data-package-sha256")
            {
                if (++i >= arguments.Length || arguments[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("资料包参数缺少值。");
                if (argument == "--data-package")
                {
                    if (package is not null) throw new ArgumentException("资料包参数重复。");
                    package = arguments[i];
                }
                else
                {
                    if (expectedHash is not null) throw new ArgumentException("资料包校验参数重复。");
                    expectedHash = arguments[i];
                }
            }
            else remaining.Add(argument);
        }
        if (builtin && package is not null || expectedHash is not null && package is null && !builtin)
            throw new ArgumentException("资料包选择参数不能混用。");
        if (expectedHash is not null && (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit)))
            throw new ArgumentException("资料包校验值格式无效。");

        // A saved, stale choice can recover to one complete built-in snapshot. Explicit
        // command-line choices must fail instead, so an MCP caller cannot query the wrong pack.
        if (package is null && !builtin && !ignoreSavedSelection && File.Exists(SettingsPath))
        {
            try
            {
                DesktopDataPaths.RequirePlainPath(SettingsPath);
                if (new FileInfo(SettingsPath).Length > 32768) throw new InvalidDataException();
                var saved = JsonSerializer.Deserialize<Selection>(File.ReadAllText(SettingsPath));
                if (saved is null || saved.SchemaVersion != 1 || !Path.IsPathFullyQualified(saved.PackagePath))
                    throw new InvalidDataException();
                var metadata = ResourcePack.ValidateExternal(saved.PackagePath);
                if (!string.Equals(metadata.PackageSha256, saved.PackageSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException();
                package = saved.PackagePath;
                expectedHash = saved.PackageSha256;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
            {
                StartupWarning = "上次选择的资料包已变动、缺失或无法读取。本次使用完整内置资料，请重新选择并校验外置资料包。";
            }
        }
        ResourcePack.Initialize(package);
        if (expectedHash is not null && !string.Equals(ResourcePack.Current.PackageSha256, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("资料包在选择后发生变化，请重新校验；本次未启动查询。");
        return remaining.ToArray();
    }

    public static ResourcePackInfo SelectForNextStart(string packagePath)
    {
        var metadata = ResourcePack.ValidateExternal(packagePath);
        var selection = new Selection(1, metadata.PackagePath!, metadata.PackageSha256);
        DesktopDataPaths.RequirePlainPath(SettingsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(selection, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return metadata;
    }

    public static void SelectBuiltinForNextStart()
    {
        DesktopDataPaths.RequirePlainPath(SettingsPath);
        if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
    }
}
