# Avaibe Exam: Windows client

The exam lockdown browser for Windows. Built with C# 12, .NET 8, WPF and WebView2.

> The app compiles with no errors (Release and Debug, .NET 8 SDK) and its logic tests pass. It has not yet been run on a Windows PC, so test the screens there before a real exam.

For the complete step-by-step guide, including everything needed for production, read
[`HOW-TO-RUN-AND-DEPLOY.md`](HOW-TO-RUN-AND-DEPLOY.md).

## Protocol v2 (2026-09-14)

The client implements CONTRACT §10:

- **https only** (http is accepted for `localhost` only); IT can preset the server address in
  `HKLM\SOFTWARE\Avaibe\Exam\BaseUrl`, which makes the login field read-only.
- **Signed commands**: `RELEASE` / `TERMINATE` must carry an ECDSA P-256 signature from the key
  pinned at enrollment. Unsigned or wrong commands are ignored and reported (`COMMAND_REJECTED`).
- **Terminate holds** the locked screen until a signed release; **offline grace** releases after
  `offlineGraceSeconds` without server contact and delivers the submission later.
- **Resources** dropdown for `policy.allowedLinks`; everything outside the exam origin is blocked,
  including frames, fetches and downloads.
- **Debug-only developer switches**: `AVAIBE_*` variables work only in Debug builds
  (`publish-debug\`). The installer is built from Release output only.
- All readiness checks are reported to the server as **client-reported**.

## Requirements

- Windows 11, or Windows 10 version 2004 (build 19041) or newer
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- WebView2 Runtime (already part of Windows 11)
- The exam server from [`../backend`](../backend/README.md), running (the old `mock-backend` cannot enrol this client)

No administrator rights are needed to run the app.

## Logic tests

UI-free checks of the sign-in messages, access code, allowed links, Back to exam, and signed release
commands. Build the app in Release first, then:

```powershell
dotnet run -c Release --project tests\AvaibeExam.LogicTests
```

To also test against a running server, set `AVAIBE_TEST_BASE_URL`, `AVAIBE_TEST_ADMIN` and
`AVAIBE_TEST_PASSWORD` first. That part creates a test student, exam and enrollment token, so point
it at a test server, not a live one.

## Build and run

In PowerShell, from this folder:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\scripts\build.ps1
.\scripts\run.ps1
```

The first line lets the scripts run in the current PowerShell window only.

To connect to a server on another computer, pass its address (it must be https unless it is localhost):

```powershell
.\scripts\run.ps1 -BaseUrl http://192.168.1.20:4000
```

First add that address to **allowedDomains** in the teacher console's Policy panel, or the exam
page will be blocked.

## Packaging

| You want | Command | Output |
|---|---|---|
| A release build | `.\scripts\build.ps1` | `publish\AvaibeExam.exe` (needs the .NET 8 Desktop Runtime) |
| A debug build (dev switches on) | `.\scripts\build.ps1 -Configuration Debug` | `publish-debug\AvaibeExam.exe` |
| One portable file | `.\scripts\publish-standalone.ps1` | `publish-standalone\AvaibeExam.exe` (no .NET needed) |
| An installer | `.\scripts\make-installer.ps1` | `installer\output\AvaibeExam-Setup-0.1.0.exe` (needs [Inno Setup](https://jrsoftware.org/isdl.php)) |

Silent install: `AvaibeExam-Setup-0.1.0.exe /VERYSILENT /NORESTART`

## If you are stuck in lockdown

Press <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Del</kbd>, open Task Manager, and end `AvaibeExam.exe`,
or release the student from the teacher console.

## Logs

`%LOCALAPPDATA%\AvaibeExam\logs\`

Delete the whole `%LOCALAPPDATA%\AvaibeExam` folder to reset the device enrollment.

## Folder layout

```
AvaibeExam/    App, Models, Networking, Lockdown, Security, Browser, Views, Util
installer/     Inno Setup script
scripts/       build, run, smoke test, standalone publish, installer
```
