using System.IO;
using System.Text.Json;

namespace Grimmcair.Desktop.Services;

/// <summary>Only the endpoint is persisted here. Remembered login is stored separately with Windows user protection.</summary>
public sealed class DesktopConnectionSettings
{
    public string BaseUrl { get; set; } = "";
    public static DesktopConnectionSettings Load(string path)
    {
        if (!File.Exists(path)) return new();
        var settings = JsonSerializer.Deserialize<DesktopConnectionSettings>(File.ReadAllText(path));
        if (settings?.BaseUrl is null) throw new InvalidDataException("服务连接设置无法读取。");
        settings.BaseUrl = settings.BaseUrl.Trim();
        return settings;
    }
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this));
        File.Move(path + ".tmp", path, true);
    }
}
