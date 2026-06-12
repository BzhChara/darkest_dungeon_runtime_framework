using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static class QuestBoardContentCatalog
{
    private static readonly JsonDocumentOptions QuestJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static IReadOnlyDictionary<string, PlotQuestDefinition> LoadEnabledPlotQuestDefinitions(string gameWorkingDirectory)
    {
        var definitions = new SortedDictionary<string, PlotQuestDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateCampaignPlotQuestFiles(gameWorkingDirectory))
        {
            foreach (var definition in ReadPlotQuestDefinitions(path))
            {
                definitions[definition.Id] = definition;
            }
        }

        return definitions;
    }

    public static JsonObject BuildQuestBoardEntry(PlotQuestDefinition definition)
    {
        var entry = CloneObject(definition.Quest);
        ValidatePlotQuestTemplate(definition, entry);
        entry["id"] = definition.Id;
        EnsureStringField(entry, "map_name", string.Empty);
        EnsureStringField(entry, "torch_setting", string.Empty);
        EnsureStringField(entry, "raid_rules_override", string.Empty);
        EnsureBoolField(entry, "is_plot_quest", true);
        EnsureBoolField(entry, "counted_in_generation", true);
        EnsureIntField(entry, "progression_goal_ids", 0);
        EnsureBoolField(entry, "use_default_progression_goals", true);
        EnsureIntField(entry, "completion_threshold", 0);
        EnsureBoolField(entry, "is_from_town_event", false);
        EnsureObjectField(entry, "threshold_rewards");

        var reward = EnsureObject(entry, "completion_reward");
        EnsureIntField(reward, "resolve_xp", 0);
        EnsureIntField(reward, "resolve_xp_per_wave_kill", 0);
        EnsureObjectField(reward, "additional_threshold_trinket_rewards");
        EnsureArrayField(reward, "trinket_retention_ids");
        EnsureIntField(reward, "max_times_dungeon_xp_awarded", 0);

        var itemDefinition = EnsureObject(reward, "items_definition");
        itemDefinition.Remove("system_config_type");
        EnsureObjectField(itemDefinition, "items");
        return entry;
    }

    private static IEnumerable<string> EnumerateCampaignPlotQuestFiles(string gameWorkingDirectory)
    {
        var baseQuestPath = Path.Combine(gameWorkingDirectory, "campaign", "quest", "quest.plot_quests.json");
        if (File.Exists(baseQuestPath))
        {
            yield return baseQuestPath;
        }

        foreach (var path in EnumerateNonModDlcFiles(gameWorkingDirectory, "quest.plot_quests.json"))
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

    private static IReadOnlyList<PlotQuestDefinition> ReadPlotQuestDefinitions(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path), QuestJsonOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("plot_quests", out var questsElement) ||
            questsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var definitions = new List<PlotQuestDefinition>();
        foreach (var questElement in questsElement.EnumerateArray())
        {
            if (questElement.ValueKind != JsonValueKind.Object ||
                !questElement.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(idElement.GetString()) ||
                !questElement.TryGetProperty("quest", out var questDataElement) ||
                questDataElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var questData = JsonNode.Parse(questDataElement.GetRawText()) as JsonObject;
            if (questData is not null)
            {
                definitions.Add(new PlotQuestDefinition(idElement.GetString()!, path, questData));
            }
        }

        return definitions;
    }

    private static void ValidatePlotQuestTemplate(PlotQuestDefinition definition, JsonObject entry)
    {
        RequireNonEmptyStringField(definition, entry, "type");
        RequireNonEmptyStringField(definition, entry, "dungeon");
        RequireIntegerField(definition, entry, "difficulty");
        RequireIntegerField(definition, entry, "length");
        if (entry["goal_ids"] is not JsonArray goalIds || goalIds.Count == 0)
        {
            throw new InvalidDataException($"Plot quest {definition.Id} from {definition.SourcePath} must define at least one goal_ids entry.");
        }

        foreach (var goalId in goalIds)
        {
            if (goalId is not JsonValue value ||
                !value.TryGetValue<string>(out var text) ||
                string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidDataException($"Plot quest {definition.Id} from {definition.SourcePath} has an invalid goal_ids entry.");
            }
        }

        if (entry["completion_reward"] is not JsonObject reward)
        {
            throw new InvalidDataException($"Plot quest {definition.Id} from {definition.SourcePath} must define completion_reward.");
        }

        RequireIntegerField(definition, reward, "completion_reward.resolve_xp");
        if (reward["items_definition"] is not JsonObject itemDefinition ||
            itemDefinition["items"] is not JsonObject)
        {
            throw new InvalidDataException($"Plot quest {definition.Id} from {definition.SourcePath} must define completion_reward.items_definition.items.");
        }
    }

    private static void RequireNonEmptyStringField(PlotQuestDefinition definition, JsonObject root, string key)
    {
        if (root[key] is not JsonValue value ||
            !value.TryGetValue<string>(out var text) ||
            string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException($"Plot quest {definition.Id} from {definition.SourcePath} must define string field {key}.");
        }
    }

    private static void RequireIntegerField(PlotQuestDefinition definition, JsonObject root, string key)
    {
        var nodeKey = key.Split('.', StringSplitOptions.RemoveEmptyEntries).Last();
        if (root[nodeKey] is not JsonValue value ||
            !value.TryGetValue<int>(out _))
        {
            throw new InvalidDataException($"Plot quest {definition.Id} from {definition.SourcePath} must define integer field {key}.");
        }
    }

    private static JsonObject EnsureObject(JsonObject root, string path)
    {
        var current = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current[part] is JsonObject existing)
            {
                current = existing;
                continue;
            }

            var created = new JsonObject();
            current[part] = created;
            current = created;
        }

        return current;
    }

    private static void EnsureStringField(JsonObject root, string key, string value)
    {
        if (root[key] is null)
        {
            root[key] = value;
        }
    }

    private static void EnsureIntField(JsonObject root, string key, int value)
    {
        if (root[key] is null)
        {
            root[key] = value;
        }
    }

    private static void EnsureBoolField(JsonObject root, string key, bool value)
    {
        if (root[key] is null)
        {
            root[key] = value;
        }
    }

    private static void EnsureObjectField(JsonObject root, string key)
    {
        if (root[key] is null)
        {
            root[key] = new JsonObject();
        }
    }

    private static void EnsureArrayField(JsonObject root, string key)
    {
        if (root[key] is null)
        {
            root[key] = new JsonArray();
        }
    }

    private static JsonObject CloneObject(JsonObject value)
    {
        return CloneNode(value) as JsonObject
            ?? throw new InvalidDataException("Expected cloned JSON node to be an object.");
    }

    private static JsonNode? CloneNode(JsonNode? node)
    {
        return node is null ? null : JsonNode.Parse(node.ToJsonString());
    }
}

internal sealed record PlotQuestDefinition(string Id, string SourcePath, JsonObject Quest);
