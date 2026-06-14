namespace DDRuntimeLoader;

internal static class QuestChainValidator
{
    private static readonly string[] SupportedQuestBoardModes =
    [
        "replaceWithFixedSet",
        "linearProgression"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static QuestChainValidationReport WriteValidationReport(
        QuestChainRule chain,
        int ruleIndex,
        IReadOnlyList<MapLayoutTemplateRule> mapLayoutTemplates,
        IReadOnlyList<MapTemplateRule> mapTemplates,
        string reportPath)
    {
        var issues = new List<QuestChainValidationIssue>();
        var mapLayoutsById = BuildMapLayoutIndex(mapLayoutTemplates, issues);
        var mapTemplatesById = BuildMapTemplateIndex(mapTemplates, issues);

        ValidateChainHeader(chain, issues);
        ValidateUnlock(chain.Unlock, issues);

        var orderedStages = BuildOrderedStageFacts(chain.Stages ?? [], mapLayoutsById, mapTemplatesById, issues);
        var questBoard = BuildQuestBoardFacts(chain.QuestBoard, orderedStages, issues);
        var report = new QuestChainValidationReport(
            "questChain",
            ruleIndex,
            chain.Id,
            chain.Name,
            chain.Mode,
            BuildUnlockFacts(chain.Unlock),
            questBoard,
            orderedStages.Count,
            orderedStages,
            !issues.Any(issue => issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)),
            issues);

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        return report;
    }

    private static Dictionary<string, MapLayoutTemplateRule> BuildMapLayoutIndex(
        IReadOnlyList<MapLayoutTemplateRule> templates,
        List<QuestChainValidationIssue> issues)
    {
        var result = new Dictionary<string, MapLayoutTemplateRule>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < templates.Count; i++)
        {
            var id = templates[i].Id.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (!result.TryAdd(id, templates[i]))
            {
                AddError(issues, "duplicate-map-layout-id", $"mapLayoutTemplates[{i}].id", $"duplicate mapLayoutTemplates id: {id}");
            }
        }

        return result;
    }

    private static Dictionary<string, MapTemplateRule> BuildMapTemplateIndex(
        IReadOnlyList<MapTemplateRule> templates,
        List<QuestChainValidationIssue> issues)
    {
        var result = new Dictionary<string, MapTemplateRule>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < templates.Count; i++)
        {
            var id = templates[i].Id.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (!result.TryAdd(id, templates[i]))
            {
                AddError(issues, "duplicate-map-template-id", $"mapTemplates[{i}].id", $"duplicate mapTemplates id: {id}");
            }
        }

        return result;
    }

    private static void ValidateChainHeader(QuestChainRule chain, List<QuestChainValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(chain.Id))
        {
            AddError(issues, "missing-chain-id", "id", "questChains entry requires id");
        }

        if (chain.Stages.Length == 0)
        {
            AddError(issues, "missing-stages", "stages", "questChains entry requires at least one stage");
        }
    }

    private static void ValidateUnlock(QuestChainUnlockRule unlock, List<QuestChainValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(unlock.Type))
        {
            return;
        }

        if (unlock.Type.Equals("afterQuest", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(unlock.QuestId))
        {
            AddError(issues, "missing-unlock-quest", "unlock.questId", "unlock type afterQuest requires questId");
        }

        if (string.IsNullOrWhiteSpace(unlock.StateKey) && !string.IsNullOrWhiteSpace(unlock.StateEquals))
        {
            AddError(issues, "missing-unlock-state-key", "unlock.stateKey", "unlock.stateEquals requires unlock.stateKey");
        }
    }

    private static QuestChainUnlockFacts BuildUnlockFacts(QuestChainUnlockRule unlock)
    {
        return new QuestChainUnlockFacts(
            unlock.Type,
            unlock.QuestId,
            unlock.Phase,
            unlock.StateKey,
            unlock.StateEquals);
    }

    private static QuestChainBoardFacts BuildQuestBoardFacts(
        QuestChainBoardRule board,
        IReadOnlyList<QuestChainStageFacts> orderedStages,
        List<QuestChainValidationIssue> issues)
    {
        var mode = string.IsNullOrWhiteSpace(board.Mode) ? "replaceWithFixedSet" : board.Mode.Trim();
        var questIdSource = string.IsNullOrWhiteSpace(board.QuestIdSource) ? "sourceQuestId" : board.QuestIdSource.Trim();
        var questIds = Array.Empty<string>();

        if (board.Enabled)
        {
            if (!SupportedQuestBoardModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
            {
                AddError(issues, "unsupported-quest-board-mode", "questBoard.mode", $"unsupported questBoard mode: {mode}");
            }

            if (!questIdSource.Equals("sourceQuestId", StringComparison.OrdinalIgnoreCase))
            {
                AddError(issues, "unsupported-quest-id-source", "questBoard.questIdSource", "questBoard currently supports only sourceQuestId");
            }

            if (mode.Equals("replaceWithFixedSet", StringComparison.OrdinalIgnoreCase) &&
                board.RemoveCompleted &&
                string.IsNullOrWhiteSpace(board.CompletedStateKey))
            {
                AddError(issues, "missing-completed-state-key", "questBoard.completedStateKey", "completedStateKey is required when removeCompleted is true");
            }

            questIds = orderedStages
                .Select(stage => stage.SourceQuestId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToArray();

            var duplicateQuestIds = questIds
                .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var duplicateQuestId in duplicateQuestIds)
            {
                AddError(issues, "duplicate-quest-board-id", "questBoard.questIds", $"questBoard would contain duplicate quest id: {duplicateQuestId}");
            }
        }

        return new QuestChainBoardFacts(
            board.Enabled,
            mode,
            questIdSource,
            board.RemoveCompleted,
            board.CompletedStateKey,
            board.RefreshTriggers ?? [],
            string.IsNullOrWhiteSpace(board.OnCompleted) ? "remove" : board.OnCompleted,
            questIds);
    }

    private static IReadOnlyList<QuestChainStageFacts> BuildOrderedStageFacts(
        IReadOnlyList<QuestChainStageRule> stages,
        IReadOnlyDictionary<string, MapLayoutTemplateRule> mapLayoutsById,
        IReadOnlyDictionary<string, MapTemplateRule> mapTemplatesById,
        List<QuestChainValidationIssue> issues)
    {
        var seenStageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenOrders = new Dictionary<int, int>();
        var facts = new List<QuestChainStageFacts>();

        for (var i = 0; i < stages.Count; i++)
        {
            var stage = stages[i];
            var order = stage.Order ?? i;
            if (stage.Order.HasValue && stage.Order.Value < 0)
            {
                AddError(issues, "negative-stage-order", $"stages[{i}].order", "stage order must be zero or greater");
            }

            if (seenOrders.TryGetValue(order, out var previousIndex))
            {
                AddError(issues, "duplicate-stage-order", $"stages[{i}].order", $"stage order {order} duplicates stages[{previousIndex}]");
            }
            else
            {
                seenOrders[order] = i;
            }

            if (string.IsNullOrWhiteSpace(stage.Id))
            {
                AddError(issues, "missing-stage-id", $"stages[{i}].id", "quest chain stage requires id");
            }
            else if (!seenStageIds.Add(stage.Id))
            {
                AddError(issues, "duplicate-stage-id", $"stages[{i}].id", $"duplicate quest chain stage id: {stage.Id}");
            }

            if (string.IsNullOrWhiteSpace(stage.SourceQuestId))
            {
                AddError(issues, "missing-source-quest", $"stages[{i}].sourceQuestId", "quest chain stage currently requires sourceQuestId");
            }

            var hasMapLayout = !string.IsNullOrWhiteSpace(stage.MapLayoutTemplateId);
            var hasMapTemplate = !string.IsNullOrWhiteSpace(stage.MapTemplateId);
            if (hasMapLayout && hasMapTemplate)
            {
                AddError(issues, "ambiguous-map-reference", $"stages[{i}]", "stage may reference mapLayoutTemplateId or mapTemplateId, not both");
            }

            QuestChainMapReferenceFacts? mapReference = null;
            if (hasMapLayout)
            {
                mapReference = ResolveMapLayoutReference(stage.MapLayoutTemplateId, mapLayoutsById, $"stages[{i}].mapLayoutTemplateId", issues);
            }
            else if (hasMapTemplate)
            {
                mapReference = ResolveMapTemplateReference(stage.MapTemplateId, mapTemplatesById, $"stages[{i}].mapTemplateId", issues);
            }

            facts.Add(new QuestChainStageFacts(
                i,
                order,
                stage.Id,
                stage.Name,
                stage.SourceQuestId,
                stage.TargetQuestId,
                stage.Region,
                stage.Difficulty,
                stage.Tags,
                mapReference));
        }

        return facts
            .OrderBy(stage => stage.Order)
            .ThenBy(stage => stage.Index)
            .ToArray();
    }

    private static QuestChainMapReferenceFacts? ResolveMapLayoutReference(
        string id,
        IReadOnlyDictionary<string, MapLayoutTemplateRule> mapLayoutsById,
        string path,
        List<QuestChainValidationIssue> issues)
    {
        if (!mapLayoutsById.TryGetValue(id, out var template))
        {
            AddError(issues, "missing-map-layout-template", path, $"mapLayoutTemplateId was not found: {id}");
            return null;
        }

        return new QuestChainMapReferenceFacts(
            "mapLayoutTemplate",
            id,
            template.Target,
            template.Source,
            template.Layout.Rooms.Length,
            template.Layout.Corridors.Length,
            template.Layout.Links.Length,
            template.Tiles.Length,
            template.Encounters.Length);
    }

    private static QuestChainMapReferenceFacts? ResolveMapTemplateReference(
        string id,
        IReadOnlyDictionary<string, MapTemplateRule> mapTemplatesById,
        string path,
        List<QuestChainValidationIssue> issues)
    {
        if (!mapTemplatesById.TryGetValue(id, out var template))
        {
            AddError(issues, "missing-map-template", path, $"mapTemplateId was not found: {id}");
            return null;
        }

        return new QuestChainMapReferenceFacts(
            "mapTemplate",
            id,
            template.Target,
            template.Source,
            0,
            0,
            0,
            0,
            0);
    }

    private static void AddError(List<QuestChainValidationIssue> issues, string code, string path, string message)
    {
        issues.Add(new QuestChainValidationIssue("error", code, path, message));
    }
}

internal sealed record QuestChainValidationReport(
    string Type,
    int RuleIndex,
    string Id,
    string Name,
    string Mode,
    QuestChainUnlockFacts Unlock,
    QuestChainBoardFacts QuestBoard,
    int StageCount,
    IReadOnlyList<QuestChainStageFacts> OrderedStages,
    bool Succeeded,
    IReadOnlyList<QuestChainValidationIssue> Issues);

internal sealed record QuestChainUnlockFacts(
    string Type,
    string QuestId,
    string Phase,
    string StateKey,
    string StateEquals);

internal sealed record QuestChainBoardFacts(
    bool Enabled,
    string Mode,
    string QuestIdSource,
    bool RemoveCompleted,
    string CompletedStateKey,
    IReadOnlyList<string> RefreshTriggers,
    string OnCompleted,
    IReadOnlyList<string> QuestIds);

internal sealed record QuestChainStageFacts(
    int Index,
    int Order,
    string Id,
    string Name,
    string SourceQuestId,
    string TargetQuestId,
    string Region,
    int? Difficulty,
    IReadOnlyList<string> Tags,
    QuestChainMapReferenceFacts? MapReference);

internal sealed record QuestChainMapReferenceFacts(
    string Type,
    string Id,
    string Target,
    string Source,
    int RoomCount,
    int CorridorCount,
    int LinkCount,
    int TileRuleCount,
    int EncounterCount);

internal sealed record QuestChainValidationIssue(
    string Severity,
    string Code,
    string Path,
    string Message);
