# Boss Gauntlet Campaign Spec

This document captures the current target mod as a framework validation scenario. The concrete rules are intentionally described in terms of reusable facts, events, predicates, actions, sidecar state, and managed capabilities. Runtime code should add generic primitives when this spec cannot be expressed, not hardcode this gameplay loop.

## Gameplay Target

The mod turns a normal Darkest Dungeon campaign into a fixed-resource boss gauntlet:

1. A newly created save is automatically initialized into this challenge rule set on first entry. The initialization is idempotent: after the profile is marked initialized, entering the same save again must not rebuild the roster, restore spent trinkets, resurrect heroes, reset town state, or regenerate completed fixed quests.
2. On initialization, the roster is normalized to exactly two max-level heroes for each available hero class. Their combat and camping skills are unlocked and fully upgraded. The stage coach no longer offers recruits.
3. The estate owns two copies of each available non-reward trinket. Darkest Dungeon reward trinkets and boss trophy trinkets are excluded from the initial pool so they can still be earned through play.
4. Wallet resources are initialized from a data-driven currency map. The current target sets gold to `20000`, crystal shards to `36`, and explicitly records heirloom resources such as busts, portraits, deeds, and crests so the values can be tuned without adding new framework code.
5. Trinkets cannot be sold. This prevents the fixed trinket pool from becoming a repeatable or front-loaded gold source.
6. Town buildings are unlocked and fully upgraded. Town events are either suppressed or replaced with a fixed event message such as `Enjoy the inferno`. This is the boss-gauntlet configuration, not the only town state the framework should support; other campaigns may intentionally leave selected buildings locked at start.
7. Initialization resets pre-existing Darkest Dungeon plot progress for this challenge profile. This prevents test or conversion profiles that already reached DD2-DD4 from skipping the intended boss-gauntlet finale gate.
8. The quest board shows only the highest-difficulty boss quests for each non-Darkest region, all at the same time.
9. Defeating a boss removes that fixed boss quest from the board and does not generate a replacement quest.
10. Winning a pre-finale boss quest grants `10000` gold after the result is observed.
11. Before the Darkest Dungeon finale unlocks, each hero and each trinket can be selected only once. This selection is consumed on any terminal attempt result, successful or failed.
12. A failed or abandoned boss attempt is not rolled back. Original settlement state, deaths, stress, diseases, quirks, loot, and other resolved consequences remain as the game recorded them.
13. The game continues to save normally. The original profile save remains the canonical record for deaths, roster attrition, stress, inventory changes, and town consequences after initialization.
14. Because the stage coach is suppressed, a campaign can become unwinnable if too many heroes die or the player suffers a major strategic failure. That failure state is intentional. The player starts over by deleting the campaign and creating a new save.
15. When every fixed boss quest is defeated, the Darkest Dungeon finale unlocks.
16. In the finale phase, sidecar boss-gauntlet reuse restrictions are cleared. This does not resurrect dead heroes or recreate missing heroes. The framework should prefer the original game Darkest Dungeon participation rule: heroes who completed a Darkest Dungeon quest cannot enter another Darkest Dungeon quest.
17. Defeating the Ancestor completes the run.

If a hero dies during the boss gauntlet, "all heroes available in the finale" means the sidecar reuse restriction is cleared. It does not resurrect heroes unless a separate revival or roster-normalization rule explicitly says so.

## Save Lifecycle

The challenge is a normal game profile after initialization, not a separate external save format. Sidecar state tracks mod metadata and restrictions; the original profile stores the real campaign consequences.

Lifecycle:

```text
new profile created
  -> first eligible profile entry
  -> profile_normalization runs once
  -> initialized marker is recorded
  -> game saves normally for the rest of the run
  -> player deletes the profile manually to start over
```

Required properties:

- Initialization must be idempotent and guarded by an initialized marker.
- Initialization must not run on an already-started normal campaign unless the user explicitly opts that profile into conversion.
- Normal post-initialization saving is desirable, because deaths, stress, quirks, diseases, resource loss, and settlement attrition are part of the challenge.
- The framework must not add hidden recovery loops such as stage coach replacement heroes, automatic resurrection, automatic trinket restoration, or quest rerolls unless another explicit mod rule declares them.
- Sidecar state should be recoverable from the original profile where possible, but the original profile should not depend on sidecar state to preserve normal game consequences.
- If original-save writes are used for initialization, they must be schema-verified, logged, reversible before first commit where practical, and gated as managed capabilities.

## Phases

```text
new_profile_detected
  -> profile_normalization
  -> boss_gauntlet
  -> darkest_finale
  -> run_completed
```

### Profile Normalization

This phase prepares a save or sidecar overlay for the custom rule set.

Required generic capabilities:

| Need | Reusable primitive |
| --- | --- |
| Detect a profile that needs first-run setup | `profile.detect_new_or_uninitialized` |
| Record setup completion | `profile.mark_initialized` |
| Two heroes per class | `roster.ensure_class_instances` |
| Max hero level and equipment | `roster.set_progression` |
| All skills unlocked and upgraded | `roster.set_skill_unlocks` |
| No stage coach recruits | `stagecoach.suppress_recruits` |
| Two of every trinket | `estate.ensure_inventory_counts` |
| Fixed starting wallet resources | `wallet.set_currency_amounts` |
| Gold reward after boss victory | `wallet.add_currency_on_event` |
| Disable trinket selling | `inventory.disable_item_sale` |
| Fully unlocked town | `town.unlock_all_buildings` |
| Per-building town access | `town.set_building_availability` |
| Fully upgraded town | `town.set_building_levels` |
| Fixed or suppressed event | `town_event.override_current` or `town_event.suppress_rotation` |
| Reset incompatible plot progress for converted/test profiles | `campaign.reset_plot_progress` |
| Fixed quest board | `quest_board.replace_with_fixed_set` |

The first implementation should materialize these as managed action artifacts and diagnostics. The final gameplay target likely needs managed original-save initialization so the game can keep saving normally afterward. Any original-save write path must remain explicitly documented, reversible before first commit where practical, schema-verified, and guarded by an idempotent initialized marker.

The town primitive should be able to express both "unlock everything" and "lock or hide specific buildings until a condition is met." Some original campaigns start with unavailable buildings, and custom campaigns may use that state as progression. A mod should not need to fake a locked building through unrelated upgrade or event fields.

### Boss Gauntlet

The boss-gauntlet phase owns the fixed pre-finale boss list. The list should be generated from content facts where possible:

```text
quest.type == kill_boss
quest.region != darkest_dungeon
quest.difficulty == highest available difficulty for that boss family
```

Concrete quest ids are acceptable inside validation fixtures, but the reusable primitive should be able to build a fixed set from content queries.

State shape:

```json
{
  "initialized": true,
  "phase": "boss_gauntlet",
  "fixedQuestIds": [],
  "completedQuestIds": [],
  "consumedHeroIds": [],
  "consumedTrinketInstanceIds": [],
  "consumedTrinketIds": [],
  "activeSelection": null,
  "attempts": []
}
```

Selection lifecycle:

```text
quest.selection_confirmed
  -> lock activeSelection
quest.attempt_resolved(success=true)
  -> record attempt
  -> consume selected heroes and trinkets
  -> add victory gold
  -> mark quest completed
  -> clear activeSelection
  -> unlock darkest_finale if every fixed quest is completed
quest.attempt_resolved(success=false)
  -> record attempt
  -> consume selected heroes and trinkets
  -> keep quest available
  -> clear activeSelection
```

This intentionally differs from the early `challenge_run_contract` validation scenario. Failure is not "retry with locked selection"; failure is "attempt consumed, consequences kept, remaining pool continues."

Required generic capabilities:

| Need | Reusable primitive |
| --- | --- |
| Preserve original save consequences | no rollback action; normal save watcher observation only |
| Know current selected quest | `quest.observe_selection_confirmed` |
| Know selected heroes | `party.observe_selection_confirmed` |
| Know selected trinkets | `equipment.observe_loadout_confirmed` |
| Observe terminal quest result | `quest.observe_attempt_resolved` |
| Idempotent attempt recording | `attempt.record_once` or stable `attemptFingerprint` payload |
| Add gold only for successful boss attempts | `wallet.add_currency_on_event` gated by event success and attempt fingerprint |
| Consume selected heroes on any terminal result | `selection.consume_heroes` |
| Consume selected trinkets on any terminal result | `selection.consume_trinkets` |
| Hide completed fixed boss quests | `quest_board.filter_completed_fixed_quests` |
| Keep failed boss quests available | `quest_board.keep_uncompleted_fixed_quests` |
| Unlock phase after all objectives | `state.transition_when_all_completed` |
| Enforce pre-finale hero reuse | `roster.enforce_availability_filter` |
| Enforce pre-finale trinket reuse | `equipment.enforce_availability_filter` |

### Darkest Finale

The finale phase should minimize custom logic:

1. Clear or ignore `consumedHeroIds`, `consumedTrinketIds`, and pre-finale availability filters.
2. Do not resurrect dead heroes, recreate missing heroes, restore trinkets, or otherwise recover resources lost during the boss-gauntlet phase.
3. Use original Darkest Dungeon quest definitions and original post-DD hero restriction where possible.
4. Observe finale completion through progression or campaign log facts.
5. Mark sidecar run state as `run_completed` after the Ancestor quest resolves successfully.

Required generic capabilities:

| Need | Reusable primitive |
| --- | --- |
| Clear sidecar restrictions by phase | `state.clear_paths` or phase-scoped filters |
| Preserve dead or missing heroes | no revival action; original roster facts remain authoritative |
| Keep original DD entry restriction | `quest.use_original_entry_rules` / no override |
| Observe DD completion | `progression.observe_plot_completion` |
| Complete run | `state.set_phase` |

## Post-Ending Expansion Maps

The earlier post-Ancestor expansion idea is a separate pressure test from the boss-gauntlet loop, but it should use the same rule model. It needs a quest or chapter chain that can unlock after the Ancestor, then point to custom map and encounter content.

Required generic capabilities:

| Need | Reusable primitive |
| --- | --- |
| Unlock a new chapter after a plot quest | `quest_chain.transition_on_completion` |
| Show only the chapter's available quests | `quest_board.replace_with_fixed_set` or `quest_board.filter_by_phase` |
| Define a deterministic room/hall graph | `map.define_fixed_layout` |
| Place content in a specific room or hall tile | `map.place_cell_content` |
| Define a specific monster lineup | `encounter.define_mash` |
| Reuse a lineup from a map cell | `map.place_named_encounter` |
| Select background or special room art | `map.set_room_visuals` or content overlay assets |

The map primitive should describe topology as data: rooms, corridors, connections, entrance, final room, and per-cell content. A straight-line dungeon and a winding dungeon should differ only in layout data, not in framework code. The first implementation should verify the original fixed plot-map format before promising exact per-cell runtime behavior.

## Rule Sketch

The validation plugin should eventually express the boss gauntlet without special C# branches:

```json
{
  "on": "quest.attempt_resolved",
  "when": {
    "all": [
      { "state": "bossGauntlet.phase", "op": "equals", "value": "boss_gauntlet" },
      { "event": "questId", "op": "in", "valueFromState": "bossGauntlet.fixedQuestIds" }
    ]
  },
  "actions": [
    { "type": "attempt.recordOnce", "capability": "state.sidecar" },
    { "type": "selection.consumeHeroes", "capability": "state.sidecar" },
    { "type": "selection.consumeTrinkets", "capability": "state.sidecar" },
    { "type": "wallet.addCurrencyOnEvent", "capability": "wallet.modify_currency" },
    { "type": "quest.markCompletedIfSuccessful", "capability": "state.sidecar" },
    { "type": "state.transitionWhenAllCompleted", "capability": "state.sidecar" }
  ]
}
```

The exact action names can change during implementation. The important constraint is that the actions remain reusable by other mods that need "consume a selected resource after an observed result" or "unlock a phase after a set is complete."

## Implementation Ladder

1. Add this spec and a validation plugin draft with no live game mutation.
2. Add an idempotent profile initialization model: detect uninitialized eligible profiles, record initialized state, and prove repeat entry does not rebuild or restore the run.
3. Add generic state actions needed by the rule sketch: consume selected resources, mark completed when successful, clear phase-scoped restrictions, and transition when a set is complete.
4. Add save/content fact extractors for fixed quest discovery, stage coach recruits, town buildings, building levels, town events, hero DD participation flags, and trinket inventory if current facts are insufficient.
5. Materialize managed artifacts for profile normalization: roster pool, trinket inventory, town maxing, town event override, and fixed quest board.
6. Add a decoded-save applier for supported profile-normalization actions, starting with wallet resources, so write behavior can be tested on project-local decoded save copies before original-save mutation exists.
7. Add runtime consumers one by one, starting with fixed quest board enforcement and pre-finale hero/trinket availability enforcement.
8. Only after diagnostics and tests are stable, consider original-save write capabilities for profile normalization. Those writes must be schema-verified, logged, reversible, and gated as managed or risky capabilities.

Current implementation status: steps 1-3 are represented in the validation plugin and safe sidecar executor. Step 5 now has observe-first managed artifacts for profile normalization, including roster shape, progression, skills, upgrade purchases, stagecoach suppression, trinket inventory counts, starting wallet resources, trinket-sale lockout, town state, town event override, and fixed quest board. Step 6 has a first decoded-save initialization path for starting estate, roster, upgrade, town, town-event, policy, and quest-board resources: `--initialize-decoded-profile --managed-action-save-dir <dir>` initializes sidecar state, emits `profile.initialization_requested`, previews the fixed quest board, runs managed-action apply, and records per-action apply details in `logs/decoded_profile_initialization_report.json`. Dry-run remains the default. Adding `--write-managed-actions` writes `wallet.setCurrencyAmounts` plus `estate.ensureInventoryCounts` into a project-local decoded `persist.estate.json` copy, `roster.ensureClassInstances` plus `roster.setProgression` plus `roster.setSkillUnlocks` into a project-local decoded `persist.roster.json` copy, `upgrade.ensurePurchases` into a project-local decoded `persist.upgrades.json` copy, `stagecoach.suppressRecruits` plus district `town.unlockAllBuildings` into a project-local decoded `persist.town.json` copy, `townEvent.overrideCurrent` current-event suppression into decoded `persist.town_event.json`, `questBoard.replaceWithFixedSet` into a project-local decoded `persist.quest.json` copy, and policy-only `inventory.disableItemSale` / town-event message data into `_ddrt_profile_policy.json`. `tools/PrepareDecodedProfileWorkspace.ps1 -EncodeInitializedProfile` can now re-encode the initialized decoded persist files into a project-local sandbox `encoded_profile` and roundtrip-decode/parse them for validation without writing Steam userdata. `tools/PromoteEncodedProfileWorkspace.ps1` adds the controlled promotion step before any real-save trial: dry-run by default, explicit external-target opt-in, running-game write guard, decoded-content-changed file selection by default, target snapshot backup, hash verification, and restore of overwritten files from the backup manifest. Startup and `--dry-run` can now consume `questBoard.replaceWithFixedSet` as plot quest availability overlays and `inventory.disableItemSale` artifacts as content overlays that set official campaign trinket entry `price` values to 0; this suppresses sale value but still needs live UI validation before it can be called a hard sell-button lockout. `--refresh-quest-board-profile <profileId>` can explicitly write the generated fixed-board `persist.quest.json` into a configured watched profile with dry-run, backup, and running-game protection. `questBoardAutoRefreshEnabled` can also let the realtime save watcher reapply that same generated board after any successfully bridged stable campaign save batch; this is broader than only reacting to `persist.quest.json` writes and covers week-settlement paths where the original game rewrites related town/progression files before the board is regenerated. Non-project running-game writes are gated by `questBoardAutoRefreshAllowRunningGameSaveWrite`. These paths are intentionally narrower than a full original week settlement: they update the current quest board but do not touch treatment timers, construction queues, hero consequences, or campaign logs. The roster writer can add class instances from a clean hero blueprint with stable pseudo-random quirks, content-derived selected skills, full roster skill lists, and progression normalization for existing and generated supported-class heroes. It no longer deep-copies existing heroes as the generated object base, and generated random quirks now respect content-declared `singleton` tags across the whole roster. `estate.ensureInventoryCounts` can filter content-derived trinket sources by rarity, which the boss-gauntlet validation plugin uses to exclude `darkest_dungeon` and `trophy` trinkets from the initial inventory. The upgrade writer can mark content-defined building, combat skill, camping skill, weapon, and armour upgrade requirements as purchased, including instanced hero purchases derived from roster hero ids. The quest-board writer resolves quest ids from enabled plot quest content and can remove completed fixed quests based on sidecar state. The validation plugin now has save-facts bridge rules for active-raid selection and terminal boss attempt observation; it uses `campaignLog.partyRaidRecordCount` as the observed attempt identity so repeated watcher passes and stale previous quest results do not consume a newly locked retry selection. `town.setBuildingLevels` remains artifact-only because ordinary building upgrade levels are currently represented by upgrade purchases, not a verified `persist.town.json` scalar. Custom town-event text still needs live runtime/content consumers; the decoded-save writer records those policies without inventing unknown original-save fields. Live validation showed that the plot quest content overlay is consumed after the original game regenerates the quest board at quest completion; explicit profile refresh covers initialization cases where waiting for a week transition would be wrong, and watcher refresh covers later original task-board regeneration. Live validation also showed that sidecar hero/trinket consumption does not yet stop the original party UI from reselecting consumed heroes, and stage coach recruit suppression does not yet persist after a later original week settlement; both require a live save writer or runtime UI/gameplay consumer rather than more sidecar-only actions.

## Open Design Points

- Exact boss quest set should be content-derived, but fixtures may start with explicit original quest ids.
- Town initialization should support explicit locked, unlocked, and upgraded states per building. The boss-gauntlet scenario chooses all unlocked and maxed, but the primitive must not be limited to that case.
- Fixed map layout support still needs original-format research. Current save facts can inspect generated raid maps, and original mash files show deterministic enemy lineups are feasible, but exact fixed room/hall authoring needs a focused prototype.
- Trinket consumption should prefer instance ids if the game exposes stable instances. If only trinket ids and counts are available, consuming one copy must decrement a sidecar count and later map that count to UI/equipment enforcement.
- DLC hero classes and DLC trinkets should be included only if their content exists in the active install and is enabled by the profile or plugin configuration.
- Finale availability should not bypass original death or missing-roster constraints unless the mod explicitly adds a revival/rebuild rule.
- Realtime profile policy enforcement is still incomplete outside the quest board: stage coach recruits and pre-finale hero/trinket availability need schema-verified live save mutation or runtime hooks after week settlement and party selection changes.
