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

The baseline expected result is declaration-level success: all validation rules should be listed as active by `--explain-rules`. The first safe executor can also exercise implemented sidecar state actions through `--emit-event`, and selected managed actions can now materialize observe-first artifacts. The launcher can compile fixed-board quest artifacts into a runtime-visible overlay manifest, and `questBoard.replaceWithFixedSet` can feed the existing virtual file layer for plot quest availability. Fixed-board artifacts can also be explicitly written to a watched profile's current `persist.quest.json` through the targeted quest-board refresh path, or re-applied by the save watcher after a live `persist.quest.json` change when quest-board auto refresh is enabled. Broader managed game mutation is still incomplete.

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

Steps 1-2 and the safe sidecar state action path exist now. Scenario 3 uses the same selection and sidecar primitives in the current boss-gauntlet campaign flow.

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

## Scenario 3: Fixed Resource Boss Gauntlet Campaign

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
capability trinket.patch_entry
capability upgrade.ensure_purchases
capability town.unlock_all_buildings
capability town.set_building_availability
capability town_event.override_current
capability quest_board.replace_with_fixed_set
capability quest_board.filter_completed_fixed_quests
capability selection.consume_heroes
capability selection.consume_trinkets
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
7. Managed action artifacts can describe the normalized roster, wallet, trinket inventory, town maxing, per-building town availability, fixed quest board, trinket-sale lockout, sidecar hero/trinket reuse state, and town-event override without mutating original saves.
8. Decoded-save managed action application can dry-run and apply supported profile-normalization actions against a project-local decoded save copy before any original-save writer exists.
9. Original-first consumers enforce the fixed quest board, trinket entry sale-value patches, wallet reward, and pre-finale hero/trinket availability. Use original content/save mechanisms where they exist, and reserve runtime/UI hooks for gaps that cannot be represented safely.
10. Managed original-save initialization, if introduced, is schema-verified, logged, idempotent, and does not restore later campaign failures.
11. The finale phase can rely on original Darkest Dungeon entry restrictions where possible, does not revive dead heroes, and starts from a phase-gated `plot_darkest_dungeon_1` quest board.

Steps 1-5 now exist for the sidecar state path. `tools/TestBossGauntletContract.ps1` initializes the plugin state, proves repeat initialization does not reset changed run state, locks selected heroes/trinkets, consumes that selection on success and failure, pays successful rewards once per attempt identity, marks only successful quests complete, transitions to `darkest_finale` when every fixed boss quest is complete, clears pre-finale reuse restrictions, preserves observed dead hero state, and generates the DD1 -> DD2 -> DD3 -> DD4 finale quest-board policy only after every fixed boss is complete. The profile-normalization slice also exists as observe-first managed artifacts for roster, upgrade purchases, stagecoach, trinket inventory, starting wallet resources, trinket entry sale-value patches, town state, town store suppression, town event, and fixed quest board. `tools/TestManagedActionSaveApplier.ps1` proves the decoded-save applier can dry-run and write the supported initialization actions into project-local decoded save copies while recognizing content-overlay-only trinket entry patches. `tools/TestContinuousProfileActionApply.ps1` proves continuous reapply now selects only stagecoach, town store, and town-event artifacts while excluding one-time wallet initialization and sidecar-only selection consumption. `tools/TestManagedActionOverlay.ps1` proves startup/dry-run overlay compilation can consume `questBoard.replaceWithFixedSet` as plot quest availability replacements, `trinket.patchEntry` as explicit id and rarity-selector `set`/`remove` trinket entry patches, and `town.unlockAllBuildings` as town building requirement `sourcePath` overlays. Live validation showed the plot quest content overlay is consumed when the original game regenerates the quest board after a completed quest; explicit profile refresh and realtime watcher refresh update the current board without simulating the full week-settlement chain. Sidecar hero/trinket consumption still does not stop the original party UI from reselecting consumed resources; hero blocking should next be tested through original roster unavailable/missing/status projection.

`tools/TestQuestBoardRealtimeRefresh.ps1` now also covers the live drift case where one fixed boss is already completed and a stale DD4 policy artifact exists: current empty policy materialization supersedes the stale finale artifact, the refreshed board keeps seven remaining boss quests, and continuous profile auto-apply clears regenerated stagecoach and town-store data with backups.

## What Counts As Framework Progress

Progress should be measured by reusable primitives, not by special-case code:

| Scenario need | Generic primitive to implement |
| --- | --- |
| Remember used heroes | sidecar state list actions |
| Detect selected party | `party.selection_confirmed` event payload |
| Hide used heroes | original roster unavailable/missing/status projection |
| Define a fixed boss quest set and finale chain | `quest_board.replace_with_fixed_set`, `quest.chain.define`, and `quest_board.policy` |
| Track active boss selection | `selection.lock` plus sidecar state |
| Remember consumed trinkets | sidecar state list actions plus `selection.consume_trinkets` |
| Consume selection after any terminal attempt | `quest.attempt_resolved` plus reusable selection-consume actions |
| Fixed simultaneous quest board | `quest_board.replace_with_fixed_set` and content-derived quest facts |
| Stop stage coach generation | `stagecoach.suppress_recruits` |
| Initialize only eligible new profiles | `profile.detect_new_or_uninitialized` plus `profile.mark_initialized` |
| Preserve campaign attrition and unwinnable states | normal save observation; no hidden restore/recovery action |
| Set fixed starting wallet resources | `wallet.set_currency_amounts` |
| Add gold after selected victories | `wallet.add_currency_on_event` with idempotent attempt identity |
| Suppress fixed trinket sale value | `trinket.patch_entry` with explicit `price: 0`; true UI sell blocking still needs a verified economy intercept |
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
