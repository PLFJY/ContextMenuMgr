<p align="center">
    <picture>
      <source media="(prefers-color-scheme: light)" srcset="./doc/images/readme/pt-hero.light.png" />
      <img src="./ContextMenuMgr.Frontend/Assets/AppIcon.png" style="height: 100px; width: 100px" />
  </picture>
</p>
<h1 align="center">
  <span>Context Menu Manager Plus</span>
</h1>
<p align="center">
  <span align="center">Context Menu Manager Plus 是一个强大的实用程序，它可帮助您管理 Windows 上的右键菜单，并避免第三方向你的右键菜单里塞屎。</span>
</p>

[English Version](./README.en.md)

> [!WARNING]
> 本项目的相当一部分代码由 AI 辅助生成，并经过持续的人工作业、联调和重构，但它仍然可能存在遗漏、边界情况处理不足或行为与预期不完全一致的问题。
> 如果你在使用过程中发现 Bug、兼容性问题、异常行为或文档缺失，欢迎积极提交 Issue。最好附上复现步骤、日志、截图和系统版本信息，这会非常有帮助。

## 项目简介

`Context Menu Manager Plus` 是一个面向 Windows 的右键菜单管理工具，重点不是“普通开关器”，而是：

- 检测新增右键菜单项
- 先拦截并默认禁用
- 再交给用户审核
- 最后由用户手动决定是否放行

项目基于：

- `.NET 10`
- `WPF`
- `WPF-UI`
- `Named Pipe IPC`
- `Windows Service`
- 原生 Win32 托盘宿主

## 核心特色

### 先拦截，再审核

这是本项目最重要的设计目标：

- 新的右键菜单项被检测到时，不是先让它直接生效
- 后端会先把它拦截为禁用状态
- 然后放入“待审核”队列
- 用户再手动选择：
  - `允许`：启用该菜单项
  - `保持禁用`：保留条目，但维持禁用
  - `移除`：删除该条目，或从审核列表中移除

这也是它和一般右键菜单管理器最大的不同。

### 后端主导架构

项目采用**后端主导**模型：

- `ContextMenuManagerPlus.Service.exe`
  - 真正的核心控制器
  - 负责监控、审核、状态库、IPC、服务生命周期
- `ContextMenuManagerPlus.TrayHost.exe`
  - 独立的每用户托盘宿主
  - 负责托盘图标、托盘菜单、系统通知、拉起前端
- `ContextMenuManagerPlus.exe`
  - 纯前端 UI
  - 按需打开
  - 关闭窗口即退出

tray 作为独立的会话侧宿主存在，前端只负责 UI。

## 功能

### 菜单项管理

- 按分类浏览右键菜单项
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
- 启用 / 禁用右键菜单项
- 删除菜单项
- 撤销删除
- 永久删除删除备份
- 搜索与筛选
- 一部分名称、图标、命令文本、CLSID 元数据解析

### 审核队列

- 新增项先进入待审核
- 待审核页支持：
  - 允许
  - 保持禁用
  - 移除
- 审核页可聚合同一逻辑项的多个分类来源
- 新增待审核项时：
  - 后端广播事件
  - tray host 弹系统通知
  - 点击通知会拉起前端并跳转到审核页

### 外部变化检测

外部变化检测重点保留：

- 外部新增
- 守护离线期间的外部开关变化

### 文件类型与规则页

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

### 设置页

- 语言切换
  - 跟随系统
  - 简体中文
  - English (United States)
- 主题切换
  - 跟随系统
  - 浅色
  - 深色
- 日志等级
- 随 Windows 启动
- 安装 / 修复服务
- 卸载服务
- 重启资源管理器
- 打开日志目录 / 状态库目录 / 配置目录
- 注册表保护增强开关

## 架构

### 1. Backend Service

项目中的 backend 是真正的主控层：

- 项目：`ContextMenuMgr.Backend`
- 对外可执行文件：`ContextMenuManagerPlus.Service.exe`

职责：

- 扫描并解析右键菜单相关注册表项
- 保存和合并本地状态库
- 执行启用 / 禁用 / 删除 / 恢复 / 审核决策
- 通过 Named Pipe 对外提供 IPC
- 在合适时机尝试拉起 tray host

### 2. Tray Host

项目：`ContextMenuMgr.TrayHost`  
对外可执行文件：`ContextMenuManagerPlus.TrayHost.exe`

职责保持很薄：

- 托盘图标
- 托盘菜单
- 系统通知
- 打开前端主界面
- 打开审核页
- 请求后端退出

Tray host 使用**原生 Win32 托盘实现**。

### 3. Frontend

项目：`ContextMenuMgr.Frontend`  
对外可执行文件：`ContextMenuManagerPlus.exe`

职责：

- 展示主界面
- 展示审核页
- 展示规则与设置
- 通过 Named Pipe 与 backend 通信
- 通过独立控制管道与 tray host / frontend 单实例逻辑协作

前端是 UI-only：

- 关闭窗口 = 退出前端进程
- 不负责托盘
- 不保留后台常驻前端进程

### 4. Shared Contracts

项目：`ContextMenuMgr.Contracts`

职责：

- IPC 请求 / 响应模型
- 通知类型
- 前端与 tray host 控制命令
- 共享常量和协议定义

## IPC 与进程协作

### Backend Pipe

主要通过 `Named Pipe` 做 JSON 请求/响应通信，用于：

- 获取快照
- 修改菜单项状态
- 执行审核决策
- 获取 / 设置保护开关
- 请求 backend 尝试拉起 tray host
- 请求 backend 正常关闭

### Frontend Control Pipe

前端有自己的控制通道，用于：

- 单实例激活
- 打开主窗口
- 跳转到审核页
- 按 id 聚焦审核项
- 正常关闭前端

### TrayHost Control Pipe

tray host 也有自己的控制通道，用于：

- 退出 tray host
- 刷新 tray 本地化文案

## 主要注册表范围

重点处理以下范围：

- `HKEY_CLASSES_ROOT\*\shell`
- `HKEY_CLASSES_ROOT\*\shellex\ContextMenuHandlers`
- `HKEY_CLASSES_ROOT\Directory\shell`
- `HKEY_CLASSES_ROOT\Directory\shellex\ContextMenuHandlers`
- `HKEY_CLASSES_ROOT\Directory\Background\shell`
- `HKEY_CLASSES_ROOT\Directory\Background\shellex\ContextMenuHandlers`
- `CLSID`
- `PackagedCom`
- 各类文件类型、扩展名、感知类型、目录类型相关分支
- 用户级 `HKCU/HKEY_USERS\<SID>\Software\Classes` 对应范围

## 仓库结构

```text
ContextMenuMgr/
├─ .github/                        # GitHub Actions
├─ artifacts/                      # 本地构建中间产物
├─ build/                          # publish 输出与安装包
├─ ContextMenuMgr.Backend/         # Windows Service / 核心后端
├─ ContextMenuMgr.Contracts/       # 共享契约
├─ ContextMenuMgr.Frontend/        # WPF 前端
├─ ContextMenuMgr.TrayHost/        # 每用户 tray 宿主
├─ Installer/                      # Inno Setup 脚本与相关资源
├─ build.ps1                       # 主构建脚本
├─ build.bat                       # build.ps1 批处理入口
├─ ContextMenuMgr.slnx             # 解决方案
├─ README.md                       # 中文主 README
└─ README.en.md                    # 英文 README
```

## 可执行文件与产物

对外名称统一为：

- 前端：`ContextMenuManagerPlus.exe`
- 后端服务：`ContextMenuManagerPlus.Service.exe`
- 托盘宿主：`ContextMenuManagerPlus.TrayHost.exe`

## 开发环境要求

- Windows 10 / 11
- .NET SDK 10
- PowerShell 5.1 或更高
- Inno Setup 6
  - 默认优先使用仓库内置：
    - `Installer\Inno Setup 6\ISCC.exe`

## 本地开发构建

```powershell
dotnet restore .\ContextMenuMgr.slnx --configfile .\NuGet.Config
dotnet build .\ContextMenuMgr.slnx --no-restore
```

## 发布与安装包构建

直接执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Release
```

### `build.ps1` 的行为

它会：

1. `restore` 整个解决方案
2. 分别 `publish`：
   - Frontend
   - Backend
   - TrayHost
3. 对以下架构和分发模式逐个生成安装包：
   - `win-x64`
   - `win-x86`
   - `win-arm64`
   - `self-contained`
   - `framework-dependent`
4. 通过 Inno Setup 生成安装包

脚本会生成 **多架构 + 多模式** 的安装包组合。

默认输出目录：

- publish 输出：`build\publish\`
- 安装包输出：`build\dist\`

`build\dist\` 中还会生成：

- `artifacts.txt`

用于列出本次生成的安装包列表。

## GitHub Actions

仓库中包含：

- `.github/workflows/manual-release.yml`

工作流行为：

- 支持手动触发
- 支持 tag 发布
- 会调用 `build.ps1`
- 上传构建产物
- 自动创建 Draft Release
- Release 的版本号和标题会从项目版本信息中解析

## 本地数据、日志与状态库

### 前端

- 配置：
  - `%LocalAppData%\ContextMenuMgr\frontend-settings.json`
- 日志：
  - `%LocalAppData%\ContextMenuMgr\Logs\frontend-debug.log`
  - `%LocalAppData%\ContextMenuMgr\Logs\frontend-crash.log`

### Tray Host

- 日志：
  - `%LocalAppData%\ContextMenuMgr\Logs\trayhost.log`

### Backend

- 日志：
  - `%ProgramData%\ContextMenuMgr\Logs\backend.log`
- 状态库：
  - `%ProgramData%\ContextMenuMgr\Data\context-menu-state.json`

说明：

- 产品对外名称为 `Context Menu Manager Plus`
- 本地数据目录保留 `ContextMenuMgr` 历史命名，以兼容旧数据

## 运行与恢复说明

### 正常链路

- 前端启动时会尝试连接 backend
- backend 可用后，前端会显式请求 backend 确保 tray host 存在
- backend 负责按当前会话拉起 tray host

### 审核通知链路

- backend 检测到新项
- backend 广播通知
- tray host 订阅到事件后弹系统通知
- 用户点击通知
- tray host 拉起 frontend 并跳转到审核页

### 异常恢复

- 如果 backend/service 异常退出
  - tray 可能一起消失
  - 但 frontend 不应被强退
- 用户可以在前端设置页使用：
  - 安装 / 修复服务
  来恢复后端

## 注意事项

- 某些系统保护的注册表根键无法被普通方式修改 ACL，这是 Windows 本身的限制
- 某些安全软件可能会拦截删除、恢复或注册表写入操作
- 图标与显示名解析是 best-effort 逻辑，不保证 100% 覆盖所有第三方菜单项
- 用户级右键菜单项、系统打包项、ShellEx / CLSID 项的行为并不完全一致，遇到边界情况时日志通常比界面提示更有价值

## 反馈建议

如果你要提 Issue，建议至少附上：

- Windows 版本
- 出问题的菜单项名称 / 注册表路径
- 前端日志
- backend 日志
- trayhost 日志（如果问题涉及托盘）
- 复现步骤
- 截图或录屏

## License

本项目遵循 GPL V3.0 协议开源，See [LICENSE](./LICENSE).

## 参考与致谢

本项目代码大量参考自以下项目：

- https://github.com/BluePointLilac/ContextMenuManager
- https://github.com/branhill/windows-11-context-menu-manager
