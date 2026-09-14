# Lockdown Browser — Shared Contract (macOS client ⇄ mock backend ⇄ exam web page)

This file is the single source of truth for the interfaces between the three parts of the
project. All agents/developers must code against it. Version: 1.0 (2026-09-12).

Product name in UI: **Avaibe Exam** (bundle id `com.avaibe.exam.mac`). Client version string: `0.1.0`.

## 1. Directory layout

```
Lockdown Browser/
  README.md                       # top-level: what this is, how to run everything
  docs/                           # CONTRACT.md (this), ARCHITECTURE.md, AAC-ENTITLEMENT.md,
                                  # LIMITATIONS.md, DEPLOYMENT.md, TEST-PLAN.md
  macos/                          # Swift package producing AvaibeExam.app
    Package.swift
    Sources/AvaibeExam/...
    Resources/Info.plist
    Resources/AvaibeExam.entitlements
    scripts/build-app.sh          # swift build + assemble .app + ad-hoc codesign
    scripts/run.sh                # build then open the .app
    project.yml                   # XcodeGen spec (for when Xcode is installed)
  mock-backend/                   # Node.js (no build step) mock of the real platform
    package.json
    server.js
    public/exam.html              # mock exam UI loaded inside the WKWebView
    public/admin.html             # mock teacher console (release codes, terminate, events)
```

## 2. Base URL and auth

* Default backend base URL: `http://localhost:4000` (client setting, editable on the login screen).
* All JSON. Requests after session start carry `Authorization: Bearer <sessionToken>`.
* Device requests carry `X-Device-Id: <deviceId>` and `X-Client-Version: 0.1.0`.
* Errors: HTTP 4xx/5xx with body `{ "error": { "code": "STRING_CODE", "message": "human text" } }`.

## 3. REST endpoints (mock backend implements all of these)

### POST /api/v1/devices/enroll
Request:
```json
{ "enrollmentToken": "SCHOOL-DEMO",        // any token accepted by the mock; "SCHOOL-*" => school mode, else byod
  "platform": "macos", "osVersion": "26.6.2", "clientVersion": "0.1.0",
  "hardwareId": "<IOPlatformUUID or generated UUID>", "deviceName": "Anoop's MacBook" }
```
Response 200:
```json
{ "deviceId": "dev_ab12", "deviceToken": "devtok_...", "mode": "school" | "byod", "minClientVersion": "0.1.0" }
```

### POST /api/v1/sessions   (start exam session)
Request:
```json
{ "studentCode": "1025", "examCode": "MATH101", "deviceId": "dev_ab12",
  "preflight": { "osVersion": "26.6.2", "sipEnabled": true, "mdmEnrolled": false,
                 "accountType": "admin"|"standard", "displayCount": 1, "screenSharingActive": false,
                 "aacEntitlementPresent": false, "cameraAuthorized": "authorized"|"denied"|"notDetermined"|"restricted",
                 "microphoneAuthorized": "authorized"|"denied"|"notDetermined"|"restricted",
                 "internetReachable": true, "clientVersion": "0.1.0" } }
```
Mock accepts any studentCode; examCode must be one of `MATH101`, `SCI202`, `DEMO` (else 404 EXAM_NOT_FOUND).
Response 200:
```json
{ "sessionId": "sess_9f3a", "sessionToken": "sesstok_...", "expiresAt": "2026-09-12T12:00:00Z",
  "student": { "id": "1025", "name": "Demo Student" },
  "exam": { "code": "MATH101", "title": "Mathematics 101", "durationMinutes": 60 },
  "examUrl": "http://localhost:4000/exam/sess_9f3a",
  "policy": { ...see §4... } }
```
Response 403 with code `PREFLIGHT_FAILED` and `"failures": ["SIP_DISABLED", "EXTERNAL_DISPLAY"]` when policy blocks start.

### POST /api/v1/sessions/:id/heartbeat   (every policy.heartbeatIntervalSeconds)
Request: `{ "status": "locked"|"unlocked"|"warning", "displayCount": 1, "lockdownMode": "aac"|"kiosk-fallback"|"none", "uptimeSeconds": 123 }`
Response 200:
```json
{ "ok": true, "serverTime": "...", "remainingSeconds": 3480,
  "command": null | { "type": "RELEASE", "authorizationId": "auth_...", "expiresAt": "...", "reason": "Teacher release" }
                 | { "type": "TERMINATE", "reason": "..." }
                 | { "type": "WARN", "message": "..." } }
```
A `RELEASE` command is delivered exactly once; the client must end lockdown and show the Exit screen.

### POST /api/v1/sessions/:id/events   (batched telemetry)
Request: `{ "events": [ { "type": "FULLSCREEN_EXIT", "severity": "low"|"medium"|"high"|"info", "timestamp": "ISO8601", "metadata": {...} } ] }`
Response 200: `{ "accepted": 3 }`
Event types used by the client (string constants): `SESSION_START`, `LOCKDOWN_ENGAGED`, `LOCKDOWN_FALLBACK`, `LOCKDOWN_FAILED`,
`LOCKDOWN_INTERRUPTED`, `DISPLAY_CHANGED`, `EXTERNAL_DISPLAY`, `SCREEN_SHARING_DETECTED`, `BLOCKED_NAVIGATION`, `BLOCKED_SHORTCUT`,
`APP_DEACTIVATED`, `WINDOW_RESIZED`, `NETWORK_OFFLINE`, `NETWORK_ONLINE`, `UNLOCK_REQUESTED`, `UNLOCK_DENIED`, `UNLOCK_COMPLETED`,
`EXAM_SUBMITTED`, `SESSION_END`, `WEB_EVENT` (forwarded from the exam page).

### POST /api/v1/sessions/:id/submit
Request: `{}` → Response `{ "ok": true, "submittedAt": "..." }`. After submit the backend auto-issues a RELEASE on the next heartbeat.

### POST /api/v1/sessions/:id/unlock   (student-entered release code path)
Request: `{ "releaseCode": "482913", "deviceId": "dev_ab12" }`
Response 200: `{ "authorized": true, "authorizationId": "auth_...", "expiresAt": "..." }`
Response 403: `{ "error": { "code": "INVALID_RELEASE_CODE" | "RELEASE_CODE_EXPIRED" | "RELEASE_CODE_USED", "message": "..." } }`
Release codes: 6 digits, single-use, expire 60 s after creation, bound to sessionId.

### Mock admin endpoints (no auth in the mock; real system uses staff RBAC)
* `GET  /api/v1/admin/sessions` → `[ { sessionId, studentId, studentName, examCode, status, lastHeartbeat, lockdownMode, displayCount, eventCount, riskLevel } ]`
* `POST /api/v1/admin/sessions/:id/release-code` → `{ "releaseCode": "482913", "expiresAt": "..." }`
* `POST /api/v1/admin/sessions/:id/release` → queues a `RELEASE` heartbeat command → `{ "ok": true }`
* `POST /api/v1/admin/sessions/:id/terminate` → queues `TERMINATE` → `{ "ok": true }`
* `POST /api/v1/admin/sessions/:id/warn` `{ "message": "..." }` → queues `WARN`
* `GET  /api/v1/admin/sessions/:id/events` → `[ event... ]` (newest last)
* `GET  /api/v1/admin/policy` / `PUT /api/v1/admin/policy` → get/update the mock's default policy (see §4)
* `GET  /admin` serves `public/admin.html`; `GET /exam/:sessionId` serves `public/exam.html` (any sessionId).
* `GET  /healthz` → `{ "ok": true, "version": "0.1.0-mock" }`

## 4. Policy object (server → client)

```json
{
  "mode": "school" | "byod",
  "allowedDomains": ["localhost", "127.0.0.1"],       // exact host or "*.example.com"; default deny everything else
  "allowedApps": [ { "bundleId": "com.apple.calculator", "teamId": "" } ],   // AEAssessmentApplication list; teamId "" = Apple-signed
  "blockExternalDisplay": true,                        // >1 display fails preflight and raises EXTERNAL_DISPLAY during exam
  "externalDisplayAction": "WARN" | "BLOCK_START" | "PAUSE" | "TERMINATE" | "FLAG",
  "requireSIP": true, "requireMDM": false, "requireStandardAccount": false,
  "requireAAC": false,                                 // true => refuse to start without a real AAC session (no fallback)
  "allowClipboard": false, "allowPrinting": false, "allowDownloads": false, "allowDevTools": false,
  "heartbeatIntervalSeconds": 10, "eventFlushIntervalSeconds": 5,
  "minClientVersion": "0.1.0",
  "allowStudentReleaseCode": true                       // show "Enter release code" on the native exit overlay
}
```

## 5. Web ⇄ native bridge (inside WKWebView)

Native registers a `WKScriptMessageHandler` named **`lockdown`**. The page sends:
```js
window.webkit.messageHandlers.lockdown.postMessage({ type: "READY" | "REPORT_EVENT" | "GET_SESSION_STATUS" | "REQUEST_EXIT" | "SUBMIT_COMPLETE" | "HEARTBEAT_PING",
                                                     id: "<optional correlation id>", payload: { ... } });
```
Native calls back into the page via `window.LockdownBridge.onMessage({ type, id, payload })`. The page must define
`window.LockdownBridge = { onMessage(msg) {...} }` before sending `READY`. Native also injects, at document start,
`window.LockdownNative = { platform: "macos", clientVersion: "0.1.0", lockdownMode: "aac"|"kiosk-fallback"|"none" }`.

Messages:
| Direction | type | payload |
|---|---|---|
| web→native | `READY` | `{}` — native replies `SESSION_STATUS` |
| web→native | `REPORT_EVENT` | `{ "type": "ANSWER_SAVED", "severity": "info", "metadata": {} }` — forwarded as `WEB_EVENT` telemetry |
| web→native | `GET_SESSION_STATUS` | `{}` — native replies `SESSION_STATUS` |
| web→native | `REQUEST_EXIT` | `{ "reason": "submitted" }` — native shows the native Exit overlay; **never** ends lockdown by itself |
| web→native | `SUBMIT_COMPLETE` | `{}` — native records `EXAM_SUBMITTED`, then shows the Exit overlay |
| native→web | `SESSION_STATUS` | `{ "sessionId", "studentName", "examTitle", "lockdownMode", "remainingSeconds", "displayCount", "online" }` |
| native→web | `WARNING` | `{ "message" }` (from a `WARN` command or a local security event) |
| native→web | `EXIT_DENIED` | `{ "reason" }` |
| native→web | `LOCKDOWN_STATE` | `{ "lockdownMode", "engaged": true/false }` |

Security rule: the bridge never exposes a way for JavaScript to end the assessment session, change policy, or
read device secrets. Only `REQUEST_EXIT` → native overlay → server-validated release code / RELEASE command ends lockdown.

## 6. Lockdown modes (client)

* `aac` — a real `AEAssessmentSession` began successfully (requires the restricted entitlement
  `com.apple.developer.automatic-assessment-configuration` granted by Apple and a Developer ID / development signing).
* `kiosk-fallback` — AAC unavailable (no entitlement, ad-hoc signed build, or session failed to begin). The client applies
  best-effort controls: fullscreen borderless window at a high window level on every screen, `NSApplication.presentationOptions`
  (hide Dock/menu bar, disable process switching, force quit, session termination, hide, Apple menu), local key-event monitor
  blocking Cmd+Q/Cmd+W/Cmd+H/Cmd+M/Cmd+Tab/Cmd+Space/Cmd+Shift+3/4/5/Ctrl+Cmd+Q/F11 etc., activation-loss detection
  (re-activates and raises `APP_DEACTIVATED`), display-change detection. This mode is honestly reported to the server and student.
* `none` — not locked (login/preflight/exit screens).

Policy `requireAAC: true` means the client must refuse to start in `kiosk-fallback`.

## 7. Client screens (SwiftUI)

1. **Login** — backend URL, enrollment token (first run only), student code, exam code, [Continue].
2. **Preflight** — checklist (macOS version, SIP, MDM, account type, displays, screen sharing, AAC entitlement, camera, mic, internet,
   client version) each ✓ / ✗ / N/A with policy-driven required/optional; [Start Exam] enabled only when all required pass.
3. **Exam** — WKWebView filling the screen + thin native status strip (student, exam, remaining time, lockdown mode badge, online dot).
4. **Warning** overlay — shown on security events (external display, deactivation, etc.) with policy action.
5. **Exit** overlay — "Exam locked. Ask your teacher for a release code." + 6-digit code field (if policy allows) + status; on
   success ends lockdown and shows the final **Exit** screen ("Lockdown released. You may close this app.") with a Quit button.

## 8. Demo credentials for the mock
* Enrollment token: `SCHOOL-DEMO` (school mode) or `BYOD-DEMO` (byod mode)
* Student code: any (e.g. `1025`); Exam codes: `MATH101` (60 min), `SCI202` (45 min), `DEMO` (5 min)
* Admin console: http://localhost:4000/admin

---

## 9. Windows client addendum (v1.1, 2026-09-12)

The Windows client (`windows/`, C# / .NET 8 / WPF / WebView2, product name **Avaibe Exam**, executable
`AvaibeExam.exe`) implements the same §2–§8 contract. Differences and additions:

### 9.1 Identity
* `platform: "windows"` in enroll; `X-Client-Version: 0.1.0` unchanged; `hardwareId` = the machine GUID from
  `HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid` (fallback: generated GUID persisted per user).
* Enrollment (deviceId/deviceToken) is stored per user with DPAPI (`ProtectedData`, CurrentUser scope) under
  `%LOCALAPPDATA%\AvaibeExam\enrollment.dat`; settings (base URL, last codes) in `%LOCALAPPDATA%\AvaibeExam\settings.json`.

### 9.2 Preflight report (same object; Windows fills the fields as follows)
| field | Windows meaning |
|---|---|
| `osVersion` | e.g. `"10.0.22631"` (Windows 11 build) plus `"edition"` in `preflightExtras` |
| `sipEnabled` | **Secure Boot enabled** (`Confirm-SecureBootUEFI` equivalent via registry `HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State\UEFISecureBootEnabled`) |
| `mdmEnrolled` | MDM/Intune enrollment present (`HKLM\SOFTWARE\Microsoft\Enrollments\*` with a `ProviderID`) or domain/AAD joined |
| `accountType` | `"admin"` if the process user is in BUILTIN\Administrators (`WindowsPrincipal.IsInRole`), else `"standard"` |
| `displayCount` | `System.Windows.Forms.Screen.AllScreens.Length` (or `EnumDisplayMonitors`) |
| `screenSharingActive` | any known remote-control / capture process running (mstsc session `SESSIONNAME` starts with `RDP-`, `TeamViewer`, `AnyDesk`, `obs64`, `obs32`, `Zoom` share, `msrdc`, `vncserver`, `chrome_remote_desktop`) |
| `aacEntitlementPresent` | **Assigned Access / kiosk session detected**: the process runs under an Assigned Access shell (`HKCU\...\AssignedAccessConfiguration` or the `ShellLauncher` / `AssignedAccess` WMI class reports an active config). This is the Windows analogue of AAC. |
| `cameraAuthorized` / `microphoneAuthorized` | Windows privacy settings `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{webcam,microphone}\Value` (`Allow` → `authorized`, `Deny` → `denied`, missing → `notDetermined`) |
| `internetReachable` | `GET /healthz` succeeds |
Additional Windows-only object `preflightExtras` (server ignores unknown fields):
`{ "edition": "Professional", "secureBoot": true, "tpmPresent": true, "virtualMachine": false, "assignedAccess": false, "rdpSession": false, "screenCaptureProtection": true }`.

### 9.3 Lockdown modes (`lockdownMode` in heartbeats)
* `"assigned-access"` — the client detected it is running inside a Windows Assigned Access / Shell Launcher kiosk
  session (school-managed mode). The OS enforces the single-app shell; the client still applies the fallback controls.
* `"kiosk-fallback"` — best-effort controls applied by the client: fullscreen borderless topmost window on the primary monitor,
  black cover windows on other monitors, low-level keyboard hook (`WH_KEYBOARD_LL`) swallowing Alt+Tab, Alt+Esc, Alt+F4, Win key
  combos, Ctrl+Esc, Ctrl+Shift+Esc, PrintScreen, Alt+PrintScreen, Win+Shift+S, Win+G, Win+D, Win+L, Win+R, F11, Ctrl+P/C/X/V
  (policy dependent) — note Ctrl+Alt+Del **cannot** be blocked; `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` so screenshots
  and screen recorders capture black; focus-loss watchdog that re-activates the window and raises `APP_DEACTIVATED`;
  blacklisted-process monitor (`PROCESS_DETECTED` event, see 9.5); clipboard cleared on engage and on focus return;
  `WM_QUERYENDSESSION` refused while locked (`ShutdownBlockReasonCreate`); WebView2 restrictions (no context menu, no dev tools,
  no downloads, no new windows, no zoom, no print, no drag-drop, status bar off, autofill off, PDF viewer off).
* `"none"` — not locked.
`policy.requireAAC: true` on Windows means "require Assigned Access" (refuse to start in `kiosk-fallback`).

### 9.4 Web ⇄ native bridge on Windows
WebView2 has no `window.webkit`. The client injects, at document start, a **shim** so the unchanged `public/exam.html` works:
```js
window.webkit = window.webkit || {};
window.webkit.messageHandlers = window.webkit.messageHandlers || {};
window.webkit.messageHandlers.lockdown = { postMessage: (m) => window.chrome.webview.postMessage(m) };
window.LockdownNative = Object.freeze({ platform: "windows", clientVersion: "0.1.0", lockdownMode: "<mode>" });
```
Native receives via `CoreWebView2.WebMessageReceived` (`WebMessageAsJson`), and sends via
`ExecuteScriptAsync("window.LockdownBridge && window.LockdownBridge.onMessage(<json>)")`. Message types are identical to §5.

### 9.5 Additional event types (Windows)
`PROCESS_DETECTED` (medium; metadata `{ "process": "obs64", "action": "flag"|"warn" }`), `RDP_SESSION` (high),
`SCREEN_CAPTURE_PROTECTION_UNAVAILABLE` (low), `SESSION_END_BLOCKED` (medium; user tried to log off / shut down),
`VIRTUAL_MACHINE_DETECTED` (medium, preflight only). The mock backend accepts any event type string.

### 9.6 Screens, demo credentials, release flow
Identical to §7 and §8. The native Exit overlay with the 6-digit release code is a WPF overlay, not web content.
