# Optional Capacity Patches

Optional capacity patches are static content helpers for validation scenarios. They are intentionally not runtime framework primitives.

## Why They Exist

Some rule sets can create pressure on vanilla content limits. For example, the boss-gauntlet validation scenario needs a larger fixed hero pool than an ordinary campaign roster.

Capacity values are authored content, not framework state. A framework plugin may provide a small optional patch when a scenario needs one, but it should not treat capacity choices as automatic repairs. If another mod already changes the same limit, or a user wants a smaller limit, that content stack should decide the final value through normal load order or a separate compatibility patch.

The framework should not hardcode capacity fixes into save writers or action executors.

## Patch

`plugins/_optional/boss_gauntlet_roster_capacity_patch`

Raises the maximum stage coach roster-size upgrade from 28 to 128 by patching:

`campaign/town/buildings/stage_coach/stage_coach.building.json`

## Load Order

Load this after ordinary stage-coach content mods. It is optional because a user may already have an equivalent Workshop mod or may not want a larger roster.

For framework validation, use:

```powershell
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/boss_gauntlet_capacity_validation_config.json --explain-patches --no-inject
```

For live framework launches, include both plugin roots in the active config:

```json
"pluginDirectories": [
  "./plugins/_validation",
  "./plugins/_optional"
]
```

If this patch later needs to be distributed as a traditional Darkest Dungeon mod, export it as a separate content pack and place it late in the in-game mod order. Traditional DD mods generally replace whole files, so they are more likely to conflict with other capacity mods than these focused virtual replacements.
