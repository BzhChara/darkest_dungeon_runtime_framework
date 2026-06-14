# Quest Chain Schema Draft

`questChains` is the generic content schema for ordered quest or chapter flows. It is intentionally not boss-specific. The same shape should describe a boss gauntlet, a post-Ancestor expansion chapter, or any fixed set of custom stages.

The current implementation is a validation and reporting slice, with two opt-in quest-board outputs:

```text
questChains
  -> validate stage order and unlock metadata
  -> validate references to mapLayoutTemplates or mapTemplates
  -> write modStateDirectory/_quest_chains/<plugin-id>/*.validation.json
  -> when questBoard.mode=replaceWithFixedSet, write a deterministic questBoard.replaceWithFixedSet artifact
  -> when questBoard.mode=linearProgression, generate a questBoardPolicies report from the ordered stages
  -> optional --preview-quest-board report shows the final fixed quest board before any write/live path
```

It does not directly modify the quest board, campaign progression, or original saves. The quest-board artifact is observe-first: it is written under sidecar state and can be inspected through `--preview-quest-board` or dry-run by the managed-action applier. Original-save writes still require explicit `--write-managed-actions`.

## Manifest Shape

```json
{
  "questChains": [
    {
      "id": "post_ancestor_probe_chain",
      "name": "Post Ancestor Probe Chain",
      "mode": "fixed_order",
      "unlock": {
        "type": "afterQuest",
        "questId": "plot_final_boss"
      },
      "questBoard": {
        "enabled": true,
        "mode": "replaceWithFixedSet",
        "questIdSource": "sourceQuestId",
        "removeCompleted": false
      },
      "stages": [
        {
          "id": "stage_01_layout_probe",
          "name": "Layout Probe",
          "order": 0,
          "sourceQuestId": "plot_dd_4",
          "targetQuestId": "probe_stage_01",
          "mapLayoutTemplateId": "dd4_high_level_layout_probe",
          "region": "darkestdungeon",
          "difficulty": 6,
          "tags": ["boss", "post_ancestor"]
        }
      ]
    }
  ]
}
```

A linear progression chain uses the same stage list but delegates task-board selection to `questBoardPolicies`:

```json
{
  "questChains": [
    {
      "id": "darkest_finale_chain",
      "mode": "linear_progression",
      "unlock": {
        "type": "stateEquals",
        "stateKey": "bossGauntlet.phase",
        "stateEquals": "darkest_finale"
      },
      "questBoard": {
        "enabled": true,
        "mode": "linearProgression",
        "questIdSource": "sourceQuestId",
        "refreshTriggers": ["immediateOnQuestComplete", "onWeekAdvance", "manual"],
        "onCompleted": "remove"
      },
      "stages": [
        { "id": "dd1", "order": 0, "sourceQuestId": "plot_darkest_dungeon_1" },
        { "id": "dd2", "order": 1, "sourceQuestId": "plot_darkest_dungeon_2" }
      ]
    }
  ]
}
```

The generated policy makes the first stage available when the unlock predicate matches and the stage quest is not completed. Each later stage requires every previous stage quest to be completed and hides itself after completion. This is a declaration wrapper over `questBoardPolicies`, not a separate task-board engine.

## Validation Rules

- Chain `id` is required.
- `stages` must contain at least one stage.
- Stage `id` values must be present and unique within the chain.
- Stage `order` defaults to array index; explicit duplicate orders are errors.
- Stage `sourceQuestId` is currently required because the first runtime content path still copies or overlays existing plot quest data.
- A stage may reference either `mapLayoutTemplateId` or `mapTemplateId`, not both.
- Map references must point at templates declared in the same plugin manifest.
- `unlock.type="afterQuest"` requires `unlock.questId`.
- `questBoard.enabled=true` currently supports `mode="replaceWithFixedSet"` for a static fixed board and `mode="linearProgression"` for generated task-board policy entries.
- `questBoard.questIdSource` currently supports only `sourceQuestId`.
- `questBoard.removeCompleted=true` requires `questBoard.completedStateKey` for `replaceWithFixedSet`; `linearProgression` pre-filters completed quests through generated policy entries instead.
- When quest-board materialization is enabled, duplicate effective quest ids are errors because the current decoded-save writer cannot represent two distinct board entries with the same source quest id.

## Managed Quest-Board Artifact

When `questBoard.enabled=true`, the loader writes two sidecar files:

- `_quest_chains/<plugin-id>/<chain>.managed.quest_board.json`: materialization report.
- `_managed_actions/static_<plugin-id>_<chain>_questBoard.replaceWithFixedSet.json`: deterministic managed action artifact.

The artifact uses the existing `questBoard.replaceWithFixedSet` action shape, so the startup overlay compiler can force active plot quests to early/repeatable availability and the managed-action applier can dry-run it against a project-local decoded `persist.quest.json` copy. It intentionally uses `sourceQuestId` as the concrete quest id because the current writer resolves only existing plot quest definitions. `targetQuestId` remains metadata for future custom quest writers.

`linearProgression` does not write this static artifact. Instead, the loader writes a generated policy report under `_quest_board_policies/<plugin-id>/`. The existing policy materializer can then produce a current one-stage `questBoard.replaceWithFixedSet` artifact from save facts and sidecar state. This keeps the chain shorthand small while reusing the same quest-board preview, profile refresh, realtime refresh, and content-overlay consumers.

`--preview-quest-board` reads materialized `questBoard.replaceWithFixedSet` artifacts, resolves their quest ids against enabled original plot quest content, applies `removeCompleted` filtering when sidecar state is available, and writes:

```text
logs/quest_board_preview_report.json
```

The report lists the final active board entries with stage metadata, content source path, dungeon, difficulty, length, and goal ids. If multiple quest-board artifacts exist, the preview follows the decoded-save applier's replace semantics: the last valid applicable artifact becomes the final board. Failed artifacts are reported as errors instead of silently falling back.

Before `--dry-run` or a real game launch, the loader also writes:

```text
logs/quest_board_launch_preflight_report.json
```

This preflight report links the quest-board preview to `logs/managed_action_overlay_manifest.json` and `logs/quest_board_runtime_overlay_report.json`. It explicitly separates candidate board state from live runtime behavior. When `saveWatchDirectories` points at one or more `profile_*` saves, the runtime overlay compiler builds virtual `profile_*/persist.quest.json` replacements from the fixed-board candidate. JSON fixtures are rewritten directly; binary DSON saves require `dsonSaveEditorJarPath` to point at DDSaveEditor so the framework can decode, patch, and re-encode the generated board. The same generated replacement can be explicitly written to a configured watched profile through `--refresh-quest-board-profile <profileId>`; that path creates a backup first and is meant to refresh the current task board, not to emulate the full original week settlement. With `questBoardAutoRefreshEnabled`, the save watcher also reapplies the generated board after the original game writes live `persist.quest.json`.

Regression coverage:

- `tools/TestQuestChainBoardArtifact.ps1` proves `questChains -> questBoard.replaceWithFixedSet artifact -> quest-board preview -> launch preflight -> runtime persist.quest.json overlay -> decoded persist.quest.json writer` can preview, preflight, dry-run, write, and repeat idempotently without accumulating duplicate artifacts. `tools/TestManagedActionOverlay.ps1` additionally proves fixed-board artifacts compile into plot quest content overlays, `tools/TestQuestBoardProfileRefresh.ps1` proves targeted profile refresh can dry-run, back up, write, and detect unchanged quest boards against a project-local watched profile, and `tools/TestQuestBoardRealtimeRefresh.ps1` proves a realtime `persist.quest.json` save change can be auto-refreshed back to the fixed board.

## Relationship To Map Layouts

`mapLayoutTemplates` owns dungeon topology and per-tile content. `questChains` owns stage order and stage-to-map binding. The split matters: a mod can reuse the same map layout in multiple chains, or use one chain with some original maps and some generated map-layout overlays.

## Current Limits

- Quest-board output is opt-in. Static `replaceWithFixedSet` materializes fixed-set board entries from `sourceQuestId`; `linearProgression` generates policy entries that must be resolved/materialized from runtime save facts and sidecar state.
- Live quest-board replacement currently has four paths: a content overlay that forces active plot quest definitions to early/repeatable availability, a virtual-save overlay for `profile_*/persist.quest.json` when readable watched profiles and DDSaveEditor are available, an explicit profile refresh command that writes the generated `persist.quest.json` replacement with backup and safety checks, and an opt-in realtime watcher refresh that reapplies the generated board after original live task-board saves. The explicit refresh is the preferred initialization path when the original game has already cached the current week's board; watcher refresh is for original week-transition board regeneration.
- No custom quest object writer exists yet; `sourceQuestId` still anchors a stage to original quest content.
- No encounter mash writer exists yet, so stage-specific monster lineups are still represented by future `encounter.*` primitives.
- Cross-plugin map template references are not supported in the first slice; keep chain and referenced map templates in the same plugin.
