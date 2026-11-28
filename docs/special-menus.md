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

`SpecialMenuService` 位于 `ContextMenuMgr.Core/Services/SpecialMenuService.cs`。当前 `SpecialMenuKind` 定义在 `ContextMenuMgr.Contracts/SpecialMenuContracts.cs`：

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

SpecialMenu 列表使用独立前端呈现缓存和稳定 Id 差量更新，缓存规则与动画见[前端](frontend-wpf-ui.md#列表缓存差量刷新与通知)。`GetSpecialMenuItemState` 用于缓存项操作前的原来源核对与迟到通知核对；SendTo/WinX 区分用户根不可访问和单项缺失，不能用吞异常的 File.Exists 判定删除。其它 kind 的定向核对复用所属范围发现，不扩为全局 classic/Win11 扫描。软件删除、恢复与授权记录仍按原有专有契约处理。

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
| GuidBlock | [GuidBlockMenuService](../ContextMenuMgr.Core/Services/GuidBlockMenuService.cs) 独立管理机器级 GUID Block 创建/开关/删除；与 classic per-registration、Win11 用户 Blocked 语义不同 |
| InternetExplorer | 旧 `MenuExt` / `-MenuExt` 路径、创建/更新脚本信息；现有 HKCU 限制如上，不是现代浏览器扩展管理 |

## WPS 与 Microsoft Office 共存

WPS / Microsoft Office 共存保护不会在 ShellNew 或 OpenWith 普通页面追加 File Association / Icon 项。两套 Office 同时存在时，后端只通过待审核专用管道读取当前用户 `HKEY_USERS\<SID>\Software\Classes` 和 ShellNew order key，检测 WPS 注入的 `.pdfwpsshellnew`、`.pptxwpsaicreateshellnew` 等 ShellNew command 项，以及 WPS 对文档关联和图标的用户级覆盖。检测结果是 `special:wps-*` 合成待审核项，不通过 ShellNew ACL lock 修复，也不会自动删除 WPS key。首次运行或重置后，当前已存在的 WPS 合成项会作为已确认 baseline 保存，不进入待审核或外部变化；同时写入 `internal:baseline:wps-office:v1`，即使当前检测结果为零项也要标记扫描已经完成。建立 WPS baseline 后首次出现的新合成项才进入待审核，常规菜单 baseline 不能影响这一判断。

[OfficeSuiteCoexistenceDetector](../ContextMenuMgr.Core/Services/OfficeSuiteCoexistenceDetector.cs) 与 catalog 专用 WPS Pipe 路径负责检测、持久状态与审核；不是 Applications 注册监控。GetOfficeSuiteCoexistenceStatus 提供共存/修复状态及受保护扩展名，GetWpsOfficePendingApprovals 提供审核项。

仅 ShellNew 页面提供“修复 WPS 文档图标覆盖”开关，替代原来的 Microsoft Office/WPS 图标来源单选。开启时，[DocumentIconRepairMutation](../ContextMenuMgr.Core/Services/DocumentIconRepairMutation.cs) 只捕获当前用户 Office ProgID 的 WPS DefaultIcon 覆盖，并在机器注册中确认仍有有效 Office 图标来源；先持久保存原始值及类型，再删除该用户覆盖值，让合并 Classes 自然继承机器 Office 图标。WPS 自身 ProgID、扩展名默认关联、UserChoice/Hash、机器注册与其它用户均不修改。因此选择 Office 默认打开时使用 Office 图标，选择 WPS 时仍使用 WPS 自己的图标；不再把 WPS 图标复制到 Office 名下，也不固定一份机器图标路径到用户注册。已有正确图标或其它自定义图标保持原样，无覆盖可修复时可开启且保存空恢复清单。

关闭时恢复该次开启前捕获的精确数据和类型；没有原始覆盖就不凭空创建 WPS 覆盖。恢复先核对全部目标：当前值必须仍是修复后的缺失值，或已经等于备份原值；发现其它新值、Office 注册已消失/改变到无法确认、SID 或目标不符时拒绝覆盖并保留恢复记录。所有目标限定在 Catalog 的 Office ProgID/DefaultIcon 白名单，按来源 RegistryView 重新开句柄读回。写入、恢复或最终状态保存失败时逆序条件回滚，保留第三方后续修改；失败写 WpsDocumentIconRollbackFailed，不盲重试。不删除 DefaultIcon 键中的其它值，恢复可能重建已消失的空键，不保证撤销空键本身。

启动/刷新同时读取真实 Office 图标与持久撤销记录：尚无用户选择时，已无 WPS 覆盖且存在可用 Office 图标就显示已修复；用户明确选择后以持久 Enabled 表示开关，Applied 另按实际图标核对。CanRestoreDocumentIcons 独立说明是否具备该用户开启前的原值；没有备份的已正常状态也允许关闭，关闭只保存 Enabled=false 偏好并保留现有图标，Tooltip 说明不会凭空写回旧 WPS 值；重启优先保留主动关闭选择，不用物理正常状态重新打开。有记录的关闭仍精确恢复原值；成功响应通过明确的 Enabled 目标确认操作完成；关闭后保留不含备份的偏好记录，不要求原本正常的图标变坏。再次开启对当前状态重新捕获备份和修复。Excel 的 .xltx/.xltm 使用实际的 Excel.Template/Excel.TemplateMacroEnabled，不把孤立的 Excel.Template.12/Excel.TemplateMacroEnabled.12 覆盖误认成现有文档的修复目标。开启时执行一次修复，不新增持续写回的保护策略；后续 WPS 再次覆盖或修复中断时保留恢复证据，状态只读核对并提示，不能把读取状态变成自动修改。原值保存、退出重启及中断后的恢复语义见[状态库](state-and-monitoring.md)。关闭后恢复 WPS 原覆盖属于用户明确意图，catalog 只确认本次图标合成项，不清其它关联或 ShellNew 审核。通知 Shell 和显示清理图标缓存入口不代表 Explorer 已完成刷新，真实效果须人工验收。

SetDocumentIconRepair 是追加的新 Pipe 命令，布尔目标与认证 SID 由既有用户上下文路由处理，不提升前端、不加载第三方扩展。旧 SetDocumentIconProvider 的枚举位置和请求字段仅用于解析已发布协议；维护决定是拒绝旧切换请求并要求更新前端，不继续保留会伪造 WPS 覆盖的旧实现。旧服务未提供修复状态或撤销能力字段时，前端禁用开关并显示更新/修复服务提示，不回退发送旧命令。

Office 原始图标来源不可用时，弹窗提供三语“修复／取消”。修复由前端在当前用户 Session 启动 ms-settings:defaultapps，提示用户为相应文档选择 Word/Excel/PowerPoint；Windows 要求在系统 UI 中确认默认应用，本程序不写 UserChoice/Hash，不通过提升、驱动、系统策略或模拟点击替用户设置默认应用。返回主窗口激活时只重新读取图标状态，不把设置进程启动、窗口激活或用户取消当成默认应用已改变，不自动重发失败的图标 mutation；图标来源仍缺失时需检查 Office 安装。系统支持边界见微软 [Windows app defaults platform](https://learn.microsoft.com/windows/apps/develop/windows-integration/default-apps-platform) 与 [Launch Windows Settings](https://learn.microsoft.com/windows/apps/develop/launch/launch-settings)。

## 失败、验证与维护

SendTo/WinX 必须来自 `BackendUserContext` 的 profile/AppData，不能由服务环境变量猜前端目录；SID 不等于桌面 Session。文件系统操作也有部分失败，日志或 `.lnk` 生成不等于 Explorer 接受。

Windows 手验分别检查 SendTo hidden/soft-delete/恢复及动态磁盘只读；WinX 各 group 创建、移动、target/arguments 更改后 hash、显示名/图标修改、恢复单 group 的 backup/回滚和真实 Win+X。新版 Windows 可能额外筛选已 hash 快捷方式，实际可见性仍需验证。对 WPS/Office 共存与单独安装分别测首次/reset、后续注入、图标切换、审核/重启与两 SID 隔离。

DocumentIconRepairMutationTests 使用隔离注册表及真实状态库覆盖删除覆盖后继承、跨实例精确撤销、关联/套件/用户边界、第三方冲突与条件回滚、部分中断及禁止重置丢失恢复记录；OfficeSuiteCoexistenceApprovalTests 保护只确认图标合成项。它们不能替代真实 Pipe 身份、WPS/Office 安装、前端开关和 Explorer 缓存验收。

变更 `SpecialMenuKind`、文件恢复策略、hash 或 WPS 检测/处置时更新本文及相关模型文档；不要在其它指南再复制 ShellNew/OpenWith 实现。
