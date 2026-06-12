namespace DDRuntimeLoader;

internal static class QuestBoardLaunchPreflightReporter
{
    private const int ReportVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static QuestBoardLaunchPreflightReport Write(
        RuntimeConfig config,
        LauncherLog log,
        QuestBoardPreviewReport preview,
        ManagedActionOverlayReport overlay,
        QuestBoardRuntimeOverlayReport runtimeOverlay,
        string mode)
    {
        var reportPath = Path.Combine(config.LogDirectory, "quest_board_launch_preflight_report.json");
        var runtimeContentOverlays = BuildRuntimeContentOverlays(overlay.VirtualFileRules);
        var issues = new List<QuestBoardLaunchPreflightIssue>();

        if (preview.ErrorCount > 0)
        {
            issues.Add(new QuestBoardLaunchPreflightIssue(
                "error",
                "quest-board-preview-has-errors",
                preview.ReportPath,
                $"quest board preview reported {preview.ErrorCount} error(s); launch preflight cannot trust the candidate board"));
        }

        var hasCandidate = preview.FinalActiveQuestCount > 0;
        var runtimeQuestBoardConsumerStatus = runtimeOverlay.Status;
        var willRuntimeReplaceQuestBoard = runtimeOverlay.VirtualFileRuleCount > 0;
        var willRuntimeForceQuestContentAvailable = runtimeContentOverlays.Any(overlay =>
            overlay.Target.Equals("campaign/quest/quest.plot_quests.json", StringComparison.OrdinalIgnoreCase) &&
            overlay.Replacements.Any(replacement => replacement.Subject.StartsWith("quest.injectFixedStage:", StringComparison.OrdinalIgnoreCase)));
        var candidateStatus = preview.ErrorCount > 0
            ? "invalid"
            : hasCandidate
                ? willRuntimeReplaceQuestBoard
                    ? "runtimeOverlayReady"
                    : "previewOnly"
                : "none";

        if (hasCandidate && !willRuntimeReplaceQuestBoard)
        {
            issues.Add(new QuestBoardLaunchPreflightIssue(
                "warning",
                "quest-board-runtime-consumer-unavailable",
                runtimeOverlay.ReportPath,
                "quest board candidates exist, but the current runtime path could not compile a save overlay; check saveWatchDirectories and dsonSaveEditorJarPath"));
        }

        if (runtimeOverlay.WarningCount > 0)
        {
            issues.Add(new QuestBoardLaunchPreflightIssue(
                "warning",
                "quest-board-runtime-overlay-has-warnings",
                runtimeOverlay.ReportPath,
                $"quest board runtime overlay reported {runtimeOverlay.WarningCount} warning(s)"));
        }

        var report = new QuestBoardLaunchPreflightReport(
            ReportVersion,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            reportPath,
            mode,
            preview.ReportPath,
            overlay.ManifestPath,
            runtimeOverlay.ReportPath,
            preview.Succeeded,
            hasCandidate,
            candidateStatus,
            preview.FinalActiveQuestCount,
            runtimeQuestBoardConsumerStatus,
            willRuntimeReplaceQuestBoard,
            willRuntimeForceQuestContentAvailable,
            runtimeOverlay.VirtualFileRuleCount,
            overlay.ArtifactCount,
            overlay.OverlayCount,
            overlay.IssueCount,
            runtimeContentOverlays.Count,
            preview.FinalActiveQuests.Select(ToCandidateQuest).ToArray(),
            runtimeContentOverlays,
            issues.Count(issue => issue.Severity == "warning"),
            issues.Count(issue => issue.Severity == "error"),
            issues);

        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        log.Info(
            $"quest-board-launch-preflight report path={Quote(reportPath)} mode={mode} " +
            $"candidateStatus={report.CandidateQuestBoardStatus} candidateQuests={report.CandidateQuestCount} " +
            $"runtimeQuestBoardConsumer={report.RuntimeQuestBoardConsumerStatus} " +
            $"willReplaceQuestBoard={report.WillRuntimeReplaceQuestBoard} " +
            $"willForceQuestContent={report.WillRuntimeForceQuestContentAvailable} " +
            $"runtimeContentOverlays={report.RuntimeContentOverlayCount} warnings={report.WarningCount} errors={report.ErrorCount}");

        foreach (var issue in issues.Where(issue => issue.Severity == "warning"))
        {
            log.Warn($"quest-board-launch-preflight issue code={issue.Code} path={Quote(issue.Path)} message={Quote(issue.Message)}");
        }

        foreach (var issue in issues.Where(issue => issue.Severity == "error"))
        {
            log.Error($"quest-board-launch-preflight issue code={issue.Code} path={Quote(issue.Path)} message={Quote(issue.Message)}");
        }

        return report;
    }

    private static IReadOnlyList<QuestBoardRuntimeContentOverlayReport> BuildRuntimeContentOverlays(
        IReadOnlyList<VirtualFileRule> virtualRules)
    {
        return virtualRules
            .Select((rule, index) => new QuestBoardRuntimeContentOverlayReport(
                index,
                rule.Target,
                rule.SourcePath,
                rule.Replacements.Length,
                rule.Replacements
                    .Select((replacement, replacementIndex) =>
                    {
                        var origin = replacement.Origin ?? PatchReplacementOrigin.Unknown;
                        return new QuestBoardRuntimeContentReplacementReport(
                            replacementIndex,
                            origin.SourceName,
                            origin.SourcePath,
                            origin.RuleIndex,
                            origin.OperationIndex,
                            origin.OperationType,
                            origin.Subject,
                            replacement.Find.Length,
                            replacement.Replace.Length);
                    })
                    .ToArray()))
            .ToArray();
    }

    private static QuestBoardLaunchCandidateQuestReport ToCandidateQuest(QuestBoardPreviewQuestReport quest)
    {
        return new QuestBoardLaunchCandidateQuestReport(
            quest.Order,
            quest.QuestId,
            quest.QuestChainId,
            quest.StageId,
            quest.StageName,
            quest.SourceQuestId,
            quest.TargetQuestId,
            quest.Region,
            quest.DeclaredDifficulty,
            quest.ContentSourcePath,
            quest.Type,
            quest.Dungeon,
            quest.ContentDifficulty,
            quest.Length,
            quest.GoalIds);
    }

    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
}

internal sealed record QuestBoardLaunchPreflightReport(
    int Version,
    string GeneratedAtUtc,
    string ReportPath,
    string Mode,
    string QuestBoardPreviewReportPath,
    string ManagedOverlayManifestPath,
    string QuestBoardRuntimeOverlayReportPath,
    bool QuestBoardPreviewSucceeded,
    bool HasQuestBoardCandidate,
    string CandidateQuestBoardStatus,
    int CandidateQuestCount,
    string RuntimeQuestBoardConsumerStatus,
    bool WillRuntimeReplaceQuestBoard,
    bool WillRuntimeForceQuestContentAvailable,
    int RuntimeQuestBoardOverlayRuleCount,
    int ManagedArtifactCount,
    int ManagedOverlayCount,
    int ManagedOverlayIssueCount,
    int RuntimeContentOverlayCount,
    IReadOnlyList<QuestBoardLaunchCandidateQuestReport> CandidateQuests,
    IReadOnlyList<QuestBoardRuntimeContentOverlayReport> RuntimeContentOverlays,
    int WarningCount,
    int ErrorCount,
    IReadOnlyList<QuestBoardLaunchPreflightIssue> Issues)
{
    public bool Succeeded => ErrorCount == 0;
}

internal sealed record QuestBoardLaunchCandidateQuestReport(
    int Order,
    string QuestId,
    string QuestChainId,
    string StageId,
    string StageName,
    string SourceQuestId,
    string TargetQuestId,
    string Region,
    int? DeclaredDifficulty,
    string ContentSourcePath,
    string Type,
    string Dungeon,
    int? ContentDifficulty,
    int? Length,
    IReadOnlyList<string> GoalIds);

internal sealed record QuestBoardRuntimeContentOverlayReport(
    int Index,
    string Target,
    string SourcePath,
    int ReplacementCount,
    IReadOnlyList<QuestBoardRuntimeContentReplacementReport> Replacements);

internal sealed record QuestBoardRuntimeContentReplacementReport(
    int Index,
    string SourceName,
    string SourcePath,
    int RuleIndex,
    int ActionIndex,
    string OperationType,
    string Subject,
    int FindChars,
    int ReplaceChars);

internal sealed record QuestBoardLaunchPreflightIssue(
    string Severity,
    string Code,
    string Path,
    string Message);
