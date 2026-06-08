# Capability and Rule Contract

This document defines the generic runtime rule model. It is intentionally not a list of special gameplay templates. A new mod idea should be decomposed into facts, events, predicates, actions, state, and capabilities. If the idea cannot be represented, the framework should add or improve a primitive in one of those categories instead of adding one-off gameplay logic.

## Current Status

- `virtualFileRules` are implemented and executable.
- `eventRules` are parsed, explained, and can be exercised through `--emit-event` for implemented safe actions.
- `stateSchema` is parsed from enabled plugins and can be initialized/read as sidecar state through `--init-mod-state` and `--dump-mod-state`.
- `--explain-rules` reports declared `eventRules`, required capabilities, action capabilities, and skip reasons.
- The first safe action executor supports sidecar state primitives and the fixed-stage challenge state primitives. Managed game mutation actions still report as unsupported.
- Implemented safe actions validate their declared arguments strictly. Missing referenced `event.*` or `state.*` paths, invalid explicit argument types, and missing definition files fail the action and are written to `logs/runtime_event_report.json`.
- Save facts are exported from original-game persist files and documented in `docs/save_field_map.md`.
- `--infer-save-events` can translate a save state report into framework events for implemented observe-first bridges. The first bridge maps last-raid progression facts to `challenge.stage_completed` or `challenge.stage_failed` for a matching stage `sourceQuestId`.
- Runtime hooks are currently observe-first. Intercepting game flow remains capability-gated work.

## Core Model

```text
facts       current known game/save/content state
events      points in game flow or launcher flow
predicates  boolean checks over facts, event payload, and mod state
actions     requested changes or side effects
state       sidecar persistent plugin data
capability  named framework power required to observe or mutate something
```

The rule engine should evaluate rules in this order:

1. Build active plugin load order.
2. Build active capability set.
3. Load sidecar state.
4. Listen for a framework event.
5. Evaluate matching `eventRules`.
6. Execute actions in rule order, honoring capability and risk policy. Current implementation only executes implemented safe actions.
7. Write diagnostics and state changes.

Sidecar state writes are strict by default: the state store writes a temporary file and requires atomic replacement to succeed. Non-atomic direct-write fallback is only available through explicit opt-in configuration or the `--allow-non-atomic-state-writes` CLI flag, and that downgrade is reported as a warning.

## Manifest Fields

The manifest may carry future runtime rules alongside existing content patches:

```json
{
  "id": "author.mod_id",
  "version": "0.1.0",
  "enabled": true,
  "capabilities": [
    "file.virtualize",
    "content.patch",
    "state.sidecar"
  ],
  "virtualFileRules": [],
  "eventRules": [],
  "stateSchema": {}
}
```

`eventRules` do not replace `virtualFileRules`. Content patches remain the right primitive for static text, data, localization, and assets. Event rules are for game-flow changes, cross-week behavior, plugin state, and runtime decisions.

## Event Rule Shape

```json
{
  "id": "rule_id",
  "enabled": true,
  "on": "campaign.week_advanced",
  "phase": "normal",
  "priority": 0,
  "requiresCapabilities": [
    "campaign.observe_week_advance",
    "state.sidecar"
  ],
  "when": {
    "all": [
      { "fact": "campaign.inRaid", "op": "equals", "value": false },
      { "state": "author.mod_id.someFlag", "op": "equals", "value": true }
    ]
  },
  "actions": [
    {
      "type": "state.incrementCounter",
      "capability": "state.sidecar",
      "risk": "safe",
      "required": true,
      "args": {
        "key": "author.mod_id.weekCount",
        "amount": 1
      }
    }
  ]
}
```

Required rule fields:

- `id`: stable rule id inside the plugin.
- `on`: event id.
- `actions`: ordered action list.

Optional rule fields:

- `enabled`: defaults to `true`.
- `phase`: load phase for rules on the same event. Initial values should mirror plugin phases: `base`, `early`, `normal`, `compat`, `late`.
- `priority`: lower numbers run first inside the same phase.
- `requiresCapabilities`: all listed capabilities must be available or the rule is skipped with diagnostics.
- `when`: predicate tree.

## Predicate Contract

Predicates are composable and data-driven:

```json
{
  "all": [
    { "fact": "progression.totalSuccessfulQuestsFinished", "op": "greaterOrEqual", "value": 10 },
    {
      "any": [
        { "fact": "quest.lastCompleted.id", "op": "equals", "value": "some_quest" },
        { "state": "author.mod_id.overrideEnabled", "op": "equals", "value": true }
      ]
    }
  ]
}
```

Initial operators:

```text
exists
notExists
equals
notEquals
greater
greaterOrEqual
less
lessOrEqual
contains
notContains
matches
```

Address spaces:

```text
fact:<facts path from save/content/runtime facts>
event:<current event payload path>
state:<plugin sidecar state path>
capability:<capability id>
```

Short forms such as `"fact": "campaign.inRaid"` are allowed and mean `fact:campaign.inRaid`.

## Action Contract

Actions are requests for framework primitives. They should not expose raw memory addresses or arbitrary native code.

```json
{
  "type": "quest.unlock",
  "capability": "quest.mutate_available_list",
  "risk": "managed",
  "required": true,
  "args": {
    "questId": "modded_intro",
    "source": "author.mod_id"
  }
}
```

Action fields:

- `type`: stable action primitive id.
- `capability`: capability required to execute it.
- `risk`: `safe`, `managed`, or `risky`.
- `required`: when true, failure follows the capability failure policy; when false, failure is logged and execution continues.
- `args`: action-specific structured arguments.

Action categories:

```text
state.*          sidecar state only
log.*            diagnostics only
content.*        virtual content patch or generated content
save.*           original save read/write through verified schema
quest.*          quest pool, quest gate, reward, or completion state
roster.*         hero pool, hero status, party filtering
town.*           town building, store, activity, recruit state
upgrade.*        upgrade purchase tree and completion state
campaign.*       campaign progression, week, ending, region gates
event.*          emit or suppress framework events
ui.*             overlay or game UI mutation
native.*         explicitly risky hook or patch wrapper
```

## State Contract

Complex mods need sidecar state so they do not have to overload original save fields.

```json
{
  "stateSchema": {
    "usedHeroes": {
      "type": "array",
      "items": "string",
      "default": []
    },
    "weekCount": {
      "type": "integer",
      "default": 0
    }
  }
}
```

Sidecar state rules:

- State is namespaced by plugin instance id.
- State writes are atomic and logged.
- Corrupt sidecar state disables the affected plugin state namespace, not the original campaign save.
- Plugin uninstall keeps state unless the user explicitly resets it.
- Initial launcher support writes `state/mod_state/<plugin-id>.json` and reports state through `--dump-mod-state`.
- A destructive `--reset-mod-state <mod-id>` should be added only after policy and backup behavior are explicit.

## Capability Contract

Capabilities describe what the framework can safely observe or change. A mod declares capabilities it needs; a rule action names the capability it uses.

Capability fields for future registry entries:

```json
{
  "id": "quest.observe_completion",
  "status": "observed",
  "risk": "safe",
  "source": "runtime-hook",
  "gameExeHashes": [
    "3800fd9aa745c31eb4744b5c260502733d16dcc39b5da29b8404982d8956fb57"
  ],
  "failurePolicy": "disableCapability",
  "logs": [
    "eventId",
    "profile",
    "questId"
  ],
  "minimumTest": "complete one original quest and confirm exactly one quest.completed event"
}
```

Capability status levels:

```text
planned      documented only
observed     hook or watcher can observe the event
passive      context can be read without changing game result
intercepted  framework can cancel or replace original behavior
stable       tested across expected original-game flows
```

Risk levels:

```text
safe     sidecar state, diagnostics, read-only facts
managed  verified schema writes or capability-wrapped intercepts
risky    memory patching, deep flow replacement, UI mutation, battle logic
```

Failure policies:

```text
disableCapability
skipRule
skipPlugin
failLaunch
```

Default policy should prefer `disableCapability` or `skipRule` unless a plugin marks an action as required and the user enabled strict behavior.

## General Gap Handling

When a mod idea cannot be expressed, classify the missing piece:

| Missing piece | Add this, not a special-case gameplay template |
| --- | --- |
| Cannot know required data | new fact extractor or content index |
| Cannot know timing | new observe-only event |
| Cannot read event context | passive event payload support |
| Cannot prevent original behavior | managed intercept capability |
| Cannot make desired change | new action primitive |
| Needs durable custom memory | sidecar state schema/action support |
| Needs visual feedback | overlay or UI capability |
| Needs unsupported engine behavior | risky native capability with exe-hash gating |

This is the rule that keeps the framework general: new gameplay ideas should expand reusable primitives, not become hardcoded modules.

## First Implementation Slice

The next code slice should stay generic:

1. Parse and log `eventRules` counts. Done as a declaration carrier.
2. Add `--explain-rules` to print declared rules, required capabilities, and skipped reasons. Done for manifest-level rule declarations.
3. Add validation manifests for quest draft, fixed-stage challenge runs, and delayed building upgrades. Done as declaration-level framework acceptance scenarios.
4. Add a capability registry document or JSON schema.
5. Add sidecar state file read/write with no gameplay actions. Initial `--init-mod-state` / `--dump-mod-state` support is implemented.
6. Add an observe-only event bus sourced from existing save watcher/runtime logs. Initial save-state-report to runtime-event bridge exists for last-raid challenge stage results.
7. Add a no-op action executor for `log.*` and `state.*`. Initial `--emit-event` support now executes implemented safe state actions against sidecar state.

Only after that should gameplay experiments be expressed as ordinary rules.
