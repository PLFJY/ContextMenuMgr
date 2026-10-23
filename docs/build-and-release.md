# 构建、版本与 Release 自动化

本文负责源码准备、本地多目标构建、版本、GitHub Release 和 GitCode 附件同步；安装文件升级见[安装器](installer-deployment.md)，Scoop/winget 见[包管理器](package-managers.md)，ProbeHost 运行语义见[深入分析](deep-analysis-probehost.md)。

## 环境与入口

[ContextMenuMgr.slnx](../ContextMenuMgr.slnx) 包含 .NET 项目、Tests 与 native C++ ProbeHost。需要 .NET 10 SDK、Visual Studio C++ Build Tools/Windows SDK；发布 ARM64 还需 C++ ARM64 toolchain。Backend 通过 `Microsoft.Windows.SDK.NET.Ref` 投影调用 PackageManager；Frontend/Backend/TrayHost/Tests 当前 TFM 为 `net10.0-windows10.0.17763.0`，Contracts 为 net10.0。不要用直接 .winmd 的旧 SDK.Contracts 替代而引入 NETSDK1130；这与禁止为托盘引入 Windows App SDK 是不同约束。

[.gitmodules](../.gitmodules) 当前仅有 `Installer/InnoDependencyInstaller` submodule；ProbeHost 的 nlohmann/json 是 vendored header，不是 submodule。递归初始化保证依赖安装器内容；ISCC 位于独立的 `Installer/Inno Setup 6`，不能把缺少 ISCC 一概归因到该 submodule。

```powershell
git clone --recursive https://github.com/PLFJY/ContextMenuMgr.git
# 已 clone 时，在仓库根执行：
git submodule update --init --recursive
dotnet build .\ContextMenuMgr.Frontend\ContextMenuMgr.Frontend.csproj
```

| 入口 | 职责 |
| --- | --- |
| [build.ps1](../build.ps1)、[build-release.bat](../build-release.bat)、[build-beta.bat](../build-beta.bat) | 默认所有 installer RID/mode 加 FD anycpu portable；MaxParallel 默认 3 |
| [Build-Target.ps1](../Scripts/Build-Target.ps1) | 单目标/CI，显式 Kind/Configuration/Platform/DistributionMode |
| [Build.Common.psm1](../Scripts/Build.Common.psm1) | MSBuild 属性求值、restore/publish、隔离输出、native/ISCC、package marker、zip/inventory |
| [Frontend csproj](../ContextMenuMgr.Frontend/ContextMenuMgr.Frontend.csproj) | 普通 build 的 Backend/TrayHost 复制和 native helper 构建/验证 |
| [NuGet.Config](../NuGet.Config) | restore 源配置 |

ISCC 解析函数为 `Resolve-IsccPath`：先仓库内，再 Program Files/PATH。当前没有 build.bat。本地脚本会生成/清理指定 build workspace，文档示例不是外部发布授权。

## 版本

唯一手改来源是 [Directory.Build.props](../Directory.Build.props) 的 AppVersion。FileVersion 为 `<AppVersion>.0`，AssemblyVersion 固定 1.0.0.0；InformationalVersion 普通为 AppVersion，Beta 追加 `-Beta`，不由每个 csproj 独立维护。脚本 `Get-FrontendVersion` 通过 dotnet msbuild 求值 InformationalVersion，空时才 FileVersion；`Get-BuildVersion` 为 Debug/Beta 再追加 `+<short SHA>`。

[Resolve-Version.ps1](../Scripts/Resolve-Version.ps1) 生成 tag/name/prerelease/previous_tag/should_create_release；Release tag `v<version>`、名称 `<tag>-Release`，Beta prerelease=true，Debug 构建但不创建 Release。脚本会 fetch tags，不应作为无副作用验证命令。Beta 基础版本必须高于最新已发布 Stable，门禁查询/API 风险见[包管理器](package-managers.md#渠道与版本)。

## 本地构建与发布布局

```powershell
.\Scripts\Build-Target.ps1 -Kind installer -Configuration Release -Platform win-x64 -DistributionMode self-contained
.\Scripts\Build-Target.ps1 -Kind portable -Configuration Release -Platform anycpu -DistributionMode framework-dependent
```

独立 portable Kind 只接受 FD anycpu；**SC portable zip 是 SC installer 目标额外输出**。后者先复制 publish 到 staging，将 `ContextMenuMgr.package.json` 改为 Portable 再压缩，不直接压带 Installer 标记的目录。Installer/Portable 都须带显式 packageKind，缺失会按 Installer 运行。SC 携带 runtime，FD 依赖目标 Desktop Runtime，不能互换命名或选择。

restore/publish .NET Frontend、Backend、TrayHost；native ProbeHost 用 MSBuild vcxproj，不用 dotnet publish。并行目标传专属 BaseOutputPath/--artifacts-path，helper OutputPath 从该根派生，避免 apphost/runtimeconfig/deps 互抢。`build.ps1` 对 publish-runs 隔离、收集 dist/artifacts.txt；目标失败停止调度并从 generic list `.ToArray()` 快照停止剩余进程。不要把 PowerShell 5.1 的 `@($genericList)` 当安全等价实现。

### ProbeHost 架构与许可

| 目标 | 携带 native PE |
| --- | --- |
| win-x86 | x86 |
| win-x64 | x64、x86 |
| win-arm64 | arm64、x64、x86 |
| FD anycpu portable | 非 Debug：x86/x64/arm64；Debug：默认 x86/x64 |

普通 Debug 默认 NativeProbeHostPlatforms=Win32,x64；Release/Beta 默认加 ARM64。Debug 需要三架构时可配置项目属性 NativeProbeHostPlatforms 为 Win32,x64,ARM64，并安装对应 toolchain。下列命令只强制重建当前配置的架构集合：

```powershell
dotnet build .\ContextMenuMgr.Frontend\ContextMenuMgr.Frontend.csproj -p:ForceRebuildNativeProbeHost=true
```

[Build-NativeProbeHostArtifacts.ps1](../Scripts/Build-NativeProbeHostArtifacts.ps1) 兼容 Windows PowerShell 5.1，单进程解析 MSBuild 一次，按 label 检查 vcxproj/cpp/h/json.hpp 时间戳增量构建；csproj target 全部最新时直接跳过 PowerShell。clean/rebuild 清 native 输出。OutDir/IntDir 按目标和 x86/x64/arm64 隔离，普通本地在 `artifacts/probehost-native/<Configuration>`，发布在各目标 workspace。

MSBuild 前检查对应 cl.exe；外部进程环境规范化 Path/PATH，避免 C++ ToolTask 重复 key。不能假设 5.1 有 ProcessStartInfo.ArgumentList；共享实现含旧运行时参数路径。每 label 构建后检查 PE Machine（x86 0x014C、x64 0x8664、arm64 0xAA64），随后 [Verify-ProbeHostArchitecture.ps1](../Scripts/Verify-ProbeHostArchitecture.ps1) 验证整体。

runtime 只复制 `ProbeHost/<arch>/ContextMenuMgr.ProbeHost.exe`，无 managed DLL/deps/runtimeconfig/Contracts 依赖；`ThirdPartyNotices/nlohmann-json-LICENSE.MIT` 必须发布。json.hpp 编译进 EXE；WIC/windowscodecs.lib 编码 PNG，不另分发图片库。

## GitHub Release

[manual-release.yml](../.github/workflows/manual-release.yml) 只有 workflow_dispatch，输入 configuration、app_id、可选 7–40 hex commit_sha。

1. resolve-metadata 先 checkout main（完整历史），空 SHA 固定该次 main HEAD；指定 SHA 必须唯一解析且为 main 祖先，再 checkout source_sha。之后脚本、版本、release notes 与全部 build job 都取所选提交，不随 main 继续前进。
2. Resolve-Version、仅 Beta 的版本门禁、早期目的检查：分页列出 releases（含 draft），同 tag 已存在 Release 一律失败；已有 Git tag 必须解析到 source_sha，不移动 tag。
3. 七目标矩阵：三 RID × 两 mode installer，另 FD anycpu portable；三个 SC installer 各额外 zip，因此正常共十个 exe/zip。每目标上传 workflow artifact。
4. create-release 按 tag concurrency 串行且不 cancel-in-progress，再查目的；下载各 artifact/正文、flatten exe/zip、New-ChecksumTable 计算 SHA256 附正文。
5. softprops/action-gh-release 创建 **draft**，target_commitish=source_sha，overwrite_files=false、fail_on_unmatched_files=true。以 Release ID 回读 draft/tag/target、资产数量/精确名称、API digest 和正文 hash 表。
6. 维护者检查并手动 publish；此时才有包管理器 release.published 下游。draft 阶段 Git tag 未必已创建；当前 YAML 没有发布后 tag 回读 job，最终 tag commit 仍需维护者核验。

[New-ReleaseNotes](../Scripts/New-ReleaseNotes.ps1) 与 [New-ChecksumTable](../Scripts/New-ChecksumTable.ps1) 是正文生成入口。不要由 target_commitish 单独推断已有 tag 指向：GitHub 对已有 tag 忽略它。重复 draft/published/immutable Release 都不重写；无 Release 的同提交现存 tag 可继续。早期 guard、最终 guard、job concurrency 是当前防护，API/上传部分失败仍可能留远端对象，须查状态后决定恢复，不声称失败无副作用。

默认 github.token 需要 contents:write；可选 secret RELEASE_TOKEN 处理含 workflow 差异旧 commit 等权限需求，不能不足时静默改 main。源码只能证明 token 使用点，**不能证明 secret/权限已配置**。app_id 默认产品 GUID，修改其输入会影响安装身份，不是文档核查应执行的操作。

## GitCode Release 同步

[sync-gitcode-release.yml](../.github/workflows/sync-gitcode-release.yml) **当前仅 workflow_dispatch(tag)**，没有 release.published trigger。resolve-tag 中残留 release 事件分支、脚本支持 automatic/release Trigger 都不使事件自动发生。发布 GitHub draft 后必须维护者手动运行 Sync Release to GitCode。配置中未启用自动同步，不能从脚本分支推断触发器存在。

[Sync-GitCodeRelease.ps1](../Scripts/Sync-GitCodeRelease.ps1) 用 GitHub 已发布 Release/tag/实际 uploaded assets 为源，不重新生成正文，不镜像 Actions artifact/自动 source archives。需要 GH_TOKEN 和 GITCODE_ACCESS_TOKEN；只用 `https://api.atomgit.com/api/v5`，API Bearer 不放 query/对象存储 header，signed URL 不写日志。

流程：

- 拒绝 draft，解析 GitHub tag 实际 commit；分页查 GitCode tags 并等待同名同 commit（默认最多 15 次、间隔 12 秒），不创建/移动 tag 迁就发布。仓库 Git mirror 和附件 mirror 独立。
- 同名 Release 不存在 POST，存在 PATCH name/body/status；prerelease→pre、stable→latest。PATCH 不改 tag/target，最终不一致失败；409 创建冲突重新读并更新。
- 下载实际 GitHub assets，检查安全文件名/大小及可用 SHA256 digest；按精确名称只补缺失，重复同名状态拒绝。没有远端大小/hash 证据的已有同名附件只能跳过，不能证明字节一致。
- 取 upload_url 返回的 url/四 header，原始文件 PUT；仅 x-obs-meta-project-id、x-obs-acl、x-obs-callback、Content-Type，不转发 API token。可重试 HTTP 状态有界 retry；普通 API 传输异常可能直接失败，PUT 每次重新获取 signed URL；五分钟无读取进展取消，不限制有进展总时长。传输失败先查附件是否出现，再决定 retry，仍受远端回调延迟限制。
- 最终回读元数据/预期名称，summary 分别记录 uploaded/present/verified。它不等于再次下载远端字节 hash，也不证明 Git mirror 配置正确。

只有实际同步，没有 dry-run 参数。中途失败可留下 Release 元数据或部分附件；先检查平台同 tag/commit/名称/重复附件再手动重跑。不要将命名幂等当成远端内容完全可验证。自动配置、镜像进度、权限、API schema/网络均需真实平台核验；离线 [Test-Sync-GitCodeRelease.ps1](../Scripts/Test-Sync-GitCodeRelease.ps1) 仅覆盖判定逻辑。

## 故障与验证

| 风险 | 当前防护或限制 |
| --- | --- |
| PowerShell binding/splatting/外部参数 | 单目标有 ValidateSet；外部参数按参数数组传递，共享 wrapper 处理引号和环境；package workflows 用 hashtable splat。运行 5.1 与 pwsh 实际入口验证，不能靠同名参数或 dry-run 推断 real-run 分支 |
| JSON 单对象/数组形状 | Release/asset 处理多处显式数组归一，GitCode ConvertTo-ReleaseArray；不是全仓 universal parser，Scoop 嵌套数组约束见包管理器文档 |
| 架构/FD-SC/资产名称不符 | PE verifier、ValidateSet、精确 manifest asset match；仍需展开实际包核对 main/helper 架构和 runtime |
| checkout 体积/Windows 长路径 | 产品 source selection 用完整历史；winget 独立使用 sparse partial clone。优先短工作路径，不假设全仓开启 longpaths |
| 权限与部分远端成功 | token 不足即失败；检查 Release/tag/附件实际状态，不直接重跑覆盖已有 Release，不触发本任务范围外发布 |

安全离线验证可用 `Test-Sync-GitCodeRelease.ps1` 和包管理器 fixture；`Test-BetaVersionGate.ps1` 本身是实时 GitHub API 门禁，没有离线 fixture 开关，不能当成无网络测试；完整构建按上述入口、检查 package marker、native 布局/license、SC/FD 运行依赖和 Installer `/LOG`。不要为文档-only 检查运行 Resolve-Version 的 fetch、真实 Release、同步或 package push。

维护者端到端验证应覆盖 source pin/无效/非 main SHA、重复/并发 draft、不同提交现存 tag、token 权限、实际十资产与散列、手动发布/tag commit、GitCode 网络中断/补传，以及包管理器的 manifest/远端 PR/审核。本地生成/编译、fixture、dry-run 各仅证明自身阶段。

修改脚本、csproj copy/native target、版本或 workflow 时更新本文；更细安装与包管理行为只在对应权威文档维护。
