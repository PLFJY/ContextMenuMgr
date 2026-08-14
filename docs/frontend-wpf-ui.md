# 前端与 WPF-UI 实现说明

相关文档：

- [ContextMenuMgr 开发者指南](./developer-guide.md)
- [进程、权限与用户上下文链路](./process-and-privilege-flows.md)
- [AI 与维护者接手 Playbook](./ai-maintainer-playbook.md)
- [排错指南](./troubleshooting.md)

## 1. 本文目的

本文记录 ContextMenuMgr 前端层的 WPF / WPF-UI 实现经验，补充 `developer-guide.md` 中的前端概览。

很多前端问题看起来像后端状态、注册表刷新或服务链路问题，实际根因可能是 WPF / WPF-UI 控件组合方式、主题资源、模板、Popup、NavigationView 或 Page/UserControl 边界不正确。遇到按钮颜色、搜索下拉、页面滚动、Popup 绑定、页面初始化崩溃等问题时，优先按本文排查前端结构，不要直接改 Backend Service、Registry catalog 或权限链路。

## 2. 前端主要入口

| 文件 / 类型 | 职责 | 注意事项 |
| --- | --- | --- |
| `App.xaml` | 应用级资源字典、WPF-UI `ThemesDictionary` / `ControlsDictionary`、全局样式、语言资源、通用 converter | 主题相关资源优先用 `DynamicResource`；全局样式不要强行覆盖控件模板内部状态。 |
| `App.Services.xaml.cs` | 前端 DI 注册入口 | 新增前端服务或 ViewModel 时优先通过 DI 注入。 |
| `MainWindow.xaml` | `ui:FluentWindow` 主窗口、标题栏、全局搜索、InfoBar、NavigationView | 标题栏 hit test、AutoSuggestBox、NavigationView 都在这里汇合。 |
| `MainWindow.xaml.cs` | 导航初始化、InfoBar 初始化、全局搜索事件、标题栏按钮、导航滚动 reset | 可以协调控件行为，但不应承载菜单业务逻辑。 |
| `ShellViewModel.cs` | 主窗口级状态、导航文本、全局搜索命令、刷新和重启 Explorer 命令 | 只负责前端协调和命令入口，不直接写注册表。 |
| `NavigationPages.cs` | WPF-UI NavigationView 使用的页面类型 | 导航项的 `TargetPageType` 应指向 Page 类型。 |

前端是 UI 层。它通过 `NamedPipeBackendClient` 请求 Backend Service，通过 `BackendServiceManager` 触发服务安装/修复等 UAC bootstrapper 操作，通过 `ContextMenuDeepAnalysisService` 启动 ProbeHost。前端不应该直接执行高权限注册表操作，也不应该直接加载第三方 Shell Extension DLL。

## 3. App.xaml 资源字典与全局样式

`App.xaml` 是 WPF-UI 主题和全局样式的核心入口。当前结构中包含：

- `ui:ThemesDictionary Theme="Light"`
- `ui:ControlsDictionary`
- `AccentButtonForeground` / `AccentButtonForegroundPointerOver` / `AccentButtonForegroundPressed`
- `CurrentLanguage`
- 全局 `Window` / `Page` / `Label` / `TextBlock` 样式
- `CardBorderStyle`
- `ChromeTitleBarButton`
- `ScrollableListBoxItemStyle`
- `DisplayOnlyListViewItemStyle`

### 3.1 主题资源必须用 DynamicResource

WPF-UI 的主题资源会在运行时切换。凡是颜色、Brush、控件前景色、背景色、边框色，只要希望跟随主题变化，就应使用 `DynamicResource`。

推荐：

```xml
Foreground="{DynamicResource TextFillColorPrimaryBrush}"
Background="{DynamicResource CardBackgroundFillColorDefaultBrush}"
BorderBrush="{DynamicResource CardStrokeColorDefaultBrush}"
```

避免在普通 UI 中硬编码黑色、白色或固定背景色。只有非常明确的特殊状态才应硬编码颜色。

### 3.2 不要用根容器 Foreground 解决所有文字颜色

WPF 的 `Foreground` 继承对普通 `TextBlock` 有效，但 WPF-UI 控件内部往往有自己的模板、状态资源和触发器。把 `Foreground` 放在 `Page`、`Grid` 或根容器上，不一定能影响：

- `ui:Button` 内部文字；
- `ui:ToggleSwitch` 的状态文字；
- `ui:AutoSuggestBox` Popup 内项目；
- `ui:NavigationViewItem`；
- 控件模板中的 `TextBlock`。

如果某个 WPF-UI 控件文字颜色不对，优先检查控件自身的 `Appearance`、WPF-UI 主题资源、项目全局样式是否 `BasedOn` 正确，以及是否把专用 style 用到了错误场景。

### 3.3 场景专用 Style 不要跨场景复用

`ChromeTitleBarButton` 是标题栏按钮样式，适合最小化、最大化、关闭按钮，不应该复用到普通页面按钮、Popup 里的按钮或 AutoSuggestBox item template 中。

`ScrollableListBoxItemStyle` 和 `DisplayOnlyListViewItemStyle` 适合“列表只负责展示、选中视觉由内部卡片控制”的场景。它们移除了默认外壳，不应无脑套到需要默认选中、键盘焦点或辅助功能反馈的列表。

如果出现类似 `PressedForeground property not found on ContentPresenter` 的绑定错误，优先检查是否把依赖特定 `TemplatedParent` 的 Button style 放进了 Popup 或 DataTemplate。

## 4. 主题系统

主题由 `FrontendThemeService` 统一管理。它负责读取 `FrontendSettingsService` 中保存的主题偏好、启动时应用主题、手动切换 Light / Dark、跟随系统时启用 `SystemThemeWatcher`，并在切换时写日志。

### 4.1 启动时必须应用一次保存主题

`App.xaml` 里的 `ThemesDictionary Theme="Light"` 只是初始资源字典。真正的用户偏好由 `FrontendThemeService.Initialize(Window mainWindow)` 在主窗口创建时应用。

规则：

- 固定 Light：调用 `ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, updateAccent: true)`；
- 固定 Dark：调用 `ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, updateAccent: true)`；
- 跟随系统：调用 `ApplicationThemeManager.ApplySystemTheme(updateAccent: true)`，并启用 `SystemThemeWatcher.Watch(...)`。

如果启动后短暂显示错误主题，优先检查 `themeService.Initialize(this)` 调用时机、保存设置是否读取正确、是否有其它代码后续调用 `ApplySystemTheme()`，以及相关 Brush 是否使用 `DynamicResource`。

### 4.2 SystemThemeWatcher 不能无条件启用

`SystemThemeWatcher` 只应在用户选择“跟随系统”时启用。如果用户选择固定 Light 或固定 Dark，却仍然保持 system watcher，系统主题变化可能覆盖用户设置。

新增主题相关 UI 时不要绕过 `FrontendThemeService` 直接调用 WPF-UI 主题 API。

## 5. FluentWindow 与自定义 TitleBar

主窗口使用 `ui:FluentWindow`，并启用：

- `ExtendsContentIntoTitleBar="True"`
- `WindowStyle="None"`
- `WindowBackdropType="Mica"`
- 自定义 `TitleBarRoot`
- `WindowChromeTitleBarFactory.Apply(this, 44)`

标题栏区域有特殊 hit test 行为。放在标题栏里的按钮、搜索框、链接或其它可交互控件，必须设置：

```xml
shell:WindowChrome.IsHitTestVisibleInChrome="True"
```

否则可能表现为按钮点不了、搜索框无法聚焦、窗口拖动异常、hover / pressed 状态不触发。

### 5.1 标题栏布局不要互相覆盖

`MainWindow.xaml` 的标题栏布局应保持清晰的列分配：

```text
Icon | Title | GlobalSearchBox | HeaderActions | WindowButtons
```

不要用过大的 `Grid.ColumnSpan` 让全局搜索框覆盖右侧按钮或窗口控制按钮。遇到标题栏点击异常时，优先检查 `Grid.Column` / `Grid.ColumnSpan`、`Width` / `MaxWidth`、hit test 属性，以及控件是否被透明元素盖住。

### 5.2 主窗口位置持久化

`MainWindow` 的位置和大小通过 `FrontendSettingsService` 持久化到前端设置文件。窗口关闭时保存 normal restore bounds；最大化关闭时保存 `RestoreBounds` 和 `Maximized` 状态，启动时先恢复 normal bounds，再恢复最大化状态。不要保存或恢复 `Minimized` 状态，恢复前必须用当前 virtual screen 校验保存坐标，避免外接显示器断开后窗口出现在不可见区域。

## 6. NavigationView 与页面导航

主导航容器使用前端本地的 `ModernNavigationView`，并通过实现 WPF-UI `INavigationView` 保持现有导航服务兼容。入口在 `MainWindow.xaml`：

- `RootNavigation.SetServiceProvider(serviceProvider)`；
- `navigationService.SetNavigationControl(RootNavigation)`；
- 菜单声明仍使用 WPF-UI `NavigationViewItem`，其 `TargetPageType` 指向页面类型；
- `NavigationPages.cs` 定义各个导航页类型。

`ModernNavigationView` 位于 `Controls/Modern/Navigation`，递归适配 `NavigationViewItem.MenuItems` / `MenuItemsSource`，保留嵌套展开、`Visibility`、`InfoBadge`、图标和原始 Binding。页面只由 `Controls/Modern/Frame/ModernFrame` 承载；页面创建顺序是 `IServiceProvider`、`INavigationViewPageProvider`、`Activator.CreateInstance`。不要把导航菜单改成 ViewModel 集合，也不要绕过 `INavigationService`。

File Types 的批量管理是 `FileTypesPageView` 内部的隐藏 `UserControl` 子视图，不是 NavigationView 页面，也不是 TabControl 中可直接选择的额外 TabItem。`FileTypesPageViewModel.IsBatchManagementActive` 控制正常 scene tabs 与 `FileTypeBatchManagementView` 的显隐；返回时只关闭隐藏子视图，保留原来的 tab 选择。Scene item 卡片里的“批量管理”入口只在 File Types host opt-in 且 item 具备文件类型注册表路径、ShellVerb 命令程序+key 或 ShellExtension CLSID 时显示。批量页标题展示源菜单项图标，列表行必须把扩展名、ProgID 或 `SystemFileAssociations` 来源提出来显示，完整注册表路径只作为次要诊断信息。删除后的相关项仍留在批量页中显示撤销入口；批量页提供单项撤销和全部撤销。文件类型 `open` / `edit` 这类核心 verb 不允许在批量页删除，只允许通过开关禁用。

`ModernFrame` 使用旧内容和新内容双 Presenter 播放导航过渡，支持 Entrance、Fade、SlideLeft、SlideRight、SlideBottom 和 Suppress；后退导航使用反向动画。转场沿用 neo 的分阶段时序：旧内容退出并清理后，新内容再进入，避免两个页面叠加位移；主窗口通过较短的 `TransitionDuration` 保持响应速度。`ModernNavigationView.Transition` / `TransitionDuration` 必须映射到本地 Frame，pane 开合使用独立的 EaseOut 宽度动画。动画被快速导航打断时必须清理旧 Storyboard、旧 PageHost 和过期的导航完成回调。

重复点击当前导航项或再次 `Navigate` 到当前页面时，应在创建 Page 之前短路，不产生动画或 journal 记录。带可导航子项的父级条目默认是分组：点击父级只展开/折叠，页面导航由子项负责。

### 6.1 导航页用 Page，可嵌入内容用 UserControl

WPF 的 `Page` 不是普通控件。`Page` 应由 `Window`、`Frame` 或导航容器承载，不应直接塞进 `Border.Child`、`Grid.Children`、`TabItem.Content` 或普通 `ContentControl`。

如果把 `Page` 当普通控件嵌入，可能在 `InitializeComponent()` 阶段抛出：

```text
InvalidOperationException: Page can only have Window or Frame as parent.
```

正确模式：

```text
SpecialMenuContentView : UserControl  // 可嵌入、可复用
SpecialMenuPageView    : Page         // 导航页 wrapper
```

一套 UI 如果既要作为导航页显示，又要嵌进其它页面的 TabItem，应拆成 UserControl 内容层 + Page wrapper。不要用 `Frame` 包一层来逃避这个问题，除非确实需要嵌套导航。

## 7. NavigationView 滚动与页面内部滚动

本地 `ModernFrame` 内容区域有一个共享的外层 `ModernScrollViewer`。如果不处理，切换页面后可能出现“新页面继承上一页滚动位置”的现象。

当前实现的规则是：

- `ModernFrame` 在内容实际呈现后重置自己的外层 `ContentScrollHost`；
- `ModernScrollViewer` 使用可重定向的垂直偏移动画实现平滑滚轮滚动；连续滚轮输入会更新当前动画目标，而不是堆积动画；
- 滚轮位于可继续滚动的页面内层 `ScrollViewer`、打开的 ComboBox 或 Popup 时，外层必须让渡输入；内层到达边界后才允许外层接管；
- 避免触碰页面内部自己的 `ListBox`、`ListView`、`ScrollViewer`。

需要在导航后定位共享外层滚动区域的页面应实现 `INavigationScrollTarget`。内容真正呈现后，`ModernFrame` 先 reset 外层 `ContentScrollHost`，再把该 `ScrollViewer` 传给当前内容或其可视树中的定位目标。定位回调执行完毕后才触发 `ModernFrame.NavigationCompleted`，`ModernNavigationView` 会转发该事件；需要等待页面就位的功能应订阅事件，不要使用固定 `Task.Delay`。`MainWindow` 不再查找 WPF-UI `NavigationViewContentPresenter`。

`ApplicationGroupsPage` 不再使用导航后滚动定位。分类页跳转到按应用管理页面时，页面通过 `GlobalSearchNavigationFilterService` 携带的 item id 进入精确筛选模式，只显示目标项所属分组和目标项本身。这样避免首次进入时全量渲染后再调用 `UpdateLayout` / 查找可视树。

边界规则：

```text
外层 NavigationView 滚动：可以在导航后重置
页面内部列表滚动：由页面自己控制，不要被 MainWindow 乱动
```

OtherRules 这类复杂页面要区分外层导航滚动、TabControl 页面滚动、左侧列表滚动和右侧详情滚动。不要因为滚动异常去改后端、注册表或菜单状态逻辑。

OtherRules 和 FileTypes 页面自身声明 `ModernScroll.Ownership="Self"`，由 Tab 内容管理内部滚动。OtherRules 的两个规则详情面板、`DragDrop` / `CommandStore` / `GuidBlock` 复用的 `SpecialMenuContentView`、以及 FileTypes 的 `SceneBrowserView` 和文件类型批量管理视图，必须以 `ModernScrollViewer` 承载可增长的内容；不要依赖 `ListBox` 的默认滚动条。

其它导航页面没有声明 `Ownership="Self"`，因此继续使用 `ModernFrame.ContentScrollHost` 这一共享的 `ModernScrollViewer`。不要在这些页面再包一层无约束的滚动宿主，否则会产生嵌套滚动或无限测量，反而使滚动失效。

SpecialMenu 的首次加载必须先让 UI 呈现加载占位和 `ProgressBar`，再请求后端快照。`CommandStore` 等结果较多的快照仅在首次加载时分批加入绑定集合，并异步提取图标；后续刷新必须原地差量更新，不能清空集合后重新添加。`CommandStore` 不启用定时全量刷新，依赖用户手动刷新和后端通知更新，避免持续重建列表造成滚动和布局卡顿。

按应用管理页（`ApplicationGroupsPageView`）**不要通过 `Ownership="Self"` 换虚拟化**：虚拟化 ListBox 会丢失 `ModernFrame.ContentScrollHost`（`ModernScrollViewer`）的平滑滚动动画，且分页/懒加载若使用 `Dispatcher.Yield(Background)` 会在渲染期间被饿死，导致列表一直"加载中"并卡顿。该页的"延迟异步加载 + 非完整加载"采用：
- 页面继续挂在 `ContentScrollHost` 下，保留外层平滑滚动；
- 首次重建同步渲染前 `InitialVisibleGroupCount`（40）组，页面立即可交互；
- 剩余组由 `DispatcherTimer`（`DispatcherPriority.Normal`，避免 Background 饿死）每 60ms 追加 12 组，直到全部渲染完成；
- 所有重建触发点（集合变化、条目属性变化、搜索、语言切换）走 ~120ms 防抖；新调度会停止旧追加定时器并重新开始；
- `IsLoading` 只在重建入口同步置位并在首屏填充后同步复位，不会出现"一直加载"；`IsEmpty` 在 `IsLoading` 期间不显示空占位。

Win11 菜单页（`Windows11ContextMenuItemViewModel`）的卡顿来自**分组项构造时同步读盘**：`Windows11ContextMenuService.LoadLogo` 是 async 方法但方法体在首个 `await` 前同步执行 `FileStream` + `BitmapImage` 解码，且无缓存，`RebuildItems` 一次性构造全部分组项时所有 Logo 都在 UI 线程串行读盘。修复：
- Logo 改为后台加载（`Task.Run` + 静态 `ConcurrentDictionary` 按 LogoPath 缓存），`LogoSource` 任务未完成返回 null、完成后 `OnPropertyChanged` 刷新，占位图标与真图标正常切换；
- `RebuildItems` 的触发源 `ItemsChanged` 事件走 ~120ms 防抖，避免扫描期间反复全量重建分组项（`Dispose` + 清空 + 重建）造成卡顿与 `CollectionChanged(Reset)` 引起的滚动跳动。

### 7.1 列表刷新时阻止 BringIntoView 冒泡到外层滚动宿主

绑定到 `ListCollectionView`（`ItemsView`）的 ListBox 在运行时调用 `ItemsView.Refresh()`（例如传统菜单页开关菜单项、Win11 页 `RebuildItems`）会触发 `CollectionChanged(Reset)`，导致 ListBox 重新生成所有 item 容器。容器重新生成期间，WPF 框架的焦点恢复 / 选中项恢复逻辑会引发 `FrameworkElement.RequestBringIntoView` 路由事件。此时 ListBox 内部 ScrollViewer 因容器尚未完成布局无法正确处理该事件，事件会继续**冒泡**到外层 `ModernScrollViewer`，外层滚动到让 ListBox 顶部可见——表现为页头标题与筛选框被滚出视野、列表顶部对齐窗口顶部。

修复方式是在对应 ListBox 上启用 `Behaviors.SuppressBringIntoViewBehavior`：

```xml
<ListBox
    behaviors:SuppressBringIntoViewBehavior.IsEnabled="True"
    ItemsSource="{Binding ItemsView}"
    ...>
```

该附加属性以 `handledEventsToo: true` 注册 `RequestBringIntoViewEvent`，在列表控件层标记 `Handled = true`，阻止事件冒泡到外层滚动宿主。由于 ListBox 内部 ScrollViewer 位于 ListBox 之下（事件先到达内层），列表自身的滚动行为不受影响。当前在传统菜单页（`CategoryPageView`）、Win11 菜单页（`Windows11ContextMenuPageView`）的 ListBox，以及 ShellNew 等 SpecialMenu 所用的 `SpecialMenuContentView` ItemsControl 上启用。其它运行时刷新或回写条目的列表如出现同样的“开关后滚动跳动”现象，可同样启用。

## 8. AutoSuggestBox 与全局搜索

标题栏全局搜索使用 WPF-UI `AutoSuggestBox`，但本项目不使用 WPF-UI 的默认过滤逻辑。

原因是本项目需要多字段搜索、自定义评分、经典菜单和 Win11 菜单合并、菜单项真实图标、跳转目标页面，以及跳转后给目标页面设置筛选文本。

传统菜单分类页、标题栏全局搜索和按应用管理页面都使用 `ContextMenuSearchMatcher` 的宽松匹配；标点、分隔符和符号不影响匹配。全局搜索候选来自已加载的 workspace/Win11 缓存，并包含传统菜单项与 Win11 菜单项的本地用户备注，输入时不得访问后端、注册表或文件系统。

### 8.1 必须拦截 TextChanged 默认过滤

WPF-UI `AutoSuggestBox` 在用户输入后会触发 `TextChanged`。如果事件没有设置 `args.Handled = true`，控件会使用 `OriginalItemsSource` 做默认过滤，并重新设置自己的 `ItemsSource`。

本项目的规则：

```csharp
if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
{
    args.Handled = true;
    viewModel.UpdateGlobalSearchText(args.Text);
    sender.ItemsSource = viewModel.GlobalSearchResults;
    sender.IsSuggestionListOpen = !string.IsNullOrWhiteSpace(args.Text)
                                  && viewModel.GlobalSearchResults.Count > 0;
}
```

如果“搜索结果已经算出来，按 Enter 也能跳转，但下拉框不显示”，优先检查 `TextChanged` 是否绑定、`args.Handled` 是否设置、`ItemsSource` 是否被默认过滤覆盖、`IsSuggestionListOpen` 是否被过早关闭。

### 8.2 SuggestionChosen 和 QuerySubmitted 的区别

WPF-UI `AutoSuggestBoxSuggestionChosenEventArgs` 使用 `SelectedItem`。

WPF-UI `AutoSuggestBoxQuerySubmittedEventArgs` 只有 `QueryText`，不要假设它有 WinUI/UWP 风格的 `ChosenSuggestion`。

本项目规则：

- 鼠标或键盘选择下拉项：走 `SuggestionChosen`，从 `args.SelectedItem` 取 `GlobalSearchResultViewModel`；
- 直接按 Enter：走 `QuerySubmitted`，打开当前结果列表第一项；
- `UpdateTextOnSelect="False"`，避免选择后控件自动把文本改成 item string；
- 跳转由 `ShellViewModel.OpenGlobalSearchResultCommand` 处理。

### 8.3 搜索结果行不要放复杂按钮

AutoSuggestBox 的建议列表在 Popup / 内部 ListView 中渲染。Popup 不在普通视觉树里，DataTemplate 内不应依赖：

- `RelativeSource AncestorType=Window`
- `ElementName=RootNavigation`
- 从 Popup 里找 `ShellViewModel`
- 标题栏专用 Button style
- 依赖错误 `TemplatedParent` 的复杂 Button 模板

当前搜索结果行右侧“跳转”只是文本提示。真正跳转由选择整行触发。

## 9. Popup / DataTemplate / Binding 边界

WPF 的 Popup 通常会脱离原视觉树。WPF-UI 的 AutoSuggestBox、Flyout、InfoBar 内部也可能引入额外模板边界。

在 Popup / Flyout 的 DataTemplate 内，推荐只绑定 item ViewModel 自身属性：

```xml
Text="{Binding DisplayName}"
Source="{Binding IconSource}"
Text="{Binding StateLabel}"
```

不推荐依赖外层窗口或 NavigationView 的 ancestor binding。更稳的做法是让 item template 只负责显示，选择事件在外层控件 code-behind 处理，或把必要命令明确放到 item ViewModel。

如果出现 `PressedForeground property not found on ContentPresenter`，通常说明某个 Button style 或模板被放到了错误上下文中。排查方向是 XAML Style / Template，而不是后端业务逻辑。

## 10. Button、Badge 与 Foreground

WPF-UI Button 的前景色由控件模板、`Appearance` 和主题资源共同决定。

Primary / Accent Button 的文字颜色应该使用 WPF-UI 的 accent text 资源，例如：

```xml
Color="{DynamicResource TextOnAccentFillColorPrimary}"
```

不要试图通过根容器 `Foreground="White"` 解决所有 Primary Button 的文字颜色。那通常只会影响普通文本，不一定影响 Button 模板内部状态。

状态标签建议使用 `Border + TextBlock`，并显式绑定动态资源。启用 / 禁用状态可以明确绑定不同 brush。不要硬编码黑白，也不要依赖父级 Foreground 继承。

## 11. 图标

本项目有两类图标：

| 图标类型 | 来源 | 用途 |
| --- | --- | --- |
| 导航 / 分类图标 | WPF-UI `SymbolIcon` | NavigationView、按钮、fallback 图标 |
| 菜单项真实图标 | `IconPreviewService` | 菜单项卡片、全局搜索结果 |
| Deep Analysis 运行时图标 | ProbeHost `iconPngBase64` | Deep Analysis 结果窗口 |

全局搜索结果应优先显示菜单项真实图标，而不是分类图标。

真实菜单项图标通常来自：

- `ContextMenuEntry.IconPath`
- `ContextMenuEntry.IconIndex`
- `ContextMenuEntry.FilePath` fallback
- `IconPreviewService.GetIcon(...)`

`SymbolIcon` 只能作为 fallback 或导航分类图标。

Deep Analysis 结果窗口显示的是 ProbeHost 运行时探测得到的菜单项图标。前端只解码 `ContextMenuDeepAnalysisMenuItem.IconPngBase64` 中的 PNG base64；为空或解码失败时不显示图标，也不提示错误。缺少图标通常表示 Shell Extension 使用自绘、回调图标或没有通过标准菜单位图暴露图标。

## 12. 页面筛选、占位状态与加载状态

页面内筛选和全局搜索不是同一个状态。

| 功能 | 位置 | 作用 |
| --- | --- | --- |
| 全局搜索 | MainWindow TitleBar | 搜索已加载候选项并跳转到对应页面 |
| 页面筛选 | Category / Win11 页面内部 | 在当前页面中过滤列表 |
| 加载 / 空状态 | 页面 UI | 告诉用户当前正在加载或确实没有数据 |

全局搜索跳转后，通过 `GlobalSearchNavigationFilterService` 请求目标页面把筛选框设置为选中菜单项名称。这样用户进入目标页面后尽可能只看到该项。

加载中 / 空状态是前端 UI 状态，不应触发后端状态修改。

传统分类页（`CategoryPageView`）和 File Types / Other Rules 所复用的 `SceneBrowserView` 共享前端设置中的 `ContextMenuListViewMode`：默认是精简视图，用户切换后会全局持久化。精简视图保留真实菜单项 `DisplayName`、图标、启用开关、更多操作入口以及待审核、检测到变更、一致性问题、注册表缺失和已删除等可操作状态；注册表路径、命令和备注等技术详情改由详细视图或“更多”操作访问。若已有命令程序路径，精简卡片可以显示其文件名作为来源提示；绝不能以 CLSID、注册表路径或内部 identity 当作应用名称，也不能在渲染时做文件或 PE 元数据扫描。

同一处还提供“隐藏已禁用项”，它直接使用 `FrontendSettings.HideDisabledItems`。开启后只隐藏未删除且已禁用的项目；已删除项目必须继续可见，以便撤销或永久删除。切换视图模式不会刷新后端、重建 workspace 或清空搜索文本；切换此筛选只刷新当前 `ItemsView`，不修改注册表。排序固定为 attention、deleted-state、display name，不能加入 `IsEnabled`，否则用户开关项目时会造成列表跳位。

SpecialMenu 行内操作的结构性可见性不能依赖临时 `IsBusy` 状态。例如可移动项目在 Busy 时仍显示 Move Up/Down 按钮，只通过 `CanMoveUp` / `CanMoveDown` 禁用它们；只有项目不支持移动、已删除或不存在可用方向时才折叠按钮。这样右侧 `Auto` 宽度操作区在异步操作期间保持稳定，不会发生横向跳动。

## 13. InfoBar、错误提示与用户友好失败

`MainWindow.xaml` 中的 `RootInfoBar` 由 `InfoBarService` 管理。它适合展示全局、非阻塞、用户可读的状态，例如服务状态提醒、需要重启 Explorer、更新提示或某些操作失败的简短说明。

Deep Analysis 失败、ProbeHost crash、Shell Extension 不兼容等属于可预期的 best-effort 失败，前端应展示用户友好文案，而不是默认提示“请报告 Bug”。

规则：

- 用户界面展示简短、可理解的原因；
- 详细诊断放日志或诊断区域；
- 不把完整堆栈直接暴露给普通用户；
- 但不要吞掉对开发者有价值的错误码、路径、架构信息。

设置页“增强”卡片包含 Registry Write Protection 和 Win11 全局恢复经典右键菜单两个独立开关。前者有单独风险确认 flyout；后者只通过说明文字提示需要重启 Explorer 或重新登录，不应在切换时自动重启 Explorer，也不应复用 Registry Write Protection 的确认逻辑。

只有开发者需求、目标参考代码或规则字典明确标注“需要重启 Explorer / 资源管理器后生效”的设置，才应联动主窗口已有的全局重启按钮；不要只凭经验推断生效条件，也不要在各页面重复添加按钮。当前约定是注入单例 `ExplorerRestartStateService`，操作成功后调用 `MarkRequired()`；`MainWindow.xaml` 通过 `ShellViewModel.NeedsExplorerRestart` 显示顶部全局“重启资源管理器”按钮，用户点击后由 `ShellViewModel.RestartExplorerCommand` 调用后端并在成功后 `Clear()`。

Win+X 页面固定显示新版 Windows 兼容性提示，并通过 Tooltip 说明：快捷方式在应用中可见但未出现在实际 Win+X 菜单时，可能是新版 Windows 的额外筛选限制，而不是创建或 hash 写入失败。

## 14. 本地化

`App.xaml` 中通过 `CurrentLanguage` 资源和全局 Style 给 `Window` / `Page` / `Label` / `TextBlock` 设置 `Language`。

新增用户可见文本时应：

- 放入 `Resources/Strings.resx`；
- 同步 `Strings.zh-CN.resx`；
- 同步 `Strings.zh-TW.resx`；
- 在 ViewModel 或 LocalizationService 中引用；
- 不要在 XAML 或 C# 中硬编码长期用户可见文本。

技术日志可以保留英文 key，但面向用户的窗口标题、按钮、提示、错误消息应走本地化资源。

## 15. 前端排错速查表

| 现象 | 优先检查 | 不要先改 |
| --- | --- | --- |
| 主题启动时不对 | `FrontendThemeService.Initialize`、保存的 theme preference、`DynamicResource` | 后端服务 / 注册表菜单逻辑 |
| TextBlock 前景色是黑色 | `App.xaml` 全局 `TextBlock` style、资源 key、是否在控件模板内 | Registry / Backend |
| Primary Button 文字颜色不对 | WPF-UI Button theme resources、Accent foreground resource、Button style `BasedOn` | 根容器强制 Foreground |
| AutoSuggestBox 搜得到但不显示下拉 | `TextChanged.Handled`、`ItemsSource`、`OriginalItemsSource` 默认过滤 | 后端扫描 |
| AutoSuggestBox 点击不跳转 | `SuggestionChosen.SelectedItem`、`QuerySubmitted.QueryText`、`OpenGlobalSearchResultCommand` | 搜索候选池重建逻辑 |
| Popup 里 Binding 报错 | DataTemplate ancestor binding、错误 Button style、Popup 视觉树边界 | Backend / Registry |
| NavigationView 切页滚动位置继承 | `ModernFrame.ContentScrollHost` 的导航完成 reset | 页面数据加载 |
| “Page 只能具有 Window 或 Frame 父级” | 是否把 `Page` 嵌进 `Border` / `TabItem`；应拆 `UserControl` | 滚动代码 / 后端 |
| OtherRules 双栏滚动异常 | 页面内部 ScrollViewer、外层 NavigationView scroll reset、TabControl selection handling | Registry catalog |
| 全局搜索跳转后目标页面未筛选 | `GlobalSearchNavigationFilterService`、目标页面 ViewModel 订阅 | 后端 snapshot |
| 图标显示成分类图标 | `IconPreviewService`、`IconPath` / `IconIndex` / `FilePath` | NavigationView symbol 配置 |
| 加载中 / 空状态显示不对 | 页面 loading/empty state property、placeholder binding、调试状态服务 | 后端菜单开关 |

## 16. 开发规则

修改前端时请先检查：

- [ ] 新页面如果用于导航，根类型可以是 `Page`。
- [ ] 可嵌入复用的内容必须是 `UserControl`，不要把 `Page` 塞进普通容器。
- [ ] 主题相关颜色和 Brush 使用 `DynamicResource`。
- [ ] 标题栏里的可交互控件设置 `WindowChrome.IsHitTestVisibleInChrome=True`。
- [ ] 修改 AutoSuggestBox 时确认 `TextChanged` 对自定义过滤设置 `Handled=True`。
- [ ] Popup / Flyout / AutoSuggestBox DataTemplate 不依赖外层视觉树 ancestor。
- [ ] 不把 `ChromeTitleBarButton` 这类专用 Style 用到普通按钮或 Popup item。
- [ ] 不用根容器 `Foreground` 强行修 WPF-UI 控件内部文字颜色。
- [ ] 不因为 UI 状态问题去改 Backend Service、Registry catalog 或权限链路。
- [ ] 新增用户可见文本同步 Resources。
- [ ] 新增全局搜索展示字段时不引入每次输入后端查询或文件 IO。
- [ ] 修改滚动逻辑时区分 NavigationView 外层滚动和页面内部滚动。

## 17. 什么时候需要更新本文

如果修改以下内容，应同步更新本文：

- WPF-UI 版本；
- `App.xaml` 全局样式；
- 主题服务；
- 主窗口标题栏；
- NavigationView 页面结构；
- Page / UserControl 复用结构；
- AutoSuggestBox 全局搜索；
- Popup / Flyout 模板；
- 全局搜索结果 item template；
- 页面加载 / 空状态；
- 图标解析和展示策略；
- 前端本地化规则。

前端问题的排查文档要以当前 XAML 和 ViewModel 实现为准，不要只复述 WPF-UI 官方文档。
