# Plugins

Runtime plugin patch manifests live here.

Minimum supported format:

```text
plugins/<plugin-id>/patches.json
```

`patches.json` currently supports plugin metadata, executable `virtualFileRules`, fixed `.dm` map templates through `mapTemplates`, high-level map layout templates through `mapLayoutTemplates`, quest/chapter ordering through `questChains`, quest-board scheduling declarations through `questBoardPolicies`, safe `eventRules`, save-fact-derived `factEventRules`, and isolated sidecar `stateSchema`.

Virtual file rules can contain low-level `replacements` or startup-compiled structured `operations`. `mapTemplates` generate project-local `.dm` artifacts before launch, then automatically become `sourcePath` virtual file rules. `mapLayoutTemplates` validate high-level room/corridor graphs, compile them into restricted low-level `mapTemplates` specs, and then generate the same `.dm` artifacts and `sourcePath` virtual file rules.

`questChains` validate fixed stage order, unlock conditions, and map template references, then write sidecar validation reports. `questBoard.mode="replaceWithFixedSet"` generates deterministic `questBoard.replaceWithFixedSet` managed action artifacts. `questBoard.mode="linearProgression"` expands stage order into `questBoardPolicies` policy facts.

`questBoardPolicies` are the candidate resolution and materialization primitive. They validate quest-board availability conditions, refresh triggers, and completion handling; write sidecar policy facts; preview enabled plot quest content through `--preview-quest-board-policies`; resolve eligible quest ids from week, completed quests, and sidecar state through `--resolve-quest-board-policies --save-state-report <path>`; and explicitly generate same-shaped `questBoard.replaceWithFixedSet` managed action artifacts through `--materialize-quest-board-policies`. When `questBoardPolicyAutoMaterializeEnabled=true`, the save-event bridge can generate the latest artifact automatically while reading save facts. Save reports with `activeProfile` make artifacts carry `profileScope`, so preview/refresh consumes only global artifacts or artifacts matching the active profile. This avoids cross-profile quest-board pollution. This layer still does not directly write `persist.quest.json` or simulate week settlement.

Complex plugins should not keep adding all content to `patches.json`. Treat `patches.json` as an entry index that points to domain files such as quests, maps, encounters, spawn pools, and `contentRefs`. The launcher computes plugin load order first, then builds final virtual file rules in order, then hands them to RuntimeHook.dll through environment variables.

`eventRules` can execute implemented safe actions through `--emit-event`, or materialize recognized managed actions into sidecar artifacts. `factEventRules` can turn facts from a save state report into normal framework events through `--infer-save-events`, then pass them to `eventRules`. Payload projection supports a limited generic array toolkit: `where`, `whereIn`, expansion, stringification, and distinct values. Contract details live in `docs/capability_rule_contract.md`.

Manifest fields:

```json
{
  "id": "author.mod_id",
  "name": "Readable Mod Name",
  "version": "0.1.0",
  "enabled": true,
  "capabilities": [
    "file.virtualize",
    "content.patch"
  ],
  "phase": "normal",
  "priority": 0,
  "depends": [],
  "optionalDepends": [],
  "loadAfter": [],
  "loadBefore": [],
  "conflicts": [],
  "modules": {
    "contentRefs": ["content/refs.json"],
    "questChains": ["quests/*.chain.json"],
    "mapLayouts": ["maps/*.layout.json"],
    "encounters": ["encounters/*.json"],
    "spawnPools": ["spawn_pools/*.json"]
  },
  "virtualFileRules": [
    {
      "when": {
        "modsPresent": [],
        "modsAbsent": [],
        "capabilitiesPresent": [],
        "capabilitiesAbsent": []
      },
      "target": "shared/app.darkest",
      "operations": []
    }
  ],
  "mapTemplates": [
    {
      "id": "dd4_custom_finale",
      "target": "maps/DD_map4.dm",
      "source": "maps/DD_map4.dm",
      "specPath": "maps/dd4_custom_finale.spec.json"
    }
  ],
  "mapLayoutTemplates": [
    {
      "id": "dd4_layout_probe",
      "target": "maps/DD_map4.dm",
      "source": "maps/DD_map4.dm",
      "layout": {
        "entrance": "start",
        "finalRoom": "boss",
        "rooms": [
          { "id": "start", "templateAreaId": "rooA", "position": [1, 2] },
          { "id": "boss", "templateAreaId": "rooC", "position": [20, 2] }
        ],
        "corridors": [
          { "id": "main_path", "templateAreaId": "corA", "route": [[2, 2], [3, 2]] }
        ],
        "links": [
          { "from": "start", "to": "main_path", "tile": 0 },
          { "from": "main_path", "to": "boss", "tile": 27 }
        ]
      },
      "tiles": [
        { "area": "boss", "tile": 0, "content": 8, "knowledge": 1, "critScout": true }
      ],
      "encounters": []
    }
  ],
  "questChains": [
    {
      "id": "post_ancestor_probe_chain",
      "name": "Post Ancestor Probe Chain",
      "mode": "fixed_order",
      "unlock": {
        "type": "afterQuest",
        "questId": "plot_final_boss"
      },
      "questBoard": {
        "enabled": true,
        "mode": "replaceWithFixedSet",
        "questIdSource": "sourceQuestId",
        "removeCompleted": false
      },
      "stages": [
        {
          "id": "stage_01_layout_probe",
          "name": "Layout Probe",
          "order": 0,
          "sourceQuestId": "plot_dd_4",
          "targetQuestId": "probe_stage_01",
          "mapLayoutTemplateId": "dd4_layout_probe",
          "region": "darkestdungeon",
          "difficulty": 6,
          "tags": ["boss", "post_ancestor"]
        }
      ]
    }
  ],
  "questBoardPolicies": [
    {
      "id": "post_ancestor_board_policy",
      "name": "Post Ancestor Board Policy",
      "mode": "mixed",
      "refreshTriggers": ["onProfileInitialize", "onWeekAdvance", "immediateOnQuestComplete"],
      "entries": [
        {
          "id": "stage_01_after_final_boss",
          "questId": "plot_dd_4",
          "availableWhen": {
            "completedQuest": "plot_final_boss",
            "weekGte": 5
          },
          "onCompleted": "remove"
        }
      ]
    }
  ],
  "factEventRules": [],
  "eventRules": [],
  "stateSchema": {}
}
```

Load relationships:

- `depends`: required dependency. If missing, skip the current plugin and warn without blocking other plugins.
- `optionalDepends`: if the target exists, order after it; if absent, ignore it.
- `loadAfter` / `loadBefore`: ordering only. They do not mean the target must exist.
- `phase` order is `base`, `early`, `normal`, `compat`, `late`.
- Lower `priority` loads earlier. Default is `0`.
- Duplicate `id` and `conflicts` warn by default and do not directly block launch.

Rule-level conditions:

- `when.modsPresent`: the rule applies only when every listed plugin id is enabled and not skipped.
- `when.modsAbsent`: the rule applies only when every listed plugin id is absent, disabled, or skipped.
- `when.capabilitiesPresent`: the rule applies only when every listed capability is declared by final enabled plugins.
- `when.capabilitiesAbsent`: the rule applies only when every listed capability is absent from final enabled plugins.
- Rules whose conditions are not satisfied appear only in explain diagnostics and do not participate in compilation, validation, preview, or runtime replacement.

Content reference boundaries:

- The framework does not need to implement full default authoring tools for new monsters, new skills, animation, textures, audio, localization, ordinary curios, or loot. Base game files, DLC files, Workshop mods, or plugin-provided files can provide that static content.
- The framework should provide `contentRefs`, dependency declarations, existence validation, load-source reports, and the ability to reference this content from `encounters`, `spawnPools`, `questChains`, and `mapLayoutTemplates`.
- For example, a new monster can come from Workshop content. The framework only needs to reference `monsterId` in an encounter table and report a required dependency if it is missing, instead of duplicating a monster authoring tool.
- Detailed boundaries live in `docs/content_reference_boundaries.md`.

Recommended complex plugin layout:

```text
plugins/author.mod_id/
  patches.json
  content/refs.json
  quests/*.chain.json
  maps/*.layout.json
  encounters/*.json
  spawn_pools/*.json
  loot/*.json
  localization/*.json
  assets/...
```

Note: `modules.contentRefs`, `encounters`, `spawnPools`, `lootPolicies`, and similar fields are the recommended direction. They are not all first-class schema yet. When adding capabilities, prefer this layered structure instead of continuing to expand a single `patches.json`.

## Fixed Map Templates

- `mapTemplates` and `mapLayoutTemplates` are optional/experimental fixed `.dm` overlay and topology diagnostic capabilities. They are not the default custom-map authoring path. Ordinary random maps, region resources, encounter pools, and full fixed maps should usually come from original DD / Workshop / plugin content files, then be referenced or scheduled by the framework through `contentRefs`, quest-board rules, and the virtual file layer.
- `mapTemplates[].target` is the in-game virtual target path, for example `maps/DD_map4.dm`.
- `mapTemplates[].source` is the template `.dm` to copy and modify. Relative paths resolve against the game directory first, then against the current plugin directory if not found. If omitted, it defaults to `target`.
- `mapTemplates[].specPath` is the rewrite spec. Relative paths resolve against the current plugin directory.
- `mapTemplates[].spec` can inline the spec. Exactly one of `specPath` or `spec` is required.
- Generated files are written to `modStateDirectory/_map_templates/<plugin-id>/` and automatically added to final `sourcePath` overlays.
- `mapTemplates[].when` uses the same condition rules as `virtualFileRules[].when`.
- Currently only existing `.dm` scalar fields can be modified. The framework cannot create/delete area, tile, or door objects yet.

## High-Level Map Layouts

- `mapLayoutTemplates[].target` and `source` use the same path resolution as `mapTemplates`.
- `layout.rooms[].templateAreaId` and `layout.corridors[].templateAreaId` point to existing areas in the source `.dm`.
- `layout.entrance`, `layout.finalRoom`, and `layout.links` are validated as a graph that can reach the final room from the entrance.
- `tiles[]` can write supported dynamic tile fields: `content`, `light`, `knowledge`, `mashIndex`, `mashType`, `curioPropHash`, `trapHash`, and `critScout`.
- `content` may be a number or numeric string. Only `empty` / `none` are additionally supported as aliases for `0`; other symbolic names are not guessed.
- Generated reports, compiled specs, `.dm` artifacts, and low-level template reports are written to `modStateDirectory/_map_layout_templates/<plugin-id>/`.
- `compileReady=true` in reports means the restricted compiler passed and generated a runtime overlay. Creating/deleting area, tile, or door objects, or materializing named encounters, still fails.

## Quest Chains

- `questChains[]` describes fixed-order or staged quest/chapter chains. It can support boss gauntlets, post-Ancestor chapters, or other custom quest flows.
- `questChains[].unlock.type="afterQuest"` requires `unlock.questId` and means the chain is expected to open after that plot quest is completed.
- `stages[].order` explicitly controls stage order. If omitted, array order is used. Duplicate order or duplicate stage id is reported as a compile error.
- `stages[].sourceQuestId` is the original quest template source for the current implementable slice. A future mature custom quest writer can extend this beyond original quest sources.
- A stage may reference `mapLayoutTemplateId` or `mapTemplateId`, but not both. Missing references are compile errors.
- `questBoard.enabled=true` is explicit opt-in. `mode="replaceWithFixedSet"` writes original plot quest ids in stage order as a static `questBoard.replaceWithFixedSet` managed artifact. `mode="linearProgression"` expands stage order into `questBoardPolicies`, so long A -> B -> C chains do not require repeated manual prerequisites. Both modes currently require `questIdSource="sourceQuestId"`.
- Static fixed-set artifacts are compiled at launch and `--dry-run` into `campaign/quest/quest.plot_quests.json` content overlays that make those quests early-available and repeatable. Linear progression requires the policy materializer to generate the current-stage artifact from save facts and sidecar state, then hand it to the same consumer.
- Validation reports are written to `modStateDirectory/_quest_chains/<plugin-id>/`; quest-board materialization reports are written there too. Only static fixed-set mode writes directly to `modStateDirectory/_managed_actions/`. Linear progression writes policy reports first and generates the current-stage artifact after policy materialization. These files do not themselves modify original saves or UI.

## Quest Board Policies

- `questBoardPolicies[]` describes when quests may appear on the quest board. It does not directly define quests, monsters, maps, or art resources. Underlying quest content should usually come from base game, DLC, Workshop, or plugin-provided DD format files and be declared through `contentRefs.quests`.
- `mode` currently supports `fixed`, `random`, and `mixed`. `refreshTriggers` currently supports `onProfileInitialize`, `onWeekAdvance`, `immediateOnQuestComplete`, and `manual`.
- `entries[].availableWhen` may declare `completedQuest(s)`, `notCompletedQuest(s)`, `weekGte`, `weekLte`, `weekEq`, `phase`, and `stateKey/stateEquals`.
- `entries[].onCompleted` currently supports `keep`, `remove`, `replace`, and `advancePhase`. If omitted, it is reported as `keep`.
- The current implementation performs schema validation, log explanation, sidecar reporting, content candidate preview, facts-driven candidate resolution, and explicit quest-board artifact materialization. It writes `modStateDirectory/_quest_board_policies/<plugin-id>/`, `logs/quest_board_policy_preview_report.json`, `logs/quest_board_policy_resolve_report.json`, `logs/quest_board_policy_materialize_report.json`, and `modStateDirectory/_managed_actions/*_questBoardPolicies_questBoard.replaceWithFixedSet.json`. It does not directly mutate `persist.quest.json` or simulate week settlement.
- `--materialize-quest-board-policies` reuses resolve results, selects fixed candidates in load order, performs reproducible selection for pool/weighted candidates, and supports `--quest-board-policy-slots <n>` and `--quest-board-policy-seed <int>`. Its output still goes through the existing `questBoard.replaceWithFixedSet` consumer.
- `questBoardPolicyAutoMaterializeEnabled=true` lets `SaveEventBridge` run the same materialization logic after reading a save-state report and write status into `logs/save_event_bridge_report.json` under `questBoardPolicyMaterialization`. If the save-state report exposes `activeProfile.profile`, generated artifacts carry `profileScope`. `--preview-quest-board --quest-board-profile-scope <profileId>`, `--refresh-quest-board-profile <profileId>`, and the realtime watcher consume only global or matching-profile artifacts. With `questBoardAutoRefreshEnabled=true`, the realtime watcher can generate the latest policy artifact after original live `persist.quest.json` writes, then use the existing fixed-board refresh writer.
- Detailed schema lives in `docs/quest_board_policies.md`.

First capability names:

- `file.virtualize`
- `content.patch`
- `content.app_config`
- `content.quest`
- `quest.chain.define`
- `quest_board.policy`
- `content.region`
- `content.localization`
- `asset.replace`
- `state.sidecar`
- `campaign.observe_week_advance`
- `quest.observe_completion`
- `save.observe_write`

Diagnostic commands:

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --explain-patches
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/rule_contract_validation_config.json --explain-rules --no-inject
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/rule_contract_validation_config.json --preview-quest-board-policies --no-inject
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/rule_contract_validation_config.json --resolve-quest-board-policies --save-state-report ./logs/quest_board_policy_contract_test/policy_week_6_necromancer_completed.json --no-inject
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/rule_contract_validation_config.json --init-mod-state --dump-mod-state --no-inject
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/rule_contract_validation_config.json --mod-state-id validation.challenge_run_contract --emit-event challenge.stage_completed --event-payload-file ./payload.json --no-inject
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/rule_contract_validation_config.json --mod-state-id validation.challenge_run_contract --infer-save-events --save-state-report ./logs/save_states/<sessionId>.json --no-inject
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --preview-managed-action-retention --managed-action-retention-keep 5 --no-inject
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --prune-managed-actions --managed-action-retention-keep 5 --no-inject
```

`--explain-patches` outputs:

- Final `order`, `status`, `phase`, `priority`, `capabilities`, and skip reason for each plugin.
- Counts for each plugin's `virtualRules`, `mapTemplates`, and `mapLayoutTemplates`.
- Counts for each plugin's `questBoardPolicies`, plus enabled policy mode, refresh trigger, entry, and availableWhen summaries.
- Ordering edges such as `mod.a -> mod.b reason=depends`.
- Load diagnostics for duplicate ids, missing dependencies, declared conflicts, and order cycles.
- Which plugin rules modify each `target`, which rules were skipped by `when`, and final replacement sources.
- Replacement operation subjects such as `key:.max_campaign_log_file_size`.

`--preview-patches` includes operation subjects in diffs and records `patch-preview-key-conflict` when multiple plugins modify the same `.darkest` key.

`--explain-rules` outputs declarative `eventRules` and `factEventRules` events, required capability, action capability, risk level, and skip reason.

`--init-mod-state` writes enabled plugin `stateSchema` defaults into `state/mod_state/<plugin-id>.json`. Existing files only receive missing keys and are not reset. `--dump-mod-state` reads sidecar state and writes `logs/mod_state_dump_report.json`.

`--emit-event` executes safe rule actions matching an event and writes `logs/runtime_event_report.json`. Current safe sidecar state and challenge state actions write real sidecar state. `quest.injectFixedStage`, `roster.filterAvailableHeroes`, `equipment.filterAvailableTrinkets`, and boss-gauntlet profile-normalization actions produce `materialized` artifacts under `modStateDirectory/_managed_actions/` without mutating the game or original saves.

Before launch or `--dry-run`, the launcher compiles consumable `quest.injectFixedStage` and `questBoard.replaceWithFixedSet` artifacts into `logs/managed_action_overlay_manifest.json`, then appends virtual replacements for related plot quest source files so source plot quests become `dungeon_level: 0` and `is_repeatable: true`. `inventory.disableItemSale` artifacts enter the manifest policy area and record sale-disable intent; with explicit `method: content_price_zero`, they also generate trinket entry overlays that suppress sale values. `trinket.patchEntry` artifacts can apply explicit `set`/`remove` edits to selected existing trinket ids. Hero/trinket reuse restrictions are kept as sidecar selection facts until a verified original roster or equipment projection exists.

`--refresh-quest-board-profile <profileId>` reuses the fixed quest-board runtime replacement to explicitly refresh the configured watched profile. It supports `--dry-run` preview, backs up before real writes, and rejects external save writes while the real game is running by default. With `questBoardAutoRefreshEnabled`, the realtime save watcher can use the same refresh writer after original live `persist.quest.json` writes. External running-game writes also require `questBoardAutoRefreshAllowRunningGameSaveWrite=true`.

`--apply-managed-actions` can dry-run these artifacts against project-local decoded JSON save copies. With explicit `--write-managed-actions`, current writers can update wallet resources, trinket inventory counts, roster class instances, roster progression, roster hero skill lists, content-defined upgrade purchases, stagecoach generated recruit suppression, district built flags, campaign plot progress reset, town-event current-event suppression, and `_ddrt_profile_policy.json` entries for trinket-sale and town-event message policy. `--apply-continuous-profile-actions` is the narrower reapply mode for original week-settlement drift: it selects the latest artifact per action/plugin/rule/profile-scope/source group and only replays stagecoach recruit suppression, town store suppression, trinket sale policy, and current town-event suppression. It does not replay one-time setup such as starting wallet resources, initial trinket inventory, hero generation, upgrade purchases, campaign progress reset, quest-board replacement, or sidecar-only selection consumption. `inventory.disableItemSale` still needs a hard runtime/UI/save consumer to truly block selling; consumed hero availability should next be tested through original roster missing/status projection before any UI hook; town-event message policy still waits for an original town-event content/save consumer. `--initialize-decoded-profile` inlines managed apply action/file details into the initialization report. Unmaterialized managed gameplay changes are reported as unimplemented.

`--preview-managed-action-retention` scans `modStateDirectory/_managed_actions/` and writes `logs/managed_action_retention_report.json`; it reports which artifacts exceed the retention count but does not delete files. `--prune-managed-actions` performs the deletion. Group keys include action type, plugin id, rule id, action index, target, `profileScope`, and `sourcePath`, so different profiles or sources do not clean up each other. Invalid artifacts are retained with warnings. Delete failures are errors and fail the command; there is no silent fallback that hides filesystem problems. The default retention count comes from `managedActionRetentionKeepLatestPerGroup` and can be overridden with `--managed-action-retention-keep <n>`.

`--infer-save-events` reads a save state report, derives events from enabled plugin `factEventRules`, and writes `logs/save_event_bridge_report.json`. The bridge does not write original saves; it only turns observed facts into normal framework events.

`example/patches.json` defaults to `enabled:false`; copy it into your own plugin before enabling it.

Native C ABI, Lua, or C# script layers are future work.
