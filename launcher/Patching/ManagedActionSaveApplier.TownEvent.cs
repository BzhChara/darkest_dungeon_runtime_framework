using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static partial class ManagedActionSaveApplier
{
    private static void ApplyTownEventOverrideCurrent(ApplyContext context, string artifactPath, JsonObject artifact)
    {
        var requestedEvent = RequireObject(artifact, "plan.arguments.event");
        var mode = ReadString(requestedEvent, "mode");
        if (!mode.Equals("override", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("suppress", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("paused", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsupported town event override mode: {mode}");
        }

        var message = ReadOptionalString(requestedEvent, "message");
        var file = context.LoadDecodedJsonFile("persist.town_event.json");
        var baseRoot = EnsureObject(file.Root, "base_root");
        var changedCount = 0;
        changedCount += SetJsonPropertyIfChanged(baseRoot, "current_result_event_id", 0, context.WriteChanges) ? 1 : 0;
        changedCount += SetJsonPropertyIfChanged(baseRoot, "has_unclaimed_interaction", false, context.WriteChanges) ? 1 : 0;
        changedCount += SetJsonPropertyIfChanged(baseRoot, "event_cost", new JsonObject(), context.WriteChanges) ? 1 : 0;
        changedCount += SetJsonPropertyIfChanged(baseRoot, "bonus_hero_entries", new JsonObject(), context.WriteChanges) ? 1 : 0;
        changedCount += SetJsonPropertyIfChanged(baseRoot, "dead_hero_entries", new JsonArray(), context.WriteChanges) ? 1 : 0;
        changedCount += SetJsonPropertyIfChanged(baseRoot, "free_upgrade_tags", new JsonObject(), context.WriteChanges) ? 1 : 0;
        if (changedCount > 0)
        {
            file.MarkChanged(changedCount);
        }

        var policyFile = LoadProfilePolicyFile(context);
        var townEventPolicy = EnsureObject(policyFile.Root, "profilePolicies.townEvent");
        var policyChangedCount = 0;
        policyChangedCount += SetJsonPropertyIfChanged(townEventPolicy, "mode", mode, context.WriteChanges) ? 1 : 0;
        policyChangedCount += SetJsonPropertyIfChanged(townEventPolicy, "message", message, context.WriteChanges) ? 1 : 0;
        policyChangedCount += SetJsonPropertyIfChanged(townEventPolicy, "saveLevelAction", "suppressCurrentEvent", context.WriteChanges) ? 1 : 0;
        if (policyChangedCount > 0)
        {
            policyFile.MarkChanged(policyChangedCount);
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            context.Issues.Add(new ManagedActionApplyIssue(
                "warning",
                "town-event-custom-message-requires-content-consumer",
                artifactPath,
                $"town event message '{message}' was recorded in policy; save-level writer can only suppress the current event"));
        }

        AddSuccessfulAction(
            context,
            artifactPath,
            artifact,
            file.Path,
            [
                $"override town event mode={mode}",
                "set current_result_event_id=0 and has_unclaimed_interaction=false",
                $"policyPath={policyFile.Path}",
                string.IsNullOrWhiteSpace(message)
                    ? "no custom message requested"
                    : "custom message requires content/localization or runtime UI consumer"
            ]);
    }
}
