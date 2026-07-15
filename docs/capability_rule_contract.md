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
- `FrameworkCapabilityRegistry` is the framework-owned source of truth for capability availability and runtime action contracts. Plugin declarations still drive `when.capabilitiesPresent` / `when.capabilitiesAbsent`, but declarations do not create framework support. An event or fact rule is active only when its required capabilities are declared by that same plugin and registered as available. Runtime actions additionally validate registered type, capability mapping, and risk before the rule becomes active.
- Managed action artifacts use schema version 2. Every artifact carries a `producer` contract that identifies its plugin/source, rule, event, action, capability, risk, and canonical definition SHA-256. Consumers accept it only when that exact producer is present in the current `PatchPlan` and the definition hash still matches. `logs/managed_action_producer_catalog.json` exposes the active producer set. Version 1 artifacts are unsupported; no compatibility reader promotes them to version 2.
- The first safe action executor supports sidecar state primitives and boss-gauntlet pressure-test primitives such as idempotent definition merge, selection locking/consumption, attempt recording, success-gated wallet rewards, successful quest completion, all-completed phase transition, and phase-gated managed action materialization. The executor also supports managed materialization for profile-normalization plans for roster, upgrade purchases, stagecoach, trinket inventory, wallet resource maps, campaign plot progress reset, town state, town store suppression, town event, fixed quest board, and trinket entry field patches. These actions report `materialized`, include a `plan` object, write a sidecar artifact under `modStateDirectory/_managed_actions/`, and do not mutate the game. Startup and `--dry-run` compile consumable `questBoard.replaceWithFixedSet`, `trinket.patchEntry`, and `town.unlockAllBuildings` artifacts into `logs/managed_action_overlay_manifest.json`; quest-board artifacts force selected source plot quests to `dungeon_level = 0` and `is_repeatable = true`, trinket patch artifacts generate entry `sourcePath` overlays, and town unlock artifacts generate building requirement `sourcePath` overlays. `--preview-quest-board`, `--refresh-quest-board-profile`, and `questBoardAutoRefreshEnabled` cover generated quest-board inspection and targeted profile refresh. `--initialize-decoded-profile` combines sidecar initialization, `profile.initialization_requested`, quest-board preview, managed-action apply, and per-action apply details into one project-local decoded-save initialization report. `--apply-managed-actions` writes supported profile-normalization actions to decoded save copies; `trinket.patchEntry` is recognized there as a content-overlay action and does not write `persist.*`. `--apply-continuous-profile-actions` is the narrower reapply mode for settlement drift: it selects only the latest continuous stagecoach/store/town-event artifact for each full producer identity, target, and profile scope, and deliberately excludes one-time setup such as wallet, trinket inventory, generated roster, upgrades, campaign progress reset, quest-board replacement, and trinket entry overlays. Trinket inventory source resolution can exclude content rarities such as `darkest_dungeon` and `trophy`, and generated roster quirks respect content `singleton` tags across a generation pass. `tools/PrepareDecodedProfileWorkspace.ps1 -EncodeInitializedProfile` can turn an initialized decoded workspace into a project-local `encoded_profile` and roundtrip it for validation. `tools/PromoteEncodedProfileWorkspace.ps1` can dry-run or explicitly write that `encoded_profile` to a target profile with target guards, running-game protection, target snapshot backup, hash verification, and manifest-based restore. `--preview-managed-action-retention` and `--prune-managed-actions` provide explicit sidecar artifact retention reports for `_managed_actions/`; only exact version 1 artifacts and proven stale owners or producer contracts are prunable, while unknown versions and malformed or structurally corrupt artifacts are retained with warnings and delete failures remain errors rather than fallback paths. Town-event text policy still needs an original content/save consumer before it changes live game behavior; consumed hero/trinket restrictions still need original-first projection before they can stop party selection. The boss-gauntlet plugin no longer materializes `town.setBuildingLevels`; ordinary building levels are represented by verified upgrade purchases. Pre-finale hero/trinket reuse is not yet live-enforced in party UI interactions.

- The realtime save watcher can now run two managed reconciliation paths after the same stable save bridge: quest-board refresh and continuous profile action auto-apply. Quest-board policy materialization writes `status=empty` markers when no policy entries are currently selected, and quest-board preview/overlay compilation uses those markers to supersede stale dynamic board artifacts. Continuous profile auto-apply decodes live profile files into a project-local workspace, reuses the existing continuous managed action applier, re-encodes changed `persist.*.json` files, backs up live targets, and writes only when its running-game write guard allows it.
- Each sidecar action executes against an isolated in-memory state document. Only a successful state-changing handler replaces the executor's current document; failed and no-change handlers discard their copies. After all matching rules run, the latest accepted document is persisted once, and a write failure remains an event error. A required action failure stops the remaining actions in that rule; an optional failure is reported as a warning and later actions can continue. Sidecar arguments are validated during event execution. Managed plans resolve referenced `event.*` and `state.*` values during materialization; consumer-specific modes and save/content shapes are validated when the overlay or decoded-save consumer reads the artifact.
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
2. Build the enabled-plugin declaration set, then resolve each rule against the framework capability/action registry.
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

- `id`: required non-empty stable rule id inside the plugin. An enabled rule with a missing or blank id is skipped during patch-plan construction, because it cannot produce a durable managed action identity.
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
  "id": "emit_boss_attempt_resolved_from_last_raid",
  "enabled": true,
  "emit": "quest.attempt_resolved",
  "phase": "normal",
  "priority": 0,
  "requiresCapabilities": [
    "state.sidecar",
    "quest.observe_attempt_resolved"
  ],
  "when": {
    "all": [
      { "state": "bossGauntlet.initialized", "op": "equals", "value": true },
      { "state": "bossGauntlet.phase", "op": "equals", "value": "boss_gauntlet" },
      { "state": "bossGauntlet.activeSelection.questId", "op": "exists" },
      {
        "fact": "progression.lastRaidQuest.names",
        "op": "contains",
        "valueFromState": "bossGauntlet.activeSelection.questId"
      },
      { "fact": "progression.lastRaidSuccess", "op": "exists" },
      { "fact": "campaignLog.partyRaidRecordCount", "op": "exists" }
    ]
  },
  "payload": {
    "questId": { "fromState": "bossGauntlet.activeSelection.questId" },
    "success": { "fromFact": "progression.lastRaidSuccess" },
    "attemptId": { "fromFact": "campaignLog.partyRaidRecordCount" },
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

Attempt-recording actions are idempotent only when the emitted payload carries a stable attempt identity. `attempt.recordOnce` accepts either a direct `fingerprint` reference or a `fingerprint` object with `explicit`, `requiresAny`, `prefix`, and ordered `parts` fields. The boss-gauntlet validation contract uses the observed party raid record count as `attemptId`, stores resolved attempts in `bossGauntlet.attempts[]`, and records `bossGauntlet.lastResolvedAttemptId` so repeated bridge passes do not duplicate attempts or rewards.

`state.setArrayCount` writes the length of a state array to another state path, and `state.setFromArrayIndex` projects one element from a state array into another state path. `state.setFromArrayIndex` requires `key`, `arrayStateKey`, and `indexStateKey`; if the index is out of range, it writes `null` unless `outOfRangeValue` is provided. Boss-gauntlet initialization uses `state.mergeDefinition` to load scenario configuration and then ordinary state actions to persist initialization flags, phase, wallet, and selection/attempt state.

If an emitted event successfully writes any sidecar state (`StateWriteCount > 0`), the bridge clears its sidecar cache before evaluating the next fact rule, even when a later required action makes the event fail overall. Later fact rules therefore reload current state from disk. This allows a post-task save report to infer `quest.selection_confirmed` from structured campaign log facts, then infer `quest.attempt_resolved` from progression facts without waiting for another watcher pass.

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
{ "fact": "progression.lastRaidQuest.names", "op": "contains", "valueFromState": "bossGauntlet.activeSelection.questId" }
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

Capabilities describe what the framework can safely observe or change. A mod declares capabilities it needs; a rule action names the capability it uses. A declaration is an opt-in requirement, not a provider registration and not proof that the capability exists.

Capability registry entries are implemented in `launcher/Patching/FrameworkCapabilityRegistry.cs`. Current enforcement uses `id`, `status`, `risk`, `source`, `effectScope`, `available`, `liveEnforced`, and `failurePolicy`. Executable-hash gates, structured log fields, and minimum-test metadata remain registry extensions for native capabilities; the target provenance shape is:

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

Registry resolution rules:

1. `when.capabilitiesPresent` and `when.capabilitiesAbsent` inspect the normalized union of declarations from enabled plugins. They answer whether a plugin set declares a dependency; they do not answer whether the framework implements it.
2. `eventRules[].requiresCapabilities` and `factEventRules[].requiresCapabilities` are resolved per plugin. Another plugin cannot grant the requirement, and an unknown or `planned` capability skips the rule with a reason during patch-plan construction.
3. Every runtime action type has registered allowed capabilities, expected risk, execution kind, status, and consumers. Action type ids are case-sensitive. A required unknown/planned action, capability mismatch, or risk mismatch skips the rule before an event can execute. An invalid optional action remains visible in diagnostics but is disabled before execution. If a valid optional action later fails parameter handling or materialization, the failure remains visible but is downgraded to a warning so the event can continue.
4. `materialized` means the event executor can write an artifact. The action's `consumers` and `liveEnforced` fields separately state whether an overlay, decoded-save applier, continuous reconciler, or live interception path exists. Artifact creation alone is never reported as live enforcement.
5. A decoded-save apply request treats a missing consumer for `required: true` as an error (`managed-action-required-consumer-missing`). The same gap for `required: false` remains a warning with status `unsupported`. Content-only actions such as `trinket.patchEntry` use a separate decoded-save recognition consumer and report `recognized`; they are not counted as decoded-save `applied` or `dry-run` effects.
6. Producer validity and the artifact envelope are evaluated before a materialized action reaches any consumer. Plugin artifacts require `status=materialized` plus a `plan` whose `kind` matches the producer action, whose `effect` and `target` are non-empty, and whose `arguments` is an object. `questBoard.replaceWithFixedSet` additionally shares its structural reader with the real consumer, so a missing, empty, or malformed `questIds` array cannot enter supersession or retention ranking; a framework-owned `status=empty` policy marker must carry an empty array rather than merely being allowed to do so. Removing, disabling, reordering, or changing the producing rule/action invalidates its old artifact. Static quest-chain board changes and active quest-board policy-set changes are checked through the same producer catalog instead of owner-only acceptance.
7. Retention ranks only eligible actions backed by a complete shared consumer structure validator, currently `questBoard.replaceWithFixedSet`; other eligible action types remain consumable but are retained without chronological ranking. Ranked retention groups, overlay supersession, and continuous-profile latest selection use the same full producer contract identity rather than partial rule keys. Explicit prune may remove exact integer version 1 artifacts, inactive or moved owners, inactive producers, and producer definition mismatches. Missing, malformed, non-integer, unknown, or future versions and parseable artifacts with malformed producer metadata, damaged envelopes, ambiguous producers, or invalid framework owner/policy sets remain for inspection instead of displacing a valid artifact or being treated as safely stale. `tools/TestManagedActionProducerIdentityConsumers.ps1` verifies that duplicate rule ids at different rule indices remain independent through both overlay compilation and continuous-profile selection.

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
| `SaveEventBridge` | Evaluates plugin-declared `factEventRules` and can emit arbitrary framework events with payloads from facts, bridge context, sidecar state, and generic payload projections | The launcher owns the generic bridge only; concrete active-raid and last-raid boss-gauntlet mappings live in `plugins/_validation` | Keep it generic. If a new mapping needs C# changes, first add a reusable predicate, payload projection, payload source, or fact extractor |
| Legacy `challenge.*` executor actions | No `challenge.*` executor actions remain; validation scenario state is composed from generic `state.*`, `selection.*`, `attempt.*`, and managed artifact actions | Keeping the resolved cleanup visible prevents the old gameplay-mode branch from returning | Keep `tools/TestArchitectureRedFlags.ps1` blocking `challenge.*` branches in launcher/runtime core code |
| `plugins/_validation` and test scripts | Contain concrete boss quest ids, stage ids, selected hero ids, and trinket ids | They are acceptance fixtures, not user-facing framework behavior | Leave concrete data in fixtures, but do not move fixture assumptions into launcher/runtime logic |
| Boss gauntlet target spec | Names a concrete fixed-resource campaign design | It is a pressure test for missing reusable primitives | Add `profile.*`, `quest_board.*`, `selection.*`, `wallet.*`, `inventory.*`, `town.*`, `stagecoach.*`, and original-first reuse projection primitives before attempting live behavior |
| Post-ending expansion map sketch | Names the post-Ancestor new-map idea | It is a pressure test for chapter chains, fixed map topology, per-cell content, and named encounters | Add `quest_chain.*`, `map.*`, `encounter.*`, and `region.*` primitives before attempting a live custom map |

Before adding a new gameplay feature, ask: can another mod with different content reuse the same primitive without changing C# or native hook code? If not, the design is still too hardcoded.

## First Implementation Slice

The next code slice should stay generic:

1. Parse and log `eventRules` counts. Done as a declaration carrier.
2. Add `--explain-rules` to print declared rules, required capabilities, and skipped reasons. Done for manifest-level rule declarations.
3. Add validation manifests for quest draft, fixed-resource boss gauntlet, and delayed building upgrades. Done as declaration-level framework acceptance scenarios.
4. Add a capability registry document or JSON schema.
5. Add sidecar state file read/write with no gameplay actions. Initial `--init-mod-state` / `--dump-mod-state` support is implemented.
6. Add an observe-only event bus sourced from existing save watcher/runtime logs. Plugin-declared `factEventRules` now bridge save state reports to ordinary runtime events.
7. Add a no-op action executor for `log.*` and `state.*`. Initial `--emit-event` support now executes implemented safe state actions against sidecar state.
8. Materialize selected managed actions into sidecar artifacts. Boss-gauntlet profile-normalization actions now write `materialized` artifacts for later overlay/hook consumers without mutating original game state.
9. Compile selected managed action artifacts into a runtime-visible overlay manifest. `questBoard.replaceWithFixedSet`, `trinket.patchEntry`, and `town.unlockAllBuildings` artifacts can enter `logs/managed_action_overlay_manifest.json`; stale fixed-board overlays are superseded by the latest applicable artifact, and RuntimeHook records manifest visibility through `DD_RUNTIME_MANAGED_OVERLAY_*` diagnostics.
10. Feed selected overlay artifacts into an existing runtime consumer. `questBoard.replaceWithFixedSet` now appends virtual file replacements for the relevant plot quest files, using concrete plot quest ids as content anchors. `trinket.patchEntry` emits trinket entry overlays that apply explicit `set`/`remove` edits to selected existing ids or field selectors. Hero/trinket reuse restrictions remain sidecar facts until they can be projected through verified original roster/equipment mechanisms.
11. Add a targeted save-refresh consumer for generated quest boards. `--refresh-quest-board-profile <profileId>` can write the generated fixed-board `persist.quest.json` into a configured watched profile with dry-run, backup, and safety checks, so initialization can update the current board without pretending to run the entire original campaign week settlement.
12. Add a realtime save-watch consumer for generated quest boards. When `questBoardAutoRefreshEnabled` is configured, any successfully bridged stable campaign save batch can trigger the same fixed-board writer; this covers original week-transition board regeneration without simulating the rest of week settlement.

Only after that should gameplay experiments be expressed as ordinary rules.
