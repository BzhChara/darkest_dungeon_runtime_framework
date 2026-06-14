# Plugins

这里放运行时插件补丁清单。

当前已经支持的最小格式：

```text
plugins/<plugin-id>/patches.json
```

`patches.json` 目前支持插件元数据、可执行的 `virtualFileRules`、固定 `.dm` 地图模板 `mapTemplates`、高层地图布局 `mapLayoutTemplates`、关卡/章节顺序 `questChains`、任务板调度声明 `questBoardPolicies`、安全 `eventRules`、从存档 facts 推导事件的 `factEventRules`，以及独立 sidecar `stateSchema`。虚拟文件规则内可以写底层 `replacements`，也可以写启动前编译的结构化 `operations`。`mapTemplates` 会在启动前生成项目内 `.dm` artifact，再自动变成 `sourcePath` 虚拟文件规则。`mapLayoutTemplates` 会先校验高层房间/走廊图，再编译成受限低层 `mapTemplates` spec，最后同样生成 `.dm` artifact 和 `sourcePath` 虚拟文件规则。`questChains` 会校验固定顺序 stage、解锁条件和地图模板引用，并写出 sidecar 验证报告；`questBoard.mode="replaceWithFixedSet"` 会生成确定路径的 `questBoard.replaceWithFixedSet` managed action artifact，`questBoard.mode="linearProgression"` 会展开成 `questBoardPolicies` 策略事实。`questBoardPolicies` 是候选解析和物化 primitive：校验任务板可用性条件、刷新触发和完成后处理，写出 sidecar 策略事实，可通过 `--preview-quest-board-policies` 解析启用 plot quest 内容，通过 `--resolve-quest-board-policies --save-state-report <path>` 根据周数、完成任务和 sidecar 状态解析 eligible quest ids，也可通过 `--materialize-quest-board-policies` 显式生成同形状的 `questBoard.replaceWithFixedSet` managed action artifact；配置 `questBoardPolicyAutoMaterializeEnabled=true` 后，save-event bridge 还能在读取 save facts 时自动生成最新 artifact。带 `activeProfile` 的 save report 会让 artifact 带 `profileScope`，preview/刷新只会消费 global 或匹配 profile 的任务板 artifact，避免不同存档互相覆盖。它本身仍不直接写 `persist.quest.json` 或模拟周结算。后续复杂插件不应把所有内容都堆进 `patches.json`；`patches.json` 应作为入口索引，引用 quests/maps/encounters/spawn_pools/contentRefs 等领域文件。启动器会先计算插件加载顺序，再按顺序逐步生成最终虚拟文件规则，最后通过环境变量交给 RuntimeHook.dll。

`eventRules` 可通过 `--emit-event` 执行已实现的安全动作，或为已识别的 managed 动作生成 sidecar artifact。`factEventRules` 可通过 `--infer-save-events` 把 save state report 中的 facts 转成普通框架事件，再交给 `eventRules`；payload 支持有限的通用数组投影，例如 `where` 条件过滤、`whereIn` 成员过滤、展开、字符串化和去重。契约细节见 `docs/capability_rule_contract.md`。

清单字段：

```json
{
  "id": "author.mod_id",
  "name": "Readable Mod Name",
  "version": "0.1.0",
  "enabled": true,
  "capabilities": [
    "file.virtualize",
    "content.patch"
  ],
  "phase": "normal",
  "priority": 0,
  "depends": [],
  "optionalDepends": [],
  "loadAfter": [],
  "loadBefore": [],
  "conflicts": [],
  "modules": {
    "contentRefs": ["content/refs.json"],
    "questChains": ["quests/*.chain.json"],
    "mapLayouts": ["maps/*.layout.json"],
    "encounters": ["encounters/*.json"],
    "spawnPools": ["spawn_pools/*.json"]
  },
  "virtualFileRules": [
    {
      "when": {
        "modsPresent": [],
        "modsAbsent": [],
        "capabilitiesPresent": [],
        "capabilitiesAbsent": []
      },
      "target": "shared/app.darkest",
      "operations": []
    }
  ],
  "mapTemplates": [
    {
      "id": "dd4_custom_finale",
      "target": "maps/DD_map4.dm",
      "source": "maps/DD_map4.dm",
      "specPath": "maps/dd4_custom_finale.spec.json"
    }
  ],
  "mapLayoutTemplates": [
    {
      "id": "dd4_layout_probe",
      "target": "maps/DD_map4.dm",
      "source": "maps/DD_map4.dm",
      "layout": {
        "entrance": "start",
        "finalRoom": "boss",
        "rooms": [
          { "id": "start", "templateAreaId": "rooA", "position": [1, 2] },
          { "id": "boss", "templateAreaId": "rooC", "position": [20, 2] }
        ],
        "corridors": [
          { "id": "main_path", "templateAreaId": "corA", "route": [[2, 2], [3, 2]] }
        ],
        "links": [
          { "from": "start", "to": "main_path", "tile": 0 },
          { "from": "main_path", "to": "boss", "tile": 27 }
        ]
      },
      "tiles": [
        { "area": "boss", "tile": 0, "content": 8, "knowledge": 1, "critScout": true }
      ],
      "encounters": []
    }
  ],
  "questChains": [
    {
      "id": "post_ancestor_probe_chain",
      "name": "Post Ancestor Probe Chain",
      "mode": "fixed_order",
      "unlock": {
        "type": "afterQuest",
        "questId": "plot_final_boss"
      },
      "questBoard": {
        "enabled": true,
        "mode": "replaceWithFixedSet",
        "questIdSource": "sourceQuestId",
        "removeCompleted": false
      },
      "stages": [
        {
          "id": "stage_01_layout_probe",
          "name": "Layout Probe",
          "order": 0,
          "sourceQuestId": "plot_dd_4",
          "targetQuestId": "probe_stage_01",
          "mapLayoutTemplateId": "dd4_layout_probe",
          "region": "darkestdungeon",
          "difficulty": 6,
          "tags": ["boss", "post_ancestor"]
        }
      ]
    }
  ],
  "questBoardPolicies": [
    {
      "id": "post_ancestor_board_policy",
      "name": "Post Ancestor Board Policy",
      "mode": "mixed",
      "refreshTriggers": ["onProfileInitialize", "onWeekAdvance", "immediateOnQuestComplete"],
      "entries": [
        {
          "id": "stage_01_after_final_boss",
          "questId": "plot_dd_4",
          "availableWhen": {
            "completedQuest": "plot_final_boss",
            "weekGte": 5
          },
          "onCompleted": "remove"
        }
      ]
    }
  ],
  "factEventRules": [],
  "eventRules": [],
  "stateSchema": {}
}
```

加载关系：

- `depends`：必需依赖；缺失时跳过当前插件并记录 warning，不阻止其他插件。
- `optionalDepends`：目标存在时排在它后面，不存在时忽略。
- `loadAfter` / `loadBefore`：只影响顺序，不表示依赖必须存在。
- `phase` 顺序为 `base`、`early`、`normal`、`compat`、`late`。
- `priority` 数值小的先加载，默认 `0`。
- 重复 `id` 和 `conflicts` 默认只记录 warning；不会直接阻止启动。

规则级条件：

- `when.modsPresent`：所有列出的插件 id 都启用且未被跳过时，规则才生效。
- `when.modsAbsent`：所有列出的插件 id 都未启用或已被跳过时，规则才生效。
- `when.capabilitiesPresent`：所有列出的能力都由最终启用插件声明时，规则才生效。
- `when.capabilitiesAbsent`：所有列出的能力都未被最终启用插件声明时，规则才生效。
- 条件不满足的规则只会出现在 explain 诊断里，不参与编译、验证、预览或运行时替换。

内容引用边界：

- 框架不需要默认实现完整的新怪物、新技能、动画、贴图、音频、语言、普通 curio 或 loot authoring 工具。原版、DLC、创意工坊 mod 或插件自带文件可以提供这些静态内容。
- 框架应该提供的是 `contentRefs`、依赖声明、存在性校验、加载来源报告，以及在 `encounters`、`spawnPools`、`questChains`、`mapLayoutTemplates` 中引用这些内容的能力。
- 例如新怪物可以来自创意工坊；框架只需要在遭遇表中引用 `monsterId`，并在缺失时报告 required dependency，而不是复制一套怪物制作器。
- 详细边界见 `docs/content_reference_boundaries.md`。

推荐的复杂插件组织：

```text
plugins/author.mod_id/
  patches.json
  content/refs.json
  quests/*.chain.json
  maps/*.layout.json
  encounters/*.json
  spawn_pools/*.json
  loot/*.json
  localization/*.json
  assets/...
```

注意：`modules.contentRefs`、`encounters`、`spawnPools`、`lootPolicies` 等是推荐方向，当前尚未全部实现为一等 schema。新增能力时应优先保持这种分层，而不是继续扩张单个 `patches.json`。

固定地图模板：

- `mapTemplates` 和 `mapLayoutTemplates` 是 optional/experimental 的固定 `.dm` overlay 与拓扑诊断能力，不是默认的自定义地图制作路线。普通随机地图、区域资源、遭遇池和完整固定图优先通过原版 DD/Workshop/plugin 内容文件提供，再由框架通过 `contentRefs`、任务板和虚拟文件层引用或调度。
- `mapTemplates[].target` 是游戏内虚拟目标路径，例如 `maps/DD_map4.dm`。
- `mapTemplates[].source` 是要复制修改的模板 `.dm`，相对路径优先按游戏目录解析；不存在时再按当前插件目录解析；省略时默认等于 `target`。
- `mapTemplates[].specPath` 是模板改写 spec，相对路径按当前插件目录解析。
- 也可以用 `mapTemplates[].spec` 内联 spec；`specPath` 和 `spec` 必须二选一。
- 生成文件写入 `modStateDirectory/_map_templates/<plugin-id>/`，并自动加入最终 `sourcePath` overlay。
- `mapTemplates[].when` 使用和 `virtualFileRules[].when` 相同的条件规则。
- 当前只支持修改已存在的 `.dm` 标量字段，不能创建/删除 area、tile 或 door 对象。

高层地图布局：

- `mapLayoutTemplates[].target` 和 `source` 使用与 `mapTemplates` 相同的路径解析规则。
- `layout.rooms[].templateAreaId` 和 `layout.corridors[].templateAreaId` 指向源 `.dm` 中已经存在的 area。
- `layout.entrance`、`layout.finalRoom`、`layout.links` 会被校验为一张可从入口走到最终房间的图。
- `tiles[]` 可写入已支持的动态 tile 字段：`content`、`light`、`knowledge`、`mashIndex`、`mashType`、`curioPropHash`、`trapHash`、`critScout`。
- `content` 可使用数字或数字字符串；当前只额外支持 `empty`/`none` 作为 `0`，其他符号名不会猜测。
- 生成的报告、编译 spec、`.dm` artifact 和低层模板报告写入 `modStateDirectory/_map_layout_templates/<plugin-id>/`。
- 报告里的 `compileReady=true` 表示已通过受限编译并生成 runtime overlay；遇到创建/删除 area、tile、door，或命名 encounter 物化时仍会失败。

关卡链：

- `questChains[]` 描述固定顺序或阶段式 quest/chapter chain，可用于 boss gauntlet、打完老祖后的新章节、或其他自定义关卡流程。
- `questChains[].unlock.type="afterQuest"` 时必须提供 `unlock.questId`，表示该 chain 预期在某个 plot quest 完成后开放。
- `stages[].order` 可显式控制顺序；不写时按数组顺序。重复 order 或重复 stage id 会作为编译错误报告。
- `stages[].sourceQuestId` 是当前可实现切片的原版 quest 模板来源。后续真正自定义 quest writer 成熟后，可以扩展为非原版来源。
- 每个 stage 可以引用 `mapLayoutTemplateId` 或 `mapTemplateId`，但不能同时引用。引用不存在会作为编译错误报告。
- `questBoard.enabled=true` 是显式 opt-in：`mode="replaceWithFixedSet"` 会把按 stage 顺序得到的原版 plot quest id 写成静态 `questBoard.replaceWithFixedSet` managed artifact；`mode="linearProgression"` 会把 stage 顺序展开成 `questBoardPolicies`，让 A -> B -> C 这类长链不用手写重复前置条件。两种模式当前都要求 `questIdSource="sourceQuestId"`。
- 静态 fixed-set artifact 会在启动和 `--dry-run` 时把 active plot quest 同步编译成 `campaign/quest/quest.plot_quests.json` 内容 overlay，将这些 quest 设为早期可用、可重复。线性 progression 需要 policy materializer 根据 save facts 和 sidecar state 生成当前阶段的同形状 artifact，再交给同一套 consumer。
- 验证报告写入 `modStateDirectory/_quest_chains/<plugin-id>/`；quest-board materialization 报告也写在同目录。只有静态 fixed-set 模式会直接写入 `modStateDirectory/_managed_actions/`，linear progression 模式先写策略报告，等策略物化后再生成当前阶段 artifact。这些文件本身不修改原版存档或 UI。

任务板策略：

- `questBoardPolicies[]` 描述任务何时可以出现在任务板，而不是直接定义任务、怪物、地图或美术资源。底层任务内容应优先来自原版、DLC、创意工坊或插件自带 DD 格式文件，并通过 `contentRefs.quests` 声明。
- `mode` 当前支持 `fixed`、`random`、`mixed`；`refreshTriggers` 当前支持 `onProfileInitialize`、`onWeekAdvance`、`immediateOnQuestComplete`、`manual`。
- `entries[].availableWhen` 可声明 `completedQuest(s)`、`notCompletedQuest(s)`、`weekGte`、`weekLte`、`weekEq`、`phase`、`stateKey/stateEquals`。
- `entries[].onCompleted` 当前支持 `keep`、`remove`、`replace`、`advancePhase`。不写时按 `keep` 记录到报告。
- 当前实现做 schema 校验、日志解释、sidecar report、内容候选预览、facts-driven 候选解析和显式任务板 artifact 物化，写入 `modStateDirectory/_quest_board_policies/<plugin-id>/`、`logs/quest_board_policy_preview_report.json`、`logs/quest_board_policy_resolve_report.json`、`logs/quest_board_policy_materialize_report.json`，以及 `modStateDirectory/_managed_actions/*_questBoardPolicies_questBoard.replaceWithFixedSet.json`；不会直接修改 `persist.quest.json` 或模拟周结算。
- `--materialize-quest-board-policies` 会复用 resolve 结果，按加载顺序选择 fixed candidate，对 pool/weighted candidate 做可复现抽选，并支持 `--quest-board-policy-slots <n>` 与 `--quest-board-policy-seed <int>`。输出 artifact 继续交给现有 `questBoard.replaceWithFixedSet` consumer 处理。
- `questBoardPolicyAutoMaterializeEnabled=true` 会让 `SaveEventBridge` 在读取 save-state report 后自动执行同一套物化逻辑，并把状态写入 `logs/save_event_bridge_report.json` 的 `questBoardPolicyMaterialization`。如果 save-state report 暴露 `activeProfile.profile`，生成的 artifact 会带 `profileScope`；`--preview-quest-board --quest-board-profile-scope <profileId>`、`--refresh-quest-board-profile <profileId>` 和实时 watcher 只消费 global 或匹配 profile 的 artifact。配合 `questBoardAutoRefreshEnabled=true` 时，实时 watcher 可在原版写入 live `persist.quest.json` 后先生成最新 policy artifact，再走既有 fixed-board refresh writer。
- 详细 schema 见 `docs/quest_board_policies.md`。

第一批能力命名：

- `file.virtualize`
- `content.patch`
- `content.app_config`
- `content.quest`
- `quest.chain.define`
- `quest_board.policy`
- `content.region`
- `content.localization`
- `asset.replace`
- `state.sidecar`
- `campaign.observe_week_advance`
- `quest.observe_completion`
- `save.observe_write`

诊断命令：

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --explain-patches
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/rule_contract_validation_config.json --explain-rules --no-inject
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/rule_contract_validation_config.json --preview-quest-board-policies --no-inject
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/rule_contract_validation_config.json --resolve-quest-board-policies --save-state-report ./logs/quest_board_policy_contract_test/policy_week_6_necromancer_completed.json --no-inject
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/rule_contract_validation_config.json --init-mod-state --dump-mod-state --no-inject
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/rule_contract_validation_config.json --mod-state-id validation.challenge_run_contract --emit-event challenge.stage_completed --event-payload-file ./payload.json --no-inject
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/rule_contract_validation_config.json --mod-state-id validation.challenge_run_contract --infer-save-events --save-state-report ./logs/save_states/<sessionId>.json --no-inject
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --preview-managed-action-retention --managed-action-retention-keep 5 --no-inject
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --prune-managed-actions --managed-action-retention-keep 5 --no-inject
```

`--explain-patches` 会输出：

- 每个插件的最终 `order`、`status`、`phase`、`priority`、`capabilities` 和跳过原因。
- 每个插件声明的 `virtualRules`、`mapTemplates` 和 `mapLayoutTemplates` 数量。
- 每个插件声明的 `questBoardPolicies` 数量，以及启用策略的 mode、refresh trigger、entry 和 availableWhen 摘要。
- 每条排序边，例如 `mod.a -> mod.b reason=depends`。
- 重复 id、缺依赖、声明冲突和顺序循环等加载诊断。
- 每个 `target` 被哪些插件规则修改、哪些规则因 `when` 跳过，以及最终替换来源。
- 每条替换的 operation subject，例如 `key:.max_campaign_log_file_size`。

`--preview-patches` 会在 diff 中输出 operation subject，并在同一 `.darkest` key 被多个插件修改时记录 `patch-preview-key-conflict`。

`--explain-rules` 会输出声明型 `eventRules` 和 `factEventRules` 的事件、所需 capability、action capability、风险等级和跳过原因。

`--init-mod-state` 会把启用插件的 `stateSchema` 默认值写到 `state/mod_state/<plugin-id>.json`。已有文件只补缺失键，不重置已有状态。`--dump-mod-state` 会读取这些 sidecar 状态并写入 `logs/mod_state_dump_report.json`。

`--emit-event` 会执行匹配事件的安全规则动作，并把执行结果写入 `logs/runtime_event_report.json`。当前 sidecar state 和 challenge state 相关安全动作会真实写入 sidecar state；`quest.injectFixedStage`、`roster.filterAvailableHeroes`、`equipment.filterAvailableTrinkets` 和 boss gauntlet profile-normalization 动作会生成 `materialized` artifact，写入 `modStateDirectory/_managed_actions/`，不改游戏或原版存档。启动游戏或 `--dry-run` 前，启动器会把可消费的 `quest.injectFixedStage` 与 `questBoard.replaceWithFixedSet` artifact 编译进 `logs/managed_action_overlay_manifest.json`，并为对应 plot quest 源文件追加虚拟替换，把源 plot quest 设置为 `dungeon_level: 0` 和 `is_repeatable: true`；`inventory.disableItemSale` artifact 也会被编译成官方 campaign trinket entry 的 `sourcePath` 覆盖，把 trinket `price` 置 0。`--refresh-quest-board-profile <profileId>` 会复用固定任务板 runtime replacement，对配置的 watched profile 进行显式任务板刷新：可配合 `--dry-run` 预览，真实写入前会备份，并默认拒绝在真实游戏进程运行时写外部存档。配置 `questBoardAutoRefreshEnabled` 后，实时 save watcher 还能在原版写入 live `persist.quest.json` 后走同一个刷新 writer；真实运行中写外部存档需要同时配置 `questBoardAutoRefreshAllowRunningGameSaveWrite=true`。`--apply-managed-actions` 可对项目内 decoded JSON 存档副本 dry-run 这些 artifact，显式 `--write-managed-actions` 时当前可写入钱包资源、trinket inventory counts、roster class instances、roster progression、roster hero skill lists、content-defined upgrade purchases、stagecoach generated recruit suppression、district built flags、campaign plot progress reset、town-event current-event suppression，以及 `_ddrt_profile_policy.json` 中的 trinket-sale 和 town-event message policy；trinket 价格压制已有内容层 consumer，但是否完全禁止 UI 点击卖出还需要实机验证，town-event message policy 仍等待 runtime/UI/content consumer。`--initialize-decoded-profile` 会把 managed apply 的 action/file 明细内联到初始化报告里。未物化的托管改游戏行为仍会报告未实现。

`--preview-managed-action-retention` 会扫描 `modStateDirectory/_managed_actions/` 并写入 `logs/managed_action_retention_report.json`，只报告每组 artifact 中哪些超过保留数量，不删除文件。`--prune-managed-actions` 才会执行删除；分组键包含 action type、plugin id、rule id、action index、target、profileScope 和 sourcePath，所以不同 profile 或不同来源的 artifact 不会互相清理。无法解析的 artifact 会保留并记录 warning，删除失败会记录 error 且命令失败；不会用静默兜底掩盖底层文件系统问题。默认保留数量来自 `managedActionRetentionKeepLatestPerGroup`，也可用 `--managed-action-retention-keep <n>` 覆盖。

`--infer-save-events` 会读取 save state report，按启用插件的 `factEventRules` 推导事件并写入 `logs/save_event_bridge_report.json`。桥接器不写原版存档，只把事实观察转成普通框架事件。

`example/patches.json` 默认 `enabled:false`，可以复制成自己的插件后再启用。

后续再考虑 native C ABI、Lua 或 C# 脚本层。
