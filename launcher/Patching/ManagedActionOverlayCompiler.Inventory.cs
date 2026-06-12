using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static partial class ManagedActionOverlayCompiler
{
    private const string TrinketEntryFilePattern = "*.entries.trinkets.json";
    private const string InventoryDisableItemSaleOverlayTarget = "content.trinkets.price";

    private static JsonObject BuildInventoryDisableItemSaleOverlay(string artifactPath, JsonObject artifact)
    {
        var itemKind = ReadString(artifact, "plan.arguments.itemKind");
        if (string.IsNullOrWhiteSpace(itemKind))
        {
            throw new InvalidOperationException("plan.arguments.itemKind is required.");
        }

        return new JsonObject
        {
            ["kind"] = "inventory.disableItemSale",
            ["effect"] = "suppressSaleValue",
            ["target"] = itemKind.Equals("trinket", StringComparison.OrdinalIgnoreCase)
                ? InventoryDisableItemSaleOverlayTarget
                : $"content.{itemKind}.price",
            ["artifactPath"] = artifactPath,
            ["eventId"] = ReadString(artifact, "eventId"),
            ["pluginId"] = ReadString(artifact, "pluginId"),
            ["sourceName"] = ReadString(artifact, "sourceName"),
            ["sourcePath"] = ReadString(artifact, "sourcePath"),
            ["ruleIndex"] = ReadInt(artifact, "ruleIndex"),
            ["ruleId"] = ReadString(artifact, "ruleId"),
            ["actionIndex"] = ReadInt(artifact, "actionIndex"),
            ["itemKind"] = itemKind,
            ["disabled"] = RequireBool(artifact, "plan.arguments.disabled")
        };
    }

    private static IReadOnlyList<OverlayVirtualRule> BuildInventoryDisableItemSaleVirtualRules(
        RuntimeConfig config,
        IReadOnlyList<JsonObject> overlays,
        JsonArray issues,
        LauncherLog log)
    {
        var enabledOverlays = overlays
            .Where(overlay =>
                ReadString(overlay, "kind").Equals("inventory.disableItemSale", StringComparison.OrdinalIgnoreCase) &&
                ReadBool(overlay, "disabled"))
            .ToArray();

        if (enabledOverlays.Length == 0)
        {
            return [];
        }

        var trinketOverlays = enabledOverlays
            .Where(overlay => ReadString(overlay, "itemKind").Equals("trinket", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var overlay in enabledOverlays.Except(trinketOverlays))
        {
            AddIssue(
                issues,
                log,
                "warning",
                "managed-overlay-inventory-item-kind-unsupported",
                ReadString(overlay, "artifactPath"),
                $"inventory.disableItemSale content overlay does not support itemKind={ReadString(overlay, "itemKind")} yet");
        }

        if (trinketOverlays.Length == 0)
        {
            return [];
        }

        var outputDirectory = Path.Combine(config.ModStateDirectory, "_managed_action_overlays", "inventory_disable_item_sale");
        Directory.CreateDirectory(outputDirectory);

        var rules = new List<OverlayVirtualRule>();
        foreach (var sourcePath in EnumerateCampaignTrinketEntryFiles(config.GameWorkingDirectory))
        {
            try
            {
                var target = ToVirtualTarget(config.GameWorkingDirectory, sourcePath);
                var outputPath = Path.Combine(outputDirectory, SafeFileName(target));
                var result = WriteTrinketSaleValueSuppressionOverlay(sourcePath, outputPath);
                if (result.AffectedEntryCount == 0)
                {
                    continue;
                }

                var summary = new JsonObject
                {
                    ["target"] = target,
                    ["effect"] = "suppressTrinketSaleValue",
                    ["sourcePath"] = outputPath,
                    ["sourceContentPath"] = sourcePath,
                    ["policyOverlayCount"] = trinketOverlays.Length,
                    ["entryCount"] = result.EntryCount,
                    ["affectedEntryCount"] = result.AffectedEntryCount,
                    ["sourceArtifacts"] = new JsonArray(trinketOverlays
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
                    $"managed-action-overlay virtual-rule itemKind=trinket target={Quote(target)} " +
                    $"sourcePath={Quote(outputPath)} affectedEntries={result.AffectedEntryCount}");
            }
            catch (Exception ex)
            {
                AddIssue(
                    issues,
                    log,
                    "warning",
                    "managed-overlay-trinket-sale-value-overlay-failed",
                    string.Join(';', trinketOverlays.Select(overlay => ReadString(overlay, "artifactPath"))),
                    $"{sourcePath}: {ex.Message}");
            }
        }

        if (rules.Count == 0)
        {
            AddIssue(
                issues,
                log,
                "warning",
                "managed-overlay-trinket-entry-files-unmodified",
                string.Join(';', trinketOverlays.Select(overlay => ReadString(overlay, "artifactPath"))),
                "inventory.disableItemSale requested trinket sale suppression, but no trinket entry prices were modified");
        }

        return rules;
    }

    private static TrinketSaleValueSuppressionResult WriteTrinketSaleValueSuppressionOverlay(string sourcePath, string outputPath)
    {
        var root = JsonNode.Parse(
                File.ReadAllText(sourcePath, Encoding.UTF8),
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                }) as JsonObject
            ?? throw new InvalidDataException("trinket entry file root must be an object");

        if (root["entries"] is not JsonArray entries)
        {
            throw new InvalidDataException("trinket entry file must contain an entries array");
        }

        var entryCount = 0;
        var affectedEntryCount = 0;
        foreach (var entry in entries.OfType<JsonObject>())
        {
            entryCount++;
            if (entry["price"] is JsonValue price &&
                price.TryGetValue<int>(out var currentPrice) &&
                currentPrice != 0)
            {
                entry["price"] = 0;
                affectedEntryCount++;
            }
        }

        if (affectedEntryCount > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            File.WriteAllText(outputPath, root.ToJsonString(JsonOptions), Encoding.UTF8);
        }

        return new TrinketSaleValueSuppressionResult(entryCount, affectedEntryCount);
    }

    private static bool RequireBool(JsonObject root, string path)
    {
        if (!TryGetPath(root, path, out var node) ||
            node is not JsonValue value ||
            !value.TryGetValue<bool>(out var result))
        {
            throw new InvalidOperationException($"Expected boolean at '{path}'.");
        }

        return result;
    }

    private static IEnumerable<string> EnumerateCampaignTrinketEntryFiles(string gameWorkingDirectory)
    {
        var baseTrinketDirectory = Path.Combine(gameWorkingDirectory, "trinkets");
        if (Directory.Exists(baseTrinketDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(baseTrinketDirectory, TrinketEntryFilePattern, SearchOption.TopDirectoryOnly)
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

            foreach (var path in Directory.EnumerateFiles(directory, TrinketEntryFilePattern, SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private static string ToVirtualTarget(string gameWorkingDirectory, string path)
    {
        var relativePath = Path.GetRelativePath(gameWorkingDirectory, path);
        return relativePath.Replace('\\', '/');
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Replace('\\', '_').Replace('/', '_'))
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.Length == 0 ? "overlay.json" : builder.ToString();
    }

    private sealed record TrinketSaleValueSuppressionResult(int EntryCount, int AffectedEntryCount);
}
