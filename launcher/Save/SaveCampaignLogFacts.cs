namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static SaveStateCampaignLogFacts BuildCampaignLogFacts(SaveStateFileReport? campaignLog)
        {
            if (campaignLog is null)
            {
                return EmptyCampaignLogFacts(null);
            }

            var chapters = ExtractCampaignLogNumericChildIds(campaignLog, "base_root.chapters")
                .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                .Select(chapterSlotId => BuildCampaignLogChapterFacts(campaignLog, chapterSlotId))
                .ToArray();
            var entries = chapters.SelectMany(chapter => chapter.Entries).ToArray();

            return new SaveStateCampaignLogFacts(
                TryGetInt(campaignLog, "base_root.version"),
                TryGetInt(campaignLog, "base_root.total_weeks"),
                chapters.Length,
                entries.Length,
                entries.Count(entry => entry.EntryKind.Equals("heroRoster", StringComparison.OrdinalIgnoreCase)),
                entries.Count(entry => entry.EntryKind.Equals("party", StringComparison.OrdinalIgnoreCase)),
                entries.Count(entry => entry.EntryKind.Equals("dungeon", StringComparison.OrdinalIgnoreCase)),
                chapters);
        }

        private static SaveStateCampaignLogFacts EmptyCampaignLogFacts(int? version)
        {
            return new SaveStateCampaignLogFacts(version, null, 0, 0, 0, 0, 0, []);
        }

        private static SaveStateCampaignLogChapterFacts BuildCampaignLogChapterFacts(
            SaveStateFileReport campaignLog,
            string chapterSlotId)
        {
            var chapterPath = $"base_root.chapters.{chapterSlotId}";
            var entries = ExtractCampaignLogNumericChildIds(campaignLog, chapterPath)
                .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                .Select(entrySlotId => BuildCampaignLogEntryFacts(campaignLog, chapterPath, entrySlotId))
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
            string entrySlotId)
        {
            var entryPath = $"{chapterPath}.{entrySlotId}";
            var heroes = BuildCampaignLogHeroFacts(campaignLog, $"{entryPath}.heroes");
            var name = EmptyToNull(TryGetString(campaignLog, $"{entryPath}.name"));
            var guid = TryGetInt(campaignLog, $"{entryPath}.guid");
            var classHash = TryGetInt(campaignLog, $"{entryPath}.class");
            var level = TryGetInt(campaignLog, $"{entryPath}.level");
            var dungeonId = TryGetInt(campaignLog, $"{entryPath}.dungeon_id");
            var entryKind = ClassifyCampaignLogEntry(heroes.Count, name, guid, classHash, level, dungeonId);

            return new SaveStateCampaignLogEntryFacts(
                entrySlotId,
                TryGetInt(campaignLog, $"{entryPath}.rtti"),
                entryKind,
                name,
                guid,
                classHash,
                level,
                dungeonId,
                heroes.Count,
                heroes,
                BuildCampaignLogExtraScalarFacts(campaignLog, entryPath));
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
                || localPath.Equals("dungeon_id", StringComparison.OrdinalIgnoreCase))
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
