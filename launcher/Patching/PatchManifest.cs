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
            manifest.EventRules ??= [];
            manifest.FactEventRules ??= [];
            manifest.StateSchema ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
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
}

internal sealed class VirtualFileRule
{
    [JsonPropertyName("when")]
    public PatchCondition? When { get; set; }

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    [JsonPropertyName("replacements")]
    public VirtualFileReplacement[] Replacements { get; set; } = [];

    [JsonPropertyName("operations")]
    public VirtualFileOperation[] Operations { get; set; } = [];
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
