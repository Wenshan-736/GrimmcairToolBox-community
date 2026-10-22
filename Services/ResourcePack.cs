using System.IO;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Grimmcair.Desktop.Services;

public sealed record ResourcePackInfo(string Revision, string MapVersion, string MapSha256,
    string ScriptSha256, string Source, string? PackagePath, string PackageSha256, int FileCount);

/// <summary>A single immutable, fully validated resource snapshot for this process.</summary>
public static class ResourcePack
{
    private const string ResourceName = "Grimmcair.Resources.gcpak";
    public const string SupportedMapVersion = "TWRPG 0.94a";
    public const string SupportedMapSha256 = "59459e3eb9dd4a47a2c820a7cd211c00569b067ef5800c2edee70a11d0336d6d";
    public const string SupportedScriptSha256 = "18a3b1b1b917e75c0cb5679891a88c08a7faa964fb18b7c634d5619f9ca9bf56";
    private const long MaxPackageBytes = 256L * 1024 * 1024;
    private const long MaxEntryBytes = 64L * 1024 * 1024;
    private const long MaxTotalBytes = 512L * 1024 * 1024;
    private const int MaxEntries = 20000;
    private static readonly string[] RequiredFiles = ["catalog.json", "save-profile.json", "guide.json", "planner.json", "concepts.json", "equipment-guide.json"];
    private static readonly byte[] Magic = "GCPAK001"u8.ToArray();
    // Build-time counterpart: scripts/pack_resources.py. Not an account key.
    private static readonly byte[] Key = Convert.FromHexString("5D4C07FB567942058A2DB69CC80304FDFEC627B149094CF4B7439E78926D682B");
    private static readonly object Gate = new();
    private static Snapshot? current;
    private static ExceptionDispatchInfo? initializationFailure;
    private sealed record Snapshot(ResourcePackInfo Info, Dictionary<string, byte[]> Files);

    public static ResourcePackInfo Current { get { lock (Gate) { Initialize(); return current!.Info; } } }

    /// <summary>Select before first read. A failed explicit choice never falls back to another package.</summary>
    public static void Initialize(string? explicitPackagePath = null)
    {
        lock (Gate)
        {
            initializationFailure?.Throw();
            if (current is not null)
            {
                if (explicitPackagePath is not null && !string.Equals(Path.GetFullPath(explicitPackagePath), current.Info.PackagePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("资料包已固定；更换资料包后请重新启动程序。");
                return;
            }
            try { current = explicitPackagePath is null ? LoadEmbedded() : LoadExternal(Path.GetFullPath(explicitPackagePath)); }
            catch (Exception error)
            {
                initializationFailure = ExceptionDispatchInfo.Capture(error);
                throw;
            }
        }
    }

    /// <summary>Validate without changing the active process snapshot.</summary>
    public static ResourcePackInfo ValidateExternal(string path) => LoadExternal(Path.GetFullPath(path)).Info;

    // Adopt the same validated bytes once. A rejected saved choice must leave the
    // process uninitialized so its documented complete-built-in fallback still works.
    internal static void InitializeSavedExternal(string path, string expectedHash)
    {
        lock (Gate)
        {
            initializationFailure?.Throw();
            if (current is not null) throw new InvalidOperationException("资料包已固定。");
            var candidate = LoadExternal(Path.GetFullPath(path));
            if (!string.Equals(candidate.Info.PackageSha256, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("资料包在选择后发生变化。");
            current = candidate;
        }
    }

    private static Snapshot LoadEmbedded()
    {
        using var source = typeof(ResourcePack).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("程序中缺少内置资料包，请重新构建或下载完整程序。");
        var encrypted = ReadBounded(source, MaxPackageBytes);
        var hash = Hash(encrypted);
        var files = ReadArchive(DecryptEnvelope(encrypted), false);
        ValidateContent(files);
        return new(new("embedded-" + hash[..12], SupportedMapVersion, SupportedMapSha256,
            SupportedScriptSha256, "embedded", null, hash, files.Count), files);
    }

    private static Snapshot LoadExternal(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxPackageBytes) throw new InvalidDataException("资料包超过允许大小。");
        var bytes = ReadBounded(file, MaxPackageBytes);
        var files = ReadArchive(bytes, true);
        using var manifest = ParseJson(files["manifest.json"]);
        var root = manifest.RootElement;
        if (Text(root, "format") != "grimmcair-knowledge-pack" || Integer(root, "schemaVersion") != 1 || Integer(root, "readerVersion") != 1)
            throw new InvalidDataException("资料包格式或读取版本不兼容。");
        ValidateIdentity(root, "mapSha256", true);
        var revision = Text(root, "revision");
        if (string.IsNullOrWhiteSpace(revision) || revision.Length > 128 || revision.Any(char.IsControl))
            throw new InvalidDataException("资料包修订标记无效。");
        if (!root.TryGetProperty("files", out var listing) || listing.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("资料包缺少完整文件清单。");
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in listing.EnumerateArray())
        {
            var name = Normalize(Text(item, "path"));
            if (name == "manifest.json" || !declared.Add(name) || !files.TryGetValue(name, out var contents))
                throw new InvalidDataException("资料包清单有重复或缺失文件。");
            if (Integer(item, "bytes") != contents.LongLength || Text(item, "sha256") != Hash(contents))
                throw new InvalidDataException("资料包文件完整性校验失败：" + name);
        }
        if (declared.Count != files.Count - 1) throw new InvalidDataException("资料包含有未登记的文件。");
        files.Remove("manifest.json");
        ValidateContent(files);
        return new(new(revision, SupportedMapVersion, SupportedMapSha256, SupportedScriptSha256,
            "external", path, Hash(bytes), files.Count), files);
    }

    private static Dictionary<string, byte[]> ReadArchive(byte[] bytes, bool external)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("资料包文件数过多。");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var allNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var directory = entry.FullName.EndsWith('/');
            var name = Normalize(directory ? entry.FullName[..^1] : entry.FullName);
            if (!allNames.Add(name)) throw new InvalidDataException("资料包含有重复路径。");
            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (unixType != 0 && unixType != 0x8000 && unixType != 0x4000)
                throw new InvalidDataException("资料包不允许链接或特殊文件。");
            if (directory)
            {
                if (entry.Length != 0 || (name != "Icons" && name != "KnowledgeImages"))
                    throw new InvalidDataException("资料包目录无效。");
                continue;
            }
            if (!AllowedName(name, external)) throw new InvalidDataException("资料包含有不支持的文件：" + name);
            if (entry.Length > MaxEntryBytes || entry.Length < 0 ||
                (entry.Length > 1024 * 1024 && entry.Length > Math.Max(1, entry.CompressedLength) * 1000L))
                throw new InvalidDataException("资料包文件大小或压缩比例异常。");
            total = checked(total + entry.Length);
            if (total > MaxTotalBytes) throw new InvalidDataException("资料包解压后超过允许大小。");
            using var input = entry.Open();
            var content = ReadBounded(input, entry.Length);
            if (content.LongLength != entry.Length) throw new InvalidDataException("资料包文件长度无效。");
            files.Add(name, content);
        }
        if (external && (!files.TryGetValue("manifest.json", out var manifest) || manifest.Length > 4 * 1024 * 1024))
            throw new InvalidDataException("资料包缺少有效清单。");
        return files;
    }

    private static bool AllowedName(string name, bool external) => RequiredFiles.Contains(name) || name == "knowledge-images.json" ||
        (external && name == "manifest.json") ||
        ((name.StartsWith("Icons/", StringComparison.Ordinal) || name.StartsWith("KnowledgeImages/", StringComparison.Ordinal)) &&
         new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico" }.Contains(Path.GetExtension(name).ToLowerInvariant()));

    private static void ValidateContent(Dictionary<string, byte[]> files)
    {
        foreach (var name in RequiredFiles)
        {
            if (!files.TryGetValue(name, out var bytes)) throw new InvalidDataException("资料包缺少必要数据：" + name);
            using var json = ParseJson(bytes);
            var root = json.RootElement;
            ValidateShape(name, root);
            if (name == "save-profile.json")
            {
                if (Text(root, "script_sha256") != SupportedScriptSha256) throw new InvalidDataException("存档解码资料与地图不兼容。");
            }
            else
            {
                ValidateIdentity(root, name == "catalog.json" ? "sourceSha256" : "mapSha256", name is "concepts.json" or "equipment-guide.json");
                if (name is "concepts.json" or "equipment-guide.json" && Integer(root, "schemaVersion") != 1)
                    throw new InvalidDataException("资料JSON版本不兼容。");
            }
            CheckReferences(root, files);
        }
        if (files.TryGetValue("knowledge-images.json", out var registry))
        {
            using var json = ParseJson(registry);
            var root = json.RootElement;
            if (Integer(root, "schemaVersion") != 1) throw new InvalidDataException("图片索引版本不兼容。");
            foreach (var (field, expected) in new[] { ("mapVersion", SupportedMapVersion), ("mapSha256", SupportedMapSha256), ("scriptSha256", SupportedScriptSha256) })
                if (root.TryGetProperty(field, out _) && Text(root, field) != expected)
                    throw new InvalidDataException("图片索引地图身份不兼容。");
            if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("图片索引缺少图片清单。");
            foreach (var image in images.EnumerateArray())
            {
                var path = Normalize(Text(image, "path"));
                if (!(path.StartsWith("Icons/", StringComparison.Ordinal) || path.StartsWith("KnowledgeImages/", StringComparison.Ordinal)) ||
                    !files.TryGetValue(path, out var contents) || contents.Length > 8 * 1024 * 1024 || Text(image, "sha256") != Hash(contents))
                    throw new InvalidDataException("图片索引资源校验失败。");
            }
            CheckReferences(root, files);
        }
    }

    private static void ValidateShape(string name, JsonElement root)
    {
        string[] arrays = name switch
        {
            "catalog.json" => ["items", "lookupItems"], "guide.json" => ["heroes", "bosses", "badgeBonuses"],
            "planner.json" => ["items", "recipes"], "concepts.json" => ["sections", "calculatorProfiles"],
            "equipment-guide.json" => ["items"], _ => []
        };
        string[] objects = name switch
        {
            "save-profile.json" => ["generations", "version_parameters", "stack_generations", "object_names"],
            "guide.json" => ["source"], "planner.json" => ["aliases", "source"],
            "concepts.json" or "equipment-guide.json" => ["metadata"], _ => []
        };
        foreach (var field in arrays)
            if (!root.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("资料JSON结构不兼容：" + name + "/" + field);
        foreach (var field in objects)
            if (!root.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("资料JSON结构不兼容：" + name + "/" + field);
    }

    private static void ValidateIdentity(JsonElement root, string mapHashField, bool requireScript)
    {
        if (Text(root, "mapVersion") != SupportedMapVersion || Text(root, mapHashField) != SupportedMapSha256 ||
            (requireScript && Text(root, "scriptSha256") != SupportedScriptSha256))
            throw new InvalidDataException("资料包地图版本或来源校验值不兼容。");
    }

    private static void CheckReferences(JsonElement node, Dictionary<string, byte[]> files)
    {
        if (node.ValueKind == JsonValueKind.Object)
            foreach (var property in node.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var text = property.Value.GetString()!;
                    var isReference = property.Name is "icon" or "heroIcon" or "skillIcon" ||
                        (property.Name is "path" or "resourcePath" && (text.StartsWith("Icons/", StringComparison.Ordinal) || text.StartsWith("KnowledgeImages/", StringComparison.Ordinal)));
                    if (isReference && text.Length != 0 && !files.ContainsKey(Normalize(text)))
                        throw new InvalidDataException("资料包缺少引用图片：" + text);
                }
                CheckReferences(property.Value, files);
            }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray()) CheckReferences(child, files);
    }

    private static JsonDocument ParseJson(byte[] bytes)
    {
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
        try { RejectDuplicateProperties(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }
    private static void RejectDuplicateProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("资料JSON含有重复字段。");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray()) RejectDuplicateProperties(child);
    }
    private static string Text(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new InvalidDataException("资料包字段无效：" + name);
    private static long Integer(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.TryGetInt64(out var number)
        ? number : throw new InvalidDataException("资料包数字字段无效：" + name);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] ReadBounded(Stream source, long maximum)
    {
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("资料包实际长度超过允许大小。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    internal static byte[] DecryptEnvelope(byte[] encrypted)
    {
        if (encrypted.Length < Magic.Length + 12 + 16 || !encrypted.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("资料包格式无效。");
        var plaintext = new byte[encrypted.Length - Magic.Length - 12 - 16];
        try
        {
            using var aes = new AesGcm(Key, 16);
            aes.Decrypt(encrypted.AsSpan(Magic.Length, 12), encrypted.AsSpan(Magic.Length + 12, plaintext.Length),
                encrypted.AsSpan(encrypted.Length - 16, 16), plaintext, Magic);
            return plaintext;
        }
        catch (CryptographicException error)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException("加密资料包校验失败，文件可能已损坏。", error);
        }
    }
    private static string Normalize(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 240 || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl) ||
            name.StartsWith('/') || name.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
            throw new ArgumentException("资料包名称必须是包内规范相对路径。", nameof(name));
        return name;
    }
    public static bool Contains(string name)
    {
        lock (Gate) { Initialize(); return current!.Files.ContainsKey(Normalize(name)); }
    }
    public static byte[] ReadBytes(string name)
    {
        lock (Gate)
        {
            Initialize();
            if (!current!.Files.TryGetValue(Normalize(name), out var bytes)) throw new FileNotFoundException("资料包中未收录指定资源。", name);
            return (byte[])bytes.Clone();
        }
    }
    public static string ReadText(string name) => Encoding.UTF8.GetString(ReadBytes(name));
}
