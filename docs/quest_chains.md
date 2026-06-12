# Quest Chain Schema Draft

`questChains` is the generic content schema for ordered quest or chapter flows. It is intentionally not boss-specific. The same shape should describe a boss gauntlet, a post-Ancestor expansion chapter, or any fixed set of custom stages.

The current implementation is a validation and reporting slice, with an opt-in managed quest-board artifact:

```text
questChains
  -> validate stage order and unlock metadata
  -> validate references to mapLayoutTemplates or mapTemplates
  -> write modStateDirectory/_quest_chains/<plugin-id>/*.validation.json
  -> when questBoard.enabled=true, write a deterministic questBoard.replaceWithFixedSet artifact
```

It does not directly modify the quest board, campaign progression, or original saves. The quest-board artifact is observe-first: it is written under sidecar state and can be inspected or dry-run by the managed-action applier. Original-save writes still require explicit `--write-managed-actions`.

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

## Validation Rules

- Chain `id` is required.
- `stages` must contain at least one stage.
- Stage `id` values must be present and unique within the chain.
- Stage `order` defaults to array index; explicit duplicate orders are errors.
- Stage `sourceQuestId` is currently required because the first runtime content path still copies or overlays existing plot quest data.
- A stage may reference either `mapLayoutTemplateId` or `mapTemplateId`, not both.
- Map references must point at templates declared in the same plugin manifest.
- `unlock.type="afterQuest"` requires `unlock.questId`.
- `questBoard.enabled=true` currently supports only `mode="replaceWithFixedSet"` and `questIdSource="sourceQuestId"`.
- `questBoard.removeCompleted=true` requires `questBoard.completedStateKey`.
- When quest-board materialization is enabled, duplicate effective quest ids are errors because the current decoded-save writer cannot represent two distinct board entries with the same source quest id.

## Managed Quest-Board Artifact

When `questBoard.enabled=true`, the loader writes two sidecar files:

- `_quest_chains/<plugin-id>/<chain>.managed.quest_board.json`: materialization report.
- `_managed_actions/static_<plugin-id>_<chain>_questBoard.replaceWithFixedSet.json`: deterministic managed action artifact.

The artifact uses the existing `questBoard.replaceWithFixedSet` action shape, so the existing managed-action applier can dry-run it against a project-local decoded `persist.quest.json` copy. It intentionally uses `sourceQuestId` as the concrete quest id because the current writer resolves only existing plot quest definitions. `targetQuestId` remains metadata for future custom quest writers.

Regression coverage:

- `tools/TestQuestChainBoardArtifact.ps1` proves `questChains -> questBoard.replaceWithFixedSet artifact -> decoded persist.quest.json` can dry-run, write, and repeat idempotently without accumulating duplicate artifacts.

## Relationship To Map Layouts

`mapLayoutTemplates` owns dungeon topology and per-tile content. `questChains` owns stage order and stage-to-map binding. The split matters: a mod can reuse the same map layout in multiple chains, or use one chain with some original maps and some generated map-layout overlays.

## Current Limits

- Quest-board output is opt-in and currently materializes only fixed-set board entries from `sourceQuestId`.
- No custom quest object writer exists yet; `sourceQuestId` still anchors a stage to original quest content.
- No encounter mash writer exists yet, so stage-specific monster lineups are still represented by future `encounter.*` primitives.
- Cross-plugin map template references are not supported in the first slice; keep chain and referenced map templates in the same plugin.
