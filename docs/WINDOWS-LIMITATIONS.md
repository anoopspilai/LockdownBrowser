# Windows Lockdown Limitations — What Each Mode Can and Cannot Enforce

Rule from the product spec: **never claim impossible prevention.** Every control below is rated
honestly per mode, and the client reports its mode (`assigned-access`, `kiosk-fallback`) to the
server and the student so teachers know which column applies. Companion to
[LIMITATIONS.md](LIMITATIONS.md) (macOS); Microsoft facts are linked to Microsoft Learn inline and
unverified statements are marked *(verify)*.

## 1. Rating legend

| Rating | Meaning |
|---|---|
| **Enforced** | The OS prevents it for the kiosk account while the Assigned Access / Shell Launcher configuration is applied. Bypass requires administrator rights on the device or defeating Windows itself. |
| **Best-effort** | The client blocks the common path (topmost window, `WH_KEYBOARD_LL`, display affinity, WebView2 settings) but another process, an OS quirk, or a determined user can get around it. |
| **Detect-only** | The client cannot stop it but notices and raises a telemetry event; policy decides the action. |
| **Not possible** | Neither prevented nor reliably detected by software on the PC. Must be handled by invigilation, room rules or IT policy. |

Key Microsoft sources for the school column:

* Assigned Access restricted user experience applies a fixed set of policy settings and AppLocker
  rules and blocks a specific list of shortcuts (Ctrl+Shift+Esc, Win+A/D/E/G/I/Q/R/S/X and others)
  — but **not** Alt+F4, Alt+Tab, Alt+Shift+Tab or Ctrl+Alt+Del
  ([policy settings](https://learn.microsoft.com/en-us/windows/configuration/assigned-access/policy-settings),
  [recommendations](https://learn.microsoft.com/en-us/windows/configuration/assigned-access/recommendations#keyboard-shortcuts)).
* Shell Launcher only replaces the shell; it "doesn't prevent a user from accessing other desktop
  applications and system components" unless paired with AppLocker/GPO/CSP
  ([Shell Launcher overview](https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/)).
* Keyboard Filter (Enterprise, Enterprise LTSC, Education, IoT Enterprise — not Pro) is the only
  supported way to suppress Ctrl+Alt+Del and Win+L at the OS level
  ([Keyboard Filter](https://learn.microsoft.com/en-us/windows/configuration/keyboard-filter/)).
* The kiosk profile does not load for members of the local Administrators group; restricted user
  experience is for standard users only
  ([configuration file — Configs](https://learn.microsoft.com/en-us/windows/configuration/assigned-access/configuration-file#configs)).

## 2. Control matrix

Columns: **School** = `assigned-access` on a Windows Pro/Education/Enterprise lab PC configured per
WINDOWS-DEPLOYMENT.md §2 (restricted user experience or Shell Launcher, standard kiosk account,
Intune policies applied); **Fallback** = `kiosk-fallback` on a school PC with a standard account but
no kiosk configuration; **BYOD** = `kiosk-fallback` on a student-owned PC, usually an admin account,
no management. "+KF" means the rating improves to Enforced when Keyboard Filter is configured
(Enterprise/Education only).

| Control | School | Fallback | BYOD | Notes |
|---|---|---|---|---|
| Taskbar / Start menu | Enforced | Best-effort | Best-effort | Assigned Access: `ShowTaskbar="false"`, Start restricted to allowed apps; Shell Launcher: no Explorer shell at all. Fallback: topmost window covers the taskbar; Win key / Ctrl+Esc swallowed by the LL hook. |
| Alt+Tab / Task View (Win+Tab) | Best-effort (+KF) | Best-effort | Best-effort | Assigned Access does not block Alt+Tab; the LL hook swallows Alt+Tab, Alt+Esc, Win+Tab. Task View button hidden by Assigned Access GPO. Touch three/four-finger swipe gestures are *not* keyboard events → detect via `APP_DEACTIVATED`. |
| Win-key shortcuts (Win+D/E/R/I/S/X/A/G …) | Enforced (list in policy-settings) | Best-effort | Best-effort | LL hook swallows any `VK_LWIN`/`VK_RWIN` chord. Win+L and Ctrl+Alt+Del: see below. |
| Alt+F4 (close window) | Best-effort (+KF) | Best-effort | Best-effort | Not blocked by Assigned Access. LL hook swallows Alt+F4; `Window.Closing` cancels while locked; a `taskkill /f` from another session is not stoppable. In Assigned Access kiosk profile the app auto-restarts if closed; in Shell Launcher the exit action applies. |
| Ctrl+Alt+Del → Task Manager / Sign out / Lock | Detect-only (+KF → Enforced sequence; Task Manager/Log off/Change password removed from the security screen by Assigned Access GPO) | Detect-only | Not possible | Ctrl+Alt+Del is the secure attention sequence handled by Winlogon before any user-mode hook; `WH_KEYBOARD_LL` never sees it *(verify: Learn page describing SAS/Winlogon handling)*. It is also the default Assigned Access breakout sequence (configurable with `v4:BreakoutSequence`). Client records `APP_DEACTIVATED` on return; Task Manager as a *process* (`taskmgr.exe`) is reported by `ProcessMonitor` where it runs in the same session. |
| Win+L (lock) | Enforced with `DisableLockWorkstation` policy (+KF) | Best-effort | Best-effort | LL hook swallows Win+L, but the OS processes the lock before some hooks on some builds *(verify on target builds)*; GPO/CSP *Remove Lock Computer* (`ADMX_CtrlAltDel/DisableLockWorkstation`) is authoritative ([ADMX_CtrlAltDel CSP](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-ctrlaltdel)). Lock also happens via Ctrl+Alt+Del screen. |
| PrintScreen / Alt+PrintScreen / Win+Shift+S (Snipping Tool) | Enforced (affinity) + hook | Enforced (affinity) + hook | Enforced (affinity) + hook | `WDA_EXCLUDEFROMCAPTURE` removes the window from OS capture on Windows 10 2004+; older builds show black (`WDA_MONITOR` behaviour) ([SetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)). Snipping Tool is also outside the AppLocker allow-list in School. Microsoft explicitly says affinity is *not* a DRM/security guarantee — treat as strong, not absolute. |
| Screen recording (Xbox Game Bar Win+G, OBS, Teams/Zoom share, Windows.Graphics.Capture consumers) | Enforced (affinity) + Detect | Enforced (affinity) + Detect | Enforced (affinity) + Detect | Capture APIs (GDI `BitBlt`, DXGI Desktop Duplication, Windows.Graphics.Capture) honour display affinity; output shows black/omitted window. School: Game DVR disabled via `ApplicationManagement/AllowGameDVR` *(verify CSP path)* and OBS not allow-listed. `ProcessMonitor` raises `PROCESS_DETECTED` for `obs64`, `obs32`, Zoom/Teams share helpers. Kernel-mode or driver-level capture (some remote tools, virtual display drivers) bypasses affinity → Detect-only at best. |
| Remote desktop (RDP `mstsc`, TeamViewer, AnyDesk, Chrome Remote Desktop, VNC) | Detect + Enforced by policy | Detect-only | Detect-only | Preflight `rdpSession` (`SESSIONNAME` starts with `RDP-`) fails start; runtime `RDP_SESSION` (high). Assigned Access kiosk experience is unsupported over Remote Desktop by design. Third-party tools show the affinity-excluded window as black on the viewer for GDI/DXGI-based capture but not for driver-based mirroring *(verify per vendor)*. School: RDP disabled (`RemoteDesktopServices/AllowUsersToConnectRemotely = 0`) and tools not allow-listed. |
| Notifications / Focus assist | Enforced | Best-effort | Not possible | Assigned Access applies *Turn off toast notifications* and removes Notification/Action Center. Fallback: toasts render above a topmost window; client enables no Focus mode (no supported API for third-party apps) *(verify)*. |
| External displays / projecting (Win+P) | Detect-only + covers | Detect-only + covers | Detect-only | `EnumDisplayMonitors` / `Screen.AllScreens` change → `DISPLAY_CHANGED`, `EXTERNAL_DISPLAY`, `externalDisplayAction`. Win+P swallowed by the hook but hardware hot-plug and Settings changes are not. Cover windows blank extended monitors; **duplicate** mode mirrors the exam by definition. |
| Clipboard | Best-effort | Best-effort | Best-effort | Cleared on engage/focus return; in-page Ctrl+C/X/V blocked when `allowClipboard=false` (hook + WebView2). Other processes read the clipboard freely; cloud clipboard (Win+V) history cleared by `Clipboard.Clear()` only for the current item *(verify history behaviour)*. School: disable clipboard history via `Privacy/AllowClipboardHistory`. |
| Printing | Enforced (no allowed print app) + WebView2 | Best-effort | Best-effort | `AreBrowserAcceleratorKeysEnabled=false` disables Ctrl+P; `window.print()` no-op via injected script; hook swallows Ctrl+P. Print-to-PDF from another app is impossible without a capture anyway. |
| Downloads | Best-effort (WebView2) | Best-effort | Best-effort | `DownloadStarting.Cancel = true`; File Explorer namespace restricted in Assigned Access (`FileExplorerNamespaceRestrictions`). Same in all modes. |
| Developer tools / F12 | Enforced (WebView2) | Enforced (WebView2) | Enforced (WebView2) | `AreDevToolsEnabled=false` is enforced by the runtime; remote debugging requires launching the runtime with `--remote-debugging-port`, which the client never sets. Rated Enforced because it is a property of the engine, not of the window. |
| New windows / popups / tabs | Best-effort | Best-effort | Best-effort | `NewWindowRequested` handled; off-domain → `BLOCKED_NAVIGATION`. `target=_blank` to an allowed host is loaded in the same view. |
| Navigation to non-allowed domains | Best-effort (+ network filter) | Best-effort | Best-effort | `NavigationStarting`/`FrameNavigationStarting` allow-list. Sub-resources (images/XHR) to other hosts are *not* blocked unless `WebResourceRequested` filtering is enabled *(verify in `Browser/`)*. School can add a firewall/DNS allow-list. |
| Other apps (already running or launched) | Enforced (AppLocker allow-list) | Best-effort | Best-effort | Assigned Access generates AppLocker rules allowing only the listed apps; a pre-launched allowed app can still be switched to (Alt+Tab, see above). Fallback: `ProcessMonitor` flags known names only; the topmost window hides but does not close other apps. |
| Virtual machines | Detect-only | Detect-only | Detect-only | Preflight `virtualMachine` via `Win32_ComputerSystem.Model/Manufacturer`, hypervisor CPUID bit, known VM drivers → `VIRTUAL_MACHINE_DETECTED`. Hidden-identifier VMs pass. Nested VM hosting a screen-shared exam is a classic bypass → invigilation. |
| Hardware capture (HDMI grabber, KVM, phone camera) | Not possible | Not possible | Not possible | Display affinity operates inside DWM; the HDMI signal contains the composed frame. Camera monitoring (roadmap) is the software mitigation. |
| Second user session / Fast User Switching | Enforced (auto-logon kiosk account, `HideFastUserSwitching`, Log off removed) | Detect-only | Not possible | Fallback: switching users leaves the locked session running; the client notices `WM_WTSSESSION_CHANGE` (`WTSRegisterSessionNotification`) → `APP_DEACTIVATED`/`SESSION_END_BLOCKED` *(verify implemented)*. |
| UAC prompts / secure desktop | Enforced (standard account cannot elevate; prompts for credentials) | Best-effort | Not possible | The secure desktop is a separate desktop: hooks, topmost windows and affinity do not apply there. A BYOD admin can elevate any tool from the Ctrl+Alt+Del path. Preflight `accountType` + `requireStandardAccount` is the mitigation. |
| Shutdown / restart / sign-out | Enforced (power options removed by policy; `Shutdown_AllowSystemToBeShutDownWithoutHavingToLogOn=0`) | Detect-only | Detect-only | `WM_QUERYENDSESSION` refused + `ShutdownBlockReasonCreate`; Windows still offers *Shut down anyway*; power button not blockable ([ShutdownBlockReasonCreate](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-shutdownblockreasoncreate)). `SESSION_END_BLOCKED` recorded. |
| Secure Boot off / test-signing / non-release OS | Preflight (`requireSIP` ⇒ Secure Boot) | Preflight | Preflight | Registry `UEFISecureBootEnabled`; server refuses via `PREFLIGHT_FAILED`. Does not detect a tampered kernel; it is a hygiene signal. |
| Network offline | Detect-only | Detect-only | Detect-only | `NETWORK_OFFLINE`/`NETWORK_ONLINE`; heartbeats and events queue; exam page must autosave. |
| Accessibility tools (Narrator, Magnifier, On-Screen Keyboard, Sticky Keys) | Best-effort (allow-list decides) | Best-effort | Best-effort | Win+U / Win+Plus swallowed; Sticky Keys can bypass hooks in some sequences per Keyboard Filter docs. Accommodation profiles must explicitly allow tools (spec §23) — do not treat as cheating by default. |

## 2a. Which OS mechanism gives which enforcement

The "School" column assumes one of the following is configured. They are not equivalent; pick per
edition and record the choice in the deployment record.

| Mechanism | Editions | What it enforces for the kiosk account | What it does *not* do | Fit for Avaibe Exam |
|---|---|---|---|---|
| Assigned Access **kiosk profile** (`KioskModeApp`) | Pro, Enterprise, Education, IoT Enterprise | Single UWP app or Microsoft Edge full screen *above the lock screen*; app auto-restarts if closed; Ctrl+Alt+Del (or a custom `BreakoutSequence`) is the only way out ([overview](https://learn.microsoft.com/en-us/windows/configuration/assigned-access/), [configuration file](https://learn.microsoft.com/en-us/windows/configuration/assigned-access/configuration-file)) | Designed for UWP/Edge; a Win32 app via `v4:ClassicAppPath` must behave as an above-lock app *(verify with WPF + WebView2 in the POC matrix)*; group accounts not supported | Not the primary path; test only |
| Assigned Access **restricted user experience** (`AllAppList`) | Pro, Enterprise, Education, IoT Enterprise | Desktop with allow-listed apps only (AppLocker rules generated), custom Start, taskbar hidden, File Explorer namespace locked, ~45 GPO/CSP settings (Task Manager/Run/Log off/Change password removed, toasts off, Task View hidden), listed Win shortcuts blocked ([policy settings](https://learn.microsoft.com/en-us/windows/configuration/assigned-access/policy-settings)) | Alt+Tab, Alt+F4, Ctrl+Alt+Del, Win+L remain; admins unaffected; a second allowed app can be switched to | **Primary school path** (works on Pro); client adds hook/affinity on top |
| **Shell Launcher v2** | Enterprise, Education, IoT Enterprise (not Pro) | Replaces `explorer.exe` with the app: no Start, taskbar, desktop or notification area exist; exit action on app exit (restart shell/device, shutdown, nothing) ([Shell Launcher](https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/)) | No app allow-list of its own ("doesn't prevent a user from accessing other desktop applications"); no shortcut blocking; runs with the user's rights | Strongest UX for Education/Enterprise labs when paired with AppLocker + Keyboard Filter |
| **Keyboard Filter** | Enterprise, Education, IoT Enterprise (not Pro) | Kernel-level suppression of any chord including Ctrl+Alt+Del, Win+L, Alt+Tab, Alt+F4; works for hardware and on-screen keyboards; admin exemption; breakout key ([Keyboard Filter](https://learn.microsoft.com/en-us/windows/configuration/keyboard-filter/)) | Bypassed in Safe Mode; cannot block the Sleep key; Sticky Keys can bypass in some sequences unless Ease of Access is force-disabled | Add-on that converts the remaining best-effort rows to Enforced |
| **Client fallback controls** (`kiosk-fallback`) | Any edition incl. Home | Topmost window + covers, `WH_KEYBOARD_LL`, display affinity, focus watchdog, process monitor, shutdown block, WebView2 hardening | Everything the OS reserves for itself (SAS, secure desktop, other processes' capture/clipboard, power button) | Always on; the only layer on BYOD |

Practical reading of the table: on **Pro**, the best achievable is restricted user experience +
client controls, and Ctrl+Alt+Del/Alt+Tab/Alt+F4 stay in the best-effort column no matter what the
client does. On **Education/Enterprise**, Shell Launcher (or restricted experience) + Keyboard Filter
+ client controls moves those rows to Enforced. Preflight reports `edition` in `preflightExtras` so
the admin console can show which tier a lab is on.

## 3. `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` explained

* **What it does.** Stores an affinity in kernel mode on the window. With `WDA_MONITOR` the window
  content is shown only on a monitor and appears black in captures; with `WDA_EXCLUDEFROMCAPTURE`
  (Windows 10 version 2004 and later) the window is omitted from the capture entirely — the
  screenshot shows whatever is behind it. On earlier versions the flag falls back to `WDA_MONITOR`
  ([SetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)).
* **Requirements.** Top-level window of the calling process; DWM composition active (always on
  Windows 8+). The client applies it to `MainWindow` and every `CoverWindow` after the HWND exists
  and re-applies after any `WindowStyle`/`AllowsTransparency` change (those recreate the HWND).
* **What it covers.** PrintScreen, Snipping Tool, Xbox Game Bar, `BitBlt`/`PrintWindow`, DXGI
  Desktop Duplication, `Windows.Graphics.Capture`, and screen-share in apps built on those APIs
  (Teams, Zoom, OBS "Display Capture") — the exam appears black or missing.
* **What it does not cover.** Photographs of the screen, HDMI/USB capture hardware, virtual display
  drivers that mirror the desktop below DWM, and any capture that runs when DWM is not composing.
  Microsoft: "there is no guarantee … will strictly protect windowed content".
* **Failure handling.** If the call returns `FALSE` (older OS, RDP session, unusual GPU stack) the
  client raises `SCREEN_CAPTURE_PROTECTION_UNAVAILABLE` (low), sets
  `preflightExtras.screenCaptureProtection=false`, and continues unless policy says otherwise.
  The Preflight screen shows the row as ✗ with "screenshots may not be blocked".
* **Side effects to test.** Some remote-support tools show a black rectangle (good); `WDA_MONITOR`
  fallback makes the whole screenshot black on the exam area, which students may report as a bug —
  the UI copy should say so.

## 4. Policy actions for detected events

`policy.externalDisplayAction` today; the same vocabulary is intended for every detect-only control
(`PROCESS_DETECTED`, `RDP_SESSION`, `APP_DEACTIVATED`, `SESSION_END_BLOCKED`).

| Action | Client behaviour | Server side | Typical use |
|---|---|---|---|
| `WARN` | Warning overlay with message; exam continues; `WARNING` sent to page | Event severity `medium` | BYOD, low-stakes |
| `BLOCK_START` | Preflight fails (`PREFLIGHT_FAILED` with e.g. `EXTERNAL_DISPLAY`, `RDP_SESSION`); during exam behaves like `WARN` | Refuses `POST /sessions` | Default for school labs |
| `PAUSE` | Exam content hidden behind overlay ("Disconnect the display / close OBS to continue"); timer keeps running; resumes when the condition clears | Event `high`, session `riskLevel` raised | Projector accidentally connected |
| `TERMINATE` | Same as remote `TERMINATE`: session ends, lockdown releases | Session status `terminated` | High-stakes, invigilated |
| `FLAG` | No UI change; event `severity: high` recorded for review | Incident created | Silent evidence collection |

## 5. Guidance for schools and for the UI copy

* Describe `kiosk-fallback` to students as "supervised mode": the app blocks the obvious paths and
  records everything else. Do not use the word "secure" without the Assigned Access badge.
* Preflight in BYOD mode must show the limitation report (which rows are best-effort) before Start
  Exam is enabled, and must state that Ctrl+Alt+Del and the power button always work.
* Prefer **Windows Education or Enterprise** for exam labs: Keyboard Filter and Shell Launcher are
  unavailable on Pro, which leaves Ctrl+Alt+Del/Alt+Tab/Alt+F4 in the best-effort column.
* Use a **standard, local kiosk account with auto-logon**; never run the exam under an administrator
  account (Assigned Access does not even apply to admins).
* Combine software controls with invigilation and room policy for anything rated Detect-only or
  Not possible (VMs, hardware capture, phones).
* Never disable Defender, SmartScreen, UAC or Secure Boot to make lockdown "stronger"; Assigned
  Access itself requires UAC enabled ([Assigned Access overview](https://learn.microsoft.com/en-us/windows/configuration/assigned-access/)).
* Re-validate this table on every Windows feature update and on each WebView2 runtime major; the
  hook list, affinity behaviour and Assigned Access shortcut list change over time.
