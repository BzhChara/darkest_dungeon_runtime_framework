# Repository Instructions

This repository is the runtime mod framework prototype for Darkest Dungeon 1 on Windows.

## Project Boundary

- Treat the current repository root as the working root for this project.
- Do not modify the original game directory, Steam userdata saves, Workshop mods, or installed game files unless the user explicitly asks for that exact operation.
- For live game validation, treat `E:\Steam\userdata\1097809614\262060\remote\profile_3` as the user's designated test profile unless the user says otherwise.
- Runtime mod state belongs in framework sidecar files under `state/`, not in original `profile_*` saves.
- Original save files are read-only by default. Any future original-save write path must be schema-verified, logged, reversible, and explicitly documented before use.
- `logs/`, `state/`, `.research/`, build output, and generated probe files are runtime or research artifacts. Do not commit them except intentional placeholders such as `.gitkeep` or deliberate documentation fixtures.

## Architecture Rules

- Keep the framework generic. New gameplay ideas should be represented as facts, events, predicates, actions, sidecar state, and capabilities.
- If a gameplay idea cannot be expressed, add or improve a reusable primitive. Do not add one-off hardcoded gameplay branches for a specific mod idea.
- Validation scenarios may be concrete, but framework runtime code must not become "one gameplay idea in code." If a first slice is intentionally narrow, document the limitation and the generic primitive it should become next.
- Content patching, event rules, state, action execution, and native hooks are separate layers. Keep their boundaries explicit.
- Treat authored game content and runtime orchestration as separate concerns. The framework should usually reference and validate externally authored monsters, skills, assets, loot, curios, localization, and Workshop/plugin content instead of reimplementing static content authoring; see `docs/content_reference_boundaries.md`.
- Prefer observe-first instrumentation before interception. Deep hooks and managed mutations should start as diagnostics or dry-run behavior before changing game outcomes.
- Capability-gate risky behavior. Do not present a feature as runtime-supported until it has a working diagnostic path and a focused regression test.
- Compatibility is preferred for plugin loading: duplicate ids, declared conflicts, and load cycles should usually warn and keep a stable order; missing required dependencies may skip only the affected plugin.
- Sidecar state is per plugin instance. Corrupt sidecar state should affect that plugin state namespace, not original campaign saves.

## Implementation Style

- Follow existing C# style and existing folder ownership: launcher config in `launcher/Config`, patch and rule logic in `launcher/Patching`, save facts in `launcher/Save`, process/injection code in `launcher/Process`.
- Keep changes focused. Avoid unrelated refactors while adding a primitive or fixing a bug.
- Prefer structured APIs and parsers over brittle string manipulation when the codebase or .NET provides a reasonable option.
- Avoid new dependencies unless they clearly reduce risk or complexity. Document why they are needed before adding them.
- Do not silently swallow build, test, or runtime failures. Report the failing command and current state.

## Required Verification

Run the narrowest useful checks for the change. For framework code, these are the usual baseline:

```powershell
dotnet build launcher/DDRuntimeLoader.csproj -c Release
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/rule_contract_validation_config.json --explain-rules --no-inject
.\tools\TestProjectRootResolution.ps1
.\tools\TestRuntimeEventExecutor.ps1
.\tools\TestBossGauntletContract.ps1
.\tools\TestSaveEventBridge.ps1
.\tools\TestRealtimeSaveBridge.ps1
.\tools\TestManagedActionOverlay.ps1
.\tools\TestManagedActionRetention.ps1
.\tools\TestQuestBoardProfileRefresh.ps1
.\tools\TestQuestBoardRealtimeRefresh.ps1
.\tools\TestManagedActionSaveApplier.ps1
.\tools\TestProfilePromotion.ps1
.\tools\TestMapFileInspector.ps1
.\tools\TestChallengeRunDryRun.ps1 -AssertSample
.\tools\TestSaveSampleFacts.ps1
git -c safe.directory=. diff --check
```

For documentation-only changes, `git diff --check` plus a focused read-through is usually enough.

## Git And Generated Files

- The user has initialized this repository and allows commits when a coherent step is complete.
- Remote `origin` is `git@github.com:BzhChara/darkest_dungeon_runtime_framework.git`; after creating a coherent local commit, push the current branch to `origin` unless the user asks not to.
- Check `git status --short` before and after work.
- Commit source, config, docs, tests, and intentional fixtures only.
- Do not stage ignored runtime outputs from `logs/` or `state/`.
- If a stale `.git/index.lock` appears and no git process is running, clean it carefully inside the repository before committing.

## Updating This File

Do not update `AGENTS.md` for ordinary progress, next-step plans, or one-off bug notes.

Update it only when a long-lived project rule changes, such as:

- a new required regression command,
- a changed safety boundary for original saves or game files,
- a new directory ownership rule,
- a new architecture principle for facts/events/actions/state/capabilities,
- a durable policy for capability risk, plugin loading, generated files, or commits.
