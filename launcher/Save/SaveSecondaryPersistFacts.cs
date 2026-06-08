namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static SaveStateGameKnowledgeFacts BuildGameKnowledgeFacts(
            SaveStateFileReport? gameKnowledge,
            ContentHashCatalog contentHashCatalog)
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
                BuildSimpleScalarFacts(gameKnowledge, "base_root.dungeons_unlocked", contentHashCatalog, resolveIntValues: true),
                BuildSimpleScalarFacts(gameKnowledge, "base_root.played_video_list", contentHashCatalog, resolveIntValues: true));
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

        private static SaveStateTutorialFacts BuildTutorialFacts(
            SaveStateFileReport? tutorial,
            ContentHashCatalog contentHashCatalog)
        {
            if (tutorial is null)
            {
                return new SaveStateTutorialFacts(null, null);
            }

            return new SaveStateTutorialFacts(
                TryGetInt(tutorial, "base_root.version"),
                BuildSimpleScalarFacts(tutorial, "base_root.dispatched_events", contentHashCatalog, resolveIntValues: true));
        }

        private static SaveStateCampaignMashFacts BuildCampaignMashFacts(
            SaveStateFileReport? campaignMash,
            ContentHashCatalog contentHashCatalog)
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
                BuildSimpleScalarFacts(campaignMash, "base_root.additional_mash_disabled_infestation_monster_class_ids", contentHashCatalog, resolveIntValues: true),
                roamingDungeonToIdKeys.Length,
                roamingDungeonToIdKeys,
                roamingIdToDungeonKeys.Length,
                roamingIdToDungeonKeys);
        }

        private static SaveStateSimpleScalarFacts? BuildSimpleScalarFacts(
            SaveStateFileReport file,
            string path,
            ContentHashCatalog? contentHashCatalog = null,
            bool resolveIntValues = false)
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
                !string.IsNullOrEmpty(scalar.Value),
                CountSimpleScalarItems(scalar),
                ParseIntVector(scalar),
                resolveIntValues && contentHashCatalog is not null
                    ? ResolveIntVectorValues(ParseIntVector(scalar), contentHashCatalog)
                    : [],
                ParseStringVector(scalar));
        }

        private static IReadOnlyList<SaveStateResolvedHashFacts> ResolveIntVectorValues(
            IReadOnlyList<int> values,
            ContentHashCatalog contentHashCatalog)
        {
            return values
                .Select(value =>
                {
                    var names = contentHashCatalog.Resolve(value);
                    return new SaveStateResolvedHashFacts(
                        value,
                        unchecked((uint)value),
                        names.Count > 0,
                        names.Count > 1,
                        names);
                })
                .ToArray();
        }

        private static IReadOnlyList<int> TryGetIntVector(SaveStateFileReport? file, string path)
        {
            var scalar = FindDsonScalar(file, path);
            return ParseIntVector(scalar);
        }

        private static IReadOnlyList<string> TryGetStringVector(SaveStateFileReport? file, string path)
        {
            var scalar = FindDsonScalar(file, path);
            return ParseStringVector(scalar);
        }

        private static IReadOnlyList<int> TryGetIntVector(IReadOnlyList<SaveStateDsonScalar> scalars, string path)
        {
            var scalar = FindDsonScalar(scalars, path);
            return ParseIntVector(scalar);
        }

        private static IReadOnlyList<string> TryGetStringVector(IReadOnlyList<SaveStateDsonScalar> scalars, string path)
        {
            var scalar = FindDsonScalar(scalars, path);
            return ParseStringVector(scalar);
        }

        private static IReadOnlyList<double> TryGetFloatArray(SaveStateFileReport? file, string path)
        {
            var scalar = FindDsonScalar(file, path);
            return ParseFloatArray(scalar);
        }

        private static IReadOnlyList<double> TryGetFloatArray(IReadOnlyList<SaveStateDsonScalar> scalars, string path)
        {
            var scalar = FindDsonScalar(scalars, path);
            return ParseFloatArray(scalar);
        }

        private static int CountSimpleScalarItems(SaveStateDsonScalar scalar)
        {
            if (scalar.Type.Equals("intVector", StringComparison.OrdinalIgnoreCase))
            {
                return ParseIntVector(scalar).Count;
            }

            if (scalar.Type.Equals("stringVector", StringComparison.OrdinalIgnoreCase))
            {
                return ParseStringVector(scalar).Count;
            }

            return 0;
        }

        private static IReadOnlyList<double> ParseFloatArray(SaveStateDsonScalar? scalar)
        {
            if (scalar is null
                || !scalar.Type.Equals("floatArray", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(scalar.Value))
            {
                return [];
            }

            try
            {
                return JsonSerializer.Deserialize<double[]>(scalar.Value) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        private static IReadOnlyList<int> ParseIntVector(SaveStateDsonScalar? scalar)
        {
            if (scalar is null
                || !scalar.Type.Equals("intVector", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(scalar.Value))
            {
                return [];
            }

            try
            {
                return JsonSerializer.Deserialize<int[]>(scalar.Value) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        private static IReadOnlyList<string> ParseStringVector(SaveStateDsonScalar? scalar)
        {
            if (scalar is null
                || !scalar.Type.Equals("stringVector", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(scalar.Value))
            {
                return [];
            }

            try
            {
                return JsonSerializer.Deserialize<string[]>(scalar.Value) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
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
