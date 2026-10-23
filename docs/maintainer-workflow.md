# 维护、测试与验收流程

约束唯一入口是 [AGENTS.md](../AGENTS.md)，按[文档索引](README.md)选受影响责任并读当前源码；本文只负责工作与验证方法，不再复制权限、状态矩阵或子系统实现。

## 证据等级

| 等级 | 文档应如何表达 |
| --- | --- |
| 已核实现有实现 | 指向当前文件/入口，说明可观察行为及失败分支；存在代码不等于现场修复 |
| 设计契约/必须保持的不变量 | 明确“必须/不得”，与现有局限分开；代码未覆盖时记录缺口，不假装完成 |
| Windows/硬件/外部集成待验收 | 指出 OS/build、身份/权限、实际调用链与验收结果，未执行不能写通过 |
| 未解决假设 | 写 Current evidence is insufficient to determine the root cause，并列缺失日志/路径/复现 |
| 历史兼容 | 仅保留仍支持的格式/签名/行为、归属证据与明确退出条件；不堆旧修复流水账 |

历史 harness、测试数量和附件观察不能自动充当当前版本的验收结果。验收记录放 PR/commit/对话，避免追加“本次执行”成为长期文档事实。

## 改动前与完成检查

先保存只读 git status/diff，尊重已有用户改动。定位进程、flow、SID/hive/view、Session、存储/权限/第三方 COM 及 UI 范围，查对应时间日志或说明缺失。沿入口→handler→物理状态→持久化→通知/UI 阅读，先确定错误层，再选最小变更。

重构、测试准入/临时资产、生产测试钩子、兼容保留和清理严格遵循 AGENTS，不另维护第二套政策。必要兼容将 released version/格式、被支持行为、可观察退出条件和 Issue/维护决定写在负责该机制的文档中；历史版本不明则明确待核实，不猜版本下限。

完成前搜索被替换名称/paths/callers/resources/docs；删临时测试/产物和无兼容必要的重复实现，更新主文档/索引，review diff/status，报告删除原因、真正执行结果和未执行验收。源码未改不因文档改动新增永久测试/门禁。

## 本地自动验证

```powershell
dotnet build .\ContextMenuMgr.Frontend\ContextMenuMgr.Frontend.csproj
dotnet test .\ContextMenuMgr.Tests\ContextMenuMgr.Tests.csproj
git diff --check
git diff --stat
```

按变更范围选择现有有价值测试，不把全量运行当成默认必需；需要 no-restore 时先确认依赖已还原。Tests 引用实际生产服务与部分前端文件（见 csproj），不是独立部署的 app。Debug native 构建所需 C++/SDK 见[构建](build-and-release.md)。

| 风险 | 现有验证入口示例 | 仍需真实环境 |
| --- | --- | --- |
| registry mutation/ACL/provenance | ShellVerbMutationTransactionIntegrationTests、ClassicShellExtensionRegistryMoveTests、ProtectedRegistryMutationTests/PrivilegeTests、RegistryProtectionTransitionTests | 实际 LocalSystem 服务/原目标 ACL/外部安全软件 |
| state/identity/并发 | ContextMenuStateStoreTests、ExternalChangeStateMachineTests、MutationOutcomeTests、FrontendControlClientTests | 两 SID/Session、profile unload、真实 Pipe 丢响应/SCM |
| SpecialMenu/Win11 | OpenWithRegistrationTests、ShellNewOrderTransactionTests、Windows11BlockedMutationTests、Packaged*Tests | Explorer、真实安装器 timing、包更新与 Windows build |
| UI/滚动 | WheelScrollPolicyTests，前端构建 | WPF template/input/真实鼠标/触屏/触摸板 |
| 发布 | Test-PackageManagerScripts.ps1、Test-Sync-GitCodeRelease.ps1；Test-BetaVersionGate.ps1 是实时 API 门禁 | 真实 assets、外部写权限、PR checks/review/CLA、索引/安装 |

有些测试改变隔离子进程 token/临时 HKU/HKLM ACL；普通权限会 skip 需要 privilege/LocalSystem 的分支。必须报告 passed/failed/skipped 及 skip 原因，不为过测试改变已安装服务权限。Category=LocalSystem 仅在明确启动的可丢弃 VM/testhost 运行，使用 GUID 测试树和清理句柄，不操作真实 Windows menu keys。临时 Registry/文件夹 fixture 只清理自己创建的对象。

## 功能成功的分层验收

mutation 分别核对请求成功、物理路径/raw value/type/ACL、fresh authoritative read、state current/backup、重启与 UI/Explorer。保存失败可以通过锁住真实测试 state 文件、允许读取但拒绝 replace 触发，再查物理回滚，不往生产加故障 callback。外部并发变化时保留第三方结果并报告冲突，不弱化断言制造成功。

timeout、cancel、Pipe broken 先查询结果，不重写。硬杀/断电不能自动继承 catchable rollback 保证。ShellNew/Win11/OpenWith/Installer 每个主文档有各自恢复/未确认边界，验证不得相互代替。

外部 workflow 分别记录生成 artifact、验证输出、远端 commit/asset/PR 与最终平台接受；dry-run、manifest generation、PR creation 均不证明发布完成。工作范围若仅文档，不触发 releases、同步、bucket push 或外部 PR。

## 文档-only 验证

枚举保留的 Markdown，逐个检查 local link、相对 source path 和 heading anchor（忽略 fenced code 中的示例）；跨目录/URL 编码、重复标题、HTML id/src 也要检查。搜索删除/改名路径的全仓引用，避免漏 README/agent/CI paths。核对命令/默认值与源码/YAML，而不是为了通过链接检查修改 CI。

最后检查 diff 仅预期文档、无生成物、无安全/恢复/兼容信息意外删除。文档-only 不证明 Windows 集成或外部平台工作，不必为无代码改动运行应用/注册表 tests。维护方式参照 [neo-bpsys-wpf 文档索引](https://github.com/PLFJY/neo-bpsys-wpf/blob/main/docs/README.md) 的按需阅读/单一责任原则；它不是本项目实现事实来源。
