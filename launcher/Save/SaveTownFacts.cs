namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static SaveStateTownFacts BuildTownFacts(SaveStateFileReport? town)
        {
            if (town is null)
            {
                return EmptyTownFacts(null);
            }

            var scalars = GetDsonScalars(town);
            var paths = town.DsonObjectPaths;
            var buildingIds = ExtractTownBuildingIds(paths, scalars);
            if (buildingIds.Count == 0)
            {
                return EmptyTownFacts(TryGetInt(town, "base_root.version"));
            }

            var activitySlots = BuildTownActivitySlots(town, buildingIds);
            var stores = BuildTownStores(town, buildingIds);
            var storeItems = BuildTownStoreItems(town, stores);
            var recruits = BuildTownRecruits(town, stores);
            var quirkTreatments = BuildTownQuirkTreatments(town, buildingIds);
            var deckHistory = BuildTownDeckHistory(town, stores);
            var buildings = BuildTownBuildings(buildingIds, town, activitySlots, stores, storeItems, recruits);

            return new SaveStateTownFacts(
                TryGetInt(town, "base_root.version"),
                buildings.Count,
                stores.Count,
                storeItems.Count,
                recruits.Count,
                activitySlots.Count,
                activitySlots.Count(slot => slot.HasHero),
                quirkTreatments.Count,
                deckHistory.Count,
                buildings,
                stores,
                storeItems.Take(1000).ToArray(),
                recruits.Take(120).ToArray(),
                activitySlots.Take(500).ToArray(),
                quirkTreatments.Take(500).ToArray(),
                deckHistory.Take(500).ToArray());
        }

        private static SaveStateTownFacts EmptyTownFacts(int? version)
        {
            return new SaveStateTownFacts(
                version,
                0,
                0,
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
                [],
                []);
        }

        private static IReadOnlyList<string> ExtractTownBuildingIds(
            IReadOnlyList<string> paths,
            IReadOnlyList<SaveStateDsonScalar> scalars)
        {
            return MergeAllDirectChildIds(
                ExtractAllDirectChildIds(paths, "base_root.buildings"),
                ExtractAllDirectChildIds(scalars, "base_root.buildings"));
        }

        private static IReadOnlyList<SaveStateTownBuildingFacts> BuildTownBuildings(
            IReadOnlyList<string> buildingIds,
            SaveStateFileReport town,
            IReadOnlyList<SaveStateTownActivitySlotFacts> activitySlots,
            IReadOnlyList<SaveStateTownStoreFacts> stores,
            IReadOnlyList<SaveStateTownStoreItemFacts> storeItems,
            IReadOnlyList<SaveStateTownRecruitFacts> recruits)
        {
            var result = new List<SaveStateTownBuildingFacts>();
            foreach (var buildingId in buildingIds)
            {
                var activityIds = ExtractTownActivityIds(town, buildingId);
                var storeIds = ExtractTownStoreIds(town, buildingId);
                result.Add(new SaveStateTownBuildingFacts(
                    buildingId,
                    activityIds.Count > 0,
                    storeIds.Count > 0,
                    activityIds.Count,
                    storeIds.Count,
                    activitySlots.Count(slot => slot.BuildingId.Equals(buildingId, StringComparison.OrdinalIgnoreCase)),
                    storeItems.Count(item => item.BuildingId.Equals(buildingId, StringComparison.OrdinalIgnoreCase)),
                    recruits.Count(recruit => recruit.BuildingId.Equals(buildingId, StringComparison.OrdinalIgnoreCase)),
                    activityIds,
                    storeIds));
            }

            return result
                .OrderBy(building => building.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IReadOnlyList<SaveStateTownActivitySlotFacts> BuildTownActivitySlots(
            SaveStateFileReport town,
            IReadOnlyList<string> buildingIds)
        {
            var result = new List<SaveStateTownActivitySlotFacts>();
            foreach (var buildingId in buildingIds)
            {
                foreach (var activityId in ExtractTownActivityIds(town, buildingId))
                {
                    var activityPath = $"base_root.buildings.{buildingId}.activities.{activityId}";
                    foreach (var slotId in ExtractNumericDirectChildIds(town, activityPath))
                    {
                        var slotPath = $"{activityPath}.{slotId.ToString(CultureInfo.InvariantCulture)}";
                        var heroId = TryGetInt(town, $"{slotPath}.hero");
                        result.Add(new SaveStateTownActivitySlotFacts(
                            buildingId,
                            activityId,
                            slotId,
                            heroId,
                            TryGetInt(town, $"{slotPath}.visitsRemaining"),
                            TryGetInt(town, $"{slotPath}.resident_occupied"),
                            TryGetBool(town, $"{slotPath}.is_side_effect_result"),
                            heroId.HasValue && heroId.Value > 0));
                    }
                }
            }

            return result
                .OrderBy(slot => slot.BuildingId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(slot => slot.ActivityId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(slot => slot.SlotIndex)
                .ToArray();
        }

        private static IReadOnlyList<SaveStateTownQuirkTreatmentFacts> BuildTownQuirkTreatments(
            SaveStateFileReport town,
            IReadOnlyList<string> buildingIds)
        {
            var result = new List<SaveStateTownQuirkTreatmentFacts>();
            foreach (var buildingId in buildingIds)
            {
                foreach (var activityId in ExtractTownActivityIds(town, buildingId))
                {
                    var treatmentPath = $"base_root.buildings.{buildingId}.activities.{activityId}.quirk_treatment";
                    foreach (var slotId in ExtractNumericDirectChildIds(town, treatmentPath))
                    {
                        var slotPath = $"{treatmentPath}.{slotId.ToString(CultureInfo.InvariantCulture)}";
                        foreach (var bucketId in ExtractAllDirectChildIds(town.DsonObjectPaths, slotPath))
                        {
                            var bucketPath = $"{slotPath}.{bucketId}";
                            result.Add(new SaveStateTownQuirkTreatmentFacts(
                                buildingId,
                                activityId,
                                slotId,
                                bucketId,
                                EmptyToNull(TryGetString(town, $"{bucketPath}.quirk_treatment")),
                                TryGetInt(town, $"{bucketPath}.quirk_treatment_action")));
                        }
                    }
                }
            }

            return result
                .OrderBy(item => item.BuildingId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ActivityId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.SlotIndex)
                .ThenBy(item => item.QuirkBucket, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IReadOnlyList<SaveStateTownStoreFacts> BuildTownStores(
            SaveStateFileReport town,
            IReadOnlyList<string> buildingIds)
        {
            var result = new List<SaveStateTownStoreFacts>();
            foreach (var buildingId in buildingIds)
            {
                foreach (var storeId in ExtractTownStoreIds(town, buildingId))
                {
                    var storePath = $"base_root.buildings.{buildingId}.store.{storeId}";
                    result.Add(new SaveStateTownStoreFacts(
                        buildingId,
                        storeId,
                        MergeAllDirectChildIds(
                            ExtractAllDirectChildIds(town.DsonObjectPaths, $"{storePath}.inventory.items"),
                            ExtractAllDirectChildIds(GetDsonScalars(town), $"{storePath}.inventory.items")).Count,
                        MergeAllDirectChildIds(
                            ExtractAllDirectChildIds(town.DsonObjectPaths, $"{storePath}.generated"),
                            ExtractAllDirectChildIds(GetDsonScalars(town), $"{storePath}.generated")).Count,
                        CountTownDeckHistoryEntries(town, storePath)));
                }
            }

            return result
                .OrderBy(store => store.BuildingId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(store => store.StoreId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IReadOnlyList<SaveStateTownStoreItemFacts> BuildTownStoreItems(
            SaveStateFileReport town,
            IReadOnlyList<SaveStateTownStoreFacts> stores)
        {
            var result = new List<SaveStateTownStoreItemFacts>();
            foreach (var store in stores)
            {
                var itemsPath = $"base_root.buildings.{store.BuildingId}.store.{store.StoreId}.inventory.items";
                foreach (var slotId in MergeAllDirectChildIds(
                                 ExtractAllDirectChildIds(town.DsonObjectPaths, itemsPath),
                                 ExtractAllDirectChildIds(GetDsonScalars(town), itemsPath))
                             .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase))
                {
                    var itemPath = $"{itemsPath}.{slotId}";
                    result.Add(new SaveStateTownStoreItemFacts(
                        store.BuildingId,
                        store.StoreId,
                        slotId,
                        TryGetString(town, $"{itemPath}.id"),
                        TryGetString(town, $"{itemPath}.type"),
                        TryGetInt(town, $"{itemPath}.amount")));
                }
            }

            return result.ToArray();
        }

        private static IReadOnlyList<SaveStateTownRecruitFacts> BuildTownRecruits(
            SaveStateFileReport town,
            IReadOnlyList<SaveStateTownStoreFacts> stores)
        {
            var result = new List<SaveStateTownRecruitFacts>();
            foreach (var store in stores)
            {
                var generatedPath = $"base_root.buildings.{store.BuildingId}.store.{store.StoreId}.generated";
                foreach (var recruitId in MergeAllDirectChildIds(
                                 ExtractAllDirectChildIds(town.DsonObjectPaths, generatedPath),
                                 ExtractAllDirectChildIds(GetDsonScalars(town), generatedPath))
                             .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase))
                {
                    var recruitPath = $"{generatedPath}.{recruitId}";
                    result.Add(new SaveStateTownRecruitFacts(
                        store.BuildingId,
                        store.StoreId,
                        recruitId,
                        TryGetString(town, $"{recruitPath}.actor.name"),
                        TryGetString(town, $"{recruitPath}.heroClass"),
                        TryGetInt(town, $"{recruitPath}.resolveXp"),
                        TryGetDouble(town, $"{recruitPath}.actor.current_hp"),
                        TryGetDouble(town, $"{recruitPath}.m_Stress"),
                        TryGetInt(town, $"{recruitPath}.weapon_rank"),
                        TryGetInt(town, $"{recruitPath}.armour_rank"),
                        TryGetBool(town, $"{recruitPath}.rescued"),
                        TryGetBool(town, $"{recruitPath}.backer_hero"),
                        TryGetBool(town, $"{recruitPath}.is_from_town_event"),
                        MergeDirectChildIds(
                            ExtractDirectChildIds(town.DsonObjectPaths, $"{recruitPath}.quirks"),
                            ExtractDirectChildIds(GetDsonScalars(town), $"{recruitPath}.quirks")),
                        ExtractDirectChildIds(GetDsonScalars(town), $"{recruitPath}.skills.selected_combat_skills"),
                        ExtractDirectChildIds(GetDsonScalars(town), $"{recruitPath}.skills.selected_camping_skills"),
                        MergeDirectChildIds(
                            ExtractDirectChildIds(town.DsonObjectPaths, $"{recruitPath}.trinkets.items"),
                            ExtractDirectChildIds(GetDsonScalars(town), $"{recruitPath}.trinkets.items"))));
                }
            }

            return result.ToArray();
        }

        private static IReadOnlyList<SaveStateTownDeckHistoryFacts> BuildTownDeckHistory(
            SaveStateFileReport town,
            IReadOnlyList<SaveStateTownStoreFacts> stores)
        {
            var result = new List<SaveStateTownDeckHistoryFacts>();
            foreach (var store in stores)
            {
                var storePath = $"base_root.buildings.{store.BuildingId}.store.{store.StoreId}";
                foreach (var deckVersionId in ExtractTownDeckVersionIds(town, storePath))
                {
                    var deckPath = $"{storePath}.{deckVersionId}";
                    foreach (var entryId in MergeAllDirectChildIds(
                                 ExtractAllDirectChildIds(town.DsonObjectPaths, deckPath),
                                 ExtractAllDirectChildIds(GetDsonScalars(town), deckPath)))
                    {
                        result.Add(new SaveStateTownDeckHistoryFacts(
                            store.BuildingId,
                            store.StoreId,
                            deckVersionId,
                            entryId,
                            TryGetInt(town, $"{deckPath}.{entryId}.count")));
                    }
                }
            }

            return result
                .OrderBy(entry => entry.BuildingId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.StoreId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.DeckVersionId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => NumericAwareSortKey(entry.EntryId), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IReadOnlyList<string> ExtractTownActivityIds(SaveStateFileReport town, string buildingId)
        {
            var path = $"base_root.buildings.{buildingId}.activities";
            return MergeAllDirectChildIds(
                ExtractAllDirectChildIds(town.DsonObjectPaths, path),
                ExtractAllDirectChildIds(GetDsonScalars(town), path));
        }

        private static IReadOnlyList<string> ExtractTownStoreIds(SaveStateFileReport town, string buildingId)
        {
            var path = $"base_root.buildings.{buildingId}.store";
            return MergeAllDirectChildIds(
                ExtractAllDirectChildIds(town.DsonObjectPaths, path),
                ExtractAllDirectChildIds(GetDsonScalars(town), path));
        }

        private static IReadOnlyList<int> ExtractNumericDirectChildIds(SaveStateFileReport town, string parentPath)
        {
            return MergeAllDirectChildIds(
                    ExtractAllDirectChildIds(town.DsonObjectPaths, parentPath),
                    ExtractAllDirectChildIds(GetDsonScalars(town), parentPath))
                .Select(TryParseInvariantInt)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .Distinct()
                .OrderBy(value => value)
                .ToArray();
        }

        private static IReadOnlyList<string> ExtractTownDeckVersionIds(SaveStateFileReport town, string storePath)
        {
            return MergeAllDirectChildIds(
                    ExtractAllDirectChildIds(town.DsonObjectPaths, storePath),
                    ExtractAllDirectChildIds(GetDsonScalars(town), storePath))
                .Where(value => value.StartsWith("deck_history_version_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static int CountTownDeckHistoryEntries(SaveStateFileReport town, string storePath)
        {
            return ExtractTownDeckVersionIds(town, storePath)
                .Sum(deckVersionId => MergeAllDirectChildIds(
                    ExtractAllDirectChildIds(town.DsonObjectPaths, $"{storePath}.{deckVersionId}"),
                    ExtractAllDirectChildIds(GetDsonScalars(town), $"{storePath}.{deckVersionId}")).Count);
        }

        private static int? TryParseInvariantInt(string value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static string NumericAwareSortKey(string value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed.ToString("D10", CultureInfo.InvariantCulture)
                : value;
        }

        private static string? EmptyToNull(string? value)
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
