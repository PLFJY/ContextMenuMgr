# AGENTS.md

This is **Context Menu Manager Plus / ContextMenuMgr**, a Windows utility with a WPF frontend, LocalSystem Backend Service, user-session TrayHost, native ProbeHost, Registry operations and Named Pipe IPC. The solution is [ContextMenuMgr.slnx](ContextMenuMgr.slnx); version properties live in [Directory.Build.props](Directory.Build.props).

## Start with the affected responsibility

Read this file, identify the subsystem, read its authoritative document, then inspect current code/interfaces. Read adjacent documents only for affected contracts; do not pre-read every general guide. Detailed docs are Simplified Chinese and must not be skipped. README is user-facing, not the implementation specification. Full routing: [docs/README.md](docs/README.md).

| Task | Primary document / starting owners |
| --- | --- |
| New maintainer / module boundaries | [Architecture](docs/architecture-overview.md) |
| IPC, SID/hive, impersonation, privilege/session flow | [Process and IPC](docs/process-and-privilege-flows.md); NamedPipeBackendServer, BackendUserContextResolver |
| Baselines, approval, external changes, state schema/reset/recovery | [State and monitoring](docs/state-and-monitoring.md); catalog, monitor, ContextMenuStateStore |
| Classic shell/shellex, backups, Registry Write Protection | [Registry model](docs/registry-model.md); ContextMenuRegistryCatalog, mutation/ACL helpers |
| Windows 11 / Blocked / Packaged COM | [Win11](docs/windows11-context-menu.md); Windows11ContextMenuCatalog, Windows11BlocksService |
| ShellNew discovery/order/ACL | [ShellNew](docs/shellnew.md); SpecialMenuService, FrontendUserClassesRoot, ShellNewOrderTransaction |
| OpenWith registrations/inspection/monitoring/authorization | [OpenWith](docs/openwith.md); SpecialMenuService, OpenWithRegistration, OpenWithMonitoringService |
| SendTo, WinX, other SpecialMenu, WPS/Office | [SpecialMenu](docs/special-menus.md) |
| WPF-UI/theme/navigation/search/async UI | [Frontend](docs/frontend-wpf-ui.md); App, MainWindow, related ViewModel |
| Service install/repair/AutoStart/TrayHost/Explorer/close | [Lifecycle](docs/runtime-lifecycle.md); bootstrapper, BackendWindowsService, FrontendAutostartLauncher |
| RuntimePaths/logging/rolling/Locks/migration/data ACL | [Runtime data](docs/runtime-data-and-logging.md) |
| Deep Analysis/native isolation | [ProbeHost](docs/deep-analysis-probehost.md) |
| Build/release/version/architectures/GitCode | [Build and release](docs/build-and-release.md) |
| Inno deployment/upgrade/runtime cleanup | [Installer](docs/installer-deployment.md) |
| Scoop/winget/dry-run/external publication | [Package managers](docs/package-managers.md) |
| Unclear report / runtime failure | [Troubleshooting](docs/troubleshooting.md); verification practice in [Maintainer workflow](docs/maintainer-workflow.md) |

## Non-negotiable boundaries

- Before changing behavior identify the process, flow, SID, SessionId, physical hive/view, elevation needs, third-party COM exposure, and whether this is only UI state. Inspect code/docs until these are clear.
- Flow A is Frontend → Backend Pipe → Service for normal runtime operations. Flow B is UAC → bootstrapper for service lifecycle/startup mode, with only the existing narrow data-ACL-repair fallback. Flow C is Service → WTS token → CreateProcessAsUser for interactive UI. Flow D is Frontend → ProbeHost for isolated analysis, never elevation.
- Never use service HKCU as frontend HKCU. User operations require the correct `HKEY_USERS/<SID>` and loaded hive; privilege does not supply user identity. Validate physical source and authenticated SID. SessionId locates a desktop, not a registry identity. Do not silently select another user for strict-auth operations.
- Never show UI/notifications directly from the service session. Restart Explorer must target only the frontend session, never all explorer.exe processes.
- Tray/notification/shell integration uses Win32/PInvoke only: no Windows App SDK, WinUI, AppNotificationManager, packaged activation or Microsoft.WindowsAppSDK. Do not change TFMs to versioned SDK TFMs **for tray/notification work**; current existing WinRT discovery TFM is a separate requirement. TrayHost remains running when its icon is hidden so notification delivery/activation continues.
- Never load/probe third-party Shell Extension DLLs inside Frontend or Backend. ProbeHost does not write registry data, execute menu commands or participate in normal scans. Expected analysis failures do not imply normal management failure.
- UI/template/binding/theme/navigation/search bugs start in the frontend. Do not change backend/registry/privilege behavior without evidence it is involved. Keep WPF Page in proper navigation hosts; embedded content uses UserControl.
- ShellNew/SendTo/WinX/OpenWith are SpecialMenu responsibilities. ShellNew ACL Lock and Registry Write Protection are separate mechanisms. Do not generalize protected-machine-ShellVerb token/ACL fallback to user hives, ShellNew or ordinary reads.
- Win11 declarations are distinct from classic registrations. Preserve authenticated SID/native read-back and application-versus-CLSID control domains. Registry success does not prove Explorer compatibility; shared/incomplete discovery does not prove an incident's cause.

## State and mutation invariants

- Missing/reset state adopts current regular/Win11/WPS state without pending, Added/Modified badges or ordinary switch mismatch warnings. Regular and WPS have separate explicit baseline markers; never infer either from unrelated records or establish a first baseline without complete interactive-user context.
- Runtime unknown items are disabled and sent to approval only after successful quarantine. Known enabled→disabled is Modified. Known disabled→enabled between settled runtime snapshots is silently disabled again. Existing-baseline startup/offline unknown is Added without quarantine; either switch direction is Modified, never silently reconciled.
- Confirmed missing physical items leave the active baseline regardless of desired state. Delete/Undo recovery metadata does not participate in Added/Modified identity. Ordinary switch differences are classification/reconciliation, not generic consistency warnings.
- Active/disabled ContextMenuHandlers duplicates keep the side with newer registry last-write time; ties use desired state, then active. Remove the older side automatically; log/retry failures without generic consistency warnings.
- Serialize full state read/merge/write across snapshots, WPS, approval, deletion/restore and reconciliation. Preserve schema migrations, corruption/backup behavior and uncertain outcomes; OpenWith uses its own section and explicit authorization, never classic quarantine.
- Capture original values/types/provenance before mutation, verify physical and logical state and persistence, and preserve external changes during conditional rollback. A successful API/log/response alone is insufficient. Never blindly retry an uncertain write.

## Evidence and change discipline

Check logs or explain why unavailable. State what files need changes and why. For third-party installer/driver failures verify protection was enabled, timestamped logs, Access Denied/equivalent, target within scope, and reproduction with protection disabled/service stopped before attributing fault. When unproven say **“Current evidence is insufficient to determine the root cause.”** and identify missing logs/paths/reproduction evidence; do not expand a patch to guess.

Refactoring is replacement: remove superseded implementations and update every caller/resource/config/doc. No parallel Old/New/V1/V2/Legacy/Compat/Backup code, reference copies, commented code, unnecessary fallback or pass-through wrappers without demonstrated compatibility. Search replaced names/paths before finishing. Windows “Legacy Context Menu” terminology is legitimate; persisted compatibility is judged by behavior.

Keep necessary bridges until released format/behavior, supported historical data, observable safe-removal condition and tracking Issue or explicit maintenance decision are documented. Do not invent removal versions, discard uncertain upgrade paths or create a tracking document per helper. Git history is the archive.

## Tests, verification and completion

- Classify every new test: permanent only for an important nontrivial invariant with realistic regression value; temporary incident/implementation checks must be removed. Passing or effort invested is not justification; no default one-class-per-Issue retention.
- Retain meaningful registry/read-back/rollback/provenance/recovery, ACL/SID/hive, corruption/migration, mutation concurrency/IPC/timeout/late-notification, malformed-input and artifact/publication safety coverage when justified. These examples do not mandate new tests.
- Remove framework/trivial accessor/constructor/enum/localization/GUID/string/private-structure checks, repetitive Busy/Loading/Toggle tests and historical reproductions already covered by an invariant. Hardware-dependent WPF experiments belong in manual checks.
- No test-only production properties, fault callbacks, simulation services, DI registrations, exposed methods or alternate paths. Only production-useful abstractions may also support tests; temporary infrastructure stays outside production. Debug tools must not affect Beta/Release.
- Distinguish request completion, physical state, authoritative read-back, persistence and user-visible result. For publishing distinguish execution, artifact creation, validation, remote publication and platform acceptance. Fabricated snapshots/mock counts/process launch/local tests do not establish end-to-end success.
- Run applicable checks/builds. Common frontend build: `dotnet build .\ContextMenuMgr.Frontend\ContextMenuMgr.Frontend.csproj`. Build/release/native changes require the build guide and x86/x64/arm64 PE/layout verification; never copy x86 binaries into arm64 directories. Documentation-only work normally needs link/source/diff checks, not full application execution.
- Finish by removing temporary tests/artifacts, superseded code, unused helpers/imports/resources/scaffolding and test hooks; search abandoned names/debug/compatibility paths, consolidate duplicates, update authoritative docs and review unrelated diff/status. Report what was deleted and why, checks actually executed, and manual Windows/hardware/external checks still pending.

## Documentation maintenance

Update only affected authoritative responsibilities with behavior/contract changes: PipeCommand/identity→process guide, state→state guide, registry/ACL→registry, SpecialMenuKind→SpecialMenu plus its owner, Win11→Win11, UI/search→frontend, RuntimePaths/logs→runtime data, SCM/Tray→lifecycle, ProbeHost→analysis/build, scripts/csproj→build/installer/package-manager guide. Link adjacent contracts instead of copying them.

Use Chinese for detailed docs, current source as implementation authority, and explicit labels for design constraints, historical compatibility, unresolved evidence and integration checks. Integrate current behavior into architectural sections; no chronological issue appendices, temporary audit reports or backup copies. New documents need independent long-term ownership. Keep [docs/README.md](docs/README.md) task routing and all local links/source paths valid.
