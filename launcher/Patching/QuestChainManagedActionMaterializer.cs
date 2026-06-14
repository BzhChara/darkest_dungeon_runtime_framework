using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace DDRuntimeLoader;

internal static class QuestChainManagedActionMaterializer
{
    private const int ReportVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static QuestChainManagedActionReport WriteQuestBoardArtifact(
        PluginManifestCandidate plugin,
        int ruleIndex,
        QuestChainValidationReport validationReport,
        string reportPath,
        string artifactPath)
    {
        var issues = new List<QuestChainManagedActionIssue>();
        var status = "skipped";

        if (!validationReport.QuestBoard.Enabled)
        {
            issues.Add(new QuestChainManagedActionIssue(
                "info",
                "quest-board-disabled",
                "questBoard.enabled",
                "quest chain did not request a quest board managed artifact"));
        }
        else if (!validationReport.QuestBoard.Mode.Equals("replaceWithFixedSet", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new QuestChainManagedActionIssue(
                "info",
                "quest-board-mode-not-static",
                "questBoard.mode",
                $"quest board mode {validationReport.QuestBoard.Mode} is handled outside static questBoard.replaceWithFixedSet materialization"));
        }
        else if (!validationReport.Succeeded)
        {
            issues.Add(new QuestChainManagedActionIssue(
                "error",
                "quest-chain-validation-failed",
                "questChains",
                "quest board managed artifact was not written because quest chain validation failed"));
        }
        else
        {
            status = "materialized";
        }

        var writtenArtifactPath = "";
        if (status.Equals("materialized", StringComparison.OrdinalIgnoreCase))
        {
            var now = DateTimeOffset.UtcNow;
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath) ?? ".");
            File.WriteAllText(
                artifactPath,
                BuildQuestBoardArtifact(plugin, ruleIndex, validationReport, now, status, issues).ToJsonString(JsonOptions),
                Encoding.UTF8);
            writtenArtifactPath = artifactPath;
        }

        var report = new QuestChainManagedActionReport(
            ReportVersion,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            validationReport.Id,
            validationReport.Name,
            validationReport.StageCount,
            validationReport.QuestBoard.Enabled,
            status,
            writtenArtifactPath,
            validationReport.QuestBoard.QuestIds,
            issues);

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        return report;
    }

    private static JsonObject BuildQuestBoardArtifact(
        PluginManifestCandidate plugin,
        int ruleIndex,
        QuestChainValidationReport validationReport,
        DateTimeOffset generatedAt,
        string status,
        IReadOnlyList<QuestChainManagedActionIssue> issues)
    {
        var questIds = new JsonArray();
        foreach (var questId in validationReport.QuestBoard.QuestIds)
        {
            questIds.Add(questId);
        }

        var stageRows = new JsonArray();
        foreach (var stage in validationReport.OrderedStages)
        {
            stageRows.Add(BuildStageRow(stage));
        }

        var arguments = new JsonObject
        {
            ["target"] = "profile.quest_board",
            ["questIds"] = questIds,
            ["questChainId"] = validationReport.Id,
            ["questIdSource"] = validationReport.QuestBoard.QuestIdSource,
            ["removeCompleted"] = validationReport.QuestBoard.RemoveCompleted,
            ["stages"] = stageRows
        };

        if (!string.IsNullOrWhiteSpace(validationReport.QuestBoard.CompletedStateKey))
        {
            arguments["completedStateKey"] = validationReport.QuestBoard.CompletedStateKey;
        }

        return new JsonObject
        {
            ["version"] = 1,
            ["generatedAtUtc"] = generatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["status"] = status,
            ["eventId"] = "quest.chain.materialized",
            ["pluginId"] = plugin.Id,
            ["sourceName"] = plugin.SourceName,
            ["sourcePath"] = plugin.Path,
            ["loadOrder"] = plugin.LoadOrder,
            ["ruleIndex"] = ruleIndex,
            ["ruleId"] = validationReport.Id,
            ["actionIndex"] = 0,
            ["action"] = new JsonObject
            {
                ["type"] = "questBoard.replaceWithFixedSet",
                ["capability"] = "quest_board.replace_with_fixed_set",
                ["risk"] = "managed",
                ["required"] = false
            },
            ["payload"] = new JsonObject
            {
                ["source"] = "questChains",
                ["questChainId"] = validationReport.Id,
                ["stageCount"] = validationReport.StageCount
            },
            ["issues"] = BuildIssueRows(issues),
            ["plan"] = new JsonObject
            {
                ["kind"] = "questBoard.replaceWithFixedSet",
                ["effect"] = "replaceWithFixedSet",
                ["target"] = "profile.quest_board",
                ["source"] = "questChains",
                ["arguments"] = arguments
            }
        };
    }

    private static JsonArray BuildIssueRows(IReadOnlyList<QuestChainManagedActionIssue> issues)
    {
        var rows = new JsonArray();
        foreach (var issue in issues)
        {
            rows.Add(new JsonObject
            {
                ["severity"] = issue.Severity,
                ["code"] = issue.Code,
                ["path"] = issue.Path,
                ["message"] = issue.Message
            });
        }

        return rows;
    }

    private static JsonObject BuildStageRow(QuestChainStageFacts stage)
    {
        var row = new JsonObject
        {
            ["id"] = stage.Id,
            ["name"] = stage.Name,
            ["order"] = stage.Order,
            ["sourceQuestId"] = stage.SourceQuestId,
            ["targetQuestId"] = stage.TargetQuestId,
            ["region"] = stage.Region
        };

        if (stage.Difficulty.HasValue)
        {
            row["difficulty"] = stage.Difficulty.Value;
        }

        var tags = new JsonArray();
        foreach (var tag in stage.Tags)
        {
            tags.Add(tag);
        }

        row["tags"] = tags;
        if (stage.MapReference is not null)
        {
            row["mapReference"] = new JsonObject
            {
                ["type"] = stage.MapReference.Type,
                ["id"] = stage.MapReference.Id,
                ["target"] = stage.MapReference.Target,
                ["source"] = stage.MapReference.Source,
                ["roomCount"] = stage.MapReference.RoomCount,
                ["corridorCount"] = stage.MapReference.CorridorCount,
                ["linkCount"] = stage.MapReference.LinkCount,
                ["tileRuleCount"] = stage.MapReference.TileRuleCount,
                ["encounterCount"] = stage.MapReference.EncounterCount
            };
        }

        return row;
    }
}

internal sealed record QuestChainManagedActionReport(
    int Version,
    string GeneratedAtUtc,
    string QuestChainId,
    string Name,
    int StageCount,
    bool QuestBoardEnabled,
    string Status,
    string ArtifactPath,
    IReadOnlyList<string> QuestIds,
    IReadOnlyList<QuestChainManagedActionIssue> Issues);

internal sealed record QuestChainManagedActionIssue(
    string Severity,
    string Code,
    string Path,
    string Message);
