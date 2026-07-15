using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static partial class ManagedActionSaveApplier
{
    private const string ProfilePolicyFileName = "_ddrt_profile_policy.json";

    private static void RecognizeTrinketPatchEntryContentOnly(ApplyContext context, string artifactPath, JsonObject artifact)
    {
        context.Actions.Add(new ManagedActionApplyActionReport(
            artifactPath,
            ReadString(artifact, "action.type"),
            "recognized",
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
