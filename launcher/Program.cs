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
            log.Info($"Game arguments: {string.Join(' ', config.GameArguments.Where(argument => !string.IsNullOrWhiteSpace(argument)))}");
            log.Info($"Runtime DLL: {config.RuntimeDllPath}");
            log.Info($"Mod state directory: {config.ModStateDirectory}");
            log.Info($"Allow non-atomic state writes: {config.AllowNonAtomicStateWrites}");
            log.Info($"Save event bridge enabled: {config.SaveEventBridgeEnabled}");
            log.Info($"Save event bridge debounce milliseconds: {config.SaveEventBridgeDebounceMilliseconds}");
            log.Info($"File IO hook enabled: {config.FileIoHookEnabled}");
            log.Info($"File IO observer enabled: {config.FileIoObserveOnly}");
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
                if ((patchPlan.HasCompileErrors || options.StrictPatches) &&
                    !options.ListPatches &&
                    !options.ExplainPatches &&
                    !options.ExplainRules &&
                    !options.InitModState &&
                    !options.DumpModState &&
                    !options.InferSaveEvents &&
                    !options.ApplyManagedActions &&
                    !options.InitializeDecodedProfile &&
                    !options.PreviewQuestBoard &&
                    !options.InspectMapFile &&
                    !options.PrototypeMapFinalRoom &&
                    !options.PrototypeMapTemplate &&
                    !options.WatchSavesForMilliseconds.HasValue &&
                    string.IsNullOrWhiteSpace(options.EmitEvent))
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

            if (options.ExplainRules)
            {
                patchPlan.LogRuleExplanation(log);
                patchPlan.LogFactEventRuleExplanation(log);
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

            var modStateSucceeded = true;
            if (options.PreviewQuestBoard)
            {
                modStateSucceeded &= QuestBoardPreviewReporter.Write(config, log).Succeeded;
            }

            if (options.InitModState)
            {
                modStateSucceeded &= ModStateStore.InitializeDefaults(config, patchPlan, log, options.ModStateId).Succeeded;
            }

            if (options.DumpModState)
            {
                modStateSucceeded &= ModStateStore.Dump(config, patchPlan, log, options.ModStateId).Succeeded;
            }

            if (!string.IsNullOrWhiteSpace(options.EmitEvent))
            {
                modStateSucceeded &= RuntimeEventExecutor.Execute(
                    config,
                    patchPlan,
                    log,
                    options.EmitEvent,
                    options.EventPayload,
                    options.EventPayloadFile,
                    projectRoot,
                    options.ModStateId).Succeeded;
            }

            if (options.InferSaveEvents)
            {
                if (string.IsNullOrWhiteSpace(options.SaveStateReportPath))
                {
                    throw new ArgumentException("--infer-save-events requires --save-state-report <path>.");
                }

                modStateSucceeded &= SaveEventBridge.Execute(
                    config,
                    patchPlan,
                    log,
                    options.SaveStateReportPath,
                    projectRoot,
                    options.ModStateId).Succeeded;
            }

            if (options.ApplyManagedActions)
            {
                if (string.IsNullOrWhiteSpace(options.ManagedActionSaveDirectory))
                {
                    throw new ArgumentException("--apply-managed-actions requires --managed-action-save-dir <path>.");
                }

                modStateSucceeded &= ManagedActionSaveApplier.Apply(
                    config,
                    log,
                    projectRoot,
                    options.ManagedActionSaveDirectory,
                    options.WriteManagedActions).Succeeded;
            }

            if (options.InitializeDecodedProfile)
            {
                if (string.IsNullOrWhiteSpace(options.ManagedActionSaveDirectory))
                {
                    throw new ArgumentException("--initialize-decoded-profile requires --managed-action-save-dir <path>.");
                }

                modStateSucceeded &= DecodedProfileInitializer.Run(
                    config,
                    patchPlan,
                    log,
                    projectRoot,
                    options.ManagedActionSaveDirectory,
                    options.WriteManagedActions,
                    options.ModStateId,
                    options.EventPayload,
                    options.EventPayloadFile).Succeeded;
            }

            if (options.InspectMapFile)
            {
                if (string.IsNullOrWhiteSpace(options.MapFilePath))
                {
                    throw new ArgumentException("--inspect-map-file requires a path.");
                }

                var outputPath = ResolveMapReportOutputPath(projectRoot, config.LogDirectory, options.MapFilePath, options.MapReportOutputPath);
                SaveDirectoryWatcher.WriteMapFileInspectionReport(options.MapFilePath, outputPath, log);
            }

            if (options.PrototypeMapFinalRoom)
            {
                if (string.IsNullOrWhiteSpace(options.MapPrototypeSourcePath))
                {
                    throw new ArgumentException("--prototype-map-final-room requires a source .dm path.");
                }

                if (string.IsNullOrWhiteSpace(options.MapFinalRoomId))
                {
                    throw new ArgumentException("--prototype-map-final-room requires --map-final-room-id <areaId>.");
                }

                var outputPath = ResolveMapPrototypeOutputPath(projectRoot, options.MapPrototypeSourcePath, options.MapFinalRoomId, options.MapPrototypeOutputPath);
                var reportPath = ResolveMapPrototypeReportOutputPath(projectRoot, config.LogDirectory, options.MapPrototypeSourcePath, options.MapFinalRoomId, options.MapPrototypeReportOutputPath);
                SaveDirectoryWatcher.WriteMapFinalRoomPrototype(options.MapPrototypeSourcePath, outputPath, options.MapFinalRoomId, reportPath, log);
            }

            if (options.PrototypeMapTemplate)
            {
                if (string.IsNullOrWhiteSpace(options.MapPrototypeSourcePath))
                {
                    throw new ArgumentException("--prototype-map-template requires a source .dm path.");
                }

                if (string.IsNullOrWhiteSpace(options.MapTemplateSpecPath))
                {
                    throw new ArgumentException("--prototype-map-template requires --map-template-spec <path>.");
                }

                var outputPath = ResolveMapTemplateOutputPath(projectRoot, options.MapPrototypeSourcePath, options.MapTemplateSpecPath, options.MapPrototypeOutputPath);
                var reportPath = ResolveMapTemplateReportOutputPath(projectRoot, config.LogDirectory, options.MapPrototypeSourcePath, options.MapTemplateSpecPath, options.MapPrototypeReportOutputPath);
                SaveDirectoryWatcher.WriteMapTemplatePrototype(options.MapPrototypeSourcePath, options.MapTemplateSpecPath, outputPath, reportPath, log);
            }

            if (options.WatchSavesForMilliseconds.HasValue)
            {
                using var diagnosticWatcher = SaveDirectoryWatcher.Start(config, patchPlan, projectRoot, log);
                if (diagnosticWatcher is null)
                {
                    log.Warn("Watch-save diagnostic requested, but no save watcher was started.");
                    return 3;
                }

                log.Info($"Watch-save diagnostic running for {options.WatchSavesForMilliseconds.Value} milliseconds.");
                Thread.Sleep(TimeSpan.FromMilliseconds(options.WatchSavesForMilliseconds.Value));
                log.Info("Watch-save diagnostic completed.");
                return 0;
            }

            if (options.ListPatches ||
                options.ExplainPatches ||
                options.ExplainRules ||
                options.ValidateOnly ||
                options.PreviewPatches ||
                options.InitModState ||
                options.DumpModState ||
                options.InferSaveEvents ||
                options.ApplyManagedActions ||
                options.InitializeDecodedProfile ||
                options.PreviewQuestBoard ||
                options.InspectMapFile ||
                options.PrototypeMapFinalRoom ||
                options.PrototypeMapTemplate ||
                options.WatchSavesForMilliseconds.HasValue ||
                !string.IsNullOrWhiteSpace(options.EmitEvent))
            {
                log.Info("Inspection requested. No process was started.");
                return modStateSucceeded ? 0 : 3;
            }

            var managedOverlay = ManagedActionOverlayCompiler.Compile(config, log);
            var questBoardPreview = QuestBoardPreviewReporter.Write(config, log);
            var questBoardLaunchPreflight = QuestBoardLaunchPreflightReporter.Write(
                config,
                log,
                questBoardPreview,
                managedOverlay,
                options.DryRun ? "dry-run" : "launch");
            if (!questBoardLaunchPreflight.Succeeded)
            {
                return 3;
            }

            var runtimeEnvironment = config.BuildRuntimeEnvironment(projectRoot, patchPlan);
            ManagedActionOverlayCompiler.ApplyEnvironment(runtimeEnvironment, managedOverlay);

            if (options.DryRun)
            {
                log.Info("Dry run requested. No process was started.");
                return 0;
            }

            int? gameProcessId = null;
            using var saveWatcher = SaveDirectoryWatcher.Start(config, patchPlan, projectRoot, log);

            if (config.EnableInjection && !options.NoInject && config.StartSuspendedForInjection)
            {
                using var environmentScope = ProcessEnvironmentScope.Apply(runtimeEnvironment);
                using var suspendedProcess = SuspendedProcess.Start(
                    config.GameExecutablePath,
                    config.GameWorkingDirectory,
                    config.GameArguments);
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
                foreach (var argument in config.GameArguments)
                {
                    if (!string.IsNullOrWhiteSpace(argument))
                    {
                        startInfo.ArgumentList.Add(argument);
                    }
                }

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
        if (options.WriteManagedActions && !options.ApplyManagedActions)
        {
            if (!options.InitializeDecodedProfile)
                throw new InvalidOperationException("--write-managed-actions requires --apply-managed-actions or --initialize-decoded-profile.");
        }

        if (options.InitializeDecodedProfile && options.ApplyManagedActions)
            throw new InvalidOperationException("--initialize-decoded-profile cannot be combined with --apply-managed-actions because it runs managed action application internally.");

        if (!File.Exists(config.GameExecutablePath))
            throw new FileNotFoundException("Game executable was not found.", config.GameExecutablePath);

        if (!Directory.Exists(config.GameWorkingDirectory))
            throw new DirectoryNotFoundException($"Game working directory was not found: {config.GameWorkingDirectory}");

        var willStartGame = !options.DryRun &&
            !options.ListPatches &&
            !options.ExplainPatches &&
            !options.ExplainRules &&
            !options.ValidateOnly &&
            !options.PreviewPatches &&
            !options.InitModState &&
            !options.DumpModState &&
            !options.InferSaveEvents &&
            !options.ApplyManagedActions &&
            !options.InitializeDecodedProfile &&
            !options.PreviewQuestBoard &&
            !options.InspectMapFile &&
            !options.PrototypeMapFinalRoom &&
            !options.PrototypeMapTemplate &&
            !options.WatchSavesForMilliseconds.HasValue &&
            string.IsNullOrWhiteSpace(options.EmitEvent);
        if (willStartGame && config.EnableInjection && !options.NoInject && !File.Exists(config.RuntimeDllPath))
            throw new FileNotFoundException("Runtime DLL was not found. Build runtime/RuntimeHook.vcxproj first.", config.RuntimeDllPath);

        var gameArch = PeArchitecture.Read(config.GameExecutablePath);
        if (gameArch == "x64" && !Environment.Is64BitProcess)
            throw new InvalidOperationException("x64 game requires x64 launcher and x64 RuntimeHook.dll.");

        if (gameArch == "x86" && Environment.Is64BitProcess)
            log.Warn("x86 game detected. This skeleton is configured for x64; use a matching x86 launcher and DLL before injecting.");

        if (config.SaveWatchAfterExitSeconds < 0)
            throw new InvalidOperationException("saveWatchAfterExitSeconds must be zero or greater.");

        if (config.SaveEventBridgeDebounceMilliseconds < 0)
            throw new InvalidOperationException("saveEventBridgeDebounceMilliseconds must be zero or greater.");
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

    private static string ResolveMapReportOutputPath(string projectRoot, string logDirectory, string mapFilePath, string? requestedPath)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            return ResolvePreviewOutputPath(projectRoot, requestedPath);
        }

        var mapName = Path.GetFileNameWithoutExtension(mapFilePath);
        var safeMapName = string.Concat(mapName.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_'));
        var fileName = $"{safeMapName}_{DateTimeOffset.Now:yyyyMMdd_HHmmss_fff}.json";
        return Path.Combine(logDirectory, "map_file_reports", fileName);
    }

    private static string ResolveMapPrototypeOutputPath(string projectRoot, string mapFilePath, string targetFinalRoomId, string? requestedPath)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            return ResolvePreviewOutputPath(projectRoot, requestedPath);
        }

        var mapName = Path.GetFileNameWithoutExtension(mapFilePath);
        var safeMapName = string.Concat(mapName.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_'));
        var safeFinalRoom = string.Concat(targetFinalRoomId.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_'));
        var fileName = $"{safeMapName}_final_{safeFinalRoom}_{DateTimeOffset.Now:yyyyMMdd_HHmmss_fff}.dm";
        return Path.Combine(projectRoot, "logs", "map_prototypes", fileName);
    }

    private static string ResolveMapPrototypeReportOutputPath(string projectRoot, string logDirectory, string mapFilePath, string targetFinalRoomId, string? requestedPath)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            return ResolvePreviewOutputPath(projectRoot, requestedPath);
        }

        var mapName = Path.GetFileNameWithoutExtension(mapFilePath);
        var safeMapName = string.Concat(mapName.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_'));
        var safeFinalRoom = string.Concat(targetFinalRoomId.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_'));
        var fileName = $"{safeMapName}_final_{safeFinalRoom}_{DateTimeOffset.Now:yyyyMMdd_HHmmss_fff}.json";
        return Path.Combine(logDirectory, "map_prototype_reports", fileName);
    }

    private static string ResolveMapTemplateOutputPath(string projectRoot, string mapFilePath, string specPath, string? requestedPath)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            return ResolvePreviewOutputPath(projectRoot, requestedPath);
        }

        var safeMapName = SanitizeFileName(Path.GetFileNameWithoutExtension(mapFilePath));
        var safeSpecName = SanitizeFileName(Path.GetFileNameWithoutExtension(specPath));
        var fileName = $"{safeMapName}_template_{safeSpecName}_{DateTimeOffset.Now:yyyyMMdd_HHmmss_fff}.dm";
        return Path.Combine(projectRoot, "logs", "map_templates", fileName);
    }

    private static string ResolveMapTemplateReportOutputPath(string projectRoot, string logDirectory, string mapFilePath, string specPath, string? requestedPath)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            return ResolvePreviewOutputPath(projectRoot, requestedPath);
        }

        var safeMapName = SanitizeFileName(Path.GetFileNameWithoutExtension(mapFilePath));
        var safeSpecName = SanitizeFileName(Path.GetFileNameWithoutExtension(specPath));
        var fileName = $"{safeMapName}_template_{safeSpecName}_{DateTimeOffset.Now:yyyyMMdd_HHmmss_fff}.json";
        return Path.Combine(logDirectory, "map_template_reports", fileName);
    }

    private static string SanitizeFileName(string value)
    {
        return string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_'));
    }
}
