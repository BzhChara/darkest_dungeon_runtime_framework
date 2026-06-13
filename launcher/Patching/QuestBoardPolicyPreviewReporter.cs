using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static class QuestBoardPolicyPreviewReporter
{
    private const int ReportVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static QuestBoardPolicyPreviewReport Write(RuntimeConfig config, PatchPlan patchPlan, LauncherLog log)
    {
        var reportPath = Path.Combine(config.LogDirectory, "quest_board_policy_preview_report.json");
        var definitions = QuestBoardContentCatalog.LoadEnabledPlotQuestDefinitions(config.GameWorkingDirectory);
        var issues = new List<QuestBoardPolicyPreviewIssue>();
        var policies = new List<QuestBoardPolicyPreviewPolicyReport>();

        foreach (var policy in patchPlan.QuestBoardPolicyReports)
        {
            policies.Add(BuildPolicyReport(policy, definitions, issues));
        }

        var candidates = policies.SelectMany(policy => policy.Candidates).ToArray();
        var report = new QuestBoardPolicyPreviewReport(
            ReportVersion,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            reportPath,
            patchPlan.QuestBoardPolicyReports.Count,
            policies.Count(policy => policy.Status == "ready"),
            candidates.Length,
            candidates.Count(candidate => string.IsNullOrWhiteSpace(candidate.Pool) && !candidate.Weight.HasValue),
            candidates.Count(candidate => !string.IsNullOrWhiteSpace(candidate.Pool) || candidate.Weight.HasValue),
            candidates.Count(candidate => candidate.ContentStatus == "missingRequired"),
            candidates.Count(candidate => candidate.ContentStatus == "missingOptional"),
            issues.Count(issue => issue.Severity == "error"),
            issues.Count(issue => issue.Severity == "warning"),
            policies,
            issues);

        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        log.Info(
            $"quest-board-policy-preview report path={Quote(reportPath)} policies={report.PolicyCount} " +
            $"readyPolicies={report.ReadyPolicyCount} candidates={report.CandidateQuestCount} " +
            $"fixedCandidates={report.FixedCandidateCount} randomCandidates={report.RandomCandidateCount} " +
            $"missingRequired={report.MissingRequiredQuestCount} missingOptional={report.MissingOptionalQuestCount} " +
            $"warnings={report.WarningCount} errors={report.ErrorCount}");
        return report;
    }

    private static QuestBoardPolicyPreviewPolicyReport BuildPolicyReport(
        QuestBoardPolicyValidationReport policy,
        IReadOnlyDictionary<string, PlotQuestDefinition> definitions,
        List<QuestBoardPolicyPreviewIssue> issues)
    {
        var candidates = new List<QuestBoardPolicyCandidateQuestReport>();
        foreach (var entry in policy.Entries)
        {
            candidates.Add(BuildCandidate(policy, entry, definitions, issues));
        }

        var status = !policy.Succeeded
            ? "invalidPolicy"
            : candidates.Any(candidate => candidate.ContentStatus is "missingRequired" or "invalidRequiredContent")
                ? "blockedRequiredContent"
                : "ready";

        if (!policy.Succeeded)
        {
            issues.Add(new QuestBoardPolicyPreviewIssue(
                "error",
                "quest-board-policy-validation-failed",
                policy.PluginId,
                policy.Id,
                string.Empty,
                $"Policy validation failed with {policy.Issues.Count} issue(s)."));
        }

        return new QuestBoardPolicyPreviewPolicyReport(
            policy.PluginId,
            policy.PluginName,
            policy.ManifestPath,
            policy.ReportPath,
            policy.RuleIndex,
            policy.Id,
            policy.Name,
            policy.Mode,
            policy.RefreshTriggers,
            status,
            candidates.Count,
            candidates.Count(candidate => string.IsNullOrWhiteSpace(candidate.Pool) && !candidate.Weight.HasValue),
            candidates.Count(candidate => !string.IsNullOrWhiteSpace(candidate.Pool) || candidate.Weight.HasValue),
            candidates,
            policy.Issues);
    }

    private static QuestBoardPolicyCandidateQuestReport BuildCandidate(
        QuestBoardPolicyValidationReport policy,
        QuestBoardPolicyEntryFacts entry,
        IReadOnlyDictionary<string, PlotQuestDefinition> definitions,
        List<QuestBoardPolicyPreviewIssue> issues)
    {
        var contentStatus = "found";
        PlotQuestDefinition? definition = null;
        if (!definitions.TryGetValue(entry.EffectiveQuestId, out definition))
        {
            contentStatus = entry.Required ? "missingRequired" : "missingOptional";
            issues.Add(new QuestBoardPolicyPreviewIssue(
                entry.Required ? "error" : "warning",
                entry.Required ? "quest-board-policy-required-quest-missing" : "quest-board-policy-optional-quest-missing",
                policy.PluginId,
                policy.Id,
                entry.Id,
                $"Policy entry references unknown plot quest id: {entry.EffectiveQuestId}"));
        }

        var content = new QuestBoardPolicyCandidateContentReport(string.Empty, string.Empty, string.Empty, null, null, []);
        if (definition is not null)
        {
            try
            {
                content = BuildContentReport(definition);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
            {
                contentStatus = entry.Required ? "invalidRequiredContent" : "invalidOptionalContent";
                issues.Add(new QuestBoardPolicyPreviewIssue(
                    entry.Required ? "error" : "warning",
                    entry.Required ? "quest-board-policy-required-quest-invalid" : "quest-board-policy-optional-quest-invalid",
                    policy.PluginId,
                    policy.Id,
                    entry.Id,
                    ex.Message));
            }
        }

        return new QuestBoardPolicyCandidateQuestReport(
            entry.Index,
            entry.Id,
            entry.QuestId,
            entry.SourceQuestId,
            entry.EffectiveQuestId,
            entry.Pool,
            entry.Weight,
            entry.OnCompleted,
            entry.Required,
            contentStatus,
            entry.AvailableWhen.HasPredicate ? "requiresRuntimeFacts" : "staticallyEligible",
            entry.AvailableWhen,
            content);
    }

    private static QuestBoardPolicyCandidateContentReport BuildContentReport(PlotQuestDefinition definition)
    {
        var entry = QuestBoardContentCatalog.BuildQuestBoardEntry(definition);
        return new QuestBoardPolicyCandidateContentReport(
            definition.SourcePath,
            ReadOptionalString(entry, "type"),
            ReadOptionalString(entry, "dungeon"),
            ReadOptionalInt(entry, "difficulty"),
            ReadOptionalInt(entry, "length"),
            ReadOptionalStringArray(entry, "goal_ids"));
    }

    private static string ReadOptionalString(JsonObject root, string key)
    {
        return root[key]?.GetValue<string>() ?? string.Empty;
    }

    private static int? ReadOptionalInt(JsonObject root, string key)
    {
        return root[key] is JsonValue value && value.TryGetValue<int>(out var result)
            ? result
            : null;
    }

    private static IReadOnlyList<string> ReadOptionalStringArray(JsonObject root, string key)
    {
        if (root[key] is not JsonArray array)
        {
            return [];
        }

        return array
            .OfType<JsonValue>()
            .Select(value => value.TryGetValue<string>(out var text) ? text : string.Empty)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
    }

    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
}

internal sealed record QuestBoardPolicyPreviewReport(
    int Version,
    string GeneratedAtUtc,
    string ReportPath,
    int PolicyCount,
    int ReadyPolicyCount,
    int CandidateQuestCount,
    int FixedCandidateCount,
    int RandomCandidateCount,
    int MissingRequiredQuestCount,
    int MissingOptionalQuestCount,
    int ErrorCount,
    int WarningCount,
    IReadOnlyList<QuestBoardPolicyPreviewPolicyReport> Policies,
    IReadOnlyList<QuestBoardPolicyPreviewIssue> Issues)
{
    public bool Succeeded => ErrorCount == 0;
}

internal sealed record QuestBoardPolicyPreviewPolicyReport(
    string PluginId,
    string PluginName,
    string ManifestPath,
    string ValidationReportPath,
    int RuleIndex,
    string Id,
    string Name,
    string Mode,
    IReadOnlyList<string> RefreshTriggers,
    string Status,
    int CandidateCount,
    int FixedCandidateCount,
    int RandomCandidateCount,
    IReadOnlyList<QuestBoardPolicyCandidateQuestReport> Candidates,
    IReadOnlyList<QuestBoardPolicyValidationIssue> ValidationIssues);

internal sealed record QuestBoardPolicyCandidateQuestReport(
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
    string AvailabilityStatus,
    QuestBoardPolicyAvailableWhenFacts AvailableWhen,
    QuestBoardPolicyCandidateContentReport Content);

internal sealed record QuestBoardPolicyCandidateContentReport(
    string SourcePath,
    string Type,
    string Dungeon,
    int? Difficulty,
    int? Length,
    IReadOnlyList<string> GoalIds);

internal sealed record QuestBoardPolicyPreviewIssue(
    string Severity,
    string Code,
    string PluginId,
    string PolicyId,
    string EntryId,
    string Message);
