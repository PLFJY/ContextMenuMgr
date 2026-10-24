# Windows 11 应用菜单与控制域

本文负责 Win11 来源、声明身份、应用/CLSID mutation、blocked 恢复和特有 UI。入口：[Windows11ContextMenuCatalog](../ContextMenuMgr.Backend/Services/Windows11ContextMenuCatalog.cs)、[Windows11BlocksService](../ContextMenuMgr.Backend/Services/Windows11BlocksService.cs)、[Windows11BlockMutation](../ContextMenuMgr.Backend/Services/Windows11BlockMutation.cs)、[Windows11ClsidImpactIndex](../ContextMenuMgr.Backend/Services/Windows11ClsidImpactIndex.cs)、[ContextMenuRegistryCatalog](../ContextMenuMgr.Backend/Services/ContextMenuRegistryCatalog.cs) 的 SetWindows11EnabledAsync，及前端 [Windows11ContextMenuService](../ContextMenuMgr.Frontend/Services/Windows11ContextMenuService.cs) / [页面 VM](../ContextMenuMgr.Frontend/ViewModels/Windows11ContextMenuPageViewModel.cs)。

## 来源、发现与身份

| 来源 | 模型/控制域 |
| --- | --- |
| PackagedCom | 认证用户安装的 AppX/MSIX manifest 声明；CLSID 的用户/机器 Blocked 值 |
| SystemCommandStore | HKLM Explorer CommandStore 白名单 Windows.SendToMyPhone、Windows.Share；ShellVerbVisibility，不是 GUID Block |

支持门槛为 Windows >=10.0.22000。无用户上下文 catalog 跳过；专用 Pipe 与 GetSnapshot、通用 Win11 SetEnabled/ApplyDecision 使用认证 SID，不 fallback 到其它登录用户。mutation 需有效 account SID 与 loaded hive/native view，不用服务 HKCU；blocked mutation 不需要 SessionId。前端 DI 始终注册页面/服务，正常导航按 OS 隐藏，防意外导航解析崩溃；不用 HKCR/PackagedCom 存在性作支持门槛。

[PackagedContextMenuDiscovery](../ContextMenuMgr.Backend/Services/PackagedContextMenuDiscovery.cs) 用 PackageManager.FindPackagesForUser(SID)，按 full name 去重，读 AppxManifest.xml（缺失时 bundle manifest fallback）。按 AppX namespace family、Extension Category 与元素 LocalName 解析 windows.fileExplorerContextMenus、windows.comServer，关联 SurrogateServer/ExeServer class CLSID 与 verb。缺 manifest/XML/无效 CLSID/无法关联等局部跳过/告警，不证明完整发现。

声明保留 package full/family/display/publisher/install metadata、context types、Verb ID、server/class metadata；未声明 server path 则 FilePath 为空，不能用 package 目录伪造。manifest 标题不是 IExplorerCommand.GetTitle 的 Explorer 动态结果。

包身份、安装路径与 manifest 是发现依据；`Package.DisplayName` / `PublisherDisplayName` 只是展示信息。服务读取这两个本地化字段失败时，分别回退到包名称与空发布者，继续读取 manifest，不跳过整个包、不标记发现不完整。日志 `PackagedContextMenuDisplayMetadataFallback` 汇总具体失败字段、HRESULT 和 `DiscoveryUnaffected=True`；包枚举、身份或 manifest 读取/解析失败仍记录 `PackagesSkipped` / `ManifestSkipped`，标记不完整并保留前端未能确认的旧条目，不能据此推断卸载。

Packaged Id 为 `win11|{clsid}|{category}|{packageFamily}`；不同 family 同 CLSID/category 不覆盖，同 family 共存 full names 保存在引用元数据。RegistryPath 是逻辑 PackagedCom/Package/.../Class，BackendRegistryPath 是用户 Blocked 共享键，**不能按单菜单 key 删除**。普通 Delete/审核 Remove 拒绝；package 卸载由 Windows 管理。

System 项为 `win11-system|{commandKey}`，真实 HKLM CommandStore 路径；必须有 MUIVerb/ExplorerCommandHandler/command 等命令元数据，不将任意 Windows.* 加入页面。显示名用 ShellMetadataResolver。

### 缓存与性能边界

[PackagedContextMenuScanCache](../ContextMenuMgr.Backend/Services/PackagedContextMenuScanCache.cs) 按 SID 缓存 package 发现 30 秒，并发复用正在扫描；调用方取消不取消共享扫描。Blocked 不缓存，每次投影重开。常规快照重建 registry impact，手动 mutation 可复用最近 30 秒同 SID 声明/引用，缓存不保证刚安装/卸载立即可见。部分 package 错误汇总日志，不改变发现语义。

已有 regular baseline 时 mutation 只构建 Win11 projection，不扫 classic；首次缺 baseline 仍完整采纳常规来源，不能建立局部 baseline。查询、手动/审批/隔离/纠偏与 state 保存共用 catalog 持久 gate；不由局部操作清理 classic 状态。状态分类统一见[状态与监控](state-and-monitoring.md)。

Win11 页面恢复独立的前端设备/用户缓存，按应用 family 差量更新行和声明成员，不清空重建未变化行；缓存不替代 blocked 物理读回或持久恢复元数据。package 发现覆盖随三十秒缓存和 mutation discovery 一起保留。声明缺失且发现完整才可返回结构化已移除；局部 package 失败保持旧条目。列表刷新、迟到结果和动画见[前端](frontend-wpf-ui.md#列表缓存差量刷新与通知)。

## 应用操作与单 CLSID 操作

页面/搜索按 package family 一行，混合状态显示部分开启并提供全部开启/关闭。`SetWin11ApplicationEnabled` 的 ItemId 是已存在声明，Enable 为目标，后端从已验证 snapshot 解析同 family 全部目标，按 CLSID 去重并在一个 gate 内完成；前端只发一个请求，不循环写每个声明。

旧 SetWin11BlockedItem/RemoveWin11BlockedItem 仍针对单 CLSID 或单 SystemCommandStore，审核/监控按单项控制域。相同 CLSID 在其它 family/category 的关联声明也需同步状态，不代表它们拥有独立 Blocked 值。若共享影响其它具名应用/传统菜单，前端用普通语言确认真实影响；不把内部风险索引直接变成不可用开关。

新增命令在 PipeCommand 末尾，旧服务拒绝整个应用操作，不 fallback 多次旧命令造成部分成功。对于 v1.7.10 及以前 missing-required-data 响应，仅新命令解释为需修复/更新服务；维护者明确不再支持旧服务后才可移除此文案桥接。

## Packaged COM Blocked 写入与恢复

| 作用域 | native registry 路径 |
| --- | --- |
| 用户 | `HKEY_USERS/<SID>/Software/Microsoft/Windows/CurrentVersion/Shell Extensions/Blocked` |
| 机器 | `HKLM/SOFTWARE/Microsoft/Windows/CurrentVersion/Shell Extensions/Blocked` |

禁用只写当前用户版本化归属字符串 `ContextMenuMgrPlus.Win11Blocked.v1|<SID>|<CLSID>`；已有第三方禁用值满足关闭目标，保留不认领，仅本程序重复别名可归一。开启是用户明确恢复意图，允许自动清理选定 CLSID 的旧友好名、无花括号/重复 GUID 别名及其它类型值；与 OpenWith Unknown NoOpenWith 的拒绝启用语义不同。

1. 准备全部 CLSID 用户/可选机器计划，捕获原始名称、类型、精确数据、路径、SID/CLSID。
2. 修改前将恢复记录保存到 PersistedContextMenuState.Windows11BlockBackups。增量属性兼容 schema 3，普通保存保留、相同原数据不重复积累、显式 reset 会清除；恢复元数据不参与 Added/Modified。备份保存失败则无 registry mutation。
3. 按目标使用 RegistryValueMutationTransaction 条件写入，native 新句柄回读全部值及所有关联声明，再一次提交 desired/observed 并通知 monitor。
4. catchable 写入/回读/最终保存失败逆序条件回滚已完成值；第三方改值保留，不能假定比较/写入与第三方原子互斥。持久备份是恢复证据，不是已实现所有进程崩溃阶段自动 replay 的保证。

恢复机器值需前端说明所有用户影响并确认后 UnblockMachine=true；BlockMachine=true 拒绝。普通关闭不写 HKLM，开启不清整个 Blocked 键，不删无关 policy。只读扫描/自动关闭不接管已有外部禁用；用户恢复不要求旧值带本程序归属。成功 Items 是已回读且已保存的全部受影响结果，前端直接合并；响应缺失/不完整/失败才额外查询，不重复 mutation。

## SystemCommandStore 与全局经典菜单

SystemCommandStore 使用 ShellVerbVisibility 读写，不进入上述 Blocked application transaction。不可写 Windows 保护命令返回 structured protected failure，标记不可切换；不 take ownership/改 ACL/TrustedInstaller hack。也不能套用 classic HKLM Classes fallback，其物理路径并非 machine Classes ShellVerb。该分支没有 packaged Blocked 多目标回滚日志，不能由应用事务说明推断所有 Win11 来源都有同等恢复能力。

OtherRules/GuidBlock 是机器 CLSID 全局规则。不要将 Windows.Share/SendToMyPhone 的 ExplorerCommandHandler GUID 加入 GUID Lock 来绕过保护；系统 handler 共享/链路可能使现代菜单不可用或回落 classic，需具体 Explorer 证据，不能保证安全。

[Win11ClassicContextMenuService](../ContextMenuMgr.Backend/Services/Win11ClassicContextMenuService.cs) 是另一个设置：在前端 HKU `Software/Classes/CLSID/{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}/InprocServer32` 写空默认值启用经典菜单；恢复默认删除该 CLSID。不写服务 HKCU/HKLM、不复用 BlocksService。前端成功后 MarkRequired，用户点击顶部 Explorer restart 或重新登录；不自动重启，也不宣称所有将来 Windows build 继续支持该 tweak。

Win11 **应用开关**本身不标记/执行 Explorer restart，关闭并重新打开菜单观察效果；这描述当前 UI 契约，不是保证 Windows 无缓存或每个 build 都即时表现正确。

## 影响索引、兼容与 UI

Windows11ClsidImpactIndex 不加载 COM。保留 package/Verb/context/server 和 HKLM/当前 HKU Classes/CommandStore 引用，遍历 class/ProgID、SystemFileAssociations、CLSID、Background、shell/shellex、ExplorerCommandHandler/DelegateExecute/DropTarget；shell 深度 8，存在子键预算 50000，诊断样本 8。IsComplete 只表示这些步骤完成，不等于穷尽运行时依赖。

共享或不完整证据会暂停**后台自动禁用**，不直接禁止手动管理。日志 Win11TogglePlan/ApplicationMutationPlan/BlockedMutationPlan/ReadBack/PhysicalVerified/rollback 带 ExplorerValidated=False。不能仅凭引用相同 CLSID 证明 #114 根因。

v1.7.10 及以前无 family 的状态按原 family 迁移，保留 desired/pending；早先被覆盖来源采纳当前物理状态，只读 snapshot 不提交迁移。旧 Id 本地备注继续兼容读取，在明确停止支持旧格式升级前保留。备注仍存 FrontendSettings.ContextMenuItemNotes，不写 registry。

页面固定详细视图，不读/写经典 ContextMenuListViewMode；展示应用名、适用位置、发布者、标识、文件位置、备注和开关，缺字段隐藏。备注入口图标+本地化文字，不用 GUID 作应用名。技术诊断写日志，不在卡片堆风险报告。服务/VM 串行刷新与操作，mutation 等待45秒、query20秒；核对应用全部目标，失败内联普通语言+刷新入口，Uncertain 阻止重复写，fresh query 才解除。

## 诊断、验收与维护

**Current evidence is insufficient to determine the root cause.** #114 的 Terminal/Notepad Blocked 恢复观察不证明共享 CLSID、Explorer 版本或第三方依赖是哪一项根因。尚缺报告者现场的完整 A/B 验收，不能将读回/事务实现宣称为 Explorer 问题已修复。

VM 验收应记录 OS build、SID/native view、package/full family/CLSID/context、前后原始 Blocked 名称/类型/数据、state journal、请求/transaction 时间线与真实新旧菜单：

1. 固定文件/目录/背景，单 CLSID A/B 后再做应用操作；观察 Terminal/Notepad 及其它应用/显示更多选项，不以注册表成功替代 Explorer。
2. 旧值/重复别名/多类型开启自动备份恢复，仅清选定目标；多 CLSID 后一项或最终保存失败时前项回滚，外部改值保留，备份失败不得开始写。
3. 两用户和 HKLM block；取消所有用户确认无更改，确认只恢复选定应用，不触动无关 policy；x86-on-x64 native view 和 RDP 需真实服务验证。
4. 一个应用请求、完整 Items 成功不额外 snapshot；timeout/response loss 查询全部目标且不重写；旧服务拒绝新命令无部分写入。缓存冷/热、首次 baseline、gate 排队分别计时。
5. WPF 浅/深/窄窗、混合状态、备注/搜索、固定详细视图与经典偏好独立；Windows 系统命令保护失败无 ACL hack。

已有 Windows11BlockedMutationTests、PackagedContextMenuDiscoveryTests/ScanCacheTests、ContextMenuStateStoreTests 保护物理写入/回滚/发现/恢复元数据，不证明 Explorer/SCM/真实升级。修改发现、Id、Blocked/备份、impact、应用控制域、旧格式或页面核对更新本文；动态探测必须走 [ProbeHost](deep-analysis-probehost.md)，不直接移入服务。
