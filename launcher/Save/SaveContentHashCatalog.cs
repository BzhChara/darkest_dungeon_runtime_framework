namespace DDRuntimeLoader;

internal sealed partial class SaveDirectoryWatcher
{
    private static partial class SaveStateExporter
    {
        private sealed class ContentHashCatalog
        {
            private const string SourceScopeName = "game_install_no_local_mods";

            private static readonly JsonDocumentOptions ContentJsonOptions = new()
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };

            private readonly IReadOnlyDictionary<int, IReadOnlyList<string>> _namesBySignedHash;

            private ContentHashCatalog(
                IReadOnlyDictionary<int, IReadOnlyList<string>> namesBySignedHash,
                int sourceFileCount,
                int skippedSourceFileCount,
                int nameCount)
            {
                _namesBySignedHash = namesBySignedHash;
                SourceFileCount = sourceFileCount;
                SkippedSourceFileCount = skippedSourceFileCount;
                NameCount = nameCount;
            }

            public int SourceFileCount { get; }

            public int SkippedSourceFileCount { get; }

            public int NameCount { get; }

            public int HashCount => _namesBySignedHash.Count;

            public int AmbiguousHashCount => _namesBySignedHash.Values.Count(names => names.Count > 1);

            public static ContentHashCatalog Load(string gameWorkingDirectory, List<string> accessIssues)
            {
                if (!Directory.Exists(gameWorkingDirectory))
                {
                    accessIssues.Add($"Content hash catalog skipped because game directory was not found: {gameWorkingDirectory}");
                    return Empty;
                }

                var names = new HashSet<string>(StringComparer.Ordinal);
                var sourceFileCount = 0;
                var skippedSourceFileCount = 0;
                foreach (var path in Directory.EnumerateFiles(gameWorkingDirectory, "*", SearchOption.AllDirectories))
                {
                    if (IsLocalModPath(gameWorkingDirectory, path))
                    {
                        continue;
                    }

                    try
                    {
                        if (TryAddContentHashNamesFromFile(gameWorkingDirectory, path, names))
                        {
                            sourceFileCount++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        _ = ex;
                        skippedSourceFileCount++;
                    }
                }

                AddHardcodedContentHashNames(names);

                var byHash = names
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .GroupBy(DsonHash.HashNameSigned)
                    .ToDictionary(
                        group => group.Key,
                        group => (IReadOnlyList<string>)group
                            .OrderBy(name => name, StringComparer.Ordinal)
                            .ToArray());

                return new ContentHashCatalog(byHash, sourceFileCount, skippedSourceFileCount, byHash.Values.Sum(values => values.Count));
            }

            public static ContentHashCatalog Empty { get; } = new(new Dictionary<int, IReadOnlyList<string>>(), 0, 0, 0);

            public SaveStateHashCatalogFacts ToFacts()
            {
                return new SaveStateHashCatalogFacts(
                    SourceScopeName,
                    SourceFileCount,
                    SkippedSourceFileCount,
                    NameCount,
                    HashCount,
                    AmbiguousHashCount);
            }

            public IReadOnlyList<string> Resolve(int signedHash)
            {
                return _namesBySignedHash.TryGetValue(signedHash, out var names) ? names : [];
            }

            private static bool TryAddContentHashNamesFromFile(
                string gameWorkingDirectory,
                string path,
                HashSet<string> names)
            {
                var fileName = Path.GetFileName(path);
                if (fileName.Length == 0)
                {
                    return false;
                }

                if (fileName.EndsWith(".info.darkest", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith(".dungeon.json", StringComparison.OrdinalIgnoreCase))
                {
                    AddBaseName(path, names);
                    return true;
                }

                if (fileName.EndsWith(".upgrades.json", StringComparison.OrdinalIgnoreCase))
                {
                    AddBaseName(path, names);
                    var before = names.Count;
                    using var document = JsonDocument.Parse(File.ReadAllBytes(path), ContentJsonOptions);
                    AddJsonArrayEntryIds(document, "trees", "id", names);
                    foreach (var id in names.Skip(before).ToArray())
                    {
                        var dot = id.IndexOf('.');
                        if (dot >= 0 && dot + 1 < id.Length)
                        {
                            names.Add(id[(dot + 1)..]);
                        }
                    }

                    return true;
                }

                if (fileName.EndsWith(".camping_skills.json", StringComparison.OrdinalIgnoreCase))
                {
                    using var document = JsonDocument.Parse(File.ReadAllBytes(path), ContentJsonOptions);
                    AddCampingSkillNames(document, names);
                    return true;
                }

                if (fileName.EndsWith(".types.json", StringComparison.OrdinalIgnoreCase))
                {
                    using var document = JsonDocument.Parse(File.ReadAllBytes(path), ContentJsonOptions);
                    AddJsonArrayEntryIds(document, "types", "id", names);
                    AddJsonArrayEntryIds(document, "goals", "id", names);
                    return true;
                }

                if (fileName.Equals("quirk_library.json", StringComparison.OrdinalIgnoreCase))
                {
                    using var document = JsonDocument.Parse(File.ReadAllBytes(path), ContentJsonOptions);
                    AddJsonArrayEntryIds(document, "quirks", "id", names);
                    return true;
                }

                if (fileName.EndsWith(".building.json", StringComparison.OrdinalIgnoreCase))
                {
                    AddBaseName(path, names);
                    using var document = JsonDocument.Parse(File.ReadAllBytes(path), ContentJsonOptions);
                    AddBuildingActivityNames(document, names);
                    return true;
                }

                if (fileName.EndsWith(".events.json", StringComparison.OrdinalIgnoreCase))
                {
                    AddBaseName(path, names);
                    using var document = JsonDocument.Parse(File.ReadAllBytes(path), ContentJsonOptions);
                    AddJsonArrayEntryIds(document, "events", "id", names);
                    return true;
                }

                if (fileName.EndsWith(".inventory.items.darkest", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith(".inventory.system_configs.darkest", StringComparison.OrdinalIgnoreCase))
                {
                    AddInventoryItemNames(path, names);
                    return true;
                }

                if (fileName.EndsWith(".trinkets.json", StringComparison.OrdinalIgnoreCase))
                {
                    using var document = JsonDocument.Parse(File.ReadAllBytes(path), ContentJsonOptions);
                    AddJsonArrayEntryIds(document, "entries", "id", names);
                    return true;
                }

                if (fileName.Equals("curio_props.csv", StringComparison.OrdinalIgnoreCase))
                {
                    AddCurioPropNames(path, names);
                    return true;
                }

                if (fileName.Equals("obstacle_definitions.json", StringComparison.OrdinalIgnoreCase))
                {
                    using var document = JsonDocument.Parse(File.ReadAllBytes(path), ContentJsonOptions);
                    AddJsonArrayEntryIds(document, "props", "name", names);
                    return true;
                }

                if (fileName.Equals("quest.plot_quests.json", StringComparison.OrdinalIgnoreCase))
                {
                    using var document = JsonDocument.Parse(File.ReadAllBytes(path), ContentJsonOptions);
                    AddJsonArrayEntryIds(document, "plot_quests", "id", names);
                    return true;
                }

                if (fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    && TryAddTutorialPopupName(path, names))
                {
                    return true;
                }

                return false;
            }

            private static bool IsLocalModPath(string gameWorkingDirectory, string path)
            {
                var relativePath = Path.GetRelativePath(gameWorkingDirectory, path);
                var firstSeparator = relativePath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
                var firstSegment = firstSeparator >= 0 ? relativePath[..firstSeparator] : relativePath;
                return firstSegment.Equals("mods", StringComparison.OrdinalIgnoreCase);
            }

            private static void AddBaseName(string path, HashSet<string> names)
            {
                var fileName = Path.GetFileName(path);
                var dot = fileName.IndexOf('.');
                names.Add(dot > 0 ? fileName[..dot] : Path.GetFileNameWithoutExtension(path));
            }

            private static void AddJsonArrayEntryIds(
                JsonDocument document,
                string arrayName,
                string idPropertyName,
                HashSet<string> names)
            {
                if (!document.RootElement.TryGetProperty(arrayName, out var array)
                    || array.ValueKind != JsonValueKind.Array)
                {
                    return;
                }

                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty(idPropertyName, out var id)
                        && id.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(id.GetString()))
                    {
                        names.Add(id.GetString()!);
                    }
                }
            }

            private static void AddCampingSkillNames(JsonDocument document, HashSet<string> names)
            {
                if (!document.RootElement.TryGetProperty("skills", out var skills)
                    || skills.ValueKind != JsonValueKind.Array)
                {
                    return;
                }

                foreach (var skill in skills.EnumerateArray())
                {
                    if (skill.ValueKind != JsonValueKind.Object
                        || !skill.TryGetProperty("id", out var idElement)
                        || idElement.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(idElement.GetString()))
                    {
                        continue;
                    }

                    var id = idElement.GetString()!;
                    names.Add(id);
                    if (!skill.TryGetProperty("hero_classes", out var heroClasses)
                        || heroClasses.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var heroClass in heroClasses.EnumerateArray())
                    {
                        if (heroClass.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(heroClass.GetString()))
                        {
                            names.Add($"{heroClass.GetString()}.{id}");
                        }
                    }
                }
            }

            private static void AddBuildingActivityNames(JsonDocument document, HashSet<string> names)
            {
                if (!document.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Object
                    || !data.TryGetProperty("activities", out var activities)
                    || activities.ValueKind != JsonValueKind.Array)
                {
                    return;
                }

                foreach (var activity in activities.EnumerateArray())
                {
                    if (activity.ValueKind == JsonValueKind.Object
                        && activity.TryGetProperty("id", out var id)
                        && id.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(id.GetString()))
                    {
                        names.Add(id.GetString()!);
                    }
                }
            }

            private static void AddInventoryItemNames(string path, HashSet<string> names)
            {
                foreach (var line in File.ReadLines(path, Encoding.UTF8))
                {
                    var typeIndex = line.IndexOf(".type", StringComparison.OrdinalIgnoreCase);
                    var idIndex = line.IndexOf(".id", StringComparison.OrdinalIgnoreCase);
                    if (typeIndex < 0 || idIndex < 0)
                    {
                        continue;
                    }

                    AddQuotedTokenAfter(line, typeIndex, names);
                    AddQuotedTokenAfter(line, idIndex, names);
                }
            }

            private static void AddCurioPropNames(string path, HashSet<string> names)
            {
                foreach (var line in File.ReadLines(path, Encoding.UTF8))
                {
                    var comma = line.IndexOf(',');
                    var name = comma >= 0 ? line[..comma] : line;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        names.Add(name.Trim());
                    }
                }
            }

            private static bool TryAddTutorialPopupName(string path, HashSet<string> names)
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                const string prefix = "tutorial_popup.";
                if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || fileName.Length <= prefix.Length)
                {
                    return false;
                }

                names.Add(fileName[prefix.Length..]);
                return true;
            }

            private static void AddQuotedTokenAfter(string line, int startIndex, HashSet<string> names)
            {
                var quoteStart = line.IndexOf('"', startIndex);
                if (quoteStart < 0)
                {
                    return;
                }

                var quoteEnd = line.IndexOf('"', quoteStart + 1);
                if (quoteEnd <= quoteStart + 1)
                {
                    return;
                }

                names.Add(line[(quoteStart + 1)..quoteEnd]);
            }

            private static void AddHardcodedContentHashNames(HashSet<string> names)
            {
                names.Add("MONSTER_ENCOUNTERED");
                names.Add("AMBUSHED");
                names.Add("CURIO_INVESTIGATED");
                names.Add("TRAIT_APPLIED");
                names.Add("DEATHS_DOOR_APPLIED");
                names.Add("ROOM_VISITED");
                names.Add("BATTLE_COMPLETED");
                names.Add("HALLWAY_STEP_COMPLETED");
                names.Add("MONSTER_DEFEATED");
                names.Add("UNDEFINED");
            }
        }
    }
}
