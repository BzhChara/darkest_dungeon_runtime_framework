using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static class ModStateStore
{
    private const int ReportVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static ModStateStoreReport InitializeDefaults(RuntimeConfig config, PatchPlan patchPlan, LauncherLog log, string? pluginIdFilter)
    {
        var issues = new List<ModStateIssue>();
        var sources = SelectStateSources(patchPlan, pluginIdFilter, issues);
        var fileNames = BuildStateFileNames(sources);
        var plugins = new List<ModStatePluginReport>();
        var writtenCount = 0;

        Directory.CreateDirectory(config.ModStateDirectory);

        foreach (var source in sources)
        {
            var statePath = Path.Combine(config.ModStateDirectory, fileNames[source]);
            if (!TryReadStateRoot(statePath, source, issues, out var root, out var created))
            {
                plugins.Add(CreatePluginReport(source, statePath, "invalid", [], [], null));
                continue;
            }

            var state = GetOrCreateStateObject(root, source, statePath, issues);
            if (state is null)
            {
                plugins.Add(CreatePluginReport(source, statePath, "invalid", [], [], null));
                continue;
            }

            var changed = created;
            var addedKeys = new List<string>();
            foreach (var (key, schema) in source.StateSchema)
            {
                if (state.ContainsKey(key))
                {
                    continue;
                }

                state[key] = BuildDefaultValue(schema);
                addedKeys.Add(key);
                changed = true;
            }

            changed |= EnsureNumber(root, "version", ReportVersion);
            changed |= EnsureString(root, "pluginId", source.PluginId);
            changed |= EnsureString(root, "pluginSource", source.SourceName);
            changed |= EnsureString(root, "pluginManifestPath", source.SourcePath);
            changed |= EnsureNumber(root, "loadOrder", source.LoadOrder);
            changed |= EnsureString(root, "createdAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), overwrite: false);

            if (!ReferenceEquals(root["state"], state))
            {
                root["state"] = state;
                changed = true;
            }

            var writeMode = "none";
            if (changed)
            {
                root["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                var writeResult = WriteJsonAtomic(statePath, root, source, issues, config.AllowNonAtomicStateWrites);
                writeMode = writeResult.Mode;
                if (!writeResult.Succeeded)
                {
                    plugins.Add(CreatePluginReport(source, statePath, "write-failed", addedKeys, StateKeys(state), CloneNode(state), writeResult.Mode));
                    continue;
                }

                writtenCount++;
            }

            var status = created ? "created" : addedKeys.Count > 0 ? "merged-defaults" : "unchanged";
            plugins.Add(CreatePluginReport(source, statePath, status, addedKeys, StateKeys(state), CloneNode(state), writeMode));
        }

        var report = new ModStateStoreReport(
            ReportVersion,
            "init",
            config.ModStateDirectory,
            sources.Count,
            writtenCount,
            plugins,
            issues);

        LogAndWriteReport(config, log, report);
        return report;
    }

    public static ModStateStoreReport Dump(RuntimeConfig config, PatchPlan patchPlan, LauncherLog log, string? pluginIdFilter)
    {
        var issues = new List<ModStateIssue>();
        var sources = SelectStateSources(patchPlan, pluginIdFilter, issues);
        var fileNames = BuildStateFileNames(sources);
        var plugins = new List<ModStatePluginReport>();

        foreach (var source in sources)
        {
            var statePath = Path.Combine(config.ModStateDirectory, fileNames[source]);
            if (!File.Exists(statePath))
            {
                plugins.Add(CreatePluginReport(source, statePath, "missing", [], [], null));
                continue;
            }

            if (!TryReadStateRoot(statePath, source, issues, out var root, out _))
            {
                plugins.Add(CreatePluginReport(source, statePath, "invalid", [], [], null));
                continue;
            }

            var state = root["state"] as JsonObject;
            if (state is null)
            {
                issues.Add(new ModStateIssue(
                    "error",
                    "invalid-state",
                    source.PluginId,
                    statePath,
                    "state file exists but root.state is not an object"));
                plugins.Add(CreatePluginReport(source, statePath, "invalid", [], [], null));
                continue;
            }

            plugins.Add(CreatePluginReport(source, statePath, "loaded", [], StateKeys(state), CloneNode(state)));
        }

        var report = new ModStateStoreReport(
            ReportVersion,
            "dump",
            config.ModStateDirectory,
            sources.Count,
            0,
            plugins,
            issues);

        LogAndWriteReport(config, log, report);
        return report;
    }

    public static PluginStateSchemaSource? FindStateSchemaSource(PatchPlan patchPlan, string pluginId, string sourcePath)
    {
        return patchPlan.StateSchemas.FirstOrDefault(source =>
            source.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase) &&
            source.SourcePath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryOpenStateDocument(
        RuntimeConfig config,
        PatchPlan patchPlan,
        PluginStateSchemaSource source,
        List<ModStateIssue> issues,
        out ModStateDocument? document)
    {
        var statePath = GetStatePath(config, patchPlan, source);
        if (!TryReadStateRoot(statePath, source, issues, out var root, out _))
        {
            document = null;
            return false;
        }

        var state = GetOrCreateStateObject(root, source, statePath, issues);
        if (state is null)
        {
            document = null;
            return false;
        }

        if (!ReferenceEquals(root["state"], state))
        {
            root["state"] = state;
        }

        document = new ModStateDocument(source, statePath, root, state);
        return true;
    }

    public static ModStateWriteResult SaveStateDocument(ModStateDocument document, bool allowNonAtomicFallback, List<ModStateIssue> issues)
    {
        document.Root["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        return WriteJsonAtomic(document.StatePath, document.Root, document.Source, issues, allowNonAtomicFallback);
    }

    public static string GetStatePath(RuntimeConfig config, PatchPlan patchPlan, PluginStateSchemaSource source)
    {
        var fileNames = BuildStateFileNames(patchPlan.StateSchemas);
        if (!fileNames.TryGetValue(source, out var fileName))
        {
            throw new InvalidOperationException($"No state file name was generated for plugin state source: {source.PluginId}");
        }

        return Path.Combine(config.ModStateDirectory, fileName);
    }

    private static List<PluginStateSchemaSource> SelectStateSources(PatchPlan patchPlan, string? pluginIdFilter, List<ModStateIssue> issues)
    {
        var sources = patchPlan.StateSchemas
            .Where(source => string.IsNullOrWhiteSpace(pluginIdFilter) || source.PluginId.Equals(pluginIdFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(source => source.LoadOrder)
            .ThenBy(source => source.PluginId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(pluginIdFilter) && sources.Count == 0)
        {
            issues.Add(new ModStateIssue(
                "error",
                "state-schema-not-found",
                pluginIdFilter,
                string.Empty,
                "no enabled plugin stateSchema matched the requested plugin id"));
        }

        return sources;
    }

    private static Dictionary<PluginStateSchemaSource, string> BuildStateFileNames(IReadOnlyList<PluginStateSchemaSource> sources)
    {
        var duplicateIds = sources
            .GroupBy(source => source.PluginId.Trim().ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<PluginStateSchemaSource, string>();
        foreach (var source in sources)
        {
            var fileName = SanitizeFileName(source.PluginId);
            if (duplicateIds.Contains(source.PluginId.Trim().ToLowerInvariant()))
            {
                fileName += "." + ShortHash(source.SourcePath);
            }

            result[source] = fileName + ".json";
        }

        return result;
    }

    private static bool TryReadStateRoot(
        string statePath,
        PluginStateSchemaSource source,
        List<ModStateIssue> issues,
        out JsonObject root,
        out bool created)
    {
        created = false;
        root = [];
        if (!File.Exists(statePath))
        {
            created = true;
            return true;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(statePath, Encoding.UTF8));
            if (node is JsonObject obj)
            {
                root = obj;
                return true;
            }

            issues.Add(new ModStateIssue("error", "invalid-json-root", source.PluginId, statePath, "state file root must be a JSON object"));
            return false;
        }
        catch (JsonException ex)
        {
            issues.Add(new ModStateIssue("error", "invalid-json", source.PluginId, statePath, ex.Message));
            return false;
        }
    }

    private static JsonObject? GetOrCreateStateObject(JsonObject root, PluginStateSchemaSource source, string statePath, List<ModStateIssue> issues)
    {
        if (!root.ContainsKey("state"))
        {
            return [];
        }

        if (root["state"] is JsonObject state)
        {
            return state;
        }

        issues.Add(new ModStateIssue(
            "error",
            "invalid-state",
            source.PluginId,
            statePath,
            "state file exists but root.state is not an object"));
        return null;
    }

    private static JsonNode? BuildDefaultValue(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (schema.TryGetProperty("default", out var defaultValue))
        {
            return CloneElement(defaultValue);
        }

        if (schema.TryGetProperty("type", out var typeValue))
        {
            foreach (var type in SchemaTypes(typeValue))
            {
                switch (type)
                {
                    case "object":
                        return new JsonObject();
                    case "array":
                        return new JsonArray();
                    case "boolean":
                        return JsonValue.Create(false);
                    case "integer":
                        return JsonValue.Create(0);
                    case "number":
                        return JsonValue.Create(0.0);
                    case "string":
                        return JsonValue.Create(string.Empty);
                    case "null":
                        return null;
                }
            }
        }

        if (schema.TryGetProperty("properties", out _))
        {
            return new JsonObject();
        }

        if (schema.TryGetProperty("items", out _))
        {
            return new JsonArray();
        }

        return null;
    }

    private static IEnumerable<string> SchemaTypes(JsonElement typeValue)
    {
        if (typeValue.ValueKind == JsonValueKind.String)
        {
            var type = typeValue.GetString();
            if (!string.IsNullOrWhiteSpace(type))
            {
                yield return type.Trim().ToLowerInvariant();
            }
        }
        else if (typeValue.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in typeValue.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var type = item.GetString();
                if (!string.IsNullOrWhiteSpace(type))
                {
                    yield return type.Trim().ToLowerInvariant();
                }
            }
        }
    }

    private static JsonNode? CloneElement(JsonElement element)
    {
        return JsonNode.Parse(element.GetRawText());
    }

    private static JsonNode? CloneNode(JsonNode? node)
    {
        return node is null ? null : JsonNode.Parse(node.ToJsonString());
    }

    private static bool EnsureString(JsonObject root, string key, string value, bool overwrite = true)
    {
        if (root[key] is JsonValue existing && existing.TryGetValue<string>(out var current))
        {
            if (!overwrite || current == value)
            {
                return false;
            }
        }
        else if (!overwrite && root.ContainsKey(key))
        {
            return false;
        }

        root[key] = value;
        return true;
    }

    private static bool EnsureNumber(JsonObject root, string key, int value)
    {
        if (root[key] is JsonValue existing && existing.TryGetValue<int>(out var current) && current == value)
        {
            return false;
        }

        root[key] = value;
        return true;
    }

    private static string[] StateKeys(JsonObject state)
    {
        return state.Select(pair => pair.Key).OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static ModStatePluginReport CreatePluginReport(
        PluginStateSchemaSource source,
        string statePath,
        string status,
        IReadOnlyList<string> addedKeys,
        IReadOnlyList<string> stateKeys,
        JsonNode? state,
        string writeMode = "none")
    {
        return new ModStatePluginReport(
            source.PluginId,
            source.SourceName,
            source.SourcePath,
            source.LoadOrder,
            statePath,
            status,
            writeMode,
            source.StateSchema.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray(),
            addedKeys,
            stateKeys,
            state);
    }

    private static ModStateWriteResult WriteJsonAtomic(
        string path,
        JsonObject root,
        PluginStateSchemaSource source,
        List<ModStateIssue> issues,
        bool allowNonAtomicFallback)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var json = root.ToJsonString(JsonOptions);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json, Encoding.UTF8);
        try
        {
            File.Move(tempPath, path, overwrite: true);
            return new ModStateWriteResult(true, "atomic");
        }
        catch (UnauthorizedAccessException ex)
        {
            if (!allowNonAtomicFallback)
            {
                issues.Add(new ModStateIssue(
                    "error",
                    "state-atomic-write-failed",
                    source.PluginId,
                    path,
                    $"atomic state write failed and non-atomic fallback is disabled: {ex.Message}"));
                TryDeleteTempFile(tempPath, source, issues);
                return new ModStateWriteResult(false, "failed");
            }

            File.WriteAllText(path, json, Encoding.UTF8);
            issues.Add(new ModStateIssue(
                "warning",
                "state-write-fallback-non-atomic",
                source.PluginId,
                path,
                $"atomic state write failed; explicit non-atomic fallback wrote the target file: {ex.Message}"));
            TryDeleteTempFile(tempPath, source, issues);
            return new ModStateWriteResult(true, "non-atomic-fallback");
        }
        catch (IOException ex)
        {
            issues.Add(new ModStateIssue(
                "error",
                "state-atomic-write-failed",
                source.PluginId,
                path,
                $"atomic state write failed: {ex.Message}"));
            TryDeleteTempFile(tempPath, source, issues);
            return new ModStateWriteResult(false, "failed");
        }
    }

    private static void TryDeleteTempFile(string tempPath, PluginStateSchemaSource source, List<ModStateIssue> issues)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            issues.Add(new ModStateIssue(
                "warning",
                "state-temp-cleanup-failed",
                source.PluginId,
                tempPath,
                ex.Message));
        }
        catch (IOException ex)
        {
            issues.Add(new ModStateIssue(
                "warning",
                "state-temp-cleanup-failed",
                source.PluginId,
                tempPath,
                ex.Message));
        }
    }

    private static void LogAndWriteReport(RuntimeConfig config, LauncherLog log, ModStateStoreReport report)
    {
        log.Info(
            $"mod-state mode={report.Mode} directory={report.StateDirectory} " +
            $"plugins={report.PluginCount} written={report.WrittenCount} issues={report.Issues.Count}");

        foreach (var plugin in report.Plugins)
        {
            log.Info(
                $"mod-state-plugin status={plugin.Status} id={plugin.PluginId} order={plugin.LoadOrder} " +
                $"writeMode={plugin.WriteMode} schemaKeys={FormatLogList(plugin.SchemaKeys)} " +
                $"stateKeys={FormatLogList(plugin.StateKeys)} path={plugin.StatePath}");
        }

        foreach (var issue in report.Issues)
        {
            var line =
                $"mod-state-issue severity={issue.Severity} code={issue.Code} plugin={issue.PluginId} " +
                $"path={issue.Path} message={QuoteLogValue(issue.Message)}";
            if (issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase))
            {
                log.Error(line);
            }
            else
            {
                log.Warn(line);
            }
        }

        var reportPath = Path.Combine(config.LogDirectory, $"mod_state_{report.Mode}_report.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        log.Info($"mod-state-report path={reportPath}");
    }

    private static string SanitizeFileName(string value)
    {
        var builder = new StringBuilder();
        foreach (var ch in value.Trim())
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_');
        }

        var result = builder.ToString().Trim('.', '_', '-');
        return string.IsNullOrWhiteSpace(result) ? "plugin" : result;
    }

    private static string ShortHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    private static string QuoteLogValue(string value)
    {
        return string.IsNullOrEmpty(value) ? "\"\"" : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string FormatLogList(IEnumerable<string> values)
    {
        var list = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return list.Length == 0 ? "[]" : "[" + string.Join(",", list) + "]";
    }
}

internal sealed record ModStateStoreReport(
    int Version,
    string Mode,
    string StateDirectory,
    int PluginCount,
    int WrittenCount,
    IReadOnlyList<ModStatePluginReport> Plugins,
    IReadOnlyList<ModStateIssue> Issues)
{
    public bool Succeeded => Issues.All(issue => !issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
}

internal sealed record ModStatePluginReport(
    string PluginId,
    string SourceName,
    string SourcePath,
    int LoadOrder,
    string StatePath,
    string Status,
    string WriteMode,
    IReadOnlyList<string> SchemaKeys,
    IReadOnlyList<string> AddedKeys,
    IReadOnlyList<string> StateKeys,
    JsonNode? State);

internal sealed record ModStateDocument(
    PluginStateSchemaSource Source,
    string StatePath,
    JsonObject Root,
    JsonObject State);

internal sealed record ModStateIssue(
    string Severity,
    string Code,
    string PluginId,
    string Path,
    string Message);

internal sealed record ModStateWriteResult(
    bool Succeeded,
    string Mode);
