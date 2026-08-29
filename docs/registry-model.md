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
| `CanToggle` | 是否具有经过验证的普通启用/禁用操作。`PropertySheetHandlers` 等未验证类型为只读。 |
| `HasConsistencyIssue` / `ConsistencyIssue` | 状态库和真实注册表不一致时的诊断信息。 |
| `HasLegacyGlobalShellExtensionBlock` | 兼容性诊断：经典 handler 的 CLSID 同时存在于旧版机器级全局 Blocked 列表；不改变当前注册项的开关状态。 |
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

ShellVerb 开关有两层验证。首先在刚写入的物理 key 上用 `ShellVerbVisibility.IsEnabled` read-back 验证；随后按稳定 `Id` 重读同一 Classes source root 的所有物理候选。File Types / scene 的 ProgID root 不一定属于常规 `MonitoredRoots`，因此常规 snapshot 未返回该项时，会以这个已验证的物理候选作为操作结果，而不会把“常规目录未扫描此 ProgID”误报为注册表写入失败。反之，目标物理 key 缺失，或任一同 Id 物理候选仍不符合请求状态，操作仍失败；不会以缺失 logical snapshot 伪造成功。

Recycle Bin 页面额外投影一个虚拟传统项 `special:recyclebin:pintohome`，用于控制系统的“Pin to Quick access” verb。它的真实注册表位置是 `HKCR\Folder\shell\pintohome`，但只在 Recycle Bin 分类显示。启用状态不使用普通 shell verb 隐藏值，而是检查 `AppliesTo` 是否包含 `System.ParsingName:<>"::{645FF040-5081-101B-9F08-00AA002F954E}"`；禁用时只追加这个 Recycle Bin 排除条件，启用时只移除这个排除条件并保留其它 `AppliesTo` 子句。如果 `pintohome` key 不存在，快照不显示该虚拟项。

普通 ShellVerb 的命令文本编辑不解析命令行、不拆分程序和参数、也不重写引号；`SetCommandText` 只把用户输入的字符串原样写到 `<verb>\command` 的默认 `REG_SZ`。后端会先检查 `CanEditCommandText` 和当前注册表形态，并经过 Registry Write Protection preflight；不支持 Shell Extension、Windows 11 packaged context menu、`SubCommands` / `ExtendedSubCommandsKey` 父级、`DelegateExecute`、`DropTarget\CLSID` 或 `ExplorerCommandHandler` 项。

### Protected machine ShellVerb mutations

Ordinary ShellVerb visibility changes first open the entry for a normal write and then read the same key back through `ShellVerbVisibility.IsEnabled`. If Windows denies that normal operation on an actual `HKLM\SOFTWARE\Classes\...` entry, the backend may use a narrowly scoped protected-mutation fallback. It never runs for `HKEY_USERS\<SID>\Software\Classes` entries, which retain their frontend-user provenance.

The fallback captures the original owner/group/DACL descriptor, temporarily enables only `SeTakeOwnershipPrivilege` and `SeRestorePrivilege`, grants LocalSystem only `SetValue`, performs and verifies the requested visibility write, then restores and byte-verifies the exact original owner/DACL descriptor (including inheritance and ACE ordering). It does not permanently take ownership, grant Administrators or SYSTEM FullControl, or delete/replace a Windows ShellVerb key. A restore failure is reported as a failed operation and logged prominently.

This Windows ACL path is distinct from ContextMenuMgr Registry Write Protection. The latter is checked before all normal menu mutations; when it is enabled, the protected-key fallback is not attempted.

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

## 8. 状态库与显式 baseline

`ContextMenuStateStore` 保存项目自己的状态，路径为 `RuntimePaths.StateDatabasePath`。它不是注册表副本，而是“上一次已确认状态 + 用户开关意图 + 待审核状态 + 删除恢复记录”。

状态库保存继续采用 current + last-known-good backup：先写入同目录唯一临时文件，flush 并用生产解析器验证，然后原子替换 current，并把已验证的旧 current 保存为 `.bak`。损坏恢复、portable host identity 隔离规则保持不变。

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

## 9. 六条外部变化核心规则

下表是拦截、状态库和 UI 提示的唯一行为矩阵。后续改动不得引入与此表冲突的 Reappeared 或泛化 consistency 分支。

| 编号 | 场景 | 后端行为 | 前端状态 |
| --- | --- | --- | --- |
| 1 | 软件运行期间出现未知菜单项 | 立即禁用并写入状态库，进入待审核 | `Added` + `IsPendingApproval=true` |
| 2 | 软件运行期间，已知项从开变关 | 不自动重新打开，保留旧 baseline 供用户确认 | `Modified`，不再叠加“状态不一致” |
| 3 | 软件运行期间，相邻稳定快照观察到已知项从关变开 | 静默重新关闭，继续保留 `DesiredEnabled=false` | 纠偏后无待审核、无外部修改、无 generic consistency |
| 4 | 软件停止后出现未知菜单项，重启时发现 | 不隔离、不自动禁用，等待用户确认当前变化 | `Added`，不进入待审核 |
| 5 | 软件停止后已知项开关改变，重启时发现 | 无论开变关还是关变开都保留当前注册表状态，等待用户确认 | `Modified`，不待审核 |
| 6 | 实际注册表项已删除 | 完整快照连续确认缺失后，从活动 baseline 移除 | 不显示 Removed、不参与以后比对 |

“连续确认缺失”当前为两次具有交互用户上下文的持久快照，用于避开注册表枚举瞬态。上下文不完整的服务快照不得清理状态。

应用主动执行 Delete 时产生的 `.reg` 备份属于恢复记录，不属于活动监控 baseline。它可以暂时保留以支持 Undo Delete，但不参与新增、修改或重新出现分类。若相同注册表 key 再次真实出现，旧恢复记录和旧备份会被清理，该 key 按普通未知 `Added` 处理，不再使用 `Reappeared` 状态机。

## 10. 实现时序

### 10.1 状态库缺失或被重置

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

### 10.2 运行时轮询

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

### 10.3 启动/离线比对

监控启动时首先取得原始实际快照并建立内存 `knownItems`，不得先执行 disabled-state reconciliation。已有 baseline 下的未知项保留 `Added` 标记，但启动阶段不会触发 `ItemDetected`，因此不会隔离或进入待审核。元数据和开关状态无论往哪个方向变化都按 `Modified` 显示。只有后续两个相邻稳定运行时快照观察到关变开，才进入规则 3 的静默纠偏。

交互式 session 登录、解锁或连接时，monitor 仍会重建内存 baseline。若可见项数量明显低于持久活动状态数量，则延后重建，避免用户 hive 尚未加载时制造运行时新增通知。

### 10.4 一致性状态

开关差异必须落入规则 2、3 或 5，因此 `GetConsistencyIssue` 不再为 `DesiredEnabled != actual` 生成 generic consistency。用户确认 `Modified` 后，`AcknowledgeItemState` 用当前实际值更新 `DesiredEnabled`、`ObservedEnabled` 和元数据。

generic consistency 只保留无法自动归一化的真实注册表结构冲突，以及经典 handler 同时命中旧版全局 Blocked 等诊断。它不能用于表达普通开关变化；active/disabled 双键并存已属于自动修复流程，不能再显示这条泛化提示。

### 10.5 传统 Shell Extension 物理状态

传统 Shell Extension 的普通开关以注册项为单位：启用位置是 `<root>\shellex\ContextMenuHandlers\<name>`，禁用位置是 `<root>\shellex\-ContextMenuHandlers\<name>`。两者使用同一个稳定 Id。

File Types / scene（包括 `SystemFileAssociations`）根不必属于常规 `MonitoredRoots`。当这类 Shell Extension 由 `SetEnabled` 操作时，后端会按稳定 Id 在 active 与 disabled mirror 容器、HKLM 与当前前端用户 `HKEY_USERS\<sid>` Classes 中重新解析物理注册，再进行移动和验证；常规 snapshot 未包含该项时，返回已验证的物理项而不是把 scene snapshot miss 当作“未找到”。前端 payload 仅提供请求身份与 Handler CLSID，后端必须重开物理 key 并确认 CLSID 一致。

若同一稳定 Id 的 active/disabled 物理键同时存在，持久快照必须读取两侧 key 的注册表最后写入时间，以较新一侧作为当前事实并自动删除较旧一侧；不能把双键本身显示成“当前注册表状态与软件记录不一致”。若最后写入时间相同或无法完整读取，优先采用状态库 `DesiredEnabled` 对应的一侧；没有状态记录时采用 active。自动删除失败时记录 `ClassicShellExtensionDuplicateAutoRepairFailed` 并在后续快照重试，但前端仍按选中的较新一侧计算实际开关状态。归一化后的状态若与既有 baseline 不同，继续按运行时/离线六条规则进入静默纠偏或 `Modified`，不能伪造为无变化。

同一稳定 Id 在 HKLM 与 `HKEY_USERS\<SID>` 存在多个物理副本时，普通开关必须把所有副本一起移动并验证。经典普通开关不得写入全局 `Shell Extensions\Blocked`；该列表由“其他规则 / GUID 阻止”单独管理。

`PropertySheetHandlers` 仍是只读注册类型，`CanToggle=false`，不参与自动隔离或开关 reconciliation。

### 10.6 竞争与失败

- 所有会读写 `ContextMenuStateStore` 的常规快照、WPS 快照、审核、删除/恢复和 reconciliation 必须通过 catalog 的持久状态操作门串行化。
- reconciliation 写入失败时保留 `DesiredEnabled=false`，记录结构化日志并在后续快照重试；不得伪造 `ObservedEnabled=false`，也不得转成待审核。
- ContextMenuMgr 自己的写入必须从 post-write 快照更新 baseline，不能被下一轮识别为外部变化。
- `SuppressNextDetection` 只能抑制一次由应用自身恢复/创建导致的检测，建立 monitor baseline 时必须消费。
- WPS/Office 是否已有 baseline 只看 WPS marker 或旧版 WPS state，不能被常规菜单 state 影响。

## 11. 常见坑

| 坑 | 正确处理 |
| --- | --- |
| 只看 `DisplayName` 判断同一项 | 使用 `Id`、`KeyName`、`RegistryPath`、`HandlerClsid` 等稳定信息。 |
| 把 `HKCR` 当真实写入路径 | 写入时明确选择 `HKEY_USERS\<SID>\Software\Classes` 或 `HKLM\SOFTWARE\Classes`。 |
| 混用用户级和机器级 Classes | 用户级操作必须带前端用户 SID，机器级操作由服务高权限执行。 |
| 用同一种开关策略处理 `ShellVerb` 和 `ShellExtension` | 按 `EntryKind` 和路径分流。 |
| 把 Registry Write Protection 当普通禁用 | Registry Write Protection 是权限保护功能，不是菜单项状态。 |
| 假设状态库和注册表永远一致 | 外部安装器、系统更新和手工修改都会造成短暂不一致。 |
