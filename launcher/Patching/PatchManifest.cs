namespace DDRuntimeLoader;

internal sealed class PluginPatchManifest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("capabilities")]
    public string[] Capabilities { get; set; } = [];

    [JsonPropertyName("phase")]
    public string Phase { get; set; } = "normal";

    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("depends")]
    public string[] Depends { get; set; } = [];

    [JsonPropertyName("optionalDepends")]
    public string[] OptionalDepends { get; set; } = [];

    [JsonPropertyName("loadAfter")]
    public string[] LoadAfter { get; set; } = [];

    [JsonPropertyName("loadBefore")]
    public string[] LoadBefore { get; set; } = [];

    [JsonPropertyName("conflicts")]
    public string[] Conflicts { get; set; } = [];

    [JsonPropertyName("modules")]
    public PluginModuleManifest Modules { get; set; } = new();

    [JsonPropertyName("contentRefs")]
    public ContentReferenceSet ContentRefs { get; set; } = new();

    [JsonPropertyName("virtualFileRules")]
    public VirtualFileRule[] VirtualFileRules { get; set; } = [];

    [JsonPropertyName("mapTemplates")]
    public MapTemplateRule[] MapTemplates { get; set; } = [];

    [JsonPropertyName("mapLayoutTemplates")]
    public MapLayoutTemplateRule[] MapLayoutTemplates { get; set; } = [];

    [JsonPropertyName("questChains")]
    public QuestChainRule[] QuestChains { get; set; } = [];

    [JsonPropertyName("questBoardPolicies")]
    public QuestBoardPolicyRule[] QuestBoardPolicies { get; set; } = [];

    [JsonPropertyName("eventRules")]
    public RuntimeEventRule[] EventRules { get; set; } = [];

    [JsonPropertyName("factEventRules")]
    public FactEventRule[] FactEventRules { get; set; } = [];

    [JsonPropertyName("stateSchema")]
    public Dictionary<string, JsonElement> StateSchema { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static PluginPatchManifest Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var manifest = JsonSerializer.Deserialize<PluginPatchManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) ?? throw new InvalidOperationException($"Plugin patch manifest is empty: {path}");

            manifest.Name ??= string.Empty;
            manifest.Id ??= string.Empty;
            manifest.Version ??= string.Empty;
            manifest.Capabilities ??= [];
            manifest.Phase ??= "normal";
            manifest.Depends ??= [];
            manifest.OptionalDepends ??= [];
            manifest.LoadAfter ??= [];
            manifest.LoadBefore ??= [];
            manifest.Conflicts ??= [];
            manifest.Modules ??= new PluginModuleManifest();
            manifest.Modules.ContentRefs ??= [];
            manifest.ContentRefs ??= new ContentReferenceSet();
            manifest.ContentRefs.Normalize();
            manifest.VirtualFileRules ??= [];
            manifest.MapTemplates ??= [];
            manifest.MapLayoutTemplates ??= [];
            manifest.QuestChains ??= [];
            manifest.QuestBoardPolicies ??= [];
            manifest.EventRules ??= [];
            manifest.FactEventRules ??= [];
            manifest.StateSchema ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in manifest.MapLayoutTemplates)
            {
                rule.Id ??= string.Empty;
                rule.Target ??= string.Empty;
                rule.Source ??= string.Empty;
                rule.Layout ??= new MapLayoutDefinition();
                NormalizeMapLayoutDefinition(rule.Layout);
                rule.Tiles ??= [];
                foreach (var tile in rule.Tiles)
                {
                    tile.Area ??= string.Empty;
                    tile.TileId ??= string.Empty;
                    tile.Encounter ??= string.Empty;
                }

                rule.Encounters ??= [];
                foreach (var encounter in rule.Encounters)
                {
                    encounter.Id ??= string.Empty;
                    encounter.Mash ??= string.Empty;
                }
            }

            foreach (var chain in manifest.QuestChains)
            {
                chain.Id ??= string.Empty;
                chain.Name ??= string.Empty;
                chain.Mode ??= string.Empty;
                chain.Unlock ??= new QuestChainUnlockRule();
                chain.Unlock.Type ??= string.Empty;
                chain.Unlock.QuestId ??= string.Empty;
                chain.Unlock.Phase ??= string.Empty;
                chain.QuestBoard ??= new QuestChainBoardRule();
                chain.QuestBoard.Mode ??= string.Empty;
                chain.QuestBoard.QuestIdSource ??= string.Empty;
                chain.QuestBoard.CompletedStateKey ??= string.Empty;
                chain.Stages ??= [];
                foreach (var stage in chain.Stages)
                {
                    stage.Id ??= string.Empty;
                    stage.Name ??= string.Empty;
                    stage.SourceQuestId ??= string.Empty;
                    stage.TargetQuestId ??= string.Empty;
                    stage.MapLayoutTemplateId ??= string.Empty;
                    stage.MapTemplateId ??= string.Empty;
                    stage.Region ??= string.Empty;
                    stage.Tags ??= [];
                }
            }

            foreach (var policy in manifest.QuestBoardPolicies)
            {
                policy.Id ??= string.Empty;
                policy.Name ??= string.Empty;
                policy.Mode ??= string.Empty;
                policy.RefreshTriggers ??= [];
                policy.Entries ??= [];
                foreach (var entry in policy.Entries)
                {
                    entry.Id ??= string.Empty;
                    entry.QuestId ??= string.Empty;
                    entry.SourceQuestId ??= string.Empty;
                    entry.Pool ??= string.Empty;
                    entry.OnCompleted ??= string.Empty;
                    entry.AvailableWhen ??= new QuestBoardPolicyAvailableWhenRule();
                    entry.AvailableWhen.CompletedQuest ??= string.Empty;
                    entry.AvailableWhen.CompletedQuests ??= [];
                    entry.AvailableWhen.NotCompletedQuest ??= string.Empty;
                    entry.AvailableWhen.NotCompletedQuests ??= [];
                    entry.AvailableWhen.Phase ??= string.Empty;
                    entry.AvailableWhen.StateKey ??= string.Empty;
                    entry.AvailableWhen.StateEquals ??= string.Empty;
                }
            }

            foreach (var rule in manifest.EventRules)
            {
                rule.RequiresCapabilities ??= [];
                rule.Actions ??= [];
                if (rule.When is not null)
                {
                    NormalizeRuntimeRulePredicate(rule.When);
                }

                foreach (var action in rule.Actions)
                {
                    action.Args ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                }
            }

            foreach (var rule in manifest.FactEventRules)
            {
                rule.RequiresCapabilities ??= [];
                rule.Payload ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                if (rule.When is not null)
                {
                    NormalizeRuntimeRulePredicate(rule.When);
                }
            }

            return manifest;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Plugin patch manifest is invalid: {path}: {ex.Message}", ex);
        }
    }

    private static void NormalizeRuntimeRulePredicate(RuntimeRulePredicate predicate)
    {
        predicate.All ??= [];
        predicate.Any ??= [];
        predicate.None ??= [];
        foreach (var child in predicate.All.Concat(predicate.Any).Concat(predicate.None))
        {
            NormalizeRuntimeRulePredicate(child);
        }
    }

    private static void NormalizeMapLayoutDefinition(MapLayoutDefinition layout)
    {
        layout.Entrance ??= string.Empty;
        layout.FinalRoom ??= string.Empty;
        layout.Rooms ??= [];
        layout.Corridors ??= [];
        layout.Links ??= [];
        foreach (var room in layout.Rooms)
        {
            room.Id ??= string.Empty;
            room.TemplateAreaId ??= string.Empty;
            room.Position ??= [];
        }

        foreach (var corridor in layout.Corridors)
        {
            corridor.Id ??= string.Empty;
            corridor.TemplateAreaId ??= string.Empty;
            corridor.Route ??= [];
        }

        foreach (var link in layout.Links)
        {
            link.From ??= string.Empty;
            link.To ??= string.Empty;
            link.TileId ??= string.Empty;
        }
    }
}

internal sealed class PluginModuleManifest
{
    [JsonPropertyName("contentRefs")]
    public string[] ContentRefs { get; set; } = [];
}

internal sealed class ContentReferenceSet
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    [JsonPropertyName("workshop")]
    public ContentReferenceRule[] Workshop { get; set; } = [];

    [JsonPropertyName("quests")]
    public ContentReferenceRule[] Quests { get; set; } = [];

    [JsonPropertyName("dungeons")]
    public ContentReferenceRule[] Dungeons { get; set; } = [];

    [JsonPropertyName("monsters")]
    public ContentReferenceRule[] Monsters { get; set; } = [];

    [JsonPropertyName("heroClasses")]
    public ContentReferenceRule[] HeroClasses { get; set; } = [];

    [JsonPropertyName("heroSkills")]
    public ContentReferenceRule[] HeroSkills { get; set; } = [];

    [JsonPropertyName("effects")]
    public ContentReferenceRule[] Effects { get; set; } = [];

    [JsonPropertyName("buffs")]
    public ContentReferenceRule[] Buffs { get; set; } = [];

    [JsonPropertyName("traits")]
    public ContentReferenceRule[] Traits { get; set; } = [];

    [JsonPropertyName("quirks")]
    public ContentReferenceRule[] Quirks { get; set; } = [];

    [JsonPropertyName("trinkets")]
    public ContentReferenceRule[] Trinkets { get; set; } = [];

    [JsonPropertyName("curios")]
    public ContentReferenceRule[] Curios { get; set; } = [];

    [JsonPropertyName("lootTables")]
    public ContentReferenceRule[] LootTables { get; set; } = [];

    [JsonPropertyName("raidSettings")]
    public ContentReferenceRule[] RaidSettings { get; set; } = [];

    [JsonPropertyName("localizationKeys")]
    public ContentReferenceRule[] LocalizationKeys { get; set; } = [];

    [JsonPropertyName("mash")]
    public ContentReferenceRule[] Mash { get; set; } = [];

    [JsonPropertyName("maps")]
    public ContentReferenceRule[] Maps { get; set; } = [];

    [JsonPropertyName("mapGenerators")]
    public ContentReferenceRule[] MapGenerators { get; set; } = [];

    public static ContentReferenceSet Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var refs = JsonSerializer.Deserialize<ContentReferenceSet>(json, JsonOptions)
                ?? throw new InvalidOperationException($"Content reference file is empty: {path}");
            refs.Normalize();
            return refs;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Content reference file is invalid: {path}: {ex.Message}", ex);
        }
    }

    public void Normalize()
    {
        Workshop ??= [];
        Quests ??= [];
        Dungeons ??= [];
        Monsters ??= [];
        HeroClasses ??= [];
        HeroSkills ??= [];
        Effects ??= [];
        Buffs ??= [];
        Traits ??= [];
        Quirks ??= [];
        Trinkets ??= [];
        Curios ??= [];
        LootTables ??= [];
        RaidSettings ??= [];
        LocalizationKeys ??= [];
        Mash ??= [];
        Maps ??= [];
        MapGenerators ??= [];

        foreach (var reference in EnumerateRules())
        {
            reference.Id ??= string.Empty;
            reference.Path ??= string.Empty;
            reference.Provider ??= string.Empty;
            reference.WorkshopId ??= string.Empty;
            reference.PluginId ??= string.Empty;
            reference.Label ??= string.Empty;
        }
    }

    public IEnumerable<ContentReferenceRule> EnumerateRules()
    {
        return Workshop
            .Concat(Quests)
            .Concat(Dungeons)
            .Concat(Monsters)
            .Concat(HeroClasses)
            .Concat(HeroSkills)
            .Concat(Effects)
            .Concat(Buffs)
            .Concat(Traits)
            .Concat(Quirks)
            .Concat(Trinkets)
            .Concat(Curios)
            .Concat(LootTables)
            .Concat(RaidSettings)
            .Concat(LocalizationKeys)
            .Concat(Mash)
            .Concat(Maps)
            .Concat(MapGenerators);
    }
}

internal sealed class ContentReferenceRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("workshopId")]
    public string WorkshopId { get; set; } = string.Empty;

    [JsonPropertyName("pluginId")]
    public string PluginId { get; set; } = string.Empty;

    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}

internal sealed class VirtualFileRule
{
    [JsonPropertyName("when")]
    public PatchCondition? When { get; set; }

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    [JsonPropertyName("sourcePath")]
    public string SourcePath { get; set; } = string.Empty;

    [JsonPropertyName("replacements")]
    public VirtualFileReplacement[] Replacements { get; set; } = [];

    [JsonPropertyName("operations")]
    public VirtualFileOperation[] Operations { get; set; } = [];
}

internal sealed class MapTemplateRule
{
    [JsonPropertyName("when")]
    public PatchCondition? When { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("specPath")]
    public string SpecPath { get; set; } = string.Empty;

    [JsonPropertyName("spec")]
    public JsonElement Spec { get; set; }
}

internal sealed class MapLayoutTemplateRule
{
    [JsonPropertyName("when")]
    public PatchCondition? When { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("layout")]
    public MapLayoutDefinition Layout { get; set; } = new();

    [JsonPropertyName("tiles")]
    public MapLayoutTileRule[] Tiles { get; set; } = [];

    [JsonPropertyName("encounters")]
    public MapLayoutEncounterRule[] Encounters { get; set; } = [];
}

internal sealed class MapLayoutDefinition
{
    [JsonPropertyName("entrance")]
    public string Entrance { get; set; } = string.Empty;

    [JsonPropertyName("finalRoom")]
    public string FinalRoom { get; set; } = string.Empty;

    [JsonPropertyName("rooms")]
    public MapLayoutRoomRule[] Rooms { get; set; } = [];

    [JsonPropertyName("corridors")]
    public MapLayoutCorridorRule[] Corridors { get; set; } = [];

    [JsonPropertyName("links")]
    public MapLayoutLinkRule[] Links { get; set; } = [];
}

internal sealed class MapLayoutRoomRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("templateAreaId")]
    public string TemplateAreaId { get; set; } = string.Empty;

    [JsonPropertyName("position")]
    public double[] Position { get; set; } = [];
}

internal sealed class MapLayoutCorridorRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("templateAreaId")]
    public string TemplateAreaId { get; set; } = string.Empty;

    [JsonPropertyName("route")]
    public double[][] Route { get; set; } = [];
}

internal sealed class MapLayoutLinkRule
{
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("tile")]
    public int? Tile { get; set; }

    [JsonPropertyName("tileId")]
    public string TileId { get; set; } = string.Empty;
}

internal sealed class MapLayoutTileRule
{
    [JsonPropertyName("area")]
    public string Area { get; set; } = string.Empty;

    [JsonPropertyName("tile")]
    public int? Tile { get; set; }

    [JsonPropertyName("tileId")]
    public string TileId { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public JsonElement Content { get; set; }

    [JsonPropertyName("light")]
    public int? Light { get; set; }

    [JsonPropertyName("knowledge")]
    public int? Knowledge { get; set; }

    [JsonPropertyName("mashIndex")]
    public int? MashIndex { get; set; }

    [JsonPropertyName("mashType")]
    public int? MashType { get; set; }

    [JsonPropertyName("curioPropHash")]
    public int? CurioPropHash { get; set; }

    [JsonPropertyName("trapHash")]
    public int? TrapHash { get; set; }

    [JsonPropertyName("critScout")]
    public bool? CritScout { get; set; }

    [JsonPropertyName("encounter")]
    public string Encounter { get; set; } = string.Empty;
}

internal sealed class MapLayoutEncounterRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("mash")]
    public string Mash { get; set; } = string.Empty;
}

internal sealed class QuestChainRule
{
    [JsonPropertyName("when")]
    public PatchCondition? When { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("unlock")]
    public QuestChainUnlockRule Unlock { get; set; } = new();

    [JsonPropertyName("questBoard")]
    public QuestChainBoardRule QuestBoard { get; set; } = new();

    [JsonPropertyName("stages")]
    public QuestChainStageRule[] Stages { get; set; } = [];
}

internal sealed class QuestChainUnlockRule
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("questId")]
    public string QuestId { get; set; } = string.Empty;

    [JsonPropertyName("phase")]
    public string Phase { get; set; } = string.Empty;
}

internal sealed class QuestChainBoardRule
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("questIdSource")]
    public string QuestIdSource { get; set; } = string.Empty;

    [JsonPropertyName("removeCompleted")]
    public bool RemoveCompleted { get; set; }

    [JsonPropertyName("completedStateKey")]
    public string CompletedStateKey { get; set; } = string.Empty;
}

internal sealed class QuestChainStageRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("order")]
    public int? Order { get; set; }

    [JsonPropertyName("sourceQuestId")]
    public string SourceQuestId { get; set; } = string.Empty;

    [JsonPropertyName("targetQuestId")]
    public string TargetQuestId { get; set; } = string.Empty;

    [JsonPropertyName("mapLayoutTemplateId")]
    public string MapLayoutTemplateId { get; set; } = string.Empty;

    [JsonPropertyName("mapTemplateId")]
    public string MapTemplateId { get; set; } = string.Empty;

    [JsonPropertyName("region")]
    public string Region { get; set; } = string.Empty;

    [JsonPropertyName("difficulty")]
    public int? Difficulty { get; set; }

    [JsonPropertyName("tags")]
    public string[] Tags { get; set; } = [];
}

internal sealed class QuestBoardPolicyRule
{
    [JsonPropertyName("when")]
    public PatchCondition? When { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("refreshTriggers")]
    public string[] RefreshTriggers { get; set; } = [];

    [JsonPropertyName("entries")]
    public QuestBoardPolicyEntryRule[] Entries { get; set; } = [];
}

internal sealed class QuestBoardPolicyEntryRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("questId")]
    public string QuestId { get; set; } = string.Empty;

    [JsonPropertyName("sourceQuestId")]
    public string SourceQuestId { get; set; } = string.Empty;

    [JsonPropertyName("pool")]
    public string Pool { get; set; } = string.Empty;

    [JsonPropertyName("weight")]
    public int? Weight { get; set; }

    [JsonPropertyName("availableWhen")]
    public QuestBoardPolicyAvailableWhenRule AvailableWhen { get; set; } = new();

    [JsonPropertyName("onCompleted")]
    public string OnCompleted { get; set; } = string.Empty;

    [JsonPropertyName("required")]
    public bool? Required { get; set; }
}

internal sealed class QuestBoardPolicyAvailableWhenRule
{
    [JsonPropertyName("completedQuest")]
    public string CompletedQuest { get; set; } = string.Empty;

    [JsonPropertyName("completedQuests")]
    public string[] CompletedQuests { get; set; } = [];

    [JsonPropertyName("notCompletedQuest")]
    public string NotCompletedQuest { get; set; } = string.Empty;

    [JsonPropertyName("notCompletedQuests")]
    public string[] NotCompletedQuests { get; set; } = [];

    [JsonPropertyName("weekGte")]
    public int? WeekGte { get; set; }

    [JsonPropertyName("weekLte")]
    public int? WeekLte { get; set; }

    [JsonPropertyName("weekEq")]
    public int? WeekEq { get; set; }

    [JsonPropertyName("phase")]
    public string Phase { get; set; } = string.Empty;

    [JsonPropertyName("stateKey")]
    public string StateKey { get; set; } = string.Empty;

    [JsonPropertyName("stateEquals")]
    public string StateEquals { get; set; } = string.Empty;
}

internal sealed class PatchCondition
{
    [JsonPropertyName("modsPresent")]
    public string[] ModsPresent { get; set; } = [];

    [JsonPropertyName("modsAbsent")]
    public string[] ModsAbsent { get; set; } = [];

    [JsonPropertyName("capabilitiesPresent")]
    public string[] CapabilitiesPresent { get; set; } = [];

    [JsonPropertyName("capabilitiesAbsent")]
    public string[] CapabilitiesAbsent { get; set; } = [];
}

internal sealed class VirtualFileReplacement
{
    [JsonPropertyName("find")]
    public string Find { get; set; } = string.Empty;

    [JsonPropertyName("replace")]
    public string Replace { get; set; } = string.Empty;

    [JsonIgnore]
    public PatchReplacementOrigin? Origin { get; set; }
}

internal sealed class VirtualFileOperation
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("match")]
    public string Match { get; set; } = string.Empty;

    [JsonPropertyName("prefix")]
    public string Prefix { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    public string Line { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}
