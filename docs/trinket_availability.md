# Trinket Availability And Sale Policy

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

`inventory.disableItemSale` remains a policy action. Its default method is still policy-only and records intent into `_ddrt_profile_policy.json`.

For trinkets, plugins can opt into an original content projection:

```json
{
  "type": "inventory.disableItemSale",
  "capability": "inventory.disable_item_sale",
  "risk": "managed",
  "required": true,
  "args": {
    "target": "profile.inventory",
    "itemKind": "trinket",
    "method": "content_price_zero",
    "disabled": true
  }
}
```

At launch or dry-run, materialized artifacts with `method: content_price_zero` generate virtual `sourcePath` overlays for every enabled original and official non-arena DLC `*.entries.trinkets.json` file that contains nonzero prices. The generated overlay sets those trinket `price` fields to `0`.

This projection is intentionally explicit because trinket `price` can affect more than selling. If a campaign still allows trinkets to appear in the Nomad Wagon or another shop, setting prices to `0` may also affect purchase display or purchase cost. A plugin that wants "cannot sell but still buy normally" needs a verified runtime/UI/economy hook later.

`trinket.projectShardStore` is a convenience projection for one or more existing trinket ids. It keeps the original trinket id and entry, then overlays Color of Madness style store fields:

```json
{
  "type": "trinket.projectShardStore",
  "capability": "trinket.project_shard_store",
  "risk": "managed",
  "required": true,
  "args": {
    "target": "content.trinkets.shardStore",
    "enabled": true,
    "items": [
      {
        "id": "focus_ring",
        "shard": 50,
        "limit": 1,
        "rarity": "comet"
      }
    ]
  }
}
```

Single-item shorthand is also valid by placing `id`, `shard`, `limit`, and `rarity` directly under `args`. The compiler requires the trinket id to already exist in an enabled trinket entry file. It does not create buffs, icons, localization, or a new trinket from nothing. By default it removes ordinary `price`, sets `origin_dungeon` to an empty string, and defaults `rarity` to `comet` and `limit` to `1`.

`inventory.disableItemSale` and `trinket.projectShardStore` both modify trinket entry files. The overlay compiler merges them into one generated `sourcePath` file per target, so a plugin can suppress sale values and project selected items into shard-store form without one overlay undoing the other.

## Drop-Only Recipe

To make a trinket available only from a specific source:

1. Define the trinket in a normal trinket entries file.
2. Give it a dedicated rarity, such as `my_boss_only`.
3. Do not include that rarity in the Nomad Wagon `rarity_generation_table`.
4. Do not include that rarity in ordinary shared loot tables.
5. Add it to exactly the desired quest reward or boss loot path.
6. If it should not be sellable, set `price: 0` directly in the authored trinket or use `inventory.disableItemSale` with `method: content_price_zero`.
7. If it should be bought with shards instead of gold, use original Color of Madness style fields directly or use `trinket.projectShardStore` for the selected existing id.

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
- `trinket.projectShardStore` is file-structure validated, but a custom non-CoM trinket id appearing in the live shard store still needs live game validation.
