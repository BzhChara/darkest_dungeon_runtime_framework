using System.Text.Json.Nodes;

namespace DDRuntimeLoader;

internal static class ManagedActionProducerCatalogReporter
{
    private const string ReportFileName = "managed_action_producer_catalog.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Write(RuntimeConfig config, PatchPlan patchPlan, LauncherLog log)
    {
        var reportPath = Path.Combine(config.LogDirectory, ReportFileName);
        var producers = new JsonArray();
        foreach (var producer in patchPlan.ManagedActionProducers)
        {
            producers.Add(producer.ToJson());
        }

        var report = new JsonObject
        {
            ["version"] = 1,
            ["generatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["artifactVersion"] = ManagedActionProducerContractFactory.ArtifactVersion,
            ["producerCount"] = producers.Count,
            ["producers"] = producers
        };

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
        File.WriteAllText(reportPath, report.ToJsonString(JsonOptions), Encoding.UTF8);
        log.Info($"managed-action-producer-catalog report path={Quote(reportPath)} producers={producers.Count}");
        return reportPath;
    }

    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
}
