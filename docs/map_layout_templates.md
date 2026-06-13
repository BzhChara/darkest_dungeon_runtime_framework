# Map Layout Template Draft

This document defines the high-level fixed-map layout contract. The implemented slice is a plugin-declared compiler for layouts that can be represented by existing `.dm` areas, tiles, and door slots.

Status: optional/experimental. This feature is a support tool for fixed-map overlays and topology diagnostics, not the default path for custom dungeon authoring. Use original DD content formats or Workshop/plugin content first when they can express the desired map, quest, region, encounter, art, or generated-layout behavior.

The intended compile chain is:

```text
mapLayoutTemplates
  -> checked layout graph
  -> low-level mapTemplates spec or future .dm writer
  -> generated .dm artifact under modStateDirectory
  -> normal sourcePath virtual file overlay
```

The rule is the same as the rest of the framework: plugin data names the concrete map, rooms, encounters, and story choices; framework code provides reusable map primitives and validation.

The practical priority order is:

1. Reference an existing base, DLC, Workshop, or plugin-bundled map or map generator.
2. Validate the referenced content and report missing ids, unreachable topology, or obvious incompatibilities.
3. Use `mapTemplates` or `mapLayoutTemplates` only for narrow fixed-map overlays that can be represented by an existing `.dm` template.
4. Defer arbitrary map creation or live map mutation until a concrete runtime feature requires it.

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
- supported layouts compile to a low-level `mapTemplates` spec;
- reports, compiled specs, generated `.dm` files, and low-level template reports are written under `modStateDirectory/_map_layout_templates/<plugin-id>/`;
- generated `.dm` files are appended as ordinary `sourcePath` virtual file overlays;
- reports include `compileReady=true` only when the artifact and overlay were generated.

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
          "content": 8,
          "knowledge": 1,
          "critScout": true
        }
      ],
      "encounters": []
    }
  ]
}
```

Names such as `start`, `main_path`, and `ancestor_echo` are plugin-local ids. `templateAreaId` is the bridge back to the existing low-level `.dm` template until the framework can create new `.dm` areas safely.

## Implemented Slice

The current implementation does not try to create arbitrary `.dm` containers. It currently:

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
4. Compile supported layout intent into a low-level `mapTemplates` spec:
   - `finalRoomId`,
   - `entranceAreaId`,
   - `staticTiles[].mapPosition`,
   - `staticDoors[]`,
   - `staticTileDoors[]`,
   - selected `dynamicTiles[]`.
5. Reuse the existing `mapTemplates` writer, report, and `sourcePath` overlay path.
6. Fail when the requested graph needs creation/deletion of areas, tiles, door slots, or mash definitions that do not yet have writers.

## Compiler Rules

- Links must currently connect one room node and one corridor node.
- The room side reuses an existing door slot from that room template.
- The corridor side uses the `tile` or `tileId` declared on the link.
- Existing source doors and corridor `door_to` entries that are not part of the declared active layout are disabled when they touch an active area. This avoids hidden room-entry hotspots from the source template.
- Room `position` maps to `tile0.mapPosition`.
- Corridor `route` maps sequentially to `tile0`, `tile1`, and so on. The route cannot be longer than the existing template corridor tile count.
- `tiles[].content` accepts a number or numeric string. Only `empty`/`none` are recognized symbolic aliases for now.
- `tiles[].encounter` is reference-checked but not materialized; using it currently fails compilation because no `encounter.defineMash` writer exists yet.

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

These gaps are intentionally deferred. They should not be treated as the next mainline tasks simply because they are listed here.

- Creating new `.dm` areas, tiles, and door slots.
- Resizing binary DSON containers safely.
- Deterministic mash definition writing.
- Full bidirectional door semantics beyond current connectivity checks.
- UI/map icon behavior for newly created map nodes.
- Live-game validation for more than the current DD4 scalar-retargeting path.
- Runtime map-state changes during an active raid, such as unlocking a path after a key enemy dies.

The last item belongs to a different future primitive, likely `mapState.*`, not to startup `.dm` template editing. It would need combat or raid observation, current map-state facts, a reversible patch model, and live UI/game-state refresh validation before it could be considered supported.
