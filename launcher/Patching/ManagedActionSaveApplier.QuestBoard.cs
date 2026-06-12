using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static partial class ManagedActionSaveApplier
{
    private static void ApplyQuestBoardReplaceWithFixedSet(ApplyContext context, string artifactPath, JsonObject artifact)
    {
        var plan = QuestBoardFixedSetResolver.Resolve(context.GameWorkingDirectory, context.ModStateDirectory, artifact);
        var replacement = QuestBoardFixedSetResolver.BuildQuestEntries(plan.ActiveQuestIds, plan.Definitions);

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

            file.MarkChanged(Math.Max(1, plan.ActiveQuestIds.Count));
        }

        AddSuccessfulAction(
            context,
            artifactPath,
            artifact,
            file.Path,
            [
                $"replace quest board fixedQuestIds={plan.SourceQuestIds.Count} activeQuestIds={plan.ActiveQuestIds.Count} removeCompleted={plan.RemoveCompleted}",
                $"completedFiltered={plan.CompletedFilteredQuestIds.Count} definitions={plan.Definitions.Count}",
                $"quests={string.Join(",", plan.ActiveQuestIds)}"
            ]);
    }

}
