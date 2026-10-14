using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace Grimmcair.Desktop.Services;

/// <summary>Player-facing projection of the four public resources already shipped with the desktop.</summary>
internal sealed class AiKnowledgeCatalog
{
    internal const int MaxEntryCharacters = 24000;
    private string Notice => "仅查询当前桌面已收录的公开资料；本版原说明、参考资料、静态核对和未确认内容按条目保留。静态核对不等于游戏实测；没有收录不代表不存在。" +
        (HasPlayerGuides ? "已接入部分获准收录的玩家攻略，作者和来源版本单独保留。" : "玩家攻略正文尚未接入此预览。") + "资料范围以当前公开资料包为准；未发布研究稿不在查询范围。";
    private sealed record Entry(string Id, string Name, string Kind, string Source, string Version, string MapSha256, JsonObject Content, string SearchText, JsonObject ReaderContent, JsonObject ReaderSource, string ReaderSearchText);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
    private readonly HashSet<string> versions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> resourceSha256 = new(StringComparer.Ordinal);
    internal KnowledgeImageCatalog Images { get; private set; } = KnowledgeImageCatalog.Empty("unloaded");
    private bool HasPlayerGuides => entries.Values.Any(IsPlayerGuide);
    private static bool IsPlayerGuide(Entry entry) => Text(entry.Content, "sourceKind") == "player_guide";

    // A publisher may reuse its human-readable revision label. Bind saved image
    // references to the entire immutable package, including captions and sources.
    public static AiKnowledgeCatalog Load() => FromResources(ResourcePack.ReadText, ResourcePack.ReadBytes, ResourcePack.Contains, ResourcePack.Current.PackageSha256);

    // Inject only fixed resource names, so tests cannot accidentally consult saves or account state.
    internal static AiKnowledgeCatalog FromResources(Func<string, string> read, Func<string, byte[]>? readBytes = null,
        Func<string, bool>? contains = null, string? revision = null)
    {
        var result = new AiKnowledgeCatalog();
        string? expectedVersion = null;
        string? expectedDigest = null;
        string? expectedScriptDigest = null;
        var itemSearch = new Dictionary<string, string>(StringComparer.Ordinal);
        var itemReference = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var file in new[] { "catalog.json", "guide.json", "concepts.json", "equipment-guide.json" })
        {
            var resourceText = read(file);
            result.resourceSha256[file] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resourceText))).ToLowerInvariant();
            var root = JsonNode.Parse(resourceText) as JsonObject ?? throw new InvalidDataException("内置知识资料格式无效。");
            var version = Text(root, "mapVersion");
            var digest = Text(root, "mapSha256");
            if (digest.Length == 0 && file == "catalog.json") digest = Text(root, "sourceSha256");
            expectedVersion ??= version;
            expectedDigest ??= digest;
            if (string.IsNullOrWhiteSpace(digest) || !string.Equals(expectedVersion, version, StringComparison.Ordinal) ||
                !string.Equals(expectedDigest, digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("内置知识资料的地图版本不一致。");
            var scriptDigest = Text(root, "scriptSha256");
            if (scriptDigest.Length > 0)
            {
                expectedScriptDigest ??= scriptDigest;
                if (!string.Equals(expectedScriptDigest, scriptDigest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("内置知识资料的脚本版本不一致。");
            }
            if (version.Length == 0) version = "资料版本未标注";
            result.versions.Add(version);
            void Add(string id, string name, string kind, JsonObject value, string searchMetadata = "")
            {
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return;
                var text = id + " " + name + " " + searchMetadata + " " + Flatten(value);
                var reader = AiKnowledgeReader.Content(id, value);
                var source = AiKnowledgeReader.Source(id, value);
                result.entries.TryAdd(id, new(id, name, kind, file, version, digest, value, text, reader, source, name + " " + AiKnowledgeReader.SearchText(reader)));
            }
            if (file == "catalog.json")
            {
                foreach (var item in Rows(root, "items"))
                {
                    if (item["displayable"] is JsonValue visible && visible.TryGetValue<bool>(out var isVisible) && !isVisible) continue;
                    var projection = Pick(item, "id", "name", "description", "effectsText", "effectsSource", "category", "subcategory", "stage", "stageSource", "recipes", "recipePlans", "recipesSource", "usedIn");
                    projection["statusNotice"] = "地图物品原说明与装备速查来源分开保留；速查未标注的版本不能推断为当前地图已核。";
                    projection["recipeNotice"] = "recipes 中各项是可选的完整配方，不是全部相加；recipePlans.required 为必需材料，同一 choices 组内 options 选一。保留 recipesSource，空配方不表示无法获得。";
                    var metadata = Flatten(Pick(item, "searchText", "searchTags", "aliases"));
                    itemSearch[Text(item, "id")] = metadata;
                    itemReference[Text(item, "id")] = Pick(item, "effectsSource", "stageSource");
                    Add("item:" + Text(item, "id"), Text(item, "name"), "物品与配方", projection, metadata);
                }
            }
            else if (file == "equipment-guide.json")
            {
                var metadata = root["metadata"] as JsonObject;
                var records = metadata is null ? new Dictionary<string, JsonObject>() : Rows(metadata, "records")
                    .Where(x => Text(x, "itemId").Length > 0).GroupBy(x => Text(x, "itemId")).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
                foreach (var item in Rows(root, "items"))
                {
                    var projection = Pick(item, "id", "name", "sections");
                    if (records.TryGetValue(Text(item, "id"), out var record))
                    {
                        foreach (var pair in Pick(record, "researchDepth", "publicationScope", "stage", "stageStatus", "reviewStatus", "sectionSources", "sectionStatuses", "sourceCautions", "claimStatuses", "gameTested", "evidenceIds"))
                            projection[pair.Key] = pair.Value?.DeepClone();
                        if (record["claims"] is JsonArray claims)
                            projection["claims"] = new JsonArray(claims.OfType<JsonObject>().Select(x => (JsonNode)Pick(x, "id", "status", "property", "evidenceIds")).ToArray());
                    }
                    var reference = itemReference.TryGetValue(Text(item, "id"), out var itemContext) ? (JsonObject)itemContext.DeepClone() : new JsonObject();
                    if (Text(projection, "researchDepth") == "brief" && metadata?["briefReading"]?["sourceIdentity"] is JsonObject identity)
                        foreach (var pair in Pick(identity, "reference_map_version")) reference[pair.Key] = pair.Value?.DeepClone();
                    if (reference.Count > 0) projection["referenceSource"] = reference;
                    projection["statusNotice"] = "已收录的装备阅读说明；各段的参考差异、未核实和游戏未测限制仍有效。";
                    Add("equipment:" + Text(item, "id"), Text(item, "name"), "装备说明", projection,
                        itemSearch.GetValueOrDefault(Text(item, "id"), "") + " " + Flatten(Pick(item, "searchTags", "aliases")));
                }
            }
            else if (file == "guide.json")
            {
                foreach (var group in new[] { "heroes", "bosses" })
                foreach (var owner in Rows(root, group))
                {
                    var ownerId = Text(owner, "id");
                    var ownerName = Text(owner, "name");
                    var kind = group == "heroes" ? "英雄" : "首领";
                    var projection = Pick(owner, "id", "name", "stage", "attributesText", "playersText", "dropSummaryText", "drops", "researchStatus");
                    if (group == "heroes")
                        foreach (var pair in Pick(owner, "aliases", "aliasScopes", "aliasSource"))
                            projection[pair.Key] = pair.Value?.DeepClone();
                    var links = new JsonArray();
                    var units = Rows(owner, "units").ToArray();
                    foreach (var unit in units.Prepend(owner))
                    {
                        var unitId = Text(unit, "id");
                        foreach (var skill in Rows(unit, "skills"))
                        {
                            var skillId = "skill:" + ownerId + ":" + unitId + ":" + Text(skill, "id");
                            var skillName = ownerName + " · " + Text(skill, "name");
                            var skillProjection = Pick(skill, "id", "name", "key", "overviewText", "basicText", "detailsText", "readingSections", "formulas", "rangeText", "researchStatus", "mechanismCoverage", "runtimeTested", "evidenceIds", "readingClaimIds", "referenceEnumeration", "readingProvenance");
                            if (skill["readableProvenance"] is JsonObject provenance)
                            {
                                skillProjection["readingStatus"] = Pick(provenance, "sourceGroup", "runtimeTested");
                                if (provenance["dimensions"] is JsonObject dimensions)
                                {
                                    var statuses = new JsonObject();
                                    foreach (var dimension in dimensions)
                                        if (dimension.Value is JsonObject dimensionValue) statuses[dimension.Key] = Pick(dimensionValue, "status");
                                    skillProjection["dimensionStatus"] = statuses;
                                }
                            }
                            if (skill["variant"] is JsonObject variant)
                                skillProjection["variant"] = Pick(variant, "parentAbilityId", "relationKind", "relationStatus", "mechanismStatus", "mapSha256", "scriptSha256", "keyStatus", "referenceKeys", "referenceSha256");
                            skillProjection["owner"] = ownerName;
                            skillProjection["unit"] = Text(unit, "name");
                            Add(skillId, skillName, kind + "技能", skillProjection, Flatten(Pick(skill, "searchTags", "aliases")));
                            links.Add(new JsonObject { ["id"] = skillId, ["name"] = skillName });
                        }
                    }
                    projection["skills"] = links;
                    projection["units"] = new JsonArray(units.Select(x => (JsonNode)Pick(x, "id", "name", "attributesText", "originText")).ToArray());
                    Add((group == "heroes" ? "hero:" : "boss:") + ownerId, ownerName, kind, projection, Flatten(Pick(owner, "searchTags", "aliases")));
                }
                foreach (var badge in Rows(root, "badgeBonuses"))
                    Add("badge:" + Text(badge, "itemId"), Text(badge, "itemId") + " 徽章加成", "徽章说明", Pick(badge, "itemId", "text", "source", "mapDataVersion"));
            }
            else
            {
                foreach (var section in Rows(root, "sections"))
                foreach (var entry in Rows(section, "entries"))
                {
                    var entryId = Text(entry, "id");
                    var projection = Pick(entry, "id", "title", "summary", "paragraphs", "examples", "formulas", "table", "leadFormula", "versionId", "runtimeTested", "claimIds", "evidenceIds", "sourceKind", "sourceStatus", "sourceName", "author", "sourceMapVersion", "sourceVersionNotice", "playerGuideSource");
                    var links = new JsonArray();
                    foreach (var effect in Rows(entry, "effects"))
                    {
                        var id = "effect:" + entryId + ":" + Text(effect, "id");
                        var name = string.Join(" · ", new[] { Text(effect, "heroName"), Text(effect, "skillName"), Text(entry, "title") }.Where(x => x.Length > 0));
                        var effectProjection = Pick(effect, "id", "heroName", "skillName", "key", "itemId", "valueText", "targetText", "applicationText", "stackingText", "stackingRulesText", "stackingTooltip", "scalingText", "scalingKind", "scalingAttributes", "sourceKind", "sourceStatus", "sourceNoticeText", "sourceDifferenceText", "dimensionStatus", "runtimeTested", "sourceLinks", "linkedSourceKinds", "referenceSource", "sourceRefs", "equipmentFamilyId", "equipmentFamilyName", "claimIds", "evidenceIds", "noteReferences");
                        if (effect["sourceLinks"] is JsonArray sourceLinks)
                            effectProjection["sourceLinks"] = new JsonArray(sourceLinks.OfType<JsonObject>()
                                .Select(x => (JsonNode)Pick(x, "id", "sourceKind", "heroName", "skillName", "itemId", "key", "sourceStatus", "dimensionStatus")).ToArray());
                        if (effect["sourceRefs"] is JsonArray sourceRefs)
                            effectProjection["sourceContexts"] = new JsonArray(sourceRefs.OfType<JsonObject>()
                                .Select(x => Pick(x, "file", "status", "version")).DistinctBy(x => x.ToJsonString(AiKnowledgeJson.Options)).Select(x => (JsonNode)x).ToArray());
                        Add(id, name, "增益与减益", effectProjection);
                        links.Add(new JsonObject { ["id"] = id, ["name"] = name });
                    }
                    projection["effects"] = links;
                    Add("concept:" + entryId, Text(entry, "title"), Text(section, "title"), projection, Flatten(Pick(entry, "searchTags", "aliases")));
                }
            }
        }
        foreach (var entry in result.entries.Values)
            if (entry.Id.StartsWith("skill:", StringComparison.Ordinal) && entry.Content["variant"] is JsonObject variant &&
                Text(variant, "parentAbilityId") is { Length: > 0 } parentAbility)
            {
                var parentId = entry.Id[..(entry.Id.LastIndexOf(':') + 1)] + parentAbility;
                if (parentId != entry.Id && result.entries.TryGetValue(parentId, out var parent))
                    entry.ReaderContent["relatedSkill"] = new JsonObject { ["id"] = parent.Id, ["name"] = parent.Name };
            }
        foreach (var group in result.entries.Values.GroupBy(x => x.Kind)) result.counts[group.Key] = group.Count();
        revision ??= Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", result.resourceSha256.OrderBy(x => x.Key)
            .Select(pair => pair.Key + ":" + pair.Value))))).ToLowerInvariant();
        result.Images = readBytes is not null && contains?.Invoke(KnowledgeImageCatalog.ManifestName) == true
            ? KnowledgeImageCatalog.FromManifest(read(KnowledgeImageCatalog.ManifestName), revision, readBytes, result.entries.Keys.ToHashSet(StringComparer.Ordinal))
            : KnowledgeImageCatalog.Empty(revision);
        return result;
    }

    public JsonObject Status() => new()
    {
        ["name"] = "桌面内置资料预览 MCP", ["transport"] = "stdio", ["readOnly"] = true,
        ["entryCount"] = entries.Count, ["versions"] = JsonSerializer.SerializeToNode(versions.Order()),
        ["counts"] = JsonSerializer.SerializeToNode(counts), ["notice"] = Notice,
        ["resourceSha256"] = JsonSerializer.SerializeToNode(resourceSha256),
        ["knowledgeRevision"] = Images.KnowledgeRevision, ["imageCount"] = Images.Count,
        ["imageInstructions"] = "search/get 仅返回登记图片的简短说明与URI，不包含图片字节。需要图片时由客户端 resources/read 显式读取；不可按外部链接或文件路径自动加载图片。",
        ["fullResearchMcpImplemented"] = false, ["publicPackageExportSupported"] = true,
        ["collections"] = new JsonObject { ["desktop"] = "available", ["player_guides"] = HasPlayerGuides ? "available" : "not_included", ["research"] = "not_included" },
        ["playerGuideCount"] = entries.Values.Count(IsPlayerGuide),
        ["playerGuideSourceVersions"] = JsonSerializer.SerializeToNode(PlayerGuideVersions()),
        ["versionNotice"] = VersionNotice,
        ["readingInstructions"] = "先搜索，再用返回的 id 读取。hasMore=true 时，原样携带 nextCursor 和相同 id、view、field 继续读取；各页合起来才是完整条目。空白、null、缺字段均不等于 0。"
    };

    private const string VersionNotice = "mapVersion 是资料包的地图基准，不代表条目内所有参考都属于该版本。玩家攻略以 sourceMapVersion 和 sourceVersionNotice 说明来源版本，仍是玩家文字。effectsSource、recipesSource、referenceSource、sourceContexts 和各字段状态仍各自有效；版本未标注的参考不得补成本版事实。";

    public JsonObject Search(string query, string? version = null, string? collection = null)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 240) throw new ArgumentException("查询应为 1 至 240 个字符。");
        query = query.Trim();
        if (collection is not (null or "desktop" or "player_guides" or "research")) throw new ArgumentException("collection 仅支持 desktop、player_guides、research。");
        version ??= Regex.Match(query, @"(?i)(?<![\d.])(?:TWRPG\s*)?0\.\d+[a-z]?(?![A-Za-z0-9_.])") is { Success: true } match ? match.Value : null;
        var playerOnly = collection == "player_guides" || collection is null &&
            (query.Contains("天庭小不点", StringComparison.Ordinal) || query.Contains("攻略", StringComparison.Ordinal));
        if (collection == "research" || playerOnly && !HasPlayerGuides)
            return new JsonObject { ["status"] = "not_included", ["query"] = query, ["collection"] = collection ?? "player_guides", ["results"] = new JsonArray(),
                ["requestedVersion"] = version, ["notice"] = collection == "research" ? "当前桌面 MCP 尚未接入完整研究库；不能据此判断磁盘资料不存在。" : "当前资料包尚未接入玩家攻略正文；不能据此判断攻略或玩法不存在。", ["versionNotice"] = VersionNotice };
        if (!playerOnly && VersionMismatch(version) is { } mismatch) { mismatch["query"] = query; mismatch["results"] = new JsonArray(); return mismatch; }
        if (version is not null) ValidateVersion(version);
        var candidates = entries.Values.Where(entry => !playerOnly || IsPlayerGuide(entry))
            .Where(entry => version is null || !IsPlayerGuide(entry) || SourceVersionMatches(entry, version));
        var searchQuery = Regex.Replace(query, @"(?i)(?<![\d.])(?:TWRPG\s*)?0\.\d+[a-z]?(?![A-Za-z0-9_.])", " ").Trim();
        var terms = SearchTerms(searchQuery);
        var ranked = candidates.Select(entry => (Entry: entry, Score: Score(entry, searchQuery, terms)))
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Entry.Id, StringComparer.Ordinal).Take(8).ToArray();
        return new JsonObject
        {
            ["status"] = ranked.Length > 0 ? "matched" : "no_match", ["query"] = query, ["limit"] = 8,
            ["collection"] = playerOnly ? "player_guides" : "desktop", ["requestedVersion"] = version,
            ["notice"] = playerOnly ? "只搜索已收录玩家攻略；version 筛选明确标注的来源版本，未知或其他版本不视为匹配。分段适用范围仍以正文为准。" : "仅搜索桌面已收录资料。地图基准不代表参考资料的版本；静态核对不等于游戏实测。",
            ["resultNotice"] = ranked.Length > 0 ? "结果是候选条目；相关内容可能只是效果、配方或参考描述，读取后再判断。" : "在当前桌面已收录范围内没有匹配；不能据此判断地图中不存在该内容。",
            ["results"] = new JsonArray(ranked.Select(x =>
            {
                var candidate = new JsonObject
                {
                    ["id"] = x.Entry.Id, ["name"] = x.Entry.Name, ["kind"] = x.Entry.Kind,
                    ["mapVersion"] = x.Entry.Version,
                    ["sourceSummary"] = x.Entry.ReaderSource["summary"]?.DeepClone(),
                    ["snippet"] = Snippet(x.Entry.ReaderSearchText, terms)
                };
                if (IsPlayerGuide(x.Entry))
                {
                    candidate["sourceMapVersion"] = x.Entry.Content["sourceMapVersion"]?.DeepClone();
                    candidate["sourceVersionNotice"] = x.Entry.Content["sourceVersionNotice"]?.DeepClone();
                }
                var images = Images.Metadata(x.Entry.Id, 2);
                if (images.Count > 0) candidate["images"] = images;
                return (JsonNode)candidate;
            }).ToArray())
        };
    }

    public JsonObject Get(string id, string? cursor = null, string? version = null, string? field = null, string? view = null)
    {
        if (id.Length is 0 or > 256 || !Regex.IsMatch(id, @"^(item|equipment|hero|boss|skill|badge|effect|concept):[A-Za-z0-9_:\-]+$"))
            throw new ArgumentException("id 必须是搜索返回的公开条目编号，不接受文件路径。");
        if (!entries.TryGetValue(id, out var entry)) return new JsonObject { ["status"] = "no_match", ["id"] = id, ["notice"] = "当前桌面公开条目中没有这个 id；请重新搜索。未匹配不表示地图中不存在。" };
        if ((IsPlayerGuide(entry) ? PlayerGuideVersionMismatch(entry, version) : VersionMismatch(version)) is { } mismatch) { mismatch["id"] = id; return mismatch; }
        if (field is not null && (field.Length is 0 or > 128 || !Regex.IsMatch(field, @"^[A-Za-z][A-Za-z0-9]*$"))) throw new ArgumentException("field 只能是条目 content 中的一个顶层字段名。");
        view ??= field is null ? "reader" : "details";
        if (view is not ("reader" or "details")) throw new ArgumentException("view 仅支持 reader、details。");
        if (view == "reader" && field is not null) throw new ArgumentException("field 读取详细资料中的原字段；请使用 view=details，或省略 view。");
        var result = new JsonObject
        {
            ["status"] = "ok",
            ["id"] = entry.Id, ["name"] = entry.Name, ["kind"] = entry.Kind,
            ["mapVersion"] = entry.Version, ["view"] = view,
            ["truncated"] = false, ["hasMore"] = false, ["nextCursor"] = null
        };
        var images = Images.Metadata(entry.Id);
        if (images.Count > 0) result["images"] = images;
        if (IsPlayerGuide(entry)) result["sourceMapVersion"] = entry.Content["sourceMapVersion"]?.DeepClone();
        if (view == "details")
        {
            result["source"] = entry.Source;
            result["mapSha256"] = entry.MapSha256;
            result["sourceStatus"] = SourceStatus(entry);
            result["relationships"] = Relationships(entry);
            result["notice"] = Notice;
            result["versionNotice"] = VersionNotice;
            result["fieldNotice"] = "空白、null、未收录字段不代表 0、无效果或不能获得；各字段的来源差异仍有效。";
        }
        else
        {
            result["sourceSummary"] = entry.ReaderSource.DeepClone();
            result["notice"] = "地图基准不代表参考资料的版本；静态核对不等于游戏实测。空白、null 或缺字段不等于 0、无效果或不能获得。";
            result["details"] = "证据与原始字段：用相同 id、view=details 读取；可用 field 单独读取一个原字段。";
            if (entry.Kind is "装备说明" or "物品与配方")
            {
                var related = (entry.Kind == "装备说明" ? "item:" : "equipment:") + Text(entry.Content, "id");
                if (entries.ContainsKey(related)) result["relatedEntry"] = related;
            }
            if (entry.Kind == "物品与配方")
                result["recipeNotice"] = "recipes 各项是可选的完整配方，不是全部相加；recipePlans.required 为必需材料，同一 choices 组内 options 选一。它们表示配方，不能推断配装推荐；空配方不代表无法获取。";
        }
        JsonObject content;
        if (field is not null)
        {
            result["field"] = field;
            if (!entry.Content.TryGetPropertyValue(field, out var value) || value is null || value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text))
            {
                if (cursor is not null) throw new ArgumentException("缺资料的字段没有后续页。");
                result["status"] = "field_not_recorded";
                result["fieldState"] = !entry.Content.ContainsKey(field) ? "absent" : value is null ? "null" : "blank";
                result["content"] = new JsonObject { [field] = value?.DeepClone() };
                return result;
            }
            content = new JsonObject { [field] = value.DeepClone() };
        }
        else content = (JsonObject)(view == "details" ? entry.Content : entry.ReaderContent).DeepClone();
        result["content"] = content;
        if (cursor is null && result.ToJsonString(AiKnowledgeJson.Options).Length <= MaxEntryCharacters) return result;

        // JSON paths keep ownership and numeric/null values. Every string segment can be resumed,
        // including a single huge paragraph; no evidence or trailing text is discarded.
        result.Remove("content");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Id + "\n" + view + "\n" + (field ?? "") + "\n" + content.ToJsonString(AiKnowledgeJson.Options))))[..16];
        var parts = ContentParts(content);
        var start = 0;
        if (cursor is not null)
        {
            var tokens = cursor.Split(':');
            if (tokens.Length != 2 || tokens[0] != fingerprint || !int.TryParse(tokens[1], out start) || start < 0 || start >= parts.Count)
                throw new ArgumentException("续读位置无效或资料已变化；请保持原 id、view、field，并使用最新响应的 nextCursor。");
        }
        result["paged"] = true;
        result["readingInstructions"] = "contentParts 按 path 标明原字段，字符串按 stringOffset 接续。空对象、空数组、数字和 null 保留原值。hasMore=true 时用相同 id、view、field 和 nextCursor 继续；不得把单页当作全文。";
        var page = new JsonArray();
        result["contentParts"] = page;
        var index = start;
        while (index < parts.Count)
        {
            page.Add(parts[index].DeepClone());
            if (result.ToJsonString(AiKnowledgeJson.Options).Length > MaxEntryCharacters - 256 && page.Count > 1) { page.RemoveAt(page.Count - 1); break; }
            index++;
        }
        result["hasMore"] = index < parts.Count;
        result["truncated"] = index < parts.Count; // Legacy flag now means resumable, never discarded.
        result["nextCursor"] = index < parts.Count ? fingerprint + ":" + index : null;
        return result;
    }

    private JsonObject? VersionMismatch(string? requested)
    {
        if (requested is null) return null;
        ValidateVersion(requested);
        var normalized = NormalizeVersion(requested);
        if (versions.Any(value => string.Equals(NormalizeVersion(value), normalized, StringComparison.OrdinalIgnoreCase))) return null;
        if (entries.Values.Any(entry => entry.MapSha256.Length >= 12 && string.Equals(NormalizeVersion(entry.Version) + "-" + entry.MapSha256[..12], normalized, StringComparison.OrdinalIgnoreCase))) return null;
        return new JsonObject { ["status"] = "version_mismatch", ["requestedVersion"] = requested,
            ["availableVersions"] = JsonSerializer.SerializeToNode(versions.Order()), ["notice"] = "请求的版本与当前桌面资料的地图基准不符，不能用本版资料回答该版本的机制。", ["versionNotice"] = VersionNotice };
    }

    private static string NormalizeVersion(string value) => Regex.Replace(value.Trim(), @"(?i)^TWRPG\s*", "").Trim();
    private static void ValidateVersion(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested) || requested.Length > 80) throw new ArgumentException("version 应为 1 至 80 字的地图版本。");
    }
    private static bool SourceVersionMatches(Entry entry, string requested) =>
        Text(entry.Content, "sourceMapVersion") is { Length: > 0 } sourceVersion &&
        string.Equals(NormalizeVersion(sourceVersion), NormalizeVersion(requested), StringComparison.OrdinalIgnoreCase);
    private IEnumerable<string> PlayerGuideVersions() => entries.Values.Where(IsPlayerGuide)
        .Select(entry => Text(entry.Content, "sourceMapVersion")).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
    private static JsonObject? PlayerGuideVersionMismatch(Entry entry, string? requested)
    {
        if (requested is null) return null;
        ValidateVersion(requested);
        if (SourceVersionMatches(entry, requested)) return null;
        var unknown = Text(entry.Content, "sourceMapVersion").Length == 0;
        return new JsonObject { ["status"] = unknown ? "source_version_unknown" : "version_mismatch", ["requestedVersion"] = requested,
            ["sourceMapVersion"] = entry.Content["sourceMapVersion"]?.DeepClone(),
            ["notice"] = unknown ? "这份玩家攻略未注明来源地图版本，不能按请求版本认定适用；省略 version 可读取原文及版本说明。" : "请求版本与这份玩家攻略标注的来源版本不同；不能用它确认请求版本的玩法或机制。",
            ["versionNotice"] = VersionNotice };
    }

    private static JsonObject SourceStatus(Entry entry)
    {
        var status = Pick(entry.Content, "sourceStatus", "sourceKind", "sourceName", "author", "sourceMapVersion", "sourceVersionNotice", "sourceNoticeText", "sourceDifferenceText", "dimensionStatus", "researchStatus", "runtimeTested", "statusNotice", "referenceSource", "referenceEnumeration", "readingStatus", "effectsSource", "recipesSource", "sourceContexts", "researchDepth", "publicationScope", "stageStatus", "reviewStatus", "sectionStatuses", "claimStatuses", "gameTested");
        if (status.ToJsonString(AiKnowledgeJson.Options).Length <= 4000) return status;
        return new JsonObject { ["status"] = "read_source_fields", ["notice"] = "来源状态较长，完整字段保留在条目内容中。请用 get_knowledge_entry 的 field 参数按字段续读，未读完前不可合并成一种来源或版本。",
            ["fields"] = new JsonArray(status.Select(x => (JsonNode)JsonValue.Create(x.Key)!).ToArray()) };
    }

    private JsonArray Relationships(Entry entry)
    {
        var result = new JsonArray();
        if (entry.Content["variant"] is JsonObject)
            result.Add(new JsonObject { ["type"] = "skill_variant", ["field"] = "variant", ["notice"] = "完整对应与状态见 variant 字段。技能变体关系不代表配方、推荐或机制相同。" });
        if (entry.Content.ContainsKey("recipes") || entry.Content.ContainsKey("recipePlans") || entry.Content.ContainsKey("usedIn"))
            result.Add(new JsonObject { ["type"] = "recipe", ["fields"] = new JsonArray("recipes", "recipePlans", "usedIn"), ["notice"] = "这些字段表示已收录配方或材料用途。保留来源、完整配方之间的选择和组内二选一；不能改写成推荐顺序。" });
        if (entry.Kind == "装备说明" || entry.Kind == "物品与配方")
            result.Add(new JsonObject { ["type"] = "same_item", ["itemId"] = Text(entry.Content, "id"), ["notice"] = "相同物品 id 的目录与阅读说明是同一物品的不同资料，不是合成或推荐关系。" });
        result.Add(new JsonObject { ["type"] = "recommendation", ["status"] = HasPlayerGuides ? "separate_player_sources" : "not_included", ["notice"] = HasPlayerGuides ? "已收录的玩家配装与阶段路线在 player_guides 中单独查询，保留作者和来源版本；不从配方或相邻条目推断推荐。" : "玩家攻略的配装推荐和阶段路线尚未接入此 MCP，不从配方或相邻条目推断。" });
        return result;
    }

    private static List<JsonObject> ContentParts(JsonNode? content)
    {
        var parts = new List<JsonObject>();
        void Visit(JsonNode? value, string path)
        {
            if (value is JsonObject obj && obj.Count > 0)
                foreach (var pair in obj) Visit(pair.Value, path + "/" + pair.Key.Replace("~", "~0").Replace("/", "~1"));
            else if (value is JsonArray array && array.Count > 0)
                for (var i = 0; i < array.Count; i++) Visit(array[i], path + "/" + i);
            else if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.Length > 1200)
            {
                for (var offset = 0; offset < text.Length;)
                {
                    var length = Math.Min(1200, text.Length - offset);
                    if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1])) length--;
                    parts.Add(new JsonObject { ["path"] = path, ["value"] = text.Substring(offset, length), ["stringOffset"] = offset, ["stringLength"] = text.Length });
                    offset += length;
                }
            }
            else parts.Add(new JsonObject { ["path"] = path, ["value"] = value?.DeepClone() });
        }
        Visit(content, "");
        return parts;
    }

    private static List<string> SearchTerms(string query)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(query, @"[a-zA-Z0-9_\-]+|[\u4e00-\u9fff]+"))
        {
            var word = match.Value;
            result.Add(word);
            if (word.Length > 2 && word[0] >= '\u4e00')
                for (var n = 2; n <= Math.Min(4, word.Length); n++)
                    for (var i = 0; i <= word.Length - n; i++) result.Add(word.Substring(i, n));
        }
        result.ExceptWith(new[] { "怎么", "如何", "什么", "可以", "是否", "有没有", "告诉", "一下", "这个", "那个", "的技能", "技能", "多少" });
        return result.Take(400).ToList();
    }

    private static int Score(Entry entry, string query, List<string> terms)
    {
        var score = entry.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase) ? 30 : 0;
        var compactQuery = Regex.Replace(query, @"[\s·・•，,。:：\-]", "");
        var compactName = Regex.Replace(entry.Name, @"[\s·・•，,。:：\-]", "");
        var exactHeroAlias = entry.Id.StartsWith("hero:", StringComparison.Ordinal) &&
            entry.Content["aliases"] is JsonArray aliases && aliases.Any(alias =>
                alias is JsonValue value && value.TryGetValue<string>(out var name) &&
                name.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (compactName.Equals(compactQuery, StringComparison.OrdinalIgnoreCase) || exactHeroAlias) score += 500;
        else if (compactQuery.Length >= 2 && compactName.Contains(compactQuery, StringComparison.OrdinalIgnoreCase)) score += 180;
        if (entry.Id.Equals(query, StringComparison.OrdinalIgnoreCase) || Text(entry.Content, "id").Equals(query, StringComparison.OrdinalIgnoreCase)) score += 700;
        if (query.Contains(entry.Name, StringComparison.OrdinalIgnoreCase)) score += 120;
        foreach (var term in terms)
        {
            if (term.Length == 1 && char.IsAsciiLetter(term[0]))
            {
                if (string.Equals(Text(entry.Content, "key"), term, StringComparison.OrdinalIgnoreCase)) score += 150;
                continue;
            }
            if (entry.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 8 + term.Length * 3;
            else if (entry.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase)) score += Math.Min(5, term.Length);
        }
        return score;
    }

    private static string Snippet(string value, List<string> terms)
    {
        var first = terms.Where(x => x.Length >= 2).Select(x => value.IndexOf(x, StringComparison.OrdinalIgnoreCase)).Where(x => x >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, first - 45);
        return (start > 0 ? "…" : "") + value.Substring(start, Math.Min(420, value.Length - start)) + (value.Length - start > 420 ? "…" : "");
    }

    private static string Flatten(JsonNode? node)
    {
        var text = new StringBuilder();
        void Visit(JsonNode? value)
        {
            if (value is JsonObject obj) foreach (var pair in obj) Visit(pair.Value);
            else if (value is JsonArray array) foreach (var item in array) Visit(item);
            else if (value is JsonValue scalar && scalar.TryGetValue<string>(out var s)) text.Append(s).Append(' ');
        }
        Visit(node);
        return text.ToString();
    }

    private static string Text(JsonObject obj, string key) => obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static IEnumerable<JsonObject> Rows(JsonObject obj, string key) => (obj[key] as JsonArray)?.OfType<JsonObject>() ?? [];
    private static JsonObject Pick(JsonObject source, params string[] fields)
    {
        var result = new JsonObject();
        foreach (var field in fields) if (source.TryGetPropertyValue(field, out var value)) result[field] = value?.DeepClone();
        return result;
    }
}
