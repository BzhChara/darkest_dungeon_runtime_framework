namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        public static string WriteMapTemplatePrototype(
            string sourceMapFilePath,
            string specFilePath,
            string outputMapFilePath,
            string reportOutputPath,
            LauncherLog log)
        {
            var sourceFullPath = Path.GetFullPath(sourceMapFilePath);
            var specFullPath = Path.GetFullPath(specFilePath);
            var outputFullPath = Path.GetFullPath(outputMapFilePath);
            var reportFullPath = Path.GetFullPath(reportOutputPath);
            var generatedAt = DateTimeOffset.Now;
            var accessIssues = new List<string>();

            if (!File.Exists(sourceFullPath))
            {
                throw new FileNotFoundException("Source map file was not found.", sourceFullPath);
            }

            if (!File.Exists(specFullPath))
            {
                throw new FileNotFoundException("Map template spec file was not found.", specFullPath);
            }

            var spec = ReadMapTemplateSpec(specFullPath);
            if (spec.Version != 1)
            {
                accessIssues.Add($"Unsupported map template spec version: {spec.Version}");
            }

            var sourceFile = InspectFile(sourceFullPath, Path.GetFileName(sourceFullPath));
            var sourceMap = BuildMapFacts(sourceFile);
            accessIssues.AddRange(sourceFile.AccessIssues);
            if (!sourceFile.ParseStatus.Equals("dsonPartialDecoded", StringComparison.OrdinalIgnoreCase))
            {
                accessIssues.Add($"Source map did not parse as a DSON container: {sourceFullPath}");
            }

            if (!sourceMap.HasStaticSave)
            {
                accessIssues.Add("Source map has no decoded base_root.map.static_dynamic.static_save payload.");
            }

            var bytes = File.ReadAllBytes(sourceFullPath);
            var mutations = BuildMapTemplateMutations(sourceFile, sourceMap, spec, bytes.Length, accessIssues);
            AddDuplicateMapTemplateMutationIssues(mutations, accessIssues);
            if (mutations.Count == 0)
            {
                accessIssues.Add("Map template spec did not request any supported mutations.");
            }

            if (accessIssues.Count > 0)
            {
                throw new InvalidOperationException($"Map template prototype validation failed: {string.Join("; ", accessIssues)}");
            }

            foreach (var mutation in mutations)
            {
                ApplyMapTemplateMutation(bytes, mutation);
            }

            var outputDirectory = Path.GetDirectoryName(outputFullPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            File.WriteAllBytes(outputFullPath, bytes);

            var outputFile = InspectFile(outputFullPath, Path.GetFileName(outputFullPath));
            var outputMap = BuildMapFacts(outputFile);
            accessIssues.AddRange(outputFile.AccessIssues.Select(issue => $"output: {issue}"));
            if (!outputFile.ParseStatus.Equals("dsonPartialDecoded", StringComparison.OrdinalIgnoreCase))
            {
                accessIssues.Add($"Output map did not parse as a DSON container: {outputFullPath}");
            }

            foreach (var mutation in mutations)
            {
                ValidateAppliedMapTemplateMutation(outputFile, outputMap, mutation, accessIssues);
            }

            var succeeded = accessIssues.Count == 0;
            var report = new MapTemplateMutationReport(
                1,
                generatedAt,
                sourceFullPath,
                specFullPath,
                outputFullPath,
                Path.GetFileNameWithoutExtension(sourceFullPath),
                spec.Name,
                succeeded,
                mutations.Select(mutation => mutation.Report).ToArray(),
                sourceMap,
                outputMap,
                accessIssues);

            var reportDirectory = Path.GetDirectoryName(reportFullPath);
            if (!string.IsNullOrWhiteSpace(reportDirectory))
            {
                Directory.CreateDirectory(reportDirectory);
            }

            File.WriteAllText(reportFullPath, JsonSerializer.Serialize(report, SessionJsonOptions), Encoding.UTF8);
            log.Info(
                $"event name=map.template_prototype_written report={Quote(reportFullPath)} output={Quote(outputFullPath)} " +
                $"source={Quote(sourceFullPath)} spec={Quote(specFullPath)} mutations={mutations.Count} succeeded={succeeded} issues={accessIssues.Count}");
            if (!succeeded)
            {
                throw new InvalidOperationException($"Map template prototype failed validation: {string.Join("; ", accessIssues)}");
            }

            return reportFullPath;
        }

        private static MapTemplateSpec ReadMapTemplateSpec(string specFullPath)
        {
            try
            {
                var spec = JsonSerializer.Deserialize<MapTemplateSpec>(
                        File.ReadAllText(specFullPath, Encoding.UTF8),
                        SessionJsonOptions)
                    ?? throw new InvalidOperationException($"Map template spec was empty: {specFullPath}");
                spec.DynamicTiles ??= [];
                spec.StaticDoors ??= [];
                spec.StaticTileDoors ??= [];
                return spec;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Map template spec is not valid JSON: {specFullPath}: {ex.Message}", ex);
            }
        }

        private static IReadOnlyList<PlannedMapTemplateMutation> BuildMapTemplateMutations(
            SaveStateFileReport sourceFile,
            SaveStateMapFacts sourceMap,
            MapTemplateSpec spec,
            int byteLength,
            List<string> accessIssues)
        {
            var mutations = new List<PlannedMapTemplateMutation>();

            if (!string.IsNullOrWhiteSpace(spec.EntranceAreaId))
            {
                AddAreaHashMutation(
                    sourceFile,
                    sourceMap,
                    "set_entrance_area_id",
                    "base_root.map.entrance_id",
                    spec.EntranceAreaId,
                    requireRoom: true,
                    byteLength,
                    mutations,
                    accessIssues);
            }

            if (!string.IsNullOrWhiteSpace(spec.FinalRoomId))
            {
                AddAreaHashMutation(
                    sourceFile,
                    sourceMap,
                    "set_final_room_id",
                    "base_root.map.final_room_id",
                    spec.FinalRoomId,
                    requireRoom: true,
                    byteLength,
                    mutations,
                    accessIssues);
            }

            foreach (var tile in spec.DynamicTiles)
            {
                AddDynamicTileMutations(sourceFile, sourceMap, tile, byteLength, mutations, accessIssues);
            }

            foreach (var door in spec.StaticDoors)
            {
                AddStaticDoorMutations(sourceFile, sourceMap, door, byteLength, mutations, accessIssues);
            }

            foreach (var door in spec.StaticTileDoors)
            {
                AddStaticTileDoorMutations(sourceFile, sourceMap, door, byteLength, mutations, accessIssues);
            }

            return mutations;
        }

        private static void AddDuplicateMapTemplateMutationIssues(
            IReadOnlyList<PlannedMapTemplateMutation> mutations,
            List<string> accessIssues)
        {
            foreach (var group in mutations.GroupBy(mutation => mutation.Path, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            {
                accessIssues.Add($"Map template spec contains duplicate mutations for path: {group.Key}");
            }
        }

        private static void AddAreaHashMutation(
            SaveStateFileReport sourceFile,
            SaveStateMapFacts sourceMap,
            string mutation,
            string scalarPath,
            string areaId,
            bool requireRoom,
            int byteLength,
            List<PlannedMapTemplateMutation> mutations,
            List<string> accessIssues)
        {
            var area = sourceMap.Areas.FirstOrDefault(item => item.AreaId.Equals(areaId, StringComparison.OrdinalIgnoreCase));
            if (area is null)
            {
                accessIssues.Add($"{mutation} target area was not found: {areaId}");
                return;
            }

            if (requireRoom && !area.InferredRole.Equals("room", StringComparison.OrdinalIgnoreCase))
            {
                accessIssues.Add($"{mutation} target area is not a room: {areaId}");
                return;
            }

            if (!area.AreaHash.HasValue)
            {
                accessIssues.Add($"{mutation} target area has no hash: {areaId}");
                return;
            }

            AddInt32Mutation(
                sourceFile,
                mutation,
                scalarPath,
                area.AreaHash.Value,
                areaId,
                null,
                byteLength,
                mutations,
                accessIssues);
        }

        private static void AddDynamicTileMutations(
            SaveStateFileReport sourceFile,
            SaveStateMapFacts sourceMap,
            MapTemplateDynamicTileSpec tile,
            int byteLength,
            List<PlannedMapTemplateMutation> mutations,
            List<string> accessIssues)
        {
            if (string.IsNullOrWhiteSpace(tile.AreaId))
            {
                accessIssues.Add("dynamicTiles entry requires areaId.");
                return;
            }

            if (string.IsNullOrWhiteSpace(tile.TileId))
            {
                accessIssues.Add($"dynamicTiles entry for area={tile.AreaId} requires tileId.");
                return;
            }

            var tileId = NormalizeMapTileId(tile.TileId);
            var area = sourceMap.DynamicAreas.FirstOrDefault(item => item.AreaId.Equals(tile.AreaId, StringComparison.OrdinalIgnoreCase));
            if (area is null)
            {
                accessIssues.Add($"dynamic tile area was not found: {tile.AreaId}");
                return;
            }

            var tilePath = $"base_root.map.static_dynamic.areas.{area.AreaId}.tiles.{tileId}";
            AddOptionalDynamicTileIntMutation(sourceFile, tile.Content, "set_dynamic_tile_content", $"{tilePath}.content", area.AreaId, tileId, byteLength, mutations, accessIssues);
            AddOptionalDynamicTileIntMutation(sourceFile, tile.Light, "set_dynamic_tile_light", $"{tilePath}.light", area.AreaId, tileId, byteLength, mutations, accessIssues);
            AddOptionalDynamicTileIntMutation(sourceFile, tile.Knowledge, "set_dynamic_tile_knowledge", $"{tilePath}.knowledge", area.AreaId, tileId, byteLength, mutations, accessIssues);
            AddOptionalDynamicTileIntMutation(sourceFile, tile.MashIndex, "set_dynamic_tile_mash_index", $"{tilePath}.mash_index", area.AreaId, tileId, byteLength, mutations, accessIssues);
            AddOptionalDynamicTileIntMutation(sourceFile, tile.MashType, "set_dynamic_tile_mash_type", $"{tilePath}.mash_type", area.AreaId, tileId, byteLength, mutations, accessIssues);
            AddOptionalDynamicTileIntMutation(sourceFile, tile.CurioPropHash, "set_dynamic_tile_curio_prop", $"{tilePath}.curio_prop", area.AreaId, tileId, byteLength, mutations, accessIssues);
            AddOptionalDynamicTileIntMutation(sourceFile, tile.TrapHash, "set_dynamic_tile_trap", $"{tilePath}.trap", area.AreaId, tileId, byteLength, mutations, accessIssues);
            if (tile.CritScout.HasValue)
            {
                AddBoolMutation(
                    sourceFile,
                    "set_dynamic_tile_crit_scout",
                    $"{tilePath}.crit_scout",
                    tile.CritScout.Value,
                    area.AreaId,
                    tileId,
                    byteLength,
                    mutations,
                    accessIssues);
            }
        }

        private static void AddStaticDoorMutations(
            SaveStateFileReport sourceFile,
            SaveStateMapFacts sourceMap,
            MapTemplateStaticDoorSpec door,
            int byteLength,
            List<PlannedMapTemplateMutation> mutations,
            List<string> accessIssues)
        {
            if (string.IsNullOrWhiteSpace(door.AreaId))
            {
                accessIssues.Add("staticDoors entry requires areaId.");
                return;
            }

            var area = sourceMap.Areas.FirstOrDefault(item => item.AreaId.Equals(door.AreaId, StringComparison.OrdinalIgnoreCase));
            if (area is null)
            {
                accessIssues.Add($"static door area was not found: {door.AreaId}");
                return;
            }

            if (!TryNormalizeDoorSlot(door.DoorSlot, out var doorSlot, out var doorIssue))
            {
                accessIssues.Add($"static door for area={area.AreaId} has invalid doorSlot: {doorIssue}");
                return;
            }

            var doorPath = $"base_root.areas.{area.AreaId}.{doorSlot}";
            AddStaticDoorCommonMutations(
                sourceFile,
                sourceMap,
                doorPath,
                "static door",
                area.AreaId,
                null,
                door.TargetAreaId,
                door.TargetTileIndex,
                door.TargetTileId,
                door.DoorType,
                door.Implied,
                byteLength,
                mutations,
                accessIssues);
        }

        private static void AddStaticTileDoorMutations(
            SaveStateFileReport sourceFile,
            SaveStateMapFacts sourceMap,
            MapTemplateStaticTileDoorSpec door,
            int byteLength,
            List<PlannedMapTemplateMutation> mutations,
            List<string> accessIssues)
        {
            if (string.IsNullOrWhiteSpace(door.AreaId))
            {
                accessIssues.Add("staticTileDoors entry requires areaId.");
                return;
            }

            if (string.IsNullOrWhiteSpace(door.TileId))
            {
                accessIssues.Add($"staticTileDoors entry for area={door.AreaId} requires tileId.");
                return;
            }

            var tileId = NormalizeMapTileId(door.TileId);
            var area = sourceMap.Areas.FirstOrDefault(item => item.AreaId.Equals(door.AreaId, StringComparison.OrdinalIgnoreCase));
            if (area is null)
            {
                accessIssues.Add($"static tile door area was not found: {door.AreaId}");
                return;
            }

            var doorPath = $"base_root.areas.{area.AreaId}.tiles.{tileId}.door_to";
            AddStaticDoorCommonMutations(
                sourceFile,
                sourceMap,
                doorPath,
                "static tile door",
                area.AreaId,
                tileId,
                door.TargetAreaId,
                door.TargetTileIndex,
                door.TargetTileId,
                door.DoorType,
                door.Implied,
                byteLength,
                mutations,
                accessIssues);
        }

        private static void AddStaticDoorCommonMutations(
            SaveStateFileReport sourceFile,
            SaveStateMapFacts sourceMap,
            string doorPath,
            string subject,
            string areaId,
            string? tileId,
            string? targetAreaId,
            int? targetTileIndex,
            string? targetTileId,
            int? doorType,
            bool? implied,
            int byteLength,
            List<PlannedMapTemplateMutation> mutations,
            List<string> accessIssues)
        {
            if (!string.IsNullOrWhiteSpace(targetAreaId))
            {
                var targetArea = sourceMap.Areas.FirstOrDefault(item => item.AreaId.Equals(targetAreaId, StringComparison.OrdinalIgnoreCase));
                if (targetArea is null)
                {
                    accessIssues.Add($"{subject} target area was not found: {targetAreaId}");
                }
                else if (!targetArea.AreaHash.HasValue)
                {
                    accessIssues.Add($"{subject} target area has no hash: {targetAreaId}");
                }
                else
                {
                    AddInt32Mutation(sourceFile, $"set_{subject.Replace(' ', '_')}_area_to", $"{doorPath}.area_to", targetArea.AreaHash.Value, areaId, tileId, byteLength, mutations, accessIssues);
                }
            }

            if (TryResolveTargetTileIndex(targetTileIndex, targetTileId, $"{subject} area={areaId} tile={tileId ?? ""}", accessIssues, out var resolvedTileIndex))
            {
                AddInt32Mutation(sourceFile, $"set_{subject.Replace(' ', '_')}_tile_to", $"{doorPath}.tile_to", resolvedTileIndex, areaId, tileId, byteLength, mutations, accessIssues);
            }

            AddOptionalDynamicTileIntMutation(sourceFile, doorType, $"set_{subject.Replace(' ', '_')}_type", $"{doorPath}.type", areaId, tileId ?? "", byteLength, mutations, accessIssues);
            if (implied.HasValue)
            {
                AddBoolMutation(sourceFile, $"set_{subject.Replace(' ', '_')}_implied", $"{doorPath}.implied", implied.Value, areaId, tileId, byteLength, mutations, accessIssues);
            }
        }

        private static void AddOptionalDynamicTileIntMutation(
            SaveStateFileReport sourceFile,
            int? value,
            string mutation,
            string scalarPath,
            string areaId,
            string tileId,
            int byteLength,
            List<PlannedMapTemplateMutation> mutations,
            List<string> accessIssues)
        {
            if (!value.HasValue)
            {
                return;
            }

            AddInt32Mutation(sourceFile, mutation, scalarPath, value.Value, areaId, tileId, byteLength, mutations, accessIssues);
        }

        private static void AddInt32Mutation(
            SaveStateFileReport sourceFile,
            string mutation,
            string scalarPath,
            int value,
            string? areaId,
            string? tileId,
            int byteLength,
            List<PlannedMapTemplateMutation> mutations,
            List<string> accessIssues)
        {
            var scalar = FindMapTemplateScalar(sourceFile, scalarPath);
            if (scalar is null)
            {
                accessIssues.Add($"{mutation} target scalar was not found: {scalarPath}");
                return;
            }

            if (!scalar.Type.Equals("int32", StringComparison.OrdinalIgnoreCase))
            {
                accessIssues.Add($"{mutation} target scalar is not int32: {scalarPath} type={scalar.Type}");
                return;
            }

            var valueOffset = GetDsonScalarValueOffset(scalar);
            if (valueOffset < 0 || valueOffset + sizeof(int) > byteLength)
            {
                accessIssues.Add($"{mutation} value offset is outside the source file: {scalarPath} offset={valueOffset}");
                return;
            }

            mutations.Add(new PlannedMapTemplateMutation(
                mutation,
                scalarPath,
                "int32",
                areaId,
                tileId,
                scalar.Value,
                value.ToString(CultureInfo.InvariantCulture),
                value,
                null,
                scalar.Offset,
                valueOffset));
        }

        private static void AddBoolMutation(
            SaveStateFileReport sourceFile,
            string mutation,
            string scalarPath,
            bool value,
            string? areaId,
            string? tileId,
            int byteLength,
            List<PlannedMapTemplateMutation> mutations,
            List<string> accessIssues)
        {
            var scalar = FindMapTemplateScalar(sourceFile, scalarPath);
            if (scalar is null)
            {
                accessIssues.Add($"{mutation} target scalar was not found: {scalarPath}");
                return;
            }

            if (!scalar.Type.Equals("bool", StringComparison.OrdinalIgnoreCase))
            {
                accessIssues.Add($"{mutation} target scalar is not bool: {scalarPath} type={scalar.Type}");
                return;
            }

            var valueOffset = GetDsonScalarSingleByteValueOffset(scalar);
            if (valueOffset < 0 || valueOffset >= byteLength)
            {
                accessIssues.Add($"{mutation} value offset is outside the source file: {scalarPath} offset={valueOffset}");
                return;
            }

            mutations.Add(new PlannedMapTemplateMutation(
                mutation,
                scalarPath,
                "bool",
                areaId,
                tileId,
                scalar.Value,
                value ? "true" : "false",
                null,
                value,
                scalar.Offset,
                valueOffset));
        }

        private static void ApplyMapTemplateMutation(byte[] bytes, PlannedMapTemplateMutation mutation)
        {
            if (mutation.Int32Value.HasValue)
            {
                WriteInt32LittleEndian(bytes, mutation.ValueOffset, mutation.Int32Value.Value);
                return;
            }

            if (mutation.BoolValue.HasValue)
            {
                bytes[mutation.ValueOffset] = mutation.BoolValue.Value ? (byte)1 : (byte)0;
            }
        }

        private static void ValidateAppliedMapTemplateMutation(
            SaveStateFileReport outputFile,
            SaveStateMapFacts outputMap,
            PlannedMapTemplateMutation mutation,
            List<string> accessIssues)
        {
            if (mutation.Mutation.Equals("set_entrance_area_id", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(outputMap.EntranceAreaId, mutation.AreaId, StringComparison.OrdinalIgnoreCase))
                {
                    accessIssues.Add($"Output entrance area mismatch: expected={mutation.AreaId} actual={outputMap.EntranceAreaId ?? ""}");
                }

                return;
            }

            if (mutation.Mutation.Equals("set_final_room_id", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(outputMap.FinalRoomId, mutation.AreaId, StringComparison.OrdinalIgnoreCase))
                {
                    accessIssues.Add($"Output final room mismatch: expected={mutation.AreaId} actual={outputMap.FinalRoomId ?? ""}");
                }

                return;
            }

            var scalar = FindMapTemplateScalar(outputFile, mutation.Path);
            if (scalar is null)
            {
                accessIssues.Add($"Output scalar was not found after mutation: {mutation.Path}");
                return;
            }

            if (!string.Equals(scalar.Value, mutation.NewValue, StringComparison.OrdinalIgnoreCase))
            {
                accessIssues.Add($"Output scalar mismatch: path={mutation.Path} expected={mutation.NewValue} actual={scalar.Value ?? ""}");
            }
        }

        private static int GetDsonScalarSingleByteValueOffset(SaveStateDsonScalar scalar)
        {
            return scalar.Offset + Encoding.UTF8.GetByteCount(scalar.Name) + 1;
        }

        private static SaveStateDsonScalar? FindMapTemplateScalar(SaveStateFileReport file, string path)
        {
            return FindDsonScalar(file, path) ?? FindMapStaticSaveScalar(file, path);
        }

        private static SaveStateDsonScalar? FindMapStaticSaveScalar(SaveStateFileReport file, string path)
        {
            var staticSave = FindDsonScalar(file, "base_root.map.static_dynamic.static_save")?.EmbeddedDson;
            return staticSave?.AllScalars.FirstOrDefault(scalar => scalar.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryNormalizeDoorSlot(string? doorSlot, out string normalized, out string issue)
        {
            normalized = string.Empty;
            issue = string.Empty;
            if (string.IsNullOrWhiteSpace(doorSlot))
            {
                issue = "missing doorSlot";
                return false;
            }

            var value = doorSlot.Trim();
            if (value.StartsWith("door", StringComparison.OrdinalIgnoreCase))
            {
                value = value[4..];
            }

            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index is < 0 or > 7)
            {
                issue = doorSlot;
                return false;
            }

            normalized = "door" + index.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static bool TryResolveTargetTileIndex(
            int? targetTileIndex,
            string? targetTileId,
            string subject,
            List<string> accessIssues,
            out int resolvedTileIndex)
        {
            resolvedTileIndex = 0;
            if (!targetTileIndex.HasValue && string.IsNullOrWhiteSpace(targetTileId))
            {
                return false;
            }

            if (targetTileIndex.HasValue && !string.IsNullOrWhiteSpace(targetTileId))
            {
                if (!TryParseTileIndex(targetTileId, out var parsedTileIdIndex))
                {
                    accessIssues.Add($"{subject} targetTileId is invalid: {targetTileId}");
                    return false;
                }

                if (parsedTileIdIndex != targetTileIndex.Value)
                {
                    accessIssues.Add($"{subject} targetTileIndex and targetTileId disagree: targetTileIndex={targetTileIndex.Value} targetTileId={targetTileId}");
                    return false;
                }
            }

            if (targetTileIndex.HasValue)
            {
                resolvedTileIndex = targetTileIndex.Value;
                return true;
            }

            if (!TryParseTileIndex(targetTileId, out var parsedIndex))
            {
                accessIssues.Add($"{subject} targetTileId is invalid: {targetTileId}");
                return false;
            }

            resolvedTileIndex = parsedIndex;
            return true;
        }

        private static bool TryParseTileIndex(string? tileId, out int index)
        {
            index = 0;
            if (string.IsNullOrWhiteSpace(tileId))
            {
                return false;
            }

            var value = tileId.Trim();
            if (value.StartsWith("tile", StringComparison.OrdinalIgnoreCase))
            {
                value = value[4..];
            }

            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) && index >= 0;
        }

        private static string NormalizeMapTileId(string tileId)
        {
            return tileId.StartsWith("tile", StringComparison.OrdinalIgnoreCase)
                ? tileId
                : "tile" + tileId;
        }

        private sealed class MapTemplateSpec
        {
            public int Version { get; set; } = 1;
            public string? Name { get; set; }
            public string? EntranceAreaId { get; set; }
            public string? FinalRoomId { get; set; }
            public List<MapTemplateDynamicTileSpec> DynamicTiles { get; set; } = [];
            public List<MapTemplateStaticDoorSpec> StaticDoors { get; set; } = [];
            public List<MapTemplateStaticTileDoorSpec> StaticTileDoors { get; set; } = [];
        }

        private sealed class MapTemplateDynamicTileSpec
        {
            public string? AreaId { get; set; }
            public string? TileId { get; set; }
            public int? Content { get; set; }
            public int? Light { get; set; }
            public int? Knowledge { get; set; }
            public int? MashIndex { get; set; }
            public int? MashType { get; set; }
            public int? CurioPropHash { get; set; }
            public int? TrapHash { get; set; }
            public bool? CritScout { get; set; }
        }

        private sealed class MapTemplateStaticDoorSpec
        {
            public string? AreaId { get; set; }
            public string? DoorSlot { get; set; }
            public string? TargetAreaId { get; set; }
            public int? TargetTileIndex { get; set; }
            public string? TargetTileId { get; set; }
            public int? DoorType { get; set; }
            public bool? Implied { get; set; }
        }

        private sealed class MapTemplateStaticTileDoorSpec
        {
            public string? AreaId { get; set; }
            public string? TileId { get; set; }
            public string? TargetAreaId { get; set; }
            public int? TargetTileIndex { get; set; }
            public string? TargetTileId { get; set; }
            public int? DoorType { get; set; }
            public bool? Implied { get; set; }
        }

        private sealed record PlannedMapTemplateMutation(
            string Mutation,
            string Path,
            string ValueKind,
            string? AreaId,
            string? TileId,
            string? OldValue,
            string NewValue,
            int? Int32Value,
            bool? BoolValue,
            int FieldOffset,
            int ValueOffset)
        {
            public MapTemplateMutationEntry Report { get; } = new(
                Mutation,
                Path,
                ValueKind,
                AreaId,
                TileId,
                OldValue,
                NewValue,
                FieldOffset,
                ValueOffset);
        }

        private sealed record MapTemplateMutationReport(
            int Version,
            DateTimeOffset GeneratedAt,
            string SourceMapFilePath,
            string SpecFilePath,
            string OutputMapFilePath,
            string SourceMapName,
            string? SpecName,
            bool Succeeded,
            IReadOnlyList<MapTemplateMutationEntry> Mutations,
            SaveStateMapFacts SourceMap,
            SaveStateMapFacts OutputMap,
            IReadOnlyList<string> AccessIssues);

        private sealed record MapTemplateMutationEntry(
            string Mutation,
            string Path,
            string ValueKind,
            string? AreaId,
            string? TileId,
            string? OldValue,
            string NewValue,
            int FieldOffset,
            int ValueOffset);
    }
}
