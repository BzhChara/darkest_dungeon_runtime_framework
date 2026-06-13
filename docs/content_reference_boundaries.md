# Content Reference Boundaries

This project is a runtime framework and orchestration layer. It should not become a full replacement for Darkest Dungeon's normal mod authoring pipeline or Steam Workshop content packaging.

The default design rule is:

- external content owns definitions, art, audio, localization, animation, and ordinary game data files;
- the framework owns references, dependency checks, composition, ordering, runtime state, and controlled projection into saves or virtual files.

## Content Provider Types

Framework plugins should be able to reference content from several providers:

| Provider | Meaning | Framework responsibility |
| --- | --- | --- |
| `base` | Original game content | discover ids and validate references |
| `dlc` | Installed official DLC content | discover ids only when the DLC content exists |
| `workshop` | A separately installed Workshop mod | validate expected ids and report missing dependencies |
| `plugin` | Files bundled inside the current framework plugin | resolve relative paths and expose them through existing virtual file/content overlay paths |
| `external` | User-supplied local content path | validate path only when explicitly configured |

The provider is metadata for validation and reporting. The primary stable link should still be the actual game content id or target path that the game consumes.

## Not Framework-Owned By Default

These capabilities should usually be treated as externally authored content. The framework may reference, validate, copy, or overlay them, but it does not need to generate them as a core feature.

| Domain | External content should own | Framework should own |
| --- | --- | --- |
| Monsters | monster class definitions, stats, resistances, rank rules, art ids | `monsterId` references, existence checks, dependency reports |
| Monster skills | skill effect definitions, animation ids, targeting data | `skillId` references and optional compatibility checks |
| Heroes/classes | class files, skill files, animations, icons | roster generation by class id, skill unlock state, availability filters |
| Trinkets/items | item definitions, icons, rarity, prices, equip rules | inventory counts, sale policy, reward references |
| Curios/props | prop definition files, art, default interaction tables | map placement, interaction gating, event hooks, dependency validation |
| Loot tables | ordinary loot table definitions | reward selection references, phase gating, deterministic reward policies |
| Localization | `.loc` or equivalent language files | loading order, missing-key diagnostics, optional text overrides |
| Art/audio/animation | images, atlases, skeletons, audio banks | file presence checks and virtual file/sourcePath exposure |
| Quest definitions | fully custom quest objects when authored through existing DD mod formats | quest board ordering, unlock state, phase transitions, save projection |

Generating these assets can be added later as helper tooling, but it is not required for the runtime framework to be useful.

## Framework-Owned Composition

The framework should provide first-class primitives for combining external content into gameplay:

| Primitive | Purpose |
| --- | --- |
| `contentRefs` | declare required and optional ids supplied by base game, DLC, Workshop, or plugin files |
| `encounters` | define exact monster lineups by referenced monster ids, rank positions, and selection conditions |
| `spawnPools` | add, replace, remove, or reweight encounter ids for a dungeon, difficulty, phase, or quest |
| `questChains` | define stage order, unlock conditions, quest-board behavior, and phase transitions |
| `mapLayoutTemplates` | define topology, room/corridor routes, tile content, and references to encounters or curios |
| `lootPolicies` | choose which existing loot tables or item ids are awarded by phase, stage, result, or rule |
| `resourcePolicies` | control starting resources, reward resources, consumption, and recovery rules |

This keeps creative content authoring separate from runtime logic. A monster from Workshop, a monster bundled in a plugin, and an original monster should all be usable by the same `encounters` schema.

## Example Shape

```json
{
  "id": "author.post_ancestor_expansion",
  "modules": {
    "contentRefs": ["content/refs.json"],
    "questChains": ["quests/post_ancestor.chain.json"],
    "mapLayouts": ["maps/*.layout.json"],
    "encounters": ["encounters/*.json"],
    "spawnPools": ["spawn_pools/*.json"]
  }
}
```

```json
{
  "monsters": [
    {
      "id": "workshop_beast_hunter",
      "provider": "workshop",
      "workshopId": "1234567890",
      "required": true
    },
    {
      "id": "bone_soldier",
      "provider": "base",
      "required": true
    }
  ]
}
```

```json
{
  "id": "author.stage_1_hunter_pack",
  "monsters": [
    { "monsterId": "workshop_beast_hunter", "rank": 4 },
    { "monsterId": "cultist_brawler", "rank": 3 },
    { "monsterId": "cultist_acolyte", "rank": 2 },
    { "monsterId": "bone_soldier", "rank": 1 }
  ]
}
```

The framework should validate that every referenced `monsterId` is present in the active content catalog. If `workshop_beast_hunter` is missing and marked `required`, the affected module or plugin should be reported as blocked instead of silently substituting another monster.

## Missing Content Policy

Reference validation should follow the plugin-loading philosophy:

- missing required content blocks only the affected module or plugin when possible;
- missing optional content produces a warning and leaves a deterministic reduced result;
- duplicate ids are reported with provider and load-order context;
- references should never silently fall back to unrelated original content;
- generated reports should show which provider satisfied each reference.

## When To Build A Writer

A framework writer is justified only when it adds reusable runtime value that external content packs do not already cover well:

- save projection or sidecar state is required;
- content must be generated from rules, phase state, or player choices;
- ordering, dependency, or compatibility resolution needs a structured report;
- a runtime hook must enforce behavior that static files cannot express.

If the task is only "create a new monster file with art and skills", prefer documenting how to reference an existing DD/Workshop-style content pack instead of adding framework code.
