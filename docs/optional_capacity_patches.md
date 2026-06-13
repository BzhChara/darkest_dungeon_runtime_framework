# Optional Capacity Patch

This patch is a static content compatibility helper for the boss-gauntlet validation scenario. It is intentionally not a runtime framework primitive.

## Why They Exist

The boss-gauntlet profile can create more heroes than an ordinary campaign expects:

- the base stage coach roster limit tops out at 28 heroes;

This is a content-capacity concern. The framework should not hardcode it into save writers or action executors.

The framework does not ship a trinket-storage capacity patch. The base game currently gives `trinket_storage` a high limit, and if another inventory mod intentionally lowers that value, preserving or changing that choice belongs to that mod stack, not to the runtime framework.

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
