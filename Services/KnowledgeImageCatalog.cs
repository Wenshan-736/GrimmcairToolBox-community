using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Grimmcair.Desktop.Services;

/// <summary>A saved citation, never an arbitrary image location or model-provided URL.</summary>
public sealed record KnowledgeImageReference(string ImageId, string EntryId, string KnowledgeRevision, string Sha256);

public sealed record KnowledgeImageInfo(string ImageId, IReadOnlyList<string> EntryIds, string Path, string MimeType,
    int Width, int Height, string Sha256, string Title, string Caption, string SourceUrl, string Author,
    string? SourceVersion, string Completeness, string Usage, string Scope, string? SourceContextVersion)
{
    public string Uri => KnowledgeImageCatalog.UriPrefix + ImageId;
}

/// <summary>Only registered, complete, hash-checked package images may leave the resource boundary.</summary>
public sealed class KnowledgeImageCatalog
{
    public const string ManifestName = "knowledge-images.json";
    public const string UriPrefix = "grimmcair://knowledge/images/";
    public const int MaximumImageBytes = 8 * 1024 * 1024;
    public const int MaximumMessageImages = 6;
    private readonly Dictionary<string, KnowledgeImageInfo> images = new(StringComparer.Ordinal);
    private readonly Func<string, byte[]> readBytes;
    public string KnowledgeRevision { get; }
    public int Count => images.Count;
    private static readonly Lazy<KnowledgeImageCatalog> Current = new(() => AiKnowledgeCatalog.Load().Images);
    public static KnowledgeImageCatalog Load() => Current.Value;

    public IReadOnlyList<KnowledgeImageReference> ReferencesForEntry(string entryId) => images.Values
        .Where(image => image.EntryIds.Contains(entryId, StringComparer.Ordinal)).OrderBy(image => image.ImageId, StringComparer.Ordinal)
        .Take(4).Select(image => new KnowledgeImageReference(image.ImageId, entryId, KnowledgeRevision, image.Sha256)).ToArray();

    public IReadOnlyList<KnowledgeImageReference> PreviewReferences(int maximum = MaximumMessageImages) => images.Values
        .OrderBy(image => image.ImageId, StringComparer.Ordinal).Take(Math.Clamp(maximum, 0, MaximumMessageImages))
        .Select(image => new KnowledgeImageReference(image.ImageId, image.EntryIds[0], KnowledgeRevision, image.Sha256)).ToArray();

    // The desktop gallery pages through the validated registry independently of
    // chat limits. It never discovers images by enumerating package/disk paths.
    public IReadOnlyList<KnowledgeImageReference> GalleryReferences(int offset = 0, int pageSize = 12)
    {
        if (offset < 0 || pageSize is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(offset), "图片分页范围无效。");
        return images.Values.OrderBy(image => image.ImageId, StringComparer.Ordinal).Skip(offset).Take(pageSize)
            .Select(image => new KnowledgeImageReference(image.ImageId, image.EntryIds[0], KnowledgeRevision, image.Sha256)).ToArray();
    }

    private KnowledgeImageCatalog(string revision, Func<string, byte[]> read) { KnowledgeRevision = revision; readBytes = read; }

    internal static KnowledgeImageCatalog Empty(string revision) => new(revision, _ => throw new FileNotFoundException());

    internal static KnowledgeImageCatalog FromManifest(string manifest, string revision, Func<string, byte[]> read,
        IReadOnlySet<string>? allowedEntries = null)
    {
        if (manifest.Length > 2 * 1024 * 1024 || !IsSafeRevision(revision)) throw new InvalidDataException("图片登记表或资料版本无效。");
        var root = JsonNode.Parse(manifest) as JsonObject ?? throw new InvalidDataException("图片登记表无效。");
        if (root["schemaVersion"]?.GetValue<int>() != 1 || root["images"] is not JsonArray rows || rows.Count > 2048)
            throw new InvalidDataException("图片登记表版本或数量无效。");
        var result = new KnowledgeImageCatalog(revision, read);
        foreach (var node in rows)
        {
            if (node is not JsonObject row) throw new InvalidDataException("图片登记项无效。");
            var id = Text(row, "imageId", 128);
            var path = Text(row, "path", 256);
            var mime = Text(row, "mimeType", 40);
            var sha = Text(row, "sha256", 64).ToLowerInvariant();
            var width = row["width"]?.GetValue<int>() ?? 0;
            var height = row["height"]?.GetValue<int>() ?? 0;
            var entries = (row["entryIds"] as JsonArray)?.Select(value => value?.GetValue<string>() ?? "").ToArray() ?? [];
            if (!IsSafeId(id) || !IsSafePath(path, mime) || !Regex.IsMatch(sha, "^[0-9a-f]{64}$")
                || width is < 1 or > 12000 || height is < 1 or > 12000 || (long)width * height > 40_000_000
                || entries.Length is < 1 or > 32 || entries.Distinct(StringComparer.Ordinal).Count() != entries.Length
                || entries.Any(entry => !IsSafeEntryId(entry) || allowedEntries is not null && !allowedEntries.Contains(entry)))
                throw new InvalidDataException("图片编号、路径、尺寸或条目关联无效。");
            var completeness = Text(row, "completeness", 32);
            if (completeness != "complete") throw new InvalidDataException("未完整收录的图片不能作为可显示图片登记。");
            var sourceUrl = Text(row, "sourceUrl", 2048, optional: true);
            if (sourceUrl.Length > 0 && (!System.Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0))
                throw new InvalidDataException("图片来源链接无效。");
            var info = new KnowledgeImageInfo(id, entries, path, mime, width, height, sha,
                Text(row, "title", 180), Text(row, "caption", 1000, true), sourceUrl, Text(row, "author", 180, true),
                NullableText(row, "sourceVersion", 100), completeness, Text(row, "usage", 100, true),
                Text(row, "scope", 100, true), NullableText(row, "sourceContextVersion", 100));
            if (!result.images.TryAdd(id, info)) throw new InvalidDataException("图片编号重复。");
        }
        return result;
    }

    public JsonArray Metadata(string entryId, int limit = 4) => new(images.Values
        .Where(image => image.EntryIds.Contains(entryId, StringComparer.Ordinal)).OrderBy(image => image.ImageId, StringComparer.Ordinal)
        .Take(Math.Clamp(limit, 0, 4)).Select(image => (JsonNode)new JsonObject
        {
            ["imageId"] = image.ImageId, ["uri"] = image.Uri, ["entryId"] = entryId,
            ["knowledgeRevision"] = KnowledgeRevision, ["sha256"] = image.Sha256,
            ["title"] = image.Title, ["caption"] = image.Caption, ["mimeType"] = image.MimeType,
            ["width"] = image.Width, ["height"] = image.Height, ["author"] = image.Author,
            ["sourceUrl"] = image.SourceUrl, ["sourceVersion"] = image.SourceVersion,
            ["sourceContextVersion"] = image.SourceContextVersion, ["completeness"] = image.Completeness,
            ["usage"] = image.Usage
        }).ToArray());

    public JsonObject ListResources(string? cursor = null)
    {
        var ordered = images.Values.OrderBy(image => image.ImageId, StringComparer.Ordinal).ToArray();
        var start = 0;
        var token = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(KnowledgeRevision)))[..16];
        if (cursor is not null)
        {
            var parts = cursor.Split(':');
            if (parts.Length != 2 || parts[0] != token || !int.TryParse(parts[1], out start) || start < 0 || start >= ordered.Length)
                throw new ArgumentException("图片列表续读位置无效，或资料包已变化。");
        }
        var result = new JsonObject { ["resources"] = new JsonArray(ordered.Skip(start).Take(32).Select(image => (JsonNode)new JsonObject
        {
            ["uri"] = image.Uri, ["name"] = image.ImageId, ["title"] = image.Title,
            ["description"] = image.Caption, ["mimeType"] = image.MimeType
        }).ToArray()) };
        if (start + 32 < ordered.Length) result["nextCursor"] = token + ":" + (start + 32);
        return result;
    }

    public JsonObject ReadResource(string uri)
    {
        if (!uri.StartsWith(UriPrefix, StringComparison.Ordinal) || !images.TryGetValue(uri[UriPrefix.Length..], out var image)
            || image.Uri != uri) throw new ArgumentException("只允许读取已登记的知识库图片 URI。");
        var bytes = ReadVerifiedBytes(image);
        return new JsonObject { ["contents"] = new JsonArray(new JsonObject
        { ["uri"] = image.Uri, ["mimeType"] = image.MimeType, ["blob"] = Convert.ToBase64String(bytes) }) };
    }

    public bool TryResolve(KnowledgeImageReference reference, out KnowledgeImageInfo? image)
    {
        image = null;
        if (!IsValidReference(reference) || reference.KnowledgeRevision != KnowledgeRevision
            || !images.TryGetValue(reference.ImageId, out var registered) || registered.Sha256 != reference.Sha256
            || !registered.EntryIds.Contains(reference.EntryId, StringComparer.Ordinal)) return false;
        image = registered;
        return true;
    }

    public byte[] Read(KnowledgeImageReference reference) => TryResolve(reference, out var image)
        ? ReadVerifiedBytes(image!) : throw new InvalidDataException("图片引用与当前资料包不匹配，请重新查询资料。");

    public IReadOnlyList<KnowledgeImageReference> ReferencesFromToolResult(string tool, string json)
    {
        if (tool != "get_knowledge_entry" || json.Length > 150_000) return [];
        try
        {
            var result = JsonNode.Parse(json) as JsonObject;
            if (result?["status"]?.GetValue<string>() != "ok" || result["images"] is not JsonArray rows) return [];
            var entry = result["id"]?.GetValue<string>() ?? "";
            return rows.OfType<JsonObject>().Take(4).Select(row => new KnowledgeImageReference(
                Text(row, "imageId", 128), entry, Text(row, "knowledgeRevision", 256), Text(row, "sha256", 64)))
                .Where(reference => TryResolve(reference, out _)).Distinct().ToArray();
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or InvalidDataException or ArgumentException)
        { return []; }
    }

    private byte[] ReadVerifiedBytes(KnowledgeImageInfo image)
    {
        var bytes = readBytes(image.Path);
        if (bytes.Length is < 12 or > MaximumImageBytes || Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != image.Sha256)
            throw new InvalidDataException("图片内容与登记校验不一致。");
        var dimensions = ImageDimensions(bytes, image.MimeType);
        if (dimensions != (image.Width, image.Height)) throw new InvalidDataException("图片格式或尺寸与登记不一致。");
        return bytes;
    }

    internal static (int Width, int Height) ImageDimensions(byte[] bytes, string mime)
    {
        if (mime == "image/png" && bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            && bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            return (BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
        if (mime == "image/jpeg" && bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8)
        {
            var offset = 2;
            while (offset + 3 < bytes.Length)
            {
                if (bytes[offset++] != 0xff) break;
                while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
                if (offset >= bytes.Length) break;
                var marker = bytes[offset++];
                if (marker is 0xd9 or 0xda) break;
                if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
                if (offset + 2 > bytes.Length) break;
                var size = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
                if (size < 2 || offset + size > bytes.Length) break;
                if (marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
                    if (size >= 7) return (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 5, 2)), BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 3, 2)));
                offset += size;
            }
        }
        throw new InvalidDataException("仅支持登记且格式有效的 PNG/JPEG 图片。");
    }

    public static bool IsValidReference(KnowledgeImageReference? reference) => reference is not null && IsSafeId(reference.ImageId)
        && IsSafeEntryId(reference.EntryId) && IsSafeRevision(reference.KnowledgeRevision)
        && Regex.IsMatch(reference.Sha256 ?? "", "^[0-9a-f]{64}$");
    private static bool IsSafeRevision(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
    private static bool IsSafeId(string? value) => value is not null && Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$");
    private static bool IsSafeEntryId(string? value) => value is not null && value.Length <= 256 && Regex.IsMatch(value, "^(item|equipment|hero|boss|skill|badge|effect|concept):[A-Za-z0-9_:\\-]+$");
    private static bool IsSafePath(string path, string mime) => path.Length <= 256
        && Regex.IsMatch(path, "^(KnowledgeImages|Icons)/[A-Za-z0-9][A-Za-z0-9._-]*\\.(png|jpg|jpeg)$", RegexOptions.IgnoreCase)
        && !path.Contains("..", StringComparison.Ordinal)
        && (mime == "image/png" && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || mime == "image/jpeg" && (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)));
    private static string? NullableText(JsonObject row, string field, int maximum) => row[field] is null ? null : Text(row, field, maximum, true);
    private static string Text(JsonObject row, string field, int maximum, bool optional = false)
    {
        if (row[field] is null && optional) return "";
        if (row[field] is not JsonValue value || !value.TryGetValue<string>(out var text) || text.Length > maximum
            || text.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t')) || !optional && string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("图片登记文字字段无效：" + field);
        return text;
    }
}
