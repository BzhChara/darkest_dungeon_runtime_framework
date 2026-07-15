using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static class ManagedActionArtifactEligibility
{
    public static ManagedActionArtifactEligibilityResult Evaluate(PatchPlan patchPlan, JsonObject artifact)
    {
        if (artifact["version"] is not JsonValue versionNode ||
            !versionNode.TryGetValue<int>(out var version) ||
            version != ManagedActionProducerContractFactory.ArtifactVersion)
        {
            return Reject(
                "managed-artifact-version-unsupported",
                $"managed artifact version must be {ManagedActionProducerContractFactory.ArtifactVersion}");
        }

        if (artifact["producer"] is not JsonObject producerNode)
        {
            return Reject(
                "managed-artifact-producer-invalid",
                "managed artifact must declare a complete producer contract");
        }

        if (!TryReadProducer(producerNode, out var producer, out var producerError))
        {
            return Reject("managed-artifact-producer-invalid", producerError);
        }

        var metadataResult = EvaluateArtifactMetadata(artifact, producer);
        if (!metadataResult.Eligible)
        {
            return metadataResult;
        }

        if (producer.PluginId.Equals(
                ManagedActionProducerContractFactory.QuestBoardPolicyMaterializerId,
                StringComparison.OrdinalIgnoreCase))
        {
            if (!producer.Kind.Equals(
                    ManagedActionProducerContractFactory.QuestBoardPolicySetKind,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Reject(
                    "managed-artifact-framework-contract-invalid",
                    $"framework-managed artifact producer kind is invalid: {producer.Kind}");
            }

            var frameworkProducerResult = EvaluateCurrentProducer(patchPlan, producer);
            return frameworkProducerResult.Eligible
                ? EvaluateFrameworkOwnedArtifact(patchPlan, artifact)
                : frameworkProducerResult;
        }

        if (!producer.Kind.Equals(ManagedActionProducerContractFactory.RuntimeEventActionKind, StringComparison.OrdinalIgnoreCase) &&
            !producer.Kind.Equals(ManagedActionProducerContractFactory.QuestChainStaticBoardKind, StringComparison.OrdinalIgnoreCase))
        {
            return Reject(
                "managed-artifact-producer-invalid",
                $"plugin-managed artifact producer kind is invalid: {producer.Kind}");
        }

        var ownerResult = EvaluatePluginOwner(patchPlan, producer.PluginId, producer.SourcePath);
        if (!ownerResult.Eligible)
        {
            return ownerResult;
        }

        var currentProducerResult = EvaluateCurrentProducer(patchPlan, producer);
        return currentProducerResult.Eligible
            ? EvaluatePluginOwnedArtifact(artifact, producer)
            : currentProducerResult;
    }

    internal static bool TryReadProducerContract(
        JsonObject artifact,
        out ManagedActionProducerContract producer)
    {
        producer = null!;
        return artifact["producer"] is JsonObject producerNode &&
            TryReadProducer(producerNode, out producer, out _);
    }

    public static bool CanParticipateInRetentionRanking(JsonObject artifact)
    {
        return ReadOptionalStringPath(artifact, "action.type").Equals(
                "questBoard.replaceWithFixedSet",
                StringComparison.OrdinalIgnoreCase) &&
            CanSupersedeQuestBoardArtifact(artifact);
    }

    public static bool CanSupersedeQuestBoardArtifact(JsonObject artifact)
    {
        var status = ReadOptionalString(artifact, "status");
        if (status.Equals("materialized", StringComparison.OrdinalIgnoreCase))
        {
            return EvaluateQuestBoardPlan(
                artifact,
                QuestBoardQuestIdSetRequirement.NonEmpty).Eligible;
        }

        return status.Equals("empty", StringComparison.OrdinalIgnoreCase) &&
            ReadOptionalString(artifact, "pluginId").Equals(
                ManagedActionProducerContractFactory.QuestBoardPolicyMaterializerId,
                StringComparison.OrdinalIgnoreCase) &&
            EvaluateQuestBoardPlan(
                artifact,
                QuestBoardQuestIdSetRequirement.Empty).Eligible;
    }

    private static ManagedActionArtifactEligibilityResult EvaluateCurrentProducer(
        PatchPlan patchPlan,
        ManagedActionProducerContract declaredProducer)
    {
        var matches = patchPlan.ManagedActionProducers
            .Where(current => current.HasSameIdentity(declaredProducer))
            .ToArray();
        if (matches.Length == 0)
        {
            return Reject(
                "managed-artifact-producer-inactive",
                $"managed artifact producer is not active: kind={declaredProducer.Kind}, " +
                $"plugin={declaredProducer.PluginId}, rule={declaredProducer.RuleIndex}:{declaredProducer.RuleId}, " +
                $"action={declaredProducer.ActionIndex}:{declaredProducer.ActionType}");
        }

        if (matches.Length > 1)
        {
            return Reject(
                "managed-artifact-producer-ambiguous",
                $"managed artifact producer identity matched {matches.Length} active contracts");
        }

        if (!matches[0].DefinitionSha256.Equals(
                declaredProducer.DefinitionSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return Reject(
                "managed-artifact-producer-definition-mismatch",
                $"managed artifact producer definition no longer matches the active contract: " +
                $"artifact={declaredProducer.DefinitionSha256}, active={matches[0].DefinitionSha256}");
        }

        return ManagedActionArtifactEligibilityResult.Accepted;
    }

    private static ManagedActionArtifactEligibilityResult EvaluateArtifactMetadata(
        JsonObject artifact,
        ManagedActionProducerContract producer)
    {
        var mismatches = new List<string>();
        AddStringMismatch(mismatches, "pluginId", ReadOptionalString(artifact, "pluginId"), producer.PluginId);
        AddIntMismatch(mismatches, "loadOrder", ReadOptionalInt(artifact, "loadOrder"), producer.LoadOrder);
        AddIntMismatch(mismatches, "ruleIndex", ReadOptionalInt(artifact, "ruleIndex"), producer.RuleIndex);
        AddStringMismatch(mismatches, "ruleId", ReadOptionalString(artifact, "ruleId"), producer.RuleId);
        AddIntMismatch(mismatches, "actionIndex", ReadOptionalInt(artifact, "actionIndex"), producer.ActionIndex);
        AddStringMismatch(mismatches, "action.type", ReadOptionalStringPath(artifact, "action.type"), producer.ActionType);
        AddStringMismatch(mismatches, "action.capability", ReadOptionalStringPath(artifact, "action.capability"), producer.Capability);
        AddStringMismatch(mismatches, "action.risk", ReadOptionalStringPath(artifact, "action.risk"), producer.Risk);

        if (!TryReadBoolPath(artifact, "action.required", out var required) || required != producer.Required)
        {
            mismatches.Add("action.required");
        }

        if (!string.IsNullOrWhiteSpace(producer.EventId))
        {
            AddStringMismatch(mismatches, "eventId", ReadOptionalString(artifact, "eventId"), producer.EventId);
        }

        if (!string.IsNullOrWhiteSpace(producer.SourcePath) &&
            !PathsEqual(ReadOptionalString(artifact, "sourcePath"), producer.SourcePath))
        {
            mismatches.Add("sourcePath");
        }

        return mismatches.Count == 0
            ? ManagedActionArtifactEligibilityResult.Accepted
            : Reject(
                "managed-artifact-producer-metadata-mismatch",
                $"managed artifact metadata does not match its producer contract: {string.Join(',', mismatches)}");
    }

    private static ManagedActionArtifactEligibilityResult EvaluateFrameworkOwnedArtifact(
        PatchPlan patchPlan,
        JsonObject artifact)
    {
        var status = ReadOptionalString(artifact, "status");
        var expectedEventId = status.ToLowerInvariant() switch
        {
            "materialized" => "quest.board.policies.materialized",
            "empty" => "quest.board.policies.empty",
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(expectedEventId))
        {
            return Reject(
                "managed-artifact-framework-contract-invalid",
                $"framework-managed artifact status must be materialized or empty, found: {status}");
        }

        if (!ReadOptionalString(artifact, "eventId").Equals(expectedEventId, StringComparison.OrdinalIgnoreCase) ||
            !ReadOptionalStringPath(artifact, "action.type").Equals("questBoard.replaceWithFixedSet", StringComparison.OrdinalIgnoreCase) ||
            !ReadOptionalStringPath(artifact, "payload.source").Equals("questBoardPolicies", StringComparison.OrdinalIgnoreCase) ||
            !ReadOptionalStringPath(artifact, "plan.kind").Equals("questBoard.replaceWithFixedSet", StringComparison.OrdinalIgnoreCase) ||
            !ReadOptionalStringPath(artifact, "plan.source").Equals("questBoardPolicies", StringComparison.OrdinalIgnoreCase))
        {
            return Reject(
                "managed-artifact-framework-contract-invalid",
                "framework-managed artifact action, event, payload source, or plan source does not match the quest board policy materializer contract");
        }

        var frameworkSourcePath = ReadOptionalString(artifact, "sourcePath");
        if (string.IsNullOrWhiteSpace(frameworkSourcePath) || !Path.IsPathFullyQualified(frameworkSourcePath))
        {
            return Reject(
                "managed-artifact-framework-contract-invalid",
                "framework-managed artifact sourcePath must be an absolute policy resolve report path");
        }

        var questBoardPlanResult = EvaluateQuestBoardPlan(
            artifact,
            status.Equals("empty", StringComparison.OrdinalIgnoreCase)
                ? QuestBoardQuestIdSetRequirement.Empty
                : QuestBoardQuestIdSetRequirement.NonEmpty);
        if (!questBoardPlanResult.Eligible)
        {
            return questBoardPlanResult;
        }

        if (artifact["owners"] is not JsonArray owners)
        {
            return Reject(
                "managed-artifact-owner-missing",
                "framework-managed artifact must declare an owners array");
        }

        if (!TryReadPath(artifact, "plan.arguments.policies", out var policyNode) || policyNode is not JsonArray policies)
        {
            return Reject(
                "managed-artifact-policy-set-missing",
                "framework-managed artifact must declare plan.arguments.policies");
        }

        if (status.Equals("materialized", StringComparison.OrdinalIgnoreCase) &&
            (owners.Count == 0 || policies.Count == 0))
        {
            return Reject(
                "managed-artifact-owner-missing",
                "materialized framework-managed artifact must declare at least one owner and policy");
        }

        var ownerResult = EvaluateOwnerRows(patchPlan, owners, out var ownerKeys);
        if (!ownerResult.Eligible)
        {
            return ownerResult;
        }

        var policyResult = EvaluatePolicyRows(patchPlan, policies, out var policyOwnerKeys);
        if (!policyResult.Eligible)
        {
            return policyResult;
        }

        if (!ownerKeys.SetEquals(policyOwnerKeys))
        {
            return Reject(
                "managed-artifact-owner-set-mismatch",
                "framework-managed artifact owners must exactly match the pluginId and sourcePath set declared by plan.arguments.policies");
        }

        return ManagedActionArtifactEligibilityResult.Accepted;
    }

    private static ManagedActionArtifactEligibilityResult EvaluatePluginOwnedArtifact(
        JsonObject artifact,
        ManagedActionProducerContract producer)
    {
        var status = ReadOptionalString(artifact, "status");
        if (!status.Equals("materialized", StringComparison.OrdinalIgnoreCase))
        {
            return Reject(
                "managed-artifact-envelope-invalid",
                $"plugin-managed artifact status must be materialized, found: {status}");
        }

        if (artifact["plan"] is not JsonObject plan ||
            !ReadOptionalString(plan, "kind").Equals(producer.ActionType, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(ReadOptionalString(plan, "effect")) ||
            string.IsNullOrWhiteSpace(ReadOptionalString(plan, "target")) ||
            plan["arguments"] is not JsonObject)
        {
            return Reject(
                "managed-artifact-envelope-invalid",
                "plugin-managed artifact plan must declare matching kind, non-empty effect and target values, and an arguments object");
        }

        if (producer.ActionType.Equals("questBoard.replaceWithFixedSet", StringComparison.OrdinalIgnoreCase))
        {
            return EvaluateQuestBoardPlan(
                artifact,
                QuestBoardQuestIdSetRequirement.NonEmpty);
        }

        return ManagedActionArtifactEligibilityResult.Accepted;
    }

    private static ManagedActionArtifactEligibilityResult EvaluateQuestBoardPlan(
        JsonObject artifact,
        QuestBoardQuestIdSetRequirement questIdSetRequirement)
    {
        try
        {
            QuestBoardFixedSetResolver.ReadArtifactShape(artifact, questIdSetRequirement);
            return ManagedActionArtifactEligibilityResult.Accepted;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            return Reject(
                "managed-artifact-quest-board-contract-invalid",
                ex.Message);
        }
    }

    private static ManagedActionArtifactEligibilityResult EvaluateOwnerRows(
        PatchPlan patchPlan,
        JsonArray rows,
        out HashSet<string> ownerKeys)
    {
        ownerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index] is not JsonObject row)
            {
                return Reject(
                    "managed-artifact-owner-missing",
                    $"framework-managed artifact owner at index {index} must be an object");
            }

            var pluginId = ReadOptionalString(row, "pluginId");
            var sourcePath = ReadOptionalString(row, "sourcePath");
            var result = EvaluatePluginOwner(patchPlan, pluginId, sourcePath);
            if (!result.Eligible)
            {
                return result with
                {
                    Message = $"framework-managed artifact owner at index {index} is ineligible: {result.Message}"
                };
            }

            var added = ownerKeys.Add(BuildOwnerKey(pluginId, sourcePath));
            if (!added)
            {
                return Reject(
                    "managed-artifact-owner-set-mismatch",
                    $"framework-managed artifact declares duplicate owner metadata at index {index}");
            }
        }

        return ManagedActionArtifactEligibilityResult.Accepted;
    }

    private static ManagedActionArtifactEligibilityResult EvaluatePolicyRows(
        PatchPlan patchPlan,
        JsonArray rows,
        out HashSet<string> ownerKeys)
    {
        ownerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var policyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index] is not JsonObject row)
            {
                return Reject(
                    "managed-artifact-policy-set-missing",
                    $"framework-managed artifact policy at index {index} must be an object");
            }

            var pluginId = ReadOptionalString(row, "pluginId");
            var sourcePath = ReadOptionalString(row, "sourcePath");
            var policyId = ReadOptionalString(row, "policyId");
            var ruleIndex = ReadOptionalInt(row, "ruleIndex");
            var ownerResult = EvaluatePluginOwner(patchPlan, pluginId, sourcePath);
            if (!ownerResult.Eligible)
            {
                return ownerResult with
                {
                    Message = $"framework-managed artifact policy at index {index} is ineligible: {ownerResult.Message}"
                };
            }

            if (string.IsNullOrWhiteSpace(policyId) || !ruleIndex.HasValue)
            {
                return Reject(
                    "managed-artifact-policy-set-missing",
                    $"framework-managed artifact policy at index {index} must declare policyId and ruleIndex");
            }

            ownerKeys.Add(BuildOwnerKey(pluginId, sourcePath));
            if (!policyKeys.Add(BuildPolicyKey(pluginId, sourcePath, ruleIndex.Value, policyId)))
            {
                return Reject(
                    "managed-artifact-policy-set-mismatch",
                    $"framework-managed artifact declares duplicate policy metadata at index {index}");
            }
        }

        var activePolicyKeys = patchPlan.QuestBoardPolicyReports
            .Select(report => BuildPolicyKey(report.PluginId, report.ManifestPath, report.RuleIndex, report.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!policyKeys.SetEquals(activePolicyKeys))
        {
            return Reject(
                "managed-artifact-policy-set-mismatch",
                "framework-managed artifact policy identities do not match the active quest board policy set");
        }

        return ManagedActionArtifactEligibilityResult.Accepted;
    }

    private static ManagedActionArtifactEligibilityResult EvaluatePluginOwner(
        PatchPlan patchPlan,
        string pluginId,
        string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(sourcePath))
        {
            return Reject(
                "managed-artifact-owner-missing",
                "managed artifact owner must declare non-empty pluginId and sourcePath values");
        }

        var activeById = patchPlan.ActivePluginManifests
            .Where(manifest => manifest.Id.Equals(pluginId.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (activeById.Length == 0)
        {
            return Reject(
                "managed-artifact-owner-inactive",
                $"managed artifact owner plugin is not active: {pluginId.Trim()}");
        }

        if (!Path.IsPathFullyQualified(sourcePath))
        {
            return Reject(
                "managed-artifact-owner-source-mismatch",
                $"managed artifact owner sourcePath must be absolute: {sourcePath}");
        }

        string normalizedSourcePath;
        try
        {
            normalizedSourcePath = Path.GetFullPath(sourcePath);
        }
        catch (Exception ex)
        {
            return Reject(
                "managed-artifact-owner-source-mismatch",
                $"managed artifact owner sourcePath is invalid: {ex.Message}");
        }

        if (!activeById.Any(manifest => PathsEqual(manifest.Path, normalizedSourcePath)))
        {
            return Reject(
                "managed-artifact-owner-source-mismatch",
                $"managed artifact owner sourcePath does not match an active {pluginId.Trim()} instance: {normalizedSourcePath}");
        }

        return ManagedActionArtifactEligibilityResult.Accepted;
    }

    private static bool TryReadProducer(
        JsonObject root,
        out ManagedActionProducerContract producer,
        out string error)
    {
        producer = null!;
        error = "managed artifact must declare a complete producer contract";
        var kind = ReadOptionalString(root, "kind");
        var pluginId = ReadOptionalString(root, "pluginId");
        var sourceName = ReadOptionalString(root, "sourceName");
        var sourcePath = ReadOptionalString(root, "sourcePath");
        var ruleId = ReadOptionalString(root, "ruleId");
        var eventId = ReadOptionalString(root, "eventId");
        var actionType = ReadOptionalString(root, "actionType");
        var capability = ReadOptionalString(root, "capability");
        var risk = ReadOptionalString(root, "risk");
        var definitionSha256 = ReadOptionalString(root, "definitionSha256");

        if (string.IsNullOrWhiteSpace(kind) ||
            string.IsNullOrWhiteSpace(pluginId) ||
            string.IsNullOrWhiteSpace(ruleId) ||
            string.IsNullOrWhiteSpace(actionType) ||
            string.IsNullOrWhiteSpace(capability) ||
            string.IsNullOrWhiteSpace(risk) ||
            definitionSha256.Length != 64 ||
            definitionSha256.Any(character => !Uri.IsHexDigit(character)) ||
            !TryReadInt(root, "loadOrder", out var loadOrder) ||
            !TryReadInt(root, "ruleIndex", out var ruleIndex) ||
            !TryReadInt(root, "actionIndex", out var actionIndex) ||
            !TryReadBool(root, "required", out var required))
        {
            return false;
        }

        producer = new ManagedActionProducerContract(
            kind,
            pluginId,
            sourceName,
            sourcePath,
            loadOrder,
            ruleIndex,
            ruleId,
            eventId,
            actionIndex,
            actionType,
            capability,
            risk,
            required,
            definitionSha256);
        error = string.Empty;
        return true;
    }

    private static void AddStringMismatch(List<string> mismatches, string field, string actual, string expected)
    {
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add(field);
        }
    }

    private static void AddIntMismatch(List<string> mismatches, string field, int? actual, int expected)
    {
        if (!actual.HasValue || actual.Value != expected)
        {
            mismatches.Add(field);
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildOwnerKey(string pluginId, string sourcePath)
    {
        return $"{pluginId.Trim().ToLowerInvariant()}|{Path.GetFullPath(sourcePath).ToLowerInvariant()}";
    }

    private static string BuildPolicyKey(string pluginId, string sourcePath, int ruleIndex, string policyId)
    {
        return $"{BuildOwnerKey(pluginId, sourcePath)}|{ruleIndex}|{policyId.Trim().ToLowerInvariant()}";
    }

    private static string ReadOptionalString(JsonObject root, string key)
    {
        return root[key] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : string.Empty;
    }

    private static int? ReadOptionalInt(JsonObject root, string key)
    {
        return TryReadInt(root, key, out var value) ? value : null;
    }

    private static bool TryReadInt(JsonObject root, string key, out int value)
    {
        value = 0;
        return root[key] is JsonValue node && node.TryGetValue<int>(out value);
    }

    private static bool TryReadBool(JsonObject root, string key, out bool value)
    {
        value = false;
        return root[key] is JsonValue node && node.TryGetValue<bool>(out value);
    }

    private static string ReadOptionalStringPath(JsonObject root, string path)
    {
        return TryReadPath(root, path, out var node) &&
            node is JsonValue value &&
            value.TryGetValue<string>(out var result)
            ? result
            : string.Empty;
    }

    private static bool TryReadBoolPath(JsonObject root, string path, out bool result)
    {
        result = false;
        return TryReadPath(root, path, out var node) &&
            node is JsonValue value &&
            value.TryGetValue<bool>(out result);
    }

    private static bool TryReadPath(JsonObject root, string path, out JsonNode? node)
    {
        node = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (node is not JsonObject obj || !obj.TryGetPropertyValue(part, out node))
            {
                node = null;
                return false;
            }
        }

        return true;
    }

    private static ManagedActionArtifactEligibilityResult Reject(string code, string message)
    {
        return new ManagedActionArtifactEligibilityResult(false, code, message);
    }
}

internal sealed record ManagedActionArtifactEligibilityResult(
    bool Eligible,
    string Code,
    string Message)
{
    public static ManagedActionArtifactEligibilityResult Accepted { get; } = new(true, string.Empty, string.Empty);
}
