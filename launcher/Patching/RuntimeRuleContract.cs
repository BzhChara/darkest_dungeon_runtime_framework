namespace DDRuntimeLoader;

internal sealed class RuntimeEventRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("on")]
    public string On { get; set; } = string.Empty;

    [JsonPropertyName("phase")]
    public string Phase { get; set; } = "normal";

    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("requiresCapabilities")]
    public string[] RequiresCapabilities { get; set; } = [];

    [JsonPropertyName("when")]
    public RuntimeRulePredicate? When { get; set; }

    [JsonPropertyName("actions")]
    public RuntimeRuleAction[] Actions { get; set; } = [];
}

internal sealed class RuntimeRulePredicate
{
    [JsonPropertyName("all")]
    public RuntimeRulePredicate[] All { get; set; } = [];

    [JsonPropertyName("any")]
    public RuntimeRulePredicate[] Any { get; set; } = [];

    [JsonPropertyName("none")]
    public RuntimeRulePredicate[] None { get; set; } = [];

    [JsonPropertyName("fact")]
    public string Fact { get; set; } = string.Empty;

    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    [JsonPropertyName("op")]
    public string Op { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;
}

internal sealed class RuntimeRuleAction
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("capability")]
    public string Capability { get; set; } = string.Empty;

    [JsonPropertyName("risk")]
    public string Risk { get; set; } = "safe";

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("args")]
    public Dictionary<string, JsonElement> Args { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
