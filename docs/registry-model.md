# 注册表模型与传统右键菜单实现

## 本文目的

本文解释传统右键菜单如何在 ContextMenuMgr 中被扫描、建模、禁用、删除、恢复、审核和备份。这里的“传统右键菜单”主要指注册在 `shell` 和 `shellex\ContextMenuHandlers` 体系下的菜单项，不包含 Windows 11 packaged context menu，也不包含 ShellNew / SendTo / WinX 等 SpecialMenu。

相关主实现位于 `ContextMenuMgr.Backend/Services/ContextMenuRegistryCatalog.cs`、`RegistryBackupService.cs`，前后端传输模型位于 `ContextMenuMgr.Contracts/ContextMenuEntry.cs`。

## Windows 传统右键菜单基础

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

## 项目中的 MonitoredRoots

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

## ContextMenuEntry 模型

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
| `IsPendingApproval` | 运行时新增项隔离成功后进入待审核；普通 `Modified` 只显示变化提示。 |
| `HasBackup` / `DeletedAtUtc` | 删除备份相关状态。 |
| `CanToggle` | 是否具有经过验证的普通启用/禁用操作。`PropertySheetHandlers` 等未验证类型为只读。 |
| `HasConsistencyIssue` / `ConsistencyIssue` | 真实注册表结构冲突等诊断；普通开关差异属于 Modified 或纠偏。 |
| `HasLegacyGlobalShellExtensionBlock` | 兼容性诊断：经典 handler 的 CLSID 同时存在于旧版机器级全局 Blocked 列表；不改变当前注册项的开关状态。 |
| `DetectedChangeKind` / `DetectedChangeDetails` | 新增/修改分类；确认缺失从活动 baseline 移除，不展示 Removed。 |
| `IsWindows11ContextMenu` | 标识 Win11 独立目录项，含 PackagedCom 和 SystemCommandStore。传统菜单通常为 `false`。 |
| `Notes` | 诊断和补充说明，不应参与主逻辑判断。 |

## Snapshot 构建流程

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

待审核 `ApplyDecision` 在持久状态操作门内只取得一次完整 snapshot；Allow/Deny 把该 snapshot 传给开关核心逻辑，Remove 把已解析的物理条目传给删除核心逻辑。普通 classic 开关使用受限 classic projection；删除保留自己的查询路径。这样不会在一次审核操作中重复做完整注册表与 Win11 包枚举，也不会绕过原有状态库串行化。

## 启用 / 禁用策略

传统菜单的禁用方式按 `EntryKind` 和实际路径分流：

| 类型 | 当前实现倾向 |
| --- | --- |
| `shell` verb | 普通开关把“Explorer 右键菜单可见性”作为独立控制域。ContextMenuMgrPlus 只引入文档化的 `ProgrammaticAccessOnly`；该值隐藏右键菜单，但显式指定 verb 的 `ShellExecuteEx` 仍可调用命令。`LegacyDisable`、`HideBasedOnVelocityId` 仍参与只读可见性判断，但不会被普通开关盲删。`CommandFlags` 是 `EXPCMDFLAGS`，不是启用/禁用位，普通开关永不改写。 |
| 级联 `shell` verb 子项 | 顶层快照只标记 `CanManageSubMenuItems`，不递归展开。按需读取 `SubCommands`、`ExtendedSubCommandsKey` 或父级 `shell`。`SubCommands` 禁用仅从该父级列表移除引用并保存恢复顺序，绝不全局修改共享 CommandStore 命令；其余物理 ShellVerb 子项复用可见性模型。 |
| `shellex` handler | 可能在 `ContextMenuHandlers` 与 disabled mirror 路径之间移动。 |
| disabled mirror path | 用 `-ContextMenuHandlers` 识别被移出的 handler。 |
| Windows 自带标记 | `Extended`、`NoWorkingDirectory`、`NeverDefault` 等属于属性，不等于禁用状态。 |

不要承诺所有菜单项都能用同一种方式开关。某些项由第三方安装器、系统策略或 COM handler 自身逻辑控制，项目只能 best-effort 地修改注册表状态并记录结果。

ShellVerb 开关使用物理路径级事务。写入前先捕获本次会改变的 value、类型、精确数据和不含可见性值的物理注册代际指纹；写入后关闭 handle，重新打开同一物理 key 做 read-back，再执行 logical / physical reconciliation，最后才提交状态库。File Types / scene 的 ProgID root 不一定属于常规 `MonitoredRoots`，因此常规 snapshot 未返回该项时，仍以已验证的目标物理候选作为结果，保留 #111 行为。

同一个稳定 Id 可以同时存在于 `HKLM\Software\Classes` 与前端用户的 `HKEY_USERS\<SID>\Software\Classes`。用户级同名注册通常遮蔽机器级注册；单项操作只改变该卡片所代表的 `BackendRegistryPath`，验证也以该目标路径为准，不要求被遮蔽副本具有相同可见性。另一方面，ProgID、`SystemFileAssociations\.ext` 和其它 association-array source 是不同来源，其路径/Id 不会仅因 verb 名或可执行文件相同而合并；批量页可以发现相关项，但每个 mutation 仍是显式、逐物理来源执行。

首次受管禁用会把 `ProgrammaticAccessOnly` 的原始存在性、`RegistryValueKind` 和精确值写入 `PersistedContextMenuState.ShellVerbVisibilityProvenance`，并以物理路径为键。重复禁用不会覆盖原始 provenance。启用只恢复 ContextMenuMgrPlus 自己保存的值；缺少 provenance 时不会删除第三方 `ProgrammaticAccessOnly` / `LegacyDisable` / `HideBasedOnVelocityId`。如果命令或 handler 代发生变化，旧 provenance 不会应用到新注册。

v1.7.5 在禁用 classic ShellVerb 时直接写 `HideBasedOnVelocityId=0x639bc8` (DWORD)、空字符串 `ProgrammaticAccessOnly`，普通 verb 还写空字符串 `LegacyDisable`；`Folder\shell\opennewwindow` 不写 `LegacyDisable`，有 `ShowAsDisabledIfHidden` 时只保留 velocity hide marker。该版本没有保存原始 visibility provenance。升级后，只有状态库在本次 snapshot 之前已记录同一逻辑项的明确禁用意图、其物理 `BackendRegistryPath` 完全一致，且真实 key 的值与相应 v1.7.5 签名及类型严格匹配时，Allow/Enable 才能使用一次性 legacy recovery 删除这些确定的旧 marker。恢复操作执行物理代际比较、重新打开验证、逻辑核对、状态提交及有条件回滚；它不伪造未知原始值的 v1.7.6+ provenance。签名不完整、来源不明或第三方 visibility metadata 均继续返回 `SHELL_VERB_VISIBILITY_PROVENANCE_MISSING`，不会被自动删除。后续再次禁用由当前事务建立正常 provenance。

维护决定：保留v1.7.5 可见性签名恢复，直至明确停止支持所有仍带该签名、且没有物理 provenance 的持久状态升级；不能仅因项目已经生成新版 provenance 就移除它。迁移成功的单项自然转入当前事务格式，不引入永久兼容开关。

事务在物理验证、logical reconciliation、状态保存、取消或异常失败时执行乐观回滚：仅当当前值仍等于本事务写入值时才恢复原值，并重新打开 key 验证。若第三方已把值改成其它内容，则保留第三方内容并返回 `REGISTRY_MUTATION_ROLLBACK_CONFLICT`；安全回滚成功返回 `REGISTRY_MUTATION_ROLLED_BACK`。多物理 classic Shell Extension move 同样记录每一步，后续 move 或状态保存失败时逆序恢复，继续保留 #108 的 active / `-ContextMenuHandlers` 冲突保护。

File category 的 `open` 是 activation-critical safety class。若父 `shell` 默认值选中它，或前端用户的有效扩展名关联使用该 ProgID，则普通禁用、通用命令编辑和通用属性编辑在写入前返回 `FILE_TYPE_ACTIVATION_VERB_PROTECTED`。本保护不会修改 `UserChoice` 或默认应用，也不 blanket-ban `print` / `printto`；例如隐藏 `print` 只改变目标 print verb 的受管可见性 value，不改 sibling `open`、两个 command、父 `shell` 默认值或其它 association source。

Classic ShellVerb / ShellExtension、`SystemFileAssociations`、Win11 packaged COM 和 Win11 SystemCommandStore 在诊断中使用不同 source kind。Classic mutation 不写 Win11 CLSID blocked list，Win11 packaged handler mutation 也不按厂商名联动 classic ProgID。

Recycle Bin 页面额外投影一个虚拟传统项 `special:recyclebin:pintohome`，用于控制系统的“Pin to Quick access” verb。它的真实注册表位置是 `HKCR\Folder\shell\pintohome`，但只在 Recycle Bin 分类显示。启用状态不使用普通 shell verb 隐藏值，而是检查 `AppliesTo` 是否包含 `System.ParsingName:<>"::{645FF040-5081-101B-9F08-00AA002F954E}"`；禁用时只追加这个 Recycle Bin 排除条件，启用时只移除这个排除条件并保留其它 `AppliesTo` 子句。如果 `pintohome` key 不存在，快照不显示该虚拟项。

普通 ShellVerb 的命令文本编辑不解析命令行、不拆分程序和参数、也不重写引号；`SetCommandText` 只把用户输入的字符串原样写到 `<verb>\command` 的默认 `REG_SZ`。后端会先检查 `CanEditCommandText` 和当前注册表形态，并经过 Registry Write Protection preflight；不支持 Shell Extension、Windows 11 packaged context menu、`SubCommands` / `ExtendedSubCommandsKey` 父级、`DelegateExecute`、`DropTarget\CLSID` 或 `ExplorerCommandHandler` 项。

### 受保护机器 ShellVerb 的窄写入路径

普通 ShellVerb 可见性先尝试正常打开/写入，并重新读取同 key 的 `ShellVerbVisibility.IsEnabled`。仅真实 `HKLM\SOFTWARE\Classes\...\shell\<verb>` 的普通写因 ACL 拒绝时，才可进入窄 `ProtectedRegistryMutation` fallback；不适用于 HKCR、HKU、非 Classes 或无关 Classes path。Registry Write Protection preflight 仍优先阻止操作。

fallback 捕获原 Owner/Group/DACL，在服务 token 的私有可丢弃副本仅启用 `SeTakeOwnershipPrivilege` / `SeRestorePrivilege`，临时授 LocalSystem SetValue，写入/验证后精确恢复并逐字节验证原 descriptor（含继承与 ACE 顺序）。`ScopedRegistryPrivileges` 在无模拟身份下用 Query/Duplicate 打开主 token，同步挂载私有 impersonation token 并在副本上调整权限，不修改进程 token；退出恢复调用前身份，部分获取失败或并发 scope 不留下共享启用权限。只将已捕获的 Owner/Group/Access 标记待写，不把未捕获 SACL 标记写入。

不永久接管、不授 Administrators/SYSTEM FullControl、不删/替换系统 verb。第一次 owner update 即使抛错也启动清理；临时改动/恢复前比较原/预期 descriptor。外部 ACL 改动保留并返回 `REGISTRY_SECURITY_RESTORE_FAILED`，即使后续值回滚成功也不能掩盖该错误。比较/写入是乐观 Windows 操作，非跨安装器原子 CAS，仍可能在间隙竞态；不确定恢复必须失败。私有 scope 不改变认证 SID、选定 hive 或 Session。

传统分类页支持通过前端的 `CreateSceneMenuItem` 入口创建自定义 classic 菜单项。分类页把当前 `ContextMenuCategory` 映射为 `ContextMenuSceneKind.CustomRegistryPath` 和对应的 HKCR scene root（例如 `HKCR\*\shell`、`HKCR\Directory\shell`、`HKCR\Drive\shell`），再通过 Backend Pipe 交给 `FileTypeSceneMenuService.CreateSceneMenuItemAsync` 写入 `shell\<verb>\command`。首版只创建普通 shell verb 命令：前端负责生成带引号的命令行并追加 `%1` 或 `%V` 选中对象占位符，后端负责 Registry Write Protection preflight、创建不覆盖已有项的唯一 key、通知 Shell 关联变更，并在状态库中抑制本次新建项检测，避免把用户主动创建的项标为待审核。本入口不创建 ShellEx handler、不复制 CommandStore 引用，也不创建子菜单。

文件类型页的 Custom Extension 场景不是只扫描扩展名本身。后端会用前端用户上下文解析该扩展名直接关联的 class roots，并只枚举实际存在 `shell`、`shellex\ContextMenuHandlers` 或 `shellex\-ContextMenuHandlers` 的候选 root。候选来源包括 `SystemFileAssociations.<ext>`、直接 `.<ext>` key、用户与机器级扩展名默认 ProgID、用户与机器级 `OpenWithProgids`、以及当前用户 `FileExts\<ext>\UserChoice\ProgId` / `FileExts\<ext>\OpenWithProgids`。用户级读取必须走 `HKEY_USERS\<sid>`，不能用服务进程的 `HKCU` 或 `HKCR` 合并视图推断当前用户。

例如 PowerShell 7 可把 `.ps1` 的 “Run with PowerShell 7” 注册到 `HKLM\SOFTWARE\Classes\Microsoft.PowerShellScript.1\Shell\PowerShell7x64`。Custom Extension `.ps1` 页面应通过 `.ps1` 的关联 ProgID 扫描到 `Microsoft.PowerShellScript.1\shell`，并把条目的真实来源保持为 `Microsoft.PowerShellScript.1\shell\PowerShell7x64`，这样禁用、恢复、删除和编辑仍写回实际 ProgID 路径，而不是误写到 `.ps1\shell`。

File Types 的隐藏批量管理视图通过 `FindRelatedFileTypeMenuItems` 做一次性相关项扫描。该扫描覆盖 HKLM 与前端用户 `Software\Classes` 下的扩展名 key、ProgId key、以及 `SystemFileAssociations\<extension|perceivedType>` 的 `shell` / `shellex\ContextMenuHandlers` / disabled mirror 根；它不会在服务启动时运行，也不会把扫描到的文件类型项加入普通全局启动检测 baseline。ShellVerb 相关性要求命令程序路径规范化后相同并且 key name 相同；ShellExtension 相关性要求 Handler CLSID 相同；显示名只用于展示，不作为匹配依据。批量扫描会把匹配 query 的已删除备份状态合并回结果，因此删除后的相关项仍可在批量页撤销。批量视图中的开关和删除继续走普通 `SetEnabled` / `DeleteItem`，并通过 fallback `ContextMenuEntry` 支持不在普通 workspace snapshot 中的 scene-only 文件类型项。文件类型 `open` / `edit` 核心 ShellVerb 禁止删除；用户若想隐藏这类项，应使用禁用开关。

## 删除、恢复与备份

删除前 `RegistryBackupService` 用 reg.exe export 保存真实目标 .reg；备份位置与 Portable host-scoped 目录/foreign-host 拒绝统一见[运行时数据](runtime-data-and-logging.md#portable-host-identity)。不能导入另一用户/Windows 的备份。

| 操作 | 说明 |
| --- | --- |
| 删除 | 尝试备份后删除真实注册表项，并在 `ContextMenuStateStore` 中记录删除状态。 |
| 恢复 | 优先通过备份导入恢复，再更新状态库。 |
| Undo delete | 面向用户的恢复入口，依赖备份存在。 |
| Purge backup | 清理已保留的删除备份，之后恢复能力会下降。 |

保留备份是为了降低误删风险，也让审核中的 Remove 操作可回滚。备份本身不是注册表真实状态，导入失败或目标权限变化仍可能导致恢复失败。

经典注册项删除先执行 Registry Write Protection preflight，再导出真实 `BackendRegistryPath`。共享的树删除实现先只申请 `ReadKey | Delete`，检查整个子树，随后从叶到根使用 `DeleteSubKey` 删除并逐键重新打开验证；不申请 `SetValue`、不预先清空 value、不接管所有权或改 ACL。不要换回 `DeleteSubKeyTree(..., false)`：它内部请求 value-write 权限，打开被拒绝时可能把现存 key 当作缺失而静默跳过。允许 DELETE 但拒绝 SetValue 的注册项仍应能删除；确实拒绝 DELETE 时应在删除任何子键前失败。外部并发修改仍可能导致执行阶段失败，`.reg` 备份不包含 ACL，不能据此宣称安全描述符已恢复。

`ClassicDeleteStarted` 记录物理路径与备份路径；失败日志包含完整异常和回滚结果，失败响应保留 mutation 错误码。只有物理缺失回读及状态库保存成功才报告已删除。手动检查：在禁用目录删除 Shell Extension，刷新确认物理键缺失、卡片可撤销、重启后恢复记录仍存在，再撤销确认注册内容恢复；写保护开启时应拒绝且不导出新备份。

恢复用户级或 HKCR overlay 相关菜单项时，后端必须带 frontend user context 重读快照，不能用服务进程的 HKCU 或无用户上下文的全局快照判断恢复结果。

## 传统 ShellExtension 物理状态

传统 Shell Extension 的普通开关以注册项为单位：启用位置是 `<root>\shellex\ContextMenuHandlers\<name>`，禁用位置是 `<root>\shellex\-ContextMenuHandlers\<name>`。两者使用同一个稳定 Id。

File Types / scene（包括 `SystemFileAssociations`）根不必属于常规 `MonitoredRoots`。当这类 Shell Extension 由 `SetEnabled` 操作时，后端会按稳定 Id 在 active 与 disabled mirror 容器、HKLM 与当前前端用户 `HKEY_USERS\<sid>` Classes 中重新解析物理注册，再进行移动和验证；常规 snapshot 未包含该项时，返回已验证的物理项而不是把 scene snapshot miss 当作“未找到”。前端 payload 仅提供请求身份与 Handler CLSID，后端必须重开物理 key 并确认 CLSID 一致。

若同一稳定 Id 的 active/disabled 物理键同时存在，持久快照必须读取两侧 key 的注册表最后写入时间，以较新一侧作为当前事实并自动删除较旧一侧；不能把双键本身显示成“当前注册表状态与软件记录不一致”。若最后写入时间相同或无法完整读取，优先采用状态库 `DesiredEnabled` 对应的一侧；没有状态记录时采用 active。自动删除失败时记录 `ClassicShellExtensionDuplicateAutoRepairFailed` 并在后续快照重试，但前端仍按选中的较新一侧计算实际开关状态。归一化后的状态若与既有 baseline 不同，继续按运行时/离线六条规则进入静默纠偏或 `Modified`，不能伪造为无变化。

同一稳定 Id 在 HKLM 与 `HKEY_USERS\<SID>` 存在多个物理副本时，普通开关必须把所有副本一起移动并验证。经典普通开关不得写入全局 `Shell Extensions\Blocked`；该列表由“其他规则 / GUID 阻止”单独管理。

`PropertySheetHandlers` 仍是只读注册类型，`CanToggle=false`，不参与自动隔离或开关 reconciliation。

## Registry Write Protection

Registry Write Protection 的设置值不是“用户最后一次点击的值”，而是 ACL 已完成收敛的提交标记。后端从 `MonitoredRoots` 的 distinct `StableRelativePath` 派生 HKLM 与 frontend `HKEY_USERS\<SID>` target，先读取每个现存 root 的 DACL，再修改并 fresh read-back。只有全部 applicable target 验证成功才写 `backend-protection-settings.json`。

- 禁用部分失败时保留 persisted true，已成功移除的 ACL 不回滚；重试继续收敛剩余 root。
- 启用部分失败或启用后的设置保存失败时，只恢复本次操作前观察到的保护语义；不得移除原有保护规则。
- 禁用后的设置保存失败不重新加锁。
- 缺少 frontend SID 或 user hive 未加载时，在任何 HKLM/HKU mutation 前失败。
- Windows 可能合并等价 DENY ACE；验证按 SID、rights、inheritance/propagation 语义而不是 ACE 数量判断。
- 禁用只修改 Builtin Users / Authenticated Users 的显式目标 DENY rights `CreateSubKey | SetValue`，保留其它 rights、无关 ACE 和 inherited ACE。
- Registry Write Protection transition 使用独立 gate，不与状态库 gate 耦合。

## 受限快照与结果核对

普通 classic `SetEnabled` 用 monitored roots 与持久 classic state 解析/验证，写前/写后 projection 不枚举 Win11 package 或 CommandStore。写前仍归一 active/disabled 双容器并更新已有 classic state，但不得建立局部首次 baseline。Win11 Id 继续分流 Win11 catalog，scene-only 项按 `FindEntriesByIdAsync` 的物理校验处理。结果查询保留原 HKLM/前端 HKU source，在持久 gate 内合并状态，不以另一 hive 替代缺失来源。

原物理 classic 来源确认缺失时，状态查询返回 `MENU_ITEM_REMOVED` 和明确 Id；认证身份不匹配、访问拒绝、发现不完整及已有软删除恢复记录均不据此移除 UI。缓存项的文本/命令/属性/删除请求也先核实原来源，不能从前端缓存重新创建目标。协议字段与专用 SpecialMenu 查询见[IPC](process-and-privilege-flows.md#列表发现覆盖与已移除结果)。

## 回收站 Pin to Start 合成项

`special:recyclebin:pintostart` 仅在有前端 SID、且机器 `HKLM\SOFTWARE\Classes\Folder\shellex\ContextMenuHandlers\PintoStartScreen` 默认值有合法 CLSID 时投影。它是继承 Shell Extension，不是 pintohome 的 AppliesTo filter。

禁用在 `HKU\<SID>\Software\Classes\Folder\shellex\ContextMenuHandlers\PintoStartScreen` 建无默认值 shadow，同时在对应 Directory handler 注册机器 CLSID，保留普通目录 Pin to Start。两用户键作为一个逻辑事务：捕获写前状态，两个写后均核对 managed generation，再保存独立 per-SID provenance。启用只恢复该 SID 原两键，不影响其它用户；现存用户键不认领/覆盖，reset 后也不能认领遗留 shadow。

写前/后与 state commit 失败仅回滚仍匹配本次写入的键，外部改动报 conflict。实现键/合成 Id 不进入普通监控审核身份。若进程在两写间硬崩溃而无最终归属，下次保留遗留键并在切换时报 override conflict；检查两 HKU 键与状态证据，只人工移除能确认由中断操作建立的键，不推测归属。其它继承 Folder 而非 Directory 的虚拟 namespace 仍需 Explorer 验证。

## 增强菜单与详细规则

### 静态增强菜单字典

`EnhanceMenusDic.xml` 中的内置增强菜单是静态 shell 命令字典。ContextMenuMgr 只负责把这些字典项安装、禁用或移除到当前前端用户的 `Software\Classes` 下；菜单项被启用后，运行时执行必须完全由注册表中的命令、系统工具和生成的 `.vbs` / `.bat` / `.cmd` 文件完成。

增强菜单字典的源码只保留在 `ContextMenuMgr.Frontend/Resources/EnhanceMenusDic.xml`。前端构建时会把它复制到输出目录，前端读取该文件并把选中项的定义 XML 传给后端；后端不维护第二份增强菜单字典，也不在运行时重新读取项目内的字典副本。

增强菜单命令不得调用 `ContextMenuMgr` 前端、后端服务、TrayHost、Named Pipe、服务 RPC/IPC 或任何要求本程序仍在运行的宿主进程。迁移 BluePointLilac / ContextMenuManager 内置项时，应优先保持 `EnhanceMenusDic.xml` 的 `KeyName`、`SubKey`、`Command Default`、`ShellExecute`、`FileName`、`Arguments`、`PowerShellScript` 和 `CreateFile` 脚本内容语义一致；新增翻译节点只能影响显示文本，不能改变命令语义。

当 `Command` 没有 `Default` 时，后端按 BluePointLilac 的规则由 `FileName` + `Arguments` 生成注册表默认值；`FileName` 或 `Arguments` 为空且包含 `CreateFile` 时，会写入 `RuntimePaths.GeneratedProgramsDirectory` 下的持久脚本文件。`.bat` / `.cmd` 使用系统默认编码，其它生成文件（包括 `.ps1`）使用 Unicode。启用后的注册表命令必须直接指向系统工具或这些持久生成文件，而不是依赖后端服务继续运行。

`AllFilesystemObjects\shell\CopyAsPath` 直接使用注册表中的 PowerShell 命令，将 `%1` 放在单引号 here-string 的独立行中，再把完整路径写入剪贴板，不添加双引号。路径不能以 `-Command` 的位置参数传入，否则含空格或 PowerShell 特殊字符的路径会被重新解析。已启用的旧版菜单需在“其他规则”中关闭后重新启用，才能重写原有命令。

`ShellExecute` 只在确实需要 ShellExecute 专属行为时才保留旧的 `mshta vbscript:createobject("shell.application").shellexecute(...)` 包装，例如显式非空 `Directory` 或未知 ShellExecute 属性。空 `<ShellExecute/>`、`Verb="open"`，以及只有 `WindowStyle` 的增强菜单项会直接写成 `<FileName> <Arguments>`，避免多层 XML / C# / 注册表 / mshta / VBScript 转义导致简单命令失效。`Verb="runas"` / 管理员增强菜单项默认编译为独立的 `powershell.exe ... Start-Process -Verb RunAs -Wait -PassThru ...` 命令；复杂管理员动作可在字典中使用 `PowerShellScript`，由后端生成提升后的 PowerShell command block，并把提升子进程的 exit code 传回调用方。`PowerShellScript` 默认提升；仅当脚本必须留在调用它的 Explorer 用户上下文（例如停止后重启该用户的 Explorer）时，才可显式使用 `Elevate="false"`。缓存刷新会优先让 Winlogon 的 `AutoRestartShell` 自动恢复 Explorer，仅在该设置明确为 `0` 时才手动启动，避免双重启动和额外的文件管理器窗口。脚本中的环境变量保留到 Explorer 实际调用命令时才解析，不能在服务注册命令时展开为 SYSTEM profile 路径。启用后的命令仍然只依赖系统工具、注册表命令值和必要的持久生成脚本，不依赖 ContextMenuMgr 前端、后端、TrayHost 或 pipe。

增强菜单字典和后端写入层会把直接作为可执行文件使用的裸 `cmd` / `cmd.exe` 规范化为 `C:\Windows\System32\cmd.exe`，把裸 `explorer` / `explorer.exe` 规范化为 `C:\Windows\explorer.exe`；`Command Default` 只在命令开头是这些安全前缀时改写可执行文件部分。`Arguments` 和生成脚本中直接启动 Explorer 的低风险位置也应使用 `C:\Windows\explorer.exe`，但不要把 `taskkill /im explorer.exe`、`tskill explorer`、图标引用或进程名匹配参数误改成路径。写入普通 `MUIVerb` 等可见标签时会移除 Win32 菜单加速键 `&`，但保留 `&&` 表示的字面量 `&`，并且不处理 `@shell32.dll,-...` 这类资源引用。

动态扩展的详细编辑只接受 `DetailedEditDic.xml` 明确列出的厂商配置，不把任意 COM 子菜单变成可写项。字典条件/语言和命令编译统一由 `EnhanceMenuDictionary` 承担，catalog 保留注册表及状态事务。

## 调试、验证与维护

源码入口：[catalog](../ContextMenuMgr.Backend/Services/ContextMenuRegistryCatalog.cs)、[ShellVerbVisibility](../ContextMenuMgr.Backend/Services/ShellVerbVisibility.cs)、[ProtectedRegistryMutation](../ContextMenuMgr.Backend/Services/ProtectedRegistryMutation.cs)、[RegistryProtectionAcl](../ContextMenuMgr.Backend/Services/RegistryProtectionAcl.cs)、[备份](../ContextMenuMgr.Backend/Services/RegistryBackupService.cs)。状态机与并发持久化唯一说明见[状态与监控](state-and-monitoring.md)，IPC 超时/迟到通知见[进程与权限](process-and-privilege-flows.md)。

保护设置不是 ACL 事实的完整替代。检查 `RegistryProtectionTransitionStarted/Completed`、失败 target、persisted value 和实际 DACL；禁止手改 JSON false 绕过失败。受保护写入按 token acquisition、privilege enable、registry write、descriptor restoration 分阶段定位；error 5 不等于 privilege 缺失。详见[排错](troubleshooting.md#注册表权限与受保护写入)。

已有永久回归覆盖 ShellVerb 原值/代际、handler move、多来源、ACL transition、protected token、副作用/回滚、激活保护和 Pin to Start。真实 VM 仍需普通前端→LocalSystem Pipe、两个 SID、删除/Undo、Explorer 可见性、拒绝 DELETE/SetValue 的差异、外部并发改值/ACL、持久保存失败后物理回滚与重启；测试临时键成功不能证明某第三方受保护键已修复。

更改扫描根、visibility/provenance、删除、子菜单、ACL、字典或恢复策略时更新本文，并检查对应持久格式兼容；不得删除仍受支持的 v1.7.5 严格签名恢复。
