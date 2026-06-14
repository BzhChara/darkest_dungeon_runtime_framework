using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static partial class ManagedActionOverlayCompiler
{
    private static JsonObject BuildAvailabilityPolicy(
        string artifactPath,
        JsonObject artifact,
        string itemKind,
        string unavailableIdsArgument)
    {
        var actionType = ReadString(artifact, "action.type");
        var plan = RequireObject(artifact, "plan");
        var arguments = RequireObject(plan, "arguments");
        var filterId = RequireString(arguments, "filterId");
        var unavailableIds = RequireStringArray(arguments, unavailableIdsArgument);

        return new JsonObject
        {
            ["kind"] = actionType,
            ["effect"] = ReadString(plan, "effect"),
            ["target"] = ReadString(plan, "target"),
            ["artifactPath"] = artifactPath,
            ["eventId"] = ReadString(artifact, "eventId"),
            ["pluginId"] = ReadString(artifact, "pluginId"),
            ["sourceName"] = ReadString(artifact, "sourceName"),
            ["sourcePath"] = ReadString(artifact, "sourcePath"),
            ["ruleIndex"] = ReadInt(artifact, "ruleIndex"),
            ["ruleId"] = ReadString(artifact, "ruleId"),
            ["actionIndex"] = ReadInt(artifact, "actionIndex"),
            ["itemKind"] = itemKind,
            ["filterId"] = filterId,
            ["unavailableCount"] = unavailableIds.Count,
            ["unavailableIds"] = ToJsonArray(unavailableIds),
            ["consumerStatus"] = "manifestOnly",
            ["liveEnforced"] = false
        };
    }

    private static string BuildAvailabilityPolicySupersedeKey(JsonObject policy)
    {
        return string.Join('|',
            ReadString(policy, "kind"),
            ReadString(policy, "target"),
            ReadString(policy, "filterId"),
            ReadString(policy, "pluginId"),
            ReadString(policy, "sourcePath"),
            ReadString(policy, "ruleId"),
            ReadInt(policy, "actionIndex").ToString(CultureInfo.InvariantCulture));
    }

    private static IReadOnlyList<string> RequireStringArray(JsonObject root, string path)
    {
        if (!TryGetPath(root, path, out var node) || node is not JsonArray array)
        {
            throw new InvalidOperationException($"Expected array at '{path}'.");
        }

        var values = new List<string>();
        foreach (var item in array)
        {
            if (item is not JsonValue value ||
                !value.TryGetValue<string>(out var text) ||
                string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException($"Expected non-empty string items at '{path}'.");
            }

            if (!values.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                values.Add(text);
            }
        }

        return values;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }
}
