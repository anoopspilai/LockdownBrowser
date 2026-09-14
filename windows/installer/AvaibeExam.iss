; ============================================================================
;  Avaibe Exam - Inno Setup installer script
; ============================================================================
;
;  WHAT THIS FILE IS
;  -----------------
;  Inno Setup is a free installer maker for Windows (https://jrsoftware.org/isinfo.php).
;  This file is a recipe. You "compile" it with ISCC.exe (the Inno Setup Compiler) and
;  it produces ONE Setup.exe that a student or an IT admin can run.
;
;  WHAT THE PRODUCED SETUP.EXE DOES
;  --------------------------------
;    1. Asks for administrator rights (it installs for ALL users of the PC).
;    2. Copies everything from windows\publish\ into
;         C:\Program Files\Avaibe\Avaibe Exam\
;    3. Creates a Start-menu shortcut, and optionally a desktop shortcut.
;    4. Registers an entry in Settings > Apps > Installed apps, so the app can be
;       uninstalled the normal Windows way.
;    5. Checks whether the Microsoft Edge WebView2 runtime is present, and if it is
;       missing, silently installs it from a bundled bootstrapper (if you put one next
;       to this file - see "WEBVIEW2" below).
;
;  HOW TO BUILD IT
;  ---------------
;    1. Build the app first:      windows\scripts\build.ps1
;       (that fills windows\publish\ - this script copies from there)
;    2. Then either run:          windows\scripts\make-installer.ps1
;       or compile by hand:       ISCC.exe windows\installer\AvaibeExam.iss
;
;  The result is:  windows\installer\output\AvaibeExam-Setup-0.1.0.exe
;
;  NOTE ON THE .NET RUNTIME
;  ------------------------
;  windows\publish\ is a FRAMEWORK-DEPENDENT build: the target PC needs the
;  ".NET 8 Desktop Runtime" installed. This installer does NOT install .NET for you.
;  Either (a) push the .NET Desktop Runtime through Intune / a separate install, or
;  (b) change build.ps1 to publish self-contained so .NET is bundled. Keeping the two
;  concerns separate is deliberate and is the normal enterprise pattern.
;
;  NOTE ON CODE SIGNING
;  --------------------
;  This script does not sign anything. Sign the app binaries BEFORE compiling
;  (build.ps1 -Sign -CertThumbprint ...) and sign the produced Setup.exe afterwards
;  with signtool. Without a signature Windows SmartScreen will warn the user.
; ============================================================================


; ---- Preprocessor defines: change these in ONE place -----------------------
#define MyAppName        "Avaibe Exam"
#define MyAppVersion     "0.1.0"
#define MyAppPublisher   "Avaibe"
#define MyAppExeName     "AvaibeExam.exe"
#define MyAppDirName     "Avaibe\Avaibe Exam"

; Optional WebView2 bootstrapper. Download MicrosoftEdgeWebview2Setup.exe from
;   https://developer.microsoft.com/microsoft-edge/webview2/
; and drop it into this folder (windows\installer\). If it is not there, the script
; still compiles - it simply skips the WebView2 step and shows a message instead.
#define WebView2Bootstrapper "MicrosoftEdgeWebview2Setup.exe"
#if FileExists(AddBackslash(SourcePath) + WebView2Bootstrapper)
  #define HaveWebView2Bootstrapper
#endif


[Setup]
; AppId is the permanent identity of this product in the Windows uninstall database.
; NEVER change it once shipped, or upgrades will install side by side instead of replacing.
; (The doubled "{{" is how Inno Setup escapes a literal "{" character.)
AppId={{8A3C6F12-2E4B-4B8B-9C5A-7D1E6F0A9B34}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://example.invalid/avaibe
DefaultDirName={autopf}\{#MyAppDirName}
DefaultGroupName={#MyAppName}
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

; Install for ALL users -> needs administrator rights. {autopf} therefore resolves to
; "C:\Program Files" (64-bit). If you ever switch to PrivilegesRequired=lowest, {autopf}
; automatically becomes the per-user %LOCALAPPDATA%\Programs folder instead.
PrivilegesRequired=admin

; Windows 10 version 2004 (build 19041) is the minimum: the client relies on
; WDA_EXCLUDEFROMCAPTURE, which older builds do not support.
MinVersion=10.0.19041

; Install as a 64-bit application: {autopf} = "Program Files", not "Program Files (x86)",
; and the 64-bit registry view is used. "x64compatible" covers x64 and ARM64-with-x64-emulation
; and needs Inno Setup 6.3 or newer. On Inno Setup 6.0-6.2 use:  ArchitecturesInstallIn64BitMode=x64
ArchitecturesInstallIn64BitMode=x64compatible

; Where the finished Setup.exe is written, and what it is called.
OutputDir=output
OutputBaseFilename=AvaibeExam-Setup-{#MyAppVersion}

Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
; Clean, quiet wizard: no readme/licence pages are defined on purpose.
DisableWelcomePage=no
SetupLogging=yes


[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"


[Tasks]
; An optional tick-box on the "Additional tasks" page. unchecked = off by default.
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked


[Files]
; Everything the build produced, including sub-folders (runtimes\win-x64\native\WebView2Loader.dll
; lives in a sub-folder, so "recursesubdirs createallsubdirs" is essential).
; The path is relative to THIS .iss file, so it points at windows\publish\.
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; The WebView2 bootstrapper, only if you dropped it next to this script.
; "dontcopy" means: pack it inside Setup.exe but do NOT install it to disk. The [Code]
; section below extracts it to a temp folder and runs it only when it is actually needed.
#ifdef HaveWebView2Bootstrapper
Source: "{#WebView2Bootstrapper}"; Flags: dontcopy
#endif


[Icons]
; {autoprograms} = the all-users Start menu "Programs" folder.
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
; Desktop shortcut, created only if the user ticked the task above.
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon


[Run]
; Offer to launch the app at the end of the wizard. "nowait postinstall skipifsilent"
; means: do not block the wizard, only offer it at the end, and never do it during a
; silent install (/VERYSILENT), which is how Intune and school IT deploy it.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent


[UninstallDelete]
; Nothing extra is deleted. The student's local data lives in
;   %LOCALAPPDATA%\AvaibeExam  (logs, settings.json, enrollment.dat, WebView2 profile)
; and is deliberately LEFT BEHIND so a reinstall keeps the device enrolment.
; To wipe it too, uncomment the next line.
; Type: filesandordirs; Name: "{localappdata}\AvaibeExam"


[Code]
// ------------------------------------------------------------------------
//  WEBVIEW2 DETECTION
//
//  The Evergreen WebView2 runtime records its version in the registry under a
//  well-known product GUID, in the value "pv". A machine-wide (per-system) install
//  writes it under the 32-bit registry view, which on a 64-bit Windows is literally
//  the path spelled out in WV2_KEY_WOW below (note the WOW6432Node segment).
//  A per-user install writes the same key under HKCU, without WOW6432Node.
//
//  We check every plausible location and treat an empty value or "0.0.0.0" as
//  "not installed" - that is what the Microsoft sample detection code does.
//
//  Style note: these comments use "//" rather than Pascal's "{ ... }" form, because
//  the registry paths below contain "}" characters that would close a brace comment
//  early. Inside [Code] the "{" character is NOT an Inno constant marker, so the
//  GUIDs in the string literals need no escaping (unlike in [Setup] / [Files]).
// ------------------------------------------------------------------------

const
  // Written out in full rather than built by concatenation, because Pascal Script
  // const sections do not evaluate expressions.
  WV2_KEY_WOW   = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WV2_KEY_PLAIN = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function HasVersionValue(RootKey: Integer; SubKey: String): Boolean;
var
  pv: String;
begin
  Result := False;
  if RegQueryStringValue(RootKey, SubKey, 'pv', pv) then
    if (pv <> '') and (pv <> '0.0.0.0') then
      Result := True;
end;

function WebView2Installed(): Boolean;
begin
  Result := False;

  // 1. Machine-wide install, 64-bit Windows: the key really contains "WOW6432Node".
  if IsWin64 then
    if HasVersionValue(HKLM64, WV2_KEY_WOW) then
    begin
      Result := True;
      Exit;
    end;

  // 2. Machine-wide install seen through the 32-bit registry view.
  if HasVersionValue(HKLM32, WV2_KEY_PLAIN) then
  begin
    Result := True;
    Exit;
  end;

  // 3. Machine-wide install, plain 64-bit view (belt and braces).
  if IsWin64 then
    if HasVersionValue(HKLM64, WV2_KEY_PLAIN) then
    begin
      Result := True;
      Exit;
    end;

  // 4. Per-user install for the account running Setup.
  if HasVersionValue(HKCU, WV2_KEY_PLAIN) then
    Result := True;
end;

// ------------------------------------------------------------------------
//  Runs after all files are copied. If WebView2 is missing we either install it
//  from the bundled bootstrapper, or (when no bootstrapper was bundled) tell the
//  user where to get it. We never fail the installation over this: the app itself
//  shows a clear WebView2 row on its preflight screen.
// ------------------------------------------------------------------------
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;

  if WebView2Installed() then
  begin
    Log('WebView2 runtime detected; nothing to do.');
    Exit;
  end;

  Log('WebView2 runtime NOT detected.');

#ifdef HaveWebView2Bootstrapper
  // ExtractTemporaryFile unpacks a "dontcopy" file into the temp folder at run time.
  ExtractTemporaryFile('{#WebView2Bootstrapper}');
  WizardForm.StatusLabel.Caption := 'Installing the Microsoft Edge WebView2 runtime...';
  if Exec(ExpandConstant('{tmp}\{#WebView2Bootstrapper}'), '/silent /install', '',
          SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log('WebView2 bootstrapper finished with exit code ' + IntToStr(ResultCode));
    if ResultCode <> 0 then
      MsgBox('The Microsoft Edge WebView2 runtime installer returned code ' + IntToStr(ResultCode) + '.' + #13#10 +
             'Avaibe Exam is installed, but it cannot show the exam page until WebView2 is present.' + #13#10 +
             'You can install it from https://developer.microsoft.com/microsoft-edge/webview2/',
             mbInformation, MB_OK);
  end
  else
  begin
    Log('Could not start the WebView2 bootstrapper (error ' + IntToStr(ResultCode) + ').');
    MsgBox('The Microsoft Edge WebView2 runtime could not be installed automatically.' + #13#10 +
           'Please install it from https://developer.microsoft.com/microsoft-edge/webview2/',
           mbInformation, MB_OK);
  end;
#else
  // No bootstrapper was bundled at compile time - just inform the user.
  MsgBox('Avaibe Exam needs the Microsoft Edge WebView2 runtime, which was not found on this PC.' + #13#10 + #13#10 +
         'It is included in Windows 11. On Windows 10 you can install it from:' + #13#10 +
         'https://developer.microsoft.com/microsoft-edge/webview2/' + #13#10 + #13#10 +
         'Avaibe Exam is installed and will report the missing runtime on its readiness screen.',
         mbInformation, MB_OK);
#endif
end;
