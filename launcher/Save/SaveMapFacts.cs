namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static SaveStateMapFacts BuildMapFacts(SaveStateFileReport? map)
        {
            if (map is null || !map.Exists)
            {
                return new SaveStateMapFacts(false, [], false, null, null, null, null, null, null, 0, 0, 0, 0, 0, 0, 0, EmptyMapTopologyFacts(), [], []);
            }

            var staticSave = FindDsonScalar(map, "base_root.map.static_dynamic.static_save")?.EmbeddedDson;
            var staticSaveFacts = staticSave is null ? null : BuildEmbeddedDsonFacts(staticSave);
            var areas = staticSave is null ? [] : BuildMapAreaFacts(staticSave);
            var areaHashToId = areas
                .Where(area => area.AreaHash.HasValue)
                .GroupBy(area => area.AreaHash!.Value)
                .ToDictionary(group => group.Key, group => group.First().AreaId);
            var areaIdToHash = areas
                .Where(area => area.AreaHash.HasValue)
                .GroupBy(area => area.AreaId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().AreaHash, StringComparer.OrdinalIgnoreCase);
            var dynamicAreas = BuildMapDynamicAreaFacts(map, areaIdToHash);
            var entranceAreaHash = TryGetInt(map, "base_root.map.entrance_id");
            var finalRoomHash = TryGetInt(map, "base_root.map.final_room_id");
            var entranceAreaId = ResolveMapAreaId(areaHashToId, entranceAreaHash);
            var finalRoomId = ResolveMapAreaId(areaHashToId, finalRoomHash);
            var topology = BuildMapTopologyFacts(areas, entranceAreaHash, entranceAreaId, finalRoomHash, finalRoomId);

            return new SaveStateMapFacts(
                true,
                TryGetFloatArray(map, "base_root.map.bounds"),
                staticSave is not null,
                staticSaveFacts,
                TryGetBool(map, "base_root.map.populated"),
                entranceAreaHash,
                entranceAreaId,
                finalRoomHash,
                finalRoomId,
                areas.Count,
                areas.Count(area => area.InferredRole.Equals("room", StringComparison.OrdinalIgnoreCase)),
                areas.Count(area => area.InferredRole.Equals("corridor", StringComparison.OrdinalIgnoreCase)),
                areas.Sum(area => area.TileCount),
                areas.Sum(area => area.ActiveDoorCount),
                dynamicAreas.Count,
                dynamicAreas.Sum(area => area.TileCount),
                topology,
                dynamicAreas,
                areas);
        }

        private static SaveStateMapTopologyFacts EmptyMapTopologyFacts()
        {
            return new SaveStateMapTopologyFacts(false, false, false, 0, [], [], 0, 0, 0, []);
        }

        private static SaveStateMapTopologyFacts BuildMapTopologyFacts(
            IReadOnlyList<SaveStateMapAreaFacts> areas,
            int? entranceAreaHash,
            string? entranceAreaId,
            int? finalRoomHash,
            string? finalRoomId)
        {
            var issues = new List<string>();
            var areaById = areas.ToDictionary(area => area.AreaId, StringComparer.OrdinalIgnoreCase);
            var adjacency = areas.ToDictionary(
                area => area.AreaId,
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            var areaDoorEdgeCount = 0;
            var tileDoorEdgeCount = 0;
            var invalidDoorTargetCount = 0;

            foreach (var area in areas)
            {
                foreach (var door in area.Doors)
                {
                    areaDoorEdgeCount++;
                    AddMapTopologyEdge(area, door, "areaDoor", areaById, adjacency, issues, ref invalidDoorTargetCount);
                }

                foreach (var tile in area.TileSamples)
                {
                    if (tile.DoorTo is null)
                    {
                        continue;
                    }

                    tileDoorEdgeCount++;
                    AddMapTopologyEdge(area, tile.DoorTo, $"tileDoor:{tile.TileId}", areaById, adjacency, issues, ref invalidDoorTargetCount);
                }
            }

            var hasEntranceArea = entranceAreaId is not null && areaById.ContainsKey(entranceAreaId);
            if (entranceAreaHash.HasValue && !hasEntranceArea)
            {
                issues.Add($"entrance area hash {entranceAreaHash.Value} did not resolve to a decoded area.");
            }
            else if (!entranceAreaHash.HasValue && areas.Count > 0)
            {
                issues.Add("map has decoded areas but no entrance area hash.");
            }

            var hasFinalRoom = finalRoomHash is > 0 && finalRoomId is not null && areaById.ContainsKey(finalRoomId);
            if (finalRoomHash is > 0 && !hasFinalRoom)
            {
                issues.Add($"final room hash {finalRoomHash.Value} did not resolve to a decoded area.");
            }

            var reachableAreaIds = hasEntranceArea
                ? TraverseMapTopology(entranceAreaId!, adjacency)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unreachableAreaIds = areas
                .Select(area => area.AreaId)
                .Where(areaId => !reachableAreaIds.Contains(areaId))
                .OrderBy(areaId => areaId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var entranceCanReachFinal = hasEntranceArea && hasFinalRoom && reachableAreaIds.Contains(finalRoomId!);
            if (hasEntranceArea && hasFinalRoom && !entranceCanReachFinal)
            {
                issues.Add($"final room {finalRoomId} is not connected to entrance area {entranceAreaId}.");
            }

            return new SaveStateMapTopologyFacts(
                hasEntranceArea,
                hasFinalRoom,
                entranceCanReachFinal,
                reachableAreaIds.Count,
                reachableAreaIds.OrderBy(areaId => areaId, StringComparer.OrdinalIgnoreCase).ToArray(),
                unreachableAreaIds,
                areaDoorEdgeCount,
                tileDoorEdgeCount,
                invalidDoorTargetCount,
                issues);
        }

        private static void AddMapTopologyEdge(
            SaveStateMapAreaFacts sourceArea,
            SaveStateMapDoorFacts door,
            string sourceSlot,
            IReadOnlyDictionary<string, SaveStateMapAreaFacts> areaById,
            IReadOnlyDictionary<string, HashSet<string>> adjacency,
            List<string> issues,
            ref int invalidDoorTargetCount)
        {
            if (string.IsNullOrWhiteSpace(door.TargetAreaId) || !areaById.TryGetValue(door.TargetAreaId, out var targetArea))
            {
                invalidDoorTargetCount++;
                issues.Add($"{sourceArea.AreaId}.{sourceSlot} targets unresolved area hash {door.TargetAreaHash?.ToString() ?? "<null>"}.");
                return;
            }

            if (door.TargetTileIndex.HasValue &&
                (door.TargetTileIndex.Value < 0 || door.TargetTileIndex.Value >= targetArea.TileCount))
            {
                invalidDoorTargetCount++;
                issues.Add($"{sourceArea.AreaId}.{sourceSlot} targets {door.TargetAreaId} tile index {door.TargetTileIndex}, outside tile count {targetArea.TileCount}.");
                return;
            }

            adjacency[sourceArea.AreaId].Add(door.TargetAreaId);
            adjacency[door.TargetAreaId].Add(sourceArea.AreaId);
        }

        private static HashSet<string> TraverseMapTopology(
            string startAreaId,
            IReadOnlyDictionary<string, HashSet<string>> adjacency)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!adjacency.ContainsKey(startAreaId))
            {
                return visited;
            }

            var queue = new Queue<string>();
            queue.Enqueue(startAreaId);
            visited.Add(startAreaId);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var next in adjacency[current])
                {
                    if (visited.Add(next))
                    {
                        queue.Enqueue(next);
                    }
                }
            }

            return visited;
        }

        private static SaveStateEmbeddedDsonFacts BuildEmbeddedDsonFacts(
            SaveStateEmbeddedDsonSummary embedded)
        {
            return new SaveStateEmbeddedDsonFacts(
                embedded.Length,
                embedded.DsonSummary.ObjectCount,
                embedded.DsonSummary.FieldCount,
                embedded.DsonSummary.ParsedScalarCount,
                embedded.DsonSummary.RawScalarCount,
                embedded.ObjectPathCount,
                embedded.RootChildCount,
                embedded.RootChildIds);
        }

        private static IReadOnlyList<SaveStateMapAreaFacts> BuildMapAreaFacts(
            SaveStateEmbeddedDsonSummary staticSave)
        {
            var scalars = staticSave.AllScalars;
            var areaIds = MergeAllDirectChildIds(
                    ExtractAllDirectChildIds(staticSave.AllObjectPaths, "base_root.areas"),
                    ExtractAllDirectChildIds(scalars, "base_root.areas"))
                .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var areaHashToId = areaIds
                .Select(areaId => new
                {
                    AreaId = areaId,
                    AreaHash = TryGetInt(scalars, $"base_root.areas.{areaId}.id")
                })
                .Where(area => area.AreaHash.HasValue)
                .GroupBy(area => area.AreaHash!.Value)
                .ToDictionary(group => group.Key, group => group.First().AreaId);

            return areaIds
                .Select(areaId =>
                {
                    var areaPath = $"base_root.areas.{areaId}";
                    var doors = BuildMapAreaDoorFacts(scalars, areaPath, areaHashToId);
                    var tileIds = MergeAllDirectChildIds(
                            ExtractAllDirectChildIds(staticSave.AllObjectPaths, $"{areaPath}.tiles"),
                            ExtractAllDirectChildIds(scalars, $"{areaPath}.tiles"))
                        .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    return new SaveStateMapAreaFacts(
                        areaId,
                        TryGetInt(scalars, $"{areaPath}.id"),
                        TryGetInt(scalars, $"{areaPath}.kind"),
                        InferMapAreaRole(areaId, TryGetInt(scalars, $"{areaPath}.kind")),
                        EmptyToNull(TryGetString(scalars, $"{areaPath}.name")),
                        TryGetBool(scalars, $"{areaPath}.torch"),
                        TryGetFloatArray(scalars, $"{areaPath}.bounds"),
                        tileIds.Length,
                        CountMapDoorSlots(scalars, areaPath),
                        doors.Count,
                        doors,
                        tileIds
                            .Select(tileId => BuildMapTileFacts(scalars, areaPath, tileId, areaHashToId))
                            .ToArray());
                })
                .ToArray();
        }

        private static IReadOnlyList<SaveStateMapDoorFacts> BuildMapAreaDoorFacts(
            IReadOnlyList<SaveStateDsonScalar> scalars,
            string areaPath,
            IReadOnlyDictionary<int, string> areaHashToId)
        {
            return Enumerable.Range(0, 8)
                .Select(index => BuildMapDoorFacts(scalars, $"{areaPath}.door{index}", $"door{index}", areaHashToId))
                .Where(door => door.TargetAreaId is not null)
                .ToArray();
        }

        private static int CountMapDoorSlots(
            IReadOnlyList<SaveStateDsonScalar> scalars,
            string areaPath)
        {
            return Enumerable.Range(0, 8)
                .Count(index => FindDsonScalar(scalars, $"{areaPath}.door{index}.area_to") is not null);
        }

        private static SaveStateMapDoorFacts BuildMapDoorFacts(
            IReadOnlyList<SaveStateDsonScalar> scalars,
            string doorPath,
            string slotId,
            IReadOnlyDictionary<int, string> areaHashToId)
        {
            var targetAreaHash = TryGetInt(scalars, $"{doorPath}.area_to");
            var targetAreaId = targetAreaHash.HasValue && areaHashToId.TryGetValue(targetAreaHash.Value, out var resolvedAreaId)
                ? resolvedAreaId
                : null;

            return new SaveStateMapDoorFacts(
                slotId,
                targetAreaHash,
                targetAreaId,
                TryGetInt(scalars, $"{doorPath}.tile_to"),
                TryGetInt(scalars, $"{doorPath}.type"),
                TryGetBool(scalars, $"{doorPath}.implied"));
        }

        private static string InferMapAreaRole(string areaId, int? kind)
        {
            if (areaId.StartsWith("roo", StringComparison.OrdinalIgnoreCase))
            {
                return "room";
            }

            if (areaId.StartsWith("co", StringComparison.OrdinalIgnoreCase))
            {
                return "corridor";
            }

            return kind switch
            {
                0 => "room",
                1 => "corridor",
                _ => "unknown"
            };
        }

        private static SaveStateMapTileFacts BuildMapTileFacts(
            IReadOnlyList<SaveStateDsonScalar> scalars,
            string areaPath,
            string tileId,
            IReadOnlyDictionary<int, string> areaHashToId)
        {
            var tilePath = $"{areaPath}.tiles.{tileId}";
            var doorTo = BuildMapDoorFacts(scalars, $"{tilePath}.door_to", "door_to", areaHashToId);
            return new SaveStateMapTileFacts(
                tileId,
                TryGetFloatArray(scalars, $"{tilePath}.mappos"),
                TryGetFloatArray(scalars, $"{tilePath}.sidepos"),
                TryGetInt(scalars, $"{tilePath}.area_id"),
                TryGetInt(scalars, $"{tilePath}.type"),
                TryGetInt(scalars, $"{tilePath}.obstacle"),
                TryGetInt(scalars, $"{tilePath}.texture_id"),
                TryGetInt(scalars, $"{tilePath}.front_texture_id"),
                TryGetBool(scalars, $"{tilePath}.is_texture_override_valid"),
                TryGetBool(scalars, $"{tilePath}.hd_always_accessible"),
                TryGetInt(scalars, $"{tilePath}.cur"),
                doorTo.TargetAreaId is null ? null : doorTo);
        }

        private static IReadOnlyList<SaveStateMapDynamicAreaFacts> BuildMapDynamicAreaFacts(
            SaveStateFileReport map,
            IReadOnlyDictionary<string, int?> areaIdToHash)
        {
            return MergeAllDirectChildIds(
                    ExtractAllDirectChildIds(map.DsonObjectPaths, "base_root.map.static_dynamic.areas"),
                    ExtractAllDirectChildIds(GetDsonScalars(map), "base_root.map.static_dynamic.areas"))
                .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                .Select(areaId =>
                {
                    var areaPath = $"base_root.map.static_dynamic.areas.{areaId}";
                    var tileIds = MergeAllDirectChildIds(
                            ExtractAllDirectChildIds(map.DsonObjectPaths, $"{areaPath}.tiles"),
                            ExtractAllDirectChildIds(GetDsonScalars(map), $"{areaPath}.tiles"))
                        .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    return new SaveStateMapDynamicAreaFacts(
                        areaId,
                        areaIdToHash.TryGetValue(areaId, out var areaHash) ? areaHash : null,
                        TryGetInt(map, $"{areaPath}.knowledge"),
                        TryGetBool(map, $"{areaPath}.reversed"),
                        tileIds.Length,
                        tileIds
                            .Select(tileId => BuildMapDynamicTileFacts(map, areaPath, tileId))
                            .ToArray());
                })
                .ToArray();
        }

        private static SaveStateMapDynamicTileFacts BuildMapDynamicTileFacts(
            SaveStateFileReport map,
            string areaPath,
            string tileId)
        {
            var tilePath = $"{areaPath}.tiles.{tileId}";
            return new SaveStateMapDynamicTileFacts(
                tileId,
                TryGetInt(map, $"{tilePath}.light"),
                TryGetInt(map, $"{tilePath}.content"),
                TryGetInt(map, $"{tilePath}.curio_prop"),
                TryGetInt(map, $"{tilePath}.knowledge"),
                TryGetInt(map, $"{tilePath}.trap"),
                TryGetInt(map, $"{tilePath}.mash_index"),
                TryGetInt(map, $"{tilePath}.mash_type"),
                TryGetBool(map, $"{tilePath}.crit_scout"));
        }

        private static string? ResolveMapAreaId(IReadOnlyDictionary<int, string> areaHashToId, int? areaHash)
        {
            return areaHash.HasValue && areaHashToId.TryGetValue(areaHash.Value, out var areaId)
                ? areaId
                : null;
        }
    }
}
