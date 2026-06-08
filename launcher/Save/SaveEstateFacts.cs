namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static SaveStateEstateFacts BuildEstateFacts(SaveStateFileReport? estate)
        {
            if (estate is null)
            {
                return EmptyEstateFacts(null);
            }

            var walletItems = BuildEstateItemFacts(estate, "base_root.wallet");
            var estateItems = BuildEstateItemFacts(estate, "base_root.estate_items.items");
            var trinketItems = BuildEstateItemFacts(estate, "base_root.trinkets.items");

            return new SaveStateEstateFacts(
                TryGetInt(estate, "base_root.version"),
                walletItems.Count,
                walletItems,
                estateItems.Count,
                estateItems,
                trinketItems.Count,
                trinketItems,
                TryGetInt(estate, "base_root.endless_wave_highscore"),
                TryGetBool(estate, "base_root.was_endless_wave_highscore_tampered"),
                TryGetBool(estate, "base_root.performed_blueprint_correction_check"),
                TryGetBool(estate, "base_root.tampering.tampering_manager.foundGlobalTamperedFile"),
                TryGetBool(estate, "base_root.tampering.tampering_manager.foundLocalTamperedFile"),
                BuildObjectContainerFacts(estate, "base_root.trinkets"),
                BuildObjectContainerFacts(estate, "base_root.trinkets.items"),
                BuildObjectContainerFacts(estate, "base_root.darkest_dungeon_trinket_unlocks"),
                ExtractEstateChildIds(estate, "base_root.trinkets"),
                ExtractEstateChildIds(estate, "base_root.darkest_dungeon_trinket_unlocks"));
        }

        private static SaveStateEstateFacts EmptyEstateFacts(int? version)
        {
            return new SaveStateEstateFacts(
                version,
                0,
                [],
                0,
                [],
                0,
                [],
                null,
                null,
                null,
                null,
                null,
                BuildObjectContainerFacts(null, "base_root.trinkets"),
                BuildObjectContainerFacts(null, "base_root.trinkets.items"),
                BuildObjectContainerFacts(null, "base_root.darkest_dungeon_trinket_unlocks"),
                [],
                []);
        }

        private static IReadOnlyList<SaveStateEstateItemFacts> BuildEstateItemFacts(
            SaveStateFileReport estate,
            string parentPath)
        {
            return ExtractEstateChildIds(estate, parentPath)
                .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                .Select(slotId => new SaveStateEstateItemFacts(
                    slotId,
                    TryGetString(estate, $"{parentPath}.{slotId}.type"),
                    EmptyToNull(TryGetString(estate, $"{parentPath}.{slotId}.id")),
                    TryGetInt(estate, $"{parentPath}.{slotId}.amount")))
                .ToArray();
        }

        private static IReadOnlyList<string> ExtractEstateChildIds(SaveStateFileReport estate, string parentPath)
        {
            return MergeAllDirectChildIds(
                ExtractAllDirectChildIds(estate.DsonObjectPaths, parentPath),
                ExtractAllDirectChildIds(GetDsonScalars(estate), parentPath));
        }
    }
}
