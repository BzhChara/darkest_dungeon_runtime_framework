# Quest Board Policy Schema Draft

`questBoardPolicies` is the generic scheduling layer for deciding which referenced quests may appear on the task board. It is intentionally separate from static quest, dungeon, monster, map, art, and localization authoring. Those definitions should usually come from original game files, DLC, Workshop content, or plugin-bundled DD-format files and be declared through `contentRefs`.

The current implementation is validation, reporting, content-backed candidate preview, facts-driven candidate resolution, and explicit managed artifact materialization:

```text
questBoardPolicies
  -> validate policy mode, refresh triggers, entries, predicates, weights, and completion actions
  -> write modStateDirectory/_quest_board_policies/<plugin-id>/*.validation.json
  -> explain policy and entry facts through --explain-patches
  -> preview declared candidate quests through --preview-quest-board-policies
  -> write logs/quest_board_policy_preview_report.json
  -> resolve eligible candidate quests through --resolve-quest-board-policies
  -> write logs/quest_board_policy_resolve_report.json
  -> materialize selected quests through --materialize-quest-board-policies
  -> write logs/quest_board_policy_materialize_report.json
  -> write modStateDirectory/_managed_actions/*_questBoardPolicies_questBoard.replaceWithFixedSet.json
```

It does not directly modify `persist.quest.json`, simulate week settlement, or replace the existing `questBoard.replaceWithFixedSet` writer. Materialization is an explicit offline step that produces the same managed action artifact shape consumed by the existing quest-board preview, runtime overlay, decoded-save writer, and profile refresh paths. Later quest-board generators should consume these policy facts instead of hardcoding one mod's quest order.

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

## Candidate Resolution

Run:

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --resolve-quest-board-policies --save-state-report <path> --no-inject
```

The report is written to `logs/quest_board_policy_resolve_report.json`.

The resolver consumes the preview candidates plus optional runtime facts:

- `facts.campaignLog.totalWeeks` for `weekGte`, `weekLte`, and `weekEq`;
- string quest ids from `facts.completedQuestIds`, `facts.progression.completedQuestIds`, `facts.progression.completedPlotQuestDataIds`, successful `facts.progression.lastRaidQuest.names`, successful `facts.campaignLog.latestCompletedPartyRaidRecord.questId.names`, and successful `facts.campaignLog.partyRaidRecords[].questId.names`;
- matching plugin sidecar state from `modStateDirectory/*.json` for `availableWhen.phase`, `stateKey`, and `stateEquals`.

Each candidate becomes:

- `active`: fixed candidate matched all predicates;
- `eligiblePoolCandidate`: pooled/weighted candidate matched all predicates;
- `skipped`: predicate did not match, content was optional-missing, or `onCompleted` filtered an already-completed quest;
- `unevaluated`: required runtime facts were not available;
- `blocked`: required quest content was missing or invalid.

`resolvedQuestIds` is deterministic and includes all `active` and `eligiblePoolCandidate` quests in policy order. It is an eligibility report, not a final task-board artifact. Use materialization when a plugin needs a concrete board candidate set.

## Materialization

Run:

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --materialize-quest-board-policies --save-state-report <path> --quest-board-policy-slots <n> --quest-board-policy-seed <seed> --no-inject
```

`--quest-board-policy-slots` and `--quest-board-policy-seed` are optional. Without an explicit seed, the materializer uses the resolved campaign week, or `0` when no week fact exists.

The materializer:

- calls the resolver and writes the normal resolve report;
- selects fixed `active` entries in policy load order;
- draws one weighted entry per pool for `mixed` policies;
- treats `random` policies as weighted pools, drawing one from the whole policy when no explicit pool is declared;
- deduplicates quest ids by first winner instead of failing the whole board;
- applies the optional slot limit after policy order and pool draws;
- writes a `questBoard.replaceWithFixedSet` managed action artifact into `modStateDirectory/_managed_actions/`;
- writes `logs/quest_board_policy_materialize_report.json` with selected, skipped-duplicate, skipped-slot-limit, and not-drawn diagnostics.

The artifact pre-filters completed quests during policy resolution and sets `removeCompleted=false`. Existing fixed-board consumers can then read the artifact without learning about `questBoardPolicies`.

## Automatic Materialization

`questBoardPolicies` can be materialized automatically during the save-event bridge pass. Enable it in config:

```json
{
  "saveEventBridgeEnabled": true,
  "questBoardPolicyAutoMaterializeEnabled": true,
  "questBoardPolicyAutoMaterializeSlots": 4,
  "questBoardPolicyAutoMaterializeSeed": 42
}
```

For one-off diagnostics, the same behavior can be enabled from the CLI:

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --infer-save-events --auto-materialize-quest-board-policies --save-state-report <path> --quest-board-policy-slots <n> --quest-board-policy-seed <seed> --no-inject
```

The bridge writes the normal `logs/save_event_bridge_report.json` with a `questBoardPolicyMaterialization` section. When enabled and policies exist, it also writes the normal materialization report and managed action artifact.

When the save-state report has `activeProfile.profile`, automatic and explicit materialization copy it into artifact `profileScope`. A profile-scoped artifact is only consumed by quest-board preview/runtime refresh when the caller names the same target profile:

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --preview-quest-board --quest-board-profile-scope profile_3 --no-inject
```

Without a target profile, `--preview-quest-board` and startup launch preflight only consume global artifacts. This prevents a policy artifact produced from one save from silently replacing another profile's task board. Older artifacts without `profileScope` are treated as global for compatibility.

Automatic materialization does not itself mutate a live save. In realtime use, pair it with the existing fixed-board refresh path:

- `saveEventBridgeEnabled=true` watches save facts and runs fact-event rules;
- `questBoardPolicyAutoMaterializeEnabled=true` turns the latest facts into a fresh fixed-board artifact scoped to that active profile when available;
- `questBoardAutoRefreshEnabled=true` can then reapply the matching fixed-board artifact after the original game rewrites live `persist.quest.json`.

This keeps policy scheduling, artifact production, and live save refresh as separate diagnostics-backed steps.

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

- `tools/TestQuestBoardPolicyContract.ps1` proves `questBoardPolicies` parses, validates, writes structured policy facts, appears in patch explanation output, expands into a content-backed candidate preview report, resolves week/completed-quest gated candidates from save facts, materializes a fixed-board managed action artifact, auto-materializes profile-scoped artifacts from `--infer-save-events`, ignores mismatched profile artifacts by default, and feeds matching artifacts back through the existing quest-board preview consumer.
