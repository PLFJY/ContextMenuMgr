# 前端、WPF-UI 与异步交互

本文负责 UI/MVVM、资源、本地化、导航/滚动、搜索和请求结果呈现。注册表事务不属于 ViewModel；ShellNew、OpenWith、Win11 的专有 UI/核对契约分别见其[文档](README.md)。单实例与进程关闭协调见[生命周期](runtime-lifecycle.md)。

## 入口与所有权

| 入口 | 职责 |
| --- | --- |
| [App.Services.xaml.cs](../ContextMenuMgr.Frontend/App.Services.xaml.cs) | DI 注册服务、页面与 ViewModel，按现有生命周期管理 |
| [App.xaml](../ContextMenuMgr.Frontend/App.xaml) | ThemesDictionary、ControlsDictionary、动态 brush、Language、共用控件样式 |
| [MainWindow.xaml](../ContextMenuMgr.Frontend/MainWindow.xaml)、[code-behind](../ContextMenuMgr.Frontend/MainWindow.xaml.cs) | FluentWindow、自定义 TitleBar、InfoBar、搜索事件、导航协调；不承载 registry 业务 |
| [ShellViewModel](../ContextMenuMgr.Frontend/ViewModels/ShellViewModel.cs) | 主窗口命令、导航状态、刷新/Explorer restart/attention |
| [ContextMenuWorkspaceService](../ContextMenuMgr.Frontend/Services/ContextMenuWorkspaceService.cs) | 连接、常规快照、通知与操作；scene-only 项不加入全局池 |
| [NavigationPages](../ContextMenuMgr.Frontend/Views/Pages/NavigationPages.cs) | 导航 Page wrappers；可复用内容是 UserControl |

workspace 区分 Pipe reachability 和单次加载错误。Ping 成功、GetSnapshot 失败不触发 UAC 安装修复。主快照 Apply 后即清 IsLoading，WPS 审核后台刷新不阻塞主列表；WPS 启动延迟/重试不能演变成每次输入的后端查询。

## 主题、模板与本地化

`FrontendThemeService.Initialize` 在窗口创建/显示阶段应用保存主题，App.xaml 的初始 Light 不能代替用户偏好。固定 Light/Dark 调用 ApplicationThemeManager 并停止 SystemThemeWatcher；System 才 ApplySystemTheme/Watch。新增主题入口复用该服务。

颜色/brush 使用 DynamicResource，如 `TextFillColorPrimaryBrush`、`CardBackgroundFillColorDefaultBrush`。根 Foreground 不保证覆盖 WPF-UI 模板、Popup 与状态触发器；检查 Appearance、BasedOn 和模板资源。标题栏 `ChromeTitleBarButton` 不跨用到普通按钮或 Popup。启用 badge 用 accent 与 TextOnAccentFillColorPrimaryBrush，禁用用中性背景与次要前景；badge 高度/垂直对齐一致，避免 WrapPanel 按其它按钮高度拉伸。

Win11 菜单中受系统保护、暂不支持更改的条目，其状态说明使用 `SystemFillColorCautionBrush` 黄色警示文字，与普通次要说明区分，并随浅色/深色主题动态更新。

Popup/Flyout/AutoSuggestBox 建议项脱离普通视觉树。DataTemplate 以 item VM 属性为主，不依赖 Window ancestor、ElementName=RootNavigation 或错误 TemplatedParent。选择处理放外层事件或 item 明确命令；`PressedForeground ... ContentPresenter` 等绑定报错先查 Style/Template。

TitleBar 布局是 Icon、Title、GlobalSearchBox、HeaderActions、WindowButtons，避免 ColumnSpan 互相覆盖；可交互控件设 `WindowChrome.IsHitTestVisibleInChrome=True`。`MainWindowPlacementService` 保存 normal RestoreBounds/Maximized，不保存 Minimized；恢复校验 virtual screen，防断开显示器后窗口不可见。

`LocalizationService` 支持 System/zh-CN/en-US/zh-TW，更新 culture 和 CurrentLanguage。长期用户文本同步 `Strings.resx`、`Strings.zh-CN.resx`、`Strings.zh-TW.resx`，托盘资源也独立维护，切语言通知 TrayHost reload。回收站 Pin to Start 等系统名称使用 `TranslateSystem` 的用户 Windows UI 语言，不能用服务语言替代。

## Portable 生命周期设置

SettingsPage 的 `RemovePortableServiceOnExit` 仅在 RuntimePackageManifest 判定 Portable 时显示，三语资源包含退出后台后移除注册、下次可能再次 UAC 的提示。默认 true，旧 JSON 缺字段继承 true，显式 false 保留；KeepBackgroundAfterClose 和 AutoStartOnLogin 仍默认 false。Installer 隐藏并忽略自动移除，即使用户手改 JSON。

AutoStart 与自动移除通过各自命令的 `CanExecute` 互斥：一项开启时另一项不能操作，必须先手动关闭当前项；关闭当前项的命令始终允许，不会替用户关闭另一项。开关用 OneWay 显示已提交状态，操作失败强制刷新绑定以回退控件；不再由属性回调启动异步设置任务。`IsLifecycleSettingsEnabled` 在初次读取、命令执行和 reset 期间统一防重入；AutoStart 命令执行期间显示进度。自动移除只是本地持久偏好，既有 autostart 必须先通过原 verified startup-mode disable 操作关闭，之后才允许开启移除；不会为单独修改 cleanup 弹 UAC。失败恢复旧 UI/设置快照；补偿不确定或存在事务 marker 时禁用两个开关，明确提示先显式安装/修复服务；不能把后端查询失败当作 false 覆盖用户意图。事务期间取消普通窗口关闭，避免前端消失而提升操作继续。KeepBackgroundAfterClose 保持独立，详细事务/失败补偿与安全限制只见[生命周期](runtime-lifecycle.md#portable-启动策略事务)。

设置页构造时回填语言、主题、日志级别、托盘和保护偏好不触发保存/远程副作用。Workspace 以真实 Ping 设置 `IsBackendReady`，设置页订阅其变化，在就绪后单飞读取远程设置；安装/UAC 尚未完成时打开设置页不会提前调用 GetAutoStartEnabled 或 SetTrayIconPolicy。读取失败保持生命周期开关禁用并保留持久意图，不能因 finally 执行而将未知状态开放为可操作。后端不可用期间语言和日志级别仍可本地修改，运行时同步由后续就绪流程完成；页面 Dispose 移除就绪订阅。

App 以实际 Closed 触发 await 退出请求，取消 Closing 不停服；IPC 释放在退出请求之后。后台或服务控制关闭、系统会话结束、致命异常没有普通前端自动卸载意图。WPF Toggle、取消关闭、UAC 取消/失败回填、快速连续点击和真实单实例激活仍需 Windows 人工验收；策略/事务单测不证明模板或 SCM 已验证。

## 导航与页面组合

本地 [ModernNavigationView](../ContextMenuMgr.Frontend/Controls/Modern/Navigation/ModernNavigationView.xaml.cs) 实现 WPF-UI INavigationView；仍用 INavigationService、NavigationViewItem/TargetPageType，递归保留 MenuItems/Source、Binding、Visibility、InfoBadge、图标与展开。不另造平行导航集合。

[ModernFrame](../ContextMenuMgr.Frontend/Controls/Modern/Frame/ModernFrame.cs) 是 Page 宿主，依次尝试 IServiceProvider、INavigationViewPageProvider、Activator 创建。Page 不直接放 Border/Grid/TabItem/普通 ContentControl；复用内容用 `SpecialMenuContentView : UserControl` + `SpecialMenuPageView : Page`，不靠嵌套 Frame 掩盖组合错误。

转场支持 Entrance/Fade/Slide/Suppress，旧内容退出清理后新内容进入，后退反向；快速导航取消旧 Storyboard/PageHost/过期完成回调。重复当前页在创建前短路，不增 journal；带子项父节点只展开/折叠。内容呈现后 ModernFrame reset 自己的 ContentScrollHost，再调用 INavigationScrollTarget，最后 NavigationCompleted；等待就位订阅事件，不固定 Task.Delay。

File Types 批量管理仍是内部隐藏 UserControl，不新增导航 Page/TabItem；返回保留原 tab。按需查询后以动作组折叠展示物理来源，组头显示 ExactAction、SameModule 或 RegistrationFamilyCandidate 证据；组内列表限制高度并启用回收虚拟化，显示完整 HKLM/HKU 路径。组选择加物理项包含开关共同决定批量范围；候选组初始不选中，用户明确选择该组才纳入该组物理项，仍可逐项排除。混在强关系组内的弱候选物理项仍单独排除，须逐项加入。确认删除前显示组数、物理项数和用户 hive 数，普通删除保留恢复备份，Undo 不等于永久清理。批量完成显示已确认成功、失败、跳过、取消与不确定数量；取消仅停止后续项，正在进行的后端事务继续至结束或回滚。批量扫描不从旧版呈现缓存恢复缺少物理指纹的行，失败刷新保留已显示来源；真实 Explorer 可见性仍需人工验证。物理匹配和提交前核对见[注册表模型](registry-model.md)。

## 滚动、输入与渲染

共享外层为 `ModernFrame.ContentScrollHost` 的 [ModernScrollViewer](../ContextMenuMgr.Frontend/Controls/Modern/Scrolling/ModernScrollViewer.cs)。OtherRules/FileTypes 声明 `ModernScroll.Ownership=Self`、内部 ModernScrollViewer 自管；其它页沿用 Frame，不另包无约束宿主。导航只 reset 外层，不改内层列表 offset。

| 输入/条件 | 当前策略 |
| --- | --- |
| 触摸屏 | VerticalOnly panning（Style/XAML 可覆盖）；WPF 原生阈值/惯性/ratio/deceleration；manipulation 开始只取消旧滚轮动画，不改 Handled，不造 TouchMove/惯性 |
| 小 delta/Precision Touchpad 形状 | 保留 Delta/120 小数；观察非 120 整倍才进入连续模式，间隔超过 120 ms 退出，处理 timestamp wrap；lead 至多一格、duration 至多 60 ms |
| 普通完整刻度 | 默认 220 ms EaseOut，multiplier/duration/easing 可配置；同向 pending 至多三格且通常一 viewport（至少系统一格）；反向从当前可见 offset 起，目标相同不重启计时 |
| 系统设置 | WheelScrollLines 正数按 16 DIP/line，0 不滚，-1 按 viewport；限制到 0..ScrollableHeight。关闭动画/render tier 0/duration 0 立即偏移；禁用 smooth 保留原生路径，Ctrl/Shift 不接管 |

输入形状不是设备识别，完整刻度触摸板仍走离散路径。内层还能沿当前方向滚动时外层让渡，抵达**实际**边界才在同次 PreviewMouseWheel 交接一次，不重放事件；反向边界交接取消内层旧动画。ownership 查找只认显式声明，不在继承值处提前截断。打开的 ComboBox/Popup/ContextMenu 独占输入；触摸边界由 WPF 路由决定。

`ModernListScroll`、WheelScrollController/Policy 共用上述策略；沿 source ancestor 找模板 viewer，不每次扫描整树/缓存过期模板。opt-in 列表保持 CanContentScroll=True、Pixel ScrollUnit 与虚拟化，不全局改成 False。所有应用显式创建的滚动容器（含独立窗口/表单/Debug）复用 ModernScrollViewer，控件内部 TextBox/ComboBox/TreeView 模板保留原生交互。完成/卸载移除 Rendering handler，用单调 Stopwatch 计时。

刷新时 `SuppressBringIntoViewBehavior` 在 Category/Win11 ListBox 和 SpecialMenu ItemsControl 阻止 RequestBringIntoView 冒泡到外层，避免 Reset/焦点恢复把页头滚走；不靠修改后端状态解决滚动。内部列表先接收路由，不能由此假定所有键盘/辅助功能行为已验证。

Application Groups 保留 Frame 平滑滚动：后台对 UI 捕获的只读输入分组，约 120 ms 防抖，先呈现 40 组，再 Normal 优先级 DispatcherTimer 每 60 ms 追加 12 组，差量更新保留已有组，后续分组取消旧 timer；避免 Background yield 饥饿。单项开关不全量重建组，当前搜索受状态文字影响才重算。Win11 Logo 后台加载并按路径缓存，重建约 120 ms 防抖，不能在每个卡片构造时同步读盘。

## 搜索、筛选与集合归属

[ContextMenuGlobalSearchService](../ContextMenuMgr.Frontend/Services/ContextMenuGlobalSearchService.cs) 只搜索已加载 workspace 传统分类和 Windows11ContextMenuService.CurrentItems。**不覆盖** ShellNew/SendTo/WinX/OpenWith、FileTypes、OtherRules、Approvals、Settings。Win11 以应用 family 分组但保留声明字段，传统应用分组不包含 Win11。

候选随集合、显示/状态/备注、语言、设置变化重建。字段含名称、key、registry/backend/source path、command、CLSID、file、备注、分类/状态与 package/publisher/context。ContextMenuSearchMatcher 忽略标点/分隔符，如 7zip→7-Zip。输入只本地评分，不查 backend、registry 或磁盘；icon best-effort 使用 IconPreviewService，缺失用分类 fallback。

AutoSuggestBox 自定义 TextChanged 在 UserInput 设置 `args.Handled=true`，更新结果/ItemsSource/IsSuggestionListOpen，避免默认 OriginalItemsSource 过滤覆盖。SuggestionChosen 用 SelectedItem，QuerySubmitted 仅 QueryText，Enter 打开首项；UpdateTextOnSelect=False。建议行仅展示，不放依赖外层 VM/标题栏模板的复杂按钮。

`GlobalSearchNavigationFilterService` 保存目标页、分类、文本、item id 后导航，目标 VM 消费 pending filter。Application Groups 精确定位用 id 找所属分组，**显示该组全部子项**；用户编辑/清空搜索退出导航筛选。早期只显示目标子项的描述已不符合当前 `RebuildNavigationFilterGroup`。不以滚动定位或新扫描补救。

待审批由 ApprovalsPageViewModel 合并常规/Win11/WPS 的既有分组行与独立 OpenWithApprovalItemViewModel，以 IApprovalQueueItem 只共享呈现属性和稳定身份；不同物理修改语义不共享。OpenWith 使用独立 DataTemplate，显示 New/Changed/ReRegistered、作用域、原物理路径、command、当前可见性与限制；搜索包含这些字段及 OpenWith 分类标签。已隔离项显示“通过并放行”，原已禁用项仅确认保留原值；IsolationFailed/Conflict/Uncertain 提供检查入口。

OpenWithReviewQueueService 在 UI Dispatcher 上串行查询与决策，单飞刷新且保留期间收到的 invalidation；按物理路径复用行，保留选择/动画，失败刷新保留条目并暂停决策。按钮先 Busy、防重复提交，不乐观移除。45 秒决策等待失败后另用 10 秒权威查询核对；核对失败锁定 Uncertain，后续成功查询才恢复安全状态。旧响应、通知 payload/计数不直接改队列；相同 SID 的通知只触发当前查询。Shell 徽标在既有常规/WPS 数量上加未解决 OpenWith 行，排除 Reapplied/Missing。

中央检查按钮导航普通 OpenWith 并按精确物理路径筛选；普通页的审核导航将该物理路径交给 FrontendNavigationState，中央刷新后选择对应行。普通 OpenWith 注册变更不重复显示顶部横幅，监控不可用等诊断仍保留。Win32 气泡只有图标身份，统一走无单项目标的待审批激活，不能依据旧代表 DTO 猜焦点。气泡、SID 与持久规则详见 [OpenWith](openwith.md#监控审核与显式持久保护)。

workspace `ItemDetected` 可加入新常规项；generic `ItemStateChanged` 仅更新已有稳定 Id，不能引入 File Types/scene-only 项。经典分类与 Scene 共用 ContextMenuListViewMode；Win11 固定详细视图不读写它。HideDisabledItems 只隐藏未删除禁用项，deleted 必须可见；排序按 attention/deleted/name，不按 enabled，以免开关跳位。模式切换不扫后端。

## 列表缓存、差量刷新与通知

`MenuListCacheService` 是 DI 单例，只保存前端呈现 DTO；缓存身份、路径、容量和原子写见[运行时数据](runtime-data-and-logging.md#列表呈现缓存)。传统分类、应用分组和审批复用 workspace；Win11、SpecialMenu、按范围查询的 Scene/FileTypes、批量管理和子菜单各恢复自己的缓存。缓存恢复不触发 ItemDetected/托盘审核通知，不建立后端 baseline，也不恢复 Busy、Uncertain 或 ShellNew 锁状态。缓存开关须等 backend 就绪，实际请求仍由后端验证原物理来源。

有效空缓存也是已有快照。首次无缓存才显示延迟加载占位；后续扫描保留列表及失败前的数据，顶部刷新图标以 ProgressRing 表示真实列表查询。顶部命令由当前导航内容路由：共享传统页/应用分组/审批刷新 workspace，Win11 刷新应用数据源，FileTypes/OtherRules 刷新当前 tab 或活动批量管理。重复的同范围扫描复用在途任务；选择另一个 Scene 范围使旧代次结果失效，不把两个范围合并。

Pipe 结果保留整体和经典发现完整性，旧服务未提供字段时按未知处理。传统/Win11、ShellNew 物理项/包声明分区合并：包发现失败不能阻断已完整经典部分的缓存和删除 diff；不完整分区保留未返回项，不覆盖其最后完整磁盘快照。

client 在后台按稳定 Id 比较 DTO（含嵌套元数据）并复用等价对象。UI 使用当前缓存代次重新取得合并结果，再仅修改变化行；`IncrementalCollection` 保留分组、审批和 Win11 行实例，集合不发 Reset。普通属性变化由 live sorting/filtering 处理，明确更改搜索、语言或筛选时才重算视图。应用分组在 UI 线程捕获身份/搜索输入，后台只分组不可变输入，不访问 WPF ViewModel；分组 ViewModel 在 UI 线程创建。

扫描版本、逐项 mutation 计数、权威结果及移除标记共用缓存协调锁。旧扫描不能覆盖新操作，已返回但尚未应用到 UI 的结果也会重新合并；只读核对不得结束在途写入。软件 reset 增加缓存代次，旧扫描和延迟写盘不能恢复 reset 前缓存。普通迟到通知先定向核对原来源，再更新已有 workspace 项；scene-only 项仍不进入全局池。OpenWith/WinX 保留专有通知刷新和事务门，不拆除 catalog/store 串行化。

`MenuListTransitions` 仅动画已实现且在 viewport 内的容器：进入约 180 ms，退出以不可交互的 adorner 影像约 140 ms 淡出，保留行平滑移位。首屏批量呈现不逐项动画；遵从系统动画设置，虚拟化复用清理 transform，重现同 Id 取消旧退出影像，Unload 解除集合/LayoutUpdated 和动画回调。

后端确认目标缺失才返回 `MENU_ITEM_REMOVED`，客户端广播 `ItemsRemoved`，各关联列表及缓存同步清除，退出动画只负责视觉。扫描发现条目消失时静默清理；用户操作命中已删除目标时通过 `MenuListFeedbackService` 显示 WPF-UI MessageBox。IPC 客户端不依赖 Localization/Settings，避免设置 ACL 修复依赖回环；启动构建 DI provider 时验证服务图。更新通知仍使用标题栏下方横幅式 WPF-UI InfoBar，并保留链接功能；详细错误只写日志。

## 异步操作与结果

`DelayedLoadingPresentation` 在真实 loading 超过 140 ms 才显示，显示后至少约 250 ms；新状态/Dispose 取消旧回调。快加载不人为延长 IsLoading；Busy 不等于整页加载。Application Groups 本地筛选不激活全页加载，placeholder 时折叠列表、隐藏 empty。

`AsyncToggleSwitch` 在固定槽显示开/关或 ProgressRing，单项 Busy 同步置位，`ToggleBusyPresentation.WaitForFirstFrameAsync` 最多 100 ms/两个渲染事件后再写入；隐藏窗口有超时。写入期间禁重复操作，不因 Busy 折叠本来存在的按钮。普通开关仅 HideDisabled 开启时在完成后刷新过滤，避免全表 Reset 阻挡首帧。

待审批的常规/Win11/WPS 分组在允许或拒绝提交期间保留原位的禁用按钮，并在旁边显示 ProgressRing；分组 Busy 覆盖全部逐项决策，防止重复提交。OpenWith 行沿用自身的 Busy 按钮与进度提示。

classic SetEnabled 的 request→独立物理状态核对、20/10 秒预算和迟到广播见 [IPC](process-and-privilege-flows.md#请求迟到通知与不确定结果)。ProgressRing 覆盖核对阶段。权威 requested state 成功、旧 state 回退；无法确认保留当前乐观值并锁操作，直到 fresh update。item version 防旧失败覆盖新结果。Win11 核对应用全部目标，OpenWith 页串行门/IsStateUncertain 和 ShellNew 三态分别在各主文档定义，不重发写入猜结果。

`DismissDetectedChangeAsync` / `DismissConsistencyIssueAsync` **必须先 Acknowledge 成功才本地隐藏**，失败保留提示。成功后同时设置本地 dismissed；结构冲突可能仍存在，`ApplyEntry` 保持相同 issue signature 的本地忽略，签名变化重新提示。旧文档“无论后端成功与否都忽略”与当前实现不符。

SpecialMenu 首屏先呈现加载再请求；CommandStore 首次分批加入、图标异步，后续差量更新且不做定时全量刷新。其它 SpecialMenu 的定时刷新见各责任文档。新 Loading/Empty 不能引入全局模拟服务；DebugToolsWindow/强制更新入口仅 Debug 编译。F12 调试窗口还提供当前用户 OpenWith 测试注册的创建与按归属清理；创建前验证后端持久基线，创建后有界查询并显示真实送审结果，等待期间禁止提前清理，操作范围和基线等待见 [OpenWith 验收](openwith.md#监控验收)。

## 本地动作、错误与清理

`ContextMenuItemActionsService` 在前端 Session 打开真实 BackendRegistryPath，只有缺物理路径才 HKCR fallback。Regedit LastKey 为完整 hive，不拼应用语言的计算机根名称；同 Session 已有 Regedit 则 -m 新窗口，不杀用户旧窗口。启动成功只证明请求，实际定位需看选中键。

InfoBarService 的控件、位置与清理契约见上文；详细错误码/堆栈/路径写日志，不向普通用户暴露堆栈。只有需求/规则明确要求 Explorer restart 的设置，成功后通过单例 ExplorerRestartStateService.MarkRequired 联动顶部按钮，由用户执行并 Clear。Win11 应用开关不标 restart；全局经典菜单 tweak 有独立语义。重置软件设置须先完整禁用 Registry Write Protection，失败保留 authoritative toggle 并停止 reset。

ViewModel Dispose/页面卸载取消刷新、debounce、加载回调与动画，解除 LanguageChanged/SettingsChanged/workspace/filter 等订阅；不能靠窗口关闭推断静态事件无泄漏。控制 Pipe/DI/单实例退出见[生命周期](runtime-lifecycle.md)。

## 验证与维护

构建只能验证 XAML/类型，WheelScrollPolicyTests 等只保护算法。Windows 真机须测浅/深/系统主题切换、窄窗口、中文/英文 Windows 与应用交叉语言、已有 Regedit 的真实选中键、Popup/AutoSuggest 选择与 Enter、导航快速打断/重复点击、筛选/返回、busy 首帧、迟到响应/刷新竞态和关闭清理。

滚动分别用普通鼠标密集完整刻度、小 delta 触摸板、触屏；测停止/反向、内外边界、触摸文本选择/轻点按钮、ComboBox/Popup 独占、大列表虚拟化、wheel lines/page/0、关闭动画和切页/关闭停止。当前尚不能证明所有驱动 delta/timing、触摸 tap 阈值与 nested boundary feedback；合成事件不替代硬件。

修改资源/模板、WPF-UI、本地 Modern 控件、搜索范围、导航、async outcome 或清理时更新本文；子系统专有 UI 仅链接其契约，避免再追加 issue 附录。
