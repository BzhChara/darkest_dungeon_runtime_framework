using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static class ContinuousProfileActionRefreshWriter
{
    private const int ReportVersion = 1;
    private const string ReportFileName = "continuous_profile_action_refresh_report.json";

    private static readonly string[] SourceFileNames =
    [
        "persist.town.json",
        "persist.town_event.json"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static ContinuousProfileActionRefreshReport Write(
        RuntimeConfig config,
        LauncherLog log,
        string projectRoot,
        string profileId,
        string profileRoot,
        bool dryRun,
        bool allowRunningGameSaveWrite)
    {
        var reportPath = Path.Combine(config.LogDirectory, ReportFileName);
        var issues = new List<ContinuousProfileActionRefreshIssue>();
        var files = new List<ContinuousProfileActionRefreshFileReport>();
        var normalizedProfileId = profileId.Trim();
        var resolvedProfileRoot = Path.GetFullPath(profileRoot);
        var workspaceRoot = Path.Combine(
            config.ModStateDirectory,
            "_continuous_profile_apply",
            SafeFileName(normalizedProfileId),
            DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture));
        var decodedSaveDirectory = Path.Combine(workspaceRoot, "decoded_save");
        var runtimeDirectory = Path.Combine(workspaceRoot, "runtime_profile");
        var sourceFormats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ManagedActionApplyReport? applyReport = null;

        if (string.IsNullOrWhiteSpace(normalizedProfileId))
        {
            issues.Add(Error("missing-profile-id", string.Empty, "continuous profile action refresh requires a non-empty profile id."));
        }

        if (!Directory.Exists(resolvedProfileRoot))
        {
            issues.Add(Error("profile-root-missing", resolvedProfileRoot, "profile root does not exist."));
        }

        if (!config.SaveWatchDirectories.Any(root => IsInsideDirectory(root, resolvedProfileRoot)))
        {
            issues.Add(Error("profile-root-outside-watch-roots", resolvedProfileRoot, "profile root is outside configured saveWatchDirectories."));
        }

        if (!dryRun && ShouldBlockRunningGame(projectRoot, resolvedProfileRoot, allowRunningGameSaveWrite, config.GameExecutablePath))
        {
            issues.Add(Error(
                "game-running",
                resolvedProfileRoot,
                "Darkest.exe is running while the target save is outside the project root; enable continuousProfileActionAutoApplyAllowRunningGameSaveWrite or exit the game."));
        }

        if (!issues.Any(issue => issue.Severity == "error"))
        {
            Directory.CreateDirectory(decodedSaveDirectory);
            Directory.CreateDirectory(runtimeDirectory);
            foreach (var fileName in SourceFileNames)
            {
                var sourcePath = Path.Combine(resolvedProfileRoot, fileName);
                if (!File.Exists(sourcePath))
                {
                    issues.Add(Warning("source-file-missing", sourcePath, "continuous profile source file does not exist; related actions may fail."));
                    continue;
                }

                var decodedPath = Path.Combine(decodedSaveDirectory, fileName);
                try
                {
                    var format = DecodeOrCopySource(config, sourcePath, decodedPath);
                    sourceFormats[fileName] = format;
                    files.Add(new ContinuousProfileActionRefreshFileReport(
                        fileName,
                        sourcePath,
                        decodedPath,
                        string.Empty,
                        string.Empty,
                        format,
                        string.Empty,
                        false,
                        false,
                        false,
                        "decoded",
                        []));
                }
                catch (Exception ex)
                {
                    issues.Add(Error("source-decode-failed", sourcePath, ex.Message));
                }
            }
        }

        if (!issues.Any(issue => issue.Severity == "error"))
        {
            applyReport = ManagedActionSaveApplier.Apply(
                config,
                log,
                projectRoot,
                decodedSaveDirectory,
                !dryRun,
                ManagedActionApplyMode.ContinuousProfile);
            if (!applyReport.Succeeded)
            {
                issues.Add(Error(
                    "managed-action-apply-failed",
                    applyReport.SaveDirectory,
                    "continuous managed action application failed; no live profile files were promoted."));
            }
        }

        if (!issues.Any(issue => issue.Severity == "error") && applyReport is not null)
        {
            foreach (var file in applyReport.Files.Where(file => file.Written || file.ChangeCount > 0))
            {
                var fileName = Path.GetFileName(file.Path);
                if (!fileName.StartsWith("persist.", StringComparison.OrdinalIgnoreCase) ||
                    !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(Warning("policy-only-file-not-promoted", file.Path, "continuous apply wrote a project-local policy file; live profile promotion only writes persist.*.json files."));
                    continue;
                }

                var sourcePath = Path.Combine(resolvedProfileRoot, fileName);
                if (!File.Exists(sourcePath))
                {
                    issues.Add(Error("target-source-missing", sourcePath, "target persist file disappeared before promotion."));
                    continue;
                }

                var runtimePath = Path.Combine(runtimeDirectory, fileName);
                var format = sourceFormats.TryGetValue(fileName, out var value) ? value : "json";
                try
                {
                    EncodeOrCopyRuntime(config, file.Path, runtimePath, format);
                    var sourceHashBefore = ComputeSha256(sourcePath);
                    var runtimeHash = ComputeSha256(runtimePath);
                    var changed = !sourceHashBefore.Equals(runtimeHash, StringComparison.OrdinalIgnoreCase);
                    var backupPath = string.Empty;
                    var written = false;
                    if (!dryRun && changed)
                    {
                        backupPath = BackupSource(config, normalizedProfileId, sourcePath);
                        WriteReplacement(sourcePath, runtimePath);
                        written = true;
                        var sourceHashAfter = ComputeSha256(sourcePath);
                        if (!sourceHashAfter.Equals(runtimeHash, StringComparison.OrdinalIgnoreCase))
                        {
                            issues.Add(Error("written-hash-mismatch", sourcePath, "promoted persist file hash does not match generated runtime file."));
                        }
                    }

                    files.Add(new ContinuousProfileActionRefreshFileReport(
                        fileName,
                        sourcePath,
                        file.Path,
                        runtimePath,
                        backupPath,
                        format,
                        format,
                        changed,
                        written,
                        dryRun,
                        changed ? "ready" : "unchanged",
                        []));
                }
                catch (Exception ex)
                {
                    issues.Add(Error("target-promote-failed", sourcePath, ex.Message));
                }
            }
        }

        var report = new ContinuousProfileActionRefreshReport(
            ReportVersion,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            reportPath,
            normalizedProfileId,
            resolvedProfileRoot,
            workspaceRoot,
            decodedSaveDirectory,
            runtimeDirectory,
            dryRun,
            applyReport?.Succeeded == true,
            applyReport?.ArtifactCount ?? 0,
            applyReport?.AppliedActionCount ?? 0,
            applyReport?.DryRunActionCount ?? 0,
            applyReport?.FailedActionCount ?? 0,
            applyReport?.ChangedFileCount ?? 0,
            files.Count(file => file.Changed),
            files.Count(file => file.Written),
            issues.Count(issue => issue.Severity == "warning"),
            issues.Count(issue => issue.Severity == "error"),
            files,
            issues);

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        log.Info(
            $"continuous-profile-refresh report path={Quote(reportPath)} profile={Quote(normalizedProfileId)} " +
            $"dryRun={dryRun} applySucceeded={report.ApplySucceeded} artifacts={report.ApplyArtifactCount} " +
            $"changedFiles={report.ChangedFileCount} promotedChanged={report.PromotedChangedFileCount} " +
            $"written={report.PromotedWrittenFileCount} warnings={report.WarningCount} errors={report.ErrorCount}");
        foreach (var issue in issues)
        {
            var line =
                $"continuous-profile-refresh issue severity={issue.Severity} code={issue.Code} " +
                $"path={Quote(issue.Path)} message={Quote(issue.Message)}";
            if (issue.Severity == "error")
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

    private static string DecodeOrCopySource(RuntimeConfig config, string sourcePath, string decodedPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(decodedPath) ?? ".");
        var bytes = File.ReadAllBytes(sourcePath);
        if (LooksLikeJsonText(bytes))
        {
            File.WriteAllText(decodedPath, DecodeUtf8JsonText(bytes), Encoding.UTF8);
            return "json";
        }

        DecodeDson(config, sourcePath, decodedPath);
        return "dson";
    }

    private static void EncodeOrCopyRuntime(RuntimeConfig config, string decodedPath, string runtimePath, string sourceFormat)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(runtimePath) ?? ".");
        if (sourceFormat.Equals("dson", StringComparison.OrdinalIgnoreCase))
        {
            EncodeDson(config, decodedPath, runtimePath);
            if (!File.Exists(runtimePath) || new FileInfo(runtimePath).Length == 0)
            {
                throw new InvalidDataException($"DSON encode produced an empty or missing file: {runtimePath}");
            }
            return;
        }

        File.Copy(decodedPath, runtimePath, overwrite: true);
    }

    private static bool LooksLikeJsonText(byte[] bytes)
    {
        var index = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            index = 3;
        }

        while (index < bytes.Length && char.IsWhiteSpace((char)bytes[index]))
        {
            index++;
        }

        return index < bytes.Length && (bytes[index] == (byte)'{' || bytes[index] == (byte)'[');
    }

    private static string DecodeUtf8JsonText(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    private static void DecodeDson(RuntimeConfig config, string inputPath, string outputPath)
    {
        EnsureDsonCodecAvailable(config);
        RunJava(
            [
                "-jar",
                config.DsonSaveEditorJarPath,
                "decode",
                "--output",
                outputPath,
                inputPath
            ]);
    }

    private static void EncodeDson(RuntimeConfig config, string inputPath, string outputPath)
    {
        EnsureDsonCodecAvailable(config);
        RunJava(
            [
                "-jar",
                config.DsonSaveEditorJarPath,
                "encode",
                "--output",
                outputPath,
                inputPath
            ]);
    }

    private static void EnsureDsonCodecAvailable(RuntimeConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.DsonSaveEditorJarPath) || !File.Exists(config.DsonSaveEditorJarPath))
        {
            throw new FileNotFoundException("dsonSaveEditorJarPath does not point to an existing DDSaveEditor jar.", config.DsonSaveEditorJarPath);
        }
    }

    private static void RunJava(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "java",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start java for DDSaveEditor.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"DDSaveEditor failed with exit code {process.ExitCode}. stdout={stdout} stderr={stderr}");
        }
    }

    private static string BackupSource(RuntimeConfig config, string profileId, string sourcePath)
    {
        var backupDirectory = Path.Combine(
            config.ModStateDirectory,
            "_live_save_backups",
            "continuous_profile_apply",
            SafeFileName(profileId),
            DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, backupPath, overwrite: false);
        return backupPath;
    }

    private static void WriteReplacement(string sourcePath, string replacementPath)
    {
        var tempPath = sourcePath + ".ddrt.tmp";
        if (File.Exists(tempPath))
        {
            throw new IOException($"Temporary replacement file already exists: {tempPath}");
        }

        File.Copy(replacementPath, tempPath, overwrite: false);
        try
        {
            File.Copy(tempPath, sourcePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static bool ShouldBlockRunningGame(string projectRoot, string sourcePath, bool allowRunningGameSaveWrite, string gameExecutablePath)
    {
        if (allowRunningGameSaveWrite || IsInsideDirectory(projectRoot, sourcePath))
        {
            return false;
        }

        var gameName = Path.GetFileNameWithoutExtension(gameExecutablePath);
        if (string.IsNullOrWhiteSpace(gameName))
        {
            gameName = "Darkest";
        }

        return Process.GetProcessesByName(gameName).Any(process =>
        {
            try
            {
                return !process.HasExited;
            }
            catch
            {
                return true;
            }
            finally
            {
                process.Dispose();
            }
        });
    }

    private static bool IsInsideDirectory(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        if (Directory.Exists(normalizedPath))
        {
            normalizedPath = normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static ContinuousProfileActionRefreshIssue Error(string code, string path, string message)
    {
        return new ContinuousProfileActionRefreshIssue("error", code, path, message);
    }

    private static ContinuousProfileActionRefreshIssue Warning(string code, string path, string message)
    {
        return new ContinuousProfileActionRefreshIssue("warning", code, path, message);
    }

    private static string SafeFileName(string value)
    {
        return string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_'));
    }

    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
}

internal sealed record ContinuousProfileActionRefreshReport(
    int Version,
    string GeneratedAtUtc,
    string ReportPath,
    string ProfileId,
    string ProfileRoot,
    string WorkspaceRoot,
    string DecodedSaveDirectory,
    string RuntimeDirectory,
    bool DryRun,
    bool ApplySucceeded,
    int ApplyArtifactCount,
    int AppliedActionCount,
    int DryRunActionCount,
    int FailedActionCount,
    int ChangedFileCount,
    int PromotedChangedFileCount,
    int PromotedWrittenFileCount,
    int WarningCount,
    int ErrorCount,
    IReadOnlyList<ContinuousProfileActionRefreshFileReport> Files,
    IReadOnlyList<ContinuousProfileActionRefreshIssue> Issues)
{
    public bool Succeeded => ErrorCount == 0 && ApplySucceeded;
}

internal sealed record ContinuousProfileActionRefreshFileReport(
    string FileName,
    string SourcePath,
    string DecodedPath,
    string RuntimePath,
    string BackupPath,
    string SourceFormat,
    string RuntimeFormat,
    bool Changed,
    bool Written,
    bool DryRun,
    string Status,
    IReadOnlyList<string> Issues);

internal sealed record ContinuousProfileActionRefreshIssue(
    string Severity,
    string Code,
    string Path,
    string Message);
