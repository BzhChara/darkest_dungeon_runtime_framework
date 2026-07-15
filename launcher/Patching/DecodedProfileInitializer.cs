namespace DDRuntimeLoader;

internal static class DecodedProfileInitializer
{
    private const int ReportVersion = 1;
    private const string InitializationEventId = "profile.initialization_requested";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static DecodedProfileInitializationReport Run(
        RuntimeConfig config,
        PatchPlan patchPlan,
        LauncherLog log,
        string projectRoot,
        string saveDirectory,
        bool writeChanges,
        string? modStateId,
        string? eventPayload,
        string? eventPayloadFile)
    {
        var reportPath = Path.Combine(config.LogDirectory, "decoded_profile_initialization_report.json");
        var issues = new List<DecodedProfileInitializationIssue>();
        ManagedActionApplyReport? applyReport = null;
        QuestBoardPreviewReport? questBoardPreview = null;

        var stateReport = ModStateStore.InitializeDefaults(config, patchPlan, log, modStateId);
        AddStateIssues(issues, stateReport.Issues);
        if (!stateReport.Succeeded)
        {
            AddIssue(
                issues,
                "error",
                "decoded-profile-state-init-failed",
                stateReport.StateDirectory,
                "sidecar state initialization failed; profile initialization event and decoded-save apply were skipped");
        }

        RuntimeEventExecutionReport? eventReport = null;
        if (stateReport.Succeeded)
        {
            eventReport = RuntimeEventExecutor.Execute(
                config,
                patchPlan,
                log,
                InitializationEventId,
                eventPayload,
                eventPayloadFile,
                projectRoot,
                modStateId);

            AddRuntimeEventIssues(issues, eventReport.Issues);
            if (!eventReport.Succeeded)
            {
                AddIssue(
                    issues,
                    "error",
                    "decoded-profile-initialization-event-failed",
                    InitializationEventId,
                    "profile initialization event failed; decoded-save apply was skipped");
            }
        }

        if (stateReport.Succeeded && eventReport?.Succeeded == true)
        {
            questBoardPreview = QuestBoardPreviewReporter.Write(config, patchPlan, log);
            AddQuestBoardPreviewIssues(issues, questBoardPreview.Issues);
            if (!questBoardPreview.Succeeded)
            {
                AddIssue(
                    issues,
                    "error",
                    "decoded-profile-quest-board-preview-failed",
                    questBoardPreview.ReportPath,
                    "quest board preview failed after profile initialization materialization; decoded-save apply was skipped");
            }
        }

        if (stateReport.Succeeded && eventReport?.Succeeded == true && questBoardPreview?.Succeeded == true)
        {
            applyReport = ManagedActionSaveApplier.Apply(config, patchPlan, log, projectRoot, saveDirectory, writeChanges);
            AddManagedActionApplyIssues(issues, applyReport.Issues);
            if (!applyReport.Succeeded)
            {
                AddIssue(
                    issues,
                    "error",
                    "decoded-profile-managed-action-apply-failed",
                    applyReport.SaveDirectory,
                    "managed action apply failed for decoded profile initialization");
            }
        }

        var report = new DecodedProfileInitializationReport(
            ReportVersion,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            reportPath,
            InitializationEventId,
            config.ModStateDirectory,
            modStateId ?? string.Empty,
            saveDirectory,
            !writeChanges,
            stateReport.Succeeded,
            stateReport.PluginCount,
            stateReport.WrittenCount,
            eventReport?.Succeeded ?? false,
            eventReport?.MatchedRuleCount ?? 0,
            eventReport?.ExecutedActionCount ?? 0,
            eventReport?.MaterializedActionCount ?? 0,
            eventReport?.StateWriteCount ?? 0,
            questBoardPreview?.Succeeded,
            questBoardPreview?.ReportPath ?? string.Empty,
            questBoardPreview?.FinalActiveQuestCount ?? 0,
            applyReport is null,
            BuildApplySkippedReason(stateReport, eventReport, questBoardPreview),
            applyReport?.Succeeded,
            applyReport?.SaveDirectory ?? string.Empty,
            applyReport?.ArtifactCount ?? 0,
            applyReport?.SupportedActionCount ?? 0,
            applyReport?.RecognizedActionCount ?? 0,
            applyReport?.DryRunActionCount ?? 0,
            applyReport?.AppliedActionCount ?? 0,
            applyReport?.UnsupportedActionCount ?? 0,
            applyReport?.FailedActionCount ?? 0,
            applyReport?.ChangedFileCount ?? 0,
            applyReport?.Actions ?? [],
            applyReport?.Files ?? [],
            issues.Count(issue => issue.Severity == "error"),
            issues.Count(issue => issue.Severity == "warning"),
            issues);

        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        log.Info(
            $"decoded-profile-initialization report path={Quote(reportPath)} dryRun={report.DryRun} " +
            $"stateSucceeded={report.StateSucceeded} eventSucceeded={report.EventSucceeded} " +
            $"materializedActions={report.MaterializedActionCount} questBoardCandidates={report.QuestBoardCandidateCount} " +
            $"applySkipped={report.ApplySkipped} applySucceeded={report.ApplySucceeded} " +
            $"applyArtifacts={report.ApplyArtifactCount} supported={report.ApplySupportedActionCount} " +
            $"recognized={report.ApplyRecognizedActionCount} " +
            $"applied={report.ApplyAppliedActionCount} dryRunActions={report.ApplyDryRunActionCount} " +
            $"unsupported={report.ApplyUnsupportedActionCount} failed={report.ApplyFailedActionCount} " +
            $"changedFiles={report.ApplyChangedFileCount} warnings={report.WarningCount} errors={report.ErrorCount}");

        foreach (var issue in issues)
        {
            var line = $"decoded-profile-initialization issue code={issue.Code} path={Quote(issue.Path)} message={Quote(issue.Message)}";
            if (issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase))
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

    private static string BuildApplySkippedReason(
        ModStateStoreReport stateReport,
        RuntimeEventExecutionReport? eventReport,
        QuestBoardPreviewReport? questBoardPreview)
    {
        if (!stateReport.Succeeded)
        {
            return "state initialization failed";
        }

        if (eventReport is null)
        {
            return "initialization event was not run";
        }

        if (!eventReport.Succeeded)
        {
            return "initialization event failed";
        }

        if (questBoardPreview is null)
        {
            return "quest board preview was not run";
        }

        if (!questBoardPreview.Succeeded)
        {
            return "quest board preview failed";
        }

        return string.Empty;
    }

    private static void AddStateIssues(
        List<DecodedProfileInitializationIssue> issues,
        IReadOnlyList<ModStateIssue> stateIssues)
    {
        foreach (var issue in stateIssues)
        {
            AddIssue(
                issues,
                issue.Severity,
                $"state.{issue.Code}",
                issue.Path,
                issue.Message);
        }
    }

    private static void AddRuntimeEventIssues(
        List<DecodedProfileInitializationIssue> issues,
        IReadOnlyList<RuntimeEventExecutionIssue> eventIssues)
    {
        foreach (var issue in eventIssues)
        {
            var path = string.Join('/',
                new[] { issue.PluginId, issue.RuleId, issue.ActionType }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            AddIssue(
                issues,
                issue.Severity,
                $"event.{issue.Code}",
                path,
                issue.Message);
        }
    }

    private static void AddQuestBoardPreviewIssues(
        List<DecodedProfileInitializationIssue> issues,
        IReadOnlyList<QuestBoardPreviewIssue> previewIssues)
    {
        foreach (var issue in previewIssues)
        {
            AddIssue(
                issues,
                issue.Severity,
                $"questBoardPreview.{issue.Code}",
                issue.ArtifactPath,
                issue.Message);
        }
    }

    private static void AddManagedActionApplyIssues(
        List<DecodedProfileInitializationIssue> issues,
        IReadOnlyList<ManagedActionApplyIssue> applyIssues)
    {
        foreach (var issue in applyIssues)
        {
            AddIssue(
                issues,
                issue.Severity,
                $"managedActionApply.{issue.Code}",
                issue.ArtifactPath,
                issue.Message);
        }
    }

    private static void AddIssue(
        List<DecodedProfileInitializationIssue> issues,
        string severity,
        string code,
        string path,
        string message)
    {
        issues.Add(new DecodedProfileInitializationIssue(severity, code, path, message));
    }

    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
}

internal sealed record DecodedProfileInitializationReport(
    int Version,
    string GeneratedAtUtc,
    string ReportPath,
    string InitializationEventId,
    string ModStateDirectory,
    string ModStateId,
    string RequestedSaveDirectory,
    bool DryRun,
    bool StateSucceeded,
    int StatePluginCount,
    int StateWrittenCount,
    bool EventSucceeded,
    int MatchedRuleCount,
    int ExecutedActionCount,
    int MaterializedActionCount,
    int StateWriteCount,
    bool? QuestBoardPreviewSucceeded,
    string QuestBoardPreviewReportPath,
    int QuestBoardCandidateCount,
    bool ApplySkipped,
    string ApplySkippedReason,
    bool? ApplySucceeded,
    string ResolvedSaveDirectory,
    int ApplyArtifactCount,
    int ApplySupportedActionCount,
    int ApplyRecognizedActionCount,
    int ApplyDryRunActionCount,
    int ApplyAppliedActionCount,
    int ApplyUnsupportedActionCount,
    int ApplyFailedActionCount,
    int ApplyChangedFileCount,
    IReadOnlyList<ManagedActionApplyActionReport> ApplyActions,
    IReadOnlyList<ManagedActionApplyFileReport> ApplyFiles,
    int ErrorCount,
    int WarningCount,
    IReadOnlyList<DecodedProfileInitializationIssue> Issues)
{
    public bool Succeeded => ErrorCount == 0 && StateSucceeded && EventSucceeded && QuestBoardPreviewSucceeded == true && ApplySucceeded == true;
}

internal sealed record DecodedProfileInitializationIssue(
    string Severity,
    string Code,
    string Path,
    string Message);
