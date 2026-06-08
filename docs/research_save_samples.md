# Research Save Samples

Source: `.research/DarkestDungeonSaveEditor-0.0.70/src/test/resources`.

Scanner: `tools/InspectResearchSaveSamples.ps1`.

Latest scan output: `logs/research_save_samples/saveeditor_samples_20260608_230958.json`.

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
| `bool` | 14092 |
| `boolPair` | 50 |
| `float32` | 285 |
| `floatArray` | 1 |
| `int32` | 50789 |
| `intPair` | 68 |
| `intVector` | 1378 |
| `raw` | 331 |
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
- DSON wildcard path matching now tolerates object ids containing dots, such as `Dr. Pants`, so `backer_heroes.*.combat_skills` still resolves.

## Deliberately Deferred

- `base_root.darkest_dungeon_trinket_unlocks` remains deferred. Current live samples and the SaveEditor v0.0.70 research samples keep this container empty, so there is no trustworthy non-empty schema to promote yet.

## Remaining Raw Fields

The latest scan leaves 331 raw scalar entries:

| Pattern | Count | Status |
| --- | ---: | --- |
| `base_root.heroes.[].hero_file_data.raw_data` | 211 | Expected at the top-level roster file. State reports decode these nested DSON blobs into `facts.heroes` for `persist.roster.json`; `persist.roster.network.json` remains optional network scope. |
| `base_root.quests.[].use_default_progression_goals` | 6 | Observed only in `profileReddit` as single byte `0xAC`; keep raw until another sample or SaveEditor behavior explains it. |
| `base_root.map.static_dynamic.static_save` | 1 | Large embedded map/static dynamic payload. It starts with a DSON-like nested header but needs separate map-specific decoding before promotion. |
