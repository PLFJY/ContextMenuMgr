# 服务、Session、TrayHost 与前端生命周期

本文负责服务安装/修复、SCM recovery、AutoStart、WTS 启动、托盘和关闭语义。权限/IPC 规则见[进程与权限](process-and-privilege-flows.md)；日志和路径只在[运行时数据与日志](runtime-data-and-logging.md)定义。

## 服务入口与启动所有权

[Program](../ContextMenuMgr.Backend/Program.cs) 先识别一次性 `--service-bootstrap` 及字典校验入口，再进入服务运行；[BackendWindowsService](../ContextMenuMgr.Backend/Hosting/BackendWindowsService.cs) 的 OnStart 调度 `BackendRuntime.StartAsync`，启停原因交给 [ServiceLifecyclePolicy](../ContextMenuMgr.Backend/Hosting/ServiceLifecyclePolicy.cs)。组合顺序见[模块地图](architecture-overview.md)。SCM Running 与 runtime/Pipe 就绪是不同状态。

## 安装、修复与移除服务

`BackendServiceManager` 在需要服务级维护时解析 `ContextMenuManagerPlus.Service.exe`，用 `ProcessStartInfo.Verb = "runas"` 启动 elevated backend process。这个进程执行 `--service-bootstrap` 命令后退出，不是长期运行的 Backend Service。

Portable 包在 `install-or-repair` 触发 UAC 前会先由前端检查当前应用目录中的运行时文件是否带有 Mark-of-the-Web `Zone.Identifier`。如果检测到被 Windows 阻止的 runtime 文件，前端返回 `PORTABLE_RUNTIME_FILES_BLOCKED` 并提示用户确认后只解除当前 portable 应用目录内运行时文件的阻止状态；不会扫描应用目录外路径，也不会自动修改用户文件。

当前 bootstrapper 支持的命令由 `BackendServiceBootstrapper.Execute` 分发：

| 命令 | 用途 | 用户 SID |
| --- | --- | --- |
| `install-or-repair` | 创建/修复服务、处理旧服务名、按用户自启动策略设置并验证服务启动类型与恢复策略，启动服务并等待真实 pipe Ping 可用 | 需要传 `--user-sid` 才能正确读取用户自启动策略 |
| `uninstall` | 停止并删除服务 | 不依赖用户 SID |
| `force-remove-service` | 容错移除当前和旧服务名的 SCM 注册，用于残留 / stale service 修复；不删除用户数据、应用设置或右键菜单注册表项 | 不依赖用户 SID |
| `stop` | 停止服务 | 不依赖用户 SID |
| `set-startup-mode` | 关闭时设置并验证 `demand` 后写 policy；开启时复用完整 install/repair 收敛路径，只有启动模式、恢复策略、Running 与真实 pipe Ping 均成功才写 `StartWithWindows=1` | 需要 `--user-sid` |
| `repair-runtime-data-acl` | 只修复 `RuntimePaths.RootDirectory` 运行时目录 ACL，不安装/卸载/修改服务 | 不依赖用户 SID |

结果通过 `--result-file` 指向的 JSON 文件返回，形状对应 `BootstrapResult(bool Success, string Code, string Detail)`。`BackendServiceManager` 会等待进程退出，读取 result file，然后删除临时文件。bootstrapper 还写 `RuntimePaths.LogsDirectory\bootstrap.log`。

`--user-sid` 的意义是让 elevated 进程明确知道前端用户是谁。elevated 进程自己的 `HKCU` 不能当作前端用户 `HKCU` 使用。当前代码在 `BackendServiceBootstrapper` 中验证 SID，并在服务安装/启动模式场景读取或写入 `HKEY_USERS\<sid>\Software\ContextMenuMgr\Frontend`。

服务移除使用同一套容错路径：停止服务只是 best-effort，即使 `ServiceController.Status`、`Stop()` 或 `WaitForStatus(Stopped)` 失败，bootstrapper 仍会继续向 SCM 请求删除服务注册，并轮询 SCM 而不是只看注册表。删除返回 `SERVICE_PENDING_DELETE` 时表示 Windows 已标记删除但仍有进程持有服务句柄；用户需要关闭 Services MMC、任务管理器服务页或其它持有句柄的进程，必要时重启后再重试。

服务创建、Install/Repair 与 Force Repair 重建都会覆盖式配置 SCM failure actions：约 5 秒、15 秒、60 秒重启，失败计数约 1 天后重置，并开启 non-crash failure actions。正常 SCM stop、前端明确请求、卸载/强力修复和 Windows shutdown 保持零退出码；runtime 启动异常或无法解释的 `ServiceBase.Run` 返回使用非零退出码，使 SCM 恢复策略可以生效。移除服务前还会 best-effort 关闭 failure-actions flag，避免修复/升级与恢复动作竞争。

不要用这条链路做普通菜单开关、Win11 禁用、SpecialMenu 修改、AutoStart 运行时读写或 Restart Explorer。它的主要职责是服务生命周期维护，不是 runtime backend；`repair-runtime-data-acl` 是为了 portable / broken install 自修复保留的窄 fallback，不应扩展成普通运行时操作入口。

## 跨 Session 启动与激活

Windows Service 运行在服务 Session，不能直接在用户桌面显示 UI。需要显示 TrayHost 或 Frontend 时，后端服务必须先找到交互式 Session，再用该 Session 的用户 token 启动进程。

`BackendWindowsService` 设置 `CanHandleSessionChangeEvent = true`，在 `SessionLogon`、`SessionUnlock`、`ConsoleConnect`、`RemoteConnect` 时调用 `BackendRuntime.NotifyInteractiveSessionAvailable(sessionId)`。`BackendRuntime` 再按策略调用 `FrontendAutostartLauncher.TryLaunchTrayHostForActiveSession`。

`FrontendAutostartLauncher` 的关键步骤：

1. 选择目标 Session：优先 `WTSGetActiveConsoleSessionId`，否则枚举 active/connected Session。
2. 通过 `WTSQueryUserToken` 取用户 token，并从 token 解析 SID。
3. 若 `requireAutostartPolicy` 为 true，读取 `HKEY_USERS\<sid>\Software\ContextMenuMgr\Frontend\StartWithWindows`。
4. 读取同一 policy key 下的 `ShowTrayIcon`，缺失时默认为显示；为 `0` 时用 `--hide-tray-icon` 启动 TrayHost。
5. 检查目标 Session 中是否已有 `ContextMenuManagerPlus.TrayHost.exe` 或 `ContextMenuManagerPlus.exe`。
6. 使用 `DuplicateTokenEx`、`CreateEnvironmentBlock`、`CreateProcessAsUser`，并设置桌面为 `winsta0\default`。

服务启动本身也会主动执行一次上述 TrayHost 检查，因此即使使用 delayed-auto 且服务在登录 Session 事件之后才启动，也仍能发现当前交互 Session。TrayHost 的“是否已运行”检查与 `CreateProcessAsUser` 在同一锁内执行，避免服务启动和 Session 通知同时触发时创建两个进程。

打开 Frontend 时，已有同 Session 进程但 frontend control pipe 暂时不可用表示该进程可能仍在启动或正在退出。TrayHost 和后端会在有界 startup grace 内重试完整的 pipe handshake；grace 耗尽时不会立即再启动一个 Frontend。只有目标 Session 中没有 Frontend 进程时才创建新进程。每次 pipe 请求的 connect、write 和 response read 共用同一个总超时，调用方不会无限等待。

Frontend 自身仍用全局 mutex 判定单实例。secondary 在 mutex 已被占用时以同一有界策略转发激活请求；如果原 owner 在等待期间退出，secondary 可以安全取得已释放或 abandoned 的 mutex 并成为 primary。pipe 暂时未就绪且 mutex 仍被持有时，secondary 有界退出，不再按一次 IPC 失败杀死同 Session 的 Frontend 进程。

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

## 用户启动意图与通知投递

开机启动同时涉及服务启动模式和用户级策略：

| 层 | 文件 | 当前行为 |
| --- | --- | --- |
| 用户级 policy | `AutoStartService`、`FrontendAutostartLauncher` | 写/读 `HKEY_USERS\<sid>\Software\ContextMenuMgr\Frontend\StartWithWindows` 和 `ShowTrayIcon`。 |
| 旧 Run key | `AutoStartService` | 读取时作为 fallback；写入时清理 `ContextMenuManagerPlus.TrayHost` 旧值。 |
| 服务启动模式 | `BackendServiceBootstrapper` | 关闭为 `demand`；开启时，服务程序与 Windows 在同一卷用 `auto`，在其它盘或 UNC 路径用 `delayed-auto`。系统盘来自实际 Windows 目录，不能硬编码 `C:`。 |
| SCM 恢复 | `BackendServiceBootstrapper`、`BackendWindowsService` | 创建/修复时配置 5 秒、15 秒、60 秒重启和约 1 天 reset，并启用 non-crash failure actions；意外启动失败返回非零，明确停止仍返回零。 |
| 用户 Session 进程 | `FrontendAutostartLauncher` | 服务启动或 Session 事件时按 policy best-effort 拉起 TrayHost；`ShowTrayIcon=0` 时传入 `--hide-tray-icon`。 |

TrayHost 是每用户进程，因为托盘图标、通知和通知点击激活必须出现在用户交互式桌面。服务只是负责在正确 Session 中启动它，后端通知仍通过 IPC 交给 TrayHost 后再由 `Shell_NotifyIconW` 显示。开机启动必须带用户 SID，因为服务启动模式是机器级，用户是否希望随 Windows 启动以及是否显示托盘图标是用户级。TrayHost 可以没有可见托盘图标，但进程必须继续运行。

`NativeTrayHost` 使用 `PostMessage(WM_APP + 1)` 将通知、图标显隐和本地化更新派发到隐藏窗口所属线程，与 `TaskbarCreated` 和托盘回调串行处理。只保留最新的一条尚未成功提交的通知；`Shell_NotifyIcon(NIM_MODIFY)` 返回 true 后立即清除投递缓存，此结果只表示 Shell 接受提交，不代表用户已经看到通知。图标注册或通知提交失败时，使用同线程 Win32 timer 每秒重试，最多额外五次；预算耗尽或 timer 创建失败时丢弃未提交通知并停止重试。已有恢复期间的 `TaskbarCreated` 不重置预算；后续新通知或新的图标恢复事件可再次尝试。图标重建不会重新缓存已提交通知。

投递缓存与通知点击上下文独立：仅成功提交设置点击上下文，Explorer 重启不清除此上下文，也不再次投递通知。仅 `NIN_BALLOONUSERCLICK` 路由通知激活，常规审核通知打开 Approvals，OpenWith attention 由 TrayHostRunner 路由 OpenWith；普通鼠标/键盘托盘选择打开主窗口，不消费通知点击上下文。Win32 回调只标识图标，无法可靠区分连续通知对应的审核项，因此通知点击使用现有 frontend control pipe 打开普通 Approvals 页面，不再传递最近接收的 item id。审核已处理时仍可安全打开当前审核列表，无需新增后端协议。

`StartWithWindows` 是用户意图，不等于系统已健康实现该意图。启用操作按以下顺序收敛：验证服务注册与 executable path，设置并回读 startup mode，配置并验证 recovery，必要时启动并等待 SCM `Running`，最后等待 backend pipe 的真实 `Ping`；只有全部成功才写用户 policy。`Running` 但 pipe 不响应不能报告成功。普通关闭前端窗口时，仅 `KeepBackgroundAfterClose` 决定当前运行的 Backend Service 和 TrayHost 是否继续运行；即使 `StartWithWindows=true`，关闭保留后台选项也会请求两者退出。`StartWithWindows` 仍决定下次登录时的启动行为。

`bootstrap.log` 和 `service-startup.log` 的生命周期诊断应包含 service/executable、实际 system/service drive、start type、delayed-auto、SCM status、recovery 是否配置、当前用户 policy，以及停止原因。原因只能在可证明时写 `FrontendRequest`、`WindowsShutdown`、`Uninstall`、`ForceRepair` 等；无法证明的 SCM stop 使用 `ServiceControlManager`，无回调返回才标记 `UnexpectedProcessExit`。

## 前端与服务关闭

[App.OnExit](../ContextMenuMgr.Frontend/App.xaml.cs) 取消控制 Pipe，等待 server 与 DI 异步释放各至多约一秒，释放 ShellViewModel、CTS 和单实例 mutex；日志在主实例拥有 mutex 时结束会话。该有界退出不是所有异步业务必已完成的保证。各页面负责自己的订阅、定时器、图标任务和加载回调释放；UI 细节见[前端](frontend-wpf-ui.md)。

`FrontendCloseLifecyclePolicy` 将用户关闭与服务通知关闭区分；只有 `KeepBackgroundAfterClose=false` 的普通关闭才 best-effort 请求 Backend shutdown 和 TrayHost exit，两个短请求各有约 800 ms 预算。不因 StartWithWindows=true 覆盖本次关闭选择；也不能由请求发出推断两个进程已退出。服务可用 keep-frontend marker 在修复/特定失败时保留前端，marker 是生命周期协调，不是持久菜单状态。

`BackendWindowsService.StopCore` 先取消服务 CTS，`BackendRuntime.StopAsync` 等待 monitor，按前端关闭策略处理 UI，再调用 Pipe Stop；Pipe Stop 取消 accept loop 并等待 ShellNew mutation gate。它没有统一 await 全部 Pipe client task 的 drain，不能承诺所有类型请求都完成后才退出。OpenWith coordinator 由同一 monitor cycle 管理，不另遗留轮询线程。硬杀/断电不能享有正常停止的保证。普通停止会先广播/请求前端退出，等待约两秒后可终止所选活动 Session 中剩余 Frontend；不能把这项 shutdown fallback 与启动 grace 中“不因一次握手失败杀进程”混为一谈。服务 OnStop/OnShutdown 明确原因，`ServiceBase.Run` 异常返回由入口判定非零失败，不能把意外失败记录成有意停止。

Restart Explorer 通过 Pipe 解析前端 Session，`ExplorerRestartService` 只处理同 Session 的 explorer.exe。SID 负责 hive，SessionId 负责桌面；不全局杀进程、不从服务 Session 启动 UI。

## 兼容与尚待验证

源码还有一个退出码边界：bootstrapper.TryRun 按结果设置 Environment.ExitCode，但 Program.Main 在 TryRun=true 后显式 return 0；不能承诺失败一定经 OS 进程退出码反映。Frontend 应同时检查 result JSON/Code/Detail，现场进程退出行为尚待验证。

旧 `ContextMenuManagerService` 清理由 bootstrapper 和 installer 实际调用，保留到维护者明确停止支持旧服务名安装；历史发布下限尚未完整核实。无调用的旧 Frontend PowerShell 生命周期脚本已经由 bootstrapper 取代，不应恢复平行实现。

`KillFrontendProcessesForActiveSession` 在 targetSessionId<0 时的条件会匹配全部同名 Frontend，未严格 fail-closed；部署停止/无 Session 场景需核查该现有行为。前述“所选 Session”是正常可解析路径，不覆盖此例外。

当前服务创建默认 LocalSystem；已存在服务修复更新 binary/startup/recovery，不统一重配 account、token DACL 或 SCM RequiredPrivileges。不要仅由服务名推断部署身份。Frontend 使用 **Global** 单实例 mutex，不是每 Session 独立前端；跨用户同时激活、RDP/快速切换和全局 mutex 的实际体验仍须验收，不能由 TrayHost 的用户 Session 启动推导整个应用多实例隔离。

## 调试与验证

看 `bootstrap.log` 的路径、SID、SCM startup/recovery/Running/Ping，再看 `service-startup.log` 和 backend WTS/stop reason、`trayhost.log` 的图标提交/恢复。`NIM_MODIFY=true` 仅为 Shell 接受投递，不证明通知可见；普通托盘激活和 notification activation 需分别检查。

永久覆盖入口为 `ServiceLifecycleTests`、`FrontendControlClientTests` 和共享日志测试。VM 手验须覆盖：标准用户 UAC 安装、同/非系统卷启动、已有 service account、pending-delete、失败恢复和有意停止无重启风暴；登录/解锁、RDP、隐藏图标收通知、通知与普通点击、两次 Explorer 重启不重放旧通知、连续启动的单实例竞争、主实例启动/退出期间 control Pipe 不就绪。不能把人工发送 TaskbarCreated 或隔离窗口实验等同于真实产品流程。

变更 bootstrap 命令、SCM、AutoStart、WTS、激活、关闭、Win32 通知投递时更新本文；安装文件部署另见[安装升级](installer-deployment.md)。
