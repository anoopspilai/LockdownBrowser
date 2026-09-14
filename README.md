# Avaibe Exam: Windows Lockdown Browser

A secure exam browser for Windows, plus a small mock server so you can run the whole flow on one
computer.

When an exam starts, the app takes over the screen and keeps the student inside the exam page
until a teacher releases it or the exam is submitted.

| Part | Folder | Status |
|---|---|---|
| Windows client (C#, .NET 8, WPF, WebView2) | [`windows/`](windows/README.md) | Code complete, not compiled yet |
| Mock exam server and teacher console (Node.js) | [`mock-backend/`](mock-backend/README.md) | Working |

> The Windows client has not been built on Windows yet. The first build may need a few small fixes.

## Quick start

**1. Start the mock server** (Node.js 18 or newer):

```bash
cd mock-backend
node server.js
```

Open the teacher console at http://localhost:4000/admin.

**2. Build and run the Windows app** (Windows 10 build 19041+ or Windows 11, .NET 8 SDK), in PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
cd windows
.\scripts\build.ps1
.\scripts\run.ps1
```

**3. Sign in** with enrollment token `SCHOOL-DEMO`, any student code such as `1025`, and exam code
`DEMO` (5 minutes). Press **Continue**, then **Start Exam**.

**4. Release the student** from the teacher console with **Remote release**, or give them a
one-time **Release code**.

## If the screen is locked and you are stuck

- Release the student from the teacher console.
- Wait. The `DEMO` exam ends by itself after 5 minutes.
- Press <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Del</kbd>, open Task Manager, and end `AvaibeExam.exe`.

## More

- Building, packaging and installer commands: [`windows/README.md`](windows/README.md)
- Running the mock server: [`mock-backend/README.md`](mock-backend/README.md)
