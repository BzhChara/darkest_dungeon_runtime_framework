# Runtime Mod Platform Design

这份文档定义框架后续要走向的运行时 Mod 平台，而不是只停留在文件替换工具。通用规则契约见 `docs/capability_rule_contract.md`；框架验收场景见 `docs/validation_scenarios.md`；本文里的玩法段落只是用例，不是专用模板。

目标不是让玩家只能改数值、替换贴图或追加文本，而是让玩家能重组《Darkest Dungeon》的关键玩法循环，例如：

- 打完祖父后不结束游戏，解锁新区域、新剧情和新任务链。
- 把马车招募改成固定关卡挑战：预设满级英雄池、原版 Boss 关卡链、每关自选四人和饰品，失败可用原选择重试，通关后已选英雄和饰品进入不可用池。
- 给建筑升级加入等待周数、并行升级补偿和跨周完成逻辑。

这些能力不能只靠 `find -> replace` 完成。文件虚拟化仍然是内容地基，但平台还需要事件、状态、动作、Hook 能力和诊断工具。后续新增玩法应该先映射到 facts、events、predicates、actions、state、capabilities；映射不出来时扩展这些通用原语，而不是为某个玩法写特殊路径。

## Design Principles

- 兼容优先：重复修改同一内容默认不阻止启动，按顺序叠加执行，并在验证和预览里报告。
- 诊断优先：玩家应该能看到加载顺序、事件触发、状态变化、最终虚拟文件和潜在冲突。
- 安全边界硬处理：路径越界、位数不匹配、JSON 无法解析、明确 required 的操作失败，才默认阻止启动。
- 数据层先行，代码层后置：优先做声明式规则和状态机；Lua/C#/native 插件层等核心能力稳定后再加。
- 内容引用优先：怪物、技能、动画、贴图、音频、语言、普通 curio 和 loot 等静态内容优先由原版、DLC、创意工坊或插件自带文件提供；框架负责引用、校验、组合、排序和运行时投影。不要为了已有静态内容 authoring 流程添加专用 runtime 代码。
- 最小可验证闭环：每个深层能力都先做 observe-only 探针，再做最小可回退 PoC，最后再开放给插件。

## Platform Layers

```text
Plugin Manifest
  -> Content Patch Layer
  -> Event Layer
  -> State Layer
  -> Action Layer
  -> Hook Capability Layer
  -> Diagnostics Layer
```

### Content Patch Layer

当前已经有原型：

- 虚拟读取 `.darkest` / `.json` / localization / 资源文件。
- `replacements` 底层字符串替换。
- 插件 manifest 支持 `id`、`version`、`capabilities`、`phase`、`priority`、`depends`、`optionalDepends`、`loadAfter`、`loadBefore`、`conflicts`。
- `virtualFileRules.when` 支持 `modsPresent` / `modsAbsent` / `capabilitiesPresent` / `capabilitiesAbsent`，用于声明兼容补丁或条件补丁。
- `operations` 启动前按加载顺序、基于当前虚拟文本逐步编译成 `replacements`。
- operation 编译会保留 subject，例如 `key:.some_key`，用于解释最终来源和发现同一 key 的多插件修改。
- validate、preview、diff。

后续要增强：

- `.darkest` 专用解析器：理解 `.key value`、数组、继承式数据和常见 section。
- 内容 id 索引和引用校验：怪物、技能、饰品、任务、区域、建筑、curio、loot table、资源文件等。索引的第一目标是让插件能引用原版、DLC、创意工坊或插件自带内容，并报告缺失/重复/来源，不是让框架重写所有静态内容生成器。
- 更细的补丁解释器：说明某个 key 的最终值来自哪些插件和哪些规则。

静态内容与运行时编排的边界见 `docs/content_reference_boundaries.md`。例如新怪物可以来自创意工坊；框架只需要让 `encounter` 或 `spawnPool` 引用这个 `monsterId`，并在缺失时阻止或降级对应模块。

### Event Layer

事件层负责把游戏内部流程暴露成可订阅的节点。

当前 observe-only v0 已实现最低风险探针：RuntimeHook 会观察 `CreateFileW/CreateFileA/WriteFile`、`MoveFile/MoveFileEx`、`CopyFile`、`DeleteFile`、`ReplaceFile` 和 `SetFileAttributes`，把已知文件活动分类为 `data.*`、`asset.*`、`save.*` 事件。这一步只写日志，不读取事件上下文，不拦截流程，也不修改存档。真实游戏采样默认优先保留 `save.*`，普通 data/asset 事件使用独立预算，并会过滤 Steam overlay 日志这类外部噪声。

优先级从低风险到高风险：

1. observe-only：只记录事件是否发生。
2. passive read：读取事件上下文，但不改结果。
3. intercept：允许取消原逻辑或替换结果。
4. synthesize：框架主动生成额外事件。

第一批候选事件：

```text
campaign.loaded
campaign.week_advanced
quest.selected
quest.started
quest.completed
quest.failed
town.entered
roster.opened
roster.hero_added
party.selection_started
party.selection_confirmed
building.upgrade_requested
building.upgrade_completed
save.loaded
save.before_write
```

高风险事件暂缓：

```text
battle.turn_started
battle.skill_resolved
battle.ai_decision_requested
ui.widget_created
```

### State Layer

复杂 Mod 必须能保存自己的状态，不能只依赖原存档字段。

默认采用旁路存档：

```text
state/mod_state/<plugin-id>.json
```

状态命名空间按插件隔离：

```json
{
  "mods": {
    "example.roster_draft": {
      "usedHeroes": ["hero_001", "hero_002"],
      "draftModeEnabled": true
    },
    "example.building_delay": {
      "pendingUpgrades": [
        {
          "building": "blacksmith",
          "level": 3,
          "remainingWeeks": 2
        }
      ]
    }
  }
}
```

原则：

- 默认不改原存档结构。
- 初始实现先存放在框架项目目录下，避免污染原版 profile；后续可以按 campaign/run/profile 再分层。
- 原存档写入前后都要有日志。
- 旁路状态损坏时，默认禁用相关插件状态并 warning，不破坏原存档。
- 插件卸载后，其状态保留但不执行，便于回退。

### Action Layer

动作层是给声明式事件规则使用的最小能力集合。

示例：

```json
{
  "on": "building.upgrade_requested",
  "when": {
    "building": "blacksmith"
  },
  "actions": [
    { "cancelOriginal": true },
    { "spendOriginalCost": true },
    {
      "queueBuildingUpgrade": {
        "building": "blacksmith",
        "weeks": 3,
        "parallelCompensation": "reduce_by_active_count"
      }
    }
  ]
}
```

动作分级：

- safe：只写框架旁路状态或日志。
- managed：通过已知游戏 API 或已验证 Hook 改结果。
- risky：内存补丁、深层流程替换、UI 强改。默认需要显式启用。

当前第一批 managed 动作仍采用 observe-first 物化：`quest.injectFixedStage`、`roster.filterAvailableHeroes`、`equipment.filterAvailableTrinkets` 以及 boss gauntlet 的 profile-normalization 动作会写入 `modStateDirectory/_managed_actions/`。启动器已经能把 `quest.injectFixedStage` 和 `questBoard.replaceWithFixedSet` artifact 编译成 `logs/managed_action_overlay_manifest.json`，并把 manifest 路径和计数传给 RuntimeHook 做诊断；同时会为相关 `quest.plot_quests.json` 文件追加虚拟文件替换，把源 plot quest 强制为早期可用、可重复。`--refresh-quest-board-profile <profileId>` 还能把生成好的固定任务板写入配置的 watched profile 当前 `persist.quest.json`，支持 `--dry-run`、写前备份、路径校验和运行中游戏保护；`questBoardAutoRefreshEnabled` 还可以让实时 save watcher 在任意成功桥接的稳定 campaign 存档批次后走同一个 writer 自动重刷任务板，不再只依赖 `persist.quest.json` 自身变化。真实运行中外部存档写入需要配置 `questBoardAutoRefreshAllowRunningGameSaveWrite=true`。这些都是任务板刷新，不是完整周结算模拟。`inventory.disableItemSale` 的 trinket artifact 现在也会在启动或 `--dry-run` 前编译成官方 campaign trinket entries 的 `sourcePath` 覆盖，把 trinket `price` 写成 0；这验证的是内容层 sale value suppression，是否彻底禁用 UI 卖出点击还需要实机确认。`--apply-managed-actions --managed-action-save-dir <dir>` 现在能读取这些 artifact 并生成 `logs/managed_action_apply_report.json`；默认是 dry-run，只有同时传 `--write-managed-actions` 才会写入，而且第一版只允许项目目录内的 decoded JSON 存档副本。`--initialize-decoded-profile` 的汇总报告会内联 apply action/file 明细，方便直接检查每个 artifact 的 dry-run/applied/unsupported 状态。`--preview-managed-action-retention` / `--prune-managed-actions` 是 `_managed_actions/` 的显式维护工具：按 action、插件、规则、目标、profile scope 和来源分组保留最新 artifact，写 `logs/managed_action_retention_report.json`；无法解析的 artifact 会保留并警告，删除失败会报错。`tools/PrepareDecodedProfileWorkspace.ps1` 可以把真实 `profile_*` 的 top-level `persist*.json` 只读解码到 `state/decoded_profiles/<session>/decoded_save`，并可选调用 `--initialize-decoded-profile`；传入 `-EncodeInitializedProfile` 时，会把初始化后的 decoded persist 文件重新编码到同一 workspace 的 `encoded_profile`，再立即 roundtrip decode 到 `roundtrip_decoded` 并做 JSON parse 校验；它不写回 Steam userdata。`tools/PromoteEncodedProfileWorkspace.ps1` 是单独的晋级工具：默认 dry-run，只允许项目内 target profile，并且默认只提升 workspace 报告中 decoded 内容实际变化的 encoded 文件，避免单纯 re-encode 导致无关 persist 文件被重写；完整覆盖需要显式 `-PromoteAllEncodedFiles`。真实外部 profile 需要显式 `-AllowExternalTarget`，游戏运行中外部写入还需要 `-AllowRunningGameSaveWrite`。写入前会备份目标 profile 的现有文件快照和 manifest，写入后校验 hash；`-RestoreFromReport` 可以按 manifest 恢复被覆盖的原有文件，promotion 新增文件会保留并在报告中 warning，避免自动删除路径成为新的风险源。当前已支持 `wallet.setCurrencyAmounts` / `wallet.setCurrencyAmount` 写入 `persist.estate.json` 钱包资源，支持 `estate.ensureInventoryCounts` 为 trinket inventory 写入指定数量并按内容 rarity 排除初始来源，支持 `inventory.disableItemSale` 写入项目内 `_ddrt_profile_policy.json` 销售禁用策略，也支持 `roster.ensureClassInstances` 向 `persist.roster.json` 补齐每个可用职业的 hero 实例，支持 `roster.setProgression` 统一设置已有/生成英雄的 resolve XP、武器/护甲等级和 max 装备下的当前 HP，并支持 `roster.setSkillUnlocks` 按职业内容定义填充英雄自身的 combat/camping skill 列表。`upgrade.ensurePurchases` 现在能写入 decoded `persist.upgrades.json`，从原版内容定义读取 building、combat_skill、camping_skill、weapon、armour 等升级树，按 requirement code 补齐 purchase 记录；instanced 树会从 `profile.roster.heroes` 的英雄 id 和职业推导 `instance_number`。`stagecoach.suppressRecruits` 能清空 decoded `persist.town.json` 中 `stage_coach.store.*.generated` 招募池，`town.unlockAllBuildings` 能把已存在的 district `built` 标志置为 true；`townEvent.overrideCurrent` 现在能写 `persist.town_event.json` 的 current event suppress 字段，并把请求的 message 记录到 `_ddrt_profile_policy.json`。普通镇建筑等级仍通过 `upgrade.ensurePurchases` 的购买树表达，当前没有 verified `persist.town.json` 直接等级字段。自定义城镇事件文本仍需要后续 runtime/UI/content consumer 才能在游戏内生效；当前 writer 不伪造未知 save 字段。`roster.ensureClassInstances` 现在用干净 hero blueprint 生成新英雄，而不是深拷贝现有存档英雄后覆盖字段，避免把旧英雄或测试样本里的无关状态带入新对象；随机 quirk 选择会读取内容 `tags` 并让 `singleton` quirk 在同一批生成中全 roster 只分配一次。`content.trinkets.enabled` 当前从安装目录的 base trinket entries 和官方非竞技场 DLC trinket entries 读取；`content.hero_classes.enabled` 当前从 base heroes 和官方非竞技场数字 DLC hero definitions 读取；`content.upgrades.enabled` 当前从 base upgrades、base camping skills 和官方非竞技场 DLC 定义读取。如果安装目录本身被 mod 改写，要得到纯原版结果需要干净内容源。其它 profile-normalization、英雄过滤或饰品过滤 artifact 会被识别并报告为未实现，等待后续 runtime consumer 或 schema-verified save writer；当前已知 live 缺口包括过周后马车重新刷新、sidecar 已消耗英雄/饰品仍能在原版 UI 里再次选择。

第一批动作候选：

```text
setFlag
clearFlag
incrementCounter
queueBuildingUpgrade
advanceQueuedUpgrades
unlockRegion
unlockQuest
lockHero
unlockHero
markHeroUsed
filterPartySelection
showDialogue
emitEvent
cancelOriginal
```

### Hook Capability Layer

Hook 不应该直接暴露成“任意函数地址”，而应该包装成能力。

示例能力：

```text
file.virtualize
campaign.observe_week_advance
quest.observe_completion
building.intercept_upgrade_request
roster.filter_available_heroes
save.attach_sidecar_state
```

每个能力必须定义：

- 当前状态：planned / materialized / observed / intercepted / stable。
- 适用游戏 exe hash。
- 失败策略：disable capability / skip mod / fail launch。
- 日志字段。
- 最小测试场景。

### Diagnostics Layer

诊断层是通用框架的必要组成，不是附属工具。

必须能回答：

- 哪些插件启用了。
- 最终加载顺序是什么。
- 哪些补丁被跳过，为什么。
- 哪些事件被触发。
- 哪些动作执行了。
- 哪些状态被写入。
- 哪些 managed action artifact 被编译成 overlay manifest，哪些只是保留在 sidecar 中。
- 最终游戏读到的虚拟文件是什么。
- 某个玩法修改来自哪个插件、哪条规则、哪个动作。

现有工具：

```text
--list-patches
--explain-patches
--validate-only
--validate-patches
--preview-patches
--strict-patches
--init-mod-state
--dump-mod-state
--emit-event <event-id>
--initialize-decoded-profile
--apply-managed-actions
--managed-action-save-dir
--write-managed-actions
--preview-managed-action-retention
--prune-managed-actions
--managed-action-retention-keep
tools/PrepareDecodedProfileWorkspace.ps1
```

后续工具：

```text
--trace-events
--reset-mod-state <mod-id>
--explain <target-or-event>
```

## Manifest Direction

插件清单需要逐步扩展，但默认保持兼容。

草案：

```json
{
  "id": "example.building_delay",
  "name": "Delayed Building Upgrades",
  "version": "0.1.0",
  "enabled": true,
  "capabilities": [
    "file.virtualize",
    "content.patch"
  ],

  "phase": "normal",
  "priority": 100,
  "depends": [],
  "optionalDepends": [],
  "loadAfter": [],
  "loadBefore": [],

  "virtualFileRules": [],
  "factEventRules": [],
  "eventRules": [],
  "stateSchema": {}
}
```

依赖策略：

- `depends`：缺失时跳过当前插件并 warning；不阻止其他插件。
- `optionalDepends`：存在则排在它后面，不存在不影响。
- `loadAfter/loadBefore`：只影响顺序，不代表必须存在。
- 重复 `id`：默认 warning，内部用路径生成唯一 instance id。
- 冲突：当前默认 warning，不阻止启动。

## Example: Fixed Resource Boss Gauntlet

当前目标规格见：

```text
docs/boss_gauntlet_campaign_spec.md
```

目标：取消长期马车养成，改成固定资源的 Boss 讨伐战役。新建存档第一次进入时自动初始化固定满级英雄池、固定饰品池、20000 金币、满级城镇和固定任务板，并禁用饰品出售；之后游戏正常保存，不再重建或恢复损失。前置 Boss 阶段中，人物和饰品在任意终局尝试后都会被消耗，成功和失败都不回滚结算状态；每次前置 Boss 胜利额外补充 10000 金币；如果人物死光或严重失误导致不可通关，这是预期失败状态，玩家删除存档并新建存档重新挑战。全部前置 Boss 被击败后进入极暗地牢终局，只解除前置 Boss 阶段的 sidecar 一次性使用限制，不复活此前死亡的英雄，并尽量复用原版极暗地牢“通关角色不能再次进入”的限制。

需要能力：

```text
profile.entered
profile.normalized
quest.selection_confirmed
quest.attempt_resolved
profile.detect_new_or_uninitialized
profile.mark_initialized
quest_board.replace_with_fixed_set
quest_board.filter_completed_fixed_quests
roster.ensure_class_instances
roster.set_progression
roster.set_skill_unlocks
roster.enforce_availability_filter
equipment.enforce_availability_filter
estate.ensure_inventory_counts
wallet.set_currency_amounts
wallet.modify_currency
inventory.disable_item_sale
stagecoach.suppress_recruits
town.unlock_all_buildings
town.set_building_levels
town_event.override_current
state.bossGauntlet.consumedHeroIds
state.bossGauntlet.consumedTrinketIds
```

声明式草案：

```json
{
  "on": "profile.initialization_requested",
  "actions": [
    { "type": "roster.ensureClassInstances", "classCount": 2, "level": "max" },
    { "type": "estate.ensureInventoryCounts", "kind": "trinket", "count": 2 },
    { "type": "wallet.setCurrencyAmounts", "amounts": { "gold": 20000, "bust": 0, "portrait": 0, "deed": 0, "crest": 0, "shard": 0 } },
    { "type": "inventory.disableItemSale", "kind": "trinket" },
    { "type": "stagecoach.suppressRecruits" },
    { "type": "town.unlockAllBuildings" },
    { "type": "town.setBuildingLevels", "level": "max" },
    { "type": "questBoard.replaceWithFixedSet", "source": "highest_non_darkest_bosses" },
    { "type": "profile.markInitialized", "stateKey": "bossGauntlet.initialized" }
  ]
}
```

```json
{
  "on": "quest.selection_confirmed",
  "actions": [
    { "type": "selection.lock", "stateKey": "bossGauntlet.activeSelection" }
  ]
}
```

```json
{
  "on": "quest.attempt_resolved",
  "actions": [
    { "type": "attempt.recordOnce", "stateKey": "bossGauntlet.attempts" },
    { "type": "selection.consumeHeroes", "stateKey": "bossGauntlet.consumedHeroIds" },
    { "type": "selection.consumeTrinkets", "stateKey": "bossGauntlet.consumedTrinketIds" },
    { "type": "wallet.addCurrencyOnEvent", "currency": "gold", "amount": 10000, "when": "event.success == true" },
    { "type": "quest.markCompletedIfSuccessful", "stateKey": "bossGauntlet.completedQuestIds" },
    { "type": "state.transitionWhenAllCompleted", "stateKey": "bossGauntlet.phase", "to": "darkest_finale" }
  ]
}
```

最小 PoC：

1. 不直接改真实存档。
2. 先用旁路状态记录 initialized、fixedQuestIds、completedQuestIds、activeSelection、consumedHeroIds、consumedTrinketIds。
3. 通过 dry-run 诊断报告证明初始化幂等：第一次进档构建，后续进档不重建、不复活、不补饰品。
4. 再输出固定任务板和当前可选/不可选英雄、饰品。
5. 再验证极暗地牢终局解锁只清除 sidecar 限制，不触发英雄复活或 roster rebuild。
6. 再 Hook 任务板、可选角色列表、饰品列表和出征校验。
7. 最后补 UI 提示。

早期 `validation.challenge_run_contract` 仍保留“失败锁定重试”的测试语义，用来验证 state/event/managed artifact 管线。它不是当前目标玩法的最终规格。

## Example: Delayed Building Upgrades

目标：建筑升级不再立即完成，而是在若干周后完成；越高级等待越久；同时升级多个建筑时有时间补偿。

需要能力：

```text
building.upgrade_requested
campaign.week_advanced
building.apply_upgrade
state.pendingUpgrades
ui.show_pending_upgrade
```

声明式草案：

```json
{
  "on": "building.upgrade_requested",
  "when": {
    "building": "blacksmith"
  },
  "actions": [
    { "cancelOriginal": true },
    { "spendOriginalCost": true },
    {
      "queueBuildingUpgrade": {
        "weeksFormula": "1 + targetLevel",
        "parallelCompensation": "reduce_by_active_count"
      }
    }
  ]
}
```

```json
{
  "on": "campaign.week_advanced",
  "actions": [
    { "advanceQueuedUpgrades": true },
    { "completeReadyBuildingUpgrades": true }
  ]
}
```

最小 PoC：

1. observe building upgrade click。
2. 阻止原升级，写入 pending 状态。
3. observe week advance 并减少 remainingWeeks。
4. 到 0 时调用或模拟原升级完成。
5. 最后处理 UI 倒计时。

## Example: Post-Ancestor Campaign

目标：祖父战结束后不直接结束，而是解锁新区域、新剧情、新任务链。

需要能力：

```text
quest.completed
campaign.ending_requested
region.unlock
quest.inject
dialogue.show
state.storyFlags
```

声明式草案：

```json
{
  "on": "quest.completed",
  "when": {
    "questId": "ancestor_final"
  },
  "actions": [
    { "setFlag": { "postgame_unlocked": true } },
    { "unlockRegion": "black_coast" },
    { "unlockQuest": "black_coast_intro" },
    { "showDialogue": "postgame_intro_001" }
  ]
}
```

最小 PoC：

1. observe final quest completion。
2. 写入 `postgame_unlocked=true`。
3. 阻止结局流程只打日志。
4. 注入一个已有类型的新任务。
5. 再做新区域 UI。

## Roadmap

1. 保持文件虚拟化、验证、预览稳定。
2. 设计并实现插件加载顺序和依赖图，但默认兼容优先。
3. 做事件探针，只记录不改逻辑。
4. 做旁路 Mod 状态存档。当前已有启动器级 `--init-mod-state` / `--dump-mod-state` 初始读写。
5. 做最小事件规则执行器。当前已有 `--emit-event`，可执行已实现的安全 sidecar state 动作，并可为部分 managed 动作生成 sidecar artifact；真实游戏事件接入和真实游戏修改仍待做。
6. 选择一个 PoC：固定关卡挑战适合作为第一个玩法 dry-run，因为它先验证 facts、sidecar state、选择过滤和状态推进，不需要马上拦截真实 UI。
7. 再做建筑升级等待，因为它验证事件、状态、跨周推进和 UI 提示。
8. 最后做 post-Ancestor campaign。

## Non-Goals For Now

- 不做任意 Lua/C#/native 插件加载。
- 不承诺任意新增 UI。
- 不改原版存档结构。
- 不直接开放任意内存写入。
- 不绕过 Steam、DRM 或系统安全机制。
