# Framework Validation Scenarios

This document uses gameplay ideas as framework validation scenarios. They are not built-in templates. They are acceptance tests for whether generic primitives are expressive enough.

The rule is:

```text
If a scenario cannot be expressed with facts, events, predicates, actions, state, and capabilities,
add a reusable primitive. Do not hardcode the scenario.
```

Validation manifests live under `plugins/_validation/` and are loaded by:

```text
config/rule_contract_validation_config.json
```

Run:

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/rule_contract_validation_config.json --explain-rules --no-inject
```

The baseline expected result is declaration-level success: all validation rules should be listed as active by `--explain-rules`. The first safe executor can also exercise implemented sidecar state actions through `--emit-event`, and selected managed actions can now materialize observe-first artifacts. The launcher can compile fixed-stage and fixed-board quest artifacts into a runtime-visible overlay manifest, and both `quest.injectFixedStage` and `questBoard.replaceWithFixedSet` can feed the existing virtual file layer for plot quest availability. Fixed-board artifacts can also be explicitly written to a watched profile's current `persist.quest.json` through the targeted quest-board refresh path, or re-applied by the save watcher after a live `persist.quest.json` change when quest-board auto refresh is enabled. Broader managed game mutation is still incomplete.

## Scenario 1: Quest Draft Contract

Goal:

- Replace long-term stagecoach recruitment with per-quest party drafting.
- Make all base heroes and draft equipment available.
- Let the player pick a party.
- Mark selected heroes as used.
- Exclude used heroes from later selections.

Validation manifest:

```text
plugins/_validation/quest_draft_contract/patches.json
```

Required generic primitives:

```text
facts.heroDefinitions
facts.roster
event party.selection_started
event party.selection_confirmed
state.usedHeroIds
action roster.unlockDraftPool
action equipment.unlockDraftLoadout
action roster.filterAvailableHeroes
action state.addUniqueRange
capability party.observe_selection_started
capability party.observe_selection_confirmed
capability roster.unlock_draft_pool
capability roster.filter_available_heroes
capability equipment.unlock_for_draft
capability state.sidecar
```

Acceptance ladder:

1. `--explain-rules` lists both rules as active.
2. Sidecar state can store `usedHeroIds`.
3. The framework can observe party selection start and confirmation.
4. The framework can read selected hero ids from the confirmation event.
5. The framework can filter available heroes before selection.
6. The framework can expose enough UI feedback to show why a hero is unavailable.

Steps 1-2 and the safe sidecar state action path exist now. Scenario 3 refines this into a fixed-stage challenge mode with retry and trinket-lock semantics.

## Scenario 2: Delayed Building Upgrades Contract

Goal:

- Intercept a building upgrade request.
- Spend the original cost.
- Prevent the original immediate upgrade result.
- Queue the upgrade in sidecar state with remaining weeks.
- Advance the queue each campaign week.
- Apply ready upgrades through a managed upgrade primitive.

Validation manifest:

```text
plugins/_validation/delayed_building_upgrades_contract/patches.json
```

Required generic primitives:

```text
facts.upgrades
facts.town
facts.wallet
event building.upgrade_requested
event campaign.week_advanced
state.pendingUpgrades
action event.cancelOriginal
action upgrade.spendOriginalCost
action upgrade.queuePending
action upgrade.advancePending
action upgrade.applyReadyQueued
capability building.intercept_upgrade_request
capability campaign.observe_week_advance
capability upgrade.spend_original_cost
capability upgrade.queue_pending
capability upgrade.apply_completed
capability state.sidecar
```

Acceptance ladder:

1. `--explain-rules` lists both rules as active.
2. Sidecar state can store `pendingUpgrades`.
3. The framework can observe week advancement.
4. The framework can observe building upgrade requests.
5. The framework can intercept upgrade requests and cancel original completion.
6. The framework can spend the original cost through a verified primitive.
7. The framework can apply a queued upgrade through a verified primitive.
8. The framework can expose pending upgrade status in diagnostics, and later in UI or overlay.

Only step 1 and the manifest parsing pieces exist now.

## Scenario 3: Fixed Stage Challenge Run Contract

Goal:

- Replace campaign growth with a self-contained challenge run.
- Define a fixed chain of stages, initially copied from original boss quests.
- Provide a fixed preset hero pool. Preset heroes are intended to be max-level, with full positive quirks randomized and exactly one negative quirk randomized.
- Let the player choose 4 heroes per stage.
- Let the player freely assign trinkets before a stage, but selected trinkets cannot be reused later.
- If the player fails a stage, allow retrying that same stage, but keep the already selected heroes and trinkets locked for the retry.
- If the player clears a stage, mark selected heroes and selected trinkets as used, then advance to the next stage.
- Clearing all stages wins the challenge.

This is an early validation scenario for sidecar state, save-event inference, managed action artifacts, and fixed-stage quest overlay. It is not the current target gameplay spec. The current target is Scenario 4, where failure consumes the selected heroes and trinkets instead of locking them for retry.

Validation manifest and data:

```text
plugins/_validation/challenge_run_contract/patches.json
plugins/_validation/challenge_run_contract/challenge.json
plugins/_validation/challenge_run_contract/sample_state.json
```

Dry-run tools:

```text
tools/TestChallengeRunDryRun.ps1
tools/TestSaveEventBridge.ps1
tools/TestRealtimeSaveBridge.ps1
tools/TestManagedActionOverlay.ps1
```

Run:

```text
.\tools\TestChallengeRunDryRun.ps1 -AssertSample
.\tools\TestChallengeRunDryRun.ps1 -Outcome stage_failed -SelectedHeroIds 1,2,7,8 -SelectedTrinketIds berserk_mask,immunity_mask,fortunate_armlet,sb_4,sb_3,sb_2,sb_1,bleeding_pendant
.\tools\TestChallengeRunDryRun.ps1 -Outcome stage_completed -SelectedHeroIds 1,2,7,8 -SelectedTrinketIds berserk_mask,immunity_mask,fortunate_armlet,sb_4,sb_3,sb_2,sb_1,bleeding_pendant
.\tools\TestSaveEventBridge.ps1
.\tools\TestRealtimeSaveBridge.ps1
.\tools\TestManagedActionOverlay.ps1
```

Required generic primitives:

```text
facts.heroes
facts.estate.trinkets
event challenge.run_started
event challenge.stage_selection_started
event challenge.stage_selection_confirmed
event challenge.stage_completed
event challenge.stage_failed
state.challengeRun.currentStageIndex
state.challengeRun.lockedStageSelection
state.challengeRun.usedHeroIds
state.challengeRun.usedTrinketIds
state.challengeRun.stageAttempts
action challenge.initializeRunState
action quest.injectFixedStage
action roster.filterAvailableHeroes
action equipment.filterAvailableTrinkets
action challenge.lockStageSelection
action challenge.advanceStage
action challenge.recordFailedAttempt
capability state.sidecar
capability challenge.define_stage_chain
capability challenge.lock_stage_selection
capability challenge.advance_stage
capability roster.provide_fixed_hero_pool
capability roster.filter_available_heroes
capability equipment.provide_fixed_trinket_pool
capability equipment.filter_available_trinkets
capability quest.inject_fixed_stage
```

Acceptance ladder:

1. `--explain-rules` lists all challenge-run rules as active.
2. The dry-run tool can read a challenge definition, sample save facts, and sidecar state.
3. The dry-run tool can report available/unavailable heroes and trinkets.
4. The dry-run tool can simulate stage failure by recording an attempt and locking the selected heroes/trinkets without marking them used.
5. The dry-run tool can simulate stage completion by adding selected heroes/trinkets to used lists and advancing the stage index.
6. Sidecar state can be loaded and saved through the framework, not just a standalone tool.
7. The framework can observe stage selection confirmation and stage completion/failure from game flow.
8. The framework can inject or replace fixed stages through a managed quest/content primitive.
9. The framework can materialize preset max-level heroes with randomized positive/negative quirks through verified roster/save primitives.

Steps 1-6 now exist for the sidecar state path: `--emit-event` can lock selection, record failed attempts, consume selected heroes/trinkets, and advance the stage in framework state. Step 7 has a generic save-facts bridge: `--infer-save-events` evaluates plugin-declared `factEventRules`; the validation plugin uses those rules to map active raid party/loadout facts or structured post-task `campaignLog.partyRaidRecords` to `challenge.stage_selection_confirmed`, and last raid quest/result facts to `challenge.stage_completed` or `challenge.stage_failed` for the matching current stage. A single bridge pass can now reload sidecar state between inferred events, so a post-task save report can infer selection and completion together. The launcher-side watcher can also run that bridge during game execution after debounced stable `profile_*` save changes, while keeping original saves read-only. Step 8 has an observe-first materialization path: `challenge.stage_selection_started` produces `materialized` artifacts for fixed-stage quest injection, hero filtering, and trinket filtering under `modStateDirectory/_managed_actions/`; startup and `--dry-run` compile the latest fixed-stage quest artifact into `logs/managed_action_overlay_manifest.json`, supersede older fixed-stage artifacts for the same source rule, and append one virtual file replacement for `campaign/quest/quest.plot_quests.json` that forces the selected source plot quest to early/repeatable availability. Real quest list control, roster materialization, and UI filtering still require later capabilities.

## Scenario 4: Fixed Resource Boss Gauntlet Campaign

Reference spec:

```text
docs/boss_gauntlet_campaign_spec.md
```

Goal:

- Automatically initialize a newly created eligible save into a fixed-resource campaign on first entry. The initialized profile then saves normally and is not rebuilt on later entries.
- Normalize the new save into two max-level heroes per class, all skills unlocked and upgraded, two of every eligible non-reward trinket, data-driven starting wallet resources including gold, shards, and heirlooms, fully unlocked and upgraded town for this scenario, empty stage coach, disabled trinket selling, and fixed or suppressed town events. The underlying town primitive should also support campaigns that start with selected buildings locked.
- Replace normal quest generation with a fixed simultaneous board of highest-difficulty non-Darkest boss quests.
- Consume selected heroes and selected trinkets on any terminal boss attempt result, success or failure.
- Add 10000 gold after each successful pre-finale boss quest.
- Keep failed boss quests available, but do not restore the consumed selection or roll back original settlement consequences. If roster attrition makes the campaign unwinnable, the player deletes the save and starts a new one.
- Remove defeated fixed boss quests without generating replacements.
- Unlock the Darkest Dungeon finale after all fixed boss quests are defeated.
- In the finale, clear pre-finale sidecar reuse restrictions and prefer original Darkest Dungeon participation rules. Dead or missing heroes from the boss-gauntlet phase are not revived or recreated.

Required generic primitives:

```text
event profile.entered
event profile.initialization_requested
event quest.selection_confirmed
event quest.attempt_resolved
state.bossGauntlet.initialized
state.bossGauntlet.phase
state.bossGauntlet.fixedQuestIds
state.bossGauntlet.completedQuestIds
state.bossGauntlet.consumedHeroIds
state.bossGauntlet.consumedTrinketIds
state.bossGauntlet.activeSelection
action attempt.recordOnce
action selection.consumeHeroes
action selection.consumeTrinkets
action wallet.addCurrencyOnEvent
action quest.markCompletedIfSuccessful
action state.transitionWhenAllCompleted
action state.clearPaths
capability profile.detect_new_or_uninitialized
capability profile.mark_initialized
capability roster.ensure_class_instances
capability roster.set_progression
capability roster.set_skill_unlocks
capability stagecoach.suppress_recruits
capability estate.ensure_inventory_counts
capability wallet.set_currency_amounts
capability wallet.modify_currency
capability inventory.disable_item_sale
capability town.unlock_all_buildings
capability town.set_building_availability
capability town.set_building_levels
capability town_event.override_current
capability quest_board.replace_with_fixed_set
capability quest_board.filter_completed_fixed_quests
capability roster.enforce_availability_filter
capability equipment.enforce_availability_filter
capability progression.observe_plot_completion
```

Validation manifest and data:

```text
plugins/_validation/boss_gauntlet_campaign_contract/patches.json
plugins/_validation/boss_gauntlet_campaign_contract/boss_gauntlet.json
```

Dry-run tool:

```text
tools/TestBossGauntletContract.ps1
```

Acceptance ladder:

1. The scenario has a validation manifest draft and content-derived or fixture-defined boss quest set.
2. The rule engine can simulate first-entry initialization and prove it is idempotent on later entries.
3. The rule engine can simulate `quest.attempt_resolved` and consume selected heroes/trinkets on both success and failure.
4. The rule engine can add the 10000 gold victory reward exactly once for each successful pre-finale boss attempt.
5. The rule engine can mark only successful boss attempts as completed, transition to `darkest_finale` when all fixed boss quests are completed, and materialize the first finale quest board.
6. Save/content facts can report enough data for roster, wallet, trinket inventory, trinket sale UI/actions, town building availability, town state, quest board, campaign log, and Darkest Dungeon participation decisions.
7. Managed action artifacts can describe the normalized roster, wallet, trinket inventory, town maxing, per-building town availability, fixed quest board, trinket-sale lockout, phase-scoped hero/trinket availability policy, and town-event override without mutating original saves.
8. Decoded-save managed action application can dry-run and apply supported profile-normalization actions against a project-local decoded save copy before any original-save writer exists.
9. Runtime consumers enforce the fixed quest board, disabled trinket selling, wallet reward, and pre-finale hero/trinket availability.
10. Managed original-save initialization, if introduced, is schema-verified, logged, idempotent, and does not restore later campaign failures.
11. The finale phase can rely on original Darkest Dungeon entry restrictions where possible, does not revive dead heroes, and starts from a phase-gated `plot_darkest_dungeon_1` quest board.

Steps 1-5 now exist for the sidecar state path. `tools/TestBossGauntletContract.ps1` initializes the plugin state, proves repeat initialization does not reset changed run state, locks selected heroes/trinkets, consumes that selection on success and failure, pays successful rewards once per attempt identity, marks only successful quests complete, transitions to `darkest_finale` after all fixed boss quests are complete, clears pre-finale reuse restrictions, preserves observed dead hero state, generates a DD1 -> DD2 -> DD3 -> DD4 finale quest-board policy only after every fixed boss is complete, and proves save facts can materialize the current finale stage into the latest quest-board preview. It also proves terminal pre-finale attempts materialize `roster.enforceAvailabilityFilter` and `equipment.enforceAvailabilityFilter` artifacts from the sidecar consumed lists, and that final prerequisite completion materializes empty filters after the phase transition. The profile-normalization slice of step 7 also exists as observe-first managed artifacts for roster, upgrade purchases, stagecoach, trinket inventory, starting wallet resources, trinket sale lockout, town state, town store suppression, town event, fixed quest board, and phase-scoped availability policy. Step 8 has a first decoded-save applier: `tools/TestManagedActionSaveApplier.ps1` proves `wallet.setCurrencyAmounts`, `estate.ensureInventoryCounts`, `roster.ensureClassInstances`, `roster.setProgression`, `roster.setSkillUnlocks`, `roster.enforceAvailabilityFilter`, `equipment.enforceAvailabilityFilter`, `upgrade.ensurePurchases`, `stagecoach.suppressRecruits`, district-scoped `town.unlockAllBuildings`, `town.suppressStoreItems`, `townEvent.overrideCurrent`, `inventory.disableItemSale`, and `questBoard.replaceWithFixedSet` can dry-run and then write starting wallet resources, two-copy trinket inventory with rarity exclusions, two clean-blueprint hero instances per enabled class, existing/generated hero progression, normal selected combat/camping skill slots, purchased building/hero upgrade-tree requirements, empty stagecoach generated recruit pools, empty declared town store inventory/generated pools, built district flags, current town-event suppression, decoded profile policy for trinket-sale lockout and hero/trinket availability filters, policy-only town-event message, and content-derived fixed quest board entries into project-local decoded save copies. `tools/TestManagedActionSaveApplier.ps1` also proves availability filters can be cleared by writing empty source lists. `tools/TestManagedActionSaveApplier.ps1` also proves `roster.ensureClassInstances` respects content `singleton` quirk tags across the generated roster. `tools/TestManagedActionSaveApplier.ps1` also proves `tools/PrepareDecodedProfileWorkspace.ps1 -EncodeInitializedProfile` can take a sandbox binary profile, decode it, initialize/write managed actions, re-encode every decoded persist file into `encoded_profile`, roundtrip decode them into `roundtrip_decoded`, and preserve key initialized roster and quest-board facts. `tools/TestProfilePromotion.ps1` proves the encoded-profile promotion tool can dry-run decoded-content-changed files only, exclude decoded-unchanged files by default, snapshot target files, write changed files, detect unchanged repeat writes, and restore overwritten files from the backup manifest while warning about promotion-added files left in place. `tools/TestManagedActionOverlay.ps1` also proves startup/dry-run overlay compilation can consume `questBoard.replaceWithFixedSet` as plot quest availability replacements, `inventory.disableItemSale` as `sourcePath` overlays that suppress positive official campaign trinket `price` values to 0, `town.unlockAllBuildings` as town building requirement `sourcePath` overlays, and `roster.enforceAvailabilityFilter` / `equipment.enforceAvailabilityFilter` as manifest-only availability policies that do not claim live enforcement. `tools/TestQuestBoardProfileRefresh.ps1` proves the targeted profile refresh path dry-runs, backs up, writes, and then detects unchanged fixed quest boards against a project-local watched profile fixture. `tools/TestQuestBoardRealtimeRefresh.ps1` proves the realtime save watcher can reapply the fixed board after a stable campaign save batch even when the changed file is not `persist.quest.json`. `tools/PrepareDecodedProfileWorkspace.ps1` now bridges a real top-level `profile_*` into a project-local decoded workspace and can run `--initialize-decoded-profile` against that copy, keeping the original Steam userdata save read-only. The quest writer also filters completed fixed quests from sidecar state when `removeCompleted` is enabled. The upgrade test covers a non-instanced building tree, an existing hero skill tree, and a generated hero skill tree. The generated roster JSON preserves DDSaveEditor/DSON float token shapes for fields such as `current_hp` and `m_Stress`, does not inherit marker fields from existing hero templates, and the encoded roster, upgrades, town, town-event, and quest probes remain accepted by DDSaveEditor. `town.setBuildingLevels` is still explicitly reported as unsupported because ordinary building levels are represented by verified upgrade purchases. Live validation showed the plot quest content overlay is consumed when the original game regenerates the quest board after a completed quest; the explicit profile refresh and realtime watcher refresh update the current board without running or simulating the full week-settlement chain. Live validation also showed that sidecar hero/trinket consumption plus profile policy is not yet enough to stop the original party UI from reselecting consumed heroes, and stagecoach/store suppression can be undone by a later original week settlement until a live save writer or runtime consumer exists.

## What Counts As Framework Progress

Progress should be measured by reusable primitives, not by special-case code:

| Scenario need | Generic primitive to implement |
| --- | --- |
| Remember used heroes | sidecar state list actions |
| Detect selected party | `party.selection_confirmed` event payload |
| Hide used heroes | `roster.filter_available_heroes` capability |
| Define a fixed challenge stage chain | `challenge.define_stage_chain` plus `quest.inject_fixed_stage` |
| Lock failed-stage retry selection | `challenge.lock_stage_selection` plus sidecar state |
| Remember used trinkets | sidecar state list actions plus `equipment.filter_available_trinkets` |
| Consume selection after any terminal attempt | `quest.attempt_resolved` plus reusable selection-consume actions |
| Fixed simultaneous quest board | `quest_board.replace_with_fixed_set` and content-derived quest facts |
| Stop stage coach generation | `stagecoach.suppress_recruits` |
| Initialize only new challenge saves | `profile.detect_new_or_uninitialized` plus `profile.mark_initialized` |
| Preserve campaign attrition and unwinnable states | normal save observation; no hidden restore/recovery action |
| Set fixed starting wallet resources | `wallet.set_currency_amounts` |
| Add gold after selected victories | `wallet.add_currency_on_event` with idempotent attempt identity |
| Prevent selling fixed trinket resources | `inventory.disable_item_sale` or an equivalent economy intercept |
| Normalize roster/trinkets/town | managed profile-normalization actions |
| Start with selected buildings locked or unlocked | `town.set_building_availability` |
| Unlock finale without reviving dead heroes | phase-scoped filter clearing, not roster rebuilding |
| Reuse original DD participation rule | phase-scoped filters and original quest entry rules |
| Define a straight or winding custom dungeon route | `map.define_fixed_layout` with room/hall graph data |
| Put fixed content in a specific map cell | `map.place_cell_content` and `map.place_named_encounter` |
| Define exact enemy quantity, type, and order | `encounter.define_mash` |
| Queue upgrades | sidecar state object-list actions |
| Move time forward | `campaign.week_advanced` event |
| Stop original upgrade | `building.intercept_upgrade_request` capability |
| Apply queued upgrade | verified `upgrade.apply_completed` action |

When these scenarios can run through the same rule engine without scenario-specific branches, the framework has a meaningful initial runtime capability.

When listing follow-up work from these scenarios, use the three-way classification in `docs/framework_capability_matrix.md`: existing ability that only needs plugin configuration, existing base that needs a declarative wrapper, or a truly missing framework primitive. For example, a linear `A quest completed -> show B quest` chain is configuration with existing primitives unless the goal is to add a shorter `questChain` authoring wrapper.
