using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal sealed record ManagedActionProfileScope(
    string Kind,
    string ProfileId,
    string ProfileRoot,
    string Source)
{
    public static ManagedActionProfileScope Global { get; } = new("global", string.Empty, string.Empty, string.Empty);

    public bool IsGlobal => !Kind.Equals("profile", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(ProfileId);

    public bool Matches(string? targetProfileId)
    {
        if (IsGlobal)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(targetProfileId) &&
            ProfileId.Equals(targetProfileId.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

internal static class ManagedActionProfileScopeResolver
{
    public static ManagedActionProfileScope FromSaveStateReport(string? saveStateReportPath)
    {
        if (string.IsNullOrWhiteSpace(saveStateReportPath) || !File.Exists(saveStateReportPath))
        {
            return ManagedActionProfileScope.Global;
        }

        var root = JsonNode.Parse(File.ReadAllText(saveStateReportPath, Encoding.UTF8)) as JsonObject;
        if (root is null)
        {
            return ManagedActionProfileScope.Global;
        }

        var activeProfile = root["activeProfile"] as JsonObject;
        var profileId = ReadString(activeProfile, "profile");
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return ManagedActionProfileScope.Global;
        }

        return new ManagedActionProfileScope(
            "profile",
            profileId.Trim(),
            ReadString(activeProfile, "root"),
            "saveStateReport.activeProfile");
    }

    public static ManagedActionProfileScope FromArtifact(JsonObject artifact)
    {
        if (artifact["profileScope"] is not JsonObject profileScope)
        {
            return ManagedActionProfileScope.Global;
        }

        return new ManagedActionProfileScope(
            ReadString(profileScope, "kind"),
            ReadString(profileScope, "profileId"),
            ReadString(profileScope, "profileRoot"),
            ReadString(profileScope, "source"));
    }

    public static JsonObject ToJson(ManagedActionProfileScope scope)
    {
        return new JsonObject
        {
            ["kind"] = scope.Kind,
            ["profileId"] = scope.ProfileId,
            ["profileRoot"] = scope.ProfileRoot,
            ["source"] = scope.Source
        };
    }

    public static string NormalizeTargetProfileId(string? profileId)
    {
        return string.IsNullOrWhiteSpace(profileId) ? string.Empty : profileId.Trim();
    }

    private static string ReadString(JsonObject? root, string key)
    {
        return root is not null && root[key] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : string.Empty;
    }
}
