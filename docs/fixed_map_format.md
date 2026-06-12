# Fixed Map Format Notes

This document records the current evidence for original fixed plot maps. It is a research note for future `map.*` and `encounter.*` capabilities, not a promise that map writing is stable yet.

## Source Files

Plot quests can point to fixed maps through `quest.map_name` in `campaign/quest/quest.plot_quests.json`.

Original fixed map files live under:

```text
E:\Steam\steamapps\common\DarkestDungeon\maps\*.dm
```

Verified original maps:

| Map | Areas | Rooms | Corridors | Tiles | Notes |
| --- | ---: | ---: | ---: | ---: | --- |
| `crow_map1.dm` | 1 | 1 | 0 | 1 | single-room encounter map |
| `DD_map1.dm` | 33 | 15 | 18 | 123 | Darkest Dungeon quest 1 |
| `DD_map2.dm` | 40 | 18 | 22 | 159 | Darkest Dungeon quest 2 |
| `DD_map3.dm` | 63 | 31 | 32 | 231 | Darkest Dungeon quest 3 |
| `DD_map4.dm` | 4 | 3 | 1 | 31 | Darkest Dungeon finale |
| `town_invasion_0.dm` | 15 | 8 | 7 | 56 | town invasion map |
| `tutorial_crypts.dm` | 16 | 8 | 8 | 56 | tutorial map |

All listed maps currently parse as DSON containers with zero inspection issues through the launcher map-file inspector.

## Inspector

The launcher can now inspect a single fixed map without starting the game:

```powershell
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/default_config.json --inspect-map-file "E:\Steam\steamapps\common\DarkestDungeon\maps\DD_map4.dm" --no-inject
```

By default, reports are written under:

```text
logs/map_file_reports/
```

Use `--map-report-output <path>` to place the report at a project-local path.

Regression check:

```powershell
.\tools\TestMapFileInspector.ps1
```

## Prototype Mutation

The launcher can also create a project-local prototype copy that changes the top-level final room id in place:

```powershell
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/default_config.json --prototype-map-final-room "E:\Steam\steamapps\common\DarkestDungeon\maps\DD_map4.dm" --map-final-room-id rooC --no-inject
```

This is intentionally narrow:

- it copies the source map to `logs/map_prototypes/` by default,
- it writes only `base_root.map.final_room_id`,
- the field must be an existing int32 scalar,
- the target area id must already exist in the source map and must be inferred as a room,
- the output map is immediately parsed again and the mutation report fails if the final room does not match.

Use `--map-prototype-output <path>` and `--map-prototype-report-output <path>` to choose project-local output paths.

## Template Mutation Prototype

The launcher also has a stricter template-driven prototype writer. It copies an existing `.dm` file, applies a small JSON spec to already-decoded scalar fields, writes a project-local `.dm`, and immediately parses the output again.

```powershell
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/default_config.json --prototype-map-template "E:\Steam\steamapps\common\DarkestDungeon\maps\DD_map4.dm" --map-template-spec ".\logs\map_templates\dd4_spec.json" --map-prototype-output ".\logs\map_templates\DD_map4_template.dm" --map-prototype-report-output ".\logs\map_templates\DD_map4_template.report.json" --no-inject
```

First supported spec shape:

```json
{
  "version": 1,
  "name": "dd4_rooC_dynamic_tile_probe",
  "entranceAreaId": "rooA",
  "finalRoomId": "rooC",
  "dynamicTiles": [
    {
      "areaId": "rooC",
      "tileId": "tile0",
      "content": 8,
      "knowledge": 1,
      "critScout": true
    }
  ],
  "staticTiles": [
    {
      "areaId": "rooC",
      "tileId": "tile0",
      "mapPosition": [20, 2]
    }
  ],
  "staticDoors": [
    {
      "areaId": "rooB",
      "doorSlot": "door4",
      "disabled": true
    }
  ],
  "staticTileDoors": [
    {
      "areaId": "corA",
      "tileId": "tile17",
      "disabled": true
    },
    {
      "areaId": "corA",
      "tileId": "tile27",
      "targetAreaId": "rooC",
      "targetTileIndex": 0,
      "doorType": 2,
      "implied": true
    }
  ]
}
```

Supported dynamic tile fields are `content`, `light`, `knowledge`, `mashIndex`, `mashType`, `curioPropHash`, `trapHash`, and `critScout`.

Supported static topology scalar fields:

- `staticTiles[]` mutates `base_root.areas.<areaId>.tiles.<tileId>.*`.
- `staticDoors[]` mutates `base_root.areas.<areaId>.<doorSlot>.*`.
- `staticTileDoors[]` mutates `base_root.areas.<areaId>.tiles.<tileId>.door_to.*`.
- `staticTiles[]` supports `mapPosition` and `sidePosition`; these are fixed-width float arrays and the requested value count must match the existing field.
- Door entries support `targetAreaId`, `targetTileIndex` or `targetTileId`, `doorType`, and `implied`.
- Door entries also support `disabled: true`, which rewrites the existing door to the original empty-door pattern: `area_to=hash("none")`, `tile_to=0`, `type=0`, and `implied=true`.

This writer is intentionally strict:

- it only mutates scalar fields that already exist in the template,
- `entranceAreaId` and `finalRoomId` must resolve to existing room areas,
- `staticDoors` and `staticTileDoors` cannot create missing door slots or missing tile `door_to` objects,
- `disabled: true` cannot be combined with target fields for the same door entry,
- field type mismatches fail the run,
- output is parsed again and every mutation is validated before success is reported.

## Plugin Map Templates

Enabled plugins can declare the same fixed-map template work directly in `patches.json`:

```json
{
  "id": "author.fixed_map_example",
  "enabled": true,
  "capabilities": ["map.template.fixed"],
  "mapTemplates": [
    {
      "id": "dd4_custom_finale",
      "target": "maps/DD_map4.dm",
      "source": "maps/DD_map4.dm",
      "specPath": "maps/dd4_custom_finale.spec.json"
    }
  ]
}
```

At patch-plan build time the launcher:

- resolves `target` as the game path that will be virtually overlaid,
- resolves relative `source` against the game working directory first, then the declaring plugin directory when the game-relative file does not exist; omitted `source` defaults to `target`,
- resolves `specPath` relative to the declaring plugin directory,
- writes generated artifacts under `modStateDirectory/_map_templates/<plugin-id>/`,
- immediately parses and validates the generated `.dm`,
- appends a normal `sourcePath` virtual file rule that serves the generated artifact for `target`.

`mapTemplates[].spec` can be used instead of `specPath` for inline JSON. `specPath` and `spec` are mutually exclusive. `mapTemplates[].when` uses the same condition shape as `virtualFileRules[].when`.

## Topology Validation

Map inspection and template mutation reports include a `map.topology` or `outputMap.topology` object. It is a read-only diagnostic layer over decoded static map facts.

Current topology facts:

- `hasEntranceArea`: the decoded entrance hash resolves to a known area.
- `hasFinalRoom`: the decoded final-room hash resolves to a known area. A final-room hash of `0` is treated as "no final room".
- `entranceCanReachFinal`: the entrance and final room are connected by the decoded area-door and tile-door graph.
- `reachableAreaIds`: areas connected to the entrance through decoded doors.
- `unreachableAreaIds`: decoded areas not connected to the entrance. These are facts, not automatic hard errors, because template-derived maps may intentionally leave unused source-template rooms disconnected.
- `areaDoorEdgeCount`: active room/area door edges.
- `tileDoorEdgeCount`: active tile `door_to` edges.
- `invalidDoorTargetCount`: doors that point outside decoded target areas or target tile ranges.
- `issues`: hard topology problems such as unresolved entrance/final hashes, invalid door targets, or a declared final room that is not reachable from the entrance.

This validation is intentionally generic. It does not know about a specific mod's desired route; it only reports whether the decoded topology is structurally coherent enough for a compiler or plugin rule to make a policy decision.

## Static Topology

The `.dm` files are binary DSON-like map files, not JSON text. They expose the same broad structure as `persist.map.json`:

```text
base_root.version
base_root.map.bounds
base_root.map.populated
base_root.map.entrance_id
base_root.map.final_room_id
base_root.map.static_dynamic.static_save
base_root.map.static_dynamic.areas
```

The nested `static_save` payload defines the fixed topology:

```text
base_root.areas.<areaId>.id
base_root.areas.<areaId>.kind
base_root.areas.<areaId>.name
base_root.areas.<areaId>.bounds
base_root.areas.<areaId>.door0..door7.area_to
base_root.areas.<areaId>.door0..door7.tile_to
base_root.areas.<areaId>.door0..door7.type
base_root.areas.<areaId>.door0..door7.implied
base_root.areas.<areaId>.tiles.<tileId>.mappos
base_root.areas.<areaId>.tiles.<tileId>.sidepos
base_root.areas.<areaId>.tiles.<tileId>.type
base_root.areas.<areaId>.tiles.<tileId>.obstacle
base_root.areas.<areaId>.tiles.<tileId>.door_to.*
```

Area ids use the same practical convention seen in generated map saves:

```text
roo*  room
cor*  corridor
```

Room-to-corridor connections are recoverable through area door slots. For example, `DD_map4.dm` has entrance `rooA`, final room `rooB`, one corridor `corA`, and room doors that target specific corridor tile indexes.

This is enough evidence to model a future `map.define_fixed_layout` primitive as room/corridor graph data: areas, tiles, connections, entrance, final room, and per-tile coordinates.

## Dynamic Tile State

The top-level `base_root.map.static_dynamic.areas` tree carries initial dynamic tile state:

```text
light
content
curio_prop
knowledge
trap
mash_index
mash_type
crit_scout
```

Across original fixed maps, non-empty patterns include:

```text
content=1,  mash_type=3, mash_index=0
content=6,  mash_type=3, mash_index=0
content=7,  mash_type=5, mash_index=-1
content=8,  mash_type=5, mash_index=-1
content=9,  mash_type=5, mash_index=-1
content=13, mash_type=5, mash_index=-1
```

The exact gameplay meaning of every numeric `content` value still needs confirmation. The current strong inference is that values paired with `mash_type=3` and `mash_index=0` represent battle content. Values without mash data likely represent non-battle tile content such as obstacle, trap, curio, quest interaction, or special map markers.

## Encounter Binding

Enemy composition is not embedded as monster ids inside `.dm`. Fixed maps point at map content and mash indexes/types. The monster lineups are defined in dungeon mash files such as:

```text
dungeons/darkestdungeon/darkestdungeon.6.mash.darkest
```

Named entries such as `dd_quest_1_mash_01` and `dd_quest_2_miniboss_1` show the intended route for deterministic fights:

```text
encounter.define_mash
map.place_named_encounter
```

## Current Conclusion

The framework can now read original fixed map topology well enough to support a data model for straight-line or winding custom maps. It also has a narrow project-local in-place mutation prototype for one top-level int32 field, plus a virtual-file `sourcePath` overlay path that can serve generated or copied `.dm` bytes for an original game map path.

Live validation on 2026-06-13 proved the runtime overlay reaches the actual game map screen. The test launched the game with `maps/DD_map4.dm` virtually backed by a project-local copy of `DD_map1.dm`; RuntimeHook logged `mode=sourcePath` with `sourceBytes=125348` and `virtualBytes=125348`, and the user visually confirmed that the DD4 map screen became the larger DD1-style topology instead of the original 4-area finale map.

Further live validation on 2026-06-13 proved selected scalar topology rewrites in game:

- Rewriting `finalRoomId`, room/corridor door targets, and `staticTiles[].mapPosition` can move the DD4 final room marker and its usable entrance to the far-right room.
- Moving the marker alone is not enough; the game still honors old corridor tile `door_to` entries as invisible W-key room-entry hotspots.
- Rewriting `corA.tile17.door_to` with `disabled: true` removed the hidden middle-room entry hotspot and stopped the crash, while `corA.tile27 -> rooC` remained usable.

This proves whole-file `.dm` replacement works in game. The template mutation writer now proves safe in-place mutation for selected existing scalar fields, including room door and corridor tile `door_to` topology fields. Plugin `mapTemplates` now turn those mutations into startup-generated artifacts and ordinary `sourcePath` overlays. Map reports now include generic topology validation for entrance/final reachability, unreachable source-template areas, and invalid door targets. Plugin `mapLayoutTemplates` can now validate a high-level room/corridor graph against an existing source `.dm`, but it reports `compileReady=false` and does not yet generate `.dm` bytes. It still does not prove safe arbitrary map generation from a high-level layout; creating/removing areas, tiles, and door slots remains a future map-system milestone.

The virtual overlay syntax is intentionally whole-file and binary-safe:

```json
{
  "target": "maps/DD_map4.dm",
  "sourcePath": "./logs/map_prototypes/DD_map4_final_rooC.dm"
}
```

The source path is resolved under the framework project root and currently cannot be mixed with text replacements or line operations for the same target. The next implementation step is described in `docs/map_layout_templates.md`: compile a validated high-level layout description to supported `mapTemplates` mutations, then later grow into controlled creation/removal of rooms, corridors, tiles, and door slots.
