using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
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

            int? gameProcessId = null;
            using var saveWatcher = SaveDirectoryWatcher.Start(config, log);

            if (config.EnableInjection && !options.NoInject && config.StartSuspendedForInjection)
            {
                using var environmentScope = ProcessEnvironmentScope.Apply(runtimeEnvironment);
                using var suspendedProcess = SuspendedProcess.Start(config.GameExecutablePath, config.GameWorkingDirectory);
                gameProcessId = suspendedProcess.ProcessId;
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
                gameProcessId = process.Id;
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

            if (saveWatcher is not null && gameProcessId.HasValue)
            {
                saveWatcher.WaitForGameExit(gameProcessId.Value, config.SaveWatchAfterExitSeconds);
                log.Info("Launcher finished after game process exit and save watch grace period.");
            }
            else
            {
                log.Info("Launcher finished its startup work. Game process remains independent.");
            }
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

        if (config.SaveWatchAfterExitSeconds < 0)
            throw new InvalidOperationException("saveWatchAfterExitSeconds must be zero or greater.");
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

    [JsonPropertyName("eventProbeEnabled")]
    public bool EventProbeEnabled { get; set; } = true;

    [JsonPropertyName("eventProbeLogFileOpen")]
    public bool EventProbeLogFileOpen { get; set; } = true;

    [JsonPropertyName("eventProbeLogFileWrite")]
    public bool EventProbeLogFileWrite { get; set; } = true;

    [JsonPropertyName("eventProbeLogSaveFiles")]
    public bool EventProbeLogSaveFiles { get; set; } = true;

    [JsonPropertyName("eventProbeLogDataFiles")]
    public bool EventProbeLogDataFiles { get; set; }

    [JsonPropertyName("eventProbeLogAssetFiles")]
    public bool EventProbeLogAssetFiles { get; set; }

    [JsonPropertyName("eventProbeMaxLogEntries")]
    public int EventProbeMaxLogEntries { get; set; } = 5000;

    [JsonPropertyName("eventProbeMaxSaveLogEntries")]
    public int EventProbeMaxSaveLogEntries { get; set; } = 20000;

    [JsonPropertyName("eventProbeIgnorePathFragments")]
    public string[] EventProbeIgnorePathFragments { get; set; } =
    [
        "Steam/logs/",
        "gameoverlay_renderer.txt"
    ];

    [JsonPropertyName("saveWatchEnabled")]
    public bool SaveWatchEnabled { get; set; }

    [JsonPropertyName("saveWatchDirectories")]
    public string[] SaveWatchDirectories { get; set; } = [];

    [JsonPropertyName("saveWatchAfterExitSeconds")]
    public int SaveWatchAfterExitSeconds { get; set; } = 10;

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
        SaveWatchDirectories = SaveWatchDirectories
            .Select(path => ResolvePath(projectRoot, path))
            .ToArray();
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
            ["DD_RUNTIME_EVENT_PROBE_ENABLED"] = EventProbeEnabled ? "1" : "0",
            ["DD_RUNTIME_EVENT_PROBE_LOG_FILE_OPEN"] = EventProbeLogFileOpen ? "1" : "0",
            ["DD_RUNTIME_EVENT_PROBE_LOG_FILE_WRITE"] = EventProbeLogFileWrite ? "1" : "0",
            ["DD_RUNTIME_EVENT_PROBE_LOG_SAVE_FILES"] = EventProbeLogSaveFiles ? "1" : "0",
            ["DD_RUNTIME_EVENT_PROBE_LOG_DATA_FILES"] = EventProbeLogDataFiles ? "1" : "0",
            ["DD_RUNTIME_EVENT_PROBE_LOG_ASSET_FILES"] = EventProbeLogAssetFiles ? "1" : "0",
            ["DD_RUNTIME_EVENT_PROBE_MAX_ENTRIES"] = EventProbeMaxLogEntries.ToString(),
            ["DD_RUNTIME_EVENT_PROBE_MAX_SAVE_ENTRIES"] = EventProbeMaxSaveLogEntries.ToString(),
            ["DD_RUNTIME_EVENT_PROBE_IGNORE_PATH_FRAGMENTS"] = string.Join(';', EventProbeIgnorePathFragments),
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
                $"virtualRules={plugin.VirtualFileRuleCount} path={plugin.Path}");
            AddVirtualRules(sourceRules, skippedRules, plugin.Manifest.VirtualFileRules, plugin.SourceName, plugin.Path, activePluginIds, activeCapabilities);
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
        IReadOnlySet<string> activePluginIds,
        IReadOnlySet<string> activeCapabilities)
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

            var condition = EvaluatePatchCondition(rule.When, activePluginIds, activeCapabilities);
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

    private static PatchConditionResult EvaluatePatchCondition(
        PatchCondition? condition,
        IReadOnlySet<string> activePluginIds,
        IReadOnlySet<string> activeCapabilities)
    {
        if (condition is null)
        {
            return new PatchConditionResult(true, "no condition");
        }

        var modsPresent = CleanModReferences(condition.ModsPresent).ToArray();
        var modsAbsent = CleanModReferences(condition.ModsAbsent).ToArray();
        var capabilitiesPresent = CleanCapabilityReferences(condition.CapabilitiesPresent).ToArray();
        var capabilitiesAbsent = CleanCapabilityReferences(condition.CapabilitiesAbsent).ToArray();
        if (modsPresent.Length == 0 && modsAbsent.Length == 0 && capabilitiesPresent.Length == 0 && capabilitiesAbsent.Length == 0)
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

        var missingCapabilities = capabilitiesPresent
            .Where(capability => !activeCapabilities.Contains(NormalizeCapability(capability)))
            .ToArray();
        if (missingCapabilities.Length > 0)
        {
            return new PatchConditionResult(false, "capabilitiesPresent missing: " + string.Join(",", missingCapabilities));
        }

        var presentCapabilities = capabilitiesAbsent
            .Where(capability => activeCapabilities.Contains(NormalizeCapability(capability)))
            .ToArray();
        if (presentCapabilities.Length > 0)
        {
            return new PatchConditionResult(false, "capabilitiesAbsent present: " + string.Join(",", presentCapabilities));
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
            var subject = DescribeOperationSubject(operation);
            var lines = SplitLinesPreserveEndings(updatedText);
            var preferredEol = lines.FirstOrDefault(line => line.Eol.Length > 0)?.Eol ?? "\n";
            var compiled = CompileOperation(operation, lines, preferredEol, sourceName, sourcePath, ruleIndex, operationIndex, rule.Target, compileIssues);
            for (var replacementIndex = 0; replacementIndex < compiled.Count; replacementIndex++)
            {
                compiled[replacementIndex] = WithOrigin(
                    compiled[replacementIndex],
                    new PatchReplacementOrigin(sourceName, sourcePath, ruleIndex, replacementIndex, operationIndex, operation.Type, subject));
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
                $"rule={ruleIndex} operation={operationIndex} type={operation.Type} subject={QuoteLogValue(subject)} replacements={compiled.Count}");
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

    private static string DescribeOperationSubject(VirtualFileOperation operation)
    {
        var type = operation.Type.Trim();
        if (type.Equals("setValue", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(operation.Key) ? "key:" : "key:" + operation.Key.Trim();
        }

        if (!string.IsNullOrWhiteSpace(operation.Key))
        {
            return "key:" + operation.Key.Trim();
        }

        if (!string.IsNullOrWhiteSpace(operation.Prefix) && TryReadDarkestKey(operation.Prefix, out var prefixKey))
        {
            return "key:" + prefixKey;
        }

        if (!string.IsNullOrWhiteSpace(operation.Match) && TryReadDarkestKey(operation.Match, out var matchKey))
        {
            return "key:" + matchKey;
        }

        if (!string.IsNullOrWhiteSpace(operation.Line) && TryReadDarkestKey(operation.Line, out var lineKey))
        {
            return "key:" + lineKey;
        }

        if (!string.IsNullOrEmpty(operation.Match))
        {
            return "match:" + operation.Match;
        }

        if (!string.IsNullOrEmpty(operation.Prefix))
        {
            return "prefix:" + operation.Prefix;
        }

        if (type.Equals("appendEnd", StringComparison.OrdinalIgnoreCase))
        {
            return "file:end";
        }

        return string.IsNullOrWhiteSpace(type) ? "operation" : "operation:" + type;
    }

    private static bool TryReadDarkestKey(string value, out string key)
    {
        var trimmed = value.TrimStart();
        if (!trimmed.StartsWith(".", StringComparison.Ordinal))
        {
            key = string.Empty;
            return false;
        }

        var length = 0;
        while (length < trimmed.Length && !char.IsWhiteSpace(trimmed[length]))
        {
            length++;
        }

        key = trimmed[..length];
        return key.Length > 1;
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
                $"priority={manifest.Priority} capabilities={FormatLogList(manifest.Capabilities)} " +
                $"rules={manifest.VirtualFileRuleCount} path={manifest.Path}");
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
                $"capabilities={FormatLogList(manifest.Capabilities)} rules={manifest.VirtualFileRuleCount} " +
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
                $"source={origin.SourceName} rule={origin.RuleIndex} operation={origin.OperationIndex} " +
                $"type={origin.OperationType} subject={QuoteLogValue(origin.Subject)}");
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
        var warnings = applications
            .Where(application => application.Matches > 0 && application.FirstLine > 0)
            .GroupBy(application => application.FirstLine)
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                var sources = string.Join(", ", group.Select(application => application.Origin.SourceName).Distinct(StringComparer.OrdinalIgnoreCase));
                return $"patch-preview-conflict target={target} line={group.Key} replacements={group.Count()} sources={sources}";
            })
            .ToList();

        warnings.AddRange(applications
            .Where(application => application.Matches > 0 && IsKeySubject(application.Origin.Subject))
            .GroupBy(application => application.Origin.Subject, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                var sources = string.Join(", ", group.Select(application => application.Origin.SourceName).Distinct(StringComparer.OrdinalIgnoreCase));
                return $"patch-preview-key-conflict target={target} subject={group.Key} replacements={group.Count()} sources={sources}";
            }));

        return warnings.ToArray();
    }

    private static bool IsKeySubject(string subject)
    {
        return subject.StartsWith("key:", StringComparison.OrdinalIgnoreCase) && subject.Length > "key:".Length;
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

    private static string QuoteLogValue(string value)
    {
        return string.IsNullOrEmpty(value) ? "\"\"" : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
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
                var origin = replacement.Origin ?? PatchReplacementOrigin.Unknown;
                var matches = CountOccurrences(currentText, replacement.Find);
                log.Info(
                    $"patch-validate-match target={rule.Target} rule={ruleIndex} replacement={replacementIndex} " +
                    $"matches={matches} source={origin.SourceName} operation={origin.OperationIndex} " +
                    $"type={origin.OperationType} subject={QuoteLogValue(origin.Subject)} findChars={replacement.Find.Length}");

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

    private static string QuoteLogValue(string value)
    {
        return string.IsNullOrEmpty(value) ? "\"\"" : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
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

    [JsonPropertyName("capabilities")]
    public string[] Capabilities { get; set; } = [];

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
            manifest.Capabilities ??= [];
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

    [JsonPropertyName("capabilitiesPresent")]
    public string[] CapabilitiesPresent { get; set; } = [];

    [JsonPropertyName("capabilitiesAbsent")]
    public string[] CapabilitiesAbsent { get; set; } = [];
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
    private readonly object _sync = new();

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
        lock (_sync)
        {
            Console.WriteLine(line);
            _writer.WriteLine(line);
        }
    }

    public void Dispose() => _writer.Dispose();
}

internal sealed class SaveDirectoryWatcher : IDisposable
{
    private const string DarkestDungeonAppId = "262060";
    private const int MaxSummaryFilesPerLine = 32;
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromMilliseconds(250);
    private static readonly JsonSerializerOptions SessionJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    private readonly LauncherLog _log;
    private readonly List<string> _directories;
    private readonly string _gameWorkingDirectory;
    private readonly string _sessionDirectory;
    private readonly string _sessionId;
    private readonly DateTimeOffset _startedAt;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, SaveFileSnapshot> _initialSnapshot;
    private readonly Dictionary<string, int> _eventCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _recentEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private int? _gameProcessId;
    private int? _gameExitCode;
    private int _afterExitSeconds;
    private DateTimeOffset? _gameExitedAt;
    private bool _disposed;

    private SaveDirectoryWatcher(IReadOnlyList<string> directories, string logDirectory, string gameWorkingDirectory, LauncherLog log)
    {
        _directories = directories.ToList();
        _gameWorkingDirectory = gameWorkingDirectory;
        _sessionDirectory = Path.Combine(logDirectory, "save_sessions");
        _startedAt = DateTimeOffset.Now;
        _sessionId = $"{_startedAt:yyyyMMdd_HHmmss_fff}_launcher_{Environment.ProcessId}";
        _log = log;

        foreach (var directory in _directories)
        {
            RegisterDirectory(directory);
        }

        _initialSnapshot = CaptureSnapshot(_directories);
        _log.Info($"Save sidecar initial snapshot files={_initialSnapshot.Count}");
    }

    public static SaveDirectoryWatcher? Start(RuntimeConfig config, LauncherLog log)
    {
        if (!config.SaveWatchEnabled) return null;

        var directories = ResolveWatchDirectories(config, log);
        if (directories.Count == 0)
        {
            log.Warn("Save sidecar watcher is enabled, but no existing save directories were found.");
            return null;
        }

        log.Info($"Save sidecar watcher enabled. directories={directories.Count} afterExitSeconds={config.SaveWatchAfterExitSeconds}");
        foreach (var directory in directories)
        {
            log.Info($"Save sidecar watcher directory: {directory}");
        }

        return new SaveDirectoryWatcher(directories, config.LogDirectory, config.GameWorkingDirectory, log);
    }

    public void WaitForGameExit(int processId, int afterExitSeconds)
    {
        _gameProcessId = processId;
        _afterExitSeconds = afterExitSeconds;
        _log.Info($"Save sidecar watcher waiting for game process exit. PID={processId}");
        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit();
            var exitCode = TryReadExitCode(process);
            _gameExitedAt = DateTimeOffset.Now;
            _gameExitCode = exitCode;
            var exitCodeText = exitCode.HasValue ? exitCode.Value.ToString() : "unknown";
            _log.Info($"Game process exited. PID={processId} exitCode={exitCodeText}");
        }
        catch (ArgumentException)
        {
            _gameExitedAt = DateTimeOffset.Now;
            _log.Info($"Game process already exited. PID={processId}");
        }
        catch (InvalidOperationException ex)
        {
            _gameExitedAt = DateTimeOffset.Now;
            _log.Warn($"Could not wait for game process PID={processId}: {ex.Message}");
        }

        if (afterExitSeconds > 0)
        {
            _log.Info($"Continuing save sidecar watch for {afterExitSeconds} seconds after game exit.");
            Thread.Sleep(TimeSpan.FromSeconds(afterExitSeconds));
        }
    }

    private static int? TryReadExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        var finalSnapshot = CaptureSnapshot(_directories);
        var completedAt = DateTimeOffset.Now;
        var analysis = AnalyzeSnapshot(finalSnapshot);
        LogSnapshotDiff(analysis);
        WriteSessionReport(finalSnapshot.Count, completedAt, analysis);
    }

    private static List<string> ResolveWatchDirectories(RuntimeConfig config, LauncherLog log)
    {
        var configured = config.SaveWatchDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        var candidates = configured.Length > 0
            ? configured
            : DiscoverDefaultSaveDirectories(config.GameWorkingDirectory).ToArray();

        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (!Directory.Exists(fullPath))
            {
                log.Warn($"Save sidecar watcher skipped missing directory: {fullPath}");
                continue;
            }

            if (seen.Add(fullPath))
            {
                directories.Add(fullPath);
            }
        }

        return directories;
    }

    private static IEnumerable<string> DiscoverDefaultSaveDirectories(string gameWorkingDirectory)
    {
        var steamRoot = TryInferSteamRoot(gameWorkingDirectory);
        if (!string.IsNullOrWhiteSpace(steamRoot))
        {
            var userdataRoot = Path.Combine(steamRoot, "userdata");
            if (Directory.Exists(userdataRoot))
            {
                foreach (var userDirectory in Directory.EnumerateDirectories(userdataRoot))
                {
                    var remoteDirectory = Path.Combine(userDirectory, DarkestDungeonAppId, "remote");
                    if (Directory.Exists(remoteDirectory))
                    {
                        yield return remoteDirectory;
                    }
                }
            }
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents))
        {
            var darkestDocuments = Path.Combine(documents, "Darkest");
            if (Directory.Exists(darkestDocuments))
            {
                yield return darkestDocuments;
            }
        }
    }

    private static string? TryInferSteamRoot(string gameWorkingDirectory)
    {
        var directory = new DirectoryInfo(gameWorkingDirectory);
        while (directory is not null)
        {
            if (directory.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
            {
                return directory.Parent?.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private void RegisterDirectory(string directory)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
                | NotifyFilters.CreationTime
                | NotifyFilters.Attributes
        };

        watcher.Created += (_, e) => LogPathEvent("save.sidecar_created", e);
        watcher.Changed += (_, e) => LogPathEvent("save.sidecar_changed", e);
        watcher.Deleted += (_, e) => LogPathEvent("save.sidecar_deleted", e);
        watcher.Renamed += (_, e) => LogRenameEvent(e);
        watcher.Error += (_, e) =>
        {
            CountEvent("save.sidecar_error");
            _log.Warn($"event name=save.sidecar_error message={Quote(e.GetException().Message)}");
        };
        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
    }

    private void LogPathEvent(string eventName, FileSystemEventArgs e)
    {
        if (ShouldSuppress(eventName, e.FullPath)) return;
        CountEvent(eventName);
        _log.Info($"event name={eventName} change={e.ChangeType} {DescribePath(e.FullPath)}");
    }

    private void LogRenameEvent(RenamedEventArgs e)
    {
        if (ShouldSuppress("save.sidecar_renamed", e.FullPath)) return;
        CountEvent("save.sidecar_renamed");
        _log.Info($"event name=save.sidecar_renamed oldPath={Quote(e.OldFullPath)} {DescribePath(e.FullPath)}");
    }

    private void CountEvent(string eventName)
    {
        lock (_sync)
        {
            _eventCounts.TryGetValue(eventName, out var count);
            _eventCounts[eventName] = count + 1;
        }
    }

    private bool ShouldSuppress(string eventName, string path)
    {
        var key = $"{eventName}|{path}";
        var now = DateTimeOffset.UtcNow;

        lock (_sync)
        {
            if (_recentEvents.TryGetValue(key, out var previous) && now - previous < DedupeWindow)
            {
                return true;
            }

            _recentEvents[key] = now;
            if (_recentEvents.Count > 2048)
            {
                foreach (var stale in _recentEvents
                             .Where(pair => now - pair.Value > TimeSpan.FromSeconds(5))
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    _recentEvents.Remove(stale);
                }
            }

            return false;
        }
    }

    private SaveSessionAnalysis AnalyzeSnapshot(IReadOnlyDictionary<string, SaveFileSnapshot> finalSnapshot)
    {
        var created = 0;
        var changed = 0;
        var deleted = 0;
        var snapshotChanges = new List<SaveSnapshotChange>();

        foreach (var (path, snapshot) in finalSnapshot.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!_initialSnapshot.TryGetValue(path, out var initial))
            {
                created++;
                snapshotChanges.Add(SaveSnapshotChange.Created(path, snapshot));
            }
            else if (initial != snapshot)
            {
                changed++;
                snapshotChanges.Add(SaveSnapshotChange.Changed(path, initial, snapshot));
            }
        }

        foreach (var (path, snapshot) in _initialSnapshot.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!finalSnapshot.ContainsKey(path))
            {
                deleted++;
                snapshotChanges.Add(SaveSnapshotChange.Deleted(path, snapshot));
            }
        }

        var stableChanges = snapshotChanges
            .Select(change => StableSaveChange.TryCreate(change))
            .Where(change => change is not null)
            .Select(change => change!)
            .ToArray();
        var profiles = BuildProfileSummaries(stableChanges);
        var activeProfile = InferActiveProfile(profiles);

        return new SaveSessionAnalysis(created, changed, deleted, snapshotChanges, stableChanges, profiles, activeProfile);
    }

    private void LogSnapshotDiff(SaveSessionAnalysis analysis)
    {
        foreach (var change in analysis.SnapshotChanges.OrderBy(change => change.Path, StringComparer.OrdinalIgnoreCase))
        {
            switch (change.Kind)
            {
                case SaveSnapshotChangeKind.Created:
                    CountEvent("save.sidecar_snapshot_created");
                    _log.Info($"event name=save.sidecar_snapshot_created {DescribeSnapshot(change.Path, change.After!.Value)}");
                    break;
                case SaveSnapshotChangeKind.Changed:
                    CountEvent("save.sidecar_snapshot_changed");
                    _log.Info($"event name=save.sidecar_snapshot_changed oldSize={change.Before!.Value.Length} oldLastWriteUtc={change.Before.Value.LastWriteUtc:O} {DescribeSnapshot(change.Path, change.After!.Value)}");
                    break;
                case SaveSnapshotChangeKind.Deleted:
                    CountEvent("save.sidecar_snapshot_deleted");
                    _log.Info($"event name=save.sidecar_snapshot_deleted {DescribeSnapshot(change.Path, change.Before!.Value)}");
                    break;
            }
        }

        _log.Info($"Save sidecar final snapshot files={_initialSnapshot.Count + analysis.Created - analysis.Deleted} created={analysis.Created} changed={analysis.Changed} deleted={analysis.Deleted}");
        CountEvent("save.sidecar_session_summary");
        _log.Info($"event name=save.sidecar_session_summary snapshotChanges={analysis.SnapshotChanges.Count} stableJsonChanges={analysis.StableChanges.Count} transientOrUnknownChanges={analysis.TransientOrUnknownChanges}");

        foreach (var profile in analysis.Profiles)
        {
            CountEvent("save.sidecar_profile_summary");
            _log.Info(
                $"event name=save.sidecar_profile_summary profile={profile.Profile} root={Quote(profile.Root)} stableCreated={profile.StableCreated} stableChanged={profile.StableChanged} stableDeleted={profile.StableDeleted} live={profile.Live} backup={profile.Backup}");

            foreach (var fileGroup in profile.Files
                         .GroupBy(file => (file.Kind, file.Area))
                         .OrderBy(group => group.Key.Kind)
                         .ThenBy(group => group.Key.Area, StringComparer.OrdinalIgnoreCase))
            {
                var files = fileGroup
                    .Select(file => file.Path)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var listedFiles = files.Take(MaxSummaryFilesPerLine).ToArray();
                var omitted = Math.Max(0, files.Length - listedFiles.Length);

                CountEvent("save.sidecar_profile_files");
                _log.Info(
                    $"event name=save.sidecar_profile_files profile={profile.Profile} kind={fileGroup.Key.Kind.ToString().ToLowerInvariant()} area={fileGroup.Key.Area} count={files.Length} omitted={omitted} files={Quote(string.Join(';', listedFiles))}");
            }
        }

        if (analysis.ActiveProfile is not null)
        {
            CountEvent("save.sidecar_active_profile");
            _log.Info(
                $"event name=save.sidecar_active_profile profile={analysis.ActiveProfile.Profile} root={Quote(analysis.ActiveProfile.Root)} confidence={analysis.ActiveProfile.Confidence} score={analysis.ActiveProfile.Score} reasons={Quote(string.Join(';', analysis.ActiveProfile.Reasons))}");
        }
    }

    private static IReadOnlyList<SaveProfileSummary> BuildProfileSummaries(IReadOnlyList<StableSaveChange> stableChanges)
    {
        return stableChanges
            .GroupBy(change => (change.ProfileRoot, change.Profile), StableSaveChangeGroupComparer.Instance)
            .OrderBy(group => group.Key.ProfileRoot, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.Profile, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var files = group
                    .Select(change => new SaveProfileFileChange(
                        change.Kind,
                        change.Area,
                        change.RelativePath,
                        change.Before,
                        change.After))
                    .OrderBy(file => file.Area, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return new SaveProfileSummary(
                    group.Key.Profile,
                    group.Key.ProfileRoot,
                    files.Count(file => file.Kind == SaveSnapshotChangeKind.Created),
                    files.Count(file => file.Kind == SaveSnapshotChangeKind.Changed),
                    files.Count(file => file.Kind == SaveSnapshotChangeKind.Deleted),
                    files.Count(file => file.Area.Equals("live", StringComparison.OrdinalIgnoreCase)),
                    files.Count(file => file.Area.Equals("backup", StringComparison.OrdinalIgnoreCase)),
                    files);
            })
            .ToArray();
    }

    private static ActiveProfileInference? InferActiveProfile(IReadOnlyList<SaveProfileSummary> profiles)
    {
        var scoredProfiles = profiles
            .Select(ScoreProfile)
            .Where(profile => profile.Score > 0)
            .OrderByDescending(profile => profile.Score)
            .ThenBy(profile => profile.Profile, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (scoredProfiles.Length == 0)
        {
            return null;
        }

        var best = scoredProfiles[0];
        var secondScore = scoredProfiles.Length > 1 ? scoredProfiles[1].Score : 0;
        var margin = best.Score - secondScore;
        var confidence = best.Score >= 80 && margin >= 30
            ? "high"
            : best.Score >= 45 && margin >= 15
                ? "medium"
                : "low";

        var alternatives = scoredProfiles
            .Skip(1)
            .Select(profile => new ActiveProfileAlternative(profile.Profile, profile.Root, profile.Score))
            .ToArray();

        return new ActiveProfileInference(best.Profile, best.Root, confidence, best.Score, best.Reasons, alternatives);
    }

    private static ScoredProfile ScoreProfile(SaveProfileSummary profile)
    {
        var score = 0;
        var reasons = new List<string>();
        var liveFiles = profile.Files
            .Where(file => file.Area.Equals("live", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Path)
            .ToArray();
        var backupFiles = profile.Files
            .Where(file => file.Area.Equals("backup", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Path)
            .ToArray();

        if (liveFiles.Contains("persist.game.json", StringComparer.OrdinalIgnoreCase))
        {
            score += 50;
            reasons.Add("live persist.game.json changed");
        }

        if (liveFiles.Contains("persist.narration.json", StringComparer.OrdinalIgnoreCase))
        {
            score += 35;
            reasons.Add("live persist.narration.json changed");
        }

        var campaignFiles = new[]
        {
            "persist.town.json",
            "persist.roster.json",
            "persist.progression.json",
            "persist.quest.json",
            "persist.upgrades.json",
            "persist.estate.json"
        };
        var campaignHits = liveFiles.Count(file => campaignFiles.Contains(file, StringComparer.OrdinalIgnoreCase));
        if (campaignHits > 0)
        {
            score += campaignHits * 25;
            reasons.Add($"live campaign files changed={campaignHits}");
        }

        if (profile.Backup > 0)
        {
            var backupScore = Math.Min(25, profile.Backup * 2);
            score += backupScore;
            reasons.Add($"backup stable json changed={profile.Backup}");
        }

        if (profile.Live > 0)
        {
            var liveScore = Math.Min(20, profile.Live * 5);
            score += liveScore;
            reasons.Add($"live stable json changed={profile.Live}");
        }

        var knownAuxiliaryFiles = new[]
        {
            "persist.circus_estate.json",
            "persist.rankings.json",
            "persist.prize_booth.json",
            "persist.banner_custom.json",
            "persist.mp_progression.json"
        };
        if (liveFiles.Length > 0 && liveFiles.All(file => knownAuxiliaryFiles.Contains(file, StringComparer.OrdinalIgnoreCase)))
        {
            score = Math.Max(0, score - 20);
            reasons.Add("only auxiliary or circus files changed");
        }

        return new ScoredProfile(profile.Profile, profile.Root, score, reasons);
    }

    private void WriteSessionReport(int finalFileCount, DateTimeOffset completedAt, SaveSessionAnalysis analysis)
    {
        Directory.CreateDirectory(_sessionDirectory);
        var eventCounts = SnapshotEventCounts();
        var report = new SaveSessionReport(
            1,
            _sessionId,
            _startedAt,
            completedAt,
            new SaveSessionGameInfo(_gameProcessId, _gameExitedAt, _gameExitCode),
            new SaveSessionWatchInfo(_directories, _afterExitSeconds, _initialSnapshot.Count, finalFileCount),
            eventCounts,
            new SaveSessionSnapshotInfo(
                analysis.Created,
                analysis.Changed,
                analysis.Deleted,
                analysis.SnapshotChanges.Count,
                analysis.StableChanges.Count,
                analysis.TransientOrUnknownChanges),
            analysis.Profiles,
            analysis.ActiveProfile);
        var path = Path.Combine(_sessionDirectory, $"{_sessionId}.json");
        var json = JsonSerializer.Serialize(report, SessionJsonOptions);
        File.WriteAllText(path, json, Encoding.UTF8);
        CountEvent("save.sidecar_session_report_written");
        _log.Info($"event name=save.sidecar_session_report_written path={Quote(path)}");

        var stateReportPath = SaveStateExporter.TryWriteReport(_sessionDirectory, _sessionId, completedAt, report, _gameWorkingDirectory, _log);
        if (!string.IsNullOrWhiteSpace(stateReportPath))
        {
            CountEvent("save.state_report_written");
        }
    }

    private IReadOnlyDictionary<string, int> SnapshotEventCounts()
    {
        lock (_sync)
        {
            return _eventCounts
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, SaveFileSnapshot> CaptureSnapshot(IEnumerable<string> directories)
    {
        var snapshot = new Dictionary<string, SaveFileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var file = new FileInfo(path);
                        snapshot[path] = new SaveFileSnapshot(file.Length, file.LastWriteTimeUtc);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return snapshot;
    }

    private static string DescribePath(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                return DescribeSnapshot(path, new SaveFileSnapshot(file.Length, file.LastWriteTimeUtc));
            }

            if (Directory.Exists(path))
            {
                var directory = new DirectoryInfo(path);
                return $"path={Quote(path)} exists=1 directory=1 lastWriteUtc={directory.LastWriteTimeUtc:O}";
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return $"path={Quote(path)} exists=0";
    }

    private static string DescribeSnapshot(string path, SaveFileSnapshot snapshot)
    {
        return $"path={Quote(path)} exists=1 directory=0 size={snapshot.Length} lastWriteUtc={snapshot.LastWriteUtc:O}";
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static bool IsTransientSaveFileName(string fileName)
    {
        return fileName.EndsWith(".stmp", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("~RF", StringComparison.OrdinalIgnoreCase);
    }

    private enum SaveSnapshotChangeKind
    {
        Created,
        Changed,
        Deleted
    }

    private readonly record struct SaveFileSnapshot(long Length, DateTime LastWriteUtc);

    private sealed record SaveSessionAnalysis(
        int Created,
        int Changed,
        int Deleted,
        IReadOnlyList<SaveSnapshotChange> SnapshotChanges,
        IReadOnlyList<StableSaveChange> StableChanges,
        IReadOnlyList<SaveProfileSummary> Profiles,
        ActiveProfileInference? ActiveProfile)
    {
        public int TransientOrUnknownChanges => SnapshotChanges.Count - StableChanges.Count;
    }

    private readonly record struct SaveSnapshotChange(
        SaveSnapshotChangeKind Kind,
        string Path,
        SaveFileSnapshot? Before,
        SaveFileSnapshot? After)
    {
        public static SaveSnapshotChange Created(string path, SaveFileSnapshot after) =>
            new(SaveSnapshotChangeKind.Created, path, null, after);

        public static SaveSnapshotChange Changed(string path, SaveFileSnapshot before, SaveFileSnapshot after) =>
            new(SaveSnapshotChangeKind.Changed, path, before, after);

        public static SaveSnapshotChange Deleted(string path, SaveFileSnapshot before) =>
            new(SaveSnapshotChangeKind.Deleted, path, before, null);
    }

    private sealed record StableSaveChange(
        SaveSnapshotChangeKind Kind,
        string ProfileRoot,
        string Profile,
        string Area,
        string RelativePath,
        SaveFileSnapshot? Before,
        SaveFileSnapshot? After)
    {
        public static StableSaveChange? TryCreate(SaveSnapshotChange change)
        {
            var fileName = Path.GetFileName(change.Path);
            if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || IsTransientSaveFileName(fileName))
            {
                return null;
            }

            var file = new FileInfo(Path.GetFullPath(change.Path));
            var profileDirectory = file.Directory;
            while (profileDirectory is not null && !profileDirectory.Name.StartsWith("profile_", StringComparison.OrdinalIgnoreCase))
            {
                profileDirectory = profileDirectory.Parent;
            }

            if (profileDirectory is null)
            {
                return null;
            }

            var profile = profileDirectory.Name;
            var profileRoot = profileDirectory.FullName;
            var relativePath = Path.GetRelativePath(profileRoot, change.Path)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            var relativeParts = relativePath
                .Split('/')
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToArray();
            var area = relativeParts.Length > 1 && relativeParts[0].Equals("backup", StringComparison.OrdinalIgnoreCase)
                ? "backup"
                : "live";

            return new StableSaveChange(change.Kind, profileRoot, profile, area, relativePath, change.Before, change.After);
        }
    }

    private sealed record SaveProfileSummary(
        string Profile,
        string Root,
        int StableCreated,
        int StableChanged,
        int StableDeleted,
        int Live,
        int Backup,
        IReadOnlyList<SaveProfileFileChange> Files);

    private sealed record SaveProfileFileChange(
        SaveSnapshotChangeKind Kind,
        string Area,
        string Path,
        SaveFileSnapshot? Before,
        SaveFileSnapshot? After);

    private sealed record ScoredProfile(
        string Profile,
        string Root,
        int Score,
        IReadOnlyList<string> Reasons);

    private sealed record ActiveProfileInference(
        string Profile,
        string Root,
        string Confidence,
        int Score,
        IReadOnlyList<string> Reasons,
        IReadOnlyList<ActiveProfileAlternative> Alternatives);

    private sealed record ActiveProfileAlternative(
        string Profile,
        string Root,
        int Score);

    private sealed record SaveSessionReport(
        int Version,
        string SessionId,
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        SaveSessionGameInfo Game,
        SaveSessionWatchInfo Watch,
        IReadOnlyDictionary<string, int> EventCounts,
        SaveSessionSnapshotInfo Snapshot,
        IReadOnlyList<SaveProfileSummary> Profiles,
        ActiveProfileInference? ActiveProfile);

    private sealed record SaveSessionGameInfo(
        int? ProcessId,
        DateTimeOffset? ExitedAt,
        int? ExitCode);

    private sealed record SaveSessionWatchInfo(
        IReadOnlyList<string> Directories,
        int AfterExitSeconds,
        int InitialFileCount,
        int FinalFileCount);

    private sealed record SaveSessionSnapshotInfo(
        int Created,
        int Changed,
        int Deleted,
        int SnapshotChanges,
        int StableJsonChanges,
        int TransientOrUnknownChanges);

    private static class SaveStateExporter
    {
        private static readonly string[] CandidateFiles =
        [
            "persist.game.json",
            "persist.town.json",
            "persist.roster.json",
            "persist.progression.json",
            "persist.upgrades.json",
            "persist.estate.json"
        ];

        private static readonly string[] KnownMarkers =
        [
            "base_root",
            "version",
            "totalelapsed",
            "raiddungeon",
            "estatename",
            "game_mode",
            "date_time",
            "buildings",
            "heroes",
            "hero_file_data",
            "roster.status",
            "heroClass",
            "dungeon",
            "completed_plot_quests_data",
            "total_quests_finished",
            "last_quest_played_id",
            "purchases",
            "tree_id",
            "requirement_code",
            "is_purchased",
            "wallet",
            "amount",
            "type",
            "gold",
            "bust",
            "portrait",
            "deed",
            "crest",
            "shard",
            "memory",
            "blueprint"
        ];

        private static readonly string[] ValueCandidateKeys =
        [
            "estatename",
            "game_mode",
            "date_time",
            "raiddungeon",
            "dd_mode"
        ];

        private static readonly string[] FloatFieldNames =
        [
            "totalelapsed",
            "current_hp",
            "m_Stress"
        ];

        private static readonly string[] UInt32FieldNames =
        [
            "tree_id"
        ];

        private static readonly string[] SingleByteStringFieldNames =
        [
            "requirement_code"
        ];

        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        private const int MaxInlineValueDistance = 16;

        public static string? TryWriteReport(
            string sessionDirectory,
            string sessionId,
            DateTimeOffset generatedAt,
            SaveSessionReport sessionReport,
            string gameWorkingDirectory,
            LauncherLog log)
        {
            if (sessionReport.ActiveProfile is null)
            {
                log.Warn("event name=save.state_report_skipped reason=no_active_profile");
                return null;
            }

            var logDirectory = Directory.GetParent(sessionDirectory)?.FullName ?? sessionDirectory;
            var stateDirectory = Path.Combine(logDirectory, "save_states");
            Directory.CreateDirectory(stateDirectory);

            var accessIssues = new List<string>();
            var activeRoot = sessionReport.ActiveProfile.Root;
            if (!Directory.Exists(activeRoot))
            {
                accessIssues.Add($"Active profile directory was not found: {activeRoot}");
            }

            var fileReports = CandidateFiles
                .Select(name => InspectFile(Path.Combine(activeRoot, name), name))
                .ToArray();
            foreach (var issue in fileReports.SelectMany(file => file.AccessIssues))
            {
                accessIssues.Add(issue);
            }

            var upgradeCatalog = UpgradeDefinitionCatalog.Load(gameWorkingDirectory, accessIssues);

            var parseStatus = fileReports.Any(file => file.Format.Equals("jsonText", StringComparison.OrdinalIgnoreCase))
                ? "partialJsonText"
                : fileReports.Any(file => file.ParseStatus.Equals("dsonPartialDecoded", StringComparison.OrdinalIgnoreCase))
                    ? "dsonPartialDecoded"
                    : "binaryStringIndexOnly";
            if (fileReports.All(file => !file.Exists))
            {
                parseStatus = "noCandidateFiles";
            }
            var facts = BuildSaveStateFacts(fileReports, upgradeCatalog);

            var report = new SaveStateReport(
                1,
                sessionId,
                generatedAt,
                parseStatus,
                "Darkest Dungeon persist files use a DSON binary container despite the .json extension; this report is read-only and exports bounded DSON metadata, scalar samples, visible string candidates, and conservative state facts.",
                sessionReport.ActiveProfile,
                facts,
                CandidateFiles,
                fileReports,
                accessIssues);

            var path = Path.Combine(stateDirectory, $"{sessionId}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(report, SessionJsonOptions), Encoding.UTF8);
            log.Info($"event name=save.state_report_written path={Quote(path)} parseStatus={parseStatus} files={fileReports.Length} accessIssues={accessIssues.Count}");
            TryWriteFileMapReport(logDirectory, sessionId, generatedAt, sessionReport, activeRoot, fileReports, log);
            return path;
        }

        private static SaveStateFileReport InspectFile(string path, string fileName)
        {
            var accessIssues = new List<string>();
            if (!File.Exists(path))
            {
                accessIssues.Add($"Candidate file was not found: {path}");
                return new SaveStateFileReport(
                    fileName,
                    path,
                    false,
                    null,
                    null,
                    null,
                    "missing",
                    "missing",
                    null,
                    null,
                    null,
                    null,
                    null,
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    accessIssues);
            }

            try
            {
                var bytes = File.ReadAllBytes(path);
                var info = new FileInfo(path);
                var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                var firstByte = bytes.FirstOrDefault();
                if (LooksLikeJsonText(bytes))
                {
                    return InspectJsonText(path, fileName, bytes, info, sha256, accessIssues);
                }

                var container = TryParseBinaryContainer(bytes, accessIssues);
                var strings = container?.Strings ?? ExtractPrintableStrings(bytes);
                var printableStrings = container is null ? strings : ExtractPrintableStrings(bytes);
                var markerSet = KnownMarkers.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var markers = strings
                    .Select(item => item.Value)
                    .Where(value => markerSet.Contains(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(120)
                    .ToArray();
                var valueCandidates = ExtractValueCandidates(strings, printableStrings);
                var samples = strings
                    .Select(item => item.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(120)
                    .ToArray();
                var heroFacts = ExtractHeroFactsFromRoster(fileName, bytes, container, accessIssues);
                var parseStatus = container is not null
                    ? "dsonPartialDecoded"
                    : firstByte == 0x01 ? "binaryStringIndexOnly" : "unknownBinary";

                return new SaveStateFileReport(
                    fileName,
                    path,
                    true,
                    info.Length,
                    info.LastWriteTimeUtc,
                    sha256,
                    "binaryContainer",
                    parseStatus,
                    ToHex(bytes.Take(32)),
                    container?.StringCount,
                    container?.StringIndexOffset,
                    container?.StringDataOffset,
                    container?.DsonSummary,
                    container?.DsonScalars.Take(320).ToArray() ?? [],
                    container?.DsonScalars ?? [],
                    container?.DsonObjectPaths.Take(1000).ToArray() ?? [],
                    heroFacts,
                    [],
                    markers,
                    valueCandidates,
                    samples,
                    container?.Strings.Take(240).ToArray() ?? [],
                    accessIssues);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                accessIssues.Add($"{fileName}: {ex.Message}");
                return new SaveStateFileReport(
                    fileName,
                    path,
                    true,
                    null,
                    null,
                    null,
                    "unreadable",
                    "error",
                    null,
                    null,
                    null,
                    null,
                    null,
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    accessIssues);
            }
        }

        private static void TryWriteFileMapReport(
            string logDirectory,
            string sessionId,
            DateTimeOffset generatedAt,
            SaveSessionReport sessionReport,
            string activeRoot,
            IReadOnlyList<SaveStateFileReport> analyzedFiles,
            LauncherLog log)
        {
            if (sessionReport.ActiveProfile is null)
            {
                return;
            }

            var accessIssues = new List<string>();
            var entries = new List<SaveFileMapEntry>();
            var analyzedByName = analyzedFiles.ToDictionary(file => file.FileName, StringComparer.OrdinalIgnoreCase);

            if (!Directory.Exists(activeRoot))
            {
                accessIssues.Add($"Active profile directory was not found: {activeRoot}");
            }
            else
            {
                foreach (var source in EnumeratePersistFiles(activeRoot))
                {
                    SaveStateFileReport inspected;
                    if (source.Area.Equals("live", StringComparison.OrdinalIgnoreCase)
                        && analyzedByName.TryGetValue(source.FileName, out var cached))
                    {
                        inspected = cached;
                    }
                    else
                    {
                        inspected = InspectFile(source.Path, source.FileName);
                    }

                    foreach (var issue in inspected.AccessIssues)
                    {
                        accessIssues.Add(issue);
                    }

                    entries.Add(BuildFileMapEntry(source, inspected));
                }
            }

            var mapDirectory = Path.Combine(logDirectory, "save_file_maps");
            Directory.CreateDirectory(mapDirectory);
            var report = new SaveFileMapReport(
                1,
                sessionId,
                generatedAt,
                sessionReport.ActiveProfile,
                activeRoot,
                CandidateFiles,
                entries
                    .OrderBy(entry => entry.Area, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Priority)
                    .ThenBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                accessIssues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

            var path = Path.Combine(mapDirectory, $"{sessionId}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(report, SessionJsonOptions), Encoding.UTF8);
            log.Info($"event name=save.file_map_report_written path={Quote(path)} files={entries.Count} live={entries.Count(entry => entry.Area.Equals("live", StringComparison.OrdinalIgnoreCase))} backup={entries.Count(entry => entry.Area.Equals("backup", StringComparison.OrdinalIgnoreCase))} accessIssues={accessIssues.Count}");
        }

        private static IEnumerable<SaveFileMapSource> EnumeratePersistFiles(string activeRoot)
        {
            foreach (var path in Directory.EnumerateFiles(activeRoot, "persist*.json", SearchOption.TopDirectoryOnly))
            {
                yield return new SaveFileMapSource(path, Path.GetFileName(path), Path.GetFileName(path), "live");
            }

            var backupRoot = Path.Combine(activeRoot, "backup");
            if (!Directory.Exists(backupRoot))
            {
                yield break;
            }

            foreach (var path in Directory.EnumerateFiles(backupRoot, "persist*.json", SearchOption.TopDirectoryOnly))
            {
                yield return new SaveFileMapSource(path, Path.GetFileName(path), Path.Combine("backup", Path.GetFileName(path)), "backup");
            }
        }

        private static SaveFileMapEntry BuildFileMapEntry(SaveFileMapSource source, SaveStateFileReport inspected)
        {
            var classification = ClassifyPersistFile(source.FileName);
            var isCandidate = CandidateFiles.Contains(source.FileName, StringComparer.OrdinalIgnoreCase);
            var coverage = DetermineFileCoverage(source.FileName, isCandidate, inspected);

            return new SaveFileMapEntry(
                source.FileName,
                source.RelativePath,
                source.Area,
                inspected.Path,
                inspected.Exists,
                inspected.Length,
                inspected.LastWriteUtc,
                inspected.Sha256,
                inspected.Format,
                inspected.ParseStatus,
                isCandidate,
                classification.Priority,
                classification.Category,
                classification.ModRelevance,
                coverage,
                inspected.DsonSummary,
                inspected.MarkerStrings,
                inspected.ValueCandidates.Select(candidate => candidate.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                inspected.DsonScalars.Count,
                inspected.DsonObjectPaths.Count,
                inspected.AccessIssues);
        }

        private static SaveFileMapClassification ClassifyPersistFile(string fileName)
        {
            return fileName.ToLowerInvariant() switch
            {
                "persist.game.json" => new SaveFileMapClassification(1, "campaign_runtime", "Campaign identity, mode, elapsed time, current raid state, and game options."),
                "persist.estate.json" => new SaveFileMapClassification(1, "estate_resources", "Wallet resources and estate-level inventory/tamper metadata."),
                "persist.roster.json" => new SaveFileMapClassification(1, "heroes", "Hero roster entry points and partially decoded nested hero raw_data facts."),
                "persist.upgrades.json" => new SaveFileMapClassification(2, "upgrade_tree", "Building purchase tree and upgrade unlock state; tree_id is numeric until static definitions are mapped."),
                "persist.quest.json" => new SaveFileMapClassification(3, "quests", "Quest generation, available missions, and dungeon selection state."),
                "persist.town_event.json" => new SaveFileMapClassification(3, "town_events", "Current and historical town event state."),
                "persist.town.json" => new SaveFileMapClassification(4, "town_runtime", "Hamlet buildings, shops, activity slots, inventories, and runtime town state."),
                "persist.progression.json" => new SaveFileMapClassification(5, "progression", "Dungeon XP, boss/story progression, quest history, and unlock conditions."),
                "persist.game_knowledge.json" => new SaveFileMapClassification(6, "knowledge", "Discovered game knowledge and UI reveal state."),
                "persist.journal.json" => new SaveFileMapClassification(6, "journal", "Collected journal pages and related discovery state."),
                "persist.narration.json" => new SaveFileMapClassification(6, "narration", "Narration playback and bark/history gating state."),
                "persist.tutorial.json" => new SaveFileMapClassification(6, "tutorial", "Tutorial prompt completion and gating state."),
                "persist.campaign_log.json" => new SaveFileMapClassification(7, "history_log", "Campaign history/log data, likely secondary for runtime rules."),
                "persist.campaign_mash.json" => new SaveFileMapClassification(7, "history_log", "Campaign aggregate/log companion data, likely secondary for runtime rules."),
                _ => new SaveFileMapClassification(9, "unknown", "Unclassified persist data; inspect when a mod idea needs it.")
            };
        }

        private static string DetermineFileCoverage(string fileName, bool isCandidate, SaveStateFileReport inspected)
        {
            if (!inspected.Exists)
            {
                return "missing";
            }

            if (inspected.ParseStatus.Equals("dsonPartialDecoded", StringComparison.OrdinalIgnoreCase))
            {
                if (fileName.Equals("persist.roster.json", StringComparison.OrdinalIgnoreCase)
                    && inspected.DsonSummary?.RawScalarCount > 0)
                {
                    if (inspected.Heroes.Count > 0)
                    {
                        return "candidate_nested_dson_partial";
                    }

                    return "candidate_nested_raw_pending";
                }

                if (fileName.Equals("persist.upgrades.json", StringComparison.OrdinalIgnoreCase)
                    && inspected.DsonSummary?.RawScalarCount > 0)
                {
                    return "candidate_upgrade_purchases_partial";
                }

                return isCandidate ? "candidate_dson_partial" : "mapped_dson_partial";
            }

            if (inspected.Format.Equals("jsonText", StringComparison.OrdinalIgnoreCase))
            {
                return isCandidate ? "candidate_json_text" : "mapped_json_text";
            }

            return isCandidate ? "candidate_unresolved" : "mapped_unresolved";
        }

        private static SaveStateFileReport InspectJsonText(
            string path,
            string fileName,
            byte[] bytes,
            FileInfo info,
            string sha256,
            IReadOnlyList<string> accessIssues)
        {
            using var document = JsonDocument.Parse(bytes);
            var topLevelKeys = document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject().Select(property => property.Name).Take(120).ToArray()
                : [];

            return new SaveStateFileReport(
                fileName,
                path,
                true,
                info.Length,
                info.LastWriteTimeUtc,
                sha256,
                "jsonText",
                "parsedJsonText",
                null,
                null,
                null,
                null,
                null,
                [],
                [],
                [],
                [],
                topLevelKeys,
                [],
                [],
                [],
                [],
                accessIssues);
        }

        private static bool LooksLikeJsonText(byte[] bytes)
        {
            foreach (var b in bytes)
            {
                if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                {
                    continue;
                }

                return b is (byte)'{' or (byte)'[';
            }

            return false;
        }

        private static SaveStateFacts BuildSaveStateFacts(IReadOnlyList<SaveStateFileReport> files, UpgradeDefinitionCatalog upgradeCatalog)
        {
            var game = files.FirstOrDefault(file => file.FileName.Equals("persist.game.json", StringComparison.OrdinalIgnoreCase));
            var progression = files.FirstOrDefault(file => file.FileName.Equals("persist.progression.json", StringComparison.OrdinalIgnoreCase));
            var estate = files.FirstOrDefault(file => file.FileName.Equals("persist.estate.json", StringComparison.OrdinalIgnoreCase));
            var upgrades = files.FirstOrDefault(file => file.FileName.Equals("persist.upgrades.json", StringComparison.OrdinalIgnoreCase));
            var town = files.FirstOrDefault(file => file.FileName.Equals("persist.town.json", StringComparison.OrdinalIgnoreCase));
            var roster = files.FirstOrDefault(file => file.FileName.Equals("persist.roster.json", StringComparison.OrdinalIgnoreCase));

            return new SaveStateFacts(
                new SaveStateCampaignFacts(
                    TryGetInt(game, "base_root.version"),
                    TryGetDouble(game, "base_root.totalelapsed"),
                    TryGetBool(game, "base_root.inraid"),
                    TryGetString(game, "base_root.raiddungeon"),
                    TryGetString(game, "base_root.estatename"),
                    TryGetString(game, "base_root.game_mode"),
                    TryGetString(game, "base_root.date_time"),
                    TryGetString(game, "base_root.town_events"),
                    TryGetString(game, "base_root.never_again")),
                new SaveStateProgressionFacts(
                    TryGetInt(progression, "base_root.total_quests_finished"),
                    TryGetInt(progression, "base_root.total_successful_quests_finished"),
                    TryGetInt(progression, "base_root.last_quest_played_id"),
                    TryGetInt(progression, "base_root.last_quest_played_xp"),
                    TryGetBool(progression, "base_root.last_raid_success"),
                    TryGetBool(progression, "base_root.last_raid_was_a_plot_quest")),
                BuildWalletFacts(estate),
                BuildUpgradeFacts(upgrades, upgradeCatalog),
                ExtractDirectChildIds(town?.DsonObjectPaths ?? [], "base_root.buildings"),
                ExtractDirectChildIds(roster?.DsonObjectPaths ?? [], "base_root.heroes"),
                roster?.Heroes ?? []);
        }

        private static IReadOnlyDictionary<string, int> BuildWalletFacts(SaveStateFileReport? estate)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (estate is null)
            {
                return result;
            }

            var scalars = GetDsonScalars(estate);
            var byPath = scalars.ToDictionary(scalar => scalar.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var typeScalar in scalars.Where(scalar => scalar.Path.StartsWith("base_root.wallet.", StringComparison.OrdinalIgnoreCase)
                         && scalar.Path.EndsWith(".type", StringComparison.OrdinalIgnoreCase)
                         && scalar.Type.Equals("string", StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(scalar.Value)))
            {
                var prefix = typeScalar.Path[..^".type".Length];
                if (!byPath.TryGetValue($"{prefix}.amount", out var amountScalar)
                    || !int.TryParse(amountScalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
                {
                    continue;
                }

                result[typeScalar.Value!] = amount;
            }

            return result;
        }

        private static SaveStateUpgradeFacts BuildUpgradeFacts(SaveStateFileReport? upgrades, UpgradeDefinitionCatalog upgradeCatalog)
        {
            if (upgrades is null)
            {
                return EmptyUpgradeFacts(null, upgradeCatalog);
            }

            var purchaseIds = MergeAllDirectChildIds(
                    ExtractAllDirectChildIds(upgrades.DsonObjectPaths, "base_root.purchases"),
                    ExtractAllDirectChildIds(GetDsonScalars(upgrades), "base_root.purchases"))
                .Select(id => int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : (int?)null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .OrderBy(id => id)
                .ToArray();
            if (purchaseIds.Length == 0)
            {
                return EmptyUpgradeFacts(TryGetInt(upgrades, "base_root.version"), upgradeCatalog);
            }

            var purchases = new List<SaveStateUpgradePurchaseFacts>();
            foreach (var purchaseId in purchaseIds)
            {
                var path = $"base_root.purchases.{purchaseId.ToString(CultureInfo.InvariantCulture)}";
                var treeId = TryGetUInt(upgrades, $"{path}.tree_id");
                var requirementCode = TryGetString(upgrades, $"{path}.requirement_code");
                var lookup = treeId.HasValue ? upgradeCatalog.Find(treeId.Value) : null;
                var definition = lookup is { IsAmbiguous: false } ? lookup.PreferredDefinition : null;
                purchases.Add(new SaveStateUpgradePurchaseFacts(
                    purchaseId,
                    TryGetInt(upgrades, $"{path}.instance_number"),
                    treeId,
                    ResolveTreeName(lookup),
                    lookup?.IsAmbiguous == true,
                    definition?.SourceRelativePath,
                    definition?.Tags ?? [],
                    BuildRequirementDefinitionFacts(definition, requirementCode),
                    requirementCode,
                    TryGetBool(upgrades, $"{path}.is_purchased")));
            }

            var treeFacts = purchases
                .Where(purchase => purchase.TreeId.HasValue)
                .GroupBy(purchase => purchase.TreeId!.Value)
                .OrderBy(group => group.Key)
                .Select(group =>
                {
                    var groupPurchases = group.ToArray();
                    var lookup = upgradeCatalog.Find(group.Key);
                    var definition = lookup is { IsAmbiguous: false } ? lookup.PreferredDefinition : null;
                    return new SaveStateUpgradeTreeFacts(
                        group.Key,
                        ResolveTreeName(lookup),
                        lookup?.IsAmbiguous == true,
                        definition?.SourceRelativePath,
                        definition?.IsInstanced,
                        definition?.Tags ?? [],
                        definition?.Requirements.Select(BuildRequirementDefinitionFacts).ToArray() ?? [],
                        groupPurchases.Length,
                        groupPurchases.Count(purchase => purchase.IsPurchased == true),
                        groupPurchases.Count(purchase => purchase.IsPurchased == false),
                        groupPurchases
                            .Select(purchase => purchase.InstanceNumber)
                            .Where(value => value.HasValue)
                            .Select(value => value!.Value)
                            .Distinct()
                            .OrderBy(value => value)
                            .ToArray(),
                        groupPurchases
                            .Select(purchase => purchase.RequirementCode)
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Select(value => value!)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        groupPurchases
                            .Where(purchase => purchase.IsPurchased == true)
                            .Select(purchase => purchase.RequirementCode)
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Select(value => value!)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                            .ToArray());
                })
                .ToArray();

            return new SaveStateUpgradeFacts(
                TryGetInt(upgrades, "base_root.version"),
                purchases.Count,
                purchases.Count(purchase => purchase.IsPurchased == true),
                purchases.Count(purchase => purchase.IsPurchased == false),
                purchases.Count(purchase => !purchase.IsPurchased.HasValue),
                treeFacts.Length,
                upgradeCatalog.SourceFileCount,
                upgradeCatalog.DefinitionCount,
                upgradeCatalog.NameCandidateCount,
                treeFacts.Count(tree => !string.IsNullOrWhiteSpace(tree.TreeName) && !tree.TreeNameAmbiguous),
                treeFacts.Count(tree => string.IsNullOrWhiteSpace(tree.TreeName)),
                treeFacts.Count(tree => tree.TreeNameAmbiguous),
                purchases.Take(1000).ToArray(),
                treeFacts.Take(1000).ToArray());
        }

        private static SaveStateUpgradeFacts EmptyUpgradeFacts(int? version, UpgradeDefinitionCatalog upgradeCatalog)
        {
            return new SaveStateUpgradeFacts(
                version,
                0,
                0,
                0,
                0,
                0,
                upgradeCatalog.SourceFileCount,
                upgradeCatalog.DefinitionCount,
                upgradeCatalog.NameCandidateCount,
                0,
                0,
                0,
                [],
                []);
        }

        private static string? ResolveTreeName(UpgradeDefinitionLookup? lookup)
        {
            return lookup is { IsAmbiguous: false }
                ? lookup.PreferredDefinition?.Id
                : null;
        }

        private static SaveStateUpgradeRequirementDefinitionFacts? BuildRequirementDefinitionFacts(
            UpgradeTreeDefinition? definition,
            string? requirementCode)
        {
            if (definition is null || string.IsNullOrWhiteSpace(requirementCode))
            {
                return null;
            }

            return definition.Requirements
                .FirstOrDefault(requirement => requirement.Code.Equals(requirementCode, StringComparison.OrdinalIgnoreCase))
                is { } requirement
                ? BuildRequirementDefinitionFacts(requirement)
                : null;
        }

        private static SaveStateUpgradeRequirementDefinitionFacts BuildRequirementDefinitionFacts(UpgradeRequirementDefinition requirement)
        {
            return new SaveStateUpgradeRequirementDefinitionFacts(
                requirement.Code,
                requirement.CurrencyCost,
                requirement.PrerequisiteResolveLevel,
                requirement.Prerequisites
                    .Select(prerequisite => new SaveStateUpgradePrerequisiteDefinitionFacts(prerequisite.TreeId, prerequisite.RequirementCode))
                    .ToArray());
        }

        private sealed class UpgradeDefinitionCatalog
        {
            private static readonly JsonDocumentOptions UpgradeJsonOptions = new()
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };

            private readonly IReadOnlyDictionary<uint, UpgradeDefinitionLookup> _byTreeId;

            private UpgradeDefinitionCatalog(
                IReadOnlyDictionary<uint, UpgradeDefinitionLookup> byTreeId,
                int sourceFileCount,
                int definitionCount,
                int nameCandidateCount)
            {
                _byTreeId = byTreeId;
                SourceFileCount = sourceFileCount;
                DefinitionCount = definitionCount;
                NameCandidateCount = nameCandidateCount;
            }

            public int SourceFileCount { get; }

            public int DefinitionCount { get; }

            public int NameCandidateCount { get; }

            public static UpgradeDefinitionCatalog Load(string gameWorkingDirectory, List<string> accessIssues)
            {
                if (string.IsNullOrWhiteSpace(gameWorkingDirectory) || !Directory.Exists(gameWorkingDirectory))
                {
                    accessIssues.Add($"Upgrade definition catalog skipped because game directory was not found: {gameWorkingDirectory}");
                    return new UpgradeDefinitionCatalog(new Dictionary<uint, UpgradeDefinitionLookup>(), 0, 0, 0);
                }

                var definitions = new List<UpgradeTreeDefinition>();
                var sourceFileCount = 0;
                foreach (var path in Directory.EnumerateFiles(gameWorkingDirectory, "*.upgrades.json", SearchOption.AllDirectories)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var fileDefinitions = ReadUpgradeDefinitionFile(gameWorkingDirectory, path);
                        if (fileDefinitions.Count == 0)
                        {
                            continue;
                        }

                        sourceFileCount++;
                        definitions.AddRange(fileDefinitions);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        accessIssues.Add($"Upgrade definition catalog skipped file {path}: {ex.Message}");
                    }
                }

                foreach (var path in Directory.EnumerateFiles(gameWorkingDirectory, "*.camping_skills.json", SearchOption.AllDirectories)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var fileDefinitions = ReadCampingSkillDefinitionFile(gameWorkingDirectory, path);
                        if (fileDefinitions.Count == 0)
                        {
                            continue;
                        }

                        sourceFileCount++;
                        definitions.AddRange(fileDefinitions);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        accessIssues.Add($"Upgrade definition catalog skipped camping file {path}: {ex.Message}");
                    }
                }

                foreach (var path in Directory.EnumerateFiles(gameWorkingDirectory, "persist.upgrades.json", SearchOption.AllDirectories)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        definitions.AddRange(ReadStartingSaveUpgradeAliases(gameWorkingDirectory, path));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        accessIssues.Add($"Upgrade definition catalog skipped save template file {path}: {ex.Message}");
                    }
                }

                var byTreeId = definitions
                    .GroupBy(definition => definition.TreeId)
                    .ToDictionary(
                        group => group.Key,
                        group => new UpgradeDefinitionLookup(group.Key, group.ToArray()),
                        EqualityComparer<uint>.Default);

                return new UpgradeDefinitionCatalog(
                    byTreeId,
                    sourceFileCount,
                    definitions.Count(definition => definition.HasDefinition),
                    definitions.Count(definition => !definition.HasDefinition));
            }

            public UpgradeDefinitionLookup? Find(uint treeId)
            {
                return _byTreeId.TryGetValue(treeId, out var lookup) ? lookup : null;
            }

            private static IReadOnlyList<UpgradeTreeDefinition> ReadUpgradeDefinitionFile(string gameWorkingDirectory, string path)
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path), UpgradeJsonOptions);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("trees", out var treesElement)
                    || treesElement.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                var relativePath = Path.GetRelativePath(gameWorkingDirectory, path)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                var sourcePriority = GetUpgradeDefinitionSourcePriority(relativePath);
                var definitions = new List<UpgradeTreeDefinition>();
                foreach (var treeElement in treesElement.EnumerateArray())
                {
                    if (treeElement.ValueKind != JsonValueKind.Object
                        || !treeElement.TryGetProperty("id", out var idElement)
                        || idElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var id = idElement.GetString();
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    var isInstanced = TryGetBool(treeElement, "is_instanced");
                    var tags = treeElement.TryGetProperty("tags", out var tagsElement)
                        ? ReadStringArray(tagsElement)
                        : [];
                    var requirements = treeElement.TryGetProperty("requirements", out var requirementsElement)
                        ? ReadRequirements(requirementsElement)
                        : [];

                    definitions.Add(new UpgradeTreeDefinition(
                        id,
                        HashDsonName(id),
                        isInstanced,
                        tags,
                        relativePath,
                        sourcePriority,
                        requirements,
                        true,
                        "upgrade_tree"));
                }

                return definitions;
            }

            private static IReadOnlyList<UpgradeTreeDefinition> ReadCampingSkillDefinitionFile(string gameWorkingDirectory, string path)
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path), UpgradeJsonOptions);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("skills", out var skillsElement)
                    || skillsElement.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                var relativePath = Path.GetRelativePath(gameWorkingDirectory, path)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                var sourcePriority = GetUpgradeDefinitionSourcePriority(relativePath);
                var definitions = new List<UpgradeTreeDefinition>();
                foreach (var skillElement in skillsElement.EnumerateArray())
                {
                    if (skillElement.ValueKind != JsonValueKind.Object
                        || !skillElement.TryGetProperty("id", out var idElement)
                        || idElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var skillId = idElement.GetString();
                    if (string.IsNullOrWhiteSpace(skillId))
                    {
                        continue;
                    }

                    var heroClasses = skillElement.TryGetProperty("hero_classes", out var heroClassesElement)
                        ? ReadStringArray(heroClassesElement)
                        : [];
                    if (heroClasses.Count == 0)
                    {
                        continue;
                    }

                    var requirements = skillElement.TryGetProperty("upgrade_requirements", out var requirementsElement)
                        ? ReadRequirements(requirementsElement)
                        : [];
                    foreach (var heroClass in heroClasses)
                    {
                        var id = $"{heroClass}.{skillId}";
                        definitions.Add(new UpgradeTreeDefinition(
                            id,
                            HashDsonName(id),
                            true,
                            ["camping_skill"],
                            relativePath,
                            sourcePriority,
                            requirements,
                            true,
                            "camping_skill"));
                    }
                }

                return definitions;
            }

            private static IReadOnlyList<UpgradeTreeDefinition> ReadStartingSaveUpgradeAliases(string gameWorkingDirectory, string path)
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path), UpgradeJsonOptions);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("data", out var dataElement)
                    || dataElement.ValueKind != JsonValueKind.Object
                    || !dataElement.TryGetProperty("purchases", out var purchasesElement)
                    || purchasesElement.ValueKind != JsonValueKind.Object)
                {
                    return [];
                }

                var relativePath = Path.GetRelativePath(gameWorkingDirectory, path)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                var definitions = new List<UpgradeTreeDefinition>();
                foreach (var purchaseProperty in purchasesElement.EnumerateObject())
                {
                    if (string.IsNullOrWhiteSpace(purchaseProperty.Name)
                        || purchaseProperty.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var treeId = TryGetUInt(purchaseProperty.Value, "tree_id") ?? HashDsonName(purchaseProperty.Name);
                    definitions.Add(new UpgradeTreeDefinition(
                        purchaseProperty.Name,
                        treeId,
                        null,
                        [],
                        relativePath,
                        -100,
                        [],
                        false,
                        "starting_save_alias"));
                }

                return definitions;
            }

            private static int GetUpgradeDefinitionSourcePriority(string relativePath)
            {
                if (relativePath.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
                {
                    return 30;
                }

                if (relativePath.Contains("/modes/", StringComparison.OrdinalIgnoreCase))
                {
                    return 20;
                }

                if (relativePath.StartsWith("dlc/", StringComparison.OrdinalIgnoreCase))
                {
                    return 10;
                }

                return 0;
            }

            private static IReadOnlyList<UpgradeRequirementDefinition> ReadRequirements(JsonElement requirementsElement)
            {
                if (requirementsElement.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                var requirements = new List<UpgradeRequirementDefinition>();
                foreach (var requirementElement in requirementsElement.EnumerateArray())
                {
                    if (requirementElement.ValueKind != JsonValueKind.Object
                        || !requirementElement.TryGetProperty("code", out var codeElement)
                        || codeElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var code = codeElement.GetString();
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        continue;
                    }

                    requirements.Add(new UpgradeRequirementDefinition(
                        code,
                        requirementElement.TryGetProperty("currency_cost", out var currencyElement)
                            ? ReadCurrencyCost(currencyElement)
                            : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                        TryGetInt(requirementElement, "prerequisite_resolve_level"),
                        requirementElement.TryGetProperty("prerequisite_requirements", out var prerequisitesElement)
                            ? ReadPrerequisites(prerequisitesElement)
                            : []));
                }

                return requirements;
            }

            private static IReadOnlyDictionary<string, int> ReadCurrencyCost(JsonElement currencyElement)
            {
                var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (currencyElement.ValueKind != JsonValueKind.Array)
                {
                    return result;
                }

                foreach (var entry in currencyElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object
                        || !entry.TryGetProperty("type", out var typeElement)
                        || typeElement.ValueKind != JsonValueKind.String
                        || !entry.TryGetProperty("amount", out var amountElement)
                        || amountElement.ValueKind != JsonValueKind.Number
                        || !amountElement.TryGetInt32(out var amount))
                    {
                        continue;
                    }

                    var type = typeElement.GetString();
                    if (!string.IsNullOrWhiteSpace(type))
                    {
                        result[type] = amount;
                    }
                }

                return result;
            }

            private static IReadOnlyList<UpgradePrerequisiteDefinition> ReadPrerequisites(JsonElement prerequisitesElement)
            {
                if (prerequisitesElement.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                var prerequisites = new List<UpgradePrerequisiteDefinition>();
                foreach (var prerequisiteElement in prerequisitesElement.EnumerateArray())
                {
                    if (prerequisiteElement.ValueKind != JsonValueKind.Object
                        || !prerequisiteElement.TryGetProperty("tree_id", out var treeElement)
                        || treeElement.ValueKind != JsonValueKind.String
                        || !prerequisiteElement.TryGetProperty("requirement_code", out var codeElement)
                        || codeElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var treeId = treeElement.GetString();
                    var code = codeElement.GetString();
                    if (!string.IsNullOrWhiteSpace(treeId) && !string.IsNullOrWhiteSpace(code))
                    {
                        prerequisites.Add(new UpgradePrerequisiteDefinition(treeId, code));
                    }
                }

                return prerequisites;
            }

            private static IReadOnlyList<string> ReadStringArray(JsonElement element)
            {
                if (element.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                return element
                    .EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            private static bool? TryGetBool(JsonElement element, string propertyName)
            {
                return element.TryGetProperty(propertyName, out var property)
                    ? property.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null
                    }
                    : null;
            }

            private static int? TryGetInt(JsonElement element, string propertyName)
            {
                return element.TryGetProperty(propertyName, out var property)
                    && property.ValueKind == JsonValueKind.Number
                    && property.TryGetInt32(out var value)
                    ? value
                    : null;
            }

            private static uint? TryGetUInt(JsonElement element, string propertyName)
            {
                return element.TryGetProperty(propertyName, out var property)
                    && property.ValueKind == JsonValueKind.Number
                    && property.TryGetUInt32(out var value)
                    ? value
                    : null;
            }
        }

        private static uint HashDsonName(string value)
        {
            var hash = 0u;
            foreach (var b in Encoding.UTF8.GetBytes(value))
            {
                unchecked
                {
                    hash = hash * 53u + b;
                }
            }

            return hash;
        }

        private sealed record UpgradeDefinitionLookup(
            uint TreeId,
            IReadOnlyList<UpgradeTreeDefinition> Definitions)
        {
            public bool IsAmbiguous { get; } = Definitions
                .Select(definition => definition.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .Any();

            public UpgradeTreeDefinition? PreferredDefinition { get; } = Definitions
                .GroupBy(definition => definition.Id, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Any(definition => definition.HasDefinition))
                .ThenByDescending(group => group.Max(definition => definition.SourcePriority))
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(definition => definition.HasDefinition)
                    .ThenByDescending(definition => definition.SourcePriority)
                    .ThenBy(definition => definition.SourceRelativePath, StringComparer.OrdinalIgnoreCase)
                    .First())
                .FirstOrDefault();
        }

        private sealed record UpgradeTreeDefinition(
            string Id,
            uint TreeId,
            bool? IsInstanced,
            IReadOnlyList<string> Tags,
            string SourceRelativePath,
            int SourcePriority,
            IReadOnlyList<UpgradeRequirementDefinition> Requirements,
            bool HasDefinition,
            string DefinitionKind);

        private sealed record UpgradeRequirementDefinition(
            string Code,
            IReadOnlyDictionary<string, int> CurrencyCost,
            int? PrerequisiteResolveLevel,
            IReadOnlyList<UpgradePrerequisiteDefinition> Prerequisites);

        private sealed record UpgradePrerequisiteDefinition(
            string TreeId,
            string RequirementCode);

        private static IReadOnlyList<string> ExtractDirectChildIds(IReadOnlyList<string> paths, string parentPath)
        {
            return ExtractDirectChildIds(paths, parentPath, 120);
        }

        private static IReadOnlyList<string> ExtractAllDirectChildIds(IReadOnlyList<string> paths, string parentPath)
        {
            return ExtractDirectChildIds(paths, parentPath, null);
        }

        private static IReadOnlyList<string> ExtractDirectChildIds(IReadOnlyList<string> paths, string parentPath, int? maxCount)
        {
            var prefix = parentPath + ".";
            var values = paths
                .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(path =>
                {
                    var rest = path[prefix.Length..];
                    var dot = rest.IndexOf('.');
                    return dot >= 0 ? rest[..dot] : rest;
                })
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);

            return maxCount.HasValue
                ? values.Take(maxCount.Value).ToArray()
                : values.ToArray();
        }

        private static IReadOnlyList<string> ExtractDirectChildIds(IReadOnlyList<SaveStateDsonScalar> scalars, string parentPath)
        {
            return ExtractDirectChildIds(scalars.Select(scalar => scalar.Path).ToArray(), parentPath);
        }

        private static IReadOnlyList<string> ExtractAllDirectChildIds(IReadOnlyList<SaveStateDsonScalar> scalars, string parentPath)
        {
            return ExtractAllDirectChildIds(scalars.Select(scalar => scalar.Path).ToArray(), parentPath);
        }

        private static IReadOnlyList<string> MergeDirectChildIds(params IReadOnlyList<string>[] idLists)
        {
            return MergeDirectChildIds(120, idLists);
        }

        private static IReadOnlyList<string> MergeAllDirectChildIds(params IReadOnlyList<string>[] idLists)
        {
            return MergeDirectChildIds(null, idLists);
        }

        private static IReadOnlyList<string> MergeDirectChildIds(int? maxCount, params IReadOnlyList<string>[] idLists)
        {
            var values = idLists
                .SelectMany(list => list)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);

            return maxCount.HasValue
                ? values.Take(maxCount.Value).ToArray()
                : values.ToArray();
        }

        private static IReadOnlyList<SaveStateHeroFacts> ExtractHeroFactsFromRoster(
            string fileName,
            byte[] bytes,
            BinaryContainerInfo? container,
            List<string> accessIssues)
        {
            if (!fileName.Equals("persist.roster.json", StringComparison.OrdinalIgnoreCase) || container is null)
            {
                return [];
            }

            var heroes = new List<SaveStateHeroFacts>();
            foreach (var scalar in container.DsonScalars
                         .Where(scalar => scalar.Path.EndsWith(".hero_file_data.raw_data", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(scalar => scalar.Path, StringComparer.OrdinalIgnoreCase))
            {
                var heroId = ExtractHeroIdFromRawDataPath(scalar.Path);
                if (string.IsNullOrWhiteSpace(heroId))
                {
                    continue;
                }

                var nameLength = Encoding.UTF8.GetByteCount(scalar.Name) + 1;
                var valueStart = Align4(scalar.Offset + nameLength);
                var fieldEnd = scalar.Offset + scalar.Size;
                if (fieldEnd > bytes.Length || valueStart + 4 > fieldEnd)
                {
                    accessIssues.Add($"{fileName}: hero raw_data has no length prefix path={scalar.Path}");
                    continue;
                }

                var nestedLength = ReadInt32LittleEndian(bytes, valueStart);
                var nestedOffset = valueStart + 4;
                var availableLength = fieldEnd - nestedOffset;
                if (nestedLength < 0
                    || availableLength < 0
                    || nestedLength > availableLength
                    || nestedOffset > bytes.Length
                    || bytes.Length - nestedOffset < nestedLength)
                {
                    accessIssues.Add($"{fileName}: hero raw_data length is invalid path={scalar.Path} length={nestedLength}");
                    continue;
                }

                if (nestedLength < 0x40 || ReadUInt32LittleEndian(bytes, nestedOffset) != 0x0000B101)
                {
                    accessIssues.Add($"{fileName}: hero raw_data is not a DSON container path={scalar.Path}");
                    continue;
                }

                var nestedIssues = new List<string>();
                var nested = TryParseDsonContainer(bytes, nestedOffset, nestedLength, nestedIssues);
                foreach (var issue in nestedIssues)
                {
                    accessIssues.Add($"{fileName}: hero={heroId} {issue}");
                }

                if (nested is null)
                {
                    accessIssues.Add($"{fileName}: failed to parse nested hero DSON path={scalar.Path}");
                    continue;
                }

                heroes.Add(BuildSaveStateHeroFacts(heroId, nestedLength, nested));
            }

            return heroes
                .OrderBy(hero => int.TryParse(hero.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : int.MaxValue)
                .ThenBy(hero => hero.Id, StringComparer.OrdinalIgnoreCase)
                .Take(120)
                .ToArray();
        }

        private static string? ExtractHeroIdFromRawDataPath(string path)
        {
            const string prefix = "base_root.heroes.";
            const string suffix = ".hero_file_data.raw_data";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                || path.Length <= prefix.Length + suffix.Length)
            {
                return null;
            }

            return path[prefix.Length..^suffix.Length];
        }

        private static SaveStateHeroFacts BuildSaveStateHeroFacts(string heroId, int rawDataLength, BinaryContainerInfo nested)
        {
            return new SaveStateHeroFacts(
                heroId,
                TryGetString(nested.DsonScalars, "base_root.actor.name"),
                TryGetString(nested.DsonScalars, "base_root.heroClass"),
                TryGetInt(nested.DsonScalars, "base_root.roster.status"),
                TryGetInt(nested.DsonScalars, "base_root.resolveXp"),
                TryGetDouble(nested.DsonScalars, "base_root.actor.current_hp"),
                TryGetDouble(nested.DsonScalars, "base_root.m_Stress"),
                TryGetInt(nested.DsonScalars, "base_root.weapon_rank"),
                TryGetInt(nested.DsonScalars, "base_root.armour_rank"),
                TryGetBool(nested.DsonScalars, "base_root.backer_hero"),
                rawDataLength,
                nested.DsonSummary.ObjectCount,
                nested.DsonSummary.FieldCount,
                ExtractDirectChildIds(nested.DsonObjectPaths, "base_root.quirks"),
                ExtractDirectChildIds(nested.DsonScalars, "base_root.skills.selected_combat_skills"),
                ExtractDirectChildIds(nested.DsonScalars, "base_root.skills.selected_camping_skills"),
                MergeDirectChildIds(
                    ExtractDirectChildIds(nested.DsonObjectPaths, "base_root.trinkets.items"),
                    ExtractDirectChildIds(nested.DsonScalars, "base_root.trinkets.items")));
        }

        private static string? TryGetString(SaveStateFileReport? file, string path)
        {
            var scalar = FindDsonScalar(file, path);
            return scalar is not null && scalar.Type.Equals("string", StringComparison.OrdinalIgnoreCase)
                ? scalar.Value
                : null;
        }

        private static int? TryGetInt(SaveStateFileReport? file, string path)
        {
            var scalar = FindDsonScalar(file, path);
            return scalar is not null && int.TryParse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        private static uint? TryGetUInt(SaveStateFileReport? file, string path)
        {
            var scalar = FindDsonScalar(file, path);
            return scalar is not null && uint.TryParse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        private static double? TryGetDouble(SaveStateFileReport? file, string path)
        {
            var scalar = FindDsonScalar(file, path);
            return scalar is not null && double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        private static bool? TryGetBool(SaveStateFileReport? file, string path)
        {
            var scalar = FindDsonScalar(file, path);
            return scalar is not null && bool.TryParse(scalar.Value, out var value)
                ? value
                : null;
        }

        private static string? TryGetString(IReadOnlyList<SaveStateDsonScalar> scalars, string path)
        {
            var scalar = FindDsonScalar(scalars, path);
            return scalar is not null && scalar.Type.Equals("string", StringComparison.OrdinalIgnoreCase)
                ? scalar.Value
                : null;
        }

        private static int? TryGetInt(IReadOnlyList<SaveStateDsonScalar> scalars, string path)
        {
            var scalar = FindDsonScalar(scalars, path);
            return scalar is not null && int.TryParse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        private static double? TryGetDouble(IReadOnlyList<SaveStateDsonScalar> scalars, string path)
        {
            var scalar = FindDsonScalar(scalars, path);
            return scalar is not null && double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        private static bool? TryGetBool(IReadOnlyList<SaveStateDsonScalar> scalars, string path)
        {
            var scalar = FindDsonScalar(scalars, path);
            return scalar is not null && bool.TryParse(scalar.Value, out var value)
                ? value
                : null;
        }

        private static SaveStateDsonScalar? FindDsonScalar(SaveStateFileReport? file, string path)
        {
            return GetDsonScalars(file).FirstOrDefault(scalar => scalar.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        }

        private static SaveStateDsonScalar? FindDsonScalar(IReadOnlyList<SaveStateDsonScalar> scalars, string path)
        {
            return scalars.FirstOrDefault(scalar => scalar.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        }

        private static IReadOnlyList<SaveStateDsonScalar> GetDsonScalars(SaveStateFileReport? file)
        {
            if (file is null)
            {
                return [];
            }

            return file.AllDsonScalars.Count > 0 ? file.AllDsonScalars : file.DsonScalars;
        }

        private static BinaryContainerInfo? TryParseBinaryContainer(byte[] bytes, List<string> accessIssues)
        {
            return TryParseDsonContainer(bytes, 0, bytes.Length, accessIssues);
        }

        private static BinaryContainerInfo? TryParseDsonContainer(byte[] bytes, int baseOffset, int length, List<string> accessIssues)
        {
            if (baseOffset < 0
                || length < 0
                || baseOffset > bytes.Length
                || bytes.Length - baseOffset < length
                || length < 0x40)
            {
                return null;
            }

            var endOffset = baseOffset + length;
            var magic = ReadUInt32LittleEndian(bytes, baseOffset);
            var headerLength = ReadInt32LittleEndian(bytes, baseOffset + 0x08);
            var meta1Size = ReadInt32LittleEndian(bytes, baseOffset + 0x10);
            var objectCount = ReadInt32LittleEndian(bytes, baseOffset + 0x14);
            var meta1OffsetRelative = ReadInt32LittleEndian(bytes, baseOffset + 0x18);
            var stringCountRaw = ReadUInt32LittleEndian(bytes, baseOffset + 0x2C);
            var stringIndexOffsetRaw = ReadUInt32LittleEndian(bytes, baseOffset + 0x30);
            var dataLength = ReadInt32LittleEndian(bytes, baseOffset + 0x38);
            var stringDataOffsetRaw = ReadUInt32LittleEndian(bytes, baseOffset + 0x3C);
            if (magic != 0x0000B101
                || headerLength != 0x40
                || objectCount < 0
                || meta1OffsetRelative < 0
                || meta1Size < 0
                || dataLength < 0
                || stringCountRaw > 100_000
                || stringCountRaw > int.MaxValue
                || stringIndexOffsetRaw > int.MaxValue
                || stringDataOffsetRaw > int.MaxValue)
            {
                return null;
            }

            var stringCount = (int)stringCountRaw;
            var stringIndexOffsetRelative = (int)stringIndexOffsetRaw;
            var stringDataOffsetRelative = (int)stringDataOffsetRaw;
            var meta1Offset = baseOffset + meta1OffsetRelative;
            var stringIndexOffset = baseOffset + stringIndexOffsetRelative;
            var stringDataOffset = baseOffset + stringDataOffsetRelative;
            var objectTableSize = (long)objectCount * 16L;
            var meta1End = (long)meta1Offset + objectTableSize;
            var stringIndexEnd = (long)stringIndexOffset + (long)stringCount * 12L;
            var stringDataEnd = (long)stringDataOffset + dataLength;
            if (meta1OffsetRelative > length
                || stringIndexOffsetRelative > length
                || stringDataOffsetRelative > length
                || meta1End > endOffset
                || meta1Size < objectTableSize
                || stringIndexEnd > endOffset
                || stringDataEnd > endOffset
                || stringIndexEnd != stringDataOffset)
            {
                return null;
            }

            var objectEntries = ReadDsonObjectEntries(bytes, objectCount, meta1Offset);
            var fieldEntries = ReadDsonFieldEntries(bytes, stringCount, stringIndexOffset, stringDataOffset);
            var dsonObjectPaths = BuildDsonObjectPaths(objectEntries, fieldEntries);
            var dsonScalars = ExtractDsonScalars(bytes, fieldEntries, objectEntries, dsonObjectPaths, stringDataOffset, dataLength);
            var strings = new List<SaveStateBinaryString>();
            foreach (var field in fieldEntries)
            {
                if (field.AbsoluteOffset < baseOffset || field.AbsoluteOffset >= endOffset)
                {
                    accessIssues.Add($"DSON field {field.Index} points outside file: absoluteOffset={field.AbsoluteOffset}");
                    continue;
                }

                strings.Add(new SaveStateBinaryString(
                    field.AbsoluteOffset,
                    field.Name,
                    field.Index,
                    field.Hash,
                    field.Metadata,
                    field.RelativeOffset));
            }

            var dsonSummary = new SaveStateDsonSummary(
                headerLength,
                objectCount,
                stringCount,
                dataLength,
                stringDataOffsetRelative,
                dsonScalars.Count(scalar => !scalar.Type.Equals("raw", StringComparison.OrdinalIgnoreCase)),
                dsonScalars.Count(scalar => scalar.Type.Equals("raw", StringComparison.OrdinalIgnoreCase)));

            return new BinaryContainerInfo(
                stringCount,
                stringIndexOffsetRelative,
                stringDataOffsetRelative,
                strings,
                dsonSummary,
                dsonScalars,
                dsonObjectPaths);
        }

        private static IReadOnlyList<DsonObjectEntry> ReadDsonObjectEntries(byte[] bytes, int count, int offset)
        {
            var entries = new List<DsonObjectEntry>();
            for (var i = 0; i < count; i++)
            {
                var entryOffset = offset + i * 16;
                entries.Add(new DsonObjectEntry(
                    i,
                    ReadInt32LittleEndian(bytes, entryOffset),
                    ReadInt32LittleEndian(bytes, entryOffset + 4),
                    ReadInt32LittleEndian(bytes, entryOffset + 8),
                    ReadInt32LittleEndian(bytes, entryOffset + 12)));
            }

            return entries;
        }

        private static IReadOnlyList<DsonFieldEntry> ReadDsonFieldEntries(byte[] bytes, int count, int offset, int dataOffset)
        {
            var entries = new List<DsonFieldEntry>();
            for (var i = 0; i < count; i++)
            {
                var entryOffset = offset + i * 12;
                var hash = ReadUInt32LittleEndian(bytes, entryOffset);
                var relativeOffset = (int)ReadUInt32LittleEndian(bytes, entryOffset + 4);
                var metadata = ReadUInt32LittleEndian(bytes, entryOffset + 8);
                var nameLength = (int)((metadata & 0x7FC) >> 2);
                var absoluteOffset = dataOffset + relativeOffset;
                var name = nameLength > 0 && absoluteOffset + nameLength <= bytes.Length
                    ? ReadNullTerminatedUtf8(bytes, absoluteOffset, nameLength)
                    : string.Empty;
                entries.Add(new DsonFieldEntry(
                    i,
                    name,
                    relativeOffset,
                    absoluteOffset,
                    nameLength,
                    hash,
                    metadata,
                    (metadata & 1) != 0));
            }

            return entries;
        }

        private static IReadOnlyDictionary<int, string> BuildDsonObjectPaths(
            IReadOnlyList<DsonObjectEntry> objectEntries,
            IReadOnlyList<DsonFieldEntry> fieldEntries)
        {
            var fieldsByIndex = fieldEntries.ToDictionary(field => field.Index);
            var objectsByIndex = objectEntries.ToDictionary(entry => entry.ObjectIndex);
            var pathsByMeta2Index = new Dictionary<int, string>();

            foreach (var entry in objectEntries.OrderBy(entry => entry.ObjectIndex))
            {
                if (!fieldsByIndex.TryGetValue(entry.Meta2Index, out var field))
                {
                    continue;
                }

                if (entry.ParentObjectIndex < 0
                    || !objectsByIndex.TryGetValue(entry.ParentObjectIndex, out var parentEntry)
                    || !pathsByMeta2Index.TryGetValue(parentEntry.Meta2Index, out var parentPath))
                {
                    pathsByMeta2Index[entry.Meta2Index] = field.Name;
                    continue;
                }

                pathsByMeta2Index[entry.Meta2Index] = $"{parentPath}.{field.Name}";
            }

            return pathsByMeta2Index;
        }

        private static IReadOnlyList<SaveStateDsonScalar> ExtractDsonScalars(
            byte[] bytes,
            IReadOnlyList<DsonFieldEntry> fields,
            IReadOnlyList<DsonObjectEntry> objects,
            IReadOnlyDictionary<int, string> objectPaths,
            int dataOffset,
            int dataLength)
        {
            var scalars = new List<SaveStateDsonScalar>();
            var orderedFields = fields.OrderBy(field => field.Index).ToArray();
            for (var i = 0; i < orderedFields.Length; i++)
            {
                var field = orderedFields[i];
                if (field.IsObject)
                {
                    continue;
                }

                var nextRelativeOffset = i + 1 < orderedFields.Length
                    ? orderedFields[i + 1].RelativeOffset
                    : dataLength;
                var endOffset = dataOffset + nextRelativeOffset;
                var nameEnd = field.AbsoluteOffset + field.NameLength;
                if (endOffset < nameEnd || nameEnd > bytes.Length)
                {
                    continue;
                }

                var path = BuildDsonFieldPath(field, objects, objectPaths);
                var size = endOffset - field.AbsoluteOffset;
                var rawHex = ToHex(bytes.Skip(nameEnd).Take(Math.Min(16, Math.Max(0, endOffset - nameEnd))));
                var valueStart = Align4(nameEnd);
                var remaining = endOffset - nameEnd;
                var alignedRemaining = endOffset - valueStart;

                if (remaining == 1 && bytes[nameEnd] is 0 or 1)
                {
                    scalars.Add(new SaveStateDsonScalar(path, field.Name, "bool", bytes[nameEnd] == 0 ? "false" : "true", field.AbsoluteOffset, size, rawHex));
                    continue;
                }

                if (remaining == 1
                    && SingleByteStringFieldNames.Contains(field.Name, StringComparer.OrdinalIgnoreCase)
                    && bytes[nameEnd] is >= 32 and <= 126)
                {
                    scalars.Add(new SaveStateDsonScalar(path, field.Name, "string", ((char)bytes[nameEnd]).ToString(), field.AbsoluteOffset, size, rawHex));
                    continue;
                }

                if (alignedRemaining >= 4 && TryReadDsonString(bytes, valueStart, alignedRemaining, out var stringValue))
                {
                    scalars.Add(new SaveStateDsonScalar(path, field.Name, "string", stringValue, field.AbsoluteOffset, size, rawHex));
                    continue;
                }

                if (alignedRemaining == 4)
                {
                    if (FloatFieldNames.Contains(field.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        scalars.Add(new SaveStateDsonScalar(
                            path,
                            field.Name,
                            "float32",
                            BitConverter.ToSingle(bytes, valueStart).ToString("R", CultureInfo.InvariantCulture),
                            field.AbsoluteOffset,
                            size,
                            rawHex));
                    }
                    else if (UInt32FieldNames.Contains(field.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        scalars.Add(new SaveStateDsonScalar(
                            path,
                            field.Name,
                            "uint32",
                            ReadUInt32LittleEndian(bytes, valueStart).ToString(CultureInfo.InvariantCulture),
                            field.AbsoluteOffset,
                            size,
                            rawHex));
                    }
                    else
                    {
                        scalars.Add(new SaveStateDsonScalar(
                            path,
                            field.Name,
                            "int32",
                            ReadInt32LittleEndian(bytes, valueStart).ToString(CultureInfo.InvariantCulture),
                            field.AbsoluteOffset,
                            size,
                            rawHex));
                    }

                    continue;
                }

                scalars.Add(new SaveStateDsonScalar(path, field.Name, "raw", null, field.AbsoluteOffset, size, rawHex));
            }

            return scalars;
        }

        private static string BuildDsonFieldPath(
            DsonFieldEntry field,
            IReadOnlyList<DsonObjectEntry> objects,
            IReadOnlyDictionary<int, string> objectPaths)
        {
            var parent = objects
                .Where(entry => entry.Meta2Index < field.Index && field.Index <= entry.Meta2Index + entry.AllChildCount)
                .OrderByDescending(entry => entry.Meta2Index)
                .FirstOrDefault();

            return parent is not null && objectPaths.TryGetValue(parent.Meta2Index, out var parentPath)
                ? $"{parentPath}.{field.Name}"
                : field.Name;
        }

        private static bool TryReadDsonString(byte[] bytes, int offset, int remaining, out string value)
        {
            value = string.Empty;
            if (remaining < 5)
            {
                return false;
            }

            var length = ReadInt32LittleEndian(bytes, offset);
            if (length < 1 || length != remaining - 4 || offset + 4 + length > bytes.Length || bytes[offset + 4 + length - 1] != 0)
            {
                return false;
            }

            try
            {
                value = StrictUtf8.GetString(bytes, offset + 4, length - 1);
                return true;
            }
            catch (DecoderFallbackException)
            {
                value = string.Empty;
                return false;
            }
        }

        private static int Align4(int value)
        {
            return (value + 3) & ~3;
        }

        private static uint ReadUInt32LittleEndian(byte[] bytes, int offset)
        {
            return BitConverter.ToUInt32(bytes, offset);
        }

        private static int ReadInt32LittleEndian(byte[] bytes, int offset)
        {
            return BitConverter.ToInt32(bytes, offset);
        }

        private static string ReadNullTerminatedAscii(byte[] bytes, int offset)
        {
            var end = offset;
            while (end < bytes.Length && bytes[end] != 0)
            {
                end++;
            }

            return Encoding.ASCII.GetString(bytes, offset, end - offset);
        }

        private static string ReadNullTerminatedUtf8(byte[] bytes, int offset, int maxLength)
        {
            var length = Math.Max(0, maxLength - 1);
            return StrictUtf8.GetString(bytes, offset, length);
        }

        private static IReadOnlyList<SaveStateBinaryString> ExtractPrintableStrings(byte[] bytes)
        {
            var strings = new List<SaveStateBinaryString>();
            var builder = new StringBuilder();
            var start = 0;

            for (var i = 0; i < bytes.Length; i++)
            {
                var b = bytes[i];
                if (b is >= 32 and <= 126)
                {
                    if (builder.Length == 0) start = i;
                    builder.Append((char)b);
                    continue;
                }

                FlushString(strings, builder, start);
            }

            FlushString(strings, builder, start);
            return strings;
        }

        private static void FlushString(List<SaveStateBinaryString> strings, StringBuilder builder, int start)
        {
            if (builder.Length >= 4)
            {
                strings.Add(new SaveStateBinaryString(start, builder.ToString(), null, null, null, null));
            }

            builder.Clear();
        }

        private static IReadOnlyList<SaveStateValueCandidate> ExtractValueCandidates(
            IReadOnlyList<SaveStateBinaryString> strings,
            IReadOnlyList<SaveStateBinaryString> printableStrings)
        {
            var keys = ValueCandidateKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<SaveStateValueCandidate>();
            for (var i = 0; i < strings.Count; i++)
            {
                var key = strings[i].Value;
                if (!keys.Contains(key)) continue;

                var keyEnd = strings[i].Offset + key.Length + 1;
                var value = printableStrings
                    .Where(item => item.Offset >= keyEnd && item.Offset - keyEnd <= MaxInlineValueDistance)
                    .OrderBy(item => item.Offset)
                    .FirstOrDefault(item => IsLikelyScalarCandidate(item.Value, keys));
                if (string.IsNullOrWhiteSpace(value.Value)) continue;

                candidates.Add(new SaveStateValueCandidate(key, value.Value, value.Offset, value.StringIndex, "inlineString"));
                if (candidates.Count >= 80) break;
            }

            return candidates;
        }

        private static bool IsLikelyScalarCandidate(string? value, HashSet<string> keys)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (keys.Contains(value)) return false;
            if (KnownMarkers.Contains(value, StringComparer.OrdinalIgnoreCase)) return false;
            return value.All(ch => ch is >= ' ' and <= '~');
        }

        private static string ToHex(IEnumerable<byte> bytes)
        {
            return string.Join(' ', bytes.Select(value => value.ToString("X2")));
        }

        private sealed record BinaryContainerInfo(
            int StringCount,
            int StringIndexOffset,
            int StringDataOffset,
            IReadOnlyList<SaveStateBinaryString> Strings,
            SaveStateDsonSummary DsonSummary,
            IReadOnlyList<SaveStateDsonScalar> DsonScalars,
            IReadOnlyDictionary<int, string> DsonObjectPathsByMeta2Index)
        {
            public IReadOnlyList<string> DsonObjectPaths { get; } = DsonObjectPathsByMeta2Index
                .Values
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private sealed record DsonObjectEntry(
            int ObjectIndex,
            int ParentObjectIndex,
            int Meta2Index,
            int DirectChildCount,
            int AllChildCount);

        private sealed record DsonFieldEntry(
            int Index,
            string Name,
            int RelativeOffset,
            int AbsoluteOffset,
            int NameLength,
            uint Hash,
            uint Metadata,
            bool IsObject);
    }

    private sealed record SaveStateReport(
        int Version,
        string SessionId,
        DateTimeOffset GeneratedAt,
        string ParseStatus,
        string Notes,
        ActiveProfileInference ActiveProfile,
        SaveStateFacts Facts,
        IReadOnlyList<string> CandidateFiles,
        IReadOnlyList<SaveStateFileReport> Files,
        IReadOnlyList<string> AccessIssues);

    private sealed record SaveStateFileReport(
        string FileName,
        string Path,
        bool Exists,
        long? Length,
        DateTime? LastWriteUtc,
        string? Sha256,
        string Format,
        string ParseStatus,
        string? BinaryHeaderHex,
        int? BinaryStringCount,
        int? BinaryStringIndexOffset,
        int? BinaryStringDataOffset,
        SaveStateDsonSummary? DsonSummary,
        IReadOnlyList<SaveStateDsonScalar> DsonScalars,
        [property: JsonIgnore] IReadOnlyList<SaveStateDsonScalar> AllDsonScalars,
        IReadOnlyList<string> DsonObjectPaths,
        IReadOnlyList<SaveStateHeroFacts> Heroes,
        IReadOnlyList<string> JsonTopLevelKeys,
        IReadOnlyList<string> MarkerStrings,
        IReadOnlyList<SaveStateValueCandidate> ValueCandidates,
        IReadOnlyList<string> StringSamples,
        IReadOnlyList<SaveStateBinaryString> BinaryStrings,
        IReadOnlyList<string> AccessIssues);

    private sealed record SaveFileMapReport(
        int Version,
        string SessionId,
        DateTimeOffset GeneratedAt,
        ActiveProfileInference ActiveProfile,
        string ActiveRoot,
        IReadOnlyList<string> CandidateFiles,
        IReadOnlyList<SaveFileMapEntry> Files,
        IReadOnlyList<string> AccessIssues);

    private sealed record SaveFileMapEntry(
        string FileName,
        string RelativePath,
        string Area,
        string Path,
        bool Exists,
        long? Length,
        DateTime? LastWriteUtc,
        string? Sha256,
        string Format,
        string ParseStatus,
        bool CandidateFile,
        int Priority,
        string Category,
        string ModRelevance,
        string Coverage,
        SaveStateDsonSummary? DsonSummary,
        IReadOnlyList<string> MarkerStrings,
        IReadOnlyList<string> ValueCandidateKeys,
        int DsonScalarSampleCount,
        int DsonObjectPathCount,
        IReadOnlyList<string> AccessIssues);

    private sealed record SaveFileMapClassification(
        int Priority,
        string Category,
        string ModRelevance);

    private sealed record SaveFileMapSource(
        string Path,
        string FileName,
        string RelativePath,
        string Area);

    private sealed record SaveStateFacts(
        SaveStateCampaignFacts Campaign,
        SaveStateProgressionFacts Progression,
        IReadOnlyDictionary<string, int> Wallet,
        SaveStateUpgradeFacts Upgrades,
        IReadOnlyList<string> BuildingIds,
        IReadOnlyList<string> HeroIds,
        IReadOnlyList<SaveStateHeroFacts> Heroes);

    private sealed record SaveStateUpgradeFacts(
        int? Version,
        int PurchaseCount,
        int PurchasedCount,
        int UnpurchasedCount,
        int UnknownPurchaseStateCount,
        int TreeCount,
        int DefinitionSourceFileCount,
        int DefinitionTreeCount,
        int NameCandidateCount,
        int MappedTreeCount,
        int UnmappedTreeCount,
        int AmbiguousTreeCount,
        IReadOnlyList<SaveStateUpgradePurchaseFacts> Purchases,
        IReadOnlyList<SaveStateUpgradeTreeFacts> Trees);

    private sealed record SaveStateUpgradePurchaseFacts(
        int Index,
        int? InstanceNumber,
        uint? TreeId,
        string? TreeName,
        bool TreeNameAmbiguous,
        string? DefinitionSource,
        IReadOnlyList<string> TreeTags,
        SaveStateUpgradeRequirementDefinitionFacts? RequirementDefinition,
        string? RequirementCode,
        bool? IsPurchased);

    private sealed record SaveStateUpgradeTreeFacts(
        uint TreeId,
        string? TreeName,
        bool TreeNameAmbiguous,
        string? DefinitionSource,
        bool? IsInstanced,
        IReadOnlyList<string> Tags,
        IReadOnlyList<SaveStateUpgradeRequirementDefinitionFacts> DefinedRequirements,
        int PurchaseCount,
        int PurchasedCount,
        int UnpurchasedCount,
        IReadOnlyList<int> InstanceNumbers,
        IReadOnlyList<string> RequirementCodes,
        IReadOnlyList<string> PurchasedRequirementCodes);

    private sealed record SaveStateUpgradeRequirementDefinitionFacts(
        string Code,
        IReadOnlyDictionary<string, int> CurrencyCost,
        int? PrerequisiteResolveLevel,
        IReadOnlyList<SaveStateUpgradePrerequisiteDefinitionFacts> Prerequisites);

    private sealed record SaveStateUpgradePrerequisiteDefinitionFacts(
        string TreeId,
        string RequirementCode);

    private sealed record SaveStateHeroFacts(
        string Id,
        string? Name,
        string? HeroClass,
        int? RosterStatus,
        int? ResolveXp,
        double? CurrentHp,
        double? Stress,
        int? WeaponRank,
        int? ArmourRank,
        bool? BackerHero,
        int RawDataLength,
        int NestedObjectCount,
        int NestedFieldCount,
        IReadOnlyList<string> QuirkIds,
        IReadOnlyList<string> CombatSkillIds,
        IReadOnlyList<string> CampingSkillIds,
        IReadOnlyList<string> TrinketIds);

    private sealed record SaveStateCampaignFacts(
        int? Version,
        double? TotalElapsed,
        bool? InRaid,
        string? RaidDungeon,
        string? EstateName,
        string? GameMode,
        string? DateTime,
        string? TownEvents,
        string? NeverAgain);

    private sealed record SaveStateProgressionFacts(
        int? TotalQuestsFinished,
        int? TotalSuccessfulQuestsFinished,
        int? LastQuestPlayedId,
        int? LastQuestPlayedXp,
        bool? LastRaidSuccess,
        bool? LastRaidWasPlotQuest);

    private sealed record SaveStateDsonSummary(
        int HeaderLength,
        int ObjectCount,
        int FieldCount,
        int DataLength,
        int DataOffset,
        int ParsedScalarCount,
        int RawScalarCount);

    private sealed record SaveStateDsonScalar(
        string Path,
        string Name,
        string Type,
        string? Value,
        int Offset,
        int Size,
        string? RawHex);

    private sealed record SaveStateValueCandidate(
        string Key,
        string Value,
        int Offset,
        int? StringIndex,
        string Confidence);

    private readonly record struct SaveStateBinaryString(
        int Offset,
        string Value,
        int? StringIndex,
        uint? Hash,
        uint? Metadata,
        int? RelativeOffset);

    private sealed class StableSaveChangeGroupComparer : IEqualityComparer<(string ProfileRoot, string Profile)>
    {
        public static readonly StableSaveChangeGroupComparer Instance = new();

        public bool Equals((string ProfileRoot, string Profile) x, (string ProfileRoot, string Profile) y)
        {
            return StringComparer.OrdinalIgnoreCase.Equals(x.ProfileRoot, y.ProfileRoot)
                && StringComparer.OrdinalIgnoreCase.Equals(x.Profile, y.Profile);
        }

        public int GetHashCode((string ProfileRoot, string Profile) obj)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ProfileRoot),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Profile));
        }
    }
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
