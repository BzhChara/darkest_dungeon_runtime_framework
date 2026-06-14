using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal enum ManagedActionApplyMode
{
    All,
    ContinuousProfile
}

internal static partial class ManagedActionSaveApplier
{
    private static readonly HashSet<string> ContinuousProfileActionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "stagecoach.suppressRecruits",
        "town.suppressStoreItems",
        "townEvent.overrideCurrent"
    };

    private static IReadOnlyList<string> SelectArtifactPaths(
        string artifactDirectory,
        ManagedActionApplyMode applyMode,
        string? targetProfileId,
        LauncherLog log)
    {
        var allPaths = Directory.EnumerateFiles(artifactDirectory, "*.json")
            .OrderBy(path => File.GetLastWriteTimeUtc(path))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (applyMode == ManagedActionApplyMode.All)
        {
            return allPaths;
        }

        if (applyMode != ManagedActionApplyMode.ContinuousProfile)
        {
            throw new InvalidOperationException($"Unsupported managed action apply mode: {applyMode}");
        }

        var invalidPaths = new List<string>();
        var candidates = new List<ContinuousProfileArtifactCandidate>();
        var profileSkippedCount = 0;
        var normalizedTargetProfileId = ManagedActionProfileScopeResolver.NormalizeTargetProfileId(targetProfileId);
        foreach (var path in allPaths)
        {
            if (!TryReadContinuousProfileCandidate(path, normalizedTargetProfileId, log, out var candidate, out var profileSkipped))
            {
                invalidPaths.Add(path);
                continue;
            }

            if (profileSkipped)
            {
                profileSkippedCount++;
                continue;
            }

            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        var selected = candidates
            .GroupBy(candidate => candidate.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(candidate => candidate.LastWriteUtc)
                .ThenBy(candidate => candidate.ArtifactPath, StringComparer.OrdinalIgnoreCase)
                .Last())
            .OrderBy(candidate => candidate.LastWriteUtc)
            .ThenBy(candidate => candidate.ArtifactPath, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.ArtifactPath)
            .ToArray();

        log.Info(
            $"managed-action-apply continuous-profile selection scanned={allPaths.Length} " +
            $"selected={selected.Length} invalid={invalidPaths.Count} profileSkipped={profileSkippedCount} " +
            $"ignored={allPaths.Length - selected.Length - invalidPaths.Count - profileSkippedCount} " +
            $"targetProfile={Quote(normalizedTargetProfileId)}");

        return invalidPaths
            .OrderBy(path => File.GetLastWriteTimeUtc(path))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Concat(selected)
            .ToArray();
    }

    private static bool TryReadContinuousProfileCandidate(
        string artifactPath,
        string targetProfileId,
        LauncherLog log,
        out ContinuousProfileArtifactCandidate? candidate,
        out bool profileSkipped)
    {
        candidate = null;
        profileSkipped = false;

        JsonObject artifact;
        try
        {
            artifact = JsonNode.Parse(File.ReadAllText(artifactPath, Encoding.UTF8)) as JsonObject
                ?? throw new InvalidDataException("artifact root must be a JSON object");
        }
        catch (Exception ex)
        {
            log.Warn($"managed-action-apply continuous-profile could not inspect artifact path={Quote(artifactPath)} message={Quote(ex.Message)}");
            return false;
        }

        try
        {
            var actionType = ReadOptionalStringPath(artifact, "action.type");
            if (!ContinuousProfileActionTypes.Contains(actionType))
            {
                return true;
            }

            var status = ReadOptionalStringPath(artifact, "status");
            if (!status.Equals("materialized", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var profileScope = ManagedActionProfileScopeResolver.FromArtifact(artifact);
            if (!profileScope.Matches(targetProfileId))
            {
                profileSkipped = true;
                return true;
            }

            var groupKey = BuildContinuousProfileGroupKey(artifact, actionType);
            candidate = new ContinuousProfileArtifactCandidate(
                artifactPath,
                actionType,
                groupKey,
                File.GetLastWriteTimeUtc(artifactPath));
            return true;
        }
        catch (Exception ex)
        {
            log.Warn($"managed-action-apply continuous-profile could not inspect artifact path={Quote(artifactPath)} message={Quote(ex.Message)}");
            return false;
        }
    }

    private static string BuildContinuousProfileGroupKey(JsonObject artifact, string actionType)
    {
        var profileScope = ManagedActionProfileScopeResolver.FromArtifact(artifact);
        return string.Join('|',
            NormalizeGroupPart(actionType),
            NormalizeGroupPart(ReadOptionalStringPath(artifact, "pluginId")),
            NormalizeGroupPart(ReadOptionalStringPath(artifact, "ruleId")),
            ReadOptionalIntPath(artifact, "actionIndex")?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            NormalizeGroupPart(ReadOptionalStringPath(artifact, "plan.target")),
            NormalizeGroupPart(profileScope.Kind),
            NormalizeGroupPart(profileScope.ProfileId),
            NormalizeGroupPart(ReadOptionalStringPath(artifact, "sourcePath")));
    }

    private static string GetApplyModeReportName(ManagedActionApplyMode applyMode)
    {
        return applyMode switch
        {
            ManagedActionApplyMode.All => "all",
            ManagedActionApplyMode.ContinuousProfile => "continuousProfile",
            _ => throw new InvalidOperationException($"Unsupported managed action apply mode: {applyMode}")
        };
    }

    private static int? ReadOptionalIntPath(JsonObject root, string path)
    {
        return TryReadOptionalPath(root, path, out var node) &&
            node is JsonValue value &&
            value.TryGetValue<int>(out var result)
            ? result
            : null;
    }

    private static bool TryReadOptionalPath(JsonObject root, string path, out JsonNode? node)
    {
        node = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (node is not JsonObject obj || !obj.TryGetPropertyValue(part, out node))
            {
                node = null;
                return false;
            }
        }

        return true;
    }

    private static string NormalizeGroupPart(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private sealed record ContinuousProfileArtifactCandidate(
        string ArtifactPath,
        string ActionType,
        string GroupKey,
        DateTime LastWriteUtc);
}
