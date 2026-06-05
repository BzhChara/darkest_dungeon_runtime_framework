namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static IReadOnlyList<SaveStateHeroFacts> ExtractHeroFactsFromRoster(
            string fileName,
            byte[] bytes,
            BinaryContainerInfo? container,
            List<string> accessIssues)
        {
            if (!fileName.Equals("persist.roster.json", StringComparison.OrdinalIgnoreCase) || container is null)
            {
                return [];
            }

            var heroes = new List<SaveStateHeroFacts>();
            foreach (var scalar in container.DsonScalars
                         .Where(scalar => scalar.Path.EndsWith(".hero_file_data.raw_data", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(scalar => scalar.Path, StringComparer.OrdinalIgnoreCase))
            {
                var heroId = ExtractHeroIdFromRawDataPath(scalar.Path);
                if (string.IsNullOrWhiteSpace(heroId))
                {
                    continue;
                }

                var nameLength = Encoding.UTF8.GetByteCount(scalar.Name) + 1;
                var valueStart = Align4(scalar.Offset + nameLength);
                var fieldEnd = scalar.Offset + scalar.Size;
                if (fieldEnd > bytes.Length || valueStart + 4 > fieldEnd)
                {
                    accessIssues.Add($"{fileName}: hero raw_data has no length prefix path={scalar.Path}");
                    continue;
                }

                var nestedLength = ReadInt32LittleEndian(bytes, valueStart);
                var nestedOffset = valueStart + 4;
                var availableLength = fieldEnd - nestedOffset;
                if (nestedLength < 0
                    || availableLength < 0
                    || nestedLength > availableLength
                    || nestedOffset > bytes.Length
                    || bytes.Length - nestedOffset < nestedLength)
                {
                    accessIssues.Add($"{fileName}: hero raw_data length is invalid path={scalar.Path} length={nestedLength}");
                    continue;
                }

                if (nestedLength < 0x40 || ReadUInt32LittleEndian(bytes, nestedOffset) != 0x0000B101)
                {
                    accessIssues.Add($"{fileName}: hero raw_data is not a DSON container path={scalar.Path}");
                    continue;
                }

                var nestedIssues = new List<string>();
                var nested = TryParseDsonContainer(bytes, nestedOffset, nestedLength, nestedIssues);
                foreach (var issue in nestedIssues)
                {
                    accessIssues.Add($"{fileName}: hero={heroId} {issue}");
                }

                if (nested is null)
                {
                    accessIssues.Add($"{fileName}: failed to parse nested hero DSON path={scalar.Path}");
                    continue;
                }

                heroes.Add(BuildSaveStateHeroFacts(heroId, nestedLength, nested));
            }

            return heroes
                .OrderBy(hero => int.TryParse(hero.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : int.MaxValue)
                .ThenBy(hero => hero.Id, StringComparer.OrdinalIgnoreCase)
                .Take(120)
                .ToArray();
        }

        private static string? ExtractHeroIdFromRawDataPath(string path)
        {
            const string prefix = "base_root.heroes.";
            const string suffix = ".hero_file_data.raw_data";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                || path.Length <= prefix.Length + suffix.Length)
            {
                return null;
            }

            return path[prefix.Length..^suffix.Length];
        }

        private static SaveStateHeroFacts BuildSaveStateHeroFacts(string heroId, int rawDataLength, BinaryContainerInfo nested)
        {
            var quirkIds = MergeAllDirectChildIds(
                ExtractAllDirectChildIds(nested.DsonObjectPaths, "base_root.quirks"),
                ExtractAllDirectChildIds(nested.DsonScalars, "base_root.quirks"));
            var trinkets = BuildHeroTrinketFacts(nested, "base_root.trinkets.items");

            return new SaveStateHeroFacts(
                heroId,
                EmptyToNull(TryGetString(nested.DsonScalars, "base_root.actor.name")),
                EmptyToNull(TryGetString(nested.DsonScalars, "base_root.heroClass")),
                TryGetInt(nested.DsonScalars, "base_root.roster.status"),
                TryGetInt(nested.DsonScalars, "base_root.roster.before_on_start_town_visit_status"),
                TryGetInt(nested.DsonScalars, "base_root.roster.missing_duration"),
                TryGetInt(nested.DsonScalars, "base_root.roster.story_variation"),
                EmptyToNull(TryGetString(nested.DsonScalars, "base_root.roster.missing_from")),
                EmptyToNull(TryGetString(nested.DsonScalars, "base_root.roster.building_name")),
                TryGetInt(nested.DsonScalars, "base_root.roster.timestamp"),
                TryGetInt(nested.DsonScalars, "base_root.resolveXp"),
                TryGetDouble(nested.DsonScalars, "base_root.actor.current_hp"),
                TryGetDouble(nested.DsonScalars, "base_root.m_Stress"),
                TryGetInt(nested.DsonScalars, "base_root.weapon_rank"),
                TryGetInt(nested.DsonScalars, "base_root.armour_rank"),
                TryGetInt(nested.DsonScalars, "base_root.actor.colour_variation"),
                TryGetBool(nested.DsonScalars, "base_root.backer_hero"),
                TryGetBool(nested.DsonScalars, "base_root.actor.combat_ready"),
                TryGetInt(nested.DsonScalars, "base_root.actor.stunned"),
                TryGetBool(nested.DsonScalars, "base_root.is_death_heart_attack_completed"),
                TryGetBool(nested.DsonScalars, "base_root.visited_deaths_door"),
                TryGetInt(nested.DsonScalars, "base_root.deaths_door_enter_effect_round_cooldown"),
                TryGetBool(nested.DsonScalars, "base_root.has_had_heart_attack"),
                TryGetInt(nested.DsonScalars, "base_root.steps_taken"),
                TryGetInt(nested.DsonScalars, "base_root.enemies_killed"),
                TryGetInt(nested.DsonScalars, "base_root.provisions_consumed"),
                TryGetInt(nested.DsonScalars, "base_root.number_of_successful_darkest_dungeon_quests"),
                TryGetBool(nested.DsonScalars, "base_root.is_from_town_event"),
                EmptyToNull(TryGetString(nested.DsonScalars, "base_root.affliction_type_id")),
                TryGetInt(nested.DsonScalars, "base_root.affliction_severity"),
                EmptyToNull(TryGetString(nested.DsonScalars, "base_root.virtue_type_id")),
                rawDataLength,
                nested.DsonSummary.ObjectCount,
                nested.DsonSummary.FieldCount,
                quirkIds,
                BuildHeroQuirkFacts(nested, quirkIds),
                ExtractAllDirectChildIds(nested.DsonScalars, "base_root.skills.selected_combat_skills"),
                ExtractAllDirectChildIds(nested.DsonScalars, "base_root.skills.selected_camping_skills"),
                trinkets
                    .Select(trinket => trinket.ItemId)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                trinkets);
        }

        private static IReadOnlyList<SaveStateHeroQuirkFacts> BuildHeroQuirkFacts(
            BinaryContainerInfo nested,
            IReadOnlyList<string> quirkIds)
        {
            return quirkIds
                .Select(quirkId =>
                {
                    var path = $"base_root.quirks.{quirkId}";
                    return new SaveStateHeroQuirkFacts(
                        quirkId,
                        TryGetBool(nested.DsonScalars, $"{path}.is_new"),
                        TryGetBool(nested.DsonScalars, $"{path}.is_locked"),
                        TryGetInt(nested.DsonScalars, $"{path}.mission_count"),
                        TryGetInt(nested.DsonScalars, $"{path}.replaces_quirk"),
                        TryGetBool(nested.DsonScalars, $"{path}.replaces_quirk_viewed"),
                        TryGetInt(nested.DsonScalars, $"{path}.evolution_duration_remaining"));
                })
                .OrderBy(quirk => quirk.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IReadOnlyList<SaveStateHeroTrinketFacts> BuildHeroTrinketFacts(
            BinaryContainerInfo nested,
            string itemsPath)
        {
            return MergeAllDirectChildIds(
                    ExtractAllDirectChildIds(nested.DsonObjectPaths, itemsPath),
                    ExtractAllDirectChildIds(nested.DsonScalars, itemsPath))
                .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                .Select(slotId =>
                {
                    var itemPath = $"{itemsPath}.{slotId}";
                    return new SaveStateHeroTrinketFacts(
                        slotId,
                        EmptyToNull(TryGetString(nested.DsonScalars, $"{itemPath}.id")),
                        EmptyToNull(TryGetString(nested.DsonScalars, $"{itemPath}.type")),
                        TryGetInt(nested.DsonScalars, $"{itemPath}.amount"));
                })
                .ToArray();
        }
    }
}
