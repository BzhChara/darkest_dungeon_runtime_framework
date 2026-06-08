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
}
