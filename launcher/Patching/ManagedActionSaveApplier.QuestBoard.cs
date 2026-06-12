using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static partial class ManagedActionSaveApplier
{
    private static readonly JsonDocumentOptions QuestJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static void ApplyQuestBoardReplaceWithFixedSet(ApplyContext context, string artifactPath, JsonObject artifact)
    {
        var questIds = ReadStringArray(ReadNode(artifact, "plan.arguments.questIds"), "plan.arguments.questIds");
        if (questIds.Count == 0)
        {
            throw new InvalidDataException("plan.arguments.questIds must contain at least one quest id.");
        }

        var removeCompleted = ReadOptionalBool(RequireObject(artifact, "plan.arguments"), "removeCompleted") == true;
        var completedQuestIds = removeCompleted
            ? ResolveCompletedQuestIds(context, artifact)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeQuestIds = questIds
            .Where(id => !completedQuestIds.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var definitions = LoadEnabledPlotQuestDefinitions(context.GameWorkingDirectory);
        if (definitions.Count == 0)
        {
            throw new InvalidDataException("Plot quest definition catalog produced no quest ids.");
        }

        var missingQuestIds = activeQuestIds
            .Where(id => !definitions.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingQuestIds.Length > 0)
        {
            throw new InvalidDataException($"Fixed quest board references unknown plot quest ids: {string.Join(",", missingQuestIds)}");
        }

        var replacement = new JsonObject();
        for (var i = 0; i < activeQuestIds.Length; i++)
        {
            replacement[i.ToString(CultureInfo.InvariantCulture)] = BuildQuestBoardEntry(definitions[activeQuestIds[i]]);
        }

        var file = context.LoadDecodedJsonFile("persist.quest.json");
        var baseRoot = EnsureObject(file.Root, "base_root");
        var existingQuests = baseRoot["quests"] as JsonObject ?? new JsonObject();
        var changed = !JsonNode.DeepEquals(existingQuests, replacement);
        if (changed)
        {
            if (context.WriteChanges)
            {
                baseRoot["quests"] = replacement;
            }

            file.MarkChanged(Math.Max(1, activeQuestIds.Length));
        }

        AddSuccessfulAction(
            context,
            artifactPath,
            artifact,
            file.Path,
            [
                $"replace quest board fixedQuestIds={questIds.Count} activeQuestIds={activeQuestIds.Length} removeCompleted={removeCompleted}",
                $"completedFiltered={questIds.Count - activeQuestIds.Length} definitions={definitions.Count}",
                $"quests={string.Join(",", activeQuestIds)}"
            ]);
    }

    private static HashSet<string> ResolveCompletedQuestIds(ApplyContext context, JsonObject artifact)
    {
        var stateKey = ReadString(artifact, "plan.arguments.completedStateKey");
        if (string.IsNullOrWhiteSpace(stateKey))
        {
            throw new InvalidDataException("plan.arguments.completedStateKey is required when removeCompleted is true.");
        }

        var state = LoadArtifactPluginState(context, artifact);
        if (!TryGetPath(state, stateKey, out var completedNode))
        {
            throw new InvalidDataException($"Completed quest state path was not found: {stateKey}");
        }

        return ReadStringArray(completedNode, $"state.{stateKey}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static JsonObject LoadArtifactPluginState(ApplyContext context, JsonObject artifact)
    {
        var pluginId = ReadString(artifact, "pluginId");
        var sourcePath = ReadString(artifact, "sourcePath");
        if (!Directory.Exists(context.ModStateDirectory))
        {
            throw new DirectoryNotFoundException($"Mod state directory was not found: {context.ModStateDirectory}");
        }

        foreach (var statePath in Directory.EnumerateFiles(context.ModStateDirectory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            JsonObject root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(statePath, Encoding.UTF8)) as JsonObject
                    ?? throw new InvalidDataException("state root must be a JSON object");
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                throw new InvalidDataException($"Failed to read plugin state file {statePath}: {ex.Message}", ex);
            }

            if (!ReadOptionalString(root, "pluginId").Equals(pluginId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var manifestPath = ReadOptionalString(root, "pluginManifestPath");
            if (!string.IsNullOrWhiteSpace(sourcePath) &&
                !manifestPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return root["state"] as JsonObject
                ?? throw new InvalidDataException($"Plugin state file has no root.state object: {statePath}");
        }

        throw new FileNotFoundException($"No sidecar state file matched pluginId={pluginId} sourcePath={sourcePath} in {context.ModStateDirectory}");
    }

    private static IReadOnlyDictionary<string, PlotQuestDefinition> LoadEnabledPlotQuestDefinitions(string gameWorkingDirectory)
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

    private static JsonObject BuildQuestBoardEntry(PlotQuestDefinition definition)
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

    private static IReadOnlyList<string> ReadStringArray(JsonNode? node, string path)
    {
        if (node is not JsonArray array)
        {
            throw new InvalidDataException($"{path} must be a string array.");
        }

        var result = new List<string>();
        foreach (var item in array)
        {
            if (item is not JsonValue value ||
                !value.TryGetValue<string>(out var text) ||
                string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidDataException($"{path} must contain only non-empty strings.");
            }

            result.Add(text);
        }

        return result;
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

    private static bool TryGetPath(JsonObject root, string path, out JsonNode? value)
    {
        value = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (value is not JsonObject obj || !obj.TryGetPropertyValue(part, out value))
            {
                value = null;
                return false;
            }
        }

        return true;
    }

    private sealed record PlotQuestDefinition(string Id, string SourcePath, JsonObject Quest);
}
