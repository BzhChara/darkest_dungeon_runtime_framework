using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace DDRuntimeLoader;

internal static partial class ManagedActionSaveApplier
{
    private const int FullQuirkSlotCount = 5;
    private const int DefaultSelectedCombatSkillCount = 4;
    private const int DefaultSelectedCampingSkillCount = 4;
    private const int MaxResolveXp = 46;

    private static void ApplyRosterEnsureClassInstances(ApplyContext context, string artifactPath, JsonObject artifact)
    {
        var classSource = ReadString(artifact, "plan.arguments.classSource");
        var copiesPerClass = ReadInt(ReadNode(artifact, "plan.arguments.copiesPerClass"), "plan.arguments.copiesPerClass");
        if (copiesPerClass < 0)
        {
            throw new InvalidDataException("plan.arguments.copiesPerClass must be zero or greater.");
        }

        var level = ReadString(artifact, "plan.arguments.level");
        var positiveQuirks = ReadString(artifact, "plan.arguments.positiveQuirks");
        var negativeQuirks = ReadString(artifact, "plan.arguments.negativeQuirks");
        var nameSource = ReadOptionalStringPath(artifact, "plan.arguments.nameSource");
        var nameLanguage = ReadOptionalStringPath(artifact, "plan.arguments.nameLanguage");
        var nameSeed = ReadOptionalStringPath(artifact, "plan.arguments.nameSeed");
        var nameRenamePolicy = ReadOptionalStringPath(artifact, "plan.arguments.nameRenamePolicy");
        ValidateHeroNameRenamePolicy(nameRenamePolicy);
        var classDefinitions = ResolveHeroClassDefinitions(context, classSource);
        if (classDefinitions.Count == 0)
        {
            throw new InvalidDataException($"Hero class source produced no class ids: {classSource}");
        }

        var quirkCatalog = LoadEnabledQuirkDefinitions(context.GameWorkingDirectory);
        var namePool = ResolveHeroNamePool(context, nameSource, nameLanguage);
        if (!string.IsNullOrWhiteSpace(nameRenamePolicy) &&
            !nameRenamePolicy.Equals("none", StringComparison.OrdinalIgnoreCase) &&
            namePool is null)
        {
            throw new InvalidDataException($"Hero name rename policy '{nameRenamePolicy}' requires a configured hero name source.");
        }

        var file = context.LoadDecodedJsonFile("persist.roster.json");
        var baseRoot = EnsureObject(file.Root, "base_root");
        var heroes = EnsureObject(file.Root, "base_root.heroes");
        var existingHeroes = EnumerateRosterHeroes(heroes).ToArray();
        var usedSingletonQuirks = BuildUsedSingletonQuirkSet(existingHeroes, quirkCatalog);
        var renameHeroIds = existingHeroes
            .Where(hero => ShouldRenameHeroName(hero.ActorName, nameRenamePolicy))
            .Select(hero => hero.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedHeroNames = BuildUsedHeroNameSet(existingHeroes, renameHeroIds);
        var copyIndexesByHeroId = BuildHeroCopyIndexesByClass(existingHeroes);
        var renamed = 0;
        foreach (var hero in existingHeroes.Where(hero => renameHeroIds.Contains(hero.Id)))
        {
            var copyIndex = copyIndexesByHeroId.TryGetValue(hero.Id, out var value) ? value : 0;
            var heroId = int.TryParse(hero.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHeroId)
                ? parsedHeroId
                : 0;
            var heroName = SelectGeneratedHeroName(namePool, usedHeroNames, nameSeed, hero.HeroClass, copyIndex, heroId);
            if (heroName.Equals(hero.ActorName, StringComparison.Ordinal))
            {
                continue;
            }

            if (context.WriteChanges)
            {
                EnsureObject(hero.HeroRoot, "actor")["name"] = heroName;
            }

            renamed++;
        }

        var maxHeroId = Math.Max(GetMaxNumericKey(heroes), ReadOptionalInt(baseRoot, "nextGuid") is { } nextGuid ? nextGuid - 1 : -1);
        var nextHeroId = maxHeroId + 1;
        var added = 0;
        var unchanged = 0;
        var additionsByClass = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var classDefinition in classDefinitions)
        {
            var existingForClass = existingHeroes
                .Where(hero => hero.HeroClass.Equals(classDefinition.Id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (existingForClass.Length >= copiesPerClass)
            {
                unchanged++;
                continue;
            }

            for (var copyIndex = existingForClass.Length; copyIndex < copiesPerClass; copyIndex++)
            {
                var heroId = nextHeroId++;
                var heroName = SelectGeneratedHeroName(namePool, usedHeroNames, nameSeed, classDefinition.Id, copyIndex, heroId);
                var heroEntry = BuildCleanGeneratedRosterHeroEntry(
                    classDefinition,
                    heroId,
                    copyIndex,
                    level,
                    positiveQuirks,
                    negativeQuirks,
                    heroName,
                    quirkCatalog,
                    usedSingletonQuirks);
                if (context.WriteChanges)
                {
                    heroes[heroId.ToString(CultureInfo.InvariantCulture)] = heroEntry;
                    baseRoot["nextGuid"] = nextHeroId;
                }

                added++;
                additionsByClass[classDefinition.Id] = additionsByClass.TryGetValue(classDefinition.Id, out var value) ? value + 1 : 1;
            }
        }

        if (added + renamed > 0)
        {
            file.MarkChanged(added + renamed);
        }

        AddSuccessfulAction(
            context,
            artifactPath,
            artifact,
            file.Path,
            [
                $"ensure {classDefinitions.Count} hero classes from {classSource} copiesPerClass={copiesPerClass}",
                $"added={added} renamed={renamed} unchangedClasses={unchanged} level={level} positiveQuirks={positiveQuirks} negativeQuirks={negativeQuirks}",
                $"heroNames={FormatHeroNameSource(nameSource, nameLanguage, namePool)} renamePolicy={FormatHeroNameRenamePolicy(nameRenamePolicy)}",
                $"singletonQuirksUsed={usedSingletonQuirks.Count}",
                $"addedClasses={FormatAddedClassSummary(additionsByClass)}"
            ]);
    }

    private static void ApplyRosterSetSkillUnlocks(ApplyContext context, string artifactPath, JsonObject artifact)
    {
        var skillsMode = ReadString(artifact, "plan.arguments.skills");
        if (!skillsMode.Equals("all_unlocked_and_maxed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsupported roster skill unlock mode: {skillsMode}");
        }

        var classDefinitions = LoadEnabledHeroClassDefinitions(context.GameWorkingDirectory)
            .ToDictionary(definition => definition.Id, StringComparer.OrdinalIgnoreCase);
        if (classDefinitions.Count == 0)
        {
            throw new InvalidDataException("Hero class definition catalog produced no class ids.");
        }

        var file = context.LoadDecodedJsonFile("persist.roster.json");
        var heroes = EnsureObject(file.Root, "base_root.heroes");
        var rosterHeroes = EnumerateRosterHeroes(heroes).ToArray();
        var updated = 0;
        var unchanged = 0;
        var skippedUnknownClass = 0;
        var updatedCombatSkillCount = 0;
        var updatedCampingSkillCount = 0;

        foreach (var hero in rosterHeroes)
        {
            if (!classDefinitions.TryGetValue(hero.HeroClass, out var classDefinition))
            {
                skippedUnknownClass++;
                continue;
            }

            var combatSkillIds = classDefinition.CombatSkillIds;
            var campingSkillIds = classDefinition.CampingSkillIds;
            var skills = EnsureObject(hero.HeroRoot, "skills");
            var currentCombatSkills = skills["selected_combat_skills"] as JsonObject;
            var currentCampingSkills = skills["selected_camping_skills"] as JsonObject;
            var combatChanged = !SkillSelectionMatches(currentCombatSkills, combatSkillIds);
            var campingChanged = !SkillSelectionMatches(currentCampingSkills, campingSkillIds);
            if (!combatChanged && !campingChanged)
            {
                unchanged++;
                continue;
            }

            if (context.WriteChanges)
            {
                skills["selected_combat_skills"] = BuildAllSkillSelectionObject(combatSkillIds);
                skills["selected_camping_skills"] = BuildAllSkillSelectionObject(campingSkillIds);
            }

            updated++;
            updatedCombatSkillCount += combatSkillIds.Count;
            updatedCampingSkillCount += campingSkillIds.Count;
        }

        if (updated > 0)
        {
            file.MarkChanged(updated);
        }

        AddSuccessfulAction(
            context,
            artifactPath,
            artifact,
            file.Path,
            [
                $"set roster skills mode={skillsMode} heroes={rosterHeroes.Length}",
                $"updated={updated} unchanged={unchanged} skippedUnknownClass={skippedUnknownClass}",
                $"updatedSkillSlots combat={updatedCombatSkillCount} camping={updatedCampingSkillCount}"
            ]);
    }

    private static void ApplyRosterSetProgression(ApplyContext context, string artifactPath, JsonObject artifact)
    {
        var level = ReadString(artifact, "plan.arguments.level");
        var equipment = ReadString(artifact, "plan.arguments.equipment");
        var resolveXp = ReadString(artifact, "plan.arguments.resolveXp");
        var equipmentLevel = equipment.Equals("level", StringComparison.OrdinalIgnoreCase)
            ? level
            : equipment;
        var resolveLevel = resolveXp.Equals("level", StringComparison.OrdinalIgnoreCase)
            ? level
            : resolveXp;

        var classDefinitions = LoadEnabledHeroClassDefinitions(context.GameWorkingDirectory)
            .ToDictionary(definition => definition.Id, StringComparer.OrdinalIgnoreCase);
        if (classDefinitions.Count == 0)
        {
            throw new InvalidDataException("Hero class definition catalog produced no class ids.");
        }

        var file = context.LoadDecodedJsonFile("persist.roster.json");
        var heroes = EnsureObject(file.Root, "base_root.heroes");
        var rosterHeroes = EnumerateRosterHeroes(heroes).ToArray();
        var updated = 0;
        var unchanged = 0;
        var skippedUnknownClass = 0;
        var changedProperties = 0;

        foreach (var hero in rosterHeroes)
        {
            if (!classDefinitions.TryGetValue(hero.HeroClass, out var classDefinition))
            {
                skippedUnknownClass++;
                continue;
            }

            var heroChanged = false;
            if (SetIntPropertyIfChanged(hero.HeroRoot, "resolveXp", ResolveHeroResolveXp(resolveLevel), context.WriteChanges))
            {
                changedProperties++;
                heroChanged = true;
            }

            if (SetIntPropertyIfChanged(hero.HeroRoot, "weapon_rank", ResolveHeroEquipmentRank(equipmentLevel, classDefinition.MaxWeaponRank), context.WriteChanges))
            {
                changedProperties++;
                heroChanged = true;
            }

            if (SetIntPropertyIfChanged(hero.HeroRoot, "armour_rank", ResolveHeroEquipmentRank(equipmentLevel, classDefinition.MaxArmourRank), context.WriteChanges))
            {
                changedProperties++;
                heroChanged = true;
            }

            if (equipmentLevel.Equals("max", StringComparison.OrdinalIgnoreCase) && classDefinition.MaxHp is { } maxHp)
            {
                var actor = EnsureObject(hero.HeroRoot, "actor");
                if (SetDoublePropertyIfChanged(actor, "current_hp", maxHp, context.WriteChanges))
                {
                    changedProperties++;
                    heroChanged = true;
                }
            }

            if (heroChanged)
            {
                updated++;
            }
            else
            {
                unchanged++;
            }
        }

        if (changedProperties > 0)
        {
            file.MarkChanged(changedProperties);
        }

        AddSuccessfulAction(
            context,
            artifactPath,
            artifact,
            file.Path,
            [
                $"set roster progression level={level} equipment={equipment} resolveXp={resolveXp} heroes={rosterHeroes.Length}",
                $"updated={updated} unchanged={unchanged} skippedUnknownClass={skippedUnknownClass} changedProperties={changedProperties}"
            ]);
    }

    private static IReadOnlyList<RosterHeroClassDefinition> ResolveHeroClassDefinitions(ApplyContext context, string source)
    {
        return source switch
        {
            "content.hero_classes.enabled" => LoadEnabledHeroClassDefinitions(context.GameWorkingDirectory),
            _ => throw new InvalidDataException($"Unsupported hero class source: {source}")
        };
    }

    private static JsonObject BuildCleanGeneratedRosterHeroEntry(
        RosterHeroClassDefinition classDefinition,
        int heroId,
        int copyIndex,
        string level,
        string positiveQuirkPolicy,
        string negativeQuirkPolicy,
        string heroName,
        IReadOnlyList<QuirkDefinition> quirkCatalog,
        HashSet<string> usedSingletonQuirks)
    {
        var entry = new JsonObject();
        var heroRoot = EnsureObject(entry, "hero_file_data.raw_data.base_root");
        heroRoot["roster.status"] = 0;
        heroRoot["roster.before_on_start_town_visit_status"] = 0;
        heroRoot["roster.missing_duration"] = 0;
        heroRoot["roster.story_variation"] = 0;
        heroRoot["roster.missing_from"] = 0;
        heroRoot["roster.building_name"] = string.Empty;
        heroRoot["roster.timestamp"] = 0;
        heroRoot["heroClass"] = classDefinition.Id;
        heroRoot["resolveXp"] = ResolveHeroResolveXp(level);
        heroRoot["m_Stress"] = JsonFloat(0.0);
        heroRoot["is_death_heart_attack_completed"] = false;
        heroRoot["visited_deaths_door"] = false;
        heroRoot["deaths_door_enter_effect_round_cooldown"] = 0;
        heroRoot["has_had_heart_attack"] = false;
        heroRoot["backer_hero"] = false;
        heroRoot["steps_taken"] = 0;
        heroRoot["enemies_killed"] = 0;
        heroRoot["weapon_rank"] = ResolveHeroEquipmentRank(level, classDefinition.MaxWeaponRank);
        heroRoot["armour_rank"] = ResolveHeroEquipmentRank(level, classDefinition.MaxArmourRank);
        heroRoot["dd_test_survived"] = 0;
        heroRoot["affliction_type_id"] = string.Empty;
        heroRoot["affliction_severity"] = 0;
        heroRoot["virtue_type_id"] = string.Empty;
        heroRoot["provisions_consumed"] = 0;
        heroRoot["number_of_successful_darkest_dungeon_quests"] = 0;
        heroRoot["is_from_town_event"] = false;
        heroRoot["dungeon_history"] = new JsonArray();
        heroRoot["has_item_Tracking"] = true;
        heroRoot["item_tracking"] = new JsonObject { ["supply"] = new JsonObject() };

        var actor = EnsureObject(heroRoot, "actor");
        actor["name"] = heroName;
        actor["current_hp"] = JsonFloat(classDefinition.MaxHp ?? ReadOptionalDouble(actor, "current_hp") ?? 1.0);
        actor["stunned"] = 0;
        actor["combat_ready"] = false;
        actor["damage_source_data"] = 0;
        actor["damage_source_type"] = 0;
        actor["damage_type"] = 0;
        actor["colour_variation"] = copyIndex % 4;
        actor["enemy_rank_targets"] = 0;
        actor["friendly_rank_targets"] = 0;
        actor["performing_turn"] = 0;
        actor["controlling_actor_guid"] = 0;
        actor["controlling_duration"] = 0;
        actor["current_mode_id"] = 0;
        actor["rounds_in_ranks"] = 0;
        actor["check_round_ranks"] = 0;
        actor["health_damage_blocks"] = 0;
        actor["buff_group_next_guid"] = 0;
        actor["buff_group"] = new JsonObject();
        actor["actor_dot"] = new JsonObject();

        var positiveQuirkIds = SelectQuirkIds(quirkCatalog, positive: true, positiveQuirkPolicy, classDefinition.Id, copyIndex, usedSingletonQuirks);
        var negativeQuirkIds = SelectQuirkIds(quirkCatalog, positive: false, negativeQuirkPolicy, classDefinition.Id, copyIndex, usedSingletonQuirks);
        heroRoot["quirks"] = BuildRosterQuirkObject(positiveQuirkIds.Concat(negativeQuirkIds));
        heroRoot["skills"] = new JsonObject
        {
            ["selected_combat_skills"] = BuildSkillSelectionObject(
                classDefinition.CombatSkillIds,
                Math.Max(1, classDefinition.SelectedCombatSkillMax ?? DefaultSelectedCombatSkillCount)),
            ["selected_camping_skills"] = BuildSkillSelectionObject(
                classDefinition.CampingSkillIds,
                DefaultSelectedCampingSkillCount)
        };
        heroRoot["trinkets"] = new JsonObject { ["items"] = new JsonObject() };
        return entry;
    }

    private static IReadOnlyList<RosterHeroEntry> EnumerateRosterHeroes(JsonObject heroes)
    {
        return heroes
            .Select(pair =>
            {
                if (pair.Value is not JsonObject entry)
                {
                    return null;
                }

                var heroRoot = TryGetObject(entry, "hero_file_data.raw_data.base_root");
                if (heroRoot is null)
                {
                    return null;
                }

                var heroClass = ReadOptionalString(heroRoot, "heroClass");
                if (string.IsNullOrWhiteSpace(heroClass))
                {
                    return null;
                }

                var actorName = TryGetObject(heroRoot, "actor") is { } actor
                    ? ReadOptionalString(actor, "name")
                    : string.Empty;
                return new RosterHeroEntry(pair.Key, entry, heroRoot, heroClass, actorName);
            })
            .Where(hero => hero is not null)
            .Select(hero => hero!)
            .ToArray();
    }

    private static IReadOnlyList<string>? ResolveHeroNamePool(ApplyContext context, string source, string language)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var resolvedLanguage = string.IsNullOrWhiteSpace(language) ? "english" : language;
        return source switch
        {
            "content.hero_names.enabled" => LoadEnabledHeroNames(context.GameWorkingDirectory, resolvedLanguage),
            _ => throw new InvalidDataException($"Unsupported hero name source: {source}")
        };
    }

    private static HashSet<string> BuildUsedSingletonQuirkSet(
        IReadOnlyList<RosterHeroEntry> heroes,
        IReadOnlyList<QuirkDefinition> quirkCatalog)
    {
        var singletonIds = quirkCatalog
            .Where(quirk => quirk.IsSingleton)
            .Select(quirk => quirk.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (singletonIds.Count == 0)
        {
            return used;
        }

        foreach (var hero in heroes)
        {
            foreach (var quirkId in EnumerateHeroQuirkIds(hero.HeroRoot))
            {
                if (singletonIds.Contains(quirkId))
                {
                    used.Add(quirkId);
                }
            }
        }

        return used;
    }

    private static IEnumerable<string> EnumerateHeroQuirkIds(JsonObject heroRoot)
    {
        if (heroRoot["quirks"] is not JsonObject quirks)
        {
            yield break;
        }

        foreach (var pair in quirks)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                yield return pair.Key;
            }
        }
    }

    private static IReadOnlyList<string> LoadEnabledHeroNames(string gameWorkingDirectory, string language)
    {
        var path = Path.Combine(gameWorkingDirectory, "localization", "names.string_table.xml");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Hero name string table was not found: {path}", path);
        }

        var document = XDocument.Load(path);
        var languageElement = document.Root?
            .Elements("language")
            .FirstOrDefault(element => ((string?)element.Attribute("id"))?.Equals(language, StringComparison.OrdinalIgnoreCase) == true);
        if (languageElement is null)
        {
            throw new InvalidDataException($"Hero name string table does not contain language '{language}': {path}");
        }

        var names = languageElement
            .Elements("entry")
            .Select(element => new
            {
                Id = (string?)element.Attribute("id") ?? string.Empty,
                Value = element.Value.Trim()
            })
            .Where(entry => entry.Id.StartsWith("hero_name_", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(entry.Value))
            .OrderBy(entry => TryReadHeroNameIndex(entry.Id) ?? int.MaxValue)
            .ThenBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
        {
            throw new InvalidDataException($"Hero name string table produced no names for language '{language}': {path}");
        }

        return names;
    }

    private static int? TryReadHeroNameIndex(string id)
    {
        const string Prefix = "hero_name_";
        return id.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(id[Prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }

    private static HashSet<string> BuildUsedHeroNameSet(IEnumerable<RosterHeroEntry> heroes, IReadOnlySet<string> excludedHeroIds)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hero in heroes)
        {
            if (!excludedHeroIds.Contains(hero.Id) && !string.IsNullOrWhiteSpace(hero.ActorName))
            {
                names.Add(hero.ActorName);
            }
        }

        return names;
    }

    private static IReadOnlyDictionary<string, int> BuildHeroCopyIndexesByClass(IReadOnlyList<RosterHeroEntry> heroes)
    {
        var nextCopyIndexesByClass = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var copyIndexesByHeroId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var hero in heroes.OrderBy(hero => int.TryParse(hero.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : int.MaxValue))
        {
            var copyIndex = nextCopyIndexesByClass.TryGetValue(hero.HeroClass, out var nextCopyIndex) ? nextCopyIndex : 0;
            copyIndexesByHeroId[hero.Id] = copyIndex;
            nextCopyIndexesByClass[hero.HeroClass] = copyIndex + 1;
        }

        return copyIndexesByHeroId;
    }

    private static void ValidateHeroNameRenamePolicy(string policy)
    {
        if (string.IsNullOrWhiteSpace(policy) ||
            policy.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            policy.Equals("generated_placeholders", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidDataException($"Unsupported hero name rename policy: {policy}");
    }

    private static bool ShouldRenameHeroName(string heroName, string policy)
    {
        return !string.IsNullOrWhiteSpace(policy) &&
            policy.Equals("generated_placeholders", StringComparison.OrdinalIgnoreCase) &&
            heroName.StartsWith("DDRF ", StringComparison.Ordinal);
    }

    private static string SelectGeneratedHeroName(
        IReadOnlyList<string>? namePool,
        HashSet<string> usedHeroNames,
        string seed,
        string classId,
        int copyIndex,
        int heroId)
    {
        if (namePool is null)
        {
            var generated = BuildGeneratedHeroName(classId, heroId);
            usedHeroNames.Add(generated);
            return generated;
        }

        var resolvedSeed = string.IsNullOrWhiteSpace(seed) ? "ddrt.roster.ensureClassInstances" : seed;
        var start = StableIndex($"{resolvedSeed}:{classId}:{copyIndex.ToString(CultureInfo.InvariantCulture)}:{heroId.ToString(CultureInfo.InvariantCulture)}", namePool.Count);
        for (var offset = 0; offset < namePool.Count; offset++)
        {
            var candidate = namePool[(start + offset) % namePool.Count];
            if (usedHeroNames.Add(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidDataException($"Hero name pool is exhausted after {namePool.Count.ToString(CultureInfo.InvariantCulture)} unique names.");
    }

    private static int StableIndex(string value, int modulo)
    {
        if (modulo <= 0)
        {
            throw new InvalidDataException("Stable index modulo must be greater than zero.");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var raw = ((uint)hash[0] << 24) |
            ((uint)hash[1] << 16) |
            ((uint)hash[2] << 8) |
            hash[3];
        return (int)(raw % modulo);
    }

    private static IReadOnlyList<RosterHeroClassDefinition> LoadEnabledHeroClassDefinitions(string gameWorkingDirectory)
    {
        var definitions = new SortedDictionary<string, RosterHeroClassDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var infoPath in EnumerateCampaignHeroInfoFiles(gameWorkingDirectory))
        {
            var classId = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(infoPath));
            if (string.IsNullOrWhiteSpace(classId))
            {
                continue;
            }

            definitions[classId] = ReadRosterHeroClassDefinition(infoPath, classId);
        }

        return definitions.Values.ToArray();
    }

    private static IEnumerable<string> EnumerateCampaignHeroInfoFiles(string gameWorkingDirectory)
    {
        var heroDirectory = Path.Combine(gameWorkingDirectory, "heroes");
        if (Directory.Exists(heroDirectory))
        {
            foreach (var directory in Directory.EnumerateDirectories(heroDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var classId = Path.GetFileName(directory);
                var infoPath = Path.Combine(directory, $"{classId}.info.darkest");
                if (File.Exists(infoPath))
                {
                    yield return infoPath;
                }
            }
        }

        var dlcDirectory = Path.Combine(gameWorkingDirectory, "dlc");
        if (!Directory.Exists(dlcDirectory))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(dlcDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(name) ||
                !char.IsDigit(name[0]) ||
                name.Contains("arena", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*.info.darkest", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var classId = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
                var parent = Path.GetFileName(Path.GetDirectoryName(path));
                if (!string.IsNullOrWhiteSpace(classId) &&
                    classId.Equals(parent, StringComparison.OrdinalIgnoreCase) &&
                    path.Contains($"{Path.DirectorySeparatorChar}heroes{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    yield return path;
                }
            }
        }
    }

    private static RosterHeroClassDefinition ReadRosterHeroClassDefinition(string path, string classId)
    {
        var maxHp = (int?)null;
        var maxWeaponRank = (int?)null;
        var maxArmourRank = (int?)null;
        var selectedCombatSkillsMax = (int?)null;
        var combatSkillIds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(path, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var kind = line[..separator].Trim();
            var attributes = ParseDarkestAttributeLine(line[(separator + 1)..]);
            switch (kind)
            {
                case "weapon":
                    maxWeaponRank = Math.Max(maxWeaponRank ?? 0, TryReadTrailingLevel(ReadDarkestString(attributes, "name")) ?? 0);
                    break;
                case "armour":
                    maxArmourRank = Math.Max(maxArmourRank ?? 0, TryReadTrailingLevel(ReadDarkestString(attributes, "name")) ?? 0);
                    if (ReadDarkestInt(attributes, "hp") is { } hp)
                    {
                        maxHp = Math.Max(maxHp ?? 0, hp);
                    }
                    break;
                case "combat_skill":
                    if (ReadDarkestString(attributes, "id") is { } skillId)
                    {
                        combatSkillIds.Add(skillId);
                    }
                    break;
                case "skill_selection":
                    selectedCombatSkillsMax = ReadDarkestInt(attributes, "number_of_selected_combat_skills_max");
                    break;
            }
        }

        return new RosterHeroClassDefinition(
            classId,
            maxHp,
            maxWeaponRank,
            maxArmourRank,
            selectedCombatSkillsMax,
            combatSkillIds.ToArray(),
            LoadCampingSkillIdsForHeroClass(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..")), classId));
    }

    private static IReadOnlyList<string> LoadCampingSkillIdsForHeroClass(string searchRoot, string classId)
    {
        var gameRoot = FindGameRootFromHeroSearchRoot(searchRoot);
        if (gameRoot is null)
        {
            return [];
        }

        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var campingDirectory = Path.Combine(gameRoot, "raid", "camping");
        if (Directory.Exists(campingDirectory))
        {
            ReadCampingSkillIds(campingDirectory, classId, result);
        }

        var dlcDirectory = Path.Combine(gameRoot, "dlc");
        if (Directory.Exists(dlcDirectory))
        {
            foreach (var directory in Directory.EnumerateDirectories(dlcDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(directory);
                if (string.IsNullOrWhiteSpace(name) ||
                    !char.IsDigit(name[0]) ||
                    name.Contains("arena", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ReadCampingSkillIds(directory, classId, result);
            }
        }

        return result.ToArray();
    }

    private static string? FindGameRootFromHeroSearchRoot(string searchRoot)
    {
        var current = new DirectoryInfo(searchRoot);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "heroes")) &&
                Directory.Exists(Path.Combine(current.FullName, "raid")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static void ReadCampingSkillIds(string root, string classId, SortedSet<string> result)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*.camping_skills.json", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (!document.RootElement.TryGetProperty("skills", out var skills) ||
                skills.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var skill in skills.EnumerateArray())
            {
                if (skill.ValueKind != JsonValueKind.Object ||
                    !skill.TryGetProperty("id", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String ||
                    !skill.TryGetProperty("hero_classes", out var classesElement) ||
                    classesElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var includesClass = classesElement
                    .EnumerateArray()
                    .Any(item => item.ValueKind == JsonValueKind.String &&
                        item.GetString()!.Equals(classId, StringComparison.OrdinalIgnoreCase));
                if (includesClass && !string.IsNullOrWhiteSpace(idElement.GetString()))
                {
                    result.Add(idElement.GetString()!);
                }
            }
        }
    }

    private static IReadOnlyList<QuirkDefinition> LoadEnabledQuirkDefinitions(string gameWorkingDirectory)
    {
        var definitions = new SortedDictionary<string, QuirkDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateCampaignQuirkLibraryFiles(gameWorkingDirectory))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (!document.RootElement.TryGetProperty("quirks", out var quirks) ||
                quirks.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var quirk in quirks.EnumerateArray())
            {
                if (quirk.ValueKind != JsonValueKind.Object ||
                    !quirk.TryGetProperty("id", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(idElement.GetString()) ||
                    !quirk.TryGetProperty("is_positive", out var positiveElement) ||
                    positiveElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    continue;
                }

                var id = idElement.GetString()!;
                var randomChance = quirk.TryGetProperty("random_chance", out var randomChanceElement) &&
                    randomChanceElement.ValueKind == JsonValueKind.Number &&
                    randomChanceElement.TryGetDouble(out var chance)
                    ? chance
                    : 1.0;
                var isDisease = quirk.TryGetProperty("is_disease", out var diseaseElement) &&
                    diseaseElement.ValueKind is JsonValueKind.True;
                if (randomChance <= 0 || isDisease)
                {
                    continue;
                }

                definitions[id] = new QuirkDefinition(
                    id,
                    positiveElement.GetBoolean(),
                    ReadStringArrayProperty(quirk, "incompatible_quirks"),
                    ReadStringArrayProperty(quirk, "tags"));
            }
        }

        return definitions.Values.ToArray();
    }

    private static IEnumerable<string> EnumerateCampaignQuirkLibraryFiles(string gameWorkingDirectory)
    {
        var baseQuirkDirectory = Path.Combine(gameWorkingDirectory, "shared", "quirk");
        if (Directory.Exists(baseQuirkDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(baseQuirkDirectory, "*.quirk_library.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }

        var dlcDirectory = Path.Combine(gameWorkingDirectory, "dlc");
        if (!Directory.Exists(dlcDirectory))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(dlcDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(name) ||
                !char.IsDigit(name[0]) ||
                name.Contains("arena", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*.quirk_library.json", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private static IReadOnlyList<string> SelectQuirkIds(
        IReadOnlyList<QuirkDefinition> catalog,
        bool positive,
        string policy,
        string classId,
        int copyIndex,
        HashSet<string> usedSingletonQuirks)
    {
        var count = policy switch
        {
            "none" => 0,
            "one_random" => 1,
            "full_random" => FullQuirkSlotCount,
            _ => throw new InvalidDataException($"Unsupported quirk policy: {policy}")
        };
        if (count == 0)
        {
            return [];
        }

        var selected = new List<QuirkDefinition>();
        foreach (var quirk in catalog
                     .Where(quirk => quirk.IsPositive == positive)
                     .OrderBy(quirk => StableOrderKey($"{classId}:{copyIndex}:{positive}:{policy}:{quirk.Id}"), StringComparer.Ordinal)
                     .ThenBy(quirk => quirk.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (quirk.IsSingleton && usedSingletonQuirks.Contains(quirk.Id))
            {
                continue;
            }

            if (selected.Any(existing => QuirksConflict(existing, quirk)))
            {
                continue;
            }

            selected.Add(quirk);
            if (quirk.IsSingleton)
            {
                usedSingletonQuirks.Add(quirk.Id);
            }

            if (selected.Count == count)
            {
                break;
            }
        }

        if (selected.Count < count)
        {
            throw new InvalidDataException($"Quirk catalog did not contain enough compatible {(positive ? "positive" : "negative")} quirks for policy {policy}.");
        }

        return selected.Select(quirk => quirk.Id).ToArray();
    }

    private static bool QuirksConflict(QuirkDefinition first, QuirkDefinition second)
    {
        return first.Id.Equals(second.Id, StringComparison.OrdinalIgnoreCase) ||
            first.IncompatibleQuirks.Contains(second.Id, StringComparer.OrdinalIgnoreCase) ||
            second.IncompatibleQuirks.Contains(first.Id, StringComparer.OrdinalIgnoreCase);
    }

    private static JsonObject BuildRosterQuirkObject(IEnumerable<string> quirkIds)
    {
        var result = new JsonObject();
        foreach (var quirkId in quirkIds)
        {
            result[quirkId] = new JsonObject
            {
                ["is_new"] = false,
                ["is_locked"] = false,
                ["mission_count"] = 0,
                ["replaces_quirk"] = 0,
                ["replaces_quirk_viewed"] = false,
                ["evolution_duration_remaining"] = 0
            };
        }

        return result;
    }

    private static JsonObject BuildSkillSelectionObject(IReadOnlyList<string> skillIds, int maxCount)
    {
        var result = new JsonObject();
        foreach (var skillId in skillIds.Take(maxCount))
        {
            result[skillId] = 0;
        }

        return result;
    }

    private static JsonObject BuildAllSkillSelectionObject(IReadOnlyList<string> skillIds)
    {
        var result = new JsonObject();
        foreach (var skillId in skillIds)
        {
            result[skillId] = 0;
        }

        return result;
    }

    private static bool SkillSelectionMatches(JsonObject? current, IReadOnlyList<string> skillIds)
    {
        if (current is null || current.Count != skillIds.Count)
        {
            return false;
        }

        var expected = skillIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in current)
        {
            if (!expected.Contains(pair.Key) ||
                pair.Value is not JsonValue value ||
                !value.TryGetValue<int>(out var skillValue) ||
                skillValue != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int ResolveHeroResolveXp(string level)
    {
        if (level.Equals("max", StringComparison.OrdinalIgnoreCase))
        {
            return MaxResolveXp;
        }

        if (int.TryParse(level, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericLevel))
        {
            return numericLevel switch
            {
                <= 0 => 0,
                1 => 2,
                2 => 8,
                3 => 14,
                4 => 26,
                _ => MaxResolveXp
            };
        }

        throw new InvalidDataException($"Unsupported hero level value: {level}");
    }

    private static int ResolveHeroEquipmentRank(string level, int? maxRank)
    {
        if (level.Equals("max", StringComparison.OrdinalIgnoreCase))
        {
            return maxRank ?? 0;
        }

        return int.TryParse(level, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericLevel)
            ? Math.Clamp(numericLevel - 1, 0, maxRank ?? 0)
            : throw new InvalidDataException($"Unsupported hero level value: {level}");
    }

    private static bool SetIntPropertyIfChanged(JsonObject root, string key, int value, bool writeChanges)
    {
        if (root[key] is JsonValue current &&
            current.TryGetValue<int>(out var currentValue) &&
            currentValue == value)
        {
            return false;
        }

        if (writeChanges)
        {
            root[key] = value;
        }

        return true;
    }

    private static bool SetDoublePropertyIfChanged(JsonObject root, string key, double value, bool writeChanges)
    {
        if (root[key] is JsonValue current &&
            current.TryGetValue<double>(out var currentValue) &&
            Math.Abs(currentValue - value) < 0.000001)
        {
            return false;
        }

        if (writeChanges)
        {
            root[key] = JsonFloat(value);
        }

        return true;
    }

    private static string BuildGeneratedHeroName(string classId, int heroId)
    {
        var displayClass = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(classId.Replace('_', ' '));
        return $"DDRF {displayClass} {heroId.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string ReadOptionalStringPath(JsonObject root, string path)
    {
        JsonNode? current = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            current = current is JsonObject obj ? obj[part] : null;
            if (current is null)
            {
                return string.Empty;
            }
        }

        if (current is JsonValue value && value.TryGetValue<string>(out var result))
        {
            return result;
        }

        throw new InvalidDataException($"{path} must be a string when present.");
    }

    private static int GetMaxNumericKey(JsonObject obj)
    {
        var max = -1;
        foreach (var key in obj.Select(pair => pair.Key))
        {
            if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                max = Math.Max(max, value);
            }
        }

        return max;
    }

    private static JsonObject? TryGetObject(JsonObject root, string path)
    {
        JsonNode? current = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            current = current is JsonObject obj ? obj[part] : null;
            if (current is null)
            {
                return null;
            }
        }

        return current as JsonObject;
    }

    private static double? ReadOptionalDouble(JsonObject root, string key)
    {
        return root[key] is JsonValue value && value.TryGetValue<double>(out var result)
            ? result
            : null;
    }

    private static IReadOnlyList<string> ReadStringArrayProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var arrayElement) ||
            arrayElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return arrayElement
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            .Select(item => item.GetString()!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseDarkestAttributeLine(string value)
    {
        var tokens = TokenizeDarkestLine(value);
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith(".", StringComparison.Ordinal) || token.Length <= 1)
            {
                continue;
            }

            var key = token[1..];
            var values = new List<string>();
            while (i + 1 < tokens.Count && !tokens[i + 1].StartsWith(".", StringComparison.Ordinal))
            {
                i++;
                values.Add(tokens[i]);
            }

            result[key] = values;
        }

        return result;
    }

    private static IReadOnlyList<string> TokenizeDarkestLine(string value)
    {
        var tokens = new List<string>();
        for (var i = 0; i < value.Length;)
        {
            while (i < value.Length && char.IsWhiteSpace(value[i]))
            {
                i++;
            }

            if (i >= value.Length)
            {
                break;
            }

            if (value[i] == '"')
            {
                i++;
                var start = i;
                while (i < value.Length && value[i] != '"')
                {
                    i++;
                }

                tokens.Add(value[start..Math.Min(i, value.Length)]);
                if (i < value.Length && value[i] == '"')
                {
                    i++;
                }

                continue;
            }

            var tokenStart = i;
            while (i < value.Length && !char.IsWhiteSpace(value[i]))
            {
                i++;
            }

            tokens.Add(value[tokenStart..i]);
        }

        return tokens;
    }

    private static string? ReadDarkestString(
        IReadOnlyDictionary<string, IReadOnlyList<string>> attributes,
        string key)
    {
        return attributes.TryGetValue(key, out var values) && values.Count > 0
            ? string.IsNullOrWhiteSpace(values[0]) ? null : values[0]
            : null;
    }

    private static int? ReadDarkestInt(
        IReadOnlyDictionary<string, IReadOnlyList<string>> attributes,
        string key)
    {
        return attributes.TryGetValue(key, out var values) &&
            values.Count > 0 &&
            int.TryParse(values[0].TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static int? TryReadTrailingLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var underscore = value.LastIndexOf('_');
        return underscore >= 0 &&
            underscore + 1 < value.Length &&
            int.TryParse(value[(underscore + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
                ? result
                : null;
    }

    private static string StableOrderKey(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static JsonNode JsonFloat(double value)
    {
        return JsonNode.Parse(value.ToString("0.0###############", CultureInfo.InvariantCulture))!;
    }

    private static string FormatAddedClassSummary(IReadOnlyDictionary<string, int> additionsByClass)
    {
        return additionsByClass.Count == 0
            ? "none"
            : string.Join(
                ",",
                additionsByClass
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => $"{pair.Key}:{pair.Value.ToString(CultureInfo.InvariantCulture)}"));
    }

    private static string FormatHeroNameSource(string source, string language, IReadOnlyList<string>? namePool)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "generated";
        }

        var resolvedLanguage = string.IsNullOrWhiteSpace(language) ? "english" : language;
        var count = namePool?.Count.ToString(CultureInfo.InvariantCulture) ?? "0";
        return $"{source} language={resolvedLanguage} count={count}";
    }

    private static string FormatHeroNameRenamePolicy(string policy)
    {
        return string.IsNullOrWhiteSpace(policy) ? "none" : policy;
    }

    private sealed record RosterHeroEntry(string Id, JsonObject Entry, JsonObject HeroRoot, string HeroClass, string ActorName);

    private sealed record RosterHeroClassDefinition(
        string Id,
        int? MaxHp,
        int? MaxWeaponRank,
        int? MaxArmourRank,
        int? SelectedCombatSkillMax,
        IReadOnlyList<string> CombatSkillIds,
        IReadOnlyList<string> CampingSkillIds);

    private sealed record QuirkDefinition(
        string Id,
        bool IsPositive,
        IReadOnlyList<string> IncompatibleQuirks,
        IReadOnlyList<string> Tags)
    {
        public bool IsSingleton => Tags.Contains("singleton", StringComparer.OrdinalIgnoreCase);
    }
}
