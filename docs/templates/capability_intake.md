# Capability Intake Template

Use this template before implementing a new gameplay feature, runtime hook, managed action, save writer, content overlay, or framework capability.

The purpose is to keep concrete gameplay ideas as pressure tests while only promoting reusable framework primitives.

## Request

- User intent:
- Desired player-visible behavior:
- Explicit non-goals:
- Current pressure test or scenario:

## Existing Coverage

- Existing facts:
- Existing events:
- Existing predicates:
- Existing actions:
- Existing sidecar state:
- Existing capabilities:
- Existing docs or tests:

## Original Darkest Dungeon Mechanisms Checked

Record evidence before proposing hooks.

| Area | Checked files or systems | Evidence | Status |
| --- | --- | --- | --- |
| Content files |  |  | unknown |
| Localization |  |  | unknown |
| Quest data |  |  | unknown |
| Town or building data |  |  | unknown |
| Trinket, loot, or rarity data |  |  | unknown |
| Roster or save fields |  |  | unknown |
| Existing gameplay restrictions |  |  | unknown |

Status values:

- verified by live test
- inferred from original files
- needs live validation
- unknown

## Option Matrix

| Option | Mechanism | Reuse potential | Risks | Tests | Recommendation |
| --- | --- | --- | --- | --- | --- |
| Original content mechanism |  |  |  |  |  |
| Virtual file overlay |  |  |  |  |  |
| Decoded-save projection |  |  |  |  |  |
| Sidecar state only |  |  |  |  |  |
| Runtime hook |  |  |  |  |  |
| UI/input/render hook |  |  |  |  |  |

## Proposed Primitive

- Capability or action name:
- Status: planned / materialized / observed / passive / intercepted / stable
- Risk level:
- Required fact inputs:
- Required event inputs:
- Required state inputs:
- Output or mutation:
- Diagnostics:
- Rollback or recovery:

## Reuse Test

Name at least one different mod idea that could reuse the same primitive without embedding this feature's specific ids in launcher or runtime code.

- Other plausible mod:
- What it would reuse:
- What remains plugin data:

## Architecture Checks

- Concrete mod ids stay in plugin data, fixtures, or docs:
- Quest ids stay in plugin data, fixtures, or docs:
- Hero ids stay in plugin data, fixtures, or docs:
- Trinket ids stay in plugin data, fixtures, or docs:
- Environment-specific paths stay in config:
- Hook is not proposed before original mechanisms are checked:
- Save writing is schema-verified, logged, reversible, and documented:
- Fallbacks preserve correctness or fail loudly:

## Verification Plan

- Static validation:
- Dry-run or preview command:
- Unit or contract test:
- Save sample test:
- Live observe-only validation:
- Live mutation validation, if allowed:

## Decision

- Decision:
- Why this is the lowest-risk starting point:
- What must be proven before promotion:
- Follow-up issues:
