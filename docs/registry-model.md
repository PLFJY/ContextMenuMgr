# 注册表模型与传统右键菜单实现

## 1. 本文目的

本文解释传统右键菜单如何在 ContextMenuMgr 中被扫描、建模、禁用、删除、恢复、审核和备份。这里的“传统右键菜单”主要指注册在 `shell` 和 `shellex\ContextMenuHandlers` 体系下的菜单项，不包含 Windows 11 packaged context menu，也不包含 ShellNew / SendTo / WinX 等 SpecialMenu。

相关主实现位于 `ContextMenuMgr.Backend/Services/ContextMenuRegistryCatalog.cs`、`ContextMenuRegistryMonitor.cs`、`ContextMenuStateStore.cs` 和 `RegistryBackupService.cs`，前后端传输模型位于 `ContextMenuMgr.Contracts/ContextMenuEntry.cs`。

## 2. Windows 传统右键菜单基础

传统右键菜单不是单一注册表路径，而是一组按对象类型分散的注册表入口：

| 概念 | 说明 |
| --- | --- |
| `shell` | 常见的 verb 菜单项，通常包含子键和 `command` 子键。适合“打开、编辑、用某程序处理”这类命令。 |
| `shellex\ContextMenuHandlers` | COM Shell Extension 处理器入口，通常通过 CLSID 指向第三方 DLL。 |
| `CLSID` | COM 类标识，Shell Extension 通常通过 `CLSID\{...}\InprocServer32` 找到实际 DLL。 |
| `command` | `shell` verb 的实际命令行。 |
| `MUIVerb` | 菜单显示名，可能是普通字符串，也可能引用资源。 |
| `LegacyDisable` | 常见禁用标记之一。不同入口的禁用策略不完全相同。 |
| `Extended` | 只在按住 Shift 时显示的标记。 |
| `NoWorkingDirectory` | 影响 Explorer 调用命令时的工作目录行为。 |
| `Icon` | 菜单图标来源，可包含路径和图标索引。 |
| `AppliesTo` | Explorer 条件表达式，决定菜单项适用范围。 |
| 用户级 Classes | 当前用户的 `Software\Classes`，服务端应通过 `HKEY_USERS\<SID>\Software\Classes` 定位。 |
| 机器级 Classes | `HKEY_LOCAL_MACHINE\SOFTWARE\Classes`，影响所有用户。 |

不要把 `HKCR` 当成真实单一路径。`HKEY_CLASSES_ROOT` 是用户级 Classes 与机器级 Classes 的合并视图，读取时方便，写入时必须明确写到用户级还是机器级。后端服务不能用 LocalSystem 的 `HKCU` 代替前端用户的 `HKEY_USERS\<SID>`。

## 3. 项目中的 MonitoredRoots

`ContextMenuRegistryCatalog` 用 `MonitoredRoots` 定义传统菜单扫描范围。当前代码覆盖以下 `ContextMenuCategory`：

| 分类 | 主要注册表路径 |
| --- | --- |
| `File` | `*\shell`、`*\shellex\ContextMenuHandlers`、`*\shellex\-ContextMenuHandlers` |
| `AllFileSystemObjects` | `AllFilesystemObjects\shell`、`AllFilesystemObjects\shellex\ContextMenuHandlers`、`AllFilesystemObjects\shellex\-ContextMenuHandlers` |
| `Folder` | `Folder\shell`、`Folder\shellex\ContextMenuHandlers`、`Folder\shellex\-ContextMenuHandlers` |
| `Directory` | `Directory\shell`、`Directory\shellex\ContextMenuHandlers`、`Directory\shellex\-ContextMenuHandlers` |
| `DirectoryBackground` | `Directory\Background\shell`、`Directory\Background\shellex\ContextMenuHandlers`、`Directory\Background\shellex\-ContextMenuHandlers` |
| `DesktopBackground` | `DesktopBackground\shell`、`DesktopBackground\shellex\ContextMenuHandlers`、`DesktopBackground\shellex\-ContextMenuHandlers` |
| `Drive` | `Drive\shell`、`Drive\shellex\ContextMenuHandlers`、`Drive\shellex\-ContextMenuHandlers` |
| `Library` | `LibraryFolder\shell`、`LibraryFolder\shellex\ContextMenuHandlers`、`LibraryFolder\Background\shell`、`LibraryFolder\Background\shellex\ContextMenuHandlers`、`UserLibraryFolder\shell`、`UserLibraryFolder\shellex\ContextMenuHandlers` 及对应 disabled mirror |
| `Computer` | `CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\shell`、`...\shellex\ContextMenuHandlers`、`...\shellex\-ContextMenuHandlers` |
| `RecycleBin` | `CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shell`、`...\shellex\ContextMenuHandlers`、`...\shellex\-ContextMenuHandlers`、`...\shellex\PropertySheetHandlers` |

`-ContextMenuHandlers` 是项目识别的 disabled mirror 形态之一，用于表示从启用位置移出的 Shell Extension handler。不要把所有禁用都简化成一个注册表值。

## 4. ContextMenuEntry 模型

`ContextMenuEntry` 是传统菜单、Win11 菜单和审核流程共享的传输模型。当前重要字段如下：

| 字段 | 作用 |
| --- | --- |
| `Id` | 项目内部稳定标识，不能只用显示名替代。 |
| `Category` | 菜单适用分类，对应 `ContextMenuCategory`。 |
| `EntryKind` | 菜单来源类型，例如 `ShellVerb` 或 `ShellExtension`。 |
| `KeyName` | 注册表子键名或逻辑 key。 |
| `DisplayName` | 前端显示名，可能来自 `MUIVerb`、默认值、CLSID 或推断值。 |
| `EditableText` | 可编辑显示文本，当前并非所有项都有。 |
| `RegistryPath` | 给用户或前端展示的注册表位置。 |
| `BackendRegistryPath` | 后端实际操作时使用的位置，可能不同于展示路径。 |
| `SourceRootPath` | 扫描根路径，用于判断来源和后续操作分流。 |
| `CommandText` | `shell` verb 的命令行，Shell Extension 通常为空。 |
| `CanEditCommandText` | 标识普通 legacy ShellVerb 是否允许编辑 `<verb>\command` 默认值；多命令父级、DelegateExecute、DropTarget、ExplorerCommandHandler、Shell Extension 和 Win11 项为 false。 |
| `HandlerClsid` | `shellex` handler 的 CLSID。 |
| `FilePath` | 解析出的命令程序或 COM server 路径，best-effort。 |
| `IconPath` / `IconIndex` | 图标路径和索引，best-effort。 |
| `IsEnabled` | 合并真实注册表和项目状态后的启用状态。 |
| `IsPresentInRegistry` | 当前真实注册表是否仍存在该项。 |
| `IsDeleted` | 项目状态库认为该项已删除。 |
| `IsPendingApproval` | 新增项或外部变化需要用户审核。 |
| `HasBackup` / `DeletedAtUtc` | 删除备份相关状态。 |
| `HasConsistencyIssue` / `ConsistencyIssue` | 状态库和真实注册表不一致时的诊断信息。 |
| `DetectedChangeKind` / `DetectedChangeDetails` | 监控发现的新增、删除、修改等变化。 |
| `IsWindows11ContextMenu` | 标识 Win11 packaged context menu。传统菜单通常为 `false`。 |
| `Notes` | 诊断和补充说明，不应参与主逻辑判断。 |

## 5. Snapshot 构建流程

当前后端 snapshot 是“真实注册表 + 状态库”的合并结果，不是单纯枚举注册表：

```text
注册表枚举
-> 构建 actual entries
-> 读取 ContextMenuStateStore
-> 合并 pending / deleted / backup / consistency 状态
-> 标记 enabled / disabled / deleted / pending
-> 检测新增项和外部变化
-> 合并 Windows 11 packaged entries
-> 返回前端 snapshot
```

`ContextMenuRegistryMonitor` 基于周期性 snapshot 比较发现变化。首次 baseline 和用户登录后的 baseline 重建很重要，否则容易把系统已有项误判为新安装项。

## 6. 启用 / 禁用策略

传统菜单的禁用方式按 `EntryKind` 和实际路径分流：

| 类型 | 当前实现倾向 |
| --- | --- |
| `shell` verb | 普通开关通过 `ShellVerbVisibility` 统一判断和写入，综合处理 `HideBasedOnVelocityId`、`ProgrammaticAccessOnly`、`LegacyDisable` 和相关 `CommandFlags`，避免只依赖 `LegacyDisable`。 |
| `shellex` handler | 可能在 `ContextMenuHandlers` 与 disabled mirror 路径之间移动。 |
| disabled mirror path | 用 `-ContextMenuHandlers` 识别被移出的 handler。 |
| Windows 自带标记 | `Extended`、`NoWorkingDirectory`、`NeverDefault` 等属于属性，不等于禁用状态。 |

不要承诺所有菜单项都能用同一种方式开关。某些项由第三方安装器、系统策略或 COM handler 自身逻辑控制，项目只能 best-effort 地修改注册表状态并记录结果。

Recycle Bin 页面额外投影一个虚拟传统项 `special:recyclebin:pintohome`，用于控制系统的“Pin to Quick access” verb。它的真实注册表位置是 `HKCR\Folder\shell\pintohome`，但只在 Recycle Bin 分类显示。启用状态不使用普通 shell verb 隐藏值，而是检查 `AppliesTo` 是否包含 `System.ParsingName:<>"::{645FF040-5081-101B-9F08-00AA002F954E}"`；禁用时只追加这个 Recycle Bin 排除条件，启用时只移除这个排除条件并保留其它 `AppliesTo` 子句。如果 `pintohome` key 不存在，快照不显示该虚拟项。

普通 ShellVerb 的命令文本编辑不解析命令行、不拆分程序和参数、也不重写引号；`SetCommandText` 只把用户输入的字符串原样写到 `<verb>\command` 的默认 `REG_SZ`。后端会先检查 `CanEditCommandText` 和当前注册表形态，并经过 Registry Write Protection preflight；不支持 Shell Extension、Windows 11 packaged context menu、`SubCommands` / `ExtendedSubCommandsKey` 父级、`DelegateExecute`、`DropTarget\CLSID` 或 `ExplorerCommandHandler` 项。

传统分类页支持通过前端的 `CreateSceneMenuItem` 入口创建自定义 classic 菜单项。分类页把当前 `ContextMenuCategory` 映射为 `ContextMenuSceneKind.CustomRegistryPath` 和对应的 HKCR scene root（例如 `HKCR\*\shell`、`HKCR\Directory\shell`、`HKCR\Drive\shell`），再通过 Backend Pipe 交给 `FileTypeSceneMenuService.CreateSceneMenuItemAsync` 写入 `shell\<verb>\command`。首版只创建普通 shell verb 命令：前端负责生成带引号的命令行并追加 `%1` 或 `%V` 选中对象占位符，后端负责 Registry Write Protection preflight、创建不覆盖已有项的唯一 key、通知 Shell 关联变更，并在状态库中抑制本次新建项检测，避免把用户主动创建的项标为待审核。本入口不创建 ShellEx handler、不复制 CommandStore 引用，也不创建子菜单。

文件类型页的 Custom Extension 场景不是只扫描扩展名本身。后端会用前端用户上下文解析该扩展名直接关联的 class roots，并只枚举实际存在 `shell`、`shellex\ContextMenuHandlers` 或 `shellex\-ContextMenuHandlers` 的候选 root。候选来源包括 `SystemFileAssociations.<ext>`、直接 `.<ext>` key、用户与机器级扩展名默认 ProgID、用户与机器级 `OpenWithProgids`、以及当前用户 `FileExts\<ext>\UserChoice\ProgId` / `FileExts\<ext>\OpenWithProgids`。用户级读取必须走 `HKEY_USERS\<sid>`，不能用服务进程的 `HKCU` 或 `HKCR` 合并视图推断当前用户。

例如 PowerShell 7 可把 `.ps1` 的 “Run with PowerShell 7” 注册到 `HKLM\SOFTWARE\Classes\Microsoft.PowerShellScript.1\Shell\PowerShell7x64`。Custom Extension `.ps1` 页面应通过 `.ps1` 的关联 ProgID 扫描到 `Microsoft.PowerShellScript.1\shell`，并把条目的真实来源保持为 `Microsoft.PowerShellScript.1\shell\PowerShell7x64`，这样禁用、恢复、删除和编辑仍写回实际 ProgID 路径，而不是误写到 `.ps1\shell`。

File Types 的隐藏批量管理视图通过 `FindRelatedFileTypeMenuItems` 做一次性相关项扫描。该扫描覆盖 HKLM 与前端用户 `Software\Classes` 下的扩展名 key、ProgId key、以及 `SystemFileAssociations\<extension|perceivedType>` 的 `shell` / `shellex\ContextMenuHandlers` / disabled mirror 根；它不会在服务启动时运行，也不会把扫描到的文件类型项加入普通全局启动检测 baseline。ShellVerb 相关性要求命令程序路径规范化后相同并且 key name 相同；ShellExtension 相关性要求 Handler CLSID 相同；显示名只用于展示，不作为匹配依据。批量扫描会把匹配 query 的已删除备份状态合并回结果，因此删除后的相关项仍可在批量页撤销。批量视图中的开关和删除继续走普通 `SetEnabled` / `DeleteItem`，并通过 fallback `ContextMenuEntry` 支持不在普通 workspace snapshot 中的 scene-only 文件类型项。文件类型 `open` / `edit` 核心 ShellVerb 禁止删除；用户若想隐藏这类项，应使用禁用开关。

## 7. 删除、恢复与备份

删除不总是“立即永久删除”。`RegistryBackupService` 在删除前通过 `reg.exe export` 导出注册表备份。Installer 包下备份保存在 `%ProgramData%\ContextMenuMgr\DeletedBackups`；Portable 包下备份保存在当前 host identity 前缀对应的 `<应用目录>\Data\DeletedBackups\<host-prefix>`。host identity 由 Windows `MachineGuid` 和前端用户 SID 的 SHA-256 指纹表示，JSON 和目录名只保存指纹/前缀，不保存原始 MachineGuid 或 SID。

Portable 包被复制到另一台 Windows 或另一个用户配置文件时，旧 `DeletedBackups` 不再可信。启动时后端会把当前 host 前缀之外的备份移动到 `Data\Quarantine\foreign-host-...`；恢复删除项时也只允许导入当前 host-scoped backup 目录中的 `.reg` 文件。其它路径会返回“属于不同 Windows 安装或用户配置文件，不能安全恢复”的错误。

| 操作 | 说明 |
| --- | --- |
| 删除 | 尝试备份后删除真实注册表项，并在 `ContextMenuStateStore` 中记录删除状态。 |
| 恢复 | 优先通过备份导入恢复，再更新状态库。 |
| Undo delete | 面向用户的恢复入口，依赖备份存在。 |
| Purge backup | 清理已保留的删除备份，之后恢复能力会下降。 |

保留备份是为了降低误删风险，也让审核中的 Remove 操作可回滚。备份本身不是注册表真实状态，导入失败或目标权限变化仍可能导致恢复失败。

恢复用户级或 HKCR overlay 相关菜单项时，后端必须带 frontend user context 重读快照，不能用服务进程的 HKCU 或无用户上下文的全局快照判断恢复结果。

## 8. 状态库

`ContextMenuStateStore` 保存项目自己的状态，路径为 `RuntimePaths.StateDatabasePath`。它用于记录待审核、删除、备份、外部变化和一致性信息。

状态库不是注册表本身。真实注册表可能被第三方安装器、系统更新或用户手工修改，短时间内会与状态库不一致。snapshot 构建会尽量合并并标记不一致，但不要在代码里假设状态库一定代表当前系统真实状态。

## 9. 新增项检测与 Quarantine

`ContextMenuRegistryMonitor` 定期重新取 snapshot，并与 baseline 比较。发现新增项或被外部重新启用的项后，`BackendRuntime` 会调用隔离逻辑，并通过 `BackendNotification` 通知 TrayHost 和前端审核页。

```text
Monitor baseline
-> 周期性 snapshot
-> ReconcileAndRefreshSnapshotAsync (先校正显式禁用策略漂移)
-> 对比新增 / 修改 / 删除
-> 按 DetectedChangeKind 分流:
     Added      -> QuarantineNewItemAsync
     Reappeared -> QuarantineReappearedItemAsync
-> PipeNotificationKind.ItemDetected
-> TrayHost 通知 / 前端审核页
```

Quarantine 是后端状态，不只是 UI 徽标。首次 baseline 要谨慎处理，尤其是服务启动、用户登录、Session connect 后，避免把已有菜单全部当成新增项。

`BackendRuntime.HandleNewItemDetectedAsync` 不再假设所有检测到的项都是全新项。它根据 `DetectedChangeKind` 选择隔离路径：`Added` 走 `QuarantineNewItemAsync`，`Reappeared` 走 `QuarantineReappearedItemAsync`。不通过 `DisplayName` 分类，只使用稳定 `Id`、注册表路径、key name、source root 和 CLSID 身份规则。

## 10. 外部变化状态机 (Issue #11)

### 10.1 设计目标

第三方应用（例如 Tailscale）可能在 ContextMenuMgr 后端服务启动前、启动期间或运行期间删除并重建自己的右键菜单注册表项。如果项目只靠 visibility 值（`HideBasedOnVelocityId`、`ProgrammaticAccessOnly`、`LegacyDisable`）禁用 ShellVerb，当第三方删除整个 key 时这些值会消失，key 被重建后菜单项会重新启用。

解决方案不是启动延迟、服务依赖、注册表 ACL workaround 或更快的轮询。解决方案是**保留并执行用户的显式禁用意图**。

核心分类逻辑位于 `ContextMenuChangeClassifier`，这是一个不接触注册表的纯确定性 helper，可在单元测试中直接测试。

### 10.2 运行时矩阵（监控运行期间）

| 场景 | 条件 | 行为 |
| --- | --- | --- |
| 显式禁用项被外部重新启用 | `state` 存在；`IsDeleted=false`；`DesiredEnabled=false`；实际项已启用 | 自动重新禁用；不标记待审核；不发送审核通知；保留 `DesiredEnabled=false`；记录日志 |
| 之前删除的项重新出现 | `state.IsDeleted=true`；运行时检测到相同稳定标识的注册表项 | 立即禁用/隔离；`IsPendingApproval=true`；发送正常审核通知；保留删除备份和 `DeletedAtUtc`；`DetectedChangeKind=Reappeared` |
| 完全未知的项被创建 | 无 `state`；运行时出现 | 立即禁用/隔离；`IsPendingApproval=true`；发送正常审核通知；`DetectedChangeKind=Added` |

### 10.3 启动/离线矩阵（监控未运行期间）

| 场景 | 条件 | 行为 |
| --- | --- | --- |
| 显式禁用项被重新启用或重建 | 同上运行时条件 | 在启动 reconciliation 期间自动禁用；不询问用户；不标记待审核；在 reconciliation 完成后才建立监控 baseline |
| 之前删除的项重新出现 | 同上运行时条件 | **不**自动隔离；暴露 `DetectedChangeKind=Reappeared`；显示一致性警告/高亮；让用户决定 |
| 完全未知的项出现 | 同上运行时条件 | **不**自动隔离；暴露 `DetectedChangeKind=Added`；显示为离线外部修改；让用户决定 |

### 10.4 首次运行例外

当状态库为空（首次运行）时，当前机器状态被采纳为初始 baseline。不会隔离或高亮每个已存在的菜单项。`ClassifyItemMonitorAction` 在 `hasBaseline=false` 且 `state=null` 时返回 `None`。

### 10.5 其他必须保持的行为

- 已知元数据变更（command、显示文本、icon、CLSID、模块路径、属性等）保持 `Modified`/高亮，不自动回滚或隔离。
- 普通已知项的外部删除保持静默。
- **不**自动执行 `DesiredEnabled=true`。只有显式禁用策略（`DesiredEnabled=false`）被持续执行。
- 如果 reconciliation 写入因访问被拒、注册表消失、不支持的项类型或其他瞬态竞争而失败：记录失败日志；保持 persisted desired state 不变；不假装 `ObservedEnabled` 变为 false；保持一致性问题可见；在后续轮询中自然重试；不将失败转换为待审核。

### 10.6 Reconciliation 架构

`ContextMenuRegistryCatalog.ReconcilePersistedDisabledItemsAsync` 是内部 catalog 操作，对每个实际项：

1. 加载其 persisted state；
2. 要求 `state.IsDeleted=false`、`state.DesiredEnabled=false`、实际项 `IsEnabled=true`；
3. 通过现有 per-entry-kind 实现禁用（ShellVerb → `ShellVerbVisibility`；ShellExtension → 现有 blocking/mirror 逻辑；Win11 → `Windows11ContextMenuCatalog`）；
4. 保留 `DesiredEnabled=false`；
5. 仅在注册表写入成功后设置 `ObservedEnabled=false`；
6. 保持 `IsPendingApproval=false`；
7. 仅在至少一个写入成功时调用 `ShellChangeNotifier`。

reconciliation 返回 `DisabledStateReconciliationResult(HasChanges, ReconciledItemIds, FailedItemIds)`，让监控知道是否需要刷新 snapshot。

### 10.7 监控中的 Reconciliation 时序

**启动 baseline 建立**：
1. 读取 snapshot；
2. reconcile 显式禁用状态漂移；
3. 如果有变更，重新加载 snapshot；
4. 从 post-reconciliation snapshot 建立 knownItems。

**交互式 session baseline 重建**：同上流程。

**正常运行时轮询**：
1. 读取当前 snapshot；
2. reconcile 每个显式禁用漂移；
3. 如果 reconciliation 改变了任何内容，只重新加载一次 snapshot；
4. 然后分类运行时 Added/Reappeared；
5. 从 post-write 状态更新 knownItems，避免 ContextMenuMgr 把自己的纠正写入检测为新外部变化。

不单独为每个 item 重新加载整个 snapshot。

监控循环独立于前端 pipe 连接运行。`ReconcileAndRefreshSnapshotAsync` 在每次轮询时通过 `BackendUserContextResolver.TryResolveInteractiveUserFallback()` 解析当前交互式用户上下文，并传递给 `GetSnapshotAsync`。这确保 Win11 packaged COM 项和 `HKEY_USERS\<sid>` 下的 per-user 项在用户会话暂时不可用（屏幕锁定、UAC 提升、快速用户切换）时仍能被正确枚举。如果解析失败（无活跃用户会话），回退到无 userContext 的全局枚举行为。

### 10.8 大规模缺失保护

`BuildSnapshotAsync` 在清理缺失状态之前会计算"可被 prune 的缺失项"占"活跃状态总数"的比例。当缺失比例超过 25% 时，跳过 `ConsecutiveMissingSnapshots` 累加并重置已有计数。

这是防御性措施：当大量活跃 baseline state 同时从快照中消失时，通常是枚举不完整（用户会话未就绪、per-user hive 未加载、Win11 packaged COM 查询失败）而非真实批量外部删除。如果在这种情况下继续累加 `ConsecutiveMissingSnapshots` 并清理状态库，下一次重启时这些项重新出现会被误判为 `Added`，导致大量虚假"外部新增"通知。

真实的外部批量删除（如一次性卸载多个第三方软件）不会触发此保护，因为：
- 单个软件卸载通常只影响少量项，不会超过 25% 阈值；
- 即使超过阈值，保护只是延迟清理，不影响 `DetectedChangeKind=Removed` 的暴露。

### 10.9 Reappeared 隔离路径

`QuarantineReappearedItemAsync` 不复用 `QuarantineNewItemAsync`。后者会清除 `IsDeleted`、`DeletedAtUtc`、`BackupFilePath`，这会破坏删除来源和现有恢复备份。

专用 Reappeared 路径：
- 禁用实际重建的项；
- 设置 `IsPendingApproval=true`；
- 设置 `PendingApprovalChangeKind=Reappeared`；
- **保留** persisted `IsDeleted=true`（审核未解决期间）；
- **保留** `BackupFilePath`；
- **保留** `DeletedAtUtc`；
- 写入成功后设置 `ObservedEnabled=false`；
- 向前端暴露实际存在的注册表项；
- 暴露 `DetectedChangeKind=Reappeared`。

`PendingApprovalChangeKind` 是新增的可空枚举字段。旧 state 文件没有此字段时安全反序列化为 `null`。`IsPendingApproval` 设为 `false` 时自动清除此字段；设置非 null `PendingApprovalChangeKind` 时自动将 `IsPendingApproval` 翻转为 `true`。

### 10.10 Reappeared 审核决策

对于源自之前删除项的 pending approval（`PendingApprovalChangeKind=Reappeared`）：

| 决策 | 行为 |
| --- | --- |
| Allow | 启用并接受重建项；清除 `IsDeleted`；清除 `IsPendingApproval`；在清除 `BackupFilePath` 前删除旧备份文件；持久化 `DesiredEnabled=true` 和实际观察状态 |
| Deny | 保持重建项禁用；转换为普通显式禁用项；`IsDeleted=false`；`IsPendingApproval=false`；`DesiredEnabled=false`；`ObservedEnabled=false`；删除旧删除备份并清除 `DeletedAtUtc`/`BackupFilePath` |
| Remove | 删除当前重建的注册表项；最终状态 `IsDeleted=true`、`IsPendingApproval=false`；在删除前创建当前重建注册表项的有效备份；仅在成功后替换旧备份；删除被取代的旧备份文件不泄漏孤立文件；保留新的删除时间戳和当前元数据 |

不通过设置 `BackupFilePath=null` 静默丢弃备份文件。

普通 Added 项的审核行为保持不变。

### 10.11 显式禁用状态在 key 缺失期间保留

当 `state.IsDeleted=false` 且 `state.DesiredEnabled=false` 时，即使注册表 key 暂时缺失，也保留该 state。这是为执行 "delete key → wait → recreate key" 流程的第三方应用准备的。

`ShouldPreserveExplicitDisabledState` 返回 `true` 时，`BuildSnapshotAsync` 的 missing-state pruning 跳过该项，不重置 `ConsecutiveMissingSnapshots`。普通中性 baseline state 可继续使用现有 missing-state pruning 行为。

不因注册表 key 缺失就重置 `DesiredEnabled=false`。

### 10.12 竞争处理

- key 在 snapshot 枚举和禁用写入之间消失：reconciliation 写入失败，记录日志，后续轮询自然重试；
- 第三方程序在 reconciliation 后立即重建 key：下一轮 polling 会再次 reconcile；
- reconciliation 改变多个 linked ShellExtension 投影：`UpdateLinkedShellExtensionObservedEnabled` 统一更新同 CLSID 的所有 state；
- 交互式 session baseline 重建与正常 poll 并发：monitor 串行处理；
- ContextMenuMgr 自己的纠正写入在下一 snapshot 可见：monitor 从 post-reconciliation snapshot 更新 knownItems；
- `SuppressNextDetection` 不永久隐藏真正的后续重建：monitor 在 baseline 建立时消费该标志；
- 同一逻辑项只有一个 quarantine/reconciliation 操作并发运行：使用现有 `_quarantineInProgress` 机制。

## 11. 常见坑

| 坑 | 正确处理 |
| --- | --- |
| 只看 `DisplayName` 判断同一项 | 使用 `Id`、`KeyName`、`RegistryPath`、`HandlerClsid` 等稳定信息。 |
| 把 `HKCR` 当真实写入路径 | 写入时明确选择 `HKEY_USERS\<SID>\Software\Classes` 或 `HKLM\SOFTWARE\Classes`。 |
| 混用用户级和机器级 Classes | 用户级操作必须带前端用户 SID，机器级操作由服务高权限执行。 |
| 用同一种开关策略处理 `ShellVerb` 和 `ShellExtension` | 按 `EntryKind` 和路径分流。 |
| 把 Registry Write Protection 当普通禁用 | Registry Write Protection 是权限保护功能，不是菜单项状态。 |
| 假设状态库和注册表永远一致 | 外部安装器、系统更新和手工修改都会造成短暂不一致。 |
