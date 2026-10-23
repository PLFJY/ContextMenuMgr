# 进程、权限、用户上下文与 IPC

本文负责权限/身份通道和 Named Pipe 契约；具体业务事务见各子系统。源码从 [NamedPipeBackendServer](../ContextMenuMgr.Backend/Services/NamedPipeBackendServer.cs)、[NamedPipeBackendClient](../ContextMenuMgr.Frontend/Services/NamedPipeBackendClient.cs)、[BackendUserContextResolver](../ContextMenuMgr.Backend/Services/BackendUserContextResolver.cs) 开始。

## 四条链路

| 链路 | 用途 | 上下文 |
| --- | --- | --- |
| A：Frontend → Backend Pipe → Service | 普通扫描、mutation、审核、SpecialMenu、Win11、AutoStart policy、Explorer restart、数据 ACL repair | 服务有权限，用户来源由认证 Pipe/WTS 明确解析 |
| B：Frontend → UAC runas → BackendServiceBootstrapper | 安装/修复/卸载/停止/startup mode；Pipe 不可用时仅数据 ACL repair fallback | `--user-sid` 指明用户；提升进程 HKCU 不能代替它 |
| C：Service → WTS token → CreateProcessAsUser | 用户 Session TrayHost/Frontend 启动与激活 | SID 读 policy，SessionId 指向桌面 |
| D：Frontend → ProbeHost → Shell Extension COM | Deep Analysis 隔离，非提权 | 不进入普通扫描，不写注册表、不执行菜单命令 |

B/C 的命令、SCM/启动策略和通知完整说明见[生命周期](runtime-lifecycle.md)，D 的协议/架构和隔离边界见[ProbeHost](deep-analysis-probehost.md)。不要用一次性 bootstrapper 替代普通 runtime backend，也不要从服务 Session 显示 UI。

## 身份解析与模拟边界

权限、SID、hive 和 SessionId 是四个维度。LocalSystem 的 HKCU 不是前端用户 HKCU；用户写入明确选 `HKEY_USERS/<SID>`，不因权限高而扩大到 HKLM。显示名/路径不是认证依据。

`TryResolveFromPipeClient` 的同步 `RunAsClient` callback 只取得 token SID；**不能在 callback 中读取 WindowsIdentity.Name 或引入受管理 impersonation helper/异步任务**。显示名在 callback 外 SID.Translate，SessionId 从 Pipe 客户端 PID 解析，profile 从 ProfileList 读取。原 Name 读取可将 token 经 AsyncLocal 流到下一 await；当前边界由真实 Pipe 身份回归保护。该机制不证明历史所有 error 5/1314 都有同一原因。

| 请求 | 当前身份策略 |
| --- | --- |
| GetSnapshot、Win11 专用 query/mutation，通用 SetEnabled/ApplyDecision 的 Win11 请求 | 认证 Pipe 用户，不 fallback 到另一登录用户；Win11 blocked mutation 不需要 SessionId |
| ShellNew | 认证 SID；merged HKCR 另验证 WTS token SID/SessionId、profile/hive loaded；无用户/部分发现拒绝，不回服务 HKCR |
| OpenWith snapshot/mutation/inspection/review | 仅认证 SID、loaded hive/native view；HKLM 项也不允许缺用户，不需要 SessionId |
| 部分 classic/其它用户请求 | resolver 仍有交互用户 fallback，必须逐 handler 检查，不宣称全局已禁用 fallback |
| Restart Explorer | Pipe session context，匹配用户的 session fallback；只处理该桌面 |
| 后台常规 monitor | 每轮交互用户 fallback；缺上下文保留 baseline，不隔离/纠偏/清理；OpenWith 另维护多 SID |

ShellNew 的 `FrontendUserClassesRoot` 在同步退出即恢复的服务 scope 中取 WTS token，不启用进程权限，持有 token/merged handle 至发现结束。WTSQueryUserToken 需要 LocalSystem/SeTcb，RegOpenUserClassesRoot 返回独立状态码并需要已加载 profile；失败必须区分 API 阶段/native error、incoming/process identity、权限 present/enabled 和期望/实际 SID/session，不能从总错误猜缺哪个权限。

受保护 machine ShellVerb 的私有 token、最小 rights 和安全描述符恢复见[注册表模型](registry-model.md#受保护机器-shellverb-的窄写入路径)；不得把该窄 fallback 泛化成 HKU、ShellNew 或普通读取的 ownership reset。

## Pipe 格式、权限与演进

[PipeConstants](../ContextMenuMgr.Contracts/PipeConstants.cs) 定义 backend `ContextMenuMgr.Backend`、frontend control 和 TrayHost control 名称。backend 使用 byte-mode async NamedPipeServerStream，UTF-8 一行 JSON `PipeEnvelope`，MessageType/CorrelationId 加 Request、Response 或 Notification。每个普通客户端请求独立连接，通知保持订阅连接。

服务 Pipe ACL 给 LocalSystem/Administrators FullControl，Authenticated Users ReadWrite/CreateNewInstance。它不是仅本产品二进制可访问的能力令牌；请求中的路径/SID/DTO 仍是不可信输入，handler 必须验证物理身份和权限边界。`RuleUserSid` 等字段存在不代表客户端可以冒充任意用户。OpenWith 通知另按认证 SID 过滤，不把这项保护推导成所有通知均按用户隔离。

[PipeCommand](../ContextMenuMgr.Contracts/PipeCommand.cs) 以枚举数值传输，当前使用默认 System.Text.Json，没有通用协议版本协商。新增命令追加，不能插入/重排既有值。新增字段考虑缺失/null/旧客户端语义；未知或缺必需字段由服务失败响应，不允许拆成有部分成功风险的兼容写循环。`PipeResponse.Success/Message/ErrorCode` 与 `Item/Items/SpecialItem` 是不同证据，成功需要子系统实际读回/保存；不能把空 payload 当权威状态。

Win11 应用命令的旧服务拒绝与旧 Id/状态兼容见[Win11](windows11-context-menu.md)；OpenWith ExpectedEditStamp 缺失拒绝编辑，ShellNew nullable lock Unknown 不能默认为 false，各归子系统主文档。

## 请求、迟到通知与不确定结果

每个请求有 `CorrelationId`；客户端为未提供的 `ClientOperationId` 生成 ID 并登记。后端响应继承请求 ID，成功状态变化先广播再尝试直响应，因此 Pipe 断开不能吞掉已成功 mutation 通知。独立 monitor 通知没有本地操作 ID。

`RecentClientOperationCache` 在途项不按时间过期，成功结束保留十秒以抑制本实例重复广播；超时/失败删除，使迟到成功通知能到达。其它实例没有该本地缓存仍更新。OpenWith DTO 只失效快照；普通 workspace 的 generic ItemStateChanged 只更新已存在 Id，不引入 scene-only 项。

catalog `_persistentStateGate` 串行状态敏感工作，前端多连接不意味后端所有 mutation 并行。`GetContextMenuItemState` 在该 gate 内重新解析原物理 HKLM/HKU 来源与 handler 身份，合并持久状态；classic 查询不枚举 Win11，原来源消失时报缺失，不换另一 hive。

| 等待预算 | 当前值/处置 |
| --- | --- |
| classic SetEnabled | 20 秒；超时后另用 10 秒 GetContextMenuItemState 核对原物理来源 |
| Win11 应用 mutation / snapshot | 45 / 20 秒；核对全部目标，成功完整 Items 直接合并 |
| ApplyDecision | 45 秒；超时另读快照核对，未确认时短暂拦截重复提交 |
| ShellNew / OpenWith | 有各自协作预算、单飞/串行门及 read-back，见主文档 |

预算定义见 [BackendRequestBudgets](../ContextMenuMgr.Frontend/Services/BackendRequestBudgets.cs)。超时/取消表示停止等待，不证明后端没写；关闭取消可跳过后续查询。确认请求状态才成功，确认旧状态才回退，核对不可用则显示 Uncertain 并阻止再次写；fresh notification/refresh 解除。UI item version 防旧失败任务覆盖新权威结果，细节见[前端](frontend-wpf-ui.md#异步操作与结果)。不可盲重试不确定 mutation。

## 验证与维护

检查相同 CorrelationId/ClientOperationId 的两端日志、物理路径/native view、请求完成和响应投递，`DirectResponseWriteFailed` 不是 mutation 必败。已有 `MutationOutcomeTests`、`FrontendControlClientTests` 及身份/SID fixture 保护并发/超时/认证边界；真实跨用户 Pipe、SCM、profile unload 和桌面激活仍须 VM 验收。

修改 PipeCommand/DTO、认证/fallback、广播、等待预算或 privilege flow，更新本文及受影响子系统；IPC 不用日志分类 allow-list 决定业务 routing。
