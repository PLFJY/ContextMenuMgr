# 安装器部署、升级与恢复

本文负责 Inno 安装过程、部署标识与 runtime 清理，不负责 SCM bootstrapper 或 Release 发布。入口：[build_Installer.iss](../Installer/build_Installer.iss)、[UpgradeDeployment.iss](../Installer/UpgradeDeployment.iss)、[Build.Common.psm1](../Scripts/Build.Common.psm1) 的 `Write-InstallerRuntimeInventory` / `Invoke-InstallerBuildTarget`。构建参数与产物见[构建发布](build-and-release.md)，用户数据路径见[运行时数据](runtime-data-and-logging.md)，安装后服务健康见[生命周期](runtime-lifecycle.md)。

构建向 ISCC 传 Version、AppId、publish/output directory、MyDistributionMode、MyArchitecture 与 MyUseDotNetDependencyInstaller。仅 self-contained/framework-dependent，架构、模式与 .NET dependency installer 不匹配时编译失败；FD 依赖安装器开，SC 关。`packageKind` 决定数据根，deployment mode 决定 runtime 归属，不能混用。

## 部署标识与安全升级

这是一项安装器健壮性改进，不是修复已证实的 framework-dependent publish 缺陷。`packageKind=Installer/Portable` 仍只决定运行时数据位置；部署模式不写入该 JSON，不修改 dotnet publish 参数或资产命名。

部署标识直接保存到安装目录 `{app}\ContextMenuMgr.deployment.ini`，使用普通 Inno `[INI]` entries：

```ini
[Deployment]
AppId=45156332-3408-47B7-B5D2-2567E5888F64
DistributionMode=self-contained
Architecture=win-x64
InstallPath=C:\Program Files\Context Menu Manager Plus
RuntimeFiles=hostfxr.dll|<SHA256>;...
RuntimeFilesSHA256=<Inno GetSHA256OfString(RuntimeFiles)>
Complete=1
```

`DistributionMode` 只接受 `self-contained` / `framework-dependent`；Architecture 为 `win-x86` / `win-x64` / `win-arm64`。AppId 来自当前编译配置；文件绑定该 AppId 和实际安装路径。开始改动文件前先写 `Complete=0`，正常文件阶段完成、核心文件回读及原运行服务恢复后才设为 `1`。卸载时在现有 Inno uninstall 生命周期中检查 AppId、架构和实际路径，只删除仍属于该安装的部署文件；不逐项删除可能已属于其它 AppId 的 INI 字段。不增加部署注册表字段，不在用户配置目录保存或查找标识，也不因目录位于 userprofile、桌面或 LocalAppData 而禁止安装。

清理归属只使用安装目录中的部署文件：AppId 必须等于当前 MyAppId，记录路径必须等于实际目标路径，架构必须与 incoming installer 相同，Complete 必须为 1，模式必须有效。无需查询 HKLM/HKCU、猜测卸载键名称或用卸载 EXE/DAT 的存在作安装门槛。Inno 自身按现有 AppId 处理卸载注册及日志追加；保留原 AppId 表达式，不能为了修正手工拼接键的错误而改变应用身份。路径检查只保留本地非卷根、不位于 Windows 下、非 Program Files 根及无 reparse-point 祖先等与实际目标相关的检查，不依赖用户 SID/profile。检测到 Portable 包时要求独立目录，保留其 Data。

同模式升级、framework-dependent→self-contained、全新安装均正常替换，不额外清理。只有本地部署文件已验证同 AppId、同路径、同架构、Complete=1 的 self-contained→framework-dependent 才按文件清单清理 runtime。没有部署文件、字段无效、AppId/路径/架构不匹配、Complete 非 1、inventory 缺失或校验失败时，按 Unknown 保守覆盖安装，清空待清理名单并在 /LOG 记录跳过原因，不阻止安装，也不删除不确定文件。不同架构同样不会执行旧运行时清理；这只恢复普通 Inno 文件替换，不保证旧架构附加组件已完成迁移。Unknown→framework-dependent 可能保留历史 app-local runtime，不能声称这是一项已完成清理的转换；需要消除这些残留时，可用目标架构 self-contained 正常覆盖建立记录后再转换，但不强制用户先重装。此旧版兼容保留到维护者明确停止支持无部署文件的安装，不捏造退休版本。

`Write-InstallerRuntimeInventory` 从最终 publish 的 frontend `.deps.json` 中明确的 `runtimepack.Microsoft.NETCore.App.Runtime.<RID>` / `runtimepack.Microsoft.WindowsDesktop.App.Runtime.<RID>` 的 runtime/native assets 生成所有权记录。仅接受顶层 DLL 和 createdump.exe，记录实际发布文件 SHA-256；布局变化先报错审查，不自动扩大清理。具体数量由 SDK/publish 输出决定；范围包括 hostfxr/hostpolicy、coreclr/clrjit、框架程序集及 WPF native 组件。SDK/WinRT 投影、应用程序集、第三方库、语言子目录及未知目录不在清理范围。编译输入放在 publish 目录外，不进入 Installer payload 或 Portable staging。FD 输入出现这两个 runtime pack 时构建失败，不删除 publish 文件；这不是通用 package reconciliation 框架。

`UpgradeDeployment.iss` 使用 SCM query/stop/start，只处理当前及已知旧服务名，验证服务 EXE 属于目标目录；其它目录的服务只记日志并保留，不阻止此目录的普通安装。Stopped/Missing 无需停止，Running/StopPending 有界轮询等待 Stopped（约 30 秒），其它状态或权限/超时错误终止。正常升级不删除服务注册、不改变启动类型；原先 Running 的服务在 payload 与部署记录验证后启动并等待 SCM Running；已经 StopPending 的服务等待停止，不擅自恢复为运行。这里不把 Running 等同于 pipe/application 的端到端健康。文件阶段开始前取消或准备失败会尝试恢复服务；恢复失败显示诊断并写日志。进入文件阶段后即使 BeforeInstall 报错也可能被 Inno 的文件错误对话框忽略，因此不假定 payload 未改变，不自动恢复未验证的服务。

Frontend/TrayHost 使用 Inno Restart Manager，并额外注册需删除的 runtime 文件。清理前用 Windows WMI 检查三个 host 的实际 executable path；拒绝关闭、仍运行、路径不能核实或 WMI 不可用时失败，不使用通用进程 kill。动态上一版文件名单及逐项 hash/结果验证无法用静态 `[InstallDelete]` 精确表达，因此清理在首次 `[Files] BeforeInstall` 执行，在 Restart Manager 之后、替换文件之前。先验证所有现存记录文件的 hash、属性和独占 read/delete 访问，再逐项重新验证并 DeleteFile、确认不存在。手动丢失的文件可跳过；外部修改、只读、锁定、拒绝访问、目录/reparse-point 替身都失败。安装后的主动启动项只在 DeploymentFinished 时执行。完成前验证 Frontend/Service/TrayHost 的 EXE、DLL、deps/runtimeconfig（12 个核心文件）及 SC runtime 的安装后 hashes，并回读部署记录；核心 payload hashes 只是编译期验证输入。

清理不是事务。开始改动后 Complete 保持 0。Inno 的 ssPostInstall 异常本身不会把退出码改为失败；本实现另外使用 GetCustomSetupExitCode 在验证未完成时返回 1，完成页显示 Installation incomplete，并禁止 postinstall 启动项，不自动启动未验证的服务。Inno 内部日志仍可能先记 Installation process succeeded；不能将这条引擎日志等同于部署校验完成。保留 `/LOG`，用原架构 SC 重装恢复，再重试；强制结束/停电还可能需要检查服务是否停止。未运行旧 uninstaller，未改变 AppId、append uninstall log 或快捷方式。清理没有通配符或递归删除，只删除记录且内容匹配的 runtime 文件。unins EXE/DAT、Data、用户文件、未知 DLL/目录以及外部 ProgramData/LocalAppData 配置、状态库、备份不在范围内；显式卸载时原有是否删除本地数据的选择不变。

## 验证与限制

普通用户 Inno harness/编译验证不能证明真实提升产品安装与完整升级/卸载。不能把引擎“Installation process succeeded”日志、SCM Running 或静态 inventory 验证等同于部署/启动成功。

在隔离 Windows VM 用 `/LOG` 分别验证三架构 SC/FD clean install、同模式/双向转换、无标识旧版、重复升级后卸载、自定义/Unicode 路径和真实 AppId 卸载日志追加。直接检查 INI/Complete、runtime hashes、未知文件/Data/外部状态备份、服务原先 Running/Stopped/StopPending 与真实 Pipe、Frontend/TrayHost。FD 在全局 Desktop Runtime 环境启动，SC 在无全局 runtime 环境启动。

故障场景包括 runtime 锁定/只读/变更 hash、WMI/SCM 不可用、服务在其它路径、架构不同、Portable 目录、部署标识损坏/只读、取消、中断/断电及文件阶段失败。预检失败应无不确定清理，开始改动后 Complete=0 及退出码失败，不自动启动未核验 payload。该清理不是事务，外部并发和中断可能留下部分删除；按 /LOG 与物理文件确认，再用原架构 SC 重装恢复后重试，不能盲删未知 DLL。

维护决定：无部署文件/Unknown 的旧安装保守覆盖兼容保留到维护者明确停止支持该安装格式；不能捏造退出版本、强制先卸载或以当前新标识已存在推断旧安装不受支持。修改 inventory allow-list、AppId/路径归属、清理时点、服务恢复、退出码或卸载策略时更新本文，构建入口调整另同步构建文档。
