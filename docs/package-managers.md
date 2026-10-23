# Scoop 与 winget 发布

本文负责已发布 Release → 下载/hash → manifest → 验证/artifact → 外部仓库发布的阶段边界。上游 source pin、构建资产和 GitCode 同步见[构建发布](build-and-release.md)，安装身份/升级见[安装器](installer-deployment.md)。

## 入口与阶段

[publish-package-managers.yml](../.github/workflows/publish-package-managers.yml) 监听 `release.published`，也有 `workflow_dispatch(tag, dry_run=true)`。上游只创建 draft，必须维护者手动 publish 后再处理公开可下载 assets，不能由 tag push/draft 创建替代。

```text
release event / published tag
→ Resolve-PackageRelease
→ Get-ReleaseAssetHashes（真实下载）
→ New-ScoopManifest → Test-ScoopManifest
→ New-WingetManifest → Test-WingetManifest
→ upload package-manager-manifests-<channel> artifact
→ Scoop clone/copy/diff → 可选 push
→ 启用时 winget sparse target preflight/validation → 可选 fork push / PR
```

源码入口位于 [Scripts/PackageManagers](../Scripts/PackageManagers)。生成、验证、artifact 与 publication 是分开的成功条件；Scoop 写入、winget 分支写入、PR 创建、外部审核/索引可用都不是同一状态。workflow artifact 使用 `if: always()`，可包含失败前的部分输出，但上传步骤在两个外部发布步骤**之前**，不能期待它包含后续 fork/PR 失败或最终路径验证日志。

## 渠道与版本

`Resolve-PackageRelease.ps1` 以 GitHub prerelease=true→beta、false→stable，没有跨渠道 override。Stable 发布只更新 Stable，Beta 不自动转到对应 Stable，另一渠道文件保留。Beta 基础版本必须高于最新已发布 Stable，由上游 `Test-BetaVersionGate.ps1` 查询 releases/latest；不存在 Stable 才跳过，API/权限失败不能解释成无 Stable。

| 渠道 | Scoop app | winget PackageIdentifier |
| --- | --- | --- |
| Stable | contextmenumgrplus | PLFJY.ContextMenuMgrPlus |
| Beta | contextmenumgrplus-beta | PLFJY.ContextMenuMgrPlus.Beta |

Stable PackageVersion 是 tag 去 v；Beta 使用 published_at 的 UTC 时间，如 `v1.8.0-Beta+abcdef0`、`2026-07-04T13:58:22Z` → `1.8.0-beta.20260704135822`。assetVersion 仍来自原 tag，包版本不等于文件名版本。

两个渠道共用安装 AppId `45156332-3408-47B7-B5D2-2567E5888F64`、服务名 ContextMenuManagerPlusService、安装目录 Context Menu Manager Plus，不支持并排安装。Scoop pre_install 互查另一 app，persist=Data；不同 app 名各自持久目录，不共享。

## 实际资产与 hash

[Get-ReleaseAssetHashes.ps1](../Scripts/PackageManagers/Get-ReleaseAssetHashes.ps1) 要求 GH_TOKEN，查询 Release assets，每个预期文件精确匹配一项、实际下载并计算 SHA256。只选择 SC，FD 虽在 Release 仍不用于 package manifests。

| 用途 | 文件名（arch = x64 / x86 / arm64） |
| --- | --- |
| Scoop | `ContextMenuMgrPlus-<assetVersion>-<arch>-self-contained-portable.zip` |
| winget | `ContextMenuMgrPlus-<assetVersion>-<arch>-self-contained-Setup.exe` |

Scoop architecture=64bit/32bit/arm64 各 URL/hash，无顶层 url/hash；不能选 FD anycpu zip 代替。manifest notes 提醒可能安装/修复服务，Beta 提醒回归风险。Scoop 使用 GPL-3.0-only。

`release-metadata.json`、`release-assets.json`、`scoop/*.json`、`winget/*.yaml` 和 staging `winget-validation-output.txt` 上传到 `build/package-managers` 对应 artifact。hash 是下载所得，不是 fixture；package 脚本没有重新验证主 EXE/installer PE 与 filename 的映射，仍需上游构建/layout 和实际安装检查。

## Scoop 生成、验证与写入

[New-ScoopManifest](../Scripts/PackageManagers/New-ScoopManifest.ps1) 生成单渠道 manifest；[Test-ScoopManifest](../Scripts/PackageManagers/Test-ScoopManifest.ps1) 检查文件名/版本、三架构、SC URL、license、Data、互斥 pre_install、shortcuts 的二维数组形状。

`ConvertFrom-Json` 会受到单对象/数组和管道枚举影响。此 validator 使用 `-NoEnumerate`，检查首个 shortcut 是 IList、重新序列化以 `[[` 开始；workflow 显式用 **pwsh** 调用。该参数不因脚本标注 Requires 5.1 就自动存在于 Windows PowerShell 5.1，不能把所有 package scripts 一概称为 5.1 端到端兼容。其它 JSON 输入按自己的结构处理，不以 `.Count` 单独推断数组类型。

[Update-ScoopBucket](../Scripts/PackageManagers/Update-ScoopBucket.ps1) 克隆 bucket，复制当前 manifest，显示 status/diff；DryRun 后返回，real-run 才 add/commit/push。token 只在真实写入时要求。当前不是 partial/sparse clone，bucket 异常膨胀/长路径仍可能失败。push 成功仅为 bucket 提交，不证明安装/upgrade、服务健康或该渠道真实可用。

## winget 生成、目标路径与 PR

[New-WingetManifest](../Scripts/PackageManagers/New-WingetManifest.ps1) 生成五文件：version、installer、locale.zh-CN（defaultLocale）、locale.zh-TW、locale.en-US；DefaultLocale=zh-CN，所有 PackageName=Context Menu Manager Plus，三架构 SC Inno installer。schema header 保留。

[Test-WingetManifest](../Scripts/PackageManagers/Test-WingetManifest.ps1) 先静态检查 SC/架构，再调用 `winget validate --manifest <dir> --ignore-warnings`，记录 winget version/完整输出/exit code。非零仍失败，不能只看成功字样。默认日志写在 manifest 目录**父目录**，可能改变 Git 工作区；检查 status，不把输出误提交。`Submit-WingetPr` 明确将最终路径验证日志放临时 workRoot 外围，避免混入 package directory。

目标目录：

- Stable：`manifests/p/PLFJY/ContextMenuMgrPlus/<PackageVersion>/`
- Beta：`manifests/p/PLFJY/ContextMenuMgrPlus/Beta/<PackageVersion>/`

[Submit-WingetPr](../Scripts/PackageManagers/Submit-WingetPr.ps1) 从 fork `--filter=blob:none --no-checkout` clone，设置 core.longpaths=true，fetch 官方 upstream/master --depth=1，cone sparse checkout 仅目标版本路径，并从该 upstream 建发布分支。这样避免完整 checkout 数十万文件和无关长路径，不能保证所有新路径/环境限制消失。

只复制五个 manifest，核对 identifier/version/type/文件集合，最终路径再次 winget validate；git status 所有 changed path 必须位于该版本目录。无 diff 不发 PR。real-run commit 后 `push --force-with-lease` 到按渠道/版本命名的 fork branch，查询同 head open PR；无现有 PR 才创建，正文由 [New-WingetPrMessage](../Scripts/PackageManagers/New-WingetPrMessage.ps1) 生成。

脚本不等待/处理 upstream checks、审核、CLA 签署或 merge/index refresh。创建 PR 不代表平台接受；维护者需查看实际 PR checks/reviews/CLA 状态并按平台要求处理，不能宣称 token 存在即已满足。部分失败可能已经 push 分支或创建 PR，重跑前核对远端 branch/commit/PR，避免重复或覆盖；本脚本不是外部事务。

## 配置与权限

以下是 YAML 默认值及使用点，**不是已核实的仓库设置**：

| 类型 | 名称 | 默认/用途 |
| --- | --- | --- |
| variable | SCOOP_BUCKET_REPOSITORY | PLFJY/scoop-bucket |
| variable | ENABLE_SCOOP_PUSH | true |
| variable | WINGET_FORK_REPOSITORY | 无；启用 PR 时必需 |
| variable | ENABLE_WINGET_PR | false |
| variable | PACKAGE_MANAGER_DRY_RUN | false；release 事件使用 |
| secret | SCOOP_BUCKET_TOKEN | real Scoop push 所需 bucket 写权限 |
| secret | WINGET_PR_TOKEN | fork push 与 upstream PR 权限，启用 real PR 必需 |
| runtime token | github.token → GH_TOKEN | 本仓库公开 Release 查询/下载，workflow contents:read |

正式前逐项确认配置、token scope/有效期/组织策略与 fork 可访问性；不要在日志暴露 token。public dry-run clone 与 real authenticated clone/push 是不同权限路径。

## Dry-run 与故障边界

手动运行输入已发布 tag、保持 dry_run=true。真实执行下载/hash、manifest generation/validation、artifact、Scoop clone/copy/diff；winget final target preflight 仅 ENABLE_WINGET_PR=true 才运行。dry-run 不 push Scoop/fork、不创建 PR，仍有网络和本地写入，不验证真实写入凭据。

workflow 用 hashtable `$arguments` splatting 添加 DryRun switch；脚本内 Git wrapper 的 string[] Arguments 作为 `& git @Arguments`，避免将整条命令重新解析。不要把 `-DryRun false` 当成关闭 switch 的可靠写法；新参数应按定义传递，real-run 分支必须独立验证。PowerShell binder、JSON shape、退出码和生成目录 status 都是验证对象。

当前没有覆盖整个发布 job 的远端事务/自动撤回，也没有以 release tag 串行所有 package-manager run 的 concurrency 配置。Scoop push 与 winget PR 可能一个成功另一个失败，并发同 tag 也可能相撞；失败后分别查看 bucket commit、fork 分支、PR 和平台状态再处理。网络/API、token、文件名版本/架构与 Stable/Beta 误选要分别诊断，不用 fixture/dry-run 成功代替 real-run。

## 验证与维护

[package-manager-script-tests.yml](../.github/workflows/package-manager-script-tests.yml) 在匹配 paths 的 PR 和 dev push 上运行离线 [Test-PackageManagerScripts.ps1](../Scripts/PackageManagers/Test-PackageManagerScripts.ps1)，包括版本、二维数组、渠道、locale 和架构生成。它使用假 URL/hash，不运行 winget CLI，也不验证上游资产/仓库权限。不要将它混入真实发布前生成阶段冒充真实资产验证。

```powershell
pwsh .\Scripts\PackageManagers\Test-PackageManagerScripts.ps1
```

真实验收依次保留：Release 类型/资产清单、下载 hash、两个 staging validator 输出、artifact、Scoop diff/push commit、winget 最终路径验证/fork commit/PR 链接、checks/review/CLA、最终 merge 与用户索引/安装。Stable/Beta 分开，验证三架构选择、SC/FD 不混、包内 Portable marker、Data 持久化和服务健康。没有执行到的阶段写未验证，不补推结论。

变更触发器、版本/渠道、资产名、manifest schema、变量/secret、验证输出或提交策略时更新本文；保留 docs/package-managers.md 稳定路径，因为现有 CI paths 直接引用它。
