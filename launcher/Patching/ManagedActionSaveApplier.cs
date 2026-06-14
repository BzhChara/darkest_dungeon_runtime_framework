using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static partial class ManagedActionSaveApplier
{
    private const int ReportVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static ManagedActionApplyReport Apply(
        RuntimeConfig config,
        LauncherLog log,
        string projectRoot,
        string saveDirectory,
        bool writeChanges,
        ManagedActionApplyMode applyMode = ManagedActionApplyMode.All)
    {
        var artifactDirectory = Path.Combine(config.ModStateDirectory, "_managed_actions");
        var resolvedSaveDirectory = ResolveProjectLocalDirectory(projectRoot, saveDirectory, "--managed-action-save-dir");
        if (!Directory.Exists(resolvedSaveDirectory))
        {
            throw new DirectoryNotFoundException($"Managed action save directory was not found: {resolvedSaveDirectory}");
        }

        var context = new ApplyContext(config.GameWorkingDirectory, config.ModStateDirectory, resolvedSaveDirectory, writeChanges);
        if (Directory.Exists(artifactDirectory))
        {
            foreach (var artifactPath in SelectArtifactPaths(artifactDirectory, applyMode, log))
            {
                context.ArtifactCount++;
                ApplyArtifact(context, artifactPath, log);
            }
        }

        WriteChangedFiles(context);

        var changedFiles = context.Files.Values
            .Where(file => file.Changed)
            .Select(file => new ManagedActionApplyFileReport(file.Path, file.ChangeCount, file.Written))
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var report = new ManagedActionApplyReport(
            ReportVersion,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            artifactDirectory,
            resolvedSaveDirectory,
            !writeChanges,
            GetApplyModeReportName(applyMode),
            context.ArtifactCount,
            context.Actions.Count(action => action.Status is "applied" or "dry-run"),
            context.Actions.Count(action => action.Status == "dry-run"),
            context.Actions.Count(action => action.Status == "applied"),
            context.Actions.Count(action => action.Status == "unsupported"),
            context.Actions.Count(action => action.Status == "failed"),
            changedFiles.Length,
            context.Actions,
            changedFiles,
            context.Issues);

        LogAndWriteReport(config, log, report);
        return report;
    }

    private static void ApplyArtifact(ApplyContext context, string artifactPath, LauncherLog log)
    {
        try
        {
            var artifact = JsonNode.Parse(File.ReadAllText(artifactPath, Encoding.UTF8)) as JsonObject
                ?? throw new InvalidDataException("artifact root must be a JSON object");
            var status = ReadString(artifact, "status");
            var actionType = ReadString(artifact, "action.type");
            if (!status.Equals("materialized", StringComparison.OrdinalIgnoreCase))
            {
                context.Actions.Add(new ManagedActionApplyActionReport(
                    artifactPath,
                    actionType,
                    "skipped",
                    null,
                    [],
                    [$"artifact status is {status}"]));
                return;
            }

            switch (actionType)
            {
                case "wallet.setCurrencyAmounts":
                    ApplyWalletSetCurrencyAmounts(context, artifactPath, artifact);
                    break;
                case "wallet.setCurrencyAmount":
                    ApplyWalletSetCurrencyAmount(context, artifactPath, artifact);
                    break;
                case "estate.ensureInventoryCounts":
                    ApplyEstateEnsureInventoryCounts(context, artifactPath, artifact);
                    break;
                case "inventory.disableItemSale":
                    ApplyInventoryDisableItemSale(context, artifactPath, artifact);
                    break;
                case "roster.enforceAvailabilityFilter":
                    ApplyRosterEnforceAvailabilityFilter(context, artifactPath, artifact);
                    break;
                case "equipment.enforceAvailabilityFilter":
                    ApplyEquipmentEnforceAvailabilityFilter(context, artifactPath, artifact);
                    break;
                case "campaign.resetPlotProgress":
                    ApplyCampaignResetPlotProgress(context, artifactPath, artifact);
                    break;
                case "roster.ensureClassInstances":
                    ApplyRosterEnsureClassInstances(context, artifactPath, artifact);
                    break;
                case "roster.setProgression":
                    ApplyRosterSetProgression(context, artifactPath, artifact);
                    break;
                case "roster.setSkillUnlocks":
                    ApplyRosterSetSkillUnlocks(context, artifactPath, artifact);
                    break;
                case "upgrade.ensurePurchases":
                    ApplyUpgradeEnsurePurchases(context, artifactPath, artifact);
                    break;
                case "stagecoach.suppressRecruits":
                    ApplyStagecoachSuppressRecruits(context, artifactPath, artifact);
                    break;
                case "town.unlockAllBuildings":
                    ApplyTownUnlockAllBuildings(context, artifactPath, artifact);
                    break;
                case "town.suppressStoreItems":
                    ApplyTownSuppressStoreItems(context, artifactPath, artifact);
                    break;
                case "townEvent.overrideCurrent":
                    ApplyTownEventOverrideCurrent(context, artifactPath, artifact);
                    break;
                case "questBoard.replaceWithFixedSet":
                    ApplyQuestBoardReplaceWithFixedSet(context, artifactPath, artifact);
                    break;
                default:
                    AddUnsupportedAction(context, artifactPath, artifact, actionType);
                    break;
            }
        }
        catch (Exception ex)
        {
            context.Issues.Add(new ManagedActionApplyIssue("error", "managed-action-apply-failed", artifactPath, ex.Message));
            context.Actions.Add(new ManagedActionApplyActionReport(
                artifactPath,
                string.Empty,
                "failed",
                null,
                [],
                [ex.Message]));
            log.Error($"managed-action-apply issue code=managed-action-apply-failed path={Quote(artifactPath)} message={Quote(ex.Message)}");
        }
    }

    private static void ApplyWalletSetCurrencyAmounts(ApplyContext context, string artifactPath, JsonObject artifact)
    {
        var amounts = RequireObject(artifact, "plan.arguments.amounts");
        var file = context.LoadDecodedJsonFile("persist.estate.json");
        var wallet = EnsureObject(file.Root, "base_root.wallet");
        var operations = new List<string>();

        foreach (var pair in amounts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var amount = ReadInt(pair.Value, $"plan.arguments.amounts.{pair.Key}");
            var changed = SetWalletCurrencyAmount(wallet, pair.Key, amount, context.WriteChanges);
            if (changed)
            {
                file.MarkChanged();
            }

            operations.Add($"set wallet {pair.Key}={amount}");
        }

        AddSuccessfulAction(context, artifactPath, artifact, file.Path, operations);
    }

    private static void ApplyWalletSetCurrencyAmount(ApplyContext context, string artifactPath, JsonObject artifact)
    {
        var currency = ReadString(artifact, "plan.arguments.currency");
        var amount = ReadInt(ReadNode(artifact, "plan.arguments.amount"), "plan.arguments.amount");
        var file = context.LoadDecodedJsonFile("persist.estate.json");
        var wallet = EnsureObject(file.Root, "base_root.wallet");
        var changed = SetWalletCurrencyAmount(wallet, currency, amount, context.WriteChanges);
        if (changed)
        {
            file.MarkChanged();
        }

        AddSuccessfulAction(context, artifactPath, artifact, file.Path, [$"set wallet {currency}={amount}"]);
    }

    private static void ApplyEstateEnsureInventoryCounts(ApplyContext context, string artifactPath, JsonObject artifact)
    {
        var itemKind = ReadString(artifact, "plan.arguments.itemKind");
        if (!itemKind.Equals("trinket", StringComparison.OrdinalIgnoreCase))
        {
            AddUnsupportedAction(context, artifactPath, artifact, ReadString(artifact, "action.type"));
            return;
        }

        var source = ReadString(artifact, "plan.arguments.source");
        var count = ReadInt(ReadNode(artifact, "plan.arguments.count"), "plan.arguments.count");
        var excludeRarities = ReadOptionalStringArrayPath(artifact, "plan.arguments.excludeRarities");
        if (count < 0)
        {
            throw new InvalidDataException("plan.arguments.count must be zero or greater.");
        }

        var itemIds = ResolveInventorySourceIds(context, source, excludeRarities);
        if (itemIds.Count == 0)
        {
            throw new InvalidDataException($"Inventory source produced no item ids: {source}");
        }

        var file = context.LoadDecodedJsonFile("persist.estate.json");
        var items = EnsureObject(file.Root, "base_root.trinkets.items");
        var result = EnsureInventoryItemCounts(items, itemIds, itemKind, count, context.WriteChanges);
        if (result.ChangedCount > 0)
        {
            file.MarkChanged(result.ChangedCount);
        }

        AddSuccessfulAction(
            context,
            artifactPath,
            artifact,
            file.Path,
            [
                $"ensure {result.SourceCount} {itemKind} ids from {source} copies={count}{FormatInventorySourceFilters(excludeRarities)}",
                $"added={result.AddedCount} updated={result.UpdatedCount} unchanged={result.UnchangedCount}"
            ]);
    }

    private static IReadOnlyList<string> ResolveInventorySourceIds(
        ApplyContext context,
        string source,
        IReadOnlyList<string> excludeRarities)
    {
        return source switch
        {
            "content.trinkets.enabled" => LoadEnabledContentTrinketIds(context.GameWorkingDirectory, excludeRarities),
            _ => throw new InvalidDataException($"Unsupported inventory source: {source}")
        };
    }

    private static InventoryEnsureResult EnsureInventoryItemCounts(
        JsonObject items,
        IReadOnlyList<string> itemIds,
        string itemKind,
        int count,
        bool writeChanges)
    {
        if (itemKind.Equals("trinket", StringComparison.OrdinalIgnoreCase))
        {
            return EnsureNonStackableInventoryItemCounts(items, itemIds, itemKind, count, writeChanges);
        }

        return EnsureStackableInventoryItemCounts(items, itemIds, itemKind, count, writeChanges);
    }

    private static InventoryEnsureResult EnsureNonStackableInventoryItemCounts(
        JsonObject items,
        IReadOnlyList<string> itemIds,
        string itemKind,
        int count,
        bool writeChanges)
    {
        var targetIds = new HashSet<string>(itemIds, StringComparer.OrdinalIgnoreCase);
        var entriesById = new Dictionary<string, List<JsonObject>>(StringComparer.OrdinalIgnoreCase);
        var maxNumericKey = -1;
        foreach (var pair in items)
        {
            if (int.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericKey))
            {
                maxNumericKey = Math.Max(maxNumericKey, numericKey);
            }

            if (pair.Value is JsonObject candidate &&
                ReadOptionalString(candidate, "type").Equals(itemKind, StringComparison.OrdinalIgnoreCase))
            {
                var id = ReadOptionalString(candidate, "id");
                if (!string.IsNullOrWhiteSpace(id) && targetIds.Contains(id))
                {
                    if (!entriesById.TryGetValue(id, out var entries))
                    {
                        entries = [];
                        entriesById[id] = entries;
                    }

                    entries.Add(candidate);
                }
            }
        }

        var added = 0;
        var updated = 0;
        var unchanged = 0;
        foreach (var itemId in itemIds)
        {
            entriesById.TryGetValue(itemId, out var existingEntries);
            existingEntries ??= [];

            var normalized = NormalizeNonStackableEntryAmounts(existingEntries, writeChanges);
            updated += normalized;

            var missing = Math.Max(0, count - existingEntries.Count);
            if (writeChanges)
            {
                for (var i = 0; i < missing; i++)
                {
                    maxNumericKey++;
                    items[maxNumericKey.ToString(CultureInfo.InvariantCulture)] = new JsonObject
                    {
                        ["id"] = itemId,
                        ["type"] = itemKind,
                        ["amount"] = 1
                    };
                }
            }

            added += missing;
            if (missing == 0 && normalized == 0)
            {
                unchanged++;
            }
        }

        return new InventoryEnsureResult(itemIds.Count, added, updated, unchanged);
    }

    private static int NormalizeNonStackableEntryAmounts(IReadOnlyList<JsonObject> entries, bool writeChanges)
    {
        var updated = 0;
        foreach (var entry in entries)
        {
            if (ReadOptionalInt(entry, "amount") == 1)
            {
                continue;
            }

            if (writeChanges)
            {
                entry["amount"] = 1;
            }

            updated++;
        }

        return updated;
    }

    private static InventoryEnsureResult EnsureStackableInventoryItemCounts(
        JsonObject items,
        IReadOnlyList<string> itemIds,
        string itemKind,
        int count,
        bool writeChanges)
    {
        var entriesById = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var maxNumericKey = -1;
        foreach (var pair in items)
        {
            if (int.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericKey))
            {
                maxNumericKey = Math.Max(maxNumericKey, numericKey);
            }

            if (pair.Value is JsonObject candidate &&
                ReadOptionalString(candidate, "type").Equals(itemKind, StringComparison.OrdinalIgnoreCase))
            {
                var id = ReadOptionalString(candidate, "id");
                if (!string.IsNullOrWhiteSpace(id) && !entriesById.ContainsKey(id))
                {
                    entriesById[id] = candidate;
                }
            }
        }

        var added = 0;
        var updated = 0;
        var unchanged = 0;
        foreach (var itemId in itemIds)
        {
            if (entriesById.TryGetValue(itemId, out var existing))
            {
                var currentAmount = ReadOptionalInt(existing, "amount");
                if (currentAmount == count)
                {
                    unchanged++;
                    continue;
                }

                if (writeChanges)
                {
                    existing["amount"] = count;
                }

                updated++;
                continue;
            }

            if (writeChanges)
            {
                maxNumericKey++;
                items[maxNumericKey.ToString(CultureInfo.InvariantCulture)] = new JsonObject
                {
                    ["id"] = itemId,
                    ["type"] = itemKind,
                    ["amount"] = count
                };
            }

            added++;
        }

        return new InventoryEnsureResult(itemIds.Count, added, updated, unchanged);
    }

    private static IReadOnlyList<string> LoadEnabledContentTrinketIds(
        string gameWorkingDirectory,
        IReadOnlyList<string> excludeRarities)
    {
        var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludedRarities = excludeRarities.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateCampaignTrinketEntryFiles(gameWorkingDirectory))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (!document.RootElement.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object &&
                    entry.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    var rarity = entry.TryGetProperty("rarity", out var rarityElement) &&
                        rarityElement.ValueKind == JsonValueKind.String
                        ? rarityElement.GetString()
                        : string.Empty;
                    if (!string.IsNullOrWhiteSpace(rarity) && excludedRarities.Contains(rarity))
                    {
                        continue;
                    }

                    ids.Add(id.GetString()!);
                }
            }
        }

        return ids.ToArray();
    }

    private static IEnumerable<string> EnumerateCampaignTrinketEntryFiles(string gameWorkingDirectory)
    {
        var baseTrinketDirectory = Path.Combine(gameWorkingDirectory, "trinkets");
        if (Directory.Exists(baseTrinketDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(baseTrinketDirectory, "*.entries.trinkets.json", SearchOption.TopDirectoryOnly))
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

            foreach (var path in Directory.EnumerateFiles(directory, "*.entries.trinkets.json", SearchOption.AllDirectories))
            {
                yield return path;
            }
        }
    }

    private static string FormatInventorySourceFilters(IReadOnlyList<string> excludeRarities)
    {
        return excludeRarities.Count == 0
            ? string.Empty
            : $" excludeRarities={string.Join(",", excludeRarities)}";
    }

    private static bool SetWalletCurrencyAmount(JsonObject wallet, string currency, int amount, bool writeChanges)
    {
        JsonObject? entry = null;
        var maxNumericKey = -1;
        foreach (var pair in wallet)
        {
            if (int.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericKey))
            {
                maxNumericKey = Math.Max(maxNumericKey, numericKey);
            }

            if (pair.Value is JsonObject candidate &&
                ReadOptionalString(candidate, "type").Equals(currency, StringComparison.OrdinalIgnoreCase))
            {
                entry = candidate;
            }
        }

        if (entry is not null)
        {
            var currentAmount = ReadOptionalInt(entry, "amount");
            if (currentAmount == amount)
            {
                return false;
            }

            if (writeChanges)
            {
                entry["amount"] = amount;
            }

            return true;
        }

        if (writeChanges)
        {
            wallet[(maxNumericKey + 1).ToString(CultureInfo.InvariantCulture)] = new JsonObject
            {
                ["amount"] = amount,
                ["type"] = currency
            };
        }

        return true;
    }

    private static void AddUnsupportedAction(
        ApplyContext context,
        string artifactPath,
        JsonObject artifact,
        string actionType)
    {
        var targetFile = actionType switch
        {
            "roster.ensureClassInstances" or "roster.setSkillUnlocks" => "persist.roster.json",
            "upgrade.ensurePurchases" => "persist.upgrades.json",
            "stagecoach.suppressRecruits" or "town.unlockAllBuildings" or "town.setBuildingLevels" or "town.suppressStoreItems" => "persist.town.json",
            "estate.ensureInventoryCounts" or "inventory.disableItemSale" => "persist.estate.json",
            "campaign.resetPlotProgress" => "persist.progression.json",
            "townEvent.overrideCurrent" => "persist.town_event.json",
            "questBoard.replaceWithFixedSet" => "persist.quest.json",
            _ => null
        };

        var message = $"managed action applier does not implement {actionType} yet";
        context.Issues.Add(new ManagedActionApplyIssue("warning", "managed-action-applier-not-implemented", artifactPath, message));
        context.Actions.Add(new ManagedActionApplyActionReport(
            artifactPath,
            actionType,
            "unsupported",
            targetFile is null ? null : Path.Combine(context.SaveDirectory, targetFile),
            [ReadString(artifact, "plan.effect")],
            [message]));
    }

    private static void AddSuccessfulAction(
        ApplyContext context,
        string artifactPath,
        JsonObject artifact,
        string targetFile,
        IReadOnlyList<string> operations)
    {
        context.Actions.Add(new ManagedActionApplyActionReport(
            artifactPath,
            ReadString(artifact, "action.type"),
            context.WriteChanges ? "applied" : "dry-run",
            targetFile,
            operations,
            []));
    }

    private static void WriteChangedFiles(ApplyContext context)
    {
        if (!context.WriteChanges)
        {
            return;
        }

        foreach (var file in context.Files.Values.Where(file => file.Changed))
        {
            File.WriteAllText(file.Path, file.Root.ToJsonString(JsonOptions), Encoding.UTF8);
            file.Written = true;
        }
    }

    private static JsonObject EnsureObject(JsonObject root, string path)
    {
        var current = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current[part] is JsonObject existing)
            {
                current = existing;
                continue;
            }

            var created = new JsonObject();
            current[part] = created;
            current = created;
        }

        return current;
    }

    private static bool SetJsonPropertyIfChanged(JsonObject root, string key, JsonNode? value, bool writeChanges)
    {
        if (JsonNode.DeepEquals(root[key], value))
        {
            return false;
        }

        if (writeChanges)
        {
            root[key] = CloneJsonNode(value);
        }

        return true;
    }

    private static JsonNode? CloneJsonNode(JsonNode? node)
    {
        return node?.DeepClone();
    }

    private static JsonObject RequireObject(JsonObject root, string path)
    {
        return ReadNode(root, path) as JsonObject
            ?? throw new InvalidDataException($"{path} must be a JSON object.");
    }

    private static JsonNode? ReadNode(JsonObject root, string path)
    {
        JsonNode? current = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            current = current is JsonObject obj ? obj[part] : null;
            if (current is null)
            {
                throw new InvalidDataException($"{path} is missing.");
            }
        }

        return current;
    }

    private static string ReadString(JsonObject root, string path)
    {
        return ReadNode(root, path)?.GetValue<string>()
            ?? throw new InvalidDataException($"{path} must be a string.");
    }

    private static IReadOnlyList<string> ReadOptionalStringArrayPath(JsonObject root, string path)
    {
        JsonNode? current = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            current = current is JsonObject obj ? obj[part] : null;
            if (current is null)
            {
                return [];
            }
        }

        if (current is not JsonArray array)
        {
            throw new InvalidDataException($"{path} must be a string array when present.");
        }

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

    private static string ReadOptionalString(JsonObject root, string key)
    {
        return root[key]?.GetValue<string>() ?? string.Empty;
    }

    private static int ReadInt(JsonNode? node, string path)
    {
        if (node is null)
        {
            throw new InvalidDataException($"{path} is missing.");
        }

        if (node is JsonValue value && value.TryGetValue<int>(out var result))
        {
            return result;
        }

        throw new InvalidDataException($"{path} must be an integer.");
    }

    private static bool ReadBool(JsonNode? node, string path)
    {
        if (node is null)
        {
            throw new InvalidDataException($"{path} is missing.");
        }

        if (node is JsonValue value && value.TryGetValue<bool>(out var result))
        {
            return result;
        }

        throw new InvalidDataException($"{path} must be a boolean.");
    }

    private static int? ReadOptionalInt(JsonObject root, string key)
    {
        return root[key] is JsonValue value && value.TryGetValue<int>(out var result)
            ? result
            : null;
    }

    private static string ResolveProjectLocalDirectory(string projectRoot, string path, string optionName)
    {
        var fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path));
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{optionName} must stay inside project root for decoded-save managed action application: {fullPath}");
        }

        return fullPath;
    }

    private static void LogAndWriteReport(RuntimeConfig config, LauncherLog log, ManagedActionApplyReport report)
    {
        var reportPath = Path.Combine(config.LogDirectory, "managed_action_apply_report.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        log.Info(
            $"managed-action-apply report path={Quote(reportPath)} mode={Quote(report.ApplyMode)} artifacts={report.ArtifactCount} " +
            $"dryRun={report.DryRun} supported={report.SupportedActionCount} dryRunActions={report.DryRunActionCount} " +
            $"applied={report.AppliedActionCount} unsupported={report.UnsupportedActionCount} " +
            $"failed={report.FailedActionCount} changedFiles={report.ChangedFileCount} issues={report.Issues.Count}");
    }

    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';

    private sealed class ApplyContext(string gameWorkingDirectory, string modStateDirectory, string saveDirectory, bool writeChanges)
    {
        public string GameWorkingDirectory { get; } = gameWorkingDirectory;
        public string ModStateDirectory { get; } = modStateDirectory;
        public string SaveDirectory { get; } = saveDirectory;
        public bool WriteChanges { get; } = writeChanges;
        public int ArtifactCount { get; set; }
        public Dictionary<string, DecodedJsonFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<ManagedActionApplyActionReport> Actions { get; } = [];
        public List<ManagedActionApplyIssue> Issues { get; } = [];

        public DecodedJsonFile LoadDecodedJsonFile(string fileName)
        {
            var path = Path.Combine(SaveDirectory, fileName);
            if (Files.TryGetValue(path, out var cached))
            {
                return cached;
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Decoded save file was not found: {path}", path);
            }

            var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject
                ?? throw new InvalidDataException($"Decoded save file root must be a JSON object: {path}");
            var file = new DecodedJsonFile(path, root);
            Files[path] = file;
            return file;
        }

        public DecodedJsonFile LoadOrCreateJsonFile(string fileName, Func<JsonObject> createRoot)
        {
            var path = Path.Combine(SaveDirectory, fileName);
            if (Files.TryGetValue(path, out var cached))
            {
                return cached;
            }

            var root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject
                    ?? throw new InvalidDataException($"JSON file root must be an object: {path}")
                : createRoot();
            var file = new DecodedJsonFile(path, root);
            Files[path] = file;
            return file;
        }
    }

    private sealed class DecodedJsonFile(string path, JsonObject root)
    {
        public string Path { get; } = path;
        public JsonObject Root { get; } = root;
        public bool Changed { get; private set; }
        public bool Written { get; set; }
        public int ChangeCount { get; private set; }

        public void MarkChanged(int count = 1)
        {
            Changed = true;
            ChangeCount += count;
        }
    }

    private sealed record InventoryEnsureResult(
        int SourceCount,
        int AddedCount,
        int UpdatedCount,
        int UnchangedCount)
    {
        public int ChangedCount => AddedCount + UpdatedCount;
    }
}

internal sealed record ManagedActionApplyReport(
    int Version,
    string GeneratedAtUtc,
    string ArtifactDirectory,
    string SaveDirectory,
    bool DryRun,
    string ApplyMode,
    int ArtifactCount,
    int SupportedActionCount,
    int DryRunActionCount,
    int AppliedActionCount,
    int UnsupportedActionCount,
    int FailedActionCount,
    int ChangedFileCount,
    IReadOnlyList<ManagedActionApplyActionReport> Actions,
    IReadOnlyList<ManagedActionApplyFileReport> Files,
    IReadOnlyList<ManagedActionApplyIssue> Issues)
{
    public bool Succeeded => FailedActionCount == 0;
}

internal sealed record ManagedActionApplyActionReport(
    string ArtifactPath,
    string ActionType,
    string Status,
    string? TargetFile,
    IReadOnlyList<string> Operations,
    IReadOnlyList<string> Issues);

internal sealed record ManagedActionApplyFileReport(
    string Path,
    int ChangeCount,
    bool Written);

internal sealed record ManagedActionApplyIssue(
    string Severity,
    string Code,
    string ArtifactPath,
    string Message);
