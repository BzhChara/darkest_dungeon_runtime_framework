namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static readonly string[] CandidateFiles =
        [
            "persist.game.json",
            "persist.estate.json",
            "persist.roster.json",
            "persist.upgrades.json",
            "persist.quest.json",
            "persist.town_event.json",
            "persist.town.json",
            "persist.progression.json",
            "persist.game_knowledge.json",
            "persist.journal.json",
            "persist.narration.json",
            "persist.tutorial.json",
            "persist.campaign_log.json",
            "persist.campaign_mash.json"
        ];

        private static readonly string[] OptionalCandidateFiles =
        [
            "novelty_tracker.json",
            "persist.curio_tracker.json",
            "persist.raid.json",
            "persist.map.json",
            "persist.loading_screen.json",
            "persist.mp_progression.json",
            "persist.roster.network.json",
            "novelty_tracker_mp.json",
            "backer_heroes.json"
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
            "map",
            "areas",
            "party",
            "skill_cooldown_values",
            "backgroundNames",
            "backer_heroes",
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

        private static readonly string[][] FloatPathPatterns =
        [
            ["totalelapsed"],
            ["current_hp"],
            ["m_Stress"],
            ["start_elapsed_time"],
            ["torchlight"],
            ["ambush_start_torchlight"],
            ["shard_consume_percent"],
            ["actor", "buff_group", "*", "amount"],
            ["chapters", "*", "*", "percent"],
            ["non_rolled_additional_chances", "*", "chance"],
            ["stat_database", "*", "entries", "*", "value"],
            ["stat_database", "*", "entries", "*", "time_stamp"]
        ];

        private static readonly string[] UInt32FieldNames =
        [
            "tree_id"
        ];

        private static readonly string[] SingleByteStringFieldNames =
        [
            "requirement_code"
        ];

        private static readonly string[][] IntVectorPathPatterns =
        [
            ["read_page_indexes"],
            ["raid_read_page_indexes"],
            ["raid_unread_page_indexes"],
            ["dungeons_unlocked"],
            ["played_video_list"],
            ["trinket_retention_ids"],
            ["last_party_guids"],
            ["dungeon_history"],
            ["buff_group_guids"],
            ["result_event_history"],
            ["dead_hero_entries"],
            ["additional_mash_disabled_infestation_monster_class_ids"],
            ["mash", "valid_additional_mash_entry_indexes"],
            ["party", "heroes"],
            ["skill_cooldown_keys"],
            ["skill_cooldown_values"],
            ["bufferedSpawningSlotsAvailable"],
            ["curioGroups", "*", "curios"],
            ["curioGroups", "*", "curio_table_entries"],
            ["raid_finish_quirk_monster_class_ids"],
            ["narration_audio_event_queue_tags"],
            ["dispatched_events"],
            ["backer_heroes", "*", "combat_skills"],
            ["backer_heroes", "*", "camping_skills"],
            ["backer_heroes", "*", "quirks"]
        ];

        private static readonly string[][] StringVectorPathPatterns =
        [
            ["goal_ids"],
            ["roaming_dungeon_2_ids", "*", "s"],
            ["quirk_group"],
            ["backgroundNames"],
            ["backgroundGroups", "*", "backgrounds"],
            ["backgroundGroups", "*", "background_table_entries"]
        ];

        private static readonly string[][] FloatArrayPathPatterns =
        [
            ["map", "bounds"],
            ["areas", "*", "bounds"],
            ["areas", "*", "tiles", "*", "mappos"],
            ["areas", "*", "tiles", "*", "sidepos"]
        ];

        private static readonly string[][] IntPairPathPatterns =
        [
            ["killRange"]
        ];

        private static readonly string[][] BoolPairPathPatterns =
        [
            ["profile_options", "values", "quest_select_warnings"],
            ["profile_options", "values", "provision_warnings"],
            ["profile_options", "values", "deck_based_stage_coach"],
            ["profile_options", "values", "curio_tracker"],
            ["profile_options", "values", "dd_mode"],
            ["profile_options", "values", "corpses"],
            ["profile_options", "values", "stall_penalty"],
            ["profile_options", "values", "deaths_door_recovery_debuffs"],
            ["profile_options", "values", "retreats_can_fail"],
            ["profile_options", "values", "multiplied_enemy_crits"]
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

            var fileReports = EnumerateCandidateFiles(activeRoot)
                .Select(name => InspectFile(Path.Combine(activeRoot, name), name))
                .ToArray();
            foreach (var issue in fileReports.SelectMany(file => file.AccessIssues))
            {
                accessIssues.Add(issue);
            }

            var gameReport = fileReports.FirstOrDefault(file => file.FileName.Equals("persist.game.json", StringComparison.OrdinalIgnoreCase));
            var gameMode = TryGetString(gameReport, "base_root.game_mode");
            var upgradeCatalog = UpgradeDefinitionCatalog.Load(gameWorkingDirectory, gameMode, accessIssues);
            var heroDefinitions = BuildHeroDefinitionFacts(gameWorkingDirectory, gameMode, accessIssues);
            var contentHashCatalog = ContentHashCatalog.Load(gameWorkingDirectory, accessIssues);

            var parseStatus = fileReports.Any(file => file.Format.Equals("jsonText", StringComparison.OrdinalIgnoreCase))
                ? "partialJsonText"
                : fileReports.Any(file => file.ParseStatus.Equals("dsonPartialDecoded", StringComparison.OrdinalIgnoreCase))
                    ? "dsonPartialDecoded"
                    : "binaryStringIndexOnly";
            if (fileReports.All(file => !file.Exists))
            {
                parseStatus = "noCandidateFiles";
            }
            var facts = BuildSaveStateFacts(fileReports, upgradeCatalog, heroDefinitions, contentHashCatalog);

            var report = new SaveStateReport(
                1,
                sessionId,
                generatedAt,
                parseStatus,
                "Darkest Dungeon persist files use a DSON binary container despite the .json extension; this report is read-only and exports bounded DSON metadata, scalar samples, visible string candidates, and conservative state facts.",
                sessionReport.ActiveProfile,
                facts,
                CandidateFiles,
                OptionalCandidateFiles,
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
                OptionalCandidateFiles,
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
            foreach (var path in EnumerateKnownSaveFilePaths(activeRoot))
            {
                yield return new SaveFileMapSource(path, Path.GetFileName(path), Path.GetFileName(path), "live");
            }

            var backupRoot = Path.Combine(activeRoot, "backup");
            if (!Directory.Exists(backupRoot))
            {
                yield break;
            }

            foreach (var path in EnumerateKnownSaveFilePaths(backupRoot))
            {
                yield return new SaveFileMapSource(path, Path.GetFileName(path), Path.Combine("backup", Path.GetFileName(path)), "backup");
            }
        }

        private static IReadOnlyList<string> EnumerateCandidateFiles(string root)
        {
            var names = new List<string>(CandidateFiles);
            if (Directory.Exists(root))
            {
                names.AddRange(OptionalCandidateFiles.Where(name => File.Exists(Path.Combine(root, name))));
            }

            return names
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IEnumerable<string> EnumerateKnownSaveFilePaths(string root)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(root, "persist*.json", SearchOption.TopDirectoryOnly))
            {
                if (seen.Add(Path.GetFileName(path)))
                {
                    yield return path;
                }
            }

            foreach (var fileName in OptionalCandidateFiles)
            {
                var path = Path.Combine(root, fileName);
                if (File.Exists(path) && seen.Add(fileName))
                {
                    yield return path;
                }
            }
        }

        private static SaveFileMapEntry BuildFileMapEntry(SaveFileMapSource source, SaveStateFileReport inspected)
        {
            var classification = ClassifyPersistFile(source.FileName);
            var isCandidate = IsKnownCandidateFile(source.FileName);
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
                "persist.curio_tracker.json" => new SaveFileMapClassification(5, "curio_tracker", "Curio discovery/tracking state used by campaign and raid interaction rules."),
                "novelty_tracker.json" => new SaveFileMapClassification(6, "novelty_tracker", "Campaign novelty/reveal tracking; optional but present in current and research profiles."),
                "persist.raid.json" => new SaveFileMapClassification(3, "raid_runtime", "In-raid party, cooldown, mash, curio, background, and encounter runtime state."),
                "persist.map.json" => new SaveFileMapClassification(3, "raid_map", "Generated raid map layout, room/tile bounds, and spatial state."),
                "persist.loading_screen.json" => new SaveFileMapClassification(6, "loading_screen", "Loading screen narration/event queue state."),
                "persist.mp_progression.json" => new SaveFileMapClassification(8, "network_progression", "Butcher's Circus/network progression state; optional and out of base campaign scope."),
                "persist.roster.network.json" => new SaveFileMapClassification(8, "network_roster", "Butcher's Circus/network roster state; optional and out of base campaign scope."),
                "novelty_tracker_mp.json" => new SaveFileMapClassification(8, "network_novelty", "Butcher's Circus/network novelty tracking; optional and out of base campaign scope."),
                "backer_heroes.json" => new SaveFileMapClassification(8, "backer_heroes", "Backer hero definitions and skill/quirk vectors; optional static-like save companion data."),
                _ => new SaveFileMapClassification(9, "unknown", "Unclassified persist data; inspect when a mod idea needs it.")
            };
        }

        private static bool IsKnownCandidateFile(string fileName)
        {
            return CandidateFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase)
                || OptionalCandidateFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase);
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
                    && (inspected.Heroes.Count > 0 || inspected.DsonSummary?.RawScalarCount > 0))
                {
                    if (inspected.Heroes.Count > 0)
                    {
                        return "candidate_nested_dson_partial";
                    }

                    return "candidate_nested_raw_pending";
                }

                if (GetDsonScalars(inspected).Any(scalar => scalar.Type.Equals("embeddedDson", StringComparison.OrdinalIgnoreCase)))
                {
                    return isCandidate ? "candidate_embedded_dson_partial" : "mapped_embedded_dson_partial";
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

        private static SaveStateFacts BuildSaveStateFacts(
            IReadOnlyList<SaveStateFileReport> files,
            UpgradeDefinitionCatalog upgradeCatalog,
            SaveStateHeroDefinitionFacts heroDefinitions,
            ContentHashCatalog contentHashCatalog)
        {
            var game = files.FirstOrDefault(file => file.FileName.Equals("persist.game.json", StringComparison.OrdinalIgnoreCase));
            var progression = files.FirstOrDefault(file => file.FileName.Equals("persist.progression.json", StringComparison.OrdinalIgnoreCase));
            var quest = files.FirstOrDefault(file => file.FileName.Equals("persist.quest.json", StringComparison.OrdinalIgnoreCase));
            var townEvent = files.FirstOrDefault(file => file.FileName.Equals("persist.town_event.json", StringComparison.OrdinalIgnoreCase));
            var gameKnowledge = files.FirstOrDefault(file => file.FileName.Equals("persist.game_knowledge.json", StringComparison.OrdinalIgnoreCase));
            var journal = files.FirstOrDefault(file => file.FileName.Equals("persist.journal.json", StringComparison.OrdinalIgnoreCase));
            var narration = files.FirstOrDefault(file => file.FileName.Equals("persist.narration.json", StringComparison.OrdinalIgnoreCase));
            var tutorial = files.FirstOrDefault(file => file.FileName.Equals("persist.tutorial.json", StringComparison.OrdinalIgnoreCase));
            var campaignLog = files.FirstOrDefault(file => file.FileName.Equals("persist.campaign_log.json", StringComparison.OrdinalIgnoreCase));
            var campaignMash = files.FirstOrDefault(file => file.FileName.Equals("persist.campaign_mash.json", StringComparison.OrdinalIgnoreCase));
            var estate = files.FirstOrDefault(file => file.FileName.Equals("persist.estate.json", StringComparison.OrdinalIgnoreCase));
            var upgrades = files.FirstOrDefault(file => file.FileName.Equals("persist.upgrades.json", StringComparison.OrdinalIgnoreCase));
            var town = files.FirstOrDefault(file => file.FileName.Equals("persist.town.json", StringComparison.OrdinalIgnoreCase));
            var roster = files.FirstOrDefault(file => file.FileName.Equals("persist.roster.json", StringComparison.OrdinalIgnoreCase));
            var map = files.FirstOrDefault(file => file.FileName.Equals("persist.map.json", StringComparison.OrdinalIgnoreCase));
            var raid = files.FirstOrDefault(file => file.FileName.Equals("persist.raid.json", StringComparison.OrdinalIgnoreCase));
            var curioTracker = files.FirstOrDefault(file => file.FileName.Equals("persist.curio_tracker.json", StringComparison.OrdinalIgnoreCase));
            var loadingScreen = files.FirstOrDefault(file => file.FileName.Equals("persist.loading_screen.json", StringComparison.OrdinalIgnoreCase));
            var noveltyTracker = files.FirstOrDefault(file => file.FileName.Equals("novelty_tracker.json", StringComparison.OrdinalIgnoreCase));
            var heroes = roster?.Heroes ?? [];
            var mapFacts = BuildMapFacts(map);

            return new SaveStateFacts(
                BuildPersistFileFacts(files),
                contentHashCatalog.ToFacts(),
                BuildCampaignFacts(game),
                BuildProgressionFacts(progression),
                BuildQuestFacts(quest),
                BuildTownEventFacts(townEvent),
                BuildGameKnowledgeFacts(gameKnowledge, contentHashCatalog),
                BuildJournalFacts(journal),
                BuildNarrationFacts(narration),
                BuildTutorialFacts(tutorial, contentHashCatalog),
                BuildCampaignLogFacts(campaignLog),
                BuildCampaignMashFacts(campaignMash, contentHashCatalog),
                BuildEstateFacts(estate),
                BuildWalletFacts(estate),
                BuildUpgradeFacts(upgrades, upgradeCatalog),
                heroDefinitions,
                BuildTownFacts(town),
                ExtractDirectChildIds(town?.DsonObjectPaths ?? [], "base_root.buildings"),
                ExtractDirectChildIds(roster?.DsonObjectPaths ?? [], "base_root.heroes"),
                BuildRosterFacts(roster),
                mapFacts,
                BuildRaidFacts(raid, mapFacts),
                BuildCurioTrackerFacts(curioTracker, contentHashCatalog),
                BuildLoadingScreenFacts(loadingScreen, contentHashCatalog),
                BuildNoveltyTrackerFacts(noveltyTracker),
                BuildHeroLoadoutFacts(heroes, heroDefinitions),
                heroes);
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

        private static SaveStateObjectContainerFacts BuildObjectContainerFacts(
            SaveStateFileReport? file,
            string path)
        {
            if (file is null)
            {
                return new SaveStateObjectContainerFacts(path, false, false, 0, [], 0, 0);
            }

            var prefix = path + ".";
            var scalars = GetDsonScalars(file);
            var directChildIds = MergeAllDirectChildIds(
                ExtractAllDirectChildIds(file.DsonObjectPaths, path),
                ExtractAllDirectChildIds(scalars, path));
            var exists = file.DsonObjectPaths.Any(item => item.Equals(path, StringComparison.OrdinalIgnoreCase))
                || file.DsonObjectPaths.Any(item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                || scalars.Any(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                || scalars.Any(item => item.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            return new SaveStateObjectContainerFacts(
                path,
                exists,
                directChildIds.Count > 0,
                directChildIds.Count,
                directChildIds,
                file.DsonObjectPaths.Count(item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)),
                scalars.Count(item => item.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
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

        private static string NumericAwareSortKey(string value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed.ToString("D10", CultureInfo.InvariantCulture)
                : value;
        }

        private static string? EmptyToNull(string? value)
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
