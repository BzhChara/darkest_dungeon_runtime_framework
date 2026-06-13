using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static partial class ManagedActionSaveApplier
{
    private const string DarkestDungeonId = "darkestdungeon";
    private const string HeroDarkestDungeonSuccessCountKey = "number_of_successful_darkest_dungeon_quests";
    private const string HeroDarkestDungeonTestSurvivedKey = "dd_test_survived";

    private static void ApplyCampaignResetPlotProgress(ApplyContext context, string artifactPath, JsonObject artifact)
    {
        var plotQuestIds = ReadStringArray(ReadNode(artifact, "plan.arguments.plotQuestIds"), "plan.arguments.plotQuestIds");
        if (plotQuestIds.Count == 0)
        {
            throw new InvalidDataException("plan.arguments.plotQuestIds must contain at least one plot quest id.");
        }

        var clearHeroDarkestDungeonProgress = ReadBool(
            ReadNode(artifact, "plan.arguments.clearHeroDarkestDungeonProgress"),
            "plan.arguments.clearHeroDarkestDungeonProgress");

        var plotQuestHashes = plotQuestIds
            .Select(id => DsonHash.HashNameSigned(id))
            .ToHashSet();

        var progressionFile = context.LoadDecodedJsonFile("persist.progression.json");
        var baseRoot = EnsureObject(progressionFile.Root, "base_root");
        var completedPlotData = EnsureObject(progressionFile.Root, "base_root.completed_plot_quests_data");

        var removedCompletedPlotRecords = RemoveCompletedPlotQuestData(
            completedPlotData,
            plotQuestHashes,
            context.WriteChanges);
        var resetAchievements = ResetPlotQuestAchievements(
            baseRoot["achievements"] as JsonObject,
            plotQuestIds,
            context.WriteChanges);
        var resetLastQuestReferences = ResetLastQuestReferences(baseRoot, plotQuestHashes, context.WriteChanges);
        var changedProgressionProperties = removedCompletedPlotRecords + resetAchievements + resetLastQuestReferences;
        if (changedProgressionProperties > 0)
        {
            progressionFile.MarkChanged(changedProgressionProperties);
        }

        var resetHeroProperties = 0;
        var resetHeroHistoryEntries = 0;
        if (clearHeroDarkestDungeonProgress)
        {
            var rosterFile = context.LoadDecodedJsonFile("persist.roster.json");
            var heroes = EnsureObject(rosterFile.Root, "base_root.heroes");
            var heroResult = ResetHeroDarkestDungeonProgress(heroes, context.WriteChanges);
            resetHeroProperties = heroResult.ResetPropertyCount;
            resetHeroHistoryEntries = heroResult.RemovedHistoryEntryCount;
            if (heroResult.ChangedCount > 0)
            {
                rosterFile.MarkChanged(heroResult.ChangedCount);
            }
        }

        AddSuccessfulAction(
            context,
            artifactPath,
            artifact,
            progressionFile.Path,
            [
                $"reset plot progress questIds={string.Join(",", plotQuestIds)}",
                $"removedCompletedPlotRecords={removedCompletedPlotRecords} resetAchievements={resetAchievements} resetLastQuestReferences={resetLastQuestReferences}",
                $"clearHeroDarkestDungeonProgress={clearHeroDarkestDungeonProgress} resetHeroProperties={resetHeroProperties} removedHeroDungeonHistoryEntries={resetHeroHistoryEntries}"
            ]);
    }

    private static int RemoveCompletedPlotQuestData(JsonObject completedPlotData, HashSet<int> plotQuestHashes, bool writeChanges)
    {
        var entries = completedPlotData
            .Select(pair => new
            {
                pair.Key,
                Value = pair.Value as JsonObject,
                ShouldRemove = pair.Value is JsonObject value
                    && ReadOptionalInt(value, "plot_quest_id") is { } plotQuestHash
                    && plotQuestHashes.Contains(plotQuestHash)
            })
            .ToArray();
        var removed = entries.Count(entry => entry.ShouldRemove);
        if (removed == 0)
        {
            return 0;
        }

        if (writeChanges)
        {
            var remaining = entries
                .Where(entry => !entry.ShouldRemove && entry.Value is not null)
                .OrderBy(entry => ParseNumericObjectKey(entry.Key) is null ? 1 : 0)
                .ThenBy(entry => ParseNumericObjectKey(entry.Key) ?? 0)
                .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(entry => CloneJsonNode(entry.Value)!)
                .ToArray();

            completedPlotData.Clear();
            for (var i = 0; i < remaining.Length; i++)
            {
                completedPlotData[i.ToString(CultureInfo.InvariantCulture)] = remaining[i];
            }
        }

        return removed;
    }

    private static int ResetPlotQuestAchievements(JsonObject? achievements, IReadOnlyList<string> plotQuestIds, bool writeChanges)
    {
        if (achievements is null)
        {
            return 0;
        }

        var changed = 0;
        foreach (var questId in plotQuestIds)
        {
            if (achievements[questId] is not JsonObject achievement)
            {
                continue;
            }

            if (SetBoolPropertyIfChanged(achievement, "completed", false, writeChanges))
            {
                changed++;
            }

            if (SetBoolPropertyIfChanged(achievement, "awarded", false, writeChanges))
            {
                changed++;
            }
        }

        return changed;
    }

    private static int ResetLastQuestReferences(JsonObject baseRoot, HashSet<int> plotQuestHashes, bool writeChanges)
    {
        var changed = 0;
        if (ReadOptionalInt(baseRoot, "last_quest_played_id") is { } lastQuestId &&
            plotQuestHashes.Contains(lastQuestId))
        {
            if (SetIntPropertyIfChanged(baseRoot, "last_quest_played_id", 0, writeChanges))
            {
                changed++;
            }

            if (SetBoolPropertyIfChanged(baseRoot, "last_quest_played_successfully", false, writeChanges))
            {
                changed++;
            }

            if (SetIntPropertyIfChanged(baseRoot, "last_quest_played_xp", 0, writeChanges))
            {
                changed++;
            }
        }

        if (ReadOptionalInt(baseRoot, "last_raid_quest_id") is { } lastRaidQuestId &&
            plotQuestHashes.Contains(lastRaidQuestId))
        {
            if (SetIntPropertyIfChanged(baseRoot, "last_raid_quest_id", 0, writeChanges))
            {
                changed++;
            }

            if (SetBoolPropertyIfChanged(baseRoot, "last_raid_success", false, writeChanges))
            {
                changed++;
            }

            if (SetBoolPropertyIfChanged(baseRoot, "last_raid_was_a_plot_quest", false, writeChanges))
            {
                changed++;
            }
        }

        return changed;
    }

    private static CampaignHeroDarkestDungeonResetResult ResetHeroDarkestDungeonProgress(JsonObject heroes, bool writeChanges)
    {
        var darkestDungeonHash = DsonHash.HashNameSigned(DarkestDungeonId);
        var resetProperties = 0;
        var removedHistoryEntries = 0;
        var changedHeroes = 0;

        foreach (var hero in EnumerateRosterHeroes(heroes))
        {
            var heroChanged = false;
            if (SetIntPropertyIfChanged(hero.HeroRoot, HeroDarkestDungeonSuccessCountKey, 0, writeChanges))
            {
                resetProperties++;
                heroChanged = true;
            }

            if (SetIntPropertyIfChanged(hero.HeroRoot, HeroDarkestDungeonTestSurvivedKey, 0, writeChanges))
            {
                resetProperties++;
                heroChanged = true;
            }

            if (hero.HeroRoot["dungeon_history"] is JsonArray dungeonHistory)
            {
                var removed = RemoveIntValuesFromArray(dungeonHistory, darkestDungeonHash, writeChanges);
                if (removed > 0)
                {
                    removedHistoryEntries += removed;
                    heroChanged = true;
                }
            }

            if (heroChanged)
            {
                changedHeroes++;
            }
        }

        return new CampaignHeroDarkestDungeonResetResult(changedHeroes, resetProperties, removedHistoryEntries);
    }

    private static int RemoveIntValuesFromArray(JsonArray array, int value, bool writeChanges)
    {
        var indexes = array
            .Select((node, index) => new
            {
                Index = index,
                Matches = node is JsonValue jsonValue &&
                    jsonValue.TryGetValue<int>(out var currentValue) &&
                    currentValue == value
            })
            .Where(item => item.Matches)
            .Select(item => item.Index)
            .ToArray();
        if (writeChanges)
        {
            foreach (var index in indexes.Reverse())
            {
                array.RemoveAt(index);
            }
        }

        return indexes.Length;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonNode? node, string path)
    {
        if (node is JsonArray array)
        {
            return array
                .Select((item, index) => item?.GetValue<string>()
                    ?? throw new InvalidDataException($"{path}[{index}] must be a string."))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
        {
            return [text.Trim()];
        }

        throw new InvalidDataException($"{path} must be a string or an array of strings.");
    }

    private static bool SetBoolPropertyIfChanged(JsonObject root, string key, bool value, bool writeChanges)
    {
        if (root[key] is JsonValue current &&
            current.TryGetValue<bool>(out var currentValue) &&
            currentValue == value)
        {
            return false;
        }

        if (writeChanges)
        {
            root[key] = value;
        }

        return true;
    }

    private static int? ParseNumericObjectKey(string key)
    {
        return int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private sealed record CampaignHeroDarkestDungeonResetResult(
        int ChangedHeroCount,
        int ResetPropertyCount,
        int RemovedHistoryEntryCount)
    {
        public int ChangedCount => ResetPropertyCount + RemovedHistoryEntryCount;
    }
}
