# Darkest Dungeon Runtime Framework

This is a runtime mod loader / hook framework prototype for the Steam Windows version of Darkest Dungeon 1.

The current stage is a PoC skeleton:

- The C# launcher reads configuration, validates paths, and starts the game.
- The C# launcher injects `RuntimeHook.dll` into the game process.
- The C++ DLL writes logs after it is loaded.
- The file-read hook uses MinHook to observe `CreateFileW/CreateFileA` and logs only paths with matching extensions.
- Event probe v0 uses `CreateFileW/CreateFileA/WriteFile` plus file lifecycle APIs to observe file open, write attempts, moves, copies, deletes, replacements, and attribute changes. It only writes logs and does not change game behavior.

## Current Boundary

The first stage does not:

- Modify original game files.
- Modify Workshop mod files.
- Modify saves.
- Hook combat, AI, skill resolution, or UI rendering.
- Bypass Steam, DRM, anti-cheat, or operating-system security mechanisms.

## Default Target

The default configuration points to the Steam 64-bit entry point:

```text
E:/Steam/steamapps/common/DarkestDungeon/_windows/win64/Darkest.exe
```

Testing the 32-bit version requires building both a 32-bit launcher and a 32-bit DLL. The current skeleton prioritizes x64.

`gameArguments` can pass game launch arguments, such as `["-forcetown"]` during tests to force returning to town. The default value is an empty array.

## Directory Layout

```text
config/default_config.json      Default configuration
launcher/                       C# launcher
runtime/                        C++ RuntimeHook.dll
runtime/hooks/                  Hook module interfaces
plugins/                        Plugin patch manifest directory
logs/                           Launcher and DLL logs
state/                          Framework sidecar state; generated runtime content is normally not committed
docs/architecture.md            Architecture notes
```

## Build Requirements

- .NET SDK 8.0 or later, used to build the launcher.
- Visual Studio 2026 / Build Tools with Desktop development with C++, used to build `RuntimeHook.dll`.

No NuGet packages are required. The current project uses the VS2026 `v145` platform toolset. File IO observation hooks use MinHook `v1.3.4` from `third_party/minhook`.

## File IO Observation Configuration

These fields in `config/default_config.json` control file-read logging:

```json
"fileIoHookEnabled": true,
"fileIoObserveOnly": true,
"fileIoLogExtensions": [".darkest", ".loc", ".json", ".xml", ".png", ".atlas", ".skel", ".font", ".ttf", ".otf", ".shader", ".txt"],
"fileIoMaxLogEntries": 2000,
"fileIoDeduplicate": true
```

`fileIoHookEnabled` is the master switch for file API hooks. `fileIoObserveOnly` only controls ordinary file-open observation logs. Even when it is `false`, RuntimeHook still installs the required file hooks if the event probe or virtual file rules are enabled. To fully disable file IO hooks, set `fileIoHookEnabled` to `false`. To fully disable injection, set `enableInjection` to `false`.

By default, the launcher uses `startSuspendedForInjection: true`: it starts the game suspended, injects and installs hooks, then resumes the main thread. This allows early startup resource reads to be observed.

## Event Probe v0

The event probe is the lowest-risk starting point for the future event layer. It currently only observes file activity. It does not intercept, cancel, or rewrite writes:

```json
"eventProbeEnabled": true,
"eventProbeLogFileOpen": true,
"eventProbeLogFileWrite": true,
"eventProbeLogSaveFiles": true,
"eventProbeLogDataFiles": false,
"eventProbeLogAssetFiles": false,
"eventProbeMaxLogEntries": 5000,
"eventProbeMaxSaveLogEntries": 20000,
"eventProbeIgnorePathFragments": [
  "Steam/logs/",
  "gameoverlay_renderer.txt"
],
"availabilityProbeEnabled": true,
"availabilityProbeCaptureStack": true,
"availabilityProbeMaxLogEntries": 500
```

Current event names:

- `data.file_opened`
- `data.file_write_attempted`
- `asset.file_opened`
- `asset.file_write_attempted`
- `save.file_opened`
- `save.file_write_attempted`
- `save.file_move_attempted`
- `save.file_copy_attempted`
- `save.file_delete_attempted`
- `save.file_replace_attempted`
- `save.file_set_attributes_attempted`
- `availability.candidate_file_opened`
- `availability.candidate_file_write_attempted`
- `availability.candidate_file_move_attempted`
- `availability.candidate_file_copy_attempted`
- `availability.candidate_file_delete_attempted`
- `availability.candidate_file_replace_attempted`
- `availability.candidate_file_set_attributes_attempted`

Save events are sampled by default. Data-file and asset events are disabled by default so startup-time mod, localization, layout, texture, and Steam overlay logs do not consume the event budget. `eventProbeMaxLogEntries` controls the ordinary `data` / `asset` event budget. `eventProbeMaxSaveLogEntries` separately controls the `save` event budget, so save reads and writes are not pushed out by ordinary file noise. The `save` category is detected heuristically: files are classified as saves when they are under Steam userdata `262060/remote/profile_*`, under Documents Darkest `profile_*`, or have names like `persist.*`.

The availability probe is narrower. It only becomes active when the launcher passes at least one managed availability policy from `logs/managed_action_overlay_manifest.json`. It watches candidate profile files such as `persist.roster.json`, `persist.estate.json`, `persist.raid.json`, `persist.quest.json`, and related campaign/town files, and can attach a short module-offset stack summary. This is observe-only evidence for choosing a later hard runtime/UI/save consumer; it does not block party selection or trinket equipment.

## Save Directory Sidecar Watcher

Live game tests showed that some save writes under `E:/Steam/userdata/.../262060/remote/profile_*` are not always performed directly by `Darkest.exe`. A DLL file API hook injected into the game process may therefore miss those writes. The launcher-side watcher fills that gap:

```json
"saveWatchEnabled": true,
"saveWatchDirectories": [],
"saveWatchAfterExitSeconds": 10,
"saveEventBridgeDebounceMilliseconds": 1000
```

When `saveWatchDirectories` is empty, the launcher infers the Steam root from `gameWorkingDirectory`, watches existing `userdata/*/262060/remote` directories, and also watches `Documents/Darkest` if it exists. When the watcher is enabled, the launcher waits for the game process to exit and then continues listening for `saveWatchAfterExitSeconds` seconds to catch later writes from Steam or external sync processes, such as `persist.*.json` and `backup` changes.

The watcher only logs and does not modify saves. Realtime events and exit snapshot diffs are written to `logs/launcher.log`. Event names start with `save.sidecar_*`, for example:

- `save.sidecar_created`
- `save.sidecar_changed`
- `save.sidecar_deleted`
- `save.sidecar_renamed`
- `save.sidecar_snapshot_created`
- `save.sidecar_snapshot_changed`
- `save.sidecar_snapshot_deleted`

After the exit snapshot, the watcher also emits noise-reduced summaries grouped by `profile_*` and stable `.json` files. Temporary files such as `.stmp` and `~RF*.TMP` are ignored:

- `save.sidecar_session_summary`
- `save.sidecar_profile_summary`
- `save.sidecar_profile_files`

For example, a town-stay session may summarize updates to `profile_3` files such as `persist.game.json`, `persist.narration.json`, and `backup/persist.*.json`, without requiring manual cleanup of many temporary rename events.

Each watcher session also writes a structured report:

```text
logs/save_sessions/<sessionId>.json
```

The report contains start/end time, game process information, watched directories, event counts, snapshot stats, stable JSON file changes grouped by profile, and the inferred `activeProfile`. `activeProfile` is only a diagnostic hint with `confidence` and `reasons`: for example, changes to `persist.game.json`, `persist.narration.json`, and many `backup/persist.*.json` files together look more like the active campaign profile, while only `persist.circus_estate.json` or `persist.rankings.json` changes reduce campaign-profile confidence. The framework does not write saves or block startup based on this inference.

If an `activeProfile` exists, the watcher also writes a read-only state report for that profile:

```text
logs/save_states/<sessionId>.json
```

DD1 `persist.*.json` files use a `.json` extension, but Steam saves store their actual contents in a DSON binary container. The state report does not pretend to fully deserialize those files. It records file size, timestamps, SHA-256, binary headers, DSON header/meta summaries, visible marker strings, a small number of nearby inline string key/value candidates, limited DSON scalar/object path samples, and conservative `facts`. The report `parseStatus` states whether the file is currently `dsonPartialDecoded`, `binaryStringIndexOnly`, or ordinary `parsedJsonText`. This gives future state models and binary-format parsing a stable contract.

The same exit also writes a read-only file-map report:

```text
logs/save_file_maps/<sessionId>.json
```

The file map scans live and backup `persist*.json` files under the active profile and records whether each file is a current core candidate, its priority, category, mod relevance, current coverage level, DSON summary, and access issues. It is used to choose future decoding order. It does not mean every file already has a full semantic model.

## Decoded Profile Workspace

Real `profile_*` saves are read-only by default. To validate profile initialization or managed action writes, decode the save into a project-local workspace first:

```powershell
.\tools\PrepareDecodedProfileWorkspace.ps1
.\tools\PrepareDecodedProfileWorkspace.ps1 -Initialize
.\tools\PrepareDecodedProfileWorkspace.ps1 -Initialize -WriteManagedActions
```

The default source is the test profile `E:\Steam\userdata\1097809614\262060\remote\profile_3`. The script only reads top-level `persist*.json` files in that directory and uses `.research\DDSaveEditor-v0.0.70\DDSaveEditor.jar` to decode them into:

```text
state/decoded_profiles/<session>/decoded_save
state/decoded_profiles/<session>/mod_state
```

Reports are written both into the workspace and to `logs/decoded_profile_workspaces/<session>.json`. `-Initialize` calls `--initialize-decoded-profile`, still in dry-run mode by default. Only adding `-WriteManagedActions` writes to the project-local decoded JSON copy. This flow never writes back to the original save under Steam userdata.

## Save Event Bridge

The save event bridge converts read-only save state facts into framework events, then passes them to ordinary `eventRules`. Conversion rules are declared by enabled plugin `factEventRules`; they are not hardcoded in C# for one gameplay mode. The bridge does not write original `profile_*` files and does not directly modify game UI, quest lists, or combat flow. It is currently an observe-first bridge into sidecar state.

```json
"saveEventBridgeEnabled": false
```

It is disabled by default. When enabled, the launcher sidecar watcher tries to infer events after writing `logs/save_states/<sessionId>.json`, and writes:

```text
logs/save_event_bridge_report.json
```

When the watcher observes stable `.json` save changes under `profile_*` while the game is running, it debounces by `saveEventBridgeDebounceMilliseconds`, generates realtime state reports, and runs the same bridge logic:

```text
logs/save_states/<watchSessionId>_realtime_<n>.json
```

The realtime bridge skips known non-campaign or network auxiliary files, such as `persist.circus_estate.json`, `persist.rankings.json`, `persist.mp_progression.json`, `persist.roster.network.json`, and `novelty_tracker_mp.json`. Unknown `.json` files remain eligible so future save files or gameplay extensions are not silently blocked.

The realtime bridge still only reads original saves and only writes framework sidecar state. On game exit, the original final session report and save state report remain for complete diagnostics and file-map analysis.

You can also run inference manually against one save state report:

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/rule_contract_validation_config.json --mod-state-id validation.challenge_run_contract --infer-save-events --save-state-report ./logs/save_states/<sessionId>.json --no-inject
```

Live game observation uses a dedicated configuration. It does not write original `profile_*` saves. It prepares challenge sidecar state, materializes the managed action overlay for the current stage, starts the game with RuntimeHook injected, and enables the save watcher and save event bridge:

```powershell
.\tools\StartLiveChallengeObserve.ps1
```

The compatibility entry point `.\tools\StartChallengeSaveBridgeObserve.ps1` remains available and delegates to the new live observe script. The script creates a fresh sidecar state directory for each observation, initializes `validation.challenge_run_contract`, emits `challenge.run_started` and `challenge.stage_selection_started`, and then starts the game. After entering `profile_3`, choose the boss quest for the current stage. Save changes trigger the save event bridge in realtime. After exiting the game, inspect:

```text
logs/save_sessions/<sessionId>.json
logs/save_states/<sessionId>.json
logs/save_event_bridge_report.json
state/live_challenge_observe/<sessionId>/validation.challenge_run_contract.json
```

`factEventRules` can read `fact.*`, plugin `state.*`, and bridge context, then write fields into the emitted event payload. Payloads can declare generic array projections, such as filtering current raid party members from `facts.heroes` and expanding their `trinketIds`, or using `where` to filter `campaignLog.partyRaidRecords` for the matching stage completion record. The validation plugin currently uses these rules to emit `challenge.stage_selection_confirmed` from active raid facts or post-quest campaign log facts, and `challenge.stage_completed` / `challenge.stage_failed` from last raid quest/result facts. During the same bridge pass, after one event writes sidecar state, later rules re-read state. This lets a post-quest save first infer selection confirmation, then advance the completion event. Actual quest injection, hero UI filtering, and trinket UI filtering are not hardcoded in this bridge. They are declared by ordinary `eventRules`, first materialized as managed action artifacts, and then consumed by overlay/hook layers as capabilities mature.

The watcher realtime bridge can be tested without starting the game:

```powershell
.\tools\TestRealtimeSaveBridge.ps1
```

```json
{
  "factEventRules": [
    {
      "id": "emit_stage_completed_from_last_raid",
      "emit": "challenge.stage_completed",
      "requiresCapabilities": ["state.sidecar", "challenge.observe_stage_completed"],
      "when": {
        "all": [
          { "state": "challengeRun.lockedStageSelection", "op": "exists" },
          { "fact": "progression.lastRaidSuccess", "op": "equals", "value": true },
          {
            "fact": "progression.lastRaidQuest.names",
            "op": "contains",
            "valueFromState": "challengeRun.currentStage.sourceQuestId"
          }
        ]
      },
      "payload": {
        "stageId": { "fromState": "challengeRun.currentStage.id" },
        "observedQuestNames": { "fromFact": "progression.lastRaidQuest.names" },
        "saveStateReportPath": { "fromBridge": "saveStateReportPath" }
      }
    }
  ]
}
```

## Framework Mod State

Runtime mod state is not written into original `profile_*` saves. The launcher initializes plugin `stateSchema` into an independent directory:

```json
"modStateDirectory": "./state/mod_state",
"allowNonAtomicStateWrites": false
```

Relative paths resolve under the framework project root and must remain inside the project directory to avoid accidental writes to the game directory or Steam userdata. Generated state files are ignored by `.gitignore` by default.

State writes require successful `.tmp` atomic replacement by default. If atomic write fails, the command fails and records `state-atomic-write-failed`; it does not automatically fall back to direct overwrite. Non-atomic direct writes are allowed only when `allowNonAtomicStateWrites: true` is explicitly configured or `--allow-non-atomic-state-writes` is passed. The report then records a `state-write-fallback-non-atomic` warning and `writeMode=non-atomic-fallback`.

State commands:

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --init-mod-state --no-inject
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --dump-mod-state --no-inject
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --mod-state-id validation.challenge_run_contract --init-mod-state --dump-mod-state --no-inject
```

- `--init-mod-state`: create or merge default keys from currently enabled plugin `stateSchema` without clearing existing state.
- `--dump-mod-state`: read current sidecar state, print a summary, and write `logs/mod_state_dump_report.json`.
- `--mod-state-id <plugin-id>`: process only the specified plugin state.
- `--mod-state-dir <path>`: use another sidecar state directory for this run. The path must still stay inside the framework project directory.
- `--allow-non-atomic-state-writes`: allow non-atomic state writes for this run. Use only in development environments restricted by sandboxing, antivirus, or permission policy. Normal environments should leave this disabled.

A single plugin writes to `state/mod_state/<plugin-id>.json` by default. If multiple enabled plugins repeat the same `id`, the filename gets a manifest-path hash suffix to avoid overwrites.

## Event Rule Executor

`--emit-event` can simulate a framework event without starting the game. Matching safe `eventRules` are executed in the current plugin load order and results are written back to sidecar state:

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/rule_contract_validation_config.json --mod-state-id validation.challenge_run_contract --emit-event challenge.stage_selection_confirmed --event-payload-file ./logs/runtime_event_executor_test/payloads/selection_confirmed.json --no-inject
```

The current executor implements safe state actions such as `state.addUniqueRange`, `state.incrementCounter`, `selection.lock`, `challenge.recordFailedAttempt`, `challenge.advanceStage`, and `challenge.initializeRunState`. Some `managed` game-behavior actions generate auditable artifacts but still do not perform live game mutation: `quest.injectFixedStage`, `roster.filterAvailableHeroes`, and `equipment.filterAvailableTrinkets` are reported in `logs/runtime_event_report.json` with `status: "materialized"`, `materializedActionCount`, `plan`, and `artifactPath`, and the full artifact is written under `modStateDirectory/_managed_actions/`. Other unimplemented actions still fail the event if marked `required:true`. Implemented and materialized action parameters are handled strictly: missing referenced `event.xxx` or `state.xxx` paths, explicit parameter type errors, and invalid definition file paths fail the action instead of continuing with empty or default values.

Before starting the game or during `--dry-run`, the launcher compiles consumable artifacts under `_managed_actions/` into:

```text
logs/managed_action_overlay_manifest.json
```

The overlay compiler consumes several artifact families. `quest.injectFixedStage` and `questBoard.replaceWithFixedSet` can produce plot quest virtual replacements, and `trinket.patchEntry` can produce trinket entry `sourcePath` overlays for explicit id or field-selector patches. `roster.enforceAvailabilityFilter` / `equipment.enforceAvailabilityFilter` are now exposed in the manifest as `availabilityPolicies`. Availability policies are deliberately manifest-only today: they are stable inputs for the next runtime/UI/save consumer, not claims that the original party UI is already blocked. RuntimeHook consumes the virtual file rules through the existing channel and logs manifest path, size, overlay count, policy count, and issue count through `DD_RUNTIME_MANAGED_OVERLAY_MANIFEST`. This is not a full quest-pool or UI takeover yet.

## Virtual File Prototype

The virtual file channel is enabled in the default configuration, but it does not change any game reads when no rules are enabled:

```json
"virtualFileEnabled": true
```

To globally disable virtual file replacement, set `virtualFileEnabled` to `false`.

Rule list format:

```json
"virtualFileRules": [
  {
    "target": "shared/app.darkest",
    "replacements": [
      {
        "find": ".max_campaign_log_file_size 0 ",
        "replace": ".max_campaign_log_file_size 0"
      }
    ]
  }
]
```

`target` uses relative path suffix matching. A rule can contain multiple `replacements`. Test configuration `config/virtual_file_test_config.json` returns `shared/app.darkest` as an in-memory virtual file and performs only a no-semantics string replacement: removing the trailing space from the `.max_campaign_log_file_size 0 ` line. This test does not write to disk or modify the original file. It only verifies that the file-read path can be replaced.

## Plugin Patch Manifests

The launcher scans one layer of plugin directories under `pluginDirectories` and reads each plugin directory's `patches.json`:

```json
"pluginDirectories": [
  "./plugins"
],
"pluginPatchManifestName": "patches.json"
```

Plugin manifest format:

```json
{
  "id": "author.my_runtime_patch",
  "name": "My Runtime Patch",
  "version": "0.1.0",
  "enabled": true,
  "capabilities": [
    "file.virtualize",
    "content.patch",
    "content.app_config"
  ],
  "phase": "normal",
  "priority": 0,
  "depends": [],
  "optionalDepends": [],
  "loadAfter": [],
  "loadBefore": [],
  "conflicts": [],
  "virtualFileRules": [
    {
      "when": {
        "modsPresent": [],
        "modsAbsent": [],
        "capabilitiesPresent": [],
        "capabilitiesAbsent": []
      },
      "target": "shared/app.darkest",
      "operations": [
        {
          "type": "setValue",
          "key": ".max_campaign_log_file_size",
          "value": "0"
        }
      ]
    }
  ]
}
```

Players can create `plugins/<plugin-id>/patches.json` and set `enabled` to `true`; the launcher will include it in the load plan automatically. Load order first considers `depends`, `optionalDepends`, `loadAfter`, and `loadBefore`, then `phase` and `priority`. When multiple manifests target the same file, replacements are generated step by step in final load order before being passed to the DLL. `plugins/example/patches.json` is a disabled example.

Load relationship rules:

- `depends`: required dependencies. If missing, the current plugin is skipped and a warning is logged.
- `optionalDepends`: ordered after the target when it exists; ignored when it does not exist.
- `loadAfter` / `loadBefore`: affect ordering only and do not require the target to exist.
- `phase` order is `base`, `early`, `normal`, `compat`, `late`.
- Lower `priority` values load earlier. The default is `0`.
- Duplicate `id` values, declared conflicts, and ordering cycles log warnings by default instead of blocking startup.

Capability declarations describe what a plugin intends to use or provide:

```json
"capabilities": [
  "file.virtualize",
  "content.patch",
  "content.quest",
  "content.region"
]
```

First suggested capability names:

- `file.virtualize`: virtualize file reads through RuntimeHook.
- `content.patch`: modify game data text.
- `content.app_config`: modify application config such as `shared/app.darkest`.
- `content.quest`: quest, stage, or quest-chain content.
- `content.region`: dungeon, map, or region content.
- `content.localization`: localization text.
- `asset.replace`: texture, font, skeleton, atlas, and other asset replacement.

`virtualFileRules` supports two forms:

- `replacements`: low-level string replacement with explicit `find` and `replace`.
- `operations`: structured startup operations. The launcher reads the target file and compiles operations into `replacements`.

Rules can include `when` conditions. When conditions are not satisfied, the rule is not compiled, validated, previewed, or sent to the DLL, but it is still included in `--explain-patches` diagnostics:

```json
{
  "when": {
    "modsPresent": ["author.required_mod"],
    "modsAbsent": ["author.incompatible_mod"],
    "capabilitiesPresent": ["content.quest"],
    "capabilitiesAbsent": ["content.region"]
  },
  "target": "shared/app.darkest",
  "operations": []
}
```

- `modsPresent`: all listed plugin ids must be in the final enabled list.
- `modsAbsent`: all listed plugin ids must be absent from the final enabled list.
- `capabilitiesPresent`: all listed capabilities must be declared by final enabled plugins.
- `capabilitiesAbsent`: all listed capabilities must be absent from final enabled plugins.

Currently supported `operations`:

```json
{ "type": "setValue", "key": ".some_key", "value": "123" }
{ "type": "replaceLine", "match": "old full line", "line": "new full line" }
{ "type": "replaceLine", "prefix": ".some_key", "line": ".some_key 123" }
{ "type": "appendAfter", "match": "anchor line", "content": "new line" }
{ "type": "appendEnd", "content": "new line" }
```

Structured operations compile step by step from the current virtual text. A later plugin can match the result produced by an earlier plugin's `operations`. Missing target lines or replacement text produce warnings and are skipped/no-ops by default; they do not block startup. Unsafe framework execution problems, such as path escape, unreadable targets, or invalid operation types, are still errors. Legacy `replacements` also participate in ordering simulation.

Each structured operation gets a diagnostic `subject` for explanation and conflict reports:

- `setValue` uses `key:<key>`.
- `replaceLine` / `appendAfter` first try to extract `key:<key>` from `key`, `.darkest`-style `prefix`, `match`, or `line`.
- If no key can be identified, it falls back to `match:<text>`, `prefix:<text>`, or the operation type.

Besides same-line conflicts, `--preview-patches` also reports `patch-preview-key-conflict` when multiple replacements hit the same key.

Patch inspection commands:

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --list-patches
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --explain-patches
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --validate-only
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --validate-patches
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --preview-patches
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --validate-only --strict-patches
```

- `--list-patches`: list discovered manifests, load order, enabled status, source rules, and final effective rules without starting the game.
- `--explain-patches`: explain load order, ordering edges, skip reasons, capability declarations, conditional rule diagnostics, each target's source chain, and final replacement sources. Replacement sources include operation subjects. Does not start the game.
- `--validate-only`: validate enabled rule targets, target file size against the current 16 MB virtual file limit, and match counts for each `find` and operation subject in final replacement order. Does not start the game.
- `--validate-patches`: run the same validation before startup. If errors exist, exit with failure. If validation passes, continue normal startup.
- `--preview-patches`: simulate virtual file output using RuntimeHook replacement order, write results to `logs/patch_preview`, and do not start the game.
- `--strict-patches`: promote patch compilation warnings and unmatched replacement validation warnings to failures. Multi-rule same-target diagnostics remain warnings. Disabled by default.

To choose a preview directory:

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --preview-patches --preview-output ./logs/my_preview
```

The preview directory contains:

- `summary.txt`: target file, original size, virtual size, and replacement count.
- `<target>.preview.txt`: the virtual text the game would read.
- `<target>.diff.txt`: short diffs for each replacement, including source plugin and operation subject.

`--preview-output` must remain inside the framework project directory to avoid accidental writes to the game directory or Workshop directories.

Run the virtual file test configuration:

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/virtual_file_test_config.json
```

Run the plugin manifest test configuration:

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/plugin_patch_test_config.json
```

## Expected Run Flow

```text
1. Build runtime/RuntimeHook.vcxproj to generate runtime/bin/x64/Release/RuntimeHook.dll
2. Build launcher/DDRuntimeLoader.csproj
3. Run the launcher from the project root
4. Inspect logs/launcher.log and logs/runtime_hook.log
```

## Rollback

This framework does not modify the game directory. To roll back, close the launcher and start the game through Steam normally.
