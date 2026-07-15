using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static class ManagedActionArtifactEligibility
{
    private const int SupportedArtifactVersion = 1;
    private const string QuestBoardPolicyMaterializerId = "framework.quest_board_policy_materializer";

    public static ManagedActionArtifactEligibilityResult Evaluate(PatchPlan patchPlan, JsonObject artifact)
    {
        if (artifact["version"] is not JsonValue versionNode ||
            !versionNode.TryGetValue<int>(out var version) ||
            version != SupportedArtifactVersion)
        {
            return Reject(
                "managed-artifact-version-unsupported",
                $"managed artifact version must be {SupportedArtifactVersion}");
        }

        var pluginId = ReadOptionalString(artifact, "pluginId");
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return Reject(
                "managed-artifact-owner-missing",
                "managed artifact must declare a non-empty pluginId");
        }

        if (pluginId.Equals(QuestBoardPolicyMaterializerId, StringComparison.OrdinalIgnoreCase))
        {
            return EvaluateFrameworkOwnedArtifact(patchPlan, artifact);
        }

        return EvaluatePluginOwner(
            patchPlan,
            pluginId,
            ReadOptionalString(artifact, "sourcePath"));
    }

    public static bool CanSupersedeQuestBoardArtifact(JsonObject artifact)
    {
        var status = ReadOptionalString(artifact, "status");
        if (status.Equals("materialized", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return status.Equals("empty", StringComparison.OrdinalIgnoreCase) &&
            ReadOptionalString(artifact, "pluginId").Equals(
                QuestBoardPolicyMaterializerId,
                StringComparison.OrdinalIgnoreCase);
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

        if (artifact["owners"] is not JsonArray owners)
        {
            return Reject(
                "managed-artifact-owner-missing",
                "framework-managed artifact must declare an owners array");
        }

        if (!TryReadPath(artifact, "plan.arguments.policies", out var policyNode) || policyNode is not JsonArray policies)
        {
            return Reject(
                "managed-artifact-owner-missing",
                "framework-managed artifact must declare plan.arguments.policies");
        }

        if (status.Equals("materialized", StringComparison.OrdinalIgnoreCase) &&
            (owners.Count == 0 || policies.Count == 0))
        {
            return Reject(
                "managed-artifact-owner-missing",
                "materialized framework-managed artifact must declare at least one owner and policy");
        }

        var ownerResult = EvaluateOwnerRows(patchPlan, owners, "owner", allowDuplicateOwners: false, out var ownerKeys);
        if (!ownerResult.Eligible)
        {
            return ownerResult;
        }

        var policyResult = EvaluateOwnerRows(patchPlan, policies, "policy", allowDuplicateOwners: true, out var policyOwnerKeys);
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

    private static ManagedActionArtifactEligibilityResult EvaluateOwnerRows(
        PatchPlan patchPlan,
        JsonArray rows,
        string label,
        bool allowDuplicateOwners,
        out HashSet<string> ownerKeys)
    {
        ownerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index] is not JsonObject row)
            {
                return Reject(
                    "managed-artifact-owner-missing",
                    $"framework-managed artifact {label} at index {index} must be an object");
            }

            var pluginId = ReadOptionalString(row, "pluginId");
            var sourcePath = ReadOptionalString(row, "sourcePath");
            var result = EvaluatePluginOwner(patchPlan, pluginId, sourcePath);
            if (!result.Eligible)
            {
                return result with
                {
                    Message = $"framework-managed artifact {label} at index {index} is ineligible: {result.Message}"
                };
            }

            var ownerKey = BuildOwnerKey(pluginId, sourcePath);
            var added = ownerKeys.Add(ownerKey);
            if (!allowDuplicateOwners && !added)
            {
                return Reject(
                    "managed-artifact-owner-set-mismatch",
                    $"framework-managed artifact declares duplicate owner metadata at index {index}");
            }
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

    private static string ReadOptionalString(JsonObject root, string key)
    {
        return root[key] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : string.Empty;
    }

    private static string ReadOptionalStringPath(JsonObject root, string path)
    {
        return TryReadPath(root, path, out var node) &&
            node is JsonValue value &&
            value.TryGetValue<string>(out var result)
            ? result
            : string.Empty;
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
