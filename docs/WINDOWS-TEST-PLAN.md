# Manual Test Plan — Windows client against the mock backend

Applies to client `0.1.0` in `windows/AvaibeExam/` (C# / .NET 8 / WPF / WebView2) and
`mock-backend` (`npm start`, http://localhost:4000). Mirrors [TEST-PLAN.md](TEST-PLAN.md) (macOS);
rows that need Assigned Access or Shell Launcher require a lab PC configured per
WINDOWS-DEPLOYMENT.md §2. The code was authored on macOS without compiling, so §0 is a mandatory
first-build pass before any functional row.

## 0. First-build checklist (Windows machine)

Prerequisites: Windows 11 (or 10 22H2) x64, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0),
WebView2 Evergreen runtime present (inbox on Windows 11), Node 18+ for the mock, Git. Visual Studio
2022 with the ".NET desktop development" workload is optional but helps with XAML errors.

```powershell
git clone <repo> ; cd "Lockdown Browser"
dotnet --version                                  # 8.x
dotnet restore windows/AvaibeExam/AvaibeExam.csproj
dotnet build   windows/AvaibeExam/AvaibeExam.csproj -c Debug
dotnet run --project windows/AvaibeExam/AvaibeExam.csproj
```

Expected first-run issues to look for (fix in place, keep a list in the PR):

| Area | Symptom | Likely fix |
|---|---|---|
| Project file | `NETSDK1100` / WPF only builds on Windows | Build on Windows; `<UseWPF>true</UseWPF>`, `<TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>` (needed for WinRT/`Windows.Graphics` types if used). |
| `Screen.AllScreens` | `System.Windows.Forms` not found | `<UseWindowsForms>true</UseWindowsForms>` or replace with `EnumDisplayMonitors` P/Invoke. |
| WebView2 | `Microsoft.Web.WebView2` package missing / version mismatch | `dotnet add package Microsoft.Web.WebView2` (1.0.2xxx+); `WebView2Loader.dll` must be in `runtimes/win-x64/native`. |
| WebView2 user data folder | `EnsureCoreWebView2Async` throws `UnauthorizedAccessException` when installed under Program Files | `CoreWebView2Environment.CreateAsync(userDataFolder: %LOCALAPPDATA%\AvaibeExam\WebView2)`. |
| WebView2 startup order | `CoreWebView2` is null when settings are applied | Await `EnsureCoreWebView2Async()` before touching `CoreWebView2.*`; call `AddScriptToExecuteOnDocumentCreatedAsync` before `Navigate`. |
| P/Invoke | `EntryPointNotFoundException`, `PInvokeStackImbalance`, wrong `CharSet` | Check `DllImport("user32.dll", SetLastError = true)`; `SetWindowsHookEx(WH_KEYBOARD_LL=13, proc, GetModuleHandle(null), 0)`; `IntPtr` for HWND/LPARAM. |
| Hook delegate GC | Hook works for a minute then keys pass through; sometimes `ExecutionEngineException` | Store the `HookProc` delegate in a field; run the hook on a thread with a message loop (`Application.Run` or `GetMessage` loop). |
| Hook timeout | Keys pass through after UI stalls | Callback must return < 1 s; do only classification in the callback. |
| `SetWindowDisplayAffinity` | Returns `FALSE` / `ERROR_INVALID_PARAMETER` | Call after `SourceInitialized`; use `new WindowInteropHelper(win).Handle`; avoid `AllowsTransparency=true` on the main window (layered windows can break affinity — *verify*). |
| `ShutdownBlockReasonCreate` | `ERROR_ACCESS_DENIED` | Must be called on the UI thread that created the window. |
| WMI | `System.Management` not found | `dotnet add package System.Management`; queries `root\cimv2\mdm\dmmap` require SYSTEM — the client should only *read* `HKCU/HKLM AssignedAccessConfiguration` keys and fall back gracefully. |
| Admin check | `WindowsPrincipal` compile error | `dotnet add package System.Security.Principal.Windows` (inbox on net8.0-windows). |
| DPAPI | `ProtectedData` missing | `dotnet add package System.Security.Cryptography.ProtectedData`. |
| JSON | Enum/case mismatches vs CONTRACT | `JsonSerializerOptions { PropertyNamingPolicy = CamelCase }`, string enums for `severity`, `lockdownMode`. |
| Async in WPF | UI freezes or `InvalidOperationException` cross-thread | `Dispatcher.InvokeAsync` from heartbeat/event threads; avoid `async void` except event handlers. |
| Nullable warnings | Hundreds of CS8600-series warnings | Acceptable for the first build; fix real null derefs (`CoreWebView2`, `Session`). |
| Localhost | `http://localhost:4000` blocked as insecure content? | WebView2 allows plain http; `HttpClient` needs no special config. Backend must be running before Login. |
| First window | Borderless window covers the taskbar but not the Start button on some DPI settings | Set `SizeToContent=Manual`, `Left/Top/Width/Height` from `Screen.Bounds` in device pixels ÷ DPI scale, or `PerMonitorV2` DPI awareness in `app.manifest`. |

Exit criteria for §0: `dotnet build` clean of errors; app launches to Login; `dotnet run` with
`AVAIBE_SMOKE_TEST=1` (if implemented) prints `SMOKE_OK`; no unhandled exception in
`%LOCALAPPDATA%\AvaibeExam\logs`.

## 0b. Setup for functional rows

```powershell
cd mock-backend; npm install; npm start                     # terminal 1
dotnet run --project windows/AvaibeExam/AvaibeExam.csproj   # terminal 2
start http://localhost:4000/admin                           # teacher console
```

Demo data (CONTRACT §8): enrolment `SCHOOL-DEMO` / `BYOD-DEMO`; student `1025`; exams `MATH101`
(60 min), `SCI202` (45 min), `DEMO` (5 min). Policy is edited live in the admin console.

Recording results: one row per case with *Pass/Fail*, *build*, *Windows edition + build number*,
*mode* (`assigned-access`/`kiosk-fallback`), *notes*, plus `GET /api/v1/admin/sessions/:id/events`
for failures.

Tester safety: keep a second machine with an RDP/PowerShell Remoting or SSH path to the test PC,
and know the recovery steps in §13 before running any lockdown row. On the POC lab PC keep an
administrator account that is *not* in the Assigned Access config.

## 1. Login and enrolment

| ID | Steps | Expected |
|---|---|---|
| L1 | First launch, default URL, token `SCHOOL-DEMO`, student `1025`, exam `MATH101`, Continue | `POST /devices/enroll` 200 with `platform: "windows"`, `hardwareId` = MachineGuid; `mode: school` stored (DPAPI `enrollment.dat`); Preflight screen |
| L2 | Relaunch | Token field hidden; student/exam remembered from `settings.json` |
| L3 | Delete `%LOCALAPPDATA%\AvaibeExam`, token `BYOD-DEMO` | `mode: byod`; Preflight shows limitation report |
| L4 | Wrong URL / backend down | Clear error, stays on Login, no crash |
| L5 | Exam code `NOPE` | 404 `EXAM_NOT_FOUND` surfaced |
| L6 | Admin sets `minClientVersion: 9.9.9` | Preflight fails "Update required" |
| L7 | Copy `enrollment.dat` to another user profile | Decrypt fails (DPAPI CurrentUser) → re-enrol prompt, no crash |

## 2. Preflight (policy toggles in admin console)

| ID | Policy toggle | Machine state | Expected |
|---|---|---|---|
| P1 | defaults | normal | All required rows ✓; Start Exam enabled; `preflightExtras` posted (`edition`, `secureBoot`, `tpmPresent`, `virtualMachine`, `assignedAccess`, `rdpSession`, `screenCaptureProtection`) |
| P2 | `requireSIP: true` | Secure Boot off (VM with UEFI, Secure Boot disabled) | ✗ Secure Boot; Start disabled; forced `POST /sessions` → 403 `["SIP_DISABLED"]` |
| P3 | `requireMDM: true` | unmanaged PC | ✗ MDM; Start disabled |
| P4 | `requireStandardAccount: true` | admin account | ✗ Account type |
| P5 | `blockExternalDisplay: true`, `BLOCK_START` | 2 monitors (extend) | ✗ Displays; server `EXTERNAL_DISPLAY` |
| P6 | defaults | Start `obs64.exe` or TeamViewer before Preflight | ✗ Screen sharing (required); `PROCESS_DETECTED` |
| P7 | `requireAAC: true` | plain desktop (no Assigned Access) | ✗ Assigned Access; Start disabled with explanation |
| P8 | `requireAAC: false` | plain desktop | Row "N/A — supervised mode will be used"; Start enabled |
| P9 | defaults | camera/mic desktop-app switch off in Settings | Rows ✗ with link to `ms-settings:privacy-webcam`; ✓ after toggling on and re-check without restart |
| P10 | defaults | Wi-Fi off | ✗ Internet; ✓ when reconnected without restart |
| P11 | defaults | Run inside an RDP session | ✗ RDP session; `rdpSession: true`; start refused |
| P12 | defaults | Hyper-V/VMware guest | `virtualMachine: true`, `VIRTUAL_MACHINE_DETECTED`; start allowed unless policy blocks (note behaviour) |

## 3. Lockdown engage

| ID | Machine | Expected |
|---|---|---|
| E1 | plain desktop (fallback) | Within ~3 s: borderless topmost window on primary monitor, black covers on others, taskbar hidden behind window, status badge "Supervised (fallback)"; events `SESSION_START`, `LOCKDOWN_FALLBACK`; heartbeat `lockdownMode: kiosk-fallback`; page `LockdownNative.lockdownMode === "kiosk-fallback"` and `platform === "windows"` |
| E2 | Assigned Access restricted user experience, kiosk account | Badge "School kiosk"; `LOCKDOWN_ENGAGED {mode:"assigned-access"}`; heartbeat `assigned-access`; no Start/taskbar system-wide |
| E3 | Shell Launcher (Enterprise/Education) | Same as E2; `explorer.exe` absent from process list; after release and Quit the shell restarts per `DefaultAction` |
| E4 | fallback | Exam page loads; bridge `READY` → `SESSION_STATUS` with student name, exam title, remaining time; `WebMessageReceived` source is `http://localhost:4000` |
| E5 | fallback, affinity unsupported (Windows 10 < 2004 VM) | `SCREEN_CAPTURE_PROTECTION_UNAVAILABLE`; `screenCaptureProtection: false`; exam continues; Preflight row marked |

## 4. Blocked shortcuts (fallback; also run under Assigned Access)

Press each while locked; expect no effect and one throttled `BLOCKED_SHORTCUT {keys}` (max one per
2 s per combo):

`Alt+Tab`, `Alt+Shift+Tab`, `Alt+Esc`, `Win+Tab`, `Alt+F4`, `Ctrl+Esc`, `Win` (tap), `Win+D`,
`Win+E`, `Win+R`, `Win+I`, `Win+S`, `Win+X`, `Win+A`, `Win+G`, `Win+P`, `Win+L`, `Win+V`, `Win+U`,
`Win+Shift+S`, `PrintScreen`, `Alt+PrintScreen`, `Ctrl+Shift+Esc`, `F11`, `Ctrl+P`
(`allowPrinting=false`), `Ctrl+C/X/V` in page (`allowClipboard=false`), `F12`, `Ctrl+Shift+I`,
`Ctrl+N`, `Ctrl+T`, `Ctrl+W`, `Ctrl+F4`, `Ctrl+R`/`F5` (note: WebView2 `AreBrowserAcceleratorKeysEnabled=false`
should already suppress), `Ctrl+Plus/Minus/0` (zoom), `Ctrl+Alt+F12` (verify no hidden breakout).

Also: right-click in page → no context menu; touchpad three/four-finger swipes and edge gestures →
record behaviour (expected partial; known limitation, not failure). **Ctrl+Alt+Del** → security
screen appears (expected; see §13) and on return `APP_DEACTIVATED` is recorded.

## 5. Capture protection

| ID | Steps | Expected |
|---|---|---|
| C1 | Press `PrintScreen` while locked (key is swallowed, so also trigger a capture from a pre-started tool); after release paste into Paint | Clipboard image has the exam area missing/black (Win10 2004+/Win11: omitted; older: black) |
| C2 | Snipping Tool via Win+Shift+S | Key swallowed; if launched by mouse from another app before lock: capture shows black/omitted exam window |
| C3 | Xbox Game Bar (Win+G, or pre-opened) record | Recording shows black/omitted exam; `PROCESS_DETECTED` not required (GameBar is inbox) — note behaviour |
| C4 | OBS Display Capture started *before* lock | Preview shows black where the exam is; `PROCESS_DETECTED {process:"obs64"}` with policy action |
| C5 | OBS Window Capture of `AvaibeExam` | Black/empty; same event |
| C6 | Teams/Zoom screen share (pre-started) | Remote viewer sees black exam area; `PROCESS_DETECTED` for known share helpers (note which) |
| C7 | Cover windows | Screenshot of secondary monitor is also black/omitted |

## 6. Navigation and web policy

| ID | Steps | Expected |
|---|---|---|
| N1 | Click link to `https://example.com` in exam page | Cancelled; `BLOCKED_NAVIGATION {url}`; page stays |
| N2 | `window.open('https://example.com')` (button injected in exam.html) | No new window; blocked event |
| N3 | Link to `http://localhost:4000/other` | Allowed |
| N4 | `allowedDomains` += `*.example.com`, new session | Subdomain allowed, apex blocked |
| N5 | `Content-Disposition: attachment` link | `DownloadStarting` cancelled; `allowDownloads=false` |
| N6 | `allowClipboard=false`: copy in page, paste into release-code field | Empty / blocked; note behaviour |
| N7 | iframe from another origin posts to `chrome.webview` | Dropped; `WEB_EVENT` medium with source |
| N8 | Bridge fuzz: unknown `type`, 1 MB payload, non-JSON string | Ignored, `WEB_EVENT`, no crash |

## 7. External display (fallback and Assigned Access)

Run each with `blockExternalDisplay: true`; attach a monitor (or Miracast) after lockdown.

| `externalDisplayAction` | Attach (extend) | Attach (duplicate, Win+P by mouse in Settings pre-opened) | Detach |
|---|---|---|---|
| `WARN` | Warning overlay + `EXTERNAL_DISPLAY`, `DISPLAY_CHANGED`; new cover window appears | Same events; note that content is mirrored (known limitation) | Overlay clears; `DISPLAY_CHANGED` |
| `BLOCK_START` | Behaves as WARN during exam (verify) | — | — |
| `PAUSE` | Content hidden "Disconnect the display"; timer continues; heartbeat `warning` | Same | Content restored |
| `TERMINATE` | Session ends; Exit screen with reason; `SESSION_END` | Same | — |
| `FLAG` | No UI change; `severity: high`; admin risk rises | Same | Event only |

Also: change resolution/scale only → `DISPLAY_CHANGED` without action; window re-fits the primary
monitor (`WINDOW_RESIZED`).

## 8. Focus loss and app deactivation (fallback)

| ID | Steps | Expected |
|---|---|---|
| A1 | From another machine: `Invoke-Command`/`psexec -i` `notepad.exe` into the session | Notepad launches behind/briefly in front; exam window re-activates within ~1 s; `APP_DEACTIVATED`; `PROCESS_DETECTED` not raised (not blacklisted); Warning per policy |
| A2 | Click on the secondary monitor's cover window | Focus returns; no event spam (cover is ours) |
| A3 | Toast notification arrives (send with `New-BurntToastNotification` from a remote session) | Toast may show over the window; recorded as limitation |
| A4 | Rapid focus-steal loop script | Events throttled; app responsive |
| A5 | Start `TeamViewer.exe` / `AnyDesk.exe` pre-lock | `PROCESS_DETECTED {action}`; policy action applied |
| A6 | Fast User Switching (Ctrl+Alt+Del → Switch user), sign in as another user, switch back | Session still locked on return; `APP_DEACTIVATED`/`SESSION_END_BLOCKED` (verify which); heartbeats continued |

## 9. Network offline / online

| ID | Steps | Expected |
|---|---|---|
| W1 | Disable Wi-Fi mid-exam | Red dot; `NETWORK_OFFLINE`; page `SESSION_STATUS.online=false`; still locked |
| W2 | Re-enable | `NETWORK_ONLINE`; queued events/heartbeats flush; `remainingSeconds` re-synced |
| W3 | Stop mock backend 60 s, restart | Backoff retries; no release; session resumes |
| W4 | Offline when release code entered | "No connection"; code retained; retry works |

## 10. Heartbeat commands (admin console)

| ID | Action | Expected |
|---|---|---|
| H1 | Warn | ≤ 10 s: Warning overlay + page `WARNING`; no state change |
| H2 | Release | Lockdown ends; Exit screen; `UNLOCK_COMPLETED` + `SESSION_END`; `authorizationId` recorded; delivered once |
| H3 | Terminate | Exit screen with reason; `SESSION_END`; page cannot continue |
| H4 | Replay a captured RELEASE via proxy | Ignored (same `authorizationId`) or rejected (expired) |
| H5 | Change heartbeat interval | New sessions use it |

## 11. Release code

| ID | Steps | Expected |
|---|---|---|
| R1 | Admin → Release code; type within 60 s | 200 → released; Exit screen |
| R2 | Wait > 60 s | 403 `RELEASE_CODE_EXPIRED` → `EXIT_DENIED`, `UNLOCK_DENIED`; still locked |
| R3 | Reuse a code | 403 `RELEASE_CODE_USED` |
| R4 | Random digits ×5 | 403 `INVALID_RELEASE_CODE`; field clears; rate limit behaviour noted |
| R5 | Code for session A entered on B | Invalid |
| R6 | `allowStudentReleaseCode: false` | No code field; only remote release |
| R7 | Page sends `REQUEST_EXIT` | Exit overlay; NOT released |
| R8 | While overlay is up, press Alt+F4 / Esc / Win | Overlay stays; only digits/Backspace/Enter reach the field |

## 12. Timer, submit, close/shutdown

| ID | Steps | Expected |
|---|---|---|
| T1 | Exam `DEMO`, wait 5 min | Auto `POST /submit`; next heartbeat RELEASE; Exit screen |
| T2 | Submit in page | `SUBMIT_COMPLETE` → `EXAM_SUBMITTED`; Exit overlay; auto RELEASE |
| T3 | Alt+F4 / `Window.Close()` via UI automation while locked | Blocked; `BLOCKED_SHORTCUT`; window stays |
| T4 | Start → Power → Shut down (from another session) or `shutdown /s /t 0` **without** `/f` | `WM_QUERYENDSESSION` refused; Windows shows "Avaibe Exam is blocking shutdown: exam in progress"; `SESSION_END_BLOCKED`; *Shut down anyway* still works (document) |
| T5 | Sign out via Ctrl+Alt+Del | Same refusal path; recorded |
| T6 | Quit from final Exit screen | Clean exit; final heartbeat `unlocked` sent; affinity/hook gone (verify with a screenshot after) |
| T7 | `taskkill /f /im AvaibeExam.exe` from another session while locked | Process dies; desktop usable immediately (no residue); relaunch shows Login; server shows missed heartbeats |
| T8 | Crash injection (throw in heartbeat handler, debug build) | Unhandled-exception handler releases controls, logs, exits; no stuck hook |

## 13. Recovering a machine that appears stuck

Fallback mode hides the taskbar behind a topmost window and swallows most shortcuts, but by design
the OS retains control:

1. **Wait for the watchdog** *(verify in `Lockdown/`)* — release after prolonged loss of server
   contact, if implemented.
2. **Remote release**: admin console → Release/Terminate; effective on the next heartbeat.
3. **Ctrl+Alt+Del → Task Manager → End task `AvaibeExam.exe`** — always available in fallback mode
   (the secure attention sequence cannot be hooked). Under Assigned Access, Task Manager is removed
   from the security screen but **Sign out** returns to the login screen and the kiosk app relaunches
   on next sign-in; use the admin account.
4. **Remote kill**: from another machine `Stop-Process -Name AvaibeExam -Force` via PowerShell
   Remoting / `psexec \\pc -s taskkill /f /im AvaibeExam.exe` (test PCs only; RDP into the locked
   session fails preflight by design).
5. **Hard power-off**: hold the power button. On boot nothing auto-launches in fallback mode; under
   Shell Launcher / Assigned Access the kiosk account auto-logs on and the app restarts to Login.
6. **Remove kiosk config** (POC PC): sign in as admin, `Clear-AssignedAccess` (single-app) or set
   `MDM_AssignedAccess.Configuration = $null` as SYSTEM; unassign the Intune profile.
7. Attach `%LOCALAPPDATA%\AvaibeExam\logs\*.log` and *Applications and Services Logs → Microsoft →
   Windows → AssignedAccess → Operational* to any bug report.

Never ship a build with a hidden unlock hotkey or embedded exit password.

## 14. Windows editions POC matrix

Run E1–E3, §4, §5, T3–T5 and §13 on each cell; record edition, build (`winver`), and whether the
app launched, stayed foreground, and released cleanly. Editions per Microsoft:
Assigned Access — Pro, Enterprise, Education, IoT Enterprise
([overview](https://learn.microsoft.com/en-us/windows/configuration/assigned-access/));
Shell Launcher and Keyboard Filter — Enterprise, Education, IoT Enterprise, not Pro
([Shell Launcher](https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/),
[Keyboard Filter](https://learn.microsoft.com/en-us/windows/configuration/keyboard-filter/)).

| Edition | Single-app kiosk (`KioskModeApp`) | Restricted user experience (`AllAppList` + AutoLaunch) | Shell Launcher v2 | Keyboard Filter |
|---|---|---|---|---|
| Windows 11 Pro | UWP/Edge only by design; test `v4:ClassicAppPath` with `AvaibeExam.exe` — expect app above lock screen behaviour to be unsupported for WPF+WebView2 (*record result*) | Supported — **primary school target on Pro** | Not available | Not available → Ctrl+Alt+Del/Alt+Tab/Alt+F4 remain best-effort |
| Windows 11 Education | Same as Pro | Supported | Supported — test `RestartShell` after release and `RestartDevice` on code 10 | Supported — block Ctrl+Alt+Del, Win+L, Alt+Tab, Alt+F4; verify breakout key for invigilators |
| Windows 11 Enterprise | Same as Pro | Supported | Supported | Supported |
| Windows 10 22H2 Pro (legacy) | Same; `StartLayout` XML instead of `StartPins`; Intune multi-app template available | Supported | Not available | Not available |
| Windows 11 Home (BYOD only) | N/A | N/A | N/A | N/A — `kiosk-fallback` only |

Additional POC questions to answer and record in this file's results table:

* Does `AvaibeExam.exe` start reliably as the `AutoLaunch` app when WebView2 initialises before the
  desktop is fully ready (add a retry on `EnsureCoreWebView2Async`)?
* With Assigned Access, is `HKCU\SOFTWARE\Microsoft\Windows\AssignedAccessConfiguration` present for
  the kiosk user (detector → `assigned-access`)? Same for Shell Launcher (which key/WMI value?).
* Does `WDA_EXCLUDEFROMCAPTURE` still work when the app is the Shell Launcher shell (DWM composing)?
* Does Keyboard Filter's Ctrl+Alt+Del block conflict with the Assigned Access breakout sequence?
* Intune: does the Win32 app install on the kiosk device before the Assigned Access profile applies
  (dependency ordering), and does supersedence upgrade while the kiosk account is signed in?

## 15. Regression checklist per build

§0 build clean, L1, P1, E1, five shortcuts from §4, C1, N1, W1/W2, H1–H3, R1–R2, T2, T4, T6, T7.
