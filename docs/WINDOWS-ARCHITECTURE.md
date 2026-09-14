# Avaibe Exam — Windows Client Architecture

Scope: the native Windows client (`windows/`, executable `AvaibeExam.exe`, C# / .NET 8 / WPF /
WebView2) and how it implements the shared interfaces in [CONTRACT.md](CONTRACT.md) (§2–§8 plus the
Windows addendum §9). The backend, admin console, monitoring and AI are mocked at this stage (§10).
The companion macOS document is [ARCHITECTURE.md](ARCHITECTURE.md); the two clients share the
contract, the mock backend and the exam page, and differ only in the OS lockdown layer.

Microsoft facts are linked to Microsoft Learn inline. Anything not verified against Learn is marked
*(verify)*.

## 1. Design principles

| Principle | Windows consequence |
|---|---|
| The OS is the security layer | School-managed lockdown is delegated to **Assigned Access** (kiosk / restricted user experience) or **Shell Launcher**, configured by IT through Intune, a provisioning package or PowerShell — never by the client. The client never patches Winlogon, injects into other processes, installs drivers or touches Defender. |
| Server is authoritative | Session state, timers, policy and release authorisation live on the server; the client enforces and reports (`X-Client-Version`, heartbeats, events). |
| Honest capability reporting | `lockdownMode` is one of `assigned-access`, `kiosk-fallback`, `none` and is sent in every heartbeat and shown to the student. The client *detects* Assigned Access; it cannot create it. |
| Exit only through an authorised path | Release code (`POST /sessions/:id/unlock`) or a `RELEASE` heartbeat command. No local secret, no hotkey, no JS-reachable end-session. Ctrl+Alt+Del is documented as a recovery path, not hidden. |
| No hidden persistence | One `.exe` plus WebView2 runtime dependency. No service, no scheduled task, no Run key, no kernel driver, no shell extension. Enrollment/settings live under `%LOCALAPPDATA%\AvaibeExam`. |
| Compatible with endpoint security | Signed binaries, conventional installer, minimal privileges (runs as the signed-in standard user), no anti-analysis behaviour ([WINDOWS-DEPLOYMENT.md §6](WINDOWS-DEPLOYMENT.md)). |

## 2. Process and window model

```
 AvaibeExam.exe (one .NET 8 process, STA UI thread = WPF Dispatcher)
 ├── App (Application) ── AppState store ── screen router (Login → Preflight → Exam → Exit)
 ├── MainWindow (WPF)  ── borderless, Topmost while locked, primary monitor
 │     ├── ExamView: WebView2 control (exam page) + native status strip (WPF)
 │     ├── WarningOverlay / ExitOverlay (WPF, Z-order above the WebView2 airspace)
 │     └── HwndSource hooks: WM_QUERYENDSESSION, WM_DISPLAYCHANGE, WM_ACTIVATE
 ├── CoverWindow ×N (fallback mode): one black Topmost window per non-primary monitor
 ├── LL keyboard hook thread (WH_KEYBOARD_LL, own message loop, no UI work)
 └── WebView2 runtime processes (msedgewebview2.exe browser/renderer/GPU, spawned by the runtime)
       └── exam page ── only bridge: chrome.webview.postMessage ⇄ WebMessageReceived
```

* The exam page runs in the Evergreen WebView2 runtime's own sandboxed renderer processes. It can
  reach native code only through the single web-message channel (§5).
* WPF overlays must sit above the WebView2 HWND ("airspace"). The client hosts overlays in a
  transparent `Window` owned by `MainWindow` (or collapses the WebView2 while an overlay is up),
  so the page can neither cover nor dismiss them. *(Implementation detail; verify in `Views/`.)*
* `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` is applied to `MainWindow` and every
  `CoverWindow` once their HWNDs exist (`WindowInteropHelper.Handle`). The API requires a top-level
  window of the current process and only works while DWM composes the desktop; on Windows
  older than 10 version 2004 it behaves as `WDA_MONITOR` (window shows as black in captures)
  ([SetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)).
* In `assigned-access` mode the OS shell is already restricted (no Start/taskbar of its own, AppLocker
  allow-list). The client *still* applies every fallback control, because Assigned Access explicitly
  does **not** block Alt+F4, Alt+Tab, Alt+Shift+Tab or Ctrl+Alt+Del
  ([Assigned Access recommendations — keyboard shortcuts](https://learn.microsoft.com/en-us/windows/configuration/assigned-access/recommendations#keyboard-shortcuts)).

## 3. Module map (`windows/AvaibeExam/`)

Layout follows CONTRACT §9 and the client build prompt. Type names are indicative; the code is being
written concurrently, so treat this table as the target structure rather than a reflection of the
current tree.

| Directory | Responsibility | Key types (indicative) |
|---|---|---|
| `App/` | `App.xaml`/`App.xaml.cs` entry, single-instance mutex, DI-free composition root, `AppState` (observable store), `Constants` (`clientVersion = "0.1.0"`, timeouts), screen router | `App`, `AppState`, `Constants`, `ScreenRouter` |
| `Models/` | POCOs mirroring CONTRACT §3–§4 and §9.2/9.5 (`System.Text.Json`) | `Policy`, `Session`, `PreflightReport`, `PreflightExtras`, `HeartbeatCommand`, `TelemetryEvent`, `ApiError` |
| `Networking/` | `HttpClient` REST client with bearer/device headers, heartbeat loop, batched event queue with backoff, reachability (`/healthz`) | `ApiClient`, `HeartbeatService`, `EventQueue`, `Reachability` |
| `Lockdown/` | Lockdown state machine, Assigned Access / Shell Launcher detection, fallback kiosk controller (topmost window, cover windows, hook, focus watchdog, clipboard clear, shutdown block), release orchestration | `LockdownController`, `AssignedAccessDetector`, `KioskFallbackController`, `KeyboardHook`, `FocusWatchdog`, `ShutdownBlocker` |
| `Security/` | Preflight probes (§9.2) and runtime detectors: Secure Boot, MDM enrollment, admin membership, display count, RDP session, blacklisted processes, VM heuristics, camera/mic consent, capture-protection support | `PreflightChecker`, `ProcessMonitor`, `DisplayMonitor`, `PrivacyConsent`, `VmDetector` |
| `Browser/` | WebView2 host: environment/user-data-folder setup, `CoreWebView2Settings` hardening, navigation allow-list, popup/download/print/devtools blocking, bridge shim injection, message validation | `ExamWebView`, `NavigationPolicy`, `LockdownBridge`, `BridgeShim` (JS string) |
| `Views/` | WPF screens and overlays (CONTRACT §7): `LoginView`, `PreflightView`, `ExamView`, `WarningOverlay`, `ExitOverlay`, `ExitScreen`, `CoverWindow` | XAML + code-behind or lightweight view-models |
| `Util/` | Logging (file under `%LOCALAPPDATA%\AvaibeExam\logs`), P/Invoke declarations (`NativeMethods`), DPAPI wrapper, registry helpers, semantic version compare, WMI helper (`System.Management`) | `Log`, `NativeMethods`, `Dpapi`, `Reg`, `Version`, `Wmi` |

Dependency direction: `Views → App → Lockdown/Browser/Security/Networking → Models/Util`.
`Lockdown` never references `Browser`; the page learns about lockdown state only via bridge
messages (`LOCKDOWN_STATE`, `SESSION_STATUS`). `Util/NativeMethods` is the only file with
`DllImport` declarations so P/Invoke signatures can be reviewed in one place.

## 4. Lockdown state machine

```
                 startExam()                    AssignedAccessDetector.IsActive()
   ┌──────┐  ─────────────────▶  ┌──────────┐ ──── true ────▶ ┌─────────────────┐
   │ none │                      │ engaging │                 │ assigned-access │
   └──────┘ ◀──┐                 └──────────┘ ──── false ──┐  └─────────────────┘
      ▲        │                       │                   ▼          │
      │        │  requireAAC=true      │          ┌────────────────┐  │  release /
      │        │  and not in AA        └─────────▶│ kiosk-fallback │  │  TERMINATE /
      │        │  → LOCKDOWN_FAILED               └────────────────┘  │  fatal error
      │        │                                          │ release   │
      │        │                                          ▼           ▼
      │        │                                   ┌───────────┐
      └────────┴───────────────────────────────────│ releasing │
              hook removed, covers closed,         └───────────┘
              affinity WDA_NONE, shutdown block destroyed
```

Unlike macOS, Windows has no runtime API that *begins* an assessment session. `engaging` therefore
means "apply the fallback controls, then decide the label". Both locked states run the same client
controls; `assigned-access` additionally benefits from the OS shell restriction configured by IT.

| Transition | Trigger | Actions | Events emitted |
|---|---|---|---|
| `none → engaging` | `POST /sessions` returned 200 | Resolve `examUrl` host against `allowedDomains`; create WebView2 environment (user-data folder under `%LOCALAPPDATA%`); build `MainWindow` borderless/topmost on the primary monitor; create `CoverWindow` per other monitor; install `WH_KEYBOARD_LL` on a dedicated thread; `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`; `ShutdownBlockReasonCreate`; clear clipboard; start `FocusWatchdog`, `ProcessMonitor`, `DisplayMonitor` | `SESSION_START` |
| `engaging → assigned-access` | `AssignedAccessDetector.IsActive()` — `HKCU\SOFTWARE\Microsoft\Windows\AssignedAccessConfiguration` present, or `HKLM\Software\Microsoft\Windows\AssignedAccessConfiguration` names the current user, or Shell Launcher reports the current shell is `AvaibeExam.exe` via `WESL_UserSetting` / `MDM_AssignedAccess` ([registry keys](https://learn.microsoft.com/en-us/windows/configuration/assigned-access/recommendations#troubleshooting-and-logs), [AssignedAccess CSP](https://learn.microsoft.com/en-us/windows/client-management/mdm/assignedaccess-csp)) | Load `examUrl`; start heartbeat with `lockdownMode: "assigned-access"`; inject `LockdownNative.lockdownMode` | `LOCKDOWN_ENGAGED` `{mode:"assigned-access"}` |
| `engaging → kiosk-fallback` | Detector returns false and `policy.requireAAC == false` | Same as above with `lockdownMode: "kiosk-fallback"`; Preflight already showed the limitation report | `LOCKDOWN_FALLBACK` `{reason:"no-assigned-access"}` |
| `engaging → none` | Detector false and `policy.requireAAC == true`, or any control failed to install (hook returned `NULL`, affinity call failed on a required policy) | Tear down partial controls; show error on Preflight | `LOCKDOWN_FAILED` (high), `SCREEN_CAPTURE_PROTECTION_UNAVAILABLE` (low) when affinity failed |
| `locked → releasing` | Valid release authorisation (§6), `TERMINATE`, `externalDisplayAction: TERMINATE`, or unrecoverable error | Final heartbeat `status: unlocked`, flush events; `UnhookWindowsHookEx`; close covers; `SetWindowDisplayAffinity(WDA_NONE)`; `ShutdownBlockReasonDestroy`; `Topmost=false`; restore window chrome | `UNLOCK_COMPLETED` or `SESSION_END` |
| `releasing → none` | Teardown complete | Show final Exit screen with Quit; in Shell Launcher mode exiting the process triggers the configured return-code action (§7) | `SESSION_END` |

Runtime detectors run in both locked states and raise `DISPLAY_CHANGED`, `EXTERNAL_DISPLAY`,
`SCREEN_SHARING_DETECTED`, `RDP_SESSION`, `PROCESS_DETECTED`, `BLOCKED_NAVIGATION`,
`BLOCKED_SHORTCUT`, `APP_DEACTIVATED`, `WINDOW_RESIZED`, `SESSION_END_BLOCKED`, `NETWORK_OFFLINE`,
`NETWORK_ONLINE`. Policy `externalDisplayAction` maps to WARN / BLOCK_START / PAUSE / TERMINATE /
FLAG exactly as on macOS ([WINDOWS-LIMITATIONS.md §4](WINDOWS-LIMITATIONS.md)).

### 4.1 Fallback controls and their Win32 basis

| Control | Mechanism | Known limits (details in WINDOWS-LIMITATIONS.md) |
|---|---|---|
| Fullscreen topmost window | `WindowStyle=None`, `Topmost=true`, size = primary `Screen.Bounds`; re-asserted by `FocusWatchdog` | Another topmost window (UAC secure desktop, some overlays) can still appear. |
| Cover other monitors | One black `Topmost` `CoverWindow` per `Screen.AllScreens` entry except primary; re-created on `WM_DISPLAYCHANGE` | Duplicated (mirrored) display shows exam content by definition → `EXTERNAL_DISPLAY`. |
| Key blocking | `SetWindowsHookEx(WH_KEYBOARD_LL)` on a thread with a message loop; swallow by returning non-zero for the listed combos; must return within `LowLevelHooksTimeout` (max 1000 ms since Windows 10 1709) or the hook is silently removed ([LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)) | Ctrl+Alt+Del (secure attention sequence) is never delivered to hooks; keys on the secure desktop (UAC) are not hookable; injected input from another process still reaches the hook but a kernel-level filter does not. Delegate must be kept alive (GC pitfall). |
| Screenshot/recording protection | `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` | Not a security boundary per Microsoft; photographs, HDMI grabbers, and anything reading the GPU below DWM are out of scope. |
| Focus watchdog | `WM_ACTIVATE`/`Deactivated` → `SetForegroundWindow` + `Topmost` toggle; throttled `APP_DEACTIVATED` | `SetForegroundWindow` is restricted for background processes ([SetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow)); a determined script can keep stealing focus → detect-only. |
| Process monitor | Poll `Process.GetProcesses()` every few seconds against the §9.2 list; `PROCESS_DETECTED` | Renamed binaries and portable tools evade name matching; detect-only. |
| Clipboard | `Clipboard.Clear()` on engage and on focus return; WebView2 page-level copy/paste blocked when `allowClipboard=false` | Other processes can read/write the clipboard at any time. |
| Shutdown / sign-out | Return `FALSE` to `WM_QUERYENDSESSION` while locked and register a reason with `ShutdownBlockReasonCreate` (must be called from the window's thread) ([ShutdownBlockReasonCreate](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-shutdownblockreasoncreate)) | Windows shows the reason and lets the user **Shut down anyway**; a power button or `shutdown /f` is not blocked → `SESSION_END_BLOCKED` is a detect-only signal. |
| WebView2 restrictions | `CoreWebView2Settings`: `AreDefaultContextMenusEnabled=false`, `AreDevToolsEnabled=false`, `IsStatusBarEnabled=false`, `IsZoomControlEnabled=false`, `IsPinchZoomEnabled=false`, `AreBrowserAcceleratorKeysEnabled=false`, `IsGeneralAutofillEnabled=false`, `IsPasswordAutosaveEnabled=false`, `IsSwipeNavigationEnabled=false`; `NewWindowRequested` → handled/cancelled; `DownloadStarting` → `Cancel=true`; `NavigationStarting` + `FrameNavigationStarting` → allow-list; PDF viewer disabled via additional browser arguments *(verify exact flag in `Browser/`)* | Renderer is Microsoft-serviced Chromium; the client cannot patch it and does not try. |

## 5. Web ⇄ native bridge and the `window.webkit` shim

The exam page (`mock-backend/public/exam.html`) was written for WKWebView and calls
`window.webkit.messageHandlers.lockdown.postMessage(msg)` and expects replies on
`window.LockdownBridge.onMessage(msg)`. WebView2 exposes `window.chrome.webview.postMessage`
instead and has no `window.webkit`. Rather than fork the page, the client injects a shim at document
creation (CONTRACT §9.4):

```
  exam.html (untrusted)                                   native (trusted)
  ─────────────────────                                   ────────────────
  window.webkit.messageHandlers.lockdown.postMessage(m)
      └─ shim ─▶ window.chrome.webview.postMessage(m) ──▶ CoreWebView2.WebMessageReceived
                                                          │ e.Source origin ∈ allowedDomains?
                                                          │ e.WebMessageAsJson → type allow-list,
                                                          │ payload shape, size cap (16 KiB)
                                                          ▼
                                                        AppState / EventQueue
  window.LockdownBridge.onMessage(msg)  ◀── ExecuteScriptAsync("window.LockdownBridge&&
                                            window.LockdownBridge.onMessage(<JSON literal>)")
```

Rules enforced in `Browser/LockdownBridge`:

1. The shim and `window.LockdownNative` are added with
   `CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync` before the first navigation. The
   `LockdownNative` object is `Object.freeze`d and informational only (`platform: "windows"`,
   `clientVersion`, `lockdownMode`).
2. Every `WebMessageReceived` is checked against `e.Source` (the sending document's URI) and the
   domain allow-list; messages from other origins or iframes are dropped and logged as `WEB_EVENT`
   (medium).
3. Inbound types are an allow-list: `READY`, `REPORT_EVENT`, `GET_SESSION_STATUS`, `REQUEST_EXIT`,
   `SUBMIT_COMPLETE`, `HEARTBEAT_PING`. Unknown types, non-JSON (`WebMessageAsJson` throws for
   strings) and oversized payloads are ignored plus `WEB_EVENT`.
4. Nothing the page sends ends lockdown, changes policy or reads device secrets. `REQUEST_EXIT` and
   `SUBMIT_COMPLETE` only raise the *native* WPF Exit overlay.
5. Native → web payloads are serialised with `System.Text.Json` and embedded as a JSON string
   literal; no string interpolation of untrusted values into script.
6. `chrome.webview.postMessage` requires `CoreWebView2Settings.IsWebMessageEnabled = true`
   (default); the client never enables `AddHostObjectToScript` (no COM objects exposed to the page).
7. Navigation policy: `NavigationStarting` and `FrameNavigationStarting` compare the host against
   `allowedDomains` (exact host or `*.suffix`); off-list → `Cancel = true` + `BLOCKED_NAVIGATION`.
   `NewWindowRequested` is either routed into the same view (`e.NewWindow = CoreWebView2`) or
   cancelled; `DownloadStarting.Cancel = true` unless `allowDownloads`.

Why a shim rather than a second page: one exam page, one contract, one set of bridge tests. The
shim is four lines and is the *only* Windows-specific code the page ever touches.

## 6. Exit / release flow

### 6a. Student-entered release code

```
 Student        Native (ExitOverlay, WPF)     Backend                       Teacher (admin console)
   │                  │                         │                               │
   │                  │                         │◀── POST /admin/sessions/:id/release-code
   │                  │                         │──▶ { releaseCode, expiresAt(+60s) }
   │  reads code from teacher                   │                               │
   │── types 6 digits ▶│                        │                               │
   │                  │── POST /sessions/:id/unlock {releaseCode, deviceId} ──▶ │
   │                  │   (event UNLOCK_REQUESTED)                              │
   │                  │◀── 200 {authorized, authorizationId, expiresAt}          │
   │                  │     or 403 INVALID/EXPIRED/USED → EXIT_DENIED, UNLOCK_DENIED
   │                  │── LockdownController.Release(): state releasing (§4)
   │                  │── event UNLOCK_COMPLETED {authorizationId}
   │◀── Exit screen ──│── final heartbeat status=unlocked, flush, SESSION_END
```

The overlay is WPF, not web content, so it is reachable even if the page is broken and it is not
subject to the page's JS. The 6-digit field is the only text input active while locked; the LL
hook lets digits, Backspace and Enter through to it.

### 6b. Remote `RELEASE` heartbeat command (also used after submit)

```
 Native (HeartbeatService)                 Backend                         Teacher
   │                                          │◀── POST /admin/sessions/:id/release
   │── POST /sessions/:id/heartbeat ────────▶ │      (or auto-queued after /submit)
   │◀── { command: { type:"RELEASE", authorizationId, expiresAt, reason } }
   │   validate expiresAt > now (server time from response), authorizationId unseen
   │── Dispatcher.Invoke(LockdownController.Release(reason))
   │── UNLOCK_COMPLETED, SESSION_END ───────▶ │
   │── show Exit screen                        │
```

`TERMINATE` follows the same teardown but shows the reason as an error. `WARN` never changes state.
Timer expiry: when `remainingSeconds` reaches 0 the client posts `/submit`; the backend auto-issues
`RELEASE` on the next heartbeat. In Shell Launcher deployments the process exit code selects the
Shell Launcher action (restart shell / restart device / shut down / do nothing) — the client exits
with `0` after a normal release so the configured action (typically *restart shell*) brings the
login screen back ([Shell Launcher exit actions](https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/configure#shell-launcher-startup-and-exit-behavior)).

## 7. School-managed vs BYOD

| Aspect | School-managed (`mode: "school"`) | BYOD (`mode: "byod"`) |
|---|---|---|
| Enrollment token | `SCHOOL-*` (real system: delivered by Intune as a registry/config value) | `BYOD-*` (student self-enrols) |
| OS lockdown | Assigned Access restricted user experience (Pro/Enterprise/Education/IoT Enterprise) with `AvaibeExam.exe` in `AllowedApps` and `AutoLaunch`; or Shell Launcher (Enterprise/Education/IoT Enterprise only) with `AvaibeExam.exe` as the shell ([Assigned Access overview](https://learn.microsoft.com/en-us/windows/configuration/assigned-access/), [Shell Launcher overview](https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/)) | None; client applies `kiosk-fallback` |
| Expected `lockdownMode` | `assigned-access` | `kiosk-fallback` |
| Preflight defaults | `requireSIP` (= Secure Boot), `requireMDM`, `requireStandardAccount`, often `requireAAC` | `requireSIP` only; MDM N/A; limitation report shown |
| Extra OS controls | Intune policies: Keyboard Filter (Enterprise/Education) for Ctrl+Alt+Del/Win+L, Game DVR off, RDP off, privacy CSPs, App Control / AppLocker (see WINDOWS-DEPLOYMENT.md §2) | First-run camera/mic prompts; SmartScreen on first download |
| Release | Teacher code / remote RELEASE; IT can also remove the Assigned Access policy or sign the kiosk account out | Teacher code / remote RELEASE |
| Report to student | "Exam runs in a school-managed kiosk session" | Explicit list of best-effort controls |

Both modes share one binary and one installer; the server policy selects behaviour.

## 8. Why C#/.NET + WebView2, not Electron

* **Assigned Access and Shell Launcher want a single trustworthy executable path.** `AllowedApps`
  and the Shell Launcher `Shell` attribute point at one `.exe`; AppLocker rules are generated from
  it. An Electron app is `electron.exe` + dozens of DLLs + an `app.asar` that any user with write
  access to the install folder can edit; allow-listing it is coarse and its publisher rule covers
  every Electron app.
* **The browser engine is serviced by Microsoft, not by us.** The Evergreen WebView2 runtime is
  shared, updated through the Microsoft Edge update mechanism and included with Windows 11
  ([Distribute your app and the WebView2 Runtime](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)).
  Chromium security fixes reach students without an Avaibe release; an Electron app must ship its
  own Chromium with every fix.
* **Small trusted computing base.** One process we own, no Node runtime reachable from page
  content, no `nodeIntegration`/`contextIsolation` foot-guns; the only page→native channel is the
  web-message handler in §5.
* **Direct Win32 access.** `SetWindowDisplayAffinity`, `SetWindowsHookEx`,
  `ShutdownBlockReasonCreate`, WMI (`MDM_AssignedAccess`, `WESL_UserSetting`), DPAPI and registry
  are one `DllImport` away; no native addon build chain.
* **Defender / SmartScreen reputation** accrues to one signed binary and one publisher identity
  ([SmartScreen reputation for developers](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)).
* WPF (rather than WinUI 3) keeps the app unpackaged-friendly, works on Windows 10 (still common in
  schools even after its October 2025 end of support) and lets the Exit overlay be plain native UI.

The fallback kiosk mode exists so the product works on BYOD and un-configured lab machines, but it
is documented as best-effort and reported as such (WINDOWS-LIMITATIONS.md).

## 9. Threading and lifetime notes

* All WebView2 and WPF calls happen on the Dispatcher thread; `HeartbeatService` and `EventQueue`
  run on the thread pool and marshal UI changes with `Dispatcher.InvokeAsync`.
* The LL hook runs on its own thread with a `GetMessage` loop so a busy UI thread cannot exceed the
  1000 ms hook timeout; the hook callback only classifies the key and posts to a channel.
* The `HookProc` delegate and the `CoreWebView2Environment` are held in fields for the app lifetime
  (a collected delegate crashes the hook; a released environment ends the browser).
* On unhandled exception while locked the client releases controls *before* exiting (a crashed
  fallback process leaves nothing behind: the hook, affinity and topmost state die with the process).
* Single instance: a named mutex prevents a second `AvaibeExam.exe` from starting a second session.

## 10. What is deliberately mocked at this stage

| Component | Now | Later (ROADMAP.md) |
|---|---|---|
| Backend | `mock-backend/server.js`, in-memory | Node/TypeScript API, Postgres, Redis, RBAC, signed unlock tokens |
| Exam UI | `public/exam.html` (unchanged; shim makes it work) | React exam engine with autosave |
| Admin console | `public/admin.html` | Next.js console with RBAC, incident review |
| OS kiosk configuration | Not automated; sample XML and PowerShell in WINDOWS-DEPLOYMENT.md, applied by hand on the POC machine | Intune configuration profiles shipped with the deployment kit |
| Managed enrollment | Token typed on Login | Intune-delivered registry values read at first run |
| Monitoring | Preflight reads camera/mic consent only | Media capture, chunked upload |
| Update channel | None (min-version check only) | Signed update feed via Intune supersedence / MSIX |
