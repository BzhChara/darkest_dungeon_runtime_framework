# Map Layout Template Draft

This document defines the high-level fixed-map layout contract. The first implementation slice is a plugin-declared validation layer, not a runtime map generator yet.

The intended compile chain is:

```text
mapLayoutTemplates
  -> checked layout graph
  -> low-level mapTemplates spec or future .dm writer
  -> generated .dm artifact under modStateDirectory
  -> normal sourcePath virtual file overlay
```

The rule is the same as the rest of the framework: plugin data names the concrete map, rooms, encounters, and story choices; framework code provides reusable map primitives and validation.

## Relationship To Existing mapTemplates

`mapTemplates` currently mutate scalar fields that already exist in a source `.dm` template. They are useful for controlled edits such as:

- move a final-room marker to another existing room,
- move an existing room marker,
- retarget an existing room door,
- retarget or disable an existing corridor tile `door_to`,
- set initial dynamic tile content values.

`mapLayoutTemplates` should be a higher-level authoring layer over that path. The first implementation should compile only layouts that can be represented by the existing template's area, tile, and door slots. It should fail with diagnostics when a requested layout needs unsupported creation or deletion.

Current implementation status:

- plugin manifests can declare `mapLayoutTemplates`;
- the loader validates the source `.dm` and the declared graph;
- reports are written under `modStateDirectory/_map_layout_templates/<plugin-id>/`;
- reports include `compileReady=false`;
- no `.dm` artifact or virtual file overlay is generated from `mapLayoutTemplates` yet.

## Draft Manifest Shape

```json
{
  "mapLayoutTemplates": [
    {
      "id": "post_ancestor_straight_line_01",
      "target": "maps/DD_map4.dm",
      "source": "maps/DD_map4.dm",
      "layout": {
        "entrance": "start",
        "finalRoom": "boss",
        "rooms": [
          { "id": "start", "templateAreaId": "rooA", "position": [1, 2] },
          { "id": "choice", "templateAreaId": "rooB", "position": [12, 5] },
          { "id": "boss", "templateAreaId": "rooC", "position": [20, 2] }
        ],
        "corridors": [
          {
            "id": "main_path",
            "templateAreaId": "corA",
            "route": [
              [2, 2],
              [3, 2],
              [4, 2],
              [5, 2]
            ]
          }
        ],
        "links": [
          { "from": "start", "to": "main_path", "tile": 0 },
          { "from": "main_path", "to": "boss", "tile": 27 }
        ]
      },
      "tiles": [
        {
          "area": "main_path",
          "tile": 12,
          "content": "obstacle"
        },
        {
          "area": "boss",
          "tile": 0,
          "content": "battle",
          "encounter": "ancestor_echo"
        }
      ],
      "encounters": [
        {
          "id": "ancestor_echo",
          "mash": "author.post_ancestor.ancestor_echo"
        }
      ]
    }
  ]
}
```

Names such as `start`, `main_path`, and `ancestor_echo` are plugin-local ids. `templateAreaId` is the bridge back to the existing low-level `.dm` template until the framework can create new `.dm` areas safely.

## Implemented First Slice

The first implementation does not try to create arbitrary `.dm` containers. It currently:

1. Parse `mapLayoutTemplates` from plugin manifests.
2. Validate the declared graph:
   - entrance room exists,
   - final room exists when declared,
   - every link references declared nodes,
   - the entrance can reach the final room,
   - all linked template areas exist in the source map,
   - requested tile indexes exist in the template area,
   - no two active room markers share the same `position`.
3. Validate tile content declarations only as references:
   - tile area node exists,
   - tile index or `tileId` is in range,
   - named encounter references point at declared `encounters`.
4. Write a validation report with `compileReady=false`.

## Next Compiler Slice

The next slice should compile only supported changes into a low-level `mapTemplates` spec:

1. Convert validated layout intent into:
   - `finalRoomId`,
   - `staticTiles[].mapPosition`,
   - `staticDoors[]`,
   - `staticTileDoors[]`,
   - selected `dynamicTiles[]`.
2. Reuse the existing `mapTemplates` writer, report, and `sourcePath` overlay path.
3. Fail when the requested graph needs creation/deletion of areas, tiles, door slots, or mash definitions that do not yet have writers.

## Topology Validation

Map reports now expose `map.topology` facts. These facts should be used by future compilers and tests:

- `hasEntranceArea`
- `hasFinalRoom`
- `entranceCanReachFinal`
- `reachableAreaIds`
- `unreachableAreaIds`
- `areaDoorEdgeCount`
- `tileDoorEdgeCount`
- `invalidDoorTargetCount`
- `issues`

Unreachable areas are reported as facts, not automatically hard errors. This is deliberate: a template-derived map may leave unused original rooms disconnected while the active route remains valid. A compiler should decide whether unreachable areas are acceptable for that specific layout mode.

## Encounter Boundary

This document does not define monster lineups yet. `.dm` files appear to point at tile content and mash indexes/types, while actual monster compositions live in dungeon mash files. The reusable encounter layer should be separate:

```text
encounter.define_mash
map.place_named_encounter
```

The first map-layout compiler can accept encounter ids only as declared references and should fail if no implemented encounter writer can materialize them.

## Open Gaps

- Creating new `.dm` areas, tiles, and door slots.
- Resizing binary DSON containers safely.
- Deterministic mash definition writing.
- Full bidirectional door semantics beyond current connectivity checks.
- UI/map icon behavior for newly created map nodes.
- Live-game validation for more than the current DD4 scalar-retargeting path.
