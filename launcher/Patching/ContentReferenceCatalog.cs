using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DDRuntimeLoader;

internal static class ContentReferenceValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static ContentReferenceValidationBatch Validate(
        RuntimeConfig config,
        string projectRoot,
        IReadOnlyList<PluginManifestCandidate> plugins,
        LauncherLog log)
    {
        var declarations = plugins
            .Select(plugin => LoadDeclarations(projectRoot, plugin))
            .ToArray();
        if (declarations.All(declaration => declaration.References.Count == 0))
        {
            return new ContentReferenceValidationBatch([], declarations.SelectMany(declaration => declaration.Issues).ToArray());
        }

        var workshopIds = declarations
            .SelectMany(declaration => declaration.References)
            .Select(reference => reference.Rule.WorkshopId)
            .Concat(declarations
                .SelectMany(declaration => declaration.References)
                .Where(reference => reference.Category.Equals("workshop", StringComparison.OrdinalIgnoreCase))
                .Select(reference => reference.Lookup))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var pluginRoots = plugins.ToDictionary(
            plugin => plugin.Id,
            plugin => Path.GetDirectoryName(plugin.Path) ?? projectRoot,
            StringComparer.OrdinalIgnoreCase);
        var catalog = ContentReferenceCatalog.Build(config.GameWorkingDirectory, pluginRoots, workshopIds, log);
        var reports = new List<ContentReferenceValidationReport>();
        var issues = new List<PatchCompileIssue>();

        foreach (var declaration in declarations.Where(declaration => declaration.References.Count > 0 || declaration.Issues.Count > 0))
        {
            issues.AddRange(declaration.Issues);
            var reportDirectory = Path.Combine(config.ModStateDirectory, "_content_refs", SafeFileName(declaration.Plugin.Id));
            var reportPath = Path.Combine(reportDirectory, "content_refs.validation.json");
            var referenceReports = new List<ContentReferenceReportEntry>();

            foreach (var reference in declaration.References)
            {
                var resolution = catalog.ResolveDetailed(reference, declaration.Plugin.Id);
                var matches = resolution.Matches;
                var candidates = resolution.Candidates;
                var status = matches.Count > 0
                    ? "satisfied"
                    : reference.Rule.Required ? "missing-required" : "missing-optional";
                var provider = NormalizeProvider(reference.Rule.Provider);
                var lookup = reference.Lookup;
                var matchReports = matches
                    .Select(ToMatchReport)
                    .ToArray();
                var preferredMatch = matchReports.FirstOrDefault();
                var candidateReports = candidates
                    .Select((candidate, index) => new ContentReferenceCandidateReport(
                        candidate.Provider,
                        candidate.ProviderId,
                        candidate.SourcePath,
                        candidate.RelativePath,
                        matches.Contains(candidate),
                        matches.Count > 0 && candidate.Equals(matches[0]),
                        index))
                    .ToArray();

                referenceReports.Add(new ContentReferenceReportEntry(
                    reference.Category,
                    lookup,
                    provider,
                    reference.Rule.Required,
                    reference.Rule.WorkshopId,
                    reference.Rule.PluginId,
                    status,
                    reference.SourcePath,
                    reference.SourceIndex,
                    matchReports,
                    candidates.Count,
                    candidates.Count > 1,
                    preferredMatch,
                    candidateReports));

                if (candidates.Count > 1)
                {
                    log.Warn(
                        $"content-ref duplicate-candidates plugin={declaration.Plugin.Id} category={reference.Category} " +
                        $"lookup={QuoteLogValue(lookup)} provider={QuoteLogValue(provider)} candidates={candidates.Count} " +
                        $"matched={matches.Count} preferredProvider={QuoteLogValue(matches.Count > 0 ? matches[0].Provider : string.Empty)} " +
                        $"preferredPath={QuoteLogValue(matches.Count > 0 ? matches[0].SourcePath : string.Empty)} " +
                        $"candidateProviders={QuoteLogValue(FormatCandidateProviders(candidates))}");
                }

                if (matches.Count > 0)
                {
                    log.Info(
                        $"content-ref status=satisfied plugin={declaration.Plugin.Id} category={reference.Category} " +
                        $"lookup={QuoteLogValue(lookup)} provider={QuoteLogValue(provider)} " +
                        $"matches={matches.Count} firstProvider={matches[0].Provider} firstPath={QuoteLogValue(matches[0].SourcePath)}");
                    continue;
                }

                var isError = reference.Rule.Required;
                var message = isError
                    ? "required content reference was not found"
                    : "optional content reference was not found";
                issues.Add(new PatchCompileIssue(
                    isError,
                    declaration.Plugin.SourceName,
                    reference.SourcePath,
                    reference.SourceIndex,
                    0,
                    $"contentRefs/{reference.Category}/{lookup}",
                    $"{message}: provider={provider} workshopId={reference.Rule.WorkshopId} pluginId={reference.Rule.PluginId}"));

                if (isError)
                {
                    log.Error(
                        $"content-ref status=missing-required plugin={declaration.Plugin.Id} category={reference.Category} " +
                        $"lookup={QuoteLogValue(lookup)} provider={QuoteLogValue(provider)} source={QuoteLogValue(reference.SourcePath)}");
                }
                else
                {
                    log.Warn(
                        $"content-ref status=missing-optional plugin={declaration.Plugin.Id} category={reference.Category} " +
                        $"lookup={QuoteLogValue(lookup)} provider={QuoteLogValue(provider)} source={QuoteLogValue(reference.SourcePath)}");
                }
            }

            var report = new ContentReferenceValidationReport(
                declaration.Plugin.Id,
                declaration.Plugin.SourceName,
                declaration.Plugin.Path,
                catalog.SourceRootCount,
                catalog.EntryCount,
                referenceReports.Count,
                referenceReports.Count(entry => entry.Status.Equals("satisfied", StringComparison.OrdinalIgnoreCase)),
                referenceReports.Count(entry => entry.Status.Equals("missing-required", StringComparison.OrdinalIgnoreCase)),
                referenceReports.Count(entry => entry.Status.Equals("missing-optional", StringComparison.OrdinalIgnoreCase)),
                referenceReports.Count(entry => entry.HasDuplicateCandidates),
                reportPath,
                referenceReports);

            Directory.CreateDirectory(reportDirectory);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
            reports.Add(report);
            log.Info(
                $"content-ref-report plugin={declaration.Plugin.Id} refs={report.ReferenceCount} " +
                $"satisfied={report.SatisfiedCount} missingRequired={report.MissingRequiredCount} " +
                $"missingOptional={report.MissingOptionalCount} duplicateRefs={report.DuplicateReferenceCount} " +
                $"report={QuoteLogValue(reportPath)}");
        }

        return new ContentReferenceValidationBatch(reports, issues);
    }

    private static PluginContentReferenceDeclaration LoadDeclarations(string projectRoot, PluginManifestCandidate plugin)
    {
        var refs = new List<DeclaredContentReference>();
        var issues = new List<PatchCompileIssue>();
        var manifestDirectory = Path.GetDirectoryName(plugin.Path) ?? projectRoot;

        AddSetReferences(refs, plugin.Manifest.ContentRefs, plugin.Path, 0);

        for (var index = 0; index < plugin.Manifest.Modules.ContentRefs.Length; index++)
        {
            var moduleReference = plugin.Manifest.Modules.ContentRefs[index];
            var sourceIndex = index + 1;
            if (string.IsNullOrWhiteSpace(moduleReference))
            {
                issues.Add(new PatchCompileIssue(
                    true,
                    plugin.SourceName,
                    plugin.Path,
                    sourceIndex,
                    0,
                    "modules.contentRefs",
                    "content reference module path is empty"));
                continue;
            }

            var modulePath = Path.IsPathRooted(moduleReference)
                ? Path.GetFullPath(moduleReference)
                : Path.GetFullPath(Path.Combine(manifestDirectory, moduleReference));
            if (!IsInsideDirectory(projectRoot, modulePath))
            {
                issues.Add(new PatchCompileIssue(
                    true,
                    plugin.SourceName,
                    plugin.Path,
                    sourceIndex,
                    0,
                    "modules.contentRefs",
                    $"content reference module resolves outside project root: {modulePath}"));
                continue;
            }

            if (!File.Exists(modulePath))
            {
                issues.Add(new PatchCompileIssue(
                    true,
                    plugin.SourceName,
                    plugin.Path,
                    sourceIndex,
                    0,
                    "modules.contentRefs",
                    $"content reference module was not found: {modulePath}"));
                continue;
            }

            try
            {
                AddSetReferences(refs, ContentReferenceSet.Load(modulePath), modulePath, sourceIndex);
            }
            catch (Exception ex)
            {
                issues.Add(new PatchCompileIssue(
                    true,
                    plugin.SourceName,
                    plugin.Path,
                    sourceIndex,
                    0,
                    "modules.contentRefs",
                    ex.Message));
            }
        }

        foreach (var reference in refs.Where(reference => string.IsNullOrWhiteSpace(reference.Lookup)))
        {
            issues.Add(new PatchCompileIssue(
                true,
                plugin.SourceName,
                reference.SourcePath,
                reference.SourceIndex,
                0,
                $"contentRefs/{reference.Category}",
                "content reference requires id, path, or workshopId"));
        }

        return new PluginContentReferenceDeclaration(plugin, refs.Where(reference => !string.IsNullOrWhiteSpace(reference.Lookup)).ToArray(), issues);
    }

    private static void AddSetReferences(List<DeclaredContentReference> output, ContentReferenceSet set, string sourcePath, int sourceIndex)
    {
        AddCategory(output, "workshop", set.Workshop, sourcePath, sourceIndex);
        AddCategory(output, "quest", set.Quests, sourcePath, sourceIndex);
        AddCategory(output, "dungeon", set.Dungeons, sourcePath, sourceIndex);
        AddCategory(output, "monster", set.Monsters, sourcePath, sourceIndex);
        AddCategory(output, "heroClass", set.HeroClasses, sourcePath, sourceIndex);
        AddCategory(output, "heroSkill", set.HeroSkills, sourcePath, sourceIndex);
        AddCategory(output, "effect", set.Effects, sourcePath, sourceIndex);
        AddCategory(output, "buff", set.Buffs, sourcePath, sourceIndex);
        AddCategory(output, "trait", set.Traits, sourcePath, sourceIndex);
        AddCategory(output, "quirk", set.Quirks, sourcePath, sourceIndex);
        AddCategory(output, "trinket", set.Trinkets, sourcePath, sourceIndex);
        AddCategory(output, "curio", set.Curios, sourcePath, sourceIndex);
        AddCategory(output, "lootTable", set.LootTables, sourcePath, sourceIndex);
        AddCategory(output, "raidSetting", set.RaidSettings, sourcePath, sourceIndex);
        AddCategory(output, "localizationKey", set.LocalizationKeys, sourcePath, sourceIndex);
        AddCategory(output, "mash", set.Mash, sourcePath, sourceIndex);
        AddCategory(output, "map", set.Maps, sourcePath, sourceIndex);
        AddCategory(output, "mapGenerator", set.MapGenerators, sourcePath, sourceIndex);
    }

    private static void AddCategory(List<DeclaredContentReference> output, string category, IEnumerable<ContentReferenceRule> refs, string sourcePath, int sourceIndex)
    {
        foreach (var reference in refs)
        {
            var lookup = category.Equals("workshop", StringComparison.OrdinalIgnoreCase)
                ? FirstNonEmpty(reference.WorkshopId, reference.Id, reference.Path)
                : FirstNonEmpty(reference.Id, reference.Path, reference.WorkshopId);
            output.Add(new DeclaredContentReference(category, NormalizeLookup(category, lookup), reference, sourcePath, sourceIndex));
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static string NormalizeLookup(string category, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return IsPathCategory(category)
            ? NormalizeRelativePath(value)
            : value.Trim();
    }

    private static bool IsPathCategory(string category)
    {
        return category.Equals("mash", StringComparison.OrdinalIgnoreCase) ||
            category.Equals("map", StringComparison.OrdinalIgnoreCase) ||
            category.Equals("mapGenerator", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeProvider(string provider)
    {
        return string.IsNullOrWhiteSpace(provider) ? "any" : provider.Trim().ToLowerInvariant();
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Trim().TrimStart('/', '\\').Replace('\\', '/');
    }

    private static string SafeFileName(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "content_refs" : value.Trim();
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = trimmed
            .Select(ch => invalid.Contains(ch) || ch is '/' or '\\' or ':' ? '_' : ch)
            .ToArray();
        var safe = new string(chars).Trim('.');
        return string.IsNullOrWhiteSpace(safe) ? "content_refs" : safe;
    }

    private static bool IsInsideDirectory(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static ContentReferenceMatchReport ToMatchReport(ContentReferenceCatalogEntry match)
    {
        return new ContentReferenceMatchReport(
            match.Provider,
            match.ProviderId,
            match.SourcePath,
            match.RelativePath);
    }

    private static string FormatCandidateProviders(IReadOnlyList<ContentReferenceCatalogEntry> candidates)
    {
        return string.Join(
            ",",
            candidates.Select(candidate =>
                string.IsNullOrWhiteSpace(candidate.ProviderId)
                    ? candidate.Provider
                    : $"{candidate.Provider}:{candidate.ProviderId}"));
    }

    private static string QuoteLogValue(string value)
    {
        return string.IsNullOrEmpty(value) ? "\"\"" : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}

internal sealed class ContentReferenceCatalog
{
    private static readonly Regex HeroSkillPattern = new(
        @"(?:combat_skill|combat_move_skill|camping_skill):[^\r\n]*?\.id\s+""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex EffectNamePattern = new(
        @"effect:\s+\.name\s+""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private readonly Dictionary<string, List<ContentReferenceCatalogEntry>> _entriesByKey = new(StringComparer.OrdinalIgnoreCase);

    private ContentReferenceCatalog()
    {
    }

    public int SourceRootCount { get; private set; }
    public int EntryCount { get; private set; }

    public static ContentReferenceCatalog Build(
        string gameWorkingDirectory,
        IReadOnlyDictionary<string, string> pluginRoots,
        IReadOnlyCollection<string> workshopIds,
        LauncherLog log)
    {
        var catalog = new ContentReferenceCatalog();
        catalog.ScanContentRoot("base", string.Empty, gameWorkingDirectory, gameWorkingDirectory, log);

        foreach (var dlcDirectory in EnumerateOfficialDlcDirectories(gameWorkingDirectory))
        {
            catalog.ScanContentRoot("dlc", Path.GetFileName(dlcDirectory), dlcDirectory, dlcDirectory, log);
        }

        foreach (var workshopDirectory in EnumerateWorkshopDirectories(gameWorkingDirectory, workshopIds, log))
        {
            catalog.ScanContentRoot(
                "workshop",
                Path.GetFileName(workshopDirectory),
                workshopDirectory,
                workshopDirectory,
                log);
        }

        foreach (var pluginRoot in pluginRoots.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            catalog.ScanContentRoot("plugin", pluginRoot.Key, pluginRoot.Value, pluginRoot.Value, log);
        }

        return catalog;
    }

    public IReadOnlyList<ContentReferenceCatalogEntry> Resolve(DeclaredContentReference reference, string currentPluginId)
    {
        return ResolveDetailed(reference, currentPluginId).Matches;
    }

    public ContentReferenceResolution ResolveDetailed(DeclaredContentReference reference, string currentPluginId)
    {
        if (!_entriesByKey.TryGetValue(BuildKey(reference.Category, reference.Lookup), out var candidates))
        {
            return new ContentReferenceResolution([], []);
        }

        var provider = string.IsNullOrWhiteSpace(reference.Rule.Provider) ? "any" : reference.Rule.Provider.Trim().ToLowerInvariant();
        var expectedPluginId = string.IsNullOrWhiteSpace(reference.Rule.PluginId) ? currentPluginId : reference.Rule.PluginId.Trim();
        var orderedCandidates = OrderCandidates(candidates).ToArray();
        var matches = orderedCandidates
            .Where(candidate => ProviderMatches(candidate, provider, reference.Rule.WorkshopId, expectedPluginId))
            .ToArray();

        return new ContentReferenceResolution(orderedCandidates, matches);
    }

    private static bool ProviderMatches(ContentReferenceCatalogEntry candidate, string provider, string workshopId, string pluginId)
    {
        if (!provider.Equals("any", StringComparison.OrdinalIgnoreCase) &&
            !candidate.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(workshopId) &&
            (!candidate.Provider.Equals("workshop", StringComparison.OrdinalIgnoreCase) ||
                !candidate.ProviderId.Equals(workshopId.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(pluginId) &&
            provider.Equals("plugin", StringComparison.OrdinalIgnoreCase) &&
            (!candidate.Provider.Equals("plugin", StringComparison.OrdinalIgnoreCase) ||
                !candidate.ProviderId.Equals(pluginId.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    private static IOrderedEnumerable<ContentReferenceCatalogEntry> OrderCandidates(IEnumerable<ContentReferenceCatalogEntry> candidates)
    {
        return candidates
            .OrderBy(candidate => ProviderRank(candidate.Provider))
            .ThenBy(candidate => candidate.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.SourcePath, StringComparer.OrdinalIgnoreCase);
    }

    private void ScanContentRoot(string provider, string providerId, string root, string relativeRoot, LauncherLog log)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        SourceRootCount++;
        if (provider.Equals("workshop", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(providerId))
        {
            AddEntry("workshop", providerId, provider, providerId, root, root, string.Empty);
        }

        TryScanFiles(root, "quest.plot_quests.json", provider, file => AddPlotQuestEntries(file, provider, providerId, relativeRoot, log), log);
        TryScanFiles(root, "*.quest.plot_quests.json", provider, file => AddPlotQuestEntries(file, provider, providerId, relativeRoot, log), log);
        TryScanFiles(root, "*.dungeon.json", provider, file => AddDungeonEntry(file, provider, providerId, relativeRoot), log);
        TryScanFiles(root, "*.info.darkest", provider, file =>
        {
            AddMonsterEntry(file, provider, providerId, relativeRoot);
            AddHeroClassAndSkillEntries(file, provider, providerId, relativeRoot, log);
        }, log);
        TryScanFiles(root, "*.effects.darkest", provider, file => AddEffectEntries(file, provider, providerId, relativeRoot, log), log);
        TryScanFiles(root, "*.buffs.json", provider, file => AddRootArrayIdEntries("buff", "buffs", file, provider, providerId, relativeRoot, log), log);
        TryScanFiles(root, "*trait_library.json", provider, file => AddRootArrayIdEntries("trait", "traits", file, provider, providerId, relativeRoot, log), log);
        TryScanFiles(root, "*quirk_library.json", provider, file => AddRootArrayIdEntries("quirk", "quirks", file, provider, providerId, relativeRoot, log), log);
        TryScanFiles(root, "*.entries.trinkets.json", provider, file => AddTrinketEntries(file, provider, providerId, relativeRoot, log), log);
        TryScanFiles(root, "curio_type_library.csv", provider, file => AddCurioEntries(file, provider, providerId, relativeRoot, log), log);
        TryScanFiles(root, "loot.json", provider, file => AddLootTableEntries(file, provider, providerId, relativeRoot, log), log);
        TryScanFiles(root, "*.loot.json", provider, file => AddLootTableEntries(file, provider, providerId, relativeRoot, log), log);
        TryScanFiles(root, "raid_settings.json", provider, file => AddRaidSettingEntries(file, provider, providerId, relativeRoot, log), log);
        TryScanFiles(root, "*.string_table.xml", provider, file => AddLocalizationKeyEntries(file, provider, providerId, relativeRoot, log), log);
        TryScanFiles(root, "*.mash.darkest", provider, file => AddPathEntry("mash", file, provider, providerId, relativeRoot), log);
        TryScanFiles(root, "*.dm", provider, file => AddPathEntry("map", file, provider, providerId, relativeRoot), log);
        TryScanFiles(root, "*.map_generator.darkest", provider, file => AddPathEntry("mapGenerator", file, provider, providerId, relativeRoot), log);
    }

    private static void TryScanFiles(string root, string pattern, string provider, Action<string> onFile, LauncherLog log)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (ShouldSkipPathForProvider(file, provider))
                {
                    continue;
                }

                onFile(file);
            }
        }
        catch (Exception ex)
        {
            log.Warn($"content-catalog-scan-failed root={root} pattern={pattern} message={ex.Message}");
        }
    }

    private void AddHeroClassAndSkillEntries(string file, string provider, string providerId, string relativeRoot, LauncherLog log)
    {
        var relativePath = GetRelativePath(relativeRoot, file);
        if (!relativePath.Split('/').Any(part => part.Equals("heroes", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var fileName = Path.GetFileName(file);
        const string suffix = ".info.darkest";
        if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var heroClassId = fileName[..^suffix.Length];
        AddEntry("heroClass", heroClassId, provider, providerId, file, relativeRoot, relativePath);

        try
        {
            var text = File.ReadAllText(file, Encoding.UTF8);
            foreach (Match match in HeroSkillPattern.Matches(text))
            {
                var skillId = match.Groups[1].Value;
                AddEntry("heroSkill", skillId, provider, providerId, file, relativeRoot, relativePath);
                AddEntry("heroSkill", $"{heroClassId}.{skillId}", provider, providerId, file, relativeRoot, relativePath);
            }
        }
        catch (Exception ex)
        {
            log.Warn($"content-catalog-read-failed category=heroSkill path={file} message={ex.Message}");
        }
    }

    private void AddPlotQuestEntries(string file, string provider, string providerId, string relativeRoot, LauncherLog log)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file), JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("plot_quests", out var questsElement) ||
                questsElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var questElement in questsElement.EnumerateArray())
            {
                if (questElement.ValueKind == JsonValueKind.Object &&
                    questElement.TryGetProperty("id", out var idElement) &&
                    idElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(idElement.GetString()))
                {
                    AddEntry("quest", idElement.GetString()!, provider, providerId, file, relativeRoot, GetRelativePath(relativeRoot, file));
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn($"content-catalog-read-failed category=quest path={file} message={ex.Message}");
        }
    }

    private void AddEffectEntries(string file, string provider, string providerId, string relativeRoot, LauncherLog log)
    {
        try
        {
            var relativePath = GetRelativePath(relativeRoot, file);
            var text = File.ReadAllText(file, Encoding.UTF8);
            foreach (Match match in EffectNamePattern.Matches(text))
            {
                AddEntry("effect", match.Groups[1].Value, provider, providerId, file, relativeRoot, relativePath);
            }
        }
        catch (Exception ex)
        {
            log.Warn($"content-catalog-read-failed category=effect path={file} message={ex.Message}");
        }
    }

    private void AddRootArrayIdEntries(
        string category,
        string rootPropertyName,
        string file,
        string provider,
        string providerId,
        string relativeRoot,
        LauncherLog log)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file), JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(rootPropertyName, out var itemsElement) ||
                itemsElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var itemElement in itemsElement.EnumerateArray())
            {
                if (itemElement.ValueKind == JsonValueKind.Object &&
                    itemElement.TryGetProperty("id", out var idElement) &&
                    idElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(idElement.GetString()))
                {
                    AddEntry(category, idElement.GetString()!, provider, providerId, file, relativeRoot, GetRelativePath(relativeRoot, file));
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn($"content-catalog-read-failed category={category} path={file} message={ex.Message}");
        }
    }

    private void AddDungeonEntry(string file, string provider, string providerId, string relativeRoot)
    {
        var relativePath = GetRelativePath(relativeRoot, file);
        if (!relativePath.Split('/').Any(part => part.Equals("dungeons", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var id = Path.GetFileName(Path.GetDirectoryName(file)) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(id))
        {
            AddEntry("dungeon", id, provider, providerId, file, relativeRoot, relativePath);
        }
    }

    private void AddMonsterEntry(string file, string provider, string providerId, string relativeRoot)
    {
        var relativePath = GetRelativePath(relativeRoot, file);
        if (!relativePath.Split('/').Any(part => part.Equals("monsters", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var fileName = Path.GetFileName(file);
        const string suffix = ".info.darkest";
        if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            AddEntry("monster", fileName[..^suffix.Length], provider, providerId, file, relativeRoot, relativePath);
        }
    }

    private void AddTrinketEntries(string file, string provider, string providerId, string relativeRoot, LauncherLog log)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file), JsonOptions);
            foreach (var id in EnumerateStringProperties(document.RootElement, "id"))
            {
                AddEntry("trinket", id, provider, providerId, file, relativeRoot, GetRelativePath(relativeRoot, file));
            }
        }
        catch (Exception ex)
        {
            log.Warn($"content-catalog-read-failed category=trinket path={file} message={ex.Message}");
        }
    }

    private void AddCurioEntries(string file, string provider, string providerId, string relativeRoot, LauncherLog log)
    {
        try
        {
            var relativePath = GetRelativePath(relativeRoot, file);
            foreach (var line in File.ReadLines(file, Encoding.UTF8))
            {
                var parts = line.Split(',');
                if (parts.Length < 3)
                {
                    continue;
                }

                var id = parts[2].Trim();
                if (IsContentIdCandidate(id))
                {
                    AddEntry("curio", id, provider, providerId, file, relativeRoot, relativePath);
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn($"content-catalog-read-failed category=curio path={file} message={ex.Message}");
        }
    }

    private void AddLootTableEntries(string file, string provider, string providerId, string relativeRoot, LauncherLog log)
    {
        AddRootArrayIdEntries("lootTable", "loot_tables", file, provider, providerId, relativeRoot, log);
    }

    private void AddRaidSettingEntries(string file, string provider, string providerId, string relativeRoot, LauncherLog log)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file), JsonOptions);
            AddRaidSettingKeys(document.RootElement, "torch_settings_data_table", "torchSettings", file, provider, providerId, relativeRoot);
            AddRaidSettingKeys(document.RootElement, "raid_rules_override_data_table", "raidRulesOverride", file, provider, providerId, relativeRoot);
        }
        catch (Exception ex)
        {
            log.Warn($"content-catalog-read-failed category=raidSetting path={file} message={ex.Message}");
        }
    }

    private void AddRaidSettingKeys(
        JsonElement rootElement,
        string tableName,
        string prefix,
        string file,
        string provider,
        string providerId,
        string relativeRoot)
    {
        if (rootElement.ValueKind != JsonValueKind.Object ||
            !rootElement.TryGetProperty(tableName, out var tableElement) ||
            tableElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var itemElement in tableElement.EnumerateArray())
        {
            if (itemElement.ValueKind == JsonValueKind.Object &&
                itemElement.TryGetProperty("key", out var keyElement) &&
                keyElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(keyElement.GetString()))
            {
                var key = keyElement.GetString()!;
                AddEntry("raidSetting", key, provider, providerId, file, relativeRoot, GetRelativePath(relativeRoot, file));
                AddEntry("raidSetting", $"{prefix}.{key}", provider, providerId, file, relativeRoot, GetRelativePath(relativeRoot, file));
            }
        }
    }

    private void AddLocalizationKeyEntries(string file, string provider, string providerId, string relativeRoot, LauncherLog log)
    {
        try
        {
            var document = XDocument.Load(file, LoadOptions.None);
            foreach (var entryElement in document.Descendants("entry"))
            {
                var id = entryElement.Attribute("id")?.Value;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    AddEntry("localizationKey", id, provider, providerId, file, relativeRoot, GetRelativePath(relativeRoot, file));
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn($"content-catalog-read-failed category=localizationKey path={file} message={ex.Message}");
        }
    }

    private void AddPathEntry(string category, string file, string provider, string providerId, string relativeRoot)
    {
        var relativePath = GetRelativePath(relativeRoot, file);
        AddEntry(category, relativePath, provider, providerId, file, relativeRoot, relativePath);
    }

    private void AddEntry(string category, string id, string provider, string providerId, string sourcePath, string sourceRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var entry = new ContentReferenceCatalogEntry(
            category,
            NormalizeLookup(category, id),
            provider,
            providerId,
            sourceRoot,
            sourcePath,
            relativePath);
        var key = BuildKey(entry.Category, entry.Id);
        if (!_entriesByKey.TryGetValue(key, out var entries))
        {
            entries = [];
            _entriesByKey[key] = entries;
        }

        if (entries.Any(existing =>
                existing.Provider.Equals(entry.Provider, StringComparison.OrdinalIgnoreCase) &&
                existing.ProviderId.Equals(entry.ProviderId, StringComparison.OrdinalIgnoreCase) &&
                existing.SourcePath.Equals(entry.SourcePath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        entries.Add(entry);
        EntryCount++;
    }

    private static bool IsContentIdCandidate(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            value.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or '/');
    }

    private static IEnumerable<string> EnumerateStringProperties(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(propertyName) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    yield return property.Value.GetString()!;
                }

                foreach (var child in EnumerateStringProperties(property.Value, propertyName))
                {
                    yield return child;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var child in EnumerateStringProperties(item, propertyName))
                {
                    yield return child;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateOfficialDlcDirectories(string gameWorkingDirectory)
    {
        var dlcDirectory = Path.Combine(gameWorkingDirectory, "dlc");
        if (!Directory.Exists(dlcDirectory))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(dlcDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(name) ||
                !char.IsDigit(name[0]) ||
                name.Contains("arena", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return directory;
        }
    }

    private static IEnumerable<string> EnumerateWorkshopDirectories(string gameWorkingDirectory, IReadOnlyCollection<string> workshopIds, LauncherLog log)
    {
        if (workshopIds.Count == 0)
        {
            yield break;
        }

        var gameDirectory = new DirectoryInfo(gameWorkingDirectory);
        var steamApps = gameDirectory.Parent?.Parent;
        if (steamApps is null)
        {
            log.Warn($"content-catalog-workshop-root-unresolved gameWorkingDirectory={gameWorkingDirectory}");
            yield break;
        }

        var workshopRoot = Path.Combine(steamApps.FullName, "workshop", "content", "262060");
        foreach (var workshopId in workshopIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(workshopRoot, workshopId);
            if (Directory.Exists(candidate))
            {
                yield return candidate;
            }
            else
            {
                log.Warn($"content-catalog-workshop-missing workshopId={workshopId} path={candidate}");
            }
        }
    }

    private static bool ShouldSkipPathForProvider(string path, string provider)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Any(part => part.Equals("modes", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return provider.Equals("base", StringComparison.OrdinalIgnoreCase) &&
            parts.Any(part =>
                part.Equals("dlc", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("mods", StringComparison.OrdinalIgnoreCase));
    }

    private static int ProviderRank(string provider)
    {
        return provider.ToLowerInvariant() switch
        {
            "base" => 0,
            "dlc" => 10,
            "workshop" => 20,
            "plugin" => 30,
            _ => 100
        };
    }

    private static string BuildKey(string category, string id)
    {
        return $"{category.ToLowerInvariant()}|{NormalizeLookup(category, id)}";
    }

    private static string NormalizeLookup(string category, string id)
    {
        var trimmed = id.Trim();
        return category.Equals("mash", StringComparison.OrdinalIgnoreCase) ||
            category.Equals("map", StringComparison.OrdinalIgnoreCase) ||
            category.Equals("mapGenerator", StringComparison.OrdinalIgnoreCase)
            ? trimmed.TrimStart('/', '\\').Replace('\\', '/')
            : trimmed;
    }

    private static string GetRelativePath(string root, string path)
    {
        return Path.GetRelativePath(root, path).Replace('\\', '/');
    }
}

internal sealed record ContentReferenceValidationBatch(
    IReadOnlyList<ContentReferenceValidationReport> Reports,
    IReadOnlyList<PatchCompileIssue> Issues);

internal sealed record PluginContentReferenceDeclaration(
    PluginManifestCandidate Plugin,
    IReadOnlyList<DeclaredContentReference> References,
    IReadOnlyList<PatchCompileIssue> Issues);

internal sealed record DeclaredContentReference(
    string Category,
    string Lookup,
    ContentReferenceRule Rule,
    string SourcePath,
    int SourceIndex);

internal sealed record ContentReferenceCatalogEntry(
    string Category,
    string Id,
    string Provider,
    string ProviderId,
    string SourceRoot,
    string SourcePath,
    string RelativePath);

internal sealed record ContentReferenceResolution(
    IReadOnlyList<ContentReferenceCatalogEntry> Candidates,
    IReadOnlyList<ContentReferenceCatalogEntry> Matches);

internal sealed record ContentReferenceValidationReport(
    string PluginId,
    string PluginName,
    string ManifestPath,
    int CatalogSourceRootCount,
    int CatalogEntryCount,
    int ReferenceCount,
    int SatisfiedCount,
    int MissingRequiredCount,
    int MissingOptionalCount,
    int DuplicateReferenceCount,
    string ReportPath,
    IReadOnlyList<ContentReferenceReportEntry> References);

internal sealed record ContentReferenceReportEntry(
    string Category,
    string Lookup,
    string Provider,
    bool Required,
    string WorkshopId,
    string PluginId,
    string Status,
    string SourcePath,
    int SourceIndex,
    IReadOnlyList<ContentReferenceMatchReport> Matches,
    int CandidateCount,
    bool HasDuplicateCandidates,
    ContentReferenceMatchReport? PreferredMatch,
    IReadOnlyList<ContentReferenceCandidateReport> Candidates);

internal sealed record ContentReferenceMatchReport(
    string Provider,
    string ProviderId,
    string SourcePath,
    string RelativePath);

internal sealed record ContentReferenceCandidateReport(
    string Provider,
    string ProviderId,
    string SourcePath,
    string RelativePath,
    bool MatchesRequestedProvider,
    bool Selected,
    int ResolutionOrder);
