namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private static SaveStateUpgradeFacts BuildUpgradeFacts(SaveStateFileReport? upgrades, UpgradeDefinitionCatalog upgradeCatalog)
        {
            if (upgrades is null)
            {
                return EmptyUpgradeFacts(null, upgradeCatalog);
            }

            var purchaseIds = MergeAllDirectChildIds(
                    ExtractAllDirectChildIds(upgrades.DsonObjectPaths, "base_root.purchases"),
                    ExtractAllDirectChildIds(GetDsonScalars(upgrades), "base_root.purchases"))
                .Select(id => int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : (int?)null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .OrderBy(id => id)
                .ToArray();
            if (purchaseIds.Length == 0)
            {
                return EmptyUpgradeFacts(TryGetInt(upgrades, "base_root.version"), upgradeCatalog);
            }

            var purchases = new List<SaveStateUpgradePurchaseFacts>();
            foreach (var purchaseId in purchaseIds)
            {
                var path = $"base_root.purchases.{purchaseId.ToString(CultureInfo.InvariantCulture)}";
                var treeId = TryGetUInt(upgrades, $"{path}.tree_id");
                var requirementCode = TryGetString(upgrades, $"{path}.requirement_code");
                var lookup = treeId.HasValue ? upgradeCatalog.Find(treeId.Value) : null;
                var definition = lookup is { IsAmbiguous: false } ? lookup.PreferredDefinition : null;
                purchases.Add(new SaveStateUpgradePurchaseFacts(
                    purchaseId,
                    TryGetInt(upgrades, $"{path}.instance_number"),
                    treeId,
                    ResolveTreeName(lookup),
                    lookup?.IsAmbiguous == true,
                    definition?.SourceRelativePath,
                    definition?.Tags ?? [],
                    BuildRequirementDefinitionFacts(definition, requirementCode),
                    requirementCode,
                    TryGetBool(upgrades, $"{path}.is_purchased")));
            }

            var treeFacts = purchases
                .Where(purchase => purchase.TreeId.HasValue)
                .GroupBy(purchase => purchase.TreeId!.Value)
                .OrderBy(group => group.Key)
                .Select(group =>
                {
                    var groupPurchases = group.ToArray();
                    var lookup = upgradeCatalog.Find(group.Key);
                    var definition = lookup is { IsAmbiguous: false } ? lookup.PreferredDefinition : null;
                    return new SaveStateUpgradeTreeFacts(
                        group.Key,
                        ResolveTreeName(lookup),
                        lookup?.IsAmbiguous == true,
                        definition?.SourceRelativePath,
                        definition?.IsInstanced,
                        definition?.Tags ?? [],
                        definition?.Requirements.Select(BuildRequirementDefinitionFacts).ToArray() ?? [],
                        groupPurchases.Length,
                        groupPurchases.Count(purchase => purchase.IsPurchased == true),
                        groupPurchases.Count(purchase => purchase.IsPurchased == false),
                        groupPurchases
                            .Select(purchase => purchase.InstanceNumber)
                            .Where(value => value.HasValue)
                            .Select(value => value!.Value)
                            .Distinct()
                            .OrderBy(value => value)
                            .ToArray(),
                        groupPurchases
                            .Select(purchase => purchase.RequirementCode)
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Select(value => value!)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        groupPurchases
                            .Where(purchase => purchase.IsPurchased == true)
                            .Select(purchase => purchase.RequirementCode)
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Select(value => value!)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                            .ToArray());
                })
                .ToArray();

            return new SaveStateUpgradeFacts(
                TryGetInt(upgrades, "base_root.version"),
                purchases.Count,
                purchases.Count(purchase => purchase.IsPurchased == true),
                purchases.Count(purchase => purchase.IsPurchased == false),
                purchases.Count(purchase => !purchase.IsPurchased.HasValue),
                treeFacts.Length,
                upgradeCatalog.SourceFileCount,
                upgradeCatalog.DefinitionCount,
                upgradeCatalog.NameCandidateCount,
                treeFacts.Count(tree => !string.IsNullOrWhiteSpace(tree.TreeName) && !tree.TreeNameAmbiguous),
                treeFacts.Count(tree => string.IsNullOrWhiteSpace(tree.TreeName)),
                treeFacts.Count(tree => tree.TreeNameAmbiguous),
                purchases.Take(1000).ToArray(),
                treeFacts.Take(1000).ToArray());
        }

        private static SaveStateUpgradeFacts EmptyUpgradeFacts(int? version, UpgradeDefinitionCatalog upgradeCatalog)
        {
            return new SaveStateUpgradeFacts(
                version,
                0,
                0,
                0,
                0,
                0,
                upgradeCatalog.SourceFileCount,
                upgradeCatalog.DefinitionCount,
                upgradeCatalog.NameCandidateCount,
                0,
                0,
                0,
                [],
                []);
        }

        private static string? ResolveTreeName(UpgradeDefinitionLookup? lookup)
        {
            return lookup is { IsAmbiguous: false }
                ? lookup.PreferredDefinition?.Id
                : null;
        }

        private static SaveStateUpgradeRequirementDefinitionFacts? BuildRequirementDefinitionFacts(
            UpgradeTreeDefinition? definition,
            string? requirementCode)
        {
            if (definition is null || string.IsNullOrWhiteSpace(requirementCode))
            {
                return null;
            }

            return definition.Requirements
                .FirstOrDefault(requirement => requirement.Code.Equals(requirementCode, StringComparison.OrdinalIgnoreCase))
                is { } requirement
                ? BuildRequirementDefinitionFacts(requirement)
                : null;
        }

        private static SaveStateUpgradeRequirementDefinitionFacts BuildRequirementDefinitionFacts(UpgradeRequirementDefinition requirement)
        {
            return new SaveStateUpgradeRequirementDefinitionFacts(
                requirement.Code,
                requirement.CurrencyCost,
                requirement.PrerequisiteResolveLevel,
                requirement.Prerequisites
                    .Select(prerequisite => new SaveStateUpgradePrerequisiteDefinitionFacts(prerequisite.TreeId, prerequisite.RequirementCode))
                    .ToArray());
        }

        private sealed class UpgradeDefinitionCatalog
        {
            private static readonly JsonDocumentOptions UpgradeJsonOptions = new()
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };

            private readonly IReadOnlyDictionary<uint, UpgradeDefinitionLookup> _byTreeId;

            private UpgradeDefinitionCatalog(
                IReadOnlyDictionary<uint, UpgradeDefinitionLookup> byTreeId,
                int sourceFileCount,
                int definitionCount,
                int nameCandidateCount)
            {
                _byTreeId = byTreeId;
                SourceFileCount = sourceFileCount;
                DefinitionCount = definitionCount;
                NameCandidateCount = nameCandidateCount;
            }

            public int SourceFileCount { get; }

            public int DefinitionCount { get; }

            public int NameCandidateCount { get; }

            public static UpgradeDefinitionCatalog Load(string gameWorkingDirectory, string? gameMode, List<string> accessIssues)
            {
                if (string.IsNullOrWhiteSpace(gameWorkingDirectory) || !Directory.Exists(gameWorkingDirectory))
                {
                    accessIssues.Add($"Upgrade definition catalog skipped because game directory was not found: {gameWorkingDirectory}");
                    return new UpgradeDefinitionCatalog(new Dictionary<uint, UpgradeDefinitionLookup>(), 0, 0, 0);
                }

                var normalizedGameMode = NormalizeGameMode(gameMode);
                var definitions = new List<UpgradeTreeDefinition>();
                var sourceFileCount = 0;
                foreach (var path in Directory.EnumerateFiles(gameWorkingDirectory, "*.upgrades.json", SearchOption.AllDirectories)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var relativePath = GetRelativeCatalogPath(gameWorkingDirectory, path);
                        if (!IsApplicableToGameMode(relativePath, normalizedGameMode))
                        {
                            continue;
                        }

                        var fileDefinitions = ReadUpgradeDefinitionFile(path, relativePath);
                        if (fileDefinitions.Count == 0)
                        {
                            continue;
                        }

                        sourceFileCount++;
                        definitions.AddRange(fileDefinitions);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        accessIssues.Add($"Upgrade definition catalog skipped file {path}: {ex.Message}");
                    }
                }

                foreach (var path in Directory.EnumerateFiles(gameWorkingDirectory, "*.camping_skills.json", SearchOption.AllDirectories)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var relativePath = GetRelativeCatalogPath(gameWorkingDirectory, path);
                        if (!IsApplicableToGameMode(relativePath, normalizedGameMode))
                        {
                            continue;
                        }

                        var fileDefinitions = ReadCampingSkillDefinitionFile(path, relativePath);
                        if (fileDefinitions.Count == 0)
                        {
                            continue;
                        }

                        sourceFileCount++;
                        definitions.AddRange(fileDefinitions);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        accessIssues.Add($"Upgrade definition catalog skipped camping file {path}: {ex.Message}");
                    }
                }

                foreach (var path in Directory.EnumerateFiles(gameWorkingDirectory, "persist.upgrades.json", SearchOption.AllDirectories)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var relativePath = GetRelativeCatalogPath(gameWorkingDirectory, path);
                        if (!IsApplicableToGameMode(relativePath, normalizedGameMode))
                        {
                            continue;
                        }

                        definitions.AddRange(ReadStartingSaveUpgradeAliases(path, relativePath));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        accessIssues.Add($"Upgrade definition catalog skipped save template file {path}: {ex.Message}");
                    }
                }

                var byTreeId = definitions
                    .GroupBy(definition => definition.TreeId)
                    .ToDictionary(
                        group => group.Key,
                        group => new UpgradeDefinitionLookup(group.Key, group.ToArray()),
                        EqualityComparer<uint>.Default);

                return new UpgradeDefinitionCatalog(
                    byTreeId,
                    sourceFileCount,
                    definitions.Count(definition => definition.HasDefinition),
                    definitions.Count(definition => !definition.HasDefinition));
            }

            public UpgradeDefinitionLookup? Find(uint treeId)
            {
                return _byTreeId.TryGetValue(treeId, out var lookup) ? lookup : null;
            }

            private static IReadOnlyList<UpgradeTreeDefinition> ReadUpgradeDefinitionFile(string path, string relativePath)
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path), UpgradeJsonOptions);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("trees", out var treesElement)
                    || treesElement.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                var sourcePriority = GetUpgradeDefinitionSourcePriority(relativePath);
                var definitions = new List<UpgradeTreeDefinition>();
                foreach (var treeElement in treesElement.EnumerateArray())
                {
                    if (treeElement.ValueKind != JsonValueKind.Object
                        || !treeElement.TryGetProperty("id", out var idElement)
                        || idElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var id = idElement.GetString();
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    var isInstanced = TryGetBool(treeElement, "is_instanced");
                    var tags = treeElement.TryGetProperty("tags", out var tagsElement)
                        ? ReadStringArray(tagsElement)
                        : [];
                    var requirements = treeElement.TryGetProperty("requirements", out var requirementsElement)
                        ? ReadRequirements(requirementsElement)
                        : [];

                    definitions.Add(new UpgradeTreeDefinition(
                        id,
                        HashDsonName(id),
                        isInstanced,
                        tags,
                        relativePath,
                        sourcePriority,
                        requirements,
                        true,
                        "upgrade_tree"));
                }

                return definitions;
            }

            private static IReadOnlyList<UpgradeTreeDefinition> ReadCampingSkillDefinitionFile(string path, string relativePath)
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path), UpgradeJsonOptions);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("skills", out var skillsElement)
                    || skillsElement.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                var sourcePriority = GetUpgradeDefinitionSourcePriority(relativePath);
                var definitions = new List<UpgradeTreeDefinition>();
                foreach (var skillElement in skillsElement.EnumerateArray())
                {
                    if (skillElement.ValueKind != JsonValueKind.Object
                        || !skillElement.TryGetProperty("id", out var idElement)
                        || idElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var skillId = idElement.GetString();
                    if (string.IsNullOrWhiteSpace(skillId))
                    {
                        continue;
                    }

                    var heroClasses = skillElement.TryGetProperty("hero_classes", out var heroClassesElement)
                        ? ReadStringArray(heroClassesElement)
                        : [];
                    if (heroClasses.Count == 0)
                    {
                        continue;
                    }

                    var requirements = skillElement.TryGetProperty("upgrade_requirements", out var requirementsElement)
                        ? ReadRequirements(requirementsElement)
                        : [];
                    foreach (var heroClass in heroClasses)
                    {
                        var id = $"{heroClass}.{skillId}";
                        definitions.Add(new UpgradeTreeDefinition(
                            id,
                            HashDsonName(id),
                            true,
                            ["camping_skill"],
                            relativePath,
                            sourcePriority,
                            requirements,
                            true,
                            "camping_skill"));
                    }
                }

                return definitions;
            }

            private static IReadOnlyList<UpgradeTreeDefinition> ReadStartingSaveUpgradeAliases(string path, string relativePath)
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path), UpgradeJsonOptions);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("data", out var dataElement)
                    || dataElement.ValueKind != JsonValueKind.Object
                    || !dataElement.TryGetProperty("purchases", out var purchasesElement)
                    || purchasesElement.ValueKind != JsonValueKind.Object)
                {
                    return [];
                }

                var definitions = new List<UpgradeTreeDefinition>();
                foreach (var purchaseProperty in purchasesElement.EnumerateObject())
                {
                    if (string.IsNullOrWhiteSpace(purchaseProperty.Name)
                        || purchaseProperty.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var treeId = TryGetUInt(purchaseProperty.Value, "tree_id") ?? HashDsonName(purchaseProperty.Name);
                    definitions.Add(new UpgradeTreeDefinition(
                        purchaseProperty.Name,
                        treeId,
                        null,
                        [],
                        relativePath,
                        -100,
                        [],
                        false,
                        "starting_save_alias"));
                }

                return definitions;
            }

            private static string GetRelativeCatalogPath(string gameWorkingDirectory, string path)
            {
                return Path.GetRelativePath(gameWorkingDirectory, path)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
            }

            private static string NormalizeGameMode(string? gameMode)
            {
                if (string.IsNullOrWhiteSpace(gameMode))
                {
                    return "base";
                }

                var normalized = gameMode.Trim().ToLowerInvariant();
                return normalized switch
                {
                    "darkest" => "base",
                    "normal" => "base",
                    _ => normalized
                };
            }

            private static bool IsApplicableToGameMode(string relativePath, string gameMode)
            {
                var parts = relativePath
                    .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    if (!parts[i].Equals("modes", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return parts[i + 1].Equals(gameMode, StringComparison.OrdinalIgnoreCase);
                }

                return true;
            }

            private static int GetUpgradeDefinitionSourcePriority(string relativePath)
            {
                if (relativePath.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
                {
                    return -10;
                }

                if (relativePath.StartsWith("modes/", StringComparison.OrdinalIgnoreCase)
                    || relativePath.Contains("/modes/", StringComparison.OrdinalIgnoreCase))
                {
                    return 20;
                }

                if (relativePath.StartsWith("dlc/", StringComparison.OrdinalIgnoreCase))
                {
                    return 10;
                }

                return 0;
            }

            private static IReadOnlyList<UpgradeRequirementDefinition> ReadRequirements(JsonElement requirementsElement)
            {
                if (requirementsElement.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                var requirements = new List<UpgradeRequirementDefinition>();
                foreach (var requirementElement in requirementsElement.EnumerateArray())
                {
                    if (requirementElement.ValueKind != JsonValueKind.Object
                        || !requirementElement.TryGetProperty("code", out var codeElement)
                        || codeElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var code = codeElement.GetString();
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        continue;
                    }

                    requirements.Add(new UpgradeRequirementDefinition(
                        code,
                        requirementElement.TryGetProperty("currency_cost", out var currencyElement)
                            ? ReadCurrencyCost(currencyElement)
                            : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                        TryGetInt(requirementElement, "prerequisite_resolve_level"),
                        requirementElement.TryGetProperty("prerequisite_requirements", out var prerequisitesElement)
                            ? ReadPrerequisites(prerequisitesElement)
                            : []));
                }

                return requirements;
            }

            private static IReadOnlyDictionary<string, int> ReadCurrencyCost(JsonElement currencyElement)
            {
                var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (currencyElement.ValueKind != JsonValueKind.Array)
                {
                    return result;
                }

                foreach (var entry in currencyElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object
                        || !entry.TryGetProperty("type", out var typeElement)
                        || typeElement.ValueKind != JsonValueKind.String
                        || !entry.TryGetProperty("amount", out var amountElement)
                        || amountElement.ValueKind != JsonValueKind.Number
                        || !amountElement.TryGetInt32(out var amount))
                    {
                        continue;
                    }

                    var type = typeElement.GetString();
                    if (!string.IsNullOrWhiteSpace(type))
                    {
                        result[type] = amount;
                    }
                }

                return result;
            }

            private static IReadOnlyList<UpgradePrerequisiteDefinition> ReadPrerequisites(JsonElement prerequisitesElement)
            {
                if (prerequisitesElement.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                var prerequisites = new List<UpgradePrerequisiteDefinition>();
                foreach (var prerequisiteElement in prerequisitesElement.EnumerateArray())
                {
                    if (prerequisiteElement.ValueKind != JsonValueKind.Object
                        || !prerequisiteElement.TryGetProperty("tree_id", out var treeElement)
                        || treeElement.ValueKind != JsonValueKind.String
                        || !prerequisiteElement.TryGetProperty("requirement_code", out var codeElement)
                        || codeElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var treeId = treeElement.GetString();
                    var code = codeElement.GetString();
                    if (!string.IsNullOrWhiteSpace(treeId) && !string.IsNullOrWhiteSpace(code))
                    {
                        prerequisites.Add(new UpgradePrerequisiteDefinition(treeId, code));
                    }
                }

                return prerequisites;
            }

            private static IReadOnlyList<string> ReadStringArray(JsonElement element)
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

            private static bool? TryGetBool(JsonElement element, string propertyName)
            {
                return element.TryGetProperty(propertyName, out var property)
                    ? property.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null
                    }
                    : null;
            }

            private static int? TryGetInt(JsonElement element, string propertyName)
            {
                return element.TryGetProperty(propertyName, out var property)
                    && property.ValueKind == JsonValueKind.Number
                    && property.TryGetInt32(out var value)
                    ? value
                    : null;
            }

            private static uint? TryGetUInt(JsonElement element, string propertyName)
            {
                return element.TryGetProperty(propertyName, out var property)
                    && property.ValueKind == JsonValueKind.Number
                    && property.TryGetUInt32(out var value)
                    ? value
                    : null;
            }
        }

        private static uint HashDsonName(string value)
        {
            var hash = 0u;
            foreach (var b in Encoding.UTF8.GetBytes(value))
            {
                unchecked
                {
                    hash = hash * 53u + b;
                }
            }

            return hash;
        }

        private sealed record UpgradeDefinitionLookup(
            uint TreeId,
            IReadOnlyList<UpgradeTreeDefinition> Definitions)
        {
            public bool IsAmbiguous { get; } = Definitions
                .Select(definition => definition.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .Any();

            public UpgradeTreeDefinition? PreferredDefinition { get; } = Definitions
                .GroupBy(definition => definition.Id, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Any(definition => definition.HasDefinition))
                .ThenByDescending(group => group.Max(definition => definition.SourcePriority))
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(definition => definition.HasDefinition)
                    .ThenByDescending(definition => definition.SourcePriority)
                    .ThenBy(definition => definition.SourceRelativePath, StringComparer.OrdinalIgnoreCase)
                    .First())
                .FirstOrDefault();
        }

        private sealed record UpgradeTreeDefinition(
            string Id,
            uint TreeId,
            bool? IsInstanced,
            IReadOnlyList<string> Tags,
            string SourceRelativePath,
            int SourcePriority,
            IReadOnlyList<UpgradeRequirementDefinition> Requirements,
            bool HasDefinition,
            string DefinitionKind);

        private sealed record UpgradeRequirementDefinition(
            string Code,
            IReadOnlyDictionary<string, int> CurrencyCost,
            int? PrerequisiteResolveLevel,
            IReadOnlyList<UpgradePrerequisiteDefinition> Prerequisites);

        private sealed record UpgradePrerequisiteDefinition(
            string TreeId,
            string RequirementCode);

    }
}
