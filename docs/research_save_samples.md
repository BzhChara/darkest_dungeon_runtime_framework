# Research Save Samples

Source: `.research/DarkestDungeonSaveEditor-0.0.70/src/test/resources`.

Scanner: `tools/InspectResearchSaveSamples.ps1`.

Latest scan output: `logs/research_save_samples/saveeditor_samples_20260608_233114.json`.

## Scan Result

All JSON files in the SaveEditor v0.0.70 test resources were processed through the framework's current DSON inspector.

| Metric | Value |
| --- | ---: |
| Sample directories | 13 |
| JSON files | 80 |
| Files with access issues | 0 |
| Files with `dsonPartialDecoded` status | 80 |

Decoded scalar type totals from the latest scan:

| Type | Count |
| --- | ---: |
| `bool` | 14098 |
| `boolPair` | 50 |
| `embeddedDson` | 325 |
| `float32` | 285 |
| `floatArray` | 1 |
| `int32` | 50789 |
| `intPair` | 68 |
| `intVector` | 1378 |
| `string` | 22178 |
| `stringVector` | 101 |
| `uint32` | 5429 |

## Sample Directory Coverage

| Sample | Files | Coverage note |
| --- | ---: | --- |
| `backerHeroes` | 1 | `backer_heroes.json`; backer hero name/class plus combat skill, camping skill, and quirk vectors. |
| `backgroundNames` | 1 | `persist.raid.json`; raid background string vectors, party vectors, cooldown vectors, and `killRange` int pairs. |
| `dead_hero_entries` | 1 | `persist.town_event.json`; non-empty `dead_hero_entries` and `result_event_history` int vectors. |
| `modlimit` | 15 | Full campaign-like profile with `novelty_tracker.json` and `persist.curio_tracker.json`. |
| `networkFiles` | 6 | Optional network/Butcher's Circus files, including MP progression, network roster, and MP novelty tracker. |
| `nonAsciiField` | 1 | `persist.roster.json`; validates roster DSON string handling with non-ASCII names in nested hero data. |
| `otherFiles` | 3 | Campaign log `quirk_group` string vectors plus raid cooldown/mash vectors. |
| `profile1` | 18 | Broadest profile sample; includes raid, map, loading screen, curio tracker, and novelty tracker. |
| `profileReddit` | 16 | Full profile with non-empty estate trinket inventory, completed plot quest data, flashbacks, curio tracker, and novelty tracker. |
| `profileSwitch` | 15 | Switch-origin full profile plus novelty tracker. |
| `quirk_monster_class_ids` | 1 | Raid sample with `raid_finish_quirk_monster_class_ids`, background vectors, and `killRange` int pairs. |
| `skillCooldownValues` | 1 | Raid sample with non-empty monster skill cooldown key/value vectors. |
| `valid_additional_mash_entry_indexes` | 1 | Raid sample with non-empty `mash.valid_additional_mash_entry_indexes`. |

## Parser Coverage Added From These Samples

- Optional save files are now inspected only when present: `novelty_tracker.json`, `persist.curio_tracker.json`, `persist.raid.json`, `persist.map.json`, `persist.loading_screen.json`, `persist.mp_progression.json`, `persist.roster.network.json`, `novelty_tracker_mp.json`, and `backer_heroes.json`.
- `float32` hardcoded paths now match SaveEditor's type table for actor buff amounts, campaign log percent fields, and non-rolled additional chance values.
- `floatArray` is decoded for map bounds paths such as `base_root.map.bounds`.
- `intPair` is decoded for raid `killRange`.
- `boolPair` is decoded for profile option values that are stored as two aligned 32-bit bool values.
- `embeddedDson` is decoded for SaveEditor `TYPE_FILE` fields such as roster `hero_file_data.raw_data` and map `static_dynamic.static_save`.
- Non-ASCII single-byte bool values are decoded as bool, matching SaveEditor's rule that non-zero single-byte values are true.
- DSON wildcard path matching now tolerates object ids containing dots, such as `Dr. Pants`, so `backer_heroes.*.combat_skills` still resolves.

## Deliberately Deferred

- `base_root.darkest_dungeon_trinket_unlocks` remains deferred. Current live samples and the SaveEditor v0.0.70 research samples keep this container empty, so there is no trustworthy non-empty schema to promote yet.

## Remaining Raw Fields

The latest scan leaves 0 raw scalar entries across all 80 JSON files.

## Remaining Semantic Backlog

| Pattern | Count | Status |
| --- | ---: | --- |
| `base_root.heroes.[].hero_file_data.raw_data` | 324 | Parsed as `embeddedDson`; campaign roster entries also promote to `facts.heroes`. `persist.roster.network.json` remains optional network scope. |
| `base_root.map.static_dynamic.static_save` | 1 | Parsed as `embeddedDson`: 45,249 bytes, 292 objects, 1,669 fields, 1,377 parsed scalar fields, root children `areas` and `ext_data`. `facts.map` now promotes map bounds, area count, tile count, area bounds, and tile coordinate samples. |
| `base_root.darkest_dungeon_trinket_unlocks` | 0 non-empty samples | Deferred until a non-empty sample exists. |
