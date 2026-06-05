namespace DDRuntimeLoader;

internal sealed partial class RuntimeConfig
{
    private static string ReplaceAllText(string text, string find, string replace, out int replacements)
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

    private static void AddVirtualRules(
        List<VirtualFileRuleSource> output,
        List<VirtualFileRuleSkip> skipped,
        IEnumerable<VirtualFileRule>? input,
        string sourceName,
        string sourcePath,
        IReadOnlySet<string> activePluginIds,
        IReadOnlySet<string> activeCapabilities)
    {
        var index = 0;
        foreach (var rule in input ?? [])
        {
            index++;
            if (string.IsNullOrWhiteSpace(rule.Target))
            {
                continue;
            }

            var hasReplacements = (rule.Replacements ?? []).Any(replacement => !string.IsNullOrEmpty(replacement.Find));
            var hasOperations = (rule.Operations ?? []).Length > 0;
            if (!hasReplacements && !hasOperations)
            {
                continue;
            }

            var condition = EvaluatePatchCondition(rule.When, activePluginIds, activeCapabilities);
            if (!condition.Matched)
            {
                skipped.Add(new VirtualFileRuleSkip(
                    sourceName,
                    sourcePath,
                    index,
                    rule.Target,
                    rule.Replacements?.Length ?? 0,
                    rule.Operations?.Length ?? 0,
                    condition.Reason));
                continue;
            }

            output.Add(new VirtualFileRuleSource(
                sourceName,
                sourcePath,
                index,
                new VirtualFileRule
                {
                    Target = rule.Target,
                    Replacements = rule.Replacements ?? [],
                    Operations = rule.Operations ?? [],
                    When = rule.When
                },
                condition.Reason));
        }
    }

    private static PatchConditionResult EvaluatePatchCondition(
        PatchCondition? condition,
        IReadOnlySet<string> activePluginIds,
        IReadOnlySet<string> activeCapabilities)
    {
        if (condition is null)
        {
            return new PatchConditionResult(true, "no condition");
        }

        var modsPresent = CleanModReferences(condition.ModsPresent).ToArray();
        var modsAbsent = CleanModReferences(condition.ModsAbsent).ToArray();
        var capabilitiesPresent = CleanCapabilityReferences(condition.CapabilitiesPresent).ToArray();
        var capabilitiesAbsent = CleanCapabilityReferences(condition.CapabilitiesAbsent).ToArray();
        if (modsPresent.Length == 0 && modsAbsent.Length == 0 && capabilitiesPresent.Length == 0 && capabilitiesAbsent.Length == 0)
        {
            return new PatchConditionResult(true, "empty condition");
        }

        var missingPresent = modsPresent
            .Where(modId => !activePluginIds.Contains(NormalizePluginId(modId)))
            .ToArray();
        if (missingPresent.Length > 0)
        {
            return new PatchConditionResult(false, "modsPresent missing: " + string.Join(",", missingPresent));
        }

        var presentAbsent = modsAbsent
            .Where(modId => activePluginIds.Contains(NormalizePluginId(modId)))
            .ToArray();
        if (presentAbsent.Length > 0)
        {
            return new PatchConditionResult(false, "modsAbsent present: " + string.Join(",", presentAbsent));
        }

        var missingCapabilities = capabilitiesPresent
            .Where(capability => !activeCapabilities.Contains(NormalizeCapability(capability)))
            .ToArray();
        if (missingCapabilities.Length > 0)
        {
            return new PatchConditionResult(false, "capabilitiesPresent missing: " + string.Join(",", missingCapabilities));
        }

        var presentCapabilities = capabilitiesAbsent
            .Where(capability => activeCapabilities.Contains(NormalizeCapability(capability)))
            .ToArray();
        if (presentCapabilities.Length > 0)
        {
            return new PatchConditionResult(false, "capabilitiesAbsent present: " + string.Join(",", presentCapabilities));
        }

        return new PatchConditionResult(true, "condition matched");
    }

    private List<VirtualFileReplacement> CompileVirtualFileOperations(
        VirtualFileRule rule,
        string currentText,
        string sourceName,
        string sourcePath,
        int ruleIndex,
        List<PatchCompileIssue> compileIssues,
        LauncherLog log,
        out string updatedText)
    {
        updatedText = currentText;
        var replacements = new List<VirtualFileReplacement>();
        var operations = rule.Operations ?? [];
        if (operations.Length == 0)
        {
            return replacements;
        }

        for (var operationIndex = 0; operationIndex < operations.Length; operationIndex++)
        {
            var operation = operations[operationIndex];
            var subject = DescribeOperationSubject(operation);
            var lines = SplitLinesPreserveEndings(updatedText);
            var preferredEol = lines.FirstOrDefault(line => line.Eol.Length > 0)?.Eol ?? "\n";
            var compiled = CompileOperation(operation, lines, preferredEol, sourceName, sourcePath, ruleIndex, operationIndex, rule.Target, compileIssues);
            for (var replacementIndex = 0; replacementIndex < compiled.Count; replacementIndex++)
            {
                compiled[replacementIndex] = WithOrigin(
                    compiled[replacementIndex],
                    new PatchReplacementOrigin(sourceName, sourcePath, ruleIndex, replacementIndex, operationIndex, operation.Type, subject));
                updatedText = ReplaceAllText(updatedText, compiled[replacementIndex].Find, compiled[replacementIndex].Replace, out var applied);
                if (applied == 0)
                {
                    AddCompileIssue(
                        compileIssues,
                        false,
                        sourceName,
                        sourcePath,
                        ruleIndex,
                        operationIndex,
                        rule.Target,
                        $"compiled operation replacement did not match current virtual text: type={operation.Type}");
                }
            }
            replacements.AddRange(compiled);

            log.Info(
                $"patch-operation-compiled source={sourceName} target={rule.Target} " +
                $"rule={ruleIndex} operation={operationIndex} type={operation.Type} subject={QuoteLogValue(subject)} replacements={compiled.Count}");
        }

        return replacements;
    }

    private static List<VirtualFileReplacement> CompileOperation(
        VirtualFileOperation operation,
        IReadOnlyList<TextLineSegment> lines,
        string preferredEol,
        string sourceName,
        string sourcePath,
        int ruleIndex,
        int operationIndex,
        string target,
        List<PatchCompileIssue> compileIssues)
    {
        var type = operation.Type.Trim();
        if (type.Equals("setValue", StringComparison.OrdinalIgnoreCase))
        {
            return CompileSetValue(operation, lines, sourceName, sourcePath, ruleIndex, operationIndex, target, compileIssues);
        }

        if (type.Equals("replaceLine", StringComparison.OrdinalIgnoreCase))
        {
            return CompileReplaceLine(operation, lines, sourceName, sourcePath, ruleIndex, operationIndex, target, compileIssues);
        }

        if (type.Equals("appendAfter", StringComparison.OrdinalIgnoreCase))
        {
            return CompileAppendAfter(operation, lines, sourceName, sourcePath, ruleIndex, operationIndex, target, compileIssues);
        }

        if (type.Equals("appendEnd", StringComparison.OrdinalIgnoreCase))
        {
            return CompileAppendEnd(operation, lines, preferredEol, sourceName, sourcePath, ruleIndex, operationIndex, target, compileIssues);
        }

        AddCompileIssue(compileIssues, true, sourceName, sourcePath, ruleIndex, operationIndex, target, $"unknown operation type: {operation.Type}");
        return [];
    }

    private static List<VirtualFileReplacement> CompileSetValue(
        VirtualFileOperation operation,
        IReadOnlyList<TextLineSegment> lines,
        string sourceName,
        string sourcePath,
        int ruleIndex,
        int operationIndex,
        string target,
        List<PatchCompileIssue> compileIssues)
    {
        if (string.IsNullOrWhiteSpace(operation.Key))
        {
            AddCompileIssue(compileIssues, true, sourceName, sourcePath, ruleIndex, operationIndex, target, "setValue requires key");
            return [];
        }

        var matches = lines.Where(line => LineHasKey(line.Text, operation.Key)).ToList();
        if (matches.Count == 0)
        {
            AddCompileIssue(compileIssues, false, sourceName, sourcePath, ruleIndex, operationIndex, target, $"setValue key was not found: {operation.Key}");
            return [];
        }

        return matches.Select(line => new VirtualFileReplacement
        {
            Find = line.Raw,
            Replace = LeadingWhitespace(line.Text) + operation.Key + " " + operation.Value + line.Eol
        }).ToList();
    }

    private static List<VirtualFileReplacement> CompileReplaceLine(
        VirtualFileOperation operation,
        IReadOnlyList<TextLineSegment> lines,
        string sourceName,
        string sourcePath,
        int ruleIndex,
        int operationIndex,
        string target,
        List<PatchCompileIssue> compileIssues)
    {
        if (string.IsNullOrEmpty(operation.Line))
        {
            AddCompileIssue(compileIssues, true, sourceName, sourcePath, ruleIndex, operationIndex, target, "replaceLine requires line");
            return [];
        }

        var matches = MatchLines(operation, lines).ToList();
        if (matches.Count == 0)
        {
            AddCompileIssue(compileIssues, false, sourceName, sourcePath, ruleIndex, operationIndex, target, "replaceLine did not match any line");
            return [];
        }

        return matches.Select(line => new VirtualFileReplacement
        {
            Find = line.Raw,
            Replace = operation.Line + line.Eol
        }).ToList();
    }

    private static List<VirtualFileReplacement> CompileAppendAfter(
        VirtualFileOperation operation,
        IReadOnlyList<TextLineSegment> lines,
        string sourceName,
        string sourcePath,
        int ruleIndex,
        int operationIndex,
        string target,
        List<PatchCompileIssue> compileIssues)
    {
        var content = OperationContent(operation);
        if (string.IsNullOrEmpty(content))
        {
            AddCompileIssue(compileIssues, true, sourceName, sourcePath, ruleIndex, operationIndex, target, "appendAfter requires content or text");
            return [];
        }

        var matches = MatchLines(operation, lines).ToList();
        if (matches.Count == 0)
        {
            AddCompileIssue(compileIssues, false, sourceName, sourcePath, ruleIndex, operationIndex, target, "appendAfter did not match any line");
            return [];
        }

        return matches.Select(line => new VirtualFileReplacement
        {
            Find = line.Raw,
            Replace = line.Raw + (line.Eol.Length == 0 ? "\n" : string.Empty) + EnsureTrailingEol(content, line.Eol.Length == 0 ? "\n" : line.Eol)
        }).ToList();
    }

    private static List<VirtualFileReplacement> CompileAppendEnd(
        VirtualFileOperation operation,
        IReadOnlyList<TextLineSegment> lines,
        string preferredEol,
        string sourceName,
        string sourcePath,
        int ruleIndex,
        int operationIndex,
        string target,
        List<PatchCompileIssue> compileIssues)
    {
        var content = OperationContent(operation);
        if (string.IsNullOrEmpty(content))
        {
            AddCompileIssue(compileIssues, true, sourceName, sourcePath, ruleIndex, operationIndex, target, "appendEnd requires content or text");
            return [];
        }

        var anchor = lines.LastOrDefault(line => line.Raw.Length > 0);
        if (anchor is null)
        {
            AddCompileIssue(compileIssues, false, sourceName, sourcePath, ruleIndex, operationIndex, target, "appendEnd cannot compile against an empty file");
            return [];
        }

        var separator = anchor.Eol.Length == 0 ? preferredEol : string.Empty;
        return
        [
            new VirtualFileReplacement
            {
                Find = anchor.Raw,
                Replace = anchor.Raw + separator + EnsureTrailingEol(content, preferredEol)
            }
        ];
    }

    private static IEnumerable<TextLineSegment> MatchLines(VirtualFileOperation operation, IReadOnlyList<TextLineSegment> lines)
    {
        if (!string.IsNullOrEmpty(operation.Match))
        {
            return lines.Where(line => line.Text == operation.Match);
        }

        if (!string.IsNullOrEmpty(operation.Prefix))
        {
            return lines.Where(line => line.Text.TrimStart().StartsWith(operation.Prefix, StringComparison.Ordinal));
        }

        return [];
    }

    private static string DescribeOperationSubject(VirtualFileOperation operation)
    {
        var type = operation.Type.Trim();
        if (type.Equals("setValue", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(operation.Key) ? "key:" : "key:" + operation.Key.Trim();
        }

        if (!string.IsNullOrWhiteSpace(operation.Key))
        {
            return "key:" + operation.Key.Trim();
        }

        if (!string.IsNullOrWhiteSpace(operation.Prefix) && TryReadDarkestKey(operation.Prefix, out var prefixKey))
        {
            return "key:" + prefixKey;
        }

        if (!string.IsNullOrWhiteSpace(operation.Match) && TryReadDarkestKey(operation.Match, out var matchKey))
        {
            return "key:" + matchKey;
        }

        if (!string.IsNullOrWhiteSpace(operation.Line) && TryReadDarkestKey(operation.Line, out var lineKey))
        {
            return "key:" + lineKey;
        }

        if (!string.IsNullOrEmpty(operation.Match))
        {
            return "match:" + operation.Match;
        }

        if (!string.IsNullOrEmpty(operation.Prefix))
        {
            return "prefix:" + operation.Prefix;
        }

        if (type.Equals("appendEnd", StringComparison.OrdinalIgnoreCase))
        {
            return "file:end";
        }

        return string.IsNullOrWhiteSpace(type) ? "operation" : "operation:" + type;
    }

    private static bool TryReadDarkestKey(string value, out string key)
    {
        var trimmed = value.TrimStart();
        if (!trimmed.StartsWith(".", StringComparison.Ordinal))
        {
            key = string.Empty;
            return false;
        }

        var length = 0;
        while (length < trimmed.Length && !char.IsWhiteSpace(trimmed[length]))
        {
            length++;
        }

        key = trimmed[..length];
        return key.Length > 1;
    }

    private static List<TextLineSegment> SplitLinesPreserveEndings(string text)
    {
        var lines = new List<TextLineSegment>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\r' && text[i] != '\n')
            {
                continue;
            }

            var eolLength = 1;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                eolLength = 2;
            }

            lines.Add(new TextLineSegment(text[start..i], text.Substring(i, eolLength)));
            i += eolLength - 1;
            start = i + 1;
        }

        if (start < text.Length)
        {
            lines.Add(new TextLineSegment(text[start..], string.Empty));
        }

        return lines;
    }

    private static bool LineHasKey(string line, string key)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith(key, StringComparison.Ordinal))
        {
            return false;
        }

        return trimmed.Length == key.Length || char.IsWhiteSpace(trimmed[key.Length]);
    }

    private static string LeadingWhitespace(string value)
    {
        var length = 0;
        while (length < value.Length && char.IsWhiteSpace(value[length]))
        {
            length++;
        }

        return value[..length];
    }

    private static string OperationContent(VirtualFileOperation operation)
    {
        return !string.IsNullOrEmpty(operation.Content) ? operation.Content : operation.Text;
    }

    private static string EnsureTrailingEol(string value, string eol)
    {
        return value.EndsWith("\n", StringComparison.Ordinal) || value.EndsWith("\r", StringComparison.Ordinal)
            ? value
            : value + eol;
    }

    private static void AddCompileIssue(
        List<PatchCompileIssue> compileIssues,
        bool isError,
        string sourceName,
        string sourcePath,
        int ruleIndex,
        int operationIndex,
        string target,
        string message)
    {
        compileIssues.Add(new PatchCompileIssue(isError, sourceName, sourcePath, ruleIndex, operationIndex, target, message));
    }

    private static VirtualFileReplacement WithOrigin(VirtualFileReplacement replacement, PatchReplacementOrigin origin)
    {
        return new VirtualFileReplacement
        {
            Find = replacement.Find,
            Replace = replacement.Replace,
            Origin = origin
        };
    }

    private static List<VirtualFileRule> MergeVirtualRules(IEnumerable<VirtualFileRuleSource> input)
    {
        var ordered = new List<VirtualFileRule>();
        var byTarget = new Dictionary<string, VirtualFileRule>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceRule in input)
        {
            var rule = sourceRule.Rule;
            var key = NormalizeVirtualTargetKey(rule.Target);
            if (byTarget.TryGetValue(key, out var existing))
            {
                existing.Replacements = existing.Replacements.Concat(rule.Replacements).ToArray();
                continue;
            }

            ordered.Add(rule);
            byTarget[key] = rule;
        }

        return ordered;
    }

    private static string NormalizeVirtualTargetKey(string target)
    {
        return target.Trim().Replace('\\', '/');
    }

    public string ResolveVirtualTargetPath(string target)
    {
        var normalized = target.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
        {
            return Path.GetFullPath(normalized);
        }

        normalized = normalized.TrimStart(Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(GameWorkingDirectory, normalized));
    }

    private static bool IsInsideDirectory(string directory, string path)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
