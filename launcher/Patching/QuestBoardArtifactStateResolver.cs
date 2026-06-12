using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static class QuestBoardArtifactStateResolver
{
    public static HashSet<string> ResolveCompletedQuestIds(string modStateDirectory, JsonObject artifact)
    {
        var stateKey = ReadString(artifact, "plan.arguments.completedStateKey");
        if (string.IsNullOrWhiteSpace(stateKey))
        {
            throw new InvalidDataException("plan.arguments.completedStateKey is required when removeCompleted is true.");
        }

        var state = LoadArtifactPluginState(modStateDirectory, artifact);
        if (!TryGetPath(state, stateKey, out var completedNode))
        {
            throw new InvalidDataException($"Completed quest state path was not found: {stateKey}");
        }

        return ReadStringArray(completedNode, $"state.{stateKey}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static JsonObject LoadArtifactPluginState(string modStateDirectory, JsonObject artifact)
    {
        var pluginId = ReadString(artifact, "pluginId");
        var sourcePath = ReadString(artifact, "sourcePath");
        if (!Directory.Exists(modStateDirectory))
        {
            throw new DirectoryNotFoundException($"Mod state directory was not found: {modStateDirectory}");
        }

        foreach (var statePath in Directory.EnumerateFiles(modStateDirectory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            JsonObject root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(statePath, Encoding.UTF8)) as JsonObject
                    ?? throw new InvalidDataException("state root must be a JSON object");
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                throw new InvalidDataException($"Failed to read plugin state file {statePath}: {ex.Message}", ex);
            }

            if (!ReadOptionalString(root, "pluginId").Equals(pluginId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var manifestPath = ReadOptionalString(root, "pluginManifestPath");
            if (!string.IsNullOrWhiteSpace(sourcePath) &&
                !manifestPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return root["state"] as JsonObject
                ?? throw new InvalidDataException($"Plugin state file has no root.state object: {statePath}");
        }

        throw new FileNotFoundException($"No sidecar state file matched pluginId={pluginId} sourcePath={sourcePath} in {modStateDirectory}");
    }

    private static IReadOnlyList<string> ReadStringArray(JsonNode? node, string path)
    {
        if (node is not JsonArray array)
        {
            throw new InvalidDataException($"{path} must be a string array.");
        }

        var result = new List<string>();
        foreach (var item in array)
        {
            if (item is not JsonValue value ||
                !value.TryGetValue<string>(out var text) ||
                string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidDataException($"{path} must contain only non-empty strings.");
            }

            result.Add(text);
        }

        return result;
    }

    private static string ReadString(JsonObject root, string path)
    {
        if (TryGetPath(root, path, out var node) &&
            node is JsonValue value &&
            value.TryGetValue<string>(out var text))
        {
            return text;
        }

        throw new InvalidDataException($"{path} must be a string.");
    }

    private static string ReadOptionalString(JsonObject root, string key)
    {
        return root[key]?.GetValue<string>() ?? string.Empty;
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
}
