using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static partial class ManagedActionSaveApplier
{
    private const string ProfilePolicyFileName = "_ddrt_profile_policy.json";

    private static void ApplyTrinketPatchEntryContentOnly(ApplyContext context, string artifactPath, JsonObject artifact)
    {
        context.Actions.Add(new ManagedActionApplyActionReport(
            artifactPath,
            ReadString(artifact, "action.type"),
            context.WriteChanges ? "applied" : "dry-run",
            null,
            [
                "trinket entry patch is a content overlay action",
                "no decoded save file is written by this apply pass"
            ],
            []));
    }

    private static DecodedJsonFile LoadProfilePolicyFile(ApplyContext context)
    {
        return context.LoadOrCreateJsonFile(ProfilePolicyFileName, () => new JsonObject
        {
            ["version"] = 1,
            ["profilePolicies"] = new JsonObject()
        });
    }
}
