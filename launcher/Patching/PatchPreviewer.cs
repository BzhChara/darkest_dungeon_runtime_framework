namespace DDRuntimeLoader;

internal static class PatchPreviewer
{
    public static void WritePreview(RuntimeConfig config, PatchPlan patchPlan, string outputDirectory, LauncherLog log)
        => WritePreview(config, patchPlan.EffectiveVirtualFileRules, outputDirectory, log);

    public static void WritePreview(RuntimeConfig config, IReadOnlyList<VirtualFileRule> rules, string outputDirectory, LauncherLog log)
    {
        Directory.CreateDirectory(outputDirectory);
        var results = new List<PatchPreviewResult>();

        log.Info($"Patch preview started. output={outputDirectory} effectiveRules={rules.Count}");
        foreach (var rule in rules)
        {
            var result = PreviewRule(config, rule, log);
            results.Add(result);
            WritePreviewFiles(outputDirectory, result);
        }

        WriteSummary(outputDirectory, results);
        log.Info($"Patch preview completed. targets={results.Count} output={outputDirectory}");
    }

    private static PatchPreviewResult PreviewRule(RuntimeConfig config, VirtualFileRule rule, LauncherLog log)
    {
        var targetPath = config.ResolveVirtualTargetPath(rule.Target);
        if (!IsInsideDirectory(config.GameWorkingDirectory, targetPath))
        {
            throw new InvalidOperationException($"Patch preview target resolves outside game working directory: {rule.Target} -> {targetPath}");
        }

        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException("Patch preview target file was not found.", targetPath);
        }

        var originalText = File.ReadAllText(targetPath, Encoding.UTF8);
        var currentText = originalText;
        var applications = new List<PatchReplacementApplication>();
        var replacementsApplied = 0;

        for (var replacementIndex = 0; replacementIndex < rule.Replacements.Length; replacementIndex++)
        {
            var replacement = rule.Replacements[replacementIndex];
            var matches = CountOccurrences(currentText, replacement.Find);
            var firstLine = matches == 0 ? 0 : LineNumberOf(currentText, replacement.Find);
            var before = matches == 0 ? string.Empty : LineContaining(currentText, replacement.Find);
            currentText = ReplaceAll(currentText, replacement.Find, replacement.Replace, out var applied);
            replacementsApplied += applied;
            var after = applied == 0 ? string.Empty : FirstReplacementLine(replacement.Replace);

            applications.Add(new PatchReplacementApplication(
                replacement.Origin ?? PatchReplacementOrigin.Unknown,
                replacementIndex,
                applied,
                firstLine,
                before,
                after));
        }

        var warnings = BuildConflictWarnings(rule.Target, applications);
        foreach (var warning in warnings)
        {
            log.Warn(warning);
        }

        log.Info(
            $"patch-preview target={rule.Target} originalBytes={Encoding.UTF8.GetByteCount(originalText)} " +
            $"virtualBytes={Encoding.UTF8.GetByteCount(currentText)} replacements={replacementsApplied}");

        return new PatchPreviewResult(
            rule.Target,
            targetPath,
            originalText,
            currentText,
            Encoding.UTF8.GetByteCount(originalText),
            Encoding.UTF8.GetByteCount(currentText),
            rule.Replacements.Length,
            replacementsApplied,
            applications,
            warnings);
    }

    private static void WritePreviewFiles(string outputDirectory, PatchPreviewResult result)
    {
        var name = SafeFileName(result.Target);
        File.WriteAllText(Path.Combine(outputDirectory, name + ".preview.txt"), result.VirtualText, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outputDirectory, name + ".diff.txt"), BuildDiff(result), new UTF8Encoding(false));
    }

    private static void WriteSummary(string outputDirectory, IReadOnlyList<PatchPreviewResult> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Patch Preview Summary");
        builder.AppendLine("=====================");
        builder.AppendLine();

        foreach (var result in results)
        {
            builder.AppendLine($"Target: {result.Target}");
            builder.AppendLine($"Path: {result.TargetPath}");
            builder.AppendLine($"Original bytes: {result.OriginalBytes}");
            builder.AppendLine($"Virtual bytes: {result.VirtualBytes}");
            builder.AppendLine($"Replacement attempts: {result.ReplacementAttempts}");
            builder.AppendLine($"Replacements applied: {result.ReplacementsApplied}");
            foreach (var warning in result.Warnings)
            {
                builder.AppendLine($"Warning: {warning}");
            }
            builder.AppendLine();
        }

        File.WriteAllText(Path.Combine(outputDirectory, "summary.txt"), builder.ToString(), new UTF8Encoding(false));
    }

    private static string BuildDiff(PatchPreviewResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Target: {result.Target}");
        builder.AppendLine($"Path: {result.TargetPath}");
        builder.AppendLine($"Original bytes: {result.OriginalBytes}");
        builder.AppendLine($"Virtual bytes: {result.VirtualBytes}");
        builder.AppendLine($"Replacements applied: {result.ReplacementsApplied}");
        foreach (var warning in result.Warnings)
        {
            builder.AppendLine($"Warning: {warning}");
        }
        builder.AppendLine();

        foreach (var application in result.Applications)
        {
            var origin = application.Origin;
            builder.AppendLine(
                $"@@ replacement={application.ReplacementIndex} line={application.FirstLine} matches={application.Matches} " +
                $"source={origin.SourceName} rule={origin.RuleIndex} operation={origin.OperationIndex} " +
                $"type={origin.OperationType} subject={QuoteLogValue(origin.Subject)}");
            if (application.Matches == 0)
            {
                builder.AppendLine("! no match at preview time");
            }
            else
            {
                builder.AppendLine("- " + application.Before);
                builder.AppendLine("+ " + application.After);
            }
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> BuildConflictWarnings(string target, IReadOnlyList<PatchReplacementApplication> applications)
    {
        var warnings = applications
            .Where(application => application.Matches > 0 && application.FirstLine > 0)
            .GroupBy(application => application.FirstLine)
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                var sources = string.Join(", ", group.Select(application => application.Origin.SourceName).Distinct(StringComparer.OrdinalIgnoreCase));
                return $"patch-preview-conflict target={target} line={group.Key} replacements={group.Count()} sources={sources}";
            })
            .ToList();

        warnings.AddRange(applications
            .Where(application => application.Matches > 0 && IsKeySubject(application.Origin.Subject))
            .GroupBy(application => application.Origin.Subject, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                var sources = string.Join(", ", group.Select(application => application.Origin.SourceName).Distinct(StringComparer.OrdinalIgnoreCase));
                return $"patch-preview-key-conflict target={target} subject={group.Key} replacements={group.Count()} sources={sources}";
            }));

        return warnings.ToArray();
    }

    private static bool IsKeySubject(string subject)
    {
        return subject.StartsWith("key:", StringComparison.OrdinalIgnoreCase) && subject.Length > "key:".Length;
    }

    private static bool IsInsideDirectory(string directory, string path)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReplaceAll(string text, string find, string replace, out int replacements)
    {
        replacements = 0;
        if (find.Length == 0)
        {
            return text;
        }

        var position = 0;
        while ((position = text.IndexOf(find, position, StringComparison.Ordinal)) >= 0)
        {
            text = text.Remove(position, find.Length).Insert(position, replace);
            position += replace.Length;
            replacements++;
        }

        return text;
    }

    private static int CountOccurrences(string text, string find)
    {
        if (find.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var position = 0;
        while ((position = text.IndexOf(find, position, StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += find.Length;
        }

        return count;
    }

    private static int LineNumberOf(string text, string find)
    {
        var position = text.IndexOf(find, StringComparison.Ordinal);
        if (position < 0)
        {
            return 0;
        }

        var line = 1;
        for (var i = 0; i < position; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static string LineContaining(string text, string find)
    {
        var position = text.IndexOf(find, StringComparison.Ordinal);
        if (position < 0)
        {
            return string.Empty;
        }

        var start = text.LastIndexOf('\n', position);
        start = start < 0 ? 0 : start + 1;
        var end = text.IndexOf('\n', position);
        if (end < 0)
        {
            end = text.Length;
        }

        return text[start..end].TrimEnd('\r');
    }

    private static string FirstReplacementLine(string replacement)
    {
        var end = replacement.IndexOf('\n', StringComparison.Ordinal);
        var line = end < 0 ? replacement : replacement[..end];
        return line.TrimEnd('\r');
    }

    private static string QuoteLogValue(string value)
    {
        return string.IsNullOrEmpty(value) ? "\"\"" : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string SafeFileName(string target)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(target.Length);
        foreach (var ch in target.Replace('\\', '_').Replace('/', '_'))
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.Length == 0 ? "patch" : builder.ToString();
    }
}
