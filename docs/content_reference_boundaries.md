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
| Map generators | `.map_generator.darkest` rules for ordinary generated quest layouts | choose which generator-backed quest is eligible and validate referenced dungeon/type ids |
| Fixed maps | authored `.dm` maps and their art/content references | select, gate, overlay, or validate map references |
| Dungeon/region packs | dungeon folders, walls, rooms, colour grades, quest-select art, raid settings | content catalog indexing, dependency checks, quest-board scheduling |
| Town/building static data | building layout JSON, static slots, static costs, static recruit tables | runtime queues, delayed completion, save projection, capability-gated overrides |
| Gameplay scalar rules | walking speed, combat speed, stack size, static balance values | load-order reports, conflict diagnostics, optional virtual file overlays |
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

## Workshop Sampling Notes

The current Workshop install contains enough variety to set a practical boundary. The sampled tags include `New Class`, `Skins`, `Trinkets`, `Gameplay Tweaks`, `Localization`, `Dungeon`, `Monsters`, `Boss`, `Miniboss`, `UI`, `font`, and `Compatibility`.

These samples support the same rule: most static authoring is already covered by the original Darkest Dungeon mod file formats. The framework should catalog and orchestrate that content instead of replacing it.

| Sample | Observed authoring path | Framework boundary |
| --- | --- | --- |
| `1689234891` Sunward Isles | new dungeon `ship`, quest generation, plot quests, quest types, fixed `.dm` maps, map generator, mash files, curios, inventory, districts, town events, localization | reference its dungeon/quest/monster/trinket ids; gate and schedule quests; validate missing dependencies |
| `3669966489` Fiend Festival | new dungeon with `sinister_circus.quest.*.json`, `sinister_circus.map_generator.darkest`, inventory, raid settings, monsters, loot, trinkets | treat as an external content provider; do not build a separate dungeon editor first |
| `3081931947` 2B Class Mod | hero info/art, effects, upgrades, loot, trinkets, buffs, traits, localization | generate or filter roster entries by class id; do not reimplement class authoring |
| `2433996706` Here Be Monsters | monster info/art, monster AI, mash pools, effects, loot, trinkets, localization | reference monster and encounter ids; validate that required mash and monster files exist |
| skin/image replacement mods | replacement hero/town images and art manifests | report file-level conflicts and final provider; no skin system is required |
| trinket packs | trinket entries, rarities, sets, effects, buffs, icons, localization | reference trinket ids in inventory, rewards, and sale policies |
| inventory/stack mods | `inventory/*.darkest` static value changes | virtualize or report conflicts; runtime inventory logic is separate |
| speed tweak mods | `shared/*.rules.json` scalar changes | static content overlay and conflict reporting are enough |
| town building tweak mods | building JSON, layout files, upgrade JSON | static building edits stay external; dynamic construction queues or delayed upgrades belong to framework actions |
| localization/font mods | localization XML/LOC and font assets | load order, missing key checks, and provider reports only |
| music/compatibility mods | raid settings, audio banks, quest generation overrides | report which content they patch; allow explicit load ordering |

## Consequence For Quest Board Work

Quest-board control should not require the framework to author every quest, monster, dungeon, or map. A plugin should be able to point at an original, DLC, Workshop, or plugin-bundled quest and then describe when it is eligible.

The reusable primitive should be a quest-board policy over referenced content:

- immediate availability after a prerequisite quest completes;
- availability on the next week advance after a prerequisite completes;
- `week >= N` availability;
- exact-week windows such as `week == N`;
- fixed entries, random draws from eligible pools, or a mixture of both;
- completion handling such as keep, remove, replace, or advance phase.

The framework-owned part is the predicate, scheduling, and projection path. The content-owned part is the underlying quest, dungeon, map, encounter, and assets.

Example shape:

```json
{
  "contentRefs": {
    "workshop": [
      { "workshopId": "1689234891", "required": true }
    ],
    "quests": [
      { "id": "plot_samurai", "provider": "workshop", "required": true }
    ],
    "dungeons": [
      { "id": "ship", "provider": "workshop", "required": true }
    ],
    "monsters": [
      { "id": "ghost_samurai_A", "provider": "workshop", "required": true }
    ]
  },
  "questBoardPolicy": {
    "mode": "mixed",
    "refreshTriggers": ["immediateOnQuestComplete", "onWeekAdvance"],
    "entries": [
      {
        "questId": "plot_samurai",
        "availableWhen": {
          "completedQuest": "plot_lions",
          "weekGte": 5
        },
        "onCompleted": "remove"
      }
    ]
  }
}
```

The exact schema can evolve, but the boundary should stay stable: static content packs provide ids and files; the framework decides if, when, and how those ids participate in runtime gameplay.
