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

Popup/Flyout/AutoSuggestBox 建议项脱离普通视觉树。DataTemplate 以 item VM 属性为主，不依赖 Window ancestor、ElementName=RootNavigation 或错误 TemplatedParent。选择处理放外层事件或 item 明确命令；`PressedForeground ... ContentPresenter` 等绑定报错先查 Style/Template。

TitleBar 布局是 Icon、Title、GlobalSearchBox、HeaderActions、WindowButtons，避免 ColumnSpan 互相覆盖；可交互控件设 `WindowChrome.IsHitTestVisibleInChrome=True`。`MainWindowPlacementService` 保存 normal RestoreBounds/Maximized，不保存 Minimized；恢复校验 virtual screen，防断开显示器后窗口不可见。

`LocalizationService` 支持 System/zh-CN/en-US/zh-TW，更新 culture 和 CurrentLanguage。长期用户文本同步 `Strings.resx`、`Strings.zh-CN.resx`、`Strings.zh-TW.resx`，托盘资源也独立维护，切语言通知 TrayHost reload。回收站 Pin to Start 等系统名称使用 `TranslateSystem` 的用户 Windows UI 语言，不能用服务语言替代。

## 导航与页面组合

本地 [ModernNavigationView](../ContextMenuMgr.Frontend/Controls/Modern/Navigation/ModernNavigationView.xaml.cs) 实现 WPF-UI INavigationView；仍用 INavigationService、NavigationViewItem/TargetPageType，递归保留 MenuItems/Source、Binding、Visibility、InfoBadge、图标与展开。不另造平行导航集合。

[ModernFrame](../ContextMenuMgr.Frontend/Controls/Modern/Frame/ModernFrame.cs) 是 Page 宿主，依次尝试 IServiceProvider、INavigationViewPageProvider、Activator 创建。Page 不直接放 Border/Grid/TabItem/普通 ContentControl；复用内容用 `SpecialMenuContentView : UserControl` + `SpecialMenuPageView : Page`，不靠嵌套 Frame 掩盖组合错误。

转场支持 Entrance/Fade/Slide/Suppress，旧内容退出清理后新内容进入，后退反向；快速导航取消旧 Storyboard/PageHost/过期完成回调。重复当前页在创建前短路，不增 journal；带子项父节点只展开/折叠。内容呈现后 ModernFrame reset 自己的 ContentScrollHost，再调用 INavigationScrollTarget，最后 NavigationCompleted；等待就位订阅事件，不固定 Task.Delay。

File Types 批量管理是内部隐藏 UserControl，不新增导航 Page/TabItem；返回保留原 tab。按需查询 scene-only 相关项，单项/批量操作复用后端安全和备份，删除项仍留供 Undo；物理匹配范围见[注册表模型](registry-model.md)。

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

Application Groups 保留 Frame 平滑滚动：后台对 UI 快照分组/搜索，约 120 ms 防抖，先呈现 40 组，再 Normal 优先级 DispatcherTimer 每 60 ms 追加 12 组，重建取消旧 timer；避免 Background yield 饥饿。单项开关不全量重建组，当前搜索受状态文字影响才重算。Win11 Logo 后台加载并按路径缓存，重建约 120 ms 防抖，不能在每个卡片构造时同步读盘。

## 搜索、筛选与集合归属

[ContextMenuGlobalSearchService](../ContextMenuMgr.Frontend/Services/ContextMenuGlobalSearchService.cs) 只搜索已加载 workspace 传统分类和 Windows11ContextMenuService.CurrentItems。**不覆盖** ShellNew/SendTo/WinX/OpenWith、FileTypes、OtherRules、Approvals、Settings。Win11 以应用 family 分组但保留声明字段，传统应用分组不包含 Win11。

候选随集合、显示/状态/备注、语言、设置变化重建。字段含名称、key、registry/backend/source path、command、CLSID、file、备注、分类/状态与 package/publisher/context。ContextMenuSearchMatcher 忽略标点/分隔符，如 7zip→7-Zip。输入只本地评分，不查 backend、registry 或磁盘；icon best-effort 使用 IconPreviewService，缺失用分类 fallback。

AutoSuggestBox 自定义 TextChanged 在 UserInput 设置 `args.Handled=true`，更新结果/ItemsSource/IsSuggestionListOpen，避免默认 OriginalItemsSource 过滤覆盖。SuggestionChosen 用 SelectedItem，QuerySubmitted 仅 QueryText，Enter 打开首项；UpdateTextOnSelect=False。建议行仅展示，不放依赖外层 VM/标题栏模板的复杂按钮。

`GlobalSearchNavigationFilterService` 保存目标页、分类、文本、item id 后导航，目标 VM 消费 pending filter。Application Groups 精确定位用 id 找所属分组，**显示该组全部子项**；用户编辑/清空搜索退出导航筛选。早期只显示目标子项的描述已不符合当前 `RebuildNavigationFilterGroup`。不以滚动定位或新扫描补救。

workspace `ItemDetected` 可加入新常规项；generic `ItemStateChanged` 仅更新已有稳定 Id，不能引入 File Types/scene-only 项。经典分类与 Scene 共用 ContextMenuListViewMode；Win11 固定详细视图不读写它。HideDisabledItems 只隐藏未删除禁用项，deleted 必须可见；排序按 attention/deleted/name，不按 enabled，以免开关跳位。模式切换不扫后端。

## 异步操作与结果

`DelayedLoadingPresentation` 在真实 loading 超过 140 ms 才显示，显示后至少约 250 ms；新状态/Dispose 取消旧回调。快加载不人为延长 IsLoading；Busy 不等于整页加载。Application Groups 本地筛选不激活全页加载，placeholder 时折叠列表、隐藏 empty。

`AsyncToggleSwitch` 在固定槽显示开/关或 ProgressRing，单项 Busy 同步置位，`ToggleBusyPresentation.WaitForFirstFrameAsync` 最多 100 ms/两个渲染事件后再写入；隐藏窗口有超时。写入期间禁重复操作，不因 Busy 折叠本来存在的按钮。普通开关仅 HideDisabled 开启时在完成后刷新过滤，避免全表 Reset 阻挡首帧。

classic SetEnabled 的 request→独立物理状态核对、20/10 秒预算和迟到广播见 [IPC](process-and-privilege-flows.md#请求迟到通知与不确定结果)。ProgressRing 覆盖核对阶段。权威 requested state 成功、旧 state 回退；无法确认保留当前乐观值并锁操作，直到 fresh update。item version 防旧失败覆盖新结果。Win11 核对应用全部目标，OpenWith 页串行门/IsStateUncertain 和 ShellNew 三态分别在各主文档定义，不重发写入猜结果。

`DismissDetectedChangeAsync` / `DismissConsistencyIssueAsync` **必须先 Acknowledge 成功才本地隐藏**，失败保留提示。成功后同时设置本地 dismissed；结构冲突可能仍存在，`ApplyEntry` 保持相同 issue signature 的本地忽略，签名变化重新提示。旧文档“无论后端成功与否都忽略”与当前实现不符。

SpecialMenu 首屏先呈现加载再请求；CommandStore 首次分批加入、图标异步，后续差量更新且不做定时全量刷新。其它 SpecialMenu 的定时刷新见各责任文档。新 Loading/Empty 不能引入全局模拟服务；DebugToolsWindow/强制更新入口仅 Debug 编译。

## 本地动作、错误与清理

`ContextMenuItemActionsService` 在前端 Session 打开真实 BackendRegistryPath，只有缺物理路径才 HKCR fallback。Regedit LastKey 为完整 hive，不拼应用语言的计算机根名称；同 Session 已有 Regedit 则 -m 新窗口，不杀用户旧窗口。启动成功只证明请求，实际定位需看选中键。

InfoBarService 承载简短非阻塞反馈；详细错误码/堆栈/路径写日志，不向普通用户暴露堆栈。只有需求/规则明确要求 Explorer restart 的设置，成功后通过单例 ExplorerRestartStateService.MarkRequired 联动顶部按钮，由用户执行并 Clear。Win11 应用开关不标 restart；全局经典菜单 tweak 有独立语义。重置软件设置须先完整禁用 Registry Write Protection，失败保留 authoritative toggle 并停止 reset。

ViewModel Dispose/页面卸载取消刷新、debounce、加载回调与动画，解除 LanguageChanged/SettingsChanged/workspace/filter 等订阅；不能靠窗口关闭推断静态事件无泄漏。控制 Pipe/DI/单实例退出见[生命周期](runtime-lifecycle.md)。

## 验证与维护

构建只能验证 XAML/类型，WheelScrollPolicyTests 等只保护算法。Windows 真机须测浅/深/系统主题切换、窄窗口、中文/英文 Windows 与应用交叉语言、已有 Regedit 的真实选中键、Popup/AutoSuggest 选择与 Enter、导航快速打断/重复点击、筛选/返回、busy 首帧、迟到响应/刷新竞态和关闭清理。

滚动分别用普通鼠标密集完整刻度、小 delta 触摸板、触屏；测停止/反向、内外边界、触摸文本选择/轻点按钮、ComboBox/Popup 独占、大列表虚拟化、wheel lines/page/0、关闭动画和切页/关闭停止。当前尚不能证明所有驱动 delta/timing、触摸 tap 阈值与 nested boundary feedback；合成事件不替代硬件。

修改资源/模板、WPF-UI、本地 Modern 控件、搜索范围、导航、async outcome 或清理时更新本文；子系统专有 UI 仅链接其契约，避免再追加 issue 附录。
