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
}
