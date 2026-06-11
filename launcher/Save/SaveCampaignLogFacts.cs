namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static SaveStateCampaignLogFacts BuildCampaignLogFacts(
            SaveStateFileReport? campaignLog,
            ContentHashCatalog contentHashCatalog)
        {
            if (campaignLog is null)
            {
                return EmptyCampaignLogFacts(null);
            }

            var chapters = ExtractCampaignLogNumericChildIds(campaignLog, "base_root.chapters")
                .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                .Select(chapterSlotId => BuildCampaignLogChapterFacts(campaignLog, chapterSlotId, contentHashCatalog))
                .ToArray();
            var entries = chapters.SelectMany(chapter => chapter.Entries).ToArray();
            var partyRaidRecords = chapters
                .SelectMany(chapter => chapter.Entries
                    .Where(IsPartyRaidRecord)
                    .Select(entry => BuildCampaignLogPartyRaidRecordFacts(chapter, entry)))
                .OrderBy(record => record.ChapterIndex ?? int.MinValue)
                .ThenBy(record => NumericAwareSortKey(record.ChapterSlotId), StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => NumericAwareSortKey(record.EntrySlotId), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var completedPartyRaidRecords = partyRaidRecords
                .Where(record => record.Start == false && record.Success == true)
                .ToArray();

            return new SaveStateCampaignLogFacts(
                TryGetInt(campaignLog, "base_root.version"),
                TryGetInt(campaignLog, "base_root.total_weeks"),
                chapters.Length,
                entries.Length,
                entries.Count(entry => entry.EntryKind.Equals("heroRoster", StringComparison.OrdinalIgnoreCase)),
                entries.Count(entry => entry.EntryKind.Equals("party", StringComparison.OrdinalIgnoreCase)),
                entries.Count(entry => entry.EntryKind.Equals("dungeon", StringComparison.OrdinalIgnoreCase)),
                partyRaidRecords.Length,
                completedPartyRaidRecords.Length,
                completedPartyRaidRecords.LastOrDefault(),
                partyRaidRecords,
                chapters);
        }

        private static SaveStateCampaignLogFacts EmptyCampaignLogFacts(int? version)
        {
            return new SaveStateCampaignLogFacts(version, null, 0, 0, 0, 0, 0, 0, 0, null, [], []);
        }

        private static SaveStateCampaignLogChapterFacts BuildCampaignLogChapterFacts(
            SaveStateFileReport campaignLog,
            string chapterSlotId,
            ContentHashCatalog contentHashCatalog)
        {
            var chapterPath = $"base_root.chapters.{chapterSlotId}";
            var entries = ExtractCampaignLogNumericChildIds(campaignLog, chapterPath)
                .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                .Select(entrySlotId => BuildCampaignLogEntryFacts(campaignLog, chapterPath, entrySlotId, contentHashCatalog))
                .ToArray();

            return new SaveStateCampaignLogChapterFacts(
                chapterSlotId,
                TryGetInt(campaignLog, $"{chapterPath}.chapterIndex"),
                entries.Length,
                entries);
        }

        private static SaveStateCampaignLogEntryFacts BuildCampaignLogEntryFacts(
            SaveStateFileReport campaignLog,
            string chapterPath,
            string entrySlotId,
            ContentHashCatalog contentHashCatalog)
        {
            var entryPath = $"{chapterPath}.{entrySlotId}";
            var heroes = BuildCampaignLogHeroFacts(campaignLog, $"{entryPath}.heroes");
            var heroGuids = heroes
                .Select(hero => hero.Guid)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToArray();
            var name = EmptyToNull(TryGetString(campaignLog, $"{entryPath}.name"));
            var guid = TryGetInt(campaignLog, $"{entryPath}.guid");
            var classHash = TryGetInt(campaignLog, $"{entryPath}.class");
            var level = TryGetInt(campaignLog, $"{entryPath}.level");
            var dungeonId = TryGetInt(campaignLog, $"{entryPath}.dungeon_id");
            var entryKind = ClassifyCampaignLogEntry(heroes.Count, name, guid, classHash, level, dungeonId);
            var questHash = TryGetInt(campaignLog, $"{entryPath}.quest");
            var questIdHash = TryGetInt(campaignLog, $"{entryPath}.quest_id");
            var dungeonTypeHash = TryGetInt(campaignLog, $"{entryPath}.dungeon_type");

            return new SaveStateCampaignLogEntryFacts(
                entrySlotId,
                TryGetInt(campaignLog, $"{entryPath}.rtti"),
                entryKind,
                name,
                guid,
                classHash,
                level,
                dungeonId,
                questHash,
                ResolveHashValue(questHash, contentHashCatalog),
                questIdHash,
                ResolveHashValue(questIdHash, contentHashCatalog),
                dungeonTypeHash,
                ResolveHashValue(dungeonTypeHash, contentHashCatalog),
                TryGetInt(campaignLog, $"{entryPath}.difficulty"),
                TryGetInt(campaignLog, $"{entryPath}.length"),
                TryGetInt(campaignLog, $"{entryPath}.score"),
                TryGetBool(campaignLog, $"{entryPath}.start"),
                TryGetBool(campaignLog, $"{entryPath}.success"),
                TryGetBool(campaignLog, $"{entryPath}.is_wave"),
                TryGetBool(campaignLog, $"{entryPath}.is_highscore"),
                TryGetBool(campaignLog, $"{entryPath}.endless_wave"),
                heroes.Count,
                heroGuids,
                heroes,
                BuildCampaignLogExtraScalarFacts(campaignLog, entryPath));
        }

        private static bool IsPartyRaidRecord(SaveStateCampaignLogEntryFacts entry)
        {
            return entry.EntryKind.Equals("party", StringComparison.OrdinalIgnoreCase)
                && entry.HeroCount > 0
                && (entry.QuestIdHash.HasValue
                    || entry.QuestHash.HasValue
                    || entry.DungeonTypeHash.HasValue
                    || entry.Start.HasValue
                    || entry.Success.HasValue);
        }

        private static SaveStateCampaignLogPartyRaidRecordFacts BuildCampaignLogPartyRaidRecordFacts(
            SaveStateCampaignLogChapterFacts chapter,
            SaveStateCampaignLogEntryFacts entry)
        {
            return new SaveStateCampaignLogPartyRaidRecordFacts(
                chapter.ChapterSlotId,
                chapter.ChapterIndex,
                entry.SlotId,
                entry.Rtti,
                entry.QuestHash,
                entry.Quest,
                entry.QuestIdHash,
                entry.QuestId,
                entry.DungeonTypeHash,
                entry.DungeonType,
                entry.Difficulty,
                entry.Length,
                entry.Score,
                entry.Start,
                entry.Success,
                entry.IsWave,
                entry.IsHighscore,
                entry.EndlessWave,
                entry.HeroCount,
                entry.HeroGuids,
                entry.Heroes);
        }

        private static IReadOnlyList<SaveStateCampaignLogHeroFacts> BuildCampaignLogHeroFacts(
            SaveStateFileReport campaignLog,
            string heroesPath)
        {
            return ExtractCampaignLogNumericChildIds(campaignLog, heroesPath)
                .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                .Select(slotId =>
                {
                    var heroPath = $"{heroesPath}.{slotId}";
                    return new SaveStateCampaignLogHeroFacts(
                        slotId,
                        EmptyToNull(TryGetString(campaignLog, $"{heroPath}.name")),
                        TryGetInt(campaignLog, $"{heroPath}.guid"),
                        TryGetInt(campaignLog, $"{heroPath}.class"),
                        TryGetBool(campaignLog, $"{heroPath}.died"));
                })
                .ToArray();
        }

        private static string ClassifyCampaignLogEntry(
            int heroCount,
            string? name,
            int? guid,
            int? classHash,
            int? level,
            int? dungeonId)
        {
            if (heroCount > 0)
            {
                return "party";
            }

            if (dungeonId.HasValue)
            {
                return "dungeon";
            }

            if (!string.IsNullOrWhiteSpace(name)
                || guid.HasValue
                || classHash.HasValue
                || level.HasValue)
            {
                return "heroRoster";
            }

            return "unknown";
        }

        private static IReadOnlyList<SaveStateCampaignLogScalarFacts> BuildCampaignLogExtraScalarFacts(
            SaveStateFileReport campaignLog,
            string entryPath)
        {
            var prefix = entryPath + ".";
            return GetDsonScalars(campaignLog)
                .Where(scalar => scalar.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(scalar => !IsStandardCampaignLogEntryScalarPath(scalar.Path[prefix.Length..]))
                .OrderBy(scalar => scalar.Path, StringComparer.OrdinalIgnoreCase)
                .Select(scalar => new SaveStateCampaignLogScalarFacts(
                    scalar.Path[prefix.Length..],
                    scalar.Name,
                    scalar.Type,
                    scalar.Value))
                .ToArray();
        }

        private static bool IsStandardCampaignLogEntryScalarPath(string localPath)
        {
            if (localPath.Equals("rtti", StringComparison.OrdinalIgnoreCase)
                || localPath.Equals("name", StringComparison.OrdinalIgnoreCase)
                || localPath.Equals("guid", StringComparison.OrdinalIgnoreCase)
                || localPath.Equals("class", StringComparison.OrdinalIgnoreCase)
                || localPath.Equals("level", StringComparison.OrdinalIgnoreCase)
                || localPath.Equals("dungeon_id", StringComparison.OrdinalIgnoreCase)
                || localPath.Equals("quest", StringComparison.OrdinalIgnoreCase)
                || localPath.Equals("quest_id", StringComparison.OrdinalIgnoreCase)
                || localPath.Equals("dungeon_type", StringComparison.OrdinalIgnoreCase)
                || localPath.Equals("difficulty", StringComparison.OrdinalIgnoreCase)
                || localPath.Equals("length", StringComparison.OrdinalIgnoreCase)
                || localPath.Equals("score", StringComparison.OrdinalIgnoreCase)
                || localPath.Equals("start", StringComparison.OrdinalIgnoreCase)
                || localPath.Equals("success", StringComparison.OrdinalIgnoreCase)
                || localPath.Equals("is_wave", StringComparison.OrdinalIgnoreCase)
                || localPath.Equals("is_highscore", StringComparison.OrdinalIgnoreCase)
                || localPath.Equals("endless_wave", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!localPath.StartsWith("heroes.", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return localPath.EndsWith(".name", StringComparison.OrdinalIgnoreCase)
                || localPath.EndsWith(".guid", StringComparison.OrdinalIgnoreCase)
                || localPath.EndsWith(".class", StringComparison.OrdinalIgnoreCase)
                || localPath.EndsWith(".died", StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<string> ExtractCampaignLogNumericChildIds(
            SaveStateFileReport campaignLog,
            string parentPath)
        {
            return MergeAllDirectChildIds(
                    ExtractAllDirectChildIds(campaignLog.DsonObjectPaths, parentPath),
                    ExtractAllDirectChildIds(GetDsonScalars(campaignLog), parentPath))
                .Where(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                .ToArray();
        }
    }
}
