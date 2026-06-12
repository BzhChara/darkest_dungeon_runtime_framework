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

    [JsonPropertyName("virtualFileRules")]
    public VirtualFileRule[] VirtualFileRules { get; set; } = [];

    [JsonPropertyName("mapTemplates")]
    public MapTemplateRule[] MapTemplates { get; set; } = [];

    [JsonPropertyName("mapLayoutTemplates")]
    public MapLayoutTemplateRule[] MapLayoutTemplates { get; set; } = [];

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
            manifest.VirtualFileRules ??= [];
            manifest.MapTemplates ??= [];
            manifest.MapLayoutTemplates ??= [];
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
                    tile.Content ??= string.Empty;
                    tile.Encounter ??= string.Empty;
                }

                rule.Encounters ??= [];
                foreach (var encounter in rule.Encounters)
                {
                    encounter.Id ??= string.Empty;
                    encounter.Mash ??= string.Empty;
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
    public string Content { get; set; } = string.Empty;

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
