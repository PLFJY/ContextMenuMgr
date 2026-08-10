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

Frontend、Backend 和 Tests 的 Windows TFM 以 `windows10.0.17763.0` 为共同基线。Backend 用 `Microsoft.Windows.SDK.NET.Ref` 的 Windows Runtime 投影调用 `PackageManager.FindPackagesForUser` 发现打包 ShellNew 声明；不要把它替换成直接引用 `.winmd` 的旧 `Microsoft.Windows.SDK.Contracts`，后者在 .NET 5+ 会触发 `NETSDK1130`。

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

`Build-NativeProbeHostArtifacts.ps1` 会把 MSBuild Platform 映射到 label：`Win32 -> x86`、`x64 -> x64`、`ARM64 -> arm64`。每个 label 构建或增量跳过后都会立即读取目标 exe 的 PE Machine 并验证：`x86 -> 0x014C`、`x64 -> 0x8664`、`arm64 -> 0xAA64`。`Build-NativeProbeHost.ps1` 保留为单架构 helper。

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

`Invoke-InstallerBuildTarget` 会向 Inno Setup 传入应用版本、AppId、发布目录、输出目录、架构选项和是否启用 .NET dependency installer。framework-dependent 安装包会把 `MyUseDotNetDependencyInstaller` 设为 `1`。

self-contained installer 目标还会从同一个 installer publish 输出生成 portable zip。因为 installer publish 输出带 Installer 标记，脚本必须先复制到 staging 目录，再把 staging 中的 `ContextMenuMgr.package.json` 覆盖为 Portable，最后压缩该 staging 目录；不要直接压缩 installer publish 目录作为 portable 包。

## 8. GitHub Actions

`.github/workflows/manual-release.yml` 是当前手动发布 workflow。它通过 `workflow_dispatch` 接收 `configuration` 和 `app_id`，主要流程是：

```text
resolve-metadata
-> Resolve-Version.ps1
-> Beta version gate（仅 Beta 配置，见下文）
-> New-ReleaseNotes.ps1
-> build matrix
-> Scripts/Build-Target.ps1
-> 上传 installer / portable artifacts
-> 汇总 artifacts
-> New-ChecksumTable.ps1
-> 创建 draft release
```

`Beta version gate` 步骤只在 `configuration == Beta` 且 `should_create_release == true` 时执行，调用 `Scripts/Test-BetaVersionGate.ps1` 查询 GitHub API 获取最新已发布的非预发布 Release，比较当前 Beta 基础版本与最新 Stable 版本。如果 Beta 基础版本 ≤ Stable 版本，workflow 直接报错，阻止后续构建和 draft release 创建。该门禁的原因和包管理器版本策略见 [包管理器发布说明](./package-managers.md)。

构建矩阵覆盖 `win-x64`、`win-x86`、`win-arm64` 的 self-contained 和 framework-dependent installer，以及 framework-dependent portable。

`.github/workflows/publish-package-managers.yml` 是包管理器发布 workflow。它在 GitHub Release 从 draft 被维护者手动发布后，通过 `release.published` 触发，读取已公开的 Release assets，生成并发布 Scoop / winget manifests。Scoop 和 winget 的详细渠道、变量、secret、dry-run 和首个 Beta 验证流程见 [包管理器发布说明](./package-managers.md)。

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
