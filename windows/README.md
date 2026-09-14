# Avaibe Exam: Windows client

The exam lockdown browser for Windows. Built with C# 12, .NET 8, WPF and WebView2.

> This code has not been compiled on Windows yet. The first build may need a few small fixes.

## Requirements

- Windows 11, or Windows 10 version 2004 (build 19041) or newer
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- WebView2 Runtime (already part of Windows 11)
- The mock server from [`../mock-backend`](../mock-backend/README.md), running

No administrator rights are needed to run the app.

## Build and run

In PowerShell, from this folder:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\scripts\build.ps1
.\scripts\run.ps1
```

The first line lets the scripts run in the current PowerShell window only.

To connect to a mock server on another computer, pass its address:

```powershell
.\scripts\run.ps1 -BaseUrl http://192.168.1.20:4000
```

First add that address to **allowedDomains** in the teacher console's Policy panel, or the exam
page will be blocked.

## Packaging

| You want | Command | Output |
|---|---|---|
| A release build | `.\scripts\build.ps1` | `publish\AvaibeExam.exe` (needs the .NET 8 Desktop Runtime) |
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
