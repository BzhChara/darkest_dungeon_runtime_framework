using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static partial class ManagedActionSaveApplier
{
    private static void ApplyQuestBoardReplaceWithFixedSet(ApplyContext context, string artifactPath, JsonObject artifact)
    {
        var questIds = ReadStringArray(ReadNode(artifact, "plan.arguments.questIds"), "plan.arguments.questIds");
        if (questIds.Count == 0)
        {
            throw new InvalidDataException("plan.arguments.questIds must contain at least one quest id.");
        }

        var removeCompleted = ReadOptionalBool(RequireObject(artifact, "plan.arguments"), "removeCompleted") == true;
        var completedQuestIds = removeCompleted
            ? ResolveCompletedQuestIds(context, artifact)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeQuestIds = questIds
            .Where(id => !completedQuestIds.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var definitions = QuestBoardContentCatalog.LoadEnabledPlotQuestDefinitions(context.GameWorkingDirectory);
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

        var replacement = new JsonObject();
        for (var i = 0; i < activeQuestIds.Length; i++)
        {
            replacement[i.ToString(CultureInfo.InvariantCulture)] =
                QuestBoardContentCatalog.BuildQuestBoardEntry(definitions[activeQuestIds[i]]);
        }

        var file = context.LoadDecodedJsonFile("persist.quest.json");
        var baseRoot = EnsureObject(file.Root, "base_root");
        var existingQuests = baseRoot["quests"] as JsonObject ?? new JsonObject();
        var changed = !JsonNode.DeepEquals(existingQuests, replacement);
        if (changed)
        {
            if (context.WriteChanges)
            {
                baseRoot["quests"] = replacement;
            }

            file.MarkChanged(Math.Max(1, activeQuestIds.Length));
        }

        AddSuccessfulAction(
            context,
            artifactPath,
            artifact,
            file.Path,
            [
                $"replace quest board fixedQuestIds={questIds.Count} activeQuestIds={activeQuestIds.Length} removeCompleted={removeCompleted}",
                $"completedFiltered={questIds.Count - activeQuestIds.Length} definitions={definitions.Count}",
                $"quests={string.Join(",", activeQuestIds)}"
            ]);
    }

    private static HashSet<string> ResolveCompletedQuestIds(ApplyContext context, JsonObject artifact)
    {
        return QuestBoardArtifactStateResolver.ResolveCompletedQuestIds(context.ModStateDirectory, artifact);
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
}
