# Runtime Mod Platform Design

This document defines the runtime mod platform direction for the framework. The goal is not to stop at a file replacement tool. The generic rule contract lives in `docs/capability_rule_contract.md`; acceptance scenarios live in `docs/validation_scenarios.md`. Gameplay sections in this document are use cases, not dedicated one-off templates.

The target is to let players reshape key Darkest Dungeon gameplay loops instead of only changing numbers, replacing images, or appending text. Examples:

- Keep the campaign going after the Ancestor finale by unlocking a new region, new story, and a new quest chain.
- Replace stagecoach-based long-term roster growth with a fixed-stage boss gauntlet: a preset max-level hero pool, original boss quest chain, four heroes and trinkets selected per stage, locked retry selection on failure, and used heroes/trinkets removed from the pre-finale pool after resolved attempts.
- Add building upgrade wait times, parallel upgrade compensation, and cross-week completion logic.

These features cannot be done with only `find -> replace`. File virtualization remains the content foundation, but the platform also needs events, state, actions, hook capabilities, and diagnostics. New gameplay should first be mapped to facts, events, predicates, actions, state, and capabilities. If it cannot be expressed, extend those reusable primitives instead of adding a special path for a single gameplay idea.

## Design Principles

- Compatibility first: multiple mods changing the same content should usually not block launch. Apply them in stable order and report the final result during validation and preview.
- Diagnostics first: players and authors should be able to see load order, event triggers, state changes, final virtual files, and likely conflicts.
- Hard safety boundaries: block launch by default for path traversal, architecture mismatch, JSON parse failures, and explicitly required operations that fail.
- Data layer before code layer: prefer declarative rules and state machines. Add Lua/C#/native plugin layers only after the core primitives are stable.
- Content references first: static content such as monsters, skills, animation, textures, audio, localization, ordinary curios, and loot should usually come from the base game, DLC, Workshop mods, or plugin-provided files. The framework references, validates, composes, orders, and projects that content at runtime. Do not add dedicated runtime code just to duplicate existing static content authoring workflows.
- Small verifiable loops: every deep capability starts with observe-only probes, then a minimal reversible PoC, then plugin-facing exposure.

## Platform Layers

```text
Plugin Manifest
  -> Content Patch Layer
  -> Event Layer
  -> State Layer
  -> Action Layer
  -> Hook Capability Layer
  -> Diagnostics Layer
```

### Content Patch Layer

Current prototype:

- Virtual reads for `.darkest` / `.json` / localization / asset files.
- Low-level string `replacements`.
- Plugin manifest fields: `id`, `version`, `capabilities`, `phase`, `priority`, `depends`, `optionalDepends`, `loadAfter`, `loadBefore`, and `conflicts`.
- `virtualFileRules.when` supports `modsPresent` / `modsAbsent` / `capabilitiesPresent` / `capabilitiesAbsent` for compatibility and conditional patches.
- `operations` compile before launch into `replacements`, step by step, using the current virtual text and load order.
- Operation compilation preserves subjects such as `key:.some_key`, which helps explain the final source and detect multiple mods changing the same key.
- Validation, preview, and diff output.

Planned improvements:

- A `.darkest` parser that understands `.key value`, arrays, inherited data, and common sections.
- Content id indexing and reference validation for monsters, skills, trinkets, quests, regions, buildings, curios, loot tables, asset files, and similar content. The first goal of indexing is to let plugins reference base-game, DLC, Workshop, or plugin-provided content and report missing, duplicate, and source information. It is not to rebuild every static content generator inside the framework.
- More detailed patch explain output that shows which plugins and rules produced the final value of a key.

Static content and runtime orchestration boundaries are documented in `docs/content_reference_boundaries.md`. For example, a new monster can come from a Workshop mod; the framework only needs `encounter` or `spawnPool` rules to reference the `monsterId`, then block or degrade the dependent module if that reference is missing.

### Event Layer

The event layer exposes game flow points that rules can subscribe to.

Current observe-only v0 is the lowest-risk probe. RuntimeHook observes `CreateFileW/CreateFileA/WriteFile`, `MoveFile/MoveFileEx`, `CopyFile`, `DeleteFile`, `ReplaceFile`, and `SetFileAttributes`, then classifies known file activity as `data.*`, `asset.*`, or `save.*` events. It only writes logs. It does not read event context, intercept control flow, or modify saves. Real-game sampling keeps `save.*` events by default, gives ordinary data/asset events a separate budget, and filters external noise such as Steam overlay logs.

When the overlay manifest contains hero/trinket availability policy, RuntimeHook enables the focused `availability.*` probe. The probe marks open/write/lifecycle events for `persist.roster.json`, `persist.estate.json`, `persist.raid.json`, `persist.quest.json`, and related campaign/town files, and can emit call-stack module offsets. This locates hook points for later hard consumers without changing game results.

Priority from low risk to high risk:

1. observe-only: record whether an event happened.
2. passive read: read event context without changing the result.
3. intercept: cancel original logic or replace a result.
4. synthesize: generate additional framework events.

First candidate events:

```text
campaign.loaded
campaign.week_advanced
quest.selected
quest.started
quest.completed
quest.failed
town.entered
roster.opened
roster.hero_added
party.selection_started
party.selection_confirmed
building.upgrade_requested
building.upgrade_completed
save.loaded
save.before_write
```

High-risk events are deferred:

```text
battle.turn_started
battle.skill_resolved
battle.ai_decision_requested
ui.widget_created
```

### State Layer

Complex mods need their own durable state. They cannot depend only on original save fields.

Default state is sidecar state:

```text
state/mod_state/<plugin-id>.json
```

State namespaces are isolated by plugin:

```json
{
  "mods": {
    "example.roster_draft": {
      "usedHeroes": ["hero_001", "hero_002"],
      "draftModeEnabled": true
    },
    "example.building_delay": {
      "pendingUpgrades": [
        {
          "building": "blacksmith",
          "level": 3,
          "remainingWeeks": 2
        }
      ]
    }
  }
}
```

Principles:

- Do not change original save structure by default.
- Store the initial implementation under the framework project directory to avoid polluting original profiles. Later versions can add campaign/run/profile scoping.
- Log before and after original-save writes.
- If sidecar state is corrupt, disable the affected plugin state namespace and warn. Do not damage original saves.
- Keep state after plugin uninstall so users can roll back or inspect it.

### Action Layer

The action layer is the minimal capability set used by declarative event rules.

Example:

```json
{
  "on": "building.upgrade_requested",
  "when": {
    "building": "blacksmith"
  },
  "actions": [
    { "cancelOriginal": true },
    { "spendOriginalCost": true },
    {
      "queueBuildingUpgrade": {
        "building": "blacksmith",
        "weeks": 3,
        "parallelCompensation": "reduce_by_active_count"
      }
    }
  ]
}
```

Action risk levels:

- safe: writes only framework sidecar state or logs.
- managed: changes results through known game APIs or verified hooks.
- risky: memory patches, deep flow replacement, or heavy UI changes. These require explicit opt-in by default.

Current managed actions still use observe-first materialization. `quest.injectFixedStage`, `roster.filterAvailableHeroes`, `equipment.filterAvailableTrinkets`, and boss-gauntlet profile-normalization actions write artifacts into `modStateDirectory/_managed_actions/`.

The launcher can compile `quest.injectFixedStage` and `questBoard.replaceWithFixedSet` artifacts into `logs/managed_action_overlay_manifest.json`, pass manifest path and counts to RuntimeHook for diagnostics, and append virtual replacements for related `quest.plot_quests.json` files so source plot quests become early-available and repeatable.

`town.unlockAllBuildings` artifacts can generate town building `.building.json` `sourcePath` overlays that reduce original entrance requirements to 0. This opens buildings such as the Survivalist, Blacksmith, and Guild immediately. Building upgrade levels are still represented through `upgrade.ensurePurchases`.

`roster.enforceAvailabilityFilter` and `equipment.enforceAvailabilityFilter` enter overlay manifest `availabilityPolicies` and trigger the focused availability probe, but they do not yet block the original UI.

`--refresh-quest-board-profile <profileId>` can write the generated fixed quest board into the watched profile's current `persist.quest.json`, with `--dry-run`, pre-write backup, path validation, and running-game protection. `questBoardAutoRefreshEnabled` lets the realtime save watcher reapply the same writer after any successfully bridged stable campaign save batch, not only after `persist.quest.json` changes. Live writes to external saves require `questBoardAutoRefreshAllowRunningGameSaveWrite=true`. These are quest-board refreshes, not full week-settlement simulations.

`inventory.disableItemSale` trinket artifacts are now recorded only in manifest/profile policy. They no longer generate trinket entry `sourcePath` price overlays. Hard UI sell-button disable still requires a runtime/UI/save consumer.

`--apply-managed-actions --managed-action-save-dir <dir>` can read these artifacts and generate `logs/managed_action_apply_report.json`. It is dry-run by default. Writes require `--write-managed-actions`, and the first version only writes project-local decoded JSON save copies.

`--initialize-decoded-profile` inlines apply action/file details into its summary report so each artifact's dry-run, applied, or unsupported status can be inspected directly.

`--preview-managed-action-retention` and `--prune-managed-actions` explicitly maintain `_managed_actions/`: they group by action, plugin, rule, target, profile scope, and source; keep the newest artifacts; and write `logs/managed_action_retention_report.json`. Invalid artifacts are retained with warnings. Delete failures are errors.

`tools/PrepareDecodedProfileWorkspace.ps1` can read a real `profile_*` top-level `persist*.json` set into `state/decoded_profiles/<session>/decoded_save`, optionally invoke `--initialize-decoded-profile`, and, with `-EncodeInitializedProfile`, re-encode initialized decoded persist files into the same workspace's `encoded_profile`, then immediately roundtrip-decode to `roundtrip_decoded` and JSON-parse them. It does not write Steam userdata.

`tools/PromoteEncodedProfileWorkspace.ps1` is the separate promotion tool. It defaults to dry-run, allows only project-local target profiles by default, and promotes only encoded files whose decoded content changed in the workspace report. Full encoded-profile overwrite requires `-PromoteAllEncodedFiles`. External real profiles require `-AllowExternalTarget`, and external writes while the game is running require `-AllowRunningGameSaveWrite`. Before writing, it snapshots target files and a manifest; after writing, it verifies hashes. `-RestoreFromReport` restores overwritten original files from that manifest. Promotion-added files remain and are reported as warnings to avoid turning cleanup into a risky implicit deletion path.

Currently implemented decoded-save writers:

- `wallet.setCurrencyAmounts` / `wallet.setCurrencyAmount` write wallet resources into `persist.estate.json`.
- `estate.ensureInventoryCounts` writes specified trinket inventory counts and can exclude initial sources by content rarity.
- `inventory.disableItemSale` writes sale-disable policy into project-local `_ddrt_profile_policy.json`.
- `roster.ensureClassInstances` adds hero instances for enabled classes into `persist.roster.json`.
- `roster.setProgression` normalizes existing and generated heroes' resolve XP, weapon/armor level, and current HP under max equipment.
- `roster.setSkillUnlocks` writes normal selected combat/camping skill slots from class content definitions. Full skill unlock/max purchase state is represented by `upgrade.ensurePurchases` in `persist.upgrades.json`.
- `upgrade.ensurePurchases` writes decoded `persist.upgrades.json`, reads base content upgrade trees for building, combat skill, camping skill, weapon, and armour requirements, and fills purchase records by requirement code. Instanced trees infer `instance_number` from `profile.roster.heroes` hero ids and classes.
- `stagecoach.suppressRecruits` clears `stage_coach.store.*.generated` recruit pools in decoded `persist.town.json`.
- `town.suppressStoreItems` clears selected town building store `inventory.items` / `generated` fields.
- `town.unlockAllBuildings` sets existing district `built` flags to true.
- `townEvent.overrideCurrent` suppresses the current event in `persist.town_event.json` and records requested message policy into `_ddrt_profile_policy.json`.

Ordinary town building levels are still expressed by `upgrade.ensurePurchases`; there is no verified direct `persist.town.json` building-level scalar. Custom town-event text still needs a runtime/UI/content consumer before it appears in game; the writer does not invent unknown save fields.

`roster.ensureClassInstances` generates new heroes from a clean hero blueprint instead of deep-copying existing save heroes, which avoids carrying unrelated old hero or test sample state into new objects. Random quirk selection reads content tags and keeps `singleton` quirks unique across the generated roster pass.

Content readers:

- `content.trinkets.enabled` currently reads base trinket entries and official non-arena DLC trinket entries from the install directory.
- `content.hero_classes.enabled` currently reads base heroes and official non-arena numeric DLC hero definitions.
- `content.upgrades.enabled` currently reads base upgrades, base camping skills, and official non-arena DLC definitions.

If the install directory itself has been modified by other mods, a clean content source is required for pure-base results. Other profile-normalization behavior and hard hero/trinket restrictions still need future runtime consumers or schema-verified save writers. Known live gaps include stagecoach/store regeneration after week settlement and consumed sidecar heroes/trinkets still being selectable in the original UI.

First action candidates:

```text
setFlag
clearFlag
incrementCounter
queueBuildingUpgrade
advanceQueuedUpgrades
unlockRegion
unlockQuest
lockHero
unlockHero
markHeroUsed
filterPartySelection
showDialogue
emitEvent
cancelOriginal
```

### Hook Capability Layer

Hooks should not be exposed as arbitrary function addresses. They should be wrapped as capabilities.

Example capabilities:

```text
file.virtualize
campaign.observe_week_advance
quest.observe_completion
building.intercept_upgrade_request
roster.filter_available_heroes
save.attach_sidecar_state
```

Every capability must define:

- current status: planned / materialized / observed / intercepted / stable
- applicable game executable hash
- failure strategy: disable capability / skip mod / fail launch
- log fields
- minimum test scenario

### Diagnostics Layer

Diagnostics are a required part of the generic framework, not an accessory.

They must answer:

- Which plugins are enabled.
- What the final load order is.
- Which patches were skipped and why.
- Which events fired.
- Which actions executed.
- Which state was written.
- Which managed action artifacts were compiled into the overlay manifest and which remain only in sidecar state.
- What virtual file the game finally read.
- Which plugin, rule, and action produced a gameplay change.

Current tools:

```text
--list-patches
--explain-patches
--validate-only
--validate-patches
--preview-patches
--strict-patches
--init-mod-state
--dump-mod-state
--emit-event <event-id>
--initialize-decoded-profile
--apply-managed-actions
--managed-action-save-dir
--write-managed-actions
--preview-managed-action-retention
--prune-managed-actions
--managed-action-retention-keep
tools/PrepareDecodedProfileWorkspace.ps1
```

Future tools:

```text
--trace-events
--reset-mod-state <mod-id>
--explain <target-or-event>
```

## Manifest Direction

Plugin manifests should grow gradually while remaining compatible by default.

Draft:

```json
{
  "id": "example.building_delay",
  "name": "Delayed Building Upgrades",
  "version": "0.1.0",
  "enabled": true,
  "capabilities": [
    "file.virtualize",
    "content.patch"
  ],

  "phase": "normal",
  "priority": 100,
  "depends": [],
  "optionalDepends": [],
  "loadAfter": [],
  "loadBefore": [],

  "virtualFileRules": [],
  "factEventRules": [],
  "eventRules": [],
  "stateSchema": {}
}
```

Dependency policy:

- `depends`: if missing, skip the affected plugin and warn; do not block other plugins.
- `optionalDepends`: if present, order after it; if absent, ignore it.
- `loadAfter/loadBefore`: ordering only; they do not mean the target must exist.
- duplicate `id`: warn by default and derive a unique internal instance id from the path.
- conflicts: warn by default and do not block launch.

## Example: Fixed Resource Boss Gauntlet

Current target spec:

```text
docs/boss_gauntlet_campaign_spec.md
```

Goal: replace long-term stagecoach growth with a fixed-resource boss campaign. On first entry to a new save, initialize a fixed max-level hero pool, fixed trinket pool, 20000 gold, maxed town, fixed quest board, and disabled trinket sale. After that, the game saves normally and losses are not rebuilt or restored. During the prerequisite boss phase, heroes and trinkets are consumed after any terminal attempt. Success and failure do not roll back settlement state. Each prerequisite boss victory adds 10000 gold. If the player runs out of heroes or makes the run unwinnable, that is an expected failure state; the player can delete the save and start over. After all prerequisite bosses are defeated, enter the Darkest Dungeon finale. Only the pre-finale sidecar one-use restrictions are cleared; dead heroes are not revived. Prefer reusing the original Darkest Dungeon rule that prevents victorious Darkest Dungeon heroes from entering again.

Required capabilities:

```text
profile.entered
profile.normalized
quest.selection_confirmed
quest.attempt_resolved
profile.detect_new_or_uninitialized
profile.mark_initialized
quest_board.replace_with_fixed_set
quest_board.filter_completed_fixed_quests
roster.ensure_class_instances
roster.set_progression
roster.set_skill_unlocks
roster.enforce_availability_filter
equipment.enforce_availability_filter
estate.ensure_inventory_counts
wallet.set_currency_amounts
wallet.modify_currency
inventory.disable_item_sale
stagecoach.suppress_recruits
town.unlock_all_buildings
town.set_building_levels
town_event.override_current
state.bossGauntlet.consumedHeroIds
state.bossGauntlet.consumedTrinketIds
```

Declarative draft:

```json
{
  "on": "profile.initialization_requested",
  "actions": [
    { "type": "roster.ensureClassInstances", "classCount": 2, "level": "max" },
    { "type": "estate.ensureInventoryCounts", "kind": "trinket", "count": 2 },
    { "type": "wallet.setCurrencyAmounts", "amounts": { "gold": 20000, "bust": 0, "portrait": 0, "deed": 0, "crest": 0, "shard": 0 } },
    { "type": "inventory.disableItemSale", "kind": "trinket" },
    { "type": "stagecoach.suppressRecruits" },
    { "type": "town.unlockAllBuildings" },
    { "type": "town.suppressStoreItems", "buildingIds": ["nomad_wagon"], "sections": ["inventory.items", "generated"] },
    { "type": "town.setBuildingLevels", "level": "max" },
    { "type": "questBoard.replaceWithFixedSet", "source": "highest_non_darkest_bosses" },
    { "type": "profile.markInitialized", "stateKey": "bossGauntlet.initialized" }
  ]
}
```

```json
{
  "on": "quest.selection_confirmed",
  "actions": [
    { "type": "selection.lock", "stateKey": "bossGauntlet.activeSelection" }
  ]
}
```

```json
{
  "on": "quest.attempt_resolved",
  "actions": [
    { "type": "attempt.recordOnce", "stateKey": "bossGauntlet.attempts" },
    { "type": "selection.consumeHeroes", "stateKey": "bossGauntlet.consumedHeroIds" },
    { "type": "selection.consumeTrinkets", "stateKey": "bossGauntlet.consumedTrinketIds" },
    { "type": "wallet.addCurrencyOnEvent", "currency": "gold", "amount": 10000, "when": "event.success == true" },
    { "type": "quest.markCompletedIfSuccessful", "stateKey": "bossGauntlet.completedQuestIds" },
    { "type": "state.transitionWhenAllCompleted", "stateKey": "bossGauntlet.phase", "to": "darkest_finale" }
  ]
}
```

Minimum PoC:

1. Do not directly mutate the real save.
2. Record `initialized`, `fixedQuestIds`, `completedQuestIds`, `activeSelection`, `consumedHeroIds`, and `consumedTrinketIds` in sidecar state first.
3. Use dry-run diagnostics to prove initialization is idempotent: first profile entry builds the setup, later entries do not rebuild, revive, or refill trinkets.
4. Output the fixed quest board and current selectable/unselectable heroes and trinkets.
5. Verify that finale unlock only clears sidecar pre-finale restrictions and does not revive heroes or rebuild the roster.
6. Hook the quest board, selectable hero list, trinket list, and embark validation.
7. Add UI hints last.

The early `validation.challenge_run_contract` still preserves the "failure locks retry selection" test semantics to validate the state/event/managed-artifact pipeline. It is not the final target gameplay spec.

## Example: Delayed Building Upgrades

Goal: building upgrades no longer complete immediately. They complete after several weeks; higher levels take longer; multiple simultaneous upgrades get time compensation.

Required capabilities:

```text
building.upgrade_requested
campaign.week_advanced
building.apply_upgrade
state.pendingUpgrades
ui.show_pending_upgrade
```

Declarative draft:

```json
{
  "on": "building.upgrade_requested",
  "when": {
    "building": "blacksmith"
  },
  "actions": [
    { "cancelOriginal": true },
    { "spendOriginalCost": true },
    {
      "queueBuildingUpgrade": {
        "weeksFormula": "1 + targetLevel",
        "parallelCompensation": "reduce_by_active_count"
      }
    }
  ]
}
```

```json
{
  "on": "campaign.week_advanced",
  "actions": [
    { "advanceQueuedUpgrades": true },
    { "completeReadyBuildingUpgrades": true }
  ]
}
```

Minimum PoC:

1. Observe building upgrade clicks.
2. Block the original upgrade and write pending state.
3. Observe week advance and decrease `remainingWeeks`.
4. At 0, call or simulate original upgrade completion.
5. Add UI countdown handling last.

## Example: Post-Ancestor Campaign

Goal: do not end the campaign directly after the Ancestor battle. Unlock a new region, story, and quest chain.

Required capabilities:

```text
quest.completed
campaign.ending_requested
region.unlock
quest.inject
dialogue.show
state.storyFlags
```

Declarative draft:

```json
{
  "on": "quest.completed",
  "when": {
    "questId": "ancestor_final"
  },
  "actions": [
    { "setFlag": { "postgame_unlocked": true } },
    { "unlockRegion": "black_coast" },
    { "unlockQuest": "black_coast_intro" },
    { "showDialogue": "postgame_intro_001" }
  ]
}
```

Minimum PoC:

1. Observe final quest completion.
2. Write `postgame_unlocked=true`.
3. Block the ending flow and only log first.
4. Inject a quest using an existing quest type.
5. Add new region UI later.

## Roadmap

1. Keep file virtualization, validation, and preview stable.
2. Design and implement plugin load order and dependency graph with compatibility-first defaults.
3. Build event probes that log only.
4. Build sidecar mod state. The launcher already has initial `--init-mod-state` / `--dump-mod-state` read/write support.
5. Build the smallest event rule executor. `--emit-event` already executes implemented safe sidecar state actions and can generate sidecar artifacts for selected managed actions. Real game event intake and real game mutation are still future work.
6. Choose a PoC: the fixed-stage challenge is the best first gameplay dry-run because it validates facts, sidecar state, selection filtering, and state progression before intercepting real UI.
7. Add delayed building upgrades next because they validate events, state, week progression, and UI hints.
8. Add the post-Ancestor campaign after that.

## Non-Goals For Now

- Do not load arbitrary Lua/C#/native plugins yet.
- Do not promise arbitrary new UI yet.
- Do not change original save structure.
- Do not expose arbitrary memory writes.
- Do not bypass Steam, DRM, or system security mechanisms.
