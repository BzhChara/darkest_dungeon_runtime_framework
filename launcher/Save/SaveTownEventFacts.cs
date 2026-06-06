namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static SaveStateTownEventFacts BuildTownEventFacts(SaveStateFileReport? townEvent)
        {
            if (townEvent is null)
            {
                return EmptyTownEventFacts(null);
            }

            var resultEventHistoryIds = ExtractTownEventChildIds(townEvent, "base_root.result_event_history");
            var deadHeroEntryIds = ExtractTownEventChildIds(townEvent, "base_root.dead_hero_entries");
            var bonusHeroEntryIds = ExtractTownEventChildIds(townEvent, "base_root.bonus_hero_entries");
            var eventCostIds = ExtractTownEventChildIds(townEvent, "base_root.event_cost");
            var freeUpgradeTags = ExtractTownEventChildIds(townEvent, "base_root.free_upgrade_tags");
            var nonRolledAdditionalChanceIds = ExtractTownEventChildIds(townEvent, "base_root.non_rolled_additional_chances");

            return new SaveStateTownEventFacts(
                TryGetInt(townEvent, "base_root.version"),
                TryGetInt(townEvent, "base_root.current_result_event_id"),
                TryGetBool(townEvent, "base_root.has_unclaimed_interaction"),
                TryGetInt(townEvent, "base_root.last_town_event_week"),
                TryGetInt(townEvent, "base_root.rng_seed"),
                resultEventHistoryIds.Count,
                deadHeroEntryIds.Count,
                bonusHeroEntryIds.Count,
                eventCostIds.Count,
                freeUpgradeTags.Count,
                nonRolledAdditionalChanceIds.Count,
                resultEventHistoryIds,
                deadHeroEntryIds,
                bonusHeroEntryIds,
                eventCostIds,
                freeUpgradeTags,
                nonRolledAdditionalChanceIds);
        }

        private static SaveStateTownEventFacts EmptyTownEventFacts(int? version)
        {
            return new SaveStateTownEventFacts(
                version,
                null,
                null,
                null,
                null,
                0,
                0,
                0,
                0,
                0,
                0,
                [],
                [],
                [],
                [],
                [],
                []);
        }

        private static IReadOnlyList<string> ExtractTownEventChildIds(SaveStateFileReport townEvent, string parentPath)
        {
            return MergeAllDirectChildIds(
                ExtractAllDirectChildIds(townEvent.DsonObjectPaths, parentPath),
                ExtractAllDirectChildIds(GetDsonScalars(townEvent), parentPath));
        }
    }
}
