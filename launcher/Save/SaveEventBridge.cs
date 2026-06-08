using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

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

        var issues = new List<SaveEventBridgeIssue>();
        var pluginReports = new List<SaveEventBridgePluginReport>();
        var observedRaid = ReadObservedRaidResult(saveReport);

        var stateInitReport = ModStateStore.InitializeDefaults(config, patchPlan, log, pluginIdFilter);
        foreach (var issue in stateInitReport.Issues)
        {
            issues.Add(new SaveEventBridgeIssue(issue.Severity, issue.Code, issue.PluginId, issue.Message));
        }

        foreach (var sourceRule in SelectChallengeSources(patchPlan, pluginIdFilter))
        {
            pluginReports.Add(ProcessChallengeSource(
                config,
                patchPlan,
                log,
                projectRoot,
                reportPath,
                observedRaid,
                sourceRule,
                issues));
        }

        var report = new SaveEventBridgeReport(
            ReportVersion,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            reportPath,
            observedRaid.QuestHash,
            observedRaid.QuestNames,
            observedRaid.Success,
            pluginReports.Count,
            pluginReports.Count(plugin => plugin.Status.Equals("event-executed", StringComparison.OrdinalIgnoreCase)),
            pluginReports,
            issues);

        LogAndWriteReport(config, log, report);
        return report;
    }

    private static SaveEventBridgePluginReport ProcessChallengeSource(
        RuntimeConfig config,
        PatchPlan patchPlan,
        LauncherLog log,
        string projectRoot,
        string saveStateReportPath,
        ObservedRaidResult observedRaid,
        RuntimeEventRuleSource sourceRule,
        List<SaveEventBridgeIssue> issues)
    {
        var initAction = sourceRule.Rule.Actions
            .First(action => action.Type.Equals("challenge.initializeRunState", StringComparison.OrdinalIgnoreCase));
        var stateKey = ReadStringArg(initAction, "stateKey") ?? "challengeRun";
        var definition = ReadStringArg(initAction, "definition");
        if (string.IsNullOrWhiteSpace(definition))
        {
            return Skipped(sourceRule, "missing-definition", "challenge.initializeRunState has no definition arg");
        }

        var stateSource = ModStateStore.FindStateSchemaSource(patchPlan, sourceRule.PluginId, sourceRule.SourcePath);
        if (stateSource is null)
        {
            return Skipped(sourceRule, "state-schema-not-found", "challenge plugin has no active stateSchema");
        }

        var stateIssues = new List<ModStateIssue>();
        if (!ModStateStore.TryOpenStateDocument(config, patchPlan, stateSource, stateIssues, out var document) || document is null)
        {
            foreach (var issue in stateIssues)
            {
                issues.Add(new SaveEventBridgeIssue(issue.Severity, issue.Code, issue.PluginId, issue.Message));
            }

            return Skipped(sourceRule, "state-unavailable", "challenge sidecar state is unavailable");
        }

        if (document.State[stateKey] is not JsonObject runState)
        {
            return Skipped(sourceRule, "state-key-invalid", $"state.{stateKey} is not an object");
        }

        if (!TryReadInt(runState["currentStageIndex"], out var currentStageIndex))
        {
            return Skipped(sourceRule, "current-stage-index-invalid", $"state.{stateKey}.currentStageIndex is missing or not an integer");
        }

        var definitionPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceRule.SourcePath) ?? projectRoot, definition));
        var challenge = JsonNode.Parse(File.ReadAllText(definitionPath, Encoding.UTF8)) as JsonObject
            ?? throw new InvalidDataException($"Challenge definition root must be a JSON object: {definitionPath}");

        if (challenge["stages"] is not JsonArray stages)
        {
            return Skipped(sourceRule, "stages-invalid", $"challenge definition has no stages array: {definitionPath}");
        }

        if (currentStageIndex < 0 || currentStageIndex >= stages.Count)
        {
            return Skipped(sourceRule, "challenge-complete", $"currentStageIndex={currentStageIndex} is outside stage count={stages.Count}");
        }

        if (stages[currentStageIndex] is not JsonObject stage)
        {
            return Skipped(sourceRule, "stage-invalid", $"challenge stage at index {currentStageIndex} is not an object");
        }

        var stageId = ReadString(stage, "id");
        var sourceQuestId = ReadString(stage, "sourceQuestId");
        if (string.IsNullOrWhiteSpace(stageId) || string.IsNullOrWhiteSpace(sourceQuestId))
        {
            return Skipped(sourceRule, "stage-missing-quest", "current challenge stage must declare id and sourceQuestId");
        }

        if (observedRaid.Success is null)
        {
            return Skipped(sourceRule, "no-last-raid-result", "save facts do not expose lastRaidSuccess");
        }

        var expectedQuestHash = DsonHash.HashNameSigned(sourceQuestId);
        var hashMatches = observedRaid.QuestHash == expectedQuestHash;
        var nameMatches = observedRaid.QuestNames.Contains(sourceQuestId, StringComparer.OrdinalIgnoreCase);
        if (!hashMatches && !nameMatches)
        {
            return new SaveEventBridgePluginReport(
                sourceRule.PluginId,
                sourceRule.SourceName,
                sourceRule.SourcePath,
                sourceRule.LoadOrder,
                "no-match",
                $"last raid quest did not match current stage sourceQuestId={sourceQuestId}",
                stateKey,
                currentStageIndex,
                stageId,
                sourceQuestId,
                expectedQuestHash,
                null,
                null,
                null);
        }

        var eventId = observedRaid.Success == true ? "challenge.stage_completed" : "challenge.stage_failed";
        var observedQuestNames = new JsonArray();
        foreach (var name in observedRaid.QuestNames)
        {
            observedQuestNames.Add(name);
        }

        var payload = new JsonObject
        {
            ["stageId"] = stageId,
            ["sourceQuestId"] = sourceQuestId,
            ["observedQuestHash"] = observedRaid.QuestHash,
            ["observedQuestNames"] = observedQuestNames,
            ["observedSuccess"] = observedRaid.Success,
            ["saveStateReportPath"] = saveStateReportPath
        };

        var executionReport = RuntimeEventExecutor.Execute(
            config,
            patchPlan,
            log,
            eventId,
            payload.ToJsonString(JsonOptions),
            null,
            projectRoot,
            sourceRule.PluginId);

        return new SaveEventBridgePluginReport(
            sourceRule.PluginId,
            sourceRule.SourceName,
            sourceRule.SourcePath,
            sourceRule.LoadOrder,
            executionReport.Succeeded ? "event-executed" : "event-failed",
            executionReport.Succeeded ? "matched current stage and executed event" : "matched current stage but event execution failed",
            stateKey,
            currentStageIndex,
            stageId,
            sourceQuestId,
            expectedQuestHash,
            eventId,
            executionReport.Succeeded,
            executionReport);
    }

    private static IEnumerable<RuntimeEventRuleSource> SelectChallengeSources(PatchPlan patchPlan, string? pluginIdFilter)
    {
        return patchPlan.SourceRuntimeEventRules
            .Where(source => string.IsNullOrWhiteSpace(pluginIdFilter) || source.PluginId.Equals(pluginIdFilter, StringComparison.OrdinalIgnoreCase))
            .Where(source => source.Rule.Actions.Any(action => action.Type.Equals("challenge.initializeRunState", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(source => source.LoadOrder)
            .ThenBy(source => source.SourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.RuleIndex);
    }

    private static ObservedRaidResult ReadObservedRaidResult(JsonObject saveReport)
    {
        var progression = saveReport["facts"]?["progression"];
        var questHash = ReadInt(progression?["lastRaidQuestId"]);
        var success = ReadBool(progression?["lastRaidSuccess"]);
        var names = ReadStringArray(progression?["lastRaidQuest"]?["names"]);
        return new ObservedRaidResult(questHash, names, success);
    }

    private static SaveEventBridgePluginReport Skipped(RuntimeEventRuleSource sourceRule, string status, string reason)
    {
        return new SaveEventBridgePluginReport(
            sourceRule.PluginId,
            sourceRule.SourceName,
            sourceRule.SourcePath,
            sourceRule.LoadOrder,
            status,
            reason,
            string.Empty,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    private static string ResolveInputPath(string projectRoot, string path)
    {
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path));
    }

    private static string? ReadStringArg(RuntimeRuleAction action, string argName)
    {
        return action.Args.TryGetValue(argName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? ReadString(JsonObject obj, string propertyName)
    {
        return obj[propertyName] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;
    }

    private static int? ReadInt(JsonNode? node)
    {
        return TryReadInt(node, out var value) ? value : null;
    }

    private static bool TryReadInt(JsonNode? node, out int value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<int>(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static bool? ReadBool(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result
            : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        return array
            .OfType<JsonValue>()
            .Select(value => value.TryGetValue<string>(out var text) ? text : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void LogAndWriteReport(RuntimeConfig config, LauncherLog log, SaveEventBridgeReport report)
    {
        log.Info(
            $"save-event-bridge plugins={report.PluginCount} inferredEvents={report.InferredEventCount} " +
            $"lastRaidQuestHash={report.ObservedQuestHash?.ToString(CultureInfo.InvariantCulture) ?? "null"} " +
            $"lastRaidSuccess={report.ObservedSuccess?.ToString() ?? "null"} issues={report.Issues.Count}");

        foreach (var plugin in report.Plugins)
        {
            log.Info(
                $"save-event-bridge-plugin status={plugin.Status} plugin={plugin.PluginId} " +
                $"stage={plugin.StageId ?? "null"} quest={plugin.SourceQuestId ?? "null"} event={plugin.EventId ?? "null"} " +
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

    private sealed record ObservedRaidResult(
        int? QuestHash,
        IReadOnlyList<string> QuestNames,
        bool? Success);
}

internal sealed record SaveEventBridgeReport(
    int Version,
    string GeneratedAtUtc,
    string SaveStateReportPath,
    int? ObservedQuestHash,
    IReadOnlyList<string> ObservedQuestNames,
    bool? ObservedSuccess,
    int PluginCount,
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
    string Status,
    string Reason,
    string StateKey,
    int? CurrentStageIndex,
    string? StageId,
    string? SourceQuestId,
    int? ExpectedQuestHash,
    string? EventId,
    bool? ExecutionSucceeded,
    RuntimeEventExecutionReport? ExecutionReport);

internal sealed record SaveEventBridgeIssue(
    string Severity,
    string Code,
    string PluginId,
    string Message);
