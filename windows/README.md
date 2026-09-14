# Avaibe Exam — Windows lockdown client

Native Windows client of the exam platform: **C# 12 / .NET 8 / WPF / WebView2**, product name
*Avaibe Exam*, executable `AvaibeExam.exe`. It implements the shared contract in
[`../docs/CONTRACT.md`](../docs/CONTRACT.md) (§2–§8) plus the Windows addendum (§9) and mirrors the
macOS client's state machine, screens and behaviour one-to-one.

> **Written on macOS without compilation.** No .NET SDK was available while this code was written, so
> the first Windows build may need minor fixes (a renamed WebView2 member, a XAML nit, an ambiguous
> type). Start with [`SELF-REVIEW.md`](SELF-REVIEW.md) — it lists what was checked and the few APIs
> the author was less than certain about — and follow `docs/WINDOWS-TEST-PLAN.md` for the manual
> test pass.

## Prerequisites

| Requirement | Notes |
|---|---|
| Windows 11 (or Windows 10 2004+, build 19041+) | `WDA_EXCLUDEFROMCAPTURE` needs 2004+; older builds fall back to `WDA_MONITOR`. |
| .NET 8 SDK | https://dotnet.microsoft.com/download/dotnet/8.0 (`dotnet --version` → `8.x`). |
| WebView2 Runtime (Evergreen) | Pre-installed on Windows 11 and on any Windows 10 with Edge. Otherwise: https://developer.microsoft.com/microsoft-edge/webview2/ . The preflight screen shows the detected runtime version. |
| PowerShell 5.1 or 7 | for the `scripts/*.ps1` helpers. |
| Mock backend | `../mock-backend` (Node.js, `npm start`), on the same PC or on the Mac. |

No administrator rights are required or requested (`app.manifest` → `asInvoker`). The client never
changes registry policies, Defender, Task Manager or any other system setting.

## Build / run

```powershell
cd windows
.\scripts\build.ps1            # restore + build + publish → windows\publish\AvaibeExam.exe
.\scripts\run.ps1              # build (Debug) then start the client
.\scripts\smoke.ps1            # starts with AVAIBE_SMOKE_TEST=1, checks the log for SMOKE_OK, exit 0/1
```

`build.ps1 -Sign -CertThumbprint <sha1>` signs the published binaries with `signtool` (Authenticode).
Plain `dotnet build AvaibeExam.sln` / `dotnet run --project AvaibeExam` work too.

### Packaging

| Script | Output | Needs .NET on the target PC? |
|---|---|---|
| `.\scripts\build.ps1` | `windows\publish\` — framework-dependent folder (`AvaibeExam.exe` + DLLs) | Yes: .NET 8 **Desktop** Runtime |
| `.\scripts\publish-standalone.ps1` | `windows\publish-standalone\AvaibeExam.exe` — one self-contained single-file exe (~150-180 MB) you can copy and double-click | No |
| `.\scripts\make-installer.ps1` | `windows\installer\output\AvaibeExam-Setup-0.1.0.exe` — Inno Setup installer built from `windows\installer\AvaibeExam.iss` | Yes (packages `publish\`) |

Both packaging paths still require the **WebView2 Evergreen runtime** on the target PC (inbox on
Windows 11). `make-installer.ps1` needs [Inno Setup 6](https://jrsoftware.org/isdl.php); the `.iss`
script installs to `{autopf}\Avaibe\Avaibe Exam` for all users, adds Start-menu (and optional
desktop) shortcuts plus an uninstaller entry, requires build 19041+, and silently installs WebView2
from a `MicrosoftEdgeWebview2Setup.exe` dropped next to the `.iss` if the runtime is missing.
Silent deployment: `AvaibeExam-Setup-0.1.0.exe /VERYSILENT /NORESTART`.

A beginner-level, step-by-step walkthrough of all of the above (install the tools, first build,
first run, making the exe, making the installer, signing, kiosk mode) is in
[`../docs/GUIDE-WINDOWS.md`](../docs/GUIDE-WINDOWS.md).

Environment variables (dev only — never set by a normal Start-menu launch):

| Variable | Effect |
|---|---|
| `AVAIBE_BASE_URL` | Pre-fills the backend URL (default `http://localhost:4000`). |
| `AVAIBE_AUTO_RUN=1` | Fills the login form from `AVAIBE_AUTO_TOKEN` / `AVAIBE_AUTO_STUDENT` / `AVAIBE_AUTO_EXAM`, continues, and presses **Start Exam** as soon as the preflight allows it. |
| `AVAIBE_AUTO_EXIT_AFTER=<s>` | Hard-exits the process after *s* seconds (safety net for unattended runs). |
| `AVAIBE_SMOKE_TEST=1` | Logs `SMOKE_OK` once the window is shown and exits 0 after 1 s. |

`run.ps1 -AutoRun -Token SCHOOL-DEMO -Student 1025 -Exam DEMO -ExitAfter 120` maps to the above.

Logs: `%LOCALAPPDATA%\AvaibeExam\logs\avaibe-YYYYMMDD.log` (14 days kept). Data:
`enrollment.dat` (DPAPI, current user), `settings.json` (base URL, last codes — no secrets),
`WebView2\` (browser profile). Delete the folder to reset the device.

## Demo flow with the mock backend

1. Start the mock: `cd mock-backend && npm start` (Mac or the Windows PC). Admin console:
   `http://<host>:4000/admin`.
2. Start the client. Login: backend URL, enrollment token `SCHOOL-DEMO` (first run only), student code
   `1025`, exam code `DEMO` (5 min) / `MATH101` / `SCI202` → **Continue**.
3. Preflight: the checklist (Windows version, Secure Boot, MDM, account type, displays, screen
   sharing, Assigned Access, camera, mic, internet, client version, WebView2, physical machine, local
   session) with the server policy. **Start Exam** is enabled when every *required* row passes and the
   server accepted the session (a `403 PREFLIGHT_FAILED` is shown here with *Re-run checks*).
4. Exam: lockdown engages (badge shows the mode), the exam page loads inside WebView2, heartbeats and
   events flow to the admin console.
5. Release: in the admin console issue a **release code** (student types the 6 digits on the native
   exit overlay after pressing *Submit* / *Request exit* in the page) or press **Release** /
   **Terminate** / **Warn**. When time runs out the client auto-submits and waits for the RELEASE.
6. Exit screen: "Lockdown released. You may close this app." → **Quit** (the only place quitting is
   allowed while a session existed).

**Mock on the Mac, client on Windows:** set `AVAIBE_BASE_URL=http://<mac-ip>:4000` (or type it on the
login screen). The mock's default policy only allows `localhost` / `127.0.0.1`, so the exam page would be
blocked as `BLOCKED_NAVIGATION` — open the admin console → *Policy* panel and add the Mac's IP to
`allowedDomains` first. Make sure the Mac firewall allows incoming connections on port 4000.

## Lockdown modes

| `lockdownMode` | When | What enforces it |
|---|---|---|
| `assigned-access` | The client detects it runs inside a Windows **Assigned Access / Shell Launcher** kiosk session (registry `AssignedAccessConfiguration`, custom Winlogon shell, or no Explorer shell in the session). Windows analogue of macOS AAC. | The OS single-app shell **plus** every kiosk-fallback control below. |
| `kiosk-fallback` | Normal desktop session. | Best effort, applied by the client: borderless maximized topmost window; black cover windows on other monitors (`blockExternalDisplay`); `WH_KEYBOARD_LL` hook swallowing Alt+Tab/Esc/F4/Space, Win keys, Ctrl+Esc, Ctrl+Shift+Esc, PrintScreen, F11/F12, browser keys, Ctrl+N/T/W/O/S/U/H/J and (policy) Ctrl+P / Ctrl+C/X/V; `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` so screenshots/recorders see black; focus watchdog (500 ms) re-activating the window and raising `APP_DEACTIVATED` with the foreground process name; blacklisted-process monitor (`PROCESS_DETECTED`, detection only — nothing is killed); clipboard cleared on engage and focus return; log off / shutdown refused (`WM_QUERYENDSESSION`, `SESSION_END_BLOCKED`); WebView2 restrictions (no context menu, dev tools, downloads, popups, zoom, print/browser accelerators, drag-drop, status bar, autofill; PDF toolbar stripped; all permission prompts denied; navigation default-deny per `allowedDomains`). |
| `none` | Login / Preflight / Exit screens. | — |

`policy.requireAAC: true` means *require Assigned Access*: the client refuses to start in
`kiosk-fallback` (`LOCKDOWN_FAILED`), exactly like the macOS client refuses without AAC.

## What cannot be blocked (honestly reported)

* **Ctrl+Alt+Del** (secure attention sequence) and everything behind it: lock, switch user, sign out,
  **Task Manager** — this build does *not* disable Task Manager or any registry policy. A student can
  end `AvaibeExam.exe` there; the server sees the heartbeats stop.
* **Win+L** on some builds (Windows handles it before low-level hooks; the only reliable block is the
  `DisableLockWorkstation` policy, which we deliberately do not set).
* The **secure desktop** (UAC prompts), a second keyboard/mouse on another session, hardware capture
  devices, virtual machines (detected and flagged via `VIRTUAL_MACHINE_DETECTED`, not prevented),
  remote-control tools that are already connected (detected via `PROCESS_DETECTED` / `RDP_SESSION`).
* `WDA_EXCLUDEFROMCAPTURE` protects the top-level window; verify on the target build that the WebView2
  area is black in screenshots too (see the test plan) — if not, the event
  `SCREEN_CAPTURE_PROTECTION_UNAVAILABLE` / the `captureProtection` field in `LOCKDOWN_*` tells the server
  what level was achieved.
* Power button / reboot.

Every limitation is written to the log and reported to the server as `kiosk-fallback`; the student
sees the amber "Kiosk fallback" badge.

## If the client gets stuck while locked

1. Ask the teacher for a release code or a **Release** from the admin console (normal path).
2. Otherwise press **Ctrl+Alt+Del → Task Manager → AvaibeExam.exe → End task**. The keyboard hook,
   topmost window and shutdown block disappear with the process.
3. `AVAIBE_AUTO_EXIT_AFTER` is available for unattended test runs.

## File map

```
windows/
  AvaibeExam.sln
  README.md, SELF-REVIEW.md
  scripts/build.ps1 | run.ps1 | smoke.ps1 | publish-standalone.ps1 | make-installer.ps1
  installer/AvaibeExam.iss      Inno Setup script → installer/output/AvaibeExam-Setup-0.1.0.exe
  AvaibeExam/
    AvaibeExam.csproj, app.manifest
    App.xaml(.cs)                 composition root: mutex, exception handlers, AppState + MainWindow, auto-run, smoke test
    MainWindow.xaml(.cs)          single window; swaps Login/Preflight/Exam/Exit; refuses Close while locked
    App/Constants.cs              versions, env vars, paths            (namespace AvaibeExam.Core)
    App/AppState.cs               the state machine (mirrors macOS AppState.swift)
    Models/Json.cs, Models.cs     every contract object (§2–§4, §9) + lenient enum converters
    Networking/ApiClient.cs       HttpClient, every endpoint, headers, ApiException(code, failures)
    Networking/EventReporter.cs   thread-safe telemetry queue, periodic flush, retry
    Networking/HeartbeatService.cs PeriodicTimer heartbeat, commands dispatched to the UI thread
    Lockdown/LockdownCoordinator.cs Engage/Release; assigned-access vs kiosk-fallback; events
    Lockdown/KioskFallback.cs     §9.3 controls (window, covers, hook, capture affinity, watchdog, shutdown block)
    Lockdown/KeyboardHook.cs      WH_KEYBOARD_LL block list
    Lockdown/AssignedAccessDetector.cs kiosk-session detection (registry / shell / explorer)
    Lockdown/ProcessMonitor.cs    blacklisted process scan (PROCESS_DETECTED)
    Security/Preflight.cs         §9.2 report + extras + checklist rows
    Security/DeviceIdentity.cs    MachineGuid, DPAPI enrollment file, settings.json
    Security/DisplayMonitor.cs    monitor count + change events
    Security/NetworkMonitor.cs    availability events + /healthz probe
    Browser/ExamWebView.cs        WebView2 setup, navigation policy, dialogs, permissions, shim injection
    Browser/WebMessageBridge.cs   strict message parsing + native→web send
    Views/*.xaml(.cs)             Login, Preflight, Exam (status strip + WebView2 host), Warning/Pause/Exit overlays, Exit
    Util/Log.cs | Version.cs | NativeMethods.cs | ObservableObject.cs
```

## Security notes

* No secrets in the binary. The enrollment token is sent once; `deviceToken` is stored DPAPI-protected
  for the current user only; the session token lives in memory.
* No hidden persistence, no services, no scheduled tasks, no registry policies, no elevation.
* The bridge exposes nothing that ends the session: only `REQUEST_EXIT` → native overlay →
  server-validated release code or a `RELEASE` heartbeat command ends lockdown.
