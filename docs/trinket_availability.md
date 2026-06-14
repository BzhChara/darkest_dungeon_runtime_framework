# Trinket Availability And Entry Patching

This note defines the original-first model for trinket economy rules. The framework should reference and compose normal Darkest Dungeon trinket, rarity, loot, and quest files before adding UI or memory hooks.

## Original Concepts

Darkest Dungeon separates trinket behavior across several content paths:

| Goal | Original path |
| --- | --- |
| Define a trinket | `trinkets/*.entries.trinkets.json` |
| Group trinkets for random selection | `trinkets/*.rarities.trinkets.json` plus each entry's `rarity` |
| Generate Nomad Wagon stock | `campaign/town/buildings/nomad_wagon/nomad_wagon.building.json` `rarity_generation_table` |
| Generate ordinary random loot | `loot/*.loot.json` trinket entries by `rarity` |
| Award an exact quest or boss trinket | `campaign/quest/*.quest.plot_quests.json` `completion_reward.items` with `type: trinket` |
| Control sale value | trinket entry `price` |
| Sell for crystal shards | Color of Madness trinket entries with `rarity`, `shard`, and `limit` |

There is no verified separate `can_sell` flag in the original trinket entry files inspected so far. Original trophy trinkets use `rarity: trophy` and `price: 0`. For framework purposes, `price: 0` is treated as a sale-value suppression projection, not as a proven universal UI lockout primitive.

## Implemented Framework Slice

`trinket.patchEntry` is the single managed action for modifying existing trinket entry fields. It keeps the original trinket definition file as the source, then applies explicit `set` and `remove` operations chosen by the plugin author.

Exact id patch:

```json
{
  "type": "trinket.patchEntry",
  "capability": "trinket.patch_entry",
  "risk": "managed",
  "required": true,
  "args": {
    "target": "content.trinkets.entries",
    "enabled": true,
    "items": [
      {
        "id": "focus_ring",
        "set": {
          "price": 7500,
          "limit": 1,
          "rarity": "very_rare"
        }
      },
      {
        "id": "my_shard_trinket",
        "set": {
          "rarity": "comet",
          "shard": 50,
          "limit": 1
        },
        "remove": ["price"]
      }
    ]
  }
}
```

Selector patch:

```json
{
  "type": "trinket.patchEntry",
  "capability": "trinket.patch_entry",
  "risk": "managed",
  "required": true,
  "args": {
    "target": "content.trinkets.entries",
    "enabled": true,
    "items": [
      {
        "where": {
          "rarity": ["common", "uncommon", "rare", "very_rare", "ancestral"]
        },
        "set": {
          "price": 0
        }
      }
    ]
  }
}
```

Single-item shorthand is also valid by placing `id` or `where`, `set`, and `remove` directly under `args`. The compiler requires each patch item to have either an exact id selector or a non-empty `where` selector. It does not create buffs, icons, localization, or a new trinket from nothing. It does not infer ordinary-vs-shard semantics: `price`, `shard`, `limit`, `rarity`, `origin_dungeon`, or other entry fields change only when the plugin explicitly sets or removes them.

The selector currently matches existing entry fields by exact scalar value. String comparison is case-insensitive. A selector value can be an array, in which case any listed value can match. This supports rarity batches without adding a separate sale-lock action.

At launch or dry-run, materialized `trinket.patchEntry` artifacts generate virtual `sourcePath` overlays for enabled original and official non-arena DLC `*.entries.trinkets.json` files that contain matching entries. The generated overlay only changes matching entries and leaves unrelated trinkets untouched.

## Drop-Only Recipe

To make a trinket available only from a specific source:

1. Define the trinket in a normal trinket entries file.
2. Give it a dedicated rarity, such as `my_boss_only`.
3. Do not include that rarity in the Nomad Wagon `rarity_generation_table`.
4. Do not include that rarity in ordinary shared loot tables.
5. Add it to exactly the desired quest reward or boss loot path.
6. If it should not have sale value, set `price: 0` directly in the authored trinket or use `trinket.patchEntry` to set `price: 0`.
7. If it should be bought with shards instead of gold, use original Color of Madness style fields directly or use `trinket.patchEntry` to explicitly set `shard`/`rarity`/`limit` and remove `price` if that is the intended content shape.

The framework should eventually expose this as a higher-level `lootPolicy` or trinket availability declaration, then compile it into the original content files. Static trinket definitions, icons, effects, buffs, and localization remain authored by normal DD mod content.

## Quest Or Boss Reward

The most stable way to say "this boss drops this trinket" is to put the trinket in the boss plot quest completion reward:

```json
{
  "id": "my_special_trinket",
  "type": "trinket",
  "amount": 1
}
```

This follows the original boss trophy pattern. It awards the trinket when the quest resolves, not as a monster corpse loot popup.

## Monster Kill Loot

Monster `.info.darkest` files can point at loot codes, and loot tables can draw trinkets by rarity. Exact per-monster trinket drops are therefore possible by giving the trinket a dedicated rarity and using a dedicated loot table/code for that monster.

This is more fragile than quest completion rewards because it touches monster info, loot code naming, and battle loot timing. Use it only when the design specifically needs kill-time loot rather than quest completion rewards.

## Current Gaps

- There is no verified hard UI sell-button hook.
- There is no instance-level trinket identity projection for "this exact copy is consumed".
- There is no implemented high-level generator yet for "only quest A drops trinket X" or "boss Y kill loot includes trinket Z"; that belongs under future `lootPolicies`.
- Workshop/plugin trinket entry overlays need provider-aware expansion beyond the current original and official non-arena DLC scan.
- `trinket.patchEntry` is file-structure validated, but whether a particular field combination has the intended live economy behavior still needs game validation.
