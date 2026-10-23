<div align="center">
  <p align="center">
      <img src="./ContextMenuMgr.Frontend/Assets/AppIcon.png" style="height: 100px; width: 100px" />
  </p>
  <h1 align="center">
    <span>Context Menu Manager Plus</span>
  </h1>
  <p align="center">
    <span align="center">Context Menu Manager Plus is a powerful utility that helps you manage your Windows context menu and prevent third-party software from filling it with unwanted entries.</span>
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

[中文版 README](./README.md)

> [!WARNING]
> A significant portion of this project was generated with AI assistance and then continuously revised, tested, and reshaped by hand. It may still contain gaps, edge-case regressions, or behavior that does not fully match expectations.
> If you hit bugs, compatibility issues, missing documentation, or surprising behavior, please open an Issue. Reproduction steps, logs, screenshots, and Windows version details are especially helpful.

## 🚀 Overview

`Context Menu Manager Plus` is a Windows context menu management tool.

It is not just a simple enable/disable switcher. It is designed around the following goals:

- intercept new items first, then let the user review them
- manage classic Windows context menu items
- manage Windows 11 modern context menu items
- detect new context menu entries added by third-party software
- protect context-menu-related registry locations from unwanted modifications
- provide global search, page-level filtering, and runtime menu analysis
- keep the app usable through a coordinated frontend / backend service / tray host model

In short, the goal is:

> Turn the Windows context menu from “software can add whatever it wants” into “the user decides what is allowed to stay”.

The project is built with:

- `.NET 10`
- `WPF`
- `WPF-UI`
- `Named Pipe IPC`
- `Windows Service`
- native Win32 tray host
- isolated ProbeHost helper process

---

## 📦 Installation

To get started quickly, choose one of the installation methods below:

<details open>
<summary><strong>Download the .exe file from GitHub</strong></summary>

Go to the [GitHub releases](https://github.com/PLFJY/ContextMenuMgr/releases), scroll down and select **Assets** to reveal the installation files, and choose the one that matches your architecture and install scope. For most devices, that would be _x64-self-contained_.

</details>

<details>
<summary><strong>WinGet</strong></summary>

Download Context Menu Manager Plus from [WinGet](https://github.com/microsoft/winget-cli#installing-the-client). To install Context Menu Manager Plus through WinGet, run the following command from the command line / PowerShell:

- Latest release (default)

```powershell
winget install PLFJY.ContextMenuMgrPlus
```

- Beta version

```powershell
winget install PLFJY.ContextMenuMgrPlus.Beta
```
</details>

<details>
<summary><strong>Scoop</strong></summary>

Download Context Menu Manager Plus from [Scoop](https://scoop.sh/). To install Context Menu Manager Plus through Scoop, run the following command from the command line / PowerShell:

You need to add our Scoop bucket first:

```
scoop bucket add PLFJY https://github.com/PLFJY/scoop-bucket.git
```

- Latest release (default)

```powershell
scoop install PLFJY/ContextMenuMgrPlus
```

- Beta version

```powershell
scoop install PLFJY/ContextMenuMgrPlus-Beta
```
</details>

## 📚 Table Of Contents

- [Overview](#-overview)
- [Installation](#-installation)
- [Core Features](#-core-features)
- [Features](#-features)
- [Documentation - Chinese Only](#-documentation---chinese-only)
- [Notes](#️-notes)
- [Reporting Issues](#-reporting-issues)
- [Contribution Guide](#-contribution-guide)
- [License](#-license)
- [References And Acknowledgements](#-references-and-acknowledgements)

## ✨ Core Features

- Intercept First, Review Second

  This is the most important design goal of the project. With an established runtime baseline, newly detected regular entries that support safe disabling are disabled and sent for review. First run/reset adopts current state; offline additions are marked as changes. New OpenWith registrations are review-only by default, with persistent protection requiring explicit authorization.

  Available actions:

  - `Allow`: allow and enable the item
  - `Keep disabled`: keep the item but leave it disabled
  - `Remove`: remove the item from the registry or from the review queue

  This approval-first workflow is the main difference between this project and ordinary context menu managers.

- **Classic and Windows 11 menu support**: handles classic `shell` / `shellex` entries separately from Windows 11 Packaged COM / AppX entries.
- **Frontend, backend service, and tray host**: the frontend handles interaction, the backend service owns scanning and registry operations, and TrayHost handles notifications and tray access.
- **Global search and page filtering**: search by title, command, registry path, file path, CLSID, Windows 11 package metadata, and related fields.
- **Shell Extension Deep Analysis**: probes third-party Shell Extensions through an isolated ProbeHost; probe failures usually do not affect normal menu management.
- **Registry protection enhancements**: can reduce unwanted context-menu registry writes; temporarily disable it before installing drivers or software that adds menu entries.
- **Special menu support**: covers New, Send To, Win + X, and other entries that are not ordinary `shell` / `shellex` items.

## 🧩 Features

- **Classic context menu management**: browse file, folder, directory background, desktop background, drive, library, This PC, Recycle Bin, and related scenes; enable, disable, delete, undo delete, edit some display names, inspect commands, and open registry / CLSID / file locations.
- **Windows 11 modern menu management**: enumerate modern entries, inspect package / publisher / context type / COM Server information, and enable or disable user-level entries.
- **Approval queue and notifications**: new items appear on the approvals page, grouped by logical item when needed; TrayHost can show a notification and open the frontend.
- **External change tracking**: distinguishes runtime and offline additions, modifications, and re-enabling; confirmed missing entries leave the monitoring baseline, while deletion backups remain recovery-only.
- **File type and rule pages**: covers shortcuts, UWP shortcuts, executables, custom extensions, perceived types, directory types, Enhance Menu, Detailed Edit, and custom registry paths.
- **Settings page**: language, theme, log level, start with Windows, service install / repair / uninstall, Explorer restart, data folder shortcuts, and registry protection switches.

## 📖 Documentation - Chinese Only

Use the [Maintainer Documentation Index](./docs/README.md) for architecture, menu mechanisms, frontend, runtime, build/release, and diagnostics. Detailed technical documents are in Simplified Chinese; contributors start with [AGENTS.md](./AGENTS.md).

## ⚠️ Notes

* This project modifies the registry. Use it carefully.
* Delete, disable, ACL protection, and restore operations may be affected by system permissions, security software, or Windows protection mechanisms.
* Some protected registry roots cannot have their ACL changed in ordinary ways. This is a Windows limitation.
* Security software may block delete, restore, registry-write, or Shell Extension probing operations.
* Icon, display-name, command-text, and CLSID metadata resolution are best-effort and cannot cover every third-party item perfectly.
* Shell Extension Deep Analysis is best-effort. Failure is often expected.
* User-level items, machine-level items, PackagedCom entries, ShellEx entries, and CLSID-based entries do not all behave the same way.
* Enabling registry protection may prevent some installers or driver installers from writing context-menu-related entries.
* Logs are usually more useful than UI messages when diagnosing edge cases.

## 💬 Reporting Issues

When opening an issue, it is very helpful to include:

- Windows version
- affected menu item name or registry path
- frontend log
- backend log
- trayhost log if the issue involves tray behavior
- reproduction steps
- screenshot or screen recording

## 🤝 Contribution Guide

This project includes built-in development documentation (located in the `docs/` directory), along with `AGENTS.md` and `CLAUDE.md`. You can use majority Agent tools to contribute to this project.

## 📄 License

This project is licensed under GPL v3.0. See [LICENSE](./LICENSE).

## 🙏 References And Acknowledgements

This project draws heavily from the following repositories:

- https://github.com/BluePointLilac/ContextMenuManager
- https://github.com/branhill/windows-11-context-menu-manager
- https://github.com/iNKORE-NET/UI.WPF.Modern


## ⭐ Stargazers over time
[![Stargazers over time](https://starchart.cc/PLFJY/ContextMenuMgr.svg?variant=adaptive)](https://starchart.cc/PLFJY/ContextMenuMgr)

​
