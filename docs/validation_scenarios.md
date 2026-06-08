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

The baseline expected result is declaration-level success: all validation rules should be listed as active by `--explain-rules`. The first safe executor can also exercise implemented sidecar state actions through `--emit-event`. Runtime hooks and managed game mutation actions are still not implemented.

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

Validation manifest and data:

```text
plugins/_validation/challenge_run_contract/patches.json
plugins/_validation/challenge_run_contract/challenge.json
plugins/_validation/challenge_run_contract/sample_state.json
```

Dry-run tool:

```text
tools/TestChallengeRunDryRun.ps1
```

Run:

```text
.\tools\TestChallengeRunDryRun.ps1 -AssertSample
.\tools\TestChallengeRunDryRun.ps1 -Outcome stage_failed -SelectedHeroIds 1,2,7,8 -SelectedTrinketIds berserk_mask,immunity_mask,fortunate_armlet,sb_4,sb_3,sb_2,sb_1,bleeding_pendant
.\tools\TestChallengeRunDryRun.ps1 -Outcome stage_completed -SelectedHeroIds 1,2,7,8 -SelectedTrinketIds berserk_mask,immunity_mask,fortunate_armlet,sb_4,sb_3,sb_2,sb_1,bleeding_pendant
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

Steps 1-6 now exist for the sidecar state path: `--emit-event` can lock selection, record failed attempts, consume selected heroes/trinkets, and advance the stage in framework state. Later steps require runtime event observation and managed mutation capabilities.

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
| Queue upgrades | sidecar state object-list actions |
| Move time forward | `campaign.week_advanced` event |
| Stop original upgrade | `building.intercept_upgrade_request` capability |
| Apply queued upgrade | verified `upgrade.apply_completed` action |

When these scenarios can run through the same rule engine without scenario-specific branches, the framework has a meaningful initial runtime capability.
