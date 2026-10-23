# Windows 11 新右键菜单实现说明

Win11 页面按应用管理菜单，经新增的 `SetWin11ApplicationEnabled` 进入 `ContextMenuRegistryCatalog.SetWindows11EnabledAsync`。同 package family 的所有声明由后端在一个持久状态 gate 内准备、备份、写入、回读并保存，任何一步失败均条件回滚已写入的值。前端只发一个应用操作请求，不循环发送多个开关。

原 `SetWin11BlockedItem` / `RemoveWin11BlockedItem` 仍是单 CLSID（或单 System CommandStore）操作，审核、隔离和纠偏也维持单项控制域。新增命令追加在 PipeCommand 末尾，旧服务不认识时拒绝整个请求，前端提示在设置中修复服务，不 fallback 成部分成功的旧式循环。 对 v1.7.10 及以前服务的旧式 missing-required-data 响应，前端仅在新应用命令上解释为需要更新服务；当维护者明确停止支持这类旧服务后，可移除此文案桥接（#114）。
## 1. Windows 11 新菜单和传统菜单的区别

Windows 11 新右键菜单不等同于传统 `shell` / `shellex\ContextMenuHandlers`。当前实现区分两类可管理的新菜单来源：

- `PackagedCom`：菜单声明和 COM 信息来自前端用户已安装 AppX / MSIX package 的 manifest，禁用状态通过 Shell Extensions blocked list 表达。
- `SystemCommandStore`：Windows 内置命令来自 `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell`，例如 `Windows.SendToMyPhone`、`Windows.Share`。这类命令不是 packaged COM 菜单项，不能按 GUID Block / GUID Lock 处理。

传统菜单可以在 `ContextMenuRegistryCatalog` 的 `MonitoredRoots` 中扫描；Windows 11 新菜单由 `Windows11ContextMenuCatalog` 单独枚举，并通过 `IsWindows11ContextMenu = true` 标记。不要把 packaged COM、System CommandStore 和 GUID Block 混为同一种禁用模型。

## 2. 扫描来源

当前后端扫描主要基于以下来源。package full name 会在读取 manifest 前去重；单个 package 缺少 manifest、XML 损坏、CLSID 无效或 COM class 无法与 verb CLSID 关联时只跳过该声明/package，不中断整个 snapshot：

| 来源 | 作用 |
| --- | --- |
| `PackageManager.FindPackagesForUser(frontendSid)` | 按前端用户 SID 枚举该用户实际安装的 AppX / MSIX package；这是 packaged menu 的权威候选来源，不使用服务进程的合并 HKCR 代替用户 package context。 |
| `AppxManifest.xml` | package 主 manifest。 |
| `AppxMetadata\AppxBundleManifest.xml` | 主 manifest 不存在时的 fallback。 |
| `Extension Category="windows.fileExplorerContextMenus"` / `FileExplorerContextMenus` | manifest 中声明 Explorer context menu verb 的位置。解析按 AppX manifest namespace family、`Category` 和元素 `LocalName` 定位，兼容 `desktop4` 等版本化 namespace。 |
| `Extension Category="windows.comServer"` / `ComServer` | manifest 中声明 COM server 和 class 的位置。兼容未版本化 `com` 及 `com4` 等版本化 namespace，当前支持 `SurrogateServer` 和 `ExeServer`。 |
| `ContextTypes` | 由 manifest 的 item type 映射到项目的 `ContextMenuCategory`。 |
| `HKLM\...\Explorer\CommandStore\shell` | 读取明确支持的 Windows 内置系统命令，当前包括 `Windows.SendToMyPhone` 和 `Windows.Share`；命令还必须带有 `MUIVerb`、`ExplorerCommandHandler`、`command` 子键或其它 shell 命令元数据。不要把所有 `Windows.*` CommandStore 项都放进 Win11 页面。 |

manifest 解析是 best-effort。缺少命名空间、manifest 不存在、XML 结构变化或包数据异常时，当前代码会跳过或记录 warning，而不是让整个 snapshot 失败。

## 3. Windows11ContextMenuCatalog

`Windows11ContextMenuCatalog` 位于 `ContextMenuMgr.Backend/Services/Windows11ContextMenuCatalog.cs`。当前流程如下：

```text
检查 Windows 版本 >= 10.0.22000
-> 获取 frontend user SID
-> PackageManager.FindPackagesForUser(frontendSid)
-> 按 package full name 去重并取得安装路径
-> 读取 AppxManifest.xml 或 AppxBundleManifest.xml
-> PackagedContextMenuDiscovery 按 Category + LocalName 解析 FileExplorerContextMenus
-> 解析版本无关的 ComServer / SurrogateServer / ExeServer
-> 匹配 CLSID
-> 根据 ContextTypes 映射 ContextMenuCategory
-> 根据 blocked list 判断 IsEnabled
-> 创建 ContextMenuEntry
```

`PackagedContextMenuDiscovery` 位于 `ContextMenuMgr.Backend/Services/PackagedContextMenuDiscovery.cs`，负责 SID-scoped package 枚举、manifest 加载、verb 与 COM class 的 CLSID 关联。`Windows11ContextMenuCatalog` 通过 `PackagedContextMenuScanCache` 对同一 SID 的包发现结果做 30 秒缓存；并发快照复用正在进行的扫描，单个调用方超时不会取消共享扫描。blocked list 不进入缓存，每次投影仍按当前用户 SID 重新读取。常规快照与手动刷新重新建立 registry 引用索引；手动开关可复用最近 30 秒同 SID 的声明及引用元数据，不因此缓存开关状态，也不强制失效 package cache。后台自动隔离/纠偏仍使用常规扫描的影响证据。新安装/卸载的 package 最多延迟约 30 秒进入常规快照；已有手动操作发现缓存可能再沿用至其 30 秒到期。单包读取异常按轮次汇总为 `PackagedContextMenuPackagesSkipped`，记录总数及少量带 package、阶段、HRESULT 的样本，避免异常包刷满日志。

手动开关在 regular baseline 尚未建立时先完整采纳当前常规菜单；已有明确 baseline 时只构建 Win11 mutation snapshot，不扫描经典菜单。写后复用声明元数据重新打开 native blocked list / CommandStore 投影，并保存所有受影响声明。成功响应 `Items` 返回已经回读且持久化的关联项，前端合并这些结果，无需再次全量查询。响应丢失、不完整或失败时才做额外查询，不重复提交。整个状态读、改、存仍处于原持久 gate 内；局部操作不清理经典菜单 baseline。`Win11ToggleCompleted` 记录后端总耗时。

生成的 packaged COM `ContextMenuEntry` 使用 `win11|{clsid}|{category}|{packageFamilyName}` 形态的 `Id`，`RegistryPath` 指向逻辑上的 `PackagedCom\Package\...\Class\{CLSID}`，`BackendRegistryPath` 指向当前用户的 blocked list。契约保留 package full/family/display/publisher/install metadata、manifest context type、Verb ID、handler CLSID、COM server/class display metadata 和 manifest 明确声明的 server path。manifest 没有建立 server path 时 `FilePath` 保持为空，不用 package 目录伪造 COM 路径。manifest display metadata 只是诊断/展示候选，不等于 `IExplorerCommand::GetTitle()` 在 Explorer 中动态返回的最终标题。

packaged COM 条目的 `BackendRegistryPath` 是共享 blocked list，不能当作单条菜单注册表键删除。普通删除命令对 Win11 条目会拒绝；待审核界面也不提供这类项目的“移除”，应使用“保持禁用”或在 Windows 中卸载对应 package。

System CommandStore `ContextMenuEntry` 使用 `win11-system|{commandKey}` 形态的 `Id`，`KeyName` 是命令名（如 `Windows.SendToMyPhone`），`RegistryPath` / `BackendRegistryPath` 指向 HKLM CommandStore 命令键，`HandlerClsid` 来自 `ExplorerCommandHandler` 或等价的 GUID 值，`Windows11SourceKind = SystemCommandStore`。显示名通过 `ShellMetadataResolver.ResolveVerbDisplayName` 解析 `MUIVerb` 等资源字符串。

## 4. 启用 / 禁用模型

Win11 新菜单通过 blocked list 启用或禁用：

| 范围 | 路径 | 说明 |
| --- | --- | --- |
| 机器级 | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked` | 对所有用户生效；页面恢复时说明影响并由用户确认，然后只清理所选应用的值。 |
| 用户级 | `HKEY_USERS\<sid>\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked` | 对指定用户生效，必须带正确用户 SID。 |

禁用在当前用户 CLSID 下创建本程序的版本化标记 `ContextMenuMgrPlus.Win11Blocked.v1|<SID>|<CLSID>`；已有第三方禁用值已经满足关闭目标，保留原值，不接管。仅本程序重复别名可在关闭操作中归一化。启用是用户明确的恢复意图：自动处理所选菜单的旧版友好名称、无花括号/重复 GUID 名称和不同 value kind，不再让用户打开注册表或停止服务。

`Windows11BlockMutation` 准备同一 CLSID 的用户/可选机器级计划，复用 RegistryValueMutationTransaction 的单值条件事务。恢复前把准确的物理路径、SID、CLSID、原始 value 名称/类型/数据写入对应状态的 `Windows11BlockBackups`，该恢复日志不参与 identity/Added/Modified 比较；备份保存失败则不修改任何值。备份采用增量属性，兼容当前 schema 3，重复原始数据不重复积累。它不因普通状态保存而被清空，显式重置状态库仍按已有规则清除。

应用操作先准备所有目标并保存备份，再逐项写入；native registry view 重新打开核对全部值及所有声明后，才提交开关状态和通知 monitor。失败则逆序条件回滚，保留外部并发改值。机器级恢复只在用户确认影响所有用户后传 `UnblockMachine=true`，普通禁用仍只写当前用户；`BlockMachine=true` 拒绝。不会清空整个 Blocked 键，也不会把 GUID Block 编辑器作为恢复入口。读扫描不自动删除未知来源的策略。

索引不完整或传统菜单共用只影响后台自动隔离/纠偏，不能据此锁死用户开关。手动操作按用户明确选择执行；存在其他应用共用时用应用名称说明真实影响，由用户一次确认。注册表回读依旧不是 Explorer 真实菜单验证，也不是与第三方程序共享的原子锁。
System CommandStore 项不使用 blocked list。`Windows.SendToMyPhone`、`Windows.Share` 等命令通过 `ShellVerbVisibility` 的普通 shell verb 可见性值启用 / 禁用：

- snapshot 使用 `ShellVerbVisibility.IsEnabled(commandKey)` 判断状态；
- 禁用使用 `ShellVerbVisibility.SetEnabled(commandKey, registryPath, false)`；
- 启用使用 `ShellVerbVisibility.SetEnabled(commandKey, registryPath, true)`。

运行时新增项隔离及已知禁用项的静默纠偏也按此路径处理 System CommandStore；不可写的受保护命令标记为不可切换，不进入自动禁用尝试。Packaged COM 项仍按用户 blocked list 处理。

如果 HKLM CommandStore 命令键受 Windows 保护，后端不接管所有权、不修改 ACL、不使用 TrustedInstaller hack，而是返回结构化失败：`This Windows 11 system command is protected by Windows and cannot be safely modified by ContextMenuMgr.` 前端应显示受保护、不可切换状态，而不是通用权限错误。

Win11 页面中的菜单开关更改后，关闭并重新打开右键菜单查看结果；不设置 Explorer 待重启状态，也不要求重启或重新登录。此行为与下面的全局经典菜单设置分开。

## 4.1 GUID Block / GUID Lock 风险

`Other Rules / GuidBlock` 写的是 `Shell Extensions\Blocked`，适用于明确要阻止的 shell extension CLSID。它不是 Windows 11 内置 CommandStore 命令的推荐管理方式。

不要把 `Windows.SendToMyPhone` / `Windows.Share` 的 `ExplorerCommandHandler` GUID 加入 GUID Lock。Windows 11 内置命令可能共享 Explorer 命令处理器或依赖系统级处理链，阻止错误 GUID 可能导致 Windows 11 现代右键菜单整体不可用，Explorer 回退到经典菜单。对这些命令应使用 Win11 页面中的 `System Command / CommandStore` 项；如果命令受保护，则保持不可切换并提示原因。

## 4.2 全局恢复经典右键菜单设置

设置页“增强”卡片中的“禁用 Win11 新版右键菜单”不是 packaged COM 条目的 blocked list，也不是传统 `shell` / `shellex` 开关。它使用 Windows 11 常见的每用户注册表 tweak：

| 状态 | 注册表行为 |
| --- | --- |
| 启用经典菜单 / 禁用新版精简菜单 | 创建 `HKEY_USERS\<sid>\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32`，并把默认值设置为空字符串。 |
| 恢复 Windows 11 默认新版菜单 | 删除 `HKEY_USERS\<sid>\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}`。 |

该功能通过链路 A 进入 `Win11ClassicContextMenuService`。后端必须从 pipe 客户端解析前端用户上下文，并写 `HKEY_USERS\<sid>`；不要写服务进程的 `Registry.CurrentUser`，不要写 HKLM，也不要复用 `Windows11BlocksService`。更改后需要重启 Explorer 或重新登录才会可靠生效，当前实现不会在切换时自动重启 Explorer。

因为该功能的开发需求和对应 registry tweak 行为都明确要求重启 Explorer 或重新登录后才会可靠生效，前端设置页在写入成功后只调用 `ExplorerRestartStateService.MarkRequired()`，让主窗口顶部已有的全局“重启资源管理器”按钮显示出来。不要在设置页新增独立重启按钮，也不要在切换开关时直接调用 `RestartExplorer` pipe 命令；真正的重启动作仍由用户点击全局按钮触发，成功后由 `ShellViewModel` 清除 `NeedsRestart` 状态。

## 5. 为什么 userContext 必须正确

Win11 新菜单的 snapshot 和开关都依赖用户上下文：

| 场景 | userContext 错误的后果 |
| --- | --- |
| snapshot | `IsEnabled` 可能读取错误用户 hive，刷新后显示状态错误。 |
| 禁用 | blocked value 写到错误 SID 下，前端看似成功但 Explorer 对当前用户不生效。 |
| 恢复 | 从错误 SID 删除 value，当前用户仍被禁用。 |
| 审核 | 待审核项和逻辑 identity 可能与真实用户状态不一致。 |

Win11 专用查询和开关只接受认证 Pipe 用户，不 fallback 到另一交互用户。常规 GetSnapshot 同样解析认证用户；通用 SetEnabled / ApplyDecision 的 Win11 请求也禁用 fallback。catalog 缺少上下文时不枚举用户菜单，写入要求有效 account SID 和已加载 hive。SID 决定 registry scope，SessionId 不参与 blocked value mutation。

## 6. 前端服务和页面

前端由 `ContextMenuMgr.Frontend/Services/Windows11ContextMenuService.cs` 和 `Windows11ContextMenuPageViewModel.cs` 负责展示与操作：

| 组件 | 职责 |
| --- | --- |
| `Windows11ContextMenuService` | 通过后端 snapshot 构建 `Windows11ContextMenuItemDefinition`，维护 `CurrentItems`，调用 `SetWin11BlockedItemAsync` 和 `RemoveWin11BlockedItemAsync`。 |
| `Windows11ContextMenuPageViewModel` | 展示条目、分组、筛选、刷新、处理全局搜索跳转。 |
| `CurrentItems` | 前端缓存的 Win11 条目池，避免页面每次筛选都访问后端。 |
| `ContextMenuSearchMatcher` | 全局搜索和页面筛选共用匹配逻辑。 |
| `GlobalSearchNavigationFilterService` | 搜索结果跳转到 Win11 页面后设置页面筛选文本和目标项。 |

`Windows11ContextMenuPageViewModel` 和导航 Page 在 DI 中始终注册，避免 Win10 或其它不支持环境被意外导航到该页面时因服务解析失败而崩溃。正常导航项仍由 `ShellViewModel.IsWindows11ContextMenuSupported` 按 Windows 版本隐藏；`Windows11ContextMenuService.RefreshAsync` / `EnsureLoadedAsync` 在不支持时直接返回。前端不再用 `HKCR\PackagedCom` 是否存在作为支持门槛，否则会在后端能够从用户 package context 发现项目时跳过 snapshot。

Win11 页面筛选是前端内存筛选。只有刷新 snapshot 或执行开关操作时才需要访问后端。

Win11 菜单项支持与传统菜单一致的本地用户备注：`Windows11ContextMenuItemViewModel` 暴露 `UserNote` / `HasUserNote` / `UserNoteDisplay` / `EditUserNoteCommand`，备注通过 `FrontendSettingsService.GetContextMenuItemNote` / `UpdateContextMenuItemNote` 持久化，以 `Windows11ContextMenuItemDefinition.Id` 为键，不写入注册表、不访问后端。备注内容参与页面筛选与全局搜索匹配；全局搜索候选在 `FrontendSettingsService.SettingsChanged`（备注编辑触发）时重建。

## 7. 常见坑

| 坑 | 正确处理 |
| --- | --- |
| 用传统 `shell` / `shellex` 策略禁用 Win11 新菜单 | 使用 blocked list。 |
| 在 snapshot 中丢掉 userContext | `IsEnabled` 会读错用户 hive。 |
| 禁用写到服务 `HKCU` | 写 `HKEY_USERS\<sid>\...\Blocked`。 |
| 假设一个 CLSID 只对应一个 context type | manifest 中一个 CLSID 可能声明多个类型，项目会映射到多个 `ContextMenuCategory`。 |
| 假设 Win11 项一定有普通 DLL 图标 | packaged COM 的图标和 server 路径解析都是 best-effort。 |
| 把 manifest 解析失败当严重错误 | AppxManifest 解析是 best-effort，应记录并跳过异常 package。 |


## 8. #114 的诊断、页面与验收

Current evidence is insufficient to determine the root cause. 用户报告删除 Terminal/Notepad 的用户 Blocked 值后恢复，但尚不能证明共享 CLSID、Explorer 版本或第三方依赖中哪一项是根因。诊断只写日志，不把这类未证实的风险变成页面上成段警告或不可用开关。

`Windows11ClsidImpactIndex` 保留 Package full/family/display identity、Verb、ContextType、COM server path 以及物理 HKLM / frontend HKU Classes/CommandStore 引用。遍历顶层 class/ProgID、SystemFileAssociations、CLSID、Background、shell、shellex、ExplorerCommandHandler、DelegateExecute 和 DropTarget；不加载 COM。shell 深度最多 8 层，最多读取 50000 个存在子键，诊断样本最多 8 条。`IsComplete` 表示这些发现步骤完成，不证明全部运行时依赖已穷尽。共享/不完整证据暂停自动禁用，用户手动管理仍可用。

不同 family 的相同 CLSID/category 保留独立声明 Id，同 family 共存版本的 full name 来源保留在引用元数据。v1.7.10 及以前无 family 的状态按原 package family 迁移，保留 desired/pending；先前被覆盖的来源采纳当前物理状态。只读 snapshot 不提交迁移。旧 Id 备注仍兼容读取；在维护者明确停止支持旧格式升级前保留此桥接。

Win11 页面固定使用详细视图，展示应用名、适用位置、发布者、应用标识、文件位置、备注入口和开关，缺少的字段隐藏。页面不提供精简/详细切换，也不读取或修改经典菜单的 `FrontendSettings.ContextMenuListViewMode`。同 family 合并一行，混合状态显示“部分开启”并提供“全部开启 / 全部关闭”两个直接动作。系统命令保持独立。影响索引仅保留在 `Win11TogglePlan` / `Win11ApplicationMutationPlan` / `Win11BlockedMutationPlan` / `Win11BlockedReadBack` / `Win11BlockedPhysicalVerified` / 回滚日志，标记 `ExplorerValidated=False`。跨应用/“显示更多选项”共用或恢复所有用户设置时，才显示一次普通语言的实际影响确认。

后端查询与 mutation 共用持久 gate；前端串行操作和刷新（写入等待上限 45 秒、查询等待上限 20 秒），核对应用内每个目标，超时不自动重复写入。上限不是操作延时。查询失败、目标消失或超时后未确认全部目标时保留当前显示状态，给出普通语言的状态与刷新入口。成功刷新解除不确定状态；Win11 开关不触发 Explorer 重启入口。

参考项目同样采用 Blocked；独立 MenuCapture 的动态探测不能直接移入服务。此次不加载第三方 DLL、不增加探测进程，未来动态探测仍须复用 ProbeHost 链路 D。

### 人工验收

1. 正常进入页面，检查每个应用只出现一行，固定显示发布者、应用标识和文件位置等详细信息，缺失字段隐藏，页头没有视图切换控件。切换经典菜单的精简/详细模式后再进入 Win11 页，确认详细信息始终显示，经典菜单偏好保持原值。验证浅色、深色及窄窗口下开关与备注入口可用。
2. 在可恢复 VM 中，对旧版禁用记录、不同类型及重复 GUID 别名直接开启应用。无需 Regedit 或停止服务；核对已自动保存准确备份、目标值已清除、刷新和状态库均为开启，不相关菜单保持原样。
3. 一个应用声明多个 CLSID 时只发送一个请求，核对全部目标；人为让后一个目标写入/回读或最终保存失败，确认前面已修改目标回滚。备份写入失败必须在首次 registry mutation 前停止；外部并发改值不得覆盖。
4. 对机器级禁用确认只提示一次普通语言说明；取消不写入，确认后只清理所选应用。真正受 Windows 保护的系统命令仍显示不可更改，无 ACL/所有权 hack。
5. 对 #114 在固定文件、目录、背景上下文下进行单 CLSID 的底层 A/B，再比较软件的应用操作；记录 OS build、目标清单、Before/After、状态保存，重新打开真实新旧菜单核对，无需重启 Explorer。测量点击到完成的耗时，检查成功时只有一个操作请求而无额外快照，且不出现待重启提示。物理回读/隔离测试不替代这个现场验证。
6. 验证响应丢失只回读不重复提交；不同应用不互相遮挡状态；服务旧版本拒绝新应用命令且没有部分写入。真实多用户、SCM、x86-on-x64 仍需部署验收。
