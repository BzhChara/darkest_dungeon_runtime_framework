using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static class QuestBoardRuntimeOverlayCompiler
{
    private const int ReportVersion = 1;
    private const string PersistQuestFileName = "persist.quest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static QuestBoardRuntimeOverlayReport Compile(
        RuntimeConfig config,
        LauncherLog log,
        QuestBoardPreviewReport preview)
    {
        var outputDirectory = Path.Combine(config.ModStateDirectory, "_managed_action_overlays", "quest_board_replace_fixed_set");
        var reportPath = Path.Combine(config.LogDirectory, "quest_board_runtime_overlay_report.json");
        var profileReports = new List<QuestBoardRuntimeOverlayProfileReport>();
        var issues = new List<QuestBoardRuntimeOverlayIssue>();
        var virtualRules = new List<VirtualFileRule>();

        if (preview.ErrorCount > 0)
        {
            issues.Add(new QuestBoardRuntimeOverlayIssue(
                "error",
                "quest-board-preview-has-errors",
                preview.ReportPath,
                $"quest board preview reported {preview.ErrorCount} error(s); runtime overlay was not compiled"));
        }
        else if (preview.FinalActiveQuestCount > 0)
        {
            Directory.CreateDirectory(outputDirectory);
            var activeQuestIds = preview.FinalActiveQuests
                .OrderBy(quest => quest.Order)
                .Select(quest => quest.QuestId)
                .Where(questId => !string.IsNullOrWhiteSpace(questId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var definitions = QuestBoardContentCatalog.LoadEnabledPlotQuestDefinitions(config.GameWorkingDirectory);
            var replacement = QuestBoardFixedSetResolver.BuildQuestEntries(activeQuestIds, definitions);
            var sources = EnumeratePersistQuestSources(config).ToArray();
            if (sources.Length == 0)
            {
                issues.Add(new QuestBoardRuntimeOverlayIssue(
                    "warning",
                    "quest-board-runtime-no-save-sources",
                    string.Join(';', config.SaveWatchDirectories),
                    "no profile_*/persist.quest.json sources were found under saveWatchDirectories; runtime quest-board overlay cannot be generated"));
            }

            foreach (var source in sources)
            {
                try
                {
                    var profileOutputPrefix = Path.Combine(outputDirectory, SafeFileName(source.ProfileId));
                    var result = CompileProfileOverlay(config, source, replacement, profileOutputPrefix);
                    var summary = new QuestBoardRuntimeOverlayProfileReport(
                        source.ProfileId,
                        source.Target,
                        source.SourcePath,
                        result.DecodedSourcePath,
                        result.RuntimeSourcePath,
                        result.SourceFormat,
                        result.RuntimeSourceFormat,
                        activeQuestIds.Length,
                        result.Changed,
                        "ready",
                        []);
                    profileReports.Add(summary);
                    virtualRules.Add(new VirtualFileRule
                    {
                        Target = source.Target,
                        SourcePath = result.RuntimeSourcePath
                    });
                    log.Info(
                        $"quest-board-runtime-overlay virtual-rule profile={source.ProfileId} " +
                        $"target={Quote(source.Target)} sourcePath={Quote(result.RuntimeSourcePath)} " +
                        $"sourceFormat={result.SourceFormat} runtimeFormat={result.RuntimeSourceFormat} quests={activeQuestIds.Length}");
                }
                catch (Exception ex)
                {
                    issues.Add(new QuestBoardRuntimeOverlayIssue(
                        "warning",
                        "quest-board-runtime-profile-overlay-failed",
                        source.SourcePath,
                        ex.Message));
                    profileReports.Add(new QuestBoardRuntimeOverlayProfileReport(
                        source.ProfileId,
                        source.Target,
                        source.SourcePath,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        activeQuestIds.Length,
                        false,
                        "failed",
                        [ex.Message]));
                    log.Warn(
                        $"quest-board-runtime-overlay issue code=quest-board-runtime-profile-overlay-failed " +
                        $"profile={source.ProfileId} path={Quote(source.SourcePath)} message={Quote(ex.Message)}");
                }
            }
        }

        var status = BuildStatus(preview, virtualRules.Count, issues);
        var report = new QuestBoardRuntimeOverlayReport(
            ReportVersion,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            reportPath,
            status,
            preview.ReportPath,
            preview.FinalActiveQuestCount,
            profileReports.Count,
            virtualRules.Count,
            profileReports,
            issues.Count(issue => issue.Severity == "warning"),
            issues.Count(issue => issue.Severity == "error"),
            issues,
            virtualRules);

        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), Utf8NoBom);
        log.Info(
            $"quest-board-runtime-overlay report path={Quote(reportPath)} status={report.Status} " +
            $"candidateQuests={report.CandidateQuestCount} profiles={report.ProfileCount} " +
            $"virtualRules={report.VirtualFileRuleCount} warnings={report.WarningCount} errors={report.ErrorCount}");
        return report;
    }

    public static void ApplyEnvironment(Dictionary<string, string> values, QuestBoardRuntimeOverlayReport report)
    {
        if (report.VirtualFileRules.Count == 0)
        {
            return;
        }

        if (!values.TryGetValue("DD_RUNTIME_VIRTUAL_RULE_COUNT", out var countText) ||
            !int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset) ||
            offset < 0)
        {
            offset = 0;
        }

        for (var i = 0; i < report.VirtualFileRules.Count; i++)
        {
            var ruleIndex = offset + i;
            var rule = report.VirtualFileRules[i];
            values[$"DD_RUNTIME_VIRTUAL_RULE_{ruleIndex}_TARGET"] = rule.Target;
            values[$"DD_RUNTIME_VIRTUAL_RULE_{ruleIndex}_SOURCE_PATH"] = rule.SourcePath;
            values[$"DD_RUNTIME_VIRTUAL_RULE_{ruleIndex}_REPLACEMENT_COUNT"] =
                rule.Replacements.Length.ToString(CultureInfo.InvariantCulture);
        }

        values["DD_RUNTIME_VIRTUAL_RULE_COUNT"] = (offset + report.VirtualFileRules.Count).ToString(CultureInfo.InvariantCulture);
    }

    private static QuestBoardRuntimeProfileOverlayResult CompileProfileOverlay(
        RuntimeConfig config,
        PersistQuestSource source,
        JsonObject replacement,
        string outputPrefix)
    {
        var decodedOutputPath = outputPrefix + ".decoded.persist.quest.json";
        var runtimeOutputPath = outputPrefix + ".persist.quest.json";
        var readResult = ReadPersistQuestRoot(config, source.SourcePath, decodedOutputPath);
        var root = readResult.Root;
        var baseRoot = EnsureObject(root, "base_root");
        var existingQuests = baseRoot["quests"] as JsonObject ?? new JsonObject();
        var changed = !JsonNode.DeepEquals(existingQuests, replacement);
        baseRoot["quests"] = CloneObject(replacement);

        Directory.CreateDirectory(Path.GetDirectoryName(decodedOutputPath) ?? ".");
        File.WriteAllText(decodedOutputPath, root.ToJsonString(JsonOptions), Utf8NoBom);

        if (readResult.SourceFormat.Equals("dson", StringComparison.OrdinalIgnoreCase))
        {
            EncodeDson(config, decodedOutputPath, runtimeOutputPath);
            ValidateDsonOutput(runtimeOutputPath);
            return new QuestBoardRuntimeProfileOverlayResult(
                decodedOutputPath,
                runtimeOutputPath,
                readResult.SourceFormat,
                "dson",
                changed);
        }

        File.Copy(decodedOutputPath, runtimeOutputPath, overwrite: true);
        return new QuestBoardRuntimeProfileOverlayResult(
            decodedOutputPath,
            runtimeOutputPath,
            readResult.SourceFormat,
            "json",
            changed);
    }

    private static QuestBoardRuntimeReadResult ReadPersistQuestRoot(
        RuntimeConfig config,
        string sourcePath,
        string decodedOutputPath)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        if (LooksLikeJsonText(bytes))
        {
            var root = JsonNode.Parse(DecodeUtf8JsonText(bytes)) as JsonObject
                ?? throw new InvalidDataException($"persist quest source root must be a JSON object: {sourcePath}");
            return new QuestBoardRuntimeReadResult(root, "json");
        }

        DecodeDson(config, sourcePath, decodedOutputPath);
        var decodedRoot = JsonNode.Parse(File.ReadAllText(decodedOutputPath, Encoding.UTF8)) as JsonObject
            ?? throw new InvalidDataException($"decoded persist quest root must be a JSON object: {decodedOutputPath}");
        return new QuestBoardRuntimeReadResult(decodedRoot, "dson");
    }

    private static void DecodeDson(RuntimeConfig config, string inputPath, string outputPath)
    {
        EnsureDsonCodecAvailable(config);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        RunJava(
            "decode",
            [
                "-jar",
                config.DsonSaveEditorJarPath,
                "decode",
                "--output",
                outputPath,
                inputPath
            ]);
    }

    private static void EncodeDson(RuntimeConfig config, string inputPath, string outputPath)
    {
        EnsureDsonCodecAvailable(config);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        RunJava(
            "encode",
            [
                "-jar",
                config.DsonSaveEditorJarPath,
                "encode",
                "--output",
                outputPath,
                inputPath
            ]);
    }

    private static void EnsureDsonCodecAvailable(RuntimeConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.DsonSaveEditorJarPath))
        {
            throw new FileNotFoundException("dsonSaveEditorJarPath is empty; binary DSON persist.quest.json cannot be encoded for runtime overlay.");
        }

        if (!File.Exists(config.DsonSaveEditorJarPath))
        {
            throw new FileNotFoundException("DDSaveEditor jar was not found; binary DSON persist.quest.json cannot be encoded for runtime overlay.", config.DsonSaveEditorJarPath);
        }
    }

    private static void RunJava(string operation, string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "java",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"DDSaveEditor {operation} failed with exit code {process.ExitCode}: {stderr.Trim()} {stdout.Trim()}".Trim());
        }
    }

    private static void ValidateDsonOutput(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 4 || bytes[0] != 0x01 || bytes[1] != 0xB1 || bytes[2] != 0x00 || bytes[3] != 0x00)
        {
            throw new InvalidDataException($"encoded DSON output did not start with the expected Darkest Dungeon save magic: {path}");
        }
    }

    private static IEnumerable<PersistQuestSource> EnumeratePersistQuestSources(RuntimeConfig config)
    {
        var byTarget = new Dictionary<string, PersistQuestSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in config.SaveWatchDirectories)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            AddProfileDirectory(root, byTarget);
            foreach (var profileDirectory in Directory.EnumerateDirectories(root, "profile_*", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                AddProfileDirectory(profileDirectory, byTarget);
            }
        }

        return byTarget.Values
            .OrderBy(source => source.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddProfileDirectory(string directory, Dictionary<string, PersistQuestSource> byTarget)
    {
        var profileId = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(profileId) ||
            !profileId.StartsWith("profile_", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var sourcePath = Path.Combine(directory, PersistQuestFileName);
        if (!File.Exists(sourcePath))
        {
            return;
        }

        var target = $"{profileId}/{PersistQuestFileName}";
        byTarget[target] = new PersistQuestSource(profileId, target, sourcePath);
    }

    private static string BuildStatus(
        QuestBoardPreviewReport preview,
        int virtualRuleCount,
        IReadOnlyList<QuestBoardRuntimeOverlayIssue> issues)
    {
        if (preview.ErrorCount > 0)
        {
            return "blocked";
        }

        if (preview.FinalActiveQuestCount == 0)
        {
            return "none";
        }

        if (virtualRuleCount == 0)
        {
            return "unavailable";
        }

        return issues.Any(issue => issue.Severity == "warning") ? "partial" : "ready";
    }

    private static bool LooksLikeJsonText(byte[] bytes)
    {
        var index = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            index = 3;
        }

        while (index < bytes.Length && bytes[index] is 0x09 or 0x0A or 0x0D or 0x20)
        {
            index++;
        }

        return index < bytes.Length && bytes[index] is (byte)'{' or (byte)'[';
    }

    private static string DecodeUtf8JsonText(byte[] bytes)
    {
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? 3
            : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
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

    private static JsonObject CloneObject(JsonObject value)
    {
        return JsonNode.Parse(value.ToJsonString(JsonOptions)) as JsonObject
            ?? throw new InvalidDataException("Expected cloned JSON node to be an object.");
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) || ch is '/' or '\\' ? '_' : ch);
        }

        return builder.ToString();
    }

    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';

    private sealed record PersistQuestSource(
        string ProfileId,
        string Target,
        string SourcePath);

    private sealed record QuestBoardRuntimeReadResult(
        JsonObject Root,
        string SourceFormat);

    private sealed record QuestBoardRuntimeProfileOverlayResult(
        string DecodedSourcePath,
        string RuntimeSourcePath,
        string SourceFormat,
        string RuntimeSourceFormat,
        bool Changed);
}

internal sealed record QuestBoardRuntimeOverlayReport(
    int Version,
    string GeneratedAtUtc,
    string ReportPath,
    string Status,
    string QuestBoardPreviewReportPath,
    int CandidateQuestCount,
    int ProfileCount,
    int VirtualFileRuleCount,
    IReadOnlyList<QuestBoardRuntimeOverlayProfileReport> Profiles,
    int WarningCount,
    int ErrorCount,
    IReadOnlyList<QuestBoardRuntimeOverlayIssue> Issues,
    [property: JsonIgnore] IReadOnlyList<VirtualFileRule> VirtualFileRules)
{
    public bool Succeeded => ErrorCount == 0;
}

internal sealed record QuestBoardRuntimeOverlayProfileReport(
    string ProfileId,
    string Target,
    string SourcePath,
    string DecodedSourcePath,
    string RuntimeSourcePath,
    string SourceFormat,
    string RuntimeSourceFormat,
    int ActiveQuestCount,
    bool Changed,
    string Status,
    IReadOnlyList<string> Issues);

internal sealed record QuestBoardRuntimeOverlayIssue(
    string Severity,
    string Code,
    string Path,
    string Message);
