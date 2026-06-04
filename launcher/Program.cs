using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DDRuntimeLoader;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var options = LauncherOptions.Parse(args);
            var configPath = options.ConfigPath ?? FindConfigPath();
            var projectRoot = GetProjectRoot(configPath);
            var config = RuntimeConfig.Load(configPath);
            config.ApplyOverrides(options);
            config.ResolvePaths(projectRoot);

            using var log = LauncherLog.Open(config.LogDirectory);
            log.Info("DDRuntimeLoader started");
            log.Info($"Config: {configPath}");
            log.Info($"Project root: {projectRoot}");
            log.Info($"Game executable: {config.GameExecutablePath}");
            log.Info($"Game working directory: {config.GameWorkingDirectory}");
            log.Info($"Runtime DLL: {config.RuntimeDllPath}");
            log.Info($"Injection enabled: {config.EnableInjection && !options.NoInject}");
            log.Info($"Start suspended for injection: {config.StartSuspendedForInjection && config.EnableInjection && !options.NoInject}");

            ValidateConfig(config, options, log);
            log.Info($"Game SHA-256: {ComputeSha256(config.GameExecutablePath)}");
            log.Info($"Game architecture: {PeArchitecture.Read(config.GameExecutablePath)}");
            log.Info($"Launcher architecture: {(Environment.Is64BitProcess ? "x64" : "x86")}");

            var patchPlan = config.BuildPatchPlan(projectRoot, log);
            if (patchPlan.CompileIssues.Count > 0)
            {
                patchPlan.LogCompileIssues(log);
                if ((patchPlan.HasCompileErrors || options.StrictPatches) && !options.ListPatches && !options.ExplainPatches)
                {
                    return 3;
                }
            }

            if (options.ListPatches)
            {
                patchPlan.LogSummary(log);
            }

            if (options.ExplainPatches)
            {
                patchPlan.LogExplanation(log);
            }

            if (options.ValidatePatches || options.ValidateOnly)
            {
                var validation = PatchValidator.Validate(config, patchPlan, log, options.StrictPatches);
                if (!validation.Succeeded)
                {
                    return 3;
                }
            }

            if (options.PreviewPatches)
            {
                var previewOutput = ResolvePreviewOutputPath(projectRoot, options.PreviewOutputPath ?? Path.Combine(config.LogDirectory, "patch_preview"));
                PatchPreviewer.WritePreview(config, patchPlan, previewOutput, log);
            }

            if (options.ListPatches || options.ExplainPatches || options.ValidateOnly || options.PreviewPatches)
            {
                log.Info("Patch inspection requested. No process was started.");
                return 0;
            }

            var runtimeEnvironment = config.BuildRuntimeEnvironment(projectRoot, patchPlan);

            if (options.DryRun)
            {
                log.Info("Dry run requested. No process was started.");
                return 0;
            }

            if (config.EnableInjection && !options.NoInject && config.StartSuspendedForInjection)
            {
                using var environmentScope = ProcessEnvironmentScope.Apply(runtimeEnvironment);
                using var suspendedProcess = SuspendedProcess.Start(config.GameExecutablePath, config.GameWorkingDirectory);
                log.Info($"Game process started suspended. PID={suspendedProcess.ProcessId}");

                try
                {
                    DllInjector.Inject(suspendedProcess.ProcessId, config.RuntimeDllPath);
                    log.Info("Runtime DLL injection completed.");
                    suspendedProcess.Resume();
                    log.Info("Game process resumed after injection.");
                }
                catch (Exception ex)
                {
                    log.Error($"Runtime DLL injection failed: {ex.Message}");
                    if (config.KillGameOnInjectionFailure)
                    {
                        log.Error("killGameOnInjectionFailure is enabled. Terminating game process.");
                        suspendedProcess.Terminate(exitCode: 2);
                    }
                    else
                    {
                        log.Warn("Resuming game process without RuntimeHook because killGameOnInjectionFailure is disabled.");
                        suspendedProcess.Resume();
                    }
                    return 2;
                }
            }
            else
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = config.GameExecutablePath,
                    WorkingDirectory = config.GameWorkingDirectory,
                    UseShellExecute = false
                };
                foreach (var (key, value) in runtimeEnvironment)
                {
                    startInfo.Environment[key] = value;
                }

                using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start game process.");
                log.Info($"Game process started. PID={process.Id}");

                if (config.EnableInjection && !options.NoInject)
                {
                    try
                    {
                        DllInjector.Inject(process.Id, config.RuntimeDllPath);
                        log.Info("Runtime DLL injection completed.");
                    }
                    catch (Exception ex)
                    {
                        log.Error($"Runtime DLL injection failed: {ex.Message}");
                        if (config.KillGameOnInjectionFailure && !process.HasExited)
                        {
                            log.Error("killGameOnInjectionFailure is enabled. Terminating game process.");
                            process.Kill(entireProcessTree: true);
                        }
                        return 2;
                    }
                }
                else
                {
                    log.Info("Injection skipped by configuration or command line.");
                }
            }

            log.Info("Launcher finished its startup work. Game process remains independent.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string FindConfigPath()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var config = Path.Combine(dir.FullName, "config", "config.json");
                if (File.Exists(config)) return config;

                var defaultConfig = Path.Combine(dir.FullName, "config", "default_config.json");
                if (File.Exists(defaultConfig)) return defaultConfig;

                dir = dir.Parent;
            }
        }

        throw new FileNotFoundException("Could not find config/config.json or config/default_config.json.");
    }

    private static string GetProjectRoot(string configPath)
    {
        var configDir = Directory.GetParent(Path.GetFullPath(configPath));
        return configDir?.Parent?.FullName ?? Directory.GetCurrentDirectory();
    }

    private static void ValidateConfig(RuntimeConfig config, LauncherOptions options, LauncherLog log)
    {
        if (!File.Exists(config.GameExecutablePath))
            throw new FileNotFoundException("Game executable was not found.", config.GameExecutablePath);

        if (!Directory.Exists(config.GameWorkingDirectory))
            throw new DirectoryNotFoundException($"Game working directory was not found: {config.GameWorkingDirectory}");

        var willStartGame = !options.DryRun && !options.ListPatches && !options.ExplainPatches && !options.ValidateOnly && !options.PreviewPatches;
        if (willStartGame && config.EnableInjection && !options.NoInject && !File.Exists(config.RuntimeDllPath))
            throw new FileNotFoundException("Runtime DLL was not found. Build runtime/RuntimeHook.vcxproj first.", config.RuntimeDllPath);

        var gameArch = PeArchitecture.Read(config.GameExecutablePath);
        if (gameArch == "x64" && !Environment.Is64BitProcess)
            throw new InvalidOperationException("x64 game requires x64 launcher and x64 RuntimeHook.dll.");

        if (gameArch == "x86" && Environment.Is64BitProcess)
            log.Warn("x86 game detected. This skeleton is configured for x64; use a matching x86 launcher and DLL before injecting.");
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ResolvePreviewOutputPath(string projectRoot, string path)
    {
        var fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path));
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Preview output must stay inside project root: {fullPath}");
        }

        return fullPath;
    }
}

internal sealed class RuntimeConfig
{
    [JsonPropertyName("gameExecutablePath")]
    public string GameExecutablePath { get; set; } = string.Empty;

    [JsonPropertyName("gameWorkingDirectory")]
    public string GameWorkingDirectory { get; set; } = string.Empty;

    [JsonPropertyName("runtimeDllPath")]
    public string RuntimeDllPath { get; set; } = string.Empty;

    [JsonPropertyName("logDirectory")]
    public string LogDirectory { get; set; } = string.Empty;

    [JsonPropertyName("enableInjection")]
    public bool EnableInjection { get; set; } = true;

    [JsonPropertyName("killGameOnInjectionFailure")]
    public bool KillGameOnInjectionFailure { get; set; }

    [JsonPropertyName("startSuspendedForInjection")]
    public bool StartSuspendedForInjection { get; set; } = true;

    [JsonPropertyName("fileIoObserveOnly")]
    public bool FileIoObserveOnly { get; set; } = true;

    [JsonPropertyName("fileIoLogExtensions")]
    public string[] FileIoLogExtensions { get; set; } =
    [
        ".darkest",
        ".loc",
        ".json",
        ".xml",
        ".png",
        ".atlas",
        ".skel",
        ".font",
        ".ttf",
        ".otf",
        ".shader",
        ".txt"
    ];

    [JsonPropertyName("fileIoMaxLogEntries")]
    public int FileIoMaxLogEntries { get; set; } = 2000;

    [JsonPropertyName("fileIoDeduplicate")]
    public bool FileIoDeduplicate { get; set; } = true;

    [JsonPropertyName("pluginDirectories")]
    public string[] PluginDirectories { get; set; } = ["./plugins"];

    [JsonPropertyName("pluginPatchManifestName")]
    public string PluginPatchManifestName { get; set; } = "patches.json";

    public static RuntimeConfig Load(string path)
    {
        var json = File.ReadAllText(path, Encoding.UTF8);
        return JsonSerializer.Deserialize<RuntimeConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? throw new InvalidOperationException("Config file is empty or invalid.");
    }

    public void ApplyOverrides(LauncherOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.GameExecutablePath)) GameExecutablePath = options.GameExecutablePath;
        if (!string.IsNullOrWhiteSpace(options.RuntimeDllPath)) RuntimeDllPath = options.RuntimeDllPath;
        if (options.NoInject) EnableInjection = false;
    }

    public void ResolvePaths(string projectRoot)
    {
        GameExecutablePath = ResolvePath(projectRoot, GameExecutablePath);
        GameWorkingDirectory = ResolvePath(projectRoot, GameWorkingDirectory);
        RuntimeDllPath = ResolvePath(projectRoot, RuntimeDllPath);
        LogDirectory = ResolvePath(projectRoot, LogDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    private static string ResolvePath(string basePath, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(basePath, path));
    }

    public Dictionary<string, string> BuildRuntimeEnvironment(string projectRoot, PatchPlan patchPlan)
    {
        var values = new Dictionary<string, string>
        {
            ["DD_RUNTIME_FRAMEWORK_ROOT"] = projectRoot,
            ["DD_RUNTIME_LOG_DIR"] = LogDirectory,
            ["DD_RUNTIME_FILE_IO_OBSERVE_ONLY"] = FileIoObserveOnly ? "1" : "0",
            ["DD_RUNTIME_FILE_IO_LOG_EXTENSIONS"] = string.Join(';', FileIoLogExtensions),
            ["DD_RUNTIME_FILE_IO_MAX_ENTRIES"] = FileIoMaxLogEntries.ToString(),
            ["DD_RUNTIME_FILE_IO_DEDUPLICATE"] = FileIoDeduplicate ? "1" : "0",
            ["DD_RUNTIME_VIRTUAL_FILE_ENABLED"] = VirtualFileEnabled ? "1" : "0"
        };

        var rules = patchPlan.EffectiveVirtualFileRules;
        values["DD_RUNTIME_VIRTUAL_RULE_COUNT"] = rules.Count.ToString();
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            values[$"DD_RUNTIME_VIRTUAL_RULE_{i}_TARGET"] = rule.Target;
            values[$"DD_RUNTIME_VIRTUAL_RULE_{i}_REPLACEMENT_COUNT"] = rule.Replacements.Length.ToString();

            for (var j = 0; j < rule.Replacements.Length; j++)
            {
                values[$"DD_RUNTIME_VIRTUAL_RULE_{i}_REPLACEMENT_{j}_FIND"] = rule.Replacements[j].Find;
                values[$"DD_RUNTIME_VIRTUAL_RULE_{i}_REPLACEMENT_{j}_REPLACE"] = rule.Replacements[j].Replace;
            }
        }

        return values;
    }

    [JsonPropertyName("virtualFileEnabled")]
    public bool VirtualFileEnabled { get; set; } = true;

    [JsonPropertyName("virtualFileTarget")]
    public string VirtualFileTarget { get; set; } = string.Empty;

    [JsonPropertyName("virtualFileFind")]
    public string VirtualFileFind { get; set; } = string.Empty;

    [JsonPropertyName("virtualFileReplace")]
    public string VirtualFileReplace { get; set; } = string.Empty;

    [JsonPropertyName("virtualFileRules")]
    public VirtualFileRule[] VirtualFileRules { get; set; } = [];

    public PatchPlan BuildPatchPlan(string projectRoot, LauncherLog log)
    {
        var sourceRules = new List<VirtualFileRuleSource>();
        var skippedRules = new List<VirtualFileRuleSkip>();
        var compileIssues = new List<PatchCompileIssue>();
        var manifestName = string.IsNullOrWhiteSpace(PluginPatchManifestName) ? "patches.json" : PluginPatchManifestName;
        var pluginCandidates = DiscoverPluginPatchManifests(projectRoot, manifestName, log).ToList();
        var loadPlan = BuildPluginLoadPlan(pluginCandidates, log);
        var activePluginIds = loadPlan.OrderedEnabledPlugins
            .Select(plugin => NormalizePluginId(plugin.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AddVirtualRules(sourceRules, skippedRules, VirtualFileRules, "config virtualFileRules", string.Empty, activePluginIds);

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
                activePluginIds);
        }

        foreach (var plugin in loadPlan.OrderedEnabledPlugins)
        {
            log.Info(
                $"Plugin patch manifest enabled: order={plugin.LoadOrder} id={plugin.Id} " +
                $"name={plugin.Name} phase={NormalizePhase(plugin.Manifest.Phase)} " +
                $"priority={plugin.Manifest.Priority} virtualRules={plugin.VirtualFileRuleCount} path={plugin.Path}");
            AddVirtualRules(sourceRules, skippedRules, plugin.Manifest.VirtualFileRules, plugin.SourceName, plugin.Path, activePluginIds);
        }

        return new PatchPlan(
            loadPlan.Manifests,
            loadPlan.LoadRules,
            loadPlan.Diagnostics,
            sourceRules,
            skippedRules,
            BuildEffectiveVirtualRules(sourceRules, compileIssues, log),
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
            .Where(builder => builder.Replacements.Count > 0)
            .Select(builder => new VirtualFileRule
            {
                Target = builder.Target,
                Replacements = builder.Replacements.ToArray()
            })
            .ToList();
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
                new PatchReplacementOrigin(sourceRule.SourceName, sourceRule.SourcePath, sourceRule.RuleIndex, replacementIndex, -1, "replacement"));
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

    private static string NormalizePluginId(string id)
    {
        return id.Trim().ToLowerInvariant();
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

    private static string ReplaceAllText(string text, string find, string replace, out int replacements)
    {
        replacements = 0;
        if (find.Length == 0)
        {
            return text;
        }

        var position = 0;
        while ((position = text.IndexOf(find, position, StringComparison.Ordinal)) >= 0)
        {
            text = text.Remove(position, find.Length).Insert(position, replace);
            position += replace.Length;
            replacements++;
        }

        return text;
    }

    private static void AddVirtualRules(
        List<VirtualFileRuleSource> output,
        List<VirtualFileRuleSkip> skipped,
        IEnumerable<VirtualFileRule>? input,
        string sourceName,
        string sourcePath,
        IReadOnlySet<string> activePluginIds)
    {
        var index = 0;
        foreach (var rule in input ?? [])
        {
            index++;
            if (string.IsNullOrWhiteSpace(rule.Target))
            {
                continue;
            }

            var hasReplacements = (rule.Replacements ?? []).Any(replacement => !string.IsNullOrEmpty(replacement.Find));
            var hasOperations = (rule.Operations ?? []).Length > 0;
            if (!hasReplacements && !hasOperations)
            {
                continue;
            }

            var condition = EvaluatePatchCondition(rule.When, activePluginIds);
            if (!condition.Matched)
            {
                skipped.Add(new VirtualFileRuleSkip(
                    sourceName,
                    sourcePath,
                    index,
                    rule.Target,
                    rule.Replacements?.Length ?? 0,
                    rule.Operations?.Length ?? 0,
                    condition.Reason));
                continue;
            }

            output.Add(new VirtualFileRuleSource(
                sourceName,
                sourcePath,
                index,
                new VirtualFileRule
                {
                    Target = rule.Target,
                    Replacements = rule.Replacements ?? [],
                    Operations = rule.Operations ?? [],
                    When = rule.When
                },
                condition.Reason));
        }
    }

    private static PatchConditionResult EvaluatePatchCondition(PatchCondition? condition, IReadOnlySet<string> activePluginIds)
    {
        if (condition is null)
        {
            return new PatchConditionResult(true, "no condition");
        }

        var modsPresent = CleanModReferences(condition.ModsPresent).ToArray();
        var modsAbsent = CleanModReferences(condition.ModsAbsent).ToArray();
        if (modsPresent.Length == 0 && modsAbsent.Length == 0)
        {
            return new PatchConditionResult(true, "empty condition");
        }

        var missingPresent = modsPresent
            .Where(modId => !activePluginIds.Contains(NormalizePluginId(modId)))
            .ToArray();
        if (missingPresent.Length > 0)
        {
            return new PatchConditionResult(false, "modsPresent missing: " + string.Join(",", missingPresent));
        }

        var presentAbsent = modsAbsent
            .Where(modId => activePluginIds.Contains(NormalizePluginId(modId)))
            .ToArray();
        if (presentAbsent.Length > 0)
        {
            return new PatchConditionResult(false, "modsAbsent present: " + string.Join(",", presentAbsent));
        }

        return new PatchConditionResult(true, "condition matched");
    }

    private List<VirtualFileReplacement> CompileVirtualFileOperations(
        VirtualFileRule rule,
        string currentText,
        string sourceName,
        string sourcePath,
        int ruleIndex,
        List<PatchCompileIssue> compileIssues,
        LauncherLog log,
        out string updatedText)
    {
        updatedText = currentText;
        var replacements = new List<VirtualFileReplacement>();
        var operations = rule.Operations ?? [];
        if (operations.Length == 0)
        {
            return replacements;
        }

        for (var operationIndex = 0; operationIndex < operations.Length; operationIndex++)
        {
            var operation = operations[operationIndex];
            var lines = SplitLinesPreserveEndings(updatedText);
            var preferredEol = lines.FirstOrDefault(line => line.Eol.Length > 0)?.Eol ?? "\n";
            var compiled = CompileOperation(operation, lines, preferredEol, sourceName, sourcePath, ruleIndex, operationIndex, rule.Target, compileIssues);
            for (var replacementIndex = 0; replacementIndex < compiled.Count; replacementIndex++)
            {
                compiled[replacementIndex] = WithOrigin(
                    compiled[replacementIndex],
                    new PatchReplacementOrigin(sourceName, sourcePath, ruleIndex, replacementIndex, operationIndex, operation.Type));
                updatedText = ReplaceAllText(updatedText, compiled[replacementIndex].Find, compiled[replacementIndex].Replace, out var applied);
                if (applied == 0)
                {
                    AddCompileIssue(
                        compileIssues,
                        false,
                        sourceName,
                        sourcePath,
                        ruleIndex,
                        operationIndex,
                        rule.Target,
                        $"compiled operation replacement did not match current virtual text: type={operation.Type}");
                }
            }
            replacements.AddRange(compiled);

            log.Info(
                $"patch-operation-compiled source={sourceName} target={rule.Target} " +
                $"rule={ruleIndex} operation={operationIndex} type={operation.Type} replacements={compiled.Count}");
        }

        return replacements;
    }

    private static List<VirtualFileReplacement> CompileOperation(
        VirtualFileOperation operation,
        IReadOnlyList<TextLineSegment> lines,
        string preferredEol,
        string sourceName,
        string sourcePath,
        int ruleIndex,
        int operationIndex,
        string target,
        List<PatchCompileIssue> compileIssues)
    {
        var type = operation.Type.Trim();
        if (type.Equals("setValue", StringComparison.OrdinalIgnoreCase))
        {
            return CompileSetValue(operation, lines, sourceName, sourcePath, ruleIndex, operationIndex, target, compileIssues);
        }

        if (type.Equals("replaceLine", StringComparison.OrdinalIgnoreCase))
        {
            return CompileReplaceLine(operation, lines, sourceName, sourcePath, ruleIndex, operationIndex, target, compileIssues);
        }

        if (type.Equals("appendAfter", StringComparison.OrdinalIgnoreCase))
        {
            return CompileAppendAfter(operation, lines, sourceName, sourcePath, ruleIndex, operationIndex, target, compileIssues);
        }

        if (type.Equals("appendEnd", StringComparison.OrdinalIgnoreCase))
        {
            return CompileAppendEnd(operation, lines, preferredEol, sourceName, sourcePath, ruleIndex, operationIndex, target, compileIssues);
        }

        AddCompileIssue(compileIssues, true, sourceName, sourcePath, ruleIndex, operationIndex, target, $"unknown operation type: {operation.Type}");
        return [];
    }

    private static List<VirtualFileReplacement> CompileSetValue(
        VirtualFileOperation operation,
        IReadOnlyList<TextLineSegment> lines,
        string sourceName,
        string sourcePath,
        int ruleIndex,
        int operationIndex,
        string target,
        List<PatchCompileIssue> compileIssues)
    {
        if (string.IsNullOrWhiteSpace(operation.Key))
        {
            AddCompileIssue(compileIssues, true, sourceName, sourcePath, ruleIndex, operationIndex, target, "setValue requires key");
            return [];
        }

        var matches = lines.Where(line => LineHasKey(line.Text, operation.Key)).ToList();
        if (matches.Count == 0)
        {
            AddCompileIssue(compileIssues, false, sourceName, sourcePath, ruleIndex, operationIndex, target, $"setValue key was not found: {operation.Key}");
            return [];
        }

        return matches.Select(line => new VirtualFileReplacement
        {
            Find = line.Raw,
            Replace = LeadingWhitespace(line.Text) + operation.Key + " " + operation.Value + line.Eol
        }).ToList();
    }

    private static List<VirtualFileReplacement> CompileReplaceLine(
        VirtualFileOperation operation,
        IReadOnlyList<TextLineSegment> lines,
        string sourceName,
        string sourcePath,
        int ruleIndex,
        int operationIndex,
        string target,
        List<PatchCompileIssue> compileIssues)
    {
        if (string.IsNullOrEmpty(operation.Line))
        {
            AddCompileIssue(compileIssues, true, sourceName, sourcePath, ruleIndex, operationIndex, target, "replaceLine requires line");
            return [];
        }

        var matches = MatchLines(operation, lines).ToList();
        if (matches.Count == 0)
        {
            AddCompileIssue(compileIssues, false, sourceName, sourcePath, ruleIndex, operationIndex, target, "replaceLine did not match any line");
            return [];
        }

        return matches.Select(line => new VirtualFileReplacement
        {
            Find = line.Raw,
            Replace = operation.Line + line.Eol
        }).ToList();
    }

    private static List<VirtualFileReplacement> CompileAppendAfter(
        VirtualFileOperation operation,
        IReadOnlyList<TextLineSegment> lines,
        string sourceName,
        string sourcePath,
        int ruleIndex,
        int operationIndex,
        string target,
        List<PatchCompileIssue> compileIssues)
    {
        var content = OperationContent(operation);
        if (string.IsNullOrEmpty(content))
        {
            AddCompileIssue(compileIssues, true, sourceName, sourcePath, ruleIndex, operationIndex, target, "appendAfter requires content or text");
            return [];
        }

        var matches = MatchLines(operation, lines).ToList();
        if (matches.Count == 0)
        {
            AddCompileIssue(compileIssues, false, sourceName, sourcePath, ruleIndex, operationIndex, target, "appendAfter did not match any line");
            return [];
        }

        return matches.Select(line => new VirtualFileReplacement
        {
            Find = line.Raw,
            Replace = line.Raw + (line.Eol.Length == 0 ? "\n" : string.Empty) + EnsureTrailingEol(content, line.Eol.Length == 0 ? "\n" : line.Eol)
        }).ToList();
    }

    private static List<VirtualFileReplacement> CompileAppendEnd(
        VirtualFileOperation operation,
        IReadOnlyList<TextLineSegment> lines,
        string preferredEol,
        string sourceName,
        string sourcePath,
        int ruleIndex,
        int operationIndex,
        string target,
        List<PatchCompileIssue> compileIssues)
    {
        var content = OperationContent(operation);
        if (string.IsNullOrEmpty(content))
        {
            AddCompileIssue(compileIssues, true, sourceName, sourcePath, ruleIndex, operationIndex, target, "appendEnd requires content or text");
            return [];
        }

        var anchor = lines.LastOrDefault(line => line.Raw.Length > 0);
        if (anchor is null)
        {
            AddCompileIssue(compileIssues, false, sourceName, sourcePath, ruleIndex, operationIndex, target, "appendEnd cannot compile against an empty file");
            return [];
        }

        var separator = anchor.Eol.Length == 0 ? preferredEol : string.Empty;
        return
        [
            new VirtualFileReplacement
            {
                Find = anchor.Raw,
                Replace = anchor.Raw + separator + EnsureTrailingEol(content, preferredEol)
            }
        ];
    }

    private static IEnumerable<TextLineSegment> MatchLines(VirtualFileOperation operation, IReadOnlyList<TextLineSegment> lines)
    {
        if (!string.IsNullOrEmpty(operation.Match))
        {
            return lines.Where(line => line.Text == operation.Match);
        }

        if (!string.IsNullOrEmpty(operation.Prefix))
        {
            return lines.Where(line => line.Text.TrimStart().StartsWith(operation.Prefix, StringComparison.Ordinal));
        }

        return [];
    }

    private static List<TextLineSegment> SplitLinesPreserveEndings(string text)
    {
        var lines = new List<TextLineSegment>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\r' && text[i] != '\n')
            {
                continue;
            }

            var eolLength = 1;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                eolLength = 2;
            }

            lines.Add(new TextLineSegment(text[start..i], text.Substring(i, eolLength)));
            i += eolLength - 1;
            start = i + 1;
        }

        if (start < text.Length)
        {
            lines.Add(new TextLineSegment(text[start..], string.Empty));
        }

        return lines;
    }

    private static bool LineHasKey(string line, string key)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith(key, StringComparison.Ordinal))
        {
            return false;
        }

        return trimmed.Length == key.Length || char.IsWhiteSpace(trimmed[key.Length]);
    }

    private static string LeadingWhitespace(string value)
    {
        var length = 0;
        while (length < value.Length && char.IsWhiteSpace(value[length]))
        {
            length++;
        }

        return value[..length];
    }

    private static string OperationContent(VirtualFileOperation operation)
    {
        return !string.IsNullOrEmpty(operation.Content) ? operation.Content : operation.Text;
    }

    private static string EnsureTrailingEol(string value, string eol)
    {
        return value.EndsWith("\n", StringComparison.Ordinal) || value.EndsWith("\r", StringComparison.Ordinal)
            ? value
            : value + eol;
    }

    private static void AddCompileIssue(
        List<PatchCompileIssue> compileIssues,
        bool isError,
        string sourceName,
        string sourcePath,
        int ruleIndex,
        int operationIndex,
        string target,
        string message)
    {
        compileIssues.Add(new PatchCompileIssue(isError, sourceName, sourcePath, ruleIndex, operationIndex, target, message));
    }

    private static VirtualFileReplacement WithOrigin(VirtualFileReplacement replacement, PatchReplacementOrigin origin)
    {
        return new VirtualFileReplacement
        {
            Find = replacement.Find,
            Replace = replacement.Replace,
            Origin = origin
        };
    }

    private static List<VirtualFileRule> MergeVirtualRules(IEnumerable<VirtualFileRuleSource> input)
    {
        var ordered = new List<VirtualFileRule>();
        var byTarget = new Dictionary<string, VirtualFileRule>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceRule in input)
        {
            var rule = sourceRule.Rule;
            var key = NormalizeVirtualTargetKey(rule.Target);
            if (byTarget.TryGetValue(key, out var existing))
            {
                existing.Replacements = existing.Replacements.Concat(rule.Replacements).ToArray();
                continue;
            }

            ordered.Add(rule);
            byTarget[key] = rule;
        }

        return ordered;
    }

    private static string NormalizeVirtualTargetKey(string target)
    {
        return target.Trim().Replace('\\', '/');
    }

    public string ResolveVirtualTargetPath(string target)
    {
        var normalized = target.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
        {
            return Path.GetFullPath(normalized);
        }

        normalized = normalized.TrimStart(Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(GameWorkingDirectory, normalized));
    }

    private static bool IsInsideDirectory(string directory, string path)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}

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
                $"priority={manifest.Priority} rules={manifest.VirtualFileRuleCount} path={manifest.Path}");
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
                $"rules={manifest.VirtualFileRuleCount} skipReason={QuoteLogValue(manifest.SkipReason)} path={manifest.Path}");
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
                    $"type={origin.OperationType} findChars={replacement.Find.Length} replaceChars={replacement.Replace.Length}");
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
}

internal sealed record PatchManifestInfo(
    string Name,
    string Id,
    string Version,
    string Path,
    string Status,
    bool Enabled,
    int VirtualFileRuleCount,
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
    string OperationType)
{
    public static PatchReplacementOrigin Unknown { get; } = new("unknown", string.Empty, -1, -1, -1, "unknown");
}

internal static class PatchPreviewer
{
    public static void WritePreview(RuntimeConfig config, PatchPlan patchPlan, string outputDirectory, LauncherLog log)
    {
        Directory.CreateDirectory(outputDirectory);
        var results = new List<PatchPreviewResult>();

        log.Info($"Patch preview started. output={outputDirectory} effectiveRules={patchPlan.EffectiveVirtualFileRules.Count}");
        foreach (var rule in patchPlan.EffectiveVirtualFileRules)
        {
            var result = PreviewRule(config, rule, log);
            results.Add(result);
            WritePreviewFiles(outputDirectory, result);
        }

        WriteSummary(outputDirectory, results);
        log.Info($"Patch preview completed. targets={results.Count} output={outputDirectory}");
    }

    private static PatchPreviewResult PreviewRule(RuntimeConfig config, VirtualFileRule rule, LauncherLog log)
    {
        var targetPath = config.ResolveVirtualTargetPath(rule.Target);
        if (!IsInsideDirectory(config.GameWorkingDirectory, targetPath))
        {
            throw new InvalidOperationException($"Patch preview target resolves outside game working directory: {rule.Target} -> {targetPath}");
        }

        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException("Patch preview target file was not found.", targetPath);
        }

        var originalText = File.ReadAllText(targetPath, Encoding.UTF8);
        var currentText = originalText;
        var applications = new List<PatchReplacementApplication>();
        var replacementsApplied = 0;

        for (var replacementIndex = 0; replacementIndex < rule.Replacements.Length; replacementIndex++)
        {
            var replacement = rule.Replacements[replacementIndex];
            var matches = CountOccurrences(currentText, replacement.Find);
            var firstLine = matches == 0 ? 0 : LineNumberOf(currentText, replacement.Find);
            var before = matches == 0 ? string.Empty : LineContaining(currentText, replacement.Find);
            currentText = ReplaceAll(currentText, replacement.Find, replacement.Replace, out var applied);
            replacementsApplied += applied;
            var after = applied == 0 ? string.Empty : FirstReplacementLine(replacement.Replace);

            applications.Add(new PatchReplacementApplication(
                replacement.Origin ?? PatchReplacementOrigin.Unknown,
                replacementIndex,
                applied,
                firstLine,
                before,
                after));
        }

        var warnings = BuildConflictWarnings(rule.Target, applications);
        foreach (var warning in warnings)
        {
            log.Warn(warning);
        }

        log.Info(
            $"patch-preview target={rule.Target} originalBytes={Encoding.UTF8.GetByteCount(originalText)} " +
            $"virtualBytes={Encoding.UTF8.GetByteCount(currentText)} replacements={replacementsApplied}");

        return new PatchPreviewResult(
            rule.Target,
            targetPath,
            originalText,
            currentText,
            Encoding.UTF8.GetByteCount(originalText),
            Encoding.UTF8.GetByteCount(currentText),
            rule.Replacements.Length,
            replacementsApplied,
            applications,
            warnings);
    }

    private static void WritePreviewFiles(string outputDirectory, PatchPreviewResult result)
    {
        var name = SafeFileName(result.Target);
        File.WriteAllText(Path.Combine(outputDirectory, name + ".preview.txt"), result.VirtualText, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outputDirectory, name + ".diff.txt"), BuildDiff(result), new UTF8Encoding(false));
    }

    private static void WriteSummary(string outputDirectory, IReadOnlyList<PatchPreviewResult> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Patch Preview Summary");
        builder.AppendLine("=====================");
        builder.AppendLine();

        foreach (var result in results)
        {
            builder.AppendLine($"Target: {result.Target}");
            builder.AppendLine($"Path: {result.TargetPath}");
            builder.AppendLine($"Original bytes: {result.OriginalBytes}");
            builder.AppendLine($"Virtual bytes: {result.VirtualBytes}");
            builder.AppendLine($"Replacement attempts: {result.ReplacementAttempts}");
            builder.AppendLine($"Replacements applied: {result.ReplacementsApplied}");
            foreach (var warning in result.Warnings)
            {
                builder.AppendLine($"Warning: {warning}");
            }
            builder.AppendLine();
        }

        File.WriteAllText(Path.Combine(outputDirectory, "summary.txt"), builder.ToString(), new UTF8Encoding(false));
    }

    private static string BuildDiff(PatchPreviewResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Target: {result.Target}");
        builder.AppendLine($"Path: {result.TargetPath}");
        builder.AppendLine($"Original bytes: {result.OriginalBytes}");
        builder.AppendLine($"Virtual bytes: {result.VirtualBytes}");
        builder.AppendLine($"Replacements applied: {result.ReplacementsApplied}");
        foreach (var warning in result.Warnings)
        {
            builder.AppendLine($"Warning: {warning}");
        }
        builder.AppendLine();

        foreach (var application in result.Applications)
        {
            var origin = application.Origin;
            builder.AppendLine(
                $"@@ replacement={application.ReplacementIndex} line={application.FirstLine} matches={application.Matches} " +
                $"source={origin.SourceName} rule={origin.RuleIndex} operation={origin.OperationIndex} type={origin.OperationType}");
            if (application.Matches == 0)
            {
                builder.AppendLine("! no match at preview time");
            }
            else
            {
                builder.AppendLine("- " + application.Before);
                builder.AppendLine("+ " + application.After);
            }
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> BuildConflictWarnings(string target, IReadOnlyList<PatchReplacementApplication> applications)
    {
        return applications
            .Where(application => application.Matches > 0 && application.FirstLine > 0)
            .GroupBy(application => application.FirstLine)
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                var sources = string.Join(", ", group.Select(application => application.Origin.SourceName).Distinct(StringComparer.OrdinalIgnoreCase));
                return $"patch-preview-conflict target={target} line={group.Key} replacements={group.Count()} sources={sources}";
            })
            .ToArray();
    }

    private static bool IsInsideDirectory(string directory, string path)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReplaceAll(string text, string find, string replace, out int replacements)
    {
        replacements = 0;
        if (find.Length == 0)
        {
            return text;
        }

        var position = 0;
        while ((position = text.IndexOf(find, position, StringComparison.Ordinal)) >= 0)
        {
            text = text.Remove(position, find.Length).Insert(position, replace);
            position += replace.Length;
            replacements++;
        }

        return text;
    }

    private static int CountOccurrences(string text, string find)
    {
        if (find.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var position = 0;
        while ((position = text.IndexOf(find, position, StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += find.Length;
        }

        return count;
    }

    private static int LineNumberOf(string text, string find)
    {
        var position = text.IndexOf(find, StringComparison.Ordinal);
        if (position < 0)
        {
            return 0;
        }

        var line = 1;
        for (var i = 0; i < position; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static string LineContaining(string text, string find)
    {
        var position = text.IndexOf(find, StringComparison.Ordinal);
        if (position < 0)
        {
            return string.Empty;
        }

        var start = text.LastIndexOf('\n', position);
        start = start < 0 ? 0 : start + 1;
        var end = text.IndexOf('\n', position);
        if (end < 0)
        {
            end = text.Length;
        }

        return text[start..end].TrimEnd('\r');
    }

    private static string FirstReplacementLine(string replacement)
    {
        var end = replacement.IndexOf('\n', StringComparison.Ordinal);
        var line = end < 0 ? replacement : replacement[..end];
        return line.TrimEnd('\r');
    }

    private static string SafeFileName(string target)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(target.Length);
        foreach (var ch in target.Replace('\\', '_').Replace('/', '_'))
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.Length == 0 ? "patch" : builder.ToString();
    }
}

internal sealed class PatchValidationResult
{
    public PatchValidationResult(int errorCount, int warningCount)
    {
        ErrorCount = errorCount;
        WarningCount = warningCount;
    }

    public int ErrorCount { get; }
    public int WarningCount { get; }
    public bool Succeeded => ErrorCount == 0;
}

internal static class PatchValidator
{
    private const long MaxVirtualFileBytes = 16 * 1024 * 1024;

    public static PatchValidationResult Validate(RuntimeConfig config, PatchPlan patchPlan, LauncherLog log, bool strictPatches)
    {
        var errors = 0;
        var warnings = 0;

        log.Info(
            $"Patch validation started. sourceRules={patchPlan.SourceVirtualFileRules.Count} " +
            $"effectiveRules={patchPlan.EffectiveVirtualFileRules.Count}");

        if (!config.VirtualFileEnabled && patchPlan.EffectiveVirtualFileRules.Count > 0)
        {
            warnings++;
            log.Warn("Virtual file rules exist, but virtualFileEnabled is false. These rules will not be applied.");
        }

        foreach (var group in patchPlan.SourceVirtualFileRules.GroupBy(rule => NormalizeTargetKey(rule.Rule.Target)))
        {
            var count = group.Count();
            if (count > 1)
            {
                warnings++;
                var sources = string.Join(", ", group.Select(rule => rule.SourceName).Distinct(StringComparer.OrdinalIgnoreCase));
                log.Warn($"Multiple enabled source rules target the same virtual file: target={group.Key} count={count} sources={sources}");
            }
        }

        if (patchPlan.EffectiveVirtualFileRules.Count == 0)
        {
            log.Info("Patch validation found no enabled virtual file rules.");
            log.Info("Patch validation completed. errors=0 warnings=" + warnings);
            return new PatchValidationResult(errors, warnings);
        }

        for (var ruleIndex = 0; ruleIndex < patchPlan.EffectiveVirtualFileRules.Count; ruleIndex++)
        {
            var rule = patchPlan.EffectiveVirtualFileRules[ruleIndex];
            var targetPath = config.ResolveVirtualTargetPath(rule.Target);
            if (!IsInsideDirectory(config.GameWorkingDirectory, targetPath))
            {
                errors++;
                log.Error($"Patch target resolves outside game working directory: target={rule.Target} path={targetPath}");
                continue;
            }

            if (!File.Exists(targetPath))
            {
                errors++;
                log.Error($"Patch target file was not found: target={rule.Target} path={targetPath}");
                continue;
            }

            var info = new FileInfo(targetPath);
            if (info.Length > MaxVirtualFileBytes)
            {
                errors++;
                log.Error($"Patch target exceeds runtime virtual file size limit: target={rule.Target} bytes={info.Length} limit={MaxVirtualFileBytes}");
                continue;
            }

            string currentText;
            try
            {
                currentText = File.ReadAllText(targetPath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                errors++;
                log.Error($"Patch target could not be read: target={rule.Target} path={targetPath}: {ex.Message}");
                continue;
            }

            for (var replacementIndex = 0; replacementIndex < rule.Replacements.Length; replacementIndex++)
            {
                var replacement = rule.Replacements[replacementIndex];
                var matches = CountOccurrences(currentText, replacement.Find);
                log.Info(
                    $"patch-validate-match target={rule.Target} rule={ruleIndex} replacement={replacementIndex} " +
                    $"matches={matches} findChars={replacement.Find.Length}");

                if (matches == 0)
                {
                    if (strictPatches)
                    {
                        errors++;
                        log.Error($"Patch replacement text was not found: target={rule.Target} replacement={replacementIndex}");
                    }
                    else
                    {
                        warnings++;
                        log.Warn($"Patch replacement text was not found: target={rule.Target} replacement={replacementIndex}");
                    }
                }

                currentText = ReplaceAll(currentText, replacement.Find, replacement.Replace, out _);
            }
        }

        log.Info($"Patch validation completed. errors={errors} warnings={warnings}");
        return new PatchValidationResult(errors, warnings);
    }

    private static string NormalizeTargetKey(string target)
    {
        return target.Trim().Replace('\\', '/').ToLowerInvariant();
    }

    private static bool IsInsideDirectory(string directory, string path)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReplaceAll(string text, string find, string replace, out int replacements)
    {
        replacements = 0;
        if (find.Length == 0)
        {
            return text;
        }

        var position = 0;
        while ((position = text.IndexOf(find, position, StringComparison.Ordinal)) >= 0)
        {
            text = text.Remove(position, find.Length).Insert(position, replace);
            position += replace.Length;
            replacements++;
        }

        return text;
    }

    private static int CountOccurrences(string text, string find)
    {
        if (find.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var position = 0;
        while ((position = text.IndexOf(find, position, StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += find.Length;
        }

        return count;
    }
}

internal sealed class PluginPatchManifest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("phase")]
    public string Phase { get; set; } = "normal";

    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("depends")]
    public string[] Depends { get; set; } = [];

    [JsonPropertyName("optionalDepends")]
    public string[] OptionalDepends { get; set; } = [];

    [JsonPropertyName("loadAfter")]
    public string[] LoadAfter { get; set; } = [];

    [JsonPropertyName("loadBefore")]
    public string[] LoadBefore { get; set; } = [];

    [JsonPropertyName("conflicts")]
    public string[] Conflicts { get; set; } = [];

    [JsonPropertyName("virtualFileRules")]
    public VirtualFileRule[] VirtualFileRules { get; set; } = [];

    public static PluginPatchManifest Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var manifest = JsonSerializer.Deserialize<PluginPatchManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) ?? throw new InvalidOperationException($"Plugin patch manifest is empty: {path}");

            manifest.Name ??= string.Empty;
            manifest.Id ??= string.Empty;
            manifest.Version ??= string.Empty;
            manifest.Phase ??= "normal";
            manifest.Depends ??= [];
            manifest.OptionalDepends ??= [];
            manifest.LoadAfter ??= [];
            manifest.LoadBefore ??= [];
            manifest.Conflicts ??= [];
            manifest.VirtualFileRules ??= [];
            return manifest;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Plugin patch manifest is invalid: {path}: {ex.Message}", ex);
        }
    }
}

internal sealed class VirtualFileRule
{
    [JsonPropertyName("when")]
    public PatchCondition? When { get; set; }

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    [JsonPropertyName("replacements")]
    public VirtualFileReplacement[] Replacements { get; set; } = [];

    [JsonPropertyName("operations")]
    public VirtualFileOperation[] Operations { get; set; } = [];
}

internal sealed class PatchCondition
{
    [JsonPropertyName("modsPresent")]
    public string[] ModsPresent { get; set; } = [];

    [JsonPropertyName("modsAbsent")]
    public string[] ModsAbsent { get; set; } = [];
}

internal sealed class VirtualFileReplacement
{
    [JsonPropertyName("find")]
    public string Find { get; set; } = string.Empty;

    [JsonPropertyName("replace")]
    public string Replace { get; set; } = string.Empty;

    [JsonIgnore]
    public PatchReplacementOrigin? Origin { get; set; }
}

internal sealed class VirtualFileOperation
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("match")]
    public string Match { get; set; } = string.Empty;

    [JsonPropertyName("prefix")]
    public string Prefix { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    public string Line { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

internal sealed class LauncherOptions
{
    public string? ConfigPath { get; private set; }
    public string? GameExecutablePath { get; private set; }
    public string? RuntimeDllPath { get; private set; }
    public bool DryRun { get; private set; }
    public bool NoInject { get; private set; }
    public bool ListPatches { get; private set; }
    public bool ExplainPatches { get; private set; }
    public bool ValidatePatches { get; private set; }
    public bool ValidateOnly { get; private set; }
    public bool PreviewPatches { get; private set; }
    public bool StrictPatches { get; private set; }
    public string? PreviewOutputPath { get; private set; }

    public static LauncherOptions Parse(string[] args)
    {
        var options = new LauncherOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config":
                    options.ConfigPath = RequireValue(args, ref i, "--config");
                    break;
                case "--game":
                    options.GameExecutablePath = RequireValue(args, ref i, "--game");
                    break;
                case "--dll":
                    options.RuntimeDllPath = RequireValue(args, ref i, "--dll");
                    break;
                case "--dry-run":
                    options.DryRun = true;
                    break;
                case "--no-inject":
                    options.NoInject = true;
                    break;
                case "--list-patches":
                    options.ListPatches = true;
                    break;
                case "--explain-patches":
                    options.ExplainPatches = true;
                    break;
                case "--validate-patches":
                    options.ValidatePatches = true;
                    break;
                case "--validate-only":
                    options.ValidateOnly = true;
                    options.ValidatePatches = true;
                    break;
                case "--preview-patches":
                    options.PreviewPatches = true;
                    break;
                case "--strict-patches":
                    options.StrictPatches = true;
                    break;
                case "--preview-output":
                    options.PreviewOutputPath = RequireValue(args, ref i, "--preview-output");
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }
        return options;
    }

    private static string RequireValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {name}.");
        index++;
        return args[index];
    }
}

internal sealed class LauncherLog : IDisposable
{
    private readonly StreamWriter _writer;

    private LauncherLog(string path)
    {
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
        {
            AutoFlush = true
        };
    }

    public static LauncherLog Open(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        return new LauncherLog(Path.Combine(logDirectory, "launcher.log"));
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
        Console.WriteLine(line);
        _writer.WriteLine(line);
    }

    public void Dispose() => _writer.Dispose();
}

internal static class PeArchitecture
{
    public static string Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        stream.Seek(0x3C, SeekOrigin.Begin);
        var peOffset = reader.ReadInt32();
        stream.Seek(peOffset + 4, SeekOrigin.Begin);
        var machine = reader.ReadUInt16();
        return machine switch
        {
            0x014c => "x86",
            0x8664 => "x64",
            _ => $"unknown-0x{machine:x4}"
        };
    }
}

internal sealed class ProcessEnvironmentScope : IDisposable
{
    private readonly Dictionary<string, string?> _previousValues = new(StringComparer.OrdinalIgnoreCase);

    private ProcessEnvironmentScope(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values)
        {
            _previousValues[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    public static ProcessEnvironmentScope Apply(IReadOnlyDictionary<string, string> values) => new(values);

    public void Dispose()
    {
        foreach (var (key, value) in _previousValues)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}

internal sealed class SuspendedProcess : IDisposable
{
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint STILL_ACTIVE = 259;
    private IntPtr _processHandle;
    private IntPtr _threadHandle;
    private bool _resumed;
    private bool _terminated;

    private SuspendedProcess(IntPtr processHandle, IntPtr threadHandle, int processId)
    {
        _processHandle = processHandle;
        _threadHandle = threadHandle;
        ProcessId = processId;
    }

    public int ProcessId { get; }

    public static SuspendedProcess Start(string executablePath, string workingDirectory)
    {
        var startupInfo = new STARTUPINFO
        {
            cb = (uint)Marshal.SizeOf<STARTUPINFO>()
        };

        var commandLine = new StringBuilder($"\"{executablePath}\"");
        if (!CreateProcessW(
                executablePath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CREATE_SUSPENDED,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out var processInformation))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW failed");
        }

        return new SuspendedProcess(
            processInformation.hProcess,
            processInformation.hThread,
            (int)processInformation.dwProcessId);
    }

    public void Resume()
    {
        if (_resumed || _terminated) return;

        var result = ResumeThread(_threadHandle);
        if (result == uint.MaxValue)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread failed");

        _resumed = true;
    }

    public void Terminate(uint exitCode)
    {
        if (_terminated) return;

        if (!TerminateProcess(_processHandle, exitCode))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "TerminateProcess failed");

        _terminated = true;
    }

    public void Dispose()
    {
        if (!_resumed && !_terminated && _processHandle != IntPtr.Zero)
        {
            if (GetExitCodeProcess(_processHandle, out var exitCode) && exitCode == STILL_ACTIVE)
            {
                TerminateProcess(_processHandle, 3);
            }
        }

        if (_threadHandle != IntPtr.Zero)
        {
            CloseHandle(_threadHandle);
            _threadHandle = IntPtr.Zero;
        }

        if (_processHandle != IntPtr.Zero)
        {
            CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal static class DllInjector
{
    private const uint PROCESS_CREATE_THREAD = 0x0002;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint INFINITE = 0xFFFFFFFF;

    public static void Inject(int processId, string dllPath)
    {
        dllPath = Path.GetFullPath(dllPath);
        var dllBytes = Encoding.Unicode.GetBytes(dllPath + "\0");

        var process = OpenProcess(
            PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE,
            false,
            processId);
        if (process == IntPtr.Zero) ThrowLastWin32("OpenProcess failed");

        var remoteMemory = IntPtr.Zero;
        var thread = IntPtr.Zero;
        try
        {
            remoteMemory = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)dllBytes.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remoteMemory == IntPtr.Zero) ThrowLastWin32("VirtualAllocEx failed");

            if (!WriteProcessMemory(process, remoteMemory, dllBytes, (UIntPtr)dllBytes.Length, out var bytesWritten) || bytesWritten.ToUInt64() != (ulong)dllBytes.Length)
                ThrowLastWin32("WriteProcessMemory failed");

            var kernel32 = GetModuleHandle("kernel32.dll");
            if (kernel32 == IntPtr.Zero) ThrowLastWin32("GetModuleHandle(kernel32.dll) failed");

            var loadLibrary = GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero) ThrowLastWin32("GetProcAddress(LoadLibraryW) failed");

            thread = CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, loadLibrary, remoteMemory, 0, out _);
            if (thread == IntPtr.Zero) ThrowLastWin32("CreateRemoteThread failed");

            var wait = WaitForSingleObject(thread, INFINITE);
            if (wait != WAIT_OBJECT_0) throw new Win32Exception($"WaitForSingleObject returned 0x{wait:x8}.");

            if (!GetExitCodeThread(thread, out var exitCode)) ThrowLastWin32("GetExitCodeThread failed");
            if (exitCode == 0) throw new InvalidOperationException("LoadLibraryW returned NULL in the target process.");
        }
        finally
        {
            if (thread != IntPtr.Zero) CloseHandle(thread);
            if (remoteMemory != IntPtr.Zero) VirtualFreeEx(process, remoteMemory, UIntPtr.Zero, MEM_RELEASE);
            CloseHandle(process);
        }
    }

    private static void ThrowLastWin32(string message) => throw new Win32Exception(Marshal.GetLastWin32Error(), message);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, UIntPtr size, out UIntPtr bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr threadAttributes, UIntPtr stackSize, IntPtr startAddress, IntPtr parameter, uint creationFlags, out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
