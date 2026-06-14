# Architecture

The long-term runtime platform design lives in `docs/runtime_mod_platform.md`. The generic rule contract lives in `docs/capability_rule_contract.md`. The generality checklist for new capabilities lives in `docs/framework_capability_matrix.md`. Content reference and authoring boundaries live in `docs/content_reference_boundaries.md`. Acceptance scenarios live in `docs/validation_scenarios.md`. This document records the current skeleton and short-term component boundaries; the platform document records the direction for events, state, actions, and deeper hook capabilities.

## Phase 1: Injection and Logging

The goal is to prove three things:

1. The launcher can reliably find the game entry point.
2. The launcher can load a `RuntimeHook.dll` that matches the game architecture.
3. The DLL can write logs after entering the game process.

This phase does not change game logic.

## Components

### DDRuntimeLoader

C# console launcher.

Responsibilities:

- Read `config/default_config.json` or `config/config.json`.
- Validate the game path, DLL path, and log directory.
- Compute and log the game executable SHA-256.
- Start `Darkest.exe` suspended by default, inject the DLL first, then resume the main thread so early resource reads are not missed.
- Inject RuntimeHook.dll through a remote `LoadLibraryW` thread.
- Write `DD_RUNTIME_FRAMEWORK_ROOT`, `DD_RUNTIME_LOG_DIR`, file IO observation config, and event probe config into the game process environment.
- Optionally run a launcher-side save directory watcher for real Steam userdata / Documents Darkest save writes, then keep listening briefly after game exit for external sync writes.
- Scan plugin patch manifests at `plugins/<plugin-id>/patches.json`, build a load plan from manifest dependency and ordering fields, then write virtual file rules into `DD_RUNTIME_VIRTUAL_RULE_*` environment variables.
- Validate patch rules before launch: target file existence, current virtual file size limits, string hit counts after final replacement ordering, and same-target multi-rule hints.
- Explain and preview patch results without launching the game, including load order, ordering edges, skip reasons, virtual file text, short diffs, and same-target line conflict hints.
- Simulate events with `--emit-event` without launching the game, execute implemented safe `eventRules` actions, and write sidecar state. Some managed actions are materialized as sidecar artifacts first and do not directly change the live game.
- Before game launch or `--dry-run`, compile consumable sidecar artifacts from `_managed_actions/` into `logs/managed_action_overlay_manifest.json`, then expose them to RuntimeHook diagnostics through `DD_RUNTIME_MANAGED_OVERLAY_*`. Today `quest.injectFixedStage` and `questBoard.replaceWithFixedSet` are appended to the existing virtual file rule environment. `questBoard.replaceWithFixedSet` can also explicitly refresh the current quest board for a watched profile through `--refresh-quest-board-profile`, or be reapplied by the save watcher when `questBoardAutoRefreshEnabled` observes live stable save batches. Empty quest-board policy materialization markers supersede stale dynamic board artifacts. `continuousProfileActionAutoApplyEnabled` lets the same watcher decode a live profile into a project-local workspace, apply only continuous profile actions, re-encode changed persist files, back up originals, and write them back under its running-game write guard. `inventory.disableItemSale` defaults to a profile sale policy; for trinkets, `method: content_price_zero` also generates original-content `price = 0` virtual overlays. Hero/trinket reuse restrictions remain sidecar selection facts until they are projected through verified original mechanisms.

### RuntimeHook.dll

C++ DLL.

Responsibilities:

- Create an initialization thread after `DLL_PROCESS_ATTACH`.
- Initialize logging.
- Record process, module path, and environment variables.
- Initialize file IO hooks, the virtual file channel, and observe-only event probes.
- Record managed action overlay manifest path, file size, overlay count, and issue count. Consume launcher-added overlay rules through the existing virtual file channel. Hero/trinket reuse restrictions are not compiled into a custom manifest policy; future hard enforcement should first test original roster unavailable/missing/status fields.

### Hook Layer

Later phases add MinHook or an equivalent library here.

Recommended order:

1. Observe file read paths. Log only. The current phase hooks `CreateFileW/CreateFileA` through MinHook.
2. Return virtual content for `.darkest` / localization files. The current prototype supports configured rule lists: each rule matches one path suffix and applies ordered string replacements, then serves the result through virtual handles for `ReadFile` / `GetFileSize` / `SetFilePointer` / `CloseHandle`. Rules may also use `sourcePath` to provide full-file bytes generated under the project root, mainly for binary replacements such as `.dm` files. `sourcePath` is not mixed with text replacements or operations today, and RuntimeHook reads `sourcePath` through Win32 extended-path form so generated files still open when the project path exceeds 260 characters. Save files such as `profile_*/persist.*.json` do not use runtime virtual file replacement by default, because the game's StorageManager relies on real directory enumeration sizes during startup sync, backup, and save transfer. Those changes should go through managed save writers with backup and reporting.
3. Observe file writes and lifecycle operations. Log only. The current phase hooks `WriteFile`, `MoveFile/MoveFileEx`, `CopyFile`, `DeleteFile`, `ReplaceFile`, and `SetFileAttributes`, classifies known real file activity as `data` / `asset` / `save`, and gives `save` events a separate log budget. External noisy paths can be filtered by config.
4. Use the launcher sidecar watcher for observe-only records of external save writes the DLL cannot cover.
5. Add structured hooks for data loading functions.
6. Touch battle, AI, and save logic last.

### Plugin Layer

The first plugin layer implements patch manifests only and does not load third-party code:

- The launcher scans configured `pluginDirectories`.
- Each plugin directory contains one `patches.json`.
- Manifests with `enabled:false` are logged but do not participate in rule merging.
- Manifests with `enabled:true` may provide `id`, `version`, `capabilities`, `phase`, `priority`, `depends`, `optionalDepends`, `loadAfter`, `loadBefore`, `conflicts`, and `virtualFileRules`.
- Manifests can now also declare `eventRules` and `stateSchema`; modular `contentRefs` should continue to grow. `eventRules` can be explained with `--explain-rules` and executed with `--emit-event` for implemented safe actions or selected managed action materialization. `quest.injectFixedStage` and `questBoard.replaceWithFixedSet` artifacts enter the overlay manifest before launch and generate virtual replacement entries for related `quest.plot_quests.json` files. Fixed quest boards can also be written in a controlled way to watched profile `persist.quest.json` through `--refresh-quest-board-profile <profileId>`, or reapplied by the save watcher after live quest-board saves. `inventory.disableItemSale` artifacts enter the manifest policy area; trinket artifacts with `method: content_price_zero` also generate trinket entry price overlays. Selection consumption remains sidecar state until a verified original projection is implemented. `stateSchema` can be initialized and read from framework sidecar state directories.
- Static content authoring and runtime orchestration are separate layers. Monsters, monster skills, textures, animation, audio, localization, and ordinary curio/loot definitions may come from the base game, DLC, Workshop mods, or plugin-provided files. The framework should prioritize references, validation, dependency reports, composition, ordering, and runtime projection. It should implement a writer only when rule generation or runtime enforcement truly requires one.
- Duplicate `id`, declared conflicts, and order cycles default to warnings. Missing required dependencies skip the affected plugin without blocking other plugins.
- `virtualFileRules` can use `when.modsPresent` / `when.modsAbsent` / `when.capabilitiesPresent` / `when.capabilitiesAbsent` as rule-level conditions. Rules whose conditions are not satisfied appear only in explain diagnostics and do not enter final patch compilation.
- `operations` are compiled before launch into low-level string `replacements`, applying load order and the current virtual text step by step.
- Compiled replacements keep their operation subject, such as `key:.some_key`, for explain, validate, preview diff, and key-level conflict hints.

Consider these only after the foundation is stable:

- native C ABI plugins
- Lua plugins
- C# plugin host

## Risk Control

- Every hook must be configurable off.
- Every deep hook must bind to the game executable hash.
- Plugin enable order must be logged.
- Custom save state is not written by default.
- Crash triage should start from the last runtime log entry.
