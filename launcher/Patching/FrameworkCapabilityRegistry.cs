namespace DDRuntimeLoader;

internal enum FrameworkActionExecutionKind
{
    Unavailable,
    Sidecar,
    ManagedArtifact
}

internal sealed record FrameworkCapabilityDefinition(
    string Id,
    string Status,
    string Risk,
    string Source,
    string EffectScope,
    bool Available,
    bool LiveEnforced,
    string FailurePolicy);

internal sealed record FrameworkActionDefinition(
    string Type,
    FrameworkActionExecutionKind ExecutionKind,
    string Status,
    string Risk,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Consumers,
    bool Available,
    bool LiveEnforced);

internal sealed record FrameworkCapabilityResolution(
    string Id,
    string Status,
    string Risk,
    string Source,
    string EffectScope,
    bool Available,
    bool LiveEnforced,
    string FailurePolicy);

internal static class FrameworkCapabilityRegistry
{
    public const string DecodedSaveConsumer = "decoded-save-applier";
    public const string DecodedSaveRecognitionConsumer = "decoded-save-recognizer";

    private static readonly IReadOnlyDictionary<string, FrameworkCapabilityDefinition> CapabilityDefinitions =
        BuildCapabilityDefinitions();

    private static readonly IReadOnlyDictionary<string, FrameworkActionDefinition> ActionDefinitions =
        BuildActionDefinitions();

    public static IReadOnlyList<FrameworkCapabilityDefinition> Capabilities { get; } = CapabilityDefinitions.Values
        .OrderBy(definition => definition.Id, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static IReadOnlyList<FrameworkActionDefinition> Actions { get; } = ActionDefinitions.Values
        .OrderBy(definition => definition.Type, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static FrameworkCapabilityResolution ResolveCapability(string capability)
    {
        var id = NormalizeCapability(capability);
        if (CapabilityDefinitions.TryGetValue(id, out var definition))
        {
            return new FrameworkCapabilityResolution(
                definition.Id,
                definition.Status,
                definition.Risk,
                definition.Source,
                definition.EffectScope,
                definition.Available,
                definition.LiveEnforced,
                definition.FailurePolicy);
        }

        return new FrameworkCapabilityResolution(
            id,
            "unknown",
            "unknown",
            "unregistered",
            "none",
            false,
            false,
            "skipRule");
    }

    public static bool TryGetAction(string type, out FrameworkActionDefinition definition)
    {
        return ActionDefinitions.TryGetValue(NormalizeActionType(type), out definition!);
    }

    public static bool IsSidecarAction(string type)
    {
        return TryGetAction(type, out var definition) &&
               definition.Available &&
               definition.ExecutionKind == FrameworkActionExecutionKind.Sidecar;
    }

    public static bool IsManagedArtifactAction(string type)
    {
        return TryGetAction(type, out var definition) &&
               definition.Available &&
               definition.ExecutionKind == FrameworkActionExecutionKind.ManagedArtifact;
    }

    public static bool HasConsumer(string type, string consumer)
    {
        return TryGetAction(type, out var definition) &&
               definition.Available &&
               definition.Consumers.Contains(consumer, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> ValidateAction(
        RuntimeRuleAction action,
        IReadOnlySet<string> declaredCapabilities)
    {
        var issues = new List<string>();
        var type = NormalizeActionType(action.Type);
        var capability = NormalizeCapability(action.Capability);
        var risk = NormalizeRisk(action.Risk);

        if (string.IsNullOrWhiteSpace(type))
        {
            issues.Add("action type is missing");
        }

        if (string.IsNullOrWhiteSpace(capability))
        {
            issues.Add("action capability is missing");
        }
        else
        {
            if (!declaredCapabilities.Contains(capability))
            {
                issues.Add($"capability {capability} is not declared by this plugin");
            }

            var capabilityResolution = ResolveCapability(capability);
            if (!capabilityResolution.Available)
            {
                issues.Add(
                    $"capability {capability} is unavailable " +
                    $"(status={capabilityResolution.Status}, source={capabilityResolution.Source})");
            }
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            return issues;
        }

        if (!TryGetAction(type, out var actionDefinition))
        {
            issues.Add($"action type {type} is not registered");
            return issues;
        }

        if (!actionDefinition.Available)
        {
            issues.Add($"action type {type} is unavailable (status={actionDefinition.Status})");
        }

        if (!string.IsNullOrWhiteSpace(capability) &&
            !actionDefinition.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(
                $"action type {type} does not support capability {capability}; " +
                $"expected {string.Join(',', actionDefinition.Capabilities)}");
        }

        if (!risk.Equals(actionDefinition.Risk, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add($"action type {type} requires risk={actionDefinition.Risk}, declared risk={risk}");
        }

        return issues;
    }

    private static IReadOnlyDictionary<string, FrameworkCapabilityDefinition> BuildCapabilityDefinitions()
    {
        var definitions = new[]
        {
            Capability("attempt.record_once", "stable", "safe", "runtime-event-executor", "sidecar-state", true),
            Capability("building.intercept_upgrade_request", "planned", "risky", "unimplemented-native-intercept", "original-town-upgrade", false),
            Capability("campaign.observe_week_advance", "planned", "safe", "unimplemented-save-delta-inference", "campaign-week-event", false),
            Capability("campaign.reset_plot_progress", "materialized", "managed", "managed-action-pipeline", "decoded-profile", true),
            Capability("content.app_config", "stable", "managed", "app-config-patch-compiler", "virtual-content", true),
            Capability("content.patch", "stable", "managed", "patch-plan-compiler", "virtual-content", true),
            Capability("content_refs.validate", "stable", "safe", "content-reference-validator", "content-catalog", true),
            Capability("equipment.filter_available_trinkets", "materialized", "managed", "managed-action-artifact-store", "artifact-only", true),
            Capability("equipment.observe_loadout_confirmed", "observed", "safe", "save-event-bridge", "save-fact-inference", true),
            Capability("equipment.unlock_for_draft", "planned", "managed", "unimplemented-draft-consumer", "party-selection", false),
            Capability("estate.ensure_inventory_counts", "materialized", "managed", "managed-action-pipeline", "decoded-profile", true),
            Capability("estate.remove_inventory_items", "materialized", "managed", "managed-action-pipeline", "decoded-profile", true),
            Capability("file.virtualize", "stable", "managed", "runtime-file-overlay", "game-content-read", true, true),
            Capability("party.observe_selection_confirmed", "observed", "safe", "save-event-bridge", "save-fact-inference", true),
            Capability("party.observe_selection_started", "planned", "safe", "unimplemented-selection-observer", "party-selection", false),
            Capability("profile.detect_new_or_uninitialized", "passive", "safe", "decoded-profile-initializer", "sidecar-initialization", true),
            Capability("profile.mark_initialized", "stable", "safe", "runtime-event-executor", "sidecar-state", true),
            Capability("progression.observe_plot_completion", "passive", "safe", "save-state-exporter", "save-facts", true),
            Capability("quest.chain.define", "stable", "managed", "quest-chain-compiler", "generated-content-and-policy", true),
            Capability("quest.mark_completed", "stable", "safe", "runtime-event-executor", "sidecar-state", true),
            Capability("quest.observe_attempt_resolved", "observed", "safe", "save-event-bridge", "save-fact-inference", true),
            Capability("quest.observe_selection_confirmed", "observed", "safe", "save-event-bridge", "save-fact-inference", true),
            Capability("quest_board.filter_completed_fixed_quests", "stable", "managed", "quest-board-resolver", "resolved-quest-board", true),
            Capability("quest_board.policy", "stable", "managed", "quest-board-policy-compiler", "managed-artifact", true),
            Capability("quest_board.replace_with_fixed_set", "materialized", "managed", "managed-action-pipeline", "content-overlay-and-decoded-profile", true),
            Capability("roster.ensure_class_instances", "materialized", "managed", "managed-action-pipeline", "decoded-profile", true),
            Capability("roster.filter_available_heroes", "materialized", "managed", "managed-action-artifact-store", "artifact-only", true),
            Capability("roster.set_progression", "materialized", "managed", "managed-action-pipeline", "decoded-profile", true),
            Capability("roster.set_skill_unlocks", "materialized", "managed", "managed-action-pipeline", "decoded-profile", true),
            Capability("roster.unlock_draft_pool", "planned", "managed", "unimplemented-draft-consumer", "party-selection", false),
            Capability("save.observe_write", "observed", "safe", "save-directory-watcher", "save-file-events", true),
            Capability("selection.consume_heroes", "stable", "safe", "runtime-event-executor", "sidecar-state", true),
            Capability("selection.consume_trinkets", "stable", "safe", "runtime-event-executor", "sidecar-state", true),
            Capability("selection.lock", "stable", "safe", "runtime-event-executor", "sidecar-state", true),
            Capability("stagecoach.roster_capacity", "stable", "managed", "virtual-file-patch", "content-overlay", true),
            Capability("stagecoach.suppress_recruits", "materialized", "managed", "managed-action-pipeline", "decoded-profile", true),
            Capability("state.sidecar", "stable", "safe", "mod-state-store", "sidecar-state", true),
            Capability("state.transition_phase", "stable", "safe", "runtime-event-executor", "sidecar-state", true),
            Capability("town.set_building_levels", "materialized", "managed", "managed-action-artifact-store", "artifact-only", true),
            Capability("town.suppress_store_items", "materialized", "managed", "managed-action-pipeline", "decoded-profile", true),
            Capability("town.unlock_all_buildings", "materialized", "managed", "managed-action-pipeline", "content-overlay-and-decoded-profile", true),
            Capability("town_event.override_current", "materialized", "managed", "managed-action-pipeline", "decoded-profile", true),
            Capability("trinket.patch_entry", "materialized", "managed", "managed-action-pipeline", "content-overlay", true),
            Capability("upgrade.apply_completed", "planned", "managed", "unimplemented-upgrade-queue-consumer", "original-upgrade-state", false),
            Capability("upgrade.ensure_purchases", "materialized", "managed", "managed-action-pipeline", "decoded-profile", true),
            Capability("upgrade.queue_pending", "planned", "safe", "unimplemented-upgrade-queue", "sidecar-state", false),
            Capability("upgrade.spend_original_cost", "planned", "managed", "unimplemented-upgrade-cost-consumer", "original-wallet", false),
            Capability("wallet.modify_currency", "stable", "safe", "runtime-event-executor", "sidecar-state", true),
            Capability("wallet.set_currency_amount", "materialized", "managed", "managed-action-pipeline", "decoded-profile", true),
            Capability("wallet.set_currency_amounts", "materialized", "managed", "managed-action-pipeline", "decoded-profile", true)
        };

        return definitions.ToDictionary(definition => definition.Id, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, FrameworkActionDefinition> BuildActionDefinitions()
    {
        var definitions = new[]
        {
            SidecarAction("state.setValue", "state.sidecar", "profile.mark_initialized"),
            SidecarAction("state.clearPaths", "state.sidecar"),
            SidecarAction("state.addUniqueRange", "state.sidecar"),
            SidecarAction("state.addUnique", "state.sidecar"),
            SidecarAction("state.incrementCounter", "state.sidecar"),
            SidecarAction("state.setFromArrayIndex", "state.sidecar"),
            SidecarAction("state.setArrayCount", "state.sidecar"),
            SidecarAction("state.mergeDefinition", "state.sidecar"),
            SidecarAction("attempt.recordOnce", "attempt.record_once"),
            SidecarAction("selection.lock", "selection.lock"),
            SidecarAction("selection.consumeHeroes", "selection.consume_heroes"),
            SidecarAction("selection.consumeTrinkets", "selection.consume_trinkets"),
            SidecarAction("quest.markCompletedIfSuccessful", "quest.mark_completed"),
            SidecarAction("state.transitionWhenAllCompleted", "state.transition_phase"),
            SidecarAction("wallet.addCurrencyOnEvent", "wallet.modify_currency"),
            ManagedAction("roster.filterAvailableHeroes", "roster.filter_available_heroes"),
            ManagedAction("equipment.filterAvailableTrinkets", "equipment.filter_available_trinkets"),
            ManagedAction("roster.ensureClassInstances", "roster.ensure_class_instances", DecodedSaveConsumer),
            ManagedAction("roster.setProgression", "roster.set_progression", DecodedSaveConsumer),
            ManagedAction("roster.setSkillUnlocks", "roster.set_skill_unlocks", DecodedSaveConsumer),
            ManagedAction("upgrade.ensurePurchases", "upgrade.ensure_purchases", DecodedSaveConsumer),
            ManagedAction("stagecoach.suppressRecruits", "stagecoach.suppress_recruits", DecodedSaveConsumer, "continuous-profile-applier"),
            ManagedAction("estate.ensureInventoryCounts", "estate.ensure_inventory_counts", DecodedSaveConsumer),
            ManagedAction("estate.removeInventoryItems", "estate.remove_inventory_items", DecodedSaveConsumer),
            ManagedAction("wallet.setCurrencyAmount", "wallet.set_currency_amount", DecodedSaveConsumer),
            ManagedAction("wallet.setCurrencyAmounts", "wallet.set_currency_amounts", DecodedSaveConsumer),
            ManagedAction("trinket.patchEntry", "trinket.patch_entry", "managed-action-overlay-compiler", DecodedSaveRecognitionConsumer),
            ManagedAction("campaign.resetPlotProgress", "campaign.reset_plot_progress", DecodedSaveConsumer),
            ManagedAction("town.unlockAllBuildings", "town.unlock_all_buildings", "managed-action-overlay-compiler", DecodedSaveConsumer),
            ManagedAction("town.setBuildingLevels", "town.set_building_levels"),
            ManagedAction("town.suppressStoreItems", "town.suppress_store_items", DecodedSaveConsumer, "continuous-profile-applier"),
            ManagedAction("townEvent.overrideCurrent", "town_event.override_current", DecodedSaveConsumer, "continuous-profile-applier"),
            ManagedAction("questBoard.replaceWithFixedSet", "quest_board.replace_with_fixed_set", "quest-board-preview", "managed-action-overlay-compiler", DecodedSaveConsumer),
            PlannedAction("roster.unlockDraftPool", "managed", "roster.unlock_draft_pool"),
            PlannedAction("equipment.unlockDraftLoadout", "managed", "equipment.unlock_for_draft"),
            PlannedAction("event.cancelOriginal", "managed", "building.intercept_upgrade_request"),
            PlannedAction("upgrade.spendOriginalCost", "managed", "upgrade.spend_original_cost"),
            PlannedAction("upgrade.queuePending", "safe", "upgrade.queue_pending"),
            PlannedAction("upgrade.advancePending", "safe", "state.sidecar"),
            PlannedAction("upgrade.applyReadyQueued", "managed", "upgrade.apply_completed")
        };

        return definitions.ToDictionary(definition => definition.Type, StringComparer.Ordinal);
    }

    private static FrameworkCapabilityDefinition Capability(
        string id,
        string status,
        string risk,
        string source,
        string effectScope,
        bool available,
        bool liveEnforced = false)
    {
        return new FrameworkCapabilityDefinition(
            id,
            status,
            risk,
            source,
            effectScope,
            available,
            liveEnforced,
            available ? "skipRule" : "disableCapability");
    }

    private static FrameworkActionDefinition SidecarAction(string type, params string[] capabilities)
    {
        return new FrameworkActionDefinition(
            type,
            FrameworkActionExecutionKind.Sidecar,
            "stable",
            "safe",
            capabilities,
            ["runtime-event-executor"],
            true,
            false);
    }

    private static FrameworkActionDefinition ManagedAction(
        string type,
        string capability,
        params string[] consumers)
    {
        return new FrameworkActionDefinition(
            type,
            FrameworkActionExecutionKind.ManagedArtifact,
            "materialized",
            "managed",
            [capability],
            ["managed-action-artifact-store", .. consumers],
            true,
            false);
    }

    private static FrameworkActionDefinition PlannedAction(
        string type,
        string risk,
        string capability)
    {
        return new FrameworkActionDefinition(
            type,
            FrameworkActionExecutionKind.Unavailable,
            "planned",
            risk,
            [capability],
            [],
            false,
            false);
    }

    private static string NormalizeCapability(string capability)
    {
        return (capability ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string NormalizeActionType(string type)
    {
        return (type ?? string.Empty).Trim();
    }

    private static string NormalizeRisk(string risk)
    {
        return (risk ?? string.Empty).Trim().ToLowerInvariant();
    }
}
