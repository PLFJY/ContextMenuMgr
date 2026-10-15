# 构建与发布说明

## 1. 构建入口

当前仓库主要构建入口如下：

| 入口 | 作用 |
| --- | --- |
| `build.ps1` | 本地完整发布入口，默认构建 installer 和 portable 产物。 |
| `build-release.bat` | 调用 `build.ps1 -Configuration Release` 的批处理入口。 |
| `build-beta.bat` | 调用 `build.ps1 -Configuration Beta` 的批处理入口。 |
| `Scripts/Build-Target.ps1` | GitHub Actions 和单目标构建使用的入口。 |
| `Scripts/Build.Common.psm1` | restore、publish、打包、ProbeHost 多架构映射、Inno Setup 调用等公共函数。 |
| `NuGet.Config` | restore 使用的 NuGet 配置。 |

当前代码库没有发现名为 `build.bat` 的入口；不要在文档或自动化中假设它存在。

主开发入口是根目录 `ContextMenuMgr.slnx`。该 slnx 同时包含 Frontend、Backend、TrayHost、Contracts 和 native C++ `ContextMenuMgr.ProbeHost.vcxproj`；不需要打开单独的 ProbeHost-only solution 才能看到 ProbeHost 项目。前端项目和发布脚本仍会自动构建、复制 native ProbeHost 多架构产物。

## 2. 获取源码与 Submodule 要求

本仓库包含 Git submodule。首次获取源码时建议使用 recursive clone：

```powershell
git clone --recursive https://github.com/PLFJY/ContextMenuMgr.git
```

如果已经使用普通 `git clone` 获取仓库，请在仓库根目录执行：

```powershell
git submodule update --init --recursive
```

未初始化 submodule 时，可能出现以下问题：

- 仓库内置的 Inno Setup 编译器缺失，例如 `Installer\Inno Setup 6\ISCC.exe` 不存在；
- 安装包构建失败；
- 未来 native ProbeHost 或其它第三方源码依赖缺失；
- CI / 本地构建表现不一致。

因此，任何涉及安装包、内置构建工具、native helper 或第三方源码依赖的任务，都应先确认 submodule 已初始化。Agent 不应假设普通 clone 已经包含完整构建依赖。

## 3. 项目产物

| 项目 | 输出 | 说明 |
| --- | --- | --- |
| Frontend | `ContextMenuManagerPlus.exe` | WPF UI，最终发布目录的主程序。 |
| Backend Service | `ContextMenuManagerPlus.Service.exe`、`ContextMenuManagerPlus.Service.dll` | Windows Service / bootstrapper 相关后端程序。 |
| TrayHost | `ContextMenuManagerPlus.TrayHost.exe` | 每用户托盘进程。 |
| ProbeHost | `ProbeHost\<arch>\ContextMenuMgr.ProbeHost.exe` | Deep Analysis 多架构隔离进程。 |
| Contracts | `ContextMenuMgr.Contracts.dll` | pipe 契约、模型、共享路径常量。ProbeHost 已改为 native C++，不再运行时依赖 Contracts DLL。 |

## 4. Debug 本地构建

`ContextMenuMgr.Frontend.csproj` 在普通 framework-dependent build 中会负责准备运行所需辅助产物。Debug 本地开发默认只构建 `Win32,x64` ProbeHost，避免未安装 ARM64 C++ 工具链的 x64 开发机无法 `dotnet run`；Release / Beta 和发布构建仍默认构建 `Win32,x64,ARM64`。portable anycpu 发布的最终 ProbeHost 校验会沿用同一组标签：Debug 校验 x86 / x64，非 Debug 校验 x86 / x64 / arm64。

Frontend、Backend 和 Tests 的 Windows TFM 以 `windows10.0.17763.0` 为共同基线。Backend 用 `Microsoft.Windows.SDK.NET.Ref` 的 Windows Runtime 投影调用 `PackageManager.FindPackagesForUser`，按前端 SID 发现打包 ShellNew 和 Windows 11 context-menu 声明；不要把它替换成直接引用 `.winmd` 的旧 `Microsoft.Windows.SDK.Contracts`，后者在 .NET 5+ 会触发 `NETSDK1130`。

```text
Frontend build
-> 使用 MSBuild 构建 native C++ ProbeHost（Debug 默认 Win32 / x64；Release / Beta 默认 Win32 / x64 / ARM64）
-> 复制 Backend artifacts
-> 复制 TrayHost artifacts
-> 复制已构建 ProbeHost 架构目录下的 ContextMenuMgr.ProbeHost.exe
-> 复制 ThirdPartyNotices\nlohmann-json-LICENSE.MIT
-> 执行 Verify-ProbeHostArchitecture.ps1
```

ProbeHost 是单文件 native exe。前端项目不再检查 `ContextMenuMgr.ProbeHost.dll`、`.deps.json`、`.runtimeconfig.json` 或 ProbeHost 目录中的 `ContextMenuMgr.Contracts.dll`。本地构建要求安装 Visual Studio Build Tools C++ workload、Windows SDK；如果要构建 arm64 ProbeHost，还需要 ARM64 工具链。已安装 ARM64 工具链的开发机可用 `-p:NativeProbeHostPlatforms=Win32,x64,ARM64` 强制 Debug 也构建三架构。

普通 `dotnet build` / `dotnet run` 不会无条件清理 native ProbeHost 输出。`ContextMenuMgr.Frontend.csproj` 会先用 MSBuild incremental build 检查 `ContextMenuMgr.ProbeHost.vcxproj`、`src\**\*.cpp`、`src\**\*.h` 和 `third_party\nlohmann\json.hpp` 是否晚于当前 `NativeProbeHostPlatforms` 对应架构的目标 exe；全部最新时，`BuildNativeProbeHostArtifacts` target 会直接跳过，不启动 PowerShell。

如果 target 需要运行，`Scripts\Build-NativeProbeHostArtifacts.ps1` 会在一次 Windows PowerShell 5.1 兼容的 PowerShell 进程中处理 `NativeProbeHostPlatforms` 指定的平台，并且只解析一次 `MSBuild.exe` 路径。脚本仍会对每个架构单独检查目标 `ContextMenuMgr.ProbeHost.exe` 是否最新；如果某个架构已是最新，会输出该 label 的 `Skipped=True` 并跳过该架构的 MSBuild。需要强制重建时可传入：

```powershell
dotnet build .\ContextMenuMgr.Frontend\ContextMenuMgr.Frontend.csproj -p:ForceRebuildNativeProbeHost=true
```

`dotnet clean` / `dotnet rebuild` 会清理 native ProbeHost 输出和 obj 目录。

本地 native ProbeHost 输出和中间目录按架构隔离，不共用 `OutDir` 或 `IntDir`：

```text
artifacts\probehost-native\<Configuration>\x86\
artifacts\probehost-native\<Configuration>\x64\
artifacts\probehost-native\<Configuration>\arm64\
artifacts\probehost-native\<Configuration>\obj\x86\
artifacts\probehost-native\<Configuration>\obj\x64\
artifacts\probehost-native\<Configuration>\obj\arm64\
```

`Build-NativeProbeHostArtifacts.ps1` 会把 MSBuild Platform 映射到 label：`Win32 -> x86`、`x64 -> x64`、`ARM64 -> arm64`。每个 label 构建或增量跳过后都会立即读取目标 exe 的 PE Machine 并验证：`x86 -> 0x014C`、`x64 -> 0x8664`、`arm64 -> 0xAA64`。单架构构建也使用此脚本，通过 `-Platforms x64`（或 `Win32` / `ARM64`）选择平台；不保留另一套单架构实现。

## 5. Release 发布

`build.ps1` 负责组合多个平台和分发模式。`Scripts/Build.Common.psm1` 中的关键步骤包括：

`build.ps1` 会按 `MaxParallel` 并行启动多个 `Scripts/Build-Target.ps1` 子进程。任一目标失败后会停止继续调度，并对仍在运行的目标取 `.ToArray()` 快照后逐个停止，避免在 PowerShell 5.1 中直接用 `@(...)` 包装 generic list 时触发类型转换异常。

并行发布目标会给 `dotnet restore/publish` 传入目标专属的 `BaseOutputPath` 和 `--artifacts-path`。辅助项目的 `OutputPath` 必须从 `$(BaseOutputPath)` 派生；不要硬编码回仓库级共享目录，否则 x86/x64/arm64 同时发布时可能抢写同一个 `.runtimeconfig.json`、`.deps.json` 或 apphost。

| 步骤 | 说明 |
| --- | --- |
| restore | 对 Frontend、Backend、TrayHost 运行 `dotnet restore`，使用 `NuGet.Config`。ProbeHost 是 native C++ 项目，不走 dotnet restore。 |
| publish | 对项目运行 `dotnet publish`，按平台和分发模式输出到 publish workspace。 |
| self-contained | 发布时携带 .NET runtime，安装包不依赖本机已安装 runtime。 |
| framework-dependent | 依赖本机 .NET runtime，安装包可启用 .NET dependency installer。 |
| installer | 调用 Inno Setup 生成安装包。 |
| portable | 当前支持 `framework-dependent` + `anycpu` 的 portable zip。 |
| `artifacts.txt` | `build.ps1` 在 `build/dist` 下写入产物清单。 |

发布目录必须包含 `ContextMenuMgr.package.json` 运行时包类型标记。Installer 发布目录写入 `{ "packageKind": "Installer" }`，Portable 发布目录写入 `{ "packageKind": "Portable" }`。运行时代码只读取 `AppContext.BaseDirectory` 下的该文件；缺失或无效时按 Installer 处理，不根据路径猜测包类型。

`Build-Target.ps1` 对 installer 目标要求平台是 `win-x64`、`win-x86` 或 `win-arm64`；portable 当前只支持 `framework-dependent anycpu`。

## 6. ProbeHost 多架构

`Get-ProbeHostArchitectureMap` 定义发布包应携带的 ProbeHost 架构：

| 发布平台 | ProbeHost 架构 |
| --- | --- |
| `win-x86` | x86 |
| `win-x64` | x64、x86 |
| `win-arm64` | arm64、x64、x86 |

这样做是因为目标 Shell Extension DLL 的架构可能与主程序平台不同。例如 x64 系统仍可能安装 x86 handler，arm64 系统也可能需要 x64 或 x86 ProbeHost。

常见错误包括：目录标签和 PE 架构不一致、只发布主架构缺少 x86 fallback、机器缺少 C++ build tools 或 ARM64 工具链。

当前 Release 脚本使用 MSBuild 构建 `ContextMenuMgr.ProbeHost.vcxproj`，按 `Win32`、`x64`、`ARM64` 平台生成单个 `ContextMenuMgr.ProbeHost.exe`，再复制到 `ProbeHost\<arch>`。不再对 ProbeHost 运行 `dotnet publish`，也不会发布 `.dll`、`.deps.json`、`.runtimeconfig.json` 或 ProbeHost 目录内的 `ContextMenuMgr.Contracts.dll`。

构建脚本在调用 native MSBuild 前会先检查目标架构的 `cl.exe` 是否存在，并用规范化后的进程环境启动 MSBuild，避免当前 shell 同时带有 `Path` / `PATH` 两个环境变量时触发 C++ ToolTask 的 `Item has already been added` 异常。该外部命令启动逻辑必须兼容 Windows PowerShell 5.1，因为前端 csproj 的 ProbeHost target 通过 `powershell.exe` 调用脚本；不能依赖仅 PowerShell 7 / newer .NET 才有的 `ProcessStartInfo.ArgumentList` API。缺少 ARM64 C++ build tools 时，发布构建会在 ProbeHost arm64 阶段直接报出缺失工具链；需要通过 Visual Studio Installer 安装 C++ ARM64 build tools 和 Windows SDK 后再重试。

Release 构建同样按发布目标和 architecture label 隔离 native ProbeHost 中间目录。例如 `installer\self-contained\win-x64` 的 native obj 位于：

```text
build\publish\_artifacts\installer\self-contained\win-x64\probehost-native-obj\x64\
build\publish\_artifacts\installer\self-contained\win-x64\probehost-native-obj\x86\
```

发布输出仍位于最终发布目录下的 `ProbeHost\<arch>\ContextMenuMgr.ProbeHost.exe`。每个 label 的 MSBuild 构建完成后会立即验证该 exe 的 PE Machine，随后仍执行 `Verify-ProbeHostArchitecture.ps1 -Root <ProbeHostRoot> -Labels ...` 做整体发布目录校验。

ProbeHost 使用 vendored `nlohmann/json` 单头文件解析 request/result JSON：

- 头文件位于 `ContextMenuMgr.ProbeHost\third_party\nlohmann\json.hpp`；
- 不使用 vcpkg、Conan、NuGet 或 Git submodule 获取 JSON；
- `json.hpp` 是编译期依赖，不复制到 runtime output；
- 发行产物必须包含 `ThirdPartyNotices\nlohmann-json-LICENSE.MIT`；
- ProbeHost exe 包含该 header-only 依赖编译后的代码。

Deep Analysis 菜单项图标编码使用 Windows 内置 WIC，native ProbeHost 链接 `windowscodecs.lib`。这不需要复制新的运行时文件，也不改变 ProbeHost 多架构输出布局。

## 7. Inno Setup

安装包脚本位于 `Installer/build_Installer.iss`。构建脚本会定位 `ISCC.exe`，优先检查仓库内 `Installer\Inno Setup 6\ISCC.exe`，然后检查 Program Files 和 PATH。

`Invoke-InstallerBuildTarget` 会向 Inno Setup 传入应用版本、AppId、发布目录、输出目录、显式部署模式 `MyDistributionMode`、目标 RID `MyArchitecture`、架构选项和是否启用 .NET dependency installer。仅接受 `self-contained` / `framework-dependent`；模式、dependency installer 和架构/注册表视图不一致时编译失败。framework-dependent 安装包把 `MyUseDotNetDependencyInstaller` 设为 `1`，self-contained 为 `0`。

self-contained installer 目标还会从同一个 installer publish 输出生成 portable zip。因为 installer publish 输出带 Installer 标记，脚本必须先复制到 staging 目录，再把 staging 中的 `ContextMenuMgr.package.json` 覆盖为 Portable，最后压缩该 staging 目录；不要直接压缩 installer publish 目录作为 portable 包。

### 部署模式记录与安全升级

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

`Write-InstallerRuntimeInventory` 从最终 publish 的 frontend `.deps.json` 中明确的 `runtimepack.Microsoft.NETCore.App.Runtime.<RID>` / `runtimepack.Microsoft.WindowsDesktop.App.Runtime.<RID>` 的 runtime/native assets 生成所有权记录。仅接受顶层 DLL 和 createdump.exe，记录实际发布文件 SHA-256；布局变化先报错审查，不自动扩大清理。现有 10.0.12 x64 输出记录 239 个文件，包括 hostfxr/hostpolicy、coreclr/clrjit、框架程序集及 WPF native 组件。SDK/WinRT 投影、应用程序集、第三方库、语言子目录及未知目录不在清理范围。编译输入放在 publish 目录外，不进入 Installer payload 或 Portable staging。FD 输入出现这两个 runtime pack 时构建失败，不删除 publish 文件；这不是通用 package reconciliation 框架。

`UpgradeDeployment.iss` 使用 SCM query/stop/start，只处理当前及已知旧服务名，验证服务 EXE 属于目标目录；其它目录的服务只记日志并保留，不阻止此目录的普通安装。Stopped/Missing 无需停止，Running/StopPending 有界轮询等待 Stopped（约 30 秒），其它状态或权限/超时错误终止。正常升级不删除服务注册、不改变启动类型；原先 Running 的服务在 payload 与部署记录验证后启动并等待 SCM Running；已经 StopPending 的服务等待停止，不擅自恢复为运行。这里不把 Running 等同于 pipe/application 的端到端健康。文件阶段开始前取消或准备失败会尝试恢复服务；恢复失败显示诊断并写日志。进入文件阶段后即使 BeforeInstall 报错也可能被 Inno 的文件错误对话框忽略，因此不假定 payload 未改变，不自动恢复未验证的服务。

Frontend/TrayHost 使用 Inno Restart Manager，并额外注册需删除的 runtime 文件。清理前用 Windows WMI 检查三个 host 的实际 executable path；拒绝关闭、仍运行、路径不能核实或 WMI 不可用时失败，不使用通用进程 kill。动态上一版文件名单及逐项 hash/结果验证无法用静态 `[InstallDelete]` 精确表达，因此清理在首次 `[Files] BeforeInstall` 执行，在 Restart Manager 之后、替换文件之前。先验证所有现存记录文件的 hash、属性和独占 read/delete 访问，再逐项重新验证并 DeleteFile、确认不存在。手动丢失的文件可跳过；外部修改、只读、锁定、拒绝访问、目录/reparse-point 替身都失败。安装后的主动启动项只在 DeploymentFinished 时执行。完成前验证 Frontend/Service/TrayHost 的 EXE、DLL、deps/runtimeconfig（12 个核心文件）及 SC runtime 的安装后 hashes，并回读部署记录；核心 payload hashes 只是编译期验证输入。

清理不是事务。开始改动后 Complete 保持 0。Inno 的 ssPostInstall 异常本身不会把退出码改为失败；本实现另外使用 GetCustomSetupExitCode 在验证未完成时返回 1，完成页显示 Installation incomplete，并禁止 postinstall 启动项，不自动启动未验证的服务。Inno 内部日志仍可能先记 Installation process succeeded；不能将这条引擎日志等同于部署校验完成。保留 `/LOG`，用原架构 SC 重装恢复，再重试；强制结束/停电还可能需要检查服务是否停止。未运行旧 uninstaller，未改变 AppId、append uninstall log 或快捷方式。清理没有通配符或递归删除，只删除记录且内容匹配的 runtime 文件。unins EXE/DAT、Data、用户文件、未知 DLL/目录以及外部 ProgramData/LocalAppData 配置、状态库、备份不在范围内；显式卸载时原有是否删除本地数据的选择不变。

验证使用仓库 ISCC 6.7.3 和现有三架构 publish 输出，没有重新 dotnet publish。两种部署模式的 x64、x86、ARM64 共六种正式安装器配置编译通过。此前 PowerShell 5.1 参数转发、Portable staging/marker 和 ProbeHost PE 检查通过。

此次 Windows 验证使用临时目录与普通用户 Inno harness，直接包含生产 UpgradeDeployment.iss 和 INI 定义；完整文件阶段使用真实 SC runtime、真实 FD publish payload。没有注册/停止真实产品服务，也未启动产品程序。执行并回读的结果：

| 场景 | 实际结果 |
| --- | --- |
| 本地有效 SC 标识、没有卸载注册 → FD | 删除清单中的 239 个旧 runtime 文件；FD payload 安装后匹配实际发布文件，Complete=1、退出码 0；FD 本身需要的 6 个同名程序集重新安装并按 incoming hash 校验 |
| 旧版无标识；AppId、架构、路径、Complete、模式、清单校验错误 | 检测按 Unknown 放行、清空清理名单；损坏清单的实际覆盖安装零额外删除、Complete=1、退出码 0，不声称旧 runtime 已消除 |
| runtime 文件被真实读句柄锁定 | 预检零删除；即使 Inno 忽略 BeforeInstall 错误继续文件阶段，最终仍 Complete=0、退出码 1、不执行 postinstall 启动项 |
| 清理完成后 payload 被临时测试代码损坏 | 239 次实际删除后验证失败，Complete=0、退出码 1、不执行 postinstall 启动项 |
| 中文/西里尔自定义路径 | 标识实际起始字节为 FF FE，路径完整回读，文件安装成功；初始 22,480 字符清单也经生产检测验证 |
| 部署标识卸载 | 实际运行临时 Inno uninstaller，本 AppId 标识被删除；被改为其它 AppId 的标识完整保留 |

上述文件测试回读确认 unins EXE/DAT 哨兵、未知 DLL、Data 哨兵保留；这些不是旧版真实卸载日志兼容实测。临时脚本、夹具和故障注入代码不进入生产，验证后删除。Inno 错误对话框可忽略 BeforeInstall/AfterInstall 异常、ssPostInstall 异常不改变默认成功退出码等行为通过实际故障测试确认，未假定安装具有完整回滚。

仍须在隔离 Windows VM 用 /LOG 验证真实两种 clean installs、同模式升级、双向切换、旧版升级、重装、自定义路径、多次升级后卸载、取消及中断；检查部署文件、实际 Inno AppId 卸载注册/日志追加、SCM/pipe/Frontend/TrayHost、数据和备份。FD 在全局 Desktop Runtime 下启动，SC 在无全局 runtime 环境启动。还需验证不同真实服务路径/架构、Portable 目录拒绝、只读标识、提权与非提权差异。当前没有隔离 VM，未进行产品的实际提升安装；真实服务恢复、应用启动及完整产品升级/卸载周期仍未验证。
## 8. GitHub Actions

`.github/workflows/manual-release.yml` 是当前手动发布 workflow。它通过 `workflow_dispatch` 接收 `configuration`、`app_id` 和可选的 `commit_sha`，主要流程是：

```text
resolve-metadata
-> Checkout main / 解析可选 commit_sha / Checkout 固定 source_sha
-> Resolve-Version.ps1
-> Beta version gate（仅 Beta 配置，见下文）
-> New-ReleaseNotes.ps1
-> build matrix
-> Scripts/Build-Target.ps1
-> 上传 installer / portable artifacts
-> 汇总 artifacts
-> New-ChecksumTable.ps1
-> 复查同名 Release / tag
-> 使用 source_sha 创建 tag 和 draft release
-> 验证 tag 的最终 commit
```

`Beta version gate` 步骤只在 `configuration == Beta` 且 `should_create_release == true` 时执行，调用 `Scripts/Test-BetaVersionGate.ps1` 查询 GitHub API 获取最新已发布的非预发布 Release，比较当前 Beta 基础版本与最新 Stable 版本。如果 Beta 基础版本 ≤ Stable 版本，workflow 直接报错，阻止后续构建和 draft release 创建。该门禁的原因和包管理器版本策略见 [包管理器发布说明](./package-managers.md)。

构建矩阵覆盖 `win-x64`、`win-x86`、`win-arm64` 的 self-contained 和 framework-dependent installer，以及 framework-dependent portable。

`.github/workflows/publish-package-managers.yml` 是包管理器发布 workflow。它在 GitHub Release 从 draft 被维护者手动发布后，通过 `release.published` 触发，读取已公开的 Release assets，生成并发布 Scoop / winget manifests。Scoop 和 winget 的详细渠道、变量、secret、dry-run 和首个 Beta 验证流程见 [包管理器发布说明](./package-managers.md)。

### Release 源 commit 和重入保护（#137）

Run workflow 有两种模式，均从 `main` 检出起点：

- `commit_sha` 留空：使用这次运行开始时检出的 `main` HEAD，立即固定为 `source_sha`。后续即使 `main` 有新提交，本次各个 job 仍构建原先选定的 SHA。
- `commit_sha` 输入 7–40 位十六进制 SHA：必须可唯一解析为 commit，且位于 `main` 的祖先历史中；不接受其它分支独有、无效或歧义 SHA。选择后再次 checkout 指定 commit，因此版本、发布说明、编译和打包脚本均取自该 commit。旧 commit 的 `Directory.Build.props` 版本决定 Release tag 和产物版本，不使用当前 `main` 的新版本号。

`resolve-metadata` 把准确的 `source_sha` 传递给全部七个 build matrix 目标及 `create-release`；所有构建从同一提交执行。创建 Release 时向 `softprops/action-gh-release` 显式传入 `target_commitish: source_sha`，若 tag 不存在，由 GitHub 创建指向该提交的 tag；若 tag 已存在，必须预先验证该 tag 解引用后恰好指向 `source_sha`，绝不移动现有 tag。创建 draft 后再次读取远端 tag 核验。GitHub API 对已经存在的 tag 会忽略 `target_commitish`，所以不能只添加该参数而不检查 tag。

防止 #137 重现：`resolve-metadata` 在构建前、`create-release` 在上传前分别分页列出 GitHub Releases（含 draft，不能只用按 tag 查询接口）；同名 Release 已存在时，无论是 draft、published 还是 immutable，**直接失败**，不允许任何重新生成的正文覆盖它。最终上传 job 按 release tag 分组串行化并禁止 cancel-in-progress，以封闭两次 workflow 同时通过早期检查的竞态。上传动作还设置 `overwrite_files: false` 与 `fail_on_unmatched_files: true`。取消/失败的 build 不会修改已经存在的 Release。若没有 Release 但已有同名 tag，只有 tag 指向同一 `source_sha` 才允许继续。tag 不存在属于正常首次发布，不得因 Git 命令返回非零状态导致 PowerShell step 误报失败。

发布流程仍然先创建 **draft**，由维护者手动发布，再触发现有 Scoop、winget 与 GitCode 下游流程；不自动 publish，也不改变现有矩阵、安装器 GUID 或 stable/beta 渠道逻辑。

权限：默认使用 `github.token`（`contents: write`）。如 GitHub 因旧提交包含 workflow 文件差异而拒绝为目标 commit 创建 tag/Release，可设置可选仓库 secret `RELEASE_TOKEN`，使用具备所需仓库 Contents/Workflows 写入权限的 token；不允许遇到权限不足时静默改用 `main` HEAD。运行失败的 tag 创建与 draft 创建需要检查 GitHub 状态后再处理，不以 workflow steps success 代替最终资产与校验表的真实验证。

手动验证需要覆盖：默认 main 模式、指定祖先 SHA、非 main SHA/歧义 SHA、已有 tag 指向不同提交、重复触发未发布 draft、已发布 immutable Release、同时运行两次相同 tag、旧 commit 创建 tag 的 token 权限，以及 tag/Release/资产实际 commit 与散列一致性。当前仅修改 workflow 配置与文档，尚未实际运行 GitHub Actions 端到端发布。

### GitHub Release 同步到 GitCode

GitHub Release 是发布元数据和用户下载附件的唯一 source of truth；Git 仓库镜像与 Release 附件镜像是两件独立的事情。`.github/workflows/manual-release.yml` 仍然只负责构建、生成 Release body/checksum 表并创建 GitHub draft Release。维护者发布该 draft 后，`.github/workflows/sync-gitcode-release.yml`（Actions 页面名称为 **Sync Release to GitCode**）会由 `release.published` 自动触发，将已发布的 GitHub Release 同步到 `PLFJY/ContextMenuMgr` GitCode 仓库。

同步 workflow 只使用 AtomGit / GitCode API 2.0 的 `https://api.atomgit.com/api/v5`，并需要仓库 secret：

```text
GITCODE_ACCESS_TOKEN
```

令牌仅以内存中的 `Authorization: Bearer` 请求头发送；不要改用 query string token，也不要新增第二个 GitCode token secret。

同步会重新从 GitHub Release API 查询指定 tag，因此自动和手动路径使用完全相同的逻辑：读取 tag、名称、原始 Markdown body、prerelease 状态、目标 commit，以及 GitHub Release API 中的实际 uploaded assets。Actions artifact 和 GitHub 自动生成的 source archive 不会被当作要镜像的附件。Release body 使用 UTF-8 JSON 序列化传递，不会重新调用 `New-ReleaseNotes.ps1` 或从提交重新生成内容。

为方便首轮验证、补历史 Release 和故障恢复，workflow 也提供 **Run workflow**。唯一输入是 GitHub Release tag（例如 `v1.7.1`）；它是一次实际同步，不是 dry run。手动输入仍为 draft 的 GitHub Release 会被拒绝。

创建 GitCode Release 前，脚本会通过 `GET /repos/:owner/:repo/tags` 分页查找同名 tag，并校验它解析到与 GitHub Release tag 相同的 commit。GitCode API 2.0 没有提供按名称 GET 单个普通 tag 的接口；`/tags/:tag_name` 只用于 DELETE，不能用 GET。等待使用有界重试（默认 15 次、每次 12 秒）；脚本绝不会为使 Release 创建成功而把 tag 指向 `main` 或另一 commit。若镜像尚未同步，workflow 会明确失败，之后可用同一 tag 再次手动运行。

同步按 tag 查询 GitCode Release：不存在则创建，存在则 PATCH 更新。创建请求发送文档支持的 `tag_name`、`name`、`body`、`target_commitish` 和 `release_status`；PATCH 只发送其文档支持的 `name`、`body` 和 `release_status`。GitHub prerelease 映射为 GitCode `release_status=pre`，稳定版映射为 `release_status=latest`。对于已存在的 Release，API 不支持通过 PATCH 修改 tag 或 `target_commitish`，因此最终校验发现目标 commit 不一致时会失败并明确报告，而不是伪造同步成功。

附件上传前会先完整下载 GitHub Release 的真实附件到临时目录，并验证每个文件名、大小及 GitHub 提供的 SHA-256 digest（若有）。随后脚本通过 GitCode Release 的 `upload_url` 接口获取文档所述的 `url` 和 `headers`，使用原始文件内容执行 HTTP PUT，并且只发送返回的 `x-obs-meta-project-id`、`x-obs-acl`、`x-obs-callback` 和 `Content-Type` 四个对象存储 header。对象存储 endpoint 不会收到 GitCode API Bearer token，signed upload URL 也不会写入日志。

手动重跑是幂等的：脚本会按精确文件名枚举 GitCode 已有附件，只上传缺失的文件。例如六个附件已有四个时，重跑只会补传剩余两个。GitCode API 的 Release attachment schema 没有可用于安全替换的大小/checksum 字段时，已有同名附件不会被重复上传；若同名附件重复导致状态不明确，脚本会失败而不是声称附件已完全一致。最后会再次读取 GitCode Release，验证元数据和所有 GitHub uploaded asset 文件名；缺少任何预期附件都会使 workflow 失败。每次运行会在 Actions summary 中记录创建/更新、已存在、已上传和最终验证计数，但不会包含 token 或 signed URL。

纯 PowerShell 判定逻辑由以下命令本地验证，不会访问或修改 GitCode：

```powershell
.\Scripts\Test-Sync-GitCodeRelease.ps1
```

## 9. 构建排错

| 问题 | 优先检查 |
| --- | --- |
| 缺 .NET SDK | `dotnet --info`，workflow 使用 `10.0.x`。 |
| Inno Setup 找不到 | `Scripts/Build.Common.psm1` 的 `Get-InnoSetupCompilerPath` 搜索路径。 |
| ProbeHost 缺失 | `ContextMenuMgr.Frontend.csproj` 的 native MSBuild 目标和发布目录内容。 |
| ProbeHost 架构错 | `Scripts/Verify-ProbeHostArchitecture.ps1` 输出和 `ProbeHost\<arch>` 目录。 |
| ARM64 installer 在 ProbeHost 阶段失败 | 安装 Visual Studio C++ ARM64 build tools 和 Windows SDK；脚本会在调用 MSBuild 前检查 `arm64\cl.exe`。 |
| C++ ToolTask 报 `Key in dictionary: 'Path' Key being added: 'PATH'` | 使用当前构建脚本重试；native MSBuild 调用会传入规范化后的 PATH 环境。 |
| artifacts 目录污染 | 清理 `build\publish-runs`、`build\publish`、`build\dist` 后重试。 |
| framework-dependent 包运行时缺失 | 检查目标机器 .NET runtime 或安装包 dependency installer 设置。 |
| portable 目标参数错误 | 当前只支持 `-Kind portable -Platform anycpu -DistributionMode framework-dependent`。 |
