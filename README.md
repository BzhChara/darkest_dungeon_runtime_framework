# Darkest Dungeon Runtime Framework

这是一个面向《Darkest Dungeon 1》Steam Windows 版的运行时 Mod Loader / Hook 框架原型。

当前阶段只做 PoC 骨架：

- C# 启动器读取配置、校验路径、启动游戏。
- C# 启动器将 `RuntimeHook.dll` 注入游戏进程。
- C++ DLL 被加载后写入日志。
- 文件读取 Hook 使用 MinHook 观察 `CreateFileW/CreateFileA`，只记录匹配扩展名的路径。
- 事件探针 v0 使用 `CreateFileW/CreateFileA/WriteFile` 和文件生命周期 API 观察文件打开、写入尝试、移动、复制、删除、替换和属性变更，只写日志，不修改任何游戏行为。

## 当前边界

第一阶段不做这些事：

- 不修改原版游戏文件。
- 不修改 Workshop Mod 文件。
- 不修改存档。
- 不 Hook 战斗、AI、技能结算或 UI 渲染。
- 不绕过 Steam、DRM、反作弊或系统安全机制。

## 默认目标

默认配置指向 Steam 版 64 位入口：

```text
E:/Steam/steamapps/common/DarkestDungeon/_windows/win64/Darkest.exe
```

如果要测试 32 位版本，需要同时编译 32 位启动器和 32 位 DLL。当前骨架优先支持 x64。

`gameArguments` 可用于传递游戏启动参数，例如测试时使用 `["-forcetown"]` 强制回到城镇。默认值为空数组。

## 目录结构

```text
config/default_config.json      默认配置
launcher/                       C# 启动器
runtime/                        C++ RuntimeHook.dll
runtime/hooks/                  Hook 模块接口
plugins/                        插件补丁清单目录
logs/                           启动器和 DLL 日志
state/                          框架 sidecar 状态目录，运行生成内容默认不进 git
docs/architecture.md            架构说明
```

## 构建要求

- .NET SDK 8.0 或更新版本，用于构建启动器。
- Visual Studio 2026 / Build Tools，安装 Desktop development with C++，用于构建 RuntimeHook.dll。

不需要 NuGet 包。当前工程使用 VS2026 `v145` 平台工具集。文件 IO 观察 Hook 固定使用 `third_party/minhook` 中的 MinHook `v1.3.4`。

## 文件 IO 观察配置

`config/default_config.json` 中的这些字段控制文件读取日志：

```json
"fileIoHookEnabled": true,
"fileIoObserveOnly": true,
"fileIoLogExtensions": [".darkest", ".loc", ".json", ".xml", ".png", ".atlas", ".skel", ".font", ".ttf", ".otf", ".shader", ".txt"],
"fileIoMaxLogEntries": 2000,
"fileIoDeduplicate": true
```

`fileIoHookEnabled` 是文件 API hook 的总开关。`fileIoObserveOnly` 只控制普通文件打开观察日志；即使它为 `false`，只要启用了事件探针或虚拟文件规则，RuntimeHook 仍会安装必要的文件 hook。需要完全关闭文件 IO Hook 时，把 `fileIoHookEnabled` 改成 `false`。如果需要完全关闭注入，把 `enableInjection` 改成 `false`。

默认启动器会使用 `startSuspendedForInjection: true`：先挂起启动游戏，注入并安装 Hook 后再恢复主线程。这样能观察游戏启动早期的资源读取。

## 事件探针 v0

事件探针是后续事件层的最低风险起点。当前只观察文件活动，不拦截、不取消、不改写写入：

```json
"eventProbeEnabled": true,
"eventProbeLogFileOpen": true,
"eventProbeLogFileWrite": true,
"eventProbeLogSaveFiles": true,
"eventProbeLogDataFiles": false,
"eventProbeLogAssetFiles": false,
"eventProbeMaxLogEntries": 5000,
"eventProbeMaxSaveLogEntries": 20000,
"eventProbeIgnorePathFragments": [
  "Steam/logs/",
  "gameoverlay_renderer.txt"
]
```

当前事件名：

- `data.file_opened`
- `data.file_write_attempted`
- `asset.file_opened`
- `asset.file_write_attempted`
- `save.file_opened`
- `save.file_write_attempted`
- `save.file_move_attempted`
- `save.file_copy_attempted`
- `save.file_delete_attempted`
- `save.file_replace_attempted`
- `save.file_set_attributes_attempted`

默认采样存档类事件，数据文件和资产事件默认关闭，避免启动期 Mod、localization、layout、贴图和 Steam overlay 日志把事件上限刷满。`eventProbeMaxLogEntries` 控制普通 `data` / `asset` 事件预算，`eventProbeMaxSaveLogEntries` 单独控制 `save` 事件预算，所以存档读写不会被普通文件噪声挤掉。`save` 分类是启发式识别：Steam userdata 下的 `262060/remote/profile_*`、Documents Darkest 下的 `profile_*`，或文件名类似 `persist.*` 时会归为存档类。

## 存档目录 sidecar watcher

真实游戏测试显示，`E:/Steam/userdata/.../262060/remote/profile_*` 中的部分存档落盘并不一定由 `Darkest.exe` 进程直接执行，因此注入游戏进程的 DLL 文件 API Hook 可能看不到这些写入。启动器侧 watcher 用于补足这条链路：

```json
"saveWatchEnabled": true,
"saveWatchDirectories": [],
"saveWatchAfterExitSeconds": 10,
"saveEventBridgeDebounceMilliseconds": 1000
```

`saveWatchDirectories` 为空时，启动器会从 `gameWorkingDirectory` 推断 Steam 根目录，自动监控现有的 `userdata/*/262060/remote`，并额外监控 `Documents/Darkest`（如果目录存在）。开启 watcher 后，启动器会等待游戏进程退出，并在退出后继续监听 `saveWatchAfterExitSeconds` 秒，用来捕获 Steam 或外部同步进程稍后写入的 `persist.*.json`、`backup` 等存档变化。

watcher 只记录日志，不修改存档。实时事件和退出快照差异会写入 `logs/launcher.log`，事件名以 `save.sidecar_*` 开头，例如：

- `save.sidecar_created`
- `save.sidecar_changed`
- `save.sidecar_deleted`
- `save.sidecar_renamed`
- `save.sidecar_snapshot_created`
- `save.sidecar_snapshot_changed`
- `save.sidecar_snapshot_deleted`

退出快照之后还会输出降噪摘要，按 `profile_*` 和稳定 `.json` 文件聚合，忽略 `.stmp`、`~RF*.TMP` 等中间文件：

- `save.sidecar_session_summary`
- `save.sidecar_profile_summary`
- `save.sidecar_profile_files`

例如一次城镇停留可能汇总成 `profile_3` 的 `persist.game.json`、`persist.narration.json` 和 `backup/persist.*.json` 更新，而不需要先从大量临时重命名事件里手动整理。

每次 watcher 会话还会写一个结构化报告：

```text
logs/save_sessions/<sessionId>.json
```

报告包含启动/结束时间、游戏进程信息、监控目录、事件计数、快照统计、按 profile 聚合的稳定 JSON 文件变化，以及 `activeProfile` 推断。`activeProfile` 只是一条带 `confidence` 和 `reasons` 的诊断提示：例如 `persist.game.json`、`persist.narration.json` 和大量 `backup/persist.*.json` 一起变化时，更像当前战役 profile；只有 `persist.circus_estate.json`、`persist.rankings.json` 等文件变化时，会降低战役 profile 置信度。框架不会因为这个推断去写存档或阻止启动。

如果存在 `activeProfile`，watcher 还会基于该 profile 写一个只读状态报告：

```text
logs/save_states/<sessionId>.json
```

DD1 的 `persist.*.json` 文件扩展名是 `.json`，但 Steam 存档里的实际内容是 DSON 二进制容器。状态报告不会假装已经完整反序列化这些文件；它会记录文件大小、时间戳、SHA-256、二进制头、DSON header/meta 摘要、可见 marker 字符串、少量短距离内联字符串候选 key/value、有限的 DSON scalar/object 路径样本，以及保守的 `facts` 摘要。报告的 `parseStatus` 会标明当前是 `dsonPartialDecoded`、`binaryStringIndexOnly` 还是普通 `parsedJsonText`。这给后续状态模型和二进制格式解析留出稳定契约。

同一次退出还会写一个只读文件地图报告：

```text
logs/save_file_maps/<sessionId>.json
```

文件地图会扫描 active profile 下 live/backup 的所有 `persist*.json`，标出是否属于当前核心候选、优先级、类别、mod 相关性、当前覆盖程度、DSON 摘要和访问问题。它用于决定后续解码顺序，不代表对应文件已经有完整语义模型。

## Decoded profile 工作区

真实 `profile_*` 仍然默认只读。需要验证 profile 初始化或 managed action 写入时，先把存档解码到项目内工作区：

```powershell
.\tools\PrepareDecodedProfileWorkspace.ps1
.\tools\PrepareDecodedProfileWorkspace.ps1 -Initialize
.\tools\PrepareDecodedProfileWorkspace.ps1 -Initialize -WriteManagedActions
```

默认源是测试档 `E:\Steam\userdata\1097809614\262060\remote\profile_3`。脚本只读取该目录下的 top-level `persist*.json`，使用 `.research\DDSaveEditor-v0.0.70\DDSaveEditor.jar` 解码到：

```text
state/decoded_profiles/<session>/decoded_save
state/decoded_profiles/<session>/mod_state
```

报告会同时写入工作区和 `logs/decoded_profile_workspaces/<session>.json`。`-Initialize` 会调用 `--initialize-decoded-profile`，默认仍是 dry-run；只有再加 `-WriteManagedActions` 才会写项目内 decoded JSON 副本。这个流程不会写回 Steam userdata 里的原始存档。

## 存档事件桥

存档事件桥把只读 save state facts 转换成框架事件，再交给普通 `eventRules` 执行。转换规则由启用插件的 `factEventRules` 声明，不在 C# 里写死某一种玩法。它不写原版 `profile_*`，也不直接修改游戏 UI、任务列表或战斗流程；当前只作为 observe-first 到 sidecar state 的桥接层。

```json
"saveEventBridgeEnabled": false
```

默认关闭。开启后，启动器 sidecar watcher 在生成 `logs/save_states/<sessionId>.json` 后，会尝试根据该报告推断事件，并写入：

```text
logs/save_event_bridge_report.json
```

当 watcher 在游戏运行中观察到 `profile_*` 下稳定 `.json` 存档变化时，也会按 `saveEventBridgeDebounceMilliseconds` 去抖后生成实时状态报告，并执行同一条桥接逻辑：

```text
logs/save_states/<watchSessionId>_realtime_<n>.json
```

实时桥接会跳过已知非战役/网络辅助文件，例如 `persist.circus_estate.json`、`persist.rankings.json`、`persist.mp_progression.json`、`persist.roster.network.json` 和 `novelty_tracker_mp.json`；未知 `.json` 仍保留触发资格，避免未来新存档文件或新玩法扩展被静默挡掉。

实时桥接仍然只读原版存档，只写框架自己的 sidecar state。游戏退出时保留原来的最终 session report / save state report，用于完整诊断和文件地图分析。

也可以手动对某个 save state report 执行一次推断：

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/rule_contract_validation_config.json --mod-state-id validation.challenge_run_contract --infer-save-events --save-state-report ./logs/save_states/<sessionId>.json --no-inject
```

真实游戏观察用专门配置运行，不写原版 `profile_*`，会先准备 challenge sidecar state、物化当前 stage 的 managed action overlay，再启动游戏并注入 RuntimeHook，同时开启 save watcher 和 save event bridge：

```powershell
.\tools\StartLiveChallengeObserve.ps1
```

兼容入口 `.\tools\StartChallengeSaveBridgeObserve.ps1` 仍然可用，它会转调新的 live observe 脚本。该脚本会为本次观察创建新的 sidecar state 目录，先初始化 `validation.challenge_run_contract`，发出 `challenge.run_started` 和 `challenge.stage_selection_started`，再启动游戏。进入 `profile_3` 后选择当前 stage 对应的 boss 关；存档变化会实时触发 save event bridge。退出游戏后查看：

```text
logs/save_sessions/<sessionId>.json
logs/save_states/<sessionId>.json
logs/save_event_bridge_report.json
state/live_challenge_observe/<sessionId>/validation.challenge_run_contract.json
```

`factEventRules` 可以读取 `fact.*`、插件 `state.*` 和桥接器上下文，并把字段写入事件 payload。payload 可以声明通用数组投影，例如从 `facts.heroes` 里筛出当前 raid 队伍成员，再展开这些英雄的 `trinketIds`，或用 `where` 从 `campaignLog.partyRaidRecords` 里筛出匹配关卡的完成记录。验证插件现在用这些规则从 active raid facts 或任务后的 campaign log facts 发出 `challenge.stage_selection_confirmed`，并从 last raid quest/result facts 发出 `challenge.stage_completed` 或 `challenge.stage_failed`。同一次 bridge pass 中，前一个事件写入 sidecar state 后，后续规则会重新读取 state，因此任务后存档可以先补推选人确认，再推进完成事件。真正的关卡注入、选人 UI 过滤、饰品 UI 过滤不在这个桥接器里硬编码；它们由普通 `eventRules` 声明，并先物化为 managed action artifact，再由 overlay/hook 层按能力逐步消费。

watcher 的实时桥接可以用不启动游戏的诊断脚本测试：

```powershell
.\tools\TestRealtimeSaveBridge.ps1
```

```json
{
  "factEventRules": [
    {
      "id": "emit_stage_completed_from_last_raid",
      "emit": "challenge.stage_completed",
      "requiresCapabilities": ["state.sidecar", "challenge.observe_stage_completed"],
      "when": {
        "all": [
          { "state": "challengeRun.lockedStageSelection", "op": "exists" },
          { "fact": "progression.lastRaidSuccess", "op": "equals", "value": true },
          {
            "fact": "progression.lastRaidQuest.names",
            "op": "contains",
            "valueFromState": "challengeRun.currentStage.sourceQuestId"
          }
        ]
      },
      "payload": {
        "stageId": { "fromState": "challengeRun.currentStage.id" },
        "observedQuestNames": { "fromFact": "progression.lastRaidQuest.names" },
        "saveStateReportPath": { "fromBridge": "saveStateReportPath" }
      }
    }
  ]
}
```

## 框架 Mod 状态存档

运行时 Mod 自己需要的状态不写进原版 `profile_*`。启动器会把插件 `stateSchema` 初始化到独立目录：

```json
"modStateDirectory": "./state/mod_state",
"allowNonAtomicStateWrites": false
```

相对路径会解析到框架项目根目录下，并且必须留在项目目录内，避免误写到游戏目录或 Steam userdata。生成的状态文件默认被 `.gitignore` 忽略。
状态写入默认要求 `.tmp` 原子替换成功；如果原子写失败，命令会失败并记录 `state-atomic-write-failed`，不会自动改用直接覆盖。只有显式配置 `allowNonAtomicStateWrites: true` 或传入 `--allow-non-atomic-state-writes` 时，才允许非原子直接写入，并且报告中会记录 `state-write-fallback-non-atomic` warning 和 `writeMode=non-atomic-fallback`。

状态命令：

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --init-mod-state --no-inject
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --dump-mod-state --no-inject
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --mod-state-id validation.challenge_run_contract --init-mod-state --dump-mod-state --no-inject
```

- `--init-mod-state`：按当前启用插件的 `stateSchema` 创建或合并默认键，不清空已有状态。
- `--dump-mod-state`：读取当前 sidecar 状态，输出摘要并写入 `logs/mod_state_dump_report.json`。
- `--mod-state-id <plugin-id>`：只处理指定插件的状态。
- `--mod-state-dir <path>`：本次运行临时改用另一个 sidecar 状态目录，路径仍必须位于框架项目目录内。
- `--allow-non-atomic-state-writes`：本次运行允许非原子状态写入，只用于受沙盒、杀软或权限策略限制的开发环境；正常环境应保持关闭。

单个插件默认写入 `state/mod_state/<plugin-id>.json`。如果多个启用插件重复同一 `id`，文件名会追加 manifest 路径哈希，避免互相覆盖。

## 事件规则执行器

`--emit-event` 可以在不启动游戏的情况下模拟一个框架事件，按当前插件加载顺序执行匹配的安全 `eventRules`，并把结果写回 sidecar state：

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/rule_contract_validation_config.json --mod-state-id validation.challenge_run_contract --emit-event challenge.stage_selection_confirmed --event-payload-file ./logs/runtime_event_executor_test/payloads/selection_confirmed.json --no-inject
```

当前执行器实现安全状态动作，例如 `state.addUniqueRange`、`state.incrementCounter`、`challenge.lockStageSelection`、`challenge.recordFailedAttempt`、`challenge.advanceStage` 和 `challenge.initializeRunState`。部分 `managed` 游戏行为动作会生成可审计 artifact，但仍不执行真实游戏修改：`quest.injectFixedStage`、`roster.filterAvailableHeroes` 和 `equipment.filterAvailableTrinkets` 会在 `logs/runtime_event_report.json` 中记录 `status: "materialized"`、`materializedActionCount`、`plan` 和 `artifactPath`，并把完整 artifact 写入 `modStateDirectory/_managed_actions/`。其他未实现 action 如果标成 `required:true`，本次事件仍会失败。已实现和已物化 action 的参数按严格模式处理：引用的 `event.xxx`、`state.xxx` 或 `challenge.xxx` 路径不存在、显式参数类型错误、定义文件路径错误，都会让 action 失败，而不是当作空值或默认值继续执行。

启动游戏或 `--dry-run` 前，启动器会把 `_managed_actions/` 下的可消费 artifact 编译成：

```text
logs/managed_action_overlay_manifest.json
```

当前第一版 overlay compiler 只消费 `quest.injectFixedStage`，并按 `kind + target + pluginId + sourcePath + ruleId + actionIndex` 只保留最新 artifact，避免旧 stage 残留影响运行时，同时不把重复插件 id 的不同 manifest 误合并。它会把当前 fixed stage 的 `sourceQuestId` 映射到 `campaign/quest/quest.plot_quests.json`，追加一条虚拟文件规则，把该原版 plot quest 的 `dungeon_level` 设为 `0`、`is_repeatable` 设为 `true`，作为第一版可验证的 quest/content overlay consumer。英雄和饰品过滤 artifact 仍会保留在 sidecar 中，但暂不进入 overlay manifest。RuntimeHook 会通过既有虚拟文件通道消费这条规则，并通过 `DD_RUNTIME_MANAGED_OVERLAY_MANIFEST` 在启动日志中记录 manifest 路径、大小和 overlay 数量。这还不是完整任务池/UI 接管。

## 虚拟文件原型

默认配置中虚拟文件通道是打开的，但没有启用规则时不会改变任何游戏读取结果：

```json
"virtualFileEnabled": true
```

如果需要全局关闭虚拟文件替换，把 `virtualFileEnabled` 改成 `false`。

规则列表格式：

```json
"virtualFileRules": [
  {
    "target": "shared/app.darkest",
    "replacements": [
      {
        "find": ".max_campaign_log_file_size 0 ",
        "replace": ".max_campaign_log_file_size 0"
      }
    ]
  }
]
```

`target` 使用相对路径后缀匹配；一条规则可以包含多条 `replacements`。测试配置 `config/virtual_file_test_config.json` 会把 `shared/app.darkest` 以内存虚拟文件返回，并只做一个无语义变化的字符串替换：去掉 `.max_campaign_log_file_size 0 ` 行末尾的空格。这个测试不写磁盘、不改原文件，只验证文件读取链路可以被替换。

## 插件补丁清单

启动器会扫描 `pluginDirectories` 里的一层插件目录，并读取每个插件目录下的 `patches.json`：

```json
"pluginDirectories": [
  "./plugins"
],
"pluginPatchManifestName": "patches.json"
```

插件清单格式：

```json
{
  "id": "author.my_runtime_patch",
  "name": "My Runtime Patch",
  "version": "0.1.0",
  "enabled": true,
  "capabilities": [
    "file.virtualize",
    "content.patch",
    "content.app_config"
  ],
  "phase": "normal",
  "priority": 0,
  "depends": [],
  "optionalDepends": [],
  "loadAfter": [],
  "loadBefore": [],
  "conflicts": [],
  "virtualFileRules": [
    {
      "when": {
        "modsPresent": [],
        "modsAbsent": [],
        "capabilitiesPresent": [],
        "capabilitiesAbsent": []
      },
      "target": "shared/app.darkest",
      "operations": [
        {
          "type": "setValue",
          "key": ".max_campaign_log_file_size",
          "value": "0"
        }
      ]
    }
  ]
}
```

玩家可以新建 `plugins/<plugin-id>/patches.json`，把 `enabled` 设为 `true` 后启动器会自动纳入加载计划。加载顺序先看 `depends`、`optionalDepends`、`loadAfter`、`loadBefore`，再看 `phase` 和 `priority`；同一个 `target` 被多个清单命中时，会按最终加载顺序逐步生成替换项后交给 DLL。`plugins/example/patches.json` 是默认关闭的示例。

加载关系规则：

- `depends`：必需依赖；缺失时跳过当前插件并记录 warning。
- `optionalDepends`：目标存在时排在它后面，不存在时忽略。
- `loadAfter` / `loadBefore`：只影响顺序，不要求目标必须存在。
- `phase` 顺序为 `base`、`early`、`normal`、`compat`、`late`。
- `priority` 数值小的先加载，默认 `0`。
- 重复 `id`、声明冲突和顺序循环默认只记录 warning，不直接阻止启动。

能力声明用于表达插件打算使用或提供的框架能力：

```json
"capabilities": [
  "file.virtualize",
  "content.patch",
  "content.quest",
  "content.region"
]
```

第一批建议命名：

- `file.virtualize`：通过 RuntimeHook 虚拟化文件读取。
- `content.patch`：修改游戏数据文本。
- `content.app_config`：修改 `shared/app.darkest` 这类应用配置。
- `content.quest`：任务、关卡或任务链内容。
- `content.region`：地区、地图或区域内容。
- `content.localization`：本地化文本。
- `asset.replace`：贴图、字体、骨骼、atlas 等资源替换。

`virtualFileRules` 支持两种写法：

- `replacements`：底层字符串替换，直接提供 `find` 和 `replace`。
- `operations`：启动前结构化操作，启动器会读取目标文件并编译成 `replacements`。

规则可以加 `when` 条件；条件不满足时，这条规则不会编译、验证、预览或传给 DLL，但会进入 `--explain-patches` 诊断：

```json
{
  "when": {
    "modsPresent": ["author.required_mod"],
    "modsAbsent": ["author.incompatible_mod"],
    "capabilitiesPresent": ["content.quest"],
    "capabilitiesAbsent": ["content.region"]
  },
  "target": "shared/app.darkest",
  "operations": []
}
```

- `modsPresent`：列出的插件 id 都在最终启用列表中时才生效。
- `modsAbsent`：列出的插件 id 都不在最终启用列表中时才生效。
- `capabilitiesPresent`：列出的能力都由最终启用插件声明时才生效。
- `capabilitiesAbsent`：列出的能力都没有被最终启用插件声明时才生效。

当前支持的 `operations`：

```json
{ "type": "setValue", "key": ".some_key", "value": "123" }
{ "type": "replaceLine", "match": "old full line", "line": "new full line" }
{ "type": "replaceLine", "prefix": ".some_key", "line": ".some_key 123" }
{ "type": "appendAfter", "match": "anchor line", "content": "new line" }
{ "type": "appendEnd", "content": "new line" }
```

结构化操作会基于当前虚拟文本逐步编译：前一个插件修改后的结果，可以被后一个插件的 `operations` 继续匹配。找不到目标行或替换文本时默认记录 warning 并跳过/无效，不阻止启动；路径越界、目标文件无法读取、类型写错等框架无法安全执行的问题仍会作为 error。旧 `replacements` 也会参与顺序模拟。

每条结构化操作会生成一个诊断 `subject`，用于解释和冲突报告：

- `setValue` 使用 `key:<key>`。
- `replaceLine` / `appendAfter` 会优先从 `key`、`.darkest` 风格 `prefix`、`match` 或 `line` 中提取 `key:<key>`。
- 不能识别 key 时，会退回到 `match:<text>`、`prefix:<text>` 或操作类型。

`--preview-patches` 除了同一行冲突外，还会报告同一 key 被多条替换命中的 `patch-preview-key-conflict`。

补丁检查命令：

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --list-patches
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --explain-patches
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --validate-only
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --validate-patches
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --preview-patches
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --validate-only --strict-patches
```

- `--list-patches`：列出发现的清单、加载顺序、启用状态、源规则和最终有效规则，不启动游戏。
- `--explain-patches`：解释加载顺序、排序边、跳过原因、能力声明、条件规则诊断、每个 target 的来源链路和最终替换来源；替换来源会包含 operation subject，不启动游戏。
- `--validate-only`：验证启用规则的 `target` 是否存在、目标文件是否超过当前 16MB 虚拟文件限制，并按最终替换顺序统计每条 `find` 命中次数和 operation subject，不启动游戏。
- `--validate-patches`：启动前执行同样的验证；如果有错误，直接退出并返回失败码；验证通过后继续正常启动。
- `--preview-patches`：按 RuntimeHook 的替换顺序模拟虚拟文件结果，写入 `logs/patch_preview`，不启动游戏。
- `--strict-patches`：把补丁编译 warning 和替换未命中的验证 warning 升级为失败；同目标多规则提示仍保留为诊断 warning。默认不启用。

如果要指定预览目录：

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --preview-patches --preview-output ./logs/my_preview
```

预览目录会生成：

- `summary.txt`：目标文件、原始大小、虚拟大小、替换次数。
- `<target>.preview.txt`：游戏会读到的虚拟文本。
- `<target>.diff.txt`：每条替换的简短差异、来源插件和 operation subject。

`--preview-output` 必须位于框架项目目录内，避免误写游戏目录或 Workshop 目录。

运行测试配置：

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/virtual_file_test_config.json
```

运行插件清单测试配置：

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/plugin_patch_test_config.json
```

## 预期运行流程

```text
1. 构建 runtime/RuntimeHook.vcxproj，生成 runtime/bin/x64/Release/RuntimeHook.dll
2. 构建 launcher/DDRuntimeLoader.csproj
3. 从工程根目录运行启动器
4. 查看 logs/launcher.log 和 logs/runtime_hook.log
```

## 回退方式

这个框架不改游戏目录。想回退时直接关闭启动器，用 Steam 原方式启动游戏即可。
