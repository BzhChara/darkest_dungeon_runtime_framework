namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher : IDisposable
{
    private const string DarkestDungeonAppId = "262060";
    private const int MaxSummaryFilesPerLine = 32;
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromMilliseconds(250);
    private static readonly HashSet<string> KnownAuxiliaryOrNetworkSaveFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "persist.circus_estate.json",
        "persist.rankings.json",
        "persist.prize_booth.json",
        "persist.banner_custom.json",
        "persist.mp_progression.json",
        "persist.roster.network.json",
        "novelty_tracker_mp.json"
    };
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
    private readonly RuntimeConfig _config;
    private readonly PatchPlan _patchPlan;
    private readonly string _projectRoot;
    private readonly List<string> _directories;
    private readonly string _gameWorkingDirectory;
    private readonly string _sessionDirectory;
    private readonly string _sessionId;
    private readonly DateTimeOffset _startedAt;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, SaveFileSnapshot> _initialSnapshot;
    private readonly Dictionary<string, int> _eventCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _recentEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingBridgeProfile> _pendingBridgeProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private Timer? _bridgeTimer;
    private bool _bridgeRunning;
    private int _bridgeSequence;
    private int? _gameProcessId;
    private int? _gameExitCode;
    private int _afterExitSeconds;
    private DateTimeOffset? _gameExitedAt;
    private bool _disposed;

    private SaveDirectoryWatcher(
        RuntimeConfig config,
        PatchPlan patchPlan,
        string projectRoot,
        IReadOnlyList<string> directories,
        LauncherLog log)
    {
        _config = config;
        _patchPlan = patchPlan;
        _projectRoot = projectRoot;
        _directories = directories.ToList();
        _gameWorkingDirectory = config.GameWorkingDirectory;
        _sessionDirectory = Path.Combine(config.LogDirectory, "save_sessions");
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

    public static SaveDirectoryWatcher? Start(RuntimeConfig config, PatchPlan patchPlan, string projectRoot, LauncherLog log)
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

        return new SaveDirectoryWatcher(config, patchPlan, projectRoot, directories, log);
    }

    public static string WriteMapFileInspectionReport(string mapFilePath, string outputPath, LauncherLog log)
    {
        return SaveStateExporter.WriteMapFileInspectionReport(mapFilePath, outputPath, log);
    }

    public static string WriteMapFinalRoomPrototype(
        string sourceMapFilePath,
        string outputMapFilePath,
        string targetFinalRoomId,
        string reportOutputPath,
        LauncherLog log)
    {
        return SaveStateExporter.WriteMapFinalRoomPrototype(
            sourceMapFilePath,
            outputMapFilePath,
            targetFinalRoomId,
            reportOutputPath,
            log);
    }

    public static string WriteMapTemplatePrototype(
        string sourceMapFilePath,
        string specFilePath,
        string outputMapFilePath,
        string reportOutputPath,
        LauncherLog log)
    {
        return SaveStateExporter.WriteMapTemplatePrototype(
            sourceMapFilePath,
            specFilePath,
            outputMapFilePath,
            reportOutputPath,
            log);
    }

    public static string WriteMapLayoutTemplateValidationReport(
        string sourceMapFilePath,
        MapLayoutTemplateRule template,
        string reportOutputPath,
        LauncherLog log)
    {
        return SaveStateExporter.WriteMapLayoutTemplateValidationReport(
            sourceMapFilePath,
            template,
            reportOutputPath,
            log);
    }

    public static string WriteMapLayoutTemplatePrototype(
        string sourceMapFilePath,
        MapLayoutTemplateRule template,
        string compiledSpecOutputPath,
        string outputMapFilePath,
        string validationReportOutputPath,
        string mapTemplateReportOutputPath,
        LauncherLog log)
    {
        return SaveStateExporter.WriteMapLayoutTemplatePrototype(
            sourceMapFilePath,
            template,
            compiledSpecOutputPath,
            outputMapFilePath,
            validationReportOutputPath,
            mapTemplateReportOutputPath,
            log);
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

        _bridgeTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        FlushPendingRealtimeSaveEventBridge(force: true);
        _bridgeTimer?.Dispose();

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
        ScheduleRealtimeSaveEventBridge(e.FullPath, eventName);
    }

    private void LogRenameEvent(RenamedEventArgs e)
    {
        if (ShouldSuppress("save.sidecar_renamed", e.FullPath)) return;
        CountEvent("save.sidecar_renamed");
        _log.Info($"event name=save.sidecar_renamed oldPath={Quote(e.OldFullPath)} {DescribePath(e.FullPath)}");
        ScheduleRealtimeSaveEventBridge(e.FullPath, "save.sidecar_renamed");
    }

    private void ScheduleRealtimeSaveEventBridge(string path, string reason)
    {
        if (!_config.SaveEventBridgeEnabled)
        {
            return;
        }

        var stablePath = StableSavePath.TryCreate(path);
        if (stablePath is null)
        {
            return;
        }

        if (!ShouldRunRealtimeSaveEventBridge(stablePath, out var skipReason))
        {
            CountEvent("save.event_bridge_realtime_ignored_auxiliary");
            _log.Info(
                $"event name=save.event_bridge_realtime_ignored_auxiliary profile={stablePath.Profile} " +
                $"root={Quote(stablePath.ProfileRoot)} reason={Quote(skipReason)} path={Quote(stablePath.RelativePath)}");
            return;
        }

        PendingBridgeProfile pending;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var now = DateTimeOffset.Now;
            if (_pendingBridgeProfiles.TryGetValue(stablePath.ProfileRoot, out var existing))
            {
                pending = existing with
                {
                    LastSeenAt = now,
                    ChangeCount = existing.ChangeCount + 1,
                    LastReason = reason,
                    LastRelativePath = stablePath.RelativePath
                };
            }
            else
            {
                pending = new PendingBridgeProfile(
                    stablePath.Profile,
                    stablePath.ProfileRoot,
                    now,
                    now,
                    1,
                    reason,
                    stablePath.RelativePath);
            }

            _pendingBridgeProfiles[stablePath.ProfileRoot] = pending;
            _bridgeTimer ??= new Timer(_ => FlushPendingRealtimeSaveEventBridge(force: false));
            _bridgeTimer.Change(
                TimeSpan.FromMilliseconds(_config.SaveEventBridgeDebounceMilliseconds),
                Timeout.InfiniteTimeSpan);
        }

        CountEvent("save.event_bridge_realtime_scheduled");
        _log.Info(
            $"event name=save.event_bridge_realtime_scheduled profile={stablePath.Profile} " +
            $"root={Quote(stablePath.ProfileRoot)} reason={reason} path={Quote(stablePath.RelativePath)}");
    }

    private void FlushPendingRealtimeSaveEventBridge(bool force)
    {
        while (true)
        {
            PendingBridgeProfile[] pendingProfiles;
            lock (_sync)
            {
                if (_bridgeRunning)
                {
                    if (!force)
                    {
                        return;
                    }

                    Monitor.Wait(_sync, TimeSpan.FromSeconds(5));
                    continue;
                }

                if (_pendingBridgeProfiles.Count == 0)
                {
                    return;
                }

                _bridgeRunning = true;
                pendingProfiles = _pendingBridgeProfiles.Values
                    .OrderBy(profile => profile.Profile, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                _pendingBridgeProfiles.Clear();
            }

            try
            {
                foreach (var profile in pendingProfiles)
                {
                    try
                    {
                        ExecuteRealtimeSaveEventBridge(profile);
                    }
                    catch (Exception ex)
                    {
                        CountEvent("save.event_bridge_realtime_failed");
                        _log.Error(
                            $"event name=save.event_bridge_realtime_failed profile={profile.Profile} " +
                            $"root={Quote(profile.Root)} message={Quote(ex.Message)}");
                    }
                }
            }
            finally
            {
                lock (_sync)
                {
                    _bridgeRunning = false;
                    Monitor.PulseAll(_sync);
                    if (!force && _pendingBridgeProfiles.Count > 0)
                    {
                        _bridgeTimer?.Change(
                            TimeSpan.FromMilliseconds(_config.SaveEventBridgeDebounceMilliseconds),
                            Timeout.InfiniteTimeSpan);
                    }
                }
            }

            if (!force)
            {
                return;
            }
        }
    }

    private void ExecuteRealtimeSaveEventBridge(PendingBridgeProfile profile)
    {
        var sequence = Interlocked.Increment(ref _bridgeSequence);
        var generatedAt = DateTimeOffset.Now;
        var reportSessionId = $"{_sessionId}_realtime_{sequence:D4}";
        var activeProfile = new ActiveProfileInference(
            profile.Profile,
            profile.Root,
            "event",
            100,
            [
                $"realtime stable save changes={profile.ChangeCount}",
                $"last event={profile.LastReason}",
                $"last path={profile.LastRelativePath}"
            ],
            []);
        var sessionReport = new SaveSessionReport(
            1,
            reportSessionId,
            _startedAt,
            generatedAt,
            new SaveSessionGameInfo(_gameProcessId, _gameExitedAt, _gameExitCode),
            new SaveSessionWatchInfo(_directories, _afterExitSeconds, _initialSnapshot.Count, _initialSnapshot.Count),
            SnapshotEventCounts(),
            new SaveSessionSnapshotInfo(0, 0, 0, 0, 0, 0),
            [],
            activeProfile);

        CountEvent("save.state_report_realtime_requested");
        _log.Info(
            $"event name=save.state_report_realtime_requested profile={profile.Profile} " +
            $"root={Quote(profile.Root)} changes={profile.ChangeCount} sessionId={reportSessionId}");

        var stateReportPath = SaveStateExporter.TryWriteReport(
            _sessionDirectory,
            reportSessionId,
            generatedAt,
            sessionReport,
            _gameWorkingDirectory,
            _log,
            writeFileMapReport: false);
        if (string.IsNullOrWhiteSpace(stateReportPath))
        {
            CountEvent("save.state_report_realtime_skipped");
            return;
        }

        CountEvent("save.state_report_realtime_written");
        var bridgeReport = SaveEventBridge.Execute(_config, _patchPlan, _log, stateReportPath, _projectRoot, null);
        CountEvent(bridgeReport.Succeeded ? "save.event_bridge_realtime_completed" : "save.event_bridge_realtime_failed");
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

        if (liveFiles.Length > 0 && liveFiles.All(IsKnownAuxiliaryOrNetworkSaveFile))
        {
            score = Math.Max(0, score - 20);
            reasons.Add("only auxiliary, circus, or network files changed");
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
            if (_config.SaveEventBridgeEnabled)
            {
                var bridgeReport = SaveEventBridge.Execute(_config, _patchPlan, _log, stateReportPath, _projectRoot, null);
                CountEvent(bridgeReport.Succeeded ? "save.event_bridge_completed" : "save.event_bridge_failed");
            }
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

    private static bool ShouldRunRealtimeSaveEventBridge(StableSavePath stablePath, out string skipReason)
    {
        var fileName = Path.GetFileName(stablePath.RelativePath);
        if (IsKnownAuxiliaryOrNetworkSaveFile(fileName))
        {
            skipReason = "known auxiliary or network save file";
            return false;
        }

        skipReason = "";
        return true;
    }

    private static bool IsKnownAuxiliaryOrNetworkSaveFile(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        return KnownAuxiliaryOrNetworkSaveFiles.Contains(fileName);
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

    private sealed record StableSavePath(
        string ProfileRoot,
        string Profile,
        string Area,
        string RelativePath)
    {
        public static StableSavePath? TryCreate(string path)
        {
            var fileName = Path.GetFileName(path);
            if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || IsTransientSaveFileName(fileName))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(path);
            var directoryName = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directoryName))
            {
                return null;
            }

            var profileDirectory = new DirectoryInfo(directoryName);
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
            var relativePath = Path.GetRelativePath(profileRoot, fullPath)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            var relativeParts = relativePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var area = relativeParts.Length > 1 && relativeParts[0].Equals("backup", StringComparison.OrdinalIgnoreCase)
                ? "backup"
                : "live";

            return new StableSavePath(profileRoot, profile, area, relativePath);
        }
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
            var path = StableSavePath.TryCreate(change.Path);
            if (path is null)
            {
                return null;
            }

            return new StableSaveChange(
                change.Kind,
                path.ProfileRoot,
                path.Profile,
                path.Area,
                path.RelativePath,
                change.Before,
                change.After);
        }
    }

    private sealed record PendingBridgeProfile(
        string Profile,
        string Root,
        DateTimeOffset FirstSeenAt,
        DateTimeOffset LastSeenAt,
        int ChangeCount,
        string LastReason,
        string LastRelativePath);

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
}
