namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static SaveStateProgressionFacts BuildProgressionFacts(
            SaveStateFileReport? progression,
            ContentHashCatalog contentHashCatalog)
        {
            if (progression is null)
            {
                return EmptyProgressionFacts(null);
            }

            var dungeons = BuildProgressionDungeonFacts(progression);
            var achievements = BuildProgressionAchievementFacts(progression, "base_root.achievements");
            var realAchievements = BuildProgressionAchievementFacts(progression, "base_root.real_achievements");
            var completedPlotQuestData = BuildProgressionCompletedPlotQuestFacts(progression);
            var flashbackCompletions = BuildProgressionFlashbackCompletionFacts(progression);

            return new SaveStateProgressionFacts(
                TryGetInt(progression, "base_root.version"),
                TryGetInt(progression, "base_root.total_quests_finished"),
                TryGetInt(progression, "base_root.total_successful_quests_finished"),
                TryGetInt(progression, "base_root.total_recruited_stage_coach_heroes"),
                TryGetInt(progression, "base_root.last_quest_played_id"),
                ResolveHashValue(TryGetInt(progression, "base_root.last_quest_played_id"), contentHashCatalog),
                TryGetBool(progression, "base_root.last_quest_played_successfully"),
                TryGetInt(progression, "base_root.last_quest_played_xp"),
                TryGetInt(progression, "base_root.last_raid_quest_id"),
                ResolveHashValue(TryGetInt(progression, "base_root.last_raid_quest_id"), contentHashCatalog),
                TryGetBool(progression, "base_root.last_raid_success"),
                TryGetBool(progression, "base_root.last_raid_was_a_plot_quest"),
                new SaveStateProgressionInfestationFacts(
                    TryGetInt(progression, "base_root.infestation.sequence_element_id"),
                    TryGetInt(progression, "base_root.infestation.rng_seed"),
                    TryGetInt(progression, "base_root.infestation.number_of_weeks_left_in_sequence_element"),
                    TryGetInt(progression, "base_root.infestation.number_of_weeks_total_in_sequence_element")),
                dungeons.Count,
                dungeons,
                achievements.Count,
                achievements.Count(achievement => achievement.Completed == true),
                achievements.Count(achievement => achievement.Awarded == true),
                achievements,
                realAchievements.Count,
                realAchievements.Count(achievement => achievement.Completed == true),
                realAchievements.Count(achievement => achievement.Awarded == true),
                realAchievements,
                BuildObjectContainerFacts(progression, "base_root.completed_plot_quests_data"),
                BuildObjectContainerFacts(progression, "base_root.flashback_completion_counts"),
                completedPlotQuestData.Count,
                completedPlotQuestData,
                flashbackCompletions.Count,
                flashbackCompletions,
                ExtractProgressionChildIds(progression, "base_root.completed_plot_quests_data"),
                ExtractProgressionChildIds(progression, "base_root.flashback_completion_counts"));
        }

        private static SaveStateProgressionFacts EmptyProgressionFacts(int? version)
        {
            return new SaveStateProgressionFacts(
                version,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                new SaveStateProgressionInfestationFacts(null, null, null, null),
                0,
                [],
                0,
                0,
                0,
                [],
                0,
                0,
                0,
                [],
                BuildObjectContainerFacts(null, "base_root.completed_plot_quests_data"),
                BuildObjectContainerFacts(null, "base_root.flashback_completion_counts"),
                0,
                [],
                0,
                [],
                [],
                []);
        }

        private static IReadOnlyList<SaveStateProgressionDungeonFacts> BuildProgressionDungeonFacts(
            SaveStateFileReport progression)
        {
            return ExtractProgressionChildIds(progression, "base_root.dungeon")
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(dungeonId => new SaveStateProgressionDungeonFacts(
                    dungeonId,
                    TryGetInt(progression, $"base_root.dungeon.{dungeonId}.xp")))
                .ToArray();
        }

        private static IReadOnlyList<SaveStateProgressionCompletedPlotQuestFacts> BuildProgressionCompletedPlotQuestFacts(
            SaveStateFileReport progression)
        {
            const string parentPath = "base_root.completed_plot_quests_data";
            return ExtractProgressionChildIds(progression, parentPath)
                .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                .Select(slotId =>
                {
                    var questPath = $"{parentPath}.{slotId}";
                    var heroes = ExtractProgressionChildIds(progression, $"{questPath}.heroes")
                        .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                        .Select(heroSlotId => new SaveStateProgressionCompletedPlotQuestHeroFacts(
                            heroSlotId,
                            TryGetInt(progression, $"{questPath}.heroes.{heroSlotId}.guid"),
                            TryGetBool(progression, $"{questPath}.heroes.{heroSlotId}.survived"),
                            TryGetBool(progression, $"{questPath}.heroes.{heroSlotId}.last_blow")))
                        .ToArray();

                    return new SaveStateProgressionCompletedPlotQuestFacts(
                        slotId,
                        TryGetInt(progression, $"{questPath}.plot_quest_id"),
                        heroes.Length,
                        heroes);
                })
                .ToArray();
        }

        private static IReadOnlyList<SaveStateProgressionFlashbackCompletionFacts> BuildProgressionFlashbackCompletionFacts(
            SaveStateFileReport progression)
        {
            const string parentPath = "base_root.flashback_completion_counts";
            return ExtractProgressionChildIds(progression, parentPath)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(flashbackId => new SaveStateProgressionFlashbackCompletionFacts(
                    flashbackId,
                    TryGetInt(progression, $"{parentPath}.{flashbackId}")))
                .ToArray();
        }

        private static IReadOnlyList<SaveStateProgressionAchievementFacts> BuildProgressionAchievementFacts(
            SaveStateFileReport progression,
            string parentPath)
        {
            return ExtractProgressionChildIds(progression, parentPath)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(achievementKey => BuildProgressionAchievementFacts(progression, parentPath, achievementKey))
                .ToArray();
        }

        private static SaveStateProgressionAchievementFacts BuildProgressionAchievementFacts(
            SaveStateFileReport progression,
            string parentPath,
            string achievementKey)
        {
            var achievementPath = $"{parentPath}.{achievementKey}";
            var conditions = ExtractProgressionChildIds(progression, $"{achievementPath}.conditions")
                .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                .Select(slotId => new SaveStateProgressionAchievementConditionFacts(
                    slotId,
                    TryGetInt(progression, $"{achievementPath}.conditions.{slotId}.enemies_killed")))
                .ToArray();

            return new SaveStateProgressionAchievementFacts(
                achievementKey,
                TryGetString(progression, $"{achievementPath}.id"),
                TryGetInt(progression, $"{achievementPath}.rtti"),
                TryGetBool(progression, $"{achievementPath}.completed"),
                TryGetBool(progression, $"{achievementPath}.awarded"),
                conditions,
                BuildProgressionAchievementExtraScalarFacts(progression, achievementPath));
        }

        private static IReadOnlyList<SaveStateProgressionAchievementScalarFacts> BuildProgressionAchievementExtraScalarFacts(
            SaveStateFileReport progression,
            string achievementPath)
        {
            var prefix = achievementPath + ".";
            return GetDsonScalars(progression)
                .Where(scalar => scalar.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(scalar => !IsStandardAchievementScalarPath(scalar.Path[prefix.Length..]))
                .OrderBy(scalar => scalar.Path, StringComparer.OrdinalIgnoreCase)
                .Select(scalar => new SaveStateProgressionAchievementScalarFacts(
                    scalar.Path[prefix.Length..],
                    scalar.Name,
                    scalar.Type,
                    scalar.Value))
                .ToArray();
        }

        private static bool IsStandardAchievementScalarPath(string localPath)
        {
            if (localPath.Equals("id", StringComparison.OrdinalIgnoreCase)
                || localPath.Equals("rtti", StringComparison.OrdinalIgnoreCase)
                || localPath.Equals("completed", StringComparison.OrdinalIgnoreCase)
                || localPath.Equals("awarded", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return localPath.StartsWith("conditions.", StringComparison.OrdinalIgnoreCase)
                && localPath.EndsWith(".enemies_killed", StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<string> ExtractProgressionChildIds(SaveStateFileReport progression, string parentPath)
        {
            return MergeAllDirectChildIds(
                ExtractAllDirectChildIds(progression.DsonObjectPaths, parentPath),
                ExtractAllDirectChildIds(GetDsonScalars(progression), parentPath));
        }
    }
}
