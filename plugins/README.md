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
  "phase": "normal",
  "priority": 0,
  "depends": [],
  "optionalDepends": [],
  "loadAfter": [],
  "loadBefore": [],
  "conflicts": [],
  "virtualFileRules": []
}
```

加载关系：

- `depends`：必需依赖；缺失时跳过当前插件并记录 warning，不阻止其他插件。
- `optionalDepends`：目标存在时排在它后面，不存在时忽略。
- `loadAfter` / `loadBefore`：只影响顺序，不表示依赖必须存在。
- `phase` 顺序为 `base`、`early`、`normal`、`compat`、`late`。
- `priority` 数值小的先加载，默认 `0`。
- 重复 `id` 和 `conflicts` 默认只记录 warning；不会直接阻止启动。

诊断命令：

```text
dotnet run --project launcher/DDRuntimeLoader.csproj -c Release --no-build -- --explain-patches
```

`--explain-patches` 会输出：

- 每个插件的最终 `order`、`status`、`phase`、`priority` 和跳过原因。
- 每条排序边，例如 `mod.a -> mod.b reason=depends`。
- 重复 id、缺依赖、声明冲突和顺序循环等加载诊断。
- 每个 `target` 被哪些插件规则修改，以及最终替换来源。

`example/patches.json` 默认 `enabled:false`，可以复制成自己的插件后再启用。

后续再考虑 native C ABI、Lua 或 C# 脚本层。
