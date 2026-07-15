using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal sealed record ManagedActionProducerContract(
    string Kind,
    string PluginId,
    string SourceName,
    string SourcePath,
    int LoadOrder,
    int RuleIndex,
    string RuleId,
    string EventId,
    int ActionIndex,
    string ActionType,
    string Capability,
    string Risk,
    bool Required,
    string DefinitionSha256)
{
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["kind"] = Kind,
            ["pluginId"] = PluginId,
            ["sourceName"] = SourceName,
            ["sourcePath"] = SourcePath,
            ["loadOrder"] = LoadOrder,
            ["ruleIndex"] = RuleIndex,
            ["ruleId"] = RuleId,
            ["eventId"] = EventId,
            ["actionIndex"] = ActionIndex,
            ["actionType"] = ActionType,
            ["capability"] = Capability,
            ["risk"] = Risk,
            ["required"] = Required,
            ["definitionSha256"] = DefinitionSha256
        };
    }

    public bool HasSameIdentity(ManagedActionProducerContract other)
    {
        return BuildIdentityKey().Equals(other.BuildIdentityKey(), StringComparison.Ordinal);
    }

    public string BuildIdentityKey()
    {
        return ManagedActionCompositeKey.Build(
            NormalizeIdentityPart(Kind),
            NormalizeIdentityPart(PluginId),
            NormalizeIdentityPath(SourcePath),
            LoadOrder.ToString(CultureInfo.InvariantCulture),
            RuleIndex.ToString(CultureInfo.InvariantCulture),
            NormalizeIdentityPart(RuleId),
            NormalizeIdentityPart(EventId),
            ActionIndex.ToString(CultureInfo.InvariantCulture),
            NormalizeIdentityPart(ActionType),
            NormalizeIdentityPart(Capability),
            NormalizeIdentityPart(Risk),
            Required ? "true" : "false");
    }

    private static string NormalizeIdentityPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path).ToLowerInvariant();
        }
        catch
        {
            return NormalizeIdentityPart(path);
        }
    }

    private static string NormalizeIdentityPart(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }
}

internal static class ManagedActionProducerContractFactory
{
    public const int ArtifactVersion = 2;
    public const string RuntimeEventActionKind = "runtimeEventAction";
    public const string QuestChainStaticBoardKind = "questChainStaticBoard";
    public const string QuestBoardPolicySetKind = "questBoardPolicySet";
    public const string QuestBoardPolicyMaterializerId = "framework.quest_board_policy_materializer";

    private static readonly JsonSerializerOptions DescriptorJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static IReadOnlyList<ManagedActionProducerContract> BuildRuntimeEventActionContracts(
        IEnumerable<RuntimeEventRuleSource> sourceRules)
    {
        var contracts = new List<ManagedActionProducerContract>();
        foreach (var sourceRule in sourceRules)
        {
            var actions = sourceRule.Rule.Actions ?? [];
            for (var actionIndex = 0; actionIndex < actions.Length; actionIndex++)
            {
                if (sourceRule.OptionalActionSkipReasons.ContainsKey(actionIndex) ||
                    !FrameworkCapabilityRegistry.IsManagedArtifactAction(actions[actionIndex].Type))
                {
                    continue;
                }

                contracts.Add(CreateRuntimeEventAction(sourceRule, actionIndex));
            }
        }

        return contracts;
    }

    public static ManagedActionProducerContract CreateRuntimeEventAction(
        RuntimeEventRuleSource sourceRule,
        int actionIndex)
    {
        var actions = sourceRule.Rule.Actions ?? [];
        if (actionIndex < 0 || actionIndex >= actions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(actionIndex));
        }

        var action = actions[actionIndex];
        var descriptor = new JsonObject
        {
            ["rule"] = JsonSerializer.SerializeToNode(sourceRule.Rule, DescriptorJsonOptions),
            ["actionIndex"] = actionIndex
        };

        return new ManagedActionProducerContract(
            RuntimeEventActionKind,
            sourceRule.PluginId,
            sourceRule.SourceName,
            Path.GetFullPath(sourceRule.SourcePath),
            sourceRule.LoadOrder,
            sourceRule.RuleIndex,
            sourceRule.Rule.Id,
            sourceRule.Rule.On,
            actionIndex,
            action.Type,
            action.Capability,
            action.Risk,
            action.Required,
            ComputeDefinitionSha256(descriptor));
    }

    public static ManagedActionProducerContract? CreateStaticQuestChainBoard(
        PluginManifestCandidate plugin,
        int ruleIndex,
        QuestChainValidationReport report)
    {
        if (!report.Succeeded ||
            !report.QuestBoard.Enabled ||
            !report.QuestBoard.Mode.Equals("replaceWithFixedSet", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var descriptor = new JsonObject
        {
            ["type"] = report.Type,
            ["id"] = report.Id,
            ["mode"] = report.Mode,
            ["unlock"] = JsonSerializer.SerializeToNode(report.Unlock, DescriptorJsonOptions),
            ["questBoard"] = JsonSerializer.SerializeToNode(report.QuestBoard, DescriptorJsonOptions),
            ["orderedStages"] = JsonSerializer.SerializeToNode(report.OrderedStages, DescriptorJsonOptions)
        };

        return new ManagedActionProducerContract(
            QuestChainStaticBoardKind,
            plugin.Id,
            plugin.SourceName,
            Path.GetFullPath(plugin.Path),
            plugin.LoadOrder,
            ruleIndex,
            report.Id,
            "quest.chain.materialized",
            0,
            "questBoard.replaceWithFixedSet",
            "quest_board.replace_with_fixed_set",
            "managed",
            false,
            ComputeDefinitionSha256(descriptor));
    }

    public static ManagedActionProducerContract CreateQuestBoardPolicySet(
        IReadOnlyList<QuestBoardPolicyValidationReport> reports)
    {
        var policyRows = new JsonArray();
        foreach (var report in reports)
        {
            policyRows.Add(new JsonObject
            {
                ["pluginId"] = report.PluginId,
                ["sourcePath"] = NormalizePath(report.ManifestPath),
                ["ruleIndex"] = report.RuleIndex,
                ["id"] = report.Id,
                ["mode"] = report.Mode,
                ["refreshTriggers"] = JsonSerializer.SerializeToNode(report.RefreshTriggers, DescriptorJsonOptions),
                ["succeeded"] = report.Succeeded,
                ["entries"] = JsonSerializer.SerializeToNode(report.Entries, DescriptorJsonOptions)
            });
        }

        var descriptor = new JsonObject
        {
            ["policies"] = policyRows
        };

        return new ManagedActionProducerContract(
            QuestBoardPolicySetKind,
            QuestBoardPolicyMaterializerId,
            "Quest Board Policy Materializer",
            string.Empty,
            int.MaxValue,
            0,
            "questBoardPolicies.materialized",
            string.Empty,
            0,
            "questBoard.replaceWithFixedSet",
            "quest_board.replace_with_fixed_set",
            "managed",
            false,
            ComputeDefinitionSha256(descriptor));
    }

    private static string ComputeDefinitionSha256(JsonNode descriptor)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, descriptor);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var property in obj.OrderBy(property => property.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    private static string NormalizePath(string path)
    {
        return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path).ToLowerInvariant();
    }
}
