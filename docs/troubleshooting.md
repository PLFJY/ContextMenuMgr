# 排错与证据收集

先定位进程/flow、实际 SID/hive/view 与 Session，用户可见症状不能直接指认权限或 backend。不明确时写 **Current evidence is insufficient to determine the root cause.** 并列待检查项；验证方法见[维护流程](maintainer-workflow.md)。日志路径、预算/归档和 Locks 规则只在[运行时数据与日志](runtime-data-and-logging.md)维护。

## 最小证据包

记录应用/服务实际版本与 executable 路径、Installer/Portable、SC/FD、Windows build/架构、用户 SID/Session、操作对象 Id/真实 BackendRegistryPath/native view、时间和重现步骤。收集整个 Logs 的活动与归档，必要时该进程 TEMP fallback；可切 Information 复现成功 query 时间线。敏感身份/路径按需脱敏，保留可关联的一致标识。

区分 request/CorrelationId/ClientOperationId/TransactionId、物理状态、fresh read、state persistence 和 UI/Explorer。日志无异常、进程启动、一次写 API 或 UI 乐观切换均不能代替后几层。

## 连接、数据与服务

| 症状 | 检查顺序 | 权威说明 |
| --- | --- | --- |
| 前端连不上 Backend | SCM status/executable，service-startup/bootstrap/backend，Pipe ACL/连接与 Ping | [生命周期](runtime-lifecycle.md)、[IPC](process-and-privilege-flows.md) |
| Ping 成功而快照失败/超时 | 同一请求的完成时间、package warning/缓存、catalog gate、state load/ACL/JSON；不直接安装/强力修复 | [状态](state-and-monitoring.md)、[Win11](windows11-context-menu.md) |
| current JSON 损坏 | CorruptionDetected/Quarantined/RecoveredFromBackup/ResetAfterCorruption，Quarantine 原件及 .bak；I/O/未来 schema 不应被重置 | [状态恢复](state-and-monitoring.md#原子保存与损坏恢复) |
| 新装/reset 出现全部待审核/Modified | 首次是否完整用户上下文，两个 marker 是否独立提交、并发/保存失败；不能手改 desired 掩盖 | [状态矩阵](state-and-monitoring.md) |
| Portable 搬迁后旧状态/备份不可用 | package marker、host 指纹/quarantine/current scoped backups；首次缓存用户与实际 Pipe 用户可能不同 | [host identity](runtime-data-and-logging.md#portable-host-identity) |
| 设置无法保存/日志缺失 | 根目录及 Logs/Locks ACL、只读/冲突/reparse path、Data ACL repair/fallback；无日志不是无失败证据 | [数据与日志](runtime-data-and-logging.md) |

服务维护检查 `--result-file`、退出码/BootstrapResult、UAC 取消、--user-sid、SERVICE_PENDING_DELETE；关闭 Services MMC/任务管理器服务页等持有句柄后再试，必要时重启。强力修复删除服务注册不等于清理用户菜单/数据。

安装性能以 bootstrap 开始、逐项 ScmOperation、SCM Running、真实 Pipe Ping 与前端快照完成分段计时。当前代码直接调用 SCM，不再启动 sc.exe；若日志仍显示 RunSc，应先确认实际运行的构建和 executable 路径。原生调用快不代表服务启动或菜单扫描已完成，不能用只读查询的毫秒数宣称完整安装性能。操作失败查看 Win32Error 与具体 API 阶段，不通过跳过权限/回读/恢复策略核验来降低耗时。

Portable 运行文件 Zone.Identifier 可导致 bootstrap 启动/结果缺失。前端只检查当前 runtime 文件，需用户确认才 Unblock，不扫描目录外用户文档。可在下载 zip 属性解除阻止后重解压；检查 PortableMotwScan/Detected/Unblock/ServiceBootstrapBlockedByMotw。不得把所有 UAC 失败归因到 MOTW。

Portable 退出后服务仍存在时，检查 `service-startup.log` 的 PortableServiceCleanup Trigger/Stage/Win32Error/Result，并对照原 SCM PathName、PID、StartMode、用户 StartWithWindows/旧 Run 值、后台保留和 cleanup 设置。MarkedForDeletion/AlreadyMarkedForDeletion 不等于 SCM absent；先等旧进程退出并关闭 Services MMC/任务管理器服务页等句柄，再从 SCM 查询。前端快速重开最多有界观察后提示重试，不强制改写另一个副本的服务注册。

若是 RetainedByPolicy、StartupOwnershipUncertain、ActivationInProgress、调用用户 hive/其它 Session/消费者无法确认、缺失或非法设置，保留注册是有意的 fail-closed 结果；正常停止仍应检查 StopReason 和资源释放日志。认证的独占前端退出不因无关离线用户 hive 未加载而拒绝清理；系统关机仍要求核验全部用户的保留 policy，hive 已卸载时不能承诺该次自动移除。消费者拒绝日志包含 CallerPID/CallerSession、UserSessions、ConsumerPIDs，用于区分托盘尚未退出、其它副本和其它登录用户。安装完成边缘的生命周期锁最多等待一秒，持续竞争仍拒绝。残留 portable-startup-transaction.marker 会持续否决自动删除，两个设置开关锁定；显式修复核对后保守关闭自动移除再解除 veto。PORTABLE_STARTUP_ROLLBACK_FAILED 要收集同一次 bootstrap.log 的 SCM 配置/用户 policy/JSON 提交与补偿各阶段；未确认前不反复切换猜结果，使用显式服务修复并再次核对。Installer 没有自动移除分支。完整契约与真机验收见[生命周期](runtime-lifecycle.md#portable-自动移除-scm-注册)。

## 请求超时与真实结果

若启动中先出现 Operation Timeout、随后扫描成功，对照设置页请求与 bootstrap Ping 的时间：构造阶段不应发出 SetTrayIconPolicy/日志同步，也不应在服务尚未就绪时读取 GetAutoStartEnabled。检查 Workspace.IsBackendReady 与设置页初始回填/就绪订阅，不能单纯延长所有 Pipe 超时或吞掉真正的操作失败。初始探测 Ping 不可达可进入正常安装流程，不等于菜单扫描请求已经失败。

classic 看 FrontendRequestStarted、SetEnabledTimeout、OutcomeVerificationStarted/VerifiedApplied/VerifiedNotApplied/Uncertain；backend 对照操作和 DirectResponseWriteFailed。广播在直响应前，迟到成功应能到达停止等待的客户端。超时不是没写，查询原来源失败不是可安全重试。

ApplyDecision 超时要确认 pending、enabled/deleted、备份/状态；不能因前端等待结束重复 Remove。Win11 核对应用全部目标，ShellNew 独立读锁，OpenWith fresh snapshot；各自契约见主文档，不用同一 bool rollback 表示所有 outcome。

## 注册表权限与受保护写入

先查 Registry Write Protection persisted setting 与 transition 的每个 target/DACL，而非只看 toggle。禁用部分失败可能仍 persisted=true、已成功解锁 root 保持解锁；按具体失败 root 修复再重试，不手改 JSON false。ShellNew order ACL 是另一功能。

classic 查看 ClassicMutationStarted/Completed、真实物理 source、MutationApplied/PhysicalVerification/StateSave 与回滚码：ROLLED_BACK 表示回退并验证；ROLLBACK_CONFLICT 保留外部新值；SECURITY_RESTORE_FAILED 即使值回滚成功仍是失败。FILE_TYPE_ACTIVATION_VERB_PROTECTED 是写前安全拒绝，PROVENANCE_MISSING 是归属不足，不是权限不足。

删除看 ClassicDeleteStarted 的 path/backup 和整树 ReadKey/Delete 检查，再重开同 key/view。当前逐叶 DeleteSubKey 回读，不用会申请 SetValue 且可能静默跳过的 DeleteSubKeyTree(..., false)。这保留 DELETE-only 场景，但真实第三方自保护/并发仍可拒绝。Safe360Ext 名称本身不证明安全软件阻止删除；.reg 不包含 ACL，Undo 导入不代表安全描述符恢复。

### Token 与 descriptor 分阶段诊断

受保护 HKLM Classes ShellVerb 普通写拒绝后才有 ProtectedShellVerbFallback；HKU 不走此路径，Registry Write Protection preflight 不绕过。实现与私有 token/ACL 约束见[注册表模型](registry-model.md#受保护机器-shellverb-的窄写入路径)。

1. 只读收集 `sc.exe qc ContextMenuManagerPlusService`、`sc.exe qprivs ContextMenuManagerPlusService`、`sc.exe qsidtype ContextMenuManagerPlusService`、PID/实际路径/版本/account，不改配置猜测修复。
2. 重现同一物理项，关联 IncomingContext/ServiceContext、API Phase、native error/HRESULT、privilege present/enabled 与 restoration；不要输出 raw token/凭据/descriptor dump。
3. OpenProcessToken(Query/Duplicate) error 5 是 token 对象 access 检查失败；EnablePrivilege/PrivilegeAbsent 1300 才是 privilege 不在 token；Duplicate/Attach/OpenPrivateThreadToken、TakeOwnership/GrantValueAccess、RegistrySecurityRestore 是不同阶段。HRESULT 不直接冒充 native error。
4. 仍失败时在可丢弃 service-context debugger 比较 Query、Query/Duplicate、Query/Adjust 只读打开结果，核查 token DACL/限制、SCM account/RequiredPrivileges 和外部安全事件；不全局 enable privilege、不把前端提权当证明。
5. 比较物理 value/Owner/Group/DACL、fresh state/pending 与重启；descriptor restoration 不确定则停重复写，保留证据。

历史 #140 记录过 primary token Adjust/Query open error 5；当前代码改用私有副本，#141 又修正过 callback Name 的 managed token flow。这些事实不能证明 #140 机器 token access 拒绝的具体根因。原始失败线程 identity、token DACL/SCM restriction 和安全事件仍不足；仍需取得部署日志验证。

## ShellNew 与 OpenWith

ShellNew 1314 必须从 ShellNewMergedClassesOpenFailed.Stage 区分 SessionValidation/WTSQueryUserToken/TokenValidation/ProfileAvailability/RegOpenUserClassesRoot，检查 token SID/session、profile/hive 与 incoming/process privileges。不能由服务启动 SYSTEM 推断失败线程 token，也不自动启用 SeTcb/改账户策略。ShellNewDiscoveryTiming/SlowCandidate/ApiTiming 区分 WTS/RegOpen/classic/package，8 秒协作预算不是同步 API 硬超时。

读锁 Unknown 不推为 Unlocked；发现失败不能写排序，已独立确认 Locked 可单独解锁。丢失原 Classes 时不能用部分列表/包声明猜重建，停止/卸载服务不会自动恢复旧 ACL。完整安全恢复及 Windows 验收只见 [ShellNew](shellnew.md#恢复与真实-windows-验证)。历史 #141 原始 API/Classes 证据不足，不能由新 repair 实现称全部机器恢复。

OpenWith 开关只代表选定 Applications 的 NoOpenWith；候选仍显示时先检查其它 ProgID/FileExts/Capabilities/AppX 来源，不删 UserChoice/Hash。Unknown/Changed visibility 不自动认领；Uncertain/Conflict 停止纠正需审核。受系统保护 badge 是 UI 只读标签，不证明新增 ACL。授权、重注册/backup 冲突、SID 通知与 source inspection 都只见 [OpenWith](openwith.md)。

## Win11 与 Explorer

刷新后开启先查认证 SID/native HKU/机器 Blocked、GUID aliases、实际所有应用目标和 state journal。其它菜单消失须同一 OS build/文件/目录/背景做单 CLSID A/B，比对真实 modern 与 classic 菜单；共享索引/读回成功都不是 #114 根因或修复证据。当前应用开关不标 Explorer restart，全局 classic tweak 另有要求。看 Win11TogglePlan/ApplicationMutationPlan/BlockedReadBack/PhysicalVerified/rollback 与 ExplorerValidated=False；完整验证见 [Win11](windows11-context-menu.md#诊断验收与维护)。

Restart Explorer 无效查前端 SessionId、同 Session KilledCount 和自动恢复，不全局 kill。TrayHost 不可见先区分隐藏图标、进程缺失、policy、WTS 启动与投递失败。TaskbarCreated 图标重建不重放已提交通知；NIM_MODIFY 成功也不保证用户看到。真实 Explorer 重启、通知点击与普通托盘激活分别验证，见[生命周期](runtime-lifecycle.md)。

SendTo/WinX 修改无效查用户 AppData、物理文件、WinX target/arguments/hash 与实际菜单；WinX 删除无 Undo，单 group restore 有 backup。新版 Windows 可能额外筛选，不由 hash API 成功推导显示成功，见 [SpecialMenu](special-menus.md)。

## 前端、深入分析与发布

| 症状 | 先读/查 |
| --- | --- |
| 搜索无下拉/不跳转/不筛选 | [前端搜索](frontend-wpf-ui.md#搜索筛选与集合归属)：Handled、ItemsSource、SelectedItem、pending filter/目标 id |
| 主题/Popup/滚动/Page parent | [前端](frontend-wpf-ui.md)：DynamicResource、watcher、模板边界、ModernFrame ownership 与硬件输入 |
| 注册表定位不准 | RegistryEditorNavigationRequested/真实 BackendRegistryPath，已有 Regedit 是否 -m 新实例；实际选中键需人工看 |
| ProbeHost 缺失/架构错/crash/无菜单 | [ProbeHost](deep-analysis-probehost.md)：selection/PE、result/stderr、SpecificHandler 与 WholeContextMenu 不混；不改普通开关 |
| 发布资产/架构/FD-SC/GitCode | [构建发布](build-and-release.md)、[安装器](installer-deployment.md)：检查实际 YAML、tag/SHA、marker/hash/部分远端成功 |
| manifest 生成但无法安装/搜索 | [包管理器](package-managers.md)：bucket commit/fork PR/checks/review/CLA/merge/index 各阶段独立 |

## 第三方安装器与驱动归因

先验证当时服务/保护是否运行、相关保护开关、同时间日志、Access Denied/UnauthorizedAccessException、目标是否在保护范围，以及关闭保护/停服务 A/B。没有这些证据，不能把驱动安装失败归因“右键菜单拦截”。停服务也不自动撤销既有 ACL，应核对实际状态；同时收第三方自己的安装日志。

修改错误码、日志关键事件或恢复入口时更新对应主文档和本页诊断路由；不在此再次复制完整事务/状态机。
