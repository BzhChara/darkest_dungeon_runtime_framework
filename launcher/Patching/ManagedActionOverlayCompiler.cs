using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static class ManagedActionOverlayCompiler
{
    private const int ReportVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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
            ["overlays"] = overlays,
            ["issues"] = issues
        };

        File.WriteAllText(reportPath, report.ToJsonString(JsonOptions), Encoding.UTF8);
        log.Info(
            $"managed-action-overlay manifest path={Quote(reportPath)} artifacts={artifactCount} " +
            $"overlays={overlays.Count} ignored={ignoredArtifactCount} superseded={supersededOverlayCount} issues={issues.Count}");

        return new ManagedActionOverlayReport(reportPath, artifactCount, overlays.Count, issues.Count);
    }

    public static void ApplyEnvironment(Dictionary<string, string> values, ManagedActionOverlayReport report)
    {
        values["DD_RUNTIME_MANAGED_OVERLAY_MANIFEST"] = report.ManifestPath;
        values["DD_RUNTIME_MANAGED_OVERLAY_COUNT"] = report.OverlayCount.ToString(CultureInfo.InvariantCulture);
        values["DD_RUNTIME_MANAGED_OVERLAY_ISSUE_COUNT"] = report.IssueCount.ToString(CultureInfo.InvariantCulture);
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

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}

internal sealed record ManagedActionOverlayReport(
    string ManifestPath,
    int ArtifactCount,
    int OverlayCount,
    int IssueCount);
