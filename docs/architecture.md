# Architecture

运行时平台的长期设计见 `docs/runtime_mod_platform.md`，通用规则契约见 `docs/capability_rule_contract.md`，验收场景见 `docs/validation_scenarios.md`。本文档记录当前骨架和短期组件边界；平台文档记录事件、状态、动作和深层 Hook 能力的方向。

## Phase 1: Injection and Logging

目标是证明三件事：

1. 启动器可以稳定找到游戏入口。
2. 启动器可以加载匹配位数的 `RuntimeHook.dll`。
3. DLL 进入游戏进程后可以写日志。

这一阶段不修改游戏逻辑。

## Components

### DDRuntimeLoader

C# 控制台启动器。

职责：

- 读取 `config/default_config.json` 或 `config/config.json`。
- 校验游戏路径、DLL 路径、日志目录。
- 计算并记录游戏 exe SHA-256。
- 默认以 suspended 模式启动 `Darkest.exe`，先注入 DLL 再恢复主线程，避免错过早期资源读取。
- 通过 `LoadLibraryW` 远程线程注入 RuntimeHook.dll。
- 将 `DD_RUNTIME_FRAMEWORK_ROOT`、`DD_RUNTIME_LOG_DIR`、文件 IO 观察配置和事件探针配置写入游戏进程环境。
- 可选启动器侧存档目录 watcher，监控 Steam userdata / Documents Darkest 中的真实存档落盘，并在游戏退出后继续短暂监听外部同步写入。
- 扫描插件补丁清单 `plugins/<plugin-id>/patches.json`，按 manifest 依赖和顺序字段生成加载计划，再把虚拟文件规则写入 `DD_RUNTIME_VIRTUAL_RULE_*` 环境变量。
- 在启动前验证补丁规则：目标文件存在性、当前虚拟文件大小限制、按最终替换顺序统计字符串命中次数和同目标多规则提示。
- 在不启动游戏的情况下解释和预览补丁结果，输出加载顺序、排序边、跳过原因、虚拟文件文本、简短 diff 和同一目标行冲突提示。
- 在不启动游戏的情况下用 `--emit-event` 模拟事件，执行已实现的安全 `eventRules` 动作并写入 sidecar state；部分 managed 动作先物化为 sidecar artifact，不执行真实游戏修改。
- 在启动游戏或 `--dry-run` 前，把 `_managed_actions/` 中可消费的 sidecar artifact 编译成 `logs/managed_action_overlay_manifest.json`，并通过 `DD_RUNTIME_MANAGED_OVERLAY_*` 环境变量暴露给 RuntimeHook 诊断。

### RuntimeHook.dll

C++ DLL。

职责：

- 在 `DLL_PROCESS_ATTACH` 后创建初始化线程。
- 初始化日志。
- 记录进程、模块路径和环境变量。
- 初始化文件 IO Hook、虚拟文件通道和 observe-only 事件探针。
- 记录 managed action overlay manifest 的路径、文件大小、overlay 数量和 issue 数量；当前只做可见性诊断，不替换游戏内容。

### Hook Layer

后续阶段会在这里加入 MinHook 或等价库。

建议顺序：

1. 观察文件读取路径，只记录不修改。当前阶段通过 MinHook 挂 `CreateFileW/CreateFileA`。
2. 对 `.darkest` / localization 文件做虚拟内容返回。当前原型支持配置规则列表：每条规则匹配一个路径后缀，并按顺序执行多条字符串替换，通过虚拟句柄响应 `ReadFile` / `GetFileSize` / `SetFilePointer` / `CloseHandle`。
3. 观察文件写入和生命周期操作，只记录不修改。当前阶段通过 MinHook 挂 `WriteFile`、`MoveFile/MoveFileEx`、`CopyFile`、`DeleteFile`、`ReplaceFile` 和 `SetFileAttributes`，把已知真实文件活动分类成 `data` / `asset` / `save` 事件；`save` 事件有独立日志预算，外部噪声路径可通过配置过滤。
4. 对 DLL 无法覆盖的外部存档落盘，先由启动器 sidecar watcher 做 observe-only 记录。
5. 对数据加载函数做结构化 Hook。
6. 最后才碰战斗、AI 和存档相关逻辑。

### Plugin Layer

第一版插件层先只实现补丁清单，不加载第三方代码：

- 启动器扫描配置中的 `pluginDirectories`。
- 每个插件目录读取一个 `patches.json`。
- `enabled:false` 的清单只记录日志，不参与规则合并。
- `enabled:true` 的清单可提供 `id`、`version`、`capabilities`、`phase`、`priority`、`depends`、`optionalDepends`、`loadAfter`、`loadBefore`、`conflicts` 和 `virtualFileRules`。
- 清单现在也可以声明 `eventRules` 和 `stateSchema`。`eventRules` 可通过 `--explain-rules` 解释，并可通过 `--emit-event` 执行已实现的安全动作或物化 selected managed action artifact；`quest.injectFixedStage` artifact 会在启动前进入 overlay manifest，`stateSchema` 可初始化/读取到框架 sidecar 状态目录。
- 重复 `id`、声明冲突和顺序循环默认只记录 warning；必需依赖缺失时跳过当前插件，不阻止其他插件。
- `virtualFileRules` 可使用 `when.modsPresent` / `when.modsAbsent` / `when.capabilitiesPresent` / `when.capabilitiesAbsent` 做规则级条件；条件不满足的规则只进入 explain 诊断，不参与最终补丁链。
- `operations` 会在启动前按加载顺序、基于当前虚拟文本逐步编译成底层字符串 `replacements`。
- 编译后的替换会保留 operation subject，例如 `key:.some_key`，用于 explain、validate、preview diff 和 key 级冲突提示。

稳定后再考虑：

- native C ABI 插件
- Lua 插件
- C# 插件宿主

## Risk Control

- 所有 Hook 必须可配置关闭。
- 所有深层 Hook 必须绑定游戏 exe hash。
- 插件启用顺序必须记录到日志。
- 默认不写入自定义存档状态。
- 崩溃排查优先看最后一条 runtime 日志。
