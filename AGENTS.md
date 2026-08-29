# AGENTS.md

This repository is **Context Menu Manager Plus / ContextMenuMgr**.

It is a Windows system utility involving a WPF frontend, Windows Service backend, TrayHost, ProbeHost, Windows Registry operations, user SIDs, session IDs, Named Pipe IPC, multi-architecture build artifacts, and multiple privilege/user-context flows.

This file is the main entry point for AI coding agents and human maintainers.

Before analyzing bugs, adding features, refactoring, or changing behavior, read this file first, then read the relevant documents under `docs/`.

Most detailed documents under `docs/` are written in Simplified Chinese. Do not skip them because of language. Read and translate them internally if needed.

---

## 1. Required Reading

Before starting any task, read at least:

1. `docs/ai-maintainer-playbook.md`
2. `docs/process-and-privilege-flows.md`
3. `docs/developer-guide.md`

For module-specific work, also read the matching topic document:

| Task type | Required document |
| --- | --- |
| Classic context menus / registry menu model | `docs/registry-model.md` |
| ShellNew / SendTo / WinX / SpecialMenu | `docs/special-menus.md` |
| Windows 11 modern context menu | `docs/windows11-context-menu.md` |
| Deep Analysis / ProbeHost / Shell Extension probing | `docs/deep-analysis-probehost.md` |
| Frontend UI / WPF-UI / theme / NavigationView / AutoSuggestBox | `docs/frontend-wpf-ui.md` |
| Build, release, installer, multi-architecture ProbeHost | `docs/build-and-release.md` |
| Unclear bugs, user reports, runtime failures | `docs/troubleshooting.md` |
| New agent handoff, fault attribution, pre-change checklist | `docs/ai-maintainer-playbook.md` |

Do not rely only on `README.md`. The README is user-facing. Development decisions must be based on `docs/` and the current code.

---

## 2. Identify the Correct Flow Before Editing

This project is not a single “run as administrator” model. Every bug or feature must first be mapped to the correct process, privilege, and user-context flow.

| Flow | Shape | Main purpose |
| --- | --- | --- |
| Flow A | Frontend -> Backend Pipe -> Backend Service | Normal runtime menu operations, scans, approvals, Win11 blocking, SpecialMenu operations, runtime AutoStart read/write |
| Flow B | Frontend -> UAC runas -> BackendServiceBootstrapper | Install/repair/uninstall/stop service, set service startup mode |
| Flow C | Backend Service -> WTS User Token -> CreateProcessAsUser | Launch TrayHost or Frontend inside the interactive user session |
| Flow D | Frontend -> ProbeHost -> Shell Extension COM | Deep Analysis only; isolated third-party Shell Extension probing |

Before modifying code, answer:

- Which process is involved?
- Which flow should handle this task?
- Does it need the frontend user SID?
- Does it need a session ID?
- Does it touch `HKCU` or `HKEY_USERS\<SID>`?
- Does it require elevated registry writes?
- Does it load or probe a third-party Shell Extension?
- Is this only a frontend UI state problem?

If you cannot answer these questions, stop and read the relevant docs or inspect the code first.

---

## 3. Hard Rules

### User context

- Do not use the service process `HKCU` as the frontend user’s `HKCU`.
- User-scoped registry reads/writes must check whether they need `HKEY_USERS\<SID>`.
- Privilege and user context are different problems. LocalSystem is highly privileged, but it is not the interactive frontend user.
- Win11 user blocked lists, SpecialMenu, AutoStart policy, Restart Explorer, and user-profile paths may require frontend user context.

### Privilege flows

- Normal runtime operations should go through the Backend Pipe. Do not route them through the UAC bootstrapper.
- The UAC bootstrapper is only for service lifecycle and startup-mode operations.
- A Windows Service must not directly show UI. Launch TrayHost or Frontend through the interactive user session.
- Restart Explorer must target the frontend user session. Do not blindly kill all `explorer.exe` processes.

### Tray icon, notifications, and shell integration

- For tray icon, notification, and shell integration work, use Win32/PInvoke only.
- Do not introduce Windows App SDK, WinUI, AppNotificationManager, packaged activation, or Microsoft.WindowsAppSDK for tray notifications.
- Do not change project TFMs to versioned Windows SDK TFMs such as `net10.0-windows10.0.xxxxx.0` for tray or notification work.
- TrayHost is the user-session notification agent. The backend service must not show user notifications directly.
- Hiding the tray icon must not remove or stop the TrayHost process; TrayHost must keep receiving backend notifications and handling notification activation.

### Frontend UI and WPF-UI

- Frontend UI bugs are not automatically backend, registry, or service bugs.
- For WPF-UI, theme, NavigationView, AutoSuggestBox, Popup, XAML style, or Page/UserControl issues, read `docs/frontend-wpf-ui.md` before changing code.
- Do not fix UI state, template, or binding bugs by changing Backend Service, registry scanning, or privilege flows unless there is evidence the backend is involved.

### SpecialMenu

- ShellNew, SendTo, and WinX are not ordinary `shell` / `shellex` entries.
- For SpecialMenu bugs, inspect `SpecialMenuService` and the related ViewModel first. Do not start by changing `ContextMenuRegistryCatalog`.
- Registry Write Protection and ShellNew ACL Lock are different features and must not be mixed.

### Windows 11 modern context menu

- Windows 11 modern context menu entries are not classic context menu entries.
- Win11 snapshot and block/unblock operations must preserve the correct user context.
- If a Win11 item appears enabled again after refresh, first verify whether the snapshot used the correct `HKEY_USERS\<SID>` blocked list.

### State database and external-change rules

- An absent or reset state database must adopt the current regular-menu, Win11, and WPS/Office state without pending approvals, external-change badges, or saved-state mismatch warnings.
- Regular menus and WPS/Office use separate explicit baseline markers. Never infer one source's baseline from unrelated state records, and never commit a first baseline from a snapshot without an interactive user context.
- During runtime, an unknown item is disabled and sent to approval; a known enabled item that becomes disabled is `Modified`; a known disabled item observed changing to enabled between settled monitor snapshots is silently disabled again.
- At startup with an existing baseline, an offline unknown item is `Added` without quarantine, and either direction of a known item's switch-state change is `Modified`; startup/offline disabled-to-enabled drift must not be silently reconciled.
- A confirmed missing registry item must be removed from the active monitoring baseline regardless of its previous desired state. Delete/Undo backup metadata is recovery-only and must not participate in Added/Modified identity comparisons.
- Ordinary enabled-state differences are classified changes or reconciliation work, not generic consistency issues. Generic consistency is reserved for actual registry-structure conflicts.
- If active and disabled `ContextMenuHandlers` keys coexist for one stable Id, compare their registry last-write timestamps, keep the newer side, and remove the older side automatically. Timestamp ties fall back to persisted desired state, then active. Do not surface this repair as a generic consistency warning; log failures and retry.
- State-store read/merge/write sequences from snapshots, WPS refresh, approvals, deletion/restore, and reconciliation must remain serialized so concurrent flows cannot overwrite each other's state.

### ProbeHost / Deep Analysis

- ProbeHost is not an elevation path. It is an isolation path.
- Do not directly load third-party Shell Extension DLLs inside the Frontend or Backend Service.
- ProbeHost must not write registry data, execute menu commands, or participate in normal scans.
- Deep Analysis failures are often expected limitations and do not imply normal menu management is broken.

### Fault attribution

- Do not attribute third-party software, installer, or driver failures to this project without evidence.
- If Registry Write Protection is suspected, first verify:
  - whether the relevant protection was enabled;
  - whether logs exist at the same timestamp;
  - whether there is `Access Denied`, `UnauthorizedAccessException`, or equivalent evidence;
  - whether the target registry path is within this project’s protection scope;
  - whether the issue can be reproduced after disabling protection or stopping the service.
- Do not assume “driver install failed” means “context menu interception caused it.”

---

## 4. Common Task Routing

| Task | Inspect first | Do not start with |
| --- | --- | --- |
| Classic menu enable/disable bug | `ContextMenuRegistryCatalog`, `NamedPipeBackendServer`, `docs/registry-model.md` | UAC bootstrapper |
| Win11 menu state bug | `Windows11ContextMenuCatalog`, `Windows11BlocksService`, `docs/windows11-context-menu.md` | classic `shell` / `shellex` toggle logic |
| ShellNew ordering / lock / unlock bug | `SpecialMenuService`, `docs/special-menus.md` | `ContextMenuRegistryCatalog` |
| SendTo / WinX modification bug | `SpecialMenuService`, related ViewModel | classic menu scanning logic |
| Service install / repair failure | `BackendServiceManager`, `BackendServiceBootstrapper` | `NamedPipeBackendServer` |
| Start with Windows not working | `AutoStartService`, `BackendServiceBootstrapper`, `FrontendAutostartLauncher` | legacy Run-key assumptions |
| TrayHost not appearing | `FrontendAutostartLauncher`, `BackendWindowsService`, WTS flow | frontend window logic |
| Restart Explorer not working | user session context / `ExplorerRestartService` | registry menu logic |
| Deep Analysis failure | `ContextMenuDeepAnalysisService`, ProbeHost, `docs/deep-analysis-probehost.md` | normal toggle logic |
| Global search bug | `ContextMenuGlobalSearchService`, `GlobalSearchNavigationFilterService`, `ShellViewModel`, `docs/frontend-wpf-ui.md` | backend scanner |
| Theme / WPF-UI / NavigationView / AutoSuggestBox issue | `FrontendThemeService`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `docs/frontend-wpf-ui.md` | backend service or registry logic |
| Build / release / architecture issue | `Scripts/Build.Common.psm1`, frontend csproj, `docs/build-and-release.md` | runtime business logic |

---

## 5. Pre-Change Checklist

Before editing code, verify:

- [ ] I have read `docs/ai-maintainer-playbook.md`.
- [ ] I have identified the correct flow for this task.
- [ ] I know whether this task needs a user SID.
- [ ] I know whether this task needs a session ID.
- [ ] I am not using the service `HKCU` as the frontend user `HKCU`.
- [ ] I am not using the UAC bootstrapper as the normal runtime backend.
- [ ] I am not trying to show UI directly from the service session.
- [ ] I am not mixing Registry Write Protection with ShellNew ACL Lock.
- [ ] I am not loading third-party Shell Extension DLLs inside the Frontend or Backend Service.
- [ ] If this is a frontend UI/WPF-UI issue, I have read `docs/frontend-wpf-ui.md`.
- [ ] I have checked relevant logs, or I can explain why logs are unavailable.
- [ ] I can explain which files need changes and why.
- [ ] I can provide manual verification steps.

---

## 6. Build and Verification

Common local frontend build:

```powershell
dotnet build .\ContextMenuMgr.Frontend\ContextMenuMgr.Frontend.csproj
```

Before full build/release work, read:

- `docs/build-and-release.md`

When changing ProbeHost, architecture folders, or build scripts, verify:

- x86 / x64 / arm64 ProbeHost artifacts are placed in the correct directories;
- native ProbeHost build outputs match the current scripts;
- architecture verification scripts are used when applicable;
- x86 binaries are not accidentally copied into arm64 directories.

---

## 7. Documentation Maintenance Rules

If you change any of the following, update `docs/` in the same change:

| Change | Update |
| --- | --- |
| Add or change `PipeCommand` | `docs/process-and-privilege-flows.md`, `docs/developer-guide.md` |
| Change privilege flow or user context behavior | `docs/process-and-privilege-flows.md`, `docs/ai-maintainer-playbook.md` |
| Change classic menu scan/toggle strategy | `docs/registry-model.md` |
| Add or change `SpecialMenuKind` | `docs/special-menus.md` |
| Change Win11 blocked list or Packaged COM logic | `docs/windows11-context-menu.md` |
| Change Registry Write Protection | `docs/registry-model.md`, `docs/troubleshooting.md` |
| Change ShellNew ACL Lock | `docs/special-menus.md` |
| Change ProbeHost selection or Deep Analysis behavior | `docs/deep-analysis-probehost.md` |
| Change Frontend theme, WPF-UI styles, NavigationView, AutoSuggestBox, or Page/UserControl composition | `docs/frontend-wpf-ui.md` |
| Change `build.ps1`, release scripts, or csproj copy targets | `docs/build-and-release.md` |
| Change `RuntimePaths` or log paths | `docs/troubleshooting.md`, `docs/developer-guide.md` |
| Change global search coverage | `docs/developer-guide.md`, `docs/troubleshooting.md`, `docs/frontend-wpf-ui.md` |

Documentation must describe the current code, not old README assumptions.

---

## 8. When Evidence Is Insufficient

If the root cause is not proven, say:

> Current evidence is insufficient to determine the root cause.

Then list the files, logs, registry paths, or reproduction steps that still need to be checked.

Do not invent causes.
Do not expand the patch scope just to make progress.
