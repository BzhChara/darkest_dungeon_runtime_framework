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
| The framework cannot reset or gate story progress for a converted profile | `campaign.*` progression action over declared quest ids | treating one test profile's DD4 state as a global rule |
| The framework cannot control town access state | `town.*` building availability and level actions | only supporting "unlock everything" initialization |
| The framework cannot reference or validate a special fixed map | optional/experimental `map.*` inspection or fixed-layout overlay | making a full map editor the default path when DD/Workshop map formats already work |
| The framework cannot define deterministic fights | `encounter.*` mash or named-encounter actions | putting monster lineups in quest-chain code |
| The framework needs a monster, skill, curio, loot table, localization key, or asset that normal DD/Workshop content can already provide | `contentRef.*` declaration plus content index/reference validation | a framework-specific monster/skill/asset generator as the default path |
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
6. Is the missing piece truly runtime behavior, or should it be an external content reference checked by the framework?
7. What is the lowest-risk status this primitive can start with: planned, materialized, observed, passive, intercepted, or stable?
8. What diagnostics will tell a mod author why it did or did not run?

If the answer to question 3 is weak, keep the behavior in a validation plugin or sample until the generic shape is clearer.

## Next-Step Classification

When proposing follow-up work, classify it before calling it a framework gap:

| Classification | Meaning | Example | Correct next step |
| --- | --- | --- | --- |
| Existing ability, configuration only | The facts, predicates, actions, state, and writer already exist. A plugin can express the behavior with current rule syntax. | `DD1 -> DD2 -> DD3 -> DD4` as a linear quest-board chain using completed quest state and newer `questBoard.replaceWithFixedSet` artifacts | Add or adjust plugin configuration and tests. Do not describe it as a missing runtime capability. |
| Existing base, needs declarative wrapper | The behavior can be expressed today, but only with repetitive or error-prone low-level rules. | A `questChain` shorthand that expands ordered quest stages into the repeated completion predicates and board materialization actions | Add a schema/materializer helper if repeated by multiple scenarios; keep the low-level primitives unchanged. |
| Missing framework primitive | The current rule system cannot observe, decide, persist, or mutate the needed thing without new reusable framework code. | Blocking consumed heroes in the original party UI after sidecar state marks them used | Add a generic fact extractor, predicate, action, save writer, or hook capability with diagnostics and tests. |

This classification is part of the planning output. It prevents treating a missing plugin rule as if it required new C# or native hook work.

## Current Pressure Tests

| Pressure test | Generic primitives it should prove | Not acceptable as framework core |
| --- | --- | --- |
| Fixed boss gauntlet campaign | profile initialization, roster generation, skill unlock lists, wallet/resource setup, trinket inventory setup, campaign plot progress reset, fixed quest board, selection consumption, all-objectives phase transition, phase-gated finale board materialization | launcher code that knows the boss-gauntlet stage ids |
| Delayed building upgrades | upgrade request observation, cost handling, sidecar queue, week advance event, completion action | hardcoded blacksmith-only delay logic |
| Fixed-stage challenge run | stage-chain state, quest injection artifact, party selection observation, completion/failure events | a single built-in challenge mode path |
| Post-ending expansion | progression facts, quest/region unlocks, narration/content overlays, phase-gated quest board, fixed map topology, named encounters | hardcoded "after ancestor" script branch |

These tests should continue to be concrete enough to catch real gaps, but their reusable results should be named as framework primitives.

## Current Generic Capability Shape

| Area | Current generic form | Next likely gap |
| --- | --- | --- |
| Wallet/resources | `wallet.setCurrencyAmounts`, `wallet.modifyCurrency` | live original-save write policy and reward identity coverage |
| Estate inventory | `estate.ensureInventoryCounts` with content-source rarity exclusions, `inventory.disableItemSale` policy plus trinket price `sourcePath` overlay | live hard sell/use restrictions and item identity tracking |
| Roster generation | `roster.ensureClassInstances` clean hero blueprint plus `roster.setProgression` decoded-save normalization for existing/generated heroes; random quirk generation respects content `singleton` tags across the generated roster | richer hero blueprint arguments, exact-count policies, and class-specific defaults |
| Roster skills | `roster.setSkillUnlocks` skill id lists | live enforcement and richer skill selection policies |
| Upgrade purchases | `upgrade.ensurePurchases` for content-defined building, hero skill, weapon, and armour requirements | original-save write policy and narrower per-tree/per-level selection modes |
| Town runtime | `stagecoach.suppressRecruits` and district-scoped `town.unlockAllBuildings` decoded-save writers | ordinary building unlock UI verification, `town.setBuildingAvailability`, and `town.setBuildingLevels` semantics |
| Selection state | `selection.lock`, `selection.consumeHeroes`, `selection.consumeTrinkets` | live UI/game enforcement instead of sidecar-only observation |
| Campaign progress | `campaign.resetPlotProgress` decoded-save writer removes declared completed plot quest records, clears matching plot achievements, clears stale last quest references, and can clear hero Darkest Dungeon participation flags; sidecar rules can gate phase transitions on completed objective sets | broader chapter/ending policy and live UI refresh |
| Quest board | `questBoard.replaceWithFixedSet` decoded-save writer, virtual `persist.quest.json` overlay, plot quest availability overlay from enabled plot quest content, explicit watched-profile refresh with backup, opt-in realtime refresh after successfully bridged stable campaign save batches, `questBoardPolicies` validation/reporting/candidate preview/facts-driven resolution/materialization/bridge auto-materialization/profile-scoped scheduling for fixed/random/mixed eligibility rules, weighted pool draws, slot caps, and managed fixed-board artifact output, plus generic managed action artifact retention preview/prune tooling | broader non-plot quest list control |
| Quest overlays | `quest.injectFixedStage` and fixed-board plot quest virtual file consumers | full roster/UI selection control |
| Profile workspace | `tools/PrepareDecodedProfileWorkspace.ps1` decodes a source profile into project-local workspace, can initialize/write managed actions, re-encode to sandbox `encoded_profile`, and roundtrip-validate decoded JSON; `tools/PromoteEncodedProfileWorkspace.ps1` can dry-run/write decoded-content-changed encoded files with backup manifest, hash checks, external-target guard, running-game write guard, and restore of overwritten files | live `profile_3` promotion trial and broader UI/runtime validation |
| Map topology | optional/experimental fixed map facts, topology validation, scalar `mapTemplates`, generated `.dm` sourcePath overlays, and `mapLayoutTemplates` room/corridor graph compiler for existing area/tile/door templates | contentRef-first map and generator references; defer arbitrary `.dm` creation and live `mapState.*` mutation until a concrete runtime need exists |
| Content references | plugin-declared `contentRefs` validate base, official DLC, declared Workshop IDs, and plugin-bundled quest/dungeon/monster/hero class/hero skill/effect/buff/trait/quirk/trinket/curio/loot table/raid setting/localization/mash/map/map-generator references with provider-aware reports, duplicate-candidate diagnostics, preferred resolution, and required/optional missing policy | finer module-level disablement for missing required content and broader static file conflict summaries |
| Encounters | original mash content can be indexed from dungeon files | `encounter.defineMash`, referenced monster ids, and named encounter placement tied to fixed layouts |
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
