using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Grimmcair.Desktop.Services;

/// <summary>Lossless player prose with short provenance; the original projection remains available as details.</summary>
internal static class AiKnowledgeReader
{
    internal static JsonObject Content(string id, JsonObject original)
    {
        var kind = id.Split(':')[0];
        var result = kind switch
        {
            "item" => Pick(original, "description", "effectsText", "category", "subcategory", "stage", "recipes", "recipePlans", "usedIn"),
            "equipment" => Pick(original, "sections", "stage", "sourceCautions"),
            "hero" or "boss" => Pick(original, "stage", "attributesText", "playersText", "dropSummaryText", "drops", "skills", "units"),
            "skill" => Pick(original, "key", "owner", "unit", "overviewText", "basicText", "detailsText", "readingSections", "formulas", "rangeText"),
            "badge" => Pick(original, "text"),
            "effect" => Pick(original, "heroName", "skillName", "key", "valueText", "targetText", "applicationText", "stackingText", "stackingRulesText", "stackingTooltip", "scalingText", "scalingAttributes", "sourceNoticeText", "sourceDifferenceText", "equipmentFamilyName"),
            _ => Pick(original, "title", "summary", "paragraphs", "examples", "formulas", "table", "leadFormula", "effects")
        };
        if (Text(original, "sourceKind") == "player_guide")
        {
            // The generated summary repeats source metadata, not gameplay prose.
            // Remove only that exact template; a separately written summary is retained.
            var sourceVersion = Text(original, "sourceMapVersion");
            var metadataSummary = $"玩家攻略。作者／提供者：{Text(original, "author")}。来源：{Text(original, "sourceName")}。来源版本：{(sourceVersion.Length > 0 ? sourceVersion : "未注明")}。{Text(original, "sourceVersionNotice")}";
            if (Text(original, "summary") == metadataSummary) result.Remove("summary");
        }

        if (result["sections"] is JsonArray sections)
        {
            var origins = (original["sectionSources"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
            var projected = new JsonArray();
            for (var i = 0; i < sections.Count; i++)
            {
                if (sections[i] is not JsonObject section) { projected.Add(sections[i]?.DeepClone()); continue; }
                var value = Pick(section, "title", "text");
                // Index and title together prevent attaching a source status to a different repeated heading.
                if (i < origins.Length && Text(origins[i], "title") == Text(section, "title"))
                    value["source"] = Status(Text(origins[i], "status"));
                projected.Add(value);
            }
            result["sections"] = projected;
        }
        if (result["readingSections"] is JsonArray reading)
        {
            result["readingSections"] = new JsonArray(reading.Select(x => x is JsonObject obj ? (JsonNode)Pick(obj, "title", "text") : x?.DeepClone()).ToArray());
            var combined = string.Join("\n\n", reading.OfType<JsonObject>().Select(x => Text(x, "text")));
            // Only remove an exact duplicate after whitespace normalization, never a summary or a subset.
            if (NormalizeText(combined) == NormalizeText(Text(original, "detailsText")) && combined.Length > 0)
                result.Remove("detailsText");
        }
        if (result.ContainsKey("formulas")) result["formulas"] = ReadableTree(result["formulas"], FormulaFields);
        if (result.ContainsKey("examples")) result["examples"] = ReadableTree(result["examples"], FormulaFields);
        if (result["drops"] is JsonArray drops)
            result["drops"] = new JsonArray(drops.Select(x => x is JsonObject drop ? (JsonNode)Drop(drop) : x?.DeepClone()).ToArray());
        foreach (var field in new[] { "recipes", "recipePlans", "usedIn" })
            if (result.ContainsKey(field)) result[field] = RecipeTree(result[field]);
        if (result["units"] is JsonArray units)
            result["units"] = new JsonArray(units.Select(x => x is JsonObject unit ? (JsonNode)Pick(unit, "name", "attributesText", "originText") : x?.DeepClone()).ToArray());
        if (original["sourceLinks"] is JsonArray links)
            result["relatedSources"] = new JsonArray(links.OfType<JsonObject>().Select(x =>
            {
                var source = Pick(x, "heroName", "skillName", "key");
                source["source"] = Status(Text(x, "sourceStatus"));
                if (Text(x, "itemId").Length > 0) source["entryId"] = "item:" + Text(x, "itemId");
                if (x["dimensionStatus"] is JsonObject dimensions) source["fields"] = Dimensions(dimensions);
                return (JsonNode)source;
            }).ToArray());
        return result;
    }

    internal static JsonObject Source(string id, JsonObject original)
    {
        if (Text(original, "sourceKind") == "player_guide")
        {
            var version = Text(original, "sourceMapVersion");
            return new JsonObject
            {
                ["summary"] = $"玩家攻略；作者／提供者：{Text(original, "author")}；来源：{Text(original, "sourceName")}；来源版本：{(version.Length > 0 ? version : "未注明")}。不等于本版已核机制。",
                ["versionNotice"] = original["sourceVersionNotice"]?.DeepClone(),
                ["documentRole"] = original["documentRole"]?.DeepClone(),
                ["documentRoleNotice"] = original["documentRoleNotice"]?.DeepClone()
            };
        }
        var kind = id.Split(':')[0];
        var result = new JsonObject();
        var status = Text(original, "sourceStatus");
        if (status.Length == 0) status = Text(original, "researchStatus");
        result["summary"] = kind switch
        {
            "item" => "物品描述来自本版地图；效果补充、阶段和配方另标来源。",
            "equipment" when Text(original, "researchDepth") == "brief" => "本版装备与技能说明，以及版本未标注的装备速查；逐段来源见正文。",
            "equipment" => "本版装备研读；原说明、静态核对及来源差异分别保留。",
            "hero" => "本版英雄目录；技能说明按链接分别读取。",
            "badge" => "装备速查参考，地图版本未标注。",
            _ when status.Length > 0 => Status(status),
            _ => "本版已收录资料；各项说明中的适用条件和来源限制继续有效。"
        };
        if (original["dimensionStatus"] is JsonObject dimensions)
        {
            result["fields"] = Dimensions(dimensions);
            if (dimensions.Count > 0) result["summary"] = "本条资料按字段分别记录来源和核对状态，具体见下列说明。";
        }
        if (original["claimStatuses"] is JsonObject claims)
            result["includedSources"] = new JsonArray(claims.Where(x => x.Value is JsonValue value && value.TryGetValue<int>(out var count) && count > 0)
                .Select(x => x.Key == "source_description" ? "来源文字记录，具体版本见正文与参考说明。" : Status(x.Key)).Distinct(StringComparer.Ordinal).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        var references = new JsonObject();
        foreach (var field in new[] { "effectsSource", "recipesSource", "stageSource", "referenceSource", "referenceEnumeration" })
            if (original.TryGetPropertyValue(field, out var value)) references[FieldLabel(field)] = Reference(value);
        if (kind == "badge" && original.TryGetPropertyValue("mapDataVersion", out var badgeVersion)) references["地图版本"] = badgeVersion?.DeepClone();
        if (references.Count > 0) result["reference"] = references;
        if (original["sourceContexts"] is JsonArray contexts)
        {
            var concise = contexts.OfType<JsonObject>().Select(x =>
            {
                var context = Pick(x, "version");
                if (x.ContainsKey("status")) context["说明"] = Status(Text(x, "status"));
                return context;
            }).Where(x => x.Count > 0).DistinctBy(x => x.ToJsonString(AiKnowledgeJson.Options)).ToArray();
            if (concise.Length > 0) result["sourceVersions"] = new JsonArray(concise.Select(x => (JsonNode)x).ToArray());
        }
        var runtime = original["runtimeTested"] ?? original["gameTested"] ?? original["readingStatus"]?["runtimeTested"] ?? original["readingProvenance"]?["runtimeTested"];
        if (runtime is JsonValue flag && flag.TryGetValue<bool>(out var tested))
            result["gameTest"] = tested ? "资料记载已做游戏实测；范围以正文为准。" : "尚未游戏实测。";
        if (kind == "skill")
        {
            result["basicText"] = "本版原说明与基础配置；配置值不等于实际结算。";
            if (original["variant"] is JsonObject variant)
            {
                result["variant"] = new JsonObject
                {
                    ["relation"] = Relation(Text(variant, "relationKind")),
                    ["identity"] = Status(Text(variant, "relationStatus")),
                    ["mechanism"] = Status(Text(variant, "mechanismStatus")),
                    ["key"] = Status(Text(variant, "keyStatus"))
                };
                if (variant.TryGetPropertyValue("referenceKeys", out var keys))
                    result["variant"]!["referenceKeys"] = keys?.DeepClone();
            }
        }
        return result;
    }

    internal static string SearchSource(string id, JsonObject original) => Text(Source(id, original), "summary");

    internal static string SearchText(JsonObject reader)
    {
        var text = new System.Text.StringBuilder();
        void Visit(JsonNode? value)
        {
            if (value is JsonObject obj)
                foreach (var pair in obj)
                    if (pair.Key is not ("id" or "entryId" or "source")) Visit(pair.Value);
            if (value is JsonArray array) foreach (var child in array) Visit(child);
            if (value is JsonValue scalar && scalar.TryGetValue<string>(out var s)) text.Append(s).Append(' ');
        }
        Visit(reader);
        return text.ToString();
    }

    private static JsonObject Drop(JsonObject drop)
    {
        var result = Pick(drop, "itemName", "baseChanceText", "notes", "detailsText");
        if (Text(drop, "itemId").Length > 0) result["entryId"] = "item:" + Text(drop, "itemId");
        if (drop.ContainsKey("researchStatus")) result["source"] = Status(Text(drop, "researchStatus"));
        if (drop.ContainsKey("referenceSource")) result["referenceSource"] = Reference(drop["referenceSource"]);
        if (drop.ContainsKey("referenceRecords")) result["referenceRecords"] = DropReference(drop["referenceRecords"]);
        return result;
    }

    private static JsonNode? DropReference(JsonNode? value)
    {
        if (value is JsonArray array) return new JsonArray(array.Select(DropReference).ToArray());
        if (value is not JsonObject obj) return value?.DeepClone();
        var result = Pick(obj, "itemName", "basePercent", "condition", "referenceConditionalPercent");
        var itemId = Text(obj, "itemId");
        if (itemId.Length == 0) itemId = Text(obj, "sourceItemId");
        if (itemId.Length > 0) result["entryId"] = "item:" + itemId;
        if (obj.ContainsKey("containerContents")) result["containerContents"] = DropReference(obj["containerContents"]);
        return result;
    }

    private static JsonNode? RecipeTree(JsonNode? value)
    {
        if (value is JsonArray array) return new JsonArray(array.Select(RecipeTree).ToArray());
        if (value is not JsonObject obj) return value?.DeepClone();
        var result = Pick(obj, "name", "quantity", "available", "stage", "stageRange");
        if (obj.TryGetPropertyValue("id", out var id)) result["entryId"] = id is null ? null : "item:" + id.GetValue<string>();
        if (obj.TryGetPropertyValue("stageSource", out var stage)) result["stageSource"] = Reference(stage);
        foreach (var field in new[] { "ingredients", "required", "choices", "options" })
            if (obj.ContainsKey(field)) result[field] = RecipeTree(obj[field]);
        return result;
    }

    private static readonly HashSet<string> FormulaFields = new(StringComparer.Ordinal)
    {
        "title", "label", "expression", "formula", "unit", "text", "variablesText", "conditionsText", "inputsText", "resultText", "examples", "conditions", "variables", "inputs", "result", "value", "name", "description", "note", "notes", "arithmetic", "numerator", "denominator"
    };
    private static JsonNode? ReadableTree(JsonNode? value, HashSet<string> fields)
    {
        if (value is JsonArray array) return new JsonArray(array.Select(x => ReadableTree(x, fields)).ToArray());
        if (value is not JsonObject obj) return value?.DeepClone();
        var result = new JsonObject();
        foreach (var pair in obj)
            if (fields.Contains(pair.Key)) result[pair.Key] = pair.Key is "variables" or "inputs" or "conditions" ? pair.Value?.DeepClone() : ReadableTree(pair.Value, fields);
        if (obj["statuses"] is JsonArray statuses)
            result["source"] = new JsonArray(statuses.Select(x => (JsonNode)JsonValue.Create(Status(x?.GetValue<string>() ?? ""))!).ToArray());
        return result;
    }

    private static JsonNode? Reference(JsonNode? value)
    {
        if (value is JsonArray array) return new JsonArray(array.Select(Reference).ToArray());
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
            return text switch
            {
                "reference-stage" => JsonValue.Create("装备速查阶段分类，地图版本未标注。"),
                "reference-equipment-description" => JsonValue.Create("装备速查，地图版本未标注。"),
                _ => JsonValue.Create(text)
            };
        if (value is not JsonObject obj) return value?.DeepClone();
        var result = Pick(obj, "name", "mapDataVersion", "reference_map_version", "version");
        foreach (var field in new[] { "effectsSource" })
            if (obj.ContainsKey(field)) result[FieldLabel(field)] = Reference(obj[field]);
        // File/member names locate technical evidence; retain only descriptive labels in the reader view.
        foreach (var field in new[] { "dropSource", "stageSource" })
            if (obj[field] is JsonValue location && location.TryGetValue<string>(out var label) &&
                (Regex.IsMatch(label, @"[\u4e00-\u9fff]") || label == "reference-stage")) result[FieldLabel(field)] = Reference(location);
        if (obj.ContainsKey("status")) result["说明"] = Status(Text(obj, "status"));
        if (obj.Any(x => x.Key is "mapDataVersion" or "reference_map_version" or "version" && x.Value is null)) result["版本说明"] = "参考资料未标注地图版本，不作为本版已核事实。";
        return result;
    }

    private static JsonObject Dimensions(JsonObject dimensions)
    {
        var result = new JsonObject();
        foreach (var group in dimensions.GroupBy(x => x.Value is JsonObject obj ? Text(obj, "status") : x.Value?.GetValue<string>() ?? "", StringComparer.Ordinal))
            result[string.Join("、", group.Select(x => FieldLabel(x.Key)))] = Status(group.Key);
        return result;
    }

    private static string FieldLabel(string field) => field switch
    {
        "value" => "数值", "target" => "对象", "condition" => "条件", "stacking" => "叠加", "scaling" or "attribute_scaling" => "属性加成",
        "runtime" => "游戏表现", "native_result" => "原生效果", "source_description" => "原说明",
        "effective_cooldown" => "实际冷却", "geometry_and_filters" => "范围与筛选", "timing_and_cancellation" => "时序与取消",
        "damage_or_healing_resolution" => "伤害或治疗结算", "snapshot_and_rounding" => "属性取值与取整", "buff_stacking_and_lifecycle" => "状态叠加与持续",
        "unit_attribution_and_inheritance" => "单位归属与继承", "equipment_and_form_branches" => "装备与形态变化",
        "effectsSource" => "效果补充", "recipesSource" => "配方", "stageSource" => "阶段", "dropSource" => "掉落", "referenceSource" => "参考资料", "referenceEnumeration" => "参考目录",
        _ => field
    };

    private static string Status(string value) => value switch
    {
        "source_description" => "本版原说明，未据此认定已静态核对。",
        "reference_description" or "reference_unverified_for_current_map" => "参考资料，地图版本未标注，未核对为本版事实。",
        "static_verified" => "本版静态已核。",
        "static_verified_registration" => "本版掉落登记已静态核对；基础登记值不等于最终领取概率。",
        "mixed" => "多种来源，具体区别见正文；不表示整段已核对。",
        "per_section" => "按各段分别标注来源，不能合并为一种状态。",
        "pending" => "此项尚待核对。",
        "needs_recheck" => "已有记录需要重新核对。",
        "unconfirmed" => "尚无确认结论，原因与限制见正文。",
        "conflict" => "来源有差异，不能合并为同一结论。",
        "static_verified_partial" or "partial_static_verified" => "部分已静态核对，未核范围见正文。",
        "static_verified_with_pending_edges" => "主要逻辑已静态核对，边界仍待确认。",
        "static_verified_with_open_questions" or "static_verified_with_pending" => "已有静态结论，仍有未解决问题。",
        "static_verified_with_runtime_limits" => "已有静态结论，游戏表现仍有限制待核。",
        "source_description_with_static_timing" => "本版原说明，时序有静态补证。",
        "directory_reference_with_current_object_values" => "目录采用参考资料，基础属性来自本版地图；两者不能视为同一来源。",
        "current_map_identity_checked" => "本版对应关系已核对。",
        "current_map_registration_checked" => "本版登记关系已核对。",
        "reviewed_relation" => "关联关系已有核对记录；范围见正文。",
        "not_expanded" => "机制未展开研读。",
        "reference_sequence_current_terminal_key" => "参考资料提供按键顺序，本版仅核对末段按键。",
        "current_object_state_label" => "本版说明中的状态名称。",
        "current_object_spellbook" => "本版技能书配置。",
        "current_map_state_switch" => "本版状态切换。",
        "" => "资料未标注该项状态。",
        _ => value
    };

    private static string Relation(string value) => value switch
    {
        "ability_family" => "同一技能的关联形态。", "same_unit_swap" => "同一单位上的技能切换。", "spellbook_child" => "技能书内的子技能。",
        "same_caster_button" => "同一施法者的关联按钮。", "actor_registration_checked" => "已核对施法单位登记关系。", "paired_command" => "配对指令。",
        "" => "未标注关系类型。", _ => value
    };
    private static string NormalizeText(string value) => Regex.Replace(value, @"\s+", " ").Trim();
    private static string Text(JsonObject obj, string key) => obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static JsonObject Pick(JsonObject source, params string[] fields)
    {
        var result = new JsonObject();
        foreach (var field in fields) if (source.TryGetPropertyValue(field, out var value)) result[field] = value?.DeepClone();
        return result;
    }
}
