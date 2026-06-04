# Plugins

这里放运行时插件补丁清单。

当前已经支持的最小格式：

```text
plugins/<plugin-id>/patches.json
```

`patches.json` 目前支持插件元数据和 `virtualFileRules`。规则内可以写底层 `replacements`，也可以写启动前编译的结构化 `operations`。启动器会先计算插件加载顺序，再按顺序逐步生成最终虚拟文件规则，最后通过环境变量交给 RuntimeHook.dll。

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
  ]
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

诊断命令：

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --explain-patches
```

`--explain-patches` 会输出：

- 每个插件的最终 `order`、`status`、`phase`、`priority`、`capabilities` 和跳过原因。
- 每条排序边，例如 `mod.a -> mod.b reason=depends`。
- 重复 id、缺依赖、声明冲突和顺序循环等加载诊断。
- 每个 `target` 被哪些插件规则修改、哪些规则因 `when` 跳过，以及最终替换来源。
- 每条替换的 operation subject，例如 `key:.max_campaign_log_file_size`。

`--preview-patches` 会在 diff 中输出 operation subject，并在同一 `.darkest` key 被多个插件修改时记录 `patch-preview-key-conflict`。

`example/patches.json` 默认 `enabled:false`，可以复制成自己的插件后再启用。

后续再考虑 native C ABI、Lua 或 C# 脚本层。
