using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static partial class ManagedActionSaveApplier
{
    private static void ApplyRosterEnforceAvailabilityFilter(ApplyContext context, string artifactPath, JsonObject artifact)
    {
        ApplyAvailabilityFilterPolicy(
            context,
            artifactPath,
            artifact,
            category: "roster",
            itemKind: "hero",
            idsArgumentPath: "plan.arguments.unavailableHeroIds",
            summaryProperty: "unavailableHeroIds");
    }

    private static void ApplyEquipmentEnforceAvailabilityFilter(ApplyContext context, string artifactPath, JsonObject artifact)
    {
        ApplyAvailabilityFilterPolicy(
            context,
            artifactPath,
            artifact,
            category: "equipment",
            itemKind: "trinket",
            idsArgumentPath: "plan.arguments.unavailableTrinketIds",
            summaryProperty: "unavailableTrinketIds");
    }

    private static void ApplyAvailabilityFilterPolicy(
        ApplyContext context,
        string artifactPath,
        JsonObject artifact,
        string category,
        string itemKind,
        string idsArgumentPath,
        string summaryProperty)
    {
        var filterId = ReadString(artifact, "plan.arguments.filterId");
        if (string.IsNullOrWhiteSpace(filterId))
        {
            throw new InvalidDataException("plan.arguments.filterId must not be empty.");
        }

        var unavailableIds = ReadOptionalStringArrayPath(artifact, idsArgumentPath);
        var policyFile = LoadProfilePolicyFile(context);
        var categoryPolicy = EnsureObject(policyFile.Root, $"profilePolicies.{category}");
        var filters = EnsureObject(categoryPolicy, "availabilityFilters");
        var filter = filters[filterId] as JsonObject;
        if (filter is null)
        {
            filter = new JsonObject();
            if (context.WriteChanges)
            {
                filters[filterId] = filter;
            }
        }

        var changedCount = 0;
        changedCount += SetJsonPropertyIfChanged(filter, "itemKind", itemKind, context.WriteChanges) ? 1 : 0;
        changedCount += SetJsonPropertyIfChanged(filter, "effect", "unavailable", context.WriteChanges) ? 1 : 0;
        changedCount += SetJsonPropertyIfChanged(filter, "unavailableIds", BuildStringArray(unavailableIds), context.WriteChanges) ? 1 : 0;
        changedCount += SetJsonPropertyIfChanged(filter, "profilePolicyOnly", true, context.WriteChanges) ? 1 : 0;

        var summaryIds = BuildAvailabilityPolicySummary(filters, filterId, unavailableIds, context.WriteChanges);
        changedCount += SetJsonPropertyIfChanged(categoryPolicy, summaryProperty, BuildStringArray(summaryIds), context.WriteChanges) ? 1 : 0;

        if (changedCount > 0)
        {
            policyFile.MarkChanged(changedCount);
        }

        context.Issues.Add(new ManagedActionApplyIssue(
            "warning",
            "managed-action-hard-availability-not-verified",
            artifactPath,
            $"{ReadString(artifact, "action.type")} recorded decoded profile policy for {itemKind} availability; hard party UI/gameplay enforcement still requires a live runtime consumer"));

        AddSuccessfulAction(
            context,
            artifactPath,
            artifact,
            policyFile.Path,
            [
                $"record {category} availability filter filterId={filterId} itemKind={itemKind} unavailable={unavailableIds.Count}",
                $"summary {summaryProperty}={summaryIds.Count}",
                "policy is reversible data for runtime consumers; no unknown roster status or inventory field was mutated"
            ]);
    }

    private static IReadOnlyList<string> BuildAvailabilityPolicySummary(
        JsonObject filters,
        string currentFilterId,
        IReadOnlyList<string> currentUnavailableIds,
        bool writeChanges)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in filters)
        {
            if (pair.Key.Equals(currentFilterId, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var id in currentUnavailableIds)
                {
                    ids.Add(id);
                }

                continue;
            }

            if (pair.Value is not JsonObject filter ||
                filter["unavailableIds"] is not JsonArray unavailableIds)
            {
                continue;
            }

            foreach (var item in unavailableIds)
            {
                if (item is JsonValue value &&
                    value.TryGetValue<string>(out var text) &&
                    !string.IsNullOrWhiteSpace(text))
                {
                    ids.Add(text);
                }
            }
        }

        if (!writeChanges && !filters.ContainsKey(currentFilterId))
        {
            foreach (var id in currentUnavailableIds)
            {
                ids.Add(id);
            }
        }

        return ids
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static JsonArray BuildStringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            array.Add(value);
        }

        return array;
    }
}
