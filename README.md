# Context Menu Manager

[English Documentation](./README.en.md)

> [!WARNING]
> 本项目的相当一部分代码由 AI 辅助生成，并经过持续人工修改与整合，但仍然可能存在遗漏、边界情况处理不足或行为与预期不完全一致的问题。
> 如果你在使用过程中发现 Bug、异常行为、文档缺失或兼容性问题，欢迎积极提交 Issue，最好附上复现步骤、日志和截图，这会非常有帮助。

## 项目简介

`Context Menu Manager` 一个面向 Windows 的右键菜单管理工具

- `.NET 10`
- `WPF`
- `WPF-UI`
- `Named Pipe IPC`
- `Windows Service`

它由两个进程组成：

- `ContextMenuManager.exe`
  普通权限的前端桌面程序，负责 UI、设置、审核队列、规则编辑和安装引导。
- `ContextMenuManager.Service.exe`
  高权限后端服务，负责扫描和修改右键菜单注册表项、监控新增项、处理审核决策、维护状态库。

## 核心特色

本项目最重要的特性不是“普通的右键菜单开关器”，而是：

- 新增右键菜单项被检测到时，不是先放进系统里生效，而是先由后端服务立即拦截并禁用
- 被拦截的项目会进入“待审核”队列，等待用户手动处理
- 用户可以明确选择：
  - 允许：启用该菜单项
  - 保持禁用：保留该项目，但不让它生效
  - 移除：直接删除该项目

也就是说，这个项目的核心设计目标是：

- 先拦截
- 再审核
- 最后由用户手动放行

这也是它和一般“右键菜单管理器”最不同的地方。

## 当前功能

- 按分类浏览 Windows 右键菜单项
  - 文件
  - 所有对象
  - 文件夹
  - 目录
  - 目录背景
  - 桌面背景
  - 磁盘分区
  - 库
  - 此电脑
  - 回收站
- 启用 / 禁用菜单项
- 删除菜单项、撤销删除、永久删除
- 新增菜单项审核
  - 新菜单项出现时，先禁用并进入待审核队列
  - 允许：启用
  - 保持禁用：维持禁用状态
  - 移除：删除该项或从审核列表移除
- 检测外部修改
  - 外部新增
  - 外部缺失
  - 外部修改
  - 删除后再次出现
- 图标与名称解析
  - Shell Verb
  - ShellEx / CLSID
  - 一部分系统项、打包应用项、GUID 映射项
- 文件类型页
  - 快捷方式
  - UWP 快捷方式
  - 可执行文件
  - 自定义扩展名
  - 感知类型
  - 目录类型
  - 未知类型
- 其他规则页
  - 增强菜单
  - 详细编辑
  - 自定义注册表路径
- 设置页
  - 语言切换：跟随系统 / 中文 / English (United States)
  - 主题切换：跟随系统 / 浅色 / 深色
  - 开机时以最小化启动
  - 日志等级
  - 安装 / 修复 / 卸载服务
  - 重启资源管理器
  - 打开日志、状态库、配置目录
  - 注册表权限锁定开关

## 架构概览

### Frontend

前端是一个 WPF 桌面程序，使用 `WPF-UI` 做 Fluent 风格界面。主要职责：

- 展示菜单项和分类导航
- 展示待审核数量
- 操作启用 / 禁用 / 删除 / 撤销
- 配置语言、主题、服务、日志等设置
- 通过 Named Pipe 与后端通信

### Backend Service

后端是高权限服务，主要职责：

- 枚举和监控注册表中的右键菜单项
- 修改菜单项启用状态
- 对新菜单项先禁用再进入审核
- 保存状态库和删除备份
- 为前端提供 IPC 接口

### IPC

前后端通过 `Named Pipe` 进行 JSON 请求 / 响应通信。

## 主要注册表范围

项目当前重点处理以下类型的右键菜单范围：

- `HKEY_CLASSES_ROOT\*\shell`
- `HKEY_CLASSES_ROOT\*\shellex\ContextMenuHandlers`
- `HKEY_CLASSES_ROOT\Directory\shell`
- `HKEY_CLASSES_ROOT\Directory\shellex\ContextMenuHandlers`
- `HKEY_CLASSES_ROOT\Directory\Background\shell`
- `HKEY_CLASSES_ROOT\Directory\Background\shellex\ContextMenuHandlers`
- 各类 `CLSID`、`PackagedCom`、文件类型和扩展名相关分支

## 目录结构

```text
ContextMenuMgr/
├─ ContextMenuMgr.Frontend/         # WPF 前端
├─ ContextMenuMgr.Backend/          # Windows Service 后端
├─ ContextMenuMgr.Contracts/        # 前后端共享契约
├─ Installer/                       # Inno Setup 安装脚本
├─ build.ps1                        # 外层构建 + publish + 打包脚本
├─ build.bat                        # build.ps1 的批处理入口
├─ ContextMenuMgr.slnx              # 解决方案
├─ README.md                        # 中文主说明
└─ README.en.md                     # 英文说明
```

## 开发环境要求

- Windows 11 x64
- .NET SDK 10
- PowerShell 5.1 或更高
- Inno Setup 6
  - 默认脚本路径使用：`Installer\Inno Setup 6\ISCC.exe`

## 本地构建

```powershell
dotnet restore .\ContextMenuMgr.slnx --configfile .\NuGet.Config
dotnet build .\ContextMenuMgr.slnx --no-restore
```

## 发布与安装包构建

直接执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Release
```

构建脚本会自动执行：

1. `dotnet restore`
2. `dotnet publish` 前端项目到 `build\ContextMenuManager`
3. 使用 Inno Setup 生成安装包

默认输出：

- 发布目录：`build\ContextMenuManager\`
- 安装包：`build\ContextMenuManager_Setup.exe`

## 可执行文件名称

对外发布名称已经统一为：

- 前端：`ContextMenuManager.exe`
- 服务：`ContextMenuManager.Service.exe`

## 日志、状态库、配置文件位置

当前默认位置如下：

- 前端日志：
  - `%LocalAppData%\ContextMenuMgr\Logs\frontend-debug.log`
  - `%LocalAppData%\ContextMenuMgr\Logs\frontend-crash.log`
- 后端日志：
  - `%ProgramData%\ContextMenuMgr\Logs\backend.log`
- 前端配置：
  - `%LocalAppData%\ContextMenuMgr\frontend-settings.json`
- 后端状态库：
  - `%ProgramData%\ContextMenuMgr\Data\context-menu-state.json`

说明：

- 目前应用对外显示名已统一为 `Context Menu Manager`
- 但本地数据目录仍暂时保留 `ContextMenuMgr` 历史命名，以兼容现有数据

## 服务安装与启动说明

- 前端启动时会尝试连接服务
- 如果服务未安装或不可用，可以在设置页中执行：
  - 安装服务
  - 修复服务
  - 卸载服务
- 前端退出后，后端会根据当前实现自动回收，避免后台残留

## 注意事项

- 某些系统保护注册表项不能被普通方式修改 ACL，这是 Windows 限制，不是程序 Bug
- 某些安全软件可能会拦截删除、恢复或注册表写入，这时前端会提示超时或失败
- 图标和名称解析依赖系统注册表、资源字符串、文件路径、CLSID / GUID 映射，不保证 100% 覆盖所有第三方项

## License

本项目遵循 GPL V3.0 协议开源，See [LICENSE](./LICENSE).
