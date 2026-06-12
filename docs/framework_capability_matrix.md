# Framework Capability Matrix

This document is the review checklist for turning gameplay ideas into reusable framework capabilities. It exists to prevent the framework from becoming a collection of hardcoded examples.

Concrete ideas are still useful. A concrete mod design is a pressure test. The framework should absorb only the generic primitive that the idea reveals.

## Core Rule

Do not add a new C# or native branch whose only meaningful caller is one named mod idea.

When an idea cannot be expressed by existing rules, classify the missing primitive:

| Missing capability | Add this kind of primitive | Avoid this |
| --- | --- | --- |
| The framework cannot see a game/save/content condition | fact extractor or content index | a rule that assumes one sample save |
| The framework cannot detect timing | observe-only event or save fact bridge rule | polling a specific mod's state name in core code |
| The framework cannot keep durable mod memory | sidecar state schema/action | writing fake state into unrelated original save fields |
| The framework cannot change a resource | `wallet.*`, `estate.*`, or inventory action | `bossGauntlet.giveStartingGold` |
| The framework cannot create or normalize heroes | `roster.*` action driven by class/content facts | copying a fixed hero object from a sample save |
| The framework cannot express skill training | `upgrade.*` or town upgrade-tree writer | setting a boss-gauntlet-only flag in roster |
| The framework cannot control available quests | `quest_board.*` or `quest.*` action | hardcoding one campaign's quest ids in launcher code |
| The framework cannot control town access state | `town.*` building availability and level actions | only supporting "unlock everything" initialization |
| The framework cannot define dungeon shape | `map.*` fixed-layout or generated-layout actions | a special post-Ancestor map branch |
| The framework cannot define deterministic fights | `encounter.*` mash or named-encounter actions | putting monster lineups in quest-chain code |
| The framework cannot restrict selection | `selection.*`, `roster.filter*`, or `equipment.filter*` | special casing one stage chain |
| The framework cannot replace original behavior safely | managed intercept capability with diagnostics | silent memory patch or broad fallback |
| The framework needs unsupported engine behavior | risky native capability gated by exe hash | treating risky hook behavior as a normal safe action |

## Intake Checklist

Before implementing a new gameplay feature, answer these questions:

1. Which fact, event, predicate, action, state, and capability does the feature need?
2. Which existing primitive already covers part of it?
3. Which missing piece is generic enough for another mod with different content?
4. Can the new primitive be tested without the original game running?
5. Does the implementation keep concrete quest ids, hero ids, item ids, and stage ids inside plugin data, fixtures, or docs instead of framework runtime code?
6. What is the lowest-risk status this primitive can start with: planned, materialized, observed, passive, intercepted, or stable?
7. What diagnostics will tell a mod author why it did or did not run?

If the answer to question 3 is weak, keep the behavior in a validation plugin or sample until the generic shape is clearer.

## Current Pressure Tests

| Pressure test | Generic primitives it should prove | Not acceptable as framework core |
| --- | --- | --- |
| Fixed boss gauntlet campaign | profile initialization, roster generation, skill unlock lists, wallet/resource setup, trinket inventory setup, fixed quest board, selection consumption, phase transition | launcher code that knows the boss-gauntlet stage ids |
| Delayed building upgrades | upgrade request observation, cost handling, sidecar queue, week advance event, completion action | hardcoded blacksmith-only delay logic |
| Fixed-stage challenge run | stage-chain state, quest injection artifact, party selection observation, completion/failure events | a single built-in challenge mode path |
| Post-ending expansion | progression facts, quest/region unlocks, narration/content overlays, phase-gated quest board, fixed map topology, named encounters | hardcoded "after ancestor" script branch |

These tests should continue to be concrete enough to catch real gaps, but their reusable results should be named as framework primitives.

## Current Generic Capability Shape

| Area | Current generic form | Next likely gap |
| --- | --- | --- |
| Wallet/resources | `wallet.setCurrencyAmounts`, `wallet.modifyCurrency` | live original-save write policy and reward identity coverage |
| Estate inventory | `estate.ensureInventoryCounts` | item sale/use restrictions and item identity tracking |
| Roster generation | `roster.ensureClassInstances` clean hero blueprint | richer hero blueprint arguments and class-specific defaults |
| Roster skills | `roster.setSkillUnlocks` skill id lists | live enforcement and richer skill selection policies |
| Upgrade purchases | `upgrade.ensurePurchases` for content-defined building, hero skill, weapon, and armour requirements | original-save write policy and narrower per-tree/per-level selection modes |
| Town runtime | `stagecoach.suppressRecruits` and district-scoped `town.unlockAllBuildings` decoded-save writers | ordinary building unlock UI verification, `town.setBuildingAvailability`, and `town.setBuildingLevels` semantics |
| Selection state | `selection.lock`, `selection.consumeHeroes`, `selection.consumeTrinkets` | live UI/game enforcement instead of sidecar-only observation |
| Quest board | `questBoard.replaceWithFixedSet` decoded-save writer from enabled plot quest content | runtime board intercept and broader availability modes |
| Quest overlays | `quest.injectFixedStage` virtual file consumer | full quest list/availability control |
| Map topology | fixed map facts, topology validation, scalar `mapTemplates`, generated `.dm` sourcePath overlays, and validate-only `mapLayoutTemplates` room/corridor graph diagnostics | high-level `mapLayoutTemplates` compiler for room/hall graph, entrance/final room, and per-cell content |
| Encounters | original mash content can be indexed from dungeon files | `encounter.defineMash` and named encounter placement tied to fixed layouts |
| Save-to-event bridge | plugin-declared `factEventRules` and payload projections | more fact extractors and reusable projection operators |
| Sidecar state | plugin namespaced state schema/actions | reset/backup policy and campaign scoping |

## Red Flags

Stop and redesign if a change has one of these shapes:

- A framework class contains a specific mod id, stage id, quest id, hero id, or trinket id outside validation/test code.
- A new action name describes a full gameplay idea instead of a reusable operation.
- A fallback silently continues after losing correctness guarantees.
- A save writer copies a large unknown object and relies on overwriting enough fields.
- A rule can only work with one sample save rather than facts/content definitions.
- A risky hook is presented as stable before observe-first diagnostics and a focused regression path exist.

## Review Output

Every new primitive should leave a small trail:

- capability or action name,
- status and risk level,
- required fact/event/state inputs,
- what concrete pressure test motivated it,
- one other plausible mod that could reuse it,
- focused test or dry-run command.

This keeps the user's ideas valuable as discovery tools without letting them define the framework boundary.
