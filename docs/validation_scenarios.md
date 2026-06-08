# Framework Validation Scenarios

This document uses two gameplay ideas as framework validation scenarios. They are not built-in templates. They are acceptance tests for whether generic primitives are expressive enough.

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

The current expected result is declaration-level success: all validation rules should be listed as active by `--explain-rules`. This proves the generic rule language can carry the scenario contract. It does not mean the runtime hooks and actions are implemented yet.

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

Only steps 1 and the manifest parsing pieces exist now.

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

## What Counts As Framework Progress

Progress should be measured by reusable primitives, not by special-case code:

| Scenario need | Generic primitive to implement |
| --- | --- |
| Remember used heroes | sidecar state list actions |
| Detect selected party | `party.selection_confirmed` event payload |
| Hide used heroes | `roster.filter_available_heroes` capability |
| Queue upgrades | sidecar state object-list actions |
| Move time forward | `campaign.week_advanced` event |
| Stop original upgrade | `building.intercept_upgrade_request` capability |
| Apply queued upgrade | verified `upgrade.apply_completed` action |

When both scenarios can run through the same rule engine without scenario-specific branches, the framework has a meaningful initial runtime capability.
