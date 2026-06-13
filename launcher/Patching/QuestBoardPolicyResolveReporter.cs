using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static class QuestBoardPolicyResolveReporter
{
    private const int ReportVersion = 1;
    private const string ReportFileName = "quest_board_policy_resolve_report.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static QuestBoardPolicyResolveReport Write(
        RuntimeConfig config,
        PatchPlan patchPlan,
        LauncherLog log,
        string projectRoot,
        string? saveStateReportPath)
    {
        var reportPath = Path.Combine(config.LogDirectory, ReportFileName);
        var issues = new List<QuestBoardPolicyResolveIssue>();
        var preview = QuestBoardPolicyPreviewReporter.Write(config, patchPlan, log);
        var context = BuildContext(config, projectRoot, saveStateReportPath, issues);
        var policies = preview.Policies
            .Select(policy => BuildPolicyReport(config, policy, context, issues))
            .ToArray();
        var candidates = policies.SelectMany(policy => policy.Candidates).ToArray();
        var resolvedQuestIds = policies
            .SelectMany(policy => policy.ResolvedQuestIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (preview.ErrorCount > 0)
        {
            issues.Add(new QuestBoardPolicyResolveIssue(
                "error",
                "quest-board-policy-preview-has-errors",
                string.Empty,
                string.Empty,
                string.Empty,
                $"quest board policy preview reported {preview.ErrorCount} error(s); policy resolution is blocked for invalid required content"));
        }

        var report = new QuestBoardPolicyResolveReport(
            ReportVersion,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            reportPath,
            preview.ReportPath,
            context.SaveStateReportPath,
            context.HasSaveFacts,
            context.Week,
            context.CompletedQuestIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
            policies.Length,
            policies.Count(policy => policy.Status is "resolved" or "empty"),
            resolvedQuestIds.Length,
            candidates.Count(candidate => candidate.ResolutionStatus is "active" or "eligiblePoolCandidate"),
            candidates.Count(candidate => candidate.ResolutionStatus == "skipped"),
            candidates.Count(candidate => candidate.ResolutionStatus == "unevaluated"),
            issues.Count(issue => issue.Severity == "error"),
            issues.Count(issue => issue.Severity == "warning"),
            resolvedQuestIds,
            policies,
            issues);

        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        log.Info(
            $"quest-board-policy-resolve report path={Quote(reportPath)} policies={report.PolicyCount} " +
            $"resolvedQuests={report.ResolvedQuestCount} activeCandidates={report.ActiveCandidateCount} " +
            $"skipped={report.SkippedCandidateCount} unevaluated={report.UnevaluatedCandidateCount} " +
            $"week={FormatNullableInt(report.Week)} completedQuests={report.CompletedQuestIds.Count} " +
            $"warnings={report.WarningCount} errors={report.ErrorCount}");
        foreach (var issue in issues)
        {
            var line =
                $"quest-board-policy-resolve issue severity={issue.Severity} code={issue.Code} " +
                $"plugin={Quote(issue.PluginId)} policy={Quote(issue.PolicyId)} entry={Quote(issue.EntryId)} " +
                $"message={Quote(issue.Message)}";
            if (issue.Severity == "error")
            {
                log.Error(line);
            }
            else
            {
                log.Warn(line);
            }
        }

        return report;
    }

    private static QuestBoardPolicyResolvePolicyReport BuildPolicyReport(
        RuntimeConfig config,
        QuestBoardPolicyPreviewPolicyReport policy,
        ResolveContext context,
        List<QuestBoardPolicyResolveIssue> issues)
    {
        var state = LoadPolicyState(config, policy, issues);
        var candidates = policy.Candidates
            .Select(candidate => BuildCandidateReport(policy, candidate, context, state, issues))
            .ToArray();
        var resolvedQuestIds = candidates
            .Where(candidate => candidate.ResolutionStatus is "active" or "eligiblePoolCandidate")
            .Select(candidate => candidate.EffectiveQuestId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var status = BuildPolicyStatus(policy, candidates, resolvedQuestIds);

        return new QuestBoardPolicyResolvePolicyReport(
            policy.PluginId,
            policy.PluginName,
            policy.ManifestPath,
            policy.ValidationReportPath,
            policy.RuleIndex,
            policy.Id,
            policy.Name,
            policy.Mode,
            policy.RefreshTriggers,
            "allEligibleDeterministic",
            status,
            candidates.Length,
            candidates.Count(candidate => candidate.ResolutionStatus is "active" or "eligiblePoolCandidate"),
            candidates.Count(candidate => candidate.ResolutionStatus == "skipped"),
            candidates.Count(candidate => candidate.ResolutionStatus == "unevaluated"),
            resolvedQuestIds,
            candidates);
    }

    private static QuestBoardPolicyResolveCandidateReport BuildCandidateReport(
        QuestBoardPolicyPreviewPolicyReport policy,
        QuestBoardPolicyCandidateQuestReport candidate,
        ResolveContext context,
        JsonObject? state,
        List<QuestBoardPolicyResolveIssue> issues)
    {
        if (candidate.ContentStatus is "missingRequired" or "invalidRequiredContent")
        {
            return Candidate(candidate, "blocked", "contentInvalid", [$"contentStatus={candidate.ContentStatus}"]);
        }

        if (candidate.ContentStatus is "missingOptional" or "invalidOptionalContent")
        {
            return Candidate(candidate, "skipped", "optionalContentInvalid", [$"contentStatus={candidate.ContentStatus}"]);
        }

        var reasons = new List<string>();
        var missingFacts = new List<string>();
        var matched = EvaluateAvailability(candidate, context, state, reasons, missingFacts);
        if (missingFacts.Count > 0)
        {
            issues.Add(new QuestBoardPolicyResolveIssue(
                "warning",
                "quest-board-policy-runtime-facts-missing",
                policy.PluginId,
                policy.Id,
                candidate.Id,
                $"Entry requires unavailable fact(s): {string.Join(",", missingFacts)}"));
            reasons.AddRange(missingFacts.Select(fact => $"missing:{fact}"));
            return Candidate(candidate, "unevaluated", "missingRuntimeFacts", reasons);
        }

        if (!matched)
        {
            return Candidate(candidate, "skipped", "predicateNotMatched", reasons);
        }

        if (context.CompletedQuestIds.Contains(candidate.EffectiveQuestId) &&
            candidate.OnCompleted is "remove" or "replace" or "advancePhase")
        {
            reasons.Add($"completedAction={candidate.OnCompleted}");
            return Candidate(candidate, "skipped", "completedActionFiltered", reasons);
        }

        var resolutionStatus = string.IsNullOrWhiteSpace(candidate.Pool) && !candidate.Weight.HasValue
            ? "active"
            : "eligiblePoolCandidate";
        return Candidate(candidate, resolutionStatus, "matched", reasons.Count == 0 ? ["no predicates"] : reasons);
    }

    private static bool EvaluateAvailability(
        QuestBoardPolicyCandidateQuestReport candidate,
        ResolveContext context,
        JsonObject? state,
        List<string> reasons,
        List<string> missingFacts)
    {
        var availability = candidate.AvailableWhen;
        var matched = true;

        if (availability.WeekGte.HasValue || availability.WeekLte.HasValue || availability.WeekEq.HasValue)
        {
            if (!context.Week.HasValue)
            {
                missingFacts.Add("week");
            }
            else
            {
                var week = context.Week.Value;
                if (availability.WeekGte.HasValue)
                {
                    var ok = week >= availability.WeekGte.Value;
                    reasons.Add($"week>={availability.WeekGte.Value}:{ok}");
                    matched &= ok;
                }

                if (availability.WeekLte.HasValue)
                {
                    var ok = week <= availability.WeekLte.Value;
                    reasons.Add($"week<={availability.WeekLte.Value}:{ok}");
                    matched &= ok;
                }

                if (availability.WeekEq.HasValue)
                {
                    var ok = week == availability.WeekEq.Value;
                    reasons.Add($"week=={availability.WeekEq.Value}:{ok}");
                    matched &= ok;
                }
            }
        }

        foreach (var questId in availability.CompletedQuests)
        {
            if (!context.HasCompletedQuestFacts)
            {
                missingFacts.Add("completedQuestIds");
                continue;
            }

            var ok = context.CompletedQuestIds.Contains(questId);
            reasons.Add($"completed:{questId}:{ok}");
            matched &= ok;
        }

        foreach (var questId in availability.NotCompletedQuests)
        {
            if (!context.HasCompletedQuestFacts)
            {
                missingFacts.Add("completedQuestIds");
                continue;
            }

            var ok = !context.CompletedQuestIds.Contains(questId);
            reasons.Add($"notCompleted:{questId}:{ok}");
            matched &= ok;
        }

        if (!string.IsNullOrWhiteSpace(availability.Phase))
        {
            if (state is null || !TryGetPath(state, "phase", out var phaseNode))
            {
                missingFacts.Add("state.phase");
            }
            else
            {
                var phase = ReadScalarAsString(phaseNode);
                var ok = phase.Equals(availability.Phase, StringComparison.OrdinalIgnoreCase);
                reasons.Add($"phase=={availability.Phase}:{ok}");
                matched &= ok;
            }
        }

        if (!string.IsNullOrWhiteSpace(availability.StateKey))
        {
            if (state is null || !TryGetPath(state, availability.StateKey, out var node))
            {
                missingFacts.Add($"state.{availability.StateKey}");
            }
            else if (!string.IsNullOrWhiteSpace(availability.StateEquals))
            {
                var value = ReadScalarAsString(node);
                var ok = value.Equals(availability.StateEquals, StringComparison.OrdinalIgnoreCase);
                reasons.Add($"state.{availability.StateKey}=={availability.StateEquals}:{ok}");
                matched &= ok;
            }
            else
            {
                var ok = IsTruthy(node);
                reasons.Add($"state.{availability.StateKey}:truthy:{ok}");
                matched &= ok;
            }
        }

        return matched;
    }

    private static QuestBoardPolicyResolveCandidateReport Candidate(
        QuestBoardPolicyCandidateQuestReport candidate,
        string resolutionStatus,
        string predicateStatus,
        IReadOnlyList<string> reasons)
    {
        return new QuestBoardPolicyResolveCandidateReport(
            candidate.Index,
            candidate.Id,
            candidate.QuestId,
            candidate.SourceQuestId,
            candidate.EffectiveQuestId,
            candidate.Pool,
            candidate.Weight,
            candidate.OnCompleted,
            candidate.Required,
            candidate.ContentStatus,
            predicateStatus,
            resolutionStatus,
            candidate.AvailableWhen,
            candidate.Content,
            reasons);
    }

    private static ResolveContext BuildContext(
        RuntimeConfig config,
        string projectRoot,
        string? saveStateReportPath,
        List<QuestBoardPolicyResolveIssue> issues)
    {
        JsonObject? facts = null;
        var resolvedSaveStateReportPath = string.Empty;
        if (!string.IsNullOrWhiteSpace(saveStateReportPath))
        {
            resolvedSaveStateReportPath = Path.GetFullPath(Path.IsPathRooted(saveStateReportPath)
                ? saveStateReportPath
                : Path.Combine(projectRoot, saveStateReportPath));
            var saveReport = JsonNode.Parse(File.ReadAllText(resolvedSaveStateReportPath, Encoding.UTF8)) as JsonObject
                ?? throw new InvalidDataException($"Save state report root must be a JSON object: {resolvedSaveStateReportPath}");
            facts = saveReport["facts"] as JsonObject ?? saveReport;
        }

        var completedQuestIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasCompletedQuestFacts = false;
        int? week = null;
        if (facts is not null)
        {
            week = ReadOptionalIntPath(facts, "campaignLog.totalWeeks");
            hasCompletedQuestFacts = AddCompletedQuestFacts(facts, completedQuestIds);
        }

        return new ResolveContext(
            resolvedSaveStateReportPath,
            facts is not null,
            week,
            hasCompletedQuestFacts,
            completedQuestIds);
    }

    private static bool AddCompletedQuestFacts(JsonObject facts, HashSet<string> completedQuestIds)
    {
        var touched = false;
        touched |= AddStringArrayPath(facts, "completedQuestIds", completedQuestIds);
        touched |= AddStringArrayPath(facts, "progression.completedQuestIds", completedQuestIds);
        touched |= AddStringArrayPath(facts, "progression.completedPlotQuestDataIds", completedQuestIds);

        if (ReadOptionalBoolPath(facts, "progression.lastRaidSuccess") == true)
        {
            touched |= AddStringArrayPath(facts, "progression.lastRaidQuest.names", completedQuestIds);
        }

        if (ReadOptionalBoolPath(facts, "campaignLog.latestCompletedPartyRaidRecord.success") == true &&
            ReadOptionalBoolPath(facts, "campaignLog.latestCompletedPartyRaidRecord.start") != true)
        {
            touched |= AddStringArrayPath(facts, "campaignLog.latestCompletedPartyRaidRecord.questId.names", completedQuestIds);
            touched |= AddStringArrayPath(facts, "campaignLog.latestCompletedPartyRaidRecord.quest.names", completedQuestIds);
        }

        if (TryGetPath(facts, "campaignLog.partyRaidRecords", out var recordsNode) && recordsNode is JsonArray records)
        {
            touched = true;
            foreach (var recordNode in records.OfType<JsonObject>())
            {
                if (ReadOptionalBool(recordNode, "success") != true || ReadOptionalBool(recordNode, "start") == true)
                {
                    continue;
                }

                AddStringArrayPath(recordNode, "questId.names", completedQuestIds);
                AddStringArrayPath(recordNode, "quest.names", completedQuestIds);
            }
        }

        return touched;
    }

    private static bool AddStringArrayPath(JsonObject root, string path, HashSet<string> values)
    {
        if (!TryGetPath(root, path, out var node))
        {
            return false;
        }

        foreach (var value in ReadStringValues(node))
        {
            values.Add(value);
        }

        return true;
    }

    private static IEnumerable<string> ReadStringValues(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                foreach (var value in ReadStringValues(item))
                {
                    yield return value;
                }
            }
            yield break;
        }

        var text = ReadScalarAsString(node);
        if (!string.IsNullOrWhiteSpace(text))
        {
            yield return text;
        }
    }

    private static JsonObject? LoadPolicyState(
        RuntimeConfig config,
        QuestBoardPolicyPreviewPolicyReport policy,
        List<QuestBoardPolicyResolveIssue> issues)
    {
        if (!Directory.Exists(config.ModStateDirectory))
        {
            return null;
        }

        foreach (var statePath in Directory.EnumerateFiles(config.ModStateDirectory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            JsonObject root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(statePath, Encoding.UTF8)) as JsonObject
                    ?? throw new InvalidDataException("state root must be a JSON object");
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                issues.Add(new QuestBoardPolicyResolveIssue(
                    "warning",
                    "quest-board-policy-state-read-failed",
                    policy.PluginId,
                    policy.Id,
                    string.Empty,
                    $"Failed to read sidecar state file {statePath}: {ex.Message}"));
                continue;
            }

            if (!ReadOptionalString(root, "pluginId").Equals(policy.PluginId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var manifestPath = ReadOptionalString(root, "pluginManifestPath");
            if (!string.IsNullOrWhiteSpace(manifestPath) &&
                !manifestPath.Equals(policy.ManifestPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return root["state"] as JsonObject;
        }

        return null;
    }

    private static string BuildPolicyStatus(
        QuestBoardPolicyPreviewPolicyReport policy,
        IReadOnlyList<QuestBoardPolicyResolveCandidateReport> candidates,
        IReadOnlyList<string> resolvedQuestIds)
    {
        if (policy.Status != "ready")
        {
            return policy.Status;
        }

        if (candidates.Any(candidate => candidate.ResolutionStatus == "unevaluated"))
        {
            return resolvedQuestIds.Count > 0 ? "partial" : "waitingForFacts";
        }

        return resolvedQuestIds.Count > 0 ? "resolved" : "empty";
    }

    private static int? ReadOptionalIntPath(JsonObject root, string path)
    {
        return TryGetPath(root, path, out var node) &&
            node is JsonValue value &&
            value.TryGetValue<int>(out var result)
                ? result
                : null;
    }

    private static bool? ReadOptionalBoolPath(JsonObject root, string path)
    {
        return TryGetPath(root, path, out var node) && node is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result
            : null;
    }

    private static bool? ReadOptionalBool(JsonObject root, string key)
    {
        return root[key] is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result
            : null;
    }

    private static string ReadOptionalString(JsonObject root, string key)
    {
        return root[key]?.GetValue<string>() ?? string.Empty;
    }

    private static string ReadScalarAsString(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return text ?? string.Empty;
            }

            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue.ToString(CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<long>(out var longValue))
            {
                return longValue.ToString(CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<bool>(out var boolValue))
            {
                return boolValue ? "true" : "false";
            }
        }

        return node.ToJsonString(JsonOptions);
    }

    private static bool IsTruthy(JsonNode? node)
    {
        if (node is null)
        {
            return false;
        }

        if (node is JsonArray array)
        {
            return array.Count > 0;
        }

        if (node is JsonObject obj)
        {
            return obj.Count > 0;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolValue))
            {
                return boolValue;
            }

            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue != 0;
            }

            if (value.TryGetValue<long>(out var longValue))
            {
                return longValue != 0;
            }

            if (value.TryGetValue<string>(out var text))
            {
                return !string.IsNullOrWhiteSpace(text) &&
                    !text.Equals("false", StringComparison.OrdinalIgnoreCase) &&
                    !text.Equals("0", StringComparison.OrdinalIgnoreCase);
            }
        }

        return true;
    }

    private static bool TryGetPath(JsonNode? root, string path, out JsonNode? value)
    {
        value = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (value is JsonObject obj)
            {
                if (!obj.TryGetPropertyValue(part, out value))
                {
                    value = null;
                    return false;
                }

                continue;
            }

            if (value is JsonArray array &&
                int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
                index >= 0 &&
                index < array.Count)
            {
                value = array[index];
                continue;
            }

            value = null;
            return false;
        }

        return true;
    }

    private static string FormatNullableInt(int? value)
    {
        return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "\"\"";
    }

    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';

    private sealed record ResolveContext(
        string SaveStateReportPath,
        bool HasSaveFacts,
        int? Week,
        bool HasCompletedQuestFacts,
        HashSet<string> CompletedQuestIds);
}

internal sealed record QuestBoardPolicyResolveReport(
    int Version,
    string GeneratedAtUtc,
    string ReportPath,
    string PreviewReportPath,
    string SaveStateReportPath,
    bool HasSaveFacts,
    int? Week,
    IReadOnlyList<string> CompletedQuestIds,
    int PolicyCount,
    int ReadyPolicyCount,
    int ResolvedQuestCount,
    int ActiveCandidateCount,
    int SkippedCandidateCount,
    int UnevaluatedCandidateCount,
    int ErrorCount,
    int WarningCount,
    IReadOnlyList<string> ResolvedQuestIds,
    IReadOnlyList<QuestBoardPolicyResolvePolicyReport> Policies,
    IReadOnlyList<QuestBoardPolicyResolveIssue> Issues)
{
    public bool Succeeded => ErrorCount == 0;
}

internal sealed record QuestBoardPolicyResolvePolicyReport(
    string PluginId,
    string PluginName,
    string ManifestPath,
    string ValidationReportPath,
    int RuleIndex,
    string Id,
    string Name,
    string Mode,
    IReadOnlyList<string> RefreshTriggers,
    string SelectionMode,
    string Status,
    int CandidateCount,
    int ActiveCandidateCount,
    int SkippedCandidateCount,
    int UnevaluatedCandidateCount,
    IReadOnlyList<string> ResolvedQuestIds,
    IReadOnlyList<QuestBoardPolicyResolveCandidateReport> Candidates);

internal sealed record QuestBoardPolicyResolveCandidateReport(
    int Index,
    string Id,
    string QuestId,
    string SourceQuestId,
    string EffectiveQuestId,
    string Pool,
    int? Weight,
    string OnCompleted,
    bool Required,
    string ContentStatus,
    string PredicateStatus,
    string ResolutionStatus,
    QuestBoardPolicyAvailableWhenFacts AvailableWhen,
    QuestBoardPolicyCandidateContentReport Content,
    IReadOnlyList<string> Reasons);

internal sealed record QuestBoardPolicyResolveIssue(
    string Severity,
    string Code,
    string PluginId,
    string PolicyId,
    string EntryId,
    string Message);
