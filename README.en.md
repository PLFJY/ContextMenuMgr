<p align="center">
    <picture>
      <source media="(prefers-color-scheme: light)" srcset="./doc/images/readme/pt-hero.light.png" />
      <img src="./ContextMenuMgr.Frontend/Assets/AppIcon.png" style="height: 100px; width: 100px" />
  </picture>
</p>
<h1 align="center">
  <span>Context Menu Manager</span>
</h1>
<p align="center">
  <span align="center">Context Menu Manager is a powerful utility that help you manage you context menu on Windows and avoid third party to add rubish to your context menu.</span>
</p>

[中文版文档](./README.md)

> [!WARNING]
> A significant portion of this project was generated with AI assistance and then continuously revised, tested, and reshaped by hand. It may still contain gaps, edge-case regressions, or behavior that does not fully match expectations.
> If you hit bugs, compatibility issues, missing documentation, or surprising behavior, please open an Issue. Reproduction steps, logs, screenshots, and Windows version details are especially helpful.

## Overview

`Context Menu Manager` is a Windows context menu management tool focused on an approval-first workflow rather than simple toggling.

The project is built around:

- `.NET 10`
- `WPF`
- `WPF-UI`
- `Named Pipe IPC`
- `Windows Service`
- a native Win32 tray host

## Core Idea

### Intercept first, review second

This project is intentionally designed to do more than just enable or disable context menu items:

- when a new menu item is detected, it is intercepted first
- it is forced into a disabled state
- it is then placed into a review queue
- the user explicitly decides what to do next

The available review actions are:

- `Allow`
- `Keep disabled`
- `Remove`

That “intercept -> review -> manually allow” pipeline is the main differentiator of this project.

### Backend-driven process model

The project follows a backend-driven model:

- `ContextMenuManager.Service.exe`
  - the real controller and runtime core
- `ContextMenuManager.TrayHost.exe`
  - a separate per-user tray surface
- `ContextMenuManager.exe`
  - an on-demand UI process only

The tray exists as a separate per-user surface, while the frontend remains a UI process.

## Features

### Context menu management

- Browse context menu items by category
  - File
  - All Objects
  - Folder
  - Directory
  - Directory Background
  - Desktop Background
  - Drive
  - Library
  - This PC
  - Recycle Bin
- Enable / disable items
- Delete items
- Undo delete
- Permanently remove delete backups
- Search and filter entries
- Parse names, icons, command text, and CLSID metadata for many items

### Review queue

- New items are placed into a review queue
- Review actions:
  - `Allow`
  - `Keep disabled`
  - `Remove`
- Approval items can aggregate multiple category sources for the same logical item
- New approval events are forwarded to the tray host, which shows a system notification
- Clicking the notification launches the frontend and opens the approvals page

### External-change tracking

External-change tracking focuses on:

- externally added items
- external enabled/disabled changes that happened while the guard was offline

### File-type and rules pages

- File Types page
  - Shortcuts
  - UWP shortcuts
  - Executables
  - Custom extensions
  - Perceived types
  - Directory types
  - Unknown types
- Other Rules page
  - Enhance Menu
  - Detailed Edit
  - Custom registry paths

### Settings page

- Language
  - Follow system
  - Simplified Chinese
  - English (United States)
- Theme
  - Follow system
  - Light
  - Dark
- Log level
- Start with Windows
- Install / repair service
- Uninstall service
- Restart Explorer
- Open logs / state / config folders
- Registry protection enhancement switch

## Architecture

### 1. Backend Service

Project: `ContextMenuMgr.Backend`  
Executable: `ContextMenuManager.Service.exe`

Responsibilities:

- enumerate and monitor context-menu-related registry entries
- store and merge persisted state
- apply enable / disable / delete / restore / approval decisions
- expose backend IPC
- ensure the tray host exists when appropriate

### 2. Tray Host

Project: `ContextMenuMgr.TrayHost`  
Executable: `ContextMenuManager.TrayHost.exe`

The tray host is intentionally thin:

- tray icon
- tray menu
- system notifications
- launching the frontend main window
- opening the approvals UI
- requesting backend shutdown

The tray host uses a **native Win32 tray implementation**.

### 3. Frontend

Project: `ContextMenuMgr.Frontend`  
Executable: `ContextMenuManager.exe`

Responsibilities:

- main UI
- approvals UI
- rules pages
- settings UI
- IPC with backend
- control-pipe cooperation for frontend single-instance behavior

The frontend is UI-only:

- closing the window exits the frontend process
- no tray ownership
- no background-resident frontend process

### 4. Shared Contracts

Project: `ContextMenuMgr.Contracts`

Responsibilities:

- IPC request / response models
- notification kinds
- tray host and frontend control commands
- shared protocol constants

## IPC and Process Coordination

### Backend pipe

JSON-over-Named-Pipe is used for:

- snapshot retrieval
- item state updates
- approval decisions
- registry-protection settings
- explicit tray-host startup requests
- backend shutdown requests

### Frontend control pipe

Used for:

- single-instance activation
- showing the main window
- opening the approvals page
- focusing a specific approval item
- shutting down the frontend cleanly

### Tray host control pipe

Used for:

- tray host exit
- tray localization reload

## Main Registry Scopes

The code primarily targets:

- `HKEY_CLASSES_ROOT\*\shell`
- `HKEY_CLASSES_ROOT\*\shellex\ContextMenuHandlers`
- `HKEY_CLASSES_ROOT\Directory\shell`
- `HKEY_CLASSES_ROOT\Directory\shellex\ContextMenuHandlers`
- `HKEY_CLASSES_ROOT\Directory\Background\shell`
- `HKEY_CLASSES_ROOT\Directory\Background\shellex\ContextMenuHandlers`
- `CLSID`
- `PackagedCom`
- file-type, extension, perceived-type, and directory-type branches
- user-level `HKCU/HKEY_USERS\<SID>\Software\Classes` ranges

## Repository Layout

```text
ContextMenuMgr/
├─ .github/                        # GitHub Actions
├─ artifacts/                      # Local intermediate build output
├─ build/                          # Publish output and installers
├─ ContextMenuMgr.Backend/         # Windows Service / backend core
├─ ContextMenuMgr.Contracts/       # Shared contracts
├─ ContextMenuMgr.Frontend/        # WPF frontend
├─ ContextMenuMgr.TrayHost/        # Per-user tray host
├─ Installer/                      # Inno Setup scripts and related files
├─ build.ps1                       # Main build script
├─ build.bat                       # Batch wrapper for build.ps1
├─ ContextMenuMgr.slnx             # Solution
├─ README.md                       # Chinese primary README
└─ README.en.md                    # English README
```

## Executables

Public-facing executable names are:

- Frontend: `ContextMenuManager.exe`
- Backend service: `ContextMenuManager.Service.exe`
- Tray host: `ContextMenuManager.TrayHost.exe`

## Requirements

- Windows 10 / 11
- .NET SDK 10
- PowerShell 5.1 or later
- Inno Setup 6
  - the repo prefers the bundled compiler under:
    - `Installer\Inno Setup 6\ISCC.exe`

## Local Development Build

```powershell
dotnet restore .\ContextMenuMgr.slnx --configfile .\NuGet.Config
dotnet build .\ContextMenuMgr.slnx --no-restore
```

## Publish and Installer Build

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Release
```

### What `build.ps1` does

The current script:

1. restores the full solution
2. publishes:
   - Frontend
   - Backend
   - TrayHost
3. generates installers for multiple architecture / distribution combinations

The combinations are:

- `win-x64`
- `win-x86`
- `win-arm64`
- `self-contained`
- `framework-dependent`

Outputs:

- publish output: `build\publish\`
- installers: `build\dist\`
- artifact manifest: `build\dist\artifacts.txt`

## GitHub Actions

The repository includes:

- `.github/workflows/manual-release.yml`

Workflow behavior:

- supports manual dispatch
- supports tag-based release flow
- calls `build.ps1`
- uploads build artifacts
- creates a draft release
- resolves release version/title from project version metadata

## Runtime Data and Logs

### Frontend

- settings:
  - `%LocalAppData%\ContextMenuMgr\frontend-settings.json`
- logs:
  - `%LocalAppData%\ContextMenuMgr\Logs\frontend-debug.log`
  - `%LocalAppData%\ContextMenuMgr\Logs\frontend-crash.log`

### Tray Host

- log:
  - `%LocalAppData%\ContextMenuMgr\Logs\trayhost.log`

### Backend

- log:
  - `%ProgramData%\ContextMenuMgr\Logs\backend.log`
- state store:
  - `%ProgramData%\ContextMenuMgr\Data\context-menu-state.json`

Note:

- the public product name is `Context Menu Manager`
- the local data folder keeps the historical `ContextMenuMgr` name for compatibility

## Runtime and Recovery Flow

### Normal flow

- the frontend starts and tries to connect to backend
- once backend is reachable, the frontend can explicitly ask backend to ensure the tray host exists
- backend then launches the tray host for the active user session

### Approval notification flow

- backend detects a new item
- backend broadcasts a notification event
- tray host receives it and shows a system notification
- the user clicks the notification
- tray host launches the frontend and opens the approvals page

### Recovery expectations

- if the backend/service exits unexpectedly:
  - tray may disappear
  - the frontend should remain usable
- the user can go to Settings and run:
  - install / repair service
  to restore backend functionality

## Notes

- Some protected registry roots cannot have their ACL changed in ordinary ways; this is a Windows limitation, not necessarily an app bug
- Security software may block delete, restore, or registry-write operations
- Icon and display-name resolution is best-effort and cannot guarantee perfect coverage for every third-party extension
- User-level items, packaged system items, and shell extensions do not all behave identically; logs are often more informative than the UI when diagnosing edge cases

## Reporting Issues

When opening an issue, it is very helpful to include:

- Windows version
- affected menu item name or registry path
- frontend log
- backend log
- trayhost log if the issue involves tray behavior
- reproduction steps
- screenshot or screen recording

## License

This project is licensed under GPL v3.0. See [LICENSE](./LICENSE).
