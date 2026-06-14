using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static partial class ManagedActionOverlayCompiler
{
    private const string InventoryDisableItemSaleOverlayTarget = "profile.inventory.saleDisabled";

    private static JsonObject BuildInventoryDisableItemSaleOverlay(string artifactPath, JsonObject artifact)
    {
        var itemKind = ReadString(artifact, "plan.arguments.itemKind");
        if (string.IsNullOrWhiteSpace(itemKind))
        {
            throw new InvalidOperationException("plan.arguments.itemKind is required.");
        }

        return new JsonObject
        {
            ["kind"] = "inventory.disableItemSale",
            ["effect"] = "recordSalePolicy",
            ["target"] = InventoryDisableItemSaleOverlayTarget,
            ["artifactPath"] = artifactPath,
            ["eventId"] = ReadString(artifact, "eventId"),
            ["pluginId"] = ReadString(artifact, "pluginId"),
            ["sourceName"] = ReadString(artifact, "sourceName"),
            ["sourcePath"] = ReadString(artifact, "sourcePath"),
            ["ruleIndex"] = ReadInt(artifact, "ruleIndex"),
            ["ruleId"] = ReadString(artifact, "ruleId"),
            ["actionIndex"] = ReadInt(artifact, "actionIndex"),
            ["itemKind"] = itemKind,
            ["disabled"] = RequireBool(artifact, "plan.arguments.disabled")
        };
    }

    private static IReadOnlyList<OverlayVirtualRule> BuildInventoryDisableItemSaleVirtualRules(
        RuntimeConfig config,
        IReadOnlyList<JsonObject> overlays,
        JsonArray issues,
        LauncherLog log)
    {
        foreach (var overlay in overlays.Where(overlay =>
                     ReadString(overlay, "kind").Equals("inventory.disableItemSale", StringComparison.OrdinalIgnoreCase) &&
                     ReadBool(overlay, "disabled")))
        {
            log.Info(
                $"managed-action-overlay policy-only itemKind={Quote(ReadString(overlay, "itemKind"))} " +
                $"target={Quote(ReadString(overlay, "target"))} artifact={Quote(ReadString(overlay, "artifactPath"))}");
        }

        return [];
    }

    private static bool RequireBool(JsonObject root, string path)
    {
        if (!TryGetPath(root, path, out var node) ||
            node is not JsonValue value ||
            !value.TryGetValue<bool>(out var result))
        {
            throw new InvalidOperationException($"Expected boolean at '{path}'.");
        }

        return result;
    }

    private static string ToVirtualTarget(string gameWorkingDirectory, string path)
    {
        var relativePath = Path.GetRelativePath(gameWorkingDirectory, path);
        return relativePath.Replace('\\', '/');
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Replace('\\', '_').Replace('/', '_'))
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.Length == 0 ? "overlay.json" : builder.ToString();
    }
}
