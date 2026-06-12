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
            "managed-action-runtime-consumer-required",
            artifactPath,
            $"inventory.disableItemSale recorded policy for itemKind={itemKind}; live enforcement still requires a runtime/economy consumer"));

        AddSuccessfulAction(
            context,
            artifactPath,
            artifact,
            policyFile.Path,
            [
                $"record inventory sale policy itemKind={itemKind} disabled={disabled}",
                "no verified persist.estate sale-disable field exists; runtime/economy consumer required for live enforcement"
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
