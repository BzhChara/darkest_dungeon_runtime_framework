namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static SaveStateMapFacts BuildMapFacts(SaveStateFileReport? map)
        {
            if (map is null || !map.Exists)
            {
                return new SaveStateMapFacts(false, [], false, null, 0, 0, 0, 0, 0, []);
            }

            var staticSave = FindDsonScalar(map, "base_root.map.static_dynamic.static_save")?.EmbeddedDson;
            var staticSaveFacts = staticSave is null ? null : BuildEmbeddedDsonFacts(staticSave);
            var areas = staticSave is null ? [] : BuildMapAreaFacts(staticSave);

            return new SaveStateMapFacts(
                true,
                TryGetFloatArray(map, "base_root.map.bounds"),
                staticSave is not null,
                staticSaveFacts,
                areas.Count,
                areas.Count(area => area.InferredRole.Equals("room", StringComparison.OrdinalIgnoreCase)),
                areas.Count(area => area.InferredRole.Equals("corridor", StringComparison.OrdinalIgnoreCase)),
                areas.Sum(area => area.TileCount),
                areas.Sum(area => area.ActiveDoorCount),
                areas);
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
                            .Take(40)
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
    }
}
