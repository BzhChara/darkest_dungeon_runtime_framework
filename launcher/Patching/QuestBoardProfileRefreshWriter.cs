using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static class QuestBoardProfileRefreshWriter
{
    private const int ReportVersion = 1;
    private const string ReportFileName = "quest_board_profile_refresh_report.json";
    private const string PersistQuestFileName = "persist.quest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static QuestBoardProfileRefreshReport Write(
        RuntimeConfig config,
        LauncherLog log,
        string projectRoot,
        QuestBoardRuntimeOverlayReport overlayReport,
        string profileId,
        bool dryRun,
        bool allowRunningGameSaveWrite,
        string? expectedProfileRoot = null)
    {
        var reportPath = Path.Combine(config.LogDirectory, ReportFileName);
        var issues = new List<QuestBoardProfileRefreshIssue>();
        var normalizedProfileId = profileId.Trim();
        var matchingProfiles = overlayReport.Profiles
            .Where(item => item.ProfileId.Equals(normalizedProfileId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var expectedSourcePath = string.IsNullOrWhiteSpace(expectedProfileRoot)
            ? string.Empty
            : Path.GetFullPath(Path.Combine(expectedProfileRoot, PersistQuestFileName));
        var profile = string.IsNullOrWhiteSpace(expectedSourcePath)
            ? matchingProfiles.FirstOrDefault()
            : matchingProfiles.FirstOrDefault(item =>
                Path.GetFullPath(item.SourcePath).Equals(expectedSourcePath, StringComparison.OrdinalIgnoreCase));

        string sourcePath = profile?.SourcePath ?? expectedSourcePath;
        string replacementPath = profile?.RuntimeSourcePath ?? string.Empty;
        string backupPath = string.Empty;
        string sourceHashBefore = string.Empty;
        string replacementHash = string.Empty;
        string sourceHashAfter = string.Empty;
        string writeMode = "none";
        var changed = false;
        var written = false;

        if (string.IsNullOrWhiteSpace(normalizedProfileId))
        {
            issues.Add(Error("missing-profile-id", string.Empty, "--refresh-quest-board-profile requires a non-empty profile id."));
        }

        if (!overlayReport.Succeeded)
        {
            issues.Add(Error(
                "quest-board-runtime-overlay-failed",
                overlayReport.ReportPath,
                "quest board runtime overlay report contains errors; refresh writer will not mutate a save."));
        }

        if (!string.IsNullOrWhiteSpace(overlayReport.TargetProfileId) &&
            !overlayReport.TargetProfileId.Equals(normalizedProfileId, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Error(
                "profile-scope-mismatch",
                overlayReport.ReportPath,
                $"runtime overlay target profile is {overlayReport.TargetProfileId}, but refresh target is {normalizedProfileId}."));
        }

        if (profile is null)
        {
            issues.Add(Error(
                "profile-not-found",
                string.IsNullOrWhiteSpace(expectedSourcePath) ? normalizedProfileId : expectedSourcePath,
                string.IsNullOrWhiteSpace(expectedSourcePath)
                    ? $"profile was not present in quest board runtime overlay report: {normalizedProfileId}"
                    : $"profile source was not present in quest board runtime overlay report: {expectedSourcePath}"));
        }
        else if (!profile.Status.Equals("ready", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Error(
                "profile-overlay-not-ready",
                profile.SourcePath,
                $"profile overlay status is {profile.Status}; refresh writer requires ready."));
        }

        if (profile is not null)
        {
            ValidatePaths(config, sourcePath, replacementPath, issues);
            ValidateReplacementShape(replacementPath, issues);
            if (!dryRun && ShouldBlockRunningGame(projectRoot, sourcePath, allowRunningGameSaveWrite, config.GameExecutablePath))
            {
                issues.Add(Error(
                    "game-running",
                    sourcePath,
                    "Darkest.exe is running while the target save is outside the project root; exit the game or pass --allow-running-game-save-write."));
            }
        }

        if (!issues.Any(issue => issue.Severity == "error") && profile is not null)
        {
            sourceHashBefore = ComputeSha256(sourcePath);
            replacementHash = ComputeSha256(replacementPath);
            changed = !sourceHashBefore.Equals(replacementHash, StringComparison.OrdinalIgnoreCase);

            if (!dryRun && changed)
            {
                try
                {
                    backupPath = BackupSource(config, normalizedProfileId, sourcePath);
                    writeMode = WriteReplacement(sourcePath, replacementPath);
                    sourceHashAfter = ComputeSha256(sourcePath);
                    written = true;
                    if (!sourceHashAfter.Equals(replacementHash, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(Error(
                            "written-hash-mismatch",
                            sourcePath,
                            "persist.quest.json was written, but the final hash does not match the generated replacement."));
                    }
                }
                catch (Exception ex)
                {
                    issues.Add(Error(
                        "write-failed",
                        sourcePath,
                        ex.Message));
                }
            }
            else if (!dryRun)
            {
                sourceHashAfter = sourceHashBefore;
            }
        }

        var status = BuildStatus(issues, dryRun, changed, written);
        var report = new QuestBoardProfileRefreshReport(
            ReportVersion,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            reportPath,
            status,
            normalizedProfileId,
            sourcePath,
            replacementPath,
            backupPath,
            dryRun,
            changed,
            written,
            writeMode,
            sourceHashBefore,
            replacementHash,
            sourceHashAfter,
            issues.Count(issue => issue.Severity == "warning"),
            issues.Count(issue => issue.Severity == "error"),
            issues);

        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        log.Info(
            $"quest-board-profile-refresh report path={Quote(reportPath)} profile={Quote(normalizedProfileId)} " +
            $"status={report.Status} dryRun={report.DryRun} changed={report.Changed} written={report.Written} " +
            $"warnings={report.WarningCount} errors={report.ErrorCount}");
        foreach (var issue in issues)
        {
            var line =
                $"quest-board-profile-refresh issue severity={issue.Severity} code={issue.Code} " +
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

    private static void ValidatePaths(
        RuntimeConfig config,
        string sourcePath,
        string replacementPath,
        List<QuestBoardProfileRefreshIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            issues.Add(Error("source-missing", sourcePath, $"source {PersistQuestFileName} was not found."));
            return;
        }

        if (!config.SaveWatchDirectories.Any(root => IsInsideDirectory(root, sourcePath)))
        {
            issues.Add(Error(
                "source-outside-save-watch",
                sourcePath,
                "source save path is outside configured saveWatchDirectories."));
        }

        if (string.IsNullOrWhiteSpace(replacementPath) || !File.Exists(replacementPath))
        {
            issues.Add(Error("replacement-missing", replacementPath, "generated quest-board replacement file was not found."));
        }
    }

    private static void ValidateReplacementShape(string replacementPath, List<QuestBoardProfileRefreshIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(replacementPath) || !File.Exists(replacementPath))
        {
            return;
        }

        var bytes = File.ReadAllBytes(replacementPath);
        if (bytes.Length >= 4 && bytes[0] == 0x01 && bytes[1] == 0xB1 && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return;
        }

        if (LooksLikeJsonText(bytes))
        {
            var root = JsonNode.Parse(DecodeUtf8JsonText(bytes)) as JsonObject
                ?? throw new InvalidDataException($"replacement {PersistQuestFileName} JSON root must be an object: {replacementPath}");
            if (root["base_root"] is JsonObject baseRoot && baseRoot["quests"] is JsonObject)
            {
                return;
            }
        }

        issues.Add(Error(
            "replacement-invalid-shape",
            replacementPath,
            $"replacement {PersistQuestFileName} is neither DSON save data nor decoded quest-board JSON."));
    }

    private static bool ShouldBlockRunningGame(
        string projectRoot,
        string sourcePath,
        bool allowRunningGameSaveWrite,
        string gameExecutablePath)
    {
        if (allowRunningGameSaveWrite || IsInsideDirectory(projectRoot, sourcePath))
        {
            return false;
        }

        var expectedPath = Path.GetFullPath(gameExecutablePath);
        foreach (var process in Process.GetProcessesByName("Darkest"))
        {
            try
            {
                if (Path.GetFullPath(process.MainModule?.FileName ?? string.Empty)
                    .Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                return true;
            }
        }

        return false;
    }

    private static string BackupSource(RuntimeConfig config, string profileId, string sourcePath)
    {
        var backupDirectory = Path.Combine(
            config.ModStateDirectory,
            "_live_save_backups",
            "quest_board_refresh",
            DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture),
            SafeFileName(profileId));
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, PersistQuestFileName);
        File.Copy(sourcePath, backupPath, overwrite: false);
        return backupPath;
    }

    private static string WriteReplacement(string sourcePath, string replacementPath)
    {
        File.Copy(replacementPath, sourcePath, overwrite: true);
        return "direct-overwrite-after-backup";
    }

    private static string BuildStatus(
        IReadOnlyList<QuestBoardProfileRefreshIssue> issues,
        bool dryRun,
        bool changed,
        bool written)
    {
        if (issues.Any(issue => issue.Severity == "error"))
        {
            return "blocked";
        }

        if (dryRun)
        {
            return changed ? "dry-run-would-write" : "dry-run-unchanged";
        }

        if (written)
        {
            return "written";
        }

        return changed ? "pending" : "unchanged";
    }

    private static bool IsInsideDirectory(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool LooksLikeJsonText(byte[] bytes)
    {
        var index = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            index = 3;
        }

        while (index < bytes.Length && bytes[index] is 0x09 or 0x0A or 0x0D or 0x20)
        {
            index++;
        }

        return index < bytes.Length && bytes[index] is (byte)'{' or (byte)'[';
    }

    private static string DecodeUtf8JsonText(byte[] bytes)
    {
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? 3
            : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static QuestBoardProfileRefreshIssue Error(string code, string path, string message)
    {
        return new QuestBoardProfileRefreshIssue("error", code, path, message);
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) || ch is '/' or '\\' ? '_' : ch);
        }

        return builder.ToString();
    }

    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
}

internal sealed record QuestBoardProfileRefreshReport(
    int Version,
    string GeneratedAtUtc,
    string ReportPath,
    string Status,
    string ProfileId,
    string SourcePath,
    string ReplacementPath,
    string BackupPath,
    bool DryRun,
    bool Changed,
    bool Written,
    string WriteMode,
    string SourceHashBefore,
    string ReplacementHash,
    string SourceHashAfter,
    int WarningCount,
    int ErrorCount,
    IReadOnlyList<QuestBoardProfileRefreshIssue> Issues)
{
    public bool Succeeded => ErrorCount == 0;
}

internal sealed record QuestBoardProfileRefreshIssue(
    string Severity,
    string Code,
    string Path,
    string Message);
