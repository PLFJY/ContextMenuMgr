# ShellNew 发现、排序与锁定

本文是 ShellNew 物理注册、发现、排序/ACL 条件事务及恢复的权威说明。入口为 [SpecialMenuService](../ContextMenuMgr.Backend/Services/SpecialMenuService.cs)、[FrontendUserClassesRoot](../ContextMenuMgr.Backend/Services/FrontendUserClassesRoot.cs)、[ShellNewOrderTransaction](../ContextMenuMgr.Backend/Services/ShellNewOrderTransaction.cs)、[PackagedShellNewDiscovery](../ContextMenuMgr.Backend/Services/PackagedShellNewDiscovery.cs)；请求模型见 [SpecialMenuContracts](../ContextMenuMgr.Contracts/SpecialMenuContracts.cs)，UI 所有者为 [SpecialMenuPageViewModel](../ContextMenuMgr.Frontend/ViewModels/SpecialMenuPageViewModel.cs)。属于链路 A，需要认证 SID；merged HKCR 发现另需匹配 SessionId。

WPS/Office 共存检测和图标选择见 [SpecialMenu](special-menus.md#wps-与-microsoft-office-共存)。

## 发现与物理来源

| 概念 | 当前实现说明 |
| --- | --- |
| `ShellNew` 子键 | 位于扩展名 Classes 下，例如 `Software\Classes\.txt\ShellNew`。发现使用前端交互用户的合并 HKCR；修改仍使用明确的物理 HKU/HKLM 路径。 |
| 文件扩展名与新建项 | 每个扩展名可能对应一个“新建”菜单项，显示名按 Windows / BluePointLilac 语义解析。 |
| `NullFile` | 表示创建空文件。当前创建请求未提供 `DataText` 时倾向写入 `NullFile`。 |
| `Data` | 当前 `ShellNewCreateRequest` 支持 `DataText`，后端会写入二进制 `Data`。 |
| `Command` | `ShellNewUpdateRequest` 支持更新 `Command`。 |
| 创建请求字段 | 新建时会应用前端表单中的扩展名、显示名、图标路径、命令、数据文本和 `BeforeSeparator`；命令优先于 `Data`，没有命令和数据时写 `NullFile`。 |
| `FileName` / `Directory` | Windows ShellNew 支持这些创建方式；枚举会把 `NullFile`、`Data`、`FileName`、`Directory`、`Command` 视为有效 ShellNew 值。 |
| `Config\BeforeSeparator` | 当前更新请求支持 `BeforeSeparator`。 |
| Explorer ShellNew order key | `Software\Microsoft\Windows\CurrentVersion\Explorer\Discardable\PostSetup\ShellNew`，控制 Explorer 侧排序信息。 |
| Classes 排序值 | 排序会同时考虑扩展名、Explorer order key 和 Classes 相关数据。 |

ShellNew 快照发现通过 `RegOpenUserClassesRoot` 打开前端交互用户的合并 HKCR（`HKU\<SID>\Software\Classes` + `HKLM\SOFTWARE\Classes`），而不是服务进程的 `Registry.ClassesRoot` 或手工叠加两棵树。它从这个有效视图枚举扩展名、解析 ProgID、`ShellNew`/`-ShellNew`、显示名和图标；因此用户扩展名关联到机器 ProgID 的跨 hive 注册也能与 Explorer 一致。服务使用请求携带的 SessionId 取得 WTS 用户 token，以只读方式打开句柄；无法取得 token、profile 未加载或 API 失败时分阶段记录 `ShellNewMergedClassesOpenFailed` 并使发现失败，不退回到 LocalSystem 的 HKCR。WTS token 的 SID 和 TokenSessionId 必须与认证 Pipe 用户一致；ShellNew 不使用另一交互用户 fallback。64 位系统请求 64 位 Classes view，以匹配 64 位 Explorer。

每个发现项同时解析实际提供 `ShellNew` 值的物理来源：优先用户 `HKU\<SID>\Software\Classes\...`，否则机器 `HKLM\SOFTWARE\Classes\...`，并将该物理路径作为 `RegistryPath` 和 `DisabledRegistryPath` 返回。合并视图只用于发现和有效元数据，绝不作为写入目标；开关、软删除、恢复、导出/定位和 ShellNew-local 编辑都使用此物理来源。扩展名/ProgID 的元数据来源可以不同于 ShellNew 的可变来源。

除传统物理注册外，快照还使用 `PackageManager.FindPackagesForUser(frontendSid)` 扫描该前台用户已安装 AppX/MSIX 包的 `AppxManifest.xml`。带有 `uap4:ShellNewFileName` 的 `FileType` 声明会作为 `PackagedShellNew` 返回，并携带 package full/family name、Application ID、FileTypeAssociation、原始 `FileType` 声明、`ShellNewFileName` / `ShellNewDisplayName` / `ShellNewCommandParameters` 元数据。它们在同一列表显示“应用提供 · 只读”，不参与 ShellNew order lock 或排序；`CanEdit`、`CanDelete`、`CanMove` 都为 false，服务端也拒绝开关、编辑、删除和恢复请求。前端为保持列表右侧布局，会保留禁用的开关、编辑和删除按钮，但不显示排序按钮。不得修改 `AppxManifest.xml`、安装目录、UserChoice、ProgID 或为此类项目制造注册表 ShellNew key；目前没有已验证的独立 Windows 开关。

## 创建、编辑与开关

创建 ShellNew 时，后端仍写入明确的用户 Classes 物理路径，而不能使用服务进程的 `Registry.ClassesRoot` 来验证 ProgId。解析顺序是用户 `HKU\<SID>\Software\Classes\.ext`、机器 `HKLM\SOFTWARE\Classes\.ext`、用户 `FileExts\.ext\UserChoice\ProgId`，再考虑 `OpenWithProgids`。ProgId 只在同一组用户 / 机器 Classes 根中存在时才视为有效；`UserChoice` 只读不写，不修改 hash，也不会为项目创建全局 fake ProgId。

ShellNew 显示名遵循 Windows / BluePointLilac 优先级：`ShellNew\MenuText` 仅在它是 `@...` 间接资源字符串且能解析为非空文本时优先；其次读取默认 ProgID 的 `FriendlyTypeName`；再读取默认 ProgID 的默认值；最后回退到扩展名。创建或编辑普通用户显示名时，ContextMenuMgr 不写纯文本 `ShellNew\MenuText`，而是写入前端用户覆盖层 `HKU\<SID>\Software\Classes\<ProgID>\FriendlyTypeName`。编辑显示名时会从实际可写的用户 ShellNew 覆盖键删除 `MenuText`，避免旧值继续压过 `FriendlyTypeName`。

即使扩展名没有出现在用户或机器 Classes 中，创建仍会继续写用户级 `HKU\<SID>\Software\Classes\.ext\ShellNew`。ProgId 是可选元数据：解析到有效 ProgId 时用于 per-user `FriendlyTypeName` 和返回项 metadata；没有 ProgId 时仍创建扩展名级 ShellNew，但普通显示名不会保存为纯文本 `MenuText`，刷新后可能回退到扩展名。`UserChoice` 始终只读，不写入、不修改 hash。

编辑 ShellNew-local 属性时，后端写入发现结果携带的实际物理 `ShellNew` 或 `-ShellNew` 路径；不会通过 HKCR 隐式选择 hive，也不会因为扫描而把机器注册移动到用户 hive。显示名覆盖仍写到用户 ProgID 的 `FriendlyTypeName`。

ShellNew 开关会在同一物理来源的 `ShellNew` 与 `-ShellNew` sibling key 之间移动注册。每次操作都会以当前实际存在的 sibling key 为准，而不信任前端旧快照中的 alternate-path metadata；移动后会确认源 key 已不存在、目标 key 已存在，再返回与物理路径一致的 `RegistryPath`、`Id`、`IsEnabled` 和 alternate path metadata。若两个 sibling 同时存在或都不存在，操作会失败而不会只修改返回 DTO 的启用状态。

当 WPS 与 Microsoft Office 共存时，新建菜单页的“文档图标提供方”开关通过后端 Pipe 写入当前前端用户 `HKEY_USERS\<SID>\Software\Classes` 下的 Office ProgID `DefaultIcon` 覆盖。这是用户主动选择；成功写入后，后端会确认对应的 `special:wps-office-icon:document-icons` 合成检测项，不能把这次写入重新送入待审核。来自 WPS 或其他程序的关联修改和 ShellNew 注入仍保持原有检测规则。

前端可先显示已验证设备/用户身份的历史列表；缓存条目及其中的 OrderLocked metadata 不设置当前发现有效性或锁状态。当前 ACL 仍由独立查询核实。传统物理项和 packaged 声明分别记录扫描覆盖：包失败保留旧只读声明，不阻断完整物理项的差量更新或缓存。缓存与过渡动画的共享行为见[前端](frontend-wpf-ui.md#列表缓存差量刷新与通知)。

## 排序与 ACL 条件事务

新建菜单排序锁使用独立的 `GetShellNewOrderLock` 读取物理 ACL，不依赖昂贵的项目/包枚举或条目的 `OrderLocked` metadata。前端保留 Unknown / Locked / Unlocked，写入期间禁用重复切换和移动；超时或失败后用独立请求核对实际锁状态。无法核对时显示明确错误并保持 Unknown，不恢复旧 Toggle。刷新用状态版本阻止较旧的查询覆盖新的核对结果。

`GetShellNewItems` 只有完整的经典发现成功后才返回列表；成功的零项与失败不同。WTS token、合并 HKCR、逐扩展名读取、物理来源解析、枚举期间候选集合变化或取消均使整个发现失败，不返回部分列表，也不继续用 packaged-only 结果充当经典发现。枚举只读，不修复注册或关联。包声明仍按原有只读/best-effort 方式发现，但必须先完成经典扫描，包失败不能为经典结果背书。

排序锁定和 `MoveShellNewAsync` 共用 `ShellNewOrderTransaction`，替代原先分开的 unlock / 无条件覆盖 Classes / relock 路径：

1. 串行化本服务的锁定、解锁、移动和锁状态查询；发现前捕获同一用户物理 HKU order key 的 Classes 是否存在、原始类型/数据及 Owner、Group、DACL。锁定/移动必须取得完整且非空的经典发现，不扫描包清单；单独解锁不依赖发现，也不创建或清空 Classes。
2. proposed entries 必须是权威经典条目的完整排列，不允许未知/缺失/重复输入。Classes 用原生 value API 捕获/比较原始类型及字节；已有类型异常、非规范 MULTI_SZ 或含歧义空条目时拒绝排序，不自动规范化；已有未知项保留原文和相对槽位，已发现项在其槽位中排列，新增已发现项追加。不会删除未确定归属的现有项，不会写空列表。候选减少时也不会据此删除原列表中的其它项。
3. 先验证 ACL 可读/可修改及目标规则，不提前解锁。最后一次取消检查之后，同步完成提交或回滚，不让 Pipe 断开/取消中断两次写入之间的状态。正常服务停止会等待已经开始的 ShellNew mutation 退出，并拒绝新的 mutation。
4. 每次值或 DACL 修改前检查捕获的值和描述符仍匹配，之后通过新句柄回读；最终再次验证值和实际锁状态。DACL 只添加/移除明确匹配的 WorldSid `Deny SetValue | CreateSubKey | Delete` 和历史精确 `WriteKey` 规则（无继承/传播）；通过 RegGetKeySecurity / RegSetKeySecurity 仅读取 Owner/Group/DACL 并仅写原始 DACL，避免 .NET 合并同 SID deny ACE 而丢失第三方规则边界；保留其它 ACE、DACL 保护/继承和 Owner/Group，不写所有者，不请求 SACL，不 take ownership、不重置 DACL、不启用权限。未知 World deny 阻止操作时拒绝，留给外部核查。
5. catchable failure 时，仅在实际值和 ACL 同时匹配本操作已知中间/目标状态时恢复原值（含不存在）和原 DACL，并重新回读。外部变更或读回失败则停止回滚，记录 `ShellNewOrderRollbackUncertain` 并返回错误。Windows 写回 unprotected DACL 时自动计算的 `SE_DACL_AUTO_INHERITED` 位不作为冲突；Owner/Group、DACL protected 标志、每个 ACE 及其继承/传播位仍参与比较。

这是逐步验证的条件事务，不是 Windows 原子 compare-and-swap，也不是持久崩溃恢复日志。外部写入仍可能发生在比较与写 API 之间；硬杀进程/断电可能留下中间状态，不能宣称自动恢复。对原本不存在的 order key，失败时恢复 Classes 的不存在状态，但保留新建的空键并记录，避免删除并发新增的数据。旧版已经丢失的 Classes 无法从发现结果可靠还原；恢复步骤见下文恢复步骤。

## 预算、取消与前端收敛

所有 ShellNew 发现共用 single-flight gate，已有扫描尚未结束时拒绝新扫描，不排队堆积；每轮有 8 秒协作取消预算，扩展名及包之间检查取消。WTS、RegOpenUserClassesRoot、经典扫描、包清单和慢扩展名/慢包均有窄范围计时。Windows 同步注册表/资源/PackageManager API 本身可能不可取消，因此预算不是硬终止承诺。前端 ShellNew 自动刷新在上轮结束后等待 30 秒；其它 SpecialMenu 仍等待 5 秒。锁状态查询不等待包扫描，仅在独立 8 秒预算内等待 mutation；并发写请求直接拒绝，不排队等待到前端超时后才写入。

兼容维护决定（#141）：保留历史精确 WorldSid `Deny WriteKey`、无继承/传播的 ACL 格式；其最早发布版本未核实，不捏造版本下限。只有维护者明确停止支持该 ACL 格式并验证受支持安装已完成外部修复后，才能删除此窄迁移识别。包含额外未知 rights 的 broad deny 不属于可删除签名。

如果旧版本 broad `WriteKey` lock 或外部工具留下的 broken ACL 无法通过 `ReadPermissions | ChangePermissions` 安全修改，本程序会返回明确错误：需要先用 BluePointLilac / ContextMenuManager 或系统工具解锁 / 修复，然后重试。主后端不再执行 take ownership、replacement DACL 或自动 ACL reset。

## 恢复与真实 Windows 验证

安全恢复流程：

1. 暂停反复点击锁/刷新。以受影响的交互用户确认 SID，导出当前 `HKEY_USERS\<SID>\Software\Microsoft\Windows\CurrentVersion\Explorer\Discardable\PostSetup\ShellNew`，另保存 Owner/Group/DACL（`.reg` 不包含 ACL），记录 Classes 是否存在、类型、完整内容和时间。不要使用 SYSTEM HKCU，也不要导入另一个用户/机器的备份。
2. 新版本独立读锁如果确认 Locked，即使列表发现失败仍可请求单独解锁；它只移除精确的历史/窄规则，不写 Classes。读锁 Unknown 或未知/损坏 ACL 导致拒绝时，先保留证据，再由管理员使用已核实的外部解锁机制。报告者称 BluePointLilac/ContextMenuManager 的关闭“锁定并排序”恢复了其 Explorer 菜单；这是一台机器的观察，不保证恢复所有机器的历史自定义排序。操作前后必须核对 ACL、值及真实 Explorer 菜单，不能自动套用替换 DACL/接管所有权脚本。
3. 如果有同一 Windows 安装、同一 SID、故障前的可信注册表备份，解锁并排除并发写入后，仅恢复经过核对的 Classes 原始类型/数据（或备份明确证明的原不存在状态）；不要整体导入可能覆盖其它设置的无关备份。恢复后重新打开物理 key，核对值及 Owner/Group/DACL，再在当前用户桌面检查实际“新建”菜单；必要时通过现有用户 Session 的重启 Explorer 操作刷新。
4. 没有原始备份时，无法从日志、当前不完整扫描、已安装软件或 packaged 声明可靠重建原 Classes。不要猜测排序、写空列表、删除 Classes 强制重建，或改 ShellNew 注册、文件关联、ProgID、UserChoice。可以仅验证外部解锁后 Explorer 是否自行恢复展示；仍缺项时继续收集物理注册/备份证据。卸载或停止服务不会自动恢复已经存在的 ACL/历史排序。

Windows 验收（尚须在受影响配置执行）：

| 配置 | 必须观察 |
| --- | --- |
| Windows 11 24H2 Installer / Portable | 标准前端 + 真实 LocalSystem 服务，经典和包提供项共存；新建列表、启用/解锁、上下移动及实际 Explorer New 菜单。 |
| 报告标注的 Windows build（须先核实）Installer / Portable | 同上，并复现原报告中加载失败→点击锁→超时的时序；确认失败时 Classes 与 ACL 无变化，超时后 Toggle 最终来自独立读锁。 |
| 两个不同 SID / Session、含 RDP | 各自 HKU 与 merged HKCR 隔离，错 SID/Session、profile/hive 不可用时失败；绝不 fallback 到另一个登录用户或 SYSTEM。 |
| 故障与并发 | 在隔离 VM 用临时扩展名/测试键验证逐键拒绝读取、RegOpen 失败、成功零经典项和 packaged-only。对非空/异型/含未知 Classes 均确认无丢失。用断点/外部写入验证解锁后写失败、最后加锁失败及回滚前 Classes/ACL 改变；外部值/ACE 保留，rollback uncertain 有明确错误。 |
| 取消 / Pipe 断开 / 重启 | 最终 mutation 边界前取消无更改；边界后的取消或断 Pipe 不打断同步提交/回滚。快速重复 Toggle、两个前端并发请求不能排队留下迟到写入。正常 SCM stop 等待现有 mutation；重新启动后读实际 ACL/Classes。硬杀进程/断电没有持久回放保证，应人工检查中间状态。 |

每次验收分别记录 request outcome、原始/最终物理 Classes 和类型、ACL、fresh query、前端三态与真实 Explorer 菜单。日志成功、编译或隔离键测试不能代替菜单可见性。值/ACL 比较不是原子 CAS，Windows 比较与写入之间仍有竞态窗口；不能承诺排除所有外部修改，也不能承诺硬崩溃后的自动恢复。

自动回归保留原生 WTS 获取失败/无效 Session 与 fail-closed snapshot、隔离 HKCU 的类型/未知项保留、精确 ACL 循环、解锁后的拒写回滚、捕获后的外部值/ACL 冲突和取消；Dispatcher 下的迟到/丢失响应测试验证 Unknown 及重复点击保护。单独 RegOpen 失败、完整逐键部分读取、真实 Pipe/SCM 和中途外部回滚冲突仍属于上述 VM 验收，不把合成输入测试当作端到端验证。原生 Pipe 回归只检查身份边界，不启动服务或改变 SCM；完整部署仍须按表验收。

历史 #141 记录过 merged-root 错误 1314、约十秒锁请求与 broken response；历史实验记录与附件摘要不构成每台受影响机器的现场验收。当前 resolver 的 SID-only callback 及永久身份回归可在源码定位，不能由它推断每台机器 1314 的失败 API，更不能证明遗失的 Classes 已恢复。分阶段身份采证统一见[进程与权限](process-and-privilege-flows.md#身份解析与模拟边界)。

维护决定：保留历史精确 WorldSid deny 的识别和安全解锁，退出条件见上文；发现/事务/DTO、取消边界或物理来源变化时更新本文。Registry Write Protection 则由[注册表模型](registry-model.md#registry-write-protection)负责，不能用其开关解 ShellNew 锁。
