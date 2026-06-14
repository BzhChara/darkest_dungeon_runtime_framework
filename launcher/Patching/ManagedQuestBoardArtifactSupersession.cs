using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static class ManagedQuestBoardArtifactSupersession
{
    public static HashSet<string> FindSupersededArtifacts(
        string artifactDirectory,
        string? targetProfileId,
        LauncherLog log)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(artifactDirectory))
        {
            return result;
        }

        var latestByKey = new Dictionary<string, QuestBoardArtifactCandidate>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<QuestBoardArtifactCandidate>();
        var normalizedTargetProfileId = ManagedActionProfileScopeResolver.NormalizeTargetProfileId(targetProfileId);
        foreach (var path in Directory.EnumerateFiles(artifactDirectory, "*.json")
                     .OrderBy(path => File.GetLastWriteTimeUtc(path))
                     .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var artifact = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject
                    ?? throw new InvalidDataException("artifact root must be a JSON object");
                var actionType = ReadOptionalStringPath(artifact, "action.type");
                if (!actionType.Equals("questBoard.replaceWithFixedSet", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var profileScope = ManagedActionProfileScopeResolver.FromArtifact(artifact);
                if (!profileScope.Matches(normalizedTargetProfileId))
                {
                    continue;
                }

                var candidate = new QuestBoardArtifactCandidate(
                    path,
                    BuildSupersedeKey(artifact, normalizedTargetProfileId, profileScope),
                    File.GetLastWriteTimeUtc(path));
                candidates.Add(candidate);
                latestByKey[candidate.Key] = candidate;
            }
            catch (Exception ex)
            {
                log.Warn($"quest-board-artifact-supersession skipped path={Quote(path)} message={Quote(ex.Message)}");
            }
        }

        foreach (var candidate in candidates)
        {
            if (latestByKey.TryGetValue(candidate.Key, out var latest) &&
                !candidate.Path.Equals(latest.Path, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(candidate.Path);
            }
        }

        return result;
    }

    private static string BuildSupersedeKey(
        JsonObject artifact,
        string targetProfileId,
        ManagedActionProfileScope profileScope)
    {
        var effectiveProfileKind = string.IsNullOrWhiteSpace(targetProfileId)
            ? (profileScope.IsGlobal ? "global" : profileScope.Kind)
            : "profile";
        var effectiveProfileId = string.IsNullOrWhiteSpace(targetProfileId)
            ? profileScope.ProfileId
            : targetProfileId;

        return string.Join('|',
            Normalize(ReadOptionalStringPath(artifact, "action.type")),
            Normalize(ReadOptionalStringPath(artifact, "plan.target")),
            Normalize(ReadOptionalStringPath(artifact, "pluginId")),
            Normalize(ReadOptionalStringPath(artifact, "sourcePath")),
            Normalize(ReadOptionalStringPath(artifact, "ruleId")),
            ReadOptionalIntPath(artifact, "actionIndex")?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Normalize(effectiveProfileKind),
            Normalize(effectiveProfileId));
    }

    private static string ReadOptionalStringPath(JsonObject root, string path)
    {
        return TryReadOptionalPath(root, path, out var node) &&
            node is JsonValue value &&
            value.TryGetValue<string>(out var result)
            ? result
            : string.Empty;
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

    private static string Normalize(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';

    private sealed record QuestBoardArtifactCandidate(string Path, string Key, DateTime LastWriteUtc);
}
