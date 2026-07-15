using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static class QuestBoardFixedSetResolver
{
    public static QuestBoardFixedSetPlan Resolve(string gameWorkingDirectory, string modStateDirectory, JsonObject artifact)
    {
        var shape = ReadArtifactShape(artifact, QuestBoardQuestIdSetRequirement.NonEmpty);
        var completedQuestIds = shape.RemoveCompleted
            ? QuestBoardArtifactStateResolver.ResolveCompletedQuestIds(modStateDirectory, artifact)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distinctQuestIds = shape.QuestIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var activeQuestIds = distinctQuestIds
            .Where(id => !completedQuestIds.Contains(id))
            .ToArray();
        var completedFilteredQuestIds = distinctQuestIds
            .Where(id => completedQuestIds.Contains(id))
            .ToArray();

        var definitions = QuestBoardContentCatalog.LoadEnabledPlotQuestDefinitions(gameWorkingDirectory);
        if (definitions.Count == 0)
        {
            throw new InvalidDataException("Plot quest definition catalog produced no quest ids.");
        }

        var missingQuestIds = activeQuestIds
            .Where(id => !definitions.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingQuestIds.Length > 0)
        {
            throw new InvalidDataException($"Fixed quest board references unknown plot quest ids: {string.Join(",", missingQuestIds)}");
        }

        return new QuestBoardFixedSetPlan(
            shape.QuestIds,
            distinctQuestIds,
            activeQuestIds,
            completedFilteredQuestIds,
            shape.RemoveCompleted,
            definitions);
    }

    public static QuestBoardFixedSetArtifactShape ReadArtifactShape(
        JsonObject artifact,
        QuestBoardQuestIdSetRequirement questIdSetRequirement)
    {
        var arguments = RequireObject(artifact, "plan.arguments");
        var questIds = ReadStringArray(arguments["questIds"], "plan.arguments.questIds");
        if (questIdSetRequirement == QuestBoardQuestIdSetRequirement.NonEmpty && questIds.Count == 0)
        {
            throw new InvalidDataException("plan.arguments.questIds must contain at least one quest id.");
        }

        if (questIdSetRequirement == QuestBoardQuestIdSetRequirement.Empty && questIds.Count != 0)
        {
            throw new InvalidDataException("plan.arguments.questIds must be empty for an empty quest-board marker.");
        }

        var removeCompleted = ReadOptionalBool(arguments, "removeCompleted");
        if (removeCompleted && string.IsNullOrWhiteSpace(ReadOptionalString(arguments, "completedStateKey")))
        {
            throw new InvalidDataException("plan.arguments.completedStateKey is required when removeCompleted is true.");
        }

        return new QuestBoardFixedSetArtifactShape(questIds, removeCompleted);
    }

    public static JsonObject BuildQuestEntries(
        IReadOnlyList<string> activeQuestIds,
        IReadOnlyDictionary<string, PlotQuestDefinition> definitions)
    {
        var replacement = new JsonObject();
        for (var i = 0; i < activeQuestIds.Count; i++)
        {
            var questId = activeQuestIds[i];
            if (!definitions.TryGetValue(questId, out var definition))
            {
                throw new InvalidDataException($"Fixed quest board references unknown plot quest id: {questId}");
            }

            replacement[i.ToString(CultureInfo.InvariantCulture)] =
                QuestBoardContentCatalog.BuildQuestBoardEntry(definition);
        }

        return replacement;
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

    private static JsonObject RequireObject(JsonObject root, string path)
    {
        return ReadNode(root, path) as JsonObject
            ?? throw new InvalidDataException($"{path} must be a JSON object.");
    }

    private static JsonNode? ReadNode(JsonObject root, string path)
    {
        if (TryGetPath(root, path, out var node) && node is not null)
        {
            return node;
        }

        throw new InvalidDataException($"{path} is missing.");
    }

    private static bool ReadOptionalBool(JsonObject root, string key)
    {
        if (!root.TryGetPropertyValue(key, out var node) || node is null)
        {
            return false;
        }

        if (node is JsonValue value && value.TryGetValue<bool>(out var result))
        {
            return result;
        }

        throw new InvalidDataException($"plan.arguments.{key} must be a boolean when present.");
    }

    private static string ReadOptionalString(JsonObject root, string key)
    {
        return root[key] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : string.Empty;
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

internal sealed record QuestBoardFixedSetArtifactShape(
    IReadOnlyList<string> QuestIds,
    bool RemoveCompleted);

internal enum QuestBoardQuestIdSetRequirement
{
    NonEmpty,
    Empty
}

internal sealed record QuestBoardFixedSetPlan(
    IReadOnlyList<string> SourceQuestIds,
    IReadOnlyList<string> DistinctQuestIds,
    IReadOnlyList<string> ActiveQuestIds,
    IReadOnlyList<string> CompletedFilteredQuestIds,
    bool RemoveCompleted,
    IReadOnlyDictionary<string, PlotQuestDefinition> Definitions);
