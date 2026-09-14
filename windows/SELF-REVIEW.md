# SELF-REVIEW — Windows client (written on macOS, never compiled)

This file records the review pass done after writing `windows/` and lists the places a human should
look at first if `dotnet build` fails. Everything below was checked by reading, not by compiling.

## 1. What was checked, file by file

Mechanical checks (scripted over all 31 `.cs` files and 9 `.xaml` files):

* Every file has explicit `using` directives for every namespace it uses — the project would also
  build with `ImplicitUsings` disabled. (Only false positives remained: a comment containing "Task",
  `List<int>.ToArray()`.)
* Every `x:Class` matches `namespace` + `partial class` of its code-behind.
* Every XAML event handler (`Click`, `TextChanged`, `PreviewTextInput`, `KeyDown`) has a method with
  that name in the code-behind; signatures use the WPF arg types (`RoutedEventArgs`,
  `TextChangedEventArgs`, `TextCompositionEventArgs`, `System.Windows.Input.KeyEventArgs`).
* Every `StaticResource` / `FindResource` key used in Views and code exists in `App.xaml`.
* `Binding` paths used in XAML (`BaseUrlText`, `EnrollmentTokenText`, `StudentCode`, `ExamCode`,
  `PreflightItems`, item paths `StatusGlyph`, `Status`, `Name`, `RequiredText`, `Required`,
  `Detail`) exist on `AppState` / `PreflightItem`. All other UI updates are done in code-behind
  `Render()` methods reacting to `PropertyChanged`, so there are no other binding paths to break.

Manual checks:

| Area | Checked |
|---|---|
| Namespaces | Folder `App/` uses namespace `AvaibeExam.Core` (a namespace `AvaibeExam.App` would clash with the `App` class). `Util/Version.cs` defines `SemVer` (not `Version`, which would clash with `System.Version`). No type is named `Screen`, `Application`, `Timer`, `Color`. |
| WinForms vs WPF ambiguity | `UseWindowsForms=true` only for `System.Windows.Forms.Screen`. The csproj removes the WinForms implicit usings (`<Using Remove="System.Windows.Forms" />`, `System.Drawing`) and the two files that need WinForms alias it (`using WinForms = System.Windows.Forms;`). `MessageBox`, `Application`, `Clipboard` are always written fully qualified as `System.Windows.*`. |
| async | No `async void` anywhere. UI-thread work goes through `AppState.Fire(Func<Task>)` which awaits and logs. Event handlers are synchronous; `ScriptDialogOpening` uses `GetDeferral()` + `Dispatcher.BeginInvoke`. |
| Threading | `HeartbeatService`, `EventReporter`, `ProcessMonitor`, `NetworkMonitor` run on `Task.Run` + `PeriodicTimer` and marshal every callback with `Dispatcher.InvokeAsync/BeginInvoke`. `SystemEvents.DisplaySettingsChanged` and `NetworkChange` handlers marshal to the dispatcher. The keyboard hook runs on the UI thread (installed there), callbacks throttled. `AppState` is UI-thread only. |
| Event handler signatures | `EventHandler` / `EventHandler<T>` handlers use `object? sender`; `RoutedEventHandler`, `UnhandledExceptionEventHandler`, `DispatcherUnhandledExceptionEventHandler` use non-nullable `object sender`. `HwndSourceHook` signature `(IntPtr, int, IntPtr, IntPtr, ref bool)`. |
| Color-Color rule | Properties whose name equals their type (`Policy`, `LockdownMode`, `Dispatcher`, `DisplayMonitor`, `NetworkMonitor`, `Enrollment`) rely on C#'s "Color Color" rule inside method bodies; the field initializers were changed to not depend on it (`new Policy()`, default enum, fully-qualified `AvaibeExam.Security.DisplayMonitor.CurrentCount()`). |
| C# keywords | Parameter formerly named `required` (contextual keyword since C# 11) renamed to `isRequired`. |
| JSON | All wire objects use `[JsonPropertyName]`; enums with non-C# wire strings use `MappedEnumConverter<T>` (lenient, `HandleNull = true`). `Policy.Normalized()` clamps intervals after deserialization. `Dictionary<string, object?>` metadata serializes by runtime type (string/bool/int/JsonElement/nested dictionary). |
| P/Invoke | Signatures follow Win32 prototypes; pointer-size-safe `Get/SetWindowLongPtr` wrapper; hook delegate stored in a field (GC-safe); `KBDLLHOOKSTRUCT` layout `uint,uint,uint,uint,UIntPtr`. |
| Contract §9 | `platform: "windows"`, `hardwareId` = MachineGuid, DPAPI enrollment, `preflightExtras` object, `lockdownMode` `assigned-access`/`kiosk-fallback`/`none`, shim from §9.4 verbatim (plus `Object.freeze` for `LockdownNative`), extra events from §9.5, native WPF exit overlay. |

## 2. APIs I am less than certain about (check these first on a build failure)

1. **`<Using Remove="System.Windows.Forms" />` / `System.Drawing`** in the csproj. If the build reports
   `CS0104 'UserControl' is an ambiguous reference` (or `Brushes`, `Color`, `Window`...), the removal
   did not take effect. Fix: set `<ImplicitUsings>disable</ImplicitUsings>` — every file already has
   explicit usings — or move the `<Using Remove>` items into a `Directory.Build.targets`.
2. **`Microsoft.Web.WebView2.Wpf.WebView2.AllowExternalDrop`** (added around 1.0.1185). If missing in
   1.0.2592.51, delete the line in `Browser/ExamWebView.cs` (drag-drop is then only blocked by the
   kiosk window setup).
3. **`CoreWebView2Settings.HiddenPdfToolbarItems`** / `CoreWebView2PdfToolbarItems` flag names
   (`Save`, `Print`, `SaveAs`, `FullScreen`, `MoreSettings`). Delete the assignment if a name differs.
4. **`CoreWebView2Settings.UserAgent`** (1.0.864+) — should exist; remove if not.
5. **`CoreWebView2ProcessFailedEventArgs.ExitCode`** (1.0.1054+) — only used in a log line.
6. **`CoreWebView2Environment.CreateAsync(browserExecutableFolder:, userDataFolder:, options:)`** named
   parameters — if the parameter names differ, pass them positionally.
7. **`WebView2.DefaultBackgroundColor`** is `System.Drawing.Color` on the WPF control (that is why the
   file uses `System.Drawing.Color.Black` fully qualified).
8. **`System.Security.Cryptography.ProtectedData`** — referenced as a NuGet package (8.0.0) in case the
   WindowsDesktop shared framework does not expose it; if the build warns about a conflict with the
   framework copy, remove the `PackageReference`.
9. **`Dispatcher.InvokeAsync(Func<T>)` awaited directly** (`DispatcherOperation<T>` has `GetAwaiter`).
   If the compiler complains, wrap with `.Task`.
10. **`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` and WebView2**: the affinity is applied to the
    top-level window and re-applied after CoreWebView2 initializes. Whether the WebView2 child HWND is
    also excluded from capture must be verified on real hardware (`docs/WINDOWS-TEST-PLAN.md`); the
    achieved level is reported in `captureProtection` metadata of `LOCKDOWN_ENGAGED/FALLBACK`.
11. **`EnsureCoreWebView2Async` before the HWND exists**: the control is placed in the visual tree
    (`Screen = Exam` → `ExamView` hosts it) *before* `InitializeAsync()` is awaited; the WPF control
    waits for its HWND internally. If initialization hangs, await one `Dispatcher.Yield()` /
    `Loaded` before calling `InitializeAsync()`.
12. **Airspace**: WPF overlays cannot be drawn over the WebView2 HWND, so `ExamView` hides the web host
    (`Visibility.Hidden`) while a Warning/Pause/Exit overlay is visible. The page keeps running.
13. **`Environment.OSVersion.Version.Build`** returns the real build on .NET 5+ (no compatibility
    shim) — used for the Windows-version and capture-protection checks.
14. **Win+L**: swallowing the Win key makes Win+L usually not fire, but this is not guaranteed;
    documented in `KeyboardHook.cs` and README.
15. **Low-level hook timeout**: Windows may silently drop a WH_KEYBOARD_LL hook whose callback is
    slow; the watchdog re-installs the hook every 30 s (`KeyboardHook.Reinstall`).

## 3. Contract items not fully satisfied / deviations

* **Mock backend accepts only `aac | kiosk-fallback | none`** for `lockdownMode` in heartbeats
  (`mock-backend/server.js`, `LOCKDOWN_MODES`). The Windows client sends `assigned-access` per §9.3;
  the mock ignores the value (keeps the previous mode) rather than rejecting the heartbeat. The
  mock-backend was out of scope for this task — add `"assigned-access"` to `LOCKDOWN_MODES` (and the
  admin badge map) there.
* `preflightExtras` is sent as a **top-level** field of `POST /api/v1/sessions` next to `preflight`
  (the contract says "additional object", not where); the mock ignores unknown fields.
* **JavaScript `prompt()`** is rendered as an OK/Cancel box that returns the default text (WPF has no
  built-in text prompt); `alert`/`confirm`/`beforeunload` are native message boxes.
* **Assigned Access detection** uses HKCU `AssignedAccessConfiguration`, a custom Winlogon shell and
  "no explorer.exe in this session". The MDM bridge WMI class (`MDM_AssignedAccess`) is not queried
  (would need `System.Management` and admin rights) — documented TODO in `AssignedAccessDetector.cs`.
* **PDF viewer** is not disabled outright (no documented switch); its Save/Print/SaveAs/FullScreen
  toolbar items are hidden and downloads are blocked.
* **TPM presence** is a registry heuristic (`ACPI\MSFT0101` or `Services\TPM\Parameters`), no
  `Get-Tpm`.
* `WarningOverlay` / `PauseOverlay` have minimal code-behind files (`.xaml.cs`) — XAML user controls
  need `InitializeComponent`.
* Task Manager, Win+L policy, Defender: intentionally untouched (as required).

## 4. Independent review (second pass)

Second, independent read of every `.cs`, `.xaml`, `.csproj`, `.manifest` and `.ps1` file acting as the
compiler (still no Windows build available). Checked: csproj/SDK interplay, every WPF / WebView2
1.0.2592.51 / System.Text.Json / P/Invoke / Win32 / registry / principal API member used, C# 12
language rules (definite assignment, Color-Color rule, switch fall-through, lambda ↔ delegate
conversions, `DispatcherOperation<T>` awaits, nullable-only warnings), XAML (`x:Class` ↔ code-behind
for all 9 files, every `StaticResource`/`FindResource` key exists in `App.xaml`, every XAML event
handler exists with the right signature, well-formedness via `xmllint`), a scripted using-directive
audit of all 31 `.cs` files (every file compiles with implicit usings off), runtime order
(WebView2 HWND before `EnsureCoreWebView2Async`, hook delegate rooting, `ShutdownBlockReasonCreate`
on the UI thread, mutex release, smoke/auto-run flow), and PowerShell syntax of the three scripts.

### Changed

| File:line | Before → After |
|---|---|
| `AvaibeExam/AvaibeExam.csproj:11` | `<ImplicitUsings>enable</ImplicitUsings>` → `disable`. The WinForms implicit usings (`System.Windows.Forms`, `System.Drawing`) are added by the SDK's `.targets`, which is imported *after* the project body, so the `<Using Remove>` items could not be relied on and `UserControl`/`Application`/`MessageBox`/`Brushes`/`Color`/`Button`/`TextBox`/`KeyEventArgs` would have become CS0104-ambiguous. All files already carry complete explicit usings (audited by script). |
| `AvaibeExam/AvaibeExam.csproj:31-40` | Removed the now-pointless `<Using Remove="System.Windows.Forms" />` / `System.Drawing` item group. |
| `AvaibeExam/Browser/ExamWebView.cs:34` | `= LockdownMode.None` → `= AvaibeExam.Models.LockdownMode.None` (property named like its type in an initializer; fully qualified so it does not depend on the Color-Color rule). |
| `AvaibeExam/Browser/ExamWebView.cs:75` | `CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: …, options: …)` → positional `CreateAsync(null, Constants.WebView2UserDataFolder, options)`. |
| `AvaibeExam/Browser/ExamWebView.cs:91-110` | `IsGeneralAutofillEnabled`, `IsPasswordAutosaveEnabled`, `IsPinchZoomEnabled`, `IsSwipeNavigationEnabled`, `HiddenPdfToolbarItems`, `UserAgent`, `AllowExternalDrop` were set directly → each wrapped in `TrySetting(name, () => …)` (logs a warning instead of throwing). The SDK (1.0.2592) is newer than some installed Evergreen runtimes and accessing an unimplemented setting throws at runtime, which would have aborted the exam start; none of these is security-critical (downloads/popups/navigation/dialogs are enforced by the event handlers). Core settings (dev tools, context menus, script dialogs, status bar, zoom, accelerator keys, web message, script) are still applied unguarded. |
| `AvaibeExam/Browser/ExamWebView.cs:~145` | Added `private static void TrySetting(string name, Action apply)` helper. |
| `AvaibeExam/Browser/ExamWebView.cs:~380` | `ProcessFailed` log line read `e.Reason` / `e.ExitCode` unguarded → read inside `try { } catch { }` (both members are newer than `ProcessFailedKind`; log only). |
| `AvaibeExam/Models/Models.cs:295` | `Policy.ExternalDisplayAction` initializer `= ExternalDisplayAction.Warn` → `= AvaibeExam.Models.ExternalDisplayAction.Warn` (same Color-Color-in-initializer hardening). |

### Verified correct (no change needed)

* `Dispatcher.InvokeAsync(Func<T>)` awaited directly (`DispatcherOperation<T>.GetAwaiter()` exists);
  `InvokeAsync(() => { … })` / `InvokeAsync(() => Apply(x))` bind to the `Action` overload.
* `WebView2.DefaultBackgroundColor` is `System.Drawing.Color` on the WPF control (file uses it fully
  qualified); `AllowExternalDrop`, `ZoomFactor` exist on the WPF control; `CoreWebView2PdfToolbarItems`
  members `Save|Print|SaveAs|FullScreen|MoreSettings` exist; `CoreWebView2ScriptDialogKind.Beforeunload`,
  `CoreWebView2ProcessFailedKind.BrowserProcessExited`, `CoreWebView2PermissionState.Deny`,
  `CoreWebView2DownloadStartingEventArgs.Cancel/Handled/DownloadOperation.Uri`,
  `WindowCloseRequested` (`EventHandler<object>`) all match 1.0.2592.51.
* `Microsoft.Web.WebView2 1.0.2592.51` and `System.Security.Cryptography.ProtectedData 8.0.0` are real
  package versions; the ProtectedData package coexists with the WindowsDesktop framework copy (SDK
  conflict resolution picks one, no error).
* Every `EventHandler`/`EventHandler<T>`/`PropertyChangedEventHandler`/`CancelEventHandler` handler
  takes `object? sender`; `RoutedEventHandler`, `DispatcherUnhandledExceptionEventHandler`,
  `UnhandledExceptionEventHandler` handlers take `object sender`. `HwndSourceHook` signature matches.
* P/Invoke: `KBDLLHOOKSTRUCT` (`uint,uint,uint,uint,UIntPtr`), `SetWindowsHookEx`/`CallNextHookEx`/
  `UnhookWindowsHookEx`, `SetWindowDisplayAffinity`, `ShutdownBlockReasonCreate` (Unicode),
  `SetWindowPos`, `Get/SetWindowLongPtr` with 32-bit fallback, `GetWindowThreadProcessId(out uint)`,
  `AttachThreadInput` all follow the Win32 prototypes.
* `KioskFallback.UpdateCoverWindows`: `var ex` inside the `try` block and `catch (Exception ex)` are
  sibling scopes — legal.
* `ApiClient.RequestAsync`: `response` is definitely assigned after the try/catch (the catch always
  throws).
* No `async void`; every XAML `x:Class` matches; all 23 `StaticResource` keys and 4 `FindResource`
  keys exist in `App.xaml`; all 11 XAML event handlers exist with WPF argument types.
* `App.xaml` has no `StartupUri`; `OnStartup` creates the single `MainWindow`; the WebView2 control is
  hosted by `ExamView` (in the visual tree) before `EnsureCoreWebView2Async` is awaited, and the WPF
  control itself waits for its HWND. Smoke test logs `SMOKE_OK` then `Shutdown(0)` after 1 s;
  auto-run reaches `StartExam` exactly once (`_autoRunPending` + unsubscribe).
* Scripts: `restore` without `-r` still covers `win-x64` because the csproj declares
  `<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>`, so `publish -r win-x64 --no-restore` is valid;
  `${env:ProgramFiles(x86)}` is the correct syntax; `$env:AVAIBE_BASE_URL = ""` simply unsets the
  variable (the client treats empty as unset).

### Still uncertain (cannot be settled without a Windows build / device)

1. Whether `WDA_EXCLUDEFROMCAPTURE` on the top-level window also blacks out the WebView2 child HWND
   (item 10 in §2) — hardware test.
2. `EnsureCoreWebView2Async` timing if the exam screen is ever shown while the window is not yet
   loaded (item 11) — the code path always shows the window first, so this should not occur.
3. Win+L and the low-level hook timeout behaviour (items 14/15) — hardware test.
4. `System.Security.Cryptography.ProtectedData` package vs. framework copy may produce an
   informational NU1605/NETSDK warning on some SDKs; not an error (`TreatWarningsAsErrors=false`).
5. Exact WPF `Unloaded` timing of `ExamView` vs. `ExamWebView.Teardown()` (the control is disposed
   just after the screen switches to Exit; WPF tolerates disposing an `HwndHost` that is still being
   removed from the tree, but this is worth one manual check in the test plan).
