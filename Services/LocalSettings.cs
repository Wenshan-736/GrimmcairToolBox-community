using System.IO;
using System.Text.Json;

namespace Grimmcair.Desktop.Services;

/// <summary>Local paths and hotkeys. File monitoring is restarted only after an explicit settings save.</summary>
public sealed record LocalSettings
{
    public string GameDirectory { get; init; } = "";
    public string SaveSourceDirectory { get; init; } = FindReferenceSaveDirectory();
    public string ReplaySourceDirectory { get; init; } = "";
    public string ScreenshotDirectory { get; init; } = "";
    public string SaveArchiveDirectory { get; init; } = "";
    public string ReplayArchiveDirectory { get; init; } = "";
    public string ScreenshotHotkey { get; init; } = "Ctrl+F8";
    public string LoadHotkey { get; init; } = "Ctrl+F9";
    public string SaveHotkey { get; init; } = "Ctrl+F10";

    public static LocalSettings Suggested(string? archiveRoot = null)
    {
        archiveRoot ??= Path.Combine(DesktopDataPaths.Current.DataDirectory, "Archives");
        return new LocalSettings
        {
            ScreenshotDirectory = Path.Combine(archiveRoot, "截图"),
            SaveArchiveDirectory = Path.Combine(archiveRoot, "存档"),
            ReplayArchiveDirectory = Path.Combine(archiveRoot, "录像")
        };
    }

    public static string FindReferenceSaveDirectory(string? startDirectory = null)
    {
        for (var current = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var path = Path.Combine(current.FullName, "tool", "ref", "save");
            if (Directory.Exists(path)) return path;
        }
        return "";
    }

    public string ResolveReplaySourceDirectory() => !string.IsNullOrWhiteSpace(ReplaySourceDirectory)
        ? ReplaySourceDirectory : string.IsNullOrWhiteSpace(GameDirectory) ? "" : Path.Combine(GameDirectory, "Replay");

    // The current map writes PreloadGenEnd("TWRPG\\" + name + ".txt").
    // This only fills preferences; it never creates or modifies a game directory.
    public LocalSettings WithGameDirectory(string directory)
    {
        var path = directory.Trim();
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择完整的游戏目录。");
        return this with { GameDirectory = path, ReplaySourceDirectory = Path.Combine(path, "Replay"),
            SaveSourceDirectory = Path.Combine(path, "TWRPG") };
    }

    /// <summary>Called when the user explicitly saves a newly selected source folder.</summary>
    public string? ValidateSaveSourceDirectory()
    {
        if (string.IsNullOrWhiteSpace(SaveSourceDirectory)) return null;
        if (!Directory.Exists(SaveSourceDirectory)) return "存档来源文件夹不存在，请选择游戏写入存档的文件夹。";
        try
        {
            return Directory.EnumerateFiles(SaveSourceDirectory, "*.txt", SearchOption.TopDirectoryOnly).Any()
                ? null : "这个文件夹中没有 .txt 存档文件，请选择已经包含存档的文件夹。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return $"无法读取存档来源文件夹：{error.Message}"; }
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var assigned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in new[] { ("截图", ScreenshotHotkey), ("下档", LoadHotkey), ("存档", SaveHotkey) })
        {
            if (!TryNormalizeHotkey(value, out var hotkey, out var error))
                errors.Add($"{name}快捷键：{error}");
            else if (hotkey.Length > 0 && !assigned.TryAdd(hotkey, name))
                errors.Add($"{name}与{assigned[hotkey]}使用了相同的快捷键 {hotkey}。");
        }
        foreach (var (name, value) in new[] { ("游戏目录", GameDirectory), ("存档来源目录", SaveSourceDirectory), ("录像来源目录", ReplaySourceDirectory), ("截图目录", ScreenshotDirectory), ("存档归档目录", SaveArchiveDirectory), ("录像归档目录", ReplayArchiveDirectory) })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var path = value.Trim();
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.Contains('*') || path.Contains('?') || !Path.IsPathFullyQualified(path))
                errors.Add($"{name}请填写完整的文件夹路径，或暂时留空。");
        }
        return errors;
    }

    public LocalSettings Normalize()
    {
        var errors = Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, errors));
        TryNormalizeHotkey(ScreenshotHotkey, out var screenshot, out _);
        TryNormalizeHotkey(LoadHotkey, out var load, out _);
        TryNormalizeHotkey(SaveHotkey, out var save, out _);
        return this with
        {
            GameDirectory = GameDirectory?.Trim() ?? "",
            SaveSourceDirectory = SaveSourceDirectory?.Trim() ?? "",
            ReplaySourceDirectory = ReplaySourceDirectory?.Trim() ?? "",
            ScreenshotDirectory = ScreenshotDirectory?.Trim() ?? "",
            SaveArchiveDirectory = SaveArchiveDirectory?.Trim() ?? "",
            ReplayArchiveDirectory = ReplayArchiveDirectory?.Trim() ?? "",
            ScreenshotHotkey = screenshot, LoadHotkey = load, SaveHotkey = save
        };
    }

    public static bool TryNormalizeHotkey(string? value, out string normalized, out string error)
    {
        normalized = "";
        error = "";
        if (string.IsNullOrWhiteSpace(value)) return true;
        var parts = value.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? key = null;
        foreach (var part in parts)
        {
            var upper = part.ToUpperInvariant();
            if (upper is "CTRL" or "ALT" or "SHIFT")
            {
                if (!modifiers.Add(upper)) { error = "修饰键不能重复。"; return false; }
            }
            else if (key is null && HotkeyVirtualKey(upper) != 0) key = upper;
            else { error = "支持单个字母、数字、功能键或数字小键盘，也可搭配 Ctrl、Alt、Shift。"; return false; }
        }
        if (key is null) { error = "请同时按下一个功能键、字母或数字。"; return false; }
        if (key == "F4" && modifiers.Contains("ALT"))
        { error = "Alt+F4 是系统关闭窗口快捷键，请换一个组合。"; return false; }
        var ordered = new List<string>();
        if (modifiers.Contains("CTRL")) ordered.Add("Ctrl");
        if (modifiers.Contains("ALT")) ordered.Add("Alt");
        if (modifiers.Contains("SHIFT")) ordered.Add("Shift");
        ordered.Add(key);
        normalized = string.Join('+', ordered);
        return true;
    }

    private static bool IsFunctionKey(string value) => value.Length >= 2 && value[0] == 'F'
        && int.TryParse(value.AsSpan(1), out var number) && number is >= 1 and <= 24 && value == $"F{number}";

    public static uint HotkeyVirtualKey(string key)
    {
        key = key.ToUpperInvariant();
        if (IsFunctionKey(key)) return (uint)(0x70 + int.Parse(key.AsSpan(1)) - 1);
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) return key[0];
        if (key.Length == 7 && key.StartsWith("NUMPAD") && key[6] is >= '0' and <= '9') return (uint)(0x60 + key[6] - '0');
        return key switch { "MULTIPLY" => 0x6A, "ADD" => 0x6B, "SUBTRACT" => 0x6D,
            "DECIMAL" => 0x6E, "DIVIDE" => 0x6F, "SPACE" => 0x20, "INSERT" => 0x2D,
            "HOME" => 0x24, "END" => 0x23, "PAGEUP" => 0x21, "PAGEDOWN" => 0x22,
            "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28,
            "PRINTSCREEN" => 0x2C, "PAUSE" => 0x13, "SCROLL" => 0x91, _ => 0 };
    }
}

public sealed class LocalSettingsSavedEventArgs(LocalSettings oldSettings, LocalSettings newSettings) : EventArgs
{
    public LocalSettings OldSettings { get; } = oldSettings;
    public LocalSettings NewSettings { get; } = newSettings;
}

public sealed class LocalSettingsStore
{
    public string FilePath { get; }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public LocalSettingsStore(string? filePath = null) => FilePath = Path.GetFullPath(filePath ?? Path.Combine(DesktopDataPaths.Current.DataDirectory, "settings.json"));

    public LocalSettings Load()
    {
        if (!File.Exists(FilePath)) return LocalSettings.Suggested(Path.Combine(Path.GetDirectoryName(FilePath)!, "Archives"));
        var settings = JsonSerializer.Deserialize<LocalSettings>(File.ReadAllText(FilePath), JsonOptions)
            ?? throw new InvalidDataException("设置文件内容为空。");
        return settings.Normalize();
    }

    public void Save(LocalSettings settings)
    {
        var content = JsonSerializer.Serialize(settings.Normalize(), JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporaryFile = FilePath + ".tmp";
        File.WriteAllText(temporaryFile, content);
        File.Move(temporaryFile, FilePath, overwrite: true);
    }
}
