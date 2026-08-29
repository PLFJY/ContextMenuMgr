# ContextMenuMgr 开发者指南

相关文档：

- [AI 与维护者接手 Playbook](./ai-maintainer-playbook.md)
- [进程、权限与用户上下文链路](./process-and-privilege-flows.md)
- [注册表模型与传统右键菜单实现](./registry-model.md)
- [SpecialMenu 实现说明](./special-menus.md)
- [Windows 11 新右键菜单实现说明](./windows11-context-menu.md)
- [Deep Analysis 与 ProbeHost 实现说明](./deep-analysis-probehost.md)
- [构建与发布说明](./build-and-release.md)
- [排错指南](./troubleshooting.md)

## 1. 项目目标

ContextMenuMgr 是一个以“审核优先、用户控制”为核心的 Windows 右键菜单管理工具。它不是简单开关器：当前实现不仅枚举和启用/禁用传统右键菜单，还会监控新增项、自动隔离待审核项、维护删除备份、处理 Windows 11 packaged context menu、管理 ShellNew / SendTo / WinX 等特殊入口，并提供隔离进程中的 Deep Analysis。

开发时要把“菜单项展示状态”、“后端状态库”、“注册表真实状态”、“用户上下文”和“桌面 Session”分开考虑。很多功能看似都是开关，但底层路径完全不同。

## 2. 项目进程模型

| 项目 | 路径 | 输出 exe | 职责 | 不应该做的事情 |
| --- | --- | --- | --- | --- |
| Frontend | `ContextMenuMgr.Frontend` | `ContextMenuManagerPlus.exe` | WPF UI、导航、搜索、设置、通过 named pipe 调用后端、启动 UAC bootstrapper、启动 ProbeHost | 不直接做高权限注册表修改；不直接加载第三方 Shell Extension DLL。 |
| Backend Service | `ContextMenuMgr.Backend` | `ContextMenuManagerPlus.Service.exe` | LocalSystem 服务、pipe server、注册表目录、监控、审核、SpecialMenu、Win11、AutoStart、Explorer restart、拉起 TrayHost/Frontend | 不在服务 Session 直接显示 UI；不把 SYSTEM 的 `HKCU` 当成用户 hive。 |
| TrayHost | `ContextMenuMgr.TrayHost` | `ContextMenuManagerPlus.TrayHost.exe` | 每用户托盘进程、后台通知、从通知打开前端或审核页 | 不承担注册表写入和菜单管理核心逻辑。 |
| ProbeHost | `ContextMenuMgr.ProbeHost` | `ContextMenuMgr.ProbeHost.exe` | Deep Analysis 的隔离 COM 探测进程，多架构发布 | 不提权、不写注册表、不执行菜单命令。 |
| Contracts | `ContextMenuMgr.Contracts` | `ContextMenuMgr.Contracts.dll` | pipe 契约、通知、菜单模型、Deep Analysis 模型、路径和服务元数据 | 不放具体平台操作实现。 |

## 3. 代码地图

| 区域 | 主要文件 | 职责 | 常见坑 |
| --- | --- | --- | --- |
| 前端 DI / 启动 | `App.Services.xaml.cs` | 注册服务、ViewModel、页面和 WPF-UI 导航 | 新服务要按生命周期选择 Singleton，不要绕过 DI 到处 new。 |
| 主窗口 / 导航 | `MainWindow.xaml`、`MainWindow.xaml.cs`、`ShellViewModel.cs` | 导航栏、全局搜索、刷新、Explorer restart、审批 badge | 搜索跳转需要同时导航和设置页面筛选。 |
| WorkspaceService | `ContextMenuWorkspaceService.cs` | 后端连接、快照加载、通知、普通菜单动作、服务状态 | 不要在页面 ViewModel 中重复写后端连接流程。 |
| BackendClient | `NamedPipeBackendClient.cs` | `PipeRequest` 发送、响应解析、通知订阅 | 新 runtime 操作优先新增 `PipeCommand`，保持结构化响应。 |
| BackendServiceManager | `BackendServiceManager.cs` | UAC bootstrapper、服务安装/卸载/停止/启动模式 | 只用于服务生命周期，不用于普通菜单开关。 |
| BackendRuntime | `BackendRuntime.cs` | 组合后端服务、启动 pipe/monitor、隔离新增项、确保 TrayHost | 新增项隔离和通知去重要保持稳定逻辑 key。 |
| NamedPipeBackendServer | `NamedPipeBackendServer.cs` | pipe ACL、请求分发、用户上下文解析、通知广播 | userContext 解析路径不能随意复用到无关功能。 |
| ContextMenuRegistryCatalog | `ContextMenuRegistryCatalog.cs` | 传统菜单枚举、开关、审核、删除备份、Registry Write Protection | 传统菜单和 Win11 新菜单不是同一模型。 |
| ContextMenuRegistryMonitor | `ContextMenuRegistryMonitor.cs` | 轮询快照、发现运行时新增项、维护内存 baseline | 只有相邻运行时快照确认的关变开才交给 catalog 静默纠正；启动/登录后的首个稳定快照只做离线 baseline。 |
| ContextMenuStateStore | `ContextMenuStateStore.cs` | `RuntimePaths.StateDatabasePath` 状态库 | 审核状态不是纯 UI 状态。 |
| SpecialMenuService | `SpecialMenuService.cs` | ShellNew、SendTo、WinX、OpenWith、DragDrop、CommandStore、GuidBlock、IE MenuExt | ShellNew/SendTo/WinX/OpenWith 必须带正确用户上下文。 |
| Windows11ContextMenuCatalog | `Windows11ContextMenuCatalog.cs` | PackagedCom 和 AppxManifest 枚举、按 blocked list 判断启用状态 | 没有用户 SID 会跳过 Win11 snapshot。 |
| Windows11BlocksService | `Windows11BlocksService.cs` | HKLM 和用户级 Win11 blocked list 读写 | 用户级 blocked list 不能写 SYSTEM 的 `HKCU`。 |
| Win11ClassicContextMenuService | `Win11ClassicContextMenuService.cs` | 每用户 Win11 全局恢复经典右键菜单注册表 tweak | 必须写 `HKEY_USERS\<sid>\Software\Classes`，不能写 SYSTEM `HKCU` 或 HKLM。 |
| AutoStartService | `AutoStartService.cs` | 用户级 `StartWithWindows` / `ShowTrayIcon` policy 和旧 Run value 清理 | 开机启动同时影响服务启动模式和用户策略；托盘图标显示策略不能写到 SYSTEM `HKCU`。 |
| FrontendAutostartLauncher | `FrontendAutostartLauncher.cs` | 服务跨 Session 启动 TrayHost/Frontend | UI 进程必须用 WTS token 进用户 Session。 |
| ContextMenuGlobalSearchService | `ContextMenuGlobalSearchService.cs` | 本地候选池、搜索评分、经典/Win11 结果合并 | 不要每次输入打后端。 |
| ContextMenuDeepAnalysisService | `ContextMenuDeepAnalysisService.cs` | 选择 ProbeHost 架构、启动隔离进程、解析结果 | ProbeHost 失败不能影响普通菜单管理。 |
| ProbeHost | `ContextMenuMgr.ProbeHost/src`、`ContextMenuMgr.ProbeHost.vcxproj` | native C++ COM 探测、SpecificHandler/WholeContextMenu、菜单枚举 | 不要把它改成提权进程或注册表写入工具。 |
| 构建脚本 | `build.ps1`、`Scripts/Build.Common.psm1`、`ContextMenuMgr.Frontend.csproj`、`Installer/build_Installer.iss` | 多目标发布、ProbeHost 多架构、安装包 | 改动后必须验证 x86/x64/arm64 ProbeHost 布局。 |

## 4. 启动与运行时流程

前端启动的大致流程：

```text
ContextMenuManagerPlus.exe
  -> App.Services.xaml.cs 建立 DI
  -> MainWindow / ShellViewModel
  -> ContextMenuWorkspaceService.InitializeAsync
  -> EnsureBackendReadyAsync
      -> NamedPipeBackendClient.PingAsync
      -> 如服务缺失或不可用，提示或调用 BackendServiceManager
  -> 同步日志级别
  -> SubscribeNotifications
  -> EnsureTrayHost
  -> GetSnapshot
  -> 分类页面从 WorkspaceService.Items 本地筛选展示
```

服务启动的大致流程：

```text
SCM 启动 ContextMenuManagerPlus.Service.exe --service
  -> BackendWindowsService.OnStart
  -> BackendRuntime.StartAsync
      -> legacy runtime file migration
      -> RuntimeDataAclRepairService 修复 RuntimePaths.RootDirectory ACL
      -> FileLogger
      -> ContextMenuStateStore / RegistryBackupService / Catalog
      -> SpecialMenuService / Windows11BlocksService / AutoStartService
      -> NamedPipeBackendServer.Start
      -> ContextMenuRegistryMonitor.Start
      -> best-effort TryEnsureTrayHost
```

审核通知的大致流程：

```text
ContextMenuRegistryMonitor 轮询快照
  -> 确认 regular baseline 已存在并发现运行时 Added
  -> BackendRuntime.HandleNewItemDetectedAsync
  -> ContextMenuRegistryCatalog.QuarantineNewItemAsync
  -> BackendNotification(ItemDetected)
  -> Frontend / TrayHost 展示审核入口
  -> 用户 Allow / Deny / Remove
  -> PipeCommand.ApplyDecision
```

## 5. 传统右键菜单实现

传统菜单由 `ContextMenuRegistryCatalog` 枚举 `MonitoredRoots`。核心类型是 `ContextMenuEntry`，它记录 `Category`、`EntryKind`、`RegistryPath`、`BackendRegistryPath`、`SourceRootPath`、`CommandText`、`CanEditCommandText`、`HandlerClsid`、启用状态、删除状态、审核状态和一致性问题。

当前传统菜单主要覆盖两类注册表模型：

| 类型 | 示例路径 | 处理方式 |
| --- | --- | --- |
| `shell` verb | `*\shell`、`Directory\Background\shell` | 读取命令、图标和属性；通过移动/改写相关键和值实现启用/禁用和属性修改。 |
| `shellex\ContextMenuHandlers` | `*\shellex\ContextMenuHandlers` | 读取 handler CLSID；普通开关在当前注册项的 `ContextMenuHandlers` / `-ContextMenuHandlers` 容器之间移动。`Shell Extensions\Blocked` 是独立的 CLSID 全局规则。 |

普通 legacy `shell` verb 的命令文本编辑通过 `PipeCommand.SetCommandText` 进入 `ContextMenuRegistryCatalog.ApplyCommandTextAsync`，写 `<verb>\command` 的默认 `REG_SZ`。后端只为没有 `SubCommands` / `ExtendedSubCommandsKey`、没有 `DelegateExecute`、没有 `DropTarget\CLSID`、没有 `ExplorerCommandHandler` 的传统 ShellVerb 设置 `CanEditCommandText=true`；Shell Extension、Windows 11 packaged context menu 和多命令父级不走这条编辑路径。

ShellVerb visibility mutation always verifies `ShellVerbVisibility.IsEnabled` on the same physical key, then refreshes the authoritative catalog entry before persisting desired/observed state. For a protected machine `HKLM\SOFTWARE\Classes` verb, `ContextMenuRegistryCatalog` first attempts the ordinary write. Only an ACL access-denied failure can invoke `ProtectedRegistryMutation`; it temporarily enables `SeTakeOwnershipPrivilege` and `SeRestorePrivilege`, grants LocalSystem `SetValue` only, verifies the mutation, then restores and verifies the original owner/DACL descriptor. It never applies to `HKEY_USERS\<SID>\Software\Classes`, never bypasses Registry Write Protection, and never uses BluePointLilac's unconditional ownership takeover.

`ContextMenuStateStore` 保存后端状态，不只是缓存。它用于保存已确认开关状态、pending approval、删除恢复记录和被抑制的检测等。常规菜单与 WPS/Office 分别用 `internal:baseline:regular:v1`、`internal:baseline:wps-office:v1` 标记 baseline 已完整建立；不能再用“存在任意 state”代替某一数据源的 baseline。`RegistryBackupService` 在删除前调用 `reg.exe export` 保存 `.reg`，恢复时调用 `reg.exe import`。

设置页的“重置状态数据库”必须通过链路 A 的 `PipeCommand.ResetStateDatabase` 执行，不能由前端直接删除 `%ProgramData%` 或 portable `Data` 中的文件。后端在持久状态操作门和各 store 自身 gate 内删除 current、`.bak`、临时 generations、backend protection settings 与当前 host 删除备份，然后由同一次前端刷新依次重建 regular 和 WPS baseline。

StateStore 的 JSON 写入是 crash-safe staged write：unique temp（与 current 同目录）→ write-through flush/close → 用生产 parser 验证 envelope 或 legacy dictionary → `File.Replace` current，同时保留已验证旧 current 的 `.bak`。加载发现损坏的 current 会保留原始文件到 `RuntimePaths.QuarantineDirectory\corrupt-state-...`，然后验证并恢复 `.bak`；没有有效备份时返回空状态，由 catalog 使用交互用户上下文从当前注册表重建常规与 WPS baseline，避免任何 `Added` / `Modified` / pending 误报。恢复不修改注册表。读写 ACL/I/O 失败和未来 schema 不是 corruption reset；Portable host identity mismatch 仍走独立的 `foreign-host-...` quarantine。

外部变化检测由 `ContextMenuRegistryMonitor` 轮询实现。状态库相关快照和用户操作通过 catalog 的持久状态操作门串行，避免 monitor、前端刷新和 WPS 刷新互相覆盖。服务启动早于交互式 Session 时，无用户上下文的快照不得提交首次 baseline；观察到 Session 后重建内存 baseline 前，仍用持久活跃数量的 80% 阈值防止用户 hive 尚未完全加载时制造运行时新增通知。

传统菜单和 Win11 新菜单不是同一套模型。不要把 `PackagedCom` 项当作普通 `shell` / `shellex` 项处理。

文件类型页的 Custom Extension 场景会基于前端用户上下文解析扩展名关联的 class roots，而不是只扫描 `SystemFileAssociations.<ext>` 和 `.<ext>`。它会包含直接关联的默认 ProgID、`OpenWithProgids`、以及当前用户 `FileExts` 的 `UserChoice` / `OpenWithProgids`，并只枚举存在传统菜单子键的候选 root。用户级关联读取必须使用 `HKEY_USERS\<sid>`；例如 `.ps1` 可以通过机器级默认 ProgID 扫描到 PowerShell 7 注册在 `Microsoft.PowerShellScript.1\shell` 下的 verb，同时保持真实 ProgID 路径用于后续启用、禁用和删除。

### Enhance Menus 字典

`EnhanceMenusDic.xml` 中的内置增强菜单是静态 shell 命令字典。ContextMenuMgr 只负责把这些字典项安装、禁用或移除到当前前端用户的 `Software\Classes` 下；菜单项被启用后，运行时执行必须完全由注册表中的命令、系统工具和生成的 `.vbs` / `.bat` / `.cmd` 文件完成。

增强菜单字典的源码只保留在 `ContextMenuMgr.Frontend/Resources/EnhanceMenusDic.xml`。前端构建时会把它复制到输出目录，前端读取该文件并把选中项的定义 XML 传给后端；后端不维护第二份增强菜单字典，也不在运行时重新读取项目内的字典副本。

增强菜单命令不得调用 `ContextMenuMgr` 前端、后端服务、TrayHost、Named Pipe、服务 RPC/IPC 或任何要求本程序仍在运行的宿主进程。迁移 BluePointLilac / ContextMenuManager 内置项时，应优先保持 `EnhanceMenusDic.xml` 的 `KeyName`、`SubKey`、`Command Default`、`ShellExecute`、`FileName`、`Arguments`、`PowerShellScript` 和 `CreateFile` 脚本内容语义一致；新增翻译节点只能影响显示文本，不能改变命令语义。

当 `Command` 没有 `Default` 时，后端按 BluePointLilac 的规则由 `FileName` + `Arguments` 生成注册表默认值；`FileName` 或 `Arguments` 为空且包含 `CreateFile` 时，会写入 `RuntimePaths.GeneratedProgramsDirectory` 下的持久脚本文件。`.bat` / `.cmd` 使用系统默认编码，其它生成文件（包括 `.ps1`）使用 Unicode。启用后的注册表命令必须直接指向系统工具或这些持久生成文件，而不是依赖后端服务继续运行。

`ShellExecute` 只在确实需要 ShellExecute 专属行为时才保留旧的 `mshta vbscript:createobject("shell.application").shellexecute(...)` 包装，例如显式非空 `Directory` 或未知 ShellExecute 属性。空 `<ShellExecute/>`、`Verb="open"`，以及只有 `WindowStyle` 的增强菜单项会直接写成 `<FileName> <Arguments>`，避免多层 XML / C# / 注册表 / mshta / VBScript 转义导致简单命令失效。`Verb="runas"` / 管理员增强菜单项默认编译为独立的 `powershell.exe ... Start-Process -Verb RunAs ...` 命令；复杂管理员动作可在字典中使用 `PowerShellScript`，由后端生成提升后的 PowerShell command block。启用后的命令仍然只依赖系统工具、注册表命令值和必要的持久生成脚本，不依赖 ContextMenuMgr 前端、后端、TrayHost 或 pipe。

增强菜单字典和后端写入层会把直接作为可执行文件使用的裸 `cmd` / `cmd.exe` 规范化为 `C:\Windows\System32\cmd.exe`，把裸 `explorer` / `explorer.exe` 规范化为 `C:\Windows\explorer.exe`；`Command Default` 只在命令开头是这些安全前缀时改写可执行文件部分。`Arguments` 和生成脚本中直接启动 Explorer 的低风险位置也应使用 `C:\Windows\explorer.exe`，但不要把 `taskkill /im explorer.exe`、`tskill explorer`、图标引用或进程名匹配参数误改成路径。写入普通 `MUIVerb` 等可见标签时会移除 Win32 菜单加速键 `&`，但保留 `&&` 表示的字面量 `&`，并且不处理 `@shell32.dll,-...` 这类资源引用。

## 6. 新菜单项检测与审核

核心规则固定为：空库/重置先无提示采纳当前常规菜单与 WPS 状态；运行时未知项隔离并待审核；已知项开变关标为 `Modified`；只有相邻运行时快照确认的已知项关变开才静默关回；离线未知项重启后只标 `Added`、不待审核；离线开关变化无论方向均标 `Modified`；完整快照连续确认实际项缺失后从活动 baseline 移除。开关差异不得再同时显示 generic consistency，详细矩阵见 `docs/registry-model.md`。

新增项审核不是单纯 UI 状态。流程是：

```text
Monitor 发现新增项
  -> Catalog 确认 regular baseline 已建立并判断为 Added
  -> QuarantineNewItemAsync 立即禁用/隔离
  -> StateStore 标记 IsPendingApproval
  -> BackendNotification.ItemDetected
  -> TrayHost/Frontend 通知用户
  -> ApplyDecision: Allow / Deny / Remove
```

| 决策 | 当前行为 |
| --- | --- |
| `Allow` | 允许并清除 pending 状态。 |
| `Deny` | 保持禁用并清除或更新 pending 状态。 |
| `Remove` | 对待审核项走删除路径，必要时清理备份状态。 |

`ContextMenuApprovalIdentity` 为传统菜单和 Win11 packaged 菜单生成逻辑 key，用于分组和通知去重。Win11 项可能在多个 category 下出现，所以逻辑 key 不直接等于 category-specific ID。

## 7. Registry Write Protection

Registry Write Protection 是针对传统右键菜单受监控根的 ACL 防护。设置入口是 `GetRegistryProtectionSettingAsync` / `SetRegistryProtectionSettingAsync`，配置保存在 `RuntimePaths.BackendProtectionSettingsPath`。

它和普通禁用的区别：

| 功能 | 作用 |
| --- | --- |
| 普通禁用 | 修改某个菜单项的注册表状态，让它不显示或不可用。 |
| Registry Write Protection | 修改受监控根的 ACL，阻止第三方新增/修改菜单项。 |

它和 ShellNew ACL Lock 的区别：

| 功能 | 目标 |
| --- | --- |
| Registry Write Protection | 传统菜单 `MonitoredRoots` 对应的注册表根。 |
| ShellNew ACL Lock | 用户 Explorer ShellNew order key：`Software\Microsoft\Windows\CurrentVersion\Explorer\Discardable\PostSetup\ShellNew`。 |

开启 Registry Write Protection 后，第三方安装器或软件更新器写右键菜单时可能失败或行为异常。前端的 `RegistryProtectionDialog` 会在普通菜单编辑、禁用、删除等操作前提示用户解锁。后端也有 preflight 和异常 fallback：如果检测到保护已开启，会返回 `PipeErrorCodes.RegistryWriteProtectionEnabled`。

代码中存在 app-owned operation 的临时 unlock/relock 路径：`RunWithRegistryWriteProtectionTemporarilyDisabledAsync` 会在后端内部操作前临时解除保护，完成后尝试恢复。这是 best-effort，relock 失败会记录错误并可能把警告追加到响应消息中。

## 8. SpecialMenu 实现

`SpecialMenuService` 管理的入口不是普通菜单项。`SpecialMenuKind` 当前包括 `ShellNew`、`SendTo`、`WinX`、`OpenWith`、`DragDrop`、`CommandStore`、`GuidBlock`、`InternetExplorer`。其中 `ShellNew`、`SendTo`、`WinX`、`OpenWith` 明确需要 `BackendUserContext`。

WPS / Microsoft Office 共存保护由 `OfficeSuiteCoexistenceDetector` 提供。它只在 WPS Office 和 Microsoft Office 都被检测到时启用，并且只通过链路 A 读取 / 写入当前前端用户的 `HKEY_USERS\<sid>\Software\Classes`。该保护会把 WPS 对 Office 文档扩展名默认 ProgID、Office ProgID `DefaultIcon`、以及 WPS ShellNew command 注入的变更生成为 `special:wps-*` 合成待审核项。WPS 合成项通过 `PipeCommand.GetWpsOfficePendingApprovals` 单独进入待审核页，不进入普通菜单 snapshot、文件页面、ShellNew 页面、OpenWith 页面或全局搜索。它不使用宽泛 ACL 硬拦截 WPS，不修改 Windows `UserChoice` / Hash，不恢复默认应用关联。

文档图标来源切换同样属于 WPS / Microsoft Office 共存保护的一部分，只在 ShellNew 页面以 `Microsoft Office <ToggleSwitch> WPS Office` 形式展示；OpenWith 页面不展示该开关，WPS 合成异常条目仍只进入待审核页。`PipeCommand.GetOfficeSuiteCoexistenceStatus` 返回检测状态和受保护扩展名列表；`PipeCommand.SetDocumentIconProvider` 只切换用户级 `HKU\<sid>\Software\Classes\<OfficeProgID>\DefaultIcon`，并在成功后通知 Shell 关联变化。默认打开方式切换不是当前功能的一部分，后续若要支持必须单独设计，不能直接写 `UserChoice` Hash。

### ShellNew

ShellNew 由文件扩展名下的 `ShellNew` 子键和 Explorer 的排序键共同决定。当前实现会读取用户 `Software\Classes`，并使用 `HKEY_USERS\<sid>\Software\Classes` 来避免写到 SYSTEM 的 `HKCU`。

关键点：

| 内容 | 说明 |
| --- | --- |
| ShellNew 子键 | 典型位置是用户或机器 Classes 下的 `.<ext>\ShellNew`，可能有 `NullFile`、`Data`、`Command`、`Config\BeforeSeparator` 等值。 |
| Explorer ShellNew order key | `Software\Microsoft\Windows\CurrentVersion\Explorer\Discardable\PostSetup\ShellNew`，用于排序和锁定。 |
| Classes 排序 | `MoveShellNewAsync` 会基于当前 real items 和 order key 重写顺序。 |
| ACL lock / unlock | `SetShellNewOrderLockAsync` 给 order key 加或移除显式 Deny 规则，防止 Explorer 或第三方改排序。 |
| ACL 修改范围 | ShellNew lock/unlock 只请求 `ReadPermissions` / `ChangePermissions`。v2 lock 添加显式 WorldSid `Deny SetValue | CreateSubKey | Delete`，并保留现有 inherited / explicit allow 规则。 |

显示名解析与 BluePointLilac / ContextMenuManager 对齐：先读取可解析的 `ShellNew\MenuText` 间接资源字符串，再读取默认 ProgID 的 `FriendlyTypeName`，再读取默认 ProgID 默认值，最后回退到扩展名。普通用户输入的显示名不写纯文本 `ShellNew\MenuText`；创建和编辑都会写到 `HKU\<sid>\Software\Classes\<ProgID>\FriendlyTypeName`。如果 ShellNew 项来自 HKLM，后端优先创建 `HKU\<sid>\Software\Classes\...` 用户覆盖层并修改覆盖层中的 ShellNew-local 值，避免默认修改机器范围。没有有效默认 ProgID 时，显示名不能安全保存为 FriendlyTypeName；创建可继续生成扩展名级 ShellNew，但刷新显示可能回退到扩展名。

ShellNew 的 ACL Lock 只作用于 ShellNew order key，不是全局 Registry Write Protection。v2 lock 故意避免 `RegistryRights.WriteKey`，因为 .NET 中 `WriteKey` 过宽，可能阻止后续 ACL 读取 / 修改并让本程序无法解锁。ContextMenuMgr 不再为 ShellNew lock/unlock 执行 take ownership、replacement DACL 或高危所有权 / 还原类权限 fallback。若旧版 broad `WriteKey` lock 或外部工具造成的 broken ACL 无法用 `ReadPermissions | ChangePermissions` 安全修改，应提示用户用 BluePointLilac / ContextMenuManager 或系统工具解锁 / 修复后再重试。

### SendTo

SendTo 是用户 profile 下的文件系统目录：`%APPDATA%\Microsoft\Windows\SendTo`。当前实现通过 `BackendUserContext.GetSendToPath()` 定位用户目录，创建/更新 `.lnk`，用 `DesktopIniStore` 维护显示名。删除不是直接永久删除，而是软删除到 `.deleted` 目录；恢复时再移回。

### WinX

WinX 是用户 `LocalAppData` 下的目录：`%LOCALAPPDATA%\Microsoft\Windows\WinX`。当前实现支持 group 和 entry 的创建、更新、移动、软删除、恢复默认。`.lnk` 需要 `WinXHasher.HashLnk`，否则 Windows 可能不接受或不显示该快捷方式。排序通过重写文件名前缀和跨 group 移动完成。

### OpenWith

OpenWith 管理所有文件“打开方式”菜单中的应用注册。后端枚举 `HKU\<sid>\Software\Classes\Applications` 和 `HKLM\SOFTWARE\Classes\Applications`，通过应用 key 上的 `NoOpenWith` 控制是否出现在菜单中，并提供 `NoUseStoreOpenWith` policy 开关。新增项只写当前前端用户的 `HKU\<sid>\Software\Classes\Applications\<app.exe>\shell\open\command` 和 `FriendlyAppName`；不要写服务 `HKCU`。

## 9. Windows 11 新右键菜单

Windows 11 新右键菜单由 packaged COM 和 AppX manifest 驱动，不等于传统 `shell` / `shellex`。

`Windows11ContextMenuCatalog` 当前做法：

1. 仅在 `OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)` 时支持。
2. 从 `Registry.ClassesRoot\PackagedCom\Package` 找 package。
3. 从 package repository 找安装路径。
4. 解析 `AppxManifest.xml` 或 `AppxMetadata\AppxBundleManifest.xml`。
5. 提取 `ComServer`、CLSID、display name 和 `ContextTypes`。
6. 映射到 `ContextMenuCategory` 并生成 `ContextMenuEntry`。
7. 结合 HKLM blocked list 和用户 blocked list 判断 `IsEnabled`。

blocked list 有两层：

| 作用域 | 路径 |
| --- | --- |
| 机器级 | `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked` |
| 用户级 | `HKEY_USERS\<sid>\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked` |

snapshot 和开关都需要 userContext。没有用户 SID 时，`Windows11ContextMenuCatalog.EnumerateEntriesAsync` 当前会跳过枚举；`Windows11BlocksService` 对用户级写入会返回缺少用户上下文，机器级 fallback 只适用于明确机器级操作。

## 10. AutoStart 与 TrayHost

开机启动同时涉及服务启动模式和用户级策略：

| 层 | 文件 | 当前行为 |
| --- | --- | --- |
| 用户级 policy | `AutoStartService`、`FrontendAutostartLauncher` | 写/读 `HKEY_USERS\<sid>\Software\ContextMenuMgr\Frontend\StartWithWindows` 和 `ShowTrayIcon`。 |
| 旧 Run key | `AutoStartService` | 读取时作为 fallback；写入时清理 `ContextMenuManagerPlus.TrayHost` 旧值。 |
| 服务启动模式 | `BackendServiceBootstrapper` | `install-or-repair` 和 `set-startup-mode` 根据 `--user-sid` 对应 policy 设置服务 `auto` 或 `demand`。 |
| 用户 Session 进程 | `FrontendAutostartLauncher` | 服务启动或 Session 事件时按 policy best-effort 拉起 TrayHost；`ShowTrayIcon=0` 时传入 `--hide-tray-icon`。 |

TrayHost 是每用户进程，因为托盘图标、通知和通知点击激活必须出现在用户交互式桌面。服务只是负责在正确 Session 中启动它，后端通知仍通过 IPC 交给 TrayHost 后再由 `Shell_NotifyIconW` 显示。开机启动必须带用户 SID，因为服务启动模式是机器级，用户是否希望随 Windows 启动以及是否显示托盘图标是用户级。TrayHost 可以没有可见托盘图标，但进程必须继续运行。

## 11. Restart Explorer

Restart Explorer 通过 pipe 命令 `RestartExplorer` 进入后端。`NamedPipeBackendServer` 会调用 `ResolveFrontendUserSessionContextAsync`，必须拿到前端用户的 `SessionId`。`ExplorerRestartService.RestartExplorer` 只杀同一 Session 中的 `explorer.exe`。

不要杀所有 `explorer.exe`。多用户、远程桌面和服务 Session 场景下，杀错 Session 会影响其他用户，或者完全没有效果。也不要在服务 Session 里尝试启动/控制 Explorer UI。

## 12. 全局搜索与页面筛选

全局搜索由 `ContextMenuGlobalSearchService` 维护本地候选池。候选来源包括：

- `ContextMenuWorkspaceService.Items` 中的传统右键菜单项，范围是 `File`、`Folder`、`Directory`、`DirectoryBackground`、`DesktopBackground`、`Drive`、`Library`、`Computer`、`RecycleBin` 等 `ContextMenuCategory`。
- `Windows11ContextMenuService.CurrentItems` 中的 Win11 项。

当前全局搜索不覆盖 ShellNew / SendTo / WinX / OpenWith、FileTypes、OtherRules、Approvals、Settings 等页面。新增页面如果需要被全局搜索命中，必须显式扩展 `ContextMenuGlobalSearchService` 的候选池和跳转筛选逻辑。

候选会在 workspace item 集合变化、item 显示名/状态/本地备注变化、Win11 items 变化、语言变化、设置变化（含 Win11 本地备注编辑）时重建。搜索字段包括 display name、key name、registry path、backend registry path、source root、command、CLSID、file path、本地备注（传统菜单与 Win11 菜单）、状态标签、分类标签，以及 Win11 的 package / publisher / context types。`ContextMenuSearchMatcher` 会忽略标点、分隔符和符号，因此 `7zip` 可以命中 `7-Zip`；分类页筛选和全局搜索共用该匹配器。

搜索结果会 best-effort 使用菜单项实际 icon；没有可用图标时使用分类或 Win11 fallback 图标。输入时 `ShellViewModel` 只调用本地 `Search`，不访问后端，所以可以快速响应。用户选择结果后，`GlobalSearchNavigationFilterService.RequestFilter` 保存目标页面、分类、是否 Win11、筛选文本和 itemId，再导航到目标页面。目标页面消费 pending filter，把页面筛选框设为选中菜单项名称。

不要把每次输入改成后端查询。后端快照应由刷新或通知驱动。

传统菜单项与 Win11 菜单项的用户备注都保存在 `FrontendSettings.ContextMenuItemNotes`，以稳定 item id 为键，不写入注册表；Win11 项以 `Windows11ContextMenuItemDefinition.Id`（如 `win11|{clsid}|{category}` 或 `win11-system|{commandKey}`）为键。`ApplicationGroupsPageViewModel` 只使用已加载的传统菜单 workspace 项，依次按已解析文件路径、命令中的 exe/dll、CLSID、注册表路径 fallback 分组；Win11 modern menu 不参与分组。“全部禁用”复用 `ContextMenuWorkspaceService.SetEnabledAsync`，仅处理当前启用且可切换的项。

File Types 页提供隐藏的文件类型相关项批量管理子视图，不作为普通导航页面，也不增加可直接选择的 TabItem。入口在 File Types 的 scene 菜单项卡片上；前端用 `ContextMenuApplicationIdentityService` 生成菜单身份，后端通过 `PipeCommand.FindRelatedFileTypeMenuItems` 按需扫描 HKLM 与前端用户 `Software\Classes` 中的扩展名、ProgId 和 `SystemFileAssociations` 根。ShellVerb 只按规范化命令程序路径加 key name 匹配，ShellExtension 只按 Handler CLSID 匹配，不按显示名匹配。查询结果不写入普通 workspace，也不扩展服务启动检测 baseline；批量启用、禁用和删除复用现有 per-item 操作与备份/保护逻辑。

`CategoryPageView` 中的传统菜单项可以携带稳定 item id 跳转到 `ApplicationGroupsPage`。跳转请求复用 `GlobalSearchNavigationFilterService` 的 pending request 机制；按应用管理页面按 item id 找到目标传统菜单项，进入导航精确筛选模式，只渲染目标项所属的一个分组，并且该分组内只包含目标项本身。搜索框显示目标项的可读名称；用户手动修改搜索框或点击清除筛选后，页面退出精确筛选并恢复普通分组搜索。该定位过程只使用前端已加载数据，不触发新的后端、注册表、文件系统查询或导航后滚动定位。

## 13. Deep Analysis 与 ProbeHost

Deep Analysis 用于观察 shell extension 在样本路径下实际向 `IContextMenu` 填充了哪些菜单项。它不改变菜单状态。

前端流程：

```text
用户点击深入分析
  -> ContextMenuDeepAnalysisService.SelectProbeHost
  -> 验证 ProbeHost 架构和依赖
  -> 写 request JSON 到临时目录
  -> 启动 ContextMenuMgr.ProbeHost.exe --request ... --result ...
  -> 捕获 stdout/stderr
  -> 优先解析 result file，其次解析 stdout
  -> UI 展示菜单项或失败 diagnostics
```

ProbeHost 支持 `x86`、`x64`、`arm64`。选择依据主要是 handler DLL 的 PE machine type；unknown 时当前实现倾向于当前前端进程架构。`ContextMenuMgr.Frontend.csproj` 和 `Scripts/Build.Common.psm1` 都有多架构 native ProbeHost 发布逻辑。

`SpecificHandler` 直接 `CoCreateInstance` 目标 handler 并尝试 `IShellExtInit` / `IContextMenu`。`WholeContextMenu` 让 Shell 对样本路径创建完整上下文菜单。失败是常见结果，尤其是第三方 handler 依赖真实文件类型、进程环境、扩展动词、Explorer 内部状态或特定架构时。

ProbeHost 不写注册表、不执行菜单命令、不提权。不要在前端或后端直接加载第三方 Shell Extension DLL。

## 14. 主题、设置与本地化

`RuntimePaths` 通过 `AppContext.BaseDirectory` 下的 `ContextMenuMgr.package.json` 显式识别包类型，不根据安装路径猜测。缺失或无效时默认为 Installer。Installer 根目录是 `%ProgramData%\ContextMenuMgr`；Portable 根目录是 `<应用目录>\Data`。`FrontendSettingsService`、日志、状态库、后端保护设置和删除备份都从该根目录派生。旧版本可能使用 `%LOCALAPPDATA%\ContextMenuMgr`、`%ProgramData%\ContextMenuMgr` 或 `%ProgramData%\ContextMenuMgr\Data`，当前代码保留 copy-only 迁移/兼容路径常量；Portable 迁移不会移动或删除 ProgramData 数据，也不会覆盖已有 portable 数据。

`ContextMenuWorkspaceService` treats service/pipe reachability and an individual backend operation as separate health signals. A successful `Ping` followed by a failed `GetSnapshot` shows a data-load error and must not start UAC service repair or recommend Install/Repair. StateStore automatic recovery emits one localized `ServiceMessage` through the existing notification connection; repeated WPS approval refresh failures back off from the normal five-second interval up to one minute and reset to the normal interval after success.

Portable 包的运行时数据分为两类：纯 UI 偏好可以随包移动，但注册表运行时状态必须绑定当前 Windows 安装和前端用户。`ContextMenuStateStore` 使用 schemaVersion=2 envelope 保存 `hostIdentity`，其中只包含 `MachineGuid + frontend user SID` 的 SHA-256 指纹、短前缀、schema version 和创建时间，不保存原始 MachineGuid 或 SID。Portable 模式下如果 `context-menu-state.json` 的指纹与当前主机不匹配，后端不会加载旧状态，而是把旧文件移动到 `RuntimePaths.QuarantineDirectory` 下的 `foreign-host-...` 目录，并创建带当前指纹的新空状态库；legacy raw dictionary 只在能验证当前 host identity 时迁移一次。

Portable 删除备份按 host identity 分目录，当前主机目录由 `RuntimePaths.GetHostScopedDeletedBackupsDirectory(hostPrefix)` 生成。不同指纹或旧根目录下的 `.reg` 备份会被移动到 `Data\Quarantine`，恢复时只允许导入当前 host-scoped `DeletedBackups` 目录下的备份。`frontend-settings.json` 仍可跨主机加载语言、主题、颜色等纯偏好；其中 `ContextMenuItemNotes` 仍是 item/registry-bound 数据，后续应迁移到 host-bound 本地状态文件或在 foreign-host 检测时清理。

手动验证 portable host identity guard 时，先用 portable 包在机器/用户 A 启动并确认 `context-menu-state.json` 写出 schemaVersion 和 hostIdentity 指纹；再把 `Data` 复制到模拟机器/用户 B（可直接改 JSON 中的 fingerprint），启动后确认旧状态被移动到 `Data\Quarantine\foreign-host-...`、新状态为空且带当前指纹。随后确认旧 fingerprint 的 `DeletedBackups` 不会被使用，`RestoreBackupAsync` 对当前 host-scoped 目录外的 `.reg` 返回 foreign-host 拒绝消息，`frontend-settings.json` 的语言/主题/颜色仍可加载，Installer 模式仍能读取既有状态和备份。

后端启动时会在创建 `FileLogger`、`ContextMenuStateStore` 和 `BackendProtectionSettingsStore` 前 best-effort 调用 `RuntimeDataAclRepairService`。前端保存设置遇到 ACL 相关的 `UnauthorizedAccessException` / `IOException` / `SecurityException` 时，会先通过 `PipeCommand.RepairRuntimeDataAcl` 请求后端修复并重试一次；如果 pipe 不可用，再通过 UAC bootstrapper 的 `repair-runtime-data-acl` 命令做独立 fallback。该修复只作用于 `RuntimePaths.RootDirectory` 文件系统 ACL，不是 Registry Write Protection，也不是 ShellNew ACL Lock。

`FrontendThemeService` 基于 `AppThemeOption` 调用 WPF-UI 的 `ApplicationThemeManager`。启动时 `Initialize` 会应用保存的主题；选择 System 时会启用 `SystemThemeWatcher`，Light/Dark 会关闭系统跟随。

`LocalizationService` 支持 System、`zh-CN`、`en-US`、`zh-TW`，会设置当前线程 culture 和 WPF `CurrentLanguage`。设置页切换语言后还会 best-effort 通知 TrayHost reload localization。

设置页和服务交互的重点：

| 设置 | 交互路径 |
| --- | --- |
| 日志级别 | 前端本地更新，并同步 Backend 和 TrayHost。 |
| 开机启动 | `FrontendStartupService` 通过 pipe 写用户 policy；若服务已安装，再通过 bootstrapper 设置服务启动模式。 |
| 显示系统托盘图标 | 前端本地更新，通过 `PipeCommand.SetTrayIconPolicy` 写 `HKEY_USERS\<sid>\Software\ContextMenuMgr\Frontend\ShowTrayIcon`，并通过 TrayHost control pipe 运行时隐藏/显示图标。 |
| Registry Write Protection | 通过 workspace 调用后端 pipe；前端本地设置只做 UI 同步。 |
| 禁用 Win11 新版右键菜单 / 恢复经典右键菜单 | 通过 workspace 调用后端 pipe；后端使用前端用户 SID 写 `HKEY_USERS\<sid>\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}`，前端设置文件只镜像真实注册表状态；写入成功后只置位 `ExplorerRestartStateService.MarkRequired()`，由主窗口顶部全局重启按钮执行 Explorer 重启。 |
| 服务安装/卸载/停止 | `BackendServiceManager` 走 UAC bootstrapper。Portable 包安装/修复前会检查当前应用目录运行时文件的 Mark-of-the-Web；命中时返回 `PORTABLE_RUNTIME_FILES_BLOCKED`，由设置页提示用户确认后解除当前 portable runtime 文件的 `Zone.Identifier`。 |
| 强力修复服务 | 设置页调用 `BackendServiceManager.ForceRemoveServiceAsync`，通过 `ContextMenuManagerPlus.Service.exe --service-bootstrap force-remove-service` 容错移除当前服务名和旧服务名；成功后才继续安装 / 修复。若返回 `SERVICE_PENDING_DELETE`，前端提示关闭 Services MMC、任务管理器服务页或重启后再重试。 |

## 15. 构建与发布

`build.ps1` 是总入口，会并行启动 `Scripts/Build-Target.ps1`，按平台和分发模式生成 installer 与 portable 包。版本来自前端 csproj 的 `InformationalVersion` / `FileVersion`；Debug 和 Beta 会追加 git short commit。

`Scripts/Build.Common.psm1` 负责实际发布：

| 内容 | 说明 |
| --- | --- |
| Frontend 发布 | 发布 `ContextMenuManagerPlus.exe`，并把 Backend、TrayHost、ProbeHost 复制进输出。 |
| Backend 发布 | 发布 `ContextMenuManagerPlus.Service.exe`。 |
| TrayHost 发布 | 发布 `ContextMenuManagerPlus.TrayHost.exe`。 |
| ProbeHost 发布 | 按平台发布多架构 native exe。`win-x64` 包含 x64 + x86；`win-arm64` 包含 arm64 + x64 + x86；framework-dependent portable 的 anycpu 路径由前端 csproj 构建 x86/x64/arm64。Release 脚本使用 MSBuild 构建 vcxproj，不再对 ProbeHost 使用 dotnet publish。 |
| 验证 | `Scripts/Verify-ProbeHostArchitecture.ps1` 检查目录标签和 PE machine type 是否匹配。 |
| Inno Setup | `Installer/build_Installer.iss` 打包完整发布目录，framework-dependent 模式启用 .NET dependency installer。 |

`ContextMenuMgr.Frontend.csproj` 也定义了构建时的 native ProbeHost 多架构目标：构建后只复制 `ContextMenuMgr.ProbeHost.exe` 到 `ProbeHost\x86`、`ProbeHost\x64`、`ProbeHost\arm64`，并把 `nlohmann-json-LICENSE.MIT` 复制到 `ThirdPartyNotices`。

构建脚本改动后必须验证多架构 ProbeHost。Deep Analysis 的很多失败最终来自发布布局缺文件或架构标签错误。

## 16. 开发规则

如果是新接手项目或让 AI Agent 修改代码，建议先阅读 `ai-maintainer-playbook.md`，按其中模板先判定链路和证据，再决定修改位置。

- 新 runtime 操作优先走 Backend Pipe，不要随便走 UAC bootstrapper。
- 用户级注册表必须使用 `HKEY_USERS\<sid>`。
- 需要 UI 的东西必须进用户 Session。
- SpecialMenu 必须带正确 `userContext`。
- Win11 blocked list 必须带 `userContext`。
- Registry Write Protection 和 ShellNew ACL Lock 不要混用。
- ProbeHost 失败不能影响普通菜单管理。
- 不要在前端或后端直接加载第三方 Shell Extension DLL。
- 全局搜索不要每次输入打后端。
- 构建脚本改动必须验证多架构 ProbeHost。
- `SessionId` 只解决桌面/进程位置问题，不解决用户注册表路径问题。
- `LocalSystem` 只代表高权限，不代表正确用户上下文。
- 新增 `PipeCommand` 时要同时考虑响应字段、日志、通知广播和前端异常处理。

## 文档维护原则

- 涉及权限链路、用户上下文、ProbeHost、构建脚本的改动，应同步更新 `docs`。
- 新增 `PipeCommand` 时，应检查 `process-and-privilege-flows.md` 的功能到链路表。
- 新增 `SpecialMenuKind` 时，应更新 `special-menus.md`。
- 修改 ProbeHost 架构选择或发布目录时，应更新 `deep-analysis-probehost.md` 和 `build-and-release.md`。
- 修改 `RuntimePaths` 时，应更新 `troubleshooting.md` 和本指南。
- 修改全局搜索支持范围时，应更新本指南和 `troubleshooting.md`。
- 文档描述必须以当前代码为准，不要复读旧 README。

## 17. 常见排错方向

| 问题 | 优先看什么文件 | 优先看什么日志 | 最可能混错哪条链路 |
| --- | --- | --- | --- |
| 前端连不上后端 | `NamedPipeBackendClient.cs`、`NamedPipeBackendServer.cs`、`BackendRuntime.cs` | `frontend-debug.log`、`backend.log`、`service-startup.log` | 把服务未启动当成 pipe 协议错误。 |
| 服务安装失败 | `BackendServiceManager.cs`、`BackendServiceBootstrapper.cs` | `bootstrap.log`、bootstrap result detail、`service-startup.log` | 链路 B result-file 或 UAC 被取消。 |
| 托盘不出现 | `FrontendAutostartLauncher.cs`、`BackendWindowsService.cs`、`TrayHostRunner.cs` | `backend.log`、`trayhost.log` | 链路 C 缺 SessionId 或用户 policy 关闭。 |
| 开机启动不生效 | `AutoStartService.cs`、`BackendServiceBootstrapper.cs`、`FrontendAutostartLauncher.cs` | `backend.log`、`bootstrap.log` | 只改服务启动模式，没写用户 `StartWithWindows`，或反过来。 |
| Win11 禁用后刷新丢失 | `Windows11ContextMenuCatalog.cs`、`Windows11BlocksService.cs` | `backend.log` 中 `Win11 command` 和 `OpenUserBlockedKey` | 丢了 `userContext`，写到错误用户 hive。 |
| ShellNew 锁住后解不开 | `SpecialMenuService.cs` | `backend.log` 中 `ShellNewOrderLockRequest`、`TryReadShellNewOrderLockState` 和 `HKEY_USERS\<sid>\Software\Microsoft\Windows\CurrentVersion\Explorer\Discardable\PostSetup\ShellNew` | 把 ShellNew ACL Lock 当成 Registry Write Protection，或期待主程序 take ownership 修复 broken ACL。 |
| SendTo / WinX 修改无效 | `SpecialMenuService.cs`、`DesktopIniStore.cs`、`WinXHasher.cs` | `backend.log` | 缺用户 profile 上下文，或 WinX `.lnk` 没重新 hash。 |
| 重启 Explorer 没反应 | `NamedPipeBackendServer.cs`、`ExplorerRestartService.cs` | `backend.log` 中 `RestartExplorerRequest` | 没拿到前端用户 SessionId，或目标 Session 没有 explorer。 |
| ProbeHost 缺失 | `ContextMenuDeepAnalysisService.cs`、`ContextMenuMgr.Frontend.csproj` | `frontend-debug.log` | 发布布局缺 native exe，或本机缺少 C++ build tools 导致未构建。 |
| ProbeHost 架构错 | `ContextMenuDeepAnalysisService.cs`、`Verify-ProbeHostArchitecture.ps1`、`Build.Common.psm1` | `frontend-debug.log` | `ProbeHost\x86/x64/arm64` 目录和实际 exe machine type 不匹配。 |
| 深入分析失败 | `ContextMenuDeepAnalysisService.cs`、`ContextMenuMgr.ProbeHost/src` | `frontend-debug.log` 的 ProbeHost diagnostics | 把 COM 探测失败当成菜单管理失败。 |
| 全局搜索搜得到但不跳转 | `ShellViewModel.cs`、`GlobalSearchNavigationFilterService.cs`、目标页面 ViewModel | `frontend-debug.log` 中 `GlobalSearchOpenResult`、`GlobalSearchFilterRequested` | 只导航，没消费 pending filter 或 target page type 不匹配。 |
| 主题启动时不生效 | `FrontendThemeService.cs`、`SettingsPageViewModel.cs` | `frontend-debug.log` 中 `ThemeStartupInitialize` | 设置服务没加载、System watcher 状态和显式主题混用。 |
| 传统 Shell Extension 开关影响其它分类 | `ContextMenuRegistryCatalog.cs`、`ContextMenuStateStore.cs` | `backend.log` 中 `ClassicShellExtensionMove*`、`ClassicShellExtensionDuplicateAutoRepair*` | 误把注册项状态当作 CLSID 全局 Blocked 状态；确认 active/disabled sibling container、key 最后写入时间和 frontend SID hive。 |
