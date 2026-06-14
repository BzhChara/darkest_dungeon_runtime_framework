using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static partial class ManagedActionOverlayCompiler
{
    private const string TrinketEntryFilePattern = "*.entries.trinkets.json";
    private const string InventorySalePolicyOverlayTarget = "profile.inventory.saleDisabled";
    private const string InventoryTrinketSaleValueOverlayTarget = "content.trinkets.price";
    private const string TrinketShardStoreOverlayTarget = "content.trinkets.shardStore";

    private static JsonObject BuildInventoryDisableItemSaleOverlay(string artifactPath, JsonObject artifact)
    {
        var itemKind = ReadString(artifact, "plan.arguments.itemKind");
        if (string.IsNullOrWhiteSpace(itemKind))
        {
            throw new InvalidOperationException("plan.arguments.itemKind is required.");
        }

        var method = ReadString(artifact, "plan.arguments.method");
        if (string.IsNullOrWhiteSpace(method))
        {
            method = "policy";
        }

        var suppressSaleValue = itemKind.Equals("trinket", StringComparison.OrdinalIgnoreCase) &&
            IsContentPriceZeroMethod(method);

        return new JsonObject
        {
            ["kind"] = "inventory.disableItemSale",
            ["effect"] = suppressSaleValue ? "suppressSaleValue" : "recordSalePolicy",
            ["target"] = suppressSaleValue
                ? InventoryTrinketSaleValueOverlayTarget
                : InventorySalePolicyOverlayTarget,
            ["artifactPath"] = artifactPath,
            ["eventId"] = ReadString(artifact, "eventId"),
            ["pluginId"] = ReadString(artifact, "pluginId"),
            ["sourceName"] = ReadString(artifact, "sourceName"),
            ["sourcePath"] = ReadString(artifact, "sourcePath"),
            ["ruleIndex"] = ReadInt(artifact, "ruleIndex"),
            ["ruleId"] = ReadString(artifact, "ruleId"),
            ["actionIndex"] = ReadInt(artifact, "actionIndex"),
            ["itemKind"] = itemKind,
            ["method"] = method,
            ["disabled"] = RequireBool(artifact, "plan.arguments.disabled")
        };
    }

    private static JsonObject BuildTrinketProjectShardStoreOverlay(string artifactPath, JsonObject artifact)
    {
        var plan = RequireObject(artifact, "plan");
        var arguments = RequireObject(plan, "arguments");
        var enabled = ReadOptionalBool(arguments, "enabled", true);
        var items = BuildShardStoreProjectionItems(arguments);

        return new JsonObject
        {
            ["kind"] = "trinket.projectShardStore",
            ["effect"] = "projectShardStore",
            ["target"] = TrinketShardStoreOverlayTarget,
            ["artifactPath"] = artifactPath,
            ["eventId"] = ReadString(artifact, "eventId"),
            ["pluginId"] = ReadString(artifact, "pluginId"),
            ["sourceName"] = ReadString(artifact, "sourceName"),
            ["sourcePath"] = ReadString(artifact, "sourcePath"),
            ["ruleIndex"] = ReadInt(artifact, "ruleIndex"),
            ["ruleId"] = ReadString(artifact, "ruleId"),
            ["actionIndex"] = ReadInt(artifact, "actionIndex"),
            ["enabled"] = enabled,
            ["items"] = items
        };
    }

    private static IReadOnlyList<OverlayVirtualRule> BuildTrinketEntryVirtualRules(
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

        foreach (var overlay in enabledOverlays.Where(overlay =>
                     ReadString(overlay, "effect").Equals("recordSalePolicy", StringComparison.OrdinalIgnoreCase)))
        {
            log.Info(
                $"managed-action-overlay policy-only itemKind={Quote(ReadString(overlay, "itemKind"))} " +
                $"target={Quote(ReadString(overlay, "target"))} artifact={Quote(ReadString(overlay, "artifactPath"))}");
        }

        var trinketSaleValueOverlays = enabledOverlays
            .Where(overlay =>
                ReadString(overlay, "effect").Equals("suppressSaleValue", StringComparison.OrdinalIgnoreCase) &&
                ReadString(overlay, "itemKind").Equals("trinket", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var shardStoreOverlays = overlays
            .Where(overlay =>
                ReadString(overlay, "kind").Equals("trinket.projectShardStore", StringComparison.OrdinalIgnoreCase) &&
                ReadBool(overlay, "enabled"))
            .ToArray();
        var shardStoreItems = BuildShardStoreProjectionItemMap(shardStoreOverlays);

        if (trinketSaleValueOverlays.Length == 0 &&
            shardStoreItems.Count == 0)
        {
            return [];
        }

        var outputDirectory = Path.Combine(config.ModStateDirectory, "_managed_action_overlays", "trinket_entry_projection");
        Directory.CreateDirectory(outputDirectory);

        var rules = new List<OverlayVirtualRule>();
        var resolvedShardStoreItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourcePath in EnumerateCampaignTrinketEntryFiles(config.GameWorkingDirectory))
        {
            try
            {
                var target = ToVirtualTarget(config.GameWorkingDirectory, sourcePath);
                var outputPath = Path.Combine(outputDirectory, SafeFileName(target));
                var result = WriteTrinketEntryProjectionOverlay(
                    sourcePath,
                    outputPath,
                    trinketSaleValueOverlays.Length > 0,
                    shardStoreItems,
                    resolvedShardStoreItemIds);
                if (result.SaleValueAffectedEntryCount == 0 &&
                    result.ShardStoreAffectedEntryCount == 0)
                {
                    continue;
                }

                var summary = new JsonObject
                {
                    ["target"] = target,
                    ["effect"] = result.SaleValueAffectedEntryCount > 0
                        ? "suppressTrinketSaleValue"
                        : "projectTrinketShardStore",
                    ["sourcePath"] = outputPath,
                    ["sourceContentPath"] = sourcePath,
                    ["policyOverlayCount"] = trinketSaleValueOverlays.Length,
                    ["shardStoreOverlayCount"] = shardStoreOverlays.Length,
                    ["entryCount"] = result.EntryCount,
                    ["affectedEntryCount"] = result.SaleValueAffectedEntryCount,
                    ["saleValueAffectedEntryCount"] = result.SaleValueAffectedEntryCount,
                    ["shardStoreAffectedEntryCount"] = result.ShardStoreAffectedEntryCount,
                    ["shardStoreItemIds"] = new JsonArray(result.AffectedShardStoreItemIds
                        .Select(id => (JsonNode?)id)
                        .ToArray()),
                    ["sourceArtifacts"] = BuildTrinketEntryProjectionSourceArtifacts(
                        trinketSaleValueOverlays,
                        shardStoreOverlays)
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
                    $"sourcePath={Quote(outputPath)} saleValueAffectedEntries={result.SaleValueAffectedEntryCount} " +
                    $"shardStoreAffectedEntries={result.ShardStoreAffectedEntryCount}");
            }
            catch (Exception ex)
            {
                AddIssue(
                    issues,
                    log,
                    "error",
                    "managed-overlay-trinket-entry-projection-failed",
                    string.Join(';', trinketSaleValueOverlays.Concat(shardStoreOverlays).Select(overlay => ReadString(overlay, "artifactPath"))),
                    $"{sourcePath}: {ex.Message}");
            }
        }

        foreach (var missingId in shardStoreItems.Keys
                     .Where(id => !resolvedShardStoreItemIds.Contains(id))
                     .OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            AddIssue(
                issues,
                log,
                "warning",
                "managed-overlay-trinket-shard-store-item-not-found",
                shardStoreItems[missingId].ArtifactPath,
                $"trinket.projectShardStore referenced trinket id '{missingId}', but no enabled trinket entry file defined it");
        }

        if (trinketSaleValueOverlays.Length > 0 &&
            rules.All(rule => ReadInt(rule.Summary, "saleValueAffectedEntryCount") == 0))
        {
            AddIssue(
                issues,
                log,
                "warning",
                "managed-overlay-trinket-entry-files-unmodified",
                string.Join(';', trinketSaleValueOverlays.Select(overlay => ReadString(overlay, "artifactPath"))),
                "inventory.disableItemSale requested trinket sale-value suppression, but no trinket entry prices were modified");
        }

        return rules;
    }

    private static TrinketEntryProjectionResult WriteTrinketEntryProjectionOverlay(
        string sourcePath,
        string outputPath,
        bool suppressSaleValue,
        IReadOnlyDictionary<string, TrinketShardStoreProjectionItem> shardStoreItems,
        HashSet<string> resolvedShardStoreItemIds)
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
        var saleValueAffectedEntryCount = 0;
        var shardStoreAffectedEntryCount = 0;
        var affectedShardStoreItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.OfType<JsonObject>())
        {
            entryCount++;
            var id = ReadString(entry, "id");
            if (suppressSaleValue &&
                entry["price"] is JsonValue price &&
                price.TryGetValue<int>(out var currentPrice) &&
                currentPrice != 0)
            {
                entry["price"] = 0;
                saleValueAffectedEntryCount++;
            }

            if (!string.IsNullOrWhiteSpace(id) &&
                shardStoreItems.TryGetValue(id, out var shardStoreItem))
            {
                entry["rarity"] = shardStoreItem.Rarity;
                entry["shard"] = shardStoreItem.Shard;
                entry["limit"] = shardStoreItem.Limit;
                entry["origin_dungeon"] = shardStoreItem.OriginDungeon;

                if (shardStoreItem.RemovePrice)
                {
                    entry.Remove("price");
                }

                shardStoreAffectedEntryCount++;
                affectedShardStoreItemIds.Add(id);
                resolvedShardStoreItemIds.Add(id);
            }
        }

        if (saleValueAffectedEntryCount > 0 ||
            shardStoreAffectedEntryCount > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            File.WriteAllText(outputPath, root.ToJsonString(JsonOptions), Utf8NoBom);
        }

        return new TrinketEntryProjectionResult(
            entryCount,
            saleValueAffectedEntryCount,
            shardStoreAffectedEntryCount,
            affectedShardStoreItemIds
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static JsonArray BuildShardStoreProjectionItems(JsonObject arguments)
    {
        var result = new JsonArray();
        if (arguments["items"] is JsonArray items)
        {
            foreach (var node in items)
            {
                if (node is not JsonObject item)
                {
                    throw new InvalidOperationException("plan.arguments.items entries must be objects.");
                }

                result.Add(BuildShardStoreProjectionItem(item));
            }
        }
        else
        {
            result.Add(BuildShardStoreProjectionItem(arguments));
        }

        if (result.Count == 0)
        {
            throw new InvalidOperationException("trinket.projectShardStore requires at least one item.");
        }

        return result;
    }

    private static JsonObject BuildShardStoreProjectionItem(JsonObject item)
    {
        var id = ReadFirstString(item, "id", "trinketId", "itemId");
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("trinket.projectShardStore item requires id.");
        }

        var shard = RequireNonNegativeInt(item, "shard");
        var limit = ReadOptionalNonNegativeInt(item, "limit", 1);
        var rarity = ReadString(item, "rarity");
        if (string.IsNullOrWhiteSpace(rarity))
        {
            rarity = "comet";
        }

        var originDungeon = ReadString(item, "originDungeon");
        if (string.IsNullOrWhiteSpace(originDungeon))
        {
            originDungeon = ReadString(item, "origin_dungeon");
        }

        return new JsonObject
        {
            ["id"] = id,
            ["shard"] = shard,
            ["limit"] = limit,
            ["rarity"] = rarity,
            ["originDungeon"] = originDungeon,
            ["removePrice"] = ReadOptionalBool(item, "removePrice", true)
        };
    }

    private static Dictionary<string, TrinketShardStoreProjectionItem> BuildShardStoreProjectionItemMap(
        IReadOnlyList<JsonObject> overlays)
    {
        var result = new Dictionary<string, TrinketShardStoreProjectionItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var overlay in overlays)
        {
            if (overlay["items"] is not JsonArray items)
            {
                continue;
            }

            foreach (var node in items)
            {
                if (node is not JsonObject item)
                {
                    continue;
                }

                var id = RequireString(item, "id");
                result[id] = new TrinketShardStoreProjectionItem(
                    id,
                    ReadInt(item, "shard"),
                    ReadInt(item, "limit"),
                    RequireString(item, "rarity"),
                    ReadString(item, "originDungeon"),
                    ReadOptionalBool(item, "removePrice", true),
                    ReadString(overlay, "artifactPath"));
            }
        }

        return result;
    }

    private static JsonArray BuildTrinketEntryProjectionSourceArtifacts(
        IReadOnlyList<JsonObject> saleValueOverlays,
        IReadOnlyList<JsonObject> shardStoreOverlays)
    {
        var result = new JsonArray();
        foreach (var overlay in saleValueOverlays.Concat(shardStoreOverlays))
        {
            result.Add(new JsonObject
            {
                ["kind"] = ReadString(overlay, "kind"),
                ["pluginId"] = ReadString(overlay, "pluginId"),
                ["ruleId"] = ReadString(overlay, "ruleId"),
                ["artifactPath"] = ReadString(overlay, "artifactPath")
            });
        }

        return result;
    }

    private static bool IsContentPriceZeroMethod(string method)
    {
        return method.Equals("content_price_zero", StringComparison.OrdinalIgnoreCase) ||
            method.Equals("contentPriceZero", StringComparison.OrdinalIgnoreCase) ||
            method.Equals("price_zero", StringComparison.OrdinalIgnoreCase) ||
            method.Equals("priceZero", StringComparison.OrdinalIgnoreCase) ||
            method.Equals("suppress_sale_value", StringComparison.OrdinalIgnoreCase) ||
            method.Equals("suppressSaleValue", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadFirstString(JsonObject root, params string[] paths)
    {
        foreach (var path in paths)
        {
            var value = ReadString(root, path);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static int RequireNonNegativeInt(JsonObject root, string path)
    {
        if (!TryGetPath(root, path, out var node) ||
            node is not JsonValue value ||
            !value.TryGetValue<int>(out var result))
        {
            throw new InvalidOperationException($"Expected integer at '{path}'.");
        }

        if (result < 0)
        {
            throw new InvalidOperationException($"Expected non-negative integer at '{path}'.");
        }

        return result;
    }

    private static int ReadOptionalNonNegativeInt(JsonObject root, string path, int defaultValue)
    {
        if (!TryGetPath(root, path, out var node) ||
            node is null)
        {
            return defaultValue;
        }

        if (node is not JsonValue value ||
            !value.TryGetValue<int>(out var result))
        {
            throw new InvalidOperationException($"Expected integer at '{path}'.");
        }

        if (result < 0)
        {
            throw new InvalidOperationException($"Expected non-negative integer at '{path}'.");
        }

        return result;
    }

    private static bool ReadOptionalBool(JsonObject root, string path, bool defaultValue)
    {
        if (!TryGetPath(root, path, out var node) ||
            node is null)
        {
            return defaultValue;
        }

        if (node is not JsonValue value ||
            !value.TryGetValue<bool>(out var result))
        {
            throw new InvalidOperationException($"Expected boolean at '{path}'.");
        }

        return result;
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

    private sealed record TrinketEntryProjectionResult(
        int EntryCount,
        int SaleValueAffectedEntryCount,
        int ShardStoreAffectedEntryCount,
        IReadOnlyList<string> AffectedShardStoreItemIds);

    private sealed record TrinketShardStoreProjectionItem(
        string Id,
        int Shard,
        int Limit,
        string Rarity,
        string OriginDungeon,
        bool RemovePrice,
        string ArtifactPath);
}
