using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static partial class ManagedActionOverlayCompiler
{
    private const int ReportVersion = 1;
    private const string QuestPlotFileTarget = "campaign/quest/quest.plot_quests.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static ManagedActionOverlayReport Compile(RuntimeConfig config, LauncherLog log)
    {
        var artifactDirectory = Path.Combine(config.ModStateDirectory, "_managed_actions");
        var overlayCandidates = new List<JsonObject>();
        var issues = new JsonArray();
        var artifactCount = 0;
        var ignoredArtifactCount = 0;

        if (Directory.Exists(artifactDirectory))
        {
            foreach (var artifactPath in Directory.EnumerateFiles(artifactDirectory, "*.json")
                         .OrderBy(path => File.GetLastWriteTimeUtc(path))
                         .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                artifactCount++;
                try
                {
                    var artifact = JsonNode.Parse(File.ReadAllText(artifactPath, Encoding.UTF8)) as JsonObject
                        ?? throw new InvalidOperationException("artifact root must be a JSON object");
                    var actionType = ReadString(artifact, "action.type");
                    var status = ReadString(artifact, "status");

                    if (!status.Equals("materialized", StringComparison.OrdinalIgnoreCase))
                    {
                        ignoredArtifactCount++;
                        continue;
                    }

                    if (actionType.Equals("quest.injectFixedStage", StringComparison.OrdinalIgnoreCase))
                    {
                        overlayCandidates.Add(BuildQuestInjectFixedStageOverlay(artifactPath, artifact));
                    }
                    else if (actionType.Equals("inventory.disableItemSale", StringComparison.OrdinalIgnoreCase))
                    {
                        var overlay = BuildInventoryDisableItemSaleOverlay(artifactPath, artifact);
                        if (ReadBool(overlay, "disabled"))
                        {
                            overlayCandidates.Add(overlay);
                        }
                        else
                        {
                            ignoredArtifactCount++;
                        }
                    }
                    else
                    {
                        ignoredArtifactCount++;
                    }
                }
                catch (Exception ex)
                {
                    issues.Add(new JsonObject
                    {
                        ["severity"] = "warning",
                        ["code"] = "managed-artifact-read-failed",
                        ["artifactPath"] = artifactPath,
                        ["message"] = ex.Message
                    });
                    log.Warn($"managed-action-overlay issue code=managed-artifact-read-failed path={Quote(artifactPath)} message={Quote(ex.Message)}");
                }
            }
        }

        var selectedOverlays = overlayCandidates
            .GroupBy(BuildOverlaySupersedeKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();
        var supersededOverlayCount = overlayCandidates.Count - selectedOverlays.Length;
        var overlays = new JsonArray();
        foreach (var overlay in selectedOverlays)
        {
            overlays.Add(overlay);
        }

        var virtualRules = BuildOverlayVirtualRules(config, selectedOverlays, issues, log);
        var virtualRuleSummaries = new JsonArray();
        foreach (var virtualRule in virtualRules)
        {
            virtualRuleSummaries.Add(virtualRule.Summary);
        }

        if (virtualRules.Count > 0)
        {
            var previewOutput = Path.Combine(config.LogDirectory, "managed_action_overlay_preview");
            PatchPreviewer.WritePreview(
                config,
                virtualRules.Select(rule => rule.Rule).ToArray(),
                previewOutput,
                log);
        }

        var reportPath = Path.Combine(config.LogDirectory, "managed_action_overlay_manifest.json");
        var report = new JsonObject
        {
            ["version"] = ReportVersion,
            ["generatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["artifactDirectory"] = artifactDirectory,
            ["artifactCount"] = artifactCount,
            ["overlayCount"] = overlays.Count,
            ["ignoredArtifactCount"] = ignoredArtifactCount,
            ["supersededOverlayCount"] = supersededOverlayCount,
            ["virtualFileRuleCount"] = virtualRules.Count,
            ["virtualFileReplacementCount"] = virtualRules.Sum(rule => rule.Rule.Replacements.Length),
            ["overlays"] = overlays,
            ["virtualFileRules"] = virtualRuleSummaries,
            ["issues"] = issues
        };

        File.WriteAllText(reportPath, report.ToJsonString(JsonOptions), Utf8NoBom);
        log.Info(
            $"managed-action-overlay manifest path={Quote(reportPath)} artifacts={artifactCount} " +
            $"overlays={overlays.Count} virtualRules={virtualRules.Count} " +
            $"ignored={ignoredArtifactCount} superseded={supersededOverlayCount} issues={issues.Count}");

        return new ManagedActionOverlayReport(
            reportPath,
            artifactCount,
            overlays.Count,
            issues.Count,
            virtualRules.Select(rule => rule.Rule).ToArray());
    }

    public static void ApplyEnvironment(Dictionary<string, string> values, ManagedActionOverlayReport report)
    {
        values["DD_RUNTIME_MANAGED_OVERLAY_MANIFEST"] = report.ManifestPath;
        values["DD_RUNTIME_MANAGED_OVERLAY_COUNT"] = report.OverlayCount.ToString(CultureInfo.InvariantCulture);
        values["DD_RUNTIME_MANAGED_OVERLAY_ISSUE_COUNT"] = report.IssueCount.ToString(CultureInfo.InvariantCulture);
        AppendVirtualRules(values, report.VirtualFileRules);
    }

    private static JsonObject BuildQuestInjectFixedStageOverlay(string artifactPath, JsonObject artifact)
    {
        var plan = RequireObject(artifact, "plan");
        var stage = RequireObject(plan, "stage");

        return new JsonObject
        {
            ["kind"] = "quest.injectFixedStage",
            ["effect"] = ReadString(plan, "effect"),
            ["target"] = ReadString(plan, "target"),
            ["artifactPath"] = artifactPath,
            ["eventId"] = ReadString(artifact, "eventId"),
            ["pluginId"] = ReadString(artifact, "pluginId"),
            ["sourceName"] = ReadString(artifact, "sourceName"),
            ["sourcePath"] = ReadString(artifact, "sourcePath"),
            ["ruleIndex"] = ReadInt(artifact, "ruleIndex"),
            ["ruleId"] = ReadString(artifact, "ruleId"),
            ["actionIndex"] = ReadInt(artifact, "actionIndex"),
            ["stageId"] = ReadString(stage, "id"),
            ["stageName"] = ReadString(stage, "name"),
            ["sourceQuestId"] = ReadString(stage, "sourceQuestId"),
            ["stage"] = CloneNode(stage)
        };
    }

    private static string BuildOverlaySupersedeKey(JsonObject overlay)
    {
        return string.Join('|',
            ReadString(overlay, "kind"),
            ReadString(overlay, "target"),
            ReadString(overlay, "pluginId"),
            ReadString(overlay, "sourcePath"),
            ReadString(overlay, "ruleId"),
            ReadInt(overlay, "actionIndex").ToString(CultureInfo.InvariantCulture));
    }

    private static IReadOnlyList<OverlayVirtualRule> BuildOverlayVirtualRules(
        RuntimeConfig config,
        IReadOnlyList<JsonObject> overlays,
        JsonArray issues,
        LauncherLog log)
    {
        var virtualRules = new List<OverlayVirtualRule>();
        var questOverlays = overlays
            .Where(overlay =>
                ReadString(overlay, "kind").Equals("quest.injectFixedStage", StringComparison.OrdinalIgnoreCase) &&
                ReadString(overlay, "target").Equals("quest.currentStage", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (questOverlays.Length > 0)
        {
            var questPlotPath = Path.Combine(config.GameWorkingDirectory, QuestPlotFileTarget.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(questPlotPath))
            {
                AddIssue(
                    issues,
                    log,
                    "warning",
                    "managed-overlay-quest-file-missing",
                    string.Empty,
                    $"Quest plot file was not found: {questPlotPath}");
            }
            else
            {
                string questPlotText;
                try
                {
                    questPlotText = File.ReadAllText(questPlotPath, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    AddIssue(
                        issues,
                        log,
                        "warning",
                        "managed-overlay-quest-file-read-failed",
                        string.Empty,
                        $"Quest plot file could not be read: {ex.Message}");
                    questPlotText = string.Empty;
                }

                if (!string.IsNullOrEmpty(questPlotText))
                {
                    var replacements = new List<VirtualFileReplacement>();
                    var replacementSummaries = new JsonArray();
                    var currentQuestPlotText = questPlotText;
                    foreach (var overlay in questOverlays)
                    {
                        try
                        {
                            var replacement = BuildQuestPlotReplacement(currentQuestPlotText, overlay, replacements.Count);
                            replacements.Add(replacement.Replacement);
                            replacementSummaries.Add(replacement.Summary);
                            currentQuestPlotText = ReplaceAllText(
                                currentQuestPlotText,
                                replacement.Replacement.Find,
                                replacement.Replacement.Replace);
                            log.Info(
                                $"managed-action-overlay virtual-rule sourceQuest={Quote(replacement.SourceQuestId)} " +
                                $"stage={Quote(replacement.StageId)} target={Quote(QuestPlotFileTarget)} " +
                                $"findChars={replacement.Replacement.Find.Length} replaceChars={replacement.Replacement.Replace.Length}");
                        }
                        catch (Exception ex)
                        {
                            AddIssue(
                                issues,
                                log,
                                "warning",
                                "managed-overlay-quest-replacement-failed",
                                ReadString(overlay, "artifactPath"),
                                ex.Message);
                        }
                    }

                    if (replacements.Count > 0)
                    {
                        var summary = new JsonObject
                        {
                            ["target"] = QuestPlotFileTarget,
                            ["effect"] = "forcePlotQuestAvailable",
                            ["replacementCount"] = replacements.Count,
                            ["replacements"] = replacementSummaries
                        };

                        var rule = new VirtualFileRule
                        {
                            Target = QuestPlotFileTarget,
                            Replacements = replacements.ToArray()
                        };

                        virtualRules.Add(new OverlayVirtualRule(rule, summary));
                    }
                }
            }
        }

        virtualRules.AddRange(BuildInventoryDisableItemSaleVirtualRules(config, overlays, issues, log));
        return virtualRules;
    }

    private static QuestPlotReplacement BuildQuestPlotReplacement(string questPlotText, JsonObject overlay, int replacementIndex)
    {
        var sourceQuestId = RequireString(overlay, "sourceQuestId");
        var stageId = RequireString(overlay, "stageId");
        var rawQuest = FindPlotQuestRawText(questPlotText, sourceQuestId)
            ?? throw new InvalidOperationException($"Source quest '{sourceQuestId}' was not found in {QuestPlotFileTarget}.");
        var questObject = JsonNode.Parse(rawQuest) as JsonObject
            ?? throw new InvalidOperationException($"Source quest '{sourceQuestId}' was not a JSON object.");

        questObject["dungeon_level"] = 0;
        questObject["is_repeatable"] = true;

        var replacement = new VirtualFileReplacement
        {
            Find = rawQuest,
            Replace = questObject.ToJsonString(JsonOptions),
            Origin = new PatchReplacementOrigin(
                ReadString(overlay, "sourceName"),
                ReadString(overlay, "sourcePath"),
                ReadInt(overlay, "ruleIndex"),
                replacementIndex,
                ReadInt(overlay, "actionIndex"),
                "managedOverlay",
                $"quest.injectFixedStage:{sourceQuestId}->{stageId}")
        };

        var summary = new JsonObject
        {
            ["kind"] = ReadString(overlay, "kind"),
            ["sourceQuestId"] = sourceQuestId,
            ["stageId"] = stageId,
            ["stageName"] = ReadString(overlay, "stageName"),
            ["artifactPath"] = ReadString(overlay, "artifactPath"),
            ["effect"] = "forcePlotQuestAvailable",
            ["setDungeonLevel"] = 0,
            ["setRepeatable"] = true,
            ["findChars"] = replacement.Find.Length,
            ["replaceChars"] = replacement.Replace.Length
        };

        return new QuestPlotReplacement(sourceQuestId, stageId, replacement, summary);
    }

    private static string? FindPlotQuestRawText(string questPlotText, string sourceQuestId)
    {
        using var document = JsonDocument.Parse(questPlotText, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        if (!document.RootElement.TryGetProperty("plot_quests", out var quests) ||
            quests.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"{QuestPlotFileTarget} does not contain a plot_quests array.");
        }

        foreach (var quest in quests.EnumerateArray())
        {
            if (quest.ValueKind == JsonValueKind.Object &&
                quest.TryGetProperty("id", out var id) &&
                sourceQuestId.Equals(id.GetString(), StringComparison.OrdinalIgnoreCase))
            {
                return quest.GetRawText();
            }
        }

        return null;
    }

    private static string ReplaceAllText(string text, string find, string replace)
    {
        if (find.Length == 0)
        {
            return text;
        }

        var position = 0;
        while ((position = text.IndexOf(find, position, StringComparison.Ordinal)) >= 0)
        {
            text = text.Remove(position, find.Length).Insert(position, replace);
            position += replace.Length;
        }

        return text;
    }

    private static void AppendVirtualRules(Dictionary<string, string> values, IReadOnlyList<VirtualFileRule> virtualRules)
    {
        if (virtualRules.Count == 0)
        {
            return;
        }

        if (!values.TryGetValue("DD_RUNTIME_VIRTUAL_RULE_COUNT", out var countText) ||
            !int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset) ||
            offset < 0)
        {
            offset = 0;
        }

        for (var i = 0; i < virtualRules.Count; i++)
        {
            var ruleIndex = offset + i;
            var rule = virtualRules[i];
            values[$"DD_RUNTIME_VIRTUAL_RULE_{ruleIndex}_TARGET"] = rule.Target;
            values[$"DD_RUNTIME_VIRTUAL_RULE_{ruleIndex}_SOURCE_PATH"] = rule.SourcePath;
            values[$"DD_RUNTIME_VIRTUAL_RULE_{ruleIndex}_REPLACEMENT_COUNT"] =
                rule.Replacements.Length.ToString(CultureInfo.InvariantCulture);

            for (var replacementIndex = 0; replacementIndex < rule.Replacements.Length; replacementIndex++)
            {
                var replacement = rule.Replacements[replacementIndex];
                values[$"DD_RUNTIME_VIRTUAL_RULE_{ruleIndex}_REPLACEMENT_{replacementIndex}_FIND"] = replacement.Find;
                values[$"DD_RUNTIME_VIRTUAL_RULE_{ruleIndex}_REPLACEMENT_{replacementIndex}_REPLACE"] = replacement.Replace;
            }
        }

        values["DD_RUNTIME_VIRTUAL_RULE_COUNT"] = (offset + virtualRules.Count).ToString(CultureInfo.InvariantCulture);
    }

    private static JsonObject RequireObject(JsonObject root, string path)
    {
        if (TryGetPath(root, path, out var node) && node is JsonObject obj)
        {
            return obj;
        }

        throw new InvalidOperationException($"Expected object at '{path}'.");
    }

    private static string ReadString(JsonObject root, string path)
    {
        if (!TryGetPath(root, path, out var node) ||
            node is not JsonValue value ||
            !value.TryGetValue<string>(out var text))
        {
            return string.Empty;
        }

        return text;
    }

    private static string RequireString(JsonObject root, string path)
    {
        var text = ReadString(root, path);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"Expected non-empty string at '{path}'.");
        }

        return text;
    }

    private static int ReadInt(JsonObject root, string path)
    {
        if (!TryGetPath(root, path, out var node) ||
            node is not JsonValue value ||
            !value.TryGetValue<int>(out var number))
        {
            return 0;
        }

        return number;
    }

    private static bool ReadBool(JsonObject root, string path)
    {
        if (!TryGetPath(root, path, out var node) ||
            node is not JsonValue value ||
            !value.TryGetValue<bool>(out var result))
        {
            return false;
        }

        return result;
    }

    private static bool TryGetPath(JsonObject root, string path, out JsonNode? node)
    {
        node = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (node is JsonObject obj && obj.TryGetPropertyValue(part, out var next))
            {
                node = next;
                continue;
            }

            node = null;
            return false;
        }

        return true;
    }

    private static JsonNode? CloneNode(JsonNode? value)
    {
        return value is null ? null : JsonNode.Parse(value.ToJsonString());
    }

    private static void AddIssue(
        JsonArray issues,
        LauncherLog log,
        string severity,
        string code,
        string artifactPath,
        string message)
    {
        issues.Add(new JsonObject
        {
            ["severity"] = severity,
            ["code"] = code,
            ["artifactPath"] = artifactPath,
            ["message"] = message
        });

        log.Warn($"managed-action-overlay issue code={code} path={Quote(artifactPath)} message={Quote(message)}");
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}

internal sealed record ManagedActionOverlayReport(
    string ManifestPath,
    int ArtifactCount,
    int OverlayCount,
    int IssueCount,
    IReadOnlyList<VirtualFileRule> VirtualFileRules);

internal sealed record OverlayVirtualRule(
    VirtualFileRule Rule,
    JsonObject Summary);

internal sealed record QuestPlotReplacement(
    string SourceQuestId,
    string StageId,
    VirtualFileReplacement Replacement,
    JsonObject Summary);
