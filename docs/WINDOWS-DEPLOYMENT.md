# Deployment Guide — Avaibe Exam for Windows

Covers: school-managed rollout (Intune/Autopilot or provisioning package → kiosk account →
Assigned Access or Shell Launcher), BYOD download, the release pipeline (build → sign → package →
sign → publish → Intune), the update policy, and the Defender/EDR compatibility checklist. Companion
to [DEPLOYMENT.md](DEPLOYMENT.md) (macOS). Microsoft facts are linked inline; items marked
*(verify)* should be confirmed with the school's IT team or against the current Learn page before a
pilot.

## 1. Prerequisites for any distribution

| Item | Detail |
|---|---|
| Code-signing certificate | OV certificate from a public CA (key on an HSM/USB token — mandatory since June 2023) or Azure Artifact Signing (formerly Trusted Signing; organisations in USA/Canada/EU/UK only, so a UAE entity needs an OV cert or a group company in an eligible region) ([Code signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)). EV no longer gives instant SmartScreen reputation. |
| Timestamp server | RFC 3161 URL from the CA (`/tr … /td SHA256`). |
| Windows SDK | `signtool.exe`, `makeappx.exe` (if MSIX) — `C:\Program Files (x86)\Windows Kits\10\bin\<ver>\x64` ([SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool)). |
| .NET 8 SDK | `dotnet build`/`publish`; targets Windows 10 1607+ and Windows 11, x64/x86/arm64 ([.NET 8 supported OS](https://github.com/dotnet/core/blob/main/release-notes/8.0/supported-os.md)). Ship **x64** first; add arm64 when a school has Snapdragon devices. |
| WebView2 Evergreen runtime | Included in Windows 11; most Windows 10 devices already have it; installer must still check and install ([WebView2 distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)). |
| Installer tooling | WiX (MSI) **or** MSIX packaging. Decision in §4.3. |
| Intune tenant with Windows licences | Win32 app deployment needs Pro/Enterprise/Education and the Intune Management Extension ([Win32 app management](https://learn.microsoft.com/en-us/mem/intune/apps/apps-win32-app-management)). |

Minimum supported OS: Windows 10 22H2 or Windows 11 22H2+, Pro/Education/Enterprise. Windows 10
reached end of support on 14 October 2025; allow it for the pilot, plan to require Windows 11.
Home edition is BYOD-only (no Assigned Access).

## 2. School-managed path

```
 Autopilot / provisioning package ──▶ Intune (Entra-joined, MDM enrolled) ──▶ Lab PCs
        │                                 │  Win32 app (.intunewin): AvaibeExam-<ver>.msi (+ WebView2 bootstrapper)
        │                                 │  Custom OMA-URI: ./Vendor/MSFT/AssignedAccess/Configuration (or ShellLauncher)
        │                                 │  Settings catalog: privacy, RDP, Game DVR, updates, power, Keyboard Filter
        └── kiosk account (local standard, auto-logon) ◀────────────────────┘
```

### 2.1 Enrol and provision devices
1. **Autopilot** (new devices): register hardware hashes, assign a deployment profile; for
   dedicated exam PCs use *self-deploying mode* so no user signs in during provisioning
   ([Windows Autopilot self-deploying mode](https://learn.microsoft.com/en-us/autopilot/self-deploying)) *(verify profile options in tenant)*.
   **Provisioning package** (existing devices): Windows Configuration Designer `.ppkg` that joins
   Entra ID / enrols MDM and can carry the Assigned Access XML
   ([create a provisioning package](https://learn.microsoft.com/en-us/windows/configuration/provisioning-packages/provisioning-create-package)).
2. Preflight reads MDM enrollment (`HKLM\SOFTWARE\Microsoft\Enrollments\*\ProviderID`) and
   domain/Entra join; `policy.requireMDM=true` refuses to start on unmanaged PCs.
3. Create the **kiosk account**: local *standard* user (e.g. `ExamKiosk`) with auto-logon. Assigned
   Access can create and manage one for you with `<AutoLogonAccount/>`; never assign a kiosk profile
   to an administrator — it is not applied
   ([configuration file — Configs](https://learn.microsoft.com/en-us/windows/configuration/assigned-access/configuration-file#configs)).

### 2.2 Deploy the app (Intune Win32)
* Package `AvaibeExam-<ver>.msi` (plus `MicrosoftEdgeWebview2Setup.exe`) into `.intunewin` (§4.5)
  and add it as a **Windows app (Win32)**: install `msiexec /i AvaibeExam-<ver>.msi /qn`, uninstall
  `msiexec /x {ProductCode} /qn`, install behaviour **System**, detection rule = MSI product code
  or file version of `AvaibeExam.exe`, requirement rule = x64 + Windows 10 22H2+. Intune installs
  must be silent ([Win32 app management](https://learn.microsoft.com/en-us/mem/intune/apps/apps-win32-app-management)).
* Assign as **Required** to the lab device group with a deadline before the first exam window and
  a *Restart grace period*. Compare the reported version with `policy.minClientVersion`.
* Pre-seed enrolment: deliver the backend URL and a `SCHOOL-*` token as registry values under
  `HKLM\SOFTWARE\Avaibe\Exam` via a PowerShell script or settings catalog custom setting so
  students never type them *(client reads `settings.json` today; managed-registry override is a
  small follow-up)*.

### 2.3 Assigned Access — restricted user experience (Pro, Enterprise, Education, IoT Enterprise)
A Win32 app cannot be the app of the classic **single-app kiosk** profile (UWP or Microsoft Edge
only; `v4:ClassicAppPath` exists from the 2021 schema but the restricted user experience is the
documented path for desktop apps). Use an `AllAppList` profile with `AvaibeExam.exe` auto-launched
([Assigned Access overview](https://learn.microsoft.com/en-us/windows/configuration/assigned-access/),
[configuration file](https://learn.microsoft.com/en-us/windows/configuration/assigned-access/configuration-file)).

```xml
<?xml version="1.0" encoding="utf-8" ?>
<AssignedAccessConfiguration
    xmlns="http://schemas.microsoft.com/AssignedAccess/2017/config"
    xmlns:rs5="http://schemas.microsoft.com/AssignedAccess/201810/config"
    xmlns:v3="http://schemas.microsoft.com/AssignedAccess/2020/config"
    xmlns:v5="http://schemas.microsoft.com/AssignedAccess/2022/config">
  <Profiles>
    <Profile Id="{6D2F3B1A-4C4E-4B7B-9A0E-1F2E3D4C5B6A}" Name="Avaibe Exam kiosk">
      <AllAppsList>
        <AllowedApps>
          <App DesktopAppPath="%ProgramFiles%\Avaibe\Avaibe Exam\AvaibeExam.exe"
               rs5:AutoLaunch="true" />
          <!-- optional accommodation tools, only if policy.allowedApps says so -->
          <!-- <App AppUserModelId="Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" /> -->
        </AllowedApps>
      </AllAppsList>
      <rs5:FileExplorerNamespaceRestrictions />   <!-- block all folders -->
      <v5:StartPins><![CDATA[{ "pinnedList": [
        { "desktopAppLink": "%ALLUSERSPROFILE%\\Microsoft\\Windows\\Start Menu\\Programs\\Avaibe Exam.lnk" }
      ] }]]></v5:StartPins>
      <Taskbar ShowTaskbar="false" />
    </Profile>
  </Profiles>
  <Configs>
    <Config>
      <AutoLogonAccount rs5:DisplayName="Exam Kiosk" />
      <DefaultProfile Id="{6D2F3B1A-4C4E-4B7B-9A0E-1F2E3D4C5B6A}" />
    </Config>
  </Configs>
</AssignedAccessConfiguration>
```

Notes: replace the Profile Id with a real GUID (`New-Guid`); on Windows 10 use `<StartLayout>` with
`LayoutModificationTemplate` XML instead of `v5:StartPins`; the `.lnk` must exist for the pin to
show. Apply via **Intune custom OMA-URI** `./Vendor/MSFT/AssignedAccess/Configuration` (string, XML
content) ([custom settings](https://learn.microsoft.com/en-us/mem/intune/configuration/custom-settings-windows-10)),
via provisioning package path `AssignedAccess/MultiAppAssignedAccessSettings`, or locally as SYSTEM:

```powershell
# run in an elevated PowerShell started as SYSTEM (psexec -i -s powershell.exe)
$xml = Get-Content -Raw .\avaibe-assigned-access.xml
$obj = Get-CimInstance -Namespace root\cimv2\mdm\dmmap -ClassName MDM_AssignedAccess
$obj.Configuration = [System.Net.WebUtility]::HtmlEncode($xml)
Set-CimInstance -CimInstance $obj
# remove: $obj.Configuration = $null; Set-CimInstance -CimInstance $obj
```
([configure a multi-app kiosk](https://learn.microsoft.com/en-us/windows/configuration/assigned-access/configure-multi-app-kiosk)).
The Intune *Kiosk* template only offers multi-app for Windows 10; on Windows 11 use the OMA-URI
above ([Intune kiosk settings](https://learn.microsoft.com/en-us/intune/device-configuration/templates/ref-kiosk-settings-windows)).
The configuration takes effect at the kiosk account's next sign-in; Ctrl+Alt+Del is the default
breakout to the sign-in screen (change with `<v4:BreakoutSequence Key="Ctrl+Alt+F12"/>` and give the
key only to invigilators).

### 2.4 Shell Launcher alternative (Enterprise, Education, IoT Enterprise — not Pro)
Shell Launcher v2 replaces `explorer.exe` with `CustomShellHost.exe` running `AvaibeExam.exe`; no
Start, no taskbar, no desktop at all. It does *not* restrict other apps by itself — pair it with
AppLocker or the App Control policy in §2.6 ([Shell Launcher](https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/)).

```xml
<?xml version="1.0" encoding="utf-8"?>
<ShellLauncherConfiguration xmlns="http://schemas.microsoft.com/ShellLauncher/2018/Configuration"
    xmlns:V2="http://schemas.microsoft.com/ShellLauncher/2019/Configuration">
  <Profiles>
    <DefaultProfile>
      <Shell Shell="%ProgramFiles%\Avaibe\Avaibe Exam\AvaibeExam.exe" V2:AppType="Desktop">
        <DefaultAction Action="RestartShell" />
        <ReturnCodeActions>
          <ReturnCodeAction ReturnCode="0"  Action="RestartShell" />   <!-- normal release -->
          <ReturnCodeAction ReturnCode="10" Action="RestartDevice" />  <!-- client asked for reboot -->
        </ReturnCodeActions>
      </Shell>
    </DefaultProfile>
    <Profile Id="{A3F1C2D4-0001-4000-8000-000000000001}">
      <Shell Shell="%windir%\explorer.exe" />          <!-- administrators keep Explorer -->
    </Profile>
  </Profiles>
  <Configs>
    <Config><AutoLogonAccount/><Profile Id="{DefaultProfile}"/></Config>
    <Config><UserGroup Type="LocalGroup" Name="Administrators"/><Profile Id="{A3F1C2D4-0001-4000-8000-000000000001}"/></Config>
  </Configs>
</ShellLauncherConfiguration>
```
*(verify element names against the [Shell Launcher XSD](https://learn.microsoft.com/en-us/windows/configuration/assigned-access/shell-launcher/xsd) — the `Config` shape above is illustrative)*.
Enable the feature first unless you apply it through the CSP (which enables it automatically):

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Client-DeviceLockdown,Client-EmbeddedShellLauncher
# apply as SYSTEM via the MDM bridge:
$obj = Get-CimInstance -Namespace root\cimv2\mdm\dmmap -ClassName MDM_AssignedAccess
$obj.ShellLauncher = [System.Net.WebUtility]::HtmlEncode((Get-Content -Raw .\avaibe-shell-launcher.xml))
Set-CimInstance -CimInstance $obj
```
Intune: custom OMA-URI `./Vendor/MSFT/AssignedAccess/ShellLauncher`
([configure Shell Launcher](https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/configure)).
The shell runs with the signed-in account's rights; keep the kiosk account standard and UAC on.
Exit actions: `RestartShell`, `RestartDevice`, `ShutdownDevice`, `DoNothing`.

### 2.5 Camera / microphone privacy (PPPC equivalent)
* Windows privacy for **desktop (unpackaged) apps** is one global switch per capability ("Let
  desktop apps access your camera / microphone"); desktop apps cannot be toggled individually
  ([Windows camera, microphone, and privacy](https://support.microsoft.com/en-us/windows/windows-camera-microphone-and-privacy-a83257bc-e990-d54a-d212-b5e41beba857)).
  The client reads `HKCU\...\CapabilityAccessManager\ConsentStore\{webcam,microphone}\Value`
  (`Allow`/`Deny`) as `cameraAuthorized`/`microphoneAuthorized`; the per-app entries under
  `...\NonPackaged\<exe path>` record usage, not consent *(verify semantics on 24H2)*.
* Intune: Privacy CSP `LetAppsAccessCamera` / `LetAppsAccessMicrophone` = `1` (force allow) at
  device scope; the `_ForceAllowTheseApps` lists take Package Family Names, i.e. packaged apps only,
  so for the MSI build set the global value; if the MSIX build is used, add its PFN to the allow list
  ([Privacy CSP](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-privacy)).
* Do **not** set force-deny (`2`) for camera/mic on exam PCs: it removes the student's ability to
  grant access and Preflight will show ✗ permanently.

### 2.6 Policies worth pairing with Assigned Access (settings catalog / OMA-URI)

| Setting | Why |
|---|---|
| Keyboard Filter: block `Ctrl+Alt+Del`, `Win+L`, `Alt+Tab`, `Alt+F4`, `Win+Tab`, accessibility chords; breakout key for invigilators only (Enterprise/Education) ([Keyboard Filter](https://learn.microsoft.com/en-us/windows/configuration/keyboard-filter/)) | Converts the best-effort rows to Enforced. Not available on Pro. |
| `ADMX_CtrlAltDel/DisableLockWorkstation`, `RemoveLogoff`, `DisableTaskMgr` ([ADMX_CtrlAltDel](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-ctrlaltdel)) | Assigned Access already removes Task Manager/Log off/Change password from the security screen; add Lock. |
| `ApplicationManagement/AllowGameDVR = 0` *(verify path)* | Disables Xbox Game Bar capture. |
| `RemoteDesktopServices/AllowUsersToConnectRemotely = 0`; remove TeamViewer/AnyDesk from the image | RDP/remote control off. |
| `Privacy/AllowClipboardHistory = 0`, `Privacy/AllowCrossDeviceClipboard = 0` | No cloud clipboard leakage. |
| `Update/ActiveHoursStart/End`, `UpdateNotificationLevel = 2`, scheduled install outside exam windows ([Assigned Access recommendations](https://learn.microsoft.com/en-us/windows/configuration/assigned-access/recommendations#windows-update)) | No restart or toast mid-exam. |
| Power: `HidePowerOptions`, `Shutdown_AllowSystemToBeShutDownWithoutHavingToLogOn = 0`, sleep/display timeouts `0` (same page, *Power settings*) | Removes shutdown from the security screen and lock screen. |
| `WindowsLogon/HideFastUserSwitching = 1`, `EnumerateLocalUsersOnDomainJoinedComputers` as needed | One session per PC. |
| App Control for Business publisher rule for the Avaibe certificate (or rely on Assigned Access AppLocker rules) ([App Control for Business](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/appcontrol)) | Only signed Avaibe + Microsoft binaries run under the kiosk account. |
| Microsoft Edge Update policy *Update (WebView)* / `UpdatesSuppressed` window during exam days ([Enterprise management of WebView2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/enterprise)) | Prevents a runtime update landing during a session (runtime only switches on app restart anyway). |

### 2.7 Network allowlist for the exam domain
| Destination | Purpose |
|---|---|
| `https://<exam-api-host>` (443) | REST + heartbeat + telemetry |
| `https://<exam-web-host>` | Exam page in WebView2 (`examUrl`) |
| `wss://<exam-api-host>` *(future)* | Live monitoring |
| `*.microsoft.com`, `*.windowsupdate.com`, `msedge.b.tlu.dl.delivery.mp.microsoft.com` | WebView2 runtime + Edge Update, Windows Update (schedule outside exam hours) |
| CA OCSP/CRL hosts for the signing certificate, `*.digicert.com` or the CA in use | Authenticode revocation checks at install/launch |
| `time.windows.com` (NTP) | Release-code and token expiry depend on clock accuracy |

TLS interception breaks pinning if enabled later; exempt the exam hosts. Preflight sends a HEAD
request to each allowed domain.

## 3. BYOD path

1. Student downloads `AvaibeExam-<version>.msi` from the school portal over HTTPS; the portal shows
   the SHA-256, version and publisher name (§4.6).
2. **SmartScreen**: the file is Mark-of-the-Web tagged. With a valid OV/Artifact Signing signature the
   dialog shows the verified publisher; for a *new* file/publisher it may still say "unrecognized
   app" until download volume builds reputation — there is no consumer fast-track, and EV no longer
   bypasses it ([SmartScreen reputation](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)).
   Tell first-cohort students to expect *More info → Run anyway* and to check the publisher name.
   Enterprise-managed BYOD can be pre-trusted by IT submitting the file to Microsoft.
3. Installer runs per-machine (UAC prompt) or per-user *(decide in §4.3; per-user avoids UAC but
   lands in `%LOCALAPPDATA%`, which weakens allow-listing)*. It checks the WebView2 `pv` registry
   key and runs `MicrosoftEdgeWebview2Setup.exe /silent /install` if missing.
4. First run: Login screen with backend URL prefilled, `BYOD-*` token, student code, exam code.
5. Preflight shows the limitation report; Windows shows the camera/mic prompt on first use if the
   global desktop-app switch is on; if it is off, Preflight deep-links to
   `ms-settings:privacy-webcam` / `ms-settings:privacy-microphone`.
6. After the exam the student can uninstall from *Apps & features*; nothing else is installed.
   `%LOCALAPPDATA%\AvaibeExam` (DPAPI-protected enrollment, settings, logs) is harmless to leave.

Home edition is supported for BYOD (`kiosk-fallback` only). S mode devices cannot install Win32
apps outside the Store — out of scope.

## 4. Release pipeline

```
 tag ──▶ dotnet publish (Release, win-x64) ──▶ signtool sign AvaibeExam.exe (+ every Avaibe DLL)
                                                    ▼
                          WiX: MSI (per-machine, %ProgramFiles%\Avaibe\Avaibe Exam) ──▶ signtool sign .msi
                                                    ▼
                          (optional) MSIX from the same publish folder ──▶ signtool sign .msix
                                                    ▼
                          SHA-256 + version.json ──▶ portal/CDN ──▶ IntuneWinAppUtil → .intunewin ──▶ Intune
```

### 4.1 Build
```powershell
dotnet restore windows/AvaibeExam/AvaibeExam.csproj
dotnet publish windows/AvaibeExam/AvaibeExam.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=false -p:Version=0.1.0 -o build/publish
```
Framework-dependent keeps the package small but requires the .NET 8 Desktop Runtime on the target;
add `windowsdesktop-runtime-8.x-win-x64.exe /install /quiet` to the installer as a prerequisite, or
switch to `--self-contained true` (larger, no prerequisite) *(decide before pilot)*. Version comes
from the tag; `Constants.clientVersion` must match `X-Client-Version`.

### 4.2 Sign the binaries
```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 `
  /n "Avaibe" /a build/publish/AvaibeExam.exe build/publish/AvaibeExam.dll
signtool verify /pa /v build/publish/AvaibeExam.exe
```
`/fd` and `/td` are required by current SignTool; use `/f cert.pfx /p …` only in a locked CI
keychain, or `/csp`/`/kc` for a token, or the Artifact Signing `signtool` dlib
([SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool)). Timestamp every
signature so it outlives the certificate. Sign **before** packaging; never modify a signed file.

### 4.3 Package: MSI (WiX) vs MSIX
| | MSI via WiX | MSIX |
|---|---|---|
| Fits | Assigned Access `DesktopAppPath`, Shell Launcher `Shell`, Intune Win32, GPO software install, schools without Store access | Intune LOB/Store, App Installer, clean uninstall, per-app privacy lists (PFN) |
| Signing | Authenticode on `.exe`/`.dll` and on the `.msi` | Package must be signed with a cert trusted on the device; identity = publisher + name ([What is MSIX](https://learn.microsoft.com/en-us/windows/msix/overview)) |
| Path stability | `%ProgramFiles%\Avaibe\Avaibe Exam\AvaibeExam.exe` — stable path for kiosk XML | `C:\Program Files\WindowsApps\<PFN>\…` changes per version → use the AUMID instead of `DesktopAppPath` in Assigned Access |
| Decision | **Ship MSI first** (pilot) | Add MSIX once the Assigned Access AUMID path is validated in the POC matrix |

```powershell
wix build -arch x64 -d PublishDir=build/publish windows/installer/AvaibeExam.wxs -o build/AvaibeExam-0.1.0.msi
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /n "Avaibe" build/AvaibeExam-0.1.0.msi
```
The MSI: per-machine, `ALLUSERS=1`, install to `ProgramFiles64Folder`, Start-menu shortcut named
*Avaibe Exam* (used by `StartPins`), custom action that runs the WebView2 bootstrapper if the `pv`
key is absent, no services, no scheduled tasks, no Run keys, MSI `UpgradeCode` fixed for in-place
upgrades.

### 4.4 Clean-machine tests before publishing
Fresh Windows 11 Pro VM **and** a lab image with the school's EDR: download via browser (MotW set)
→ SmartScreen dialog shows the publisher → install → Preflight → exam in the expected mode → release
→ uninstall. Repeat for upgrade-over-existing, for the Intune path on an Entra-joined VM, and with
Assigned Access applied (POC matrix in WINDOWS-TEST-PLAN.md §14).

### 4.5 Intune `.intunewin`
```powershell
# tool from https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool (keep it outside the source folder)
IntuneWinAppUtil.exe -c build/intune-src -s AvaibeExam-0.1.0.msi -o build/intune -q
```
`build/intune-src` holds the MSI and `MicrosoftEdgeWebview2Setup.exe`; reference sub-files with
relative paths ([prepare Win32 app content](https://learn.microsoft.com/en-us/mem/intune/apps/apps-win32-prepare)).
Upload as a Win32 app (§2.2), set supersedence to the previous version, and use *App relationship
viewer* to check dependencies.

### 4.6 Publish
* Upload MSI (and MSIX if produced) to the portal/CDN; publish SHA-256, version, publisher
  certificate thumbprint and minimum Windows build so IT can verify with `Get-FileHash` and
  `Get-AuthenticodeSignature`.
* Update `policy.minClientVersion` only after schools have had a deployment window.
* Keep every signed release and its timestamped signature; if the signing key is ever compromised,
  rotate the certificate and re-sign — remember reputation restarts with a new publisher identity.

## 5. Update policy

| Rule | Implementation |
|---|---|
| No updates during an exam | No self-updater today. Intune deadlines/maintenance windows outside exam hours; `UpdatesSuppressed` for Edge/WebView2 on exam days; Windows Update active hours. A future in-app updater must be disabled while `lockdownMode != none`. |
| WebView2 runtime version | Evergreen (recommended by Microsoft); a new runtime is used only after app restart, so a mid-exam update cannot switch engines. `NewBrowserVersionAvailable` is logged, never acted on while locked. Fixed Version runtime (>250 MB, no auto-update, extra `icacls` on Windows 10) only if a school demands frozen engines. |
| Minimum version check | `POST /devices/enroll` and `policy.minClientVersion` vs `Constants.clientVersion` (semantic compare in `Util/Version.cs`); Preflight fails with "Update required". |
| Version reported everywhere | `X-Client-Version` header; admin console shows it per session; Intune reports installed version via detection rule. |
| Rollback | Previous signed MSI remains in Intune; supersedence can be reversed; session data is server-side so downgrade is safe. |
| Compatibility window | Current Windows 11 release and the previous one; Windows 10 22H2 best-effort during the pilot year. Re-run WINDOWS-LIMITATIONS.md validation on each feature update. |

## 6. Defender / EDR compatibility checklist

Goal (spec §18): compatibility and trust, not evasion. Every release must satisfy:

| Check | How we meet it |
|---|---|
| Signed | Every `.exe`/`.dll` we build and the installer carry an Authenticode signature with a timestamp from a consistent publisher identity. |
| Conventional installer | MSI (or MSIX) with a visible entry in *Apps & features*; silent flags for Intune; no self-extracting droppers. |
| No process injection | No `WriteProcessMemory`/`CreateRemoteThread`/DLL injection; `WH_KEYBOARD_LL` is a low-level hook that runs in *our* process (it is not injected into others) ([LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)). |
| No persistence | No service, driver, scheduled task, Run/RunOnce key, WMI subscription or startup folder entry. Kiosk auto-launch is configured by IT policy, not by the app. |
| No Defender/SmartScreen/UAC changes | The app never touches Defender exclusions, tamper protection, SmartScreen or UAC settings and does not ask IT to. |
| No anti-analysis | No debugger detection, no packing/obfuscation beyond standard Release builds, readable PE metadata (company, product, version). |
| Least privilege | Runs as the signed-in standard user; no `requireAdministrator` manifest; DPAPI CurrentUser for secrets. |
| Behaviour that *looks* suspicious is documented | Global keyboard hook, display affinity, process enumeration, `ShutdownBlockReasonCreate`, registry reads of Secure Boot/MDM/ConsentStore — listed here and in the EDR vendor note so analysts can allow-list. |
| Clean-machine + EDR tests | §4.4 on Defender-only and on the school's EDR (CrowdStrike/SentinelOne/Defender for Endpoint) with real-time protection on. Record any detection with the sample hash. |
| False-positive process | Submit via the Microsoft Security Intelligence portal (select *Microsoft Defender SmartScreen* or *Microsoft Defender Antivirus*) ([submit files](https://learn.microsoft.com/en-us/windows/security/operating-system-security/virus-and-threat-protection/microsoft-defender-smartscreen/#submit-files-to-microsoft-defender-smartscreen-for-review)); for Defender for Endpoint tenants, IT creates an allow indicator by certificate/hash. Publish hashes and thumbprint so IT can pre-allow. |
| App Control friendly | One publisher certificate; IT can author a publisher rule instead of per-hash rules ([App Control for Business](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/appcontrol)). |
