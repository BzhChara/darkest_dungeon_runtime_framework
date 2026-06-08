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

        private static SaveStateCurioTrackerFacts BuildCurioTrackerFacts(
            SaveStateFileReport? curioTracker,
            ContentHashCatalog contentHashCatalog)
        {
            if (curioTracker is null)
            {
                return new SaveStateCurioTrackerFacts(null, 0, []);
            }

            var trackedResults = ExtractSecondaryPersistChildIds(curioTracker, "base_root.tracked_results")
                .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                .Select(slotId =>
                {
                    var path = $"base_root.tracked_results.{slotId}";
                    var propNameHash = TryGetInt(curioTracker, $"{path}.prop_name_id");
                    var itemTypeHash = TryGetInt(curioTracker, $"{path}.item_type_hash");
                    var itemIdHash = TryGetInt(curioTracker, $"{path}.item_id_hash");
                    return new SaveStateCurioTrackedResultFacts(
                        slotId,
                        propNameHash,
                        ResolveHashValue(propNameHash, contentHashCatalog),
                        itemTypeHash,
                        ResolveHashValue(itemTypeHash, contentHashCatalog),
                        itemIdHash,
                        ResolveHashValue(itemIdHash, contentHashCatalog),
                        EmptyToNull(TryGetString(curioTracker, $"{path}.curio_tracker_id")));
                })
                .ToArray();

            return new SaveStateCurioTrackerFacts(
                TryGetInt(curioTracker, "base_root.version"),
                trackedResults.Length,
                trackedResults);
        }

        private static SaveStateLoadingScreenFacts BuildLoadingScreenFacts(
            SaveStateFileReport? loadingScreen,
            ContentHashCatalog contentHashCatalog)
        {
            if (loadingScreen is null)
            {
                return new SaveStateLoadingScreenFacts(null, null, null, null, null, null, null, null, null);
            }

            var titleId = TryGetInt(loadingScreen, "base_root.title_id");
            var tipId = TryGetInt(loadingScreen, "base_root.tip_id");
            var narrationEntryId = TryGetInt(loadingScreen, "base_root.narration_entry_id");

            return new SaveStateLoadingScreenFacts(
                TryGetInt(loadingScreen, "base_root.version"),
                EmptyToNull(TryGetString(loadingScreen, "base_root.background_texture_path")),
                titleId,
                ResolveHashValue(titleId, contentHashCatalog),
                tipId,
                ResolveHashValue(tipId, contentHashCatalog),
                narrationEntryId,
                ResolveHashValue(narrationEntryId, contentHashCatalog),
                BuildSimpleScalarFacts(loadingScreen, "base_root.narration_audio_event_queue_tags", contentHashCatalog, resolveIntValues: true));
        }

        private static SaveStateNoveltyTrackerFacts BuildNoveltyTrackerFacts(SaveStateFileReport? noveltyTracker)
        {
            if (noveltyTracker is null)
            {
                return new SaveStateNoveltyTrackerFacts(null, 0, 0, []);
            }

            var categories = ExtractSecondaryPersistChildIds(noveltyTracker, "base_root.novelty_tracker")
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(categoryId =>
                {
                    var categoryPath = $"base_root.novelty_tracker.{categoryId}";
                    var seenEntryIds = ExtractSecondaryPersistChildIds(noveltyTracker, categoryPath)
                        .Where(entryId => TryGetBool(noveltyTracker, $"{categoryPath}.{entryId}") == true)
                        .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    return new SaveStateNoveltyCategoryFacts(
                        categoryId,
                        seenEntryIds.Length,
                        seenEntryIds);
                })
                .Where(category => category.SeenEntryCount > 0)
                .ToArray();

            return new SaveStateNoveltyTrackerFacts(
                TryGetInt(noveltyTracker, "base_root.version"),
                categories.Length,
                categories.Sum(category => category.SeenEntryCount),
                categories);
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

        private static SaveStateResolvedHashFacts? ResolveHashValue(
            int? value,
            ContentHashCatalog contentHashCatalog)
        {
            if (!value.HasValue)
            {
                return null;
            }

            var names = contentHashCatalog.Resolve(value.Value);
            return new SaveStateResolvedHashFacts(
                value.Value,
                unchecked((uint)value.Value),
                names.Count > 0,
                names.Count > 1,
                names);
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
