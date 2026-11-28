# Portable 重构基线

源码锚点 `ef5e320a99f57db0587443cfd778b6c6e32f2fb3`，产品版本 `1.8.1`。本文固定 G0 的跨层契约、分发证据和 G1 入口；各机制仍由[维护文档索引](README.md)中的责任文档维护。

## 架构与 G1 切分位置

| 执行入口 | 现有依赖与边界 |
| --- | --- |
| Frontend | `App.OnStartup` 获取全局单实例所有权，次实例通过 Frontend Control Pipe 激活主实例。主实例创建 DI、载入设置/语言/主题，启动控制服务和 WPF 窗口。DI 将 `IBackendClient` 绑定到单例 `NamedPipeBackendClient`，并独立注册 `IBackendServiceManager`、缓存、工作区及页面服务。工作区负责服务就绪/Ping、通知连接、首轮快照与刷新；增强/详细编辑规则也有 Frontend 本地读取和编译。窗口实际关闭后按本次后台保留策略处理 Backend/TrayHost，`OnExit` 有界释放控制服务、视图模型、DI 和单实例锁。 [App](../ContextMenuMgr.Frontend/App.xaml.cs)、[DI](../ContextMenuMgr.Frontend/App.Services.xaml.cs)、[工作区](../ContextMenuMgr.Frontend/Services/ContextMenuWorkspaceService.cs) |
| 请求与回包 | `IBackendClient` 暴露异步业务 API、连接状态、缓存读取、通知和权威移除事件。当前实现同时承担缓存编排、operation ID 和 Pipe I/O。`NamedPipeBackendServer` 同时承担帧处理、连接身份、命令校验/分发、发现完整性、响应关联和通知。因此两端都需要保留 I/O 之外的契约。 [接口](../ContextMenuMgr.Frontend/Services/IBackendClient.cs)、[客户端](../ContextMenuMgr.Frontend/Services/NamedPipeBackendClient.cs)、[服务端](../ContextMenuMgr.Backend/Services/NamedPipeBackendServer.cs) |
| 普通管理 | `ContextMenuRegistryCatalog` 持有状态、备份、保护设置与快照/变更事务；`FileTypeSceneMenuService` 复用 catalog/state；`SpecialMenuService` 处理独立菜单、注册表与用户资料目录；`Windows11BlocksService`、Win11 catalog 和 `Win11ClassicContextMenuService` 负责不同 Win11 控制域。共享边界包括物理定位、权限受限的写入、回读、回滚、恢复和状态提交。当前 catalog 同时调用保护及监控相关逻辑，`SpecialMenuService.OpenWithMonitoring` 同时处理普通编辑观察和授权/隔离，提取时须沿调用点保留先后顺序。 [注册表模型](registry-model.md)、[SpecialMenu](special-menus.md)、[Win11](windows11-context-menu.md)、[OpenWith](openwith.md) |
| Service host | Backend `Program` 区分 bootstrapper、`--service` 与控制台。`BackendWindowsService` 在 SCM 回调下管理 `BackendRuntime`。`CreateDefault` 先尝试旧数据复制和数据 ACL 修复，再装配 logger、identity、store、backup、catalog、special/Win11/file-type/Explorer/AutoStart、monitor、Pipe、launcher。运行时先启动 Pipe 再启动 monitor，连接 monitor 检测、OpenWith 变化及 store 恢复到 Pipe 通知。控制台复用运行时，SCM/WTS 能力仍取决于实际 token。 [Program](../ContextMenuMgr.Backend/Program.cs)、[BackendRuntime](../ContextMenuMgr.Backend/Hosting/BackendRuntime.cs)、[Windows Service](../ContextMenuMgr.Backend/Hosting/BackendWindowsService.cs) |
| Bootstrapper 与 TrayHost | Frontend `BackendServiceManager` 使用 UAC `runas` 和结果 JSON 管理安装/修复/启动模式/停止等，Pipe 不可用时可回退到窄数据 ACL 修复。Service 用 WTS token 启动用户 Session TrayHost/Frontend。TrayHost 独立订阅 Backend Pipe，图标隐藏仍接收 Win32 通知；点击以 Frontend Control Pipe 或用户进程启动激活审批页。 [服务管理器](../ContextMenuMgr.Frontend/Services/BackendServiceManager.cs)、[Bootstrapper](../ContextMenuMgr.Backend/Hosting/BackendServiceBootstrapper.cs)、[Tray Pipe](../ContextMenuMgr.TrayHost/TrayBackendPipeClient.cs)、[生命周期](runtime-lifecycle.md) |
| Deep Analysis | Frontend 直接选择并启动独立 native ProbeHost。请求/结果走临时 JSON，使用重定向输出、超时与子进程终止机制；架构选择考虑 OS/服务器 PE/可用 probe，旧目录布局有发现兼容。普通扫描只读元数据，第三方 Shell Extension COM 的动态探测留在 ProbeHost。 [Frontend Probe 客户端](../ContextMenuMgr.Frontend/Services/ContextMenuDeepAnalysisService.cs)、[ProbeHost](deep-analysis-probehost.md) |

G1 的切分依据是 `Frontend → 异步客户端语义 → Pipe transport → 命令分发（显式身份及完整性）→ 普通操作`。Installer 继续在 `BackendRuntime` 装配 SCM/WTS/TrayHost、诊断、监控和保护；保留本地前端规则读取与独立 ProbeHost 两条路径。Dispatcher 的输入应保留命令、认证上下文、取消和 operation ID，输出保留结构化结果和提交后的通知语义。Transport 负责连接、身份捕获、帧与投递；业务侧仍需校验目标 hive、物理来源及权限。

## 命令与能力清单

以 [PipeCommand](../ContextMenuMgr.Contracts/PipeCommand.cs)、[PipeRequest](../ContextMenuMgr.Contracts/PipeRequest.cs)、[PipeResponse](../ContextMenuMgr.Contracts/PipeResponse.cs)、[分发](../ContextMenuMgr.Backend/Services/NamedPipeBackendServer.cs)和[客户端](../ContextMenuMgr.Frontend/Services/NamedPipeBackendClient.cs)为准。共 66 个命令。表中 `共享` 为 Portable 应提供的普通操作，`后台` 为 Installer-only，`宿主` 为当前生命周期或连接操作，`待核` 表示当前无可直接继承的 Portable 契约。默认共同返回 `Success/Message`，可附 `ErrorCode`、`RemovedItemIds`、`ClientOperationId`。`覆盖` 表示服务端填 `SnapshotIsComplete/ClassicSnapshotIsComplete`。`变更通知` 指成功响应带 `Item/SpecialItem` 时提交后广播，除明确的定点读回；`本地` 指调用端回填或刷新。并发、取消与接收范围适用下一节。实际方法默认是同名 `…Async`，例外在表中列出。

身份缩写 `U` 表示认证 Pipe SID，解析失败的扫描可返回机器级部分结果，用户写入受下游 hive/物理来源校验。`S` 表示实际分发按种类解析 SID（ShellNew、SendTo、WinX、OpenWith），ShellNew merged discovery 还需匹配 Session/token；其他种类按物理目标/现有写入权限处理。`M` 表示当前 handler 无 per-user resolver，依赖机器/本地资源权限；`D` 表示需认证用户桌面 Session。除标明的宿主/后台钩子，共享操作的目标无需 SCM 或 TrayHost 接口，当前仍经 Service 承载。机器注册表、保护的键与公共配置按各子系统所需权限处理。

| 命令 | 请求 → 结果 | 分发、权限与提交/缓存 | 消费者及 Portable |
| --- | --- | --- | --- |
| `Ping` | 无 → 状态 | Pipe 存活，M | 工作区/Main；宿主，保留就绪语义 |
| `EnsureTrayHost` | 无 → 接受状态 | Runtime 事件 → WTS launcher，宿主 Session | 工作区；宿主，Portable 无 |
| `SubscribeNotifications` | 无 → ACK、独立通知流 | 标记订阅连接并尝试捕获 U，限定部分用户广播 | 客户端 `ConnectAsync`；宿主，Portable 仍需业务通知 |
| `SubscribeTrayHost` | 无 → ACK、独立通知流 | 与前端共用广播，尝试捕获 U | `TrayBackendPipeClient`；宿主，Portable 无 |
| `RequestShutdown` | 无 → 接受状态 | Runtime 停机事件；处理前端结束，M | App Installer 关闭；宿主，Portable 无 |
| `RequestPortableExit` | 无 → 接受/拒绝 | Portable caller、目录、会话/消费者及 SCM 所有权关联，触发 stop/cleanup | App 旧 Portable 关闭；宿主，未来无 |
| `SetLogLevel` | `LogLevel` → 状态 | Pipe 设置 Backend logger 当前级别，M；偏好另存 Frontend | 工作区；宿主，Portable 对接本地日志 |
| `RepairRuntimeDataAcl` | 无 → 状态/错误码 | 数据根 ACL 修复，M，权限不足由维护入口处理 | `RuntimeDataAclRepairClient`；宿主，审查本地 Data 写入需求 |
| `SetAutoStartEnabled` | `AutoStartEnabled, ShowTrayIcon?` → `AutoStartEnabled` | `AutoStartService`，U，写用户 policy、清旧 Run；设置页主事务另走 bootstrapper/SCM | `FrontendStartupService`；宿主，Portable 无 |
| `GetAutoStartEnabled` | 无 → `AutoStartEnabled` | `AutoStartService`，U，读 policy 和旧 Run 兜底 | `FrontendStartupService`；宿主，Portable 无 |
| `SetTrayIconPolicy` | `ShowTrayIcon` → 状态 | `AutoStartService`，U，写用户 DWORD，TrayHost 仍运行 | 设置/FrontendStartup；宿主，Portable 无 |
| `RestartExplorer` | 无 → 状态/错误码 | `ExplorerRestartService`，D，只处理目标 Session；无数据库提交 | Shell/设置/条目动作；宿主，Portable 需原会话适配 |
| `GetSnapshot` | 无 → `Items`、覆盖 | catalog，U；合并 classic/Win11、baseline/持久状态和现有保护钩子；列表缓存分区 | 工作区/Main；共享 |
| `GetSceneSnapshot` | `SceneKind, ScopeValue?` → `Items`、覆盖 | catalog，U；场景读取并投影状态，按 scope 缓存 | `SceneContextMenuTabViewModel`；共享 |
| `SetEnhanceMenuItemEnabled` | `ScopeValue, DefinitionXml, Enable, CultureName?` → 状态 | catalog，U；编译并写规则、注册表和 `Programs`；本地复查 | `EnhanceMenuRuleService`；共享 |
| `SetDetailedEditRuleValue` | `RuleStorageKind, RulePath, RuleSection?, RuleKeyName, RuleValueKind, RuleValue?, RuleUserSid?` → 状态 | catalog 按 registry/INI 写入或清除；现有 handler 传 DTO SID，未调用 resolver；本地复查 | `DetailedEditRuleService`；待核，普通功能应保留，G1 核对 SID 与调用者绑定 |
| `AcknowledgeItemState` | `ItemId, Item?` → `Item` | catalog，U，定点核对并采纳状态，提交后变更通知 | 工作区/场景；共享，确认 Changed 独立于审批 |
| `SetEnabled` | `ItemId, Enable, Item?` → `Item` | catalog，U；经典/合成项及 Win11 分支，持久期望值/物理回读、恢复记录、变更通知 | 工作区/场景/应用分组等；共享 |
| `GetContextMenuItemState` | `ItemId, Item` → `Item` | catalog，U；权威定点读回，按缓存版本应用 | 工作区及 toggle/ack resolver；共享 |
| `SetShellAttribute` | `ItemId, ShellAttribute, Enable` → `Item` | catalog，U；物理存在验证、事务及读回、变更通知 | 工作区/场景；共享 |
| `SetDisplayText` | `ItemId, TextValue` → `Item` | catalog，U；物理存在验证、写显示名与读回、变更通知 | 工作区/场景；共享 |
| `SetCommandText` | `ItemId, TextValue` → `Item` | catalog，U；物理存在验证、写命令与读回、变更通知 | 工作区；共享 |
| `GetShellSubMenuItems` | `ParentItemId` → `ShellSubMenuItems`、覆盖 | catalog，U；验证父/子来源，按父缓存 | `ShellSubMenuWindowViewModel`；共享 |
| `SetShellSubMenuItemEnabled` | `ParentItemId, SubMenuItemId, Enable` → `ShellSubMenuItem` | catalog，U；直接项或父引用变更/状态持久化，本地回填子项 | 子菜单窗口；共享 |
| `GetRegistryProtectionSetting` | 无 → `RegistryProtectionEnabled` | catalog/store，M；读已保存策略 | 工作区/设置；后台，Portable 无 |
| `SetRegistryProtectionSetting` | `Enable` → `RegistryProtectionEnabled` | catalog，U；专用转换门，批量 ACL 收敛并提交策略；运行中发现可继续施加 | 工作区/设置；后台，Portable 无 |
| `ApplyDecision` | `ItemId, Decision` → `Item` | catalog，U；审批处置写物理状态和 baseline，变更通知 | Approvals/工作区；后台，底层 toggle/delete 仍共享 |
| `DeleteItem` | `ItemId, Item?` → `Item` | catalog，U；校验目标、导出备份、删除/回读、记录恢复与变更通知 | 工作区；共享 |
| `UndoDelete` | `ItemId` → `Item` | catalog，U；核验归属、恢复并回读、更新状态，变更通知 | 工作区；共享 |
| `PurgeDeletedItem` | `ItemId` → 状态 | catalog，M；移除旧单项恢复记录与备份，客户端发移除事件 | 工作区；共享，核对旧入口与新批量入口的作用域差异 |
| `PurgeDeletedItems` | `DeletedItemsPurge`（Id + 删除时间） → 同名结果（已清/跳过等） | catalog，U；验证当前删除代次，按已确认移除更新缓存并向同 SID 广播 | 工作区/类别/分组；共享 |
| `ResetStateDatabase` | 无 → 状态 | catalog/store，M；需通过文档图标恢复记录检查，重置 state/当前 host 备份/OpenWith；本地增加缓存代次 | 工作区/设置；共享，沿用恢复保护并核对后台 section 策略 |
| `GetSpecialMenuSnapshot` | `SpecialKind` → `SpecialItems`、覆盖 | SpecialMenu，S；套用各来源 discovery，按 kind 缓存；ShellNew classic/packaged 分区 | `SpecialMenuPageViewModel`；共享，可剥离后台状态投影 |
| `GetSpecialMenuItemState` | `SpecialItem` → `SpecialItem` | SpecialMenu，S；目标权威回读，本地按版本应用 | SpecialMenu 页面；共享 |
| `SetSpecialMenuItemEnabled` | `SpecialItem, Enable` → `SpecialItem` | SpecialMenu，S；值/键/文件移动或 OpenWith visibility，物理回读及变更通知 | SpecialMenu 页面；共享，OpenWith 有写入观察耦合 |
| `CreateSpecialMenuItem` | `ShellNewCreate/SendToCreate/WinXCreateGroup/WinXCreateEntry/OpenWithCreate/DragDropCreate/GuidBlockCreate/IeMenuCreate` → `SpecialItem?` | SpecialMenu，S；各分支写注册或文件，适用时变更通知/本地刷新；GUID Block 是排除值 | SpecialMenu 页面；共享 |
| `UpdateSpecialMenuItem` | `SpecialItem?` 和 `ShellNewUpdate/SendToUpdate/WinXUpdateEntry/OpenWithUpdate/IeMenuUpdate/DefaultDropEffect` → `SpecialItem?` | SpecialMenu，S；来源校验、编辑或设置默认拖动行为；读回/通知或本地刷新 | SpecialMenu 页面；共享 |
| `DeleteSpecialMenuItem` | `SpecialItem` → `SpecialItem?` | SpecialMenu，S；定点验证后备份/标记删除或 WinX 直接清理；变更通知或本地移除 | SpecialMenu 页面；共享 |
| `UndoSpecialMenuItem` | `SpecialItem` → `SpecialItem?` | SpecialMenu，S；按 kind 恢复物理状态与备份，变更通知 | `UndoDeleteSpecialMenuItemAsync`，SpecialMenu 页面；共享 |
| `PurgeSpecialMenuItem` | `SpecialItem` → 状态 | SpecialMenu，S；永久清理相应删除记录/备份，本地移除 | `PurgeDeletedSpecialMenuItemAsync`，SpecialMenu 页面；共享 |
| `MoveSpecialMenuItem` | `ShellNewSort/WinXMove` → 状态/`SpecialItem?` | SpecialMenu，S；ShellNew Order 写入/ACL 事务或 WinX 移动，刷新顺序 | SpecialMenu 页面；共享 |
| `RestoreSpecialMenuDefaults` | `SpecialKind, ScopeValue?` → 状态 | SpecialMenu，S；恢复已支持 kind 的实际配置/布局，本地刷新 | SpecialMenu 页面；共享 |
| `GetShellNewOrderLock` | 无 → `ShellNewOrderLocked` 或错误 | SpecialMenu，U；物理 ACL 读回，保留不一致/未知语义 | SpecialMenu 页面；共享 |
| `SetShellNewOrderLock` | `ShellNewLock` → `ShellNewOrderLocked` 或错误 | SpecialMenu，S；排序目标 ACL 一次性持久变更，走 ShellNew gate、验证与必要补偿；本地回填 | SpecialMenu 页面；共享，需 elevated token/用户 hive 验证 |
| `InspectOpenWithSources` | `ScopeValue` → `OpenWithInspection` | SpecialMenu，严格 U/native view；只读关联来源 | 无客户端方法；共享检查能力，当前无 UI 暴露 |
| `ReviewOpenWithRegistration` | `SpecialItem, OpenWithProtection?` → `SpecialItems`、覆盖 | OpenWith coordinator，严格 U；null 确认，true 显式授权，false 移除授权；读回、同 SID 通知及缓存合并 | SpecialMenu 页面；后台，普通可见性编辑仍共享 |
| `GetOpenWithPendingReviews` | 无 → `OpenWithReviews` | OpenWith coordinator，严格 U；只读该 SID 队列/版本 | `OpenWithReviewQueueService`/Debug；后台，Portable 无 |
| `DecideOpenWithReview` | `OpenWithReview` → `OpenWithReviews, SpecialItem?` | OpenWith coordinator，严格 U；确认代次和处置，按 scope 持久化，通知及前端回填 | 审核队列；后台，Portable 无 |
| `AnalyzeFileTypeContext` | `FileTypeAnalysis` → `FileTypeAnalysisResults` | `FileTypeSceneMenuService`，M；关联/路径元数据读取，本地消费 | `FileTypeMenuAnalysisPageViewModel`；共享，核对进程用户关联 |
| `CreateSceneMenuItem` | `CreateSceneMenuItem` → `Item` | `FileTypeSceneMenuService`，M；写机器场景，确认物理注册与数据库，变更通知 | Category/场景；共享 |
| `FindRelatedFileTypeMenuItems` | `FileTypeBatchQuery` → `Items`、覆盖 | catalog，U；物理注册查询，按条件缓存 | `FileTypeBatchManagementViewModel`；共享 |
| `DeleteFileTypeBatchItem` | `Item, FileTypeBatchOperationId?` → `Item` | catalog，U；验证实际去重目标，删除记录及备份，变更通知 | 批量页经工作区；共享 |
| `GetFileTypeBatchItemState` | `Item` → `Item` | catalog，U；定点读回；带 `Item` 成功时现有广播规则仍适用 | 批量页经工作区；共享，保留或显式决策该广播 |
| `SetWin11BlockedItem` | `ItemId, DisplayName?, BlockMachine?` → `Items` | catalog Win11 toggle，U；`DisplayName` 当前未被 handler 使用；CLSID 级 Blocked 写入/回读与原值来源，客户端更新关联项 | `Windows11ContextMenuService`；共享 |
| `RemoveWin11BlockedItem` | `ItemId, UnblockMachine?` → `Items` | 同上，U；移除指定 scope，必要时恢复记录，客户端更新关联项 | Windows11 服务；共享 |
| `SetWin11ApplicationEnabled` | `ItemId, Enable, BlockMachine?/UnblockMachine?` → `Items` | catalog，U；应用级多 CLSID 事务与回读，客户端更新关联项 | `SetWin11BlockedItemAsync`/`RemoveWin11BlockedItemAsync` 的 `applicationScope`；共享 |
| `GetWin11BlockedItems` | 无 → `Win11BlockedItems` | `Windows11BlocksService`，U，可含机器读；无列表缓存 | 同名客户端方法，当前无生产调用；共享兼容契约 |
| `GetWin11ContextMenuSnapshot` | 无 → `Items`、覆盖 | catalog，U；应用/CLSID、系统 CommandStore，按 Win11 scope 缓存 | Windows11 服务；共享 |
| `GetWin11ModernContextMenuDisabled` | 无 → `Win11ModernContextMenuDisabled/Status` | `Win11ClassicContextMenuService`，U；读物理用户开关/冲突 | 工作区/设置；共享 |
| `SetWin11ModernContextMenuDisabled` | `Enable`（含义为禁用 Modern） → 上述字段及 `Changed` | 同上，U；写入/回读用户切换键，客户端管理 Explorer 刷新提示 | 工作区/设置；共享 |
| `GetOfficeSuiteCoexistenceStatus` | 无 → `OfficeSuiteCoexistence` | catalog，U；读共存及图标修复状态，本地展示 | SpecialMenu 页面；共享 |
| `GetWpsOfficePendingApprovals` | 无 → `Items`、覆盖 | catalog，U；WPS 独立 baseline/审批路径，列表缓存 | 工作区审批轮询；后台，WPS 普通机制保留 |
| `SetDocumentIconRepair` | `DocumentIconRepairEnabled` → `OfficeSuiteCoexistence` | catalog，U；保存原值/类型、写入并验证，支持反向恢复；结构化错误，本地回填 | SpecialMenu 页面；共享 |
| `SetDocumentIconProvider` | 旧 `DocumentIconProvider` → 明确失败 | 旧名保留，提示更新前端，无写入/权限需求 | 无客户端方法；待核兼容保留，Portable 无功能 |
| `GetEnhanceMenuDefinitions` | 无处理 → 默认失败 | 只有 enum 与日志分类，无分发/变更 | 无客户端方法；待核兼容保留，定义由 Frontend 读取 |
| `GetEnhanceMenuState` | 无处理 → 默认失败 | 只有 enum 与日志分类，无分发/变更 | 无客户端方法；待核兼容保留，Portable 无独立请求 |

ShellNew 锁、GUID Block、Win11 `Blocked`、禁用后保持的菜单值和图标修复恢复记录均为持久管理。GUID Block 使用进程默认 view 的 `HKLM/SOFTWARE/Microsoft/Windows/CurrentVersion/Shell Extensions/Blocked`，需核对与 Win11 显式 native view 路径在不同架构上的效果。Registry Write Protection 改 ACL 并持续处理纳入保护的对象，普通/Office 新项隔离与 OpenWith 显式授权状态机也由 Installer 后台处理。未来清理应识别 ACL 的物理作用域和归属，保留 ShellNew 排序锁以及仍有效的菜单配置和恢复证据。 [GUID Block](../ContextMenuMgr.Backend/Services/GuidBlockMenuService.cs)

## 持久数据兼容

`ContextMenuMgr.package.json` 从应用目录读取，缺失/无效默认 Installer。Installer 根为 `%ProgramData%/ContextMenuMgr`，旧 Portable 使用 `<应用目录>/Data`。现有旧布局兼容为 copy-if-missing，涉及 LocalAppData、ProgramData 及旧 `ProgramData/Data` 的已知路径；目标存在优先，源保留。具体顺序、日志双 lease 和共享目录 ACL 参见[运行时数据](runtime-data-and-logging.md)及 [RuntimePaths](../ContextMenuMgr.Contracts/RuntimePaths.cs)。

| 数据 | 格式、默认值和保留要求 |
| --- | --- |
| `frontend-settings.json` | Web JSON、camelCase、缩进，枚举按数字保存。偏好有语言/主题 `System`、日志 `Warning`、列表 `Compact`、备注、筛选、打开行为、窗口位置/状态和 Win11 开关显示值。`autoStartOnLogin=false`、`showTrayIcon=true`、`keepBackgroundAfterClose=false`、`removePortableServiceOnExit=true`（损坏或不可读时保守禁用自动移除）是宿主偏好。`lockNewContextMenuItems` 默认 false，界面会同步 Backend 策略。共享 `FrontendSettingsFile` 用基于规范化路径的具名 mutex 和 flush/原子替换；升级须保留其余偏好及旧默认语义。 [设置模型](../ContextMenuMgr.Frontend/Services/FrontendSettings.cs)、[设置服务](../ContextMenuMgr.Frontend/Services/FrontendSettingsService.cs)、[文件写入](../ContextMenuMgr.Contracts/FrontendSettingsFile.cs) |
| `context-menu-state.json` | Web JSON、camelCase、缩进，枚举按数字保存。schema 3 envelope 含 `hostIdentity`、`states`、`openWith`，仍读取 schema 2 与无 envelope 字典；未知未来 schema 拒绝加载。`states` 保留物理路径/类型/Id、desired/observed、两个独立 baseline marker、删除时间/备份路径、子菜单引用、ShellVerb/RecycleBin/Win11 provenance 和按 SID 的图标修复原值。旧 `Reappeared` pending 仍可读。后台的 pending、suppression、缺失计数及 OpenWith reviews/rules/代次与隔离 journal 可与普通配置并存，保留记录与启用后台机制是独立决策。保存前验证临时文件，保留上一代 `.bak`，损坏源移入 `Quarantine`。 [状态存储](../ContextMenuMgr.Backend/Services/ContextMenuStateStore.cs)、[状态与监控](state-and-monitoring.md) |
| `host-identity.json` | `RuntimePaths.HostIdentityPath` 是声明路径，源码无读写消费者；当前实际保存的是 state envelope 中的 `hostIdentity`、备份主机目录及缓存 fingerprint。v1 对 UTF-8 `ContextMenuMgr:HostIdentity:v1\|<MachineGuid>\|<SID>` 做小写 SHA-256，12 字符前缀用于路径，完整指纹用于匹配。MachineGuid 优先 Registry64。Backend 使用首次可信的交互 fallback SID 并缓存，Frontend cache 使用进程 SID；不能由后端旧缓存推断每请求身份。Portable 外来 envelope/备份隔离，未知身份保守跳过保存，raw dictionary 只在可验证身份下迁移。 [指纹](../ContextMenuMgr.Contracts/RuntimeIdentityFingerprint.cs)、[后端身份](../ContextMenuMgr.Backend/Services/RuntimeHostIdentityProvider.cs) |
| `backend-protection-settings.json` | Web JSON，`lockNewContextMenuItems=false`；这是后台写保护提交设置。设置与实际 ACL 切换联合判定，删除 JSON 或停服不会还原 ACL。 [设置存储](../ContextMenuMgr.Backend/Services/BackendProtectionSettingsStore.cs)、[ACL 与保护](registry-model.md) |
| `DeletedBackups/`、`Quarantine/` | `.reg` 导出/导入经 `reg.exe`，保留记录与文件、物理路径、host 前缀的关联；Portable 核对当前 host 路径并隔离旧根/异机备份。SpecialMenu 同时有各自的删除布局和物理标记。Portable 目录移动与 Scoop 版本切换须专门验证记录中的绝对路径；保留隔离内容和原值依据。 [备份](../ContextMenuMgr.Backend/Services/RegistryBackupService.cs)、[SpecialMenu](special-menus.md) |
| `Programs/` | 增强菜单启用时生成并由注册表命令引用的实际脚本，须保留材料与引用关系；迁移后的路径仍需验收。 |
| `Cache/MenuLists/` | 可重建的 schema 1 DTO；以完整 host/user fingerprint 和范围读取，校验 kind/version/scope，未知身份只运行内缓存。最后完整结果可含零项，单文件 32 MiB、身份目录 128 MiB；拒绝损坏、未来格式与 reparse 文件。缓存覆盖永远不能充当物理读回或恢复依据。 [菜单缓存](../ContextMenuMgr.Frontend/Services/MenuListCacheService.cs) |
| `Logs/`、`Locks/` | 保留日志 rolling/retention 与状态证据；v1.7.10 起 `Locks/<log>.lock` 持久 lease 加旧 `Logs/<log>.lock` 双租约，固定顺序。已知历史日志按 lease 复制，锁文件不作为普通数据迁移；现有进程可能仍持有句柄。 |
| `*.marker` | `portable-startup-transaction.marker` 是未完成 SCM/用户 policy/JSON 事务的删除 veto；验证提交/补偿后才清除。`keep-frontend-on-service-stop.marker`、`service-stop-reason.marker` 是短暂的旧宿主协调证据。无权读取、残留或缺失的含义按原事务与 SCM 核验。 [启动事务](../ContextMenuMgr.Backend/Hosting/PortableStartupTransaction.cs)、[生命周期](runtime-lifecycle.md) |
| 用户注册表 | `HKU/<SID>/Software/ContextMenuMgr/Frontend` 的 `StartWithWindows`、`ShowTrayIcon` 及旧 `…/CurrentVersion/Run` 的 `ContextMenuManagerPlus.TrayHost` 为宿主策略。`HKU/<SID>/Software/Classes`、Win11 `Shell Extensions/Blocked`、经典模式键、ShellNew 排序/ACL、OpenWith visibility、Office 图标、增强/详细编辑规则写入均含普通配置或恢复依据；同时按真实 HKLM/HKU 和 view 保留物理来源。 [AutoStart](../ContextMenuMgr.Backend/Services/AutoStartService.cs)、[ShellNew](shellnew.md)、[Win11](windows11-context-menu.md) |

## 权限与原交互用户

| 路径 | 已有抽象与仍需 Windows 验收的边界 |
| --- | --- |
| Pipe 用户上下文 | `RunAsClient` 同步取得真实 SID 与已启用 `INTERACTIVE` 组，退出 callback 后解析名称、客户端 PID/SessionId、ProfileList。严格用户命令无其他用户 fallback，普通扫描可标不完整。后台 fallback 属于独立入口。Portable 跨账户 UAC 必须从可验证的原调用/桌面 token 获得原 SID、SessionId、profile 和已加载 hive，并拒绝证据矛盾；提升账户的 HKCU、用户名、SessionId 或 DTO SID 不能单独作为用户身份。 [Resolver](../ContextMenuMgr.Backend/Services/BackendUserContextResolver.cs)、[权限与 IPC](process-and-privilege-flows.md) |
| 物理 hive/view | 显式 HKU 访问与大部分 catalog/Win11/OpenWith 读写只需正确 hive、RegistryView 和 ACL；OpenWith、Win11 Blocked 及 ShellNew 使用原生视图，classic 及脚本还须按操作核对默认/WOW64 视图。`FileTypeSceneMenuService` 无认证参数的关联检查、Frontend 字典的直接 HKCU 读、注册表/目录打开及前端缓存 SID 都需要在跨账户提升下回查。 |
| 受保护写入 | `ScopedRegistryPrivileges` 临时退出 impersonation、复制私有 token 并启用 `SeTakeOwnershipPrivilege/SeRestorePrivilege`，跟踪 absent/denied/enable；机器 ShellVerb 窄路径只取得必要 rights，核验所有者、继承和 ACL 恢复。管理员组不能证明上述权限存在。ShellNew 独立 ACL/backup restore 路径还涉及 `SeBackupPrivilege`/`SeSecurityPrivilege`，需分别验证，保留取消、失败补偿和原 ACL。 [Privilege scope](../ContextMenuMgr.Backend/Services/ScopedRegistryPrivileges.cs)、[注册表模型](registry-model.md)、[ShellNew](shellnew.md) |
| ShellNew merged Classes | `FrontendUserClassesRoot` 依赖 Service 的 `WTSQueryUserToken`（LocalSystem/SeTcb），核验 token SID/Session、loaded profile，持 token 与 `RegOpenUserClassesRoot` handle。普通提升管理员不能沿用这条 WTS 路径；G2 需验证原交互 token 和物理双来源、部分扫描、锁和权限不足。 [Merged root](../ContextMenuMgr.Backend/Services/FrontendUserClassesRoot.cs) |
| 包与资料目录 | Packaged COM/ShellNew 以显式 SID 枚举用户包及 Package/AppX manifest；API 对其它用户的访问、稀疏包/更新和不完整发现须用原账户及跨账户分别验证。SendTo/WinX 用用户 context、用户 shell folder 与 profile/local app data；重定向目录、环境变量、`.lnk`/WinX hash 和文件写入权限需真实用户环境。 [Win11](windows11-context-menu.md)、[SpecialMenu](special-menus.md) |
| Explorer、COM 和 ProbeHost | Explorer restart 先验证 SID，再定位原 Session。现有实现只结束同 Session 的 Explorer，返回结束数量，Explorer 是否由系统重新启动须独立验证。普通元数据读取及快捷方式/系统 COM 要核对原用户环境、视图和 apartment；第三方 COM 只在独立 ProbeHost。现有 `Process.Start` 继承 Frontend 启动上下文，未来提升 Portable 须用原交互用户的适当非提升 token 启动 probe，验证临时文件 ACL、架构与回收。TrayHost/登录启动的 WTS/WTS-launch 权限留 Installer。 [Explorer](../ContextMenuMgr.Backend/Services/ExplorerRestartService.cs)、[ProbeHost](deep-analysis-probehost.md) |

## 请求、通知与并发

- **传输与预算。** Web JSON、UTF-8 换行分帧、`CorrelationId` 匹配。普通请求各自开一个 Pipe，连接上限 2 秒，客户端 `Task.Run` 后异步处理；同连接顺序消费，不同连接并行。无全局 FIFO。总体预算由调用者决定（例如快照 20/30 秒，WPS 5 秒，mutation 独立 budget）。客户端取消或断开停止等待，不等价于服务端取消或回滚；server token 主要随 Service 生命周期。异常/缺必填参数是失败响应，响应丢失或超时是结果不确定，需定点读回或显式刷新。订阅另有连接/ACK/断线重连。 [客户端](../ContextMenuMgr.Frontend/Services/NamedPipeBackendClient.cs)、[服务端](../ContextMenuMgr.Backend/Services/NamedPipeBackendServer.cs)
- **提交与同步。** Catalog 可重入的持久状态门包住整段 Load → Merge → Save；store 的实例门保护文件与 OpenWith section 合并，OpenWith coordinator 有独立门，Registry Write Protection 有转换门，ShellNew 有扫描/写入门及忙/不完整响应。该组合依赖同一宿主共享实例；前后两个进程分别打开同一状态文件会绕过实例门。设置 mutex 和日志 lease 也不能替代 state 数据库单 writer。文件原子替换防截断，不能解决两个提交基于同一旧状态。 [Catalog](../ContextMenuMgr.Backend/Services/ContextMenuRegistryCatalog.cs)、[状态存储](../ContextMenuMgr.Backend/Services/ContextMenuStateStore.cs)、[SpecialMenu](../ContextMenuMgr.Backend/Services/SpecialMenuService.cs)
- **提交结果与广播。** 客户端在发送每个请求前生成或保留 `ClientOperationId` 并登记，服务端保持 handler 已设或继承的 ID。广播先于写直接响应，直接写失败仍可收到已提交结果。连接写有 semaphore，服务端逐个投递并清除断开的订阅；OpenWith 和清理广播按认证 SID 限定，客户端还按进程 SID 过滤。其余典型通知有 monitor 隔离成功后的 `ItemDetected`（去重窗口）、`OpenWithChanged`、恢复/foreign-host `ServiceMessage`、`ServiceStopping`。本地操作 ID 负责去重，不提供幂等重试；失败/取消后仍可接收晚到权威成功。外部删除/重建须靠观察及权威查询确认。
- **缓存与界面。** 客户端给查询捕获版本及代次，在途 mutation 覆盖旧扫描；不完整分区可回填最后完整结果，权威缺失 tombstone、清理结果或 reset 会阻止旧扫描重新引入项。按 ops 的 `ItemsRemoved` 与 `NotificationReceived` 驱动页面更新，工作区/页面切到 WPF Dispatcher，跟踪取消和导航代次。普通 mutation 的本地可见结果和跨页面失效必须保留；未来没有持续外部监控时，以用户刷新/主动权威读取观察外部变化。
- **关停。** App 有界等待控制 Pipe/DI，列表缓存有独立 flush；后台取消 service token、等待 monitor，停止 Pipe accept 并等待 ShellNew mutation gate，尚无统一 client task drain。未来直调须保留 UI 非阻塞、等待预算与实际完成的区分、事务门、异常与提交结果、跨页面失效、晚通知和在途关闭规则。 [生命周期](runtime-lifecycle.md)、[前端](frontend-wpf-ui.md)

## 既有覆盖与缺口

已按完整测试项目和 TRX 清单审查，重点范围如下。真实 Windows 临时注册表与隔离 token 测试保留其原覆盖。

| 契约 | 当前测试入口与待验证 |
| --- | --- |
| 状态/恢复/写入竞争 | `ContextMenuStateStoreTests`、`InternalToggleBaselineTests`、`ExternalChangeStateMachineTests`、`ContextMenuMonitoringRulesTests`、`DeletedItemsPurgeTests`、`OfficeSuiteCoexistenceApprovalTests`、`DocumentIconRepairMutationTests`。已有 schema/恢复、baseline、state merge；仍需 legacy Portable 真实 Data 的 same-host/foreign-host、切换账号和旧新进程 writer 排除。 |
| 注册表、ACL、物理来源 | `ProtectedRegistryMutationTests`、`ProtectedRegistryPrivilegeTests`、`RegistryProtectionTransitionTests`、`RegistryProtectionAclTests`、`ShellVerbMutationTransactionIntegrationTests`、`FileTypeBatchPhysicalRegistrationTests`、`ClassicShellExtensionRegistryMoveTests`、`ClassicMutationPackageIsolationTests`、`RecycleBinPinToStartTests`。待验证真实 LocalSystem 与提升管理员（含缺权限）的等价性、失败回滚和旧服务消失后的 ACL。 |
| 种类与 Win11 | `OpenWithRegistrationTests`、ShellNew discovery/physical/lock/order 系列、`PackagedContextMenuDiscoveryTests`、`PackagedShellNewDiscoveryTests`、`Windows11BlockedMutationTests`、Win11 经典菜单、关联安全、增强规则、Shell 子菜单 capabilities/references。覆盖模拟/临时来源、分支与事务；需实际 package user 枚举、原用户 token、Explorer refresh、x86 on x64/arm64 视图及非提升 COM probe 验证。 |
| IPC/UI/生命周期/日志 | `MutationOutcomeTests` 覆盖请求并行/晚通知/响应 write 失败/读回，`FrontendControlClientTests` 涵盖另一条真实激活 Pipe，`ServiceLifecycleTests` 涵盖所有权与事务策略，另有 workspace scope、`MenuListCacheTests`、`RollingLogFileTests` 和 UI 合并选择测试。待验证实际 Service 跨 SID/Session、关闭在途操作、Tray 通知及 WPF Dispatcher 响应性。 |

G0 新增两个永久特征测试。一个以合成 schema-2 非空 JSON 驱动 OpenWith 首次写入并重新打开，守住普通 desired/observed 与删除备份、时间和物理来源。另一个用独立固定 v1 指纹写缓存，再用生产身份计算重开，避免同一个更新后的算法在测试读写两端掩盖旧缓存不兼容。后端 Portable 身份选择与真实恢复仍需集成验证。

## G-1 发布资产与分发基线

G-1 将 `2cc7dc5` 的落盘清理替换为 [Directory.Build.targets](../Directory.Build.targets) 的 MSBuild 筛选。[Build.Common](../Scripts/Build.Common.psm1) 按目标清目录，依次发布 Frontend、Backend、TrayHost，然后仅暂存所需 ProbeHost EXE 与 nlohmann MIT license，`Assert-NoUnneededPublishFiles` 拒绝意外 PDB 和不允许的 satellite。`FilterUnsupportedPublishSatelliteMetadata` 仅 `_IsPublishing=true` 时过滤项目卫星元数据。`FilterDistributionPublishAssets` 在 `ComputeResolvedFilesToPublishList` 后剔除 PDB 与带非允许 culture 的 `.resources.dll`，先于 publish deps 和 SDK `PrepareForBundle`，后者据此生成 `FilesToBundle`。三个宿主 `CopyOutputSymbolsToPublishDirectory=false`、`IncludeSymbolsInSingleFile=false`，SDK 正常开发符号仍生成。允许中性主程序集资源和 `en/en-US/zh/zh-CN/zh-TW/zh-Hans/zh-Hant`，实际只分发源/依赖提供的文化。

**G-1 差异。** `SatelliteResourceLanguages` 对三个宿主是无条件属性，SDK 普通构建的资产解析也消费它。Debug x64 SC 的 `bin/debug_win-x64` 从空属性的 160 个 satellite 降到默认的 28 个，两次均生成 6 个独立 PDB；无 RID 的 Debug FD 两次均为 6 个 satellite、8 个独立 PDB。项目自身的卫星元数据筛选有 publish 条件。另有两个第三方 DLL 原已内嵌 Portable PDB，`CommunityToolkit.Mvvm.dll` 和 `WinRT.Runtime.dll`；七个当前布局均检出，产品程序集及 ProbeHost 无内嵌 Portable PDB。资产筛选保留第三方 DLL 字节，两者也在本次 SDK `FilesToBundle` 诊断集合中，G3 应把这一点纳入实际 bundle 验证。

| 现有目标 | 沿用的布局 |
| --- | --- |
| x86 SC/FD Installer，x86 SC ZIP | 主托管 apphost x86；ProbeHost x86 `0x014C` |
| x64 SC/FD Installer，x64 SC ZIP | 主托管 apphost x64；ProbeHost x64 `0x8664` + x86 |
| arm64 SC/FD Installer，arm64 SC ZIP | 主托管 apphost arm64；ProbeHost arm64 `0xAA64` + x64 + x86 |
| FD AnyCPU Portable ZIP | 使用 `UseAppHost=true`、`PlatformTarget=AnyCPU` 且无 RID。本机三入口 DLL 为 `ILOnly`，三 apphost 实测均为 x64；ProbeHost x86/x64/arm64，普通 Debug 默认 x86/x64 |

相对布局包含三个产品应用、Contracts、deps/runtimeconfig、第三方托管依赖、`Assets/`、`Resources/`、culture 子目录、`ProbeHost/<arch>/ContextMenuMgr.ProbeHost.exe`、`ThirdPartyNotices/nlohmann-json-LICENSE.MIT` 与 `ContextMenuMgr.package.json`。SC 另含 CoreCLR 和 Windows Desktop runtime，FD 依赖适用的已安装 .NET。六个 Setup EXE 外层均为 Inno x86 PE，目标架构由 Inno 配置及内部 payload 确定。Installer 按构建输出 inventory 将产品/运行时文件纳入 Inno，升级只按既定清单和部署模式边界清理，以 `deployment.ini` 记录状态，另有兼容旧布局的恢复路径。 [安装部署](installer-deployment.md)、[构建与发布](build-and-release.md)

[build](../build.ps1) 按目标并行，[Build-Target](../Scripts/Build-Target.ps1) 的 `installer` 处理 3 RID × SC/FD，SC 各追加 ZIP，`portable` 只接受 FD AnyCPU。[manual-release](../.github/workflows/manual-release.yml) pin 源提交与版本、验证 tag 和重复发布，收齐十资产、计算 SHA-256、创建并回读 draft。发布后 [package workflow](../.github/workflows/publish-package-managers.yml) 按 API 实际名称下载并计算 hash，按 prerelease 分渠道，Scoop 只选三种 SC ZIP，winget 只选三种 SC Setup，分别生成和校验。dry-run 仍有下载、校验、工作目录和 diff，真实写入才 push/fork/PR。[GitCode 同步](../Scripts/Sync-GitCodeRelease.ps1) 手动触发，先核验同 tag/commit，镜像已发布资产的名称、大小、可用 hash 及部分补传。详见[包管理器](package-managers.md)及[发布](build-and-release.md)。G5 目标是六个 Installer EXE、六个 RID-specific Portable EXE；G0 沿用十资产矩阵。

GitHub 已发布不可变 Release `v1.8.1`（ID `452588673`，目标 `36f2ac1db1dc08b7e73f00fa016f8682e730ab5b`）早于 G-1。以下是实际资产名称和字节数。四个远端 ZIP 均下载并按 API SHA-256 与长度核验；远端 Installer 数值来自 API。本地 Installer 对应的文件数/字节是 Inno 输入目录，ZIP 对应展开内容。 [GitHub Release](https://github.com/PLFJY/ContextMenuMgr/releases/tag/v1.8.1)

| 资产 | G0 本地字节 | 本地 payload 文件 / 字节 | 已发布字节 |
| --- | ---: | ---: | ---: |
| `ContextMenuMgrPlus-1.8.1-arm64-framework-dependent-Setup.exe` | 10,208,753 | 41 / 40,799,611 | 13,896,859 |
| `ContextMenuMgrPlus-1.8.1-arm64-self-contained-Setup.exe` | 47,770,735 | 297 / 188,727,784 | 52,555,832 |
| `ContextMenuMgrPlus-1.8.1-arm64-self-contained-portable.zip` | 69,351,543 | 297 / 188,727,783 | 79,269,634 |
| `ContextMenuMgrPlus-1.8.1-framework-dependent-portable.zip` | 13,075,451 | 44 / 40,976,473 | 13,552,263 |
| `ContextMenuMgrPlus-1.8.1-x64-framework-dependent-Setup.exe` | 10,051,078 | 40 / 40,299,375 | 12,641,006 |
| `ContextMenuMgrPlus-1.8.1-x64-self-contained-Setup.exe` | 53,095,055 | 297 / 176,240,138 | 56,645,131 |
| `ContextMenuMgrPlus-1.8.1-x64-self-contained-portable.zip` | 74,165,519 | 297 / 176,240,137 | 81,927,199 |
| `ContextMenuMgrPlus-1.8.1-x86-framework-dependent-Setup.exe` | 9,857,210 | 39 / 39,649,135 | 11,279,319 |
| `ContextMenuMgrPlus-1.8.1-x86-self-contained-Setup.exe` | 48,907,059 | 295 / 166,468,693 | 51,450,855 |
| `ContextMenuMgrPlus-1.8.1-x86-self-contained-portable.zip` | 69,033,862 | 295 / 166,468,692 | 74,686,654 |

迁移基准使用 Scoop 仓库 `841757330da9416375ab0e917aac07efb4dddc57` 的 stable manifest。其 x64 ZIP SHA-256 为 `36808b5a482a4ab9b04d9747375cee25b5a0e4b88173a8c45d942cd4ceac6176`。ZIP 含 435 文件及 x64/x86 ProbeHost，作为有服务的旧版本输入；固定 manifest 的三架构 URL/hash 可复现下载。 [固定 Scoop manifest](https://github.com/PLFJY/scoop-bucket/blob/841757330da9416375ab0e917aac07efb4dddc57/bucket/contextmenumgrplus.json)、[x64 ZIP](https://github.com/PLFJY/ContextMenuMgr/releases/download/v1.8.1/ContextMenuMgrPlus-1.8.1-x64-self-contained-portable.zip)

## 旧 Portable 迁移输入

当前 Portable ZIP 仍使用服务。固定 `ContextMenuManagerPlusService`（已有显式兼容清理旧名 `ContextMenuManagerService`）、注册账户/类型、`ImagePath` 与 `--service`、SCM status/PID/start mode/recovery、已解析实际 EXE/目录/包标识与产品版本，是不同的归属证据。旧启动/退出使用 lifecycle mutex、前端 caller SID/Session 与其它 Pipe/Tray/Frontend 消费者确认，清理还核验目录/文件 identity、reparse 与政策。SCM mark-delete 和最终 absent 分开；MMC/其它句柄可能延迟后者。Scoop stable `contextmenumgrplus`、beta `contextmenumgrplus-beta` 各自 `persist: Data`，互相排斥，`apps/<id>/<version>`、`current` junction 与 `persist/<id>/Data`/版本内 Data junction 应分别识别，数据可能比旧版本目录寿命长。Portable 的 `appId` 和服务名与 Installer 共用，服务名本身不证明归属。 [旧 Portable 启停](runtime-lifecycle.md)、[清理代码](../ContextMenuMgr.Backend/Hosting/PortableServiceCleanup.cs)、[包管理器](package-managers.md)

| G4 情形 | 当前可采集的证据与保留要求 |
| --- | --- |
| 旧服务运行 | SCM PID/实际映像、注册参数、固定路径、manifest、Pipe 和相关 Session 消费者。先确认原版本及归属，采集停止/持久写入终态，防止并发打开 Data；旧策略与 ACL 仍可能生效。 |
| 服务已停 | SCM 配置/账户/路径、文件和 manifest、Data markers、logs/policy 仍可读，PID/连接证据缺失；停止本身不能证明可卸载或 ACL 已恢复。 |
| 注册残留且 EXE 缺失 | 路径字符串、启动模式、Data/persist/日志、包管理器元数据可旁证，进程及文件身份不可复核；保留归属未知的分支。 |
| Installer 或不同副本拥有服务 | 对照注册与实际 EXE、包标识、目录及进程证据。Installer 与其它非相关注册/TrayHost 应保留；不按固定服务名或当前 Portable 目录删除。 |
| Portable 已搬动 | SCM 指向旧位置，现有 Data/指纹与备份记录可能仍有效，但路径不是归属凭据；核对旧绝对 `.reg`、脚本/快捷方式引用及当前主机。 |
| Scoop 已切换 `current` | 分别解析旧 `ImagePath`、版本目录和 junction 目标；持久 `Data` 可能正被两版本引用。不要把 `current` 解析结果当作已注册服务的历史目标。 |
| Scoop 已清理旧版本 | 版本目录/EXE 可能无法验证；保留 SCM 配置、persist、已知渠道 manifest/digest 与日志，处理证据不足。Data junction 不等于可删除源目录。 |
| 保护 ACL 残留 | 核实保存策略、受影响的物理 hive/view、当前 ACL 与原恢复依据。在无活跃服务和缺设置时也验证 ACL；不得套用递归解锁、删除普通 ShellNew lock 或改变 GUID Block 等菜单配置。 |

## G1 入口与后续验收

1. 以本表和数值 enum/DTO 为核对依据，逐命令保留现有成功/拒绝、完整性、确认删除、缓存失效、通知关联及提交顺序。为无处理/拒绝命令保留现有响应，旧 API 退出另作兼容决定。明示 `SetDetailedEditRuleValue` DTO SID 的现有边界和 `GetFileTypeBatchItemState` 广播特例。
2. 提取顺序以 Installer 的现有 Pipe 为验收路径。业务操作依赖显式上下文，SCM/WTS/通知订阅/监控/保护留在宿主 wiring；区分 OpenWith 普通编辑观察、显式授权与后台轮询。状态库使用单一协调 writer，维持 catalog/store/独立 section 的提交与恢复契约。复跑 G0 自动套件并解释每个新增失败/跳过变化。
3. G2/G4 集成矩阵覆盖同账户提升、标准用户输入不同管理员凭据、锁屏/切换/RDP/未加载 hive，LocalSystem 与管理员权限 present/enabled/denied，普通 registry views、Win11 包和 ShellNew discovery，定点复读、晚通知/并行/关停、所有权不确定、Scoop junction/版本切换及残留 ACL。以真实原用户非提升 token、三种 ProbeHost PE 和实际 COM 激活为探测验收。
4. G3/G5 对照实际十资产，三架构 SC/FD 与 Stable/Beta 分开，查 bundle 输入、嵌入调试目录、许可、资源与运行时，核验 clean/增量/并行 publish。PowerShell 5.1/pwsh 的参数绑定/含空格路径、`ConvertFrom-Json` 单对象及数组、实际文件名/digest、dry-run 与写入路径、token 权限、短 workspace/sparse checkout/长路径、临时输出隔离均独立核对。远端发布/CLA/审核与本地生成是独立验收点。

## G0 验证记录

Windows 11 x64（OS build `26300`）、.NET SDK `11.0.100`、测试项目 TFM `net10.0-windows10.0.17763.0`、PowerShell `7.6.9`、MSVC `14.52.37025`、Inno Setup `6.7.3`。输出位置为 Git 忽略的 `artifacts/g0-baseline/current/`，含 TRX、控制台/构建日志、Release API 快照、固定 Scoop manifest、ZIP 与 `inventory/*.csv` 的相对路径、字节、SHA-256、PE Machine/CorFlags 及嵌入 PDB 标记。产品没有启动，本次证据来自源码、专项测试和实际构建文件。

| 层次 | 执行结果 |
| --- | --- |
| 静态 | 对照 66 个 enum 与 66 个表行、源码链接、调用者及 host hooks；核对序列化、物理路径、SDK target 顺序、工作流渠道和升级脚本。 |
| Debug 全量测试 | 原始基线 550 通过、4 跳过、0 失败，共 554，50 个测试类。G0 加测试后 552 通过、相同 4 跳过、0 失败，共 556。跳过项为 `IsolatedTokenWorker`（由父测试在子 testhost 执行）、`ConcurrentAndFailedScopes_LeaveProcessAndCallerTokensUntouched`、`ProtectedFallback_PreservesSecurityAndRollbackProvenance` 和 `PendingReviews_UserAndMachineRegistrationsWithSameNameStaySeparate`（需隔离的管理员/LocalSystem testhost）。沙箱首次运行在 CLI telemetry registry 权限处失败；关闭 telemetry 并用本机 Windows 环境得到上述结果。 |
| 实际 Release | 七个 `Build-Target.ps1` 目标在各自干净 publish 目录、并行度 2 下均返回 0。七个 payload 及四个本地 ZIP 无独立 PDB、无不支持的 satellite；SC 有 CoreCLR/桌面运行时，FD 无 CoreCLR；三入口 apphost 与 ProbeHost 架构、必需产品文件及 license SHA-256 已检查。Frontend/TrayHost 的英语 `Strings.resx` 随中性资源进入主程序集，两种中文卫星保留。 |
| G-1 父版本比较 | 用同机工具链单独从 `2cc7dc5` 构建 Release x64 SC。两版筛后路径集合相同，均 297 文件、176,240,138 字节。六个重新构建的产品二进制哈希有差异（四个托管 DLL、两个 ProbeHost），其它 291 个文件哈希相同；此比较建立布局和依赖保留基线。 |
| 普通 build 与增量 | 分别构建 Debug FD 和 Debug win-x64 SC，每种跑默认和空 `SatelliteResourceLanguages`。另以 Windows PowerShell 5.1、原 x64 FD 中间目录重复 Release publish，仍生成同一 payload 路径集合，40 文件中仅 `ContextMenuMgr.package.json` 字节不同，5.1 的 `-Encoding UTF8` 加 BOM，解析值仍为 `Installer`。 |
| Bundle 输入 | 对 Release win-x64 SC Frontend 执行到 SDK `PrepareForBundle`。260 个条目无独立 PDB，26 个卫星只来自 `zh-CN/zh-TW/zh-Hans/zh-Hant`。此项覆盖现有过滤的 bundle 输入，尚未运行未来 Portable bundle。 |
| 包管理器与外部 | 两个 pwsh 离线 fixtures 通过，固定 Scoop stable manifest 通过 validator，三组 URL/hash 对上实际已下载 ZIP。Windows PowerShell 5.1 跑包管理器 fixture 在继承的 `New-WingetManifest.ps1` 中文 UTF-8 无 BOM 处解析失败，当前 workflow 使用 pwsh。未写入 Release、GitCode、Scoop 或 winget。 |

下列命令以仓库根为工作目录。Release 单目标命令按表替换参数，`anycpu` 对应 `portable`。实际对照使用分开的输出目录，`-LogPath` 保存每目标 transcript。

```powershell
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$r = Join-Path (Get-Location) 'artifacts/g0-baseline/current'
dotnet test .\ContextMenuMgr.Tests\ContextMenuMgr.Tests.csproj -c Debug --logger 'trx;LogFileName=baseline-unmodified-debug.trx' --results-directory $r
dotnet test .\ContextMenuMgr.Tests\ContextMenuMgr.Tests.csproj -c Debug --logger 'trx;LogFileName=g0-debug.trx' --results-directory $r
pwsh -NoLogo -NoProfile -File .\Scripts\Build-Target.ps1 -Kind installer -Configuration Release -Platform win-x64 -DistributionMode self-contained -PublishRoot "$r/publish" -DistRoot "$r/dist" -LogPath "$r/installer-win-x64-self-contained.log"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File .\Scripts\Build-Target.ps1 -Kind installer -Configuration Release -Platform win-x64 -DistributionMode framework-dependent -PublishRoot "$r/publish" -DistRoot "$r/incremental-dist" -LogPath "$r/incremental-x64-fd.log"
pwsh -NoProfile -File .\Scripts\PackageManagers\Test-PackageManagerScripts.ps1
pwsh -NoProfile -File .\Scripts\Test-Sync-GitCodeRelease.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Scripts\PackageManagers\Test-PackageManagerScripts.ps1
```

开发对照命令为 `dotnet build .\ContextMenuMgr.Frontend\ContextMenuMgr.Frontend.csproj -c Debug --artifacts-path $d "-p:BaseOutputPath=$d\bin\"`，每种有/无 `-p:SatelliteResourceLanguages=`，SC 对照另加 `-r win-x64 --self-contained`。SDK 诊断为 `dotnet msbuild .\ContextMenuMgr.Frontend\ContextMenuMgr.Frontend.csproj -restore '-t:Build;PrepareForPublish;ComputeResolvedFilesToPublishList;PrepareForBundle' -p:Configuration=Release -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:_IsPublishing=true -p:SkipProbeHostArchitectureBuild=true "-p:ArtifactsPath=$d" "-p:BaseOutputPath=$d\bin\" -getItem:FilesToBundle '-getProperty:IncludeSymbolsInSingleFile,CopyOutputSymbolsToPublishDirectory'`。

仍需按 G1 入口所列矩阵验证真实 SCM、跨账户 UAC、多 Session、Explorer、包发现、Tray/通知、ACL 恢复、旧新 Data 并发排除和 Scoop 切换。还需 on-device x86/arm64 与真实 Inno 安装/升级、实际 bundle、端到端 Stable/Beta 发布及包管理器流程。上述本地字节清单与测试结果不包含这些运行时或平台验收。
