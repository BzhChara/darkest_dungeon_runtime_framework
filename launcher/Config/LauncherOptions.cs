using System.Globalization;

namespace DDRuntimeLoader;

internal sealed class LauncherOptions
{
    public string? ConfigPath { get; private set; }
    public string? GameExecutablePath { get; private set; }
    public string? RuntimeDllPath { get; private set; }
    public bool DryRun { get; private set; }
    public bool NoInject { get; private set; }
    public bool ListPatches { get; private set; }
    public bool ExplainPatches { get; private set; }
    public bool ExplainRules { get; private set; }
    public bool ValidatePatches { get; private set; }
    public bool ValidateOnly { get; private set; }
    public bool PreviewPatches { get; private set; }
    public bool StrictPatches { get; private set; }
    public bool InitModState { get; private set; }
    public bool DumpModState { get; private set; }
    public bool AllowNonAtomicStateWrites { get; private set; }
    public bool InferSaveEvents { get; private set; }
    public int? WatchSavesForMilliseconds { get; private set; }
    public string? ModStateId { get; private set; }
    public string? ModStateDirectory { get; private set; }
    public string? EmitEvent { get; private set; }
    public string? EventPayload { get; private set; }
    public string? EventPayloadFile { get; private set; }
    public string? SaveStateReportPath { get; private set; }
    public string? PreviewOutputPath { get; private set; }

    public static LauncherOptions Parse(string[] args)
    {
        var options = new LauncherOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config":
                    options.ConfigPath = RequireValue(args, ref i, "--config");
                    break;
                case "--game":
                    options.GameExecutablePath = RequireValue(args, ref i, "--game");
                    break;
                case "--dll":
                    options.RuntimeDllPath = RequireValue(args, ref i, "--dll");
                    break;
                case "--dry-run":
                    options.DryRun = true;
                    break;
                case "--no-inject":
                    options.NoInject = true;
                    break;
                case "--list-patches":
                    options.ListPatches = true;
                    break;
                case "--explain-patches":
                    options.ExplainPatches = true;
                    break;
                case "--explain-rules":
                    options.ExplainRules = true;
                    break;
                case "--validate-patches":
                    options.ValidatePatches = true;
                    break;
                case "--validate-only":
                    options.ValidateOnly = true;
                    options.ValidatePatches = true;
                    break;
                case "--preview-patches":
                    options.PreviewPatches = true;
                    break;
                case "--strict-patches":
                    options.StrictPatches = true;
                    break;
                case "--init-mod-state":
                    options.InitModState = true;
                    break;
                case "--dump-mod-state":
                    options.DumpModState = true;
                    break;
                case "--allow-non-atomic-state-writes":
                    options.AllowNonAtomicStateWrites = true;
                    break;
                case "--infer-save-events":
                    options.InferSaveEvents = true;
                    break;
                case "--watch-saves-for-ms":
                    options.WatchSavesForMilliseconds = RequirePositiveInt(args, ref i, "--watch-saves-for-ms");
                    break;
                case "--mod-state-id":
                    options.ModStateId = RequireValue(args, ref i, "--mod-state-id");
                    break;
                case "--mod-state-dir":
                    options.ModStateDirectory = RequireValue(args, ref i, "--mod-state-dir");
                    break;
                case "--emit-event":
                    options.EmitEvent = RequireValue(args, ref i, "--emit-event");
                    break;
                case "--event-payload":
                    options.EventPayload = RequireValue(args, ref i, "--event-payload");
                    break;
                case "--event-payload-file":
                    options.EventPayloadFile = RequireValue(args, ref i, "--event-payload-file");
                    break;
                case "--save-state-report":
                    options.SaveStateReportPath = RequireValue(args, ref i, "--save-state-report");
                    break;
                case "--preview-output":
                    options.PreviewOutputPath = RequireValue(args, ref i, "--preview-output");
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }
        return options;
    }

    private static string RequireValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {name}.");
        index++;
        return args[index];
    }

    private static int RequirePositiveInt(string[] args, ref int index, string name)
    {
        var value = RequireValue(args, ref index, name);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException($"{name} must be a positive integer.");
        }

        return parsed;
    }
}
