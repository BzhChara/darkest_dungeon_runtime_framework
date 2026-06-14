# Capability and Rule Contract

This document defines the generic runtime rule model. It is intentionally not a list of special gameplay templates. A new mod idea should be decomposed into facts, events, predicates, actions, state, and capabilities. If the idea cannot be represented, the framework should add or improve a primitive in one of those categories instead of adding one-off gameplay logic. Use `docs/framework_capability_matrix.md` as the checklist before promoting a concrete idea into a framework capability.

## Current Status

- `virtualFileRules` are implemented and executable. They support ordered text replacements/operations for text-like game data, and whole-file `sourcePath` overlays for project-local generated binary or data files.
- `mapTemplates` are implemented as a plugin-declared fixed-map compiler. They mutate existing scalar fields in a source `.dm` template, validate the generated `.dm`, write it under `modStateDirectory/_map_templates/`, and append a normal `sourcePath` virtual file rule for the declared game target. This keeps fixed-map customization on the same overlay path as other generated assets.
- Map inspection and template reports now expose `map.topology` facts for entrance/final reachability, reachable and unreachable areas, door edge counts, invalid door targets, and hard topology issues.
- `mapLayoutTemplates` are parsed from plugin manifests, validated as high-level layout declarations, then compiled to the existing low-level `mapTemplates` writer when the requested layout can be represented with existing areas, tiles, and door slots. The implemented slice writes diagnostics under `modStateDirectory/_map_layout_templates/`, checks source `.dm` areas, room/corridor roles, duplicate room positions, graph connectivity, tile references, and encounter references, then can generate a compiled spec, `.dm` artifact, low-level map template report, and normal `sourcePath` runtime overlay. It still refuses unsupported creation/deletion of map objects and named encounter materialization.
- `questChains` are parsed from plugin manifests as ordered quest/chapter declarations. The implemented slice validates stage order, unlock metadata, and map template references, writes reports under `modStateDirectory/_quest_chains/`, and supports two task-board outputs: `questBoard.mode="replaceWithFixedSet"` explicitly materializes deterministic `questBoard.replaceWithFixedSet` artifacts, while `questBoard.mode="linearProgression"` generates `questBoardPolicies` entries from the ordered stages so a long A -> B -> C chain does not need hand-written policy boilerplate. `--preview-quest-board` can resolve materialized artifacts into a final fixed-board report from original plot quest content before any save write. Startup and `--dry-run` now compile active fixed-board plot quest ids into the managed overlay manifest, forcing those plot quests to early/repeatable availability through the normal virtual file layer. Launch preflight also reports the parallel runtime save overlay status for `profile_*/persist.quest.json`. `--refresh-quest-board-profile <profileId>` can explicitly materialize the generated `persist.quest.json` replacement into a watched profile, with dry-run, backup, path checks, and running-game protection. When `questBoardAutoRefreshEnabled` is set, the save watcher can also reapply the same generated board after a successfully bridged stable campaign save batch, even when the changed file was not `persist.quest.json`. These are targeted quest-board refresh paths, not full campaign week settlement simulations.
- Content reference boundaries are part of the contract. Static content authoring such as new monster files, monster skills, art, audio, localization, ordinary curio definitions, and ordinary loot tables should usually stay in base/DLC/Workshop/plugin content packs. The framework should make those ids referencable from rules, maps, encounters, spawn pools, quests, and reward policies; it should not add runtime code just to reproduce existing content authoring workflows. See `docs/content_reference_boundaries.md`.
- `eventRules` are parsed, explained, and can be exercised through `--emit-event` for implemented safe actions and selected materialized managed action artifacts.
- `factEventRules` are parsed, explained, and can be exercised through `--infer-save-events` to convert save/content/runtime facts into ordinary framework events.
- `stateSchema` is parsed from enabled plugins and can be initialized/read as sidecar state through `--init-mod-state` and `--dump-mod-state`.
- `--explain-rules` reports declared `eventRules` and `factEventRules`, required capabilities, action capabilities, and skip reasons.
- The first safe action executor supports sidecar state primitives, fixed-stage challenge state primitives, and generic boss-gauntlet pressure-test primitives such as idempotent definition merge, selection locking/consumption, attempt recording, success-gated wallet rewards, successful quest completion, all-completed phase transition, and phase-gated managed action materialization. The executor also supports managed materialization for selected actions: fixed-stage quest injection, dry-run hero/trinket list previews, sidecar hero/trinket reuse state, and profile-normalization plans for roster, upgrade purchases, stagecoach, trinket inventory, wallet resource maps, trinket sale lockout, campaign plot progress reset, town state, town store suppression, town event, and fixed quest board. These actions report `materialized`, include a `plan` object, write a sidecar artifact under `modStateDirectory/_managed_actions/`, and do not mutate the game. Startup and `--dry-run` compile consumable `quest.injectFixedStage` and `questBoard.replaceWithFixedSet` artifacts into `logs/managed_action_overlay_manifest.json`; the plot-quest consumer appends virtual file rules for the relevant `quest.plot_quests.json` files and forces selected source plot quests to `dungeon_level = 0` and `is_repeatable = true`. Startup and `--dry-run` also expose `inventory.disableItemSale` artifacts as sale policies, generate trinket `price = 0` sourcePath overlays when `method: content_price_zero` is explicit, and consume `town.unlockAllBuildings` artifacts by generating town building `.building.json` `sourcePath` overlays with entrance requirements set to 0; trinket hard sell-button disable still requires live validation or a runtime/UI/save consumer. `--preview-quest-board` can inspect `questBoard.replaceWithFixedSet` artifacts and write `logs/quest_board_preview_report.json` with the final resolved board; startup and `--dry-run` also write `logs/quest_board_launch_preflight_report.json` to show both the content overlay and runtime `persist.quest.json` overlay readiness. `--refresh-quest-board-profile <profileId>` reuses that generated runtime `persist.quest.json` replacement as an explicit managed save refresh for a watched profile. It writes `logs/quest_board_profile_refresh_report.json`, backs up the original file before mutation, refuses non-project live saves while `Darkest.exe` is running by default, and does not attempt to emulate the broader original week settlement. With `questBoardAutoRefreshEnabled`, the realtime save watcher uses the same writer after any successfully bridged stable campaign save batch; non-project running-game writes still require `questBoardAutoRefreshAllowRunningGameSaveWrite=true` in config. `--initialize-decoded-profile` now combines sidecar initialization, `profile.initialization_requested`, quest-board preview, managed-action apply, and per-action apply details into one project-local decoded-save initialization report. `--apply-managed-actions` can dry-run these artifacts against a project-local decoded JSON save copy, and `--write-managed-actions` currently writes wallet resource map actions and trinket inventory count actions to decoded `persist.estate.json`, hero class instance generation, hero progression normalization, selected combat/camping skill slots, and optional hero Darkest Dungeon participation cleanup to decoded `persist.roster.json`, content-defined upgrade purchases to decoded `persist.upgrades.json`, stagecoach recruit suppression plus district built flags plus requested store item/generated pool clearing to decoded `persist.town.json`, current-event suppression to decoded `persist.town_event.json`, declared plot progress reset to decoded `persist.progression.json`, content-derived fixed quest board entries to decoded `persist.quest.json`, and trinket sale / town-event text policy to project-local `_ddrt_profile_policy.json`. `--apply-continuous-profile-actions` is the narrower reapply mode for settlement drift: it selects only the latest continuous stagecoach/store/town-event/policy artifacts per source group and deliberately excludes one-time setup such as wallet, trinket inventory, generated roster, upgrades, campaign progress reset, and quest-board replacement. Newer `questBoard.replaceWithFixedSet` artifacts supersede older ones during preview/refresh, so plugins can switch from one board to another by materializing a phase-gated board after a state transition or a generated linear `questChains` policy after completion facts change. Trinket inventory source resolution can exclude content rarities such as `darkest_dungeon` and `trophy`, and generated roster quirks respect content `singleton` tags across a generation pass. `tools/PrepareDecodedProfileWorkspace.ps1 -EncodeInitializedProfile` can turn that initialized decoded workspace into a project-local `encoded_profile` and immediately roundtrip decode/parse it for validation. `tools/PromoteEncodedProfileWorkspace.ps1` can then dry-run or explicitly write that `encoded_profile` to a target profile with target guards, running-game protection for external writes, full target snapshot backup, hash verification, and manifest-based restore for overwritten files; by default it only promotes encoded files whose decoded content changed in the workspace report, while `-PromoteAllEncodedFiles` opts into full encoded-profile promotion. Promotion-added files are reported and left in place rather than deleted automatically. `--preview-managed-action-retention` and `--prune-managed-actions` provide explicit sidecar artifact retention reports for `_managed_actions/`; invalid artifacts are retained with warnings, and delete failures are errors rather than fallback paths. Town-event text policy still needs an original content/save consumer before it changes live game behavior; consumed hero/trinket restrictions still need original-first projection before they can stop party selection. The boss-gauntlet plugin no longer materializes `town.setBuildingLevels`; ordinary building levels are represented by verified upgrade purchases. Stage coach/store suppression and pre-finale hero/trinket reuse are not yet live-enforced after original week settlement or party UI interactions.

- The realtime save watcher can now run two managed reconciliation paths after the same stable save bridge: quest-board refresh and continuous profile action auto-apply. Quest-board policy materialization writes `status=empty` markers when no policy entries are currently selected, and quest-board preview/overlay compilation uses those markers to supersede stale dynamic board artifacts. Continuous profile auto-apply decodes live profile files into a project-local workspace, reuses the existing continuous managed action applier, re-encodes changed `persist.*.json` files, backs up live targets, and writes only when its running-game write guard allows it.
- Implemented safe actions and managed plan actions validate their declared arguments strictly. Missing referenced `event.*`, `state.*`, or `challenge.*` paths, invalid explicit argument types, and missing definition files fail the action and are written to `logs/runtime_event_report.json`.
- Save facts are exported from original-game persist files and documented in `docs/save_field_map.md`.
- `--infer-save-events` evaluates active plugin `factEventRules` against a save state report and emits matching framework events through the same `eventRules` executor.
- Runtime hooks are currently observe-first. Intercepting game flow remains capability-gated work.

## Core Model

```text
facts       current known game/save/content state
events      points in game flow or launcher flow
predicates  boolean checks over facts, event payload, and mod state
actions     requested changes or side effects
state       sidecar persistent plugin data
capability  named framework power required to observe or mutate something
```

The rule engine should evaluate rules in this order:

1. Build active plugin load order.
2. Build active capability set.
3. Load sidecar state.
4. Listen for a framework event.
5. Evaluate matching `eventRules`.
6. Execute actions in rule order, honoring capability and risk policy. Current implementation executes implemented safe actions and can materialize observe-first artifacts for a small set of managed actions.
7. Write diagnostics and state changes.

Sidecar state writes are strict by default: the state store writes a temporary file and requires atomic replacement to succeed. Non-atomic direct-write fallback is only available through explicit opt-in configuration or the `--allow-non-atomic-state-writes` CLI flag, and that downgrade is reported as a warning.

## Manifest Fields

The manifest may carry future runtime rules alongside existing content patches:

```json
{
  "id": "author.mod_id",
  "version": "0.1.0",
  "enabled": true,
  "capabilities": [
    "file.virtualize",
    "content.patch",
    "state.sidecar"
  ],
  "virtualFileRules": [],
  "mapTemplates": [],
  "mapLayoutTemplates": [],
  "factEventRules": [],
  "eventRules": [],
  "stateSchema": {}
}
```

`eventRules` do not replace `virtualFileRules`. Content patches remain the right primitive for static text, data, localization, and assets. Event rules are for game-flow changes, cross-week behavior, plugin state, and runtime decisions.

`factEventRules` do not execute gameplay actions directly. They observe already-extracted facts, optionally compare them with sidecar state, build an event payload, and emit an ordinary framework event. This keeps save observation separate from the rule action executor.

## Event Rule Shape

```json
{
  "id": "rule_id",
  "enabled": true,
  "on": "campaign.week_advanced",
  "phase": "normal",
  "priority": 0,
  "requiresCapabilities": [
    "campaign.observe_week_advance",
    "state.sidecar"
  ],
  "when": {
    "all": [
      { "fact": "campaign.inRaid", "op": "equals", "value": false },
      { "state": "author.mod_id.someFlag", "op": "equals", "value": true }
    ]
  },
  "actions": [
    {
      "type": "state.incrementCounter",
      "capability": "state.sidecar",
      "risk": "safe",
      "required": true,
      "args": {
        "key": "author.mod_id.weekCount",
        "amount": 1
      }
    }
  ]
}
```

Required rule fields:

- `id`: stable rule id inside the plugin.
- `on`: event id.
- `actions`: ordered action list.

Optional rule fields:

- `enabled`: defaults to `true`.
- `phase`: load phase for rules on the same event. Initial values should mirror plugin phases: `base`, `early`, `normal`, `compat`, `late`.
- `priority`: lower numbers run first inside the same phase.
- `requiresCapabilities`: all listed capabilities must be available or the rule is skipped with diagnostics.
- `when`: predicate tree.

## Fact Event Rule Shape

```json
{
  "id": "emit_stage_completed_from_last_raid",
  "enabled": true,
  "emit": "challenge.stage_completed",
  "phase": "normal",
  "priority": 0,
  "requiresCapabilities": [
    "state.sidecar",
    "challenge.observe_stage_completed"
  ],
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
```

Fact event fields:

- `id`: stable rule id inside the plugin.
- `emit`: event id passed to the normal event executor.
- `requiresCapabilities`: all listed capabilities must be available or the rule is skipped with diagnostics.
- `when`: predicate tree over `fact.*`, `state.*`, and bridge context exposed as `event.*`.
- `payload`: event payload fields. Each field can be a literal value or an object using `fromFact`, `fromState`, `fromBridge`, `fromEvent`, or `value`.

Payload source objects can also apply simple projections before the event is emitted. Current projections are deliberately generic:

- `optional`: when `true`, a missing `fromFact`, `fromState`, `fromBridge`, or `fromEvent` path resolves to `null` instead of failing payload construction. Use this only for supplementary observation fields; predicates should still require the facts that are needed to safely emit the event.
- `where`: filters an array source with a predicate tree over each item. Leaf predicates use `path` for the item-relative path and support literal `value`, `valueFromFact`, `valueFromState`, `valueFromBridge`, and `valueFromEvent`.
- `whereIn`: filters an array source by comparing an item `path` with values from `values`, `valuesFromFact`, `valuesFromState`, `valuesFromBridge`, or `valuesFromEvent`.
- `selectMany`: reads a child path from every array item and flattens array children.
- `selectManyMissing`: controls missing child paths for `selectMany`; default is `error`, while `skip` treats missing or null child paths as an empty contribution for that item.
- `map` / `coerce`: supports `string` and `stringArray`.
- `distinct`: removes duplicate array items after earlier projections.

Attempt-recording actions are idempotent only when the emitted payload carries a stable attempt identity. `challenge.recordFailedAttempt` currently accepts `attemptFingerprint`, `observedAttemptId`, or `observedPartyRaidRecordCount` and stores the derived fingerprint on `stageAttempts[]`. This prevents repeated save watcher passes from recording the same observed raid result multiple times while still allowing genuinely new attempts to be recorded when the identity changes.

If a fact event rule emits an event successfully, later fact event rules in the same bridge pass reload that plugin's sidecar state before evaluating predicates. This allows a post-task save report to infer `selection_confirmed` from structured campaign log facts, then infer `stage_completed` from progression facts without waiting for another watcher pass.

Example:

```json
{
  "selectedTrinketIds": {
    "fromFact": "heroes",
    "whereIn": {
      "path": "id",
      "valuesFromFact": "raid.party.heroGuids"
    },
    "selectMany": "trinketIds",
    "map": "stringArray",
    "distinct": true
  }
}
```

## Predicate Contract

Predicates are composable and data-driven:

```json
{
  "all": [
    { "fact": "progression.totalSuccessfulQuestsFinished", "op": "greaterOrEqual", "value": 10 },
    {
      "any": [
        { "fact": "quest.lastCompleted.id", "op": "equals", "value": "some_quest" },
        { "state": "author.mod_id.overrideEnabled", "op": "equals", "value": true }
      ]
    }
  ]
}
```

Initial operators:

```text
exists
notExists
equals
notEquals
greater
greaterOrEqual
less
lessOrEqual
contains
notContains
matches
```

Address spaces:

```text
fact:<facts path from save/content/runtime facts>
event:<current event payload path>
state:<plugin sidecar state path>
capability:<capability id>
```

Short forms such as `"fact": "campaign.inRaid"` are allowed and mean `fact:campaign.inRaid`.

Leaf comparisons may use literal `"value"` or dynamic references:

```json
{ "fact": "progression.lastRaidQuest.names", "op": "contains", "valueFromState": "challengeRun.currentStage.sourceQuestId" }
```

Current dynamic reference fields are `valueFromFact`, `valueFromEvent`, and `valueFromState`.

## Action Contract

Actions are requests for framework primitives. They should not expose raw memory addresses or arbitrary native code.

```json
{
  "type": "quest.unlock",
  "capability": "quest.mutate_available_list",
  "risk": "managed",
  "required": true,
  "args": {
    "questId": "modded_intro",
    "source": "author.mod_id"
  }
}
```

Action fields:

- `type`: stable action primitive id.
- `capability`: capability required to execute it.
- `risk`: `safe`, `managed`, or `risky`.
- `required`: when true, failure follows the capability failure policy; when false, failure is logged and execution continues.
- `args`: action-specific structured arguments.

Action categories:

```text
state.*          sidecar state only
log.*            diagnostics only
content.*        virtual content patch or generated content
save.*           original save read/write through verified schema
quest.*          quest pool, quest gate, reward, or completion state
roster.*         hero pool, hero status, party filtering
town.*           town building, store, activity, recruit state
upgrade.*        upgrade purchase tree and completion state
campaign.*       campaign progression, week, ending, region gates
map.*            dungeon topology, room/hall cell content, fixed layout state
encounter.*      dungeon mash entries, named fights, monster lineup definitions
region.*         dungeon region visibility, chapter routing, region-level gates
event.*          emit or suppress framework events
ui.*             overlay or game UI mutation
native.*         explicitly risky hook or patch wrapper
```

## State Contract

Complex mods need sidecar state so they do not have to overload original save fields.

```json
{
  "stateSchema": {
    "usedHeroes": {
      "type": "array",
      "items": "string",
      "default": []
    },
    "weekCount": {
      "type": "integer",
      "default": 0
    }
  }
}
```

Sidecar state rules:

- State is namespaced by plugin instance id.
- State writes are atomic and logged.
- Corrupt sidecar state disables the affected plugin state namespace, not the original campaign save.
- Plugin uninstall keeps state unless the user explicitly resets it.
- Initial launcher support writes `state/mod_state/<plugin-id>.json` and reports state through `--dump-mod-state`.
- A destructive `--reset-mod-state <mod-id>` should be added only after policy and backup behavior are explicit.

## Capability Contract

Capabilities describe what the framework can safely observe or change. A mod declares capabilities it needs; a rule action names the capability it uses.

Capability fields for future registry entries:

```json
{
  "id": "quest.observe_completion",
  "status": "observed",
  "risk": "safe",
  "source": "runtime-hook",
  "gameExeHashes": [
    "3800fd9aa745c31eb4744b5c260502733d16dcc39b5da29b8404982d8956fb57"
  ],
  "failurePolicy": "disableCapability",
  "logs": [
    "eventId",
    "profile",
    "questId"
  ],
  "minimumTest": "complete one original quest and confirm exactly one quest.completed event"
}
```

Capability status levels:

```text
planned      documented only or emitted as an observe-first action plan
materialized observe-first action artifact exists in sidecar state, but game behavior is not changed yet
observed     hook or watcher can observe the event
passive      context can be read without changing game result
intercepted  framework can cancel or replace original behavior
stable       tested across expected original-game flows
```

Risk levels:

```text
safe     sidecar state, diagnostics, read-only facts
managed  verified schema writes or capability-wrapped intercepts
risky    memory patching, deep flow replacement, UI mutation, battle logic
```

Failure policies:

```text
disableCapability
skipRule
skipPlugin
failLaunch
```

Default policy should prefer `disableCapability` or `skipRule` unless a plugin marks an action as required and the user enabled strict behavior.

## General Gap Handling

When a mod idea cannot be expressed, classify the missing piece:

| Missing piece | Add this, not a special-case gameplay template |
| --- | --- |
| Cannot know required data | new fact extractor or content index |
| Cannot know timing | new observe-only event |
| Cannot read event context | passive event payload support |
| Cannot prevent original behavior | managed intercept capability |
| Cannot make desired change | new action primitive |
| Needs durable custom memory | sidecar state schema/action support |
| Needs one-time setup in a normal save | idempotent profile initialization capability with an initialized marker |
| Needs controlled economy changes | wallet or inventory capability with idempotent event identity |
| Needs visual feedback | overlay or UI capability |
| Needs a custom dungeon route | map topology capability plus quest/map content binding |
| Needs exact enemy composition | encounter mash capability plus named encounter placement |
| Needs unsupported engine behavior | risky native capability with exe-hash gating |

This is the rule that keeps the framework general: new gameplay ideas should expand reusable primitives, not become hardcoded modules.

Current target scenario: `docs/boss_gauntlet_campaign_spec.md`. Use it as a validation pressure test for generic primitives such as idempotent profile initialization, normal-save consequence preservation, fixed quest-board replacement, wallet/inventory economy controls, town building availability, terminal-attempt observation, selection consumption, phase transitions, fixed map topology, named encounter definitions, and phase-scoped reuse restrictions. Do not add launcher or native-hook branches that only know this specific boss-gauntlet campaign.

## Anti-Hardcoding Audit

Validation scenarios may name concrete gameplay designs, quest ids, stage ids, hero ids, or trinket ids. That is acceptable only inside validation plugins, sample fixtures, tests, and documentation examples. Framework runtime code should expose reusable primitives that plugins compose; it should not embed one mod's gameplay loop as the only path.

Current narrow slices to keep visible:

| Area | Current shape | Why it is acceptable now | Generic direction |
| --- | --- | --- | --- |
| `SaveEventBridge` | Evaluates plugin-declared `factEventRules` and can emit arbitrary framework events with payloads from facts, bridge context, sidecar state, and generic payload projections | The launcher owns the generic bridge only; the concrete active-raid and last-raid challenge mappings live in `plugins/_validation` | Keep it generic. If a new mapping needs C# changes, first add a reusable predicate, payload projection, payload source, or fact extractor |
| `RuntimeEventExecutor` challenge actions | Implements `challenge.initializeRunState`, `challenge.lockStageSelection`, `challenge.recordFailedAttempt`, and `challenge.advanceStage` directly | They are safe sidecar-state primitives used to validate stateful stage-chain behavior | Keep only if treated as reusable `challenge.*` primitives; otherwise factor repeated behavior into generic `state.*`, `event.*`, and definition-driven actions |
| `plugins/_validation` and test scripts | Contain concrete boss quest ids, stage ids, selected hero ids, and trinket ids | They are acceptance fixtures, not user-facing framework behavior | Leave concrete data in fixtures, but do not move fixture assumptions into launcher/runtime logic |
| Boss gauntlet target spec | Names a concrete fixed-resource campaign design | It is a pressure test for missing reusable primitives | Add `profile.*`, `quest_board.*`, `selection.*`, `wallet.*`, `inventory.*`, `town.*`, `stagecoach.*`, and original-first reuse projection primitives before attempting live behavior |
| Post-ending expansion map sketch | Names the post-Ancestor new-map idea | It is a pressure test for chapter chains, fixed map topology, per-cell content, and named encounters | Add `quest_chain.*`, `map.*`, `encounter.*`, and `region.*` primitives before attempting a live custom map |

Before adding a new gameplay feature, ask: can another mod with different content reuse the same primitive without changing C# or native hook code? If not, the design is still too hardcoded.

## First Implementation Slice

The next code slice should stay generic:

1. Parse and log `eventRules` counts. Done as a declaration carrier.
2. Add `--explain-rules` to print declared rules, required capabilities, and skipped reasons. Done for manifest-level rule declarations.
3. Add validation manifests for quest draft, fixed-stage challenge runs, fixed-resource boss gauntlet, and delayed building upgrades. Done as declaration-level framework acceptance scenarios.
4. Add a capability registry document or JSON schema.
5. Add sidecar state file read/write with no gameplay actions. Initial `--init-mod-state` / `--dump-mod-state` support is implemented.
6. Add an observe-only event bus sourced from existing save watcher/runtime logs. Plugin-declared `factEventRules` now bridge save state reports to ordinary runtime events.
7. Add a no-op action executor for `log.*` and `state.*`. Initial `--emit-event` support now executes implemented safe state actions against sidecar state.
8. Materialize selected managed actions into sidecar artifacts. `quest.injectFixedStage`, `roster.filterAvailableHeroes`, `equipment.filterAvailableTrinkets`, and the boss-gauntlet profile-normalization actions now write `materialized` artifacts for later overlay/hook consumers without mutating original game state.
9. Compile selected managed action artifacts into a runtime-visible overlay manifest. `quest.injectFixedStage`, `questBoard.replaceWithFixedSet`, `inventory.disableItemSale`, and `town.unlockAllBuildings` artifacts can enter `logs/managed_action_overlay_manifest.json`; stale fixed-stage and fixed-board overlays are superseded by the latest applicable artifact, and RuntimeHook records manifest visibility through `DD_RUNTIME_MANAGED_OVERLAY_*` diagnostics.
10. Feed selected overlay artifacts into an existing runtime consumer. `quest.injectFixedStage` and `questBoard.replaceWithFixedSet` now append virtual file replacements for the relevant plot quest files, using concrete plot quest ids as content anchors. `inventory.disableItemSale` remains policy-only by default; trinket artifacts with `method: content_price_zero` emit trinket entry price overlays. Hero/trinket reuse restrictions remain sidecar facts until they can be projected through verified original roster/equipment mechanisms.
11. Add a targeted save-refresh consumer for generated quest boards. `--refresh-quest-board-profile <profileId>` can write the generated fixed-board `persist.quest.json` into a configured watched profile with dry-run, backup, and safety checks, so initialization can update the current board without pretending to run the entire original campaign week settlement.
12. Add a realtime save-watch consumer for generated quest boards. When `questBoardAutoRefreshEnabled` is configured, any successfully bridged stable campaign save batch can trigger the same fixed-board writer; this covers original week-transition board regeneration without simulating the rest of week settlement.

Only after that should gameplay experiments be expressed as ordinary rules.
