# 运行时路径、数据权限与共享日志

入口：[RuntimePaths](../ContextMenuMgr.Contracts/RuntimePaths.cs)、[RuntimePackageManifest](../ContextMenuMgr.Contracts/RuntimePackageManifest.cs)、[RuntimeLogFiles](../ContextMenuMgr.Contracts/RuntimeLogFiles.cs)、[RollingLogFile](../ContextMenuMgr.Contracts/RollingLogFile.cs)、[RuntimeDataAclRepairService](../ContextMenuMgr.Backend/Services/RuntimeDataAclRepairService.cs)。状态格式/恢复只在[状态与监控](state-and-monitoring.md)维护；服务启停见[生命周期](runtime-lifecycle.md)。

## 根目录与文件所有权

只读 `AppContext.BaseDirectory/ContextMenuMgr.package.json` 的 `packageKind`（Installer/Portable），缺失/无效默认为 Installer，不按路径猜。Installer 根是 `%ProgramData%/ContextMenuMgr`；Portable 根是 `<应用目录>/Data`，`DataDirectory=RootDirectory`。包种类不等于 self-contained/framework-dependent 部署模式。

| 文件/目录 | 责任 |
| --- | --- |
| `frontend-settings.json` | FrontendSettingsService 的 UI 偏好、本地备注及 Portable 生命周期偏好；不另建后端设置副本 |
| `context-menu-state.json`、`.bak`、`.tmp-*` | 状态、恢复/归属、OpenWith section；不由前端直接删除 |
| `backend-protection-settings.json` | Registry Write Protection 提交设置，与 ACL 一起收敛 |
| `DeletedBackups`、`Quarantine` | 注册表删除恢复与损坏/foreign-host 隔离 |
| `Programs` | 已启用增强菜单的持久脚本，不能当缓存随意清除 |
| `portable-startup-transaction.marker` | 未完成 Portable 启动策略事务的删除 veto；提交/验证补偿后清除，异常残留只在显式修复核对后清除 |
| `Logs`、`Locks` | 日志与持久同步 lease，二者是根下同级目录 |

后端在 logger/store 创建前 best-effort 修复根 ACL，预创建 Logs/Locks，给 Builtin Users Modify 并修复已有子项继承。前端保存设置遇到权限类失败时先走 `RepairRuntimeDataAcl` Pipe 并重试一次；Pipe 不可用才用 bootstrapper `repair-runtime-data-acl`。这是共享文件数据可写性修复，不是菜单注册表保护、ShellNew 解锁或隔离存储。不要把 ProgramData/Portable 文件当成防同机其他用户篡改的安全边界。

`FrontendSettingsFile` 统一设置写入：同目录唯一临时文件、flush-to-disk、原子替换；按规范化路径的具名 mutex 串行短文件事务。提升的 Portable startup-mode 操作只更新两个生命周期字段，保留其它偏好，使用条件式核对防止覆盖外部策略；前端在这段事务期间保存其它偏好时保留磁盘中的生命周期值。初次 Portable 启动持久化默认设置，reset 原子保存默认而不靠删除文件表示策略。后端关机读策略遇到缺失/不可读/损坏不得从默认推导删除权限；前端加载损坏/不可读策略时也将自动移除保守置 false，避免窗口位置等无关保存把损坏文件转成删除授权；具体默认值和 unloaded-hive 保守限制见[生命周期](runtime-lifecycle.md#portable-自动移除-scm-注册)。共享可写数据仍不是授权边界，SCM 删除还要独立验证身份、实际配置与用户策略。

## Installer 与 Portable 迁移

`BackendRuntime.TryMigrateLegacyRuntimeFiles`、`FrontendSettingsService.TryMigrateLegacySettings` 保留旧 LocalAppData、ProgramData、ProgramData/Data 的 copy-if-missing。Portable 还复制缺少的已知日志、备份和 Programs；不移动/删除源、不覆盖目标。日志迁移另需双 lease，不能用普通目录递归复制锁。

维护决定：旧布局发布范围/最低升级版本尚未完整核实，保留实际可达路径；只有维护者明确停止支持这些布局，且验证目标状态/备份不丢失后才退休。不能因 Legacy 命名、目标文件已存在或一次升级成功删除兼容。无效权限/只读目录可能使 best-effort 迁移未发生，应比较源与目标和日志。

## Portable host identity

[RuntimeHostIdentityProvider](../ContextMenuMgr.Backend/Services/RuntimeHostIdentityProvider.cs) 对 purpose + MachineGuid + **交互用户 fallback SID** 做 SHA-256，保存指纹/12 字符前缀、版本与时间。它没有接收每个 Pipe 请求的 SID：首次可信结果被进程缓存。无用户时暂不可信并可重试；多用户切换后不能据此宣称逐请求 host binding，需部署验证。HKLM MachineGuid 优先 Registry64。

Portable envelope 指纹不同会隔离旧 state 至 `Quarantine/foreign-host-...`，重新建立本地空 baseline；legacy dictionary 仅在当前 identity 可验证时迁移。删除备份按 `DeletedBackups/<host-prefix>` 分目录，非当前 host/旧根的备份隔离，恢复只接受当前 scoped 目录，不能导入另一用户/Windows 的 .reg。

hostIdentity 元数据不存原始 MachineGuid/SID；state 的 OpenWith 授权却必然有 SID/HKU path，日志也可含用户路径。不能把“散列 hostIdentity”扩大为整个数据目录无身份信息。`frontend-settings.json` 语言/主题等偏好可移动，但 `ContextMenuItemNotes` 仍为 item/registry-bound 且未独立 host 隔离，属于现有限制。

## 日志入口

`Contracts/RollingLogFile` 是 append/rolling/retention 的共享实现，FrontendDebugLog、Backend FileLogger、TrayHostLogger 和 emergency writer 保留各自级别/调用边界，不另写一套轮转。

| 活动日志 | 定位用途 |
| --- | --- |
| `frontend-debug.log` / `frontend-crash.log` | WPF、启动、请求/导航/探测；未处理异常 |
| `backend.log` | 注册表、Pipe、用户上下文、monitor、Win11、SpecialMenu |
| `trayhost.log` | 通知投递、图标、激活、连接 |
| `bootstrap.log` | 一次性提升维护操作和 SCM 诊断 |
| `service-startup.log` / `backend-fallback.log` | DI/普通日志尚不可用的启动证据与独立 fallback |

## 日志轮转、锁与收集

活动文件名保持不变；归档为 `<stream>.archive-<UTC yyyyMMddTHHmmssfffffffZ>-<reason>-<guid>.log`。GUID 防止时间戳碰撞覆盖；每个流只清理自己的精确归档名。

| 流 | 每卷上限 | 归档数（活动文件另计） |
| --- | ---: | ---: |
| `frontend-debug.log`、`backend.log` | 5 MiB | 各 10 |
| `trayhost.log`、`frontend-crash.log`、`service-startup.log`、`backend-fallback.log`、`bootstrap.log` | 2 MiB | 各 5 |

Frontend 在取得单实例 ownership 后开始会话；每卷头部记录启动时间、PID、SessionId。正常退出写 shutdown 记录并归档；崩溃/强制终止保留当前卷，下次主实例启动先归档为 `recovered`。会话中达到大小限制仍会分卷，各卷共享会话标识。正常退出后没有活动 Frontend 文件是预期行为。Backend/TrayHost 在写入时轮转，不依赖进程重启；重启继续保留当前卷证据。

写入前按 UTF-8 字节数检查上限。单条超大事件保留开头和结尾（含异常栈尾部），中间用 `LogEntryTruncated` 标明省略；不会生成无限大卷。升级遇到旧巨型活动日志时，只读取至多一卷最近尾部，写 `LegacyOversizedLog` 原大小/尾部保留标记并 flush，再移除旧文件；不永久保留巨型旧卷。此前更早内容按这项明确的大小保留策略淘汰。迁移失败保留原文件、不向巨型文件追加；以后重试。

v1.7.10 起，持久 lease 位于 `RuntimePaths.LocksDirectory`（`RootDirectory\Locks\<active>.lock`），活动日志与归档仍在 `RootDirectory\Logs`。例如 Installer 使用 `%ProgramData%\ContextMenuMgr\Locks\backend.log.lock`，Portable 使用 `<应用目录>\Data\Locks\backend.log.lock`。早期 writer 可自行创建两个目录，不依赖 DI 或提权；服务既有 ACL repair 会预创建并修复 Logs/Locks，均继承 Users Modify。

升级不能假设旧 Frontend、Service、TrayHost 已全部退出。每次 append、recovery、rotation 和 clear 都按固定顺序取得 **Locks lease → 旧 `<active>.lock` lease**，整个操作结束后才释放。即使 Logs 已清理或旧进程暂时没有打开文件，后续新操作仍取得两个 lease，不以一次独占 open 判定旧进程已退出。旧路径因此可能在写入期间短暂出现在 Logs；旧进程仍可能留下它。新进程在完整受保护操作结束时，仅对七个已知 stream 的零字节普通旧 lock 设置删除，再关闭句柄；Windows 在句柄关闭前保持 delete-pending，旧版不能绕过。下次旧版重新创建路径后仍与新 writer 同步。崩溃残留在下次 recovery/write 重试，不移动或复制活跃同步原语。

首次创建共享 writer 时会检查同目录七个已知旧 lock；忙碌/不可访问的流推迟处理，不妨碍其它流或启动。已迁移、半迁移与重复启动可重入；非空旧 lock、未知文件、目录和 reparse point 不删除。Locks 目标若有非空文件或目录冲突，不覆盖，当前流拒绝无锁写入。迁移/锁失败每个进程最多报告 8 条、每条最多 1024 字符的 `LogLockMaintenance` 到 Trace 和可用的独立 TEMP `backend-fallback.log`，TEMP 自身失败只保留 Trace，避免递归。没有日志时还应核查文件系统 ACL 与冲突路径，不能据此宣称迁移成功。

数据句柄每条事件后关闭，避免阻塞维护。普通 append 不扫描目录。归档/删除被锁住或 ACL 拒绝时不突破大小/数量上限，不继续产生更多归档；失败后 30 秒再试，清空活动文件会立即解除等待。异步获取写锁/lease 各最多等 1 秒；同步 best-effort 各最多 250 ms，无后台日志队列或每条 `Task.Run`。迁移诊断只进行有界 best-effort 写入。两处磁盘都失败/等待耗尽时会丢弃该诊断记录，业务异常仍按原路径传播。

早期服务启动、bootstrap、Frontend crash 和 Backend fallback 不依赖普通 Logger/DI。正常目的地不可用时独立尝试该进程的 `%TEMP%\ContextMenuMgr\Logs`，其 lease 使用同级 `%TEMP%\ContextMenuMgr\Locks`，不与正常根共享锁；同样为 2 MiB/5 卷。LocalSystem 的 TEMP 可能位于 Windows 系统临时目录。双方均失败时不递归记录日志失败。TrayHost 磁盘写入失败采用 best-effort 丢弃，不干扰通知。

默认 Warning 不再强制记录成功的 Ping、快照、WPS 待审核等 routine query Start/End；Information 可恢复这些记录。mutation/进程维护操作的 Start/End、失败、超时、不确定结果保持关联 ID、命令、目标及耗时证据，即使配置 Error 也保留 request 失败。内部 Backend Warning/Error 独立按级别处理，不能被 query 分类吞掉。

Package discovery 按 SID 和 metadata/enumeration/manifest 阶段聚合：首个条件立即 Warning；全部失败的 package/stage/异常/HRESULT/消息与数量参与签名，前三个样本用于定位。相同条件每 15 分钟输出 `PackageDiscoveryWarningSummary` 和 `SuppressedOccurrences`；变化、恢复或容量淘汰时汇总已抑制次数。最多保留 32 个 SID/阶段条件，每条件最多 8192 字符样本；部分 enumeration 失败不会宣称其它阶段已恢复。不全局忽略任何 HRESULT，也不改变 package 枚举或 registry 行为。

“清除全部日志”在后台执行一次维护：逐流取得相同双 lease，删除精确归档名、截断活动文件，涵盖上述七种固定文件名、当前用户 emergency TEMP，以及机器 `SystemTemp`（默认 Windows `SystemTemp`）和旧机器 TMP/TEMP（默认 Windows `Temp`）内的应用日志子目录。没有活动日志/归档的流不创建 lease。未知 `.log`、状态库、设置、备份和 reparse-point 文件不处理；不搜索其它用户 profile 或自定义 service-process-only TEMP。锁住/保护或无法检查的目录/文件显示部分失败及具体路径；SYSTEM TEMP 的 ACL 可能拒绝普通前端访问，需管理员单独清理。已清理的文件不会被误报为全部成功；不停止服务，继续记录，Frontend 自动补回会话头。清理不删除 Locks 目录或持久 lease、不删除其它进程持有的旧 lock；仅在本次完整 lease 结束后按上述规则退休自己的旧路径。

设置页“打开日志文件夹”仍创建并打开 `RuntimePaths.LogsDirectory`，不打开根或 Locks；“打开数据文件夹”仍打开原有 `DataDirectory` 根。Portable 从旧 ProgramData 迁移日志时，仅在源/目标两侧取得双 lease 后 copy-if-missing 七种活动日志和精确有效归档，不覆盖、不复制 lock、不递归遍历未知目录；旧日志原件保留。其它设置、备份和 Programs 的既有迁移不变。

Windows 手工验收：分别在 Installer 和 Portable 包启动 Frontend、Service、TrayHost，确认两个同级目录及标准用户/LocalSystem 的实际写入；保留 v1.7.9 进程跨升级运行并触发轮转，确认两版本争用同一旧 lease、归档完整且不超预算；并发“清除全部日志”后继续触发日志，锁住一个归档确认部分失败；在 Logs 缺失时点击“打开日志文件夹”，确认只创建并打开 Logs，再核对“打开数据文件夹”仍是根。另测只读 Portable 根和 SYSTEM TEMP 不可访问时的实际 fallback、进程强制终止后恢复。单元测试和编译不能代替这些 SCM/Explorer/多账号验收。

报错前请收集整个 Logs 文件夹中的活动和归档日志，而非只发送 `backend.log`。复制时若遇到轮转占用可重试；不需要停止服务。正常目录不可用时也收集对应进程 TEMP 中的日志。排查超时应切换 Information 后复现，以获得成功查询的完整关联时间线；涉及未确认 mutation 的证据默认仍保留。

## 旧 writer 兼容退休

维护决定：继续兼容 #134/v1.7.9 的旧 `<active>.lock` writer。每次操作都先 Locks lease、再旧 lease，即使旧路径暂不存在也不能跳过。只有维护者明确停止支持新旧 writer 共用根（仍运行旧进程、旧包再次启动及降级均包括），并验证 Installer/Portable 停机升级约束后才可移除。一次 open/进程枚举/没有旧 lock 文件均不是安全退休证据。

## 验证与维护

`RollingLogFileTests` 覆盖滚动、所有权、并发、恢复、真实旧路径子进程竞争；`PackageDiscoveryWarningAggregatorTests` 覆盖有界告警。实际 Installer/Portable、普通用户/LocalSystem、WPF OnExit、只读根、SYSTEM TEMP 权限、并发 clear、崩溃和跨版本争锁仍需 Windows 验证。Portable 换用户/机器时同时检查 state/backup quarantine、cached identity 和本地备注，不能只改 JSON 指纹即声称多用户端到端成立。

修改 RuntimePaths、writer/lease、预算、清理、migration 或 ACL 时更新本文；路径排错只链接这里，避免其它文档保留旧表。
