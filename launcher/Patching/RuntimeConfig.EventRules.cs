namespace DDRuntimeLoader;

internal sealed partial class RuntimeConfig
{
    private static void AddRuntimeEventRules(
        List<RuntimeEventRuleSource> output,
        List<RuntimeEventRuleSkip> skipped,
        IEnumerable<RuntimeEventRule>? input,
        string pluginId,
        string sourceName,
        string sourcePath,
        int loadOrder,
        IReadOnlySet<string> activeCapabilities)
    {
        var index = 0;
        foreach (var rule in input ?? [])
        {
            index++;
            if (!rule.Enabled)
            {
                skipped.Add(new RuntimeEventRuleSkip(pluginId, sourceName, sourcePath, loadOrder, index, rule.Id, rule.On, "rule disabled"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(rule.On))
            {
                skipped.Add(new RuntimeEventRuleSkip(pluginId, sourceName, sourcePath, loadOrder, index, rule.Id, rule.On, "missing event id"));
                continue;
            }

            var actions = rule.Actions ?? [];
            if (actions.Length == 0)
            {
                skipped.Add(new RuntimeEventRuleSkip(pluginId, sourceName, sourcePath, loadOrder, index, rule.Id, rule.On, "missing actions"));
                continue;
            }

            var requiredCapabilities = CleanCapabilityReferences(rule.RequiresCapabilities).ToArray();
            var missingRequiredCapabilities = requiredCapabilities
                .Where(capability => !activeCapabilities.Contains(NormalizeCapability(capability)))
                .ToArray();
            if (missingRequiredCapabilities.Length > 0)
            {
                skipped.Add(new RuntimeEventRuleSkip(
                    pluginId,
                    sourceName,
                    sourcePath,
                    loadOrder,
                    index,
                    rule.Id,
                    rule.On,
                    "required capabilities missing: " + string.Join(",", missingRequiredCapabilities)));
                continue;
            }

            var actionCapabilities = CleanCapabilityReferences(actions.Select(action => action.Capability)).ToArray();
            var missingRequiredActionCapabilities = CleanCapabilityReferences(
                    actions
                        .Where(action => action.Required)
                        .Select(action => action.Capability))
                .Where(capability => !activeCapabilities.Contains(NormalizeCapability(capability)))
                .ToArray();
            if (missingRequiredActionCapabilities.Length > 0)
            {
                skipped.Add(new RuntimeEventRuleSkip(
                    pluginId,
                    sourceName,
                    sourcePath,
                    loadOrder,
                    index,
                    rule.Id,
                    rule.On,
                    "required action capabilities missing: " + string.Join(",", missingRequiredActionCapabilities)));
                continue;
            }

            var missingOptionalActionCapabilities = CleanCapabilityReferences(
                    actions
                        .Where(action => !action.Required)
                        .Select(action => action.Capability))
                .Where(capability => !activeCapabilities.Contains(NormalizeCapability(capability)))
                .ToArray();

            output.Add(new RuntimeEventRuleSource(
                pluginId,
                sourceName,
                sourcePath,
                loadOrder,
                index,
                rule,
                requiredCapabilities,
                actionCapabilities,
                missingOptionalActionCapabilities,
                missingOptionalActionCapabilities.Length == 0
                    ? "capabilities satisfied"
                    : "optional action capabilities missing: " + string.Join(",", missingOptionalActionCapabilities)));
        }
    }

    private static void AddFactEventRules(
        List<FactEventRuleSource> output,
        List<FactEventRuleSkip> skipped,
        IEnumerable<FactEventRule>? input,
        string pluginId,
        string sourceName,
        string sourcePath,
        int loadOrder,
        IReadOnlySet<string> activeCapabilities)
    {
        var index = 0;
        foreach (var rule in input ?? [])
        {
            index++;
            if (!rule.Enabled)
            {
                skipped.Add(new FactEventRuleSkip(pluginId, sourceName, sourcePath, loadOrder, index, rule.Id, rule.Emit, "rule disabled"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(rule.Emit))
            {
                skipped.Add(new FactEventRuleSkip(pluginId, sourceName, sourcePath, loadOrder, index, rule.Id, rule.Emit, "missing emitted event id"));
                continue;
            }

            var requiredCapabilities = CleanCapabilityReferences(rule.RequiresCapabilities).ToArray();
            var missingRequiredCapabilities = requiredCapabilities
                .Where(capability => !activeCapabilities.Contains(NormalizeCapability(capability)))
                .ToArray();
            if (missingRequiredCapabilities.Length > 0)
            {
                skipped.Add(new FactEventRuleSkip(
                    pluginId,
                    sourceName,
                    sourcePath,
                    loadOrder,
                    index,
                    rule.Id,
                    rule.Emit,
                    "required capabilities missing: " + string.Join(",", missingRequiredCapabilities)));
                continue;
            }

            output.Add(new FactEventRuleSource(
                pluginId,
                sourceName,
                sourcePath,
                loadOrder,
                index,
                rule,
                requiredCapabilities,
                "capabilities satisfied"));
        }
    }
}
