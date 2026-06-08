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
            foreach (var action in sourceRule.Rule.Actions)
            {
                var actionReport = ExecuteAction(
                    sourceRule,
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
        RuntimeRuleAction action,
        JsonObject payload,
        Dictionary<string, ModStateDocument> stateDocuments,
        HashSet<string> changedStatePaths,
        RuntimeConfig config,
        PatchPlan patchPlan,
        List<RuntimeEventExecutionIssue> issues)
    {
        var type = action.Type.Trim();
        if (!IsSupportedSafeAction(type))
        {
            var severity = action.Required ? "error" : "warning";
            var message = $"action type is not implemented by the safe event executor: {type}";
            issues.Add(new RuntimeEventExecutionIssue(severity, "unsupported-action", sourceRule.PluginId, sourceRule.Rule.Id, type, message));
            return new RuntimeEventActionExecutionReport(type, action.Capability, action.Risk, action.Required, action.Required ? "failed" : "skipped", message);
        }

        var document = GetStateDocument(sourceRule, stateDocuments, config, patchPlan, issues);
        if (document is null)
        {
            var message = "plugin sidecar state is unavailable";
            issues.Add(new RuntimeEventExecutionIssue("error", "state-unavailable", sourceRule.PluginId, sourceRule.Rule.Id, type, message));
            return new RuntimeEventActionExecutionReport(type, action.Capability, action.Risk, action.Required, "failed", message);
        }

        try
        {
            var changed = type switch
            {
                "state.addUniqueRange" => ExecuteAddUniqueRange(action, document, payload),
                "state.incrementCounter" => ExecuteIncrementCounter(action, document),
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

            return new RuntimeEventActionExecutionReport(type, action.Capability, action.Risk, action.Required, "executed", changed ? "state changed" : "no state change");
        }
        catch (Exception ex)
        {
            issues.Add(new RuntimeEventExecutionIssue("error", "action-failed", sourceRule.PluginId, sourceRule.Rule.Id, type, ex.Message));
            return new RuntimeEventActionExecutionReport(type, action.Capability, action.Risk, action.Required, "failed", ex.Message);
        }
    }

    private static bool ExecuteAddUniqueRange(RuntimeRuleAction action, ModStateDocument document, JsonObject payload)
    {
        var key = RequireStringArg(action, "key");
        JsonNode? sourceValue = null;
        if (TryGetStringArg(action, "fromEvent", out var fromEvent))
        {
            sourceValue = RequirePath(payload, fromEvent, "event", action, "fromEvent");
        }
        else if (TryGetStringArg(action, "fromState", out var fromState))
        {
            sourceValue = RequirePath(document.State, fromState, "state", action, "fromState");
        }
        else
        {
            sourceValue = ReadRequiredArgNode(action, "values");
        }

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

        var attempt = new JsonObject
        {
            ["stageId"] = CloneNode(stageId),
            ["result"] = result,
            ["selection"] = CloneNode(selection),
            ["recordedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        GetOrCreateArray(document.State, stateKey).Add(attempt);
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
            "state.addUniqueRange" or
            "state.incrementCounter" or
            "challenge.lockStageSelection" or
            "challenge.recordFailedAttempt" or
            "challenge.advanceStage" or
            "challenge.initializeRunState";
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

    private static JsonNode? ReadRequiredArgNode(RuntimeRuleAction action, string argName)
    {
        if (!action.Args.TryGetValue(argName, out var value))
        {
            throw new InvalidOperationException($"Action {action.Type} requires arg '{argName}'.");
        }

        return JsonNode.Parse(value.GetRawText());
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
            $"actions={report.ExecutedActionCount} stateWrites={report.StateWriteCount} issues={report.Issues.Count}");

        foreach (var rule in report.Rules)
        {
            log.Info(
                $"runtime-event-rule status={rule.Status} plugin={rule.PluginId} source={rule.SourceName} " +
                $"rule={rule.RuleIndex} id={QuoteLogValue(rule.RuleId)} reason={QuoteLogValue(rule.Reason)}");
            foreach (var action in rule.Actions)
            {
                log.Info(
                    $"runtime-event-action status={action.Status} type={action.Type} capability={action.Capability} " +
                    $"risk={action.Risk} required={action.Required} message={QuoteLogValue(action.Message)}");
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
    string Message);

internal sealed record RuntimeEventExecutionIssue(
    string Severity,
    string Code,
    string PluginId,
    string RuleId,
    string ActionType,
    string Message);
