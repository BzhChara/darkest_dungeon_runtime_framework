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

        var report = new SaveEventBridgeReport(
            ReportVersion,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            reportPath,
            pluginReports.Count,
            pluginReports.Count(plugin => plugin.Status.Equals("event-executed", StringComparison.OrdinalIgnoreCase)),
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
            if (TryGetStringProperty(element, "fromFact", out var fromFact))
            {
                return RequirePath(facts, fromFact, "fact", sourceRule, key);
            }

            if (TryGetStringProperty(element, "fromState", out var fromState))
            {
                if (state is null)
                {
                    throw new InvalidOperationException($"Fact event rule {sourceRule.Rule.Id} payload '{key}' requires state path '{fromState}', but plugin state is unavailable.");
                }

                return RequirePath(state, fromState, "state", sourceRule, key);
            }

            if (TryGetStringProperty(element, "fromBridge", out var fromBridge) ||
                TryGetStringProperty(element, "fromEvent", out fromBridge))
            {
                return RequirePath(bridgeContext, fromBridge, "bridge", sourceRule, key);
            }

            if (element.TryGetProperty("value", out var literal))
            {
                return JsonNode.Parse(literal.GetRawText());
            }
        }

        return JsonNode.Parse(element.GetRawText());
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
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
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
            $"issues={report.Issues.Count}");

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
    IReadOnlyList<SaveEventBridgePluginReport> Plugins,
    IReadOnlyList<SaveEventBridgeIssue> Issues)
{
    public bool Succeeded =>
        Issues.All(issue => !issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)) &&
        Plugins.All(plugin => plugin.ExecutionSucceeded != false);
}

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
