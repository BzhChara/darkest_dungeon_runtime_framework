namespace DDRuntimeLoader;

internal sealed class PatchPlan
{
    public PatchPlan(
        IReadOnlyList<PatchManifestInfo> manifests,
        IReadOnlyList<PluginLoadRule> loadRules,
        IReadOnlyList<PluginLoadDiagnostic> loadDiagnostics,
        IReadOnlyList<PluginStateSchemaSource> stateSchemas,
        IReadOnlyList<VirtualFileRuleSource> sourceVirtualFileRules,
        IReadOnlyList<VirtualFileRuleSkip> skippedVirtualFileRules,
        IReadOnlyList<RuntimeEventRuleSource> sourceRuntimeEventRules,
        IReadOnlyList<RuntimeEventRuleSkip> skippedRuntimeEventRules,
        IReadOnlyList<FactEventRuleSource> sourceFactEventRules,
        IReadOnlyList<FactEventRuleSkip> skippedFactEventRules,
        IReadOnlyList<ContentReferenceValidationReport> contentReferenceReports,
        IReadOnlyList<VirtualFileRule> effectiveVirtualFileRules,
        IReadOnlyList<PatchCompileIssue> compileIssues)
    {
        Manifests = manifests;
        LoadRules = loadRules;
        LoadDiagnostics = loadDiagnostics;
        StateSchemas = stateSchemas;
        SourceVirtualFileRules = sourceVirtualFileRules;
        SkippedVirtualFileRules = skippedVirtualFileRules;
        SourceRuntimeEventRules = sourceRuntimeEventRules;
        SkippedRuntimeEventRules = skippedRuntimeEventRules;
        SourceFactEventRules = sourceFactEventRules;
        SkippedFactEventRules = skippedFactEventRules;
        ContentReferenceReports = contentReferenceReports;
        EffectiveVirtualFileRules = effectiveVirtualFileRules;
        CompileIssues = compileIssues;
    }

    public IReadOnlyList<PatchManifestInfo> Manifests { get; }
    public IReadOnlyList<PluginLoadRule> LoadRules { get; }
    public IReadOnlyList<PluginLoadDiagnostic> LoadDiagnostics { get; }
    public IReadOnlyList<PluginStateSchemaSource> StateSchemas { get; }
    public IReadOnlyList<VirtualFileRuleSource> SourceVirtualFileRules { get; }
    public IReadOnlyList<VirtualFileRuleSkip> SkippedVirtualFileRules { get; }
    public IReadOnlyList<RuntimeEventRuleSource> SourceRuntimeEventRules { get; }
    public IReadOnlyList<RuntimeEventRuleSkip> SkippedRuntimeEventRules { get; }
    public IReadOnlyList<FactEventRuleSource> SourceFactEventRules { get; }
    public IReadOnlyList<FactEventRuleSkip> SkippedFactEventRules { get; }
    public IReadOnlyList<ContentReferenceValidationReport> ContentReferenceReports { get; }
    public IReadOnlyList<VirtualFileRule> EffectiveVirtualFileRules { get; }
    public IReadOnlyList<PatchCompileIssue> CompileIssues { get; }
    public bool HasCompileErrors => CompileIssues.Any(issue => issue.IsError);

    public void LogSummary(LauncherLog log)
    {
        log.Info($"Patch manifests discovered: {Manifests.Count}");
        foreach (var manifest in OrderedManifestsForDisplay())
        {
            log.Info(
                $"patch-manifest status={manifest.Status} order={manifest.LoadOrder} id={manifest.Id} " +
                $"name={manifest.Name} version={manifest.Version} phase={manifest.Phase} " +
                $"priority={manifest.Priority} capabilities={FormatLogList(manifest.Capabilities)} " +
                $"virtualRules={manifest.VirtualFileRuleCount} mapTemplates={manifest.MapTemplateRuleCount} " +
                $"mapLayoutTemplates={manifest.MapLayoutTemplateRuleCount} questChains={manifest.QuestChainRuleCount} " +
                $"contentRefs={manifest.ContentReferenceRuleCount} eventRules={manifest.EventRuleCount} " +
                $"factEventRules={manifest.FactEventRuleCount} path={manifest.Path}");
        }

        log.Info($"Content reference reports: {ContentReferenceReports.Count}");
        foreach (var report in ContentReferenceReports)
        {
            log.Info(
                $"content-ref-summary plugin={report.PluginId} refs={report.ReferenceCount} " +
                $"satisfied={report.SatisfiedCount} missingRequired={report.MissingRequiredCount} " +
                $"missingOptional={report.MissingOptionalCount} duplicateRefs={report.DuplicateReferenceCount} " +
                $"report={QuoteLogValue(report.ReportPath)}");
        }

        log.Info($"Enabled virtual file source rules: {SourceVirtualFileRules.Count}");
        foreach (var sourceRule in SourceVirtualFileRules)
        {
            log.Info(
                $"patch-source-rule source={sourceRule.SourceName} index={sourceRule.RuleIndex} " +
                $"target={sourceRule.Rule.Target} sourcePath={QuoteLogValue(sourceRule.Rule.SourcePath)} " +
                $"replacements={sourceRule.Rule.Replacements.Length} " +
                $"operations={sourceRule.Rule.Operations.Length} condition={QuoteLogValue(sourceRule.ConditionReason)}");
        }

        log.Info($"Skipped virtual file source rules: {SkippedVirtualFileRules.Count}");
        foreach (var skipped in SkippedVirtualFileRules)
        {
            log.Info(
                $"patch-source-rule-skipped source={skipped.SourceName} index={skipped.RuleIndex} " +
                $"target={skipped.Target} replacements={skipped.ReplacementCount} " +
                $"operations={skipped.OperationCount} reason={QuoteLogValue(skipped.Reason)}");
        }

        log.Info($"Effective virtual file rules: {EffectiveVirtualFileRules.Count}");
        foreach (var rule in EffectiveVirtualFileRules)
        {
            log.Info(
                $"patch-effective-rule target={rule.Target} sourcePath={QuoteLogValue(rule.SourcePath)} " +
                $"replacements={rule.Replacements.Length}");
        }
    }

    public void LogCompileIssues(LauncherLog log)
    {
        foreach (var issue in CompileIssues)
        {
            var message =
                $"patch-compile-issue severity={(issue.IsError ? "error" : "warning")} " +
                $"source={issue.SourceName} rule={issue.RuleIndex} operation={issue.OperationIndex} " +
                $"target={issue.Target} message={issue.Message}";

            if (issue.IsError)
            {
                log.Error(message);
            }
            else
            {
                log.Warn(message);
            }
        }
    }

    public void LogExplanation(LauncherLog log)
    {
        log.Info("Patch explanation started.");

        foreach (var manifest in OrderedManifestsForDisplay())
        {
            log.Info(
                $"patch-explain-manifest order={manifest.LoadOrder} status={manifest.Status} id={manifest.Id} " +
                $"name={manifest.Name} phase={manifest.Phase} priority={manifest.Priority} " +
                $"capabilities={FormatLogList(manifest.Capabilities)} virtualRules={manifest.VirtualFileRuleCount} " +
                $"mapTemplates={manifest.MapTemplateRuleCount} mapLayoutTemplates={manifest.MapLayoutTemplateRuleCount} " +
                $"questChains={manifest.QuestChainRuleCount} contentRefs={manifest.ContentReferenceRuleCount} " +
                $"eventRules={manifest.EventRuleCount} factEventRules={manifest.FactEventRuleCount} " +
                $"skipReason={QuoteLogValue(manifest.SkipReason)} path={manifest.Path}");
        }

        foreach (var rule in LoadRules)
        {
            log.Info(
                $"patch-explain-load-rule before={rule.BeforeId} after={rule.AfterId} " +
                $"reason={rule.Reason} reference={rule.Reference} beforePath={rule.BeforePath} afterPath={rule.AfterPath}");
        }

        foreach (var diagnostic in LoadDiagnostics)
        {
            log.Warn(
                $"patch-explain-load-diagnostic severity={diagnostic.Severity} code={diagnostic.Code} " +
                $"plugin={diagnostic.PluginId} related={diagnostic.RelatedId} message={QuoteLogValue(diagnostic.Message)}");
        }

        foreach (var report in ContentReferenceReports)
        {
            log.Info(
                $"patch-explain-content-refs plugin={report.PluginId} refs={report.ReferenceCount} " +
                $"satisfied={report.SatisfiedCount} missingRequired={report.MissingRequiredCount} " +
                $"missingOptional={report.MissingOptionalCount} catalogRoots={report.CatalogSourceRootCount} " +
                $"catalogEntries={report.CatalogEntryCount} duplicateRefs={report.DuplicateReferenceCount} " +
                $"report={QuoteLogValue(report.ReportPath)}");

            foreach (var reference in report.References)
            {
                var firstMatch = reference.Matches.FirstOrDefault();
                log.Info(
                    $"patch-explain-content-ref plugin={report.PluginId} status={reference.Status} " +
                    $"category={reference.Category} lookup={QuoteLogValue(reference.Lookup)} " +
                    $"provider={QuoteLogValue(reference.Provider)} required={reference.Required} " +
                    $"matches={reference.Matches.Count} candidates={reference.CandidateCount} " +
                    $"duplicates={reference.HasDuplicateCandidates} firstProvider={QuoteLogValue(firstMatch?.Provider ?? string.Empty)} " +
                    $"firstPath={QuoteLogValue(firstMatch?.SourcePath ?? string.Empty)} source={QuoteLogValue(reference.SourcePath)}");
            }
        }

        foreach (var group in SourceVirtualFileRules.GroupBy(rule => NormalizeTargetKey(rule.Rule.Target)).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var effective = EffectiveVirtualFileRules.FirstOrDefault(rule => NormalizeTargetKey(rule.Target).Equals(group.Key, StringComparison.OrdinalIgnoreCase));
            log.Info(
                $"patch-explain-target target={group.Key} sourceRules={group.Count()} " +
                $"effectiveReplacements={effective?.Replacements.Length ?? 0}");

            foreach (var source in group)
            {
                log.Info(
                    $"patch-explain-target-source target={group.Key} status=active source={source.SourceName} " +
                    $"rule={source.RuleIndex} sourcePath={QuoteLogValue(source.Rule.SourcePath)} " +
                    $"replacements={source.Rule.Replacements.Length} " +
                    $"operations={source.Rule.Operations.Length} reason={QuoteLogValue(source.ConditionReason)} path={source.SourcePath}");
            }
        }

        foreach (var skipped in SkippedVirtualFileRules.OrderBy(rule => NormalizeTargetKey(rule.Target), StringComparer.OrdinalIgnoreCase))
        {
            log.Info(
                $"patch-explain-target-source target={NormalizeTargetKey(skipped.Target)} status=skipped " +
                $"source={skipped.SourceName} rule={skipped.RuleIndex} replacements={skipped.ReplacementCount} " +
                $"operations={skipped.OperationCount} reason={QuoteLogValue(skipped.Reason)} path={skipped.SourcePath}");
        }

        foreach (var effectiveRule in EffectiveVirtualFileRules)
        {
            for (var i = 0; i < effectiveRule.Replacements.Length; i++)
            {
                var replacement = effectiveRule.Replacements[i];
                var origin = replacement.Origin ?? PatchReplacementOrigin.Unknown;
                log.Info(
                    $"patch-explain-replacement target={effectiveRule.Target} index={i} " +
                    $"source={origin.SourceName} rule={origin.RuleIndex} operation={origin.OperationIndex} " +
                    $"type={origin.OperationType} subject={QuoteLogValue(origin.Subject)} " +
                    $"findChars={replacement.Find.Length} replaceChars={replacement.Replace.Length}");
            }
        }

        log.Info("Patch explanation completed.");
    }

    public void LogRuleExplanation(LauncherLog log)
    {
        log.Info(
            $"Runtime rule explanation started. activeRules={SourceRuntimeEventRules.Count} " +
            $"skippedRules={SkippedRuntimeEventRules.Count}");

        foreach (var source in SourceRuntimeEventRules
                     .OrderBy(rule => rule.Rule.On, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(rule => PluginPhaseRank(rule.Rule.Phase))
                     .ThenBy(rule => rule.Rule.Priority)
                     .ThenBy(rule => rule.SourceName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(rule => rule.RuleIndex))
        {
            log.Info(
                $"runtime-rule status=active source={source.SourceName} rule={source.RuleIndex} " +
                $"id={QuoteLogValue(source.Rule.Id)} on={source.Rule.On} phase={source.Rule.Phase} " +
                $"priority={source.Rule.Priority} requires={FormatLogList(source.RequiredCapabilities)} " +
                $"actions={source.Rule.Actions.Length} actionCapabilities={FormatLogList(source.ActionCapabilities)} " +
                $"missingOptionalActionCapabilities={FormatLogList(source.MissingOptionalActionCapabilities)} " +
                $"reason={QuoteLogValue(source.Reason)} path={source.SourcePath}");

            for (var actionIndex = 0; actionIndex < source.Rule.Actions.Length; actionIndex++)
            {
                var action = source.Rule.Actions[actionIndex];
                log.Info(
                    $"runtime-rule-action source={source.SourceName} rule={source.RuleIndex} action={actionIndex} " +
                    $"type={action.Type} capability={action.Capability} risk={action.Risk} " +
                    $"required={action.Required} args={action.Args.Count}");
            }
        }

        foreach (var skipped in SkippedRuntimeEventRules
                     .OrderBy(rule => rule.SourceName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(rule => rule.RuleIndex))
        {
            log.Info(
                $"runtime-rule status=skipped source={skipped.SourceName} rule={skipped.RuleIndex} " +
                $"id={QuoteLogValue(skipped.RuleId)} on={QuoteLogValue(skipped.EventId)} " +
                $"reason={QuoteLogValue(skipped.Reason)} path={skipped.SourcePath}");
        }

        log.Info("Runtime rule explanation completed.");
    }

    public void LogFactEventRuleExplanation(LauncherLog log)
    {
        log.Info(
            $"Fact event rule explanation started. activeRules={SourceFactEventRules.Count} " +
            $"skippedRules={SkippedFactEventRules.Count}");

        foreach (var source in SourceFactEventRules
                     .OrderBy(rule => PluginPhaseRank(rule.Rule.Phase))
                     .ThenBy(rule => rule.Rule.Priority)
                     .ThenBy(rule => rule.SourceName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(rule => rule.RuleIndex))
        {
            log.Info(
                $"fact-event-rule status=active source={source.SourceName} rule={source.RuleIndex} " +
                $"id={QuoteLogValue(source.Rule.Id)} emit={source.Rule.Emit} phase={source.Rule.Phase} " +
                $"priority={source.Rule.Priority} requires={FormatLogList(source.RequiredCapabilities)} " +
                $"payloadKeys={FormatLogList(source.Rule.Payload.Keys)} reason={QuoteLogValue(source.Reason)} " +
                $"path={source.SourcePath}");
        }

        foreach (var skipped in SkippedFactEventRules
                     .OrderBy(rule => rule.SourceName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(rule => rule.RuleIndex))
        {
            log.Info(
                $"fact-event-rule status=skipped source={skipped.SourceName} rule={skipped.RuleIndex} " +
                $"id={QuoteLogValue(skipped.RuleId)} emit={QuoteLogValue(skipped.Emit)} " +
                $"reason={QuoteLogValue(skipped.Reason)} path={skipped.SourcePath}");
        }

        log.Info("Fact event rule explanation completed.");
    }

    private IOrderedEnumerable<PatchManifestInfo> OrderedManifestsForDisplay()
    {
        return Manifests
            .OrderBy(manifest => manifest.LoadOrder < 0 ? int.MaxValue : manifest.LoadOrder)
            .ThenBy(manifest => manifest.Id, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeTargetKey(string target)
    {
        return target.Trim().Replace('\\', '/').ToLowerInvariant();
    }

    private static string QuoteLogValue(string value)
    {
        return string.IsNullOrEmpty(value) ? "\"\"" : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string FormatLogList(IEnumerable<string> values)
    {
        var list = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return list.Length == 0 ? "[]" : "[" + string.Join(",", list) + "]";
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
}

internal sealed record PatchManifestInfo(
    string Name,
    string Id,
    string Version,
    string Path,
    string Status,
    bool Enabled,
    int VirtualFileRuleCount,
    int MapTemplateRuleCount,
    int MapLayoutTemplateRuleCount,
    int QuestChainRuleCount,
    int ContentReferenceRuleCount,
    int EventRuleCount,
    int FactEventRuleCount,
    string[] Capabilities,
    string Phase,
    int Priority,
    int LoadOrder,
    string SkipReason);

internal sealed record PluginLoadRule(
    string BeforeId,
    string BeforeName,
    string BeforePath,
    string AfterId,
    string AfterName,
    string AfterPath,
    string Reason,
    string Reference);

internal sealed record PluginLoadDiagnostic(
    string Severity,
    string Code,
    string PluginId,
    string RelatedId,
    string Message);

internal sealed record PluginStateSchemaSource(
    string PluginId,
    string SourceName,
    string SourcePath,
    int LoadOrder,
    IReadOnlyDictionary<string, JsonElement> StateSchema);

internal sealed class PluginLoadPlan
{
    public PluginLoadPlan(
        IReadOnlyList<PatchManifestInfo> manifests,
        IReadOnlyList<PluginLoadRule> loadRules,
        IReadOnlyList<PluginLoadDiagnostic> diagnostics,
        IReadOnlyList<PluginManifestCandidate> orderedEnabledPlugins)
    {
        Manifests = manifests;
        LoadRules = loadRules;
        Diagnostics = diagnostics;
        OrderedEnabledPlugins = orderedEnabledPlugins;
    }

    public IReadOnlyList<PatchManifestInfo> Manifests { get; }
    public IReadOnlyList<PluginLoadRule> LoadRules { get; }
    public IReadOnlyList<PluginLoadDiagnostic> Diagnostics { get; }
    public IReadOnlyList<PluginManifestCandidate> OrderedEnabledPlugins { get; }
}

internal sealed class PluginManifestCandidate
{
    public int DiscoveryIndex { get; init; }
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public PluginPatchManifest Manifest { get; init; } = new();
    public int LoadOrder { get; set; } = -1;
    public int VirtualFileRuleCount => Manifest.VirtualFileRules.Length;
    public int MapTemplateRuleCount => Manifest.MapTemplates.Length;
    public int MapLayoutTemplateRuleCount => Manifest.MapLayoutTemplates.Length;
    public int QuestChainRuleCount => Manifest.QuestChains.Length;
    public int ContentReferenceRuleCount => Manifest.ContentRefs.EnumerateRules().Count() + Manifest.Modules.ContentRefs.Length;
    public int EventRuleCount => Manifest.EventRules.Length;
    public int FactEventRuleCount => Manifest.FactEventRules.Length;

    public string SourceName => string.Equals(Name, Id, StringComparison.OrdinalIgnoreCase)
        ? Id
        : $"{Name} [{Id}]";
}

internal sealed class SequentialVirtualRuleBuilder
{
    public SequentialVirtualRuleBuilder(string target)
    {
        Target = target;
    }

    public string Target { get; }
    public string SourcePath { get; set; } = string.Empty;
    public List<VirtualFileReplacement> Replacements { get; } = [];
    public bool HasContent => !string.IsNullOrWhiteSpace(SourcePath) || Replacements.Count > 0;
}

internal sealed record VirtualFileRuleSource(
    string SourceName,
    string SourcePath,
    int RuleIndex,
    VirtualFileRule Rule,
    string ConditionReason);

internal sealed record VirtualFileRuleSkip(
    string SourceName,
    string SourcePath,
    int RuleIndex,
    string Target,
    int ReplacementCount,
    int OperationCount,
    string Reason);

internal sealed record RuntimeEventRuleSource(
    string PluginId,
    string SourceName,
    string SourcePath,
    int LoadOrder,
    int RuleIndex,
    RuntimeEventRule Rule,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> ActionCapabilities,
    IReadOnlyList<string> MissingOptionalActionCapabilities,
    string Reason);

internal sealed record RuntimeEventRuleSkip(
    string PluginId,
    string SourceName,
    string SourcePath,
    int LoadOrder,
    int RuleIndex,
    string RuleId,
    string EventId,
    string Reason);

internal sealed record FactEventRuleSource(
    string PluginId,
    string SourceName,
    string SourcePath,
    int LoadOrder,
    int RuleIndex,
    FactEventRule Rule,
    IReadOnlyList<string> RequiredCapabilities,
    string Reason);

internal sealed record FactEventRuleSkip(
    string PluginId,
    string SourceName,
    string SourcePath,
    int LoadOrder,
    int RuleIndex,
    string RuleId,
    string Emit,
    string Reason);

internal sealed record PatchConditionResult(bool Matched, string Reason);

internal sealed record PatchCompileIssue(
    bool IsError,
    string SourceName,
    string SourcePath,
    int RuleIndex,
    int OperationIndex,
    string Target,
    string Message);

internal sealed record TextLineSegment(string Text, string Eol)
{
    public string Raw => Text + Eol;
}

internal sealed class PatchPreviewResult
{
    public PatchPreviewResult(
        string target,
        string targetPath,
        string sourcePath,
        bool directSourceOverlay,
        string originalText,
        string virtualText,
        int originalBytes,
        int virtualBytes,
        int replacementAttempts,
        int replacementsApplied,
        IReadOnlyList<PatchReplacementApplication> applications,
        IReadOnlyList<string> warnings)
    {
        Target = target;
        TargetPath = targetPath;
        SourcePath = sourcePath;
        DirectSourceOverlay = directSourceOverlay;
        OriginalText = originalText;
        VirtualText = virtualText;
        OriginalBytes = originalBytes;
        VirtualBytes = virtualBytes;
        ReplacementAttempts = replacementAttempts;
        ReplacementsApplied = replacementsApplied;
        Applications = applications;
        Warnings = warnings;
    }

    public string Target { get; }
    public string TargetPath { get; }
    public string SourcePath { get; }
    public bool DirectSourceOverlay { get; }
    public string OriginalText { get; }
    public string VirtualText { get; }
    public int OriginalBytes { get; }
    public int VirtualBytes { get; }
    public int ReplacementAttempts { get; }
    public int ReplacementsApplied { get; }
    public IReadOnlyList<PatchReplacementApplication> Applications { get; }
    public IReadOnlyList<string> Warnings { get; }
}

internal sealed record PatchReplacementApplication(
    PatchReplacementOrigin Origin,
    int ReplacementIndex,
    int Matches,
    int FirstLine,
    string Before,
    string After);

internal sealed record PatchReplacementOrigin(
    string SourceName,
    string SourcePath,
    int RuleIndex,
    int ReplacementIndex,
    int OperationIndex,
    string OperationType,
    string Subject)
{
    public static PatchReplacementOrigin Unknown { get; } = new("unknown", string.Empty, -1, -1, -1, "unknown", "unknown");
}
