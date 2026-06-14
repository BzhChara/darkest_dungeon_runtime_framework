using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static partial class ManagedActionOverlayCompiler
{
    private const string TrinketEntryFilePattern = "*.entries.trinkets.json";
    private const string InventorySalePolicyOverlayTarget = "profile.inventory.saleDisabled";
    private const string InventoryTrinketSaleValueOverlayTarget = "content.trinkets.price";
    private const string TrinketEntryPatchOverlayTarget = "content.trinkets.entries";

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

    private static JsonObject BuildTrinketPatchEntryOverlay(string artifactPath, JsonObject artifact)
    {
        var plan = RequireObject(artifact, "plan");
        var arguments = RequireObject(plan, "arguments");
        var enabled = ReadOptionalBool(arguments, "enabled", true);
        var items = BuildTrinketPatchEntryItems(arguments);

        return new JsonObject
        {
            ["kind"] = "trinket.patchEntry",
            ["effect"] = "patchEntry",
            ["target"] = TrinketEntryPatchOverlayTarget,
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
        var enabledInventoryOverlays = overlays
            .Where(overlay =>
                ReadString(overlay, "kind").Equals("inventory.disableItemSale", StringComparison.OrdinalIgnoreCase) &&
                ReadBool(overlay, "disabled"))
            .ToArray();

        foreach (var overlay in enabledInventoryOverlays.Where(overlay =>
                     ReadString(overlay, "effect").Equals("recordSalePolicy", StringComparison.OrdinalIgnoreCase)))
        {
            log.Info(
                $"managed-action-overlay policy-only itemKind={Quote(ReadString(overlay, "itemKind"))} " +
                $"target={Quote(ReadString(overlay, "target"))} artifact={Quote(ReadString(overlay, "artifactPath"))}");
        }

        var trinketSaleValueOverlays = enabledInventoryOverlays
            .Where(overlay =>
                ReadString(overlay, "effect").Equals("suppressSaleValue", StringComparison.OrdinalIgnoreCase) &&
                ReadString(overlay, "itemKind").Equals("trinket", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var patchEntryOverlays = overlays
            .Where(overlay =>
                ReadString(overlay, "kind").Equals("trinket.patchEntry", StringComparison.OrdinalIgnoreCase) &&
                ReadBool(overlay, "enabled"))
            .ToArray();
        var patchItems = BuildTrinketEntryPatchItemList(patchEntryOverlays);

        if (trinketSaleValueOverlays.Length == 0 &&
            patchItems.Count == 0)
        {
            return [];
        }

        var outputDirectory = Path.Combine(config.ModStateDirectory, "_managed_action_overlays", "trinket_entry_projection");
        Directory.CreateDirectory(outputDirectory);

        var rules = new List<OverlayVirtualRule>();
        var resolvedPatchEntryItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    patchItems,
                    resolvedPatchEntryItemIds);
                if (result.SaleValueAffectedEntryCount == 0 &&
                    result.PatchEntryAffectedEntryCount == 0)
                {
                    continue;
                }

                var summary = new JsonObject
                {
                    ["target"] = target,
                    ["effect"] = result.SaleValueAffectedEntryCount > 0
                        ? "suppressTrinketSaleValue"
                        : "patchTrinketEntries",
                    ["sourcePath"] = outputPath,
                    ["sourceContentPath"] = sourcePath,
                    ["policyOverlayCount"] = trinketSaleValueOverlays.Length,
                    ["patchEntryOverlayCount"] = patchEntryOverlays.Length,
                    ["entryCount"] = result.EntryCount,
                    ["affectedEntryCount"] = result.SaleValueAffectedEntryCount,
                    ["saleValueAffectedEntryCount"] = result.SaleValueAffectedEntryCount,
                    ["patchEntryAffectedEntryCount"] = result.PatchEntryAffectedEntryCount,
                    ["patchEntryItemIds"] = new JsonArray(result.AffectedPatchEntryItemIds
                        .Select(id => (JsonNode?)id)
                        .ToArray()),
                    ["sourceArtifacts"] = BuildTrinketEntryProjectionSourceArtifacts(
                        trinketSaleValueOverlays,
                        patchEntryOverlays)
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
                    $"patchEntryAffectedEntries={result.PatchEntryAffectedEntryCount}");
            }
            catch (Exception ex)
            {
                AddIssue(
                    issues,
                    log,
                    "error",
                    "managed-overlay-trinket-entry-projection-failed",
                    string.Join(';', trinketSaleValueOverlays.Concat(patchEntryOverlays).Select(overlay => ReadString(overlay, "artifactPath"))),
                    $"{sourcePath}: {ex.Message}");
            }
        }

        foreach (var missingId in patchItems
                     .Select(item => item.Id)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(id => !resolvedPatchEntryItemIds.Contains(id))
                     .OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            AddIssue(
                issues,
                log,
                "warning",
                "managed-overlay-trinket-patch-entry-item-not-found",
                string.Join(';', patchItems
                    .Where(item => item.Id.Equals(missingId, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.ArtifactPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)),
                $"trinket.patchEntry referenced trinket id '{missingId}', but no enabled trinket entry file defined it");
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
        IReadOnlyList<TrinketEntryPatchItem> patchItems,
        HashSet<string> resolvedPatchEntryItemIds)
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
        var patchEntryAffectedEntryCount = 0;
        var affectedPatchEntryItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var matchingPatchItems = patchItems
                .Where(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matchingPatchItems.Length == 0)
            {
                continue;
            }

            foreach (var patchItem in matchingPatchItems)
            {
                foreach (var field in patchItem.RemoveFields)
                {
                    entry.Remove(field);
                }

                foreach (var pair in patchItem.SetFields)
                {
                    entry[pair.Key] = CloneNode(pair.Value);
                }
            }

            patchEntryAffectedEntryCount++;
            affectedPatchEntryItemIds.Add(id);
            resolvedPatchEntryItemIds.Add(id);
        }

        if (saleValueAffectedEntryCount > 0 ||
            patchEntryAffectedEntryCount > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            File.WriteAllText(outputPath, root.ToJsonString(JsonOptions), Utf8NoBom);
        }

        return new TrinketEntryProjectionResult(
            entryCount,
            saleValueAffectedEntryCount,
            patchEntryAffectedEntryCount,
            affectedPatchEntryItemIds
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static JsonArray BuildTrinketPatchEntryItems(JsonObject arguments)
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

                result.Add(BuildTrinketPatchEntryItem(item));
            }
        }
        else
        {
            result.Add(BuildTrinketPatchEntryItem(arguments));
        }

        if (result.Count == 0)
        {
            throw new InvalidOperationException("trinket.patchEntry requires at least one item.");
        }

        return result;
    }

    private static JsonObject BuildTrinketPatchEntryItem(JsonObject item)
    {
        var id = ReadFirstString(item, "id", "trinketId", "itemId");
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("trinket.patchEntry item requires id.");
        }

        var setFields = item["set"] as JsonObject;
        var removeFields = BuildRemoveFields(item);
        if ((setFields is null || setFields.Count == 0) &&
            removeFields.Count == 0)
        {
            throw new InvalidOperationException($"trinket.patchEntry item '{id}' requires set fields or remove fields.");
        }

        return new JsonObject
        {
            ["id"] = id,
            ["set"] = CloneNode(setFields) ?? new JsonObject(),
            ["remove"] = new JsonArray(removeFields
                .Select(field => (JsonNode?)field)
                .ToArray())
        };
    }

    private static List<TrinketEntryPatchItem> BuildTrinketEntryPatchItemList(IReadOnlyList<JsonObject> overlays)
    {
        var result = new List<TrinketEntryPatchItem>();
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

                result.Add(new TrinketEntryPatchItem(
                    RequireString(item, "id"),
                    item["set"] as JsonObject ?? new JsonObject(),
                    BuildRemoveFields(item),
                    ReadString(overlay, "artifactPath")));
            }
        }

        return result;
    }

    private static IReadOnlyList<string> BuildRemoveFields(JsonObject item)
    {
        if (!item.TryGetPropertyValue("remove", out var node) || node is null)
        {
            return [];
        }

        if (node is JsonValue value &&
            value.TryGetValue<string>(out var singleField) &&
            !string.IsNullOrWhiteSpace(singleField))
        {
            return [singleField];
        }

        if (node is not JsonArray fields)
        {
            throw new InvalidOperationException("trinket.patchEntry remove must be a string or an array of strings.");
        }

        var result = new List<string>();
        foreach (var fieldNode in fields)
        {
            if (fieldNode is not JsonValue fieldValue ||
                !fieldValue.TryGetValue<string>(out var field) ||
                string.IsNullOrWhiteSpace(field))
            {
                throw new InvalidOperationException("trinket.patchEntry remove entries must be non-empty strings.");
            }

            result.Add(field);
        }

        return result;
    }

    private static JsonArray BuildTrinketEntryProjectionSourceArtifacts(
        IReadOnlyList<JsonObject> saleValueOverlays,
        IReadOnlyList<JsonObject> patchEntryOverlays)
    {
        var result = new JsonArray();
        foreach (var overlay in saleValueOverlays.Concat(patchEntryOverlays))
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
        int PatchEntryAffectedEntryCount,
        IReadOnlyList<string> AffectedPatchEntryItemIds);

    private sealed record TrinketEntryPatchItem(
        string Id,
        JsonObject SetFields,
        IReadOnlyList<string> RemoveFields,
        string ArtifactPath);
}
