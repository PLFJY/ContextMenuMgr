# Windows 11 新右键菜单实现说明

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

`PackagedContextMenuDiscovery` 位于 `ContextMenuMgr.Backend/Services/PackagedContextMenuDiscovery.cs`，负责 SID-scoped package 枚举、manifest 加载、verb 与 COM class 的 CLSID 关联。`Windows11ContextMenuCatalog` 只负责分类映射、blocked 状态和 `ContextMenuEntry` 投影。

生成的 packaged COM `ContextMenuEntry` 使用 `win11|{clsid}|{category}` 形态的 `Id`，`RegistryPath` 指向逻辑上的 `PackagedCom\Package\...\Class\{CLSID}`，`BackendRegistryPath` 指向当前用户的 blocked list。契约保留 package full/family/display/publisher/install metadata、manifest context type、Verb ID、handler CLSID、COM server/class display metadata 和 manifest 明确声明的 server path。manifest 没有建立 server path 时 `FilePath` 保持为空，不用 package 目录伪造 COM 路径。manifest display metadata 只是诊断/展示候选，不等于 `IExplorerCommand::GetTitle()` 在 Explorer 中动态返回的最终标题。

System CommandStore `ContextMenuEntry` 使用 `win11-system|{commandKey}` 形态的 `Id`，`KeyName` 是命令名（如 `Windows.SendToMyPhone`），`RegistryPath` / `BackendRegistryPath` 指向 HKLM CommandStore 命令键，`HandlerClsid` 来自 `ExplorerCommandHandler` 或等价的 GUID 值，`Windows11SourceKind = SystemCommandStore`。显示名通过 `ShellMetadataResolver.ResolveVerbDisplayName` 解析 `MUIVerb` 等资源字符串。

## 4. 启用 / 禁用模型

Win11 新菜单通过 blocked list 启用或禁用：

| 范围 | 路径 | 说明 |
| --- | --- | --- |
| 机器级 | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked` | 对所有用户生效，当前 `Windows11BlocksService` 支持读写。 |
| 用户级 | `HKEY_USERS\<sid>\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked` | 对指定用户生效，必须带正确用户 SID。 |

`SetEnabled` 的本质是写入或删除 CLSID value：禁用时写入 CLSID value，启用时删除该 value。机器级 blocked 优先导致禁用；即使用户级没有 blocked value，只要 HKLM blocked list 存在该 CLSID，snapshot 仍显示禁用，并通过 `ContextMenuEntry.IsMachineBlocked` 传到前端。前端会禁用用户级开关，避免把“删除 HKU value”误报为能够覆盖 HKLM block。

System CommandStore 项不使用 blocked list。`Windows.SendToMyPhone`、`Windows.Share` 等命令通过 `ShellVerbVisibility` 的普通 shell verb 可见性值启用 / 禁用：

- snapshot 使用 `ShellVerbVisibility.IsEnabled(commandKey)` 判断状态；
- 禁用使用 `ShellVerbVisibility.SetEnabled(commandKey, registryPath, false)`；
- 启用使用 `ShellVerbVisibility.SetEnabled(commandKey, registryPath, true)`。

如果 HKLM CommandStore 命令键受 Windows 保护，后端不接管所有权、不修改 ACL、不使用 TrustedInstaller hack，而是返回结构化失败：`This Windows 11 system command is protected by Windows and cannot be safely modified by ContextMenuMgr.` 前端应显示受保护、不可切换状态，而不是通用权限错误。

更改 packaged COM blocked list 或 System CommandStore 可见性后，都需要重启 Explorer 或重新登录才会可靠反映在实际右键菜单中。

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

`Windows11ContextMenuCatalog` 在缺少 userContext 时有交互式 SID fallback，但代码注释已经表明这不应是主路径。正常 runtime 请求应由 `NamedPipeBackendServer` 解析前端用户上下文并传入。

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
