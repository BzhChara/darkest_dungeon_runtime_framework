namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static SaveStateNarrationFacts BuildNarrationFacts(SaveStateFileReport? narration)
        {
            if (narration is null)
            {
                return EmptyNarrationFacts(null);
            }

            var logs = new[]
            {
                BuildNarrationLogFacts(narration, "campaign", "base_root.campaign_entry_log"),
                BuildNarrationLogFacts(narration, "raid", "base_root.raid_entry_log"),
                BuildNarrationLogFacts(narration, "townVisit", "base_root.town_visit_entry_log")
            };
            var entries = logs.SelectMany(log => log.Entries).ToArray();
            var entryTypeCounts = BuildNarrationSummaryFacts(entries, entry => entry.EntryType);
            var audioEventTypeCounts = BuildNarrationSummaryFacts(entries, entry => entry.AudioEventType);

            return new SaveStateNarrationFacts(
                TryGetInt(narration, "base_root.version"),
                logs.Length,
                entries.Length,
                logs.First(log => log.LogId.Equals("campaign", StringComparison.OrdinalIgnoreCase)).EntryCount,
                logs.First(log => log.LogId.Equals("raid", StringComparison.OrdinalIgnoreCase)).EntryCount,
                logs.First(log => log.LogId.Equals("townVisit", StringComparison.OrdinalIgnoreCase)).EntryCount,
                entries.Sum(entry => entry.Count ?? 0),
                entryTypeCounts.Count,
                audioEventTypeCounts.Count,
                logs,
                entryTypeCounts,
                audioEventTypeCounts);
        }

        private static SaveStateNarrationFacts EmptyNarrationFacts(int? version)
        {
            return new SaveStateNarrationFacts(version, 0, 0, 0, 0, 0, 0, 0, 0, [], [], []);
        }

        private static SaveStateNarrationLogFacts BuildNarrationLogFacts(
            SaveStateFileReport narration,
            string logId,
            string sourcePath)
        {
            var entryIds = ExtractNarrationChildIds(narration, sourcePath);
            var entries = entryIds
                .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                .Select(slotId => BuildNarrationEntryFacts(narration, logId, sourcePath, slotId))
                .ToArray();

            return new SaveStateNarrationLogFacts(
                logId,
                sourcePath,
                entries.Length,
                entries.Sum(entry => entry.Count ?? 0),
                entries);
        }

        private static SaveStateNarrationEntryFacts BuildNarrationEntryFacts(
            SaveStateFileReport narration,
            string logId,
            string sourcePath,
            string slotId)
        {
            var entryPath = $"{sourcePath}.{slotId}";
            return new SaveStateNarrationEntryFacts(
                logId,
                slotId,
                EmptyToNull(TryGetString(narration, $"{entryPath}.entry_type")),
                EmptyToNull(TryGetString(narration, $"{entryPath}.audio_event_type")),
                TryGetInt(narration, $"{entryPath}.count"));
        }

        private static IReadOnlyList<SaveStateNarrationSummaryFacts> BuildNarrationSummaryFacts(
            IReadOnlyList<SaveStateNarrationEntryFacts> entries,
            Func<SaveStateNarrationEntryFacts, string?> keySelector)
        {
            return entries
                .Where(entry => !string.IsNullOrWhiteSpace(keySelector(entry)))
                .GroupBy(entry => keySelector(entry)!, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new SaveStateNarrationSummaryFacts(
                    group.Key,
                    group.Count(),
                    group.Sum(entry => entry.Count ?? 0)))
                .ToArray();
        }

        private static IReadOnlyList<string> ExtractNarrationChildIds(
            SaveStateFileReport narration,
            string parentPath)
        {
            return MergeAllDirectChildIds(
                ExtractAllDirectChildIds(narration.DsonObjectPaths, parentPath),
                ExtractAllDirectChildIds(GetDsonScalars(narration), parentPath));
        }
    }
}
