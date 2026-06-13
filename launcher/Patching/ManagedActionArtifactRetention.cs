using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static class ManagedActionArtifactRetention
{
    private const int ReportVersion = 1;
    private const string ReportFileName = "managed_action_retention_report.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static ManagedActionRetentionReport Write(
        RuntimeConfig config,
        LauncherLog log,
        bool apply,
        int keepLatestPerGroup)
    {
        if (keepLatestPerGroup <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keepLatestPerGroup), "managed action retention keep count must be positive.");
        }

        var artifactDirectory = Path.Combine(config.ModStateDirectory, "_managed_actions");
        var reportPath = Path.Combine(config.LogDirectory, ReportFileName);
        var issues = new List<ManagedActionRetentionIssue>();
        var artifacts = new List<RetentionArtifact>();

        if (Directory.Exists(artifactDirectory))
        {
            foreach (var artifactPath in Directory.EnumerateFiles(artifactDirectory, "*.json")
                         .OrderBy(path => File.GetLastWriteTimeUtc(path))
                         .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                artifacts.Add(ReadArtifact(artifactDirectory, artifactPath, issues));
            }
        }

        var validArtifacts = artifacts
            .Where(artifact => artifact.Valid)
            .GroupBy(artifact => artifact.GroupKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var groups = new List<ManagedActionRetentionGroupReport>();
        foreach (var group in validArtifacts)
        {
            var ordered = group
                .OrderByDescending(artifact => artifact.GeneratedAtUtc ?? DateTimeOffset.MinValue)
                .ThenByDescending(artifact => artifact.LastWriteUtc ?? DateTimeOffset.MinValue)
                .ThenByDescending(artifact => artifact.ArtifactPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            for (var index = 0; index < ordered.Length; index++)
            {
                var artifact = ordered[index];
                artifact.RankInGroup = index + 1;
                if (index < keepLatestPerGroup)
                {
                    artifact.Decision = "retain";
                    artifact.Reason = "within keepLatestPerGroup";
                    continue;
                }

                artifact.Decision = apply ? "delete" : "wouldDelete";
                artifact.Reason = $"older than latest {keepLatestPerGroup} artifact(s) in retention group";
                if (apply)
                {
                    TryDeleteArtifact(artifactDirectory, artifact, issues);
                }
            }

            var first = ordered[0];
            groups.Add(new ManagedActionRetentionGroupReport(
                group.Key,
                first.ActionType,
                first.PluginId,
                first.RuleId,
                first.ActionIndex,
                first.Target,
                first.ProfileScopeKind,
                first.ProfileScopeProfileId,
                ordered.Length,
                ordered.Count(artifact => artifact.Decision == "retain"),
                ordered.Count(artifact => artifact.Decision is "wouldDelete" or "delete" or "deleted" or "deleteFailed")));
        }

        foreach (var invalid in artifacts.Where(artifact => !artifact.Valid))
        {
            invalid.Decision = "retain";
            invalid.Reason = "artifact could not be parsed as a valid managed action";
        }

        var artifactReports = artifacts
            .OrderBy(artifact => artifact.GroupKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(artifact => artifact.RankInGroup ?? int.MaxValue)
            .ThenBy(artifact => artifact.ArtifactPath, StringComparer.OrdinalIgnoreCase)
            .Select(artifact => new ManagedActionRetentionArtifactReport(
                artifact.ArtifactPath,
                artifact.Valid,
                artifact.ActionType,
                artifact.PluginId,
                artifact.RuleId,
                artifact.ActionIndex,
                artifact.Target,
                artifact.ProfileScopeKind,
                artifact.ProfileScopeProfileId,
                artifact.GroupKey,
                artifact.RankInGroup,
                artifact.GeneratedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                artifact.LastWriteUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                artifact.Decision,
                artifact.Deleted,
                artifact.Reason))
            .ToArray();

        var report = new ManagedActionRetentionReport(
            ReportVersion,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            reportPath,
            artifactDirectory,
            apply ? "prune" : "dryRun",
            keepLatestPerGroup,
            artifacts.Count,
            groups.Count,
            artifactReports.Count(artifact => artifact.Decision == "retain"),
            artifactReports.Count(artifact => artifact.Decision is "wouldDelete" or "delete" or "deleted" or "deleteFailed"),
            artifactReports.Count(artifact => artifact.Deleted),
            issues.Count(issue => issue.Severity == "warning"),
            issues.Count(issue => issue.Severity == "error"),
            groups,
            artifactReports,
            issues);

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        log.Info(
            $"managed-action-retention report path={Quote(reportPath)} mode={report.Mode} " +
            $"artifacts={report.ArtifactCount} groups={report.GroupCount} keep={keepLatestPerGroup} " +
            $"prunable={report.PrunableCount} deleted={report.DeletedCount} warnings={report.WarningCount} errors={report.ErrorCount}");

        foreach (var issue in issues)
        {
            var line =
                $"managed-action-retention issue severity={issue.Severity} code={issue.Code} " +
                $"path={Quote(issue.ArtifactPath)} message={Quote(issue.Message)}";
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

    private static RetentionArtifact ReadArtifact(
        string artifactDirectory,
        string artifactPath,
        List<ManagedActionRetentionIssue> issues)
    {
        var fullPath = Path.GetFullPath(artifactPath);
        var lastWrite = File.GetLastWriteTimeUtc(fullPath);
        try
        {
            if (!IsInsideDirectory(artifactDirectory, fullPath))
            {
                throw new InvalidDataException("artifact path is outside the managed action artifact directory");
            }

            var artifact = JsonNode.Parse(File.ReadAllText(fullPath, Encoding.UTF8)) as JsonObject
                ?? throw new InvalidDataException("artifact root must be a JSON object");
            var actionType = ReadString(artifact, "action.type");
            if (string.IsNullOrWhiteSpace(actionType))
            {
                throw new InvalidDataException("artifact action.type is missing");
            }

            var profileScope = ManagedActionProfileScopeResolver.FromArtifact(artifact);
            var pluginId = ReadOptionalString(artifact, "pluginId");
            var ruleId = ReadOptionalString(artifact, "ruleId");
            var target = ReadOptionalString(artifact, "plan.target");
            if (string.IsNullOrWhiteSpace(target))
            {
                target = ReadOptionalString(artifact, "plan.arguments.target");
            }

            var actionIndex = ReadOptionalInt(artifact, "actionIndex");
            var sourcePath = ReadOptionalString(artifact, "sourcePath");
            var generatedAt = ReadOptionalDateTimeOffset(artifact, "generatedAtUtc");
            var groupKey = BuildGroupKey(
                actionType,
                pluginId,
                ruleId,
                actionIndex,
                target,
                sourcePath,
                profileScope);

            return new RetentionArtifact(
                fullPath,
                true,
                actionType,
                pluginId,
                ruleId,
                actionIndex,
                target,
                profileScope.Kind,
                profileScope.ProfileId,
                groupKey,
                generatedAt,
                new DateTimeOffset(DateTime.SpecifyKind(lastWrite, DateTimeKind.Utc)));
        }
        catch (Exception ex)
        {
            issues.Add(new ManagedActionRetentionIssue("warning", "managed-action-retention-artifact-invalid", fullPath, ex.Message));
            return new RetentionArtifact(
                fullPath,
                false,
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                string.Empty,
                string.Empty,
                string.Empty,
                "invalid",
                null,
                new DateTimeOffset(DateTime.SpecifyKind(lastWrite, DateTimeKind.Utc)));
        }
    }

    private static void TryDeleteArtifact(
        string artifactDirectory,
        RetentionArtifact artifact,
        List<ManagedActionRetentionIssue> issues)
    {
        try
        {
            if (!IsInsideDirectory(artifactDirectory, artifact.ArtifactPath))
            {
                throw new InvalidOperationException("refusing to delete artifact outside managed action artifact directory");
            }

            File.Delete(artifact.ArtifactPath);
            artifact.Decision = "deleted";
            artifact.Deleted = true;
        }
        catch (Exception ex)
        {
            artifact.Decision = "deleteFailed";
            artifact.Reason = ex.Message;
            issues.Add(new ManagedActionRetentionIssue("error", "managed-action-retention-delete-failed", artifact.ArtifactPath, ex.Message));
        }
    }

    private static string BuildGroupKey(
        string actionType,
        string pluginId,
        string ruleId,
        int? actionIndex,
        string target,
        string sourcePath,
        ManagedActionProfileScope profileScope)
    {
        return string.Join('|',
            NormalizeGroupPart(actionType),
            NormalizeGroupPart(pluginId),
            NormalizeGroupPart(ruleId),
            actionIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            NormalizeGroupPart(target),
            NormalizeGroupPart(profileScope.Kind),
            NormalizeGroupPart(profileScope.ProfileId),
            NormalizeGroupPart(sourcePath));
    }

    private static string ReadString(JsonObject root, string path)
    {
        return ReadNode(root, path)?.GetValue<string>() ?? string.Empty;
    }

    private static string ReadOptionalString(JsonObject root, string path)
    {
        return TryGetPath(root, path, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : string.Empty;
    }

    private static int? ReadOptionalInt(JsonObject root, string path)
    {
        return TryGetPath(root, path, out var node) && node is JsonValue value && value.TryGetValue<int>(out var result)
            ? result
            : null;
    }

    private static DateTimeOffset? ReadOptionalDateTimeOffset(JsonObject root, string path)
    {
        var text = ReadOptionalString(root, path);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result)
            ? result.ToUniversalTime()
            : null;
    }

    private static JsonNode? ReadNode(JsonObject root, string path)
    {
        return TryGetPath(root, path, out var node) ? node : null;
    }

    private static bool TryGetPath(JsonObject root, string path, out JsonNode? value)
    {
        value = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (value is not JsonObject obj || !obj.TryGetPropertyValue(part, out value))
            {
                value = null;
                return false;
            }
        }

        return true;
    }

    private static bool IsInsideDirectory(string directory, string path)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeGroupPart(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';

    private sealed class RetentionArtifact(
        string artifactPath,
        bool valid,
        string actionType,
        string pluginId,
        string ruleId,
        int? actionIndex,
        string target,
        string profileScopeKind,
        string profileScopeProfileId,
        string groupKey,
        DateTimeOffset? generatedAtUtc,
        DateTimeOffset? lastWriteUtc)
    {
        public string ArtifactPath { get; } = artifactPath;
        public bool Valid { get; } = valid;
        public string ActionType { get; } = actionType;
        public string PluginId { get; } = pluginId;
        public string RuleId { get; } = ruleId;
        public int? ActionIndex { get; } = actionIndex;
        public string Target { get; } = target;
        public string ProfileScopeKind { get; } = profileScopeKind;
        public string ProfileScopeProfileId { get; } = profileScopeProfileId;
        public string GroupKey { get; } = groupKey;
        public DateTimeOffset? GeneratedAtUtc { get; } = generatedAtUtc;
        public DateTimeOffset? LastWriteUtc { get; } = lastWriteUtc;
        public int? RankInGroup { get; set; }
        public string Decision { get; set; } = "retain";
        public bool Deleted { get; set; }
        public string Reason { get; set; } = string.Empty;
    }
}

internal sealed record ManagedActionRetentionReport(
    int Version,
    string GeneratedAtUtc,
    string ReportPath,
    string ArtifactDirectory,
    string Mode,
    int KeepLatestPerGroup,
    int ArtifactCount,
    int GroupCount,
    int RetainedCount,
    int PrunableCount,
    int DeletedCount,
    int WarningCount,
    int ErrorCount,
    IReadOnlyList<ManagedActionRetentionGroupReport> Groups,
    IReadOnlyList<ManagedActionRetentionArtifactReport> Artifacts,
    IReadOnlyList<ManagedActionRetentionIssue> Issues)
{
    public bool Succeeded => ErrorCount == 0;
}

internal sealed record ManagedActionRetentionGroupReport(
    string GroupKey,
    string ActionType,
    string PluginId,
    string RuleId,
    int? ActionIndex,
    string Target,
    string ProfileScopeKind,
    string ProfileScopeProfileId,
    int ArtifactCount,
    int RetainedCount,
    int PrunableCount);

internal sealed record ManagedActionRetentionArtifactReport(
    string ArtifactPath,
    bool Valid,
    string ActionType,
    string PluginId,
    string RuleId,
    int? ActionIndex,
    string Target,
    string ProfileScopeKind,
    string ProfileScopeProfileId,
    string GroupKey,
    int? RankInGroup,
    string GeneratedAtUtc,
    string LastWriteUtc,
    string Decision,
    bool Deleted,
    string Reason);

internal sealed record ManagedActionRetentionIssue(
    string Severity,
    string Code,
    string ArtifactPath,
    string Message);
