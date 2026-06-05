namespace DDRuntimeLoader;

internal sealed class PatchValidationResult
{
    public PatchValidationResult(int errorCount, int warningCount)
    {
        ErrorCount = errorCount;
        WarningCount = warningCount;
    }

    public int ErrorCount { get; }
    public int WarningCount { get; }
    public bool Succeeded => ErrorCount == 0;
}

internal static class PatchValidator
{
    private const long MaxVirtualFileBytes = 16 * 1024 * 1024;

    public static PatchValidationResult Validate(RuntimeConfig config, PatchPlan patchPlan, LauncherLog log, bool strictPatches)
    {
        var errors = 0;
        var warnings = 0;

        log.Info(
            $"Patch validation started. sourceRules={patchPlan.SourceVirtualFileRules.Count} " +
            $"effectiveRules={patchPlan.EffectiveVirtualFileRules.Count}");

        if (!config.VirtualFileEnabled && patchPlan.EffectiveVirtualFileRules.Count > 0)
        {
            warnings++;
            log.Warn("Virtual file rules exist, but virtualFileEnabled is false. These rules will not be applied.");
        }

        foreach (var group in patchPlan.SourceVirtualFileRules.GroupBy(rule => NormalizeTargetKey(rule.Rule.Target)))
        {
            var count = group.Count();
            if (count > 1)
            {
                warnings++;
                var sources = string.Join(", ", group.Select(rule => rule.SourceName).Distinct(StringComparer.OrdinalIgnoreCase));
                log.Warn($"Multiple enabled source rules target the same virtual file: target={group.Key} count={count} sources={sources}");
            }
        }

        if (patchPlan.EffectiveVirtualFileRules.Count == 0)
        {
            log.Info("Patch validation found no enabled virtual file rules.");
            log.Info("Patch validation completed. errors=0 warnings=" + warnings);
            return new PatchValidationResult(errors, warnings);
        }

        for (var ruleIndex = 0; ruleIndex < patchPlan.EffectiveVirtualFileRules.Count; ruleIndex++)
        {
            var rule = patchPlan.EffectiveVirtualFileRules[ruleIndex];
            var targetPath = config.ResolveVirtualTargetPath(rule.Target);
            if (!IsInsideDirectory(config.GameWorkingDirectory, targetPath))
            {
                errors++;
                log.Error($"Patch target resolves outside game working directory: target={rule.Target} path={targetPath}");
                continue;
            }

            if (!File.Exists(targetPath))
            {
                errors++;
                log.Error($"Patch target file was not found: target={rule.Target} path={targetPath}");
                continue;
            }

            var info = new FileInfo(targetPath);
            if (info.Length > MaxVirtualFileBytes)
            {
                errors++;
                log.Error($"Patch target exceeds runtime virtual file size limit: target={rule.Target} bytes={info.Length} limit={MaxVirtualFileBytes}");
                continue;
            }

            string currentText;
            try
            {
                currentText = File.ReadAllText(targetPath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                errors++;
                log.Error($"Patch target could not be read: target={rule.Target} path={targetPath}: {ex.Message}");
                continue;
            }

            for (var replacementIndex = 0; replacementIndex < rule.Replacements.Length; replacementIndex++)
            {
                var replacement = rule.Replacements[replacementIndex];
                var origin = replacement.Origin ?? PatchReplacementOrigin.Unknown;
                var matches = CountOccurrences(currentText, replacement.Find);
                log.Info(
                    $"patch-validate-match target={rule.Target} rule={ruleIndex} replacement={replacementIndex} " +
                    $"matches={matches} source={origin.SourceName} operation={origin.OperationIndex} " +
                    $"type={origin.OperationType} subject={QuoteLogValue(origin.Subject)} findChars={replacement.Find.Length}");

                if (matches == 0)
                {
                    if (strictPatches)
                    {
                        errors++;
                        log.Error($"Patch replacement text was not found: target={rule.Target} replacement={replacementIndex}");
                    }
                    else
                    {
                        warnings++;
                        log.Warn($"Patch replacement text was not found: target={rule.Target} replacement={replacementIndex}");
                    }
                }

                currentText = ReplaceAll(currentText, replacement.Find, replacement.Replace, out _);
            }
        }

        log.Info($"Patch validation completed. errors={errors} warnings={warnings}");
        return new PatchValidationResult(errors, warnings);
    }

    private static string NormalizeTargetKey(string target)
    {
        return target.Trim().Replace('\\', '/').ToLowerInvariant();
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

    private static string QuoteLogValue(string value)
    {
        return string.IsNullOrEmpty(value) ? "\"\"" : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
