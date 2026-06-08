namespace DDRuntimeLoader;

internal sealed class PatchPlan
{
    public PatchPlan(
        IReadOnlyList<PatchManifestInfo> manifests,
        IReadOnlyList<PluginLoadRule> loadRules,
        IReadOnlyList<PluginLoadDiagnostic> loadDiagnostics,
        IReadOnlyList<VirtualFileRuleSource> sourceVirtualFileRules,
        IReadOnlyList<VirtualFileRuleSkip> skippedVirtualFileRules,
        IReadOnlyList<VirtualFileRule> effectiveVirtualFileRules,
        IReadOnlyList<PatchCompileIssue> compileIssues)
    {
        Manifests = manifests;
        LoadRules = loadRules;
        LoadDiagnostics = loadDiagnostics;
        SourceVirtualFileRules = sourceVirtualFileRules;
        SkippedVirtualFileRules = skippedVirtualFileRules;
        EffectiveVirtualFileRules = effectiveVirtualFileRules;
        CompileIssues = compileIssues;
    }

    public IReadOnlyList<PatchManifestInfo> Manifests { get; }
    public IReadOnlyList<PluginLoadRule> LoadRules { get; }
    public IReadOnlyList<PluginLoadDiagnostic> LoadDiagnostics { get; }
    public IReadOnlyList<VirtualFileRuleSource> SourceVirtualFileRules { get; }
    public IReadOnlyList<VirtualFileRuleSkip> SkippedVirtualFileRules { get; }
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
                $"virtualRules={manifest.VirtualFileRuleCount} eventRules={manifest.EventRuleCount} path={manifest.Path}");
        }

        log.Info($"Enabled virtual file source rules: {SourceVirtualFileRules.Count}");
        foreach (var sourceRule in SourceVirtualFileRules)
        {
            log.Info(
                $"patch-source-rule source={sourceRule.SourceName} index={sourceRule.RuleIndex} " +
                $"target={sourceRule.Rule.Target} replacements={sourceRule.Rule.Replacements.Length} " +
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
            log.Info($"patch-effective-rule target={rule.Target} replacements={rule.Replacements.Length}");
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
                $"eventRules={manifest.EventRuleCount} " +
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
                    $"rule={source.RuleIndex} replacements={source.Rule.Replacements.Length} " +
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
}

internal sealed record PatchManifestInfo(
    string Name,
    string Id,
    string Version,
    string Path,
    string Status,
    bool Enabled,
    int VirtualFileRuleCount,
    int EventRuleCount,
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
    public int EventRuleCount => Manifest.EventRules.Length;

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
    public List<VirtualFileReplacement> Replacements { get; } = [];
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
