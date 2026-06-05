namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static SaveStateHeroDefinitionFacts BuildHeroDefinitionFacts(
            string gameWorkingDirectory,
            string? gameMode,
            List<string> accessIssues)
        {
            var normalizedGameMode = NormalizeDefinitionGameMode(gameMode);
            if (string.IsNullOrWhiteSpace(gameWorkingDirectory) || !Directory.Exists(gameWorkingDirectory))
            {
                accessIssues.Add($"Hero definition catalog skipped because game directory was not found: {gameWorkingDirectory}");
                return EmptyHeroDefinitionFacts(normalizedGameMode);
            }

            var builders = new Dictionary<string, HeroClassDefinitionBuilder>(StringComparer.OrdinalIgnoreCase);
            var sourceFileCount = 0;
            var heroRoot = Path.Combine(gameWorkingDirectory, "heroes");
            if (Directory.Exists(heroRoot))
            {
                foreach (var heroDirectory in Directory.EnumerateDirectories(heroRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var heroClass = Path.GetFileName(heroDirectory);
                    var infoPath = Path.Combine(heroDirectory, $"{heroClass}.info.darkest");
                    if (!File.Exists(infoPath))
                    {
                        continue;
                    }

                    try
                    {
                        var builder = ReadHeroInfoDefinitionFile(
                            infoPath,
                            GetRelativeDefinitionPath(gameWorkingDirectory, infoPath),
                            heroClass);
                        builders[heroClass] = builder;
                        sourceFileCount++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
                    {
                        accessIssues.Add($"Hero definition catalog skipped info file {infoPath}: {ex.Message}");
                    }
                }
            }

            var campingRoot = Path.Combine(gameWorkingDirectory, "raid", "camping");
            if (Directory.Exists(campingRoot))
            {
                foreach (var path in Directory.EnumerateFiles(campingRoot, "*.camping_skills.json", SearchOption.TopDirectoryOnly)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var added = ReadBaseCampingSkillDefinitionFile(
                            path,
                            GetRelativeDefinitionPath(gameWorkingDirectory, path),
                            builders);
                        if (added)
                        {
                            sourceFileCount++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        accessIssues.Add($"Hero definition catalog skipped camping file {path}: {ex.Message}");
                    }
                }
            }

            var classes = builders
                .Values
                .OrderBy(builder => builder.HeroClass, StringComparer.OrdinalIgnoreCase)
                .Select(builder => builder.Build())
                .ToArray();

            return new SaveStateHeroDefinitionFacts(
                "base_game_no_mods",
                normalizedGameMode,
                sourceFileCount,
                classes.Length,
                classes.Sum(hero => hero.CombatSkills.Count),
                classes.Sum(hero => hero.CampingSkills.Count),
                classes.Sum(hero => hero.Weapons.Count),
                classes.Sum(hero => hero.Armours.Count),
                classes);
        }

        private static SaveStateHeroDefinitionFacts EmptyHeroDefinitionFacts(string gameMode)
        {
            return new SaveStateHeroDefinitionFacts(
                "base_game_no_mods",
                gameMode,
                0,
                0,
                0,
                0,
                0,
                0,
                []);
        }

        private static HeroClassDefinitionBuilder ReadHeroInfoDefinitionFile(
            string path,
            string relativePath,
            string heroClass)
        {
            var builder = new HeroClassDefinitionBuilder(heroClass, relativePath);
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
                        builder.Weapons.Add(BuildHeroEquipmentDefinition("weapon", attributes));
                        break;
                    case "armour":
                        builder.Armours.Add(BuildHeroEquipmentDefinition("armour", attributes));
                        break;
                    case "combat_skill":
                        builder.CombatSkillLevels.Add(BuildHeroCombatSkillLevelDefinition(attributes));
                        break;
                    case "tag":
                        if (ReadDarkestString(attributes, "id") is { } tag)
                        {
                            builder.Tags.Add(tag);
                        }
                        break;
                    case "id_index":
                        builder.IdIndex = ReadDarkestInt(attributes, "index");
                        break;
                    case "skill_selection":
                        builder.CanSelectCombatSkills = ReadDarkestBool(attributes, "can_select_combat_skills");
                        builder.SelectedCombatSkillsMax = ReadDarkestInt(attributes, "number_of_selected_combat_skills_max");
                        break;
                    case "generation":
                        builder.Generation = BuildHeroGenerationDefinition(attributes);
                        break;
                }
            }

            return builder;
        }

        private static SaveStateHeroEquipmentDefinitionFacts BuildHeroEquipmentDefinition(
            string kind,
            IReadOnlyDictionary<string, IReadOnlyList<string>> attributes)
        {
            var name = ReadDarkestString(attributes, "name");
            var damage = ReadDarkestValues(attributes, "dmg");
            return new SaveStateHeroEquipmentDefinitionFacts(
                kind,
                TryReadTrailingLevel(name),
                name,
                ReadDarkestInt(attributes, "upgradeRequirementCode"),
                ReadDarkestString(attributes, "atk"),
                ReadDarkestString(attributes, "def"),
                damage.Count > 0 ? TryParseDefinitionInt(damage[0]) : null,
                damage.Count > 1 ? TryParseDefinitionInt(damage[1]) : null,
                ReadDarkestString(attributes, "crit"),
                ReadDarkestString(attributes, "prot"),
                ReadDarkestInt(attributes, "hp"),
                ReadDarkestInt(attributes, "spd"));
        }

        private static HeroCombatSkillLevelBuilder BuildHeroCombatSkillLevelDefinition(
            IReadOnlyDictionary<string, IReadOnlyList<string>> attributes)
        {
            return new HeroCombatSkillLevelBuilder(
                ReadDarkestString(attributes, "id") ?? string.Empty,
                new SaveStateHeroCombatSkillLevelDefinitionFacts(
                    ReadDarkestInt(attributes, "level"),
                    ReadDarkestString(attributes, "type"),
                    ReadDarkestString(attributes, "atk"),
                    JoinDarkestValues(attributes, "dmg"),
                    ReadDarkestString(attributes, "crit"),
                    JoinDarkestValues(attributes, "launch"),
                    JoinDarkestValues(attributes, "target"),
                    JoinDarkestValues(attributes, "move"),
                    JoinDarkestValues(attributes, "heal"),
                    ReadDarkestInt(attributes, "per_battle_limit"),
                    ReadDarkestBool(attributes, "is_crit_valid"),
                    ReadDarkestBool(attributes, "is_stall_invalidating"),
                    ReadDarkestValues(attributes, "effect")),
                ReadDarkestBool(attributes, "generation_guaranteed") == true);
        }

        private static SaveStateHeroGenerationDefinitionFacts BuildHeroGenerationDefinition(
            IReadOnlyDictionary<string, IReadOnlyList<string>> attributes)
        {
            return new SaveStateHeroGenerationDefinitionFacts(
                ReadDarkestBool(attributes, "is_generation_enabled"),
                ReadDarkestInt(attributes, "number_of_positive_quirks_min"),
                ReadDarkestInt(attributes, "number_of_positive_quirks_max"),
                ReadDarkestInt(attributes, "number_of_negative_quirks_min"),
                ReadDarkestInt(attributes, "number_of_negative_quirks_max"),
                ReadDarkestInt(attributes, "number_of_class_specific_camping_skills"),
                ReadDarkestInt(attributes, "number_of_shared_camping_skills"),
                ReadDarkestInt(attributes, "number_of_random_combat_skills"),
                ReadDarkestInt(attributes, "number_of_cards_in_deck"),
                ReadDarkestDouble(attributes, "card_chance"));
        }

        private static bool ReadBaseCampingSkillDefinitionFile(
            string path,
            string relativePath,
            IReadOnlyDictionary<string, HeroClassDefinitionBuilder> builders)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("skills", out var skillsElement)
                || skillsElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var added = false;
            foreach (var skillElement in skillsElement.EnumerateArray())
            {
                if (skillElement.ValueKind != JsonValueKind.Object
                    || !skillElement.TryGetProperty("id", out var idElement)
                    || idElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var id = idElement.GetString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var heroClasses = skillElement.TryGetProperty("hero_classes", out var heroClassesElement)
                    ? ReadHeroDefinitionStringArray(heroClassesElement)
                    : [];
                if (heroClasses.Count == 0)
                {
                    continue;
                }

                var skill = new SaveStateHeroCampingSkillDefinitionFacts(
                    id,
                    ReadJsonInt(skillElement, "level"),
                    ReadJsonInt(skillElement, "cost"),
                    ReadJsonInt(skillElement, "use_limit"),
                    relativePath,
                    ReadCampingUpgradeRequirementCodes(skillElement));
                foreach (var heroClass in heroClasses)
                {
                    if (!builders.TryGetValue(heroClass, out var builder))
                    {
                        continue;
                    }

                    builder.CampingSkills.Add(skill);
                    added = true;
                }
            }

            return added;
        }

        private static IReadOnlyList<string> ReadCampingUpgradeRequirementCodes(JsonElement skillElement)
        {
            if (!skillElement.TryGetProperty("upgrade_requirements", out var requirementsElement)
                || requirementsElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return requirementsElement
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("code", out var codeElement)
                    && codeElement.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(codeElement.GetString()))
                .Select(item => item.GetProperty("code").GetString()!)
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

        private static IReadOnlyList<string> ReadDarkestValues(
            IReadOnlyDictionary<string, IReadOnlyList<string>> attributes,
            string key)
        {
            return attributes.TryGetValue(key, out var values)
                ? values
                : [];
        }

        private static string? ReadDarkestString(
            IReadOnlyDictionary<string, IReadOnlyList<string>> attributes,
            string key)
        {
            return attributes.TryGetValue(key, out var values) && values.Count > 0
                ? EmptyToNull(values[0])
                : null;
        }

        private static string? JoinDarkestValues(
            IReadOnlyDictionary<string, IReadOnlyList<string>> attributes,
            string key)
        {
            return attributes.TryGetValue(key, out var values) && values.Count > 0
                ? string.Join(' ', values)
                : null;
        }

        private static int? ReadDarkestInt(
            IReadOnlyDictionary<string, IReadOnlyList<string>> attributes,
            string key)
        {
            return attributes.TryGetValue(key, out var values) && values.Count > 0
                ? TryParseDefinitionInt(values[0])
                : null;
        }

        private static bool? ReadDarkestBool(
            IReadOnlyDictionary<string, IReadOnlyList<string>> attributes,
            string key)
        {
            return attributes.TryGetValue(key, out var values)
                && values.Count > 0
                && bool.TryParse(values[0], out var parsed)
                ? parsed
                : null;
        }

        private static double? ReadDarkestDouble(
            IReadOnlyDictionary<string, IReadOnlyList<string>> attributes,
            string key)
        {
            return attributes.TryGetValue(key, out var values)
                && values.Count > 0
                && double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static int? TryReadTrailingLevel(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var underscore = value.LastIndexOf('_');
            return underscore >= 0 && underscore + 1 < value.Length
                ? TryParseDefinitionInt(value[(underscore + 1)..])
                : null;
        }

        private static int? TryParseDefinitionInt(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = value.Trim().TrimEnd('%');
            return int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static int? ReadJsonInt(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.Number
                && property.TryGetInt32(out var value)
                ? value
                : null;
        }

        private static IReadOnlyList<string> ReadHeroDefinitionStringArray(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return element
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string NormalizeDefinitionGameMode(string? gameMode)
        {
            if (string.IsNullOrWhiteSpace(gameMode))
            {
                return "base";
            }

            return gameMode.Trim().ToLowerInvariant() switch
            {
                "darkest" => "base",
                "normal" => "base",
                var value => value
            };
        }

        private static string GetRelativeDefinitionPath(string gameWorkingDirectory, string path)
        {
            return Path.GetRelativePath(gameWorkingDirectory, path)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
        }

        private sealed class HeroClassDefinitionBuilder
        {
            public HeroClassDefinitionBuilder(string heroClass, string sourceRelativePath)
            {
                HeroClass = heroClass;
                SourceRelativePath = sourceRelativePath;
            }

            public string HeroClass { get; }
            public string SourceRelativePath { get; }
            public int? IdIndex { get; set; }
            public HashSet<string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);
            public bool? CanSelectCombatSkills { get; set; }
            public int? SelectedCombatSkillsMax { get; set; }
            public SaveStateHeroGenerationDefinitionFacts? Generation { get; set; }
            public List<SaveStateHeroEquipmentDefinitionFacts> Weapons { get; } = [];
            public List<SaveStateHeroEquipmentDefinitionFacts> Armours { get; } = [];
            public List<HeroCombatSkillLevelBuilder> CombatSkillLevels { get; } = [];
            public List<SaveStateHeroCampingSkillDefinitionFacts> CampingSkills { get; } = [];

            public SaveStateHeroClassDefinitionFacts Build()
            {
                var combatSkills = CombatSkillLevels
                    .Where(level => !string.IsNullOrWhiteSpace(level.Id))
                    .GroupBy(level => level.Id, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                    {
                        var levels = group
                            .Select(level => level.Level)
                            .OrderBy(level => level.Level ?? int.MaxValue)
                            .ToArray();
                        return new SaveStateHeroCombatSkillDefinitionFacts(
                            group.Key,
                            levels.Length,
                            group.Any(level => level.GenerationGuaranteed),
                            levels);
                    })
                    .ToArray();

                return new SaveStateHeroClassDefinitionFacts(
                    HeroClass,
                    SourceRelativePath,
                    IdIndex,
                    Tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToArray(),
                    CanSelectCombatSkills,
                    SelectedCombatSkillsMax,
                    Generation,
                    Weapons.OrderBy(item => item.Level ?? int.MaxValue).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                    Armours.OrderBy(item => item.Level ?? int.MaxValue).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                    combatSkills,
                    CampingSkills
                        .GroupBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group
                            .OrderBy(skill => skill.SourceRelativePath, StringComparer.OrdinalIgnoreCase)
                            .First())
                        .OrderBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
                        .ToArray());
            }
        }

        private sealed record HeroCombatSkillLevelBuilder(
            string Id,
            SaveStateHeroCombatSkillLevelDefinitionFacts Level,
            bool GenerationGuaranteed);
    }
}
