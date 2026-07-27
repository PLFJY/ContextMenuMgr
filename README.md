<div align="center">
  <p align="center">
      <img src="./ContextMenuMgr.Frontend/Assets/AppIcon.png" style="height: 100px; width: 100px" />
  </p>
  <h1 align="center">
    <span>Context Menu Manager Plus</span>
  </h1>
  <p align="center">
    <span align="center">Context Menu Manager Plus 是一个强大的实用程序，它可帮助您管理 Windows 上的右键菜单，并避免第三方向你的右键菜单里塞屎。</span>
  </p>

  ![GitHub Repo stars](https://img.shields.io/github/stars/PLFJY/ContextMenuMgr?style=?style=flat-square)
  ![GitHub License](https://img.shields.io/github/license/PLFJY/ContextMenuMgr)
  ![GitHub Release](https://img.shields.io/github/v/release/PLFJY/ContextMenuMgr)
  ![GitHub Issues or Pull Requests](https://img.shields.io/github/issues/PLFJY/ContextMenuMgr)
  ![GitHub Issues or Pull Requests](https://img.shields.io/github/issues-pr/PLFJY/ContextMenuMgr)
  ![GitHub forks](https://img.shields.io/github/forks/PLFJY/ContextMenuMgr?style=flat-square)

  ![GitHub Release](https://img.shields.io/github/v/release/PLFJY/ContextMenuMgr)
  ![GitHub Release](https://img.shields.io/github/v/release/PLFJY/ContextMenuMgr?include_prereleases&label=beta&color=orange)

  ![WinGet Package Version](https://img.shields.io/winget/v/PLFJY.ContextMenuMgrPlus)
  ![Scoop Version](https://img.shields.io/scoop/v/contextmenumgrplus?bucket=https%3A%2F%2Fgithub.com%2FPLFJY%2Fscoop-bucket)
  ![WinGet Package Version](https://img.shields.io/winget/v/PLFJY.ContextMenuMgrPlus.Beta?label=winget%20Beta&color=orange)
  ![Scoop Version](https://img.shields.io/scoop/v/contextmenumgrplus-beta?bucket=https%3A%2F%2Fgithub.com%2FPLFJY%2Fscoop-bucket&label=scoop%20beta&color=orange)
</div>


[English Version](./README.en.md)

> [!WARNING]
> 本项目的相当一部分代码由 AI 辅助生成，并经过持续的人工作业、联调和重构，但它仍然可能存在遗漏、边界情况处理不足或行为与预期不完全一致的问题。
> 如果你在使用过程中发现 Bug、兼容性问题、异常行为或文档缺失，欢迎积极提交 Issue。最好附上复现步骤、日志、截图和系统版本信息，这会非常有帮助。

## 🚀 项目简介

`Context Menu Manager Plus` 是一个面向 Windows 的右键菜单管理工具。

它不是一个单纯的“右键菜单开关器”，而是围绕以下目标设计：

- 先拦截，再交给用户审核
- 管理传统右键菜单项
- 管理 Windows 11 新右键菜单项
- 检测第三方软件新增的右键菜单项
- 保护右键菜单相关注册表，减少第三方软件随意添加菜单项
- 提供全局搜索、页面筛选、菜单项深入分析等辅助能力
- 通过后台服务、前端界面、托盘宿主协作，尽量保持长期可用

简单来说，它的目标是：

> 帮你把 Windows 右键菜单从“软件想塞什么就塞什么”，变成“用户决定什么能留下”。

项目基于：

- `.NET 10`
- `WPF`
- `WPF-UI`
- `Named Pipe IPC`
- `Windows Service`
- 原生 Win32 托盘宿主
- 独立 ProbeHost 辅助分析进程

---

## 📦 安装

如需快速开始，请选择以下任一安装方式：

<details open>
<summary><strong>从 GitHub 下载 .exe 文件</strong></summary>

前往 [GitHub Releases](https://github.com/PLFJY/ContextMenuMgr/releases)，向下滚动并展开 **Assets**，然后选择与你的系统架构和安装范围匹配的安装文件。对大多数设备来说，通常选择 _x64-self-contained_。

</details>

<details>
<summary><strong>WinGet</strong></summary>

你可以通过 [WinGet](https://github.com/microsoft/winget-cli#installing-the-client) 安装 Context Menu Manager Plus。在命令行或 PowerShell 中运行以下命令：

- 最新正式版（默认）

```powershell
winget install PLFJY.ContextMenuMgrPlus
```

- Beta 版本

```powershell
winget install PLFJY.ContextMenuMgrPlus.Beta
```
</details>

<details>
<summary><strong>Scoop</strong></summary>

你可以通过 [Scoop](https://scoop.sh/) 安装 Context Menu Manager Plus。在命令行或 PowerShell 中运行以下命令：

需要先添加本项目的 Scoop bucket：

```
scoop bucket add PLFJY https://github.com/PLFJY/scoop-bucket.git
```

- 最新正式版（默认）

```powershell
scoop install PLFJY/ContextMenuMgrPlus
```

- Beta 版本

```powershell
scoop install PLFJY/ContextMenuMgrPlus-Beta
```
</details>

## 📚 目录

- [项目简介](#-项目简介)
- [安装](#-安装)
- [核心特色](#-核心特色)
- [功能](#-功能)
- [文档](#-文档)
- [注意事项](#️-注意事项)
- [反馈建议](#-反馈建议)
- [贡献指南](#-贡献指南)
- [License](#-license)
- [参考与致谢](#-参考与致谢)

## ✨ 核心特色

- 先拦截，再审核

  这是本项目最重要的设计目标。新的右键菜单项被检测到后，项目不会默认放任它直接生效，而是尽量先禁用或拦截该项，再放入“待审核”页面交给用户处理。

  用户可以选择：

  - `允许`：放行并启用该菜单项
  - `保持禁用`：保留该项，但继续禁用
  - `移除`：从注册表或审核队列中移除该项

  这也是它和普通右键菜单管理器最大的区别。

- **传统菜单与 Windows 11 新菜单并行管理**：分别处理 classic `shell` / `shellex` 菜单和 Windows 11 Packaged COM / AppX 菜单。
- **后端服务 + 前端 + 托盘宿主协作**：前端负责交互，后端服务负责扫描和注册表操作，TrayHost 负责通知和托盘入口。
- **全局搜索与页面筛选**：支持按标题、命令、注册表路径、文件路径、CLSID、Win11 包信息等字段查找并跳转。
- **Shell Extension 深入分析**：通过独立 ProbeHost 隔离探测第三方 Shell Extension，失败通常不影响普通菜单管理。
- **注册表保护增强**：可限制第三方随意写入右键菜单相关注册表项；安装驱动或会写菜单的软件前建议临时关闭。
- **特殊菜单支持**：覆盖新建菜单、发送到、Win + X 等与普通 `shell` / `shellex` 不同的入口。

## 🧩 功能

- **传统右键菜单管理**：浏览文件、文件夹、目录背景、桌面背景、磁盘、库、此电脑、回收站等场景；支持启用、禁用、删除、撤销删除、编辑部分显示名称、查看命令和打开相关位置。
- **Windows 11 新菜单管理**：枚举 Win11 新菜单项，查看包名、发布者、上下文类型和 COM Server 路径，并支持用户级启用 / 禁用。
- **审核队列与通知**：新增项进入待审核页，同一逻辑项会聚合展示；TrayHost 可弹出通知并拉起前端处理。
- **外部变化检测**：尽量发现第三方新增项、已知项被恢复 / 删除、注册表状态和本地状态不一致等情况。
- **文件类型与规则页**：覆盖快捷方式、UWP 快捷方式、可执行文件、自定义扩展名、感知类型、目录类型、增强菜单、详细编辑和自定义注册表路径等规则。
- **设置页**：提供语言、主题、日志等级、随 Windows 启动、服务安装 / 修复 / 卸载、重启资源管理器、日志 / 状态 / 配置目录入口和注册表保护开关。

## 📖 文档

架构设计、开发指南、构建发布和排错说明请查看 [开发文档](./docs/README.md)。

## ⚠️ 注意事项

- 本项目涉及注册表修改，请谨慎使用
- 删除、禁用、ACL 保护等操作可能受系统权限、安全软件或 Windows 保护机制影响
- 某些系统保护的注册表根键无法被普通方式修改 ACL，这是 Windows 本身限制
- 某些安全软件可能会拦截删除、恢复、注册表写入或 Shell Extension 探测
- 图标、显示名、命令文本、CLSID 元数据解析均为 best-effort，不保证覆盖所有第三方菜单项
- Shell Extension 深入分析是 best-effort，失败通常是正常现象
- 用户级右键菜单项、系统级右键菜单项、PackagedCom、ShellEx / CLSID 项的行为并不完全一致
- 开启注册表保护后，部分软件安装器或驱动安装器可能无法写入右键菜单相关项
- 遇到边界情况时，日志通常比界面提示更有价值

## 💬 反馈建议

如果你要提 Issue，建议至少附上：

- Windows 版本
- 出问题的菜单项名称 / 注册表路径
- 前端日志
- backend 日志
- trayhost 日志（如果问题涉及托盘）
- 复现步骤
- 截图或录屏

## 🤝 贡献指南

本项目中内置有开发文档（位于 `docs/` 目录下），并附带有 `AGENTS.md`、`CLAUDE.md`，可以直接利用主流的 Agent 工具为本项目做出贡献

## 📄 License

本项目遵循 GPL V3.0 协议开源，See [LICENSE](./LICENSE).

## 🙏 参考与致谢

本项目代码大量参考自以下项目：

- https://github.com/BluePointLilac/ContextMenuManager
- https://github.com/branhill/windows-11-context-menu-manager
- https://github.com/iNKORE-NET/UI.WPF.Modern

## ⭐ Stargazers over time
[![Stargazers over time](https://starchart.cc/PLFJY/ContextMenuMgr.svg?variant=adaptive)](https://starchart.cc/PLFJY/ContextMenuMgr)

## 其它 Git 镜像仓库

[![AtomGitStars](https://gitcode.com/PLFJY/ContextMenuMgr/star/new_badge.svg)](https://gitcode.com/PLFJY/ContextMenuMgr)

https://gitcode.com/PLFJY/ContextMenuMgr
