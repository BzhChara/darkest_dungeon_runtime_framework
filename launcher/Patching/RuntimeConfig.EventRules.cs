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
        IReadOnlySet<string> pluginDeclaredCapabilities)
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
            var undeclaredRequiredCapabilities = requiredCapabilities
                .Where(capability => !pluginDeclaredCapabilities.Contains(NormalizeCapability(capability)))
                .ToArray();
            if (undeclaredRequiredCapabilities.Length > 0)
            {
                skipped.Add(new RuntimeEventRuleSkip(
                    pluginId,
                    sourceName,
                    sourcePath,
                    loadOrder,
                    index,
                    rule.Id,
                    rule.On,
                    "required capabilities not declared by plugin: " + string.Join(",", undeclaredRequiredCapabilities)));
                continue;
            }

            var unavailableRequiredCapabilities = requiredCapabilities
                .Select(FrameworkCapabilityRegistry.ResolveCapability)
                .Where(capability => !capability.Available)
                .ToArray();
            if (unavailableRequiredCapabilities.Length > 0)
            {
                skipped.Add(new RuntimeEventRuleSkip(
                    pluginId,
                    sourceName,
                    sourcePath,
                    loadOrder,
                    index,
                    rule.Id,
                    rule.On,
                    "required capabilities unavailable: " + FormatCapabilityResolutions(unavailableRequiredCapabilities)));
                continue;
            }

            var requiredActionDiagnostics = actions
                .SelectMany((action, actionIndex) => action.Required
                    ? FrameworkCapabilityRegistry.ValidateAction(action, pluginDeclaredCapabilities)
                        .Select(issue => $"action[{actionIndex}] {issue}")
                    : [])
                .ToArray();
            if (requiredActionDiagnostics.Length > 0)
            {
                skipped.Add(new RuntimeEventRuleSkip(
                    pluginId,
                    sourceName,
                    sourcePath,
                    loadOrder,
                    index,
                    rule.Id,
                    rule.On,
                    "required actions unavailable: " + string.Join("; ", requiredActionDiagnostics)));
                continue;
            }

            var optionalActionSkipReasons = actions
                .Select((action, actionIndex) => new
                {
                    ActionIndex = actionIndex,
                    Issues = action.Required
                        ? []
                        : FrameworkCapabilityRegistry.ValidateAction(action, pluginDeclaredCapabilities)
                })
                .Where(result => result.Issues.Count > 0)
                .ToDictionary(
                    result => result.ActionIndex,
                    result => string.Join("; ", result.Issues));
            var optionalActionDiagnostics = optionalActionSkipReasons
                .Select(result => $"action[{result.Key}] {result.Value}")
                .ToArray();
            var actionCapabilities = CleanCapabilityReferences(actions.Select(action => action.Capability)).ToArray();

            output.Add(new RuntimeEventRuleSource(
                pluginId,
                sourceName,
                sourcePath,
                loadOrder,
                index,
                rule,
                requiredCapabilities,
                actionCapabilities,
                optionalActionSkipReasons,
                optionalActionDiagnostics.Length == 0
                    ? "capabilities satisfied"
                    : "optional action diagnostics: " + string.Join("; ", optionalActionDiagnostics)));
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
        IReadOnlySet<string> pluginDeclaredCapabilities)
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
            var undeclaredRequiredCapabilities = requiredCapabilities
                .Where(capability => !pluginDeclaredCapabilities.Contains(NormalizeCapability(capability)))
                .ToArray();
            if (undeclaredRequiredCapabilities.Length > 0)
            {
                skipped.Add(new FactEventRuleSkip(
                    pluginId,
                    sourceName,
                    sourcePath,
                    loadOrder,
                    index,
                    rule.Id,
                    rule.Emit,
                    "required capabilities not declared by plugin: " + string.Join(",", undeclaredRequiredCapabilities)));
                continue;
            }

            var unavailableRequiredCapabilities = requiredCapabilities
                .Select(FrameworkCapabilityRegistry.ResolveCapability)
                .Where(capability => !capability.Available)
                .ToArray();
            if (unavailableRequiredCapabilities.Length > 0)
            {
                skipped.Add(new FactEventRuleSkip(
                    pluginId,
                    sourceName,
                    sourcePath,
                    loadOrder,
                    index,
                    rule.Id,
                    rule.Emit,
                    "required capabilities unavailable: " + FormatCapabilityResolutions(unavailableRequiredCapabilities)));
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

    private static string FormatCapabilityResolutions(
        IEnumerable<FrameworkCapabilityResolution> resolutions)
    {
        return string.Join(
            ",",
            resolutions.Select(capability =>
                $"{capability.Id}(status={capability.Status},source={capability.Source})"));
    }
}
