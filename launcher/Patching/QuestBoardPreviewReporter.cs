using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static class QuestBoardPreviewReporter
{
    private const int ReportVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static QuestBoardPreviewReport Write(RuntimeConfig config, LauncherLog log, string? targetProfileId = null)
    {
        var artifactDirectory = Path.Combine(config.ModStateDirectory, "_managed_actions");
        var reportPath = Path.Combine(config.LogDirectory, "quest_board_preview_report.json");
        var context = new PreviewContext(
            config.GameWorkingDirectory,
            config.ModStateDirectory,
            ManagedActionProfileScopeResolver.NormalizeTargetProfileId(targetProfileId));
        var artifactReports = new List<QuestBoardPreviewArtifactReport>();
        IReadOnlyList<QuestBoardPreviewQuestReport> finalActiveQuests = [];
        var supersededArtifacts = ManagedQuestBoardArtifactSupersession.FindSupersededArtifacts(
            artifactDirectory,
            targetProfileId,
            log);

        if (Directory.Exists(artifactDirectory))
        {
            foreach (var artifactPath in Directory.EnumerateFiles(artifactDirectory, "*.json")
                         .OrderBy(path => File.GetLastWriteTimeUtc(path))
                         .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                context.ArtifactCount++;
                var artifactReport = BuildArtifactReport(
                    context,
                    artifactPath,
                    supersededArtifacts.Contains(artifactPath),
                    log);
                artifactReports.Add(artifactReport);
                if (artifactReport.Status == "wouldApply")
                {
                    finalActiveQuests = artifactReport.ActiveQuests;
                }
            }
        }

        var report = new QuestBoardPreviewReport(
            ReportVersion,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            reportPath,
            artifactDirectory,
            context.TargetProfileId,
            context.ArtifactCount,
            artifactReports.Count(report => report.Status is "wouldApply" or "failed"),
            artifactReports.Count(report => report.Status == "wouldApply"),
            finalActiveQuests.Count,
            artifactReports.Sum(report => report.CompletedFilteredQuestCount),
            context.Issues.Count(issue => issue.Severity == "error"),
            artifactReports,
            finalActiveQuests,
            context.Issues);

        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        log.Info(
            $"quest-board-preview report path={Quote(reportPath)} artifacts={report.ArtifactCount} " +
            $"targetProfile={Quote(report.TargetProfileId)} " +
            $"questBoardArtifacts={report.QuestBoardArtifactCount} wouldApply={report.WouldApplyArtifactCount} " +
            $"activeQuests={report.FinalActiveQuestCount} completedFiltered={report.CompletedFilteredQuestCount} " +
            $"issues={report.Issues.Count} errors={report.ErrorCount}");
        return report;
    }

    private static QuestBoardPreviewArtifactReport BuildArtifactReport(
        PreviewContext context,
        string artifactPath,
        bool superseded,
        LauncherLog log)
    {
        try
        {
            var artifact = JsonNode.Parse(File.ReadAllText(artifactPath, Encoding.UTF8)) as JsonObject
                ?? throw new InvalidDataException("artifact root must be a JSON object");
            var actionType = ReadString(artifact, "action.type");
            var status = ReadString(artifact, "status");
            var profileScope = ManagedActionProfileScopeResolver.FromArtifact(artifact);
            if (!actionType.Equals("questBoard.replaceWithFixedSet", StringComparison.OrdinalIgnoreCase))
            {
                return new QuestBoardPreviewArtifactReport(
                    artifactPath,
                    actionType,
                    status,
                    "ignored",
                    string.Empty,
                    string.Empty,
                    profileScope.Kind,
                    profileScope.ProfileId,
                    profileScope.ProfileRoot,
                    false,
                    0,
                    0,
                    0,
                    [],
                    [],
                    ["artifact action is not questBoard.replaceWithFixedSet"]);
            }

            if (superseded)
            {
                return new QuestBoardPreviewArtifactReport(
                    artifactPath,
                    actionType,
                    status,
                    "ignored",
                    ReadOptionalString(artifact, "pluginId"),
                    ReadOptionalString(artifact, "ruleId"),
                    profileScope.Kind,
                    profileScope.ProfileId,
                    profileScope.ProfileRoot,
                    false,
                    0,
                    0,
                    0,
                    [],
                    [],
                    ["artifact was superseded by a newer questBoard.replaceWithFixedSet artifact in the same source group"]);
            }

            if (!status.Equals("materialized", StringComparison.OrdinalIgnoreCase))
            {
                return new QuestBoardPreviewArtifactReport(
                    artifactPath,
                    actionType,
                    status,
                    "ignored",
                    ReadOptionalString(artifact, "pluginId"),
                    ReadOptionalString(artifact, "ruleId"),
                    profileScope.Kind,
                    profileScope.ProfileId,
                    profileScope.ProfileRoot,
                    false,
                    0,
                    0,
                    0,
                    [],
                    [],
                    [$"artifact status is {status}"]);
            }

            if (!profileScope.Matches(context.TargetProfileId))
            {
                return new QuestBoardPreviewArtifactReport(
                    artifactPath,
                    actionType,
                    status,
                    "ignored",
                    ReadOptionalString(artifact, "pluginId"),
                    ReadOptionalString(artifact, "ruleId"),
                    profileScope.Kind,
                    profileScope.ProfileId,
                    profileScope.ProfileRoot,
                    false,
                    0,
                    0,
                    0,
                    [],
                    [],
                    [$"artifact profile scope {profileScope.Kind}:{profileScope.ProfileId} does not match target profile {context.TargetProfileId}"]);
            }

            var questIds = ReadStringArray(ReadNode(artifact, "plan.arguments.questIds"), "plan.arguments.questIds");
            if (questIds.Count == 0)
            {
                throw new InvalidDataException("plan.arguments.questIds must contain at least one quest id.");
            }

            var removeCompleted = ReadOptionalBool(RequireObject(artifact, "plan.arguments"), "removeCompleted") == true;
            var completedQuestIds = removeCompleted
                ? QuestBoardArtifactStateResolver.ResolveCompletedQuestIds(context.ModStateDirectory, artifact)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var distinctQuestIds = questIds
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var activeQuestIds = distinctQuestIds
                .Where(id => !completedQuestIds.Contains(id))
                .ToArray();
            var filteredQuestIds = distinctQuestIds
                .Where(id => completedQuestIds.Contains(id))
                .ToArray();

            var definitions = QuestBoardContentCatalog.LoadEnabledPlotQuestDefinitions(context.GameWorkingDirectory);
            if (definitions.Count == 0)
            {
                throw new InvalidDataException("Plot quest definition catalog produced no quest ids.");
            }

            var stageRows = ReadStageRows(artifact);
            var missingQuestIds = activeQuestIds
                .Where(id => !definitions.ContainsKey(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (missingQuestIds.Length > 0)
            {
                throw new InvalidDataException($"Fixed quest board references unknown plot quest ids: {string.Join(",", missingQuestIds)}");
            }

            var activeQuests = new List<QuestBoardPreviewQuestReport>();
            for (var i = 0; i < activeQuestIds.Length; i++)
            {
                var questId = activeQuestIds[i];
                var stage = FindStageRow(stageRows, questId);
                var entry = QuestBoardContentCatalog.BuildQuestBoardEntry(definitions[questId]);
                activeQuests.Add(BuildQuestReport(artifactPath, i, "active", questId, stage, definitions[questId], entry));
            }

            var filteredQuests = new List<QuestBoardPreviewQuestReport>();
            for (var i = 0; i < filteredQuestIds.Length; i++)
            {
                var questId = filteredQuestIds[i];
                var stage = FindStageRow(stageRows, questId);
                var definition = definitions.TryGetValue(questId, out var value) ? value : null;
                var entry = definition is null ? null : QuestBoardContentCatalog.BuildQuestBoardEntry(definition);
                filteredQuests.Add(BuildQuestReport(artifactPath, i, "completedFiltered", questId, stage, definition, entry));
            }

            return new QuestBoardPreviewArtifactReport(
                artifactPath,
                actionType,
                status,
                "wouldApply",
                ReadOptionalString(artifact, "pluginId"),
                ReadOptionalString(artifact, "ruleId"),
                profileScope.Kind,
                profileScope.ProfileId,
                profileScope.ProfileRoot,
                removeCompleted,
                questIds.Count,
                activeQuests.Count,
                filteredQuests.Count,
                activeQuests,
                filteredQuests,
                []);
        }
        catch (Exception ex)
        {
            context.Issues.Add(new QuestBoardPreviewIssue("error", "quest-board-preview-artifact-failed", artifactPath, ex.Message));
            log.Error($"quest-board-preview issue code=quest-board-preview-artifact-failed path={Quote(artifactPath)} message={Quote(ex.Message)}");
            return new QuestBoardPreviewArtifactReport(
                artifactPath,
                string.Empty,
                string.Empty,
                "failed",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                false,
                0,
                0,
                0,
                [],
                [],
                [ex.Message]);
        }
    }

    private static QuestBoardPreviewQuestReport BuildQuestReport(
        string artifactPath,
        int order,
        string status,
        string questId,
        QuestBoardPreviewStageRow? stage,
        PlotQuestDefinition? definition,
        JsonObject? entry)
    {
        return new QuestBoardPreviewQuestReport(
            artifactPath,
            order,
            status,
            questId,
            stage?.QuestChainId ?? string.Empty,
            stage?.StageId ?? string.Empty,
            stage?.StageName ?? string.Empty,
            stage?.SourceQuestId ?? string.Empty,
            stage?.TargetQuestId ?? string.Empty,
            stage?.Region ?? string.Empty,
            stage?.DeclaredDifficulty,
            definition?.SourcePath ?? string.Empty,
            entry is null ? string.Empty : ReadOptionalString(entry, "type"),
            entry is null ? string.Empty : ReadOptionalString(entry, "dungeon"),
            entry is null ? null : ReadOptionalInt(entry, "difficulty"),
            entry is null ? null : ReadOptionalInt(entry, "length"),
            entry is null ? [] : ReadOptionalStringArray(entry, "goal_ids"));
    }

    private static IReadOnlyList<QuestBoardPreviewStageRow> ReadStageRows(JsonObject artifact)
    {
        var result = new List<QuestBoardPreviewStageRow>();
        if (!TryGetPath(artifact, "plan.arguments.stages", out var node) || node is not JsonArray stages)
        {
            return result;
        }

        var questChainId = ReadOptionalString(RequireObject(artifact, "plan.arguments"), "questChainId");
        foreach (var stageNode in stages)
        {
            if (stageNode is not JsonObject stage)
            {
                continue;
            }

            result.Add(new QuestBoardPreviewStageRow(
                questChainId,
                ReadOptionalString(stage, "id"),
                ReadOptionalString(stage, "name"),
                ReadOptionalString(stage, "sourceQuestId"),
                ReadOptionalString(stage, "targetQuestId"),
                ReadOptionalString(stage, "region"),
                ReadOptionalInt(stage, "difficulty")));
        }

        return result;
    }

    private static QuestBoardPreviewStageRow? FindStageRow(IReadOnlyList<QuestBoardPreviewStageRow> stages, string questId)
    {
        return stages.FirstOrDefault(stage => stage.SourceQuestId.Equals(questId, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> ReadStringArray(JsonNode? node, string path)
    {
        if (node is not JsonArray array)
        {
            throw new InvalidDataException($"{path} must be a string array.");
        }

        var result = new List<string>();
        foreach (var item in array)
        {
            if (item is not JsonValue value ||
                !value.TryGetValue<string>(out var text) ||
                string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidDataException($"{path} must contain only non-empty strings.");
            }

            result.Add(text);
        }

        return result;
    }

    private static IReadOnlyList<string> ReadOptionalStringArray(JsonObject root, string key)
    {
        if (root[key] is not JsonArray array)
        {
            return [];
        }

        return array
            .OfType<JsonValue>()
            .Select(value => value.TryGetValue<string>(out var text) ? text : string.Empty)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
    }

    private static JsonObject RequireObject(JsonObject root, string path)
    {
        return ReadNode(root, path) as JsonObject
            ?? throw new InvalidDataException($"{path} must be a JSON object.");
    }

    private static JsonNode? ReadNode(JsonObject root, string path)
    {
        if (TryGetPath(root, path, out var node) && node is not null)
        {
            return node;
        }

        throw new InvalidDataException($"{path} is missing.");
    }

    private static string ReadString(JsonObject root, string path)
    {
        return ReadNode(root, path)?.GetValue<string>()
            ?? throw new InvalidDataException($"{path} must be a string.");
    }

    private static string ReadOptionalString(JsonObject root, string key)
    {
        return root[key]?.GetValue<string>() ?? string.Empty;
    }

    private static int? ReadOptionalInt(JsonObject root, string key)
    {
        return root[key] is JsonValue value && value.TryGetValue<int>(out var result)
            ? result
            : null;
    }

    private static bool? ReadOptionalBool(JsonObject root, string key)
    {
        return root[key] is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result
            : null;
    }

    private static bool TryGetPath(JsonObject root, string path, out JsonNode? value)
    {
        value = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (value is not JsonObject obj || !obj.TryGetPropertyValue(part, out value))
            {
                value = null;
                return false;
            }
        }

        return true;
    }

    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';

    private sealed class PreviewContext(string gameWorkingDirectory, string modStateDirectory, string targetProfileId)
    {
        public string GameWorkingDirectory { get; } = gameWorkingDirectory;
        public string ModStateDirectory { get; } = modStateDirectory;
        public string TargetProfileId { get; } = targetProfileId;
        public int ArtifactCount { get; set; }
        public List<QuestBoardPreviewIssue> Issues { get; } = [];
    }
}

internal sealed record QuestBoardPreviewReport(
    int Version,
    string GeneratedAtUtc,
    string ReportPath,
    string ArtifactDirectory,
    string TargetProfileId,
    int ArtifactCount,
    int QuestBoardArtifactCount,
    int WouldApplyArtifactCount,
    int FinalActiveQuestCount,
    int CompletedFilteredQuestCount,
    int ErrorCount,
    IReadOnlyList<QuestBoardPreviewArtifactReport> Artifacts,
    IReadOnlyList<QuestBoardPreviewQuestReport> FinalActiveQuests,
    IReadOnlyList<QuestBoardPreviewIssue> Issues)
{
    public bool Succeeded => ErrorCount == 0;
}

internal sealed record QuestBoardPreviewArtifactReport(
    string ArtifactPath,
    string ActionType,
    string ArtifactStatus,
    string Status,
    string PluginId,
    string QuestChainId,
    string ProfileScopeKind,
    string ProfileScopeProfileId,
    string ProfileScopeProfileRoot,
    bool RemoveCompleted,
    int SourceQuestCount,
    int ActiveQuestCount,
    int CompletedFilteredQuestCount,
    IReadOnlyList<QuestBoardPreviewQuestReport> ActiveQuests,
    IReadOnlyList<QuestBoardPreviewQuestReport> CompletedFilteredQuests,
    IReadOnlyList<string> Issues);

internal sealed record QuestBoardPreviewQuestReport(
    string ArtifactPath,
    int Order,
    string Status,
    string QuestId,
    string QuestChainId,
    string StageId,
    string StageName,
    string SourceQuestId,
    string TargetQuestId,
    string Region,
    int? DeclaredDifficulty,
    string ContentSourcePath,
    string Type,
    string Dungeon,
    int? ContentDifficulty,
    int? Length,
    IReadOnlyList<string> GoalIds);

internal sealed record QuestBoardPreviewStageRow(
    string QuestChainId,
    string StageId,
    string StageName,
    string SourceQuestId,
    string TargetQuestId,
    string Region,
    int? DeclaredDifficulty);

internal sealed record QuestBoardPreviewIssue(
    string Severity,
    string Code,
    string ArtifactPath,
    string Message);
