namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static SaveStateCampaignFacts BuildCampaignFacts(SaveStateFileReport? game)
        {
            if (game is null)
            {
                return EmptyCampaignFacts(null);
            }

            var dlcs = BuildCampaignDlcFacts(game, "base_root.dlc");
            var presentedDlcs = BuildCampaignDlcFacts(game, "base_root.presented_dlc.dlc");
            var profileOptions = BuildCampaignProfileOptionFacts(game);

            return new SaveStateCampaignFacts(
                TryGetInt(game, "base_root.version"),
                TryGetDouble(game, "base_root.totalelapsed"),
                TryGetBool(game, "base_root.inraid"),
                TryGetString(game, "base_root.raiddungeon"),
                EmptyToNull(TryGetString(game, "base_root.raid_save")),
                TryGetString(game, "base_root.estatename"),
                TryGetString(game, "base_root.game_mode"),
                TryGetString(game, "base_root.date_time"),
                TryGetBool(game, "base_root.dlc_init"),
                TryGetBool(game, "base_root.dd_options_altered"),
                TryGetString(game, "base_root.profile_options.values.town_events"),
                TryGetString(game, "base_root.profile_options.values.never_again"),
                dlcs.Count,
                dlcs,
                presentedDlcs.Count,
                presentedDlcs,
                profileOptions.Count,
                profileOptions,
                ExtractCampaignChildIds(game, "base_root.persistent_ugcs"));
        }

        private static SaveStateCampaignFacts EmptyCampaignFacts(int? version)
        {
            return new SaveStateCampaignFacts(
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
                0,
                [],
                0,
                [],
                0,
                [],
                []);
        }

        private static IReadOnlyList<SaveStateCampaignDlcFacts> BuildCampaignDlcFacts(
            SaveStateFileReport game,
            string parentPath)
        {
            return ExtractCampaignChildIds(game, parentPath)
                .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                .Select(slotId => new SaveStateCampaignDlcFacts(
                    slotId,
                    TryGetString(game, $"{parentPath}.{slotId}.name"),
                    TryGetString(game, $"{parentPath}.{slotId}.source")))
                .ToArray();
        }

        private static IReadOnlyList<SaveStateCampaignProfileOptionFacts> BuildCampaignProfileOptionFacts(
            SaveStateFileReport game)
        {
            const string parentPath = "base_root.profile_options.values";
            var scalars = GetDsonScalars(game)
                .Where(scalar => scalar.Path.StartsWith(parentPath + ".", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            return scalars
                .Select(scalar => new SaveStateCampaignProfileOptionFacts(
                    scalar.Path[(parentPath.Length + 1)..],
                    scalar.Type,
                    scalar.Value))
                .OrderBy(option => option.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IReadOnlyList<string> ExtractCampaignChildIds(SaveStateFileReport game, string parentPath)
        {
            return MergeAllDirectChildIds(
                ExtractAllDirectChildIds(game.DsonObjectPaths, parentPath),
                ExtractAllDirectChildIds(GetDsonScalars(game), parentPath));
        }
    }
}
