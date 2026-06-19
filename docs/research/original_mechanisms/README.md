# Original Mechanism Research

This directory records evidence about original Darkest Dungeon mechanics that should be checked before adding framework hooks, custom overlays, or new save writers.

Each note should preserve the difference between proven behavior, file-based inference, and unknown runtime behavior. A concrete gameplay request can motivate the research, but the conclusion should be written as a reusable mechanism.

## Suggested Note Shape

```markdown
# Mechanism: short mechanism name

## User Intent

- What behavior motivated the research:

## Original Evidence

| Evidence | Path or observation | Status |
| --- | --- | --- |
|  |  | inferred from original files |

## Framework Projection

- Existing primitive:
- Proposed primitive, if missing:
- Lowest-risk status:

## Validation

- Static inspection:
- Dry-run or preview:
- Live observe-only:
- Live behavior:

## Do Not

- Hooks or writers that should not be added until evidence changes:
```

## Rules

- Prefer original content, save, and gameplay mechanisms before hooks.
- Do not claim a hard runtime guarantee from static file evidence alone.
- Keep environment-specific paths and sample profile details out of reusable conclusions.
- Link machine reports when a domain map creates them under `state/research/`.
