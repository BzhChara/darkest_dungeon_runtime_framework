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
- 内容 id 索引：怪物、技能、饰品、任务、区域、建筑等。
- 更细的补丁解释器：说明某个 key 的最终值来自哪些插件和哪些规则。

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

- 当前状态：planned / observed / intercepted / stable。
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
```

后续工具：

```text
--trace-events
--init-mod-state
--dump-mod-state
--emit-event <event-id>
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

## Example: Fixed Stage Challenge Mode

目标：取消长期马车养成，改成独立挑战模式。一次挑战包含固定 Boss 关卡链；玩家从预设满级英雄池中每关选择四人，并自由分配饰品。失败可以重试同一关，但选择锁定；通关后英雄和饰品都不能再用于后续关卡。

需要能力：

```text
challenge.stage_selection_started
challenge.stage_selection_confirmed
challenge.stage_completed
challenge.stage_failed
challenge.lock_stage_selection
roster.filter_available_heroes
equipment.filter_available_trinkets
quest.inject_fixed_stage
state.challengeRun.usedHeroIds
state.challengeRun.usedTrinketIds
state.challengeRun.lockedStageSelection
```

声明式草案：

```json
{
  "on": "challenge.stage_selection_started",
  "actions": [
    { "quest.injectFixedStage": { "stage": "state.challengeRun.currentStage" } },
    { "filterPartySelection": { "excludeStateList": "challengeRun.usedHeroIds" } },
    { "filterTrinketSelection": { "excludeStateList": "challengeRun.usedTrinketIds" } }
  ]
}
```

```json
{
  "on": "challenge.stage_selection_confirmed",
  "actions": [
    { "lockStageSelection": "challengeRun.lockedStageSelection" }
  ]
}
```

```json
{
  "on": "challenge.stage_completed",
  "actions": [
    { "appendSelectedHeroesToStateList": "challengeRun.usedHeroIds" },
    { "appendSelectedTrinketsToStateList": "challengeRun.usedTrinketIds" },
    { "advanceStage": "challengeRun.currentStageIndex" }
  ]
}
```

```json
{
  "on": "challenge.stage_failed",
  "actions": [
    { "recordFailedAttempt": "challengeRun.stageAttempts" }
  ]
}
```

最小 PoC：

1. 不直接改真实存档。
2. 先用旁路状态记录 currentStage、lockedStageSelection、usedHeroIds、usedTrinketIds。
3. 通过 dry-run 诊断报告输出“当前可选/不可选英雄和饰品”。
4. 再 Hook 可选角色列表和关卡选择。
5. 最后补 UI 提示。

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
5. 做最小事件规则执行器。当前已有 `--emit-event`，可执行已实现的安全 sidecar state 动作，并可为部分 managed 动作生成计划报告；真实游戏事件接入和真实游戏修改仍待做。
6. 选择一个 PoC：固定关卡挑战适合作为第一个玩法 dry-run，因为它先验证 facts、sidecar state、选择过滤和状态推进，不需要马上拦截真实 UI。
7. 再做建筑升级等待，因为它验证事件、状态、跨周推进和 UI 提示。
8. 最后做 post-Ancestor campaign。

## Non-Goals For Now

- 不做任意 Lua/C#/native 插件加载。
- 不承诺任意新增 UI。
- 不改原版存档结构。
- 不直接开放任意内存写入。
- 不绕过 Steam、DRM 或系统安全机制。
