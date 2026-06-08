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
            log.Info($"Mod state directory: {config.ModStateDirectory}");
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
                    !options.DumpModState)
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
            if (options.InitModState)
            {
                modStateSucceeded &= ModStateStore.InitializeDefaults(config, patchPlan, log, options.ModStateId).Succeeded;
            }

            if (options.DumpModState)
            {
                modStateSucceeded &= ModStateStore.Dump(config, patchPlan, log, options.ModStateId).Succeeded;
            }

            if (options.ListPatches ||
                options.ExplainPatches ||
                options.ExplainRules ||
                options.ValidateOnly ||
                options.PreviewPatches ||
                options.InitModState ||
                options.DumpModState)
            {
                log.Info("Inspection requested. No process was started.");
                return modStateSucceeded ? 0 : 3;
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

        var willStartGame = !options.DryRun &&
            !options.ListPatches &&
            !options.ExplainPatches &&
            !options.ExplainRules &&
            !options.ValidateOnly &&
            !options.PreviewPatches &&
            !options.InitModState &&
            !options.DumpModState;
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
