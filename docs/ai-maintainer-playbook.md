# AI 与维护者接手 Playbook

相关文档：

- [ContextMenuMgr 开发者指南](./developer-guide.md)
- [进程、权限与用户上下文链路](./process-and-privilege-flows.md)
- [排错指南](./troubleshooting.md)
- [注册表模型与传统右键菜单实现](./registry-model.md)
- [SpecialMenu 实现说明](./special-menus.md)
- [Windows 11 新右键菜单实现说明](./windows11-context-menu.md)
- [Deep Analysis 与 ProbeHost 实现说明](./deep-analysis-probehost.md)
- [构建与发布说明](./build-and-release.md)

本文是给后续维护者和 AI Coding Agent 的接手手册。它不是 README，也不是完整开发指南；它的目标是防止没看懂架构就改代码、把不同权限链路混在一起、把用户上下文问题误判成权限问题、把 ProbeHost 问题误判成普通菜单开关问题，或者把第三方软件异常随意归因到本项目。

## 1. 接手前必须先读什么

| 任务类型 | 必读文档 | 重点确认 |
| --- | --- | --- |
| 普通右键菜单 Bug | `developer-guide.md`、`registry-model.md`、`process-and-privilege-flows.md` | 是 `shell` verb 还是 `shellex` handler；状态来自注册表还是 `ContextMenuStateStore`；是否涉及 Registry Write Protection。 |
| Win11 新菜单 Bug | `windows11-context-menu.md`、`process-and-privilege-flows.md` | blocked list 写到 HKLM 还是 `HKEY_USERS\<SID>`；snapshot 是否带 frontend userContext。 |
| ShellNew / SendTo / WinX Bug | `special-menus.md`、`process-and-privilege-flows.md` | 是否需要 SID、ProfilePath、LocalAppData/RoamingAppData；ShellNew 发现是否需要前端 SessionId 的 merged HKCR；不要走普通菜单 catalog。 |
| 服务安装 / 开机启动 Bug | `process-and-privilege-flows.md`、`developer-guide.md`、`troubleshooting.md` | 是服务生命周期还是运行时操作；是否应走 UAC bootstrapper；是否需要 `--user-sid`。 |
| TrayHost / 前端启动 Bug | `process-and-privilege-flows.md`、`developer-guide.md` | 是否是链路 C；SessionId、WTS token、用户桌面是否正确。 |
| Restart Explorer Bug | `process-and-privilege-flows.md`、`troubleshooting.md` | 是否拿到前端用户 SessionId；是否只处理同 Session 的 `explorer.exe`。 |
| Deep Analysis / ProbeHost Bug | `deep-analysis-probehost.md`、`build-and-release.md` | ProbeHost 架构、依赖、request/result、stderr、失败是否属于预期限制。 |
| 全局搜索 Bug | `developer-guide.md`、`troubleshooting.md` | 当前候选池只覆盖传统右键菜单分类和 Win11；跳转后是否消费 pending filter。 |
| 构建 / 发布 Bug | `build-and-release.md`、`deep-analysis-probehost.md` | native ProbeHost 多架构目录、MSBuild/C++ toolchain、installer/portable 参数。 |
| UI / 主题 / 本地化 Bug | `developer-guide.md`、`troubleshooting.md` | `FrontendSettingsService`、`FrontendThemeService`、`LocalizationService` 的启动顺序和设置路径。 |
| 新增 Feature | `developer-guide.md`、`process-and-privilege-flows.md`，再读对应专题 | 先选正确链路和模块，再决定是否新增 `PipeCommand`、服务逻辑或前端页面。 |

## 2. 排查 Bug 的第一原则：先判定链路

分析任何 Bug 前，先回答这些问题：

1. 这个 Bug 涉及哪个进程？
2. 这个 Bug 走哪条链路？
3. 是否需要用户 SID？
4. 是否需要 SessionId？
5. 是否涉及注册表写入？
6. 是否涉及第三方 Shell Extension？
7. 是否涉及 UAC bootstrapper？
8. 是否只是 UI / 前端状态问题？

不要一上来就改代码。不要一上来就归因到“拦截功能”或“权限不够”。本项目中“权限”“用户 SID”“用户 hive”“SessionId”“桌面位置”是不同概念，错误归类通常会导致改错模块。

## 3. 四条链路速查

| 链路 | 用途 | 不适合做什么 | 最常见误用 |
| --- | --- | --- | --- |
| 链路 A：Frontend -> Backend Pipe -> Service | 普通 runtime 操作：传统菜单开关、Win11 blocked list、SpecialMenu、AutoStart 运行时读写、Restart Explorer。 | 不适合安装/卸载服务，不负责弹 UAC。 | 忘记解析 frontend userContext，导致用户级注册表写到错误 hive。 |
| 链路 B：Frontend -> UAC Bootstrapper | 服务安装、修复、卸载、停止、设置服务启动模式。 | 不适合普通菜单开关、Win11 禁用、SpecialMenu 修改。 | 把 elevated process 当成长期运行后端，或用它自己的 `HKCU` 当用户 hive。 |
| 链路 C：Service -> User Session Process | 后端服务在交互式用户 Session 中启动 TrayHost / Frontend。 | 不适合注册表修改。 | 从服务 Session 直接显示 UI，或只看 SID 不看 SessionId。 |
| 链路 D：Frontend -> ProbeHost | Deep Analysis 隔离探测第三方 Shell Extension。 | 不适合写注册表、执行菜单命令、提权。 | 把 ProbeHost crash 当成普通菜单开关失败。 |

## 4. 故障归因纪律

如果用户报告“某个第三方软件 / 驱动 / 安装器异常”，不能直接归因到 ContextMenuMgr。必须先找证据。

| 需要确认的问题 | 为什么 |
| --- | --- |
| 当时 ContextMenuMgr 是否在运行 | 没运行时通常不能直接归因。 |
| Backend Service 是否在运行 | 普通前端退出不代表后端退出。 |
| Registry Write Protection 是否开启 | 只有开启相关保护才可能影响第三方写菜单。 |
| ShellNew ACL Lock 是否开启 | 它只影响新建菜单排序相关 key，不是全局注册表。 |
| 日志中是否有对应时间点的操作 | 没有时间线证据不能乱归因。 |
| 是否有 Access Denied / UnauthorizedAccessException | 没有拒绝证据不能直接说是权限拦截。 |
| 受影响的注册表路径是否属于项目保护范围 | 不在范围内一般不能归因。 |
| 关闭保护 / 停止服务后是否可复现 | 用 A/B 验证排除误判。 |
| 第三方安装器自身日志怎么说 | 很多失败和本项目无关。 |

> 没有日志证据、注册表路径证据、功能开启状态证据时，不要把第三方软件或驱动安装异常归因到 ContextMenuMgr。

例如第三方驱动安装异常，不应默认认为是右键菜单新增项拦截导致。除非能证明安装器正在写入本项目保护的右键菜单相关注册表路径，并且被 Registry Write Protection 或 ACL deny 拒绝，否则它更可能是安装器自身、系统环境、安全软件、驱动残留或其它原因。

## 5. Feature 开发前的选择表

| 想加的功能 | 应优先放在哪层 | 不应该放在哪层 | 原因 |
| --- | --- | --- | --- |
| 新普通菜单操作 | 后端服务 + Backend Pipe + `ContextMenuRegistryCatalog` | UAC bootstrapper、前端直接写注册表 | 普通 runtime 操作应走链路 A，由服务执行高权限写入。 |
| 新 Win11 菜单操作 | `Windows11ContextMenuCatalog` / `Windows11BlocksService` + Backend Pipe | 传统 `shell` / `shellex` 开关逻辑 | Win11 新菜单靠 blocked list 和 userContext。 |
| 新 SpecialMenu 操作 | `SpecialMenuService` + Backend Pipe | `ContextMenuRegistryCatalog` | ShellNew / SendTo / WinX 有独立模型和用户上下文要求。 |
| 新服务管理操作 | `BackendServiceManager` + `BackendServiceBootstrapper` | 普通 runtime pipe handler | 服务生命周期才需要链路 B 和 UAC。 |
| 新托盘功能 | TrayHost 或服务到用户 Session 链路 | 后端服务直接显示 UI | 托盘是每用户 UI，必须在交互式 Session。 |
| 新开机启动行为 | `AutoStartService`、bootstrapper、`FrontendAutostartLauncher` | 只写 Run key 或只改服务启动模式 | 开机启动同时涉及服务启动模式和用户级 policy。 |
| 新全局搜索字段 | `ContextMenuGlobalSearchService` 和目标页面 filter | 每次输入访问后端 | 搜索应使用本地候选池，刷新由 snapshot/通知驱动。 |
| 新 Deep Analysis 能力 | `ContextMenuDeepAnalysisService` + ProbeHost | 前端或后端直接加载第三方 DLL | Shell Extension 运行时探测必须隔离。 |
| 新构建产物 | `build.ps1`、`Scripts/Build.Common.psm1`、`Build-Target.ps1` | 手工复制发布文件 | 构建产物要保持 CI、installer、portable 一致。 |
| 新 UI 设置项 | 前端设置服务，必要时通过 Backend Pipe 同步后端 | 绕过设置服务或直接写高权限注册表 | 设置要有本地持久化、服务同步和错误处理。 |

## 6. Bug 排查模板

```markdown
## Bug 排查记录

### 现象

### 影响范围

### 涉及功能

### 初步判定链路

- [ ] 链路 A：Frontend -> Backend Pipe -> Service
- [ ] 链路 B：Frontend -> UAC Bootstrapper
- [ ] 链路 C：Service -> User Session Process
- [ ] 链路 D：Frontend -> ProbeHost
- [ ] 纯前端 UI
- [ ] 构建 / 发布

### 是否需要用户 SID

### 是否需要 SessionId

### 相关日志

### 相关注册表路径 / 文件路径

### 已排除的原因

### 当前最可能原因

### 修改计划

### 验证步骤
```

## 7. 改代码前检查清单

- [ ] 我已经确认这属于哪条链路
- [ ] 我已经确认是否需要 userContext
- [ ] 我没有使用服务 HKCU 当用户 HKCU
- [ ] 我没有把 UAC bootstrapper 用作普通 runtime backend
- [ ] 我没有从服务 Session 直接启动 UI
- [ ] 我没有在前端 / 后端直接加载 Shell Extension DLL
- [ ] 我没有绕过 Registry Write Protection preflight
- [ ] 我没有把 ShellNew ACL Lock 和 Registry Write Protection 混在一起
- [ ] 我已经查过相关日志
- [ ] 我已经写出手动验证步骤

## 8. 常见错误归因示例

### 例 1：Win11 禁用后刷新丢失

错误归因：
“前端刷新把状态覆盖了。”

正确方向：
先查 Win11 snapshot 是否带 frontend userContext，blocked list 是否写到正确 `HKEY_USERS\<SID>`。

### 例 2：开机启动不生效

错误归因：
“Run key 没写。”

正确方向：
先查服务启动模式、`StartWithWindows` policy、`BackendServiceBootstrapper` 的 `--user-sid`、`FrontendAutostartLauncher` 是否能找到交互式 Session。

### 例 3：ShellNew 解锁失败

错误归因：
“权限不够。”

正确方向：
先查 ShellNew order key ACL、WorldSid deny rule、是否用正确 SID 打开 `HKEY_USERS`。ShellNew lock/unlock 不再走 take ownership fallback；如果 ACL 已损坏到 `ChangePermissions` 无法修改，应外部修复。

### 例 4：Deep Analysis 失败

错误归因：
“菜单项坏了。”

正确方向：
先查 ProbeHost 架构、依赖、`SpecificHandler` 是否支持、Shell Extension 是否需要 Explorer 聚合环境。失败通常不影响普通菜单开关。

### 例 5：第三方驱动 / 安装器异常

错误归因：
“右键菜单拦截导致安装失败。”

正确方向：
先确认 Registry Write Protection 是否开启、是否有本项目日志、是否有 Access Denied、目标路径是否属于右键菜单保护范围。没有证据不要归因。

## 9. 给 AI Coding Agent 的硬性要求

- 回答前必须先判断链路。
- 修改前必须先说清楚要改哪些文件、为什么是这些文件。
- 遇到用户上下文问题时必须区分 SID、HKCU、HKEY_USERS、SessionId。
- 遇到第三方软件异常时必须先找证据，不准直接归因。
- 遇到 ProbeHost 问题时不准改普通菜单开关逻辑。
- 遇到 Win11 状态问题时必须检查 userContext。
- 遇到 SpecialMenu 问题时必须检查 `SpecialMenuService`，不要先改 `ContextMenuRegistryCatalog`。
- 遇到服务安装问题时必须检查 `BackendServiceManager` / `BackendServiceBootstrapper`，不要先改 `NamedPipeBackendServer`。
- 服务卸载、强力修复和 install-or-repair 的 stale service 恢复必须共用 bootstrapper 的容错移除路径；停止失败只能作为警告记录，不能阻止后续 SCM 删除请求。
- 遇到 UI 启动 / TrayHost 问题时必须检查 `FrontendAutostartLauncher` / WTS 链路。
- 如果不确定，应该输出“当前证据不足”，不要编造原因。

## 10. 什么时候需要更新文档

- 新增 `PipeCommand`
- 新增 `SpecialMenuKind`
- 修改 Registry Write Protection
- 修改 ShellNew ACL Lock
- 修改 Win11 blocked list
- 修改 AutoStart
- 修改 TrayHost 启动
- 修改 ProbeHost 架构选择
- 修改 build/release
- 修改 `RuntimePaths`
- 修改全局搜索范围
