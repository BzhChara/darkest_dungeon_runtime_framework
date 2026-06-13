# Optional Capacity Patches

These patches are static content compatibility helpers for the boss-gauntlet validation scenario. They are intentionally not runtime framework primitives.

## Why They Exist

The boss-gauntlet profile can create more heroes and trinket slots than an ordinary campaign expects:

- the base stage coach roster limit tops out at 28 heroes;
- the current two-copy trinket initialization produced 1162 non-stackable trinket slots in `profile_3`;
- the base game `trinket_storage` data limit is 9999, but some inventory mods, such as the local Stacking Inventory sample, lower it to 999.

These are content-capacity concerns. The framework should not hardcode them into save writers or action executors.

## Patches

`plugins/_optional/boss_gauntlet_roster_capacity_patch`

Raises the maximum stage coach roster-size upgrade from 28 to 128 by patching:

`campaign/town/buildings/stage_coach/stage_coach.building.json`

`plugins/_optional/boss_gauntlet_trinket_storage_capacity_patch`

Raises `trinket_storage` back to 9999 when another content mod lowered it to 999 by patching:

`inventory/base.inventory.system_configs.darkest`

## Load Order

Load these after ordinary inventory and stage-coach content mods. They are optional because a user may already have equivalent Workshop mods.

For framework validation, use:

```powershell
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/boss_gauntlet_capacity_validation_config.json --explain-patches --no-inject
```

When validating against the unmodified base game, the trinket patch may report a warning that the `max_slots 999` replacement was not found. That branch exists for inventory mods that lower `trinket_storage`; it is expected to be inactive against the base file, which already uses 9999.

For live framework launches, include both plugin roots in the active config:

```json
"pluginDirectories": [
  "./plugins/_validation",
  "./plugins/_optional"
]
```

If these patches later need to be distributed as traditional Darkest Dungeon mods, export them as separate content packs and place them late in the in-game mod order. Traditional DD mods generally replace whole files, so they are more likely to conflict with other capacity mods than these focused virtual replacements.
