namespace DDRuntimeLoader;

internal sealed partial class RuntimeConfig
{
    public PatchPlan BuildPatchPlan(string projectRoot, LauncherLog log)
    {
        var sourceRules = new List<VirtualFileRuleSource>();
        var skippedRules = new List<VirtualFileRuleSkip>();
        var sourceRuntimeRules = new List<RuntimeEventRuleSource>();
        var skippedRuntimeRules = new List<RuntimeEventRuleSkip>();
        var sourceFactEventRules = new List<FactEventRuleSource>();
        var skippedFactEventRules = new List<FactEventRuleSkip>();
        var stateSchemas = new List<PluginStateSchemaSource>();
        var compileIssues = new List<PatchCompileIssue>();
        var manifestName = string.IsNullOrWhiteSpace(PluginPatchManifestName) ? "patches.json" : PluginPatchManifestName;
        var pluginCandidates = DiscoverPluginPatchManifests(projectRoot, manifestName, log).ToList();
        var loadPlan = BuildPluginLoadPlan(pluginCandidates, log);
        var activePluginIds = loadPlan.OrderedEnabledPlugins
            .Select(plugin => NormalizePluginId(plugin.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var activeCapabilities = loadPlan.OrderedEnabledPlugins
            .SelectMany(plugin => CleanCapabilityReferences(plugin.Manifest.Capabilities))
            .Select(NormalizeCapability)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AddVirtualRules(sourceRules, skippedRules, VirtualFileRules, "config virtualFileRules", string.Empty, activePluginIds, activeCapabilities);

        if (sourceRules.Count == 0 && !string.IsNullOrWhiteSpace(VirtualFileTarget) && !string.IsNullOrEmpty(VirtualFileFind))
        {
            AddVirtualRules(
                sourceRules,
                skippedRules,
                [
                    new VirtualFileRule
                    {
                        Target = VirtualFileTarget,
                        Replacements =
                        [
                            new VirtualFileReplacement
                            {
                                Find = VirtualFileFind,
                                Replace = VirtualFileReplace
                            }
                        ]
                    }
                ],
                "config legacy virtualFileTarget",
                string.Empty,
                activePluginIds,
                activeCapabilities);
        }

        foreach (var plugin in loadPlan.OrderedEnabledPlugins)
        {
            log.Info(
                $"Plugin patch manifest enabled: order={plugin.LoadOrder} id={plugin.Id} " +
                $"name={plugin.Name} phase={NormalizePhase(plugin.Manifest.Phase)} " +
                $"priority={plugin.Manifest.Priority} capabilities={FormatLogList(CleanCapabilityReferences(plugin.Manifest.Capabilities))} " +
                $"virtualRules={plugin.VirtualFileRuleCount} mapTemplates={plugin.MapTemplateRuleCount} eventRules={plugin.EventRuleCount} " +
                $"factEventRules={plugin.FactEventRuleCount} path={plugin.Path}");
            AddMapTemplateVirtualRules(
                projectRoot,
                sourceRules,
                skippedRules,
                compileIssues,
                plugin,
                activePluginIds,
                activeCapabilities,
                log);
            AddVirtualRules(sourceRules, skippedRules, plugin.Manifest.VirtualFileRules, plugin.SourceName, plugin.Path, activePluginIds, activeCapabilities);
            AddRuntimeEventRules(
                sourceRuntimeRules,
                skippedRuntimeRules,
                plugin.Manifest.EventRules,
                plugin.Id,
                plugin.SourceName,
                plugin.Path,
                plugin.LoadOrder,
                activeCapabilities);
            AddFactEventRules(
                sourceFactEventRules,
                skippedFactEventRules,
                plugin.Manifest.FactEventRules,
                plugin.Id,
                plugin.SourceName,
                plugin.Path,
                plugin.LoadOrder,
                activeCapabilities);
            if (plugin.Manifest.StateSchema.Count > 0)
            {
                stateSchemas.Add(new PluginStateSchemaSource(
                    plugin.Id,
                    plugin.SourceName,
                    plugin.Path,
                    plugin.LoadOrder,
                    plugin.Manifest.StateSchema));
            }
        }

        return new PatchPlan(
            loadPlan.Manifests,
            loadPlan.LoadRules,
            loadPlan.Diagnostics,
            stateSchemas,
            sourceRules,
            skippedRules,
            sourceRuntimeRules,
            skippedRuntimeRules,
            sourceFactEventRules,
            skippedFactEventRules,
            BuildEffectiveVirtualRules(projectRoot, sourceRules, compileIssues, log),
            compileIssues);
    }

    private IEnumerable<PluginManifestCandidate> DiscoverPluginPatchManifests(string projectRoot, string manifestName, LauncherLog log)
    {
        var discoveryIndex = 0;
        foreach (var configuredDirectory in PluginDirectories ?? [])
        {
            if (string.IsNullOrWhiteSpace(configuredDirectory))
            {
                continue;
            }

            var pluginRoot = ResolvePath(projectRoot, configuredDirectory);
            if (!Directory.Exists(pluginRoot))
            {
                log.Warn($"Plugin directory was not found: {pluginRoot}");
                continue;
            }

            foreach (var manifestPath in EnumeratePluginPatchManifests(pluginRoot, manifestName))
            {
                var manifest = PluginPatchManifest.Load(manifestPath);
                var fallbackId = Path.GetFileName(Path.GetDirectoryName(manifestPath)) ?? $"plugin-{discoveryIndex + 1}";
                var id = string.IsNullOrWhiteSpace(manifest.Id) ? fallbackId : manifest.Id.Trim();
                var displayName = string.IsNullOrWhiteSpace(manifest.Name)
                    ? Path.GetFileName(Path.GetDirectoryName(manifestPath)) ?? manifestPath
                    : manifest.Name;

                yield return new PluginManifestCandidate
                {
                    DiscoveryIndex = discoveryIndex++,
                    Id = id,
                    Name = displayName,
                    Path = manifestPath,
                    Manifest = manifest
                };
            }
        }
    }

    private void AddMapTemplateVirtualRules(
        string projectRoot,
        List<VirtualFileRuleSource> output,
        List<VirtualFileRuleSkip> skipped,
        List<PatchCompileIssue> compileIssues,
        PluginManifestCandidate plugin,
        IReadOnlySet<string> activePluginIds,
        IReadOnlySet<string> activeCapabilities,
        LauncherLog log)
    {
        var manifestDirectory = Path.GetDirectoryName(plugin.Path) ?? projectRoot;
        for (var index = 0; index < plugin.Manifest.MapTemplates.Length; index++)
        {
            var template = plugin.Manifest.MapTemplates[index];
            var ruleIndex = index + 1;
            if (string.IsNullOrWhiteSpace(template.Target))
            {
                AddCompileIssue(
                    compileIssues,
                    true,
                    plugin.SourceName,
                    plugin.Path,
                    ruleIndex,
                    0,
                    string.Empty,
                    "mapTemplates entry requires target");
                continue;
            }

            var condition = EvaluatePatchCondition(template.When, activePluginIds, activeCapabilities);
            if (!condition.Matched)
            {
                skipped.Add(new VirtualFileRuleSkip(
                    plugin.SourceName,
                    plugin.Path,
                    ruleIndex,
                    template.Target,
                    0,
                    0,
                    "mapTemplate " + condition.Reason));
                continue;
            }

            try
            {
                var sourcePath = ResolveMapTemplateSourcePath(projectRoot, manifestDirectory, template);
                var artifactDirectory = Path.Combine(ModStateDirectory, "_map_templates", SafeFileName(plugin.Id));
                var artifactBaseName = $"{ruleIndex:000}_{SafeFileName(string.IsNullOrWhiteSpace(template.Id) ? template.Target : template.Id)}";
                var outputPath = Path.Combine(artifactDirectory, artifactBaseName + ".dm");
                var reportPath = Path.Combine(artifactDirectory, artifactBaseName + ".report.json");
                var specPath = ResolveMapTemplateSpecPath(projectRoot, manifestDirectory, template, artifactDirectory, artifactBaseName, compileIssues, plugin, ruleIndex);
                if (string.IsNullOrWhiteSpace(specPath))
                {
                    continue;
                }

                SaveDirectoryWatcher.WriteMapTemplatePrototype(sourcePath, specPath, outputPath, reportPath, log);
                output.Add(new VirtualFileRuleSource(
                    plugin.SourceName,
                    plugin.Path,
                    ruleIndex,
                    new VirtualFileRule
                    {
                        Target = template.Target,
                        SourcePath = outputPath
                    },
                    condition.Reason + "; map template generated"));
                log.Info(
                    $"map-template-rule source={plugin.SourceName} rule={ruleIndex} id={QuoteLogValue(template.Id)} " +
                    $"target={template.Target} source={QuoteLogValue(sourcePath)} spec={QuoteLogValue(specPath)} " +
                    $"output={QuoteLogValue(outputPath)} report={QuoteLogValue(reportPath)}");
            }
            catch (Exception ex)
            {
                AddCompileIssue(
                    compileIssues,
                    true,
                    plugin.SourceName,
                    plugin.Path,
                    ruleIndex,
                    0,
                    template.Target,
                    $"map template failed: {ex.Message}");
            }
        }
    }

    private string ResolveMapTemplateSourcePath(string projectRoot, string manifestDirectory, MapTemplateRule template)
    {
        var source = string.IsNullOrWhiteSpace(template.Source) ? template.Target : template.Source;
        if (Path.IsPathRooted(source))
        {
            return ValidateMapTemplateSourcePath(projectRoot, Path.GetFullPath(source));
        }

        var relativeSource = source.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var gameCandidate = Path.GetFullPath(Path.Combine(GameWorkingDirectory, relativeSource));
        if (IsInsideDirectory(GameWorkingDirectory, gameCandidate) && File.Exists(gameCandidate))
        {
            return gameCandidate;
        }

        var pluginCandidate = Path.GetFullPath(Path.Combine(manifestDirectory, relativeSource));
        if (IsInsideDirectory(projectRoot, pluginCandidate) && File.Exists(pluginCandidate))
        {
            return pluginCandidate;
        }

        if (!IsInsideDirectory(GameWorkingDirectory, gameCandidate) && !IsInsideDirectory(projectRoot, pluginCandidate))
        {
            throw new InvalidOperationException($"map template source must stay inside the game directory or project root: {source}");
        }

        throw new FileNotFoundException($"Map template source file was not found. Tried: {gameCandidate}; {pluginCandidate}", source);
    }

    private string ValidateMapTemplateSourcePath(string projectRoot, string fullPath)
    {
        if (!IsInsideDirectory(GameWorkingDirectory, fullPath) && !IsInsideDirectory(projectRoot, fullPath))
        {
            throw new InvalidOperationException($"map template source must stay inside the game directory or project root: {fullPath}");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Map template source file was not found.", fullPath);
        }

        return fullPath;
    }

    private static string ResolveMapTemplateSpecPath(
        string projectRoot,
        string manifestDirectory,
        MapTemplateRule template,
        string artifactDirectory,
        string artifactBaseName,
        List<PatchCompileIssue> compileIssues,
        PluginManifestCandidate plugin,
        int ruleIndex)
    {
        var hasSpecPath = !string.IsNullOrWhiteSpace(template.SpecPath);
        var hasInlineSpec = template.Spec.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;
        if (hasSpecPath == hasInlineSpec)
        {
            AddCompileIssue(
                compileIssues,
                true,
                plugin.SourceName,
                plugin.Path,
                ruleIndex,
                0,
                template.Target,
                "mapTemplates entry requires exactly one of specPath or spec");
            return string.Empty;
        }

        if (hasSpecPath)
        {
            var specPath = Path.IsPathRooted(template.SpecPath)
                ? Path.GetFullPath(template.SpecPath)
                : Path.GetFullPath(Path.Combine(manifestDirectory, template.SpecPath));
            if (!IsInsideDirectory(projectRoot, specPath))
            {
                AddCompileIssue(
                    compileIssues,
                    true,
                    plugin.SourceName,
                    plugin.Path,
                    ruleIndex,
                    0,
                    template.Target,
                    $"map template specPath resolves outside project root: {specPath}");
                return string.Empty;
            }

            if (!File.Exists(specPath))
            {
                AddCompileIssue(
                    compileIssues,
                    true,
                    plugin.SourceName,
                    plugin.Path,
                    ruleIndex,
                    0,
                    template.Target,
                    $"map template specPath was not found: {specPath}");
                return string.Empty;
            }

            return specPath;
        }

        Directory.CreateDirectory(artifactDirectory);
        var inlineSpecPath = Path.Combine(artifactDirectory, artifactBaseName + ".spec.json");
        File.WriteAllText(inlineSpecPath, template.Spec.GetRawText(), Encoding.UTF8);
        return inlineSpecPath;
    }

    private static string SafeFileName(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "map_template" : value.Trim();
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = trimmed
            .Select(ch => invalid.Contains(ch) || ch is '/' or '\\' or ':' ? '_' : ch)
            .ToArray();
        var safe = new string(chars).Trim('.');
        return string.IsNullOrWhiteSpace(safe) ? "map_template" : safe;
    }

    private static IEnumerable<string> EnumeratePluginPatchManifests(string pluginRoot, string manifestName)
    {
        var rootManifest = Path.Combine(pluginRoot, manifestName);
        if (File.Exists(rootManifest))
        {
            yield return rootManifest;
        }

        foreach (var pluginDirectory in Directory.EnumerateDirectories(pluginRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(pluginDirectory, manifestName);
            if (File.Exists(manifestPath))
            {
                yield return manifestPath;
            }
        }
    }

    private static PluginLoadPlan BuildPluginLoadPlan(IReadOnlyList<PluginManifestCandidate> candidates, LauncherLog log)
    {
        var diagnostics = new List<PluginLoadDiagnostic>();
        var loadRules = new List<PluginLoadRule>();
        var enabled = candidates.Where(candidate => candidate.Manifest.Enabled).ToList();
        AddDuplicatePluginIdDiagnostics(enabled, diagnostics, log);

        var missingRequiredDependencies = new Dictionary<PluginManifestCandidate, string[]>();
        var changed = true;
        while (changed)
        {
            changed = false;
            var availableById = enabled
                .Where(candidate => !missingRequiredDependencies.ContainsKey(candidate))
                .GroupBy(candidate => NormalizePluginId(candidate.Id), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in enabled.Where(candidate => !missingRequiredDependencies.ContainsKey(candidate)))
            {
                var missing = CleanModReferences(candidate.Manifest.Depends)
                    .Where(dependency => !availableById.ContainsKey(NormalizePluginId(dependency)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (missing.Length == 0)
                {
                    continue;
                }

                missingRequiredDependencies[candidate] = missing;
                changed = true;
            }
        }

        foreach (var (candidate, missing) in missingRequiredDependencies)
        {
            diagnostics.Add(new PluginLoadDiagnostic(
                "warning",
                "missing-dependency",
                candidate.Id,
                string.Join(",", missing),
                $"required dependencies missing: {string.Join(",", missing)}"));
            log.Warn(
                $"Plugin skipped because required dependencies are missing: id={candidate.Id} " +
                $"name={candidate.Name} missing={string.Join(",", missing)} path={candidate.Path}");
        }

        var active = enabled
            .Where(candidate => !missingRequiredDependencies.ContainsKey(candidate))
            .ToList();

        var ordered = SortPluginLoadOrder(active, log, loadRules, diagnostics);
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].LoadOrder = i;
        }

        var orderByCandidate = ordered.ToDictionary(candidate => candidate, candidate => candidate.LoadOrder);
        var manifests = candidates.Select(candidate =>
        {
            var status = "disabled";
            var loadOrder = -1;
            var skipReason = string.Empty;
            if (candidate.Manifest.Enabled)
            {
                if (missingRequiredDependencies.ContainsKey(candidate))
                {
                    status = "skipped-missing-dependency";
                    skipReason = "missing dependencies: " + string.Join(",", missingRequiredDependencies[candidate]);
                }
                else
                {
                    status = "enabled";
                    loadOrder = orderByCandidate.TryGetValue(candidate, out var order) ? order : -1;
                }
            }

            return new PatchManifestInfo(
                candidate.Name,
                candidate.Id,
                candidate.Manifest.Version,
                candidate.Path,
                status,
                candidate.Manifest.Enabled,
                candidate.VirtualFileRuleCount,
                candidate.MapTemplateRuleCount,
                candidate.EventRuleCount,
                candidate.FactEventRuleCount,
                CleanCapabilityReferences(candidate.Manifest.Capabilities).ToArray(),
                NormalizePhase(candidate.Manifest.Phase),
                candidate.Manifest.Priority,
                loadOrder,
                skipReason);
        }).ToArray();

        WarnDeclaredConflicts(active, diagnostics, log);
        return new PluginLoadPlan(manifests, loadRules, diagnostics, ordered);
    }

    private static IReadOnlyList<PluginManifestCandidate> SortPluginLoadOrder(
        IReadOnlyList<PluginManifestCandidate> active,
        LauncherLog log,
        List<PluginLoadRule> loadRules,
        List<PluginLoadDiagnostic> diagnostics)
    {
        var byId = active
            .GroupBy(candidate => NormalizePluginId(candidate.Id), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var edges = active.ToDictionary(
            candidate => candidate,
            _ => new HashSet<PluginManifestCandidate>());
        var indegree = active.ToDictionary(candidate => candidate, _ => 0);

        void AddEdge(PluginManifestCandidate before, PluginManifestCandidate after, string reason, string reference)
        {
            if (ReferenceEquals(before, after) || !edges[before].Add(after))
            {
                return;
            }

            indegree[after]++;
            loadRules.Add(new PluginLoadRule(
                before.Id,
                before.Name,
                before.Path,
                after.Id,
                after.Name,
                after.Path,
                reason,
                reference));
        }

        void AddAfterEdges(PluginManifestCandidate candidate, IEnumerable<string> references, string reason)
        {
            foreach (var reference in CleanModReferences(references))
            {
                if (!byId.TryGetValue(NormalizePluginId(reference), out var dependencies))
                {
                    continue;
                }

                foreach (var dependency in dependencies)
                {
                    AddEdge(dependency, candidate, reason, reference);
                }
            }
        }

        foreach (var candidate in active)
        {
            AddAfterEdges(candidate, candidate.Manifest.Depends, "depends");
            AddAfterEdges(candidate, candidate.Manifest.OptionalDepends, "optionalDepends");
            AddAfterEdges(candidate, candidate.Manifest.LoadAfter, "loadAfter");

            foreach (var reference in CleanModReferences(candidate.Manifest.LoadBefore))
            {
                if (!byId.TryGetValue(NormalizePluginId(reference), out var targets))
                {
                    continue;
                }

                foreach (var target in targets)
                {
                    AddEdge(candidate, target, "loadBefore", reference);
                }
            }
        }

        var result = new List<PluginManifestCandidate>();
        var remaining = new HashSet<PluginManifestCandidate>(active);
        while (remaining.Count > 0)
        {
            var next = OrderPluginsByBaseRules(remaining.Where(candidate => indegree[candidate] == 0)).FirstOrDefault();
            if (next is null)
            {
                next = OrderPluginsByBaseRules(remaining).First();
                diagnostics.Add(new PluginLoadDiagnostic(
                    "warning",
                    "load-cycle",
                    next.Id,
                    string.Empty,
                    "load order cycle detected; stable fallback was used"));
                log.Warn(
                    $"Plugin load order cycle detected. Using stable fallback: id={next.Id} " +
                    $"name={next.Name} path={next.Path}");
            }

            remaining.Remove(next);
            result.Add(next);
            foreach (var after in edges[next])
            {
                indegree[after]--;
            }
        }

        return result;
    }

    private List<VirtualFileRule> BuildEffectiveVirtualRules(
        string projectRoot,
        IReadOnlyList<VirtualFileRuleSource> sourceRules,
        List<PatchCompileIssue> compileIssues,
        LauncherLog log)
    {
        var builders = new List<SequentialVirtualRuleBuilder>();
        var builderByTarget = new Dictionary<string, SequentialVirtualRuleBuilder>(StringComparer.OrdinalIgnoreCase);
        var currentTextByTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceRule in sourceRules)
        {
            var rule = sourceRule.Rule;
            var targetKey = NormalizeVirtualTargetKey(rule.Target);
            var targetPath = ResolveVirtualTargetPath(rule.Target);
            if (!IsInsideDirectory(GameWorkingDirectory, targetPath))
            {
                AddCompileIssue(
                    compileIssues,
                    true,
                    sourceRule.SourceName,
                    sourceRule.SourcePath,
                    sourceRule.RuleIndex,
                    0,
                    rule.Target,
                    $"patch target resolves outside game working directory: {targetPath}");
                continue;
            }

            if (!builderByTarget.TryGetValue(targetKey, out var builder))
            {
                builder = new SequentialVirtualRuleBuilder(rule.Target);
                builderByTarget[targetKey] = builder;
                builders.Add(builder);
            }

            if (!string.IsNullOrWhiteSpace(rule.SourcePath))
            {
                ApplySourcePathOverlay(projectRoot, sourceRule, builder, compileIssues);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(builder.SourcePath))
            {
                AddCompileIssue(
                    compileIssues,
                    true,
                    sourceRule.SourceName,
                    sourceRule.SourcePath,
                    sourceRule.RuleIndex,
                    0,
                    rule.Target,
                    "text replacements cannot be merged after a direct sourcePath virtual file overlay");
                continue;
            }

            ApplyRawReplacements(sourceRule, builder, currentTextByTarget, targetKey, compileIssues);

            if ((rule.Operations ?? []).Length == 0)
            {
                continue;
            }

            if (!TryGetCurrentVirtualText(sourceRule, targetKey, true, currentTextByTarget, compileIssues, out var currentText))
            {
                continue;
            }

            var compiled = CompileVirtualFileOperations(
                rule,
                currentText,
                sourceRule.SourceName,
                sourceRule.SourcePath,
                sourceRule.RuleIndex,
                compileIssues,
                log,
                out var updatedText);

            builder.Replacements.AddRange(compiled);
            currentTextByTarget[targetKey] = updatedText;
        }

        return builders
            .Where(builder => builder.HasContent)
            .Select(builder => new VirtualFileRule
            {
                Target = builder.Target,
                SourcePath = builder.SourcePath,
                Replacements = builder.Replacements.ToArray()
            })
            .ToList();
    }

    private void ApplySourcePathOverlay(
        string projectRoot,
        VirtualFileRuleSource sourceRule,
        SequentialVirtualRuleBuilder builder,
        List<PatchCompileIssue> compileIssues)
    {
        var rule = sourceRule.Rule;
        if ((rule.Replacements ?? []).Any(replacement => !string.IsNullOrEmpty(replacement.Find)) ||
            (rule.Operations ?? []).Length > 0)
        {
            AddCompileIssue(
                compileIssues,
                true,
                sourceRule.SourceName,
                sourceRule.SourcePath,
                sourceRule.RuleIndex,
                0,
                rule.Target,
                "sourcePath overlays currently replace the whole virtual file and cannot include replacements or operations");
            return;
        }

        if (!string.IsNullOrWhiteSpace(builder.SourcePath) || builder.Replacements.Count > 0)
        {
            AddCompileIssue(
                compileIssues,
                true,
                sourceRule.SourceName,
                sourceRule.SourcePath,
                sourceRule.RuleIndex,
                0,
                rule.Target,
                "multiple virtual file content sources target the same file");
            return;
        }

        var sourcePath = ResolvePath(projectRoot, rule.SourcePath);
        if (!IsInsideDirectory(projectRoot, sourcePath))
        {
            AddCompileIssue(
                compileIssues,
                true,
                sourceRule.SourceName,
                sourceRule.SourcePath,
                sourceRule.RuleIndex,
                0,
                rule.Target,
                $"sourcePath resolves outside project root: {sourcePath}");
            return;
        }

        if (!File.Exists(sourcePath))
        {
            AddCompileIssue(
                compileIssues,
                true,
                sourceRule.SourceName,
                sourceRule.SourcePath,
                sourceRule.RuleIndex,
                0,
                rule.Target,
                $"sourcePath file was not found: {sourcePath}");
            return;
        }

        builder.SourcePath = sourcePath;
    }

    private void ApplyRawReplacements(
        VirtualFileRuleSource sourceRule,
        SequentialVirtualRuleBuilder builder,
        Dictionary<string, string> currentTextByTarget,
        string targetKey,
        List<PatchCompileIssue> compileIssues)
    {
        var replacementIndex = 0;
        foreach (var replacement in sourceRule.Rule.Replacements ?? [])
        {
            if (string.IsNullOrEmpty(replacement.Find))
            {
                replacementIndex++;
                continue;
            }

            var withOrigin = WithOrigin(
                replacement,
                new PatchReplacementOrigin(sourceRule.SourceName, sourceRule.SourcePath, sourceRule.RuleIndex, replacementIndex, -1, "replacement", "replacement"));
            builder.Replacements.Add(withOrigin);

            if (TryGetCurrentVirtualText(sourceRule, targetKey, false, currentTextByTarget, compileIssues, out var currentText))
            {
                currentTextByTarget[targetKey] = ReplaceAllText(currentText, withOrigin.Find, withOrigin.Replace, out var applied);
                if (applied == 0)
                {
                    AddCompileIssue(
                        compileIssues,
                        false,
                        sourceRule.SourceName,
                        sourceRule.SourcePath,
                        sourceRule.RuleIndex,
                        -1,
                        sourceRule.Rule.Target,
                        "replacement text was not found in current virtual text");
                }
            }

            replacementIndex++;
        }
    }

    private bool TryGetCurrentVirtualText(
        VirtualFileRuleSource sourceRule,
        string targetKey,
        bool requireExistingFile,
        Dictionary<string, string> currentTextByTarget,
        List<PatchCompileIssue> compileIssues,
        out string currentText)
    {
        if (currentTextByTarget.TryGetValue(targetKey, out currentText!))
        {
            return true;
        }

        var targetPath = ResolveVirtualTargetPath(sourceRule.Rule.Target);
        if (!File.Exists(targetPath))
        {
            if (requireExistingFile)
            {
                AddCompileIssue(
                    compileIssues,
                    false,
                    sourceRule.SourceName,
                    sourceRule.SourcePath,
                    sourceRule.RuleIndex,
                    0,
                    sourceRule.Rule.Target,
                    $"operation target file was not found: {targetPath}");
            }

            currentText = string.Empty;
            return false;
        }

        try
        {
            currentText = File.ReadAllText(targetPath, Encoding.UTF8);
            currentTextByTarget[targetKey] = currentText;
            return true;
        }
        catch (Exception ex)
        {
            AddCompileIssue(
                compileIssues,
                true,
                sourceRule.SourceName,
                sourceRule.SourcePath,
                sourceRule.RuleIndex,
                0,
                sourceRule.Rule.Target,
                $"patch target could not be read: {ex.Message}");
            currentText = string.Empty;
            return false;
        }
    }

    private static void AddDuplicatePluginIdDiagnostics(
        IReadOnlyList<PluginManifestCandidate> enabled,
        List<PluginLoadDiagnostic> diagnostics,
        LauncherLog log)
    {
        foreach (var group in enabled.GroupBy(candidate => NormalizePluginId(candidate.Id)).Where(group => group.Count() > 1))
        {
            var instances = string.Join(" | ", group.Select(candidate => $"{candidate.Name}@{candidate.Path}"));
            diagnostics.Add(new PluginLoadDiagnostic(
                "warning",
                "duplicate-id",
                group.First().Id,
                string.Empty,
                $"duplicate enabled plugin id; instances={instances}"));
            log.Warn($"Duplicate plugin id enabled. Keeping all instances in load order: id={group.First().Id} instances={instances}");
        }
    }

    private static void WarnDeclaredConflicts(
        IReadOnlyList<PluginManifestCandidate> active,
        List<PluginLoadDiagnostic> diagnostics,
        LauncherLog log)
    {
        var activeById = active
            .GroupBy(candidate => NormalizePluginId(candidate.Id), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in active)
        {
            foreach (var conflict in CleanModReferences(candidate.Manifest.Conflicts))
            {
                if (!activeById.TryGetValue(NormalizePluginId(conflict), out var conflicts))
                {
                    continue;
                }

                foreach (var other in conflicts)
                {
                    if (ReferenceEquals(candidate, other))
                    {
                        continue;
                    }

                    var pair = new[] { candidate.Path, other.Path }.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
                    var key = $"{pair[0]}|{pair[1]}";
                    if (!reported.Add(key))
                    {
                        continue;
                    }

                    diagnostics.Add(new PluginLoadDiagnostic(
                        "warning",
                        "declared-conflict",
                        candidate.Id,
                        other.Id,
                        "declared conflict is active but not blocking startup"));
                    log.Warn(
                        $"Plugin conflict declared but not blocking startup: source={candidate.Id} " +
                        $"conflict={other.Id} sourcePath={candidate.Path} conflictPath={other.Path}");
                }
            }
        }
    }

    private static IOrderedEnumerable<PluginManifestCandidate> OrderPluginsByBaseRules(IEnumerable<PluginManifestCandidate> candidates)
    {
        return candidates
            .OrderBy(candidate => PluginPhaseRank(candidate.Manifest.Phase))
            .ThenBy(candidate => candidate.Manifest.Priority)
            .ThenBy(candidate => candidate.DiscoveryIndex)
            .ThenBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> CleanModReferences(IEnumerable<string>? references)
    {
        return (references ?? [])
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => reference.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> CleanCapabilityReferences(IEnumerable<string>? references)
    {
        return (references ?? [])
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => NormalizeCapability(reference))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizePluginId(string id)
    {
        return id.Trim().ToLowerInvariant();
    }

    private static string NormalizeCapability(string capability)
    {
        return capability.Trim().ToLowerInvariant();
    }

    private static string FormatLogList(IEnumerable<string> values)
    {
        var list = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return list.Length == 0 ? "[]" : "[" + string.Join(",", list) + "]";
    }

    private static string QuoteLogValue(string value)
    {
        return string.IsNullOrEmpty(value) ? "\"\"" : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string NormalizePhase(string phase)
    {
        var normalized = string.IsNullOrWhiteSpace(phase) ? "normal" : phase.Trim().ToLowerInvariant();
        return normalized is "base" or "early" or "normal" or "compat" or "late" ? normalized : "normal";
    }

    private static int PluginPhaseRank(string phase)
    {
        return (string.IsNullOrWhiteSpace(phase) ? "normal" : phase.Trim().ToLowerInvariant()) switch
        {
            "base" => 0,
            "early" => 100,
            "normal" => 200,
            "compat" => 300,
            "late" => 400,
            _ => 200
        };
    }

}
