namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static SaveStateMapFacts BuildMapFacts(SaveStateFileReport? map)
        {
            if (map is null || !map.Exists)
            {
                return new SaveStateMapFacts(false, [], false, null, 0, 0, []);
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
                areas.Sum(area => area.TileCount),
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

            return areaIds
                .Select(areaId =>
                {
                    var areaPath = $"base_root.areas.{areaId}";
                    var tileIds = MergeAllDirectChildIds(
                            ExtractAllDirectChildIds(staticSave.AllObjectPaths, $"{areaPath}.tiles"),
                            ExtractAllDirectChildIds(scalars, $"{areaPath}.tiles"))
                        .OrderBy(NumericAwareSortKey, StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    return new SaveStateMapAreaFacts(
                        areaId,
                        TryGetFloatArray(scalars, $"{areaPath}.bounds"),
                        tileIds.Length,
                        tileIds
                            .Take(40)
                            .Select(tileId => BuildMapTileFacts(scalars, areaPath, tileId))
                            .ToArray());
                })
                .ToArray();
        }

        private static SaveStateMapTileFacts BuildMapTileFacts(
            IReadOnlyList<SaveStateDsonScalar> scalars,
            string areaPath,
            string tileId)
        {
            var tilePath = $"{areaPath}.tiles.{tileId}";
            return new SaveStateMapTileFacts(
                tileId,
                TryGetFloatArray(scalars, $"{tilePath}.mappos"),
                TryGetFloatArray(scalars, $"{tilePath}.sidepos"));
        }
    }
}
