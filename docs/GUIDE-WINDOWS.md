# Windows Guide for Beginners

A complete, slow, step by step guide to building, running, packaging and installing the
**Avaibe Exam** Windows client.

This guide assumes you have **never built a Windows desktop app**. It assumes you know React and
Node.js. Every new word is explained the first time it appears. Every command has a sentence before
it (what it does) and a sentence after it (what you should see).

Read it in order the first time. After that, use it as a lookup.

---

## 1. What you are going to build

### 1.1 The two pieces

There are two programs. They talk to each other over plain HTTP.

```
        ┌───────────────────────────────────────────────┐
        │  mock-backend/   (Node.js, no dependencies)   │
        │                                               │
        │  http://localhost:4000/admin   teacher page   │
        │  http://localhost:4000/exam/:id student page  │
        │  /api/v1/...   sessions, heartbeats, events   │
        └──────────────────────┬────────────────────────┘
                               │  HTTP  (port 4000)
                               │
        ┌──────────────────────▼────────────────────────┐
        │  windows/   AvaibeExam.exe   (C# / .NET 8)    │
        │                                               │
        │  Login  ->  Preflight  ->  Exam  ->  Exit     │
        │  WPF window + WebView2 browser control        │
        │  Lockdown engine (keyboard hook, covers, ...) │
        └───────────────────────────────────────────────┘
```

1. **`mock-backend/`** is a small Node.js server. It pretends to be the real exam platform. It keeps
   everything in memory, so restarting it wipes all data. It also serves the teacher console page
   and the student exam page. You already know this world: it is plain Node, no npm packages.
2. **`windows/`** is the Windows desktop app. It is written in C#. When you build it you get a file
   called `AvaibeExam.exe`. That file is the exam browser the student runs.

The student app shows four screens in order:

| Screen | What the student sees |
|---|---|
| Login | Backend URL, enrollment token, student code, exam code |
| Preflight | A checklist: Windows version, displays, screen sharing, camera, internet, WebView2, and so on |
| Exam | The exam web page, loaded inside the app, with lockdown switched on |
| Exit | "Lockdown released. You may close this app." |

### 1.2 What "lockdown" means here

"Lockdown" means the app tries very hard to keep the student inside the exam page while the exam is
running. It does this by:

1. Making its window full screen, always on top, and covering the taskbar.
2. Painting other monitors black so a second screen cannot show notes.
3. Swallowing keyboard shortcuts such as Alt+Tab, Alt+F4, the Windows key, PrintScreen and F12.
4. Telling Windows to leave its window out of screenshots and screen recordings.
5. Locking down the embedded browser: no right click menu, no developer tools, no downloads, no
   popups, and only exam domains can be loaded.
6. Watching for other apps (screen recorders, remote control tools) and reporting them to the server.
7. Refusing shutdown and log off requests while an exam is running.

**Some things can never be blocked, and this project is honest about that.** Ctrl+Alt+Del always
works, and Task Manager is deliberately left alone. Section 10 explains why that is the correct
choice.

There are two lockdown strengths:

| Mode | When | Strength |
|---|---|---|
| `assigned-access` | The PC has been configured by school IT as a Windows kiosk | Strong, enforced by Windows itself |
| `kiosk-fallback` | A normal PC, no special setup | Best effort, applied by the app |

On your own laptop you will always get `kiosk-fallback`. The app shows an amber badge saying so.

### 1.3 An honest warning before you start

**This C# code was written on a Mac. It has never been compiled. Not once.**

WPF (the Windows user interface framework used here) only builds on Windows. There was no Windows
machine available when the code was written, so no compiler ever checked it.

What this means for you:

* Your first `dotnet build` may show errors. **This is normal and expected.**
* The errors will almost certainly be small: a misspelled property name, a type name that is
  ambiguous, a package that resolves differently.
* The author reviewed the code twice by reading it and wrote down every uncertain spot in
  `windows/SELF-REVIEW.md`. Section 5.4 of this guide turns that list into a plain English table.
* Fixing the **first** error often makes ten other errors disappear, because one broken line can
  confuse the compiler about everything after it.

Do not panic when you see a wall of red text. Work from the top. This is what real Windows
development looks like on day one.

---

## 2. Words you need to know

Read this table once. Come back to it whenever a word confuses you.

| Word | What it means |
|---|---|
| **C#** | A programming language made by Microsoft. Pronounced "C sharp". It looks a lot like Java or TypeScript. It has types, classes, `async`/`await`, and it is compiled, not interpreted. |
| **.NET** | The platform C# runs on. Think of it as "Node.js for C#": it provides the runtime plus a big standard library. .NET 8 is the version this project uses. |
| **.NET SDK** | The **developer** package. It contains the compiler, the `dotnet` command, and the runtime. You need this to build the app. |
| **.NET Runtime** | The **user** package. It only runs already built apps. It cannot compile anything. A student PC needs this, not the SDK. The flavour needed here is the ".NET Desktop Runtime", because desktop apps need extra window drawing libraries. |
| **WPF** | Windows Presentation Foundation. The toolkit used to draw the app's windows, buttons and text. It is the "React DOM" of this project. It only works on Windows. |
| **XAML** | The markup language WPF uses to describe screens. Pronounced "zammel". It is XML. It is roughly the JSX of WPF. Files end in `.xaml`. |
| **Code-behind** | Every `.xaml` file has a partner `.xaml.cs` file with the C# logic for that screen. `LoginView.xaml` pairs with `LoginView.xaml.cs`. |
| **WebView2** | A Microsoft component that embeds the Edge browser engine inside a desktop app. The exam page is displayed in it. It is basically an `<iframe>` you can control from C#. |
| **WebView2 Runtime** | The actual browser engine, installed separately on the PC. It ships inside Windows 11. It can never be bundled into your .exe. |
| **Compiler** | The program that turns your C# source text into machine readable code. If it finds a mistake it refuses and prints an error. |
| **Build** | Running the compiler over the whole project. Output goes into a `bin` folder. |
| **`dotnet`** | The command line tool from the SDK. `dotnet build`, `dotnet run`, `dotnet publish`. It is the `npm` of this world. |
| **NuGet package** | A reusable library you download, like an npm package. This project uses two: `Microsoft.Web.WebView2` and `System.Security.Cryptography.ProtectedData`. |
| **`.csproj`** | The project file. It is XML. It lists the target framework, the NuGet packages, the app name and version. It is the `package.json` of this world. |
| **`.sln`** (solution) | A file that groups one or more `.csproj` projects so an editor can open them together. `windows/AvaibeExam.sln` here. Optional for building. |
| **`.exe`** | An executable file on Windows. Double clicking it runs the program. |
| **DLL** | Dynamic Link Library. A file of compiled code that an `.exe` loads at run time. A normal .NET build produces one small `.exe` plus many `.dll` files next to it. |
| **Publish** | Collecting the `.exe`, its DLLs and its config into one clean folder that is ready to copy to another PC. Different from `build`, which leaves developer clutter behind. |
| **Framework dependent** | A published app that does NOT include .NET. Small, but the target PC must have the .NET Desktop Runtime installed. |
| **Self contained** | A published app that DOES include .NET inside itself. Large (150 MB or more), but runs on a PC with no .NET at all. |
| **Single file** | A publish option that packs all the DLLs inside the one `.exe`. You get one file to copy instead of a folder. |
| **PowerShell** | The modern Windows command shell. It is what you type commands into. Scripts end in `.ps1`. It is not the same as the old `cmd.exe`. |
| **Execution policy** | A PowerShell safety setting that can refuse to run `.ps1` script files. Section 3.7 shows how to work around it safely. |
| **Administrator** | The Windows equivalent of `sudo`. Some actions (installing to Program Files) need it. This app itself never asks for it. |
| **Code signing** | Attaching a cryptographic signature to your `.exe` that proves who made it and that nobody changed it. Requires buying a certificate. |
| **SmartScreen** | The Windows feature that shows a blue "Windows protected your PC" box when you run an unknown, unsigned program. |
| **MSI** | An old but very standard Windows installer format. Enterprise tools understand it well. Built with tools like WiX. |
| **Inno Setup** | A free, simple tool that turns a folder of files into a `Setup.exe`. Much easier to learn than MSI. This project uses it. |
| **MSIX** | Microsoft's modern packaging format. Clean install and uninstall, but it requires a signing certificate even to test, and it sandboxes the app. |
| **Assigned Access / kiosk mode** | A Windows feature where IT configures a PC so a chosen account can only run chosen apps. Real, OS level lockdown. |
| **Intune** | Microsoft's cloud tool for managing many Windows PCs: pushing apps, policies and kiosk configuration to a whole school. |
| **Registry** | Windows' central settings database. A tree of keys and values, like a giant config file. The app only reads from it, never writes. |
| **Visual Studio 2022** | The big, full Microsoft IDE for Windows. Heavy (many GB), but it has a visual XAML designer and the best debugger. |
| **Visual Studio Code** | The light editor you probably already use. With the "C# Dev Kit" extension it can edit, build and debug C#. No XAML designer. |
| **Command line only** | You do not need any editor at all. The `dotnet` command and the `.ps1` scripts are enough to build and run everything. |

---

## 3. What you need to install first

Do these in order. Each one has a "verify" step. Do not skip the verify steps.

### 3.1 Windows itself

You need **Windows 11**, or **Windows 10 version 2004 (build 19041) or newer**.

**Why:** the app uses a Windows feature called `WDA_EXCLUDEFROMCAPTURE` to hide its window from
screenshots. That feature only exists from build 19041 onwards.

Press the Windows key, type `winver`, press Enter. Or run this in PowerShell:

```powershell
winver
```

A small box appears. Read the line that says `Version 23H2 (OS Build 22631.xxxx)`. The number after
`Build` must be **19041 or higher**. On Windows 11 it will be 22000 or higher, so you are fine.

**If you see a build lower than 19041 → do this:** run Windows Update until you are on a newer
build. If the PC cannot update, use a different PC. The app will still start, but screenshot
protection will silently downgrade and the preflight screen will flag it.

You also need **64 bit Windows**. Almost every PC since 2012 is 64 bit.

### 3.2 The .NET 8 SDK (required)

Download from: **https://dotnet.microsoft.com/download/dotnet/8.0**

On that page, under ".NET 8.0 SDK", choose **Windows x64 Installer**. Run it and click through.

**SDK versus Runtime, in one line:** the **SDK** builds apps, the **Runtime** only runs them. The SDK
already includes a runtime, so installing the SDK is all you need on your development PC. Student PCs
only need the Runtime.

Verify it by asking the SDK to describe itself:

```powershell
dotnet --info
```

You should see a block starting with `.NET SDK:` and a `Version:` line beginning with `8.`. Further
down you should see `Microsoft.WindowsDesktop.App 8.x.x` in the installed runtimes list. That entry
is the desktop (WPF) part, and it must be there.

**If you see `dotnet : The term 'dotnet' is not recognized` → do this:** close every PowerShell
window and open a new one. The installer adds `dotnet` to your PATH, but only new windows see the
change. If it still fails, reboot, or re-run the installer.

**If you see a version starting with `9.` or `10.` and no `8.` anywhere → do this:** install the
.NET 8 SDK as well. Multiple SDK versions live side by side happily. The project explicitly targets
.NET 8, so version 8 must be present.

### 3.3 WebView2 Runtime (required, usually already there)

**On Windows 11 it is already installed.** It ships with the OS. You almost certainly do not need to
do anything.

On Windows 10, it is installed with Microsoft Edge, so it is also usually present.

Check whether the registry knows about it:

```powershell
Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" -Name pv -ErrorAction SilentlyContinue
```

If it prints a `pv` value such as `127.0.2651.98`, the runtime is installed and that is its version.

**If it prints nothing → do this:** download the "Evergreen Bootstrapper" from
**https://developer.microsoft.com/microsoft-edge/webview2/** and run it. It is a small downloader
that fetches and installs the real runtime.

The app's Preflight screen also shows the detected WebView2 version, so you get a second chance to
notice a problem.

### 3.4 Node.js (required, for the mock server)

Download the **LTS** version from **https://nodejs.org** and install it.

Verify:

```powershell
node --version
```

You should see `v18.x.x` or higher. Node 18 is the minimum for the mock server.

**If you see "not recognized" → do this:** open a new PowerShell window (same PATH reason as .NET).

### 3.5 An editor (optional but recommended)

You can do everything in this guide with PowerShell alone. But an editor helps a lot when you have
to fix compiler errors.

Pick one:

| Option | Download | Good for | Cost in disk space |
|---|---|---|---|
| **Visual Studio 2022 Community** | https://visualstudio.microsoft.com/vs/community/ | Best C# and XAML experience: visual designer, excellent debugger, error list you can click | 10 GB or more |
| **VS Code + C# Dev Kit** | https://code.visualstudio.com then install the "C# Dev Kit" extension | Familiar if you already use it for React. Edits, builds and debugs C#. No XAML designer. | Under 1 GB |
| **Nothing** | | Works fine. The `dotnet` CLI prints every error with file and line number. | 0 |

If you choose Visual Studio 2022, during setup tick the workload called **".NET desktop
development"**. That single tick installs the WPF tooling and the .NET SDK for you.

**Recommendation for you:** start with VS Code plus C# Dev Kit, because it is close to what you
already know. If XAML errors become painful, install Visual Studio 2022 later.

### 3.6 Inno Setup (optional, only for section 8)

Free. Download from **https://jrsoftware.org/isdl.php** and pick the `innosetup-6.x.x.exe` file.

You only need this when you want to make a `Setup.exe`. Skip it for now if you like.

Verify after installing:

```powershell
Test-Path "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
```

It should print `True`. `ISCC.exe` is the Inno Setup **C**ompiler.

### 3.7 How to open PowerShell, and the script permission problem

**To open PowerShell:** press the Windows key, type `powershell`, press Enter.

**To open PowerShell already inside a folder:** open that folder in File Explorer, click the address
bar at the top, type `powershell`, press Enter.

Now the one thing that trips up every beginner. Windows refuses to run `.ps1` script files by
default. When you run `.\scripts\build.ps1` you may see:

```
File ...\build.ps1 cannot be loaded because running scripts is disabled on this system.
```

This is a safety feature called the **execution policy**. It stops you double clicking a malicious
script you received by email.

Fix it for **this window only** with:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
```

Nothing is printed if it works. `-Scope Process` means "only this PowerShell window, and only until
I close it". This is the safe way. It changes nothing permanently and needs no administrator rights.

**If you see "Access is denied" → do this:** you probably typed a different `-Scope`. Use exactly
`-Scope Process`. That scope never needs administrator rights.

**Do not** use `Set-ExecutionPolicy -ExecutionPolicy Unrestricted` machine wide. It weakens the PC
permanently.

Official reference:
https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_execution_policies

---

## 4. Getting the code onto the Windows PC

The code lives on a Mac today. You need it on the Windows PC. Three ways.

### 4.1 Option A: USB stick

1. On the Mac, copy the whole `Lockdown Browser` folder to a USB stick.
2. Plug the stick into the Windows PC.
3. Copy the folder to `C:\dev\lockdown-browser`.

Simple, works offline, no accounts needed.

### 4.2 Option B: Zip file

On the Mac, right click the `Lockdown Browser` folder and choose "Compress". Send yourself the
`.zip` by email, cloud drive or file share. On Windows, right click the `.zip` and choose
**Extract All**, then extract into `C:\dev\`.

**If Windows says the files are blocked → do this:** right click the `.zip` **before** extracting,
choose Properties, tick **Unblock** at the bottom, press OK, then extract. Windows marks files that
came from the internet and sometimes refuses to run scripts from them.

### 4.3 Option C: Git (best for real work)

If you push the project to GitHub, GitLab or Azure DevOps, install Git for Windows from
https://git-scm.com/download/win and then clone it.

```powershell
git clone <your-repo-url> C:\dev\lockdown-browser
```

You should see Git counting and downloading objects, then a new folder appears.

This is the option to use once you are past the first build, because it makes sending fixes back
easy.

### 4.4 Where to put it, and why the path matters

Put the project at a **short path**:

```
C:\dev\lockdown-browser
```

**Why short?** Historically Windows limited a full file path to 260 characters. .NET builds create
deeply nested folders such as
`obj\Release\net8.0-windows10.0.19041.0\win-x64\...`. If you start at
`C:\Users\YourName\OneDrive - Your Organisation\Documents\Projects\Lockdown Browser\`, you can run
out of characters and the build fails with strange "path too long" errors.

Also **avoid OneDrive folders**. OneDrive syncs files while the compiler is writing them, which
causes random "file in use" build failures.

Also **avoid spaces** in the path if you can. `C:\dev\lockdown-browser` is better than
`C:\dev\Lockdown Browser`. Spaces work, but every command then needs quotes around the path, and
that is one more thing to get wrong.

Move into the folder now:

```powershell
cd C:\dev\lockdown-browser
```

Your prompt should change to show the new folder. Check that the contents arrived:

```powershell
dir
```

You should see `docs`, `macos`, `mock-backend`, `windows` and `README.md`.

**If you see "Cannot find path" → do this:** check where the files actually landed with
`dir C:\dev` and adjust. On the Mac side, make sure you copied the folder, not just its contents.

---

## 5. Your first build

### 5.1 Move into the windows folder

Everything in this section happens inside the `windows` folder.

```powershell
cd C:\dev\lockdown-browser\windows
```

Your prompt should end with `\windows>`.

Check the files are there:

```powershell
dir
```

You should see `AvaibeExam` (a folder), `AvaibeExam.sln`, `scripts`, `installer`, `README.md` and
`SELF-REVIEW.md`.

### 5.2 Step 1: restore

**What restore means:** it reads `AvaibeExam.csproj`, finds the list of NuGet packages, downloads
them from the internet and caches them under `C:\Users\<you>\.nuget\packages`. It is exactly
`npm install`, except the cache is shared across every project on the PC.

```powershell
dotnet restore AvaibeExam\AvaibeExam.csproj
```

You should see `Determining projects to restore...` and then either `Restored ...` with a path and a
time, or `All projects are up-to-date for restore`. Both mean success.

**If you see `Unable to load the service index for source https://api.nuget.org/v3/index.json` → do
this:** you have no internet, or a corporate proxy is in the way. See section 13 for the proxy fix.

**If you see `NU1102: Unable to find package Microsoft.Web.WebView2 with version (>= 1.0.2592.51)` →
do this:** your PC is using a private NuGet feed that does not mirror that package. Add the public
feed back:

```powershell
dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org
```

### 5.3 Step 2: build

**What build means:** the compiler reads every `.cs` and `.xaml` file, checks it for mistakes, and
produces `AvaibeExam.exe` plus its DLLs inside `AvaibeExam\bin\Debug\net8.0-windows10.0.19041.0\`.

This is **the moment of truth**. This code has never been compiled before.

```powershell
dotnet build AvaibeExam\AvaibeExam.csproj -c Debug
```

The best possible outcome is a line saying `Build succeeded.` followed by a warning count, an error
count of `0`, and a time. Warnings are fine and can be ignored for now.

The likely outcome on the very first run is a list of errors. Go to section 5.4.

**If you see `NETSDK1100: To build a project targeting Windows on this operating system, set the
EnableWindowsTargeting property to true` → do this:** you are not on Windows. This project can only
be built on Windows. Move to the Windows PC.

**If you see `MSB1009: Project file does not exist` → do this:** you are in the wrong folder. Run
`cd C:\dev\lockdown-browser\windows` first and check the path with `dir`.

### 5.4 If the build shows errors (this is expected)

#### 5.4.1 How to read a C# error

A C# compiler error looks like this:

```
C:\dev\lockdown-browser\windows\AvaibeExam\Views\LoginView.xaml.cs(18,9): error CS0103: The name 'SubtitleTxt' does not exist in the current context [C:\dev\...\AvaibeExam.csproj]
```

Break it into pieces:

| Piece | Meaning |
|---|---|
| `...\Views\LoginView.xaml.cs` | Which file has the problem |
| `(18,9)` | Line 18, character 9 |
| `error` | It stops the build. (`warning` does not stop the build.) |
| `CS0103` | The error code. Every C# error has one. |
| `The name 'SubtitleTxt' does not exist...` | The human explanation |

**The error code is searchable.** Paste `CS0103` into a search engine, or go directly to
`https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0103`. Every `CSxxxx` code has a Microsoft
Learn page with that exact URL pattern.

#### 5.4.2 The golden rule: work top down

The compiler prints errors in file order, not importance order. **Fix the first error, then rebuild.**
Very often one broken line confuses the compiler about everything after it, and fixing that one line
removes twenty errors at once.

Do not read the whole list. Read the first one. Fix it. Build again.

To see only the first few errors clearly:

```powershell
dotnet build AvaibeExam\AvaibeExam.csproj -c Debug 2>&1 | Select-String "error" | Select-Object -First 10
```

This prints at most the first ten lines that contain the word `error`, which is much easier to read
than the full output.

#### 5.4.3 The specific things the reviewers were unsure about

The author reviewed this code twice without a compiler and wrote down every spot they were not
100 percent sure of. The full technical list is in `windows/SELF-REVIEW.md` section 2 and section 4.
Here it is in plain words.

| If you see this error | What it really means | What to do |
|---|---|---|
| `CS0104: 'UserControl' is an ambiguous reference between 'System.Windows.Controls.UserControl' and 'System.Windows.Forms.UserControl'` (also for `Brushes`, `Color`, `Window`, `Button`, `TextBox`, `Application`, `MessageBox`, `KeyEventArgs`) | Two libraries define a type with the same name, and the compiler cannot guess which one you meant. The project uses WPF, but it also references Windows Forms for one small thing (counting monitors). | Open `AvaibeExam\AvaibeExam.csproj` and confirm the line `<ImplicitUsings>disable</ImplicitUsings>` is present. It already should be. If the error persists, write the full name at the error location, for example `System.Windows.Controls.UserControl`. |
| `CS1061: 'WebView2' does not contain a definition for 'AllowExternalDrop'` | The WebView2 library version on your PC is older than the one the code expects. This property blocks dragging a file into the exam page. | Delete that one line in `AvaibeExam\Browser\ExamWebView.cs`. Dragging is still blocked by the full screen kiosk window, so nothing important is lost. |
| `CS1061` or `CS0117` about `HiddenPdfToolbarItems` or `CoreWebView2PdfToolbarItems` (`Save`, `Print`, `SaveAs`, `FullScreen`, `MoreSettings`) | Those flag names hide buttons in the built in PDF viewer. A name may differ in your WebView2 version. | Delete the whole assignment in `ExamWebView.cs`. Downloads and printing are already blocked elsewhere. |
| `CS1061` about `CoreWebView2Settings.UserAgent` | The code sets a custom browser identity string. It should exist in any recent WebView2. | Delete the line if it errors. Nothing depends on it. |
| `CS1061` about `CoreWebView2ProcessFailedEventArgs.ExitCode` | Only used to write a number into the log file when the browser process crashes. | Delete the line. |
| `CS1739` or `CS1503` about `CoreWebView2Environment.CreateAsync` | The code calls a function with named arguments and one name may differ. | Pass the values positionally instead, without the `name:` prefixes. |
| `NU1605`, or a warning about `System.Security.Cryptography.ProtectedData` conflicting with the framework | This package encrypts the stored enrollment token. Some .NET installs already include it, so referencing it explicitly can produce a duplicate warning. | If it is only a **warning**, ignore it. If it is an **error**, delete the `<PackageReference Include="System.Security.Cryptography.ProtectedData" ... />` line from `AvaibeExam.csproj` and restore again. |
| `CS4008` / `CS1061` about awaiting `Dispatcher.InvokeAsync` | The code awaits a Windows UI helper directly. It should work, but if the compiler disagrees it needs one extra word. | At the error location, change `await Dispatcher.InvokeAsync(...)` to `await Dispatcher.InvokeAsync(...).Task`. |
| `CS0234` / `CS0246` about `System.Windows.Forms.Screen` | The Windows Forms reference did not come through. It is used only to count monitors. | Confirm `<UseWindowsForms>true</UseWindowsForms>` is in `AvaibeExam.csproj`. It already should be. |
| A XAML error mentioning `MC3072`, `MC3000` or "The property 'X' does not exist" | A typo or a style key used in a `.xaml` file that does not exist in `App.xaml`. | Open the named `.xaml` file at the named line. Compare the `StaticResource` key with the keys defined in `AvaibeExam\App.xaml`. |

Two more notes from the review that are **not** build errors but runtime behaviour you should check
by hand later, on real hardware:

1. Whether the screenshot protection also blacks out the WebView2 area, not just the window frame.
2. Whether Win+L is actually swallowed on your Windows build. It may not be. That is documented and
   accepted.

Both are in the manual test plan at `docs/WINDOWS-TEST-PLAN.md`.

### 5.5 Step 3: the build script

Once `dotnet build` succeeds, use the project's own script. It does restore, then build, then
publish, in one go.

First allow scripts in this window (see section 3.7):

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
```

Nothing is printed. Now run the build script:

```powershell
.\scripts\build.ps1
```

You should see `Using .NET SDK 8.x.x`, then `==> dotnet restore`, `==> dotnet build (Release)`,
`==> dotnet publish (win-x64, framework-dependent, not single-file)`, and finally a line
`Published: C:\dev\lockdown-browser\windows\publish\AvaibeExam.exe`.

**What publish means, and why it is different from build:** `build` leaves output mixed with
developer files in `bin\`. `publish` copies only what is needed to run the app into one clean
folder, `windows\publish\`. That folder is what you would give to someone else.

Look at what it produced:

```powershell
dir publish
```

You should see `AvaibeExam.exe`, several `.dll` files, `AvaibeExam.runtimeconfig.json`, and a folder
called `runtimes` (that one holds `WebView2Loader.dll`, the native part of WebView2).

**If you see "running scripts is disabled on this system" → do this:** you skipped the
`Set-ExecutionPolicy` command above. Run it, then try again.

**If you see "dotnet SDK not found" → do this:** open a new PowerShell window so the PATH is
refreshed, then re-run.

---

## 6. Running it for the first time

### 6.1 Read this box first: how to escape if you feel stuck

The app makes itself full screen, always on top, and swallows most keyboard shortcuts. The first
time that happens it feels alarming. **You are never actually trapped.** Three ways out, in order of
preference:

1. **Release it properly from the teacher console.** Open `http://localhost:4000/admin` (on another
   device, or before you start), find the session, and press **Release**. The app unlocks within a
   few seconds and shows the Exit screen.
2. **Press Ctrl+Alt+Del, choose Task Manager, find `AvaibeExam.exe`, press End task.**
   Ctrl+Alt+Del is handled by Windows itself before any app can see it, so it **always** works. This
   project deliberately does not disable Task Manager. When the process ends, the keyboard hook, the
   always on top window and the shutdown block all disappear instantly with it.
3. **Wait.** The `DEMO` exam lasts only **5 minutes**. When time runs out the client auto submits and
   the server releases it by itself.

There is also a safety switch for unattended testing. This version force quits after 120 seconds no
matter what:

```powershell
.\scripts\run.ps1 -AutoRun -Token SCHOOL-DEMO -Student 1025 -Exam DEMO -ExitAfter 120
```

You should see a yellow warning telling you the client will lock the screen automatically, then the
app starts, fills the login form by itself, and exits after two minutes.

Use that the first time if you are nervous. Have the admin page open on your phone or a second
device.

### 6.2 Step 1: start the mock server

Open a **second** PowerShell window and leave it open. This one runs the server.

```powershell
cd C:\dev\lockdown-browser\mock-backend
```

Start the server:

```bash
node server.js
```

You should see a line saying the mock backend is listening on port 4000. Leave this window running.
Every request the app makes will be logged here, which is very useful.

**If you see `Error: listen EADDRINUSE: address already in use :::4000` → do this:** something else
is on port 4000. See section 13 for how to find and stop it, or start the server on another port
with `$env:PORT=5000; node server.js` and use `http://localhost:5000` everywhere below.

**If you see `'node' is not recognized` → do this:** open a new PowerShell window, or reinstall
Node.js.

Note: there is **no `npm install` step**. The mock server has zero dependencies on purpose.

### 6.3 Step 2: open the teacher console

Open the admin page in your normal browser (Edge or Chrome, not the app):

```powershell
start http://localhost:4000/admin
```

Your browser opens a page titled with the mock console. Right now the session list is empty. Keep
this tab open. It refreshes itself every 3 seconds.

### 6.4 Step 3: start the app

Go back to your **first** PowerShell window, the one in the `windows` folder.

```powershell
.\scripts\run.ps1
```

You should see it build, then `Starting C:\dev\lockdown-browser\windows\publish\AvaibeExam.exe`, then
a line telling you where the logs are. A window appears showing the **Login** screen: a card with the
title "Avaibe Exam", a Backend URL box, an Enrollment token box, a Student code box and an Exam code
box.

**If nothing appears and the PowerShell window just returns → do this:** the app crashed at startup.
Read the log, see section 13.

### 6.5 Step 4: log in

On the Login screen, type these values:

| Field | Value | Meaning |
|---|---|---|
| Backend URL | `http://localhost:4000` | Where the server is. Already filled in by default. |
| Enrollment token | `SCHOOL-DEMO` | Identifies the school. Only needed the first time on this PC. |
| Student code | `1025` | The student's ID. The mock accepts any value. |
| Exam code | `DEMO` | Which exam. `DEMO` is 5 minutes. `MATH101` is 60. `SCI202` is 45. |

Press **Continue**.

The app contacts the server, registers this device, and moves to the **Preflight** screen.

**If you see a red error about not being able to reach the server → do this:** check the server
window from step 6.2 is still running, and that the URL has no typo. Try
`Invoke-WebRequest http://localhost:4000/healthz` in PowerShell; it should return status 200.

### 6.6 Step 5: the preflight screen

You now see a checklist. Each row is one readiness check: Windows version, Secure Boot, MDM, account
type, displays, screen sharing, Assigned Access, camera, microphone, internet, client version,
WebView2, physical machine, local session.

Rows marked **required** must pass. Rows that are only informational can fail without blocking you.

At the bottom is the **Start Exam** button. It becomes clickable when every required row passes and
the server accepted the session.

Press **Start Exam**.

**If Start Exam stays greyed out → do this:** read which required row has a cross. The most common
one on a laptop with an external monitor is the display check. Unplug the second monitor and press
**Re-run checks**. Alternatively open the admin console Policy panel and set
`externalDisplayAction` to `WARN`.

**If you see a red box saying `PREFLIGHT_FAILED` → do this:** the **server** refused, not the client.
The box lists the reasons. Open the admin console Policy panel and relax the matching setting, then
press **Re-run checks**.

### 6.7 Step 6: the exam screen (lockdown is now on)

The screen changes dramatically:

1. The window goes full screen and covers the taskbar.
2. Any second monitor turns black.
3. A status strip at the top shows the exam name, the countdown timer, and a badge saying
   **Kiosk fallback** in amber. Amber is correct here: you are not on a school configured kiosk PC.
4. Below the strip, the exam page loads with five multiple choice questions.
5. Alt+Tab, the Windows key, PrintScreen, F12 and friends now do nothing. If you press one, the app
   quietly records a `BLOCKED_SHORTCUT` event and the teacher console shows it.

Switch to the admin console on your other device. You now see one live session, with a green risk
level, a heartbeat age counting in seconds, and an expandable **Events** timeline.

### 6.8 Step 7: release the student

In the admin console, find your session. You have three buttons.

**The realistic path (release code):**

1. Press **Release code**. A 6 digit number appears with a 60 second countdown.
2. In the app, press **Request exit** on the exam page.
3. A native overlay appears asking for the code.
4. Type the 6 digits.
5. Lockdown releases.

**The quick path:** just press **Remote release** in the admin console. The next heartbeat (within a
few seconds) carries the release command and lockdown ends.

**The automatic path:** answer the questions and press Submit, or simply wait 5 minutes for the DEMO
exam to run out. Either way the server queues a release by itself.

### 6.9 Step 8: the exit screen

The window returns to a normal size. It says **"Lockdown released. You may close this app."** with a
**Quit** button.

Press **Quit**. The app closes.

This is the only place quitting is allowed once a session has started. That is deliberate.

### 6.10 Optional: running the server on the Mac instead

You can keep the mock server on your Mac and point the Windows app at it. Two extra things are
needed.

**A. Find the Mac's IP address.** On the Mac, run `ipconfig getifaddr en0`. You get something like
`192.168.1.20`.

**B. Tell the app to use it.** On the Windows Login screen, type `http://192.168.1.20:4000` as the
Backend URL. Or pass it to the run script:

```powershell
.\scripts\run.ps1 -BaseUrl http://192.168.1.20:4000
```

The app starts with that URL pre-filled.

**C. Allow that address in the policy.** This is the step people forget. The app refuses to load any
web page whose domain is not on an allow list. The mock's default list contains only `localhost` and
`127.0.0.1`. Your Mac's IP is neither, so the exam page would be refused with a
`BLOCKED_NAVIGATION` event.

Open `http://192.168.1.20:4000/admin` in a browser, find the **Policy** panel, and add `192.168.1.20`
to the `allowedDomains` field (it is a comma separated list). Save. Then start the exam.

**D. Allow the connection through the Mac firewall.** macOS may block incoming connections on port
4000. System Settings, Network, Firewall, Options, allow incoming connections for `node`.

**If the app says the server is unreachable → do this:** from the Windows PC, run
`Invoke-WebRequest http://192.168.1.20:4000/healthz`. If that also fails, the problem is the network
or the Mac firewall, not the app.

---

## 7. Making the .exe file, explained

This is the part you asked about most, so it gets its own careful section.

### 7.1 The three kinds of output

| What | Command | Needs .NET installed on the other PC? | Rough size |
|---|---|---|---|
| **Debug build** (for you, while developing) | `dotnet build -c Debug` | Yes | A folder, a few MB |
| **Release publish, framework dependent** (the normal way to ship) | `.\scripts\build.ps1` | **Yes**, the .NET 8 **Desktop** Runtime | A folder, a few MB |
| **Release publish, self contained, single file** (one portable file) | `.\scripts\publish-standalone.ps1` | **No** | One file, roughly 80 to 180 MB |

All three still need the **WebView2 Runtime** on the target PC. That is a Microsoft component and can
never be bundled inside your exe. It is part of Windows 11, so in practice it is already there.

### 7.2 bin\Debug versus bin\Release versus publish

| Folder | Who makes it | What it is |
|---|---|---|
| `AvaibeExam\bin\Debug\net8.0-windows10.0.19041.0\` | `dotnet build -c Debug` | Developer output. Includes `.pdb` debug symbol files. Compiler optimisations are **off** so the debugger can show you exact line numbers. Slower. |
| `AvaibeExam\bin\Release\net8.0-windows10.0.19041.0\` | `dotnet build -c Release` | Same layout, but optimisations are **on** and there are fewer debug aids. Faster. |
| `windows\publish\` | `dotnet publish` (via `build.ps1`) | The clean, shippable set. Only the files needed to run. This is what you copy or package. |
| `AvaibeExam\obj\` | every build | Scratch space for the compiler. Never ship it. Never commit it. Safe to delete. |

Rule of thumb: **build for yourself, publish for others.**

### 7.3 Framework dependent, explained

This is what `.\scripts\build.ps1` produces.

```powershell
.\scripts\build.ps1
```

You get `windows\publish\AvaibeExam.exe` plus a pile of DLLs. The exe is tiny, about 150 KB. All the
real code is in the DLLs next to it, and .NET itself is **not** included.

**Why choose it:** small downloads, and when Microsoft patches a .NET security bug the fix applies to
your app automatically because the runtime is shared.

**The catch:** the target PC must have the **.NET 8 Desktop Runtime** installed. Not the plain .NET
runtime, the *Desktop* one, because WPF needs the extra window drawing libraries. It comes from the
same page: https://dotnet.microsoft.com/download/dotnet/8.0

If .NET is missing, double clicking the exe shows a dialog offering to download it. That dialog
confuses students, which is why schools normally push the runtime out with Intune first.

### 7.4 Self contained and single file, explained

This is what the new script produces.

```powershell
.\scripts\publish-standalone.ps1
```

You should see `==> dotnet publish (win-x64, self-contained, single file, compressed)`, then after a
minute or two:

```
Published (self-contained, single file): C:\dev\lockdown-browser\windows\publish-standalone\AvaibeExam.exe
Size    : 92.4 MB
SHA-256 : 9F2A...
```

(The exact size depends on your .NET 8 patch level. Expect somewhere between 80 MB and 180 MB.
Compression, which this script turns on, roughly halves it.)

What the four options in that script mean:

| Option | Plain English |
|---|---|
| `--self-contained true` | Copy the whole .NET 8 runtime into the output. The PC does not need .NET. |
| `-p:PublishSingleFile=true` | Pack all the DLLs inside the one `.exe`. At start up they are unpacked into a temp folder automatically. |
| `-p:IncludeNativeLibrariesForSelfExtract=true` | Also pack the native (non .NET) DLLs, such as `WebView2Loader.dll`. Without this they stay as loose files next to the exe. |
| `-p:EnableCompressionInSingleFile=true` | Compress the packed content. Saves roughly a third of the size, costs a fraction of a second at start up. |

**How to use it:** copy that single `AvaibeExam.exe` to a USB stick, plug it into any 64 bit Windows
10 build 19041+ or Windows 11 PC, and double click it. That is all. Nothing to install.

**The `SHA-256` value** printed at the end is a fingerprint of the file. If you send the exe to
someone, send the fingerprint separately. They can run
`Get-FileHash .\AvaibeExam.exe -Algorithm SHA256` and compare. If the numbers match, the file was not
tampered with in transit.

**If you see `NETSDK1047: Assets file ... doesn't have a target for 'net8.0-windows10.0.19041.0/win-x64'` → do this:** run the publish command again without `--no-restore`. The script already does
this, so if you hit it you are probably running `dotnet publish` by hand.

**If the exe takes 5 or more seconds to start the first time → this is normal.** A compressed single
file exe has to unpack itself on first run. Later runs reuse the unpacked copy and are fast.

Reference: https://learn.microsoft.com/en-us/dotnet/core/deploying/

### 7.5 Which one should you use?

| Situation | Use |
|---|---|
| You are developing | `dotnet build -c Debug`, or `.\scripts\run.ps1` |
| Quick demo to a colleague, one PC, no IT involved | `.\scripts\publish-standalone.ps1`, copy one file |
| Real school deployment through IT | `.\scripts\build.ps1` plus the installer in section 8, with the .NET Desktop Runtime pushed separately |

---

## 8. Making a real installer (Setup.exe)

### 8.1 What an installer actually does

Copying an exe works, but it is not how software is normally delivered on Windows. A proper
installer:

1. Copies files into `C:\Program Files\...`, a place ordinary users cannot modify. That is a security
   feature, not an inconvenience.
2. Creates a **Start menu shortcut** so students can find the app by typing its name.
3. Optionally creates a desktop shortcut.
4. Registers the app in **Settings > Apps > Installed apps** so it can be uninstalled cleanly.
5. Can check for prerequisites (here, the WebView2 runtime) and install them.
6. Can be run **silently** by IT, with no clicking, on hundreds of PCs.

### 8.2 What Inno Setup is

Inno Setup is a free program that reads a recipe file and produces a `Setup.exe`. The recipe file
ends in `.iss` and is mostly plain text sections with names in square brackets. It is by far the
easiest real installer tool to learn.

This project's recipe lives at `windows\installer\AvaibeExam.iss`. It is heavily commented. Open it
and read it; you will understand most of it immediately.

What it is configured to do:

| Setting | Value | Why |
|---|---|---|
| App name | Avaibe Exam | Shown in the wizard and the uninstall list |
| Version | 0.1.0 | Matches `AvaibeExam.csproj` |
| Publisher | Avaibe | Shown in the uninstall list |
| Install folder | `C:\Program Files\Avaibe\Avaibe Exam` | All users, standard location |
| Privileges | Administrator | Required to write to Program Files |
| Minimum Windows | Build 19041 | Screenshot protection needs it |
| Architecture | 64 bit | Matches the `win-x64` build |
| Files taken from | `windows\publish\` including sub-folders | That is what `build.ps1` produces |
| Start menu shortcut | Always | So students can find it |
| Desktop shortcut | Optional tick box, off by default | Some schools want it, some do not |
| WebView2 | Checked, and installed if missing | The app cannot show the exam page without it |
| Output | `windows\installer\output\AvaibeExam-Setup-0.1.0.exe` | |

### 8.3 Install Inno Setup

Download from **https://jrsoftware.org/isdl.php**, pick `innosetup-6.x.x.exe`, run it, click through
with the defaults.

Verify:

```powershell
Test-Path "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
```

It should print `True`.

### 8.4 Optional: bundle the WebView2 installer

If you want the Setup.exe to install WebView2 automatically when it is missing, download the
**Evergreen Bootstrapper** from https://developer.microsoft.com/microsoft-edge/webview2/ and save it
as `windows\installer\MicrosoftEdgeWebview2Setup.exe`.

If you skip this, the installer still works. It just shows a message telling the user where to get
WebView2, instead of installing it silently.

### 8.5 Build the installer

```powershell
.\scripts\make-installer.ps1
```

You should see the app build first, then `Using Inno Setup compiler: ...`, then compiler output, and
finally:

```
Installer: C:\dev\lockdown-browser\windows\installer\output\AvaibeExam-Setup-0.1.0.exe
Size     : 14.2 MB
SHA-256  : 3B7C...

Silent install (for school IT / Intune):
    "AvaibeExam-Setup-0.1.0.exe" /VERYSILENT /NORESTART
```

**If you see "Inno Setup compiler (ISCC.exe) not found" → do this:** install Inno Setup 6 from
https://jrsoftware.org/isdl.php. The error message lists the exact folders that were searched.

**If you see "Nothing to package: ...\publish\AvaibeExam.exe does not exist" → do this:** the app
build failed. Run `.\scripts\build.ps1` on its own and fix the compiler errors first.

**If ISCC reports `Unknown [Setup] directive: ArchitecturesInstallIn64BitMode` or complains about the
value `x64compatible` → do this:** you have Inno Setup 6.0 to 6.2. Open
`windows\installer\AvaibeExam.iss`, find that line, and change `x64compatible` to `x64`. A comment in
the file points this out.

### 8.6 Test the installer

Double click the produced `AvaibeExam-Setup-0.1.0.exe`. Windows asks for administrator permission,
then a wizard appears. Click through it. When it finishes, press the Windows key and type "Avaibe";
the app should appear in the results.

To uninstall, go to Settings, Apps, Installed apps, find "Avaibe Exam", and choose Uninstall.

### 8.7 How school IT would install it on many PCs

IT never clicks through wizards. They run it silently:

```powershell
.\AvaibeExam-Setup-0.1.0.exe /VERYSILENT /NORESTART
```

Nothing is shown at all, and the exit code tells the deployment tool whether it worked. `/VERYSILENT`
hides the whole wizard. `/NORESTART` stops it rebooting the PC by itself.

For hundreds of PCs, this same command goes into **Intune** as a Win32 app. The full enterprise
procedure is in `docs/WINDOWS-DEPLOYMENT.md`.

### 8.8 Inno Setup versus MSI versus MSIX

| Format | Made with | Strengths | Weaknesses | Use it when |
|---|---|---|---|---|
| **Inno Setup** (`Setup.exe`) | Inno Setup 6, free | Easy to learn, one file, full scripting, silent switches | Not a native Windows package format; some enterprise tools prefer MSI | Getting started, pilots, direct downloads. **This project today.** |
| **MSI** | WiX Toolset, free | The enterprise standard. Group Policy and Intune understand it natively. Reliable repair and rollback. | Much steeper learning curve. XML heavy. | A school's IT department asks for an MSI, or you deploy by Group Policy |
| **MSIX** | Visual Studio or MakeAppx | Modern, clean uninstall, automatic updates, no leftover files | Needs a signing certificate even to test. Sandboxes the app, which can conflict with low level lockdown hooks. | Microsoft Store distribution, or a fully modern managed fleet |

**Practical advice:** stay with Inno Setup until a school's IT team specifically asks for an MSI. If
they do, `docs/WINDOWS-DEPLOYMENT.md` covers the MSI and Intune path.

---

## 9. Code signing and SmartScreen, explained simply

### 9.1 Why Windows shows a scary blue box

Run your new exe on a fresh PC and you will probably see:

```
Windows protected your PC
Microsoft Defender SmartScreen prevented an unrecognized app from starting.
Running this app might put your PC at risk.
                                                   [Don't run]
```

There is a small **More info** link. Clicking it reveals a **Run anyway** button.

This is **SmartScreen**. Windows has never seen this file before. It does not know who made it. So it
warns.

Two facts make this a real problem for an exam product:

1. Students will not trust it. Some will refuse to click "Run anyway", quite reasonably.
2. Some schools configure Windows to remove the "Run anyway" option entirely. Then the app simply
   cannot start.

### 9.2 What a code signing certificate is

A **code signing certificate** is a cryptographic identity issued to your company by a Certificate
Authority (a company Windows already trusts, such as DigiCert or Sectigo). After they verify your
company is real, you get a private key. You use it to attach a signature to your exe.

The signature proves two things:

1. **Who** published the file (your company name appears in the warning, or the warning disappears).
2. That **nobody modified** the file since you signed it.

Two honest facts about the cost:

* It is **not free**. Expect roughly 200 to 600 US dollars per year, depending on the CA and the
  certificate type.
* Since **June 2023** the private key must live on **hardware**: a USB token the CA ships to you, or
  a cloud HSM service. You cannot just download a `.pfx` file any more. This makes automated CI
  builds noticeably harder to set up.

### 9.3 How to sign, once you have a certificate

Install the certificate (or plug in the token), then find its **thumbprint**, a 40 character
identifier:

```powershell
Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Format-List Subject, Thumbprint
```

You should see your company name and a long hexadecimal `Thumbprint`.

Now build with signing turned on:

```powershell
.\scripts\build.ps1 -Sign -CertThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
```

You should see `==> signing ...\publish\AvaibeExam.exe`, then the normal Published line. The script
signs `AvaibeExam.exe` and the project's own DLLs using `signtool.exe` from the Windows SDK.

Sign the installer too:

```powershell
.\scripts\make-installer.ps1 -Sign -CertThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
```

Both scripts also pass a **timestamp URL** (`http://timestamp.digicert.com` by default). Timestamping
records *when* you signed. Without it, your signature stops being trusted the day the certificate
expires. With it, files signed while the certificate was valid stay trusted forever. Always
timestamp.

**If you see "signtool.exe not found" → do this:** install the Windows SDK, or the "Desktop
development with C++" workload in Visual Studio, which includes it.

### 9.4 Reputation, and the honest summary

Signing alone does not remove the warning immediately. SmartScreen also tracks **reputation**: how
many people have downloaded and run files signed by your certificate without trouble. Reputation
builds over days and weeks of real downloads. Extended Validation (EV) certificates used to grant
instant reputation; that is no longer the case.

So the honest position for this project today:

* **Unsigned** (where you are now): every student sees the blue warning. It works, but it looks
  unprofessional and some managed PCs will block it outright.
* **Signed, new certificate**: the warning names your company, which is far better, and it fades away
  as reputation accumulates.
* **Signed, established certificate**: no warning.

A useful workaround while you are piloting: deploy through **Intune or a school's software
deployment system** rather than asking students to download the file. Software installed by IT does
not go through the SmartScreen download reputation check.

More on certificate options:
https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options

---

## 10. School computers: real kiosk mode

### 10.1 Assigned Access

Everything the app does on its own is **best effort**. It is one normal program asking Windows
politely to stay on top and to swallow some keys. Another program with the same rights can work
around it.

**Assigned Access** is different. It is a Windows feature where an administrator configures the PC so
that one chosen account can only run a chosen list of apps. Windows itself enforces it, from below.
The Start menu and taskbar are removed, File Explorer is restricted, and roughly 45 policies are
applied (Task Manager removed from the Ctrl+Alt+Del screen, notifications off, Run box off, and so
on). The app detects when it is running inside such a session and reports `lockdownMode:
assigned-access` to the server, and the student sees a green badge instead of the amber one.

Requirements, stated plainly:

1. **Windows Pro, Education or Enterprise.** Windows **Home cannot do this**. Student owned laptops
   are usually Home, so BYOD always runs in `kiosk-fallback`.
2. **An IT administrator** must configure it. You cannot turn it on from inside the app, and the app
   never tries.
3. **A standard (non administrator) local account** for the student. Assigned Access is simply not
   applied to administrator accounts.

Reference: https://learn.microsoft.com/en-us/windows/configuration/assigned-access/

### 10.2 Intune, for many PCs

Configuring one lab PC by hand is fine. Configuring three hundred is not. **Intune** is Microsoft's
cloud management service: you define the app, the kiosk configuration and the policies once, assign
them to a group of devices, and every PC in that group receives them. It also reports which PCs
succeeded. This is how the installer from section 8 would reach a whole school, and how the .NET
Desktop Runtime and WebView2 would be pushed out ahead of it. The complete, step by step enterprise
procedure (Autopilot, provisioning packages, the Assigned Access XML, the Intune Win32 app settings)
is in **`docs/WINDOWS-DEPLOYMENT.md`**. The honest capability matrix of what each mode can and cannot
enforce is in **`docs/WINDOWS-LIMITATIONS.md`**.

### 10.3 What can never be blocked, and why that is correct

State this plainly to any school that asks:

1. **Ctrl+Alt+Del can never be blocked** by an ordinary application. It is the Secure Attention
   Sequence. Windows handles it inside Winlogon, before any program can see the keystroke. That is a
   deliberate security design: it guarantees that the screen you see after pressing it is really
   Windows and not a fake login box drawn by malware. Only the Keyboard Filter feature, which exists
   only on Windows Enterprise, Education and IoT Enterprise, can suppress it, and that is an IT
   decision, not an app decision.
2. **Task Manager is deliberately left working** by this app. The app could technically try to
   disable it through a registry policy. It does not, on purpose.

Why leaving it working is the right choice:

* **Safety.** If the app freezes while a student is locked in, with no working escape the student is
  trapped on a locked machine during a timed exam. That is a far worse outcome than a student who
  cheats by ending the process.
* **Honesty.** Ending the process does not hide anything. The heartbeats stop immediately, and the
  teacher console shows the session going stale within seconds. The cheat is detected, loudly.
* **Trust.** Exam software that silently changes system settings on a student's personal laptop is
  the kind of software that gets flagged by antivirus and written about in the press. This project
  deliberately writes no registry policies, installs no services, creates no scheduled tasks, and
  never requests administrator rights.

The product rule behind all of this is: **never claim impossible prevention.** Report honestly what
mode you are in, block what you genuinely can, detect and report the rest, and let invigilation cover
what software cannot.

Also permanently outside software's reach: photographing the screen with a phone, an HDMI capture
box between the PC and the monitor, and the power button.

---

## 11. Which parts of the code do what

### 11.1 The folders

All under `windows\AvaibeExam\`.

| Folder | One sentence |
|---|---|
| `App/` | Constants (versions, paths, environment variable names) and `AppState`, the state machine that decides which screen is showing and what happens next. |
| `Models/` | Plain C# classes that mirror every JSON object the server sends or receives, plus the JSON converters. |
| `Networking/` | Talking to the server: `ApiClient` (every HTTP endpoint), `HeartbeatService` (the periodic "I am alive" ping that also fetches commands), `EventReporter` (the queue that batches telemetry events). |
| `Lockdown/` | The lockdown engine: `LockdownCoordinator` turns it on and off, `KioskFallback` applies the window and screen controls, `KeyboardHook` swallows shortcuts, `AssignedAccessDetector` works out whether the PC is a real kiosk, `ProcessMonitor` watches for banned programs. |
| `Security/` | The readiness checks (`Preflight`), the device identity and encrypted token store (`DeviceIdentity`), and monitors for displays and network. |
| `Browser/` | The embedded browser: `ExamWebView` configures WebView2 and enforces the navigation rules, `WebMessageBridge` handles messages passed between the exam web page and the native app. |
| `Views/` | The screens themselves, as `.xaml` markup plus `.xaml.cs` code: Login, Preflight, Exam, Exit, and the Warning, Pause and Exit overlays. |
| `Util/` | Small helpers: `Log` (writes the daily log file), `SemVer` (version comparison), `NativeMethods` (the declarations that let C# call Windows system functions), `ObservableObject` (change notification for the UI). |

Plus three files at the top level: `App.xaml.cs` (the start up code), `MainWindow.xaml.cs` (the single
window that swaps screens in and out), and `AvaibeExam.csproj` (the project file).

### 11.2 Snippet 1: swallowing the Windows key

From `windows\AvaibeExam\Lockdown\KeyboardHook.cs`:

```csharp
// Windows keys: swallow completely so no Win+X combination reaches the shell.
if (vk == NativeMethods.VK_LWIN || vk == NativeMethods.VK_RWIN)
{
    if (down) { _winDown = true; Report("Win"); }
    if (up) _winDown = false;
    return new IntPtr(1);
}
```

Line by line:

1. `vk` is the "virtual key code", a number Windows uses to identify a key. `VK_LWIN` and `VK_RWIN`
   are the left and right Windows keys.
2. This code runs inside a **low level keyboard hook**. Windows calls it for every single keystroke
   on the whole PC, **before** the key reaches any application.
3. If the key was pressed down, remember that fact in `_winDown` and record a `BLOCKED_SHORTCUT`
   event through `Report`.
4. If it was released, clear the flag.
5. `return new IntPtr(1)` is the important line. Returning a non zero value tells Windows: **"this
   keystroke has been handled, do not pass it on."** The key never reaches the taskbar, so
   Win+D, Win+E, Win+R and all the others simply do not happen.

### 11.3 Snippet 2: the browser shortcut block list

Also from `KeyboardHook.cs`, inside the method that classifies a key press:

```csharp
case 0x4E: return shift ? "Ctrl+Shift+N" : "Ctrl+N";   // new window
case 0x54: return shift ? "Ctrl+Shift+T" : "Ctrl+T";   // new tab
case 0x57: return "Ctrl+W";                            // close
case 0x4F: return "Ctrl+O";                            // open file
case 0x53: return "Ctrl+S";                            // save page
case 0x55: return "Ctrl+U";                            // view source
case 0x48: return "Ctrl+H";                            // history
case 0x4A: return "Ctrl+J";                            // downloads / console
case 0x49: if (shift) return "Ctrl+Shift+I"; break;    // dev tools
case 0x50: if (!AllowPrinting) return "Ctrl+P"; break;
```

Line by line:

1. This is a `switch` over the virtual key code, reached only when Ctrl is being held.
2. `0x4E` is hexadecimal for 78, which is the letter N. Windows virtual key codes for A to Z are the
   same as ASCII, so `0x41` is A and `0x5A` is Z.
3. Returning a **non null string** means "block this key and record it under this name". Returning
   `null` (falling through) means "let it through".
4. `shift ? "Ctrl+Shift+N" : "Ctrl+N"` is C#'s ternary operator, identical to JavaScript.
5. The last two lines are conditional. `Ctrl+Shift+I` is only blocked when Shift is held, because
   plain `Ctrl+I` is harmless. `Ctrl+P` is only blocked when the school's policy says printing is not
   allowed; `AllowPrinting` comes from the server policy.

### 11.4 Snippet 3: hiding the window from screenshots

From `windows\AvaibeExam\Lockdown\KioskFallback.cs`:

```csharp
if (NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE))
{
    CaptureProtectionActive = true;
    CaptureProtectionLevel = "exclude-from-capture";
    Log.Info(LogCat, "Screen capture protection: WDA_EXCLUDEFROMCAPTURE");
    return;
}
var err1 = Marshal.GetLastWin32Error();
if (NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_MONITOR))
{
    CaptureProtectionActive = true;
    CaptureProtectionLevel = "monitor";
    Log.Warn(LogCat, $"WDA_EXCLUDEFROMCAPTURE failed ({err1}); using WDA_MONITOR");
    return;
}
```

Line by line:

1. `SetWindowDisplayAffinity` is a **Windows system function**, not a .NET one. C# reaches it through
   the declarations in `Util\NativeMethods.cs`. This technique is called P/Invoke.
2. `_hwnd` is the **window handle**, a number Windows uses to identify one particular window.
3. `WDA_EXCLUDEFROMCAPTURE` asks Windows to leave this window out of screen captures entirely. A
   screenshot shows whatever is behind the window. This needs Windows 10 build 19041 or newer.
4. If it worked, record that fact (the value goes to the server so teachers know the protection
   level) and stop.
5. If it failed, `Marshal.GetLastWin32Error()` fetches the Windows error number, saved for the log.
6. Then try the weaker, older option `WDA_MONITOR`, which makes the window appear as a **black
   rectangle** in screenshots instead of being invisible.
7. If both fail, code just below this (not shown) records the failure and raises a
   `SCREEN_CAPTURE_PROTECTION_UNAVAILABLE` event, and the exam continues. **The app never lies about
   what it achieved.**

### 11.5 Snippet 4: refusing to load a page

From `windows\AvaibeExam\Browser\ExamWebView.cs`:

```csharp
public bool IsAllowed(Uri url)
{
    if (string.Equals(url.OriginalString, "about:blank", StringComparison.OrdinalIgnoreCase)) return true;
    if (!url.IsAbsoluteUri) return false;
    var scheme = url.Scheme.ToLowerInvariant();
    if (scheme != "http" && scheme != "https") return false;
    var host = url.Host.ToLowerInvariant();
    if (string.IsNullOrEmpty(host)) return false;
    foreach (var pattern in _policy.AllowedDomains)
    {
        if (HostMatches(host, pattern)) return true;
    }
    return false;
}
```

Line by line:

1. The empty page `about:blank` is always allowed, because the browser control starts there.
2. Relative URLs are refused; only full addresses are considered.
3. Only `http` and `https` are allowed. This blocks `file://` (reading local files) and custom
   schemes that could launch other applications.
4. The host name is lower cased so that `EXAM.School.Ae` and `exam.school.ae` are treated the same.
5. The loop walks the allow list that came from the **server policy**, so a school can change it
   without a new build.
6. The final `return false` is the crucial one. This is a **default deny** list: anything not
   explicitly allowed is refused. That is the opposite of a block list, and it is far safer, because
   you cannot forget to add something.

And here is where that decision is applied:

```csharp
private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
{
    if (!IsAllowedString(e.Uri))
    {
        e.Cancel = true;
        Block(e.Uri, "domain-not-allowed");
    }
}
```

1. WebView2 raises `NavigationStarting` **before** it fetches anything.
2. `e.Cancel = true` stops the navigation dead.
3. `Block(...)` writes it to the log and sends a `BLOCKED_NAVIGATION` event to the server, so the
   teacher sees the attempt in the event timeline.

---

## 12. Changing something yourself (your first edit)

A tiny exercise so you can see the whole loop: edit, build, run, observe.

### 12.1 Open the file

The file is `windows\AvaibeExam\Views\LoginView.xaml`. Open it in your editor, or from PowerShell:

```powershell
notepad AvaibeExam\Views\LoginView.xaml
```

Notepad opens the file. It is XML.

### 12.2 Find the text

Search for this exact line (it is around line 8):

```
<TextBlock Style="{StaticResource TitleText}" Text="Avaibe Exam"/>
```

That `Text="Avaibe Exam"` is the big heading on the Login screen.

### 12.3 Change it

Change the text between the quotes only. Leave everything else alone:

```
<TextBlock Style="{StaticResource TitleText}" Text="Avaibe Exam (my build)"/>
```

Save the file.

### 12.4 Rebuild and run

```powershell
.\scripts\run.ps1
```

The app rebuilds and starts. The Login screen heading now reads **"Avaibe Exam (my build)"**.

You just changed a Windows desktop app. That is the entire loop.

### 12.5 Change it back

Edit the same line, remove ` (my build)`, save, and run again. The heading is back to normal.

### 12.6 A learning point, for free

Two lines below the heading there is a subtitle:

```
<TextBlock x:Name="SubtitleText" ... Text="Secure exam client for Windows"/>
```

If you change **that** text, nothing happens on screen. Why?

Because it has `x:Name="SubtitleText"`, and the C# code-behind file
`windows\AvaibeExam\Views\LoginView.xaml.cs` overwrites it at run time:

```csharp
SubtitleText.Text = "Secure exam client for Windows · v" + Constants.ClientVersion;
```

This is the single most important thing to understand about WPF: **the XAML sets the starting value,
and the C# code can replace it later.** If an element has an `x:Name`, search the `.xaml.cs` file for
that name before assuming the XAML is in charge.

**If you see an error like `MC3000: ... is not well-formed` → do this:** you accidentally deleted a
quote, a `<` or a `/>`. Undo your change (Ctrl+Z) and try again more carefully.

---

## 13. Common problems and fixes

| Problem | What you see | Fix |
|---|---|---|
| **`dotnet` not recognised** | `The term 'dotnet' is not recognized as the name of a cmdlet` | Close and reopen PowerShell so it picks up the new PATH. If it still fails, reinstall the .NET 8 SDK and reboot. |
| **Script will not run** | `...build.ps1 cannot be loaded because running scripts is disabled on this system` | Run `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass` in that window, then try again. |
| **Restore fails behind a proxy** | `Unable to load the service index for source https://api.nuget.org/v3/index.json` | Tell .NET about the proxy: `$env:HTTP_PROXY="http://proxy.school.ae:8080"` and `$env:HTTPS_PROXY="http://proxy.school.ae:8080"`, then restore again. If the proxy inspects TLS, you may also need `$env:DOTNET_SYSTEM_NET_HTTP_USESOCKETSHTTPHANDLER=0`. Ask your network admin for the correct proxy address. |
| **NuGet package not found** | `NU1102: Unable to find package Microsoft.Web.WebView2` | Your PC points at a private feed only. Add the public one: `dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org` |
| **WebView2 runtime missing** | The Preflight screen shows the WebView2 row with a cross, or the exam screen is blank white | Install the Evergreen Bootstrapper from https://developer.microsoft.com/microsoft-edge/webview2/ and restart the app. |
| **App closes immediately** | The window flashes and disappears, or nothing happens at all | Read the log. Run `notepad "$env:LOCALAPPDATA\AvaibeExam\logs\avaibe-$(Get-Date -Format yyyyMMdd).log"` and look at the last lines. Unhandled exceptions are written there with a full stack trace. |
| **Only one instance** | A box says "Avaibe Exam is already running." | An old copy is still alive. Open Task Manager, end `AvaibeExam.exe`, then start again. The app uses a named mutex to prevent two copies. |
| **Port 4000 already in use** | `Error: listen EADDRINUSE: address already in use :::4000` | Find the owner with `netstat -ano \| findstr :4000`, read the number in the last column (the process ID), then `taskkill /PID <that number> /F`. Or start the mock on another port with `$env:PORT=5000; node server.js`. |
| **Firewall blocks the Mac backend** | The app cannot reach `http://192.168.1.20:4000` | On the Mac: System Settings, Network, Firewall, Options, allow incoming connections for `node`. Test from Windows with `Invoke-WebRequest http://192.168.1.20:4000/healthz`. Make sure both machines are on the same Wi-Fi network. |
| **Exam page blocked** | The exam area shows an error and the console logs `BLOCKED_NAVIGATION` | The page's domain is not in `allowedDomains`. Open `http://<host>:4000/admin`, Policy panel, add the host (for example the Mac's IP), save, and start a new session. |
| **Screen looks locked and you want out** | Full screen, black second monitor, shortcuts do nothing | Ctrl+Alt+Del, Task Manager, `AvaibeExam.exe`, End task. Or press Release in the admin console. Or wait: the `DEMO` exam ends by itself after 5 minutes. |
| **SmartScreen warning** | Blue box: "Windows protected your PC" | Click **More info**, then **Run anyway**. To remove it permanently you need a code signing certificate. See section 9. |
| **Antivirus flags the exe** | Defender or a third party product quarantines `AvaibeExam.exe` | Expected, and fair: the app installs a global keyboard hook and makes an always on top window, which is exactly what a keylogger does. The real fix is code signing (section 9). For local testing, add a Defender exclusion for your `publish` folder. Never ask a school to disable Defender. |
| **Build error you do not recognise** | `error CSxxxx: ...` | Look up `https://learn.microsoft.com/en-us/dotnet/csharp/misc/csXXXX` with your number. Then check the table in section 5.4.3. Then read `windows/SELF-REVIEW.md`. |
| **Path too long** | `The specified path, file name, or both are too long` | Move the project to a shorter path such as `C:\dev\lb`, and out of any OneDrive folder. |
| **Nothing in the teacher console** | The session list stays empty | The app never reached the server. Check the Backend URL on the Login screen, and watch the server's PowerShell window: every request is printed there. If nothing is printed, the request never arrived. |

---

## 14. What to learn next

### 14.1 Recommended order of work

1. **Get the first build green.** Nothing else matters until `dotnet build` prints
   `Build succeeded` with 0 errors. Work top down through the errors, using section 5.4.
2. **Run the demo flow end to end** with the mock server on the same PC (section 6). Watch the events
   arrive in the teacher console. This proves the client, the contract and the server all agree.
3. **Work through `docs/WINDOWS-TEST-PLAN.md`.** It lists the behaviour checks that only a real
   Windows PC can settle: does PrintScreen really produce a black image, does the second monitor
   really go black, does Alt+Tab really do nothing.
4. **Make the standalone exe** (section 7) and try it on a second, clean PC with no .NET installed.
5. **Make the installer** (section 8) and install and uninstall it once.
6. **Only then** talk to a school's IT about Assigned Access and Intune (section 10, and
   `docs/WINDOWS-DEPLOYMENT.md`).
7. **Then** get a code signing certificate (section 9). It takes weeks of paperwork, so start the
   application early, but do not block the pilot on it.

### 14.2 C# basics

You know JavaScript, so focus on what is different: static types, `class` versus `record` versus
`struct`, `null` handling and the `?` operators, `async`/`await` (similar to JS but with real
threads underneath), and `using` directives (like `import`, but for namespaces rather than files).

* Official tour of C#: https://learn.microsoft.com/en-us/dotnet/csharp/tour-of-csharp/
* The C# guide: https://learn.microsoft.com/en-us/dotnet/csharp/

### 14.3 WPF basics

Focus on: XAML layout containers (`Grid`, `StackPanel`, `DockPanel`), the code-behind pattern, data
binding, and the `Dispatcher` (the rule that only the UI thread may touch UI objects, which is why
this code is full of `Dispatcher.InvokeAsync`).

* WPF overview: https://learn.microsoft.com/en-us/dotnet/desktop/wpf/
* XAML overview: https://learn.microsoft.com/en-us/dotnet/desktop/wpf/xaml/

### 14.4 The Microsoft pages that matter for this product

| Topic | Link |
|---|---|
| WebView2 | https://learn.microsoft.com/en-us/microsoft-edge/webview2/ |
| Assigned Access (kiosk mode) | https://learn.microsoft.com/en-us/windows/configuration/assigned-access/ |
| Shell Launcher | https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/ |
| Keyboard Filter (the only way to block Ctrl+Alt+Del) | https://learn.microsoft.com/en-us/windows/configuration/keyboard-filter/ |
| `SetWindowDisplayAffinity` (screenshot protection) | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity |
| .NET application publishing | https://learn.microsoft.com/en-us/dotnet/core/deploying/ |
| Code signing options | https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options |
| Intune Win32 app deployment | https://learn.microsoft.com/en-us/mem/intune/apps/apps-win32-app-management |
| PowerShell execution policies | https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_execution_policies |

### 14.5 The documents in this repository

| Document | Read it when |
|---|---|
| `windows/README.md` | You want the fast technical reference for the Windows client |
| `windows/SELF-REVIEW.md` | The build fails and you need the author's uncertainty list |
| `docs/WINDOWS-TEST-PLAN.md` | You are testing behaviour by hand |
| `docs/WINDOWS-LIMITATIONS.md` | A school asks "can you stop X?" and you need an honest answer |
| `docs/WINDOWS-DEPLOYMENT.md` | You are talking to an IT department about rolling this out |
| `docs/WINDOWS-ARCHITECTURE.md` | You want to know how the pieces fit together internally |
| `docs/CONTRACT.md` | You are building the real backend and need the exact API |
| `mock-backend/README.md` | You want the endpoint list and the curl examples |

### 14.6 One last piece of advice

Do not try to learn all of C#, all of WPF and all of Windows deployment before you start. Get the
build green. Run the demo. Change one string. Everything else will make far more sense once you have
seen the app actually running on your own screen.
