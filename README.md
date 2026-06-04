# Darkest Dungeon Runtime Framework

这是一个面向《Darkest Dungeon 1》Steam Windows 版的运行时 Mod Loader / Hook 框架原型。

当前阶段只做 PoC 骨架：

- C# 启动器读取配置、校验路径、启动游戏。
- C# 启动器将 `RuntimeHook.dll` 注入游戏进程。
- C++ DLL 被加载后写入日志。
- 文件读取 Hook 使用 MinHook 观察 `CreateFileW/CreateFileA`，只记录匹配扩展名的路径，默认不修改任何游戏行为。

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

## 目录结构

```text
config/default_config.json      默认配置
launcher/                       C# 启动器
runtime/                        C++ RuntimeHook.dll
runtime/hooks/                  Hook 模块接口
plugins/                        插件补丁清单目录
logs/                           启动器和 DLL 日志
docs/architecture.md            架构说明
```

## 构建要求

- .NET SDK 8.0 或更新版本，用于构建启动器。
- Visual Studio 2026 / Build Tools，安装 Desktop development with C++，用于构建 RuntimeHook.dll。

不需要 NuGet 包。当前工程使用 VS2026 `v145` 平台工具集。文件 IO 观察 Hook 固定使用 `third_party/minhook` 中的 MinHook `v1.3.4`。

## 文件 IO 观察配置

`config/default_config.json` 中的这些字段控制文件读取日志：

```json
"fileIoObserveOnly": true,
"fileIoLogExtensions": [".darkest", ".loc", ".json", ".xml", ".png", ".atlas", ".skel", ".font", ".ttf", ".otf", ".shader", ".txt"],
"fileIoMaxLogEntries": 2000,
"fileIoDeduplicate": true
```

如果需要完全关闭文件 IO Hook，把 `fileIoObserveOnly` 改成 `false`。如果需要完全关闭注入，把 `enableInjection` 改成 `false`。

默认启动器会使用 `startSuspendedForInjection: true`：先挂起启动游戏，注入并安装 Hook 后再恢复主线程。这样能观察游戏启动早期的资源读取。

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
        "modsAbsent": []
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

`virtualFileRules` 支持两种写法：

- `replacements`：底层字符串替换，直接提供 `find` 和 `replace`。
- `operations`：启动前结构化操作，启动器会读取目标文件并编译成 `replacements`。

规则可以加 `when` 条件；条件不满足时，这条规则不会编译、验证、预览或传给 DLL，但会进入 `--explain-patches` 诊断：

```json
{
  "when": {
    "modsPresent": ["author.required_mod"],
    "modsAbsent": ["author.incompatible_mod"]
  },
  "target": "shared/app.darkest",
  "operations": []
}
```

- `modsPresent`：列出的插件 id 都在最终启用列表中时才生效。
- `modsAbsent`：列出的插件 id 都不在最终启用列表中时才生效。

当前支持的 `operations`：

```json
{ "type": "setValue", "key": ".some_key", "value": "123" }
{ "type": "replaceLine", "match": "old full line", "line": "new full line" }
{ "type": "replaceLine", "prefix": ".some_key", "line": ".some_key 123" }
{ "type": "appendAfter", "match": "anchor line", "content": "new line" }
{ "type": "appendEnd", "content": "new line" }
```

结构化操作会基于当前虚拟文本逐步编译：前一个插件修改后的结果，可以被后一个插件的 `operations` 继续匹配。找不到目标行或替换文本时默认记录 warning 并跳过/无效，不阻止启动；路径越界、目标文件无法读取、类型写错等框架无法安全执行的问题仍会作为 error。旧 `replacements` 也会参与顺序模拟。

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
- `--explain-patches`：解释加载顺序、排序边、跳过原因、条件规则诊断、每个 target 的来源链路和最终替换来源，不启动游戏。
- `--validate-only`：验证启用规则的 `target` 是否存在、目标文件是否超过当前 16MB 虚拟文件限制，并按最终替换顺序统计每条 `find` 命中次数，不启动游戏。
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
- `<target>.diff.txt`：每条替换的简短差异和来源插件。

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
