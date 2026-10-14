# SpecialMenu 实现说明

## 1. 为什么 SpecialMenu 不是普通右键菜单

SpecialMenu 指当前代码中由 `SpecialMenuService` 管理、但不适合放进 `ContextMenuRegistryCatalog` 的菜单面。它们不完全遵循传统 `shell` / `shellex\ContextMenuHandlers` 模型：

| SpecialMenu | 为什么特殊 |
| --- | --- |
| `ShellNew` | “新建”菜单来自扩展名下的 `ShellNew` 子键，同时还受 Explorer ShellNew order key 和排序值影响。 |
| `SendTo` | “发送到”菜单主要来自用户 profile 下的文件系统目录。 |
| `WinX` | Win+X 菜单来自用户 `LocalAppData` 下的分组目录、`.lnk` 文件和 hash。 |
| `OpenWith` | “打开方式”菜单来自 Classes `Applications\<app>\shell\<verb>\command` 和 `NoOpenWith` / `NoUseStoreOpenWith` 策略，不是普通文件类型 verb。 |
| `DragDrop` | 右键拖放默认效果和 handler 配置不是普通菜单项。 |
| `CommandStore` | Explorer command store 是另一套命令注册表模型。 |
| `GuidBlock` | 通过 GUID blocked list 屏蔽对象，不是普通 verb。 |
| `InternetExplorer` | IE MenuExt 是旧式浏览器扩展入口。 |

因此不要用 `ContextMenuRegistryCatalog` 的普通开关、删除、恢复逻辑直接处理 SpecialMenu，除非代码已经明确把某个路径泛化。

## 2. SpecialMenuService 总览

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
| `InternetExplorer` | IE MenuExt 注册表路径 | 旧式功能，当前实现按执行进程的 `HKCU` 处理，属于 best-effort；后续若要面向前端用户，应补用户上下文路径。 |

前端主要由 `SpecialMenuPageViewModel` 组装 `PipeRequest`，后端由 `NamedPipeBackendServer` 分发到 `SpecialMenuService`。

## 3. ShellNew

ShellNew 是 SpecialMenu 中最容易踩坑的部分。

WPS / Microsoft Office 共存保护不会在 ShellNew 或 OpenWith 普通页面追加 File Association / Icon 项。两套 Office 同时存在时，后端只通过待审核专用管道读取当前用户 `HKEY_USERS\<SID>\Software\Classes` 和 ShellNew order key，检测 WPS 注入的 `.pdfwpsshellnew`、`.pptxwpsaicreateshellnew` 等 ShellNew command 项，以及 WPS 对文档关联和图标的用户级覆盖。检测结果是 `special:wps-*` 合成待审核项，不通过 ShellNew ACL lock 修复，也不会自动删除 WPS key。首次运行或重置后，当前已存在的 WPS 合成项会作为已确认 baseline 保存，不进入待审核或外部变化；同时写入 `internal:baseline:wps-office:v1`，即使当前检测结果为零项也要标记扫描已经完成。建立 WPS baseline 后首次出现的新合成项才进入待审核，常规菜单 baseline 不能影响这一判断。

| 概念 | 当前实现说明 |
| --- | --- |
| `ShellNew` 子键 | 位于扩展名 Classes 下，例如 `Software\Classes\.txt\ShellNew`。发现使用前端交互用户的合并 HKCR；修改仍使用明确的物理 HKU/HKLM 路径。 |
| 文件扩展名与新建项 | 每个扩展名可能对应一个“新建”菜单项，显示名按 Windows / BluePointLilac 语义解析。 |
| `NullFile` | 表示创建空文件。当前创建请求未提供 `DataText` 时倾向写入 `NullFile`。 |
| `Data` | 当前 `ShellNewCreateRequest` 支持 `DataText`，后端会写入二进制 `Data`。 |
| `Command` | `ShellNewUpdateRequest` 支持更新 `Command`。 |
| 创建请求字段 | 新建时会应用前端表单中的扩展名、显示名、图标路径、命令、数据文本和 `BeforeSeparator`；命令优先于 `Data`，没有命令和数据时写 `NullFile`。 |
| `FileName` / `Directory` | Windows ShellNew 支持这些创建方式；枚举会把 `NullFile`、`Data`、`FileName`、`Directory`、`Command` 视为有效 ShellNew 值。 |
| `Config\BeforeSeparator` | 当前更新请求支持 `BeforeSeparator`。 |
| Explorer ShellNew order key | `Software\Microsoft\Windows\CurrentVersion\Explorer\Discardable\PostSetup\ShellNew`，控制 Explorer 侧排序信息。 |
| Classes 排序值 | 排序会同时考虑扩展名、Explorer order key 和 Classes 相关数据。 |

ShellNew 快照发现通过 `RegOpenUserClassesRoot` 打开前端交互用户的合并 HKCR（`HKU\<SID>\Software\Classes` + `HKLM\SOFTWARE\Classes`），而不是服务进程的 `Registry.ClassesRoot` 或手工叠加两棵树。它从这个有效视图枚举扩展名、解析 ProgID、`ShellNew`/`-ShellNew`、显示名和图标；因此用户扩展名关联到机器 ProgID 的跨 hive 注册也能与 Explorer 一致。服务使用请求携带的 SessionId 取得 WTS 用户 token，以只读方式打开句柄；无法取得 token、profile 未加载或 API 失败时记录 `ShellNewMergedClassesOpenFailed`，不退回到 LocalSystem 的 HKCR。64 位系统请求 64 位 Classes view，以匹配 64 位 Explorer。

每个发现项同时解析实际提供 `ShellNew` 值的物理来源：优先用户 `HKU\<SID>\Software\Classes\...`，否则机器 `HKLM\SOFTWARE\Classes\...`，并将该物理路径作为 `RegistryPath` 和 `DisabledRegistryPath` 返回。合并视图只用于发现和有效元数据，绝不作为写入目标；开关、软删除、恢复、导出/定位和 ShellNew-local 编辑都使用此物理来源。扩展名/ProgID 的元数据来源可以不同于 ShellNew 的可变来源。

除传统物理注册外，快照还使用 `PackageManager.FindPackagesForUser(frontendSid)` 扫描该前台用户已安装 AppX/MSIX 包的 `AppxManifest.xml`。带有 `uap4:ShellNewFileName` 的 `FileType` 声明会作为 `PackagedShellNew` 返回，并携带 package full/family name、Application ID、FileTypeAssociation、原始 `FileType` 声明、`ShellNewFileName` / `ShellNewDisplayName` / `ShellNewCommandParameters` 元数据。它们在同一列表显示“应用提供 · 只读”，不参与 ShellNew order lock 或排序；`CanEdit`、`CanDelete`、`CanMove` 都为 false，服务端也拒绝开关、编辑、删除和恢复请求。前端为保持列表右侧布局，会保留禁用的开关、编辑和删除按钮，但不显示排序按钮。不得修改 `AppxManifest.xml`、安装目录、UserChoice、ProgID 或为此类项目制造注册表 ShellNew key；目前没有已验证的独立 Windows 开关。

创建 ShellNew 时，后端仍写入明确的用户 Classes 物理路径，而不能使用服务进程的 `Registry.ClassesRoot` 来验证 ProgId。解析顺序是用户 `HKU\<SID>\Software\Classes\.ext`、机器 `HKLM\SOFTWARE\Classes\.ext`、用户 `FileExts\.ext\UserChoice\ProgId`，再考虑 `OpenWithProgids`。ProgId 只在同一组用户 / 机器 Classes 根中存在时才视为有效；`UserChoice` 只读不写，不修改 hash，也不会为项目创建全局 fake ProgId。

ShellNew 显示名遵循 Windows / BluePointLilac 优先级：`ShellNew\MenuText` 仅在它是 `@...` 间接资源字符串且能解析为非空文本时优先；其次读取默认 ProgID 的 `FriendlyTypeName`；再读取默认 ProgID 的默认值；最后回退到扩展名。创建或编辑普通用户显示名时，ContextMenuMgr 不写纯文本 `ShellNew\MenuText`，而是写入前端用户覆盖层 `HKU\<SID>\Software\Classes\<ProgID>\FriendlyTypeName`。编辑显示名时会从实际可写的用户 ShellNew 覆盖键删除 `MenuText`，避免旧值继续压过 `FriendlyTypeName`。

即使扩展名没有出现在用户或机器 Classes 中，创建仍会继续写用户级 `HKU\<SID>\Software\Classes\.ext\ShellNew`。ProgId 是可选元数据：解析到有效 ProgId 时用于 per-user `FriendlyTypeName` 和返回项 metadata；没有 ProgId 时仍创建扩展名级 ShellNew，但普通显示名不会保存为纯文本 `MenuText`，刷新后可能回退到扩展名。`UserChoice` 始终只读，不写入、不修改 hash。

编辑 ShellNew-local 属性时，后端写入发现结果携带的实际物理 `ShellNew` 或 `-ShellNew` 路径；不会通过 HKCR 隐式选择 hive，也不会因为扫描而把机器注册移动到用户 hive。显示名覆盖仍写到用户 ProgID 的 `FriendlyTypeName`。

ShellNew 开关会在同一物理来源的 `ShellNew` 与 `-ShellNew` sibling key 之间移动注册。每次操作都会以当前实际存在的 sibling key 为准，而不信任前端旧快照中的 alternate-path metadata；移动后会确认源 key 已不存在、目标 key 已存在，再返回与物理路径一致的 `RegistryPath`、`Id`、`IsEnabled` 和 alternate path metadata。若两个 sibling 同时存在或都不存在，操作会失败而不会只修改返回 DTO 的启用状态。

当 WPS 与 Microsoft Office 共存时，新建菜单页的“文档图标提供方”开关通过后端 Pipe 写入当前前端用户 `HKEY_USERS\<SID>\Software\Classes` 下的 Office ProgID `DefaultIcon` 覆盖。这是用户主动选择；成功写入后，后端会确认对应的 `special:wps-office-icon:document-icons` 合成检测项，不能把这次写入重新送入待审核。来自 WPS 或其他程序的关联修改和 ShellNew 注入仍保持原有检测规则。

新建菜单排序锁开关在写入 ACL 成功后，会等待已经开始的旧快照刷新完成，再应用请求目标状态并启动一次新的刷新，避免自动刷新把操作前的锁状态写回界面。

排序复杂的原因是 Explorer “新建”菜单不是简单按注册表子键自然顺序显示。`SpecialMenuService` 的 `MoveShellNewAsync` 要求 ShellNew order lock 已启用；移动时会用简单 unlock 临时移除 WorldSid deny 规则，更新 `Classes` 排序值，再按原锁定状态重新加锁。

ShellNew ACL lock / unlock 只针对 ShellNew order key 相关保护。当前实现使用 v2 narrow lock：锁定时读取现有 DACL，移除重复的显式 WorldSid deny 规则和可读取的旧版 broad `WriteKey` deny 规则，再添加一个 WorldSid `Deny SetValue | CreateSubKey | Delete` 规则。v2 故意不使用 `RegistryRights.WriteKey`，因为 .NET 中 `WriteKey` 过宽，可能阻止后续读取 / 修改 ACL，导致本程序无法正常解锁。解锁时会移除旧版 WorldSid `WriteKey` deny 规则和 v2 narrow deny 规则。它只请求 `ReadPermissions` / `ChangePermissions`，不 deny `ReadPermissions`、`ChangePermissions`、ownership 修改或 `FullControl`，保留现有继承和显式 allow 规则，不合成替换 DACL，不调用 take ownership，也不启用高危所有权 / 还原类权限修复 ShellNew ACL。这里即使服务是 LocalSystem，也不能把用户级路径写到 SYSTEM 的 `HKCU`；用户级 Classes 和 Explorer order key 必须定位到 `HKEY_USERS\<sid>`。

如果旧版本 broad `WriteKey` lock 或外部工具留下的 broken ACL 无法通过 `ReadPermissions | ChangePermissions` 安全修改，本程序会返回明确错误：需要先用 BluePointLilac / ContextMenuManager 或系统工具解锁 / 修复，然后重试。主后端不再执行 take ownership、replacement DACL 或自动 ACL reset。

## 4. ShellNew ACL Lock 与 Registry Write Protection 的区别

| 项目 | ShellNew ACL Lock | Registry Write Protection |
| --- | --- | --- |
| 保护范围 | 主要针对 Explorer ShellNew order key / 新建菜单排序保护。 | 更广泛的传统右键菜单注册表写入保护。 |
| 代码路径 | `SpecialMenuService.SetShellNewOrderLockAsync`。 | `ContextMenuRegistryCatalog` 的注册表写保护相关逻辑。 |
| 用户提示 | 主要围绕 ShellNew 排序锁定 / 解锁；无法安全修改 ACL 时提示外部修复。 | 编辑菜单项时可能提示去设置页解锁。 |
| 失败形态 | legacy broad `WriteKey` 或 broken ACL 可能导致 `ChangePermissions` 修改失败；主程序不会 take ownership 或替换 DACL。 | 可能阻止第三方安装器或本程序运行时写入受保护路径。 |

不要把两者混用。ShellNew ACL Lock 不是普通菜单禁用，也不是全局右键菜单保护开关。

## 5. SendTo

SendTo 菜单来自用户 profile 下的目录，当前代码通过 `BackendUserContext.GetSendToPath()` 定位到 RoamingAppData 的 `Microsoft\Windows\SendTo`。

| 行为 | 当前实现倾向 |
| --- | --- |
| 枚举 | 读取 SendTo 目录中的文件和快捷方式。 |
| 启用 / 禁用 | 通过 hidden 属性等文件系统状态表达。 |
| 删除 / 恢复 | 使用 `.deleted` 机制进行软删除和恢复。 |
| 编辑 | 更新 `.lnk` 的目标、参数、工作目录、图标等。 |

除了用户 SendTo 目录中的真实文件，快照还会追加 Explorer 动态生成的当前可用可移动磁盘目标，例如 USB 盘。此类项来自 `DriveInfo.GetDrives()` 中 `DriveType.Removable` 且 `IsReady` 的驱动器，使用 `sendto:drive:X` 形式的稳定 id，并通过 metadata 标记为 Explorer-generated dynamic target。它们不对应 SendTo 目录内的文件，因此 `CanEdit=false`、`CanDelete=false`，前端不显示开关，后端也不会对它们执行创建、删除、编辑或 hidden 属性切换。

SendTo 是文件系统菜单，不是 registry-only 功能。没有正确 profile path 时，服务可能改到错误用户目录或找不到目标。

## 6. WinX

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

## 7. OpenWith

OpenWith 只管理用户 / 机器 Classes 的 `Applications` 物理注册及既有 Microsoft Store 入口 policy。它不枚举所有文件关联来源，也不承诺开关或删除后应用从每个 Explorer 打开方式列表中消失。操作走链路 A：Frontend -> Backend Pipe -> `SpecialMenuService`，不使用 UAC bootstrapper、ProbeHost 或新增监控。

必须区分三件事：应用注册为打开方式候选、Explorer 针对某个文件实际显示此候选、该文件类型的默认处理程序。页面的开关仅表示所选物理 `Applications` key 是否存在 `NoOpenWith`；用户 / 机器同名项分别显示和修改，不代表合并 HKCR 的最终菜单状态。微软明确说明，文件类型的 `OpenWithProgIDs` 可以在 Applications 设置 `NoOpenWith` 后继续提供应用候选：[Application Registration](https://learn.microsoft.com/en-us/windows/win32/shell/app-registration)、[Open With 注册与排除说明](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/legacy/hh127438%28v%3Dvs.85%29)。

| 来源 | 与当前管理范围的关系 |
| --- | --- |
| `HKU\<SID>\Software\Classes\Applications`、`HKLM\SOFTWARE\Classes\Applications` | 当前支持的物理注册。64 位系统固定读取 / 修改 Registry64，匹配 native Explorer；服务 x86 构建也不改用 32 位 view。 |
| Classes `.<ext>\OpenWithProgIDs` 及对应 ProgID command | 按文件类型提供候选，不随 Applications 开关或软删除改变；不删除、不修改这些 ProgID。参见 [Include an Application in Open With](https://learn.microsoft.com/en-us/windows/win32/shell/how-to-include-an-application-on-the-open-with-dialog-box)。 |
| 用户 `Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.<ext>` | `OpenWithList` / `OpenWithProgids` 是用户文件类型候选 / 使用记录来源；`UserChoice` 是默认选择证据。当前页不写入这些路径，不改 Hash。 |
| `Software\RegisteredApplications` -> 应用 `Capabilities\FileAssociations` | 应用声明支持的文件类型，可关联独立 ProgID；不能当作 Applications key 的副本，也不在本页修改。参见 [Default Programs](https://learn.microsoft.com/en-us/windows/win32/shell/default-programs)。 |
| AppX/MSIX `uap:FileTypeAssociation` 等声明 | 包注册可以提供文件类型处理能力；本页不管理包 manifest、包生成的 ProgID 或默认应用选择。参见 [FileTypeAssociation](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-uap-filetypeassociation)。 |

不通过 UserChoice / Hash、UCPD、ACL lock 或拦截安装器扩大此范围。不改 unrelated ProgID、Capabilities 或默认关联设置。如果默认调用路径本身依赖被删除的 `Applications\<app>` 注册，删除会影响该注册的调用能力；保留默认关联设置并不保证该程序仍可启动，默认选择应通过 Windows 设置管理。

WPS / Microsoft Office 共存保护不在 OpenWith 普通页面展示受保护文档扩展名 owner，也不在该页面提供默认应用切换。关联、图标、ShellNew 注入只作为 WPS 专用待审核项显示；`UserChoice` 只能作为证据读取，不能由本项目直接写入 Hash。

| 行为 | 当前实现倾向 |
| --- | --- |
| 枚举 | 读取 `HKU\<SID>\Software\Classes\Applications` 和 `HKLM\SOFTWARE\Classes\Applications`，只纳入带文件扩展名的应用 key，并选择 `shell` 下优先的非 `NeverDefault` verb。 |
| 显示名 | 当应用 key 名等于命令目标文件名时优先解析 `FriendlyAppName`，否则读取目标文件 `FileDescription`，最后回退到文件名。 |
| 启用 / 禁用 | 禁用只在值不存在时写空 `REG_SZ NoOpenWith`，同时写独立 `ContextMenuMgrPlus.OpenWithVisibility` 归属值；两值在短生命周期 Windows 注册表事务中提交。启用只撤销匹配本物理路径、command generation 和值类型 / 内容的本程序写入；预存或第三方改写的值均保留并返回错误。 |
| 新增 | 写入当前前端用户的 `HKU\<SID>\Software\Classes\Applications\<app.exe>\shell\open\command`，并写 `FriendlyAppName`；原子创建 app leaf，已有注册拒绝覆盖，也不清除已有 `NoOpenWith`。 |
| 编辑 | 更新 `FriendlyAppName` 和 command 默认值；为避免把一个应用 key 改成另一个程序，当前不允许编辑时改变目标 exe 路径。 |
| 删除 / 恢复 | 对所选 app key 使用 Win32 `RegRenameKey` 重命名为 sibling `.deleted` / 恢复原名；保留完整树、原始 ExpandString、ACL 和开关状态。拒绝缺失源、目标冲突及无效 backup metadata，不覆盖现有备份 / 重新注册的原项，不提供 copy/delete fallback。成功必须回读源已不存在、目标存在，再返回从目标读取的 DTO。 |
| 永久删除 | 仅删除经过路径和请求状态校验的所选 `.deleted` 备份，逐层加入同一注册表事务；任一子键访问拒绝 / 并发冲突会回滚，提交后确认备份不存在。失败不报告成功，不触碰重新出现的原 app key。 |
| 应用商店入口 | 保留既有 `NoUseStoreOpenWith` policy 项；开关同步 HKLM 和当前用户 HKU policy 值并回读结果。某一 hive 失败时返回失败，已完成的 policy 写入可能仍存在，应刷新核对。 |

OpenWith 需要 SID，但不需要 SessionId。请求只能使用 Pipe 客户端认证身份；解析失败不退回另一个交互用户。快照和修改先验证 account SID 与已加载的用户 hive；即使所选项属于 HKLM，用户上下文缺失也拒绝操作。服务不能用 LocalSystem `HKCU`、重建未加载的 SID hive，或信任不匹配的 `AppRegistryPath` / `DeletedPath`。

后端串行化本程序 OpenWith 快照与 mutation，校验请求携带的 visibility stamp / command；注册表事务中的非事务并发写入会使事务失败，项目不会覆盖第三方结果。成功是提交后 fresh read-back 的结论，不是永久禁止第三方重建注册。事务创建 / 提交不可用时直接报错，没有放弃验证的 fallback。

前端串行化该页快照和操作；旧快照不能在成功 response 后覆盖状态。失败 / 超时后用独立快照核对物理状态，不重试 mutation；核对失败显示结果未确认并锁定该条目操作，后续成功刷新解除。迟到通知触发重新查询，不直接使用可能过期的 DTO；旧失败任务也不能撤销较新的权威更新。

兼容决定：保留已发布的 sibling `.deleted` 及历史 `.deleted.deleted` 等重复后缀恢复路径。备份和活动项使用不同物理 Id，冲突时两者都显示；恢复只选择 DTO 指定且属于原 app 的备份。旧版本没有记录完整性，历史残缺备份不能凭后缀判为完整；缺少 command 的备份不作为可管理项，但仍阻止覆盖。也不推断旧版空 `NoOpenWith` 的归属：无 marker 的值必须保留。Issue #136 的维护决定是继续支持这些数据；只有明确停止支持历史 sibling 备份 / 完成归属核实后才可退休，不能猜测发布版本或自动删除旧值。

### Windows 10 / 11 手动验证（仍需真实 Explorer / 服务环境）

在可恢复的测试机上准备唯一应用 key 和测试扩展名，先导出测试注册；只清理自己创建的测试数据，不修改真实 `UserChoice` / Hash。分别在 Windows 10、Windows 11 执行：

1. 同一 app 分别注册在 HKU 当前 SID 和 HKLM，含 command、FriendlyAppName、ExpandString 和 nested values；运行普通权限前端 / 后端服务，分别开关。直接核对所选 hive 的空 `NoOpenWith`、归属值及刷新状态，另一 hive 不改变。服务 x86 / x64 构建应操作同一 native view。对两个用户重复，不能读取或改写另一 SID；认证失败 / hive 未加载返回错误，不新建 SID 根。
2. 预先设置空字符串、非空字符串、DWORD / Binary `NoOpenWith`。刷新后禁用保持原值，启用明确拒绝且仍显示物理禁用状态；旧版本无 marker 的值同样保留。由第三方改写 NoOpenWith、marker 或 command 后重试，旧请求拒绝，刷新准确反映当前值。
3. 分别测试 Applications-only、同时通过 `OpenWithProgIDs`、用户 FileExts、RegisteredApplications/Capabilities 和打包应用提供的候选。观察实际文件的子菜单及“选择其他应用”对话框，记录 Windows 版本和文件类型；注册表成功而 Explorer 仍显示候选属于范围限制，不能判为已验证 Explorer 移除。默认应用通过 Windows 设置选择，比较操作前后的只读 UserChoice / 默认处理行为，不手工写 Hash。
4. 连续至少三次开关、刷新、删除 / 重启服务 / 恢复：原 key 与 backup 必须互斥，ACL、值类型、未展开文本、command 与原开关状态完整恢复。人为预置 `.deleted` 冲突（包括残缺副本）时不覆盖；原 app 被安装器重建后 Undo 拒绝，两项仍可查看，Purge 只清理选中备份。验证历史重复后缀只恢复指定副本。
5. 在测试 key / 某个 backup 子键设置可恢复的权限拒绝，分别尝试 toggle、Delete、Undo、Purge。错误不得显示完成；事务失败不留半对 marker/NoOpenWith 或残缺备份。解除测试 ACL 后重试，并直接检查树和前端状态。模拟进程在原生 rename / 事务提交前后退出，不能出现半棵复制树。
6. 测试并发安装器改写、两个前端、旧刷新在操作中完成、Pipe 响应丢失 / 超时但后端已提交、核对查询也失败和随后迟到通知。核对成功后只显示真实状态；核对不可用时阻止条目重复操作，成功刷新后恢复，不把取消 / 超时当作物理写入必然失败。

自动测试使用当前普通账号的唯一 HKU Applications fixture，并清理全部测试 key / ACL。它验证注册表状态、raw values、归属、备份冲突、事务 rollback、SID 隔离和旧失败响应不会覆盖新状态；不证明 HKLM 高权限服务 mutation、跨账号真实 Pipe 身份、Explorer 候选可见性或 Windows 默认应用启动行为。原 Issue 没有操作时间线、物理注册路径或对应日志，尚不能确定其截图应用的具体注册来源；需要 `backend.log` / `frontend-debug.log` 和上述只读路径信息补齐证据。

## 8. SpecialMenu 的用户上下文要求

| SpecialMenu | 需要 SID | 需要 ProfilePath | 需要 LocalAppData/RoamingAppData | 原因 |
| --- | --- | --- | --- | --- |
| `ShellNew` | 是 | 通常需要 | 需要用户 hive 和 Explorer order key | 用户级 Classes 和 ShellNew order key 必须写 `HKEY_USERS\<SID>`。 |
| `SendTo` | 通常需要 | 是 | 需要 RoamingAppData | SendTo 是用户 profile 下目录。 |
| `WinX` | 通常需要 | 是 | 需要 LocalAppData | WinX 是每用户文件系统配置。 |
| `OpenWith` | 是 | 通常不需要 | 通常不需要 | 用户级 Applications 和 `NoUseStoreOpenWith` policy 必须写 `HKEY_USERS\<SID>`，不能写服务 `HKCU`。 |
| `DragDrop` | 视具体操作而定 | 通常不需要 | 通常不需要 | 当前实现主要按注册表路径处理。 |
| `CommandStore` | 视具体操作而定 | 通常不需要 | 通常不需要 | 属于 Explorer 命令注册表模型。 |
| `GuidBlock` | 视具体操作而定 | 通常不需要 | 通常不需要 | 以 GUID blocked list 为主。 |
| `InternetExplorer` | 视具体操作而定 | 通常不需要 | 通常不需要 | 旧式 IE MenuExt 路径。 |

这里的“通常需要”表示当前实现会通过 `BackendUserContext` 获取 SID 或 profile 目录。不要用 LocalSystem 的环境变量推断前端用户路径。

## 9. 常见坑

| 坑 | 正确处理 |
| --- | --- |
| 用普通 `ContextMenuRegistryCatalog` 处理 ShellNew 排序 | 走 `SpecialMenuService` 的 ShellNew 专用逻辑。 |
| 用服务 `HKCU` 写 ShellNew | 写 `HKEY_USERS\<sid>`。 |
| 把锁定失败简单判断为权限不够 | ACL deny、所有者、继承状态都可能影响 `ReadPermissions` / `ChangePermissions`；证据不足时不要推断根因。 |
| 期望主程序自动修复 legacy broad lock / broken ACL | ShellNew lock/unlock 不再 take ownership、不替换 DACL；旧版 `WriteKey` broad lock 无法安全修改时应使用 BluePointLilac / ContextMenuManager 或系统工具外部解锁 / 修复。 |
| 把 SendTo / WinX 当 registry-only 功能 | 它们主要是用户文件系统目录。 |
| 用服务 `HKCU` 写 OpenWith Applications | 新增和用户 policy 写 `HKEY_USERS\<sid>`；机器级项才写 HKLM。 |
| 把 ShellNew ACL Lock 和 Registry Write Protection 混用 | 两者保护范围、代码路径和用户提示都不同。 |


`GuidBlockMenuService` 独立持有机器级 GUID Block 路径，负责其枚举、创建、开关和删除；`SpecialMenuService` 只在现有分派与响应/日志边界调用它。该列表与 classic per-registration 开关、Windows 11 用户 blocked list 均有不同语义，不能互相替代。
