# OpenWith Applications 与来源检查

OpenWith 列表发现用户 / 机器 Classes 的 `Applications` 物理注册（含无常规 command、DropTarget 等只读结构），仅管理经过验证的常规结构及既有 Microsoft Store 入口 policy。扩展名来源检查按需读取独立关联来源，不承诺开关或删除后应用从每个 Explorer 打开方式列表中消失。操作走链路 A：Frontend -> Backend Pipe -> `SpecialMenuService`，不使用 UAC bootstrapper 或 ProbeHost；Applications 专用监控见下文。

必须区分三件事：应用注册为打开方式候选、Explorer 针对某个文件实际显示此候选、该文件类型的默认处理程序。页面的开关仅表示所选物理 `Applications` key 是否存在 `NoOpenWith`；用户 / 机器同名项分别显示和修改，不代表合并 HKCR 的最终菜单状态。微软明确说明，文件类型的 `OpenWithProgIDs` 可以在 Applications 设置 `NoOpenWith` 后继续提供应用候选：[Application Registration](https://learn.microsoft.com/en-us/windows/win32/shell/app-registration)、[Open With 注册与排除说明](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/legacy/hh127438%28v%3Dvs.85%29)。

## 来源与管理范围

| 来源 | 与当前管理范围的关系 |
| --- | --- |
| `HKU\<SID>\Software\Classes\Applications`、`HKLM\SOFTWARE\Classes\Applications` | 当前支持的物理注册。64 位系统固定读取 / 修改 Registry64，匹配 native Explorer；服务 x86 构建也不改用 32 位 view。 |
| Classes `.<ext>\OpenWithProgIDs` 及对应 ProgID command | 按文件类型提供候选，不随 Applications 开关或软删除改变；不删除、不修改这些 ProgID。参见 [Include an Application in Open With](https://learn.microsoft.com/en-us/windows/win32/shell/how-to-include-an-application-on-the-open-with-dialog-box)。 |
| 用户 `Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.<ext>` | `OpenWithList` / `OpenWithProgids` 是用户文件类型候选 / 使用记录来源；`UserChoice` 是默认选择证据。当前页不写入这些路径，不改 Hash。 |
| `Software\RegisteredApplications` -> 应用 `Capabilities\FileAssociations` | 应用声明支持的文件类型，可关联独立 ProgID；不能当作 Applications key 的副本，也不在本页修改。参见 [Default Programs](https://learn.microsoft.com/en-us/windows/win32/shell/default-programs)。 |
| AppX/MSIX `uap:FileTypeAssociation` 等声明 | 包注册可以提供文件类型处理能力；本页不管理包 manifest、包生成的 ProgID 或默认应用选择。参见 [FileTypeAssociation](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-uap-filetypeassociation)。 |

不通过 UserChoice / Hash、UCPD、ACL lock 或拦截安装器扩大此范围。不改 unrelated ProgID、Capabilities 或默认关联设置。如果默认调用路径本身依赖被删除的 `Applications\<app>` 注册，删除会影响该注册的调用能力；保留默认关联设置并不保证该程序仍可启动，默认选择应通过 Windows 设置管理。

WPS / Microsoft Office 共存保护不在 OpenWith 普通页面展示受保护文档扩展名 owner，也不在该页面提供默认应用切换。关联、图标、ShellNew 注入只作为 WPS 专用待审核项显示；`UserChoice` 只能作为证据读取，不能由本项目直接写入 Hash。

## 物理操作、归属与事务

| 行为 | 当前实现倾向 |
| --- | --- |
| 枚举 | 读取 `HKU\<SID>\Software\Classes\Applications` 和 `HKLM\SOFTWARE\Classes\Applications`，纳入带文件扩展名的应用 key；常规 command-bearing verb 保留旧物理 command Id。缺少 shell/verb/command、无可提取目标、DropTarget、DDE、DelegateExecute、ExplorerCommandHandler 或级联结构仍显示，但 CanEdit/CanDelete=false；开关可见但禁用，无删除/恢复操作，黄色标注“受系统保护”，并放入默认折叠的只读分组，用户点击后展开；搜索仍能筛选该分组，详细只读原因在 tooltip。无常规 command 的 Id 使用真实 app key，不制造 command。逐项访问失败保留只读物理路径。 |
| 显示名 | 优先解析物理注册的 `FriendlyAppName`（不要求 app key 名与命令目标文件名相同），否则读取目标文件 `FileDescription`，最后回退到文件名；没有目标时使用 FriendlyAppName 或原 app 名。DefaultIcon 优先，无法解析时允许空图标，不制造 exe 路径。 |
| 启用 / 禁用 | 禁用只在值不存在时写空 `REG_SZ NoOpenWith`，同时写独立 `ContextMenuMgrPlus.OpenWithVisibility` 归属值；两值在短生命周期 Windows 注册表事务中提交。启用只撤销匹配本物理路径、command generation 和值类型 / 内容的本程序写入；预存或第三方改写的值均保留并返回错误。 |
| 新增 | 写入当前前端用户的 `HKU\<SID>\Software\Classes\Applications\<app.exe>\shell\open\command`，并写 `FriendlyAppName`；名称、command 与所有新子键使用同一 TxR 事务；通过事务中已打开的现有 SID hive handle 创建子键，避免用户 hive 并发卸载时创建替代 SID 根。检查 leaf disposition 必须为新建，已有或安装器抢先创建的 key 拒绝覆盖。失败/提交前取消回滚整次创建，不清除已有 `NoOpenWith`。创建目标必须是无环境变量的绝对文件路径。 |
| 编辑 | 更新 `FriendlyAppName` 和 command 默认值；为避免把一个应用 key 改成另一个程序，当前不允许编辑时改变目标 exe 路径。先验证全部输入，再在同一 TxR 事务内校验 ExpectedEditStamp、原始 command 和支持的激活结构并写入两字段。保留现有 String/ExpandString 类型、其它值与 ACL。目标 exe 比较使用未展开的原始 token；不把 LocalSystem 环境展开结果当作交互用户的程序身份，因此即使环境表达式和绝对路径在服务进程中碰巧相同，也不能互相替换。图标环境变量交给前端用户上下文展开。命令不能在 visibility ownership marker 存在时改变代际；先启用该注册再编辑命令。 |
| 删除 / 恢复 | 对所选 app key 使用 Win32 `RegRenameKey` 重命名为 sibling `.deleted` / 恢复原名；保留完整树、原始 ExpandString、ACL 和开关状态。拒绝缺失源、目标冲突及无效 backup metadata，不覆盖现有备份 / 重新注册的原项，不提供 copy/delete fallback。成功必须回读源已不存在、目标存在，再返回从目标读取的 DTO。 |
| 永久删除 | 仅删除经过路径和请求状态校验的所选 `.deleted` 备份，逐层加入同一注册表事务；任一子键访问拒绝 / 并发冲突会回滚，提交后确认备份不存在。失败不报告成功，不触碰重新出现的原 app key。 |
| 应用商店入口 | 保留既有 `NoUseStoreOpenWith` policy 项；开关同步 HKLM 和当前用户 HKU policy 值并回读结果。某一 hive 失败时返回失败，已完成的 policy 写入可能仍存在，应刷新核对。 |

OpenWith 需要 SID，但不需要 SessionId。请求只能使用 Pipe 客户端认证身份；解析失败不退回另一个交互用户。快照和修改先验证 account SID 与已加载的用户 hive；即使所选项属于 HKLM，用户上下文缺失也拒绝操作。服务不能用 LocalSystem `HKCU`、重建未加载的 SID hive，或信任不匹配的 `AppRegistryPath` / `DeletedPath`。

后端串行化本程序 OpenWith 快照与 mutation，校验请求携带的 visibility stamp / command；注册表事务中的非事务并发写入会使事务失败，项目不会覆盖第三方结果。成功是提交后 fresh read-back 的结论，不是永久禁止第三方重建注册。事务创建 / 提交不可用时直接报错，没有放弃验证的 fallback。

前端呈现缓存仅加速显示，不恢复授权、provenance 或结果核对结论；缓存中的 rule-only/已删除恢复项仍由权威 OpenWith 快照决定生命周期。共享差量与动画见[前端](frontend-wpf-ui.md#列表缓存差量刷新与通知)。

前端串行化该页快照和操作；旧快照不能在成功 response 后覆盖状态。失败 / 超时后用独立快照核对物理状态，不重试 mutation；核对失败显示结果未确认并锁定该条目操作，后续成功刷新解除。迟到通知触发重新查询，不直接使用可能过期的 DTO；旧失败任务也不能撤销较新的权威更新。

兼容决定：保留已发布的 sibling `.deleted` 及历史 `.deleted.deleted` 等重复后缀恢复路径。备份和活动项使用不同物理 Id，冲突时两者都显示；恢复只选择 DTO 指定且属于原 app 的备份。旧版本没有记录完整性，历史残缺备份不能凭后缀判为完整；缺少 command 的备份作为已删除只读项展示，不能 Undo/Purge，但仍阻止覆盖。也不推断旧版空 `NoOpenWith` 的归属：无 marker 的值必须保留。Issue #136 的维护决定是继续支持这些数据；只有明确停止支持历史 sibling 备份 / 完成归属核实后才可退休，不能猜测发布版本或自动删除旧值。

## 按扩展名检查来源

页面输入如 .txt 后点击“检查来源”，通过新增 PipeCommand.InspectOpenWithSources（ScopeValue=扩展名）返回独立 OpenWithAssociationInspection DTO。检查结果不使用 SpecialMenuEntry.RegistryPath 或 IsEnabled 表示关联状态，也不提供直接关联修改按钮。所有证据只读，Resolved 仅表示物理引用已解析，不表示 Explorer 一定显示候选。

- Applications：只对所选扩展名检查 SupportedTypes；FileExts OpenWithList 的明确 app 名也可解析到 HKU/HKLM Applications。复用现有 Applications reader 判断可管理 Id，不把相同 exe 的 ProgID 猜成可删除的 Applications 注册。
- Classes 扩展名 OpenWithProgIDs、用户 FileExts OpenWithProgids：复用 ContextMenuRegistryCatalog.ReadOpenWithProgIdReferences 的原始来源读取。传统菜单 resolver 的 HasAnyContextMenuSubkey 过滤不适合这里，因此检查保留无 command 的 ProgID。后续按用户/机器物理 Classes 解析引用；两棵树都有 key 时显示 MultipleRegistrations，不猜合并后的有效处理程序。
- RegisteredApplications 与 Capabilities/FileAssociations：只读取所选扩展名，并分别保留声明值和间接 FileAssociations 值的准确路径。标识符分组不丢失独立物理来源。
- AppX/MSIX：只对认证用户的已安装包读取 manifest 中匹配扩展名的 FileTypeAssociation，显示 package family、Application Id、manifest 路径与 association 名，始终只读。不改包、UserChoice/Hash、UCPD 或任何关联 ACL。

结果区可选择/复制，包含本地化来源、解析状态和物理路径。未找到注册、无效标识符、拒绝访问及扫描不完整分别呈现；诊断数量最多 64。常规加载/定时刷新不触发关联检查。每个来源目录最多检查 4096 个名、总证据最多 4096 条；包最多 512 个、单 manifest 最多 8 MiB 字符；超限明确标记不完整。后端 25 秒协作取消预算，前端 30 秒等待预算和独立取消按钮；操作系统单次同步 registry/package API 本身无法被 token 抢占。取消前端请求只停止等待，不把它当成已停止服务端调用；服务端继续受自己的预算约束。结果只代表当次读取，不是跨来源原子快照。

NoOpenWith 归属分为 Enabled（无值且无 marker；孤立 marker 归为 Changed）、Owned（当前 marker 和精确数据匹配）、Unknown（无 marker，含可能来自旧版本的值）、Changed（marker/数据/command 代际不符）。Unknown 与 Changed 都保留原值并拒绝普通启用；界面禁用该开关并在来源说明 tooltip 提供导出/核查步骤；旧版值不会自动认领。界面说明先导出准确物理 Applications key，再与应用厂商/注册表编辑器核查。未实现带持久恢复快照的显式 legacy recovery，因为当前没有能证明旧值来源的资料；不能为了兼容而弱化 provenance。

Create/Update 提交成功后必须 fresh read-back；read-back 抛错或不一致返回 OpenWithOperationUncertain，不能声称没有变化。前端沿用串行门、失败/超时快照核对、IsStateUncertain、迟到通知重查，不盲重试；编辑对话框打开前捕获 ExpectedEditStamp，不能在表单期间刷新后用新 stamp 覆盖旧表单。旧客户端没有 edit stamp 时拒绝编辑并要求刷新/更新客户端。

RegRenameKey soft delete、事务 purge 和重复 .deleted 兼容仍保留；没有用 copy/delete 替代。某些 Explorer 版本是否会把 sibling .deleted 注册作为额外候选仍未实测，不能声称 rename 等于 Explorer 列表移除。新增注册默认仅送审；安装器重注册监控及显式 HKU 持久保护见下文，不提供永久自动删除。

## 注册与来源的 Windows 验证

在可恢复的测试机上准备唯一应用 key 和测试扩展名，先导出测试注册；只清理自己创建的测试数据，不修改真实 `UserChoice` / Hash。分别在 Windows 10、Windows 11 执行：

1. 同一 app 分别注册在 HKU 当前 SID 和 HKLM，含 command、FriendlyAppName、ExpandString 和 nested values；运行普通权限前端 / 后端服务，分别开关。直接核对所选 hive 的空 `NoOpenWith`、归属值及刷新状态，另一 hive 不改变。服务 x86 / x64 构建应操作同一 native view。对两个用户重复，不能读取或改写另一 SID；认证失败 / hive 未加载返回错误，不新建 SID 根。
2. 预先设置空字符串、非空字符串、DWORD / Binary `NoOpenWith`。刷新后未知/改写归属的开关不可操作，来源说明 tooltip 给出导出/核查步骤，物理值与类型保持不变；通过后端请求启用也必须拒绝。旧版本无 marker 的值同样保留。由第三方改写 NoOpenWith、marker 或 command 后重试，旧请求拒绝，刷新准确反映当前值。
3. 使用唯一 GUID 夹具：扩展名 .cmm136-<GUID>、应用 Cmm136-<GUID>.exe、独立 ProgID Cmm136.<GUID>；只对这些键操作。准备一个可丢弃测试 exe 的常规 Applications 注册、另一个仅 shell/open/DropTarget/CLSID 的注册和仅 FriendlyAppName/SupportedTypes 的注册。后两者必须展示只读且不能被伪造 DTO 启用/删除。分别测试 Applications-only、同时通过 `OpenWithProgIDs`、用户 FileExts、RegisteredApplications/Capabilities 和打包应用提供的候选。对同一扩展名同时创建 HKU Classes OpenWithProgIDs、FileExts OpenWithList/OpenWithProgids、RegisteredApplications/Capabilities 引用；点击检查，确认同一标识符分组中保留每条准确物理路径，独立 ProgID 始终只读，Applications 引用显示对应可管理注册。输入路径穿越/无效扩展名应拒绝，取消不会卡住页面，访问失败/包扫描失败显示不完整。观察实际文件的子菜单及“选择其他应用”对话框，记录 Windows 版本和文件类型；注册表成功而 Explorer 仍显示候选属于范围限制，不能判为已验证 Explorer 移除。默认应用通过 Windows 设置选择，比较操作前后的只读 UserChoice / 默认处理行为，不手工写 Hash。
4. 连续至少三次开关、刷新、删除 / 重启服务 / 恢复：原 key 与 backup 必须互斥，ACL、值类型、未展开文本、command 与原开关状态完整恢复。人为预置 `.deleted` 冲突（包括残缺副本）时不覆盖；原 app 被安装器重建后 Undo 拒绝，两项仍可查看，Purge 只清理选中备份。验证历史重复后缀只恢复指定副本。
5. 在测试 key / 某个 backup 子键设置可恢复的权限拒绝，分别尝试 toggle、Delete、Undo、Purge。错误不得显示完成；事务失败不留半对 marker/NoOpenWith 或残缺备份。解除测试 ACL 后重试，并直接检查树和前端状态。模拟进程在原生 rename / 事务提交前后退出，不能出现半棵复制树。
6. 编辑同时更改名称和目标 exe 为另一程序，操作必须失败且名称/command/ACL 完全不变。合法的名称+参数编辑必须一起提交，ExpandString 类型和未展开变量保留。表单打开后在外部改名称或 command，提交旧表单必须拒绝；事务不可用、提交前取消/写入拒绝不得留下半初始化 app key。
7. 测试并发安装器改写、两个前端、旧刷新在操作中完成、Pipe 响应丢失 / 超时但后端已提交、核对查询也失败和随后迟到通知。核对成功后只显示真实状态；核对不可用时阻止条目重复操作，成功刷新后恢复，不把取消 / 超时当作物理写入必然失败。

自动测试使用当前普通账号的唯一 HKU Applications fixture，并清理全部测试 key / ACL。它验证注册表状态、raw values、归属、备份冲突、事务 rollback、SID 隔离和旧失败响应不会覆盖新状态；不证明 HKLM 高权限服务 mutation、跨账号真实 Pipe 身份、Explorer 候选可见性或 Windows 默认应用启动行为。

## 监控、审核与显式持久保护

[OpenWithMonitoringService](../ContextMenuMgr.Backend/Services/OpenWithMonitoringService.cs) 由 `ContextMenuRegistryMonitor` 管理独立的 Applications 轮询任务，默认每轮结束后等五秒；不等待常规/Win11 全量扫描及其 catalog gate，也不做扩展名来源扫描。两个任务共用停止令牌，Stop/Completion 等待两者退出。每轮重新打开 native HKU/HKLM Applications，无长期 Registry handle/RegNotify watcher；每 root 最多 4096 名，发现预算 15 秒协作取消，单次同步 Windows API 无法抢占。

认证 Pipe 上下文和每 30 秒枚举一次的 WTS active/connected/disconnected token 提供用户。执行前独立验证 SID、loaded hive 和 Classes root；同 SID 多 Session 共用一个 baseline，不同 SID 独立。HKLM/HKU 同名项始终是不同物理来源。通知 `OpenWithChanged` 带 UserSid，只向相同认证 SID 的订阅者发送；普通 OpenWith mutation 广播也按 SID 过滤。通知 DTO 只触发 fresh snapshot，不作为状态直接覆盖 UI。TrayHost 只为持久未解决审核数量大于零的监控通知显示既有 Win32 气泡，Information 级别的 OpenWithApprovalNotificationReceived 记录收到的审核信号，不能作为 Windows 实际显示证明；激活统一通过 `OpenApprovals` 打开中央待审批并重新查询。气泡回调只有图标身份，不能从代表性的 SpecialItem 或最近一次全局状态推断历史点击目标；单条、聚合和重叠气泡均采用安全的通用待审批入口。监控不可用诊断不构造待审批，也不产生审核气泡；隐藏图标时仍可接收通知。兼容决定：保留已发布 `FrontendControlCommand.OpenWith` / `--open-openwith` 的显式页面激活，但 TrayHost 不再使用它转接审核。

### 稳定与审核

新监控或 session reset 等待 30 秒，且初始发现须 10 秒不变；单项和确认缺失也须稳定 10 秒。访问拒绝/不完整发现暂停采纳，记录失败并退避 30 秒；同一错误持续期间避免重复 attention。首次没有该 SID OpenWith baseline 时静默采纳当前项（可为零），与 regular/WPS markers 无关。已有 baseline 会在启动稳定后发现离线新增；完全在两次轮询之间出现又消失的变化不可见。

**基础流程：发现新增、改变或重注册 → 核实隔离成功 → 送审；通过后仅解除本次隔离，拒绝则保持禁用。** 首次/重置基线仍静默采纳；第三方已有禁用值保留。已核实隔离失败的注册保留物理状态与诊断日志，不进入待审批、不计入角标，同一物理代际不重复尝试；注册发生变化后重新评估。写入结果不确定时保留 journal 并只读核对，不能按已失败清除。OpenWith 使用独立 NoOpenWith/journal，不套用经典 Added/quarantine。每 SID/轮聚合一次通知。中央待审批是注册审核入口，普通 OpenWith 页保留状态、定位待审批按钮和独立持久保护管理，不再提供竞争性的接受操作。

| 持久状态 | 中央队列语义 |
| --- | --- |
| New / Changed / ReRegistered | 已核实禁用，可按当前代际通过或拒绝。确认稳定缺失后保留独立缺失身份，重现归为 ReRegistered；未观察到缺失的同内容重写按 Changed。 |
| Conflict | 显式 attention，须到 OpenWith 检查并停止或重新授权保护，不展示普通接受/拒绝。 |
| Uncertain | 显示待核实，暂停审核 mutation；查询失败、物理代际尚未稳定或 visibility journal 未完成时同样禁止决策。 |
| IsolationFailed（旧状态） | 不是待审批项；后端轮询清理旧审核记录，已核实失败不占角标、不重复写入。 |
| Reapplied | 已核实保护结果，仅信息，不计入未解决队列。 |
| Missing | 已确认缺失后不制造活动应用审批，规则仍在普通 OpenWith 页管理。尚无法确认的缺失保留 Uncertain。 |

`GetOpenWithPendingReviews` 使用认证 SID、loaded hive/native view，只读取持久 review、未完成 journal 与保护 attention 的准确物理路径，不扫描全部 Applications、不建立 baseline、不写注册表或生成 review。未完成但尚无不确定写入的隔离及已核实失败均不列入队列。最多 8192 个身份，后端 15 秒协作预算；单项读取不完整保留 Uncertain，可见性返回 null 并显示尚未核实，不使用占位布尔值猜测启用状态；状态库/身份/数量错误使整次查询失败，前端保留原列表。结果为独立强类型 OpenWithReviewSnapshot，包含用户、物理路径、SpecialMenuEntry 原始信息、status、capability/限制及 protection，并以 BaselineEstablished 明确表示该认证 SID 的持久基线是否存在；此只读标志不建立基线。HKU/HKLM 同名项分别决策。

`DecideOpenWithReview` 提交路径、审核 generation 和 Accept/Reject。generation 结合持久审核版本、SID/物理路径及完整注册 fingerprint；fingerprint 包含原始值/类型、子键及 last-write time，最多 512 个节点、深度 16、4096 个值、1 MiB 序列化指纹材料，超限 fail closed。不支持激活结构也有物理指纹。只接受与最新稳定观测和 fresh read 完全一致的 generation；解除本次隔离时还在原有 TxR 内再次核对，内容或同内容重建使旧决策失效。

Accept 经原 TxR/read-back 解除归属仍匹配的本次隔离，提交成功才移出队列；原已禁用的项仅确认并保留原值。Reject 回读已禁用后持久解除审核，保持 NoOpenWith，不删除注册或自动授权保护。Conflict/Uncertain 仅提供检查；原有保护授权须先显式停止才能放行。

兼容决定：保留已发布 `ReviewOpenWithRegistration` nullable 协议：true/false 仅管理保护，null 将原 DTO 的准确代际转到同一 Accept 实现；缺少有效代际或已无未解决审核时拒绝并要求刷新。普通页面加载及上述查询均不建立 baseline。

### 授权身份与限制

只有本程序已禁用、Owned provenance 核实、可管理的 **HKU 注册**可显式授权保护。HKLM 保持手动，现有第三方/旧版 NoOpenWith 不构成 policy。规则保存 SID、精确物理路径、全部 command-bearing verb 的代际、字面 exe 路径、SHA-256、授权、累计次数/backoff 和上次结果/时间。

exe 必须是存在的固定本地盘文件，至多 128 MiB，不含环境变量、UNC 或 reparse-point 祖先。纠正时保持禁止替换的读取句柄；二进制更新、版本路径或 command 代际变化需重新审核授权，publisher/product 不是身份依据。只有准备纠正时验证文件指纹，不应将已禁用状态的快速满足分支描述成每轮重新 hash。

### 纠正、失败与撤销

1. 重开原路径并验证 SID/path/manageability/command/visibility。已有 Owned-disabled 满足目标，无需再写。
2. 在 durable store gate 下重新检查授权，**先持久保存 Uncertain attempt reservation**，再进入实际纠正。保存失败不授予写权限。
3. 持有 exe identity 句柄，使用既有 `OpenWithRegistration.SetEnabled` TxR 写入；fresh read-back 后保存 Reapplied/时间及 review。已有 NoOpenWith 不覆盖、不认领。
4. 失败、取消或结果保存失败保留 durable Uncertain。下一轮只确认已禁用，或进入 Conflict/review，不盲重试该次未知写入。

每次授权至多三次纠正，累计持久退避 30/120/270 秒；再被恢复为开启则 Conflict，等待新授权，避免持续与安装器/管理员争写。隔离、审批放行与普通开关共用 visibility journal：先在 monitor/store gates 内验证身份与代际并持久预留，再执行原 TxR 和 fresh read-back，最后提交 baseline/review/规则结果。Enable 在预留期间暂停该路径自动保护，只有物理及保存核实后才撤销规则；失败保留既有授权。普通禁用不确认注册审核；待审核项禁止绕过中央审批启用或新增保护授权。Stop protection 只撤销授权，不改物理可见性。原项缺失仍保留 rule-only 卡片或已有 deleted backup，供用户移除授权。

Registry 和 JSON 不共享事务。写后提交失败保留 durable journal，队列和普通页面显示 Uncertain 并阻止重复开关。后续 monitor 只读核对：完整原 generation 未变则解除 journal，保留 review/规则；目标可见性、归属和非可见性内容都核实一致才提交结果。其余情况继续待核实，不盲写、不清除。

监控不 rename/purge，不增加 `.deleted` 后缀，也不覆盖备份或重注册原项。重新注册单独送审，备份仍在原页面管理；匹配显式规则时可验证禁用，不称“已自动删除”。Applications suppression 仍不能控制其它关联来源或默认应用。

### 持久格式与 reset

独立 schema-3 `openWith` section 保存 users/baseline/reviews/rules，以及 generations/reviewVersions/visibilityIntents/missingRegistrations/pendingIsolations/quarantines，不造伪 classic state。原子写、备份、损坏恢复、host quarantine 与跨 section merge 见[状态与监控](state-and-monitoring.md)。旧 schema-3 缺新增字段时使用空集合，保留原 reviews/rules；monitor 对未保存完整指纹的历史未变 baseline 补录观测，不制造升级送审洪泛。schema 2/raw 升级不制造规则，旧 schema-2 reader 拒绝新格式。reset 一起删除授权、review、baseline 和 backup，下轮重新静默采纳，但不启用注册、不删 NoOpenWith。hostIdentity 字段只保存散列；**授权 section 本身保存 SID 和物理 HKU 路径**，不能声称整个文件不含 SID。

### 监控验收

Debug 构建主窗口按 F12，在调试工具的“模拟打开方式注册”中创建唯一 `ContextMenuMgrPlus.DebugOpenWith.<GUID>.exe`。工具在前端当前账号已加载的 native `HKU\<SID>\Software\Classes\Applications` 直接模拟外部安装器写入，command 指向系统 notepad.exe，但不执行程序；不是正常管理链路 A 的替代入口。创建前通过只读审核查询验证 BaselineEstablished，未确认则拒绝创建并提示稍后重试，避免测试项被首次基线直接采纳。写后分别显示“注册已创建”和“已确认进入待审批”，最多等待两分钟定向查询真实持久队列；等待期间停用重复创建和清理，关闭窗口取消等待但保留已创建注册。超时/后端不可用明确提示送审尚未确认，不能当作未写注册表而盲重试。工具不直接生成 review、气泡或徽标；失败细节写前端日志。重新打开调试窗口仍可按“清理测试注册”删除名称符合精确 GUID 格式且 `ContextMenuMgrPlus.DebugOpenWithOwner` 与 GUID 匹配的本用户活动项，逐项回读缺失；其它注册、`.deleted` 备份和已授权规则均保留。已删除备份/缺失规则仍通过普通 OpenWith 管理。整个窗口沿用非 Debug 编译排除，不影响 Beta/Release。

在 Windows 10/11 真实服务上验证首次/重置不送审洪泛、HKU/HKLM 新项先隔离再审核、授权后相同 binary/commands 重建可纠正、重启后继续授权；改 binary/command/path/外部 visibility 应保留并 Conflict。移除 owned visibility 三次后再次重建应停止；普通 Enable 撤销规则。删除后重注册不得覆盖 backup，原项缺失仍可停保护。在一次监控已稳定的唯一测试注册上分别验证气泡投递、徽标、中央队列、通过仅解除本次隔离、拒绝保留 NoOpenWith/归属值、状态文件与重启、UI 同步；换 command/target 或重建同内容时旧请求须拒绝。锁住测试状态文件模拟提交失败，不能从响应/气泡判断已完成。验证 RDP/快速切换、hive unload/reload、SID 通知、隐藏托盘激活、真实安装器时序。registry fixture 测试不能替代 Explorer 候选列表、HKLM 提升服务或默认应用启动验收。

## 源码与维护

入口：[SpecialMenuService](../ContextMenuMgr.Backend/Services/SpecialMenuService.cs)、[OpenWithRegistration](../ContextMenuMgr.Backend/Services/OpenWithRegistration.cs)、[OpenWithSourceInspection](../ContextMenuMgr.Backend/Services/OpenWithSourceInspection.cs)、[OpenWithMonitorState](../ContextMenuMgr.Backend/Services/OpenWithMonitorState.cs)、[前端 VM](../ContextMenuMgr.Frontend/ViewModels/SpecialMenuPageViewModel.cs)、[Contracts](../ContextMenuMgr.Contracts/SpecialMenuContracts.cs)。永久回归在 [OpenWithRegistrationTests](../ContextMenuMgr.Tests/OpenWithRegistrationTests.cs)。

修改支持的激活结构、visibility/edit stamp、授权、监控预算、state section 或历史 sibling backup 兼容时更新本文；只读来源不能因为被发现就获得修改权限。无 marker 的旧值仍无法证明归属，未实现显式带恢复快照的 legacy recovery。BaiduNetdisk 等具体旧值来源需原始值/类型/marker/command 与同时间日志，当前证据不足，不能由应用名判断。
