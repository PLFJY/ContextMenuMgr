# SpecialMenu 实现说明

## 为什么 SpecialMenu 不是普通右键菜单

SpecialMenu 指当前代码中由 `SpecialMenuService` 管理、但不适合放进 `ContextMenuRegistryCatalog` 的菜单面。它们不完全遵循传统 `shell` / `shellex\ContextMenuHandlers` 模型：

| SpecialMenu | 为什么特殊 |
| --- | --- |
| `ShellNew` | “新建”菜单来自扩展名下的 `ShellNew` 子键，同时还受 Explorer ShellNew order key 和排序值影响。 |
| `SendTo` | “发送到”菜单主要来自用户 profile 下的文件系统目录。 |
| `WinX` | Win+X 菜单来自用户 `LocalAppData` 下的分组目录、`.lnk` 文件和 hash。 |
| `OpenWith` | “打开方式”候选来自 Applications 及独立关联来源；本页仅修改已验证的 Applications 注册和既有 `NoUseStoreOpenWith` 策略，不是普通文件类型 verb。 |
| `DragDrop` | 右键拖放默认效果和 handler 配置不是普通菜单项。 |
| `CommandStore` | Explorer command store 是另一套命令注册表模型。 |
| `GuidBlock` | 通过 GUID blocked list 屏蔽对象，不是普通 verb。 |
| `InternetExplorer` | IE MenuExt 是旧式浏览器扩展入口。 |

因此不要用 `ContextMenuRegistryCatalog` 的普通开关、删除、恢复逻辑直接处理 SpecialMenu，除非代码已经明确把某个路径泛化。

## SpecialMenuService 总览

`SpecialMenuService` 位于 `ContextMenuMgr.Backend/Services/SpecialMenuService.cs`。当前 `SpecialMenuKind` 定义在 `ContextMenuMgr.Contracts/SpecialMenuContracts.cs`：

| 类型 | 主要数据位置 | 用户上下文要求 |
| --- | --- | --- |
| `ShellNew` | `Software\Classes\<.ext>\ShellNew` 和 Explorer ShellNew order key | 需要用户 SID，排序和用户级 Classes 要写 `HKEY_USERS\<SID>`。 |
| `SendTo` | `%APPDATA%\Microsoft\Windows\SendTo` | 需要用户 profile path / RoamingAppData。 |
| `WinX` | `%LOCALAPPDATA%\Microsoft\Windows\WinX` | 需要用户 LocalAppData。 |
| `OpenWith` | 用户 / 机器 Classes `Applications\<app>\shell\<verb>\command`，以及 Explorer `NoUseStoreOpenWith` policy | 需要用户 SID；新增项写 `HKEY_USERS\<SID>\Software\Classes\Applications`，枚举同时读取 HKU 和 HKLM。 |
| `DragDrop` | 相关 Classes 注册表路径 | 当前实现可在服务侧处理，仍需谨慎区分用户级与机器级。 |
| `CommandStore` | Explorer CommandStore 注册表路径 | 不等同于普通右键菜单。 |
| `GuidBlock` | GUID blocked list | 用于屏蔽指定 GUID。 |
| `InternetExplorer` | IE MenuExt 注册表路径 | 当前按执行进程 HKCU，未实现前端 SID 路由；服务模式可能指向 SYSTEM，不能承诺交互用户覆盖。 |

前端主要由 `SpecialMenuPageViewModel` 组装 `PipeRequest`，后端由 `NamedPipeBackendServer` 分发到 `SpecialMenuService`。

## 专用责任文档

ShellNew 的发现、排序、ACL、恢复和三态 UI 统一见 [ShellNew](shellnew.md)；Applications 注册、只读关联来源及显式持久保护统一见 [OpenWith](openwith.md)。Registry Write Protection 统一见[注册表模型](registry-model.md#registry-write-protection)。

## SendTo

SendTo 菜单来自用户 profile 下的目录，当前代码通过 `BackendUserContext.GetSendToPath()` 定位到 RoamingAppData 的 `Microsoft\Windows\SendTo`。

| 行为 | 当前实现倾向 |
| --- | --- |
| 枚举 | 读取 SendTo 目录中的文件和快捷方式。 |
| 启用 / 禁用 | 通过 hidden 属性等文件系统状态表达。 |
| 删除 / 恢复 | 使用 `.deleted` 机制进行软删除和恢复。 |
| 编辑 | 更新 `.lnk` 的目标、参数、工作目录、图标等。 |

除了用户 SendTo 目录中的真实文件，快照还会追加 Explorer 动态生成的当前可用可移动磁盘目标，例如 USB 盘。此类项来自 `DriveInfo.GetDrives()` 中 `DriveType.Removable` 且 `IsReady` 的驱动器，使用 `sendto:drive:X` 形式的稳定 id，并通过 metadata 标记为 Explorer-generated dynamic target。它们不对应 SendTo 目录内的文件，因此 `CanEdit=false`、`CanDelete=false`，前端不显示开关，后端也不会对它们执行创建、删除、编辑或 hidden 属性切换。

SendTo 是文件系统菜单，不是 registry-only 功能。没有正确 profile path 时，服务可能改到错误用户目录或找不到目标。

## WinX

WinX 菜单来自用户 `LocalAppData` 下的 `Microsoft\Windows\WinX` 目录。它包含分组、排序、`.lnk` 文件和 hash。

| 行为 | 当前实现倾向 |
| --- | --- |
| 分组 | 以 group 目录组织。 |
| 排序 | 通过文件名前缀和分组位置处理，移动操作只移动/重命名 `.lnk` 和对应的 desktop.ini 本地化名称，不重写 WinX hash。 |
| `.lnk` | 条目是快捷方式，需要写目标、参数和工作目录。 |
| hash | `WinXHasher.HashLnk` 使用硬编码 PKEY 写入 Explorer 接受的 WinX 快捷方式 hash；新增快捷方式、恢复默认组、修改 target/arguments 后必须重新 hash。显示名、图标、RunAs 和移动排序不重新 hash。hasher 不允许使用空 target 计算 hash，并会记录 property-store/fallback/final target 与 arguments。 |
| 删除 | 当前 WinX 删除是直接删除用户 WinX 目录下的 `.lnk` 或分组目录，不使用 `.deleted` 软删除；Undo/Purge 对 WinX 已禁用，直到有持久 manifest-based undo 模型。 |
| 恢复默认 | 禁止恢复整个 WinX 根目录；只允许恢复单个 group，恢复前会把现有 group 移到 `.backup`，复制失败时回滚。`.deleted` 和 `.backup` 不作为普通 WinX group 枚举。 |

WinX 修改需要用户上下文，因为不同用户有不同的 LocalAppData 和 WinX 配置。

WinX 快捷方式的创建、更新和读取由后端 `WinXShortcutFile` 使用原生
`IShellLinkW` / `IPersistFile` 完成。每次操作在短生命周期 STA 线程中显式初始化
COM，不调用 `IShellLink.Resolve`，也不使用 `WScript.Shell`。SendTo 当前仍保留原有
`ShortcutFile` 路径，以限制改动范围。

创建 WinX 条目时，`backend.log` 会记录 `WinXCreateStart`、
`WinXShortcutWriteStart/End`、`WinXDesktopIniWriteStart/End`、
`WinXHashStart/End` 和 `WinXCreateEnd`。若创建失败，`WinXCreateFailed` 会记录失败
阶段；COM 异常同时记录 HRESULT。

## 其它特殊入口

| 入口 | 当前实现与边界 |
| --- | --- |
| DragDrop | `SpecialMenuService` 管理 Classes 的拖放 handler / DefaultDropEffect；仍有服务 HKCR helper，不能宣称已实现所有前端用户 overlay |
| CommandStore | 读取 Explorer CommandStore；与传统父级 SubCommands 引用开关、Win11 白名单系统命令是不同入口，不由它们推导任意命令可安全修改 |
| GuidBlock | [GuidBlockMenuService](../ContextMenuMgr.Backend/Services/GuidBlockMenuService.cs) 独立管理机器级 GUID Block 创建/开关/删除；与 classic per-registration、Win11 用户 Blocked 语义不同 |
| InternetExplorer | 旧 `MenuExt` / `-MenuExt` 路径、创建/更新脚本信息；现有 HKCU 限制如上，不是现代浏览器扩展管理 |

## WPS 与 Microsoft Office 共存

WPS / Microsoft Office 共存保护不会在 ShellNew 或 OpenWith 普通页面追加 File Association / Icon 项。两套 Office 同时存在时，后端只通过待审核专用管道读取当前用户 `HKEY_USERS\<SID>\Software\Classes` 和 ShellNew order key，检测 WPS 注入的 `.pdfwpsshellnew`、`.pptxwpsaicreateshellnew` 等 ShellNew command 项，以及 WPS 对文档关联和图标的用户级覆盖。检测结果是 `special:wps-*` 合成待审核项，不通过 ShellNew ACL lock 修复，也不会自动删除 WPS key。首次运行或重置后，当前已存在的 WPS 合成项会作为已确认 baseline 保存，不进入待审核或外部变化；同时写入 `internal:baseline:wps-office:v1`，即使当前检测结果为零项也要标记扫描已经完成。建立 WPS baseline 后首次出现的新合成项才进入待审核，常规菜单 baseline 不能影响这一判断。

[OfficeSuiteCoexistenceDetector](../ContextMenuMgr.Backend/Services/OfficeSuiteCoexistenceDetector.cs) 与 catalog 专用 WPS Pipe 路径负责检测、持久状态与审核；不是 Applications 注册监控。`GetOfficeSuiteCoexistenceStatus` 提供状态及受保护扩展名，`GetWpsOfficePendingApprovals` 提供审核项；`SetDocumentIconProvider` 仅写前端 HKU Office ProgID `DefaultIcon`，成功后确认本次合成项并通知 Shell。

仅 ShellNew 页面显示 Microsoft Office/WPS 图标来源选择，OpenWith 不展示默认应用 owner 或切换器。处置不写 UserChoice/Hash，不自动删 WPS key；有效默认关联变化与显示图标是不同机制。baseline/离线规则只在[状态与监控](state-and-monitoring.md)维护。

## 失败、验证与维护

SendTo/WinX 必须来自 `BackendUserContext` 的 profile/AppData，不能由服务环境变量猜前端目录；SID 不等于桌面 Session。文件系统操作也有部分失败，日志或 `.lnk` 生成不等于 Explorer 接受。

Windows 手验分别检查 SendTo hidden/soft-delete/恢复及动态磁盘只读；WinX 各 group 创建、移动、target/arguments 更改后 hash、显示名/图标修改、恢复单 group 的 backup/回滚和真实 Win+X。新版 Windows 可能额外筛选已 hash 快捷方式，实际可见性仍需验证。对 WPS/Office 共存与单独安装分别测首次/reset、后续注入、图标切换、审核/重启与两 SID 隔离。

变更 `SpecialMenuKind`、文件恢复策略、hash 或 WPS 检测/处置时更新本文及相关模型文档；不要在其它指南再复制 ShellNew/OpenWith 实现。
