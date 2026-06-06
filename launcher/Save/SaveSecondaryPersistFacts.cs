namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static SaveStateGameKnowledgeFacts BuildGameKnowledgeFacts(SaveStateFileReport? gameKnowledge)
        {
            if (gameKnowledge is null)
            {
                return new SaveStateGameKnowledgeFacts(null, 0, [], null, null);
            }

            var combatSkillIds = ExtractSecondaryPersistChildIds(gameKnowledge, "base_root.combat_skills")
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new SaveStateGameKnowledgeFacts(
                TryGetInt(gameKnowledge, "base_root.version"),
                combatSkillIds.Length,
                combatSkillIds,
                BuildSimpleScalarFacts(gameKnowledge, "base_root.dungeons_unlocked"),
                BuildSimpleScalarFacts(gameKnowledge, "base_root.played_video_list"));
        }

        private static SaveStateJournalFacts BuildJournalFacts(SaveStateFileReport? journal)
        {
            if (journal is null)
            {
                return new SaveStateJournalFacts(null, null, null, null);
            }

            return new SaveStateJournalFacts(
                TryGetInt(journal, "base_root.version"),
                BuildSimpleScalarFacts(journal, "base_root.read_page_indexes"),
                BuildSimpleScalarFacts(journal, "base_root.raid_read_page_indexes"),
                BuildSimpleScalarFacts(journal, "base_root.raid_unread_page_indexes"));
        }

        private static SaveStateTutorialFacts BuildTutorialFacts(SaveStateFileReport? tutorial)
        {
            if (tutorial is null)
            {
                return new SaveStateTutorialFacts(null, null);
            }

            return new SaveStateTutorialFacts(
                TryGetInt(tutorial, "base_root.version"),
                BuildSimpleScalarFacts(tutorial, "base_root.dispatched_events"));
        }

        private static SaveStateCampaignMashFacts BuildCampaignMashFacts(SaveStateFileReport? campaignMash)
        {
            if (campaignMash is null)
            {
                return new SaveStateCampaignMashFacts(null, null, 0, [], 0, []);
            }

            var roamingDungeonToIdKeys = ExtractSecondaryPersistChildIds(campaignMash, "base_root.roaming_dungeon_2_ids")
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var roamingIdToDungeonKeys = ExtractSecondaryPersistChildIds(campaignMash, "base_root.roaming_id_2_dungeon")
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new SaveStateCampaignMashFacts(
                TryGetInt(campaignMash, "base_root.version"),
                BuildSimpleScalarFacts(campaignMash, "base_root.additional_mash_disabled_infestation_monster_class_ids"),
                roamingDungeonToIdKeys.Length,
                roamingDungeonToIdKeys,
                roamingIdToDungeonKeys.Length,
                roamingIdToDungeonKeys);
        }

        private static SaveStateSimpleScalarFacts? BuildSimpleScalarFacts(
            SaveStateFileReport file,
            string path)
        {
            var scalar = FindDsonScalar(file, path);
            if (scalar is null)
            {
                return null;
            }

            return new SaveStateSimpleScalarFacts(
                scalar.Path,
                scalar.Name,
                scalar.Type,
                scalar.Value,
                !string.IsNullOrEmpty(scalar.Value));
        }

        private static IReadOnlyList<string> ExtractSecondaryPersistChildIds(
            SaveStateFileReport file,
            string parentPath)
        {
            return MergeAllDirectChildIds(
                ExtractAllDirectChildIds(file.DsonObjectPaths, parentPath),
                ExtractAllDirectChildIds(GetDsonScalars(file), parentPath));
        }
    }
}
