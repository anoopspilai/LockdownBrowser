# Avaibe Exam: Windows Lockdown Browser and Mock Exam API

A secure exam browser for Windows, plus a small mock server that plays the role of the exam
platform so you can run the whole flow on one computer.

When an exam starts, the Windows app takes over the screen and keeps the student inside the exam
page. It leaves only when a teacher releases it, or when the exam is submitted.

| Part | Folder | Status |
|---|---|---|
| Windows client (C#, .NET 8, WPF, WebView2) | [`windows/`](windows/README.md) | Code complete. **Not compiled yet.** See the note below. |
| Mock exam API, exam page and teacher console (Node.js) | [`mock-backend/`](mock-backend/README.md) | Working and tested |
| API contract shared by both | [`docs/CONTRACT.md`](docs/CONTRACT.md) | Source of truth |

> **Before the first build.** The Windows client was written without a Windows machine, and
> Windows interface code only compiles on Windows. Expect a few small compiler errors the first
> time. [`windows/SELF-REVIEW.md`](windows/SELF-REVIEW.md) lists the exact spots to check first,
> with a fix for each.

## How the pieces fit

```
   Teacher's browser                        Student's Windows PC
 ┌──────────────────────┐                ┌──────────────────────────────┐
 │  /admin              │                │  AvaibeExam.exe              │
 │  teacher console     │                │                              │
 └──────────┬───────────┘                │  Login → Preflight → Exam →  │
            │                            │  Exit                        │
            ▼                            │                              │
 ┌──────────────────────┐    HTTP        │  WebView2 shows the exam page│
 │  mock-backend        │◀──────────────▶│  Lockdown engine keeps the   │
 │  Node.js, port 4000  │  heartbeats,   │  student inside it           │
 │  sessions, events,   │  events,       │                              │
 │  release codes       │  commands      └──────────────────────────────┘
 └──────────────────────┘
```

1. The student signs in. The app checks the PC is ready: Windows version, number of monitors,
   screen-sharing tools, camera and microphone permission, internet.
2. The server approves the session and sends back the exam policy.
3. The app locks the screen and loads the exam page.
4. Every few seconds the app sends a heartbeat. The server can reply with a command: warn, release
   or terminate.
5. The teacher releases the student from the console, either remotely or with a one-time code.

## Quick start

You need two things running: the mock server, then the Windows app.

### 1. Start the mock server

Requires [Node.js](https://nodejs.org) 18 or newer. There is nothing to install with npm.

```bash
cd mock-backend
node server.js
```

Open the teacher console at http://localhost:4000/admin and leave the server running.

### 2. Build and run the Windows app

Requires:

- Windows 11, or Windows 10 version 2004 (build 19041) or newer
- The [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- The WebView2 Runtime, which is already part of Windows 11

In PowerShell, from the repository folder:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
cd windows
.\scripts\build.ps1
.\scripts\run.ps1
```

The first line lets the helper scripts run in this PowerShell window only.

### 3. Sign in with the demo values

| Field | Value |
|---|---|
| Backend URL | `http://localhost:4000` (already filled in) |
| Enrollment token | `SCHOOL-DEMO` for school mode, or `BYOD-DEMO` for a personal device |
| Student code | Any value, for example `1025` |
| Exam code | `DEMO` (5 minutes), `MATH101` (60 minutes) or `SCI202` (45 minutes) |

Press **Continue**, review the readiness checklist, then press **Start Exam**.

### 4. Release the student

In the teacher console, find the session and choose one:

- **Remote release** unlocks the app within a few seconds.
- **Release code** shows a 6-digit code that works once and expires after 60 seconds. The student
  types it on the app's exit screen.

## If the screen is locked and you are stuck

The app is designed to be hard to leave. These always work:

1. Release the student from the teacher console.
2. Wait. The `DEMO` exam ends and releases itself after 5 minutes.
3. Press <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Del</kbd>, open Task Manager, and end
   `AvaibeExam.exe`. Task Manager is deliberately left working.

For unattended testing, `.\scripts\run.ps1 -AutoRun -ExitAfter 120` signs in automatically and
force-closes the app after 120 seconds.

## Making files to hand out

| You want | Command | Output |
|---|---|---|
| A normal release build | `.\scripts\build.ps1` | `windows\publish\AvaibeExam.exe`. The PC needs the .NET 8 Desktop Runtime. |
| One portable file | `.\scripts\publish-standalone.ps1` | `windows\publish-standalone\AvaibeExam.exe`. Runs with no .NET installed. |
| A real installer | `.\scripts\make-installer.ps1` | `windows\installer\output\AvaibeExam-Setup-0.1.0.exe`. Needs [Inno Setup](https://jrsoftware.org/isdl.php). |

School IT can install silently with `AvaibeExam-Setup-0.1.0.exe /VERYSILENT /NORESTART`.

Unsigned apps trigger a Windows SmartScreen warning. To sign, pass a code-signing certificate:
`.\scripts\build.ps1 -Sign -CertThumbprint <thumbprint>`.

## What the lockdown can and cannot do

The app reports its real lockdown level to the server on every heartbeat, and never claims more
than Windows allows.

| Mode | When | What enforces it |
|---|---|---|
| `assigned-access` | The PC is set up by school IT in Windows kiosk mode | Windows itself, plus the app's own controls |
| `kiosk-fallback` | Any other PC, including students' own laptops | The app, on a best-effort basis |

In fallback mode the app:

- Covers the whole screen and the taskbar, and blacks out extra monitors.
- Blocks shortcuts such as Alt+Tab, Alt+F4, the Windows key, PrintScreen and Win+Shift+S.
- Asks Windows to hide its window from screenshots and most screen recorders.
- Brings itself back if the student switches away, and reports it.
- Flags remote-control and recording tools such as TeamViewer, AnyDesk and OBS. It never closes them.

No application can block <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Del</kbd>. Only Windows kiosk policy
set by IT can restrict it. Full details are in
[`docs/WINDOWS-LIMITATIONS.md`](docs/WINDOWS-LIMITATIONS.md).

## The mock API at a glance

All responses are JSON. Session endpoints need the `Authorization: Bearer <sessionToken>` header
returned when the session starts.

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/healthz` | Server health and version |
| `POST` | `/api/v1/devices/enroll` | Register a device |
| `POST` | `/api/v1/sessions` | Start an exam session, with the readiness report |
| `POST` | `/api/v1/sessions/:id/heartbeat` | Report status, receive a command |
| `POST` | `/api/v1/sessions/:id/events` | Send a batch of security events |
| `POST` | `/api/v1/sessions/:id/submit` | Submit the exam |
| `POST` | `/api/v1/sessions/:id/unlock` | Check a release code |
| `GET` | `/api/v1/admin/sessions` | List live sessions |
| `POST` | `/api/v1/admin/sessions/:id/release-code` | Create a one-time release code |
| `POST` | `/api/v1/admin/sessions/:id/release` | Release remotely |
| `POST` | `/api/v1/admin/sessions/:id/warn` | Show a warning to the student |
| `POST` | `/api/v1/admin/sessions/:id/terminate` | End the session |
| `GET` | `/api/v1/admin/sessions/:id/events` | Event timeline for one session |
| `GET` `PUT` | `/api/v1/admin/policy` | Read or change the exam policy |

The full request and response shapes are in [`docs/CONTRACT.md`](docs/CONTRACT.md). A curl
walkthrough is in [`mock-backend/README.md`](mock-backend/README.md).

> **The mock is for development only.** It keeps everything in memory, accepts any student code,
> and its admin routes have no authentication. Do not expose it to the internet.

## Documentation

| Document | Read it when |
|---|---|
| [`docs/GUIDE-WINDOWS.md`](docs/GUIDE-WINDOWS.md) | You have never built a Windows app. A slow, complete beginner walkthrough. |
| [`windows/README.md`](windows/README.md) | You want the client's settings, environment variables and file map. |
| [`windows/SELF-REVIEW.md`](windows/SELF-REVIEW.md) | The first build fails. |
| [`docs/WINDOWS-ARCHITECTURE.md`](docs/WINDOWS-ARCHITECTURE.md) | You want to understand how the client is structured. |
| [`docs/WINDOWS-LIMITATIONS.md`](docs/WINDOWS-LIMITATIONS.md) | You need to know exactly what can and cannot be blocked. |
| [`docs/WINDOWS-DEPLOYMENT.md`](docs/WINDOWS-DEPLOYMENT.md) | You are rolling out to school PCs with kiosk mode, Intune and code signing. |
| [`docs/WINDOWS-TEST-PLAN.md`](docs/WINDOWS-TEST-PLAN.md) | You are testing a build by hand. |
| [`docs/CONTRACT.md`](docs/CONTRACT.md) | You are changing the API or building a new client. |

## Repository layout

```
docs/            API contract and Windows documentation
mock-backend/    server.js, data/exams.js, public/exam.html, public/admin.html
windows/
  AvaibeExam/    App, Models, Networking, Lockdown, Security, Browser, Views, Util
  installer/     Inno Setup script
  scripts/       build, run, smoke test, standalone publish, installer
```
