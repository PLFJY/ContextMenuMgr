# 维护文档导航

本目录面向维护者与 Coding Agent，描述当前实现、必须保持的契约及尚待验证的边界；普通用户入口是[中文 README](../README.md) / [English README](../README.en.md)。先读 [AGENTS](../AGENTS.md)，选受影响责任的一份主文档，再沿其源码入口检查；不要求成组预读大指南。

## 架构与跨层契约

| 任务 | 权威文档 / 范围 | 何时追加相邻文档 |
| --- | --- | --- |
| 找进程、入口、模块所有者 | [架构与模块职责](architecture-overview.md) | 定位后转下表具体机制 |
| PipeCommand/DTO、SID/hive、Session、impersonation、UAC 通道 | [进程、权限与 IPC](process-and-privilege-flows.md) | 涉及物理写入读相应 backend；SCM/WTS 启动读生命周期 |
| state schema、baseline、审核、外部变化、reset、损坏恢复 | [状态与监控](state-and-monitoring.md) | 物理 provenance/backup 读 registry/Win11；OpenWith 授权读其专文 |

## Backend 菜单机制

| 任务 | 权威文档 / 责任 | 应读时机与相邻文档 |
| --- | --- | --- |
| classic shell/shellex、scene/文件类型、级联、增强字典、删除/Undo、Registry Write Protection | [注册表模型](registry-model.md) | 改扫描/开关/ACL/恢复先读；状态分类看状态文档，token/身份看 IPC |
| Packaged COM、SystemCommandStore、应用/CLSID、Blocked 备份/回滚、#114 边界 | [Win11 菜单](windows11-context-menu.md) | 改发现/阻止/恢复或专有页面；不能套 classic 删除 |
| ShellNew merged HKCR、物理来源、排序、ACL lock、恢复 | [ShellNew](shellnew.md) | 新建发现/锁三态/Classes 损坏；用户 token 细节看 IPC |
| Applications、只读关联来源、visibility/edit stamp、监控、审核与显式授权 | [OpenWith](openwith.md) | 打开方式问题先读；schema/存储合并看状态文档 |
| SendTo、WinX、DragDrop、CommandStore、GuidBlock、IE、WPS/Office | [SpecialMenu](special-menus.md) | 这些入口的 mutation/文件/hash/共存问题；ShellNew/OpenWith 不在此重复 |

## Frontend 与运行时

| 任务 | 权威文档 / 责任 | 应读时机与相邻文档 |
| --- | --- | --- |
| MVVM/DI、WPF-UI、theme/localization、Modern 导航/滚动、搜索/过滤、busy/outcome/cleanup | [前端](frontend-wpf-ui.md) | UI/template/binding 首选；子系统特有结果回读看其专文 |
| 安装/修复/卸载服务、SCM recovery、AutoStart、WTS、TrayHost、激活/关闭、Explorer | [运行时生命周期](runtime-lifecycle.md) | 服务与桌面进程问题；文件部署升级看安装器 |
| RuntimePaths、packageKind、data ACL、迁移/host identity、日志滚动/Locks/清理 | [运行时数据与日志](runtime-data-and-logging.md) | 改路径、记录/收集日志或 Portable 搬迁；state parser 看状态文档 |
| Deep Analysis、native COM 隔离、架构选择、图标与失败边界 | [ProbeHost](deep-analysis-probehost.md) | 探测/协议/超时问题；构建布局看构建发布 |

## 构建、发布与维护

| 任务 | 权威文档 / 责任 | 应读时机与相邻文档 |
| --- | --- | --- |
| setup、版本、SC/FD、多架构、产物、source pin、GitHub Release、GitCode 同步 | [构建与发布](build-and-release.md) | 改脚本/csproj/YAML/发布路径；当前 GitCode 仅手动触发 |
| Inno deployment.ini、runtime inventory、升级清理/中断/恢复 | [安装与部署](installer-deployment.md) | 改 installer/SC↔FD/旧安装兼容；SCM bootstrap 看生命周期 |
| Scoop/winget、真实资产 hash、manifest、验证、dry-run、bucket/fork/PR/平台接受 | [包管理器](package-managers.md) | 查生成/提交/审核边界和变量/secret 要求；不推断已配置 |
| 不明确故障、采证、权限阶段、跨层排错路由 | [排错](troubleshooting.md) | 症状入口，不是第二份机制规格 |
| 证据等级、现有测试入口、Windows/外部验收、文档检查 | [维护与验证](maintainer-workflow.md) | 需要安排验证或区分自动与现场结果；代码/测试留存硬规则只在 AGENTS |

## 阅读与维护约定

- 当前源码链接支撑实现；“必须/不得”为契约，历史数据兼容写明退出条件；“待验收/证据不足”不能被实现存在替代。真实 Windows/Explorer/SCM、硬件和外部 PR/CLA/发布状态要独立验证。
- 同一机制只有一份主解释，其它文档链接；已有责任优先整合原文，新增文件须有长期独立用途。文件按责任命名，不按 issue/日期组织，不无限追加修复附录。
- 行为/格式/安全/恢复变化同步受影响主文档。保留仍支持的旧数据、服务名、provenance/ACL/lock writer 桥接；不因“旧”删除兼容，不保留已替换实现副本。
- 源码路径、相对链接、heading anchor 和根入口保持有效。历史调查/完成的计划/临时 audit/harness 结果留 PR/commit/对话，不作为仓库新报告；文档-only 变更不构成 runtime 或外部平台验收。
