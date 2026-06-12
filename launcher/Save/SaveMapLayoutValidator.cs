namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        public static string WriteMapLayoutTemplateValidationReport(
            string sourceMapFilePath,
            MapLayoutTemplateRule template,
            string reportOutputPath,
            LauncherLog log)
        {
            var sourceFullPath = Path.GetFullPath(sourceMapFilePath);
            var reportFullPath = Path.GetFullPath(reportOutputPath);
            var generatedAt = DateTimeOffset.Now;
            var issues = new List<string>();
            var warnings = new List<string>();

            if (!File.Exists(sourceFullPath))
            {
                throw new FileNotFoundException("Source map file was not found.", sourceFullPath);
            }

            var sourceFile = InspectFile(sourceFullPath, Path.GetFileName(sourceFullPath));
            var sourceMap = BuildMapFacts(sourceFile);
            issues.AddRange(sourceFile.AccessIssues);
            if (!sourceFile.ParseStatus.Equals("dsonPartialDecoded", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"Source map did not parse as a DSON container: {sourceFullPath}");
            }

            if (!sourceMap.HasStaticSave)
            {
                issues.Add("Source map has no decoded base_root.map.static_dynamic.static_save payload.");
            }

            var layoutFacts = ValidateMapLayoutTemplate(template, sourceMap, issues, warnings);
            var succeeded = issues.Count == 0;
            var report = new MapLayoutTemplateValidationReport(
                1,
                generatedAt,
                sourceFullPath,
                template.Target,
                template.Id,
                succeeded,
                false,
                "validateOnly",
                sourceMap,
                layoutFacts,
                issues,
                warnings);

            var reportDirectory = Path.GetDirectoryName(reportFullPath);
            if (!string.IsNullOrWhiteSpace(reportDirectory))
            {
                Directory.CreateDirectory(reportDirectory);
            }

            File.WriteAllText(reportFullPath, JsonSerializer.Serialize(report, SessionJsonOptions), Encoding.UTF8);
            log.Info(
                $"event name=map.layout_template_validation_written report={Quote(reportFullPath)} " +
                $"source={Quote(sourceFullPath)} target={Quote(template.Target)} id={Quote(template.Id)} " +
                $"succeeded={succeeded} issues={issues.Count} warnings={warnings.Count} compileReady=0");
            if (!succeeded)
            {
                throw new InvalidOperationException($"Map layout template validation failed: {string.Join("; ", issues)}");
            }

            return reportFullPath;
        }

        private static MapLayoutTemplateFacts ValidateMapLayoutTemplate(
            MapLayoutTemplateRule template,
            SaveStateMapFacts sourceMap,
            List<string> issues,
            List<string> warnings)
        {
            var layout = template.Layout ?? new MapLayoutDefinition();
            var sourceAreas = sourceMap.Areas.ToDictionary(area => area.AreaId, StringComparer.OrdinalIgnoreCase);
            var nodes = new Dictionary<string, MapLayoutNodeFacts>(StringComparer.OrdinalIgnoreCase);
            var duplicateNodeIds = new List<string>();

            foreach (var room in layout.Rooms ?? [])
            {
                var node = BuildMapLayoutNodeFacts("room", room.Id, room.TemplateAreaId, sourceAreas, issues);
                if (!AddLayoutNode(nodes, node, duplicateNodeIds))
                {
                    continue;
                }

                if (room.Position is not null && room.Position.Length > 0 && room.Position.Length != 2)
                {
                    issues.Add($"room {FormatLayoutId(room.Id)} position must contain exactly two numbers.");
                }
            }

            foreach (var corridor in layout.Corridors ?? [])
            {
                var node = BuildMapLayoutNodeFacts("corridor", corridor.Id, corridor.TemplateAreaId, sourceAreas, issues);
                if (!AddLayoutNode(nodes, node, duplicateNodeIds))
                {
                    continue;
                }

                foreach (var (point, index) in (corridor.Route ?? []).Select((point, index) => (point, index)))
                {
                    if (point.Length != 2)
                    {
                        issues.Add($"corridor {FormatLayoutId(corridor.Id)} route[{index}] must contain exactly two numbers.");
                    }
                }
            }

            foreach (var duplicate in duplicateNodeIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                issues.Add($"layout node id is duplicated: {duplicate}");
            }

            AddDuplicateRoomPositionIssues(layout.Rooms ?? [], issues);

            if (string.IsNullOrWhiteSpace(layout.Entrance))
            {
                issues.Add("layout entrance is required.");
            }
            else if (!nodes.ContainsKey(layout.Entrance))
            {
                issues.Add($"layout entrance references unknown node: {layout.Entrance}");
            }
            else if (!nodes[layout.Entrance].Kind.Equals("room", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"layout entrance must reference a room node: {layout.Entrance}");
            }

            if (!string.IsNullOrWhiteSpace(layout.FinalRoom))
            {
                if (!nodes.ContainsKey(layout.FinalRoom))
                {
                    issues.Add($"layout finalRoom references unknown node: {layout.FinalRoom}");
                }
                else if (!nodes[layout.FinalRoom].Kind.Equals("room", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add($"layout finalRoom must reference a room node: {layout.FinalRoom}");
                }
            }

            var adjacency = nodes.ToDictionary(
                pair => pair.Key,
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            var linkFacts = BuildMapLayoutLinkFacts(layout.Links ?? [], nodes, adjacency, issues);
            var reachableNodeIds = string.IsNullOrWhiteSpace(layout.Entrance) || !adjacency.ContainsKey(layout.Entrance)
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : TraverseLayoutGraph(layout.Entrance, adjacency);
            var unreachableNodeIds = nodes.Keys
                .Where(id => !reachableNodeIds.Contains(id))
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (nodes.Count > 0 && reachableNodeIds.Count > 0 && unreachableNodeIds.Length > 0)
            {
                issues.Add($"layout has nodes not reachable from entrance {layout.Entrance}: {string.Join(",", unreachableNodeIds)}");
            }

            var entranceCanReachFinal = !string.IsNullOrWhiteSpace(layout.FinalRoom) &&
                reachableNodeIds.Contains(layout.FinalRoom);
            if (!string.IsNullOrWhiteSpace(layout.FinalRoom) &&
                nodes.ContainsKey(layout.FinalRoom) &&
                !entranceCanReachFinal)
            {
                issues.Add($"layout finalRoom {layout.FinalRoom} is not reachable from entrance {layout.Entrance}.");
            }

            var encounterIds = ValidateMapLayoutEncounters(template.Encounters ?? [], issues);
            var tileFacts = ValidateMapLayoutTiles(template.Tiles ?? [], nodes, encounterIds, issues, warnings);

            return new MapLayoutTemplateFacts(
                layout.Entrance,
                string.IsNullOrWhiteSpace(layout.FinalRoom) ? null : layout.FinalRoom,
                nodes.Count,
                nodes.Values.Count(node => node.Kind.Equals("room", StringComparison.OrdinalIgnoreCase)),
                nodes.Values.Count(node => node.Kind.Equals("corridor", StringComparison.OrdinalIgnoreCase)),
                linkFacts.Count,
                tileFacts.Count,
                (template.Encounters ?? []).Length,
                reachableNodeIds.Count,
                reachableNodeIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
                unreachableNodeIds,
                entranceCanReachFinal,
                nodes.Values.OrderBy(node => node.Id, StringComparer.OrdinalIgnoreCase).ToArray(),
                linkFacts,
                tileFacts);
        }

        private static MapLayoutNodeFacts BuildMapLayoutNodeFacts(
            string kind,
            string id,
            string templateAreaId,
            IReadOnlyDictionary<string, SaveStateMapAreaFacts> sourceAreas,
            List<string> issues)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                issues.Add($"layout {kind} requires id.");
            }

            if (string.IsNullOrWhiteSpace(templateAreaId))
            {
                issues.Add($"layout {kind} {FormatLayoutId(id)} requires templateAreaId.");
            }

            SaveStateMapAreaFacts? area = null;
            var templateFound = !string.IsNullOrWhiteSpace(templateAreaId) &&
                sourceAreas.TryGetValue(templateAreaId, out area);
            if (!templateFound)
            {
                issues.Add($"layout {kind} {FormatLayoutId(id)} references unknown templateAreaId: {FormatLayoutId(templateAreaId)}");
                return new MapLayoutNodeFacts(
                    id,
                    kind,
                    templateAreaId,
                    false,
                    null,
                    0,
                    0,
                    0,
                    []);
            }

            if (!area!.InferredRole.Equals(kind, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(
                    $"layout {kind} {FormatLayoutId(id)} references templateAreaId {templateAreaId} " +
                    $"with role {area.InferredRole}.");
            }

            return new MapLayoutNodeFacts(
                id,
                kind,
                templateAreaId,
                true,
                area.InferredRole,
                area.TileCount,
                area.DoorSlotCount,
                area.ActiveDoorCount,
                area.TileSamples.Select(tile => tile.TileId).ToArray());
        }

        private static bool AddLayoutNode(
            Dictionary<string, MapLayoutNodeFacts> nodes,
            MapLayoutNodeFacts node,
            List<string> duplicateNodeIds)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                return false;
            }

            if (nodes.ContainsKey(node.Id))
            {
                duplicateNodeIds.Add(node.Id);
                return false;
            }

            nodes.Add(node.Id, node);
            return true;
        }

        private static void AddDuplicateRoomPositionIssues(
            IReadOnlyList<MapLayoutRoomRule> rooms,
            List<string> issues)
        {
            foreach (var group in rooms
                         .Where(room => !string.IsNullOrWhiteSpace(room.Id) && room.Position is { Length: 2 })
                         .GroupBy(room => FormatCoordinatePair(room.Position), StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() > 1))
            {
                issues.Add($"multiple rooms share position {group.Key}: {string.Join(",", group.Select(room => room.Id))}");
            }
        }

        private static IReadOnlyList<MapLayoutLinkFacts> BuildMapLayoutLinkFacts(
            IReadOnlyList<MapLayoutLinkRule> links,
            IReadOnlyDictionary<string, MapLayoutNodeFacts> nodes,
            IReadOnlyDictionary<string, HashSet<string>> adjacency,
            List<string> issues)
        {
            var facts = new List<MapLayoutLinkFacts>();
            for (var i = 0; i < links.Count; i++)
            {
                var link = links[i];
                MapLayoutNodeFacts? fromNode = null;
                MapLayoutNodeFacts? toNode = null;
                var fromFound = !string.IsNullOrWhiteSpace(link.From) && nodes.TryGetValue(link.From, out fromNode);
                var toFound = !string.IsNullOrWhiteSpace(link.To) && nodes.TryGetValue(link.To, out toNode);
                if (!fromFound)
                {
                    issues.Add($"layout link[{i}] references unknown from node: {FormatLayoutId(link.From)}");
                }

                if (!toFound)
                {
                    issues.Add($"layout link[{i}] references unknown to node: {FormatLayoutId(link.To)}");
                }

                if (fromFound && toFound)
                {
                    adjacency[link.From].Add(link.To);
                    adjacency[link.To].Add(link.From);
                    ValidateMapLayoutLinkTile(i, link, fromNode!, toNode!, issues);
                }

                facts.Add(new MapLayoutLinkFacts(
                    link.From,
                    link.To,
                    link.Tile,
                    string.IsNullOrWhiteSpace(link.TileId) ? null : link.TileId,
                    fromFound,
                    toFound));
            }

            return facts;
        }

        private static void ValidateMapLayoutLinkTile(
            int linkIndex,
            MapLayoutLinkRule link,
            MapLayoutNodeFacts fromNode,
            MapLayoutNodeFacts toNode,
            List<string> issues)
        {
            if (!link.Tile.HasValue && string.IsNullOrWhiteSpace(link.TileId))
            {
                return;
            }

            var tileNode = fromNode.Kind.Equals("corridor", StringComparison.OrdinalIgnoreCase)
                ? fromNode
                : toNode.Kind.Equals("corridor", StringComparison.OrdinalIgnoreCase)
                    ? toNode
                    : fromNode;
            ValidateTileReference($"layout link[{linkIndex}]", tileNode, link.Tile, link.TileId, issues);
        }

        private static HashSet<string> TraverseLayoutGraph(
            string startNodeId,
            IReadOnlyDictionary<string, HashSet<string>> adjacency)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(startNodeId);
            visited.Add(startNodeId);
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

        private static HashSet<string> ValidateMapLayoutEncounters(
            IReadOnlyList<MapLayoutEncounterRule> encounters,
            List<string> issues)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var encounter in encounters)
            {
                if (string.IsNullOrWhiteSpace(encounter.Id))
                {
                    issues.Add("layout encounter requires id.");
                    continue;
                }

                if (!ids.Add(encounter.Id))
                {
                    issues.Add($"layout encounter id is duplicated: {encounter.Id}");
                }

                if (string.IsNullOrWhiteSpace(encounter.Mash))
                {
                    issues.Add($"layout encounter {encounter.Id} requires mash.");
                }
            }

            return ids;
        }

        private static IReadOnlyList<MapLayoutTileFacts> ValidateMapLayoutTiles(
            IReadOnlyList<MapLayoutTileRule> tiles,
            IReadOnlyDictionary<string, MapLayoutNodeFacts> nodes,
            IReadOnlySet<string> encounterIds,
            List<string> issues,
            List<string> warnings)
        {
            var facts = new List<MapLayoutTileFacts>();
            for (var i = 0; i < tiles.Count; i++)
            {
                var tile = tiles[i];
                MapLayoutNodeFacts? node = null;
                var areaFound = !string.IsNullOrWhiteSpace(tile.Area) && nodes.TryGetValue(tile.Area, out node);
                if (!areaFound)
                {
                    issues.Add($"layout tile[{i}] references unknown area node: {FormatLayoutId(tile.Area)}");
                }
                else
                {
                    ValidateTileReference($"layout tile[{i}]", node!, tile.Tile, tile.TileId, issues);
                }

                if (!string.IsNullOrWhiteSpace(tile.Encounter) && !encounterIds.Contains(tile.Encounter))
                {
                    issues.Add($"layout tile[{i}] references unknown encounter: {tile.Encounter}");
                }
                else if (!string.IsNullOrWhiteSpace(tile.Encounter))
                {
                    warnings.Add($"layout tile[{i}] encounter {tile.Encounter} is reference-checked only; encounter materialization is not implemented.");
                }

                facts.Add(new MapLayoutTileFacts(
                    tile.Area,
                    tile.Tile,
                    string.IsNullOrWhiteSpace(tile.TileId) ? null : tile.TileId,
                    string.IsNullOrWhiteSpace(tile.Content) ? null : tile.Content,
                    string.IsNullOrWhiteSpace(tile.Encounter) ? null : tile.Encounter,
                    areaFound));
            }

            return facts;
        }

        private static void ValidateTileReference(
            string context,
            MapLayoutNodeFacts node,
            int? tile,
            string tileId,
            List<string> issues)
        {
            if (tile.HasValue)
            {
                if (tile.Value < 0 || tile.Value >= node.TemplateTileCount)
                {
                    issues.Add(
                        $"{context} references tile index {tile.Value} on node {node.Id}, " +
                        $"outside template tile count {node.TemplateTileCount}.");
                }
            }

            var hasTileId = !string.IsNullOrWhiteSpace(tileId);
            var parsedTile = -1;
            if (hasTileId && !TryParseTileId(tileId, out parsedTile))
            {
                issues.Add($"{context} references unsupported tileId format: {tileId}");
            }
            else if (hasTileId && !node.TemplateTileIds.Contains(tileId, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(
                    $"{context} references tileId {tileId} on node {node.Id}, " +
                    "but the template area has no such tile.");
            }
        }

        private static bool TryParseTileId(string tileId, out int tileIndex)
        {
            tileIndex = -1;
            if (!tileId.StartsWith("tile", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return int.TryParse(tileId[4..], NumberStyles.Integer, CultureInfo.InvariantCulture, out tileIndex) && tileIndex >= 0;
        }

        private static string FormatCoordinatePair(IReadOnlyList<double> position)
        {
            return string.Join(",", position.Select(value => value.ToString("0.###", CultureInfo.InvariantCulture)));
        }

        private static string FormatLayoutId(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<empty>" : value;
        }

        private sealed record MapLayoutTemplateValidationReport(
            int Version,
            DateTimeOffset GeneratedAt,
            string SourceMapFilePath,
            string Target,
            string Id,
            bool Succeeded,
            bool CompileReady,
            string Phase,
            SaveStateMapFacts SourceMap,
            MapLayoutTemplateFacts Layout,
            IReadOnlyList<string> Issues,
            IReadOnlyList<string> Warnings);

        private sealed record MapLayoutTemplateFacts(
            string Entrance,
            string? FinalRoom,
            int NodeCount,
            int RoomCount,
            int CorridorCount,
            int LinkCount,
            int TileRuleCount,
            int EncounterCount,
            int ReachableNodeCount,
            IReadOnlyList<string> ReachableNodeIds,
            IReadOnlyList<string> UnreachableNodeIds,
            bool EntranceCanReachFinal,
            IReadOnlyList<MapLayoutNodeFacts> Nodes,
            IReadOnlyList<MapLayoutLinkFacts> Links,
            IReadOnlyList<MapLayoutTileFacts> Tiles);

        private sealed record MapLayoutNodeFacts(
            string Id,
            string Kind,
            string TemplateAreaId,
            bool TemplateAreaFound,
            string? TemplateAreaRole,
            int TemplateTileCount,
            int TemplateDoorSlotCount,
            int TemplateActiveDoorCount,
            IReadOnlyList<string> TemplateTileIds);

        private sealed record MapLayoutLinkFacts(
            string From,
            string To,
            int? Tile,
            string? TileId,
            bool FromFound,
            bool ToFound);

        private sealed record MapLayoutTileFacts(
            string Area,
            int? Tile,
            string? TileId,
            string? Content,
            string? Encounter,
            bool AreaFound);
    }
}
