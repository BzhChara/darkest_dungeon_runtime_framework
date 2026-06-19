# Domain Map Template

Use this template before designing features in a broad Darkest Dungeon domain such as trinkets, heroes, buildings, quest board, loot, maps, town events, or roster state.

The goal is to scan the domain first, then design individual capabilities from evidence.

## Domain

- Domain name:
- User-facing gameplay area:
- Related framework docs:
- Related validation scenarios:

## Scan Scope

| Source | Path or system | Included | Notes |
| --- | --- | --- | --- |
| Base game content |  | no |  |
| Official DLC content |  | no |  |
| Declared Workshop content |  | no |  |
| Plugin-bundled content |  | no |  |
| Decoded save files |  | no |  |
| Runtime logs or probes |  | no |  |

## Field Inventory

| Field | Example values | Count | Files | Category | Status |
| --- | --- | ---: | --- | --- | --- |
|  |  |  |  | unknown | unknown |

Category values:

- definition field
- economy field
- drop field
- UI field
- restriction field
- save field
- runtime field
- unknown field

Status values:

- verified by live test
- inferred from original files
- needs live validation
- unknown

## Relationship Map

| From | Relationship | To | Evidence | Status |
| --- | --- | --- | --- | --- |
|  | references |  |  | unknown |

Examples:

- rarity -> Nomad Wagon generation
- item id -> quest reward
- item id -> save inventory
- building id -> town building save state
- quest id -> quest board state

## Behavior Matrix

| Behavior | Original mechanism | Framework primitive today | Gap | Validation status |
| --- | --- | --- | --- | --- |
|  |  |  |  | unknown |

## Existing Framework Coverage

- Existing content index support:
- Existing managed actions:
- Existing save facts:
- Existing event bridge support:
- Existing overlay support:
- Existing live observation:
- Existing tests:

## Design Implications

- Behaviors that are configuration-only:
- Behaviors that need a declarative wrapper:
- Behaviors that need a missing reusable primitive:
- Behaviors that might need a hook later:
- Behaviors that should stay external authored content:

## Generated Artifacts

- Machine report path:
- Human research note path:
- Commands used:
- Date:

## Follow-Up

- Safe next feature:
- Required live validation:
- Unknowns:
- Do not implement yet:
