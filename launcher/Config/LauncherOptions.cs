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
    public bool ApplyManagedActions { get; private set; }
    public bool WriteManagedActions { get; private set; }
    public bool InitializeDecodedProfile { get; private set; }
    public bool PreviewQuestBoard { get; private set; }
    public bool PreviewQuestBoardPolicies { get; private set; }
    public bool ResolveQuestBoardPolicies { get; private set; }
    public bool MaterializeQuestBoardPolicies { get; private set; }
    public bool AutoMaterializeQuestBoardPolicies { get; private set; }
    public bool PreviewManagedActionRetention { get; private set; }
    public bool PruneManagedActions { get; private set; }
    public bool InspectMapFile { get; private set; }
    public bool PrototypeMapFinalRoom { get; private set; }
    public bool PrototypeMapTemplate { get; private set; }
    public bool AllowRunningGameSaveWrite { get; private set; }
    public int? WatchSavesForMilliseconds { get; private set; }
    public string? ModStateId { get; private set; }
    public string? ModStateDirectory { get; private set; }
    public string? ManagedActionSaveDirectory { get; private set; }
    public string? RefreshQuestBoardProfile { get; private set; }
    public string? QuestBoardProfileScope { get; private set; }
    public string? EmitEvent { get; private set; }
    public string? EventPayload { get; private set; }
    public string? EventPayloadFile { get; private set; }
    public string? SaveStateReportPath { get; private set; }
    public int? QuestBoardPolicySlots { get; private set; }
    public int? QuestBoardPolicySeed { get; private set; }
    public int? ManagedActionRetentionKeepLatestPerGroup { get; private set; }
    public string? PreviewOutputPath { get; private set; }
    public string? MapFilePath { get; private set; }
    public string? MapReportOutputPath { get; private set; }
    public string? MapPrototypeSourcePath { get; private set; }
    public string? MapPrototypeOutputPath { get; private set; }
    public string? MapPrototypeReportOutputPath { get; private set; }
    public string? MapFinalRoomId { get; private set; }
    public string? MapTemplateSpecPath { get; private set; }

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
                case "--apply-managed-actions":
                    options.ApplyManagedActions = true;
                    break;
                case "--write-managed-actions":
                    options.WriteManagedActions = true;
                    break;
                case "--initialize-decoded-profile":
                    options.InitializeDecodedProfile = true;
                    break;
                case "--preview-quest-board":
                    options.PreviewQuestBoard = true;
                    break;
                case "--preview-quest-board-policies":
                    options.PreviewQuestBoardPolicies = true;
                    break;
                case "--resolve-quest-board-policies":
                    options.ResolveQuestBoardPolicies = true;
                    break;
                case "--materialize-quest-board-policies":
                    options.MaterializeQuestBoardPolicies = true;
                    break;
                case "--auto-materialize-quest-board-policies":
                    options.AutoMaterializeQuestBoardPolicies = true;
                    break;
                case "--preview-managed-action-retention":
                    options.PreviewManagedActionRetention = true;
                    break;
                case "--prune-managed-actions":
                    options.PruneManagedActions = true;
                    break;
                case "--inspect-map-file":
                    options.InspectMapFile = true;
                    options.MapFilePath = RequireValue(args, ref i, "--inspect-map-file");
                    break;
                case "--prototype-map-final-room":
                    options.PrototypeMapFinalRoom = true;
                    options.MapPrototypeSourcePath = RequireValue(args, ref i, "--prototype-map-final-room");
                    break;
                case "--prototype-map-template":
                    options.PrototypeMapTemplate = true;
                    options.MapPrototypeSourcePath = RequireValue(args, ref i, "--prototype-map-template");
                    break;
                case "--refresh-quest-board-profile":
                    options.RefreshQuestBoardProfile = RequireValue(args, ref i, "--refresh-quest-board-profile");
                    break;
                case "--quest-board-profile-scope":
                    options.QuestBoardProfileScope = RequireValue(args, ref i, "--quest-board-profile-scope");
                    break;
                case "--allow-running-game-save-write":
                    options.AllowRunningGameSaveWrite = true;
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
                case "--managed-action-save-dir":
                    options.ManagedActionSaveDirectory = RequireValue(args, ref i, "--managed-action-save-dir");
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
                case "--quest-board-policy-slots":
                    options.QuestBoardPolicySlots = RequirePositiveInt(args, ref i, "--quest-board-policy-slots");
                    break;
                case "--quest-board-policy-seed":
                    options.QuestBoardPolicySeed = RequireInt(args, ref i, "--quest-board-policy-seed");
                    break;
                case "--managed-action-retention-keep":
                    options.ManagedActionRetentionKeepLatestPerGroup = RequirePositiveInt(args, ref i, "--managed-action-retention-keep");
                    break;
                case "--preview-output":
                    options.PreviewOutputPath = RequireValue(args, ref i, "--preview-output");
                    break;
                case "--map-report-output":
                    options.MapReportOutputPath = RequireValue(args, ref i, "--map-report-output");
                    break;
                case "--map-prototype-output":
                    options.MapPrototypeOutputPath = RequireValue(args, ref i, "--map-prototype-output");
                    break;
                case "--map-prototype-report-output":
                    options.MapPrototypeReportOutputPath = RequireValue(args, ref i, "--map-prototype-report-output");
                    break;
                case "--map-final-room-id":
                    options.MapFinalRoomId = RequireValue(args, ref i, "--map-final-room-id");
                    break;
                case "--map-template-spec":
                    options.MapTemplateSpecPath = RequireValue(args, ref i, "--map-template-spec");
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

    private static int RequireInt(string[] args, ref int index, string name)
    {
        var value = RequireValue(args, ref index, name);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new ArgumentException($"{name} must be an integer.");
        }

        return parsed;
    }
}
