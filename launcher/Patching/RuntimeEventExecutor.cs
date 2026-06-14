using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DDRuntimeLoader;

internal static class RuntimeEventExecutor
{
    private const int ReportVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static RuntimeEventExecutionReport Execute(
        RuntimeConfig config,
        PatchPlan patchPlan,
        LauncherLog log,
        string eventId,
        string? payloadJson,
        string? payloadFile,
        string projectRoot,
        string? pluginIdFilter)
    {
        eventId = eventId.Trim();
        if (string.IsNullOrWhiteSpace(eventId))
        {
            throw new ArgumentException("Event id cannot be empty.", nameof(eventId));
        }

        var payload = LoadPayload(projectRoot, payloadJson, payloadFile);
        var issues = new List<RuntimeEventExecutionIssue>();
        var rules = SelectRules(patchPlan, eventId, pluginIdFilter).ToArray();
        var ruleReports = new List<RuntimeEventRuleExecutionReport>();
        var stateDocuments = new Dictionary<string, ModStateDocument>(StringComparer.OrdinalIgnoreCase);
        var changedStatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var executedActionCount = 0;
        var plannedActionCount = 0;
        var materializedActionCount = 0;
        var stateWriteCount = 0;

        var initReport = ModStateStore.InitializeDefaults(config, patchPlan, log, pluginIdFilter);
        AddStateIssues(initReport.Issues, issues);

        foreach (var sourceRule in rules)
        {
            var actionReports = new List<RuntimeEventActionExecutionReport>();
            var predicateResult = EvaluatePredicate(sourceRule, sourceRule.Rule.When, payload, stateDocuments, config, patchPlan, issues);
            if (!predicateResult.Matched)
            {
                ruleReports.Add(new RuntimeEventRuleExecutionReport(
                    sourceRule.PluginId,
                    sourceRule.SourceName,
                    sourceRule.SourcePath,
                    sourceRule.LoadOrder,
                    sourceRule.RuleIndex,
                    sourceRule.Rule.Id,
                    sourceRule.Rule.On,
                    "predicate-skipped",
                    predicateResult.Reason,
                    actionReports));
                continue;
            }

            var ruleStatus = "executed";
            for (var actionIndex = 0; actionIndex < sourceRule.Rule.Actions.Length; actionIndex++)
            {
                var action = sourceRule.Rule.Actions[actionIndex];
                var actionReport = ExecuteAction(
                    sourceRule,
                    actionIndex,
                    action,
                    payload,
                    stateDocuments,
                    changedStatePaths,
                    config,
                    patchPlan,
                    issues);
                actionReports.Add(actionReport);

                if (actionReport.Status == "executed")
                {
                    executedActionCount++;
                    continue;
                }

                if (actionReport.Status == "materialized")
                {
                    materializedActionCount++;
                    continue;
                }

                if (actionReport.Status == "planned")
                {
                    plannedActionCount++;
                    continue;
                }

                if (action.Required && actionReport.Status == "failed")
                {
                    ruleStatus = "failed";
                    break;
                }
            }

            ruleReports.Add(new RuntimeEventRuleExecutionReport(
                sourceRule.PluginId,
                sourceRule.SourceName,
                sourceRule.SourcePath,
                sourceRule.LoadOrder,
                sourceRule.RuleIndex,
                sourceRule.Rule.Id,
                sourceRule.Rule.On,
                ruleStatus,
                predicateResult.Reason,
                actionReports));
        }

        foreach (var document in stateDocuments.Values.Where(document => changedStatePaths.Contains(document.StatePath)))
        {
            var stateIssues = new List<ModStateIssue>();
            var writeResult = ModStateStore.SaveStateDocument(document, config.AllowNonAtomicStateWrites, stateIssues);
            AddStateIssues(stateIssues, issues);
            if (writeResult.Succeeded)
            {
                stateWriteCount++;
            }
        }

        var report = new RuntimeEventExecutionReport(
            ReportVersion,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            eventId,
            config.ModStateDirectory,
            rules.Length,
            ruleReports.Count(rule => rule.Status != "predicate-skipped"),
            executedActionCount,
            plannedActionCount,
            materializedActionCount,
            stateWriteCount,
            payload,
            ruleReports,
            issues);

        LogAndWriteReport(config, log, report);
        return report;
    }

    private static IEnumerable<RuntimeEventRuleSource> SelectRules(PatchPlan patchPlan, string eventId, string? pluginIdFilter)
    {
        return patchPlan.SourceRuntimeEventRules
            .Where(rule => rule.Rule.On.Equals(eventId, StringComparison.OrdinalIgnoreCase))
            .Where(rule => string.IsNullOrWhiteSpace(pluginIdFilter) || rule.PluginId.Equals(pluginIdFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(rule => PluginPhaseRank(rule.Rule.Phase))
            .ThenBy(rule => rule.Rule.Priority)
            .ThenBy(rule => rule.LoadOrder)
            .ThenBy(rule => rule.SourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(rule => rule.RuleIndex);
    }

    private static JsonObject LoadPayload(string projectRoot, string? payloadJson, string? payloadFile)
    {
        if (!string.IsNullOrWhiteSpace(payloadJson) && !string.IsNullOrWhiteSpace(payloadFile))
        {
            throw new ArgumentException("Use either --event-payload or --event-payload-file, not both.");
        }

        var json = "{}";
        if (!string.IsNullOrWhiteSpace(payloadFile))
        {
            var path = Path.GetFullPath(Path.IsPathRooted(payloadFile) ? payloadFile : Path.Combine(projectRoot, payloadFile));
            json = File.ReadAllText(path, Encoding.UTF8);
        }
        else if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            json = payloadJson;
        }

        var node = JsonNode.Parse(json) ?? new JsonObject();
        if (node is JsonObject obj)
        {
            return obj;
        }

        throw new ArgumentException("Event payload must be a JSON object.");
    }

    private static RuntimePredicateResult EvaluatePredicate(
        RuntimeEventRuleSource sourceRule,
        RuntimeRulePredicate? predicate,
        JsonObject payload,
        Dictionary<string, ModStateDocument> stateDocuments,
        RuntimeConfig config,
        PatchPlan patchPlan,
        List<RuntimeEventExecutionIssue> issues)
    {
        if (predicate is null)
        {
            return new RuntimePredicateResult(true, "no predicate");
        }

        var all = predicate.All ?? [];
        foreach (var child in all)
        {
            var result = EvaluatePredicate(sourceRule, child, payload, stateDocuments, config, patchPlan, issues);
            if (!result.Matched)
            {
                return new RuntimePredicateResult(false, "all failed: " + result.Reason);
            }
        }

        var any = predicate.Any ?? [];
        if (any.Length > 0)
        {
            var anyResults = any
                .Select(child => EvaluatePredicate(sourceRule, child, payload, stateDocuments, config, patchPlan, issues))
                .ToArray();
            if (!anyResults.Any(result => result.Matched))
            {
                return new RuntimePredicateResult(false, "any failed: " + string.Join("; ", anyResults.Select(result => result.Reason)));
            }
        }

        var none = predicate.None ?? [];
        foreach (var child in none)
        {
            var result = EvaluatePredicate(sourceRule, child, payload, stateDocuments, config, patchPlan, issues);
            if (result.Matched)
            {
                return new RuntimePredicateResult(false, "none matched: " + result.Reason);
            }
        }

        if (!string.IsNullOrWhiteSpace(predicate.State))
        {
            var document = GetStateDocument(sourceRule, stateDocuments, config, patchPlan, issues);
            if (document is null)
            {
                return new RuntimePredicateResult(false, "state unavailable: " + predicate.State);
            }

            return EvaluateLeaf(predicate.Op, document.State, predicate.State, predicate.Value, "state");
        }

        if (!string.IsNullOrWhiteSpace(predicate.Fact))
        {
            var factsRoot = payload["facts"] ?? payload;
            return EvaluateLeaf(predicate.Op, factsRoot, predicate.Fact, predicate.Value, "fact");
        }

        if (!string.IsNullOrWhiteSpace(predicate.Event))
        {
            return EvaluateLeaf(predicate.Op, payload, predicate.Event, predicate.Value, "event");
        }

        return new RuntimePredicateResult(true, "empty predicate");
    }

    private static RuntimePredicateResult EvaluateLeaf(string op, JsonNode? root, string path, JsonElement? expected, string addressSpace)
    {
        var exists = TryGetPath(root, path, out var actual);
        var normalizedOp = string.IsNullOrWhiteSpace(op) ? "exists" : op.Trim().ToLowerInvariant();

        if (normalizedOp == "exists")
        {
            return new RuntimePredicateResult(exists, $"{addressSpace}.{path} exists={exists}");
        }

        if (normalizedOp == "notexists")
        {
            return new RuntimePredicateResult(!exists, $"{addressSpace}.{path} exists={exists}");
        }

        if (!exists)
        {
            return new RuntimePredicateResult(false, $"{addressSpace}.{path} missing");
        }

        var expectedNode = expected.HasValue ? JsonNode.Parse(expected.Value.GetRawText()) : null;
        var matched = normalizedOp switch
        {
            "equals" => JsonNode.DeepEquals(actual, expectedNode),
            "notequals" => !JsonNode.DeepEquals(actual, expectedNode),
            "greater" => CompareNumbers(actual, expectedNode) > 0,
            "greaterorequal" => CompareNumbers(actual, expectedNode) >= 0,
            "less" => CompareNumbers(actual, expectedNode) < 0,
            "lessorequal" => CompareNumbers(actual, expectedNode) <= 0,
            "contains" => ContainsValue(actual, expectedNode),
            "notcontains" => !ContainsValue(actual, expectedNode),
            "matches" => MatchesPattern(actual, expectedNode),
            _ => false
        };

        return new RuntimePredicateResult(matched, $"{addressSpace}.{path} {normalizedOp} matched={matched}");
    }

    private static RuntimeEventActionExecutionReport ExecuteAction(
        RuntimeEventRuleSource sourceRule,
        int actionIndex,
        RuntimeRuleAction action,
        JsonObject payload,
        Dictionary<string, ModStateDocument> stateDocuments,
        HashSet<string> changedStatePaths,
        RuntimeConfig config,
        PatchPlan patchPlan,
        List<RuntimeEventExecutionIssue> issues)
    {
        var type = action.Type.Trim();
        if (IsSupportedManagedPlanAction(type))
        {
            var managedDocument = GetStateDocument(sourceRule, stateDocuments, config, patchPlan, issues);
            if (managedDocument is null)
            {
                var message = "plugin sidecar state is unavailable";
                issues.Add(new RuntimeEventExecutionIssue("error", "state-unavailable", sourceRule.PluginId, sourceRule.Rule.Id, type, message));
                return new RuntimeEventActionExecutionReport(type, action.Capability, action.Risk, action.Required, "failed", message, null, null);
            }

            try
            {
                var plan = BuildManagedActionPlan(type, action, managedDocument, payload);
                var artifactPath = ManagedActionArtifactStore.Write(
                    config,
                    sourceRule,
                    actionIndex,
                    action,
                    payload,
                    plan);
                return new RuntimeEventActionExecutionReport(
                    type,
                    action.Capability,
                    action.Risk,
                    action.Required,
                    "materialized",
                    "managed action artifact written",
                    plan,
                    artifactPath);
            }
            catch (Exception ex)
            {
                issues.Add(new RuntimeEventExecutionIssue("error", "managed-action-materialize-failed", sourceRule.PluginId, sourceRule.Rule.Id, type, ex.Message));
                return new RuntimeEventActionExecutionReport(type, action.Capability, action.Risk, action.Required, "failed", ex.Message, null, null);
            }
        }

        if (!IsSupportedSafeAction(type))
        {
            var severity = action.Required ? "error" : "warning";
            var message = $"action type is not implemented by the safe event executor: {type}";
            issues.Add(new RuntimeEventExecutionIssue(severity, "unsupported-action", sourceRule.PluginId, sourceRule.Rule.Id, type, message));
            return new RuntimeEventActionExecutionReport(type, action.Capability, action.Risk, action.Required, action.Required ? "failed" : "skipped", message, null, null);
        }

        var document = GetStateDocument(sourceRule, stateDocuments, config, patchPlan, issues);
        if (document is null)
        {
            var message = "plugin sidecar state is unavailable";
            issues.Add(new RuntimeEventExecutionIssue("error", "state-unavailable", sourceRule.PluginId, sourceRule.Rule.Id, type, message));
            return new RuntimeEventActionExecutionReport(type, action.Capability, action.Risk, action.Required, "failed", message, null, null);
        }

        try
        {
            var changed = type switch
            {
                "state.setValue" => ExecuteSetValue(action, document, payload),
                "state.clearPaths" => ExecuteClearPaths(action, document),
                "state.addUniqueRange" => ExecuteAddUniqueRange(action, document, payload),
                "state.addUnique" => ExecuteAddUnique(action, document, payload),
                "state.incrementCounter" => ExecuteIncrementCounter(action, document),
                "state.mergeDefinition" => ExecuteMergeDefinition(sourceRule, action, document),
                "attempt.recordOnce" => ExecuteRecordAttemptOnce(action, document, payload),
                "selection.lock" => ExecuteLockSelection(action, document, payload),
                "selection.consumeHeroes" => ExecuteConsumeSelectionArray(action, document, "heroIds"),
                "selection.consumeTrinkets" => ExecuteConsumeSelectionArray(action, document, "trinketIds"),
                "quest.markCompletedIfSuccessful" => ExecuteMarkCompletedIfSuccessful(action, document, payload),
                "state.transitionWhenAllCompleted" => ExecuteTransitionWhenAllCompleted(action, document),
                "wallet.addCurrencyOnEvent" => ExecuteAddCurrencyOnEvent(action, document, payload),
                "challenge.lockStageSelection" => ExecuteLockStageSelection(action, document, payload),
                "challenge.recordFailedAttempt" => ExecuteRecordStageAttempt(action, document, payload, "failed"),
                "challenge.advanceStage" => ExecuteAdvanceStage(action, document, payload),
                "challenge.initializeRunState" => ExecuteInitializeChallengeRun(sourceRule, action, document),
                _ => false
            };

            if (changed)
            {
                changedStatePaths.Add(document.StatePath);
            }

            return new RuntimeEventActionExecutionReport(type, action.Capability, action.Risk, action.Required, "executed", changed ? "state changed" : "no state change", null, null);
        }
        catch (Exception ex)
        {
            issues.Add(new RuntimeEventExecutionIssue("error", "action-failed", sourceRule.PluginId, sourceRule.Rule.Id, type, ex.Message));
            return new RuntimeEventActionExecutionReport(type, action.Capability, action.Risk, action.Required, "failed", ex.Message, null, null);
        }
    }

    private static JsonObject BuildManagedActionPlan(string type, RuntimeRuleAction action, ModStateDocument document, JsonObject payload)
    {
        return type switch
        {
            "quest.injectFixedStage" => BuildQuestInjectFixedStagePlan(action, document, payload),
            "roster.filterAvailableHeroes" => BuildAvailabilityFilterPlan(action, document, payload, "roster.heroes", "hero"),
            "equipment.filterAvailableTrinkets" => BuildAvailabilityFilterPlan(action, document, payload, "equipment.trinkets", "trinket"),
            "roster.enforceAvailabilityFilter" => BuildGenericManagedActionPlan(action, document, payload, "enforceAvailabilityFilter", "profile.roster.availability"),
            "equipment.enforceAvailabilityFilter" => BuildGenericManagedActionPlan(action, document, payload, "enforceAvailabilityFilter", "profile.equipment.availability"),
            "roster.ensureClassInstances" => BuildGenericManagedActionPlan(action, document, payload, "ensureClassInstances", "profile.roster"),
            "roster.setProgression" => BuildGenericManagedActionPlan(action, document, payload, "setProgression", "profile.roster"),
            "roster.setSkillUnlocks" => BuildGenericManagedActionPlan(action, document, payload, "setSkillUnlocks", "profile.roster"),
            "upgrade.ensurePurchases" => BuildGenericManagedActionPlan(action, document, payload, "ensurePurchases", "profile.upgrades"),
            "stagecoach.suppressRecruits" => BuildGenericManagedActionPlan(action, document, payload, "suppressRecruits", "profile.stagecoach"),
            "estate.ensureInventoryCounts" => BuildGenericManagedActionPlan(action, document, payload, "ensureInventoryCounts", "profile.estate.inventory"),
            "wallet.setCurrencyAmount" => BuildGenericManagedActionPlan(action, document, payload, "setCurrencyAmount", "profile.wallet"),
            "wallet.setCurrencyAmounts" => BuildGenericManagedActionPlan(action, document, payload, "setCurrencyAmounts", "profile.wallet"),
            "inventory.disableItemSale" => BuildGenericManagedActionPlan(action, document, payload, "disableItemSale", "profile.inventory"),
            "campaign.resetPlotProgress" => BuildGenericManagedActionPlan(action, document, payload, "resetPlotProgress", "profile.campaignProgress"),
            "town.unlockAllBuildings" => BuildGenericManagedActionPlan(action, document, payload, "unlockAllBuildings", "profile.town"),
            "town.setBuildingLevels" => BuildGenericManagedActionPlan(action, document, payload, "setBuildingLevels", "profile.town"),
            "town.suppressStoreItems" => BuildGenericManagedActionPlan(action, document, payload, "suppressStoreItems", "profile.town.stores"),
            "townEvent.overrideCurrent" => BuildGenericManagedActionPlan(action, document, payload, "overrideCurrent", "profile.townEvent"),
            "questBoard.replaceWithFixedSet" => BuildGenericManagedActionPlan(action, document, payload, "replaceWithFixedSet", "profile.questBoard"),
            _ => throw new InvalidOperationException($"managed action type is not plannable: {type}")
        };
    }

    private static JsonObject BuildQuestInjectFixedStagePlan(RuntimeRuleAction action, ModStateDocument document, JsonObject payload)
    {
        var stage = ResolveRequiredArgNode(action, "stage", document.State, payload);
        if (stage is not JsonObject)
        {
            throw new InvalidOperationException($"Action {action.Type} arg 'stage' must resolve to a stage object.");
        }

        var plan = new JsonObject
        {
            ["kind"] = action.Type,
            ["effect"] = "injectFixedStage",
            ["target"] = "quest.currentStage",
            ["stage"] = CloneNode(stage)
        };

        if (TryGetStringArg(action, "source", out var source))
        {
            plan["source"] = source;
        }

        return plan;
    }

    private static JsonObject BuildAvailabilityFilterPlan(
        RuntimeRuleAction action,
        ModStateDocument document,
        JsonObject payload,
        string target,
        string itemKind)
    {
        var source = RequireStringArg(action, "source");
        var pool = ResolveSourceArray(action, "source", source, document.State, payload);
        var excluded = ReadStringSet(RequirePath(document.State, RequireStringArg(action, "excludeStateList"), "state", action, "excludeStateList"));

        var lockedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectionLocked = false;
        if (TryGetStringArg(action, "lockedSelectionKey", out var lockedSelectionKey) &&
            TryGetPath(document.State, lockedSelectionKey, out var lockedNode) &&
            lockedNode is not null)
        {
            lockedSet = ReadStringSet(lockedNode);
            selectionLocked = true;
        }

        var rows = new JsonArray();
        var allowedCount = 0;
        var blockedCount = 0;
        var lockedCount = 0;

        foreach (var item in pool)
        {
            var id = ReadPoolItemId(action, item);
            var reasons = new JsonArray();
            if (excluded.Contains(id))
            {
                reasons.Add("used_by_completed_stage");
            }

            if (selectionLocked && !lockedSet.Contains(id))
            {
                reasons.Add("current_stage_selection_locked");
            }

            var lockedForRetry = selectionLocked && lockedSet.Contains(id);
            var blocked = reasons.Count > 0;
            var status = blocked ? "unavailable" : lockedForRetry ? "locked_for_retry" : "available";
            if (lockedForRetry && !blocked)
            {
                lockedCount++;
            }

            if (blocked)
            {
                blockedCount++;
            }
            else
            {
                allowedCount++;
            }

            rows.Add(new JsonObject
            {
                ["id"] = id,
                ["kind"] = itemKind,
                ["status"] = status,
                ["allowed"] = !blocked,
                ["reasons"] = reasons,
                ["source"] = CloneNode(item)
            });
        }

        return new JsonObject
        {
            ["kind"] = action.Type,
            ["effect"] = "filterAvailable",
            ["target"] = target,
            ["source"] = source,
            ["selectionLocked"] = selectionLocked,
            ["totalCount"] = pool.Count,
            ["allowedCount"] = allowedCount,
            ["blockedCount"] = blockedCount,
            ["lockedCount"] = lockedCount,
            ["items"] = rows
        };
    }

    private static JsonObject BuildGenericManagedActionPlan(
        RuntimeRuleAction action,
        ModStateDocument document,
        JsonObject payload,
        string effect,
        string defaultTarget)
    {
        var arguments = ResolveManagedActionArguments(action, document.State, payload);
        var target = defaultTarget;
        if (arguments["target"] is JsonValue targetValue &&
            targetValue.TryGetValue<string>(out var targetText) &&
            !string.IsNullOrWhiteSpace(targetText))
        {
            target = targetText;
        }

        return new JsonObject
        {
            ["kind"] = action.Type,
            ["effect"] = effect,
            ["target"] = target,
            ["arguments"] = arguments
        };
    }

    private static JsonObject ResolveManagedActionArguments(RuntimeRuleAction action, JsonObject state, JsonObject payload)
    {
        var arguments = new JsonObject();
        foreach (var pair in action.Args)
        {
            var node = JsonNode.Parse(pair.Value.GetRawText());
            arguments[pair.Key] = ResolveManagedActionArgumentNode(action, pair.Key, node, state, payload);
        }

        return arguments;
    }

    private static JsonNode? ResolveManagedActionArgumentNode(
        RuntimeRuleAction action,
        string argName,
        JsonNode? node,
        JsonObject state,
        JsonObject payload)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            if (text.StartsWith("event.", StringComparison.OrdinalIgnoreCase))
            {
                return RequirePath(payload, text["event.".Length..], "event", action, argName);
            }

            if (text.StartsWith("state.", StringComparison.OrdinalIgnoreCase))
            {
                return RequirePath(state, text["state.".Length..], "state", action, argName);
            }

            return JsonValue.Create(text);
        }

        if (node is JsonArray array)
        {
            var resolved = new JsonArray();
            foreach (var item in array)
            {
                resolved.Add(ResolveManagedActionArgumentNode(action, argName, item, state, payload));
            }

            return resolved;
        }

        if (node is JsonObject obj)
        {
            var resolved = new JsonObject();
            foreach (var property in obj)
            {
                resolved[property.Key] = ResolveManagedActionArgumentNode(action, $"{argName}.{property.Key}", property.Value, state, payload);
            }

            return resolved;
        }

        return CloneNode(node);
    }

    private static JsonArray ResolveSourceArray(RuntimeRuleAction action, string argName, string source, JsonObject state, JsonObject payload)
    {
        var node = ResolveSourceNode(action, argName, source, state, payload);
        if (node is JsonArray array)
        {
            return array;
        }

        throw new InvalidOperationException($"Action {action.Type} arg '{argName}' must resolve to an array source.");
    }

    private static JsonNode? ResolveSourceNode(RuntimeRuleAction action, string argName, string source, JsonObject state, JsonObject payload)
    {
        if (source.StartsWith("event.", StringComparison.OrdinalIgnoreCase))
        {
            return RequirePath(payload, source["event.".Length..], "event", action, argName);
        }

        if (source.StartsWith("state.", StringComparison.OrdinalIgnoreCase))
        {
            return RequirePath(state, source["state.".Length..], "state", action, argName);
        }

        if (source.StartsWith("challenge.", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = source["challenge.".Length..];
            var statePath = suffix.Equals("stageChain", StringComparison.OrdinalIgnoreCase)
                ? "challengeRun.stages"
                : $"challengeRun.{suffix}";
            return RequirePath(state, statePath, "challenge state", action, argName);
        }

        throw new InvalidOperationException(
            $"Action {action.Type} arg '{argName}' uses unsupported source address '{source}'. Use event.*, state.*, or challenge.*.");
    }

    private static HashSet<string> ReadStringSet(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            throw new InvalidOperationException("Expected a JSON array of string ids.");
        }

        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in array)
        {
            if (item is not JsonValue value ||
                !value.TryGetValue<string>(out var text) ||
                string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("Expected a JSON array of non-empty string ids.");
            }

            values.Add(text);
        }

        return values;
    }

    private static string ReadPoolItemId(RuntimeRuleAction action, JsonNode? item)
    {
        if (item is JsonValue value &&
            value.TryGetValue<string>(out var scalarId) &&
            !string.IsNullOrWhiteSpace(scalarId))
        {
            return scalarId;
        }

        if (item is JsonObject obj &&
            obj["id"] is JsonValue idValue &&
            idValue.TryGetValue<string>(out var objectId) &&
            !string.IsNullOrWhiteSpace(objectId))
        {
            return objectId;
        }

        throw new InvalidOperationException($"Action {action.Type} source items must be string ids or objects with a non-empty string id.");
    }

    private static bool ExecuteAddUniqueRange(RuntimeRuleAction action, ModStateDocument document, JsonObject payload)
    {
        var key = RequireStringArg(action, "key");
        var sourceValue = ResolveOptionalSourceArg(action, document.State, payload, "values");
        var array = GetOrCreateArray(document.State, key);
        var changed = false;
        foreach (var value in AsArrayItems(sourceValue))
        {
            if (array.Any(existing => JsonNode.DeepEquals(existing, value)))
            {
                continue;
            }

            array.Add(CloneNode(value));
            changed = true;
        }

        return changed;
    }

    private static bool ExecuteSetValue(RuntimeRuleAction action, ModStateDocument document, JsonObject payload)
    {
        var key = RequireStringArg(action, "key");
        var value = ResolveRequiredArgNode(action, "value", document.State, payload);
        if (TryGetPath(document.State, key, out var existing) && JsonNode.DeepEquals(existing, value))
        {
            return false;
        }

        SetPath(document.State, key, value);
        return true;
    }

    private static bool ExecuteClearPaths(RuntimeRuleAction action, ModStateDocument document)
    {
        var value = action.Args.ContainsKey("value")
            ? ReadRequiredArgNode(action, "value")
            : null;
        var changed = false;
        foreach (var path in ReadStringArgArray(action, "paths"))
        {
            if (TryGetPath(document.State, path, out var existing) && JsonNode.DeepEquals(existing, value))
            {
                continue;
            }

            SetPath(document.State, path, value);
            changed = true;
        }

        return changed;
    }

    private static bool ExecuteAddUnique(RuntimeRuleAction action, ModStateDocument document, JsonObject payload)
    {
        var key = RequireStringArg(action, "key");
        var value = ResolveRequiredArgNode(action, "value", document.State, payload);
        var array = GetOrCreateArray(document.State, key);
        if (array.Any(existing => JsonNode.DeepEquals(existing, value)))
        {
            return false;
        }

        array.Add(CloneNode(value));
        return true;
    }

    private static bool ExecuteMergeDefinition(RuntimeEventRuleSource sourceRule, RuntimeRuleAction action, ModStateDocument document)
    {
        var stateKey = RequireStringArg(action, "stateKey");
        var definition = RequireStringArg(action, "definition");
        var overwriteExisting = ReadOptionalBoolArg(action, "overwriteExisting", false);
        var definitionPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceRule.SourcePath) ?? ".", definition));
        if (!File.Exists(definitionPath))
        {
            throw new FileNotFoundException("Definition file was not found.", definitionPath);
        }

        var definitionObject = JsonNode.Parse(File.ReadAllText(definitionPath, Encoding.UTF8)) as JsonObject;
        if (definitionObject is null)
        {
            throw new InvalidDataException($"Definition root must be a JSON object: {definitionPath}");
        }

        var target = GetOrCreateObject(document.State, stateKey);
        var changed = false;
        foreach (var property in definitionObject)
        {
            if (!overwriteExisting && target.ContainsKey(property.Key))
            {
                continue;
            }

            if (target.TryGetPropertyValue(property.Key, out var existing) && JsonNode.DeepEquals(existing, property.Value))
            {
                continue;
            }

            target[property.Key] = CloneNode(property.Value);
            changed = true;
        }

        return changed;
    }

    private static bool ExecuteRecordAttemptOnce(RuntimeRuleAction action, ModStateDocument document, JsonObject payload)
    {
        var stateKey = RequireStringArg(action, "stateKey");
        var fingerprint = ResolveFingerprintArg(action, document.State, payload, "fingerprint");
        var attempts = GetOrCreateArray(document.State, stateKey);
        if (attempts.Any(attempt => attempt is JsonObject obj &&
            TryReadString(obj["attemptFingerprint"], out var existing) &&
            existing.Equals(fingerprint, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var attempt = new JsonObject
        {
            ["attemptFingerprint"] = fingerprint,
            ["event"] = CloneNode(payload),
            ["recordedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        if (TryGetStringArg(action, "selectionStateKey", out var selectionStateKey) &&
            TryGetPath(document.State, selectionStateKey, out var selection))
        {
            attempt["selection"] = CloneNode(selection);
        }

        attempts.Add(attempt);
        return true;
    }

    private static bool ExecuteLockSelection(RuntimeRuleAction action, ModStateDocument document, JsonObject payload)
    {
        var stateKey = RequireStringArg(action, "stateKey");
        var questId = ResolveRequiredArgNode(action, "questId", document.State, payload);
        var heroIds = ResolveRequiredArgNode(action, "heroIds", document.State, payload);
        var trinketIds = ResolveRequiredArgNode(action, "trinketIds", document.State, payload);

        var locked = new JsonObject
        {
            ["questId"] = CloneNode(questId),
            ["heroIds"] = new JsonArray(AsArrayItems(heroIds).Select(CloneNode).ToArray()),
            ["trinketIds"] = new JsonArray(AsArrayItems(trinketIds).Select(CloneNode).ToArray()),
            ["lockedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        if (TryGetStringArg(action, "attemptId", out var attemptIdArg))
        {
            locked["attemptId"] = ResolveSourceText(action, document.State, payload, attemptIdArg, "attemptId");
        }

        if (TryGetPath(document.State, stateKey, out var existing) && JsonNode.DeepEquals(existing, locked))
        {
            return false;
        }

        SetPath(document.State, stateKey, locked);
        return true;
    }

    private static bool ExecuteConsumeSelectionArray(RuntimeRuleAction action, ModStateDocument document, string selectionArrayName)
    {
        var key = RequireStringArg(action, "key");
        var selectionStateKey = RequireStringArg(action, "selectionStateKey");
        var sourcePath = $"{selectionStateKey}.{selectionArrayName}";
        var sourceValue = RequirePath(document.State, sourcePath, "state", action, "selectionStateKey");
        var array = GetOrCreateArray(document.State, key);
        var changed = false;
        foreach (var value in AsArrayItems(sourceValue))
        {
            if (array.Any(existing => JsonNode.DeepEquals(existing, value)))
            {
                continue;
            }

            array.Add(CloneNode(value));
            changed = true;
        }

        return changed;
    }

    private static bool ExecuteMarkCompletedIfSuccessful(RuntimeRuleAction action, ModStateDocument document, JsonObject payload)
    {
        if (!ResolveBoolArg(action, "success", document.State, payload))
        {
            return false;
        }

        var key = RequireStringArg(action, "key");
        var questId = ResolveRequiredArgNode(action, "questId", document.State, payload);
        var array = GetOrCreateArray(document.State, key);
        if (array.Any(existing => JsonNode.DeepEquals(existing, questId)))
        {
            return false;
        }

        array.Add(CloneNode(questId));
        return true;
    }

    private static bool ExecuteTransitionWhenAllCompleted(RuntimeRuleAction action, ModStateDocument document)
    {
        var completed = ReadComparableSet(RequirePath(document.State, RequireStringArg(action, "completedKey"), "state", action, "completedKey"));
        var required = ReadComparableSet(RequirePath(document.State, RequireStringArg(action, "requiredKey"), "state", action, "requiredKey"));
        if (required.Count == 0 || !required.All(completed.Contains))
        {
            return false;
        }

        var changed = false;
        var phaseKey = RequireStringArg(action, "phaseKey");
        var nextPhase = JsonValue.Create(RequireStringArg(action, "to"));
        if (!TryGetPath(document.State, phaseKey, out var currentPhase) || !JsonNode.DeepEquals(currentPhase, nextPhase))
        {
            SetPath(document.State, phaseKey, nextPhase);
            changed = true;
        }

        if (action.Args.ContainsKey("clearPaths"))
        {
            var clearValue = action.Args.ContainsKey("clearValue")
                ? ReadRequiredArgNode(action, "clearValue")
                : null;
            foreach (var path in ReadStringArgArray(action, "clearPaths"))
            {
                if (TryGetPath(document.State, path, out var existing) && JsonNode.DeepEquals(existing, clearValue))
                {
                    continue;
                }

                SetPath(document.State, path, clearValue);
                changed = true;
            }
        }

        return changed;
    }

    private static bool ExecuteAddCurrencyOnEvent(RuntimeRuleAction action, ModStateDocument document, JsonObject payload)
    {
        var successArg = TryGetStringArg(action, "success", out _) || action.Args.ContainsKey("success")
            ? ResolveBoolArg(action, "success", document.State, payload)
            : true;
        if (!successArg)
        {
            return false;
        }

        var fingerprint = ResolveFingerprintArg(action, document.State, payload, "fingerprint");
        if (TryGetStringArg(action, "fingerprintStateKey", out var fingerprintStateKey))
        {
            var fingerprints = GetOrCreateArray(document.State, fingerprintStateKey);
            if (fingerprints.Any(existing => TryReadString(existing, out var existingText) &&
                existingText.Equals(fingerprint, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            fingerprints.Add(fingerprint);
        }

        var key = RequireStringArg(action, "key");
        var amount = ResolveIntArg(action, "amount", document.State, payload);
        var current = 0;
        if (TryGetPath(document.State, key, out var existing) && existing is JsonValue value)
        {
            if (value.TryGetValue<int>(out var intValue))
            {
                current = intValue;
            }
            else if (value.TryGetValue<long>(out var longValue))
            {
                current = checked((int)longValue);
            }
            else
            {
                throw new InvalidOperationException($"State path is not an integer currency amount: {key}");
            }
        }

        SetPath(document.State, key, JsonValue.Create(checked(current + amount)));
        return amount != 0;
    }

    private static bool ExecuteIncrementCounter(RuntimeRuleAction action, ModStateDocument document)
    {
        var key = RequireStringArg(action, "key");
        var amount = ReadOptionalIntArg(action, "amount", 1);
        var current = 0;
        if (TryGetPath(document.State, key, out var existing) && existing is JsonValue value)
        {
            if (value.TryGetValue<int>(out var intValue))
            {
                current = intValue;
            }
            else if (value.TryGetValue<long>(out var longValue))
            {
                current = checked((int)longValue);
            }
        }

        SetPath(document.State, key, JsonValue.Create(current + amount));
        return amount != 0;
    }

    private static bool ExecuteLockStageSelection(RuntimeRuleAction action, ModStateDocument document, JsonObject payload)
    {
        var stateKey = RequireStringArg(action, "stateKey");
        var stageId = ResolveRequiredArgNode(action, "stageId", document.State, payload);
        var heroIds = ResolveRequiredArgNode(action, "heroIds", document.State, payload);
        var trinketIds = ResolveRequiredArgNode(action, "trinketIds", document.State, payload);

        var locked = new JsonObject
        {
            ["stageId"] = CloneNode(stageId),
            ["heroIds"] = new JsonArray(AsArrayItems(heroIds).Select(CloneNode).ToArray()),
            ["trinketIds"] = new JsonArray(AsArrayItems(trinketIds).Select(CloneNode).ToArray())
        };

        SetPath(document.State, stateKey, locked);
        return true;
    }

    private static bool ExecuteRecordStageAttempt(RuntimeRuleAction action, ModStateDocument document, JsonObject payload, string result)
    {
        var stateKey = RequireStringArg(action, "stateKey");
        var stageId = ResolveRequiredArgNode(action, "stageId", document.State, payload);
        JsonNode? selection = null;
        if (TryGetStringArg(action, "selectionStateKey", out var selectionStateKey))
        {
            selection = RequirePath(document.State, selectionStateKey, "state", action, "selectionStateKey");
        }

        var attempts = GetOrCreateArray(document.State, stateKey);
        var attemptFingerprint = BuildStageAttemptFingerprint(stageId, payload, result);
        if (!string.IsNullOrWhiteSpace(attemptFingerprint) &&
            attempts.Any(attempt => attempt is JsonObject attemptObject &&
                TryReadString(attemptObject["attemptFingerprint"], out var existingFingerprint) &&
                existingFingerprint.Equals(attemptFingerprint, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var attempt = new JsonObject
        {
            ["stageId"] = CloneNode(stageId),
            ["result"] = result,
            ["selection"] = CloneNode(selection),
            ["recordedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        if (!string.IsNullOrWhiteSpace(attemptFingerprint))
        {
            attempt["attemptFingerprint"] = attemptFingerprint;
        }

        attempts.Add(attempt);
        return true;
    }

    private static string? BuildStageAttemptFingerprint(JsonNode? stageId, JsonObject payload, string result)
    {
        if (TryGetPath(payload, "attemptFingerprint", out var explicitFingerprint) &&
            TryReadString(explicitFingerprint, out var explicitText) &&
            !string.IsNullOrWhiteSpace(explicitText))
        {
            return $"explicit:{explicitText}";
        }

        var hasAttemptIdentity = TryGetPath(payload, "observedAttemptId", out var observedAttemptId) &&
            TryReadString(observedAttemptId, out var attemptIdText) &&
            !string.IsNullOrWhiteSpace(attemptIdText);
        var hasRaidRecordCount = TryGetPath(payload, "observedPartyRaidRecordCount", out var raidRecordCount) &&
            TryReadString(raidRecordCount, out var raidRecordCountText) &&
            !string.IsNullOrWhiteSpace(raidRecordCountText);

        if (!hasAttemptIdentity && !hasRaidRecordCount)
        {
            return null;
        }

        var parts = new List<string>
        {
            "stageAttempt",
            $"stage={ReadFingerprintValue(stageId)}",
            $"result={result}",
            $"attempt={ReadFingerprintValue(hasAttemptIdentity ? observedAttemptId : null)}",
            $"partyRaidRecordCount={ReadFingerprintValue(hasRaidRecordCount ? raidRecordCount : null)}",
            $"sourceQuestId={ReadPayloadFingerprintValue(payload, "sourceQuestId")}",
            $"observedQuestHash={ReadPayloadFingerprintValue(payload, "observedQuestHash")}",
            $"observedSuccess={ReadPayloadFingerprintValue(payload, "observedSuccess")}"
        };

        return string.Join("|", parts);
    }

    private static string ReadPayloadFingerprintValue(JsonObject payload, string path)
    {
        return TryGetPath(payload, path, out var value) ? ReadFingerprintValue(value) : string.Empty;
    }

    private static string ReadFingerprintValue(JsonNode? value)
    {
        return value?.ToJsonString(JsonOptions) ?? "null";
    }

    private static bool TryReadString(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is null)
        {
            return false;
        }

        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<string>(out var stringValue))
            {
                value = stringValue;
                return true;
            }

            if (jsonValue.TryGetValue<int>(out var intValue))
            {
                value = intValue.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (jsonValue.TryGetValue<long>(out var longValue))
            {
                value = longValue.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (jsonValue.TryGetValue<bool>(out var boolValue))
            {
                value = boolValue ? "true" : "false";
                return true;
            }
        }

        value = node.ToJsonString(JsonOptions);
        return true;
    }

    private static bool ExecuteAdvanceStage(RuntimeRuleAction action, ModStateDocument document, JsonObject payload)
    {
        var stateKey = RequireStringArg(action, "stateKey");
        var runState = GetOrCreateObject(document.State, stateKey);
        var completedStageId = ResolveRequiredArgNode(action, "completedStageId", document.State, payload);

        AddUnique(GetOrCreateArray(runState, "completedStageIds"), completedStageId);

        var selection = runState["lockedStageSelection"];
        var attempt = new JsonObject
        {
            ["stageId"] = CloneNode(completedStageId),
            ["result"] = "completed",
            ["selection"] = CloneNode(selection),
            ["recordedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };
        GetOrCreateArray(runState, "stageAttempts").Add(attempt);

        var currentIndex = 0;
        if (runState["currentStageIndex"] is JsonValue value && value.TryGetValue<int>(out var index))
        {
            currentIndex = index;
        }

        runState["currentStageIndex"] = currentIndex + 1;
        UpdateCurrentStage(runState);
        runState["lockedStageSelection"] = null;
        return true;
    }

    private static bool ExecuteInitializeChallengeRun(RuntimeEventRuleSource sourceRule, RuntimeRuleAction action, ModStateDocument document)
    {
        var stateKey = RequireStringArg(action, "stateKey");
        var runState = GetOrCreateObject(document.State, stateKey);
        var changed = EnsureJsonValue(runState, "enabled", JsonValue.Create(true));
        changed |= EnsureJsonValue(runState, "currentStageIndex", JsonValue.Create(0));
        changed |= EnsureJsonValue(runState, "completedStageIds", new JsonArray());
        changed |= EnsureJsonValue(runState, "usedHeroIds", new JsonArray());
        changed |= EnsureJsonValue(runState, "usedTrinketIds", new JsonArray());
        changed |= EnsureJsonValue(runState, "stageAttempts", new JsonArray());

        if (!action.Args.ContainsKey("definition"))
        {
            return changed;
        }

        var definition = RequireStringArg(action, "definition");
        var definitionPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceRule.SourcePath) ?? ".", definition));
        if (!File.Exists(definitionPath))
        {
            throw new FileNotFoundException("Challenge definition file was not found.", definitionPath);
        }

        var challenge = JsonNode.Parse(File.ReadAllText(definitionPath, Encoding.UTF8)) as JsonObject;
        if (challenge is null)
        {
            throw new InvalidDataException($"Challenge definition root must be a JSON object: {definitionPath}");
        }

        if (challenge["id"] is not null)
        {
            changed |= EnsureJsonValue(runState, "challengeId", CloneNode(challenge["id"]));
        }

        if (challenge["name"] is not null)
        {
            changed |= EnsureJsonValue(runState, "challengeName", CloneNode(challenge["name"]));
        }

        if (challenge["stages"] is JsonArray stages)
        {
            changed |= EnsureJsonValue(runState, "stageCount", JsonValue.Create(stages.Count));
            changed |= EnsureJsonValue(runState, "stages", CloneNode(stages));
            changed |= UpdateCurrentStage(runState);
        }

        foreach (var propertyName in new[]
        {
            "partySize",
            "maxTrinketsPerHero",
            "retryPolicy",
            "heroReuse",
            "trinketReuse",
            "heroPoolPolicy",
            "heroPool",
            "trinketPool"
        })
        {
            if (challenge[propertyName] is not null)
            {
                changed |= EnsureJsonValue(runState, propertyName, CloneNode(challenge[propertyName]));
            }
        }

        return changed;
    }

    private static bool UpdateCurrentStage(JsonObject runState)
    {
        var currentIndex = 0;
        if (runState["currentStageIndex"] is JsonValue indexValue && indexValue.TryGetValue<int>(out var index))
        {
            currentIndex = index;
        }

        JsonNode? currentStage = null;
        if (runState["stages"] is JsonArray stages && currentIndex >= 0 && currentIndex < stages.Count)
        {
            currentStage = stages[currentIndex];
        }

        if (JsonNode.DeepEquals(runState["currentStage"], currentStage))
        {
            return false;
        }

        runState["currentStage"] = CloneNode(currentStage);
        return true;
    }

    private static bool IsSupportedSafeAction(string type)
    {
        return type is
            "state.setValue" or
            "state.clearPaths" or
            "state.addUniqueRange" or
            "state.addUnique" or
            "state.incrementCounter" or
            "state.mergeDefinition" or
            "attempt.recordOnce" or
            "selection.lock" or
            "selection.consumeHeroes" or
            "selection.consumeTrinkets" or
            "quest.markCompletedIfSuccessful" or
            "state.transitionWhenAllCompleted" or
            "wallet.addCurrencyOnEvent" or
            "challenge.lockStageSelection" or
            "challenge.recordFailedAttempt" or
            "challenge.advanceStage" or
            "challenge.initializeRunState";
    }

    private static bool IsSupportedManagedPlanAction(string type)
    {
        return type is
            "quest.injectFixedStage" or
            "roster.filterAvailableHeroes" or
            "equipment.filterAvailableTrinkets" or
            "roster.enforceAvailabilityFilter" or
            "equipment.enforceAvailabilityFilter" or
            "roster.ensureClassInstances" or
            "roster.setProgression" or
            "roster.setSkillUnlocks" or
            "upgrade.ensurePurchases" or
            "stagecoach.suppressRecruits" or
            "estate.ensureInventoryCounts" or
            "wallet.setCurrencyAmount" or
            "wallet.setCurrencyAmounts" or
            "inventory.disableItemSale" or
            "campaign.resetPlotProgress" or
            "town.unlockAllBuildings" or
            "town.setBuildingLevels" or
            "town.suppressStoreItems" or
            "townEvent.overrideCurrent" or
            "questBoard.replaceWithFixedSet";
    }

    private static ModStateDocument? GetStateDocument(
        RuntimeEventRuleSource sourceRule,
        Dictionary<string, ModStateDocument> stateDocuments,
        RuntimeConfig config,
        PatchPlan patchPlan,
        List<RuntimeEventExecutionIssue> issues)
    {
        if (stateDocuments.TryGetValue(sourceRule.SourcePath, out var document))
        {
            return document;
        }

        var stateSource = ModStateStore.FindStateSchemaSource(patchPlan, sourceRule.PluginId, sourceRule.SourcePath);
        if (stateSource is null)
        {
            issues.Add(new RuntimeEventExecutionIssue(
                "error",
                "state-schema-not-found",
                sourceRule.PluginId,
                sourceRule.Rule.Id,
                string.Empty,
                "event rule needs sidecar state but plugin has no active stateSchema"));
            return null;
        }

        var stateIssues = new List<ModStateIssue>();
        if (!ModStateStore.TryOpenStateDocument(config, patchPlan, stateSource, stateIssues, out document) || document is null)
        {
            AddStateIssues(stateIssues, issues);
            return null;
        }

        stateDocuments[sourceRule.SourcePath] = document;
        return document;
    }

    private static JsonArray GetOrCreateArray(JsonObject root, string path)
    {
        if (TryGetPath(root, path, out var node))
        {
            if (node is JsonArray array)
            {
                return array;
            }

            throw new InvalidOperationException($"State path is not an array: {path}");
        }

        var created = new JsonArray();
        SetPath(root, path, created);
        return created;
    }

    private static JsonObject GetOrCreateObject(JsonObject root, string path)
    {
        if (TryGetPath(root, path, out var node))
        {
            if (node is JsonObject obj)
            {
                return obj;
            }

            throw new InvalidOperationException($"State path is not an object: {path}");
        }

        var created = new JsonObject();
        SetPath(root, path, created);
        return created;
    }

    private static bool EnsureJsonValue(JsonObject root, string key, JsonNode? value)
    {
        if (root.ContainsKey(key))
        {
            return false;
        }

        root[key] = CloneNode(value);
        return true;
    }

    private static void AddUnique(JsonArray array, JsonNode? value)
    {
        if (!array.Any(existing => JsonNode.DeepEquals(existing, value)))
        {
            array.Add(CloneNode(value));
        }
    }

    private static JsonNode? ResolveRequiredArgNode(RuntimeRuleAction action, string argName, JsonObject state, JsonObject payload)
    {
        var node = ReadRequiredArgNode(action, argName);
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            if (text.StartsWith("event.", StringComparison.OrdinalIgnoreCase))
            {
                return RequirePath(payload, text["event.".Length..], "event", action, argName);
            }

            if (text.StartsWith("state.", StringComparison.OrdinalIgnoreCase))
            {
                return RequirePath(state, text["state.".Length..], "state", action, argName);
            }
        }

        return CloneNode(node);
    }

    private static JsonNode? ResolveOptionalSourceArg(RuntimeRuleAction action, JsonObject state, JsonObject payload, string literalArgName)
    {
        if (TryGetStringArg(action, "fromEvent", out var fromEvent))
        {
            return RequirePath(payload, fromEvent, "event", action, "fromEvent");
        }

        if (TryGetStringArg(action, "fromState", out var fromState))
        {
            return RequirePath(state, fromState, "state", action, "fromState");
        }

        return ReadRequiredArgNode(action, literalArgName);
    }

    private static string ResolveFingerprintArg(RuntimeRuleAction action, JsonObject state, JsonObject payload, string argName)
    {
        var node = ResolveRequiredArgNode(action, argName, state, payload);
        if (!TryReadString(node, out var text) || string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"Action {action.Type} arg '{argName}' must resolve to a non-empty scalar fingerprint.");
        }

        return text;
    }

    private static string ResolveSourceText(RuntimeRuleAction action, JsonObject state, JsonObject payload, string source, string argName)
    {
        JsonNode? node;
        if (source.StartsWith("event.", StringComparison.OrdinalIgnoreCase))
        {
            node = RequirePath(payload, source["event.".Length..], "event", action, argName);
        }
        else if (source.StartsWith("state.", StringComparison.OrdinalIgnoreCase))
        {
            node = RequirePath(state, source["state.".Length..], "state", action, argName);
        }
        else
        {
            node = JsonValue.Create(source);
        }

        if (!TryReadString(node, out var text) || string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"Action {action.Type} arg '{argName}' must resolve to a non-empty scalar string.");
        }

        return text;
    }

    private static bool ResolveBoolArg(RuntimeRuleAction action, string argName, JsonObject state, JsonObject payload)
    {
        var node = ResolveRequiredArgNode(action, argName, state, payload);
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolValue))
            {
                return boolValue;
            }

            if (value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed))
            {
                return parsed;
            }
        }

        throw new InvalidOperationException($"Action {action.Type} arg '{argName}' must resolve to a boolean.");
    }

    private static int ResolveIntArg(RuntimeRuleAction action, string argName, JsonObject state, JsonObject payload)
    {
        var node = ResolveRequiredArgNode(action, argName, state, payload);
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }

            if (value.TryGetValue<long>(out var longValue))
            {
                return checked((int)longValue);
            }

            if (value.TryGetValue<string>(out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        throw new InvalidOperationException($"Action {action.Type} arg '{argName}' must resolve to an integer.");
    }

    private static JsonNode? ReadRequiredArgNode(RuntimeRuleAction action, string argName)
    {
        if (!action.Args.TryGetValue(argName, out var value))
        {
            throw new InvalidOperationException($"Action {action.Type} requires arg '{argName}'.");
        }

        return JsonNode.Parse(value.GetRawText());
    }

    private static string[] ReadStringArgArray(RuntimeRuleAction action, string argName)
    {
        var node = ReadRequiredArgNode(action, argName);
        if (node is not JsonArray array)
        {
            throw new InvalidOperationException($"Action {action.Type} arg '{argName}' must be an array of non-empty strings.");
        }

        var values = new List<string>();
        foreach (var item in array)
        {
            if (item is not JsonValue value ||
                !value.TryGetValue<string>(out var text) ||
                string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException($"Action {action.Type} arg '{argName}' must be an array of non-empty strings.");
            }

            values.Add(text);
        }

        return values.ToArray();
    }

    private static JsonNode? RequirePath(JsonNode? root, string path, string addressSpace, RuntimeRuleAction action, string argName)
    {
        if (TryGetPath(root, path, out var value))
        {
            return CloneNode(value);
        }

        throw new InvalidOperationException(
            $"Action {action.Type} arg '{argName}' references missing {addressSpace} path '{path}'.");
    }

    private static string RequireStringArg(RuntimeRuleAction action, string argName)
    {
        if (!TryGetStringArg(action, argName, out var value))
        {
            throw new InvalidOperationException($"Action {action.Type} requires string arg '{argName}'.");
        }

        return value;
    }

    private static bool TryGetStringArg(RuntimeRuleAction action, string argName, out string value)
    {
        value = string.Empty;
        if (!action.Args.TryGetValue(argName, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }

    private static int ReadOptionalIntArg(RuntimeRuleAction action, string argName, int fallback)
    {
        if (!action.Args.TryGetValue(argName, out var element))
        {
            return fallback;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"Action {action.Type} arg '{argName}' must be an integer.");
    }

    private static bool ReadOptionalBoolArg(RuntimeRuleAction action, string argName, bool fallback)
    {
        if (!action.Args.TryGetValue(argName, out var element))
        {
            return fallback;
        }

        if (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False)
        {
            return element.GetBoolean();
        }

        throw new InvalidOperationException($"Action {action.Type} arg '{argName}' must be a boolean.");
    }

    private static HashSet<string> ReadComparableSet(JsonNode? node)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in AsArrayItems(node))
        {
            if (!TryReadString(item, out var text) || string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("Expected a scalar or array of scalar comparable values.");
            }

            values.Add(text);
        }

        return values;
    }

    private static IEnumerable<JsonNode?> AsArrayItems(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                yield return item;
            }
            yield break;
        }

        if (node is not null)
        {
            yield return node;
        }
    }

    private static bool TryGetPath(JsonNode? root, string path, out JsonNode? value)
    {
        value = root;
        if (value is null)
        {
            return false;
        }

        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (value is JsonObject obj)
            {
                if (!obj.TryGetPropertyValue(part, out value))
                {
                    value = null;
                    return false;
                }
            }
            else if (value is JsonArray array && int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                if (index < 0 || index >= array.Count)
                {
                    value = null;
                    return false;
                }
                value = array[index];
            }
            else
            {
                value = null;
                return false;
            }
        }

        return true;
    }

    private static void SetPath(JsonObject root, string path, JsonNode? value)
    {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new InvalidOperationException("State path cannot be empty.");
        }

        JsonObject current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (current[parts[i]] is not JsonObject next)
            {
                next = new JsonObject();
                current[parts[i]] = next;
            }

            current = next;
        }

        current[parts[^1]] = CloneNode(value);
    }

    private static JsonNode? CloneNode(JsonNode? node)
    {
        return node is null ? null : JsonNode.Parse(node.ToJsonString());
    }

    private static int CompareNumbers(JsonNode? left, JsonNode? right)
    {
        var leftNumber = ReadDouble(left);
        var rightNumber = ReadDouble(right);
        return leftNumber.CompareTo(rightNumber);
    }

    private static double ReadDouble(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<double>(out var number))
        {
            return number;
        }

        return double.NaN;
    }

    private static bool ContainsValue(JsonNode? actual, JsonNode? expected)
    {
        if (actual is JsonArray array)
        {
            return array.Any(item => JsonNode.DeepEquals(item, expected));
        }

        if (actual is JsonValue actualValue && expected is JsonValue expectedValue &&
            actualValue.TryGetValue<string>(out var actualText) &&
            expectedValue.TryGetValue<string>(out var expectedText))
        {
            return actualText.Contains(expectedText, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool MatchesPattern(JsonNode? actual, JsonNode? expected)
    {
        if (actual is not JsonValue actualValue ||
            expected is not JsonValue expectedValue ||
            !actualValue.TryGetValue<string>(out var actualText) ||
            !expectedValue.TryGetValue<string>(out var pattern))
        {
            return false;
        }

        return Regex.IsMatch(actualText, pattern, RegexOptions.CultureInvariant);
    }

    private static void AddStateIssues(IEnumerable<ModStateIssue> stateIssues, List<RuntimeEventExecutionIssue> issues)
    {
        foreach (var issue in stateIssues)
        {
            var message = string.IsNullOrWhiteSpace(issue.Path)
                ? issue.Message
                : $"{issue.Message} path={issue.Path}";
            issues.Add(new RuntimeEventExecutionIssue(issue.Severity, issue.Code, issue.PluginId, string.Empty, string.Empty, message));
        }
    }

    private static void LogAndWriteReport(RuntimeConfig config, LauncherLog log, RuntimeEventExecutionReport report)
    {
        log.Info(
            $"runtime-event event={report.EventId} rules={report.RuleCount} matchedRules={report.MatchedRuleCount} " +
            $"actions={report.ExecutedActionCount} plannedActions={report.PlannedActionCount} " +
            $"materializedActions={report.MaterializedActionCount} " +
            $"stateWrites={report.StateWriteCount} issues={report.Issues.Count}");

        foreach (var rule in report.Rules)
        {
            log.Info(
                $"runtime-event-rule status={rule.Status} plugin={rule.PluginId} source={rule.SourceName} " +
                $"rule={rule.RuleIndex} id={QuoteLogValue(rule.RuleId)} reason={QuoteLogValue(rule.Reason)}");
            foreach (var action in rule.Actions)
            {
                var artifactPath = string.IsNullOrWhiteSpace(action.ArtifactPath)
                    ? string.Empty
                    : $" artifactPath={QuoteLogValue(action.ArtifactPath)}";
                log.Info(
                    $"runtime-event-action status={action.Status} type={action.Type} capability={action.Capability} " +
                    $"risk={action.Risk} required={action.Required} message={QuoteLogValue(action.Message)}{artifactPath}");
            }
        }

        foreach (var issue in report.Issues)
        {
            var line =
                $"runtime-event-issue severity={issue.Severity} code={issue.Code} plugin={issue.PluginId} " +
                $"rule={QuoteLogValue(issue.RuleId)} action={QuoteLogValue(issue.ActionType)} message={QuoteLogValue(issue.Message)}";
            if (issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase))
            {
                log.Error(line);
            }
            else
            {
                log.Warn(line);
            }
        }

        var reportPath = Path.Combine(config.LogDirectory, "runtime_event_report.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        log.Info($"runtime-event-report path={reportPath}");
    }

    private static string QuoteLogValue(string value)
    {
        return string.IsNullOrEmpty(value) ? "\"\"" : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static int PluginPhaseRank(string? phase)
    {
        return (phase ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "base" => 0,
            "early" => 10,
            "normal" => 20,
            "compat" => 30,
            "late" => 40,
            _ => 20
        };
    }
}

internal sealed record RuntimePredicateResult(bool Matched, string Reason);

internal sealed record RuntimeEventExecutionReport(
    int Version,
    string GeneratedAtUtc,
    string EventId,
    string StateDirectory,
    int RuleCount,
    int MatchedRuleCount,
    int ExecutedActionCount,
    int PlannedActionCount,
    int MaterializedActionCount,
    int StateWriteCount,
    JsonObject Payload,
    IReadOnlyList<RuntimeEventRuleExecutionReport> Rules,
    IReadOnlyList<RuntimeEventExecutionIssue> Issues)
{
    public bool Succeeded => Issues.All(issue => !issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
}

internal sealed record RuntimeEventRuleExecutionReport(
    string PluginId,
    string SourceName,
    string SourcePath,
    int LoadOrder,
    int RuleIndex,
    string RuleId,
    string EventId,
    string Status,
    string Reason,
    IReadOnlyList<RuntimeEventActionExecutionReport> Actions);

internal sealed record RuntimeEventActionExecutionReport(
    string Type,
    string Capability,
    string Risk,
    bool Required,
    string Status,
    string Message,
    JsonObject? Plan,
    string? ArtifactPath);

internal sealed record RuntimeEventExecutionIssue(
    string Severity,
    string Code,
    string PluginId,
    string RuleId,
    string ActionType,
    string Message);
