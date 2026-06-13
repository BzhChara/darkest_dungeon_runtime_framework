# Quest Board Policy Schema Draft

`questBoardPolicies` is the generic scheduling layer for deciding which referenced quests may appear on the task board. It is intentionally separate from static quest, dungeon, monster, map, art, and localization authoring. Those definitions should usually come from original game files, DLC, Workshop content, or plugin-bundled DD-format files and be declared through `contentRefs`.

The current implementation is validation, reporting, and content-backed candidate preview only:

```text
questBoardPolicies
  -> validate policy mode, refresh triggers, entries, predicates, weights, and completion actions
  -> write modStateDirectory/_quest_board_policies/<plugin-id>/*.validation.json
  -> explain policy and entry facts through --explain-patches
  -> preview declared candidate quests through --preview-quest-board-policies
  -> write logs/quest_board_policy_preview_report.json
```

It does not directly modify `persist.quest.json`, simulate week settlement, evaluate current completed-quest state, or replace the existing `questBoard.replaceWithFixedSet` writer. Later quest-board generators should consume these policy facts instead of hardcoding one mod's quest order.

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

The validator does not prove that a quest id exists. Use `contentRefs.quests` for provider-aware existence checks and duplicate-candidate reporting. Use `--preview-quest-board-policies` to resolve policy candidates against the enabled plot quest catalog and report missing or malformed quest content.

## Candidate Preview

Run:

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --preview-quest-board-policies --no-inject
```

The report is written to `logs/quest_board_policy_preview_report.json`.

Each candidate records:

- the policy and entry id that produced it;
- fixed vs pool/weighted scheduling metadata;
- `contentStatus`: `found`, `missingRequired`, `missingOptional`, `invalidRequiredContent`, or `invalidOptionalContent`;
- `availabilityStatus`: `staticallyEligible` when no predicate is declared, or `requiresRuntimeFacts` when the entry needs week, completed-quest, phase, or sidecar-state facts;
- resolved content facts from the plot quest definition, including source path, type, dungeon, difficulty, length, and goal ids.

This preview is deliberately not a live scheduler yet. Entries gated by `weekGte`, `completedQuest`, `phase`, or `stateKey` are reported as requiring runtime facts rather than being accepted or rejected from one sample save.

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

- `tools/TestQuestBoardPolicyContract.ps1` proves `questBoardPolicies` parses, validates, writes structured policy facts, appears in patch explanation output, and expands into a content-backed candidate preview report through the validation plugin.
