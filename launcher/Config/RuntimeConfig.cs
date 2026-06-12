namespace DDRuntimeLoader;

internal sealed partial class RuntimeConfig
{
    [JsonPropertyName("gameExecutablePath")]
    public string GameExecutablePath { get; set; } = string.Empty;

    [JsonPropertyName("gameWorkingDirectory")]
    public string GameWorkingDirectory { get; set; } = string.Empty;

    [JsonPropertyName("gameArguments")]
    public string[] GameArguments { get; set; } = [];

    [JsonPropertyName("runtimeDllPath")]
    public string RuntimeDllPath { get; set; } = string.Empty;

    [JsonPropertyName("logDirectory")]
    public string LogDirectory { get; set; } = string.Empty;

    [JsonPropertyName("modStateDirectory")]
    public string ModStateDirectory { get; set; } = "./state/mod_state";

    [JsonPropertyName("allowNonAtomicStateWrites")]
    public bool AllowNonAtomicStateWrites { get; set; }

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

    [JsonPropertyName("saveEventBridgeEnabled")]
    public bool SaveEventBridgeEnabled { get; set; }

    [JsonPropertyName("saveEventBridgeDebounceMilliseconds")]
    public int SaveEventBridgeDebounceMilliseconds { get; set; } = 1000;

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
        if (!string.IsNullOrWhiteSpace(options.ModStateDirectory)) ModStateDirectory = options.ModStateDirectory;
        if (options.AllowNonAtomicStateWrites) AllowNonAtomicStateWrites = true;
        if (options.NoInject) EnableInjection = false;
    }

    public void ResolvePaths(string projectRoot)
    {
        GameExecutablePath = ResolvePath(projectRoot, GameExecutablePath);
        GameWorkingDirectory = ResolvePath(projectRoot, GameWorkingDirectory);
        RuntimeDllPath = ResolvePath(projectRoot, RuntimeDllPath);
        LogDirectory = ResolvePath(projectRoot, LogDirectory);
        ModStateDirectory = ResolvePath(projectRoot, ModStateDirectory);
        if (!IsInsideDirectory(projectRoot, ModStateDirectory))
        {
            throw new InvalidOperationException($"modStateDirectory must stay inside project root: {ModStateDirectory}");
        }

        SaveWatchDirectories = SaveWatchDirectories
            .Select(path => ResolvePath(projectRoot, path))
            .ToArray();
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(ModStateDirectory);
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
            values[$"DD_RUNTIME_VIRTUAL_RULE_{i}_SOURCE_PATH"] = rule.SourcePath;
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
}
