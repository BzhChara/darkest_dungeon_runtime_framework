namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static SaveStateQuestFacts BuildQuestFacts(SaveStateFileReport? quest)
        {
            if (quest is null)
            {
                return EmptyQuestFacts(null);
            }

            var questIds = MergeAllDirectChildIds(
                ExtractAllDirectChildIds(quest.DsonObjectPaths, "base_root.quests"),
                ExtractAllDirectChildIds(GetDsonScalars(quest), "base_root.quests"));
            if (questIds.Count == 0)
            {
                return EmptyQuestFacts(TryGetInt(quest, "base_root.version"));
            }

            var quests = questIds
                .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                .Select(slotId => BuildQuestEntryFacts(quest, slotId))
                .ToArray();

            return new SaveStateQuestFacts(
                TryGetInt(quest, "base_root.version"),
                TryGetInt(quest, "base_root.plot_quest_total"),
                TryGetIntVector(quest, "base_root.trinket_retention_ids").Count,
                TryGetIntVector(quest, "base_root.trinket_retention_ids"),
                quests.Length,
                quests.Count(item => item.IsPlotQuest == true),
                quests.Count(item => item.IsFromTownEvent == true),
                quests);
        }

        private static SaveStateQuestFacts EmptyQuestFacts(int? version)
        {
            return new SaveStateQuestFacts(version, null, 0, [], 0, 0, 0, []);
        }

        private static SaveStateQuestEntryFacts BuildQuestEntryFacts(SaveStateFileReport quest, string slotId)
        {
            var path = $"base_root.quests.{slotId}";
            var goalIds = TryGetStringVector(quest, $"{path}.goal_ids");
            return new SaveStateQuestEntryFacts(
                slotId,
                TryGetString(quest, $"{path}.id"),
                TryGetString(quest, $"{path}.dungeon"),
                TryGetString(quest, $"{path}.type"),
                TryGetString(quest, $"{path}.map_name"),
                TryGetInt(quest, $"{path}.difficulty"),
                TryGetInt(quest, $"{path}.length"),
                TryGetBool(quest, $"{path}.is_plot_quest"),
                TryGetBool(quest, $"{path}.counted_in_generation"),
                TryGetBool(quest, $"{path}.is_from_town_event"),
                TryGetInt(quest, $"{path}.completion_threshold"),
                TryGetBool(quest, $"{path}.use_default_progression_goals"),
                EmptyToNull(TryGetString(quest, $"{path}.raid_rules_override")),
                EmptyToNull(TryGetString(quest, $"{path}.torch_setting")),
                goalIds.Count,
                goalIds,
                BuildSimpleScalarFacts(quest, $"{path}.progression_goal_ids"),
                BuildQuestRewardFacts(quest, $"{path}.completion_reward"));
        }

        private static SaveStateQuestRewardFacts BuildQuestRewardFacts(SaveStateFileReport quest, string rewardPath)
        {
            var itemPath = $"{rewardPath}.items_definition.items";
            var itemIds = MergeAllDirectChildIds(
                ExtractAllDirectChildIds(quest.DsonObjectPaths, itemPath),
                ExtractAllDirectChildIds(GetDsonScalars(quest), itemPath));

            return new SaveStateQuestRewardFacts(
                TryGetInt(quest, $"{rewardPath}.resolve_xp"),
                TryGetInt(quest, $"{rewardPath}.resolve_xp_per_wave_kill"),
                TryGetInt(quest, $"{rewardPath}.max_times_dungeon_xp_awarded"),
                TryGetIntVector(quest, $"{rewardPath}.trinket_retention_ids").Count,
                TryGetIntVector(quest, $"{rewardPath}.trinket_retention_ids"),
                itemIds
                    .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                    .Select(slotId =>
                    {
                        var path = $"{itemPath}.{slotId}";
                        return new SaveStateQuestRewardItemFacts(
                            slotId,
                            TryGetString(quest, $"{path}.type"),
                            EmptyToNull(TryGetString(quest, $"{path}.id")),
                            TryGetInt(quest, $"{path}.amount"));
                    })
                    .ToArray());
        }
    }
}
