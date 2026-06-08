namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static SaveStateRaidFacts BuildRaidFacts(SaveStateFileReport? raid, SaveStateMapFacts map)
        {
            if (raid is null || !raid.Exists)
            {
                return EmptyRaidFacts(null);
            }

            var areaHashToId = map.Areas
                .Where(area => area.AreaHash.HasValue)
                .GroupBy(area => area.AreaHash!.Value)
                .ToDictionary(group => group.Key, group => group.First().AreaId);

            return new SaveStateRaidFacts(
                TryGetInt(raid, "base_root.version"),
                BuildRaidInstanceFacts(raid),
                BuildRaidLocationFacts(raid, areaHashToId),
                BuildRaidPartyFacts(raid, areaHashToId),
                BuildRaidCampFacts(raid),
                BuildRaidMashFacts(raid),
                BuildRaidStatDatabaseFacts(raid));
        }

        private static SaveStateRaidFacts EmptyRaidFacts(int? version)
        {
            return new SaveStateRaidFacts(
                version,
                new SaveStateRaidInstanceFacts(
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
                    null,
                    null,
                    0,
                    [],
                    null,
                    EmptyQuestRewardFacts(),
                    0,
                    []),
                new SaveStateRaidLocationFacts(null, null, null, null, null, new SaveStateRaidDoorwayFacts(null, null, null, null), null, null, null, null, null, null),
                new SaveStateRaidPartyFacts(null, null, null, 0, [], null, 0, [], null),
                new SaveStateRaidCampFacts(null, null, null, 0, [], 0, []),
                new SaveStateRaidMashFacts(null, null),
                new SaveStateRaidStatDatabaseFacts(0, 0, []));
        }

        private static SaveStateRaidInstanceFacts BuildRaidInstanceFacts(SaveStateFileReport raid)
        {
            var path = "base_root.raid_instance";
            var goalIds = TryGetStringVector(raid, $"{path}.goal_ids");
            var progressGroups = BuildRaidProgressGroupFacts(raid, path, goalIds);

            return new SaveStateRaidInstanceFacts(
                EmptyToNull(TryGetString(raid, $"{path}.id")),
                EmptyToNull(TryGetString(raid, $"{path}.dungeon")),
                EmptyToNull(TryGetString(raid, $"{path}.type")),
                EmptyToNull(TryGetString(raid, $"{path}.map_name")),
                TryGetInt(raid, $"{path}.difficulty"),
                TryGetInt(raid, $"{path}.length"),
                TryGetBool(raid, $"{path}.is_plot_quest"),
                TryGetBool(raid, $"{path}.counted_in_generation"),
                TryGetBool(raid, $"{path}.is_from_town_event"),
                TryGetInt(raid, $"{path}.completion_threshold"),
                TryGetBool(raid, $"{path}.use_default_progression_goals"),
                EmptyToNull(TryGetString(raid, $"{path}.raid_rules_override")),
                EmptyToNull(TryGetString(raid, $"{path}.torch_setting")),
                goalIds.Count,
                goalIds,
                BuildSimpleScalarFacts(raid, $"{path}.progression_goal_ids"),
                BuildQuestRewardFacts(raid, $"{path}.completion_reward"),
                progressGroups.Count,
                progressGroups);
        }

        private static IReadOnlyList<SaveStateRaidProgressGroupFacts> BuildRaidProgressGroupFacts(
            SaveStateFileReport raid,
            string raidInstancePath,
            IReadOnlyList<string> goalIds)
        {
            var result = new List<SaveStateRaidProgressGroupFacts>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var goalId in goalIds.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                AddRaidProgressGroup(result, seenPaths, raid, goalId, $"{raidInstancePath}.{goalId}");
            }

            foreach (var childId in ExtractRaidChildIds(raid, $"{raidInstancePath}.town_progression_goals")
                         .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase))
            {
                AddRaidProgressGroup(result, seenPaths, raid, $"town_progression_goals.{childId}", $"{raidInstancePath}.town_progression_goals.{childId}");
            }

            var standardChildIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "id",
                "map_name",
                "torch_setting",
                "raid_rules_override",
                "is_plot_quest",
                "type",
                "dungeon",
                "difficulty",
                "length",
                "counted_in_generation",
                "goal_ids",
                "progression_goal_ids",
                "use_default_progression_goals",
                "completion_reward",
                "completion_threshold",
                "is_from_town_event",
                "town_progression_goals"
            };

            foreach (var childId in ExtractRaidChildIds(raid, raidInstancePath)
                         .Where(childId => !standardChildIds.Contains(childId))
                         .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase))
            {
                AddRaidProgressGroup(result, seenPaths, raid, childId, $"{raidInstancePath}.{childId}");
            }

            return result.ToArray();
        }

        private static void AddRaidProgressGroup(
            List<SaveStateRaidProgressGroupFacts> result,
            HashSet<string> seenPaths,
            SaveStateFileReport raid,
            string groupId,
            string sourcePath)
        {
            if (!seenPaths.Add(sourcePath))
            {
                return;
            }

            var scalarFields = BuildRaidScalarFacts(raid, sourcePath);
            if (scalarFields.Count == 0)
            {
                return;
            }

            result.Add(new SaveStateRaidProgressGroupFacts(
                groupId,
                sourcePath,
                scalarFields.Count,
                scalarFields));
        }

        private static SaveStateRaidLocationFacts BuildRaidLocationFacts(
            SaveStateFileReport raid,
            IReadOnlyDictionary<int, string> areaHashToId)
        {
            var inAreaHash = TryGetInt(raid, "base_root.in_area");
            var lastRoomHash = TryGetInt(raid, "base_root.last_room_id");

            return new SaveStateRaidLocationFacts(
                inAreaHash,
                ResolveAreaId(areaHashToId, inAreaHash),
                TryGetInt(raid, "base_root.areatile"),
                lastRoomHash,
                ResolveAreaId(areaHashToId, lastRoomHash),
                BuildRaidDoorwayFacts(raid, "base_root.in_doorway", areaHashToId),
                TryGetDouble(raid, "base_root.start_elapsed_time"),
                TryGetDouble(raid, "base_root.torchlight"),
                TryGetDouble(raid, "base_root.ambush_start_torchlight"),
                TryGetDouble(raid, "base_root.shard_consume_percent"),
                TryGetBool(raid, "base_root.teleported"),
                TryGetBool(raid, "base_root.inbattle"));
        }

        private static SaveStateRaidDoorwayFacts BuildRaidDoorwayFacts(
            SaveStateFileReport raid,
            string path,
            IReadOnlyDictionary<int, string> areaHashToId)
        {
            var targetAreaHash = TryGetInt(raid, $"{path}.area_to");
            return new SaveStateRaidDoorwayFacts(
                targetAreaHash,
                ResolveAreaId(areaHashToId, targetAreaHash),
                TryGetInt(raid, $"{path}.tile_to"),
                TryGetBool(raid, $"{path}.implied"));
        }

        private static SaveStateRaidPartyFacts BuildRaidPartyFacts(
            SaveStateFileReport raid,
            IReadOnlyDictionary<int, string> areaHashToId)
        {
            var retreatRoomHash = TryGetInt(raid, "base_root.party.retreat_room");
            var heroGuids = TryGetIntVector(raid, "base_root.party.heroes");
            var inventoryItems = BuildRaidItemFacts(raid, "base_root.party.inventory.items");

            return new SaveStateRaidPartyFacts(
                TryGetBool(raid, "base_root.party.IsMovingLeft()"),
                retreatRoomHash,
                ResolveAreaId(areaHashToId, retreatRoomHash),
                heroGuids.Count,
                heroGuids,
                TryGetInt(raid, "base_root.party.start_heroes_size"),
                inventoryItems.Count,
                inventoryItems,
                TryGetInt(raid, "base_root.party.hunger_room_buffer"));
        }

        private static IReadOnlyList<SaveStateRaidItemFacts> BuildRaidItemFacts(
            SaveStateFileReport raid,
            string parentPath)
        {
            return ExtractRaidChildIds(raid, parentPath)
                .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                .Select(slotId =>
                {
                    var itemPath = $"{parentPath}.{slotId}";
                    return new SaveStateRaidItemFacts(
                        slotId,
                        EmptyToNull(TryGetString(raid, $"{itemPath}.id")),
                        EmptyToNull(TryGetString(raid, $"{itemPath}.type")),
                        TryGetInt(raid, $"{itemPath}.amount"));
                })
                .ToArray();
        }

        private static SaveStateRaidCampFacts BuildRaidCampFacts(SaveStateFileReport raid)
        {
            var skillLog = ExtractRaidChildIds(raid, "base_root.camp.skill_log")
                .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                .Select(slotId =>
                {
                    var path = $"base_root.camp.skill_log.{slotId}";
                    return new SaveStateRaidCampSkillLogFacts(
                        slotId,
                        TryGetInt(raid, $"{path}.roster_id"),
                        TryGetInt(raid, $"{path}.skill_id"),
                        TryGetInt(raid, $"{path}.level"),
                        TryGetInt(raid, $"{path}.count"));
                })
                .ToArray();

            var partySkillLog = ExtractRaidChildIds(raid, "base_root.camp.party_skill_log")
                .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                .Select(slotId => BuildRaidCampPartySkillLogFacts(raid, slotId))
                .ToArray();

            return new SaveStateRaidCampFacts(
                TryGetInt(raid, "base_root.camp.phase"),
                TryGetInt(raid, "base_root.camp.camping_skill_points"),
                TryGetInt(raid, "base_root.camp.camp_finish_flashback_class_id"),
                skillLog.Length,
                skillLog,
                partySkillLog.Length,
                partySkillLog);
        }

        private static SaveStateRaidCampPartySkillLogFacts BuildRaidCampPartySkillLogFacts(
            SaveStateFileReport raid,
            string slotId)
        {
            var path = $"base_root.camp.party_skill_log.{slotId}";
            var buffResults = ExtractRaidChildIds(raid, $"{path}.party_buff_results")
                .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                .Select(resultSlotId =>
                {
                    var resultPath = $"{path}.party_buff_results.{resultSlotId}";
                    return new SaveStateRaidCampPartySkillBuffResultFacts(
                        resultSlotId,
                        TryGetInt(raid, $"{resultPath}.actor_guid"),
                        TryGetInt(raid, $"{resultPath}.party_skill_ambush"));
                })
                .ToArray();

            return new SaveStateRaidCampPartySkillLogFacts(
                slotId,
                TryGetInt(raid, $"{path}.party_skill_id"),
                TryGetInt(raid, $"{path}.party_skill_level"),
                TryGetInt(raid, $"{path}.party_skill_ambush"),
                buffResults.Length,
                buffResults);
        }

        private static SaveStateRaidMashFacts BuildRaidMashFacts(SaveStateFileReport raid)
        {
            return new SaveStateRaidMashFacts(
                TryGetBool(raid, "base_root.has_mash_data"),
                BuildSimpleScalarFacts(raid, "base_root.mash.valid_additional_mash_entry_indexes"));
        }

        private static SaveStateRaidStatDatabaseFacts BuildRaidStatDatabaseFacts(SaveStateFileReport raid)
        {
            var events = ExtractRaidChildIds(raid, "base_root.stat_database")
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(eventId =>
                {
                    var path = $"base_root.stat_database.{eventId}";
                    var entryIds = ExtractRaidChildIds(raid, $"{path}.entries");
                    return new SaveStateRaidStatEventFacts(
                        eventId,
                        TryGetInt(raid, $"{path}.count"),
                        entryIds.Count);
                })
                .ToArray();

            return new SaveStateRaidStatDatabaseFacts(
                events.Length,
                events.Sum(item => item.EntryCount),
                events);
        }

        private static IReadOnlyList<SaveStateRaidScalarFacts> BuildRaidScalarFacts(
            SaveStateFileReport raid,
            string sourcePath)
        {
            var prefix = sourcePath + ".";
            return GetDsonScalars(raid)
                .Where(scalar => scalar.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(scalar => scalar.Path, StringComparer.OrdinalIgnoreCase)
                .Select(scalar => new SaveStateRaidScalarFacts(
                    scalar.Path[prefix.Length..],
                    scalar.Name,
                    scalar.Type,
                    scalar.Value))
                .ToArray();
        }

        private static IReadOnlyList<string> ExtractRaidChildIds(SaveStateFileReport raid, string parentPath)
        {
            return MergeAllDirectChildIds(
                ExtractAllDirectChildIds(raid.DsonObjectPaths, parentPath),
                ExtractAllDirectChildIds(GetDsonScalars(raid), parentPath));
        }

        private static string? ResolveAreaId(IReadOnlyDictionary<int, string> areaHashToId, int? areaHash)
        {
            return areaHash.HasValue && areaHashToId.TryGetValue(areaHash.Value, out var areaId)
                ? areaId
                : null;
        }

        private static SaveStateQuestRewardFacts EmptyQuestRewardFacts()
        {
            return new SaveStateQuestRewardFacts(null, null, null, 0, [], []);
        }
    }
}
