# Plugins

这里放运行时插件补丁清单。

当前已经支持的最小格式：

```text
plugins/<plugin-id>/patches.json
```

`patches.json` 目前支持插件元数据、可执行的 `virtualFileRules`、安全 `eventRules`、从存档 facts 推导事件的 `factEventRules`，以及独立 sidecar `stateSchema`。虚拟文件规则内可以写底层 `replacements`，也可以写启动前编译的结构化 `operations`。启动器会先计算插件加载顺序，再按顺序逐步生成最终虚拟文件规则，最后通过环境变量交给 RuntimeHook.dll。

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

第一批能力命名：

- `file.virtualize`
- `content.patch`
- `content.app_config`
- `content.quest`
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
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/rule_contract_validation_config.json --init-mod-state --dump-mod-state --no-inject
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/rule_contract_validation_config.json --mod-state-id validation.challenge_run_contract --emit-event challenge.stage_completed --event-payload-file ./payload.json --no-inject
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --config config/rule_contract_validation_config.json --mod-state-id validation.challenge_run_contract --infer-save-events --save-state-report ./logs/save_states/<sessionId>.json --no-inject
```

`--explain-patches` 会输出：

- 每个插件的最终 `order`、`status`、`phase`、`priority`、`capabilities` 和跳过原因。
- 每条排序边，例如 `mod.a -> mod.b reason=depends`。
- 重复 id、缺依赖、声明冲突和顺序循环等加载诊断。
- 每个 `target` 被哪些插件规则修改、哪些规则因 `when` 跳过，以及最终替换来源。
- 每条替换的 operation subject，例如 `key:.max_campaign_log_file_size`。

`--preview-patches` 会在 diff 中输出 operation subject，并在同一 `.darkest` key 被多个插件修改时记录 `patch-preview-key-conflict`。

`--explain-rules` 会输出声明型 `eventRules` 和 `factEventRules` 的事件、所需 capability、action capability、风险等级和跳过原因。

`--init-mod-state` 会把启用插件的 `stateSchema` 默认值写到 `state/mod_state/<plugin-id>.json`。已有文件只补缺失键，不重置已有状态。`--dump-mod-state` 会读取这些 sidecar 状态并写入 `logs/mod_state_dump_report.json`。

`--emit-event` 会执行匹配事件的安全规则动作，并把执行结果写入 `logs/runtime_event_report.json`。当前 sidecar state 和 challenge state 相关安全动作会真实写入 sidecar state；`quest.injectFixedStage`、`roster.filterAvailableHeroes`、`equipment.filterAvailableTrinkets` 和 boss gauntlet profile-normalization 动作会生成 `materialized` artifact，写入 `modStateDirectory/_managed_actions/`，不改游戏或原版存档。启动游戏或 `--dry-run` 前，启动器会把可消费的 `quest.injectFixedStage` artifact 编译进 `logs/managed_action_overlay_manifest.json`，并为 `campaign/quest/quest.plot_quests.json` 追加一条虚拟文件替换，把当前 stage 的源 plot quest 设置为 `dungeon_level: 0` 和 `is_repeatable: true`；其他 managed artifact 当前仍只保留在 sidecar，等待对应 runtime/UI consumer。未物化的托管改游戏行为仍会报告未实现。

`--infer-save-events` 会读取 save state report，按启用插件的 `factEventRules` 推导事件并写入 `logs/save_event_bridge_report.json`。桥接器不写原版存档，只把事实观察转成普通框架事件。

`example/patches.json` 默认 `enabled:false`，可以复制成自己的插件后再启用。

后续再考虑 native C ABI、Lua 或 C# 脚本层。
