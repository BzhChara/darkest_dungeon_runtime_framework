using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace DDRuntimeLoader;

internal static class SaveEventBridge
{
    private const int ReportVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static SaveEventBridgeReport Execute(
        RuntimeConfig config,
        PatchPlan patchPlan,
        LauncherLog log,
        string saveStateReportPath,
        string projectRoot,
        string? pluginIdFilter)
    {
        var reportPath = ResolveInputPath(projectRoot, saveStateReportPath);
        var saveReport = JsonNode.Parse(File.ReadAllText(reportPath, Encoding.UTF8)) as JsonObject
            ?? throw new InvalidDataException($"Save state report root must be a JSON object: {reportPath}");
        var facts = saveReport["facts"] as JsonObject ?? saveReport;
        var bridgeContext = new JsonObject
        {
            ["saveStateReportPath"] = reportPath,
            ["generatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        var issues = new List<SaveEventBridgeIssue>();
        var pluginReports = new List<SaveEventBridgePluginReport>();
        var stateDocuments = new Dictionary<string, ModStateDocument?>(StringComparer.OrdinalIgnoreCase);

        var stateInitReport = ModStateStore.InitializeDefaults(config, patchPlan, log, pluginIdFilter);
        foreach (var issue in stateInitReport.Issues)
        {
            issues.Add(new SaveEventBridgeIssue(issue.Severity, issue.Code, issue.PluginId, issue.Message));
        }

        foreach (var sourceRule in SelectFactEventSources(patchPlan, pluginIdFilter))
        {
            pluginReports.Add(ProcessFactEventSource(
                config,
                patchPlan,
                log,
                projectRoot,
                facts,
                bridgeContext,
                sourceRule,
                stateDocuments,
                issues));
        }

        var questBoardPolicyMaterialization = BuildQuestBoardPolicyMaterializationReport(
            config,
            patchPlan,
            log,
            projectRoot,
            reportPath,
            issues);

        var report = new SaveEventBridgeReport(
            ReportVersion,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            reportPath,
            pluginReports.Count,
            pluginReports.Count(plugin => plugin.Status.Equals("event-executed", StringComparison.OrdinalIgnoreCase)),
            questBoardPolicyMaterialization,
            pluginReports,
            issues);

        LogAndWriteReport(config, log, report);
        return report;
    }

    private static SaveEventBridgePluginReport ProcessFactEventSource(
        RuntimeConfig config,
        PatchPlan patchPlan,
        LauncherLog log,
        string projectRoot,
        JsonObject facts,
        JsonObject bridgeContext,
        FactEventRuleSource sourceRule,
        Dictionary<string, ModStateDocument?> stateDocuments,
        List<SaveEventBridgeIssue> issues)
    {
        var document = GetStateDocument(config, patchPlan, sourceRule, stateDocuments, issues);
        RuntimePredicateResult predicate;
        try
        {
            predicate = EvaluatePredicate(sourceRule.Rule.When, facts, bridgeContext, document?.State);
        }
        catch (Exception ex)
        {
            issues.Add(new SaveEventBridgeIssue("error", "predicate-evaluation-failed", sourceRule.PluginId, ex.Message));
            return new SaveEventBridgePluginReport(
                sourceRule.PluginId,
                sourceRule.SourceName,
                sourceRule.SourcePath,
                sourceRule.LoadOrder,
                sourceRule.RuleIndex,
                sourceRule.Rule.Id,
                "predicate-failed",
                ex.Message,
                sourceRule.Rule.Emit,
                false,
                null);
        }

        if (!predicate.Matched)
        {
            return Skipped(sourceRule, "predicate-not-matched", predicate.Reason);
        }

        JsonObject payload;
        try
        {
            payload = BuildPayload(sourceRule, facts, bridgeContext, document?.State);
        }
        catch (Exception ex)
        {
            issues.Add(new SaveEventBridgeIssue("error", "payload-build-failed", sourceRule.PluginId, ex.Message));
            return new SaveEventBridgePluginReport(
                sourceRule.PluginId,
                sourceRule.SourceName,
                sourceRule.SourcePath,
                sourceRule.LoadOrder,
                sourceRule.RuleIndex,
                sourceRule.Rule.Id,
                "payload-failed",
                ex.Message,
                sourceRule.Rule.Emit,
                false,
                null);
        }

        var executionReport = RuntimeEventExecutor.Execute(
            config,
            patchPlan,
            log,
            sourceRule.Rule.Emit,
            payload.ToJsonString(JsonOptions),
            null,
            projectRoot,
            sourceRule.PluginId);
        if (executionReport.Succeeded)
        {
            stateDocuments.Remove(sourceRule.SourcePath);
        }

        return new SaveEventBridgePluginReport(
            sourceRule.PluginId,
            sourceRule.SourceName,
            sourceRule.SourcePath,
            sourceRule.LoadOrder,
            sourceRule.RuleIndex,
            sourceRule.Rule.Id,
            executionReport.Succeeded ? "event-executed" : "event-failed",
            executionReport.Succeeded ? "predicate matched and emitted event" : "predicate matched but event execution failed",
            sourceRule.Rule.Emit,
            executionReport.Succeeded,
            executionReport);
    }

    private static IEnumerable<FactEventRuleSource> SelectFactEventSources(PatchPlan patchPlan, string? pluginIdFilter)
    {
        return patchPlan.SourceFactEventRules
            .Where(source => string.IsNullOrWhiteSpace(pluginIdFilter) || source.PluginId.Equals(pluginIdFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(source => source.LoadOrder)
            .ThenBy(source => PluginPhaseRank(source.Rule.Phase))
            .ThenBy(source => source.Rule.Priority)
            .ThenBy(source => source.SourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.RuleIndex);
    }

    private static ModStateDocument? GetStateDocument(
        RuntimeConfig config,
        PatchPlan patchPlan,
        FactEventRuleSource sourceRule,
        Dictionary<string, ModStateDocument?> stateDocuments,
        List<SaveEventBridgeIssue> issues)
    {
        if (stateDocuments.TryGetValue(sourceRule.SourcePath, out var cached))
        {
            return cached;
        }

        var stateSource = ModStateStore.FindStateSchemaSource(patchPlan, sourceRule.PluginId, sourceRule.SourcePath);
        if (stateSource is null)
        {
            stateDocuments[sourceRule.SourcePath] = null;
            return null;
        }

        var stateIssues = new List<ModStateIssue>();
        if (!ModStateStore.TryOpenStateDocument(config, patchPlan, stateSource, stateIssues, out var document))
        {
            foreach (var issue in stateIssues)
            {
                issues.Add(new SaveEventBridgeIssue(issue.Severity, issue.Code, issue.PluginId, issue.Message));
            }

            stateDocuments[sourceRule.SourcePath] = null;
            return null;
        }

        stateDocuments[sourceRule.SourcePath] = document;
        return document;
    }

    private static JsonObject BuildPayload(FactEventRuleSource sourceRule, JsonObject facts, JsonObject bridgeContext, JsonObject? state)
    {
        var payload = new JsonObject
        {
            ["factEventRuleId"] = sourceRule.Rule.Id,
            ["saveStateReportPath"] = CloneNode(bridgeContext["saveStateReportPath"])
        };

        foreach (var (key, element) in sourceRule.Rule.Payload)
        {
            payload[key] = ResolvePayloadValue(sourceRule, key, element, facts, bridgeContext, state);
        }

        return payload;
    }

    private static JsonNode? ResolvePayloadValue(
        FactEventRuleSource sourceRule,
        string key,
        JsonElement element,
        JsonObject facts,
        JsonObject bridgeContext,
        JsonObject? state)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            JsonNode? value = null;
            var resolved = false;
            var optional = TryGetBoolProperty(element, "optional", out var optionalValue) && optionalValue;

            if (TryGetStringProperty(element, "fromFact", out var fromFact))
            {
                value = ResolvePayloadPath(facts, fromFact, "fact", sourceRule, key, optional);
                resolved = true;
            }
            else if (TryGetStringProperty(element, "fromState", out var fromState))
            {
                if (state is null)
                {
                    throw new InvalidOperationException($"Fact event rule {sourceRule.Rule.Id} payload '{key}' requires state path '{fromState}', but plugin state is unavailable.");
                }

                value = ResolvePayloadPath(state, fromState, "state", sourceRule, key, optional);
                resolved = true;
            }
            else if (TryGetStringProperty(element, "fromBridge", out var fromBridge) ||
                TryGetStringProperty(element, "fromEvent", out fromBridge))
            {
                value = ResolvePayloadPath(bridgeContext, fromBridge, "bridge", sourceRule, key, optional);
                resolved = true;
            }
            else if (element.TryGetProperty("value", out var literal))
            {
                value = JsonNode.Parse(literal.GetRawText());
                resolved = true;
            }

            if (resolved)
            {
                return ApplyPayloadProjection(sourceRule, key, element, value, facts, bridgeContext, state);
            }
        }

        return JsonNode.Parse(element.GetRawText());
    }

    private static JsonNode? ResolvePayloadPath(
        JsonObject root,
        string path,
        string addressSpace,
        FactEventRuleSource sourceRule,
        string key,
        bool optional)
    {
        if (TryGetPath(root, path, out var value))
        {
            return CloneNode(value);
        }

        if (optional)
        {
            return null;
        }

        return RequirePath(root, path, addressSpace, sourceRule, key);
    }

    private static JsonNode? ApplyPayloadProjection(
        FactEventRuleSource sourceRule,
        string key,
        JsonElement element,
        JsonNode? value,
        JsonObject facts,
        JsonObject bridgeContext,
        JsonObject? state)
    {
        var result = CloneNode(value);

        if (element.TryGetProperty("where", out var where))
        {
            result = ApplyWhereProjection(sourceRule, key, where, result, facts, bridgeContext, state);
        }

        if (element.TryGetProperty("whereIn", out var whereIn))
        {
            result = ApplyWhereInProjection(sourceRule, key, whereIn, result, facts, bridgeContext, state);
        }

        if (TryGetStringProperty(element, "selectMany", out var selectManyPath))
        {
            var missingMode = TryGetStringProperty(element, "selectManyMissing", out var configuredMissingMode)
                ? configuredMissingMode
                : "error";
            result = ApplySelectManyProjection(sourceRule, key, selectManyPath, missingMode, result);
        }

        if (TryGetStringProperty(element, "map", out var map))
        {
            result = ApplyMapProjection(sourceRule, key, map, result);
        }

        if (TryGetStringProperty(element, "coerce", out var coerce))
        {
            result = ApplyMapProjection(sourceRule, key, coerce, result);
        }

        if (TryGetBoolProperty(element, "distinct", out var distinct) && distinct)
        {
            result = ApplyDistinctProjection(sourceRule, key, result);
        }

        return result;
    }

    private static JsonNode? ApplyWhereProjection(
        FactEventRuleSource sourceRule,
        string key,
        JsonElement where,
        JsonNode? value,
        JsonObject facts,
        JsonObject bridgeContext,
        JsonObject? state)
    {
        if (where.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Fact event rule {sourceRule.Rule.Id} payload '{key}' where must be an object.");
        }

        if (value is not JsonArray sourceArray)
        {
            throw new InvalidOperationException($"Fact event rule {sourceRule.Rule.Id} payload '{key}' where requires an array source.");
        }

        var filtered = new JsonArray();
        foreach (var item in sourceArray)
        {
            var result = EvaluateProjectionPredicate(where, item, facts, bridgeContext, state);
            if (result.Matched)
            {
                filtered.Add(CloneNode(item));
            }
        }

        return filtered;
    }

    private static JsonNode? ApplyWhereInProjection(
        FactEventRuleSource sourceRule,
        string key,
        JsonElement whereIn,
        JsonNode? value,
        JsonObject facts,
        JsonObject bridgeContext,
        JsonObject? state)
    {
        if (whereIn.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Fact event rule {sourceRule.Rule.Id} payload '{key}' whereIn must be an object.");
        }

        if (value is not JsonArray sourceArray)
        {
            throw new InvalidOperationException($"Fact event rule {sourceRule.Rule.Id} payload '{key}' whereIn requires an array source.");
        }

        if (!TryGetStringProperty(whereIn, "path", out var path))
        {
            throw new InvalidOperationException($"Fact event rule {sourceRule.Rule.Id} payload '{key}' whereIn requires string property 'path'.");
        }

        var allowed = ReadComparableSet(ResolveWhereInValues(sourceRule, key, whereIn, facts, bridgeContext, state));
        var filtered = new JsonArray();
        foreach (var item in sourceArray)
        {
            if (!TryGetPath(item, path, out var candidate))
            {
                throw new InvalidOperationException($"Fact event rule {sourceRule.Rule.Id} payload '{key}' whereIn item is missing path '{path}'.");
            }

            if (ReadComparableValues(candidate).Any(allowed.Contains))
            {
                filtered.Add(CloneNode(item));
            }
        }

        return filtered;
    }

    private static JsonNode? ResolveWhereInValues(
        FactEventRuleSource sourceRule,
        string key,
        JsonElement whereIn,
        JsonObject facts,
        JsonObject bridgeContext,
        JsonObject? state)
    {
        if (TryGetStringProperty(whereIn, "valuesFromFact", out var valuesFromFact))
        {
            return RequirePath(facts, valuesFromFact, "fact", sourceRule, key);
        }

        if (TryGetStringProperty(whereIn, "valuesFromState", out var valuesFromState))
        {
            if (state is null)
            {
                throw new InvalidOperationException($"Fact event rule {sourceRule.Rule.Id} payload '{key}' whereIn requires state path '{valuesFromState}', but plugin state is unavailable.");
            }

            return RequirePath(state, valuesFromState, "state", sourceRule, key);
        }

        if (TryGetStringProperty(whereIn, "valuesFromBridge", out var valuesFromBridge) ||
            TryGetStringProperty(whereIn, "valuesFromEvent", out valuesFromBridge))
        {
            return RequirePath(bridgeContext, valuesFromBridge, "bridge", sourceRule, key);
        }

        if (whereIn.TryGetProperty("values", out var values))
        {
            return JsonNode.Parse(values.GetRawText());
        }

        throw new InvalidOperationException($"Fact event rule {sourceRule.Rule.Id} payload '{key}' whereIn requires valuesFromFact, valuesFromState, valuesFromBridge, valuesFromEvent, or values.");
    }

    private static JsonNode? ApplySelectManyProjection(
        FactEventRuleSource sourceRule,
        string key,
        string path,
        string missingMode,
        JsonNode? value)
    {
        if (value is not JsonArray sourceArray)
        {
            throw new InvalidOperationException($"Fact event rule {sourceRule.Rule.Id} payload '{key}' selectMany requires an array source.");
        }

        var skipMissing = missingMode.Trim().Equals("skip", StringComparison.OrdinalIgnoreCase);
        if (!skipMissing && !missingMode.Trim().Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Fact event rule {sourceRule.Rule.Id} payload '{key}' selectManyMissing must be 'error' or 'skip'.");
        }

        var selected = new JsonArray();
        foreach (var item in sourceArray)
        {
            if (!TryGetPath(item, path, out var child))
            {
                if (skipMissing)
                {
                    continue;
                }

                throw new InvalidOperationException($"Fact event rule {sourceRule.Rule.Id} payload '{key}' selectMany item is missing path '{path}'.");
            }

            if (child is JsonArray childArray)
            {
                foreach (var childItem in childArray)
                {
                    selected.Add(CloneNode(childItem));
                }
            }
            else
            {
                selected.Add(CloneNode(child));
            }
        }

        return selected;
    }

    private static JsonNode? ApplyMapProjection(FactEventRuleSource sourceRule, string key, string map, JsonNode? value)
    {
        return map.Trim().ToLowerInvariant() switch
        {
            "string" => JsonValue.Create(ReadScalarAsString(value)),
            "stringarray" => new JsonArray(ReadArrayValues(value).Select(item => JsonValue.Create(ReadScalarAsString(item))).ToArray()),
            _ => throw new InvalidOperationException($"Fact event rule {sourceRule.Rule.Id} payload '{key}' uses unsupported map/coerce '{map}'.")
        };
    }

    private static JsonNode? ApplyDistinctProjection(FactEventRuleSource sourceRule, string key, JsonNode? value)
    {
        if (value is not JsonArray array)
        {
            throw new InvalidOperationException($"Fact event rule {sourceRule.Rule.Id} payload '{key}' distinct requires an array source.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new JsonArray();
        foreach (var item in array)
        {
            var identity = item is JsonValue ? ReadScalarAsString(item) : item?.ToJsonString(JsonOptions) ?? "null";
            if (seen.Add(identity))
            {
                result.Add(CloneNode(item));
            }
        }

        return result;
    }

    private static IEnumerable<JsonNode?> ReadArrayValues(JsonNode? value)
    {
        if (value is JsonArray array)
        {
            foreach (var item in array)
            {
                yield return item;
            }

            yield break;
        }

        if (value is not null)
        {
            yield return value;
        }
    }

    private static HashSet<string> ReadComparableSet(JsonNode? value)
    {
        return ReadComparableValues(value).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ReadComparableValues(JsonNode? value)
    {
        foreach (var item in ReadArrayValues(value))
        {
            yield return ReadScalarAsString(item);
        }
    }

    private static string ReadScalarAsString(JsonNode? value)
    {
        if (value is not JsonValue jsonValue)
        {
            throw new InvalidOperationException("payload projection expected a scalar value.");
        }

        if (jsonValue.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (jsonValue.TryGetValue<int>(out var intValue))
        {
            return intValue.ToString(CultureInfo.InvariantCulture);
        }

        if (jsonValue.TryGetValue<long>(out var longValue))
        {
            return longValue.ToString(CultureInfo.InvariantCulture);
        }

        if (jsonValue.TryGetValue<double>(out var doubleValue))
        {
            return doubleValue.ToString(CultureInfo.InvariantCulture);
        }

        if (jsonValue.TryGetValue<bool>(out var boolValue))
        {
            return boolValue ? "true" : "false";
        }

        throw new InvalidOperationException("payload projection expected a string, number, or boolean scalar.");
    }

    private static RuntimePredicateResult EvaluatePredicate(
        RuntimeRulePredicate? predicate,
        JsonObject facts,
        JsonObject bridgeContext,
        JsonObject? state)
    {
        if (predicate is null)
        {
            return new RuntimePredicateResult(true, "no predicate");
        }

        foreach (var child in predicate.All ?? [])
        {
            var result = EvaluatePredicate(child, facts, bridgeContext, state);
            if (!result.Matched)
            {
                return new RuntimePredicateResult(false, "all failed: " + result.Reason);
            }
        }

        var any = predicate.Any ?? [];
        if (any.Length > 0)
        {
            var anyResults = any
                .Select(child => EvaluatePredicate(child, facts, bridgeContext, state))
                .ToArray();
            if (!anyResults.Any(result => result.Matched))
            {
                return new RuntimePredicateResult(false, "any failed: " + string.Join("; ", anyResults.Select(result => result.Reason)));
            }
        }

        foreach (var child in predicate.None ?? [])
        {
            var result = EvaluatePredicate(child, facts, bridgeContext, state);
            if (result.Matched)
            {
                return new RuntimePredicateResult(false, "none matched: " + result.Reason);
            }
        }

        if (!string.IsNullOrWhiteSpace(predicate.State))
        {
            if (state is null)
            {
                return new RuntimePredicateResult(false, "state unavailable: " + predicate.State);
            }

            return EvaluateLeaf(predicate, state, predicate.State, facts, bridgeContext, state, "state");
        }

        if (!string.IsNullOrWhiteSpace(predicate.Fact))
        {
            return EvaluateLeaf(predicate, facts, predicate.Fact, facts, bridgeContext, state, "fact");
        }

        if (!string.IsNullOrWhiteSpace(predicate.Event))
        {
            return EvaluateLeaf(predicate, bridgeContext, predicate.Event, facts, bridgeContext, state, "bridge");
        }

        return new RuntimePredicateResult(true, "empty predicate");
    }

    private static RuntimePredicateResult EvaluateProjectionPredicate(
        JsonElement predicate,
        JsonNode? item,
        JsonObject facts,
        JsonObject bridgeContext,
        JsonObject? state)
    {
        if (predicate.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("payload where predicate must be an object.");
        }

        if (predicate.TryGetProperty("all", out var all))
        {
            foreach (var child in EnumerateProjectionPredicateArray(all, "all"))
            {
                var result = EvaluateProjectionPredicate(child, item, facts, bridgeContext, state);
                if (!result.Matched)
                {
                    return new RuntimePredicateResult(false, "all failed: " + result.Reason);
                }
            }
        }

        if (predicate.TryGetProperty("any", out var any))
        {
            var results = EnumerateProjectionPredicateArray(any, "any")
                .Select(child => EvaluateProjectionPredicate(child, item, facts, bridgeContext, state))
                .ToArray();
            if (results.Length > 0 && !results.Any(result => result.Matched))
            {
                return new RuntimePredicateResult(false, "any failed: " + string.Join("; ", results.Select(result => result.Reason)));
            }
        }

        if (predicate.TryGetProperty("none", out var none))
        {
            foreach (var child in EnumerateProjectionPredicateArray(none, "none"))
            {
                var result = EvaluateProjectionPredicate(child, item, facts, bridgeContext, state);
                if (result.Matched)
                {
                    return new RuntimePredicateResult(false, "none matched: " + result.Reason);
                }
            }
        }

        if (TryGetStringProperty(predicate, "path", out var path))
        {
            return EvaluateProjectionLeaf(predicate, item, path, facts, bridgeContext, state);
        }

        return new RuntimePredicateResult(true, "empty where predicate");
    }

    private static IEnumerable<JsonElement> EnumerateProjectionPredicateArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"payload where predicate property '{propertyName}' must be an array.");
        }

        foreach (var item in element.EnumerateArray())
        {
            yield return item;
        }
    }

    private static RuntimePredicateResult EvaluateProjectionLeaf(
        JsonElement predicate,
        JsonNode? item,
        string path,
        JsonObject facts,
        JsonObject bridgeContext,
        JsonObject? state)
    {
        var exists = TryGetPath(item, path, out var actual);
        var op = TryGetStringProperty(predicate, "op", out var rawOp)
            ? rawOp.Trim().ToLowerInvariant()
            : "exists";

        if (op == "exists")
        {
            return new RuntimePredicateResult(exists, $"item.{path} exists={exists}");
        }

        if (op == "notexists")
        {
            return new RuntimePredicateResult(!exists, $"item.{path} exists={exists}");
        }

        if (!exists)
        {
            return new RuntimePredicateResult(false, $"item.{path} missing");
        }

        var expected = ResolveProjectionExpectedValue(predicate, facts, bridgeContext, state);
        var matched = op switch
        {
            "equals" => JsonNode.DeepEquals(actual, expected),
            "notequals" => !JsonNode.DeepEquals(actual, expected),
            "greater" => CompareNumbers(actual, expected) > 0,
            "greaterorequal" => CompareNumbers(actual, expected) >= 0,
            "less" => CompareNumbers(actual, expected) < 0,
            "lessorequal" => CompareNumbers(actual, expected) <= 0,
            "contains" => ContainsValue(actual, expected),
            "notcontains" => !ContainsValue(actual, expected),
            "matches" => MatchesPattern(actual, expected),
            _ => false
        };

        return new RuntimePredicateResult(matched, $"item.{path} {op} matched={matched}");
    }

    private static JsonNode? ResolveProjectionExpectedValue(
        JsonElement predicate,
        JsonObject facts,
        JsonObject bridgeContext,
        JsonObject? state)
    {
        if (TryGetStringProperty(predicate, "valueFromFact", out var valueFromFact))
        {
            return RequirePath(facts, valueFromFact, "fact", valueFromFact);
        }

        if (TryGetStringProperty(predicate, "valueFromState", out var valueFromState))
        {
            if (state is null)
            {
                throw new InvalidOperationException($"Payload where predicate requires state path '{valueFromState}', but plugin state is unavailable.");
            }

            return RequirePath(state, valueFromState, "state", valueFromState);
        }

        if (TryGetStringProperty(predicate, "valueFromBridge", out var valueFromBridge) ||
            TryGetStringProperty(predicate, "valueFromEvent", out valueFromBridge))
        {
            return RequirePath(bridgeContext, valueFromBridge, "bridge", valueFromBridge);
        }

        return predicate.TryGetProperty("value", out var value)
            ? JsonNode.Parse(value.GetRawText())
            : null;
    }

    private static RuntimePredicateResult EvaluateLeaf(
        RuntimeRulePredicate predicate,
        JsonNode? actualRoot,
        string actualPath,
        JsonObject facts,
        JsonObject bridgeContext,
        JsonObject? state,
        string addressSpace)
    {
        var exists = TryGetPath(actualRoot, actualPath, out var actual);
        var normalizedOp = string.IsNullOrWhiteSpace(predicate.Op) ? "exists" : predicate.Op.Trim().ToLowerInvariant();

        if (normalizedOp == "exists")
        {
            return new RuntimePredicateResult(exists, $"{addressSpace}.{actualPath} exists={exists}");
        }

        if (normalizedOp == "notexists")
        {
            return new RuntimePredicateResult(!exists, $"{addressSpace}.{actualPath} exists={exists}");
        }

        if (!exists)
        {
            return new RuntimePredicateResult(false, $"{addressSpace}.{actualPath} missing");
        }

        var expected = ResolveExpectedValue(predicate, facts, bridgeContext, state);
        var matched = normalizedOp switch
        {
            "equals" => JsonNode.DeepEquals(actual, expected),
            "notequals" => !JsonNode.DeepEquals(actual, expected),
            "greater" => CompareNumbers(actual, expected) > 0,
            "greaterorequal" => CompareNumbers(actual, expected) >= 0,
            "less" => CompareNumbers(actual, expected) < 0,
            "lessorequal" => CompareNumbers(actual, expected) <= 0,
            "contains" => ContainsValue(actual, expected),
            "notcontains" => !ContainsValue(actual, expected),
            "matches" => MatchesPattern(actual, expected),
            _ => false
        };

        return new RuntimePredicateResult(matched, $"{addressSpace}.{actualPath} {normalizedOp} matched={matched}");
    }

    private static JsonNode? ResolveExpectedValue(RuntimeRulePredicate predicate, JsonObject facts, JsonObject bridgeContext, JsonObject? state)
    {
        if (!string.IsNullOrWhiteSpace(predicate.ValueFromFact))
        {
            return RequirePath(facts, predicate.ValueFromFact, "fact", predicate.ValueFromFact);
        }

        if (!string.IsNullOrWhiteSpace(predicate.ValueFromEvent))
        {
            return RequirePath(bridgeContext, predicate.ValueFromEvent, "bridge", predicate.ValueFromEvent);
        }

        if (!string.IsNullOrWhiteSpace(predicate.ValueFromState))
        {
            if (state is null)
            {
                throw new InvalidOperationException($"Predicate requires state path '{predicate.ValueFromState}', but plugin state is unavailable.");
            }

            return RequirePath(state, predicate.ValueFromState, "state", predicate.ValueFromState);
        }

        return predicate.Value.HasValue ? JsonNode.Parse(predicate.Value.Value.GetRawText()) : null;
    }

    private static SaveEventBridgePluginReport Skipped(FactEventRuleSource sourceRule, string status, string reason)
    {
        return new SaveEventBridgePluginReport(
            sourceRule.PluginId,
            sourceRule.SourceName,
            sourceRule.SourcePath,
            sourceRule.LoadOrder,
            sourceRule.RuleIndex,
            sourceRule.Rule.Id,
            status,
            reason,
            sourceRule.Rule.Emit,
            null,
            null);
    }

    private static string ResolveInputPath(string projectRoot, string path)
    {
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path));
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"Property '{propertyName}' must be a string.");
        }

        value = property.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Property '{propertyName}' must be a non-empty string.");
        }

        return true;
    }

    private static bool TryGetBoolProperty(JsonElement element, string propertyName, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind != JsonValueKind.True && property.ValueKind != JsonValueKind.False)
        {
            throw new InvalidOperationException($"Property '{propertyName}' must be a boolean.");
        }

        value = property.GetBoolean();
        return true;
    }

    private static JsonNode? RequirePath(JsonNode? root, string path, string addressSpace, FactEventRuleSource sourceRule, string payloadKey)
    {
        if (TryGetPath(root, path, out var value))
        {
            return CloneNode(value);
        }

        throw new InvalidOperationException(
            $"Fact event rule {sourceRule.Rule.Id} payload '{payloadKey}' references missing {addressSpace} path '{path}'.");
    }

    private static JsonNode? RequirePath(JsonNode? root, string path, string addressSpace, string reference)
    {
        if (TryGetPath(root, path, out var value))
        {
            return CloneNode(value);
        }

        throw new InvalidOperationException($"Fact event predicate reference '{reference}' points to missing {addressSpace} path '{path}'.");
    }

    private static bool TryGetPath(JsonNode? root, string path, out JsonNode? value)
    {
        value = root;
        if (string.IsNullOrWhiteSpace(path))
        {
            return value is not null;
        }

        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (value is JsonObject obj)
            {
                if (!obj.TryGetPropertyValue(part, out value))
                {
                    value = null;
                    return false;
                }
            }
            else if (value is JsonArray array && int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                if (index < 0 || index >= array.Count)
                {
                    value = null;
                    return false;
                }

                value = array[index];
            }
            else
            {
                value = null;
                return false;
            }
        }

        return value is not null;
    }

    private static JsonNode? CloneNode(JsonNode? node)
    {
        return node is null ? null : JsonNode.Parse(node.ToJsonString());
    }

    private static int CompareNumbers(JsonNode? left, JsonNode? right)
    {
        var leftNumber = ReadDouble(left);
        var rightNumber = ReadDouble(right);
        return leftNumber.CompareTo(rightNumber);
    }

    private static double ReadDouble(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<double>(out var number))
        {
            return number;
        }

        throw new InvalidOperationException("predicate numeric comparison requires numeric values.");
    }

    private static bool ContainsValue(JsonNode? actual, JsonNode? expected)
    {
        if (actual is JsonArray array)
        {
            return array.Any(item => JsonNode.DeepEquals(item, expected));
        }

        if (actual is JsonValue actualValue && expected is JsonValue expectedValue &&
            actualValue.TryGetValue<string>(out var actualText) &&
            expectedValue.TryGetValue<string>(out var expectedText))
        {
            return actualText.Contains(expectedText, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool MatchesPattern(JsonNode? actual, JsonNode? expected)
    {
        if (actual is not JsonValue actualValue ||
            expected is not JsonValue expectedValue ||
            !actualValue.TryGetValue<string>(out var actualText) ||
            !expectedValue.TryGetValue<string>(out var pattern))
        {
            return false;
        }

        return Regex.IsMatch(actualText, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static SaveEventBridgeQuestBoardPolicyMaterializationReport BuildQuestBoardPolicyMaterializationReport(
        RuntimeConfig config,
        PatchPlan patchPlan,
        LauncherLog log,
        string projectRoot,
        string saveStateReportPath,
        List<SaveEventBridgeIssue> issues)
    {
        if (!config.QuestBoardPolicyAutoMaterializeEnabled)
        {
            return new SaveEventBridgeQuestBoardPolicyMaterializationReport(
                false,
                "disabled",
                "questBoardPolicyAutoMaterializeEnabled is false",
                string.Empty,
                string.Empty,
                0,
                0,
                0);
        }

        if (patchPlan.QuestBoardPolicyReports.Count == 0)
        {
            return new SaveEventBridgeQuestBoardPolicyMaterializationReport(
                true,
                "noPolicies",
                "no enabled questBoardPolicies were present in the patch plan",
                string.Empty,
                string.Empty,
                0,
                0,
                0);
        }

        try
        {
            var materializeReport = QuestBoardPolicyMaterializer.Write(
                config,
                patchPlan,
                log,
                projectRoot,
                saveStateReportPath,
                config.QuestBoardPolicyAutoMaterializeSlots,
                config.QuestBoardPolicyAutoMaterializeSeed);

            if (!materializeReport.Succeeded)
            {
                issues.Add(new SaveEventBridgeIssue(
                    "error",
                    "quest-board-policy-auto-materialize-failed",
                    string.Empty,
                    $"quest board policy materialization failed; inspect {materializeReport.ReportPath}"));
            }
            else if (materializeReport.WarningCount > 0)
            {
                issues.Add(new SaveEventBridgeIssue(
                    "warning",
                    "quest-board-policy-auto-materialize-warnings",
                    string.Empty,
                    $"quest board policy materialization reported {materializeReport.WarningCount} warning(s); inspect {materializeReport.ReportPath}"));
            }

            return new SaveEventBridgeQuestBoardPolicyMaterializationReport(
                true,
                materializeReport.Status,
                materializeReport.Succeeded
                    ? "quest board policies were materialized from the current save facts"
                    : "quest board policy materialization failed",
                materializeReport.ReportPath,
                materializeReport.ArtifactPath,
                materializeReport.SelectedQuestCount,
                materializeReport.ErrorCount,
                materializeReport.WarningCount);
        }
        catch (Exception ex)
        {
            issues.Add(new SaveEventBridgeIssue(
                "error",
                "quest-board-policy-auto-materialize-exception",
                string.Empty,
                ex.Message));
            log.Error($"save-event-bridge quest-board-policy-auto-materialize exception message={QuoteLogValue(ex.Message)}");
            return new SaveEventBridgeQuestBoardPolicyMaterializationReport(
                true,
                "failed",
                ex.Message,
                string.Empty,
                string.Empty,
                0,
                1,
                0);
        }
    }

    private static int PluginPhaseRank(string? phase)
    {
        return (phase ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "base" => 0,
            "early" => 10,
            "normal" => 20,
            "compat" => 30,
            "late" => 40,
            _ => 20
        };
    }

    private static void LogAndWriteReport(RuntimeConfig config, LauncherLog log, SaveEventBridgeReport report)
    {
        log.Info(
            $"save-event-bridge factEventRules={report.RuleCount} inferredEvents={report.InferredEventCount} " +
            $"questBoardPolicyMaterialization={report.QuestBoardPolicyMaterialization.Status} " +
            $"issues={report.Issues.Count}");

        if (report.QuestBoardPolicyMaterialization.Enabled)
        {
            log.Info(
                $"save-event-bridge quest-board-policy-materialization status={report.QuestBoardPolicyMaterialization.Status} " +
                $"selectedQuests={report.QuestBoardPolicyMaterialization.SelectedQuestCount} " +
                $"report={QuoteLogValue(report.QuestBoardPolicyMaterialization.ReportPath)} " +
                $"artifact={QuoteLogValue(report.QuestBoardPolicyMaterialization.ArtifactPath)}");
        }

        foreach (var plugin in report.Plugins)
        {
            log.Info(
                $"save-event-bridge-rule status={plugin.Status} plugin={plugin.PluginId} " +
                $"rule={plugin.RuleIndex} id={QuoteLogValue(plugin.RuleId)} event={plugin.EventId ?? "null"} " +
                $"reason={QuoteLogValue(plugin.Reason)}");
        }

        foreach (var issue in report.Issues)
        {
            var line =
                $"save-event-bridge-issue severity={issue.Severity} code={issue.Code} " +
                $"plugin={issue.PluginId} message={QuoteLogValue(issue.Message)}";
            if (issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase))
            {
                log.Error(line);
            }
            else
            {
                log.Warn(line);
            }
        }

        var outputPath = Path.Combine(config.LogDirectory, "save_event_bridge_report.json");
        File.WriteAllText(outputPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        log.Info($"save-event-bridge-report path={outputPath}");
    }

    private static string QuoteLogValue(string value)
    {
        return string.IsNullOrEmpty(value) ? "\"\"" : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}

internal sealed record SaveEventBridgeReport(
    int Version,
    string GeneratedAtUtc,
    string SaveStateReportPath,
    int RuleCount,
    int InferredEventCount,
    SaveEventBridgeQuestBoardPolicyMaterializationReport QuestBoardPolicyMaterialization,
    IReadOnlyList<SaveEventBridgePluginReport> Plugins,
    IReadOnlyList<SaveEventBridgeIssue> Issues)
{
    public bool Succeeded =>
        Issues.All(issue => !issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)) &&
        Plugins.All(plugin => plugin.ExecutionSucceeded != false);
}

internal sealed record SaveEventBridgeQuestBoardPolicyMaterializationReport(
    bool Enabled,
    string Status,
    string Reason,
    string ReportPath,
    string ArtifactPath,
    int SelectedQuestCount,
    int ErrorCount,
    int WarningCount);

internal sealed record SaveEventBridgePluginReport(
    string PluginId,
    string SourceName,
    string SourcePath,
    int LoadOrder,
    int RuleIndex,
    string RuleId,
    string Status,
    string Reason,
    string? EventId,
    bool? ExecutionSucceeded,
    RuntimeEventExecutionReport? ExecutionReport);

internal sealed record SaveEventBridgeIssue(
    string Severity,
    string Code,
    string PluginId,
    string Message);
