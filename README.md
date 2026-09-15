# Avaibe Exam: Windows Lockdown Browser

A secure exam browser for Windows, plus the exam server and teacher console, so you can run the
whole flow on one computer.

When an exam starts, the app takes over the screen and keeps the student inside the exam page
until a teacher releases it or the exam is submitted.

| Part | Folder | Status |
|---|---|---|
| Windows client (C#, .NET 8, WPF, WebView2) | [`windows/`](windows/README.md) | Code complete, not compiled yet |
| Exam server and teacher console (Node.js, SQLite, signed release commands) | [`backend/`](backend/README.md) | Working, 22 tests, fuzzed |
| Old in-memory mock (protocol v1, kept for reference only) | `mock-backend/` | Superseded. Current clients cannot enrol against it. |

> The Windows client has not been built on Windows yet. The first build may need a few small fixes.

## Quick start

**1. Start the server** (Node.js 24 or newer, no npm install needed):

```bash
cd backend
AVAIBE_ADMIN_PASSWORD='choose-a-long-password' node server.js
```

Then, in a second terminal, load the demo exams and print two enrollment tokens:

```bash
cd backend && npm run seed
```

Sign in at http://localhost:4000/admin/ as `admin` with the password you chose.

**2. Build and run the Windows app** (Windows 10 build 19041+ or Windows 11, .NET 8 SDK), in PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
cd windows
.\scripts\build.ps1
.\scripts\run.ps1
```

**3. Sign in** with one of the enrollment tokens printed by the seed command, student code `1025`,
and exam code `DEMO` (5 minutes). If you set an **access code** on an exam in the teacher console,
type it in the Access code box; otherwise leave it blank. Press **Continue**, then **Start Exam**.

**4. Release the student** from the teacher console with **Remote release**, or give them a
one-time **Release code**.

## If the screen is locked and you are stuck

- Release the student from the teacher console.
- Wait. The `DEMO` exam ends by itself after 5 minutes.
- Press <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Del</kbd>, open Task Manager, and end `AvaibeExam.exe`.

## More

- **Full guide, from first run to production:** [`windows/HOW-TO-RUN-AND-DEPLOY.md`](windows/HOW-TO-RUN-AND-DEPLOY.md)
- Building, packaging and installer commands: [`windows/README.md`](windows/README.md)
- Running the server, TLS, environment variables: [`backend/README.md`](backend/README.md)
