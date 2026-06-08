# Research Save Samples

Source: `.research/DarkestDungeonSaveEditor-0.0.70/src/test/resources` plus local supplemental `.research/profile_*` directories and their `backup` directories.

Scanner: `tools/InspectResearchSaveSamples.ps1`.

Latest scan output: `logs/research_save_samples/saveeditor_samples_20260609_002528.json`.

## Scan Result

All JSON files in the SaveEditor v0.0.70 test resources and local supplemental profile samples were processed through the framework's current DSON inspector.

| Metric | Value |
| --- | ---: |
| Sample directories | 17 |
| JSON files | 153 |
| Files with access issues | 0 |
| Files with `dsonPartialDecoded` status | 153 |

Decoded scalar type totals from the latest scan:

| Type | Count |
| --- | ---: |
| `bool` | 23475 |
| `boolPair` | 90 |
| `embeddedDson` | 420 |
| `float32` | 5201 |
| `floatArray` | 4 |
| `int32` | 67428 |
| `intPair` | 68 |
| `intVector` | 1501 |
| `string` | 32289 |
| `stringVector` | 162 |
| `uint32` | 8662 |

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
| `profile_0` | 16 | Local supplemental town-state profile; adds later campaign progression, 74 estate trinkets, 12 completed plot quest records, 4 flashback completion records, 40 tracked curio results, and 25 nested roster heroes. |
| `profile_0_backup` | 19 | Local supplemental in-raid backup; adds Prophet boss raid state, active battle state, loading screen, dynamic map state, curio tracker, novelty tracker, and 25 nested roster heroes. |
| `profile_1` | 19 | Local supplemental in-raid profile; adds cove raid state, loading screen, map, curio tracker, novelty tracker, and 21 nested roster heroes. |
| `profile_1_backup` | 19 | Local supplemental backup profile; adds a second map/raid backup shape for cross-checking optional in-raid files and 21 nested roster heroes. |
| `profile1` | 18 | Broadest profile sample; includes raid, map, loading screen, curio tracker, and novelty tracker. |
| `profileReddit` | 16 | Full profile with non-empty estate trinket inventory, completed plot quest data, flashbacks, curio tracker, and novelty tracker. |
| `profileSwitch` | 15 | Switch-origin full profile plus novelty tracker. |
| `quirk_monster_class_ids` | 1 | Raid sample with `raid_finish_quirk_monster_class_ids`, background vectors, and `killRange` int pairs. |
| `skillCooldownValues` | 1 | Raid sample with non-empty monster skill cooldown key/value vectors. |
| `valid_additional_mash_entry_indexes` | 1 | Raid sample with non-empty `mash.valid_additional_mash_entry_indexes`. |

## Parser Coverage Added From These Samples

- Optional save files are now inspected only when present: `novelty_tracker.json`, `persist.curio_tracker.json`, `persist.raid.json`, `persist.map.json`, `persist.loading_screen.json`, `persist.mp_progression.json`, `persist.roster.network.json`, `novelty_tracker_mp.json`, and `backer_heroes.json`.
- Local `.research/profile_*` directories and their `backup` directories are now included by the default research scan without scanning unrelated research source directories.
- `float32` hardcoded paths now match SaveEditor's type table for actor buff amounts, campaign log percent fields, non-rolled additional chance values, raid torchlight/time fields, and raid stat entry values/timestamps.
- `floatArray` is decoded for map bounds paths such as `base_root.map.bounds`.
- `intPair` is decoded for raid `killRange`.
- `boolPair` is decoded for profile option values that are stored as two aligned 32-bit bool values.
- `embeddedDson` is decoded for SaveEditor `TYPE_FILE` fields such as roster `hero_file_data.raw_data` and map `static_dynamic.static_save`.
- Non-ASCII single-byte bool values are decoded as bool, matching SaveEditor's rule that non-zero single-byte values are true.
- DSON wildcard path matching now tolerates object ids containing dots, such as `Dr. Pants`, so `backer_heroes.*.combat_skills` still resolves.
- `facts.raid`, `facts.curioTracker`, `facts.loadingScreen`, and `facts.noveltyTracker` now promote stable optional-file state when those files are present.
- `facts.raid.battle` now promotes active battle state when present, including round/surprise/retreat/stall counters, enemies, monster HP/status, cooldown vectors, and hero/monster initiative entries.
- `facts.map` now promotes dynamic map state from `base_root.map.static_dynamic.areas`, including populated state, entrance/final room resolution, dynamic area count, dynamic tile count, and sampled tile content/light/trap/curio/mash fields.

## Deliberately Deferred

- `base_root.darkest_dungeon_trinket_unlocks` remains deferred. Current live samples, local supplemental samples, and the SaveEditor v0.0.70 research samples keep this container empty, so there is no trustworthy non-empty schema to promote yet.

## Remaining Raw Fields

The latest scan leaves 0 raw scalar entries across all 153 JSON files.

## Remaining Semantic Backlog

| Pattern | Count | Status |
| --- | ---: | --- |
| `base_root.heroes.[].hero_file_data.raw_data` | 416 | Parsed as `embeddedDson`; campaign roster entries also promote to `facts.heroes`. `persist.roster.network.json` remains optional network scope. |
| `base_root.map.static_dynamic.static_save` | 4 | Parsed as `embeddedDson`; `profile_0_backup` contributes a 66,005-byte Prophet static map with 414 objects, 2,428 fields, and 2,014 parsed scalar fields. `facts.map` promotes map bounds, room/corridor counts, area-level door connections, tile count, area bounds, tile coordinates, static tile metadata, and dynamic area/tile state. |
| `base_root.darkest_dungeon_trinket_unlocks` | 0 non-empty samples | Deferred until a non-empty sample exists. |
