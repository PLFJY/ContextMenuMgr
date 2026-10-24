# 服务、Session、TrayHost 与前端生命周期

本文负责服务安装/修复、SCM recovery、AutoStart、WTS 启动、托盘和关闭语义。权限/IPC 规则见[进程与权限](process-and-privilege-flows.md)；日志和路径只在[运行时数据与日志](runtime-data-and-logging.md)定义。

## 服务入口与启动所有权

[Program](../ContextMenuMgr.Backend/Program.cs) 先识别一次性 `--service-bootstrap` 及字典校验入口，再进入服务运行；[BackendWindowsService](../ContextMenuMgr.Backend/Hosting/BackendWindowsService.cs) 的 OnStart 调度 `BackendRuntime.StartAsync`，启停原因交给 [ServiceLifecyclePolicy](../ContextMenuMgr.Backend/Hosting/ServiceLifecyclePolicy.cs)。组合顺序见[模块地图](architecture-overview.md)。SCM Running 与 runtime/Pipe 就绪是不同状态。

## 安装、修复与移除服务

`BackendServiceManager` 在需要服务级维护时解析 `ContextMenuManagerPlus.Service.exe`，用 `ProcessStartInfo.Verb = "runas"` 启动 elevated backend process。这个进程执行 `--service-bootstrap` 命令后退出，不是长期运行的 Backend Service。

提升后的 bootstrapper 通过 [ServiceScmNative](../ContextMenuMgr.Backend/Hosting/ServiceScmNative.cs) 直接调用 CreateService/ChangeServiceConfig/ChangeServiceConfig2，服务配置与恢复策略用 QueryServiceConfig/QueryServiceConfig2 回读，不再逐项启动 sc.exe。原生配置只支持当前产品服务名；移除前恢复开关的共享辅助函数另支持既有旧服务名，不提供 Pipe 任意服务配置能力。创建仍为独立进程 LocalSystem 服务；修复只更新既有 binary/startup/description/recovery，保留账户、依赖、ACL 等原有边界。完整符合目标的配置跳过重复写入，仍须 Running 和真实 Pipe Ping 才算收敛成功。

恢复校验逐项比较 reset=86400、restart/5000、restart/15000、restart/60000 与 non-crash failure flag，不以非空 FailureActions 值或一次 API 成功代替核验。可变配置查询使用 Windows 规定的最多 8 KiB 缓冲，避免先查询长度再重发一次 RPC；SCM 句柄、字符串和数组缓冲在确定作用域内释放。`ScmOperation` 记录具体操作、Updated/Unchanged、实际 Win32Error 和耗时；失败不退回慢速外部命令，不盲重试不确定写入。显式移除的 stop-first、旧服务名处理和有界删除观察保持不变，仅其 best-effort 恢复开关改用同一原生实现。

startup type 与 delayed-auto flag 由两个原生调用组成；部分失败先在同一服务句柄上读取中间状态，只有仍与本次修改相符时补偿并核对原值，再向上报告原始错误。外部归属/模式变化保留，补偿不确定明确返回失败；Portable 外层启动策略事务继续执行原有补偿与 marker 否决逻辑。

Portable 包在 `install-or-repair` 触发 UAC 前会先由前端检查当前应用目录中的运行时文件是否带有 Mark-of-the-Web `Zone.Identifier`。如果检测到被 Windows 阻止的 runtime 文件，前端返回 `PORTABLE_RUNTIME_FILES_BLOCKED` 并提示用户确认后只解除当前 portable 应用目录内运行时文件的阻止状态；不会扫描应用目录外路径，也不会自动修改用户文件。

Portable 启动前先从 SCM 有界观察固定服务的归属/待删除状态，确认不存在或已停止时跳过必然失败的两秒 Pipe 探测，直接进入原有安装/修复流程；其它副本、待删除超时或不确定配置不执行覆盖修复。Installer 保留既有启动探测流程。MOTW、UAC、SID/policy 与完整就绪验证不会为了缩短耗时被跳过。

bootstrap 的每次真实 Ping 使用异步 Pipe I/O，connect/write/response 共用同一个两秒取消预算；不能连接成功后无限等待响应。整体仍保留原有 Running 与 Pipe 就绪的有界观察，不把启动超时上限误当作每次必须等待的固定延迟。

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

投递缓存与通知点击上下文独立：仅成功提交设置点击上下文，Explorer 重启不清除此上下文，也不再次投递通知。仅 `NIN_BALLOONUSERCLICK` 路由通知激活，常规审核和 OpenWith 未解决审核均由 TrayHostRunner 打开中央 Approvals；普通鼠标/键盘托盘选择打开主窗口，不消费通知点击上下文。Win32 回调只标识图标，无法可靠区分连续通知对应的审核项，因此通知点击使用现有 frontend control pipe 打开普通 Approvals 页面，不再传递最近接收的 item id，也不根据最新通知类型猜测目标页。审核已处理时仍可安全打开当前审核列表；OpenWith 的 SID 过滤、诊断与待审核计数语义见 [OpenWith](openwith.md#监控审核与显式持久保护)。

`StartWithWindows` 是用户意图，不等于系统已健康实现该意图。启用操作按以下顺序收敛：验证服务注册与 executable path，设置并回读 startup mode，配置并验证 recovery，必要时启动并等待 SCM `Running`，最后等待 backend pipe 的真实 `Ping`；只有全部成功才写用户 policy。`Running` 但 pipe 不响应不能报告成功。普通关闭前端窗口时，仅 `KeepBackgroundAfterClose` 决定当前运行的 Backend Service 和 TrayHost 是否继续运行；即使 `StartWithWindows=true`，关闭保留后台选项也会请求两者退出。`StartWithWindows` 仍决定下次登录时的启动行为。

`bootstrap.log` 和 `service-startup.log` 的生命周期诊断应包含 service/executable、实际 system/service drive、start type、delayed-auto、SCM status、recovery 是否配置、当前用户 policy，以及停止原因。原因只能在可证明时写 `FrontendRequest`、`WindowsShutdown`、`Uninstall`、`ForceRepair` 等；无法证明的 SCM stop 使用 `ServiceControlManager`，无回调返回才标记 `UnexpectedProcessExit`。

## 前端与服务关闭

[App.OnExit](../ContextMenuMgr.Frontend/App.xaml.cs) 取消控制 Pipe，等待 server 与 DI 异步释放各至多约一秒，释放 ShellViewModel、CTS 和单实例 mutex；日志在主实例拥有 mutex 时结束会话。该有界退出不是所有异步业务必已完成的保证。各页面负责自己的订阅、定时器、图标任务和加载回调释放；UI 细节见[前端](frontend-wpf-ui.md)。

`FrontendCloseLifecyclePolicy` 只用 `KeepBackgroundAfterClose` 决定本次是否保留 Backend/TrayHost，不受下次登录的 AutoStart 意图覆盖。App 使用显式 ShutdownMode，在窗口实际 `Closed` 后 await 退出流程，防止取消的 Closing 提前停服；Portable 先以约 800 ms 总预算请求并等待本目录、本 Session TrayHost 退出，再以约三秒预算发送 Backend 请求，最后 Shutdown/释放 IPC；Installer 保留 Backend→TrayHost 顺序及各约 800 ms 请求预算。Portable 使用独立 `RequestPortableExit`，Installer 仍使用普通 `RequestShutdown`。服务控制关闭、系统 SessionEnding、secondary instance 和致命异常不发送 Portable 自动卸载请求。设置事务进行中取消普通窗口关闭，防止提升操作与退出交错；不能由响应或请求超时推断物理退出。keep-frontend marker 仍仅用于生命周期协调。

### Portable 自动移除 SCM 注册

实现入口为 [PortableServiceCleanup](../ContextMenuMgr.Backend/Hosting/PortableServiceCleanup.cs)、[PortableExitCaller](../ContextMenuMgr.Backend/Hosting/PortableExitCaller.cs)、[ServiceStopSequence](../ContextMenuMgr.Backend/Hosting/ServiceStopSequence.cs)。**设计约束：仅显式 Portable 包可用**，Installer 即使 JSON 伪造为 true 也不显示、不执行自动移除；显式 uninstall/force-remove 的旧服务名清理和 stop-first 逻辑保持原样。

| 偏好 | 默认值 / 含义 |
| --- | --- |
| `RemovePortableServiceOnExit` | true；可读旧 JSON 缺字段也按 true；显式 false 跨保存/升级保留 |
| `KeepBackgroundAfterClose` | false；true 时普通关闭不退出 Backend/TrayHost，但不阻止符合条件的系统关机清理 |
| `AutoStartOnLogin` / `StartWithWindows` | false；与 Portable 自动移除互斥，不改变本次后台保留选择 |

前端退出次序：TrayHost exit → 认证 Pipe 专用请求 → 验证当前注册、持久策略、调用者/其它消费者 → 打开固定服务的 QUERY_CONFIG/QUERY_STATUS/DELETE 句柄 → `DeleteService` → 立即释放本次 SCM 句柄 → 正常 `Stop()` → `StopCore()`/runtime 资源释放 → 服务进程正常退出。不启动 sc.exe/UAC 辅助卸载，不调用 bootstrapper.UninstallService。普通 RequestShutdown/SCM Stop 没有自动卸载语义。调用者身份无法验证或其它 Session/消费者仍在使用时，拒绝专用退出请求并保留 runtime；通过授权之后的清理拒绝/失败仍继续正常停服。

服务 OnStart 固定 Backend EXE、Frontend EXE、manifest 及其祖先目录句柄；运行期不允许这些对象被替换/重命名。清理时再次固定当前路径和现有设置文件，拒绝 UNC、reparse point、路径别名/变化。仅允许非模拟 LocalSystem、Session 0、实际 `--service` 进程；SCM 必须是独立 LocalSystem Win32 服务，PID 等于当前进程，ImagePath 精确等于当前 EXE 加 `--service`，启动类型为 demand。验证与 Delete 使用同一服务句柄，删除前重查配置。配合式 bootstrap 与清理使用同一有界 lifecycle mutex；无法取得锁时不猜测归属。管理员在产品之外直接并发改 SCM 属于外部特权修改，核对失败保留注册。

后端直接读取同一 `frontend-settings.json` 的小型共享契约，不复制整套设置：文件缺失、不可读、非法/重复生命周期字段、超过 4 MiB 时拒绝移除。用户 policy 使用原生 64-bit HKU，不用 SYSTEM HKCU。认证前端退出先证明调用者是独占消费者，再检查 ProfileList 中调用用户及所有已加载普通用户的 StartWithWindows 与旧 TrayHost Run 值；调用用户缺失或 hive 不可读仍拒绝移除。未登录用户的未加载 hive 不属于本次会话的有效运行策略，不再单独阻止前端退出清理；实际 SCM 仍须是 demand，不以 frontend JSON 覆盖机器配置。**系统关机的保守限制：没有认证前端作为退出发起者，所有普通用户的保留 policy 都须可核验；未加载 hive、无法枚举或超过预算时保留注册。** 不挂载/修改离线用户 hive，正常停止仍继续，不能承诺每次关机都会删除。

`OnShutdown` 在原 StopCore 前直接执行同一最佳努力清理，不回调前端 Pipe、不拉起提升进程、不引入 PRESHUTDOWN；只做一次本地 SCM 请求，没有等待本服务从 SCM 消失的轮询。后台用户进程在系统关机中可能尚未退出，其持久策略和实际 SCM 类型决定是否保留。`ServiceStopSequence` 串行处理前端请求/SCM stop/OnShutdown，删除至多一次，资源清理一次；有意停止仍为零退出码，启动异常的 recovery 语义不变。激活正在进行或无法可靠清点其它 Session/Frontend/TrayHost 时拒绝移除；停止阶段不再新建用户进程。崩溃、硬杀、断电、BSOD 不尝试自动移除。

`PortableServiceCleanup` 日志区分 stage、Win32 error、MarkedForDeletion、AlreadyMarkedForDeletion、Absent、AccessDenied、ShutdownInProgress、ScmUnavailable 和其它失败；`FullyRemovedVerified=False` 是刻意的证据边界。DeleteService 成功仅代表已标记删除，必须等进程结束、Services MMC 等其它服务句柄关闭后再从 SCM 观察 absent 才能确认完整移除。删除失败不删除数据、不改 recovery、不阻止 StopCore。

Portable 重开在 Ping/修复之前通过 [PortableServiceStartup](../ContextMenuMgr.Contracts/PortableServiceStartup.cs) 观察固定服务，前端最多约四秒、bootstrap 安装入口最多约五秒，每次观察关闭句柄并间隔等待。pending/StopPending 超时给出关闭 Services MMC 后重试/重启提示；其它副本或归属不明拒绝自动改注册。随后仍走原有 MOTW 检查、UAC 安装修复、SCM Running 和真实 Pipe Ping 核对。Installer 不走此观察分支。

### Portable 启动策略事务

两个开关用命令 `CanExecute` 互斥：开启自动移除前，用户须先关闭 Start with Windows；反向亦然，不替用户切换另一项。关闭 autostart 仍通过原 `set-startup-mode` 验证 demand 和用户禁用 policy；成功后才能开启 cleanup，本地原子保存 cleanup 不再重复启动 UAC/SCM 操作。开启 autostart 前 cleanup 已关闭；底层事务仍强制 auto=true 时 cleanup=false，保护 reset、历史/手工设置和其它调用路径。参数 `--portable-remove-on-exit` 仅在 Portable 生效。提升进程在同一生命周期锁内保存旧 SCM mode/用户 policy，复用既有 AutoStart 收敛流程，核对并清理旧 Run 值，最后条件式原子更新原 frontend settings 文件的两个字段。SCM/UAC/注册表/JSON 失败不报告成功；失败补偿在同一个提升进程执行，不再弹第二次 UAC，保留已观察到的外部更改，无法恢复时明确返回 `PORTABLE_STARTUP_ROLLBACK_FAILED` 并保留诊断。事务前持久创建 `portable-startup-transaction.marker`，只在成功提交或验证补偿完成后移除；残留/不可读 marker 一律禁止自动删除。显式 install/repair 完整验证后将 cleanup 保守置 false，再清除此 veto，不从崩溃残留推导允许删除。

UI 在初始读取、事务和 reset 期间禁止重入，回填 Toggle 不触发命令；失败恢复旧有效快照；补偿不确定时锁住两个开关并提示先显式修复。其它前端偏好在事务中保存时保留磁盘中已提交的生命周期字段，避免覆盖提升进程刚提交的状态。专用前端退出请求获取生命周期锁最多等一秒，允许刚完成的安装释放锁；仍在执行的维护操作导致有界拒绝，不重试不确定的删除。设置文件写入/迁移归属见[运行时数据](runtime-data-and-logging.md)，界面与本地化见[前端](frontend-wpf-ui.md)。

`BackendWindowsService.StopCore` 先取消服务 CTS，`BackendRuntime.StopAsync` 等待 monitor，按前端关闭策略处理 UI，再调用 Pipe Stop；Pipe Stop 取消 accept loop 并等待 ShellNew mutation gate。它没有统一 await 全部 Pipe client task 的 drain，不能承诺所有类型请求都完成后才退出。OpenWith coordinator 由同一 monitor cycle 管理，不另遗留轮询线程。硬杀/断电不能享有正常停止的保证。普通停止会先广播/请求前端退出，等待约两秒后可终止所选活动 Session 中剩余 Frontend；不能把这项 shutdown fallback 与启动 grace 中“不因一次握手失败杀进程”混为一谈。服务 OnStop/OnShutdown 明确原因，`ServiceBase.Run` 异常返回由入口判定非零失败，不能把意外失败记录成有意停止。

Restart Explorer 通过 Pipe 解析前端 Session，`ExplorerRestartService` 只处理同 Session 的 explorer.exe。SID 负责 hive，SessionId 负责桌面；不全局杀进程、不从服务 Session 启动 UI。

## 兼容与尚待验证

源码还有一个退出码边界：bootstrapper.TryRun 按结果设置 Environment.ExitCode，但 Program.Main 在 TryRun=true 后显式 return 0；不能承诺失败一定经 OS 进程退出码反映。Frontend 应同时检查 result JSON/Code/Detail，现场进程退出行为尚待验证。

旧 `ContextMenuManagerService` 清理由 bootstrapper 和 installer 实际调用，保留到维护者明确停止支持旧服务名安装；历史发布下限尚未完整核实。无调用的旧 Frontend PowerShell 生命周期脚本已经由 bootstrapper 取代，不应恢复平行实现。

`KillFrontendProcessesForActiveSession` 在 targetSessionId<0 时的条件会匹配全部同名 Frontend，未严格 fail-closed；部署停止/无 Session 场景需核查该现有行为。前述“所选 Session”是正常可解析路径，不覆盖此例外。

当前服务创建默认 LocalSystem；已存在服务修复更新 binary/startup/recovery，不统一重配 account、token DACL 或 SCM RequiredPrivileges。不要仅由服务名推断部署身份。Frontend 使用 **Global** 单实例 mutex，不是每 Session 独立前端；跨用户同时激活、RDP/快速切换和全局 mutex 的实际体验仍须验收，不能由 TrayHost 的用户 Session 启动推导整个应用多实例隔离。

## 调试与验证

看 `bootstrap.log` 的路径、SID、SCM startup/recovery/Running/Ping，再看 `service-startup.log` 和 backend WTS/stop reason、`trayhost.log` 的图标提交/恢复。`NIM_MODIFY=true` 仅为 Shell 接受投递，不证明通知可见；普通托盘激活和 notification activation 需分别检查。

永久覆盖入口为 `ServiceLifecycleTests`、`FrontendControlClientTests` 和共享日志测试。自动化策略/事务/文件身份测试不能代替 SCM 或 OS shutdown 集成验收。Portable 真机须逐项记录 clean start→UAC→Running/Ping→退出后 SCM absent、立即重开、保留后台、关闭自动移除、AutoStart 开关、真实重启/关机、其它安装占用、多登录 Session、外部工具持有 SCM 句柄；每项核对路径、启动模式、PID、停止原因、删除结果与新 Backend readiness。Installer 验证无自动移除及原 stop/repair/uninstall。未执行时明确标注未验证，不能模拟关机后声称 OS shutdown 通过。

VM 手验另须覆盖：标准用户 UAC 安装、同/非系统卷启动、已有 service account、pending-delete、失败恢复和有意停止无重启风暴；登录/解锁、RDP、隐藏图标收通知、通知与普通点击、两次 Explorer 重启不重放旧通知、连续启动的单实例竞争、主实例启动/退出期间 control Pipe 不就绪。不能把人工发送 TaskbarCreated 或隔离窗口实验等同于真实产品流程。

变更 bootstrap 命令、SCM、AutoStart、WTS、激活、关闭、Win32 通知投递时更新本文；安装文件部署另见[安装升级](installer-deployment.md)。
