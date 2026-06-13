namespace DDRuntimeLoader;

internal static class QuestBoardPolicyValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly string[] SupportedModes = ["fixed", "random", "mixed"];

    private static readonly string[] SupportedRefreshTriggers =
    [
        "onProfileInitialize",
        "onWeekAdvance",
        "immediateOnQuestComplete",
        "manual"
    ];

    private static readonly string[] SupportedCompletionActions =
    [
        "keep",
        "remove",
        "replace",
        "advancePhase"
    ];

    public static QuestBoardPolicyValidationReport WriteValidationReport(
        QuestBoardPolicyRule policy,
        int ruleIndex,
        string pluginId,
        string pluginName,
        string manifestPath,
        string reportPath)
    {
        var issues = new List<QuestBoardPolicyValidationIssue>();
        ValidateHeader(policy, issues);

        var refreshTriggers = BuildRefreshTriggerFacts(policy.RefreshTriggers ?? [], issues);
        var entries = BuildEntryFacts(policy.Entries ?? [], issues);
        var report = new QuestBoardPolicyValidationReport(
            "questBoardPolicy",
            pluginId,
            pluginName,
            manifestPath,
            reportPath,
            ruleIndex,
            policy.Id,
            policy.Name,
            Clean(policy.Mode),
            refreshTriggers,
            entries.Count,
            entries.Count(entry => string.IsNullOrWhiteSpace(entry.Pool) && !entry.Weight.HasValue),
            entries.Count(entry => !string.IsNullOrWhiteSpace(entry.Pool) || entry.Weight.HasValue),
            !issues.Any(issue => issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)),
            entries,
            issues);

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        return report;
    }

    private static void ValidateHeader(QuestBoardPolicyRule policy, List<QuestBoardPolicyValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(policy.Id))
        {
            AddError(issues, "missing-policy-id", "id", "questBoardPolicies entry requires id");
        }

        var mode = Clean(policy.Mode);
        if (string.IsNullOrWhiteSpace(mode))
        {
            AddError(issues, "missing-policy-mode", "mode", "questBoardPolicies entry requires mode");
        }
        else if (!SupportedModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
        {
            AddError(issues, "unsupported-policy-mode", "mode", $"unsupported questBoardPolicies mode: {mode}");
        }

        if ((policy.Entries ?? []).Length == 0)
        {
            AddError(issues, "missing-policy-entries", "entries", "questBoardPolicies entry requires at least one entry");
        }
    }

    private static IReadOnlyList<string> BuildRefreshTriggerFacts(
        IReadOnlyList<string> refreshTriggers,
        List<QuestBoardPolicyValidationIssue> issues)
    {
        var triggers = refreshTriggers
            .Select(Clean)
            .Where(trigger => trigger.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (triggers.Length == 0)
        {
            AddError(issues, "missing-refresh-trigger", "refreshTriggers", "questBoardPolicies entry requires at least one refresh trigger");
            return triggers;
        }

        for (var i = 0; i < triggers.Length; i++)
        {
            if (!SupportedRefreshTriggers.Contains(triggers[i], StringComparer.OrdinalIgnoreCase))
            {
                AddError(
                    issues,
                    "unsupported-refresh-trigger",
                    $"refreshTriggers[{i}]",
                    $"unsupported quest board refresh trigger: {triggers[i]}");
            }
        }

        return triggers;
    }

    private static IReadOnlyList<QuestBoardPolicyEntryFacts> BuildEntryFacts(
        IReadOnlyList<QuestBoardPolicyEntryRule> entries,
        List<QuestBoardPolicyValidationIssue> issues)
    {
        var facts = new List<QuestBoardPolicyEntryFacts>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var questId = Clean(entry.QuestId);
            var sourceQuestId = Clean(entry.SourceQuestId);
            var effectiveQuestId = string.IsNullOrWhiteSpace(questId) ? sourceQuestId : questId;
            var entryId = string.IsNullOrWhiteSpace(entry.Id) ? effectiveQuestId : Clean(entry.Id);
            var pool = Clean(entry.Pool);
            var onCompleted = string.IsNullOrWhiteSpace(entry.OnCompleted) ? "keep" : Clean(entry.OnCompleted);
            var required = entry.Required ?? true;

            if (string.IsNullOrWhiteSpace(effectiveQuestId))
            {
                AddError(
                    issues,
                    "missing-entry-quest",
                    $"entries[{i}].questId",
                    "questBoardPolicies entry requires questId or sourceQuestId");
            }

            if (entry.Weight.HasValue && entry.Weight.Value <= 0)
            {
                AddError(issues, "invalid-entry-weight", $"entries[{i}].weight", "entry weight must be greater than zero");
            }

            if (!SupportedCompletionActions.Contains(onCompleted, StringComparer.OrdinalIgnoreCase))
            {
                AddError(
                    issues,
                    "unsupported-completion-action",
                    $"entries[{i}].onCompleted",
                    $"unsupported quest board completion action: {onCompleted}");
            }

            var availableWhen = BuildAvailableWhenFacts(entry.AvailableWhen ?? new QuestBoardPolicyAvailableWhenRule(), i, issues);
            facts.Add(new QuestBoardPolicyEntryFacts(
                i,
                entryId,
                questId,
                sourceQuestId,
                effectiveQuestId,
                pool,
                entry.Weight,
                onCompleted,
                required,
                availableWhen));
        }

        return facts;
    }

    private static QuestBoardPolicyAvailableWhenFacts BuildAvailableWhenFacts(
        QuestBoardPolicyAvailableWhenRule availableWhen,
        int entryIndex,
        List<QuestBoardPolicyValidationIssue> issues)
    {
        var completedQuests = CleanQuestList(availableWhen.CompletedQuest, availableWhen.CompletedQuests);
        var notCompletedQuests = CleanQuestList(availableWhen.NotCompletedQuest, availableWhen.NotCompletedQuests);
        var phase = Clean(availableWhen.Phase);
        var stateKey = Clean(availableWhen.StateKey);
        var stateEquals = Clean(availableWhen.StateEquals);
        var weekGte = availableWhen.WeekGte;
        var weekLte = availableWhen.WeekLte;
        var weekEq = availableWhen.WeekEq;

        if (weekGte.HasValue && weekGte.Value < 0)
        {
            AddError(issues, "invalid-week-gte", $"entries[{entryIndex}].availableWhen.weekGte", "weekGte must be zero or greater");
        }

        if (weekLte.HasValue && weekLte.Value < 0)
        {
            AddError(issues, "invalid-week-lte", $"entries[{entryIndex}].availableWhen.weekLte", "weekLte must be zero or greater");
        }

        if (weekEq.HasValue && weekEq.Value < 0)
        {
            AddError(issues, "invalid-week-eq", $"entries[{entryIndex}].availableWhen.weekEq", "weekEq must be zero or greater");
        }

        if (weekGte.HasValue && weekLte.HasValue && weekLte.Value < weekGte.Value)
        {
            AddError(
                issues,
                "invalid-week-window",
                $"entries[{entryIndex}].availableWhen",
                "weekLte must be greater than or equal to weekGte");
        }

        if (weekEq.HasValue && weekGte.HasValue && weekEq.Value < weekGte.Value)
        {
            AddError(
                issues,
                "invalid-week-eq-window",
                $"entries[{entryIndex}].availableWhen.weekEq",
                "weekEq must satisfy weekGte when both are declared");
        }

        if (weekEq.HasValue && weekLte.HasValue && weekEq.Value > weekLte.Value)
        {
            AddError(
                issues,
                "invalid-week-eq-window",
                $"entries[{entryIndex}].availableWhen.weekEq",
                "weekEq must satisfy weekLte when both are declared");
        }

        if (string.IsNullOrWhiteSpace(stateKey) && !string.IsNullOrWhiteSpace(stateEquals))
        {
            AddError(
                issues,
                "missing-state-key",
                $"entries[{entryIndex}].availableWhen.stateKey",
                "stateEquals requires stateKey");
        }

        var hasPredicate =
            completedQuests.Count > 0 ||
            notCompletedQuests.Count > 0 ||
            weekGte.HasValue ||
            weekLte.HasValue ||
            weekEq.HasValue ||
            !string.IsNullOrWhiteSpace(phase) ||
            !string.IsNullOrWhiteSpace(stateKey);

        return new QuestBoardPolicyAvailableWhenFacts(
            completedQuests,
            notCompletedQuests,
            weekGte,
            weekLte,
            weekEq,
            phase,
            stateKey,
            stateEquals,
            hasPredicate);
    }

    private static IReadOnlyList<string> CleanQuestList(string singleQuest, IReadOnlyList<string> quests)
    {
        return new[] { singleQuest }
            .Concat(quests ?? [])
            .Select(Clean)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string Clean(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static void AddError(List<QuestBoardPolicyValidationIssue> issues, string code, string path, string message)
    {
        issues.Add(new QuestBoardPolicyValidationIssue("error", code, path, message));
    }
}

internal sealed record QuestBoardPolicyValidationReport(
    string Type,
    string PluginId,
    string PluginName,
    string ManifestPath,
    string ReportPath,
    int RuleIndex,
    string Id,
    string Name,
    string Mode,
    IReadOnlyList<string> RefreshTriggers,
    int EntryCount,
    int FixedEntryCount,
    int RandomEntryCount,
    bool Succeeded,
    IReadOnlyList<QuestBoardPolicyEntryFacts> Entries,
    IReadOnlyList<QuestBoardPolicyValidationIssue> Issues);

internal sealed record QuestBoardPolicyEntryFacts(
    int Index,
    string Id,
    string QuestId,
    string SourceQuestId,
    string EffectiveQuestId,
    string Pool,
    int? Weight,
    string OnCompleted,
    bool Required,
    QuestBoardPolicyAvailableWhenFacts AvailableWhen);

internal sealed record QuestBoardPolicyAvailableWhenFacts(
    IReadOnlyList<string> CompletedQuests,
    IReadOnlyList<string> NotCompletedQuests,
    int? WeekGte,
    int? WeekLte,
    int? WeekEq,
    string Phase,
    string StateKey,
    string StateEquals,
    bool HasPredicate);

internal sealed record QuestBoardPolicyValidationIssue(
    string Severity,
    string Code,
    string Path,
    string Message);
