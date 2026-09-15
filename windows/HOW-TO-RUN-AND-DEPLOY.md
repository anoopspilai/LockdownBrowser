# Avaibe Exam on Windows: How to Run It, and What Production Needs

This guide has two parts.

- **Part 1** runs the whole project on one Windows PC. Use it for development and demos.
- **Part 2** lists everything that must be done before real students sit real exams.

> **Where the project stands today**
>
> It is ready for development and demos. It is **not** ready for production yet.
>
> - The Windows app compiles with no errors and its logic tests pass, but it has not yet been run on a Windows PC. Test every screen there first.
> - The server in `backend/` is a working development server (staff login, database, signed release
>   commands), but it is not production-hardened yet: no single sign-on, no multi-factor login, one
>   server only. Section 11 lists what is missing. The old `mock-backend/` folder is reference only.
>
> Part 2 explains every gap and how to close it.

## Contents

**Part 1: Run it on one PC**

1. [What you need](#1-what-you-need)
2. [Get the code](#2-get-the-code)
3. [Start the server](#3-start-the-server)
4. [Build the Windows app](#4-build-the-windows-app)
5. [Run an exam from start to finish](#5-run-an-exam-from-start-to-finish)
6. [If you get stuck in lockdown](#6-if-you-get-stuck-in-lockdown)
7. [Use a server on another computer](#7-use-a-server-on-another-computer)
8. [Logs and resetting a PC](#8-logs-and-resetting-a-pc)

**Part 2: Production**

9. [What production looks like](#9-what-production-looks-like)
10. [Must fix in the Windows app](#10-must-fix-in-the-windows-app)
11. [Build a real server](#11-build-a-real-server)
12. [Sign the app and build the installer](#12-sign-the-app-and-build-the-installer)
13. [Prepare school PCs](#13-prepare-school-pcs)
14. [Students' own laptops](#14-students-own-laptops)
15. [Recommended exam policy](#15-recommended-exam-policy)
16. [Network requirements](#16-network-requirements)
17. [Updates](#17-updates)
18. [Antivirus and security software](#18-antivirus-and-security-software)
19. [Testing before go-live](#19-testing-before-go-live)
20. [Exam day runbook](#20-exam-day-runbook)
21. [Privacy and legal](#21-privacy-and-legal)
22. [What no lockdown can prevent](#22-what-no-lockdown-can-prevent)
23. [Go-live checklist](#23-go-live-checklist)

---

# Part 1: Run it on one PC

## 1. What you need

| Item | Why you need it | Where to get it |
|---|---|---|
| Windows 11 | The app runs here. Windows 10 support ended on 14 October 2025, so use Windows 11. | Already installed |
| .NET 8 SDK | The toolkit that turns the C# code into `AvaibeExam.exe`. | https://dotnet.microsoft.com/download/dotnet/8.0 |
| WebView2 Runtime | The browser engine that shows the exam page inside the app. | Already part of Windows 11. Otherwise https://developer.microsoft.com/microsoft-edge/webview2/ |
| Node.js 24 or newer (LTS) | Runs the exam server and teacher console. Older versions lack the built-in database. | https://nodejs.org |
| Git | Downloads the code. | https://git-scm.com/download/win |
| Inno Setup 6.3 or newer | Only needed to build the installer. | https://jrsoftware.org/isdl.php |

**SDK or Runtime?** The SDK builds apps. The Runtime only runs finished apps. On your
development PC you need the **SDK**.

After installing, open **PowerShell** (press the Windows key, type `PowerShell`, press Enter) and
check each tool:

```powershell
dotnet --info
node --version
git --version
```

You should see an `8.x` SDK listed, a Node version of `v24` or higher, and a Git version.

**If PowerShell says a command "is not recognized"**, close PowerShell and open it again. Installers
update the system path, and only new windows see the change.

## 2. Get the code

Keep the path short, with no spaces, and **outside OneDrive**.

- Windows build folders are deeply nested, and long paths can break the build.
- OneDrive syncs files while the compiler is writing them, which causes random "file in use" errors.

```powershell
mkdir C:\dev
cd C:\dev
git clone https://github.com/anoopspilai/LockdownBrowser.git
cd LockdownBrowser
```

If the repository is private, Git asks you to sign in to GitHub.

## 3. Start the server

The server is the exam platform: it hands the app its exam, checks the student, receives heartbeats,
signs release commands, and runs the teacher console. Run it **on the same Windows PC** for testing.
The app only accepts plain `http://` for `localhost`; any other address must be `https://`
(section 7).

In your **first PowerShell window**, choose an admin password and start the server:

```powershell
cd C:\dev\LockdownBrowser\backend
$env:AVAIBE_ADMIN_PASSWORD = "choose-a-long-password"
node server.js
```

The password is only used the very first time, to create the `admin` account. You should see a
line saying the server is listening on port 4000. Leave this window open.

In a **second PowerShell window**, load the demo exams and students:

```powershell
cd C:\dev\LockdownBrowser\backend
npm run seed
```

This creates the exams `DEMO` (5 minutes), `MATH101` and `SCI202`, the students `1025` and
`2001` to `2005`, and prints two **enrollment tokens**. Copy the `school` token; the app asks for it
once on each PC.

Open **http://localhost:4000/admin/** in Edge or Chrome and sign in as `admin` with the password you
chose. This is the teacher console: **Live** shows students taking an exam, **Exams** edits exams,
access codes and Resources links, **Devices** creates new enrollment tokens.

| Problem | Fix |
|---|---|
| `node:sqlite` or "Cannot find module" error | Node.js is too old. Install version 24 or newer. |
| Windows Firewall asks about Node.js | Cancel is fine when everything runs on this PC. |
| `EADDRINUSE` error | Something already uses port 4000. Run `netstat -ano \| findstr :4000`, note the last number (the process ID), then `taskkill /PID <number> /F`. |
| Forgot the admin password | `$env:AVAIBE_NEW_PASSWORD = "new-long-password"; node scripts\create-admin.js admin2 admin`, then sign in as `admin2`. |

## 4. Build the Windows app

Open a **second PowerShell window**:

```powershell
cd C:\dev\LockdownBrowser\windows
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\scripts\build.ps1
```

What each part does:

- **`Set-ExecutionPolicy ... -Scope Process`** lets the project's scripts run. Windows blocks
  unsigned scripts by default. This lifts the block for this window only, and it resets when you
  close the window.
- **`build.ps1`** runs three steps:
  1. **restore** downloads the libraries the project uses, called NuGet packages.
  2. **build** compiles the C# code and checks it for errors.
  3. **publish** copies only the files needed to run into `windows\publish\`.

When it works you see:

```
Published: C:\dev\LockdownBrowser\windows\publish\AvaibeExam.exe
```

### If the build shows errors

The code compiles cleanly with the .NET 8 SDK, so errors usually mean a different SDK or WebView2 package version.

A C# error looks like this:

```
AppState.cs(42,17): error CS0104: 'MessageBox' is an ambiguous reference ...
```

It tells you the **file** (`AppState.cs`), the **line and column** (`42,17`), and an **error code**
(`CS0104`). Search the error code online to learn what it means.

Rules that save a lot of time:

1. **Fix the first error first.** One mistake often causes many errors after it.
2. **Rebuild after each fix.**
3. **Warnings are not errors.** A warning such as `NU1605` does not stop the build.

The most likely first-build errors:

| Error | What it means | Fix |
|---|---|---|
| `CS0104` ambiguous reference to `MessageBox`, `Application`, `UserControl` or `Brushes` | Two libraries both define that name. | Write the full name, for example `System.Windows.MessageBox` instead of `MessageBox`. |
| `CS1061` a WebView2 setting "does not contain a definition" | The WebView2 package is older or newer than expected. | Update the package: `dotnet add AvaibeExam\AvaibeExam.csproj package Microsoft.Web.WebView2`. Or delete that one setting line, because the uncertain ones are optional. |
| The app opens but the exam page stays blank | WebView2 did not start. | Check the log (section 8) and confirm the WebView2 Runtime is installed. |

## 5. Run an exam from start to finish

```powershell
.\scripts\run.ps1 -Configuration Release
```

This builds the Release app (the same build the installer ships) and opens it. Plain
`.\scripts\run.ps1` builds a Debug app instead, which also honours the developer switches in
section 6.

**1. Login screen.** Fill in:

| Field | Value |
|---|---|
| Backend URL | `http://localhost:4000` (already filled in) |
| Enrollment token | The `school` token printed by `npm run seed`, or a new one from the console's **Devices** tab. Single use, valid 24 hours, asked only the first time on each PC. The old `mock-backend/` cannot enroll this app. |
| Student code | A student that exists in the teacher console, for example `1025` from the demo data |
| Exam code | `DEMO` (5 minutes, releases automatically on submit), `MATH101` (60 minutes) or `SCI202` (45 minutes) |
| Access code | Only if the teacher set one on the exam in the console. Leave it blank otherwise. After 10 wrong codes the PC is locked out of that exam for 15 minutes. |

Press **Continue**.

**2. Readiness checklist.** The app checks the PC: Windows version, Secure Boot, management status,
account type, number of displays, screen-sharing tools, kiosk mode, camera and microphone
permission, internet, WebView2, and whether it is a virtual machine. Items the exam policy requires
must pass. **Start Exam** only becomes clickable when they do.

**3. Start Exam.** The app goes full screen, covers the taskbar, and loads the exam page. The badge
at the top shows the lockdown mode:

- **Kiosk fallback** (amber) on a normal PC. This is expected on a development machine.
- **Assigned Access** (green) on a PC set up in Windows kiosk mode (section 13).

**4. Watch the teacher console.** The student's row appears with the last heartbeat, the lockdown
mode, the number of displays, and a risk level. Click **Events** to see the timeline. Try pressing
<kbd>Alt</kbd>+<kbd>Tab</kbd> on the exam PC and watch a blocked-shortcut event appear.

**5. Release the student.** Choose one:

- **Remote release.** The app unlocks within about 10 seconds, on its next heartbeat.
- **Release code.** The console shows a 6-digit code valid for 60 seconds. On the exam PC, click
  **Request exit** in the exam page, type the code, and press verify.

**6. Exit screen.** It says the lockdown is released. **Quit** now works.

Also try **Resources** in the status strip (links the teacher added to the exam) and **Back to
exam**, **Warn** (a message appears on the student's screen), **Terminate**, submitting the exam
(`DEMO` releases automatically; other exams wait for the teacher), and letting the `DEMO` timer run
out.

## 6. If you get stuck in lockdown

The app is built to be hard to leave. These always work:

1. **Release from the teacher console.** This is the normal way out.
2. **Wait.** The `DEMO` exam submits and releases itself after 5 minutes.
3. **Press <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Del</kbd>**, open **Task Manager**, select
   `AvaibeExam.exe`, and click **End task**. The app never disables Task Manager.

For unattended testing, this signs in automatically and force-closes the app after 120 seconds:

```powershell
.\scripts\run.ps1 -AutoRun -Token <enrollment-token> -ExitAfter 120
```

**These switches only exist in Debug builds.** `run.ps1` and `smoke.ps1` build a Debug app into
`windows\publish-debug\`. A Release build (`windows\publish\`, which the installer packages)
ignores every `AVAIBE_*` environment variable, so there is no hidden way out of a real exam.

Other things to know about a locked exam:

- **Terminate does not unlock.** When a teacher presses Terminate (or an extra monitor triggers
  the `TERMINATE` policy), the app submits the exam and shows "Session terminated. Wait for your
  teacher." The screen stays locked until the teacher releases the student or types a release
  code. This is deliberate.
- **Only a signed release unlocks.** Every release and terminate command carries a signature from
  the server. The app checks it against the key it pinned at enrollment. A command with a bad or
  missing signature is ignored and reported to the console as `COMMAND_REJECTED`.
- **Offline failsafe.** If the app cannot reach the server for the policy's `offlineGraceSeconds`
  (default 10 minutes), it releases the student by itself, shows why, and keeps trying to send the
  submission and events once the connection comes back. The status strip counts the offline time
  down so the student can see it.

## 7. Use a server on another computer

The app refuses plain `http://` for any address other than `localhost`, so a teacher-console server
on another computer must use **HTTPS with a certificate this PC trusts**. A self-signed certificate
made on another computer is not trusted by Windows, and the app will not connect.

- **For testing:** run the server on the same PC as the app (section 3).
- **For a school:** host the server under a real domain name with a certificate from a public
  certificate authority, for example `https://exam.yourschool.example`. Start it with
  `AVAIBE_TLS_CERT` and `AVAIBE_TLS_KEY`, or put it behind a reverse proxy that handles HTTPS and
  set `AVAIBE_PUBLIC_BASE_URL`. See `backend/README.md`.

On each exam PC, type that address in the **Backend URL** box on the login screen. IT can also
preset it for every PC in the registry value `HKLM\SOFTWARE\Avaibe\Exam\BaseUrl`, which makes
the box read-only.

The exam page's own server is always allowed. Links the teacher adds to an exam under **Resources**
are allowed automatically; you do not edit `allowedDomains` by hand.

## 8. Logs and resetting a PC

The app writes a log file each day and keeps 14 days of logs:

```
%LOCALAPPDATA%\AvaibeExam\logs\avaibe-YYYYMMDD.log
```

Open the folder:

```powershell
explorer "$env:LOCALAPPDATA\AvaibeExam\logs"
```

The same `AvaibeExam` folder also holds:

| File | What it is |
|---|---|
| `enrollment.dat` | The device's enrollment, encrypted for the current Windows user |
| `settings.json` | The last backend URL and codes typed. No secrets. |
| `WebView2\` | The embedded browser's profile |

To reset a PC to a first-run state, close the app, then run:

```powershell
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\AvaibeExam"
```

---

# Part 2: Production

## 9. What production looks like

```
   School PCs                                  Students' own laptops
   Windows 11 Education or Enterprise          Windows 11
   Managed by Intune, kiosk mode               Kiosk fallback only
          │                                              │
          │        Signed AvaibeExam installer           │
          └───────────────────┬──────────────────────────┘
                              │  HTTPS only
                              ▼
        ┌──────────────────────────────────────────────┐
        │  Your real exam platform                     │
        │  Student and staff sign-in, database,        │
        │  exam pages, teacher console, event storage, │
        │  audit log, monitoring, backups              │
        └──────────────────────────────────────────────┘
```

Production has three layers, and **all three** must be ready:

1. **The Windows app**: compiled, hardened, signed and packaged. Sections 10 and 12.
2. **The server**: `backend/`, hardened for production. Section 11.
3. **The PCs**: configured and managed by school IT. Sections 13 to 17.

## 10. Must fix in the Windows app

Do all of these before any real exam.

| # | Item | Why it matters | What to do |
|---|---|---|---|
| 1 | **Run and fully test on Windows** | The code compiles and its logic tests pass, but it has never run on a Windows PC. | Build it, run `dotnet run -c Release --project tests\AvaibeExam.LogicTests`, and pass every test in section 19. |
| 2 | ~~Remove developer switches from release builds~~ **Done (v2 pass).** | The switches `AVAIBE_AUTO_RUN`, `AVAIBE_AUTO_EXIT_AFTER`, `AVAIBE_SMOKE_TEST` and `AVAIBE_BASE_URL` now exist only in Debug builds (`#if DEBUG` in `AppState.cs`, `App.xaml.cs`, `Constants.cs`). `build.ps1` writes Debug output to `publish-debug\` and Release to `publish\`; `make-installer.ps1` refuses anything that is not a Release build. | Nothing to do. Keep it that way: never hand a `publish-debug\` folder to a school. |
| 3 | **Move from .NET 8 to .NET 10** | Microsoft stops security updates for .NET 8 on **10 November 2026**. .NET 10 is the current long-term release, supported until 14 November 2028. See https://dotnet.microsoft.com/platform/support/policy/dotnet-core | Install the .NET 10 SDK. In `AvaibeExam/AvaibeExam.csproj`, change `net8.0-windows10.0.19041.0` to `net10.0-windows10.0.19041.0`, update the NuGet packages, rebuild and retest. |
| 4 | ~~Require HTTPS~~ **Done (v2 pass).** | The app now refuses any backend URL that is not `https://`, except `localhost` / `127.0.0.1` for development. The exam page URL must be https too. | Nothing to do. Give schools an https address. |
| 5 | ~~Let IT preset the server address~~ **Done (v2 pass).** | The app reads `HKEY_LOCAL_MACHINE\SOFTWARE\Avaibe\Exam`, string value `BaseUrl`. When it is present and valid, the login field is read-only. Only administrators (Intune, Group Policy) can write that key. | Deploy the registry value with Intune or GPO on school PCs. |
| 6 | **Update the WebView2 package** | The project pins `Microsoft.Web.WebView2` version `1.0.2592.51`, from 2024. | Update to the current stable version and retest. Newer SDKs (1.0.2903.40+) also expose `ScreenCaptureStarting`, which the app should then subscribe to. |
| 7 | **Decide how .NET reaches each PC** | A normal build needs the .NET Desktop Runtime installed on every PC. | Either publish self-contained with `.\scripts\publish-standalone.ps1`, which bundles .NET, or push the runtime to PCs with Intune. |
| 8 | **Add crash recovery** | If the PC loses power mid-exam, the student must be able to rejoin with answers kept. | Test this carefully. The resume flow probably needs work on both the app and the server. |
| 9 | **Set the production signing key pin (optional but recommended)** | The app pins the server's signing key the first time a PC enrolls ("trust on first use"). A hard-coded pin removes even that first-use gap. | Put the production server's public key (base64 SPKI) into `Constants.PinnedServerPublicKey` before building the release. Enrollment against any other server then fails. |
| 10 | **Re-enroll every PC once** | Enrollments made by the old (v1) app have no pinned key and are ignored; the login screen asks for a new enrollment token. | Issue new single-use enrollment tokens from the admin console before the first v2 exam. |

What the v2 pass changed in how the app behaves, in plain words:

- **Signed release.** Release and terminate commands are signed by the server (ECDSA P-256). The app verifies the signature, the session and device ids, the time window and a one-time nonce. Anything else is ignored and reported.
- **Terminate holds.** Terminate submits the exam and keeps the screen locked ("Session terminated. Wait for your teacher.") until a signed release arrives.
- **Offline grace.** After `offlineGraceSeconds` without server contact the app releases itself, records why, and delivers the submission and events when the server is back.
- **Resources menu.** The policy's `allowedLinks` show up in a "Resources" dropdown in the status strip. Everything else outside the exam page's origin is blocked, including frames and background requests.
- **Client-reported checks.** Every readiness item, including the Assigned Access hint, is labelled "client-reported". The server decides what counts.

When you release a new version, change the version number in **three places** so they match:

| File | Setting |
|---|---|
| `AvaibeExam/AvaibeExam.csproj` | `Version`, `AssemblyVersion`, `FileVersion` |
| `AvaibeExam/App/Constants.cs` | `ClientVersion` |
| `installer/AvaibeExam.iss` | `MyAppVersion` |

## 11. Build a real server

`backend/` is a working server that implements everything the app expects. Before a real rollout
it still needs the items marked below.

### What the real server must have

| Requirement | Why | `backend/` today |
|---|---|---|
| **HTTPS everywhere**, with HSTS | Protects sessions, answers and release codes in transit. | HTTPS with HSTS when given a certificate; plain HTTP only for localhost testing |
| **Real student sign-in**, such as the school's single sign-on | Stops anyone sitting an exam as someone else. | **Missing.** The student code must exist, but there is no single sign-on |
| **Staff sign-in with multi-factor authentication and roles**: teacher, exam manager, IT admin, reviewer | Only the right teacher can release or terminate a student. | Password login with lockout and roles (admin, teacher, reviewer). **Multi-factor missing** |
| **A database**, for example PostgreSQL | Data survives restarts and crashes. | SQLite file on one server. Fine for a pilot; use a managed database to scale |
| **Short-lived session tokens bound to one device** | A copied token is useless on another PC. | Done: session token bound to the session and the device token |
| **A limit on wrong release-code guesses** | A 6-digit code has 1,000,000 possible values. Unlimited guessing could find it. Lock the session after a few wrong tries and alert the teacher. | Done: locked after 5 wrong tries, incident raised |
| **The exam page served with a secure, http-only cookie** | The page must never receive a token it could leak. | Done |
| **Append-only event storage and an audit log** that records who released whom, when and why | Needed for disputes, appeals and incident reviews. | Done: append-only events and audit log, reasons required |
| **Server-side answer autosave and time** | The server, not the PC, decides the time left and keeps the answers. | Done |
| **Rate limiting and a web application firewall** | Protects the service from abuse and overload. | Rate limits on enrollment, events and access codes. **No firewall** |
| **Monitoring and alerts** for missed heartbeats, error rates and slow responses | Problems are seen during the exam, not after. | Stale sessions and incidents in the console. **No external alerting** |
| **Backups with a tested restore** | Recovery from mistakes and failures. | **Missing.** Back up `backend/data` yourself |
| **Secrets in a secrets manager** | Keys and passwords never live in code. | The signing key is stored in the database. **Move it to a key vault** |
| **Accurate server time** (NTP) | Release codes expire after 60 seconds, so clocks must be right. | System clock |
| **Independent security testing** (penetration test) before rollout | Finds weaknesses before students do. | **Not done** |

### What the real server must implement

The app calls these endpoints; `backend/` implements all of them, with details in `backend/README.md`.

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/v1/devices/enroll` | Register a PC |
| `POST` | `/api/v1/sessions` | Start an exam, given the PC's readiness report. Returns the policy. |
| `POST` | `/api/v1/sessions/:id/heartbeat` | Receives status every few seconds. Replies with a command: `RELEASE`, `TERMINATE`, `WARN`, or nothing. |
| `POST` | `/api/v1/sessions/:id/events` | Receives batches of security events |
| `POST` | `/api/v1/sessions/:id/submit` | Submits the exam |
| `POST` | `/api/v1/sessions/:id/unlock` | Checks a release code |
| `GET` | `/healthz` | Health check |

It also needs the teacher console features: live sessions, release codes, remote release, warn,
terminate, event timelines and the policy editor.

### Plan for load

The app sends a heartbeat every 10 seconds by default. With 1,000 students online, that is about
**100 heartbeat requests every second**, plus event batches. Load-test the server at **at least
double** your largest expected exam.

## 12. Sign the app and build the installer

### Why signing matters

- Unsigned apps show a blue **"Windows protected your PC"** SmartScreen warning to every student.
- A signature proves who made the app and that nobody changed it.
- School IT can allow your app by its certificate instead of checking every new file.

### Get a code-signing certificate

| Option | Notes |
|---|---|
| **OV code-signing certificate** from a public certificate authority | Since June 2023 the private key must be kept on a hardware token or hardware security module. |
| **Azure Artifact Signing** | A Microsoft cloud signing service. Check whether your organisation is eligible before choosing it. |

An EV certificate **no longer** removes the SmartScreen warning instantly. Reputation builds up as
more people download the signed app. See
https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options

### Build, sign and package

**Step 1.** Build, sign the app files, build the installer, and sign the installer, all in one
command. Find your certificate's thumbprint in the certificate details.

```powershell
.\scripts\make-installer.ps1 -Sign -CertThumbprint <your-thumbprint>
```

The result is `installer\output\AvaibeExam-Setup-<version>.exe`.

**Step 2.** Verify the signature:

```powershell
Get-AuthenticodeSignature .\installer\output\AvaibeExam-Setup-0.1.0.exe
```

The `Status` must say `Valid`.

**Step 3.** Publish the file's fingerprint so schools can confirm their download is genuine:

```powershell
Get-FileHash .\installer\output\AvaibeExam-Setup-0.1.0.exe -Algorithm SHA256
```

### What the installer does

- Installs to `C:\Program Files\Avaibe\Avaibe Exam\` for all users. It needs administrator rights.
- Creates a Start menu shortcut, an optional desktop shortcut, and a normal uninstaller.
- Checks for the WebView2 Runtime. To install it automatically when missing, place Microsoft's
  `MicrosoftEdgeWebview2Setup.exe` in the `installer` folder before building.
- **Does not** install the .NET Desktop Runtime. See section 10, item 7.

School IT can install it silently:

```powershell
AvaibeExam-Setup-0.1.0.exe /VERYSILENT /NORESTART
```

**Never change the `AppId` value in `installer/AvaibeExam.iss` after the first release.** Windows
uses it to recognise upgrades. Changing it installs a second copy instead of upgrading.

## 13. Prepare school PCs

School PCs give the **strongest** lockdown, because Windows itself enforces it.

### Choose the right Windows edition

| Windows 11 edition | Assigned Access (kiosk mode) | Shell Launcher | Keyboard Filter (can block <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Del</kbd>) |
|---|---|---|---|
| Home | No | No | No |
| Pro | Yes | No | No |
| Education | Yes | Yes | Yes |
| Enterprise | Yes | Yes | Yes |

**Recommendation: Windows 11 Education or Enterprise** for exam labs.

### Setup steps for school IT

1. **Enroll the PCs in Microsoft Intune.** Use Windows Autopilot for new PCs, or a provisioning
   package for existing ones.
2. **Create a standard, non-administrator kiosk account.** Kiosk settings do not apply to
   administrator accounts.
3. **Deploy the signed installer** as a Win32 app in Intune. Mark it **Required** and set the
   deadline before the exam period. See
   https://learn.microsoft.com/mem/intune/apps/apps-win32-app-management
4. **Configure Assigned Access** using the restricted user experience, with `AvaibeExam.exe` as the
   app that launches automatically. This is the setup Microsoft documents for desktop apps like this
   one. See https://learn.microsoft.com/windows/configuration/assigned-access/
5. **On Education or Enterprise, add Keyboard Filter** to block <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Del</kbd>,
   <kbd>Win</kbd>+<kbd>L</kbd> and <kbd>Alt</kbd>+<kbd>Tab</kbd>. Give the breakout key only to
   invigilators. See https://learn.microsoft.com/windows/configuration/keyboard-filter/
6. **Apply supporting policies:**
   - Turn off Xbox Game Bar recording.
   - Turn off Remote Desktop, and remove remote-control tools from the PC image.
   - Turn off clipboard history and cross-device clipboard.
   - Schedule Windows Update and app updates outside exam hours.
   - Hide fast user switching, and set sleep and power options so PCs don't sleep mid-exam.
7. **Set the exam policy** to require kiosk mode, management and a standard account (section 15).
8. **Roll out in stages:** one PC, then one lab, then everyone.

When testing, confirm the app's badge shows **Assigned Access** in green on a configured PC. The
app detects kiosk mode by checking the Windows registry and shell, so verify this on your real
setup.

## 14. Students' own laptops

On a student's own laptop you do not control Windows, so the app runs in **kiosk fallback** mode.
Its controls are real but **best effort**.

- Students need administrator rights on their laptop to install the app.
- Expect the SmartScreen warning until the signed app has built up reputation. Tell students to
  check that the publisher name is correct.
- The readiness checklist can refuse to start when screen-sharing tools or extra monitors are
  detected. Set this in the policy.

**Use students' own laptops for low- and medium-stakes exams.** For high-stakes exams, use school
PCs in kiosk mode, in an invigilated room.

## 15. Recommended exam policy

Set these in the policy editor on the real server.

| Setting | School PCs | Students' own laptops | What it does |
|---|---|---|---|
| `allowedDomains` | Your exam site only | Your exam site only | The app blocks every other address. |
| `requireAAC` | `true` | `false` | On Windows this means "require kiosk mode (Assigned Access)". |
| `requireMDM` | `true` | `false` | Requires the PC to be managed, for example by Intune. |
| `requireStandardAccount` | `true` | `false` | Refuses to start from an administrator account. |
| `requireSIP` | `true` | Your choice | On Windows this means "require Secure Boot". |
| `blockExternalDisplay` | `true` | `true` | Detects and blocks extra monitors. |
| `externalDisplayAction` | `BLOCK_START` | `BLOCK_START` or `WARN` | What happens when an extra monitor is found. Options: `WARN`, `BLOCK_START`, `PAUSE`, `TERMINATE`, `FLAG`. |
| `allowClipboard` | `false` | `false` | Blocks copy and paste. |
| `allowPrinting` | `false` | `false` | Blocks printing. |
| `allowDownloads` | `false` | `false` | Blocks file downloads. |
| `allowDevTools` | `false` | `false` | Blocks browser developer tools. |
| `allowStudentReleaseCode` | `true` | `true` | Lets a teacher release a student with a one-time code. |
| `heartbeatIntervalSeconds` | `10` | `10` | How often the app checks in. |
| `minClientVersion` | Your current release | Your current release | Older app versions cannot start an exam. |
| `allowedApps` | Only approved tools | Only approved tools | For example a calculator for students with accommodations. |

Accommodations such as extra time or assistive technology must be possible. Never let lockdown
settings block a student's approved accommodation.

## 16. Network requirements

Give school IT this list to allow through the firewall and proxy:

| Destination | Port | Why |
|---|---|---|
| Your exam API address | 443 | Sign-in, heartbeats, events |
| Your exam page address | 443 | The exam page shown in the app |
| Microsoft Edge and WebView2 update services | 443 | Keeps the browser engine patched. Schedule updates outside exams. |
| Your certificate authority's revocation (OCSP and CRL) addresses | 80 and 443 | Windows checks the app's signature |
| A time server (NTP) | 123 | Release codes and tokens depend on correct time |

- If the school's proxy inspects HTTPS traffic, **exclude the exam addresses** from inspection.
- Bandwidth is small today: the app sends only short JSON messages.
- Test on the school's real network before exam day, not just on a home connection.

## 17. Updates

| Rule | How |
|---|---|
| **Never update during an exam** | Set Intune deadlines and Windows Update maintenance windows outside exam hours. |
| **Force old versions to update** | After a new version has rolled out, raise `minClientVersion` on the server. Older apps then fail the readiness check with "update required". |
| **The browser engine updates safely** | A WebView2 update only takes effect after the app restarts, so it never switches engines mid-exam. |
| **Keep a way back** | Keep the previous signed installer so you can roll back quickly. |

## 18. Antivirus and security software

To stop cheating, this app does things that security software watches closely:

- a keyboard hook that blocks shortcuts,
- hiding its window from screen capture,
- listing running programs to spot remote-control and recording tools,
- blocking shutdown and sign-out during an exam,
- reading Windows settings such as Secure Boot and management status.

That is why trust matters. The app must always:

- be signed, with a normal installer and uninstaller,
- never inject code into other programs,
- never install hidden services or start itself automatically,
- never change Microsoft Defender, SmartScreen or UAC settings,
- run as a standard user.

Before rollout, test with Microsoft Defender **and** with the security software each school uses.
If the app is wrongly flagged, submit it to Microsoft as a false positive. Give school IT the
certificate thumbprint and the file fingerprints so they can allow the app.

## 19. Testing before go-live

Tick every box on real hardware.

**Install**
- [ ] Clean install on a fresh Windows 11 PC
- [ ] Upgrade over the previous version
- [ ] Uninstall leaves no program files behind
- [ ] Silent install through Intune

**Lockdown**
- [ ] <kbd>Alt</kbd>+<kbd>Tab</kbd>, <kbd>Alt</kbd>+<kbd>F4</kbd>, <kbd>Win</kbd> key shortcuts and <kbd>Win</kbd>+<kbd>D</kbd> are blocked
- [ ] PrintScreen, Snipping Tool (<kbd>Win</kbd>+<kbd>Shift</kbd>+<kbd>S</kbd>), Xbox Game Bar and OBS capture show black instead of the exam
- [ ] Connecting a second monitor mid-exam triggers the chosen policy action
- [ ] Notifications do not appear over the exam
- [ ] Links to sites outside `allowedDomains` are blocked
- [ ] Closing, sign-out and shutdown are blocked while locked
- [ ] Remote-control tools such as TeamViewer or AnyDesk are flagged in the console

**Network**
- [ ] Unplugging the network mid-exam shows offline, and reconnecting resumes heartbeats
- [ ] It works through the school's real proxy and firewall

**Release**
- [ ] A valid release code unlocks the app
- [ ] A code is refused after 60 seconds
- [ ] A used code is refused a second time
- [ ] Repeated wrong codes lock the session (real server only)
- [ ] Remote release, warn and terminate all work

**Exam**
- [ ] Timer expiry submits automatically
- [ ] Submitting releases the student
- [ ] After a crash or power loss, the student can rejoin and answers are kept

**Editions and security software**
- [ ] Windows 11 Pro, Education and Enterprise, with kiosk mode applied
- [ ] Microsoft Defender, plus each school's security software

**Scale and people**
- [ ] Server load test at double the largest expected exam
- [ ] Screen readers and approved accommodations still work
- [ ] A pilot with a real low-stakes quiz before any high-stakes exam

## 20. Exam day runbook

**The day before**
- Confirm every PC runs the required app version.
- Run the 5-minute practice exam on at least one PC in each room.
- Check the server health page and the monitoring dashboard.
- Confirm every teacher can sign in to the console.
- Confirm updates are paused, and prepare spare PCs.

**At the start**
- Open the teacher console.
- Students sign in and pass the readiness check.
- Check every student has a recent heartbeat.
- Check school PCs show **Assigned Access**, not kiosk fallback.

**During the exam**
- Watch for amber or red risk levels and missed heartbeats.
- Use **Warn** for a first reminder.
- For a technical problem, release the student with a release code and record the reason.

**At the end**
- Confirm every student has submitted.
- Release anyone still locked.
- Export the incidents and event timelines.
- Collect the log files from any PC that had problems (section 8).

## 21. Privacy and legal

- **Tell students and parents** what is collected, why, how long it is kept, who can see it, and
  how to appeal a decision.
- **Collect only what you need**, and delete it on a fixed schedule.
- **Security events are signals for a human to review**, never automatic proof of cheating.
- **Get legal advice** on your country's education and data-protection laws before rollout.
- If camera or microphone monitoring is added later, it needs explicit consent, encryption and
  strict retention limits.

## 22. What no lockdown can prevent

Be honest with schools about these limits.

| Risk | Reality | What helps |
|---|---|---|
| <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Del</kbd> | No app can block it. Windows handles it before any app sees it. | Keyboard Filter on Education or Enterprise |
| A phone photographing the screen | Software cannot see it. | Invigilation, and camera monitoring in future |
| Another person in the room | Software cannot see it. | Invigilation |
| Capture protection | Strong, but Microsoft does not guarantee it against every tool. | Flagging recording tools, and school PCs |
| Virtual machines and hardware capture devices | Partly detected. Can be disguised. | School PCs in kiosk mode |
| Students' own laptops | Controls are best effort. | School PCs for high-stakes exams |

## 23. Go-live checklist

**Windows app**
- [ ] Compiles and passes every test in section 19
- [x] Developer switches removed from release builds (Debug-only since the v2 pass)
- [ ] Moved to .NET 10
- [x] HTTPS required (v2 pass)
- [x] Server address preset by IT (`HKLM\SOFTWARE\Avaibe\Exam\BaseUrl`, v2 pass)
- [ ] WebView2 package updated
- [ ] `Constants.PinnedServerPublicKey` set to the production key
- [ ] Version number updated in all three places
- [ ] Installer built with `make-installer.ps1` (it refuses Debug builds)

**Server**
- [ ] `backend/` hardened: single sign-on, multi-factor staff login, backups, key vault
- [ ] HTTPS, student sign-in, staff multi-factor sign-in and roles
- [ ] Database, backups and a tested restore
- [ ] Wrong release-code guesses limited
- [ ] Exam page uses a secure cookie; the `page-token` shortcut is gone
- [ ] Audit log, monitoring and alerts
- [ ] Load test passed
- [ ] Penetration test passed

**Signing and installation**
- [ ] Code-signing certificate obtained
- [ ] App and installer signed, and signatures verified
- [ ] File fingerprints published
- [ ] .NET delivery decided

**School PCs**
- [ ] Windows 11 Education or Enterprise
- [ ] Enrolled in Intune, with a standard kiosk account
- [ ] Assigned Access and Keyboard Filter configured
- [ ] Supporting policies applied and updates scheduled
- [ ] Network allowlist applied

**Policy and people**
- [ ] Production exam policy set
- [ ] Teachers trained on release codes and the console
- [ ] Students and parents informed
- [ ] Legal review done
- [ ] Pilot exam completed
