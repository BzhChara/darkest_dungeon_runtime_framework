using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static partial class ManagedActionOverlayCompiler
{
    private const string TrinketEntryFilePattern = "*.entries.trinkets.json";
    private const string TrinketEntryPatchOverlayTarget = "content.trinkets.entries";

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
        var patchEntryOverlays = overlays
            .Where(overlay =>
                ReadString(overlay, "kind").Equals("trinket.patchEntry", StringComparison.OrdinalIgnoreCase) &&
                ReadBool(overlay, "enabled"))
            .ToArray();
        var patchItems = BuildTrinketEntryPatchItemList(patchEntryOverlays);

        if (patchItems.Count == 0)
        {
            return [];
        }

        var outputDirectory = Path.Combine(config.ModStateDirectory, "_managed_action_overlays", "trinket_entry_projection");
        Directory.CreateDirectory(outputDirectory);

        var rules = new List<OverlayVirtualRule>();
        var resolvedPatchEntryItemKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourcePath in EnumerateCampaignTrinketEntryFiles(config.GameWorkingDirectory))
        {
            try
            {
                var target = ToVirtualTarget(config.GameWorkingDirectory, sourcePath);
                var outputPath = Path.Combine(outputDirectory, SafeFileName(target));
                var result = WriteTrinketEntryProjectionOverlay(
                    sourcePath,
                    outputPath,
                    patchItems,
                    resolvedPatchEntryItemKeys);
                if (result.PatchEntryAffectedEntryCount == 0)
                {
                    continue;
                }

                var summary = new JsonObject
                {
                    ["target"] = target,
                    ["effect"] = "patchTrinketEntries",
                    ["sourcePath"] = outputPath,
                    ["sourceContentPath"] = sourcePath,
                    ["patchEntryOverlayCount"] = patchEntryOverlays.Length,
                    ["entryCount"] = result.EntryCount,
                    ["affectedEntryCount"] = result.PatchEntryAffectedEntryCount,
                    ["patchEntryAffectedEntryCount"] = result.PatchEntryAffectedEntryCount,
                    ["patchEntryItemIds"] = new JsonArray(result.AffectedPatchEntryItemIds
                        .Select(id => (JsonNode?)id)
                        .ToArray()),
                    ["sourceArtifacts"] = BuildTrinketEntryProjectionSourceArtifacts(patchEntryOverlays)
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
                    $"sourcePath={Quote(outputPath)} patchEntryAffectedEntries={result.PatchEntryAffectedEntryCount}");
            }
            catch (Exception ex)
            {
                AddIssue(
                    issues,
                    log,
                    "error",
                    "managed-overlay-trinket-entry-projection-failed",
                    string.Join(';', patchEntryOverlays.Select(overlay => ReadString(overlay, "artifactPath"))),
                    $"{sourcePath}: {ex.Message}");
            }
        }

        foreach (var missingItem in patchItems
                     .Where(item => !resolvedPatchEntryItemKeys.Contains(item.Key))
                     .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var first = missingItem.First();
            var code = string.IsNullOrWhiteSpace(first.Id)
                ? "managed-overlay-trinket-patch-entry-selector-not-found"
                : "managed-overlay-trinket-patch-entry-item-not-found";
            AddIssue(
                issues,
                log,
                "warning",
                code,
                string.Join(';', patchItems
                    .Where(item => item.Key.Equals(missingItem.Key, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.ArtifactPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)),
                $"trinket.patchEntry selector '{first.Description}' did not match any enabled trinket entry");
        }

        return rules;
    }

    private static TrinketEntryProjectionResult WriteTrinketEntryProjectionOverlay(
        string sourcePath,
        string outputPath,
        IReadOnlyList<TrinketEntryPatchItem> patchItems,
        HashSet<string> resolvedPatchEntryItemKeys)
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
        var patchEntryAffectedEntryCount = 0;
        var affectedPatchEntryItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.OfType<JsonObject>())
        {
            entryCount++;
            var id = ReadString(entry, "id");

            var matchingPatchItems = patchItems
                .Where(item => MatchesTrinketEntryPatchItem(entry, item))
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
            if (!string.IsNullOrWhiteSpace(id))
            {
                affectedPatchEntryItemIds.Add(id);
            }

            foreach (var patchItem in matchingPatchItems)
            {
                resolvedPatchEntryItemKeys.Add(patchItem.Key);
            }
        }

        if (patchEntryAffectedEntryCount > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            File.WriteAllText(outputPath, root.ToJsonString(JsonOptions), Utf8NoBom);
        }

        return new TrinketEntryProjectionResult(
            entryCount,
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
        var where = item["where"] as JsonObject;
        if (string.IsNullOrWhiteSpace(id) &&
            (where is null || where.Count == 0))
        {
            throw new InvalidOperationException("trinket.patchEntry item requires id or where.");
        }

        var setFields = item["set"] as JsonObject;
        var removeFields = BuildRemoveFields(item);
        if ((setFields is null || setFields.Count == 0) &&
            removeFields.Count == 0)
        {
            throw new InvalidOperationException($"trinket.patchEntry item '{DescribeTrinketPatchEntrySelector(id, where)}' requires set fields or remove fields.");
        }

        var result = new JsonObject
        {
            ["set"] = CloneNode(setFields) ?? new JsonObject(),
            ["remove"] = new JsonArray(removeFields
                .Select(field => (JsonNode?)field)
                .ToArray())
        };

        if (!string.IsNullOrWhiteSpace(id))
        {
            result["id"] = id;
        }

        if (where is not null && where.Count > 0)
        {
            result["where"] = CloneNode(where);
        }

        return result;
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

                var id = ReadFirstString(item, "id", "trinketId", "itemId");
                var where = item["where"] as JsonObject;
                var description = DescribeTrinketPatchEntrySelector(id, where);
                result.Add(new TrinketEntryPatchItem(
                    BuildTrinketPatchEntryKey(id, where),
                    id,
                    where ?? new JsonObject(),
                    item["set"] as JsonObject ?? new JsonObject(),
                    BuildRemoveFields(item),
                    ReadString(overlay, "artifactPath"),
                    description));
            }
        }

        return result;
    }

    private static bool MatchesTrinketEntryPatchItem(JsonObject entry, TrinketEntryPatchItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Id))
        {
            return ReadString(entry, "id").Equals(item.Id, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var condition in item.Where)
        {
            if (!entry.TryGetPropertyValue(condition.Key, out var actual) ||
                !MatchesTrinketPatchWhereValue(actual, condition.Value))
            {
                return false;
            }
        }

        return item.Where.Count > 0;
    }

    private static bool MatchesTrinketPatchWhereValue(JsonNode? actual, JsonNode? expected)
    {
        if (expected is JsonArray expectedArray)
        {
            return expectedArray.Any(item => MatchesTrinketPatchWhereValue(actual, item));
        }

        if (actual is JsonArray actualArray)
        {
            return actualArray.Any(item => MatchesTrinketPatchWhereValue(item, expected));
        }

        if (TryReadString(actual, out var actualText) &&
            TryReadString(expected, out var expectedText))
        {
            return actualText.Equals(expectedText, StringComparison.OrdinalIgnoreCase);
        }

        return JsonNode.DeepEquals(actual, expected);
    }

    private static bool TryReadString(JsonNode? node, out string text)
    {
        text = string.Empty;
        if (node is not JsonValue value ||
            !value.TryGetValue<string>(out var candidate) ||
            string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        text = candidate;
        return true;
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

    private static JsonArray BuildTrinketEntryProjectionSourceArtifacts(IReadOnlyList<JsonObject> patchEntryOverlays)
    {
        var result = new JsonArray();
        foreach (var overlay in patchEntryOverlays)
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
        int PatchEntryAffectedEntryCount,
        IReadOnlyList<string> AffectedPatchEntryItemIds);

    private sealed record TrinketEntryPatchItem(
        string Key,
        string Id,
        JsonObject Where,
        JsonObject SetFields,
        IReadOnlyList<string> RemoveFields,
        string ArtifactPath,
        string Description);

    private static string BuildTrinketPatchEntryKey(string id, JsonObject? where)
    {
        return string.IsNullOrWhiteSpace(id)
            ? "where:" + (where?.ToJsonString(JsonOptions) ?? "{}")
            : "id:" + id.Trim();
    }

    private static string DescribeTrinketPatchEntrySelector(string id, JsonObject? where)
    {
        return string.IsNullOrWhiteSpace(id)
            ? "where " + (where?.ToJsonString(JsonOptions) ?? "{}")
            : "id " + id.Trim();
    }
}
