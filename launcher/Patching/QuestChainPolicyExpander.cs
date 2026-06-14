namespace DDRuntimeLoader;

internal static class QuestChainPolicyExpander
{
    private static readonly string[] DefaultRefreshTriggers =
    [
        "immediateOnQuestComplete",
        "onWeekAdvance",
        "manual"
    ];

    public static QuestBoardPolicyValidationReport? WriteLinearProgressionPolicyReport(
        PluginManifestCandidate plugin,
        int chainRuleIndex,
        QuestChainValidationReport chainReport,
        string reportPath)
    {
        if (!chainReport.Succeeded ||
            !chainReport.QuestBoard.Enabled ||
            !chainReport.QuestBoard.Mode.Equals("linearProgression", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var policy = BuildPolicy(chainReport);
        return QuestBoardPolicyValidator.WriteValidationReport(
            policy,
            chainRuleIndex,
            plugin.Id,
            plugin.SourceName,
            plugin.Path,
            reportPath);
    }

    private static QuestBoardPolicyRule BuildPolicy(QuestChainValidationReport chainReport)
    {
        return new QuestBoardPolicyRule
        {
            Id = $"{chainReport.Id}.linear_progression",
            Name = string.IsNullOrWhiteSpace(chainReport.Name)
                ? $"{chainReport.Id} Linear Progression"
                : $"{chainReport.Name} Linear Progression",
            Mode = "fixed",
            RefreshTriggers = BuildRefreshTriggers(chainReport.QuestBoard),
            Entries = BuildEntries(chainReport).ToArray()
        };
    }

    private static string[] BuildRefreshTriggers(QuestChainBoardFacts board)
    {
        var configured = board.RefreshTriggers
            .Where(trigger => !string.IsNullOrWhiteSpace(trigger))
            .Select(trigger => trigger.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return configured.Length > 0 ? configured : DefaultRefreshTriggers;
    }

    private static IEnumerable<QuestBoardPolicyEntryRule> BuildEntries(QuestChainValidationReport chainReport)
    {
        var previousQuestIds = new List<string>();
        foreach (var stage in chainReport.OrderedStages)
        {
            var completedQuestIds = BuildCompletedQuestPredicates(chainReport.Unlock, previousQuestIds);
            var notCompletedQuestIds = string.IsNullOrWhiteSpace(stage.SourceQuestId)
                ? Array.Empty<string>()
                : [stage.SourceQuestId];

            yield return new QuestBoardPolicyEntryRule
            {
                Id = string.IsNullOrWhiteSpace(stage.Id) ? stage.SourceQuestId : stage.Id,
                SourceQuestId = stage.SourceQuestId,
                OnCompleted = string.IsNullOrWhiteSpace(chainReport.QuestBoard.OnCompleted)
                    ? "remove"
                    : chainReport.QuestBoard.OnCompleted,
                Required = true,
                AvailableWhen = BuildAvailability(chainReport.Unlock, completedQuestIds, notCompletedQuestIds)
            };

            if (!string.IsNullOrWhiteSpace(stage.SourceQuestId))
            {
                previousQuestIds.Add(stage.SourceQuestId);
            }
        }
    }

    private static string[] BuildCompletedQuestPredicates(
        QuestChainUnlockFacts unlock,
        IReadOnlyList<string> previousQuestIds)
    {
        var completedQuestIds = new List<string>();
        if (unlock.Type.Equals("afterQuest", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(unlock.QuestId))
        {
            completedQuestIds.Add(unlock.QuestId);
        }

        completedQuestIds.AddRange(previousQuestIds);
        return completedQuestIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static QuestBoardPolicyAvailableWhenRule BuildAvailability(
        QuestChainUnlockFacts unlock,
        IReadOnlyList<string> completedQuestIds,
        IReadOnlyList<string> notCompletedQuestIds)
    {
        return new QuestBoardPolicyAvailableWhenRule
        {
            CompletedQuests = completedQuestIds.ToArray(),
            NotCompletedQuests = notCompletedQuestIds.ToArray(),
            Phase = unlock.Phase,
            StateKey = unlock.StateKey,
            StateEquals = unlock.StateEquals
        };
    }
}
