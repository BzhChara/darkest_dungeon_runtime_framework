namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static readonly string[] CandidateFiles =
        [
            "persist.game.json",
            "persist.town.json",
            "persist.roster.json",
            "persist.progression.json",
            "persist.upgrades.json",
            "persist.estate.json"
        ];

        private static readonly string[] KnownMarkers =
        [
            "base_root",
            "version",
            "totalelapsed",
            "raiddungeon",
            "estatename",
            "game_mode",
            "date_time",
            "buildings",
            "heroes",
            "hero_file_data",
            "roster.status",
            "heroClass",
            "dungeon",
            "completed_plot_quests_data",
            "total_quests_finished",
            "last_quest_played_id",
            "purchases",
            "tree_id",
            "requirement_code",
            "is_purchased",
            "wallet",
            "amount",
            "type",
            "gold",
            "bust",
            "portrait",
            "deed",
            "crest",
            "shard",
            "memory",
            "blueprint"
        ];

        private static readonly string[] ValueCandidateKeys =
        [
            "estatename",
            "game_mode",
            "date_time",
            "raiddungeon",
            "dd_mode"
        ];

        private static readonly string[] FloatFieldNames =
        [
            "totalelapsed",
            "current_hp",
            "m_Stress"
        ];

        private static readonly string[] UInt32FieldNames =
        [
            "tree_id"
        ];

        private static readonly string[] SingleByteStringFieldNames =
        [
            "requirement_code"
        ];

        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        private const int MaxInlineValueDistance = 16;

        public static string? TryWriteReport(
            string sessionDirectory,
            string sessionId,
            DateTimeOffset generatedAt,
            SaveSessionReport sessionReport,
            string gameWorkingDirectory,
            LauncherLog log)
        {
            if (sessionReport.ActiveProfile is null)
            {
                log.Warn("event name=save.state_report_skipped reason=no_active_profile");
                return null;
            }

            var logDirectory = Directory.GetParent(sessionDirectory)?.FullName ?? sessionDirectory;
            var stateDirectory = Path.Combine(logDirectory, "save_states");
            Directory.CreateDirectory(stateDirectory);

            var accessIssues = new List<string>();
            var activeRoot = sessionReport.ActiveProfile.Root;
            if (!Directory.Exists(activeRoot))
            {
                accessIssues.Add($"Active profile directory was not found: {activeRoot}");
            }

            var fileReports = CandidateFiles
                .Select(name => InspectFile(Path.Combine(activeRoot, name), name))
                .ToArray();
            foreach (var issue in fileReports.SelectMany(file => file.AccessIssues))
            {
                accessIssues.Add(issue);
            }

            var gameReport = fileReports.FirstOrDefault(file => file.FileName.Equals("persist.game.json", StringComparison.OrdinalIgnoreCase));
            var gameMode = TryGetString(gameReport, "base_root.game_mode");
            var upgradeCatalog = UpgradeDefinitionCatalog.Load(gameWorkingDirectory, gameMode, accessIssues);

            var parseStatus = fileReports.Any(file => file.Format.Equals("jsonText", StringComparison.OrdinalIgnoreCase))
                ? "partialJsonText"
                : fileReports.Any(file => file.ParseStatus.Equals("dsonPartialDecoded", StringComparison.OrdinalIgnoreCase))
                    ? "dsonPartialDecoded"
                    : "binaryStringIndexOnly";
            if (fileReports.All(file => !file.Exists))
            {
                parseStatus = "noCandidateFiles";
            }
            var facts = BuildSaveStateFacts(fileReports, upgradeCatalog);

            var report = new SaveStateReport(
                1,
                sessionId,
                generatedAt,
                parseStatus,
                "Darkest Dungeon persist files use a DSON binary container despite the .json extension; this report is read-only and exports bounded DSON metadata, scalar samples, visible string candidates, and conservative state facts.",
                sessionReport.ActiveProfile,
                facts,
                CandidateFiles,
                fileReports,
                accessIssues);

            var path = Path.Combine(stateDirectory, $"{sessionId}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(report, SessionJsonOptions), Encoding.UTF8);
            log.Info($"event name=save.state_report_written path={Quote(path)} parseStatus={parseStatus} files={fileReports.Length} accessIssues={accessIssues.Count}");
            TryWriteFileMapReport(logDirectory, sessionId, generatedAt, sessionReport, activeRoot, fileReports, log);
            return path;
        }

        private static SaveStateFileReport InspectFile(string path, string fileName)
        {
            var accessIssues = new List<string>();
            if (!File.Exists(path))
            {
                accessIssues.Add($"Candidate file was not found: {path}");
                return new SaveStateFileReport(
                    fileName,
                    path,
                    false,
                    null,
                    null,
                    null,
                    "missing",
                    "missing",
                    null,
                    null,
                    null,
                    null,
                    null,
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    accessIssues);
            }

            try
            {
                var bytes = File.ReadAllBytes(path);
                var info = new FileInfo(path);
                var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                var firstByte = bytes.FirstOrDefault();
                if (LooksLikeJsonText(bytes))
                {
                    return InspectJsonText(path, fileName, bytes, info, sha256, accessIssues);
                }

                var container = TryParseBinaryContainer(bytes, accessIssues);
                var strings = container?.Strings ?? ExtractPrintableStrings(bytes);
                var printableStrings = container is null ? strings : ExtractPrintableStrings(bytes);
                var markerSet = KnownMarkers.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var markers = strings
                    .Select(item => item.Value)
                    .Where(value => markerSet.Contains(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(120)
                    .ToArray();
                var valueCandidates = ExtractValueCandidates(strings, printableStrings);
                var samples = strings
                    .Select(item => item.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(120)
                    .ToArray();
                var heroFacts = ExtractHeroFactsFromRoster(fileName, bytes, container, accessIssues);
                var parseStatus = container is not null
                    ? "dsonPartialDecoded"
                    : firstByte == 0x01 ? "binaryStringIndexOnly" : "unknownBinary";

                return new SaveStateFileReport(
                    fileName,
                    path,
                    true,
                    info.Length,
                    info.LastWriteTimeUtc,
                    sha256,
                    "binaryContainer",
                    parseStatus,
                    ToHex(bytes.Take(32)),
                    container?.StringCount,
                    container?.StringIndexOffset,
                    container?.StringDataOffset,
                    container?.DsonSummary,
                    container?.DsonScalars.Take(320).ToArray() ?? [],
                    container?.DsonScalars ?? [],
                    container?.DsonObjectPaths.Take(1000).ToArray() ?? [],
                    heroFacts,
                    [],
                    markers,
                    valueCandidates,
                    samples,
                    container?.Strings.Take(240).ToArray() ?? [],
                    accessIssues);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                accessIssues.Add($"{fileName}: {ex.Message}");
                return new SaveStateFileReport(
                    fileName,
                    path,
                    true,
                    null,
                    null,
                    null,
                    "unreadable",
                    "error",
                    null,
                    null,
                    null,
                    null,
                    null,
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    accessIssues);
            }
        }

        private static void TryWriteFileMapReport(
            string logDirectory,
            string sessionId,
            DateTimeOffset generatedAt,
            SaveSessionReport sessionReport,
            string activeRoot,
            IReadOnlyList<SaveStateFileReport> analyzedFiles,
            LauncherLog log)
        {
            if (sessionReport.ActiveProfile is null)
            {
                return;
            }

            var accessIssues = new List<string>();
            var entries = new List<SaveFileMapEntry>();
            var analyzedByName = analyzedFiles.ToDictionary(file => file.FileName, StringComparer.OrdinalIgnoreCase);

            if (!Directory.Exists(activeRoot))
            {
                accessIssues.Add($"Active profile directory was not found: {activeRoot}");
            }
            else
            {
                foreach (var source in EnumeratePersistFiles(activeRoot))
                {
                    SaveStateFileReport inspected;
                    if (source.Area.Equals("live", StringComparison.OrdinalIgnoreCase)
                        && analyzedByName.TryGetValue(source.FileName, out var cached))
                    {
                        inspected = cached;
                    }
                    else
                    {
                        inspected = InspectFile(source.Path, source.FileName);
                    }

                    foreach (var issue in inspected.AccessIssues)
                    {
                        accessIssues.Add(issue);
                    }

                    entries.Add(BuildFileMapEntry(source, inspected));
                }
            }

            var mapDirectory = Path.Combine(logDirectory, "save_file_maps");
            Directory.CreateDirectory(mapDirectory);
            var report = new SaveFileMapReport(
                1,
                sessionId,
                generatedAt,
                sessionReport.ActiveProfile,
                activeRoot,
                CandidateFiles,
                entries
                    .OrderBy(entry => entry.Area, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Priority)
                    .ThenBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                accessIssues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

            var path = Path.Combine(mapDirectory, $"{sessionId}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(report, SessionJsonOptions), Encoding.UTF8);
            log.Info($"event name=save.file_map_report_written path={Quote(path)} files={entries.Count} live={entries.Count(entry => entry.Area.Equals("live", StringComparison.OrdinalIgnoreCase))} backup={entries.Count(entry => entry.Area.Equals("backup", StringComparison.OrdinalIgnoreCase))} accessIssues={accessIssues.Count}");
        }

        private static IEnumerable<SaveFileMapSource> EnumeratePersistFiles(string activeRoot)
        {
            foreach (var path in Directory.EnumerateFiles(activeRoot, "persist*.json", SearchOption.TopDirectoryOnly))
            {
                yield return new SaveFileMapSource(path, Path.GetFileName(path), Path.GetFileName(path), "live");
            }

            var backupRoot = Path.Combine(activeRoot, "backup");
            if (!Directory.Exists(backupRoot))
            {
                yield break;
            }

            foreach (var path in Directory.EnumerateFiles(backupRoot, "persist*.json", SearchOption.TopDirectoryOnly))
            {
                yield return new SaveFileMapSource(path, Path.GetFileName(path), Path.Combine("backup", Path.GetFileName(path)), "backup");
            }
        }

        private static SaveFileMapEntry BuildFileMapEntry(SaveFileMapSource source, SaveStateFileReport inspected)
        {
            var classification = ClassifyPersistFile(source.FileName);
            var isCandidate = CandidateFiles.Contains(source.FileName, StringComparer.OrdinalIgnoreCase);
            var coverage = DetermineFileCoverage(source.FileName, isCandidate, inspected);

            return new SaveFileMapEntry(
                source.FileName,
                source.RelativePath,
                source.Area,
                inspected.Path,
                inspected.Exists,
                inspected.Length,
                inspected.LastWriteUtc,
                inspected.Sha256,
                inspected.Format,
                inspected.ParseStatus,
                isCandidate,
                classification.Priority,
                classification.Category,
                classification.ModRelevance,
                coverage,
                inspected.DsonSummary,
                inspected.MarkerStrings,
                inspected.ValueCandidates.Select(candidate => candidate.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                inspected.DsonScalars.Count,
                inspected.DsonObjectPaths.Count,
                inspected.AccessIssues);
        }

        private static SaveFileMapClassification ClassifyPersistFile(string fileName)
        {
            return fileName.ToLowerInvariant() switch
            {
                "persist.game.json" => new SaveFileMapClassification(1, "campaign_runtime", "Campaign identity, mode, elapsed time, current raid state, and game options."),
                "persist.estate.json" => new SaveFileMapClassification(1, "estate_resources", "Wallet resources and estate-level inventory/tamper metadata."),
                "persist.roster.json" => new SaveFileMapClassification(1, "heroes", "Hero roster entry points and partially decoded nested hero raw_data facts."),
                "persist.upgrades.json" => new SaveFileMapClassification(2, "upgrade_tree", "Building purchase tree and upgrade unlock state; tree_id is numeric until static definitions are mapped."),
                "persist.quest.json" => new SaveFileMapClassification(3, "quests", "Quest generation, available missions, and dungeon selection state."),
                "persist.town_event.json" => new SaveFileMapClassification(3, "town_events", "Current and historical town event state."),
                "persist.town.json" => new SaveFileMapClassification(4, "town_runtime", "Hamlet buildings, shops, activity slots, inventories, and runtime town state."),
                "persist.progression.json" => new SaveFileMapClassification(5, "progression", "Dungeon XP, boss/story progression, quest history, and unlock conditions."),
                "persist.game_knowledge.json" => new SaveFileMapClassification(6, "knowledge", "Discovered game knowledge and UI reveal state."),
                "persist.journal.json" => new SaveFileMapClassification(6, "journal", "Collected journal pages and related discovery state."),
                "persist.narration.json" => new SaveFileMapClassification(6, "narration", "Narration playback and bark/history gating state."),
                "persist.tutorial.json" => new SaveFileMapClassification(6, "tutorial", "Tutorial prompt completion and gating state."),
                "persist.campaign_log.json" => new SaveFileMapClassification(7, "history_log", "Campaign history/log data, likely secondary for runtime rules."),
                "persist.campaign_mash.json" => new SaveFileMapClassification(7, "history_log", "Campaign aggregate/log companion data, likely secondary for runtime rules."),
                _ => new SaveFileMapClassification(9, "unknown", "Unclassified persist data; inspect when a mod idea needs it.")
            };
        }

        private static string DetermineFileCoverage(string fileName, bool isCandidate, SaveStateFileReport inspected)
        {
            if (!inspected.Exists)
            {
                return "missing";
            }

            if (inspected.ParseStatus.Equals("dsonPartialDecoded", StringComparison.OrdinalIgnoreCase))
            {
                if (fileName.Equals("persist.roster.json", StringComparison.OrdinalIgnoreCase)
                    && inspected.DsonSummary?.RawScalarCount > 0)
                {
                    if (inspected.Heroes.Count > 0)
                    {
                        return "candidate_nested_dson_partial";
                    }

                    return "candidate_nested_raw_pending";
                }

                if (fileName.Equals("persist.upgrades.json", StringComparison.OrdinalIgnoreCase)
                    && inspected.DsonSummary?.RawScalarCount > 0)
                {
                    return "candidate_upgrade_purchases_partial";
                }

                return isCandidate ? "candidate_dson_partial" : "mapped_dson_partial";
            }

            if (inspected.Format.Equals("jsonText", StringComparison.OrdinalIgnoreCase))
            {
                return isCandidate ? "candidate_json_text" : "mapped_json_text";
            }

            return isCandidate ? "candidate_unresolved" : "mapped_unresolved";
        }

        private static SaveStateFileReport InspectJsonText(
            string path,
            string fileName,
            byte[] bytes,
            FileInfo info,
            string sha256,
            IReadOnlyList<string> accessIssues)
        {
            using var document = JsonDocument.Parse(bytes);
            var topLevelKeys = document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject().Select(property => property.Name).Take(120).ToArray()
                : [];

            return new SaveStateFileReport(
                fileName,
                path,
                true,
                info.Length,
                info.LastWriteTimeUtc,
                sha256,
                "jsonText",
                "parsedJsonText",
                null,
                null,
                null,
                null,
                null,
                [],
                [],
                [],
                [],
                topLevelKeys,
                [],
                [],
                [],
                [],
                accessIssues);
        }

        private static bool LooksLikeJsonText(byte[] bytes)
        {
            foreach (var b in bytes)
            {
                if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                {
                    continue;
                }

                return b is (byte)'{' or (byte)'[';
            }

            return false;
        }

        private static SaveStateFacts BuildSaveStateFacts(IReadOnlyList<SaveStateFileReport> files, UpgradeDefinitionCatalog upgradeCatalog)
        {
            var game = files.FirstOrDefault(file => file.FileName.Equals("persist.game.json", StringComparison.OrdinalIgnoreCase));
            var progression = files.FirstOrDefault(file => file.FileName.Equals("persist.progression.json", StringComparison.OrdinalIgnoreCase));
            var estate = files.FirstOrDefault(file => file.FileName.Equals("persist.estate.json", StringComparison.OrdinalIgnoreCase));
            var upgrades = files.FirstOrDefault(file => file.FileName.Equals("persist.upgrades.json", StringComparison.OrdinalIgnoreCase));
            var town = files.FirstOrDefault(file => file.FileName.Equals("persist.town.json", StringComparison.OrdinalIgnoreCase));
            var roster = files.FirstOrDefault(file => file.FileName.Equals("persist.roster.json", StringComparison.OrdinalIgnoreCase));

            return new SaveStateFacts(
                new SaveStateCampaignFacts(
                    TryGetInt(game, "base_root.version"),
                    TryGetDouble(game, "base_root.totalelapsed"),
                    TryGetBool(game, "base_root.inraid"),
                    TryGetString(game, "base_root.raiddungeon"),
                    TryGetString(game, "base_root.estatename"),
                    TryGetString(game, "base_root.game_mode"),
                    TryGetString(game, "base_root.date_time"),
                    TryGetString(game, "base_root.town_events"),
                    TryGetString(game, "base_root.never_again")),
                new SaveStateProgressionFacts(
                    TryGetInt(progression, "base_root.total_quests_finished"),
                    TryGetInt(progression, "base_root.total_successful_quests_finished"),
                    TryGetInt(progression, "base_root.last_quest_played_id"),
                    TryGetInt(progression, "base_root.last_quest_played_xp"),
                    TryGetBool(progression, "base_root.last_raid_success"),
                    TryGetBool(progression, "base_root.last_raid_was_a_plot_quest")),
                BuildWalletFacts(estate),
                BuildUpgradeFacts(upgrades, upgradeCatalog),
                BuildTownFacts(town),
                ExtractDirectChildIds(town?.DsonObjectPaths ?? [], "base_root.buildings"),
                ExtractDirectChildIds(roster?.DsonObjectPaths ?? [], "base_root.heroes"),
                roster?.Heroes ?? []);
        }

        private static IReadOnlyDictionary<string, int> BuildWalletFacts(SaveStateFileReport? estate)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (estate is null)
            {
                return result;
            }

            var scalars = GetDsonScalars(estate);
            var byPath = scalars.ToDictionary(scalar => scalar.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var typeScalar in scalars.Where(scalar => scalar.Path.StartsWith("base_root.wallet.", StringComparison.OrdinalIgnoreCase)
                         && scalar.Path.EndsWith(".type", StringComparison.OrdinalIgnoreCase)
                         && scalar.Type.Equals("string", StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(scalar.Value)))
            {
                var prefix = typeScalar.Path[..^".type".Length];
                if (!byPath.TryGetValue($"{prefix}.amount", out var amountScalar)
                    || !int.TryParse(amountScalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
                {
                    continue;
                }

                result[typeScalar.Value!] = amount;
            }

            return result;
        }

        private static IReadOnlyList<string> ExtractDirectChildIds(IReadOnlyList<string> paths, string parentPath)
        {
            return ExtractDirectChildIds(paths, parentPath, 120);
        }

        private static IReadOnlyList<string> ExtractAllDirectChildIds(IReadOnlyList<string> paths, string parentPath)
        {
            return ExtractDirectChildIds(paths, parentPath, null);
        }

        private static IReadOnlyList<string> ExtractDirectChildIds(IReadOnlyList<string> paths, string parentPath, int? maxCount)
        {
            var prefix = parentPath + ".";
            var values = paths
                .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(path =>
                {
                    var rest = path[prefix.Length..];
                    var dot = rest.IndexOf('.');
                    return dot >= 0 ? rest[..dot] : rest;
                })
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);

            return maxCount.HasValue
                ? values.Take(maxCount.Value).ToArray()
                : values.ToArray();
        }

        private static IReadOnlyList<string> ExtractDirectChildIds(IReadOnlyList<SaveStateDsonScalar> scalars, string parentPath)
        {
            return ExtractDirectChildIds(scalars.Select(scalar => scalar.Path).ToArray(), parentPath);
        }

        private static IReadOnlyList<string> ExtractAllDirectChildIds(IReadOnlyList<SaveStateDsonScalar> scalars, string parentPath)
        {
            return ExtractAllDirectChildIds(scalars.Select(scalar => scalar.Path).ToArray(), parentPath);
        }

        private static IReadOnlyList<string> MergeDirectChildIds(params IReadOnlyList<string>[] idLists)
        {
            return MergeDirectChildIds(120, idLists);
        }

        private static IReadOnlyList<string> MergeAllDirectChildIds(params IReadOnlyList<string>[] idLists)
        {
            return MergeDirectChildIds(null, idLists);
        }

        private static IReadOnlyList<string> MergeDirectChildIds(int? maxCount, params IReadOnlyList<string>[] idLists)
        {
            var values = idLists
                .SelectMany(list => list)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);

            return maxCount.HasValue
                ? values.Take(maxCount.Value).ToArray()
                : values.ToArray();
        }

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
            return new SaveStateHeroFacts(
                heroId,
                TryGetString(nested.DsonScalars, "base_root.actor.name"),
                TryGetString(nested.DsonScalars, "base_root.heroClass"),
                TryGetInt(nested.DsonScalars, "base_root.roster.status"),
                TryGetInt(nested.DsonScalars, "base_root.resolveXp"),
                TryGetDouble(nested.DsonScalars, "base_root.actor.current_hp"),
                TryGetDouble(nested.DsonScalars, "base_root.m_Stress"),
                TryGetInt(nested.DsonScalars, "base_root.weapon_rank"),
                TryGetInt(nested.DsonScalars, "base_root.armour_rank"),
                TryGetBool(nested.DsonScalars, "base_root.backer_hero"),
                rawDataLength,
                nested.DsonSummary.ObjectCount,
                nested.DsonSummary.FieldCount,
                ExtractDirectChildIds(nested.DsonObjectPaths, "base_root.quirks"),
                ExtractDirectChildIds(nested.DsonScalars, "base_root.skills.selected_combat_skills"),
                ExtractDirectChildIds(nested.DsonScalars, "base_root.skills.selected_camping_skills"),
                MergeDirectChildIds(
                    ExtractDirectChildIds(nested.DsonObjectPaths, "base_root.trinkets.items"),
                    ExtractDirectChildIds(nested.DsonScalars, "base_root.trinkets.items")));
        }

        private static string? TryGetString(SaveStateFileReport? file, string path)
        {
            var scalar = FindDsonScalar(file, path);
            return scalar is not null && scalar.Type.Equals("string", StringComparison.OrdinalIgnoreCase)
                ? scalar.Value
                : null;
        }

        private static int? TryGetInt(SaveStateFileReport? file, string path)
        {
            var scalar = FindDsonScalar(file, path);
            return scalar is not null && int.TryParse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        private static uint? TryGetUInt(SaveStateFileReport? file, string path)
        {
            var scalar = FindDsonScalar(file, path);
            return scalar is not null && uint.TryParse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        private static double? TryGetDouble(SaveStateFileReport? file, string path)
        {
            var scalar = FindDsonScalar(file, path);
            return scalar is not null && double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        private static bool? TryGetBool(SaveStateFileReport? file, string path)
        {
            var scalar = FindDsonScalar(file, path);
            return scalar is not null && bool.TryParse(scalar.Value, out var value)
                ? value
                : null;
        }

        private static string? TryGetString(IReadOnlyList<SaveStateDsonScalar> scalars, string path)
        {
            var scalar = FindDsonScalar(scalars, path);
            return scalar is not null && scalar.Type.Equals("string", StringComparison.OrdinalIgnoreCase)
                ? scalar.Value
                : null;
        }

        private static int? TryGetInt(IReadOnlyList<SaveStateDsonScalar> scalars, string path)
        {
            var scalar = FindDsonScalar(scalars, path);
            return scalar is not null && int.TryParse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        private static double? TryGetDouble(IReadOnlyList<SaveStateDsonScalar> scalars, string path)
        {
            var scalar = FindDsonScalar(scalars, path);
            return scalar is not null && double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        private static bool? TryGetBool(IReadOnlyList<SaveStateDsonScalar> scalars, string path)
        {
            var scalar = FindDsonScalar(scalars, path);
            return scalar is not null && bool.TryParse(scalar.Value, out var value)
                ? value
                : null;
        }

        private static SaveStateDsonScalar? FindDsonScalar(SaveStateFileReport? file, string path)
        {
            return GetDsonScalars(file).FirstOrDefault(scalar => scalar.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        }

        private static SaveStateDsonScalar? FindDsonScalar(IReadOnlyList<SaveStateDsonScalar> scalars, string path)
        {
            return scalars.FirstOrDefault(scalar => scalar.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        }

        private static IReadOnlyList<SaveStateDsonScalar> GetDsonScalars(SaveStateFileReport? file)
        {
            if (file is null)
            {
                return [];
            }

            return file.AllDsonScalars.Count > 0 ? file.AllDsonScalars : file.DsonScalars;
        }

    }
}
