# Quest Board Policy Schema Draft

`questBoardPolicies` is the generic scheduling layer for deciding which referenced quests may appear on the task board. It is intentionally separate from static quest, dungeon, monster, map, art, and localization authoring. Those definitions should usually come from original game files, DLC, Workshop content, or plugin-bundled DD-format files and be declared through `contentRefs`.

The current implementation is validation and reporting only:

```text
questBoardPolicies
  -> validate policy mode, refresh triggers, entries, predicates, weights, and completion actions
  -> write modStateDirectory/_quest_board_policies/<plugin-id>/*.validation.json
  -> explain policy and entry facts through --explain-patches
```

It does not directly modify `persist.quest.json`, simulate week settlement, or replace the existing `questBoard.replaceWithFixedSet` writer. Later quest-board generators should consume these policy facts instead of hardcoding one mod's quest order.

## Manifest Shape

```json
{
  "contentRefs": {
    "quests": [
      { "id": "plot_kill_necromancer_3", "provider": "base", "required": true },
      { "id": "plot_kill_prophet_3", "provider": "base", "required": true }
    ]
  },
  "questBoardPolicies": [
    {
      "id": "author.chapter_one_board",
      "name": "Chapter One Board",
      "mode": "mixed",
      "refreshTriggers": [
        "onProfileInitialize",
        "onWeekAdvance",
        "immediateOnQuestComplete"
      ],
      "entries": [
        {
          "id": "necro_week_5",
          "questId": "plot_kill_necromancer_3",
          "availableWhen": {
            "weekGte": 5
          },
          "onCompleted": "remove"
        },
        {
          "id": "prophet_after_necro",
          "questId": "plot_kill_prophet_3",
          "pool": "boss_followups",
          "weight": 2,
          "availableWhen": {
            "completedQuest": "plot_kill_necromancer_3",
            "weekGte": 6
          },
          "onCompleted": "replace"
        }
      ]
    }
  ]
}
```

## Supported Fields

- `mode`: required. First slice accepts `fixed`, `random`, or `mixed`.
- `refreshTriggers`: required non-empty list. First slice accepts `onProfileInitialize`, `onWeekAdvance`, `immediateOnQuestComplete`, and `manual`.
- `entries[].questId`: concrete quest id that should be eligible.
- `entries[].sourceQuestId`: optional source quest id metadata for future custom quest writers. If `questId` is absent, it becomes the effective quest id.
- `entries[].pool`: optional grouping key for random or mixed boards.
- `entries[].weight`: optional positive integer weight.
- `entries[].availableWhen.completedQuest` / `completedQuests`: prerequisite completed quest ids.
- `entries[].availableWhen.notCompletedQuest` / `notCompletedQuests`: prerequisite absence checks.
- `entries[].availableWhen.weekGte`, `weekLte`, `weekEq`: week predicates.
- `entries[].availableWhen.phase`: sidecar or campaign phase label for future policy consumers.
- `entries[].availableWhen.stateKey` / `stateEquals`: generic sidecar state predicate metadata.
- `entries[].onCompleted`: optional, defaults to `keep`; supported values are `keep`, `remove`, `replace`, and `advancePhase`.
- `entries[].required`: optional, defaults to `true`; intended for future dependency-scoped policy consumers.

## Validation Rules

- Policy `id`, `mode`, `refreshTriggers`, and at least one entry are required.
- `mode`, refresh triggers, and completion actions must be in the supported sets above.
- Each entry must declare `questId` or `sourceQuestId`.
- `weight`, `weekGte`, `weekLte`, and `weekEq` must be non-negative where applicable; `weight` must be greater than zero.
- `weekLte` cannot be less than `weekGte`.
- `weekEq` must satisfy any declared lower or upper week bound.
- `stateEquals` requires `stateKey`.

The validator does not currently prove that a quest id exists. Use `contentRefs.quests` for provider-aware existence checks and duplicate-candidate reporting.

## Relationship To Quest Chains

`questChains` is best for authored ordered stage flows and can already materialize a deterministic `questBoard.replaceWithFixedSet` artifact when a plugin explicitly opts in.

`questBoardPolicies` is best for broader scheduling rules:

- quest A unlocks quest B;
- a quest appears only on or after week N;
- fixed quests and random eligible pools coexist;
- a completed quest stays, disappears, is replaced, or advances a phase;
- a post-ending chapter can expose different quest sets without the launcher knowing the chapter's concrete quest ids.

The two schemas can coexist. A chain can define stage order, while a policy can decide when those referenced stage quests are eligible for the board.

Regression coverage:

- `tools/TestQuestBoardPolicyContract.ps1` proves `questBoardPolicies` parses, validates, writes structured policy facts, and appears in patch explanation output through the validation plugin.
