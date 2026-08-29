# 进程、权限与用户上下文链路

相关文档：

- [AI 与维护者接手 Playbook](./ai-maintainer-playbook.md)
- [ContextMenuMgr 开发者指南](./developer-guide.md)
- [排错指南](./troubleshooting.md)
- [SpecialMenu 实现说明](./special-menus.md)
- [Windows 11 新右键菜单实现说明](./windows11-context-menu.md)
- [Deep Analysis 与 ProbeHost 实现说明](./deep-analysis-probehost.md)

## 1. 为什么需要这份文档

ContextMenuMgr 不是单一管理员权限模型。当前实现同时涉及普通 WPF 前端、LocalSystem 后端服务、一次性的 UAC elevated bootstrapper、用户 Session 中的 UI 进程、ProbeHost 隔离进程、`HKCU` / `HKEY_USERS\<SID>` 和 `SessionId`。

这些概念不能互相替代：权限、用户身份、用户注册表 hive、桌面 Session 是不同概念。LocalSystem 有高权限，但 LocalSystem 的 `HKCU` 不是当前前端用户的 `HKCU`；服务知道某个用户的 SID，也不代表它能直接在这个用户桌面显示窗口；拿到 SessionId 也不代表应该用它写注册表。

维护权限相关代码时，先判断要解决的是“权限不够”、“用户 hive 错了”，还是“进程启动到了错误桌面”。这三类问题在本项目里分别走不同链路。

## 2. 核心概念

| 概念 | 本项目中的含义 |
| --- | --- |
| 进程权限 | 进程当前 token 拥有的系统权限。后端服务通常是 LocalSystem，UAC bootstrapper 是 elevated 进程，前端通常不提权。 |
| 用户身份 | 发起操作的交互式用户。前端 pipe 连接会通过 `NamedPipeServerStream.RunAsClient` 和客户端进程解析用户。 |
| 用户 SID | 用户 hive 的稳定标识，例如 `S-1-5-21-...`。用户级注册表写入必须基于 SID。 |
| 用户注册表 hive | `HKEY_USERS\<SID>` 下的用户配置。服务进程写用户级配置时应打开这里，而不是写服务自己的 `HKCU`。 |
| `HKCU` | 当前进程 token 对应的 Current User。前端里的 `HKCU` 是前端用户，LocalSystem 服务里的 `HKCU` 是 SYSTEM 账户。 |
| `HKEY_USERS\<SID>` | 服务端定位前端用户注册表的明确路径。`AutoStartService`、`SpecialMenuService`、`Windows11BlocksService` 都依赖这种方式。 |
| `SessionId` | Windows 登录会话/桌面位置。重启 Explorer 和服务拉起 TrayHost/Frontend 需要它。 |
| 服务 Session | Windows Service 运行的非交互式 Session。服务不能直接在这里显示用户 UI。 |
| 交互式桌面 Session | 用户可见的桌面，例如 `winsta0\default`。TrayHost 和 Frontend 必须在这里启动。 |
| TrayHost / SessionHost | 运行在交互式用户 Session 中的通知与点击激活代理。后端服务通过 IPC 把通知交给 TrayHost；后端服务不直接显示用户通知。 |
| LocalSystem | 高权限服务身份。它能修改很多机器级资源，但不等于“当前用户”。 |
| Named Pipe client identity | 后端 pipe 通过客户端连接解析出来的前端用户身份，是运行时用户级操作的首选上下文来源。 |
| WTS user token | 通过 `WTSQueryUserToken` 从 SessionId 取得的用户 token，用于 `CreateProcessAsUser`。它解决的是在用户桌面启动进程。 |

## 3. 四条主要链路总览

| 链路 | 形式 | 主要用途 | 是否提权 | 是否需要用户 SID | 是否需要 SessionId | 典型文件 |
| --- | --- | --- | --- | --- | --- | --- |
| 链路 A | Frontend -> Backend Pipe -> Service | 快照、传统菜单开关、审核、SpecialMenu、Win11 blocked list、AutoStart 运行时读写、Restart Explorer、运行时数据目录 ACL 修复 | 前端不提权，服务执行高权限部分 | 用户级操作需要 | 仅 Session 相关操作需要 | `NamedPipeBackendClient.cs`、`NamedPipeBackendServer.cs`、`BackendUserContextResolver.cs` |
| 链路 B | Frontend -> UAC Bootstrapper | 安装/修复服务、卸载服务、停止服务、设置服务启动模式；仅在 backend pipe 不可用时作为运行时数据目录 ACL 修复 fallback | 是，通过 `Verb=runas` | `install-or-repair` 和 `set-startup-mode` 需要 `--user-sid`；ACL 修复不需要 | 通常不需要 | `BackendServiceManager.cs`、`BackendServiceBootstrapper.cs` |
| 链路 C | Service -> User Session Process | 后端服务拉起 TrayHost、打开 Frontend、登录/解锁后确保 TrayHost | 服务已是 LocalSystem，不是 UAC | 只用于读启动策略时需要 | 需要 | `FrontendAutostartLauncher.cs`、`BackendWindowsService.cs`、`BackendRuntime.cs` |
| 链路 D | Frontend -> ProbeHost | Deep Analysis，隔离第三方 Shell Extension COM 风险 | 否 | 不需要 | 不需要 | `ContextMenuDeepAnalysisService.cs`、`ContextMenuMgr.ProbeHost/src` |

## 4. 链路 A：Frontend -> Backend Pipe -> Service

前端通过 `NamedPipeBackendClient` 连接 `PipeConstants.PipeName`，发送 `PipeEnvelope` / `PipeRequest`。后端 `NamedPipeBackendServer` 接收请求后按 `PipeCommand` 分发，并返回 `PipeResponse`。订阅通知的连接还会收到 `BackendNotification`，用于新增菜单审核、状态变化和服务停止提示。

典型运行时操作走这条链路：

| 操作 | 后端处理入口 |
| --- | --- |
| 传统菜单快照 | `ContextMenuRegistryCatalog.GetSnapshotAsync` |
| 启用/禁用传统菜单 | `HandleSetEnabledAsync` -> `ApplyDesiredStateAsync` |
| 文件类型相关项批量管理查询 | `FindRelatedFileTypeMenuItems` -> `ContextMenuRegistryCatalog.FindRelatedFileTypeMenuItemsAsync` |
| 审核新增项 | `HandleApplyDecisionAsync` -> `ApplyDecisionAsync` |
| 删除/恢复/清理备份 | `DeleteItemAsync`、`UndoDeleteAsync`、`PurgeDeletedItemAsync` |
| 重置状态库 | `PipeCommand.ResetStateDatabase` -> 后端串行删除 current / `.bak` / 当前 host 删除备份；下一次带 frontend userContext 的常规与 WPS 快照重建 baseline |
| Registry Write Protection | `GetRegistryProtectionSettingAsync`、`SetRegistryProtectionSettingAsync` |
| SpecialMenu | `SpecialMenuService` |
| Win11 blocked list | `Windows11BlocksService` 和 `Windows11ContextMenuCatalog` |
| Win11 全局经典右键菜单设置 | `Win11ClassicContextMenuService` |
| AutoStart / 托盘图标策略运行时读写 | `AutoStartService` |
| Restart Explorer | `ExplorerRestartService.RestartExplorer` |
| 运行时数据目录 ACL 修复 | `RuntimeDataAclRepairService` |

受保护的机器级 classic ShellVerb 仍走链路 A：先由服务对 `HKLM\SOFTWARE\Classes` 的真实 `BackendRegistryPath` 执行普通写入；仅在该写入因 Windows ACL 被拒绝、且 Registry Write Protection preflight 已通过时，才临时启用 `SeTakeOwnershipPrivilege` / `SeRestorePrivilege` 完成受控 value mutation。这个 fallback 只适用于实际机器级 Classes path，绝不用于 `HKEY_USERS\<SID>\Software\Classes`，并在写入后恢复、验证原 owner/DACL。它不是 UAC bootstrapper、ShellNew ACL lock 或通用注册表 take-ownership 机制。
`NamedPipeBackendServer` 会在需要用户上下文时创建 `BackendUserContextResolver`。解析顺序是先从 pipe client 解析，失败时部分场景回退到交互式用户。`BackendUserContext` 包含 `Sid`、`UserName`、`ProfilePath`、`LocalAppDataPath`、`RoamingAppDataPath` 和可选 `SessionId`。

必须有 frontend user context 的场景包括：

| 场景 | 原因 |
| --- | --- |
| `ShellNew`、`SendTo`、`WinX`、`OpenWith` | 它们分别依赖用户 `Software\Classes`、用户 profile 目录、用户 `LocalAppData` 或用户 OpenWith policy。 |
| Win11 user blocked list | 用户级 blocked list 位于 `HKEY_USERS\<sid>\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked`。 |
| AutoStart policy | 当前实现写 `HKEY_USERS\<sid>\Software\ContextMenuMgr\Frontend`，并清理用户 Run key。 |
| Restart Explorer | 只应影响前端用户 Session 里的 `explorer.exe`。 |
| 部分传统菜单状态写入 | `ApplyDesiredStateAsync` 需要区分用户级和机器级注册表路径。 |

常见错误：

- 不要在服务里使用 `Registry.CurrentUser` 读写前端用户设置。
- 不要把 `HKCU` 和 `HKEY_USERS\<frontend sid>` 混用。
- 不要为了运行时菜单开关改走 UAC bootstrapper。
- 不要在 Win11 snapshot 或 Win11 blocked list 操作中丢掉 `userContext`。
- 不要把 `RestartExplorer` 当成普通注册表写入，它需要 SessionId。

`ContextMenuRegistryMonitor` 的监控循环独立于前端 pipe 连接运行。`ReconcileAndRefreshSnapshotAsync` 在每次轮询时通过 `BackendUserContextResolver.TryResolveInteractiveUserFallback()` 解析当前交互式用户上下文并传递给 `GetSnapshotAsync`。这确保 Win11 packaged COM 项和 `HKEY_USERS\<sid>` 下的 per-user 项在用户会话暂时不可用（屏幕锁定、UAC 提升、快速用户切换）时仍能被正确枚举，避免因枚举不完整导致状态库被错误清理。

## 5. 链路 B：Frontend -> UAC Bootstrapper

`BackendServiceManager` 在需要服务级维护时解析 `ContextMenuManagerPlus.Service.exe`，用 `ProcessStartInfo.Verb = "runas"` 启动 elevated backend process。这个进程执行 `--service-bootstrap` 命令后退出，不是长期运行的 Backend Service。

Portable 包在 `install-or-repair` 触发 UAC 前会先由前端检查当前应用目录中的运行时文件是否带有 Mark-of-the-Web `Zone.Identifier`。如果检测到被 Windows 阻止的 runtime 文件，前端返回 `PORTABLE_RUNTIME_FILES_BLOCKED` 并提示用户确认后只解除当前 portable 应用目录内运行时文件的阻止状态；不会扫描应用目录外路径，也不会自动修改用户文件。

当前 bootstrapper 支持的命令由 `BackendServiceBootstrapper.Execute` 分发：

| 命令 | 用途 | 用户 SID |
| --- | --- | --- |
| `install-or-repair` | 创建/修复服务、处理旧服务名、按用户自启动策略设置服务启动类型，并等待 pipe 可用 | 需要传 `--user-sid` 才能正确读取用户自启动策略 |
| `uninstall` | 停止并删除服务 | 不依赖用户 SID |
| `force-remove-service` | 容错移除当前和旧服务名的 SCM 注册，用于残留 / stale service 修复；不删除用户数据、应用设置或右键菜单注册表项 | 不依赖用户 SID |
| `stop` | 停止服务 | 不依赖用户 SID |
| `set-startup-mode` | 设置服务 `auto` / `demand`，并写用户级 `StartWithWindows` 策略 | 需要 `--user-sid` |
| `repair-runtime-data-acl` | 只修复 `RuntimePaths.RootDirectory` 运行时目录 ACL，不安装/卸载/修改服务 | 不依赖用户 SID |

结果通过 `--result-file` 指向的 JSON 文件返回，形状对应 `BootstrapResult(bool Success, string Code, string Detail)`。`BackendServiceManager` 会等待进程退出，读取 result file，然后删除临时文件。bootstrapper 还写 `RuntimePaths.LogsDirectory\bootstrap.log`。

`--user-sid` 的意义是让 elevated 进程明确知道前端用户是谁。elevated 进程自己的 `HKCU` 不能当作前端用户 `HKCU` 使用。当前代码在 `BackendServiceBootstrapper` 中验证 SID，并在服务安装/启动模式场景读取或写入 `HKEY_USERS\<sid>\Software\ContextMenuMgr\Frontend`。

服务移除使用同一套容错路径：停止服务只是 best-effort，即使 `ServiceController.Status`、`Stop()` 或 `WaitForStatus(Stopped)` 失败，bootstrapper 仍会继续向 SCM 请求删除服务注册，并轮询 SCM 而不是只看注册表。删除返回 `SERVICE_PENDING_DELETE` 时表示 Windows 已标记删除但仍有进程持有服务句柄；用户需要关闭 Services MMC、任务管理器服务页或其它持有句柄的进程，必要时重启后再重试。

不要用这条链路做普通菜单开关、Win11 禁用、SpecialMenu 修改、AutoStart 运行时读写或 Restart Explorer。它的主要职责是服务生命周期维护，不是 runtime backend；`repair-runtime-data-acl` 是为了 portable / broken install 自修复保留的窄 fallback，不应扩展成普通运行时操作入口。

## 6. 链路 C：Service -> User Session Process

Windows Service 运行在服务 Session，不能直接在用户桌面显示 UI。需要显示 TrayHost 或 Frontend 时，后端服务必须先找到交互式 Session，再用该 Session 的用户 token 启动进程。

`BackendWindowsService` 设置 `CanHandleSessionChangeEvent = true`，在 `SessionLogon`、`SessionUnlock`、`ConsoleConnect`、`RemoteConnect` 时调用 `BackendRuntime.NotifyInteractiveSessionAvailable(sessionId)`。`BackendRuntime` 再按策略调用 `FrontendAutostartLauncher.TryLaunchTrayHostForActiveSession`。

`FrontendAutostartLauncher` 的关键步骤：

1. 选择目标 Session：优先 `WTSGetActiveConsoleSessionId`，否则枚举 active/connected Session。
2. 通过 `WTSQueryUserToken` 取用户 token，并从 token 解析 SID。
3. 若 `requireAutostartPolicy` 为 true，读取 `HKEY_USERS\<sid>\Software\ContextMenuMgr\Frontend\StartWithWindows`。
4. 读取同一 policy key 下的 `ShowTrayIcon`，缺失时默认为显示；为 `0` 时用 `--hide-tray-icon` 启动 TrayHost。
5. 检查目标 Session 中是否已有 `ContextMenuManagerPlus.TrayHost.exe` 或 `ContextMenuManagerPlus.exe`。
6. 使用 `DuplicateTokenEx`、`CreateEnvironmentBlock`、`CreateProcessAsUser`，并设置桌面为 `winsta0\default`。

TrayHost 和 Frontend 启动区别：

| 目标 | 触发场景 | 行为 |
| --- | --- | --- |
| TrayHost | 服务启动、用户登录/解锁、前端 `EnsureTrayHost` 请求 | 通常受 `StartWithWindows` policy 影响；显式 `EnsureTrayHost` 不重置注册表监控 baseline。 |
| Frontend | 托盘通知打开主窗口或审核页 | 优先通过 frontend control pipe 唤醒现有前端；不存在时在用户 Session 中启动前端。 |

这条链路不是注册表修改链路。它解决的是“在哪个用户桌面启动进程”。

TrayHost 可以在没有可见托盘图标的情况下运行。隐藏托盘图标只通过 TrayHost 内部的 Win32 `Shell_NotifyIconW` 状态完成，不代表停止 TrayHost，也不代表后端服务可以直接显示通知。新增项检测等后端通知仍然是：

```text
Backend Service -> backend pipe notification -> user-session TrayHost -> Shell_NotifyIconW notification -> click activation -> Frontend
```

## 7. 链路 D：Frontend -> ProbeHost

`ProbeHost` 是隔离进程，不是提权进程。它只用于 Deep Analysis，前端在用户点击深入分析时由 `ContextMenuDeepAnalysisService` 启动 `ContextMenuMgr.ProbeHost.exe`，传入 `--request` 和 `--result` 临时文件路径，并捕获 stdout/stderr。

ProbeHost 的边界：

- 不写注册表。
- 不执行菜单命令。
- 不作为后端服务使用。
- 不解决权限问题。
- 只隔离第三方 Shell Extension COM 加载、初始化和 `IContextMenu.QueryContextMenu` 风险。

架构选择由前端完成。`ContextMenuDeepAnalysisService` 会读取目标 handler DLL 的 PE machine type，选择 `ProbeHost\x86`、`ProbeHost\x64` 或 `ProbeHost\arm64` 下的 native `ContextMenuMgr.ProbeHost.exe`。如果 handler 架构未知，当前实现倾向于回退到当前前端进程架构。启动前会验证 ProbeHost exe 的 PE 架构；ProbeHost 不再有 `.runtimeconfig.json`、`.deps.json` 或 ProbeHost 目录内的 `ContextMenuMgr.Contracts.dll` 运行时依赖。

`SpecificHandler` 和 `WholeContextMenu` 是不同模式：

| 模式 | 行为 | 风险和限制 |
| --- | --- | --- |
| `SpecificHandler` | `CoCreateInstance` 指定 handler，尝试 `IShellExtInit` 和 `IContextMenu` | 可能因 handler 不支持接口、初始化失败、返回空菜单或架构不匹配而失败。 |
| `WholeContextMenu` | 让 Shell 为样本路径创建完整上下文菜单，再枚举菜单项 | 结果更接近真实 Shell，但隔离粒度更粗，失败时也不能视作普通菜单管理失败。 |

深入分析失败通常是预期限制，不是菜单管理失败。常见原因包括 handler 架构不匹配、COM 初始化失败、第三方 DLL 崩溃、样本类型不支持、ProbeHost 依赖缺失或返回非 JSON 输出。

## 8. 功能到链路的选择表

| 功能 | 应走链路 | 需要用户 SID | 需要 SessionId | 备注 |
| --- | --- | --- | --- | --- |
| 传统菜单启用/禁用 | 链路 A | 可能需要 | 否 | `SetEnabled` -> `ApplyDesiredStateAsync`，用户级项不能写 SYSTEM 的 `HKCU`。 |
| 传统菜单删除/恢复 | 链路 A | 可能需要 | 否 | 删除前用 `RegistryBackupService` 导出 `.reg`；恢复后重读用户级/HKCR overlay 项时必须带 frontend user context。 |
| 文件类型相关菜单批量管理 | 链路 A | 是 | 否 | `FindRelatedFileTypeMenuItems` 只在 File Types 隐藏批量管理视图打开/刷新时扫描 HKLM 与前端用户 `Software\Classes`，不参与服务启动扫描或普通 baseline。后续开关/删除/撤销复用普通 `SetEnabled` / `DeleteItem` / `UndoDelete`，scene-only 项通过请求中的 `ContextMenuEntry` fallback 定位；已删除备份项会合并回批量结果供撤销，文件类型核心 `open` / `edit` verb 不允许删除。 |
| 编辑菜单显示名 | 链路 A | 可能需要 | 否 | `SetDisplayText`，受 Registry Write Protection preflight 影响。 |
| 编辑传统 ShellVerb 命令文本 | 链路 A | 可能需要 | 否 | `SetCommandText` -> `ApplyCommandTextAsync`，只允许普通 legacy ShellVerb 写 `<verb>\command` 默认值，不处理 Shell Extension、Win11、SubCommands、DelegateExecute、DropTarget 或 ExplorerCommandHandler。 |
| 管理经典级联 Shell Verb 子菜单 | 链路 A | 可能需要 | 否 | `GetShellSubMenuItems` 按需读取 `SubCommands`、`ExtendedSubCommandsKey` 或父级 `shell`；`SetShellSubMenuItemEnabled` 由后端重新解析稳定 parent/child identity。`SubCommands` 仅改父级引用并持久化被移除引用的顺序，不修改共享 CommandStore 项。 |
| Registry Write Protection 设置 | 链路 A | 否 | 否 | 作用于受监控传统菜单根的 ACL。 |
| Win11 新菜单项禁用/恢复 | 链路 A | 是 | 否 | user blocked list 必须带 `BackendUserContext`，机器级另有 HKLM blocked list。 |
| Win11 全局恢复经典菜单设置 | 链路 A | 是 | 否 | 写 `HKEY_USERS\<sid>\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32`，不得写服务 `HKCU` 或 HKLM；生效需要重启 Explorer 或重新登录。 |
| Win11 snapshot | 链路 A | 是 | 否 | `Windows11ContextMenuCatalog.EnumerateEntriesAsync` 没有 SID 会跳过。 |
| ShellNew 枚举 | 链路 A | 是 | 是 | 通过前端 SessionId 的 WTS 用户 token 调用 `RegOpenUserClassesRoot` 读取前端用户的合并 HKCR；同时用 `PackageManager.FindPackagesForUser(sid)` 只读扫描包清单中的 ShellNew FileType 声明；ShellNew order key 仍显式读取 `HKEY_USERS\<sid>`。 |
| WPS / Microsoft Office 共存保护 | 链路 A | 是 | 否 | 只在 WPS Office 和 Microsoft Office 同时存在时启用；读取 `HKEY_USERS\<sid>\Software\Classes`、ShellNew order key 和 HKLM Office baseline，通过专用管道生成 WPS 关联 / 图标 / ShellNew 注入项。空库/重置时先无提示采纳并写独立 WPS baseline marker；已有 marker 后的新发现才进入待审核。 |
| 文档图标来源切换 | 链路 A | 是 | 否 | `PipeCommand.SetDocumentIconProvider` 只写当前前端用户 `HKEY_USERS\<sid>\Software\Classes\<ProgID>\DefaultIcon`，不写 HKLM，不修改 UserChoice Hash。 |
| ShellNew 排序 | 链路 A | 是 | 否 | 写 Explorer ShellNew order key，可能需要临时 unlock/relock ACL。 |
| ShellNew ACL lock/unlock | 链路 A | 是 | 否 | 只锁 ShellNew order key，不等于 Registry Write Protection。 |
| SendTo 操作 | 链路 A | 是 | 否 | 操作用户 `%APPDATA%\Microsoft\Windows\SendTo`。 |
| WinX 操作 | 链路 A | 是 | 否 | 操作用户 `%LOCALAPPDATA%\Microsoft\Windows\WinX`，`.lnk` 需要 hash。 |
| OpenWith 操作 | 链路 A | 是 | 否 | 枚举 HKU/HKLM Classes `Applications`；新增和用户 policy 写 `HKEY_USERS\<sid>`，机器级项按真实 HKLM 路径修改。 |
| AutoStart 运行时读写 | 链路 A | 是 | 否 | `PipeCommand.SetAutoStartEnabled` / `GetAutoStartEnabled` 写读用户 `StartWithWindows` policy，并清理旧 Run value。 |
| 托盘图标显示策略 | 链路 A + TrayHost control pipe | 是 | 否 | `PipeCommand.SetTrayIconPolicy` 写用户 `ShowTrayIcon` policy；运行中的 TrayHost 通过 Win32 `Shell_NotifyIconW` 隐藏/显示图标。TrayHost 进程继续运行。 |
| 安装/修复服务 | 链路 B | 是 | 否 | `install-or-repair --user-sid` 用于读取用户启动策略并决定服务启动模式。 |
| 卸载服务 | 链路 B | 否 | 否 | elevated 一次性进程执行服务删除。 |
| 设置服务启动模式 | 链路 B | 是 | 否 | `set-startup-mode --enabled ... --user-sid ...`。 |
| 后端启动 TrayHost | 链路 C | 读取策略时需要 | 是 | 服务用 WTS token 在用户 Session 启动。 |
| 托盘打开前端 | 链路 C | 否 | 是 | 优先 frontend control pipe，必要时 `CreateProcessAsUser`。 |
| 重启 Explorer | 链路 A | 是 | 是 | pipe 解析前端用户 Session，只杀同 Session 的 `explorer.exe`。 |
| 修复运行时数据目录 ACL | 链路 A，pipe 不可用时链路 B fallback | 否 | 否 | 后端启动早期和 `RepairRuntimeDataAcl` pipe 命令都会对 `RuntimePaths.RootDirectory` best-effort 授予 Builtin Users Modify，并修复已有子项继承；bootstrapper fallback 只执行 `repair-runtime-data-acl`。 |
| Deep Analysis | 链路 D | 否 | 否 | 前端启动 ProbeHost，失败不影响普通菜单管理。 |

## 9. 绝对不要混用的东西

- 不要用服务 `HKCU` 读写前端用户设置。
- 不要把 UAC bootstrapper 当成普通 runtime backend。
- 不要从服务 Session 直接显示 UI。
- 不要用 SpecialMenu helper 处理普通菜单，除非代码已经明确泛化。
- 不要在 Win11 snapshot 中丢掉 `userContext`。
- 不要把 ProbeHost crash 当成普通菜单开关失败。
- 不要把 Registry Write Protection 和 ShellNew Order ACL Lock 混为一谈。
- 不要把 `SessionId` 当成 SID，也不要把 SID 当成可显示 UI 的位置。
- 不要为了“权限更高”把用户级操作改成机器级 HKLM 写入。
- 如果一个问题看起来像第三方软件、驱动或安装器异常，必须先按 Playbook 的故障归因纪律确认是否真的和本项目有关。

## 10. 日志定位

主要日志路径由 `RuntimePaths` 定义。Installer 根目录是 `%ProgramData%\ContextMenuMgr`；Portable 根目录是 `<应用目录>\Data`，包类型来自 `AppContext.BaseDirectory` 下的 `ContextMenuMgr.package.json`。

| 日志 | 位置 | 优先用于 |
| --- | --- | --- |
| `frontend-debug.log` | `RuntimePaths.LogsDirectory\frontend-debug.log` | 前端命令、UAC 启动、全局搜索、Deep Analysis 启动和结果解析。 |
| `frontend-crash.log` | `RuntimePaths.LogsDirectory\frontend-crash.log` | 前端未处理异常。 |
| `backend.log` | `RuntimePaths.LogsDirectory\backend.log` | pipe 请求、用户上下文解析、注册表操作、Win11、SpecialMenu、Explorer restart。 |
| `trayhost.log` | `RuntimePaths.LogsDirectory\trayhost.log` | TrayHost 启动、托盘通知、托盘到前端控制。 |
| `bootstrap.log` | `RuntimePaths.LogsDirectory\bootstrap.log` | `BackendServiceBootstrapper` 的 elevated 服务维护操作。 |
| `service-startup.log` | `RuntimePaths.LogsDirectory\service-startup.log` | 服务早期启动失败，尤其是 `FileLogger` 尚未可用前。 |
| bootstrap result file | `%TEMP%\ContextMenuMgr-*.json` | `BackendServiceManager` 临时读取，通常操作后删除。 |
| ProbeHost stderr / result diagnostics | 前端捕获并写入 `frontend-debug.log`，结果对象含 `DiagnosticDetails` | Deep Analysis 的架构、依赖、COM、崩溃和 JSON 解析问题。 |

排查优先级：

| 问题类型 | 优先看 |
| --- | --- |
| 前端连不上后端 | `frontend-debug.log` 中 `BackendServiceManager`、`MainViewModel`、`FrontendOperation` 相关记录，`backend.log` 的 pipe 连接记录，再看 `service-startup.log`。 |
| 服务安装/修复失败 | `bootstrap.log`、bootstrap result detail、`backend.log`、`service-startup.log`。 |
| 用户级注册表写错 | `backend.log` 中 `Sid=`、`HKEY_USERS\<sid>`、`OpenUserRegistryRoot`、`OpenUserBlockedKey`。 |
| TrayHost 不出现 | `backend.log` 的 `TryEnsureTrayHost` 相关记录，`trayhost.log`，确认 `StartWithWindows` policy。 |
| Restart Explorer 无效 | `backend.log` 的 `RestartExplorerRequest`，检查 `SessionId` 和 `KilledCount`。 |
| Deep Analysis 失败 | `frontend-debug.log` 的 `ProbeHostSelection`、`ProbeHostExit`、`ProbeHostCapturedOutput` 和结果 diagnostics。 |
