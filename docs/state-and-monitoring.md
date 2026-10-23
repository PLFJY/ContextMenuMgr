# 持久状态、监控与审核

本文是常规菜单/Win11/WPS baseline 和状态库格式、合并、恢复的权威说明；物理写入见[注册表模型](registry-model.md)和[Win11](windows11-context-menu.md)。OpenWith 使用独立 section，其规则与时序只在 [OpenWith](openwith.md) 定义，不套用常规隔离矩阵。

## 入口与保存格式

- [ContextMenuRegistryCatalog](../ContextMenuMgr.Backend/Services/ContextMenuRegistryCatalog.cs)：`RunPersistentStateOperationAsync` 串行快照、审核、删除/恢复与 reconciliation 的 Load → Merge → Save。
- [ContextMenuRegistryMonitor](../ContextMenuMgr.Backend/Services/ContextMenuRegistryMonitor.cs)：五秒轮询、稳定内存 baseline、session 重建与 OpenWith 调度。
- [ContextMenuStateStore](../ContextMenuMgr.Backend/Services/ContextMenuStateStore.cs)：store gate、schema parser、原子替换、隔离与恢复。
- [PersistedContextMenuState](../ContextMenuMgr.Backend/Services/PersistedContextMenuState.cs)：desired/observed、pending、删除备份、visibility provenance、子菜单引用、Win11 恢复日志。
- [ContextMenuChangeClassifier](../ContextMenuMgr.Backend/Services/ContextMenuChangeClassifier.cs)：身份及外部变化比较；恢复元数据不参与 Added/Modified 判断。

当前 envelope 为 `schemaVersion=3`，包含 `hostIdentity`、`states` 和独立 `openWith`。仍读取 schema 2 与 legacy raw dictionary，不由迁移凭空生成授权规则；未来 schema 拒绝读取，不能当损坏清空。普通 Save 在 store gate 中重新读取并保留最新 OpenWith section；OpenWith callback 同 gate 读取/合并最新常规 states 后保存。catalog gate 与 store gate 各有职责，不能以单次 Load/Save 自身有锁推断整段 read/merge/write 安全。

ShellVerb provenance、Win11 `Windows11BlockBackups`、Pin to Start provenance、删除备份和禁用的 SubCommands 引用属于恢复/归属信息；不能作为外部变化身份。各自兼容条件由对应子系统说明。

## 原子保存与损坏恢复

同目录唯一 `.tmp-<guid>` → write-through flush/close → 生产 parser 验证 → `File.Replace` current，保留已验证旧 current 为 `.bak`。无 current 时安装新文件；临时文件不是权威 generation，旧于 24 小时的遗留文件 best-effort 清理。

仅 JSON/结构损坏触发恢复：保留原文件至 `Quarantine/corrupt-state-...`，验证 `.bak` 后恢复；无有效备份则返回空状态，由带交互用户的完整快照重建 baseline。读写 ACL/I/O 失败和未来 schema 不走 corruption reset。恢复本身不写注册表；旧备份可能缺少最近本地意图，通知只报告恢复/重建，不保证这些意图全部找回。

Portable 的 foreign-host 隔离先于使用旧状态；实际 identity 来源、缓存边界和备份目录限制见[运行时数据](runtime-data-and-logging.md#portable-host-identity)。Installer 不应用同样的 foreign-host guard，不能声称共享根文件是每用户独立数据库。

## 状态库与显式 baseline

常规菜单与 WPS/Office 合成项共享 JSON 文件，但各自使用独立的显式 baseline 标记：

- `internal:baseline:regular:v1`
- `internal:baseline:wps-office:v1`

不能再用“状态库中存在任意记录”推断某一数据源已经建立 baseline。即使首次扫描结果为零项，也必须写入对应标记；否则未来第一个真实新增会被误当成首次采纳。

只有能够解析到交互式用户上下文的完整快照才允许创建首次 baseline。无 SID 的服务早期快照可以用于显示和诊断，但不得提交不完整 baseline。常规快照、WPS 快照、监控 reconciliation 和用户操作通过同一个持久状态操作门串行执行，避免多个“Load -> Merge -> Save”流程互相覆盖。

首次采纳的每个实际项同时保存：

- `ObservedEnabled = 当前注册表开关状态`
- `DesiredEnabled = 当前注册表开关状态`
- `IsPendingApproval = false`
- 当前显示名、命令、图标、CLSID、路径和属性元数据

因此首次运行时，原本已经关闭的菜单也会形成 `DesiredEnabled=false` 策略；监控运行期间若相邻稳定快照观察到它被第三方重新打开，后端可以静默关回去。启动首轮已经存在的差异属于离线修改，不走这条纠偏路径。

## 六条外部变化核心规则

下表是拦截、状态库和 UI 提示的行为矩阵；自动写入适用于具备安全自动禁用能力的项。只读 PropertySheetHandlers、Windows 受保护命令、共享/发现不完整的 Win11 CLSID 等不会因此获得 mutation 权限，其限制见对应主文档。后续改动不得引入与此表冲突的 Reappeared 或泛化 consistency 分支。

| 编号 | 场景 | 后端行为 | 前端状态 |
| --- | --- | --- | --- |
| 1 | 软件运行期间出现未知菜单项 | 下一轮监控发现后禁用并写入状态库，进入待审核；隔离失败时保留为未知项并在后续轮询重试 | `Added` + `IsPendingApproval=true`（成功隔离后） |
| 2 | 软件运行期间，已知项从开变关 | 不自动重新打开，保留旧 baseline 供用户确认 | `Modified`，不再叠加“状态不一致” |
| 3 | 软件运行期间，相邻稳定快照观察到已知项从关变开 | 静默重新关闭，继续保留 `DesiredEnabled=false` | 纠偏后无待审核、无外部修改、无 generic consistency |
| 4 | 软件停止后出现未知菜单项，重启时发现 | 不隔离、不自动禁用，等待用户确认当前变化 | `Added`，不进入待审核 |
| 5 | 软件停止后已知项开关改变，重启时发现 | 无论开变关还是关变开都保留当前注册表状态，等待用户确认 | `Modified`，不待审核 |
| 6 | 实际注册表项已删除 | 完整快照连续确认缺失后，从活动 baseline 移除 | 不显示 Removed、不参与以后比对 |

“连续确认缺失”当前为两次具有交互用户上下文的持久快照，用于避开注册表枚举瞬态。上下文不完整的服务快照不得清理状态。

应用主动执行 Delete 时产生的 `.reg` 备份属于恢复记录，不属于活动监控 baseline。它可以暂时保留以支持 Undo Delete，但不参与新增、修改或重新出现分类。若相同注册表 key 再次真实出现，旧恢复记录和旧备份会被清理，该 key 按普通未知 `Added` 处理，不再使用 `Reappeared` 状态机。

## 实现时序

### 状态库缺失或被重置

```text
解析 frontend userContext
-> 枚举常规注册表 + Win11 项
-> 当前状态全部写入 regular baseline（包括已关闭项）
-> 写 regular baseline marker
-> 枚举 WPS/Office 合成项
-> 当前 WPS 状态全部写入 baseline，全部已确认
-> 写 WPS baseline marker（即使当前为零项）
-> 返回无 Added / Modified / Pending / generic inconsistency 的快照
```

首次 baseline 不能经过普通“未知项”运行时隔离路径。WPS 的默认打开方式、图标覆盖和 ShellNew 注入只要在重置时已经存在，都作为当前事实采纳，不进入待审核，也不暴露外部变化徽标。

### 运行时轮询

```text
读取持久状态
-> 获取带交互用户上下文的实际快照
-> 与 monitor 上一个稳定内存快照比较
-> 仅对本轮确认的“上一轮关、本轮开”执行 DesiredEnabled=false reconciliation
-> 如有写入，只重新枚举一次
-> 比较稳定 Id 和元数据
-> 未知 Added：隔离并待审核
-> 已知 Modified：只高亮
-> 连续缺失：移除活动 baseline
-> 用 post-reconciliation 快照更新 monitor knownItems
```

监控循环独立于前端 Pipe 连接运行。普通开关、Win11 blocked list 和用户级 Classes 必须继续使用 frontend/interactive SID，不能使用服务进程 HKCU。
常规扫描只枚举 HKLM 和当前交互用户的 `HKEY_USERS\<SID>\Software\Classes`；暂时取不到交互用户上下文时，监控保留已有运行时基准，不进行新增项隔离或静默纠偏。软件内开关成功提交后，其写后条目会同步到 monitor 的内存基准，避免“用户关闭→应用在下一次轮询前重新打开”被误看成“开→开”。

### 启动/离线比对

监控启动时首先取得原始实际快照并建立内存 `knownItems`，不得先执行 disabled-state reconciliation。已有 baseline 下的未知项保留 `Added` 标记，但启动阶段不会触发 `ItemDetected`，因此不会隔离或进入待审核。元数据和开关状态无论往哪个方向变化都按 `Modified` 显示。只有后续两个相邻稳定运行时快照观察到关变开，才进入规则 3 的静默纠偏。

交互式 session 登录、解锁或连接时，monitor 仍会重建内存 baseline。若可见项数量明显低于持久活动状态数量，则延后重建，避免用户 hive 尚未加载时制造运行时新增通知。

### 一致性状态

开关差异必须落入规则 2、3 或 5，因此 `GetConsistencyIssue` 不再为 `DesiredEnabled != actual` 生成 generic consistency。用户确认 `Modified` 后，`AcknowledgeItemState` 用当前实际值更新 `DesiredEnabled`、`ObservedEnabled` 和元数据。

generic consistency 只保留无法自动归一化的真实注册表结构冲突，以及经典 handler 同时命中旧版全局 Blocked 等诊断。它不能用于表达普通开关变化；active/disabled 双键并存已属于自动修复流程，不能再显示这条泛化提示。

### 物理注册归一化

传统 handler 双容器与多副本规则见[注册表模型](registry-model.md#传统-shellextension-物理状态)，归一化后的实际变化仍按本章矩阵分类。

### 竞争与失败

- 所有会读写 `ContextMenuStateStore` 的常规快照、WPS 快照、审核、删除/恢复和 reconciliation 必须通过 catalog 的持久状态操作门串行化。
- reconciliation 写入失败时保留 `DesiredEnabled=false`，记录结构化日志并在后续快照重试；不得伪造 `ObservedEnabled=false`，也不得转成待审核。
- 已禁用 ShellVerb 的同一稳定 Id 若被第三方重新创建为可见项，且命令等物理代际指纹改变，静默重新禁用时丢弃旧代际恢复数据、捕获新代际的原值并验证写入；旧代际恢复数据仍不得用于启用新注册项。连续两次完整快照确认删除并移出 baseline 后重新出现的项仍按未知 `Added` 处理。
- ContextMenuMgr 自己的写入必须从 post-write 快照更新 baseline，不能被下一轮识别为外部变化。
- 普通开关在物理写入和逻辑验证成功后，必须用同一次写后快照的条目更新开关状态与全部可比对元数据（包括 handler 移动后的路径），并将返回前端的本次操作结果清除 `DetectedChangeKind`；写前条目及其 `Modified` 标记不能作为软件自身写入的结果。
- `SuppressNextDetection` 只能抑制一次由应用自身恢复/创建导致的检测，建立 monitor baseline 时必须消费。
- WPS/Office 是否已有 baseline 只看 WPS marker 或旧版 WPS state，不能被常规菜单 state 影响。

## 实现限制：未可信的 Portable identity

`ContextMenuStateStore.SaveCoreAsync` 在 Portable identity 未可信时记录 ContextMenuStateStoreSaveSkipped、标记 IsCurrentHostIdentityVerified=false 并直接返回，未抛保存异常。OpenWith coordinator 显式检查该标记；不能从其它调用方 `await SaveAsync` 无异常一概推断已写盘。设计上 mutation 应以持久提交作为成功条件，但此分支与逐调用方传播/启动无用户场景仍需 Windows 集成核查。

## 审核、重置与并发结果

`BackendRuntime.HandleNewItemDetectedAsync` 接收 monitor 同次扫描的用户上下文，再调用 `QuarantineNewItemAsync`；隔离失败不采纳为已稳定新项。审核 `ApplyDecision` 在 gate 内取得一次完整快照并复用：Allow 启用并清 pending，Deny 保持禁用并清 pending，Remove 走删除路径；Win11 不提供删除 package 的伪操作。

`ContextMenuApprovalIdentity` 将同一逻辑项聚合，Win11 按 CLSID 控制域去重，不等于 category-specific Id。UI 通知基线由 `WorkspaceNotificationState` 分开保存常规/WPS 去重，不能反向建立持久 baseline。WPS 前端审核刷新启动先等五秒，成功后间隔十五秒，失败指数退避到六十秒，主快照加载与该循环分开。WPS 检测范围与处置见[SpecialMenu](special-menus.md#wps-与-microsoft-office-共存)。

重置通过 `ResetStateDatabase`，持有 catalog/store gates 删除 current、backup、临时 generation 和当前 host 的删除备份；OpenWith rules/reviews/baseline 同时撤销。随后完整快照采纳当前物理状态，重置不会自动启用旧禁用项。Registry Write Protection 的设置文件和 ACL 独立，不能只删保护设置来假装解锁。

## 验证与维护

已有永久测试包括 `ContextMenuStateStoreTests`、`ExternalChangeStateMachineTests`、`ContextMenuMonitoringRulesTests`、`InternalToggleBaselineTests`、`OfficeSuiteCoexistenceApprovalTests` 和 `MutationOutcomeTests`。它们覆盖格式、恢复、状态分类和竞态，不证明真实服务/Explorer。

Windows VM 手验：空库与重置（含零项）；已有 baseline 的离线新增及双向变化；运行中新项隔离失败后重试、已知关变开纠偏；两次完整缺失后重新注册；并发 WPS/普通/OpenWith 保存；损坏 current/backup、文件替换拒绝、升级/降级拒绝未来 schema。分别核对物理状态、fresh snapshot、current/backup、重启与 UI 提示。普通监控只选择当前交互上下文，不应从 OpenWith 的多 SID 支持推导常规监控覆盖所有登录用户。

修改 schema、baseline/identity、缺失确认、reset 或合并门时更新本文及相关兼容主文档；不把某次历史测试通过数量写成长期验收结论。
