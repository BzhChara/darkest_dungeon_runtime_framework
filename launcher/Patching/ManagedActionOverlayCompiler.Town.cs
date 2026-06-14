using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static partial class ManagedActionOverlayCompiler
{
    private const string TownBuildingFilePattern = "*.building.json";

    private static JsonObject BuildTownUnlockAllBuildingsOverlay(string artifactPath, JsonObject artifact)
    {
        var mode = ReadString(artifact, "plan.arguments.mode");
        if (!mode.Equals("all_unlocked", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("all_unlocked_and_maxed", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("districts_built", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported town building unlock mode: {mode}");
        }

        return new JsonObject
        {
            ["kind"] = "town.unlockAllBuildings",
            ["effect"] = "suppressBuildingRequirements",
            ["target"] = "content.town.buildingRequirements",
            ["artifactPath"] = artifactPath,
            ["eventId"] = ReadString(artifact, "eventId"),
            ["pluginId"] = ReadString(artifact, "pluginId"),
            ["sourceName"] = ReadString(artifact, "sourceName"),
            ["sourcePath"] = ReadString(artifact, "sourcePath"),
            ["ruleIndex"] = ReadInt(artifact, "ruleIndex"),
            ["ruleId"] = ReadString(artifact, "ruleId"),
            ["actionIndex"] = ReadInt(artifact, "actionIndex"),
            ["mode"] = mode,
            ["buildingIds"] = CloneNode(ReadOptionalNode(artifact, "plan.arguments.buildingIds"))
        };
    }

    private static IReadOnlyList<OverlayVirtualRule> BuildTownUnlockAllBuildingsVirtualRules(
        RuntimeConfig config,
        IReadOnlyList<JsonObject> overlays,
        JsonArray issues,
        LauncherLog log)
    {
        var enabledOverlays = overlays
            .Where(overlay =>
                ReadString(overlay, "kind").Equals("town.unlockAllBuildings", StringComparison.OrdinalIgnoreCase) &&
                !ReadString(overlay, "mode").Equals("districts_built", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (enabledOverlays.Length == 0)
        {
            return [];
        }

        var requestedBuildingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var overlay in enabledOverlays)
        {
            if (overlay["buildingIds"] is not JsonArray buildingIds)
            {
                continue;
            }

            foreach (var id in ReadStringArray(buildingIds, "buildingIds"))
            {
                requestedBuildingIds.Add(id);
            }
        }

        var outputDirectory = Path.Combine(config.ModStateDirectory, "_managed_action_overlays", "town_unlock_all_buildings");
        Directory.CreateDirectory(outputDirectory);

        var rules = new List<OverlayVirtualRule>();
        foreach (var sourcePath in EnumerateTownBuildingFiles(config.GameWorkingDirectory))
        {
            try
            {
                var target = ToVirtualTarget(config.GameWorkingDirectory, sourcePath);
                var buildingId = ResolveTownBuildingId(sourcePath);
                if (requestedBuildingIds.Count > 0 && !requestedBuildingIds.Contains(buildingId))
                {
                    continue;
                }

                var outputPath = Path.Combine(outputDirectory, SafeFileName(target));
                var result = WriteTownBuildingUnlockOverlay(sourcePath, outputPath);
                if (result.AffectedRequirementCount == 0)
                {
                    continue;
                }

                var summary = new JsonObject
                {
                    ["target"] = target,
                    ["effect"] = "suppressTownBuildingRequirements",
                    ["sourcePath"] = outputPath,
                    ["sourceContentPath"] = sourcePath,
                    ["buildingId"] = buildingId,
                    ["policyOverlayCount"] = enabledOverlays.Length,
                    ["affectedRequirementCount"] = result.AffectedRequirementCount,
                    ["sourceArtifacts"] = new JsonArray(enabledOverlays
                        .Select(overlay => (JsonNode?)new JsonObject
                        {
                            ["pluginId"] = ReadString(overlay, "pluginId"),
                            ["ruleId"] = ReadString(overlay, "ruleId"),
                            ["artifactPath"] = ReadString(overlay, "artifactPath")
                        })
                        .ToArray())
                };

                rules.Add(new OverlayVirtualRule(
                    new VirtualFileRule
                    {
                        Target = target,
                        SourcePath = outputPath
                    },
                    summary));

                log.Info(
                    $"managed-action-overlay virtual-rule town-building target={Quote(target)} " +
                    $"sourcePath={Quote(outputPath)} building={Quote(buildingId)} affectedRequirements={result.AffectedRequirementCount}");
            }
            catch (Exception ex)
            {
                AddIssue(
                    issues,
                    log,
                    "warning",
                    "managed-overlay-town-building-unlock-overlay-failed",
                    string.Join(';', enabledOverlays.Select(overlay => ReadString(overlay, "artifactPath"))),
                    $"{sourcePath}: {ex.Message}");
            }
        }

        if (rules.Count == 0)
        {
            AddIssue(
                issues,
                log,
                "warning",
                "managed-overlay-town-building-files-unmodified",
                string.Join(';', enabledOverlays.Select(overlay => ReadString(overlay, "artifactPath"))),
                "town.unlockAllBuildings requested building requirement suppression, but no building files were modified");
        }

        return rules;
    }

    private static TownBuildingUnlockOverlayResult WriteTownBuildingUnlockOverlay(string sourcePath, string outputPath)
    {
        var root = JsonNode.Parse(
                File.ReadAllText(sourcePath, Encoding.UTF8),
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                }) as JsonObject
            ?? throw new InvalidDataException("town building file root must be an object");

        if (root["requirements"] is not JsonObject requirements)
        {
            return new TownBuildingUnlockOverlayResult(0);
        }

        var affected = 0;
        affected += SetRequirementToZero(requirements, "number_of_quests_finished");
        affected += SetRequirementToZero(requirements, "highest_dungeon_level");
        if (affected > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            File.WriteAllText(outputPath, root.ToJsonString(JsonOptions), Utf8NoBom);
        }

        return new TownBuildingUnlockOverlayResult(affected);
    }

    private static int SetRequirementToZero(JsonObject requirements, string key)
    {
        if (requirements[key] is not JsonValue value ||
            !value.TryGetValue<int>(out var current) ||
            current == 0)
        {
            return 0;
        }

        requirements[key] = 0;
        return 1;
    }

    private static IEnumerable<string> EnumerateTownBuildingFiles(string gameWorkingDirectory)
    {
        var baseBuildingDirectory = Path.Combine(gameWorkingDirectory, "campaign", "town", "buildings");
        if (Directory.Exists(baseBuildingDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(baseBuildingDirectory, TownBuildingFilePattern, SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }

        var dlcDirectory = Path.Combine(gameWorkingDirectory, "dlc");
        if (!Directory.Exists(dlcDirectory))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(dlcDirectory)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(name) ||
                !char.IsDigit(name[0]) ||
                name.Contains("arena", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var buildingDirectory = Path.Combine(directory, "campaign", "town", "buildings");
            if (!Directory.Exists(buildingDirectory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(buildingDirectory, TownBuildingFilePattern, SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private static string ResolveTownBuildingId(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath);
        return string.IsNullOrWhiteSpace(directory)
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : Path.GetFileName(directory);
    }

    private static JsonNode? ReadOptionalNode(JsonObject root, string path)
    {
        JsonNode? current = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            current = current is JsonObject obj ? obj[part] : null;
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonArray array, string path)
    {
        return array
            .Select((item, index) =>
            {
                if (item is JsonValue value &&
                    value.TryGetValue<string>(out var text) &&
                    !string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }

                throw new InvalidDataException($"{path}[{index}] must be a non-empty string.");
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private sealed record TownBuildingUnlockOverlayResult(int AffectedRequirementCount);
}
