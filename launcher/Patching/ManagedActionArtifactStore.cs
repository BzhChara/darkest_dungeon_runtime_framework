using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static class ManagedActionArtifactStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static int _sequence;

    public static string Write(
        RuntimeConfig config,
        RuntimeEventRuleSource sourceRule,
        int actionIndex,
        RuntimeRuleAction action,
        JsonObject payload,
        JsonObject plan)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var sequence = Interlocked.Increment(ref _sequence);
        var directory = Path.Combine(config.ModStateDirectory, "_managed_actions");
        Directory.CreateDirectory(directory);

        var fileName =
            $"{generatedAt:yyyyMMdd_HHmmss_fff}_{Environment.ProcessId}_{sequence:D4}_" +
            $"{SanitizeFileName(sourceRule.Rule.On)}_{SanitizeFileName(action.Type)}.json";
        var path = Path.Combine(directory, fileName);

        var artifact = new JsonObject
        {
            ["version"] = 1,
            ["generatedAtUtc"] = generatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["status"] = "materialized",
            ["eventId"] = sourceRule.Rule.On,
            ["pluginId"] = sourceRule.PluginId,
            ["sourceName"] = sourceRule.SourceName,
            ["sourcePath"] = sourceRule.SourcePath,
            ["loadOrder"] = sourceRule.LoadOrder,
            ["ruleIndex"] = sourceRule.RuleIndex,
            ["ruleId"] = sourceRule.Rule.Id,
            ["actionIndex"] = actionIndex,
            ["action"] = new JsonObject
            {
                ["type"] = action.Type,
                ["capability"] = action.Capability,
                ["risk"] = action.Risk,
                ["required"] = action.Required
            },
            ["payload"] = CloneObject(payload),
            ["plan"] = CloneObject(plan)
        };

        File.WriteAllText(path, artifact.ToJsonString(JsonOptions), Encoding.UTF8);
        return path;
    }

    private static JsonObject CloneObject(JsonObject value)
    {
        var clone = JsonNode.Parse(value.ToJsonString());
        return clone as JsonObject
            ?? throw new InvalidOperationException("Expected cloned managed action JSON to be an object.");
    }

    private static string SanitizeFileName(string value)
    {
        var sanitized = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sanitized.Append(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');
        }

        return sanitized.Length == 0 ? "unnamed" : sanitized.ToString();
    }
}
