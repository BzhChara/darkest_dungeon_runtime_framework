# Quest Chain Schema Draft

`questChains` is the generic content schema for ordered quest or chapter flows. It is intentionally not boss-specific. The same shape should describe a boss gauntlet, a post-Ancestor expansion chapter, or any fixed set of custom stages.

The current implementation is a validation and reporting slice:

```text
questChains
  -> validate stage order and unlock metadata
  -> validate references to mapLayoutTemplates or mapTemplates
  -> write modStateDirectory/_quest_chains/<plugin-id>/*.validation.json
```

It does not yet modify the quest board, campaign progression, or original saves. Runtime behavior should be added later through reusable event rules and managed actions.

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

## Relationship To Map Layouts

`mapLayoutTemplates` owns dungeon topology and per-tile content. `questChains` owns stage order and stage-to-map binding. The split matters: a mod can reuse the same map layout in multiple chains, or use one chain with some original maps and some generated map-layout overlays.

## Current Limits

- No quest board writer consumes `questChains` directly yet.
- No custom quest object writer exists yet; `sourceQuestId` still anchors a stage to original quest content.
- No encounter mash writer exists yet, so stage-specific monster lineups are still represented by future `encounter.*` primitives.
- Cross-plugin map template references are not supported in the first slice; keep chain and referenced map templates in the same plugin.
