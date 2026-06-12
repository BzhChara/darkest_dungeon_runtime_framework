using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static partial class ManagedActionSaveApplier
{
    private static readonly JsonDocumentOptions UpgradeJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static void ApplyUpgradeEnsurePurchases(ApplyContext context, string artifactPath, JsonObject artifact)
    {
        var source = ReadString(artifact, "plan.arguments.source");
        var mode = ReadString(artifact, "plan.arguments.mode");
        if (!mode.Equals("all_requirements", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsupported upgrade purchase mode: {mode}");
        }

        var categorySet = ReadStringSet(ReadNode(artifact, "plan.arguments.categories"));
        if (categorySet.Count == 0)
        {
            throw new InvalidDataException("plan.arguments.categories must contain at least one category.");
        }

        var instanceSource = ReadString(artifact, "plan.arguments.instanceSource");
        var definitions = ResolveUpgradeDefinitions(context, source)
            .Where(definition => categorySet.Contains("all") || categorySet.Contains(definition.Category))
            .ToArray();
        if (definitions.Length == 0)
        {
            throw new InvalidDataException($"Upgrade source produced no matching definitions: source={source} categories={string.Join(",", categorySet)}");
        }

        var rosterHeroesByClass = ResolveUpgradeInstanceSource(context, instanceSource);
        var file = context.LoadDecodedJsonFile("persist.upgrades.json");
        var purchases = EnsureObject(file.Root, "base_root.purchases");
        var result = EnsureUpgradePurchases(purchases, definitions, rosterHeroesByClass, context.WriteChanges);
        if (result.ChangedCount > 0)
        {
            file.MarkChanged(result.ChangedCount);
        }

        AddSuccessfulAction(
            context,
            artifactPath,
            artifact,
            file.Path,
            [
                $"ensure upgrade purchases source={source} mode={mode} categories={string.Join(",", categorySet.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}",
                $"definitions={definitions.Length} requirements={result.RequirementCount} instances={result.InstanceCount}",
                $"added={result.AddedCount} updated={result.UpdatedCount} unchanged={result.UnchangedCount} skippedDefinitions={result.SkippedDefinitionCount}"
            ]);
    }

    private static IReadOnlyList<UpgradePurchaseDefinition> ResolveUpgradeDefinitions(ApplyContext context, string source)
    {
        return source switch
        {
            "content.upgrades.enabled" => LoadEnabledUpgradeDefinitions(context.GameWorkingDirectory),
            _ => throw new InvalidDataException($"Unsupported upgrade source: {source}")
        };
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<int>> ResolveUpgradeInstanceSource(ApplyContext context, string instanceSource)
    {
        if (!instanceSource.Equals("profile.roster.heroes", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsupported upgrade instance source: {instanceSource}");
        }

        var file = context.LoadDecodedJsonFile("persist.roster.json");
        var heroes = EnsureObject(file.Root, "base_root.heroes");
        return EnumerateRosterHeroes(heroes)
            .Select(hero => int.TryParse(hero.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                ? (Hero: hero, Id: (int?)id)
                : (Hero: hero, Id: null))
            .Where(pair => pair.Id.HasValue)
            .GroupBy(pair => pair.Hero.HeroClass, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<int>)group
                    .Select(pair => pair.Id!.Value)
                    .Distinct()
                    .OrderBy(value => value)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static UpgradeEnsurePurchasesResult EnsureUpgradePurchases(
        JsonObject purchases,
        IReadOnlyList<UpgradePurchaseDefinition> definitions,
        IReadOnlyDictionary<string, IReadOnlyList<int>> rosterHeroesByClass,
        bool writeChanges)
    {
        var existing = new Dictionary<UpgradePurchaseKey, JsonObject>();
        var maxNumericKey = -1;
        foreach (var pair in purchases)
        {
            if (int.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericKey))
            {
                maxNumericKey = Math.Max(maxNumericKey, numericKey);
            }

            if (pair.Value is not JsonObject purchase)
            {
                continue;
            }

            var treeId = ReadOptionalUInt(purchase, "tree_id");
            var requirementCode = ReadOptionalString(purchase, "requirement_code");
            var instanceNumber = ReadOptionalInt(purchase, "instance_number");
            if (treeId.HasValue && instanceNumber.HasValue && !string.IsNullOrWhiteSpace(requirementCode))
            {
                existing[new UpgradePurchaseKey(treeId.Value, requirementCode, instanceNumber.Value)] = purchase;
            }
        }

        var added = 0;
        var updated = 0;
        var unchanged = 0;
        var skippedDefinitions = 0;
        var requirementCount = 0;
        var instanceCount = 0;

        foreach (var definition in definitions)
        {
            var instanceNumbers = ResolveUpgradeInstanceNumbers(definition, rosterHeroesByClass);
            if (instanceNumbers.Count == 0)
            {
                skippedDefinitions++;
                continue;
            }

            instanceCount += instanceNumbers.Count;
            foreach (var instanceNumber in instanceNumbers)
            {
                foreach (var requirementCode in definition.RequirementCodes)
                {
                    requirementCount++;
                    var key = new UpgradePurchaseKey(definition.TreeId, requirementCode, instanceNumber);
                    if (existing.TryGetValue(key, out var purchase))
                    {
                        var isPurchased = ReadOptionalBool(purchase, "is_purchased");
                        if (isPurchased == true)
                        {
                            unchanged++;
                            continue;
                        }

                        if (writeChanges)
                        {
                            purchase["is_purchased"] = true;
                        }

                        updated++;
                        continue;
                    }

                    if (writeChanges)
                    {
                        maxNumericKey++;
                        var created = new JsonObject
                        {
                            ["instance_number"] = instanceNumber,
                            ["tree_id"] = unchecked((int)definition.TreeId),
                            ["requirement_code"] = requirementCode,
                            ["is_purchased"] = true
                        };
                        purchases[maxNumericKey.ToString(CultureInfo.InvariantCulture)] = created;
                        existing[key] = created;
                    }

                    added++;
                }
            }
        }

        return new UpgradeEnsurePurchasesResult(
            requirementCount,
            instanceCount,
            added,
            updated,
            unchanged,
            skippedDefinitions);
    }

    private static IReadOnlyList<int> ResolveUpgradeInstanceNumbers(
        UpgradePurchaseDefinition definition,
        IReadOnlyDictionary<string, IReadOnlyList<int>> rosterHeroesByClass)
    {
        if (!definition.IsInstanced)
        {
            return [0];
        }

        if (string.IsNullOrWhiteSpace(definition.HeroClass))
        {
            return [];
        }

        return rosterHeroesByClass.TryGetValue(definition.HeroClass, out var heroIds)
            ? heroIds
            : [];
    }

    private static IReadOnlyList<UpgradePurchaseDefinition> LoadEnabledUpgradeDefinitions(string gameWorkingDirectory)
    {
        var definitions = new Dictionary<string, UpgradePurchaseDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateCampaignUpgradeDefinitionFiles(gameWorkingDirectory))
        {
            foreach (var definition in ReadUpgradeDefinitionFile(path))
            {
                definitions.TryAdd(definition.Id, definition);
            }
        }

        foreach (var path in EnumerateCampaignCampingSkillFiles(gameWorkingDirectory))
        {
            foreach (var definition in ReadCampingSkillUpgradeDefinitions(path))
            {
                definitions.TryAdd(definition.Id, definition);
            }
        }

        return definitions.Values
            .OrderBy(definition => definition.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(definition => definition.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> EnumerateCampaignUpgradeDefinitionFiles(string gameWorkingDirectory)
    {
        var baseUpgradeDirectory = Path.Combine(gameWorkingDirectory, "upgrades");
        if (Directory.Exists(baseUpgradeDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(baseUpgradeDirectory, "*.upgrades.json", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }

        foreach (var path in EnumerateNonModDlcFiles(gameWorkingDirectory, "*.upgrades.json"))
        {
            yield return path;
        }
    }

    private static IEnumerable<string> EnumerateCampaignCampingSkillFiles(string gameWorkingDirectory)
    {
        var baseCampingDirectory = Path.Combine(gameWorkingDirectory, "raid", "camping");
        if (Directory.Exists(baseCampingDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(baseCampingDirectory, "*.camping_skills.json", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }

        foreach (var path in EnumerateNonModDlcFiles(gameWorkingDirectory, "*.camping_skills.json"))
        {
            yield return path;
        }
    }

    private static IEnumerable<string> EnumerateNonModDlcFiles(string gameWorkingDirectory, string searchPattern)
    {
        var dlcDirectory = Path.Combine(gameWorkingDirectory, "dlc");
        if (!Directory.Exists(dlcDirectory))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(dlcDirectory)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(name) ||
                !char.IsDigit(name[0]) ||
                name.Contains("arena", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, searchPattern, SearchOption.AllDirectories)
                         .Where(path => !IsModeSpecificPath(path))
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private static bool IsModeSpecificPath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(part => part.Equals("modes", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<UpgradePurchaseDefinition> ReadUpgradeDefinitionFile(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path), UpgradeJsonOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("trees", out var treesElement) ||
            treesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var definitions = new List<UpgradePurchaseDefinition>();
        foreach (var treeElement in treesElement.EnumerateArray())
        {
            if (treeElement.ValueKind != JsonValueKind.Object ||
                !treeElement.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var id = idElement.GetString();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var requirementCodes = ReadUpgradeRequirementCodes(treeElement, "requirements");
            if (requirementCodes.Count == 0)
            {
                continue;
            }

            var tags = treeElement.TryGetProperty("tags", out var tagsElement)
                ? ReadStringArray(tagsElement)
                : [];
            var isInstanced = ReadOptionalBool(treeElement, "is_instanced") == true;
            definitions.Add(new UpgradePurchaseDefinition(
                id,
                DsonHash.HashName(id),
                ClassifyUpgradeCategory(tags, "upgrade_tree"),
                isInstanced,
                isInstanced ? ReadHeroClassFromUpgradeTreeId(id) : null,
                requirementCodes));
        }

        return definitions;
    }

    private static IReadOnlyList<UpgradePurchaseDefinition> ReadCampingSkillUpgradeDefinitions(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path), UpgradeJsonOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("skills", out var skillsElement) ||
            skillsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var definitions = new List<UpgradePurchaseDefinition>();
        foreach (var skillElement in skillsElement.EnumerateArray())
        {
            if (skillElement.ValueKind != JsonValueKind.Object ||
                !skillElement.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var skillId = idElement.GetString();
            if (string.IsNullOrWhiteSpace(skillId))
            {
                continue;
            }

            var requirementCodes = ReadUpgradeRequirementCodes(skillElement, "upgrade_requirements");
            if (requirementCodes.Count == 0)
            {
                continue;
            }

            var heroClasses = skillElement.TryGetProperty("hero_classes", out var heroClassesElement)
                ? ReadStringArray(heroClassesElement)
                : [];
            foreach (var heroClass in heroClasses)
            {
                var id = $"{heroClass}.{skillId}";
                definitions.Add(new UpgradePurchaseDefinition(
                    id,
                    DsonHash.HashName(id),
                    "camping_skill",
                    true,
                    heroClass,
                    requirementCodes));
            }
        }

        return definitions;
    }

    private static IReadOnlyList<string> ReadUpgradeRequirementCodes(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var requirementsElement) ||
            requirementsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return requirementsElement
            .EnumerateArray()
            .Where(requirement => requirement.ValueKind == JsonValueKind.Object &&
                requirement.TryGetProperty("code", out var codeElement) &&
                codeElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(codeElement.GetString()))
            .Select(requirement => requirement.GetProperty("code").GetString()!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ClassifyUpgradeCategory(IReadOnlyList<string> tags, string fallback)
    {
        if (tags.Contains("building", StringComparer.OrdinalIgnoreCase))
        {
            return "building";
        }

        if (tags.Contains("combat_skill", StringComparer.OrdinalIgnoreCase))
        {
            return "combat_skill";
        }

        if (tags.Contains("camping_skill", StringComparer.OrdinalIgnoreCase))
        {
            return "camping_skill";
        }

        if (tags.Contains("weapon", StringComparer.OrdinalIgnoreCase))
        {
            return "weapon";
        }

        if (tags.Contains("armour", StringComparer.OrdinalIgnoreCase))
        {
            return "armour";
        }

        return fallback;
    }

    private static string? ReadHeroClassFromUpgradeTreeId(string treeId)
    {
        var separator = treeId.IndexOf('.');
        return separator > 0 ? treeId[..separator] : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return element
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            .Select(item => item.GetString()!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static HashSet<string> ReadStringSet(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array
                .Select(item => item?.GetValue<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
        {
            return text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        throw new InvalidDataException("Expected a string array or comma-separated string.");
    }

    private static bool? ReadOptionalBool(JsonObject root, string key)
    {
        return root[key] is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result
            : null;
    }

    private static bool? ReadOptionalBool(JsonElement element, string propertyName)
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

    private static uint? ReadOptionalUInt(JsonObject root, string key)
    {
        if (root[key] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<uint>(out var uintValue))
        {
            return uintValue;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return unchecked((uint)intValue);
        }

        if (value.TryGetValue<long>(out var longValue))
        {
            if (longValue >= int.MinValue && longValue <= int.MaxValue)
            {
                return unchecked((uint)(int)longValue);
            }

            if (longValue >= 0 && longValue <= uint.MaxValue)
            {
                return unchecked((uint)longValue);
            }
        }

        return null;
    }

    private sealed record UpgradePurchaseDefinition(
        string Id,
        uint TreeId,
        string Category,
        bool IsInstanced,
        string? HeroClass,
        IReadOnlyList<string> RequirementCodes);

    private readonly record struct UpgradePurchaseKey(
        uint TreeId,
        string RequirementCode,
        int InstanceNumber);

    private sealed record UpgradeEnsurePurchasesResult(
        int RequirementCount,
        int InstanceCount,
        int AddedCount,
        int UpdatedCount,
        int UnchangedCount,
        int SkippedDefinitionCount)
    {
        public int ChangedCount => AddedCount + UpdatedCount;
    }
}
