# Context Menu Manager

[中文说明](./README.md)

> [!WARNING]
> A significant portion of this project was generated with AI assistance and then iteratively revised and integrated by hand. It may still contain gaps, edge-case issues, or behavior that does not fully match expectations.
> If you find bugs, unexpected behavior, missing documentation, or compatibility issues, please open an Issue and include reproduction steps, logs, and screenshots whenever possible.

## Overview

`Context Menu Manager` is a Windows context menu management tool

- `.NET 10`
- `WPF`
- `WPF-UI`
- `Named Pipe IPC`
- `Windows Service`

The solution contains two executables:

- `ContextMenuManager.exe`
  Standard-privilege desktop frontend.
- `ContextMenuManager.Service.exe`
  Elevated backend service responsible for registry monitoring and mutation.

## Core Differentiator

The most important feature of this project is that it is not just a regular context-menu toggle tool:

- When a new context menu entry is detected, the backend service intercepts it immediately and disables it first
- The intercepted item is then placed into a review queue for explicit user action
- The user can manually choose one of the following:
  - Allow: enable the menu item
  - Keep disabled: keep the item present but disabled
  - Remove: delete the item

In other words, the core workflow is:

- intercept first
- review second
- manually allow only when approved

That approval-first flow is the key differentiator of this project.

## Key Features

- Browse context menu entries by category
- Enable / disable menu entries
- Delete, undo delete, and permanently delete items
- Review newly detected menu items
- Detect external changes
- Parse item names and icons
- Manage file-type-related menu entries
- Manage enhance-menu and detailed-edit rules
- Switch language and theme
- Install, repair, or uninstall the backend service
- Restart Explorer
- Open logs, state store, and config folders

## Architecture

### Frontend

The frontend is a WPF desktop application using `WPF-UI` for a Fluent-style interface. It is responsible for:

- Category navigation and item presentation
- Review queue counters
- Enable / disable / delete / undo operations
- Language, theme, service, and logging settings
- Named Pipe communication with the backend

### Backend Service

The backend is an elevated Windows Service responsible for:

- Enumerating and monitoring context-menu-related registry entries
- Applying enabled / disabled states
- Disabling newly detected items before review
- Maintaining the state store and delete backups
- Serving IPC requests from the frontend

### IPC

Frontend and backend communicate through JSON-over-Named-Pipe requests and responses.

## Main Registry Scopes

The project currently focuses on:

- `HKEY_CLASSES_ROOT\*\shell`
- `HKEY_CLASSES_ROOT\*\shellex\ContextMenuHandlers`
- `HKEY_CLASSES_ROOT\Directory\shell`
- `HKEY_CLASSES_ROOT\Directory\shellex\ContextMenuHandlers`
- `HKEY_CLASSES_ROOT\Directory\Background\shell`
- `HKEY_CLASSES_ROOT\Directory\Background\shellex\ContextMenuHandlers`
- Various `CLSID`, `PackagedCom`, file-type, and extension-related branches

## Repository Structure

```text
ContextMenuMgr/
├─ ContextMenuMgr.Frontend/         # WPF frontend
├─ ContextMenuMgr.Backend/          # Windows Service backend
├─ ContextMenuMgr.Contracts/        # Shared contracts
├─ Installer/                       # Inno Setup scripts
├─ build.ps1                        # Build + publish + package script
├─ build.bat                        # Batch wrapper
├─ ContextMenuMgr.slnx              # Solution
├─ README.md                        # Chinese primary README
└─ README.en.md                     # English README
```

## Requirements

- Windows 11 x64
- .NET SDK 10
- PowerShell 5.1 or later
- Inno Setup 6
  - Default compiler path: `Installer\Inno Setup 6\ISCC.exe`

## Build

```powershell
dotnet restore .\ContextMenuMgr.slnx --configfile .\NuGet.Config
dotnet build .\ContextMenuMgr.slnx --no-restore
```

## Publish and Package

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Release
```

To override the installer `AppId`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Release -AppId "YOUR-GUID-HERE"
```

The build script will:

1. Run `dotnet restore`
2. Publish the frontend project to `build\ContextMenuManager`
3. Generate an Inno Setup installer

Default outputs:

- Publish folder: `build\ContextMenuManager\`
- Installer: `build\ContextMenuManager_Setup.exe`

## Executable Names

Public-facing executable names are now:

- Frontend: `ContextMenuManager.exe`
- Service: `ContextMenuManager.Service.exe`

## Runtime Data Paths

- Frontend logs:
  - `%LocalAppData%\ContextMenuMgr\Logs\frontend-debug.log`
  - `%LocalAppData%\ContextMenuMgr\Logs\frontend-crash.log`
- Backend log:
  - `%ProgramData%\ContextMenuMgr\Logs\backend.log`
- Frontend settings:
  - `%LocalAppData%\ContextMenuMgr\frontend-settings.json`
- Backend state store:
  - `%ProgramData%\ContextMenuMgr\Data\context-menu-state.json`

Note:

- The public product name is already unified as `Context Menu Manager`
- Local data folders still keep the historical `ContextMenuMgr` name for compatibility

## Service Notes

- The frontend tries to connect to the service on startup
- If the service is missing or unavailable, install / repair / uninstall it from the Settings page
- The backend is designed to clean itself up after the frontend exits under the current implementation

## Notes

- Some protected registry roots cannot have their ACL changed due to Windows restrictions
- Security software may intercept delete, restore, or registry write operations
- Icon and display-name resolution is best-effort and depends on Windows registry and resource metadata

## License

See [LICENSE](./LICENSE).
