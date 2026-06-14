using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static partial class ManagedActionSaveApplier
{
    private const string ProfilePolicyFileName = "_ddrt_profile_policy.json";

    private static void ApplyInventoryDisableItemSale(ApplyContext context, string artifactPath, JsonObject artifact)
    {
        var itemKind = ReadString(artifact, "plan.arguments.itemKind");
        if (string.IsNullOrWhiteSpace(itemKind))
        {
            throw new InvalidDataException("plan.arguments.itemKind must not be empty.");
        }

        var disabled = ReadBool(ReadNode(artifact, "plan.arguments.disabled"), "plan.arguments.disabled");
        var policyFile = LoadProfilePolicyFile(context);
        var saleDisabled = EnsureObject(policyFile.Root, "profilePolicies.inventory.saleDisabled");
        var changed = SetJsonPropertyIfChanged(saleDisabled, itemKind, disabled, context.WriteChanges);
        if (changed)
        {
            policyFile.MarkChanged();
        }

        context.Issues.Add(new ManagedActionApplyIssue(
            "warning",
            "managed-action-hard-lockout-not-verified",
            artifactPath,
            $"inventory.disableItemSale recorded decoded profile policy for itemKind={itemKind}; hard UI/economy lockout still requires a verified runtime or save consumer"));

        AddSuccessfulAction(
            context,
            artifactPath,
            artifact,
            policyFile.Path,
            [
                $"record inventory sale policy itemKind={itemKind} disabled={disabled}",
                "no verified persist.estate sale-disable field or safe content-level sale toggle exists"
            ]);
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
