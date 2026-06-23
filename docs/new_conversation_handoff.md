# New Conversation Handoff: Framework Workflow Reset

This file is a handoff note for starting a cleaner Codex conversation about the Darkest Dungeon runtime framework.

Project path:

```text
E:\数据文件\SelfMod\DarkestDungeonRuntimeFramework
```

The main goal for the next conversation is not to add more gameplay features immediately. The next conversation should first reset the working method, establish stronger architecture gates, and prevent the framework from being driven only by one-off gameplay ideas.

## Core Diagnosis

到目前为止，项目推进方式确实有问题。不是“这个框架做不了”，而是现在很多工作是在用用户的玩法灵感临时压出能力，而不是先建立一套强制的能力设计流程。

最大问题不是某个饰品功能写错，而是流程顺序错了。

之前“饰品不可出售”就是典型例子：我先按表层行为想“阻止出售”，自然容易走向 UI Hook 或运行时拦截；用户提醒原版已有类似物品后，才回到原版机制查 `price: 0`。这说明当前流程没有强制要求先证明“原版机制不够用”，再允许 Hook。

项目文档其实已经写了正确原则：

- `AGENTS.md` 要求玩法表达成 facts、events、predicates、actions、sidecar state、capabilities。
- `AGENTS.md` 明确要求优先原版机制，只有证明不足后才用 UI/input/render/memory hook。
- `docs/framework_capability_matrix.md` 已经有 intake checklist。
- `docs/framework_capability_matrix.md` 已经列了 red flags。

但这些现在主要是“提醒”，不是“闸门”。所以 Codex 仍然可能在一个新功能里直接实现，而不是先做机制研究。

## Current Code State

当前代码没有彻底坏掉，但有流程债。

核心 `launcher/` 和 `runtime/` 中没有看到大量具体 boss id、`profile_3`、`plot_kill_xxx` 被硬编码进核心逻辑。具体 boss gauntlet 内容主要还在 validation plugin、config、docs 里，这点是好事。

当前几个关键能力本身也还算通用：

- `trinket.patchEntry` 是按 id / where selector 改饰品 entry 字段，不是专门给某个饰品写死。
- `estate.removeInventoryItems` 是按来源和稀有度清理库存，不是只清某几个指定饰品。
- `town.suppressStoreItems` 和 `stagecoach.suppressRecruits` 是通用 save projection action。

但有几个明显风险：

- 早期 `challenge.*` executor action 已清理，当前应继续用 `tools/TestArchitectureRedFlags.ps1` 防止这类玩法模式分支回到核心代码。
- 很多功能是“能 materialize / 能 save apply”，但还不是 live hard enforcement。比如文档已经承认 sidecar selection consumption 不能真正阻止 UI 选择。
- `price: 0` 现在文档也只是称为 sale-value suppression，不是已证明的完整 UI 锁。
- save watcher 这种“过周后再修正”的方式有可见漂移风险，文档也承认 hard UI guarantees 不能只靠它。

所以问题不是“已经完全写成一次性脚本”，而是：项目已经复杂到不能再靠临场判断继续加功能。

## Stop Adding Gameplay Features For Now

下一步不要继续饰品、马车、任务板等具体玩法功能。先做一轮“防跑偏基础设施”。

建议按这个顺序：

1. 新增 `docs/templates/capability_intake.md`

   每个新功能先填这个，不写代码。必须回答：

   - 原版机制查了什么。
   - 已有 primitive 覆盖什么。
   - 为什么不 Hook。
   - 最小通用 primitive 是什么。
   - 另一个 mod 如何复用。

2. 新增 `docs/research/original_mechanisms/`

   把已经踩过的知识固化，例如：

   - `trinket_sale_suppression.md`
   - `stagecoach_generation.md`
   - `town_store_generation.md`
   - `hero_unavailability.md`
   - `quest_board_week_settlement.md`

3. 新增 `tools/TestArchitectureRedFlags.ps1`

   自动检查核心代码里是否出现具体 mod id、quest id、`profile_3`、`boss_gauntlet` 分支、UI Hook 未经 intake 等红旗。

4. 更新 `AGENTS.md` 的 Required Verification

   把 red flag test 加进去，让它不是建议，而是每次改框架前后的检查。

5. 审计现有能力

   特别是：

   - town event
   - selection consumption
   - continuous profile apply

   能保留的标成 stable / materialized / experimental。方向不对的标成 deprecated，不再继续扩展。

## Required New Workflow

以后不能再是：

```text
用户提出玩法 -> Codex 直接实现 -> 出问题后再找原版机制
```

应该改成：

```text
玩法意图
-> 原版机制研究
-> 方案矩阵
-> 最小通用 primitive
-> 风险等级
-> dry-run / preview
-> 实现
-> red flag 检查
-> live 验证
```

这一步如果不做，后面越加功能越会变成“能在当前机器上跑的补丁集合”，而不是通用框架。

## Domain Map Rule

复杂领域不能靠“灵感式追问”，要改成“领域级扫描”。

以后每碰一个大领域，比如饰品、英雄、建筑、任务板，不应该先实现某个功能，而应该先做一次 domain map。

### 1. Full Field Scan

扫描原版 + DLC 相关文件，列出所有字段、字段出现次数、示例值、所在文件。

以饰品领域为例，扫描范围应包括：

- `trinkets/*.entries.trinkets.json`
- `trinkets/*.rarities.trinkets.json`
- Nomad Wagon building data
- Color of Madness shard store data
- loot tables
- quest rewards
- boss rewards
- save inventory references

### 2. Relationship Scan

不只看饰品本体字段，还要看它被谁引用。

饰品领域至少要建立这些关系：

- rarity -> 马车 / 掉落池
- id -> quest reward / boss reward / save inventory
- price -> 出售 / 购买价值
- shard / limit -> 水晶商店
- `rarity=kickstarter/trophy/darkest_dungeon` -> 初始池排除、奖励来源、特殊获取路径

### 3. Behavior Matrix

每个字段要归类：

- definition field
- economy field
- drop field
- UI field
- restriction field
- unknown field

每个结论必须标注状态：

- verified by live test
- inferred from original files
- needs live validation
- unknown

### 4. Design After Domain Map

功能设计必须基于 domain map。

例如用户说“饰品不可出售”，Codex 不能只看 `price`，而要从饰品 domain map 里判断是否涉及：

- rarity
- loot
- shop
- save inventory
- quest reward
- boss reward
- limit
- shard
- UI behavior

### 5. Persist Domain Knowledge

领域扫描结果必须沉淀成文档和机器报告。

以饰品为例：

```text
docs/research/original_mechanisms/trinket_domain_map.md
state/research/trinket_field_inventory.json
```

这样即使有遗漏，也会少很多，因为不是从单个需求反推字段，而是先把这个领域的字段和引用关系铺开。

## Trinket Domain Map Should Be First

对饰品来说，下一步建议先做 domain map，不继续修功能：

1. 扫描所有原版/DLC trinket entry 字段。
2. 扫描所有 rarity 和来源。
3. 扫描马车、宝石商、loot、quest reward 对饰品和 rarity 的引用。
4. 输出“饰品机制地图”。
5. 再回头判断现有这些能力是否够：
   - `trinket.patchEntry`
   - `estate.ensureInventoryCounts`
   - `estate.removeInventoryItems`
   - `town.suppressStoreItems`
6. 决定哪里需要改、哪里只是配置、哪里应改成更高层的 generator。

一句话：以后复杂领域先做“全域机制图”，再做某个功能。这样不会每次都靠用户补充关键词。

## Suggested Prompt For The New Conversation

Use this prompt at the start of the next conversation:

```text
项目：E:\数据文件\SelfMod\DarkestDungeonRuntimeFramework

目标：暂停新增玩法功能，先做框架工作流校正。

请先阅读：
- AGENTS.md
- docs/framework_capability_matrix.md
- docs/content_reference_boundaries.md
- docs/deferred_runtime_file_operations.md
- docs/trinket_availability.md
- docs/new_conversation_handoff.md

本轮不要改代码。请先分析：
1. 当前项目有哪些防跑偏规则已经写在文档里。
2. 哪些规则没有变成强制检查。
3. 如何建立 capability intake / domain map / architecture red flag 流程。
4. 下一步应该先落地哪些文档、模板、脚本。

重要要求：
- 复杂领域先做 domain map，再做单个功能。
- 新功能先做 capability intake，不允许直接实现。
- 原版 Darkest Dungeon 机制优先；只有证明原版机制不足后，才允许提出 Hook。
- 具体玩法只能作为 pressure test，不能让 launcher/runtime 核心代码变成一个具体 mod。
```

## Immediate Next Step Recommendation

新对话第一步不应该继续修饰品、马车、任务板，也不应该启动游戏测试。

第一步应该是做一个小而明确的流程整改 PR/commit：

1. `docs/templates/capability_intake.md`
2. `docs/templates/domain_map.md`
3. `docs/research/original_mechanisms/README.md`
4. `tools/TestArchitectureRedFlags.ps1`
5. `AGENTS.md` verification update

完成后，再选“饰品领域”作为第一个 domain map 实例。
