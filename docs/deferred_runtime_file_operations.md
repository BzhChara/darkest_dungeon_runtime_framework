# Deferred Runtime File Operations

This document records runtime file work that should stay out of the current safe decoded-save and sidecar rule path until it is deliberately implemented and validated.

## Why Deferred

The current framework can safely materialize actions, compile virtual content overlays, initialize project-local decoded saves, promote encoded profile files under explicit guardrails, and reapply selected profile/quest-board state after original saves settle. These paths are useful, but they do not guarantee that the original game UI never sees stale or regenerated data for a short time.

Runtime file interception is the harder layer. It should be implemented as generic file and gameplay consumers, not as boss-gauntlet-only branches.

## Operations To Implement Later

### Runtime Reads

- Observe and classify original game reads for profile and content files, including `persist.quest.json`, `persist.town.json`, `persist.roster.json`, `persist.estate.json`, `persist.town_event.json`, `campaign/quest/*.json`, town building files, and map files.
- Route runtime reads through the managed overlay manifest when a verified consumer exists.
- Distinguish content reads from save reads. Content overlays can often be virtualized safely; save overlays need stricter profile scope and live consistency rules.
- Log source path, normalized path, source size/hash, replacement size/hash, mode, selected profile scope, and source artifact for every replacement.

### Runtime Generation

- Detect original game generation points that rewrite quest boards, stagecoach recruits, town stores, town events, and other week-settlement data.
- Prefer explicit post-generation reconciliation only when brief UI drift is acceptable.
- Full original week-settlement takeover is deferred. The current quest-board path may refresh the generated board after stable saves, but it should not pretend to replace treatment timers, construction queues, hero consequences, event rolls, campaign logs, or other original settlement work.
- Add hard runtime consumers only when the mod requires no visible drift, such as an always-empty stagecoach or hard-locked party selection.
- Keep generation consumers idempotent and report when no mutation was needed.

### Runtime Replacement

- Replace task-board reads only from resolved `questBoard.replaceWithFixedSet` or `questBoardPolicies` artifacts that match the active profile.
- Replace stagecoach or store data only from continuous profile policies such as `stagecoach.suppressRecruits` and `town.suppressStoreItems`.
- Replace selection or economy behavior only after a verified consumer exists. Sidecar selection consumption is not hard enforcement; hero unavailability should first be projected through original roster unavailable/missing/status fields. Manifest-only `inventory.disableItemSale` must not claim hard enforcement. The removed trinket `price = 0` overlay suppressed sale value only; restore it only as a clearly named sale-value policy or after live testing proves that original content fields truly disable selling.
- Keep `.dm` map replacement under the map template/overlay layer. Per-cell dynamic map mutation during a raid remains experimental and should not be mixed with task-board refresh.

## Acceptance Gates

- No silent fallback for hook failures. If a replacement cannot be served, the report must say whether the game received the original file, a virtual file, or an error.
- No one-off gameplay branch. The runtime consumer must read manifest artifacts and policies that other mods can also emit.
- Profile-scoped artifacts must not affect another profile.
- Hard UI guarantees, such as "the player can never see recruits in the stagecoach", require a runtime read or generation hook. Save watcher reconciliation alone is not enough.
- The implementation must include a live observation mode before write/replace mode, focused tests, and a documented rollback path.
