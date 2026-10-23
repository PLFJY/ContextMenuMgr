# Deep Analysis 与 ProbeHost 实现说明

## Deep Analysis 的目的

Deep Analysis 用于尝试解析 Shell Extension 实际插入到右键菜单中的菜单文字、图标、canonical verb 和帮助文本。它不是普通菜单开关功能，也不是审核流程的必要条件。

普通 snapshot 只能看到注册表中的 handler、CLSID、命令和路径；第三方 Shell Extension 真正添加哪些菜单项，需要在进程中创建 COM handler 并调用 `IContextMenu.QueryContextMenu`。这一步风险高，所以放到 ProbeHost。

## 为什么需要 ProbeHost

第三方 Shell Extension 通常是 in-process COM DLL。直接在前端或后端加载会带来这些风险：

| 风险 | 说明 |
| --- | --- |
| 崩溃 | 第三方 DLL 的 access violation 会拖垮宿主进程。 |
| 卡死 | handler 可能阻塞 Explorer 或宿主线程。 |
| 异常行为 | handler 可能依赖 Explorer 环境、选中文件、线程模型或外部组件。 |
| 架构限制 | x86 handler 不能加载到 x64 进程，arm64 也有类似限制。 |

`ContextMenuMgr.ProbeHost` 是短生命周期 native C++ Win32 隔离进程。它可以崩溃或超时，而不影响前端、后端服务和普通菜单管理。

## ProbeHost 的边界

ProbeHost 必须保持边界清晰：

| 边界 | 说明 |
| --- | --- |
| 不写注册表 | ProbeHost 只读取必要的 COM 信息并执行探测。 |
| 不执行菜单命令 | 它只枚举菜单文本，不调用菜单命令。 |
| 不常驻 | 只在用户点击 Deep Analysis 时由前端启动。 |
| 不参与普通扫描 | 普通 snapshot 不依赖 ProbeHost。 |
| 可失败 | 超时、崩溃、无结果、COM 初始化失败都是可接受失败。 |

不要把 ProbeHost 当提权链路。它由前端启动，解决的是第三方 COM 隔离和架构匹配，不解决管理员权限。

## 架构选择

当前前端 `ContextMenuDeepAnalysisService` 会根据 handler DLL 的 PE machine type 选择 ProbeHost：

| handler 架构 | 选择目录 |
| --- | --- |
| x86 | `ProbeHost\x86\ContextMenuMgr.ProbeHost.exe` |
| x64 | `ProbeHost\x64\ContextMenuMgr.ProbeHost.exe` |
| arm64 | `ProbeHost\arm64\ContextMenuMgr.ProbeHost.exe` |
| 未知 | 回退到当前前端进程架构，并允许 root fallback。 |

ARM64 Windows 上也可能需要 x64 或 x86 ProbeHost，因为第三方 handler 可能不是 arm64。ProbeHost 架构必须匹配目标 Shell Extension DLL，否则 `CoCreateInstance` 或 DLL 加载会失败，严重时会导致 native crash。

Debug 和 Release 构建产物都要验证目录标签与实际 PE machine type 一致。`Scripts/Verify-ProbeHostArchitecture.ps1` 会检查 `x86`、`x64`、`arm64` 目录中的 `ContextMenuMgr.ProbeHost.exe` 是否对应预期 machine 值。

## SpecificHandler 与 WholeContextMenu

| 模式 | 说明 | 注意事项 |
| --- | --- | --- |
| `SpecificHandler` | 只 CoCreate 当前 `HandlerClsid`，初始化该 handler，再调用 `IContextMenu.QueryContextMenu`。 | 结果更接近当前条目，但更容易因 handler 不支持 `IShellExtInit` 或初始化上下文而失败。 |
| `WholeContextMenu` | 创建示例目标的完整 shell context menu，再枚举整个菜单。 | 结果可能包含其它软件、系统菜单项和 Explorer 自身菜单，不能当作当前 handler 的精确结果。 |

不要自动把 `SpecificHandler` 失败后 `WholeContextMenu` 成功解释成“当前 handler 成功”。这两个模式回答的问题不同。

## 请求与结果流程

```text
Frontend
-> ContextMenuDeepAnalysisService.AnalyzeAsync
-> 解析 HandlerFilePath / CLSID
-> 选择 ProbeHost 架构
-> 写 request.json 到临时目录
-> 启动 ContextMenuMgr.ProbeHost.exe
-> ProbeHost 解析 COM / IContextMenu
-> 写 result.json 或输出 JSON
-> Frontend 捕获 stdout / stderr
-> 读取 ContextMenuDeepAnalysisResult
-> ContextMenuDeepAnalysisWindow 展示
```

`ContextMenuDeepAnalysisRequest` 和 `ContextMenuDeepAnalysisResult` 定义在 `ContextMenuMgr.Contracts/ContextMenuDeepAnalysisContracts.cs`。前端会设置 `WorkingDirectory` 为 ProbeHost 所在目录，并捕获 stdout、stderr、exit code 和 result file。

结果中的菜单项可选包含 `iconPngBase64`。该字段只表示 ProbeHost 已成功把菜单项的运行时图标编码为 PNG base64；缺失或为空表示没有可用图标或图标提取失败。旧结果 JSON 不包含该字段时仍应正常反序列化。

## 菜单项图标

ProbeHost 在枚举 `HMENU` 时通过 `MENUITEMINFO.hbmpItem` best-effort 读取标准菜单位图图标，并在能安全转换时把 PNG base64 写入 `iconPngBase64`。图标提取失败只影响单个菜单项图标，不应导致 `QueryContextMenu` 结果失败，也不应让 ProbeHost 进程失败。

当前只支持 Shell Extension 通过 `MENUITEMINFO.hbmpItem` 暴露的标准 `HBITMAP`。以下情况会被有意跳过：

- `HBMMENU_*` 预定义值和 `HBMMENU_CALLBACK`；
- owner-draw 菜单项；
- 回调图标或扩展自绘图标；
- 无法安全转换或编码后过大的位图。

缺少图标是正常现象，不能单独视为 Deep Analysis 或普通菜单管理 Bug。

## 失败分类

很多失败是正常限制，不应直接提示用户报告 Bug。

| 错误码 | 含义 |
| --- | --- |
| `MissingX86ProbeHost` / `MissingX64ProbeHost` / `MissingArm64ProbeHost` | 对应架构的 ProbeHost 不存在。 |
| `ProbeHostExecutableArchitectureMismatch` | 目录标签和 ProbeHost exe 实际 PE 架构不一致。 |
| `ArchitectureMismatch` | ProbeHost 进程架构与 handler DLL 架构不兼容。 |
| `CoCreateHandlerFailed` / `CoCreateHandlerNoIUnknown` | 创建 COM handler 失败。 |
| `ShellExtInitNotSupported` | handler 不支持 `IShellExtInit`。 |
| `IContextMenuNotSupported` | handler 不支持 `IContextMenu`。 |
| `QueryContextMenuFailed` | 调用 `IContextMenu.QueryContextMenu` 失败。 |
| `SpecificHandlerReturnedNoItems` | handler 成功返回但没有可显示项。 |
| `ProbeHostNativeCrash` / `ProbeHostNativeAccessViolation` / `ProbeHostStackBufferOverrun` | ProbeHost 被第三方 native 代码拖崩。 |
| `Timeout` | 超过默认 5 秒或传入超时时间，前端会尝试结束进程。 |
| `InvalidProbeHostJson` / `InvalidProbeHostOutput` | ProbeHost 未返回合法 JSON。 |

## 构建、验证与维护

Native build、Debug/Release 架构集合、许可与打包规则仅在[构建发布](build-and-release.md#probehost-架构与许可)维护；这里负责运行时选择/隔离。源码入口：[前端分析服务](../ContextMenuMgr.Frontend/Services/ContextMenuDeepAnalysisService.cs)、[ProbeHost main](../ContextMenuMgr.ProbeHost/src/main.cpp)、[ShellProbe](../ContextMenuMgr.ProbeHost/src/ShellProbe.cpp)、[协议](../ContextMenuMgr.ProbeHost/src/Protocol.cpp)、[契约](../ContextMenuMgr.Contracts/ContextMenuDeepAnalysisContracts.cs)。

前端优先读取 result file，再尝试 stdout JSON；记录 exit code、stdout/stderr 和 DiagnosticDetails。超时后尝试结束进程，取消/关闭时清理临时请求/结果；第三方 DLL 的进程内操作不受本产品“只读探测”契约约束，因此这是崩溃/架构隔离，不是安全沙箱。不能保证第三方代码无外部副作用。

手验应记录 DLL/ProbeHost PE、实际 sample 类型、模式、进程退出/超时及 result/stderr，对照 SpecificHandler 与 WholeContextMenu；空/自绘图标不得使菜单失败。实际第三方 COM、Explorer 依赖、32/64/ARM64 和卡死/崩溃必须在可恢复 Windows 环境检查，构建和序列化测试不证明具体扩展可探测。

修改架构选择、request/result、错误分类、图标/超时清理时更新本文，布局变化同步构建文档；不得将隔离失败修成 Frontend/Backend 直接加载 DLL。

## 常见坑

| 坑 | 正确处理 |
| --- | --- |
| 在前端或后端直接 `CoCreateInstance` 第三方 handler | 只能通过 ProbeHost 隔离。 |
| 把 ProbeHost 当提权进程 | 它不是提权链路。 |
| SpecificHandler 失败后把 WholeContextMenu 成功当作当前 handler 成功 | 两者结果语义不同。 |
| 把 menu accelerator `&` 当乱码 | `&` 是 Windows 菜单助记符语义。 |
| 把缺失图标当作错误 | 图标提取是 best-effort；自绘、回调或无法转换的图标可能为空。 |
| 把 ProbeHost crash 当作普通菜单开关失败 | Deep Analysis 失败不应影响开关、删除、审核。 |
| 忘记设置 `WorkingDirectory` | 偏离当前启动契约，可能影响扩展的相对依赖；ProbeHost 自身是 native，不需要 managed runtimeconfig。 |
| 把 x86 binary 放进 arm64 目录 | 架构验证会失败，运行时也会误判。 |
