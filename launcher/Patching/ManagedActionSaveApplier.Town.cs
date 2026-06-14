using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static partial class ManagedActionSaveApplier
{
    private static void ApplyStagecoachSuppressRecruits(ApplyContext context, string artifactPath, JsonObject artifact)
    {
        var mode = ReadString(artifact, "plan.arguments.mode");
        if (!mode.Equals("suppressed", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("empty", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsupported stagecoach recruit suppression mode: {mode}");
        }

        if (mode.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            AddSuccessfulAction(
                context,
                artifactPath,
                artifact,
                Path.Combine(context.SaveDirectory, "persist.town.json"),
                [
                    "stagecoach recruit suppression mode=none",
                    "no decoded save changes requested"
                ]);
            return;
        }

        var file = context.LoadDecodedJsonFile("persist.town.json");
        var stagecoach = RequireObject(file.Root, "base_root.buildings.stage_coach");
        var stores = RequireObject(stagecoach, "store");
        var storeCount = 0;
        var clearedStoreCount = 0;
        var removedRecruitCount = 0;

        foreach (var storePair in stores.ToArray())
        {
            if (storePair.Value is not JsonObject store)
            {
                continue;
            }

            storeCount++;
            if (store["generated"] is not JsonObject generated)
            {
                continue;
            }

            var recruitCount = generated.Count;
            if (recruitCount == 0)
            {
                continue;
            }

            if (context.WriteChanges)
            {
                store["generated"] = new JsonObject();
            }

            clearedStoreCount++;
            removedRecruitCount += recruitCount;
        }

        if (removedRecruitCount > 0)
        {
            file.MarkChanged(removedRecruitCount);
        }

        AddSuccessfulAction(
            context,
            artifactPath,
            artifact,
            file.Path,
            [
                $"suppress stagecoach recruits mode={mode} stores={storeCount}",
                $"clearedStores={clearedStoreCount} removedRecruits={removedRecruitCount}"
            ]);
    }

    private static void ApplyTownUnlockAllBuildings(ApplyContext context, string artifactPath, JsonObject artifact)
    {
        var mode = ReadString(artifact, "plan.arguments.mode");
        if (!mode.Equals("all_unlocked", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("all_unlocked_and_maxed", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("districts_built", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsupported town building unlock mode: {mode}");
        }

        var file = context.LoadDecodedJsonFile("persist.town.json");
        var districtBuildings = TryGetObject(file.Root, "base_root.districts.buildings");
        var districtCount = 0;
        var updatedDistrictCount = 0;
        var unchangedDistrictCount = 0;
        var skippedDistrictCount = 0;

        if (districtBuildings is not null)
        {
            foreach (var districtPair in districtBuildings)
            {
                if (districtPair.Value is not JsonObject district)
                {
                    skippedDistrictCount++;
                    continue;
                }

                districtCount++;
                var built = ReadOptionalBool(district, "built");
                if (built == true)
                {
                    unchangedDistrictCount++;
                    continue;
                }

                if (context.WriteChanges)
                {
                    district["built"] = true;
                }

                updatedDistrictCount++;
            }
        }

        if (updatedDistrictCount > 0)
        {
            file.MarkChanged(updatedDistrictCount);
        }

        AddSuccessfulAction(
            context,
            artifactPath,
            artifact,
            file.Path,
            [
                $"unlock town buildings mode={mode}",
                $"districts updated={updatedDistrictCount} unchanged={unchangedDistrictCount} skipped={skippedDistrictCount} total={districtCount}",
                "ordinary building upgrade levels are represented by upgrade.ensurePurchases; persist.town has no verified direct level scalar"
            ]);
    }

    private static void ApplyTownSuppressStoreItems(ApplyContext context, string artifactPath, JsonObject artifact)
    {
        var mode = ReadString(artifact, "plan.arguments.mode");
        if (!mode.Equals("empty", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("suppressed", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsupported town store suppression mode: {mode}");
        }

        if (mode.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            AddSuccessfulAction(
                context,
                artifactPath,
                artifact,
                Path.Combine(context.SaveDirectory, "persist.town.json"),
                [
                    "town store suppression mode=none",
                    "no decoded save changes requested"
                ]);
            return;
        }

        var requestedBuildingIds = ReadOptionalStringArrayPath(artifact, "plan.arguments.buildingIds")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requestedStoreIds = ReadOptionalStringArrayPath(artifact, "plan.arguments.storeIds")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requestedSections = ReadOptionalStringArrayPath(artifact, "plan.arguments.sections");
        var clearGenerated = requestedSections.Count == 0 ||
            requestedSections.Contains("generated", StringComparer.OrdinalIgnoreCase);
        var clearInventoryItems = requestedSections.Count == 0 ||
            requestedSections.Contains("inventory.items", StringComparer.OrdinalIgnoreCase);
        if (!clearGenerated && !clearInventoryItems)
        {
            throw new InvalidDataException("town.suppressStoreItems requires sections to include generated and/or inventory.items.");
        }

        var file = context.LoadDecodedJsonFile("persist.town.json");
        var buildings = RequireObject(file.Root, "base_root.buildings");
        var scannedStores = 0;
        var clearedStores = 0;
        var removedGeneratedCount = 0;
        var removedInventoryItemCount = 0;

        foreach (var buildingPair in buildings.ToArray())
        {
            if (requestedBuildingIds.Count > 0 && !requestedBuildingIds.Contains(buildingPair.Key))
            {
                continue;
            }

            if (buildingPair.Value is not JsonObject building ||
                building["store"] is not JsonObject stores)
            {
                continue;
            }

            foreach (var storePair in stores.ToArray())
            {
                if (requestedStoreIds.Count > 0 && !requestedStoreIds.Contains(storePair.Key))
                {
                    continue;
                }

                if (storePair.Value is not JsonObject store)
                {
                    continue;
                }

                scannedStores++;
                var storeChanged = false;
                if (clearGenerated && store["generated"] is JsonObject generated && generated.Count > 0)
                {
                    removedGeneratedCount += generated.Count;
                    storeChanged = true;
                    if (context.WriteChanges)
                    {
                        store["generated"] = new JsonObject();
                    }
                }

                if (clearInventoryItems &&
                    TryGetObject(store, "inventory.items") is JsonObject inventoryItems &&
                    inventoryItems.Count > 0)
                {
                    removedInventoryItemCount += inventoryItems.Count;
                    storeChanged = true;
                    if (context.WriteChanges)
                    {
                        EnsureObject(store, "inventory")["items"] = new JsonObject();
                    }
                }

                if (storeChanged)
                {
                    clearedStores++;
                }
            }
        }

        var removedCount = removedGeneratedCount + removedInventoryItemCount;
        if (removedCount > 0)
        {
            file.MarkChanged(removedCount);
        }

        AddSuccessfulAction(
            context,
            artifactPath,
            artifact,
            file.Path,
            [
                $"suppress town store items mode={mode} stores={scannedStores} clearedStores={clearedStores}",
                $"removedGenerated={removedGeneratedCount} removedInventoryItems={removedInventoryItemCount}",
                $"buildingFilter={(requestedBuildingIds.Count == 0 ? "<all>" : string.Join(',', requestedBuildingIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)))}",
                $"storeFilter={(requestedStoreIds.Count == 0 ? "<all>" : string.Join(',', requestedStoreIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)))}"
            ]);
    }
}
