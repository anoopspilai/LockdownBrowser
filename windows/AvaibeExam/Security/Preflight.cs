using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Threading.Tasks;
using AvaibeExam.Core;
using AvaibeExam.Lockdown;
using AvaibeExam.Models;
using AvaibeExam.Networking;
using AvaibeExam.Util;
using Microsoft.Win32;

namespace AvaibeExam.Security;

/// <summary>Everything the preflight measured, plus human-readable details for the checklist.</summary>
public sealed class PreflightRaw
{
    public PreflightReport Report { get; set; } = new PreflightReport();
    public PreflightExtras Extras { get; set; } = new PreflightExtras();
    public bool OsVersionOk { get; set; }
    public string OsDetail { get; set; } = string.Empty;
    public string SecureBootDetail { get; set; } = string.Empty;
    public string MdmDetail { get; set; } = string.Empty;
    public string ScreenSharingDetail { get; set; } = string.Empty;
    public string AssignedAccessDetail { get; set; } = string.Empty;
    public string InternetDetail { get; set; } = string.Empty;
    public bool WebView2Available { get; set; }
    public string WebView2Detail { get; set; } = string.Empty;
    public string VirtualMachineDetail { get; set; } = string.Empty;
    /// <summary>HKCU LowLevelHooksTimeout value in ms; 0 when absent (Windows default applies).</summary>
    public int LowLevelHooksTimeoutMs { get; set; }
    public string HookTimeoutDetail { get; set; } = string.Empty;
    public bool DebuggerAttached { get; set; }
    public string DebuggerDetail { get; set; } = string.Empty;
}

/// <summary>
/// Builds the PreflightReport (+ Windows extras) sent to the server and the checklist shown on
/// the Preflight screen, exactly per CONTRACT §9.2. Every check is read-only and best-effort;
/// anything that cannot be determined is reported conservatively.
/// </summary>
public static class Preflight
{
    private const string LogCat = "preflight";

    public static async Task<PreflightRaw> RunAsync(ApiClient? api)
    {
        var raw = await Task.Run(() => RunLocalChecks()).ConfigureAwait(true);

        // Internet: GET /healthz (§9.2). Runs after the local checks so the API call is not
        // blocked by registry reads.
        var online = NetworkMonitor.Snapshot();
        var internetDetail = online ? "Network interface available" : "No network interface available";
        if (api != null)
        {
            try
            {
                var h = await api.HealthzAsync().ConfigureAwait(true);
                online = true;
                internetDetail = "Backend reachable (" + (h.Version ?? "ok") + ")";
            }
            catch (Exception ex)
            {
                internetDetail = online
                    ? "Network up but backend unreachable: " + ex.Message
                    : "Offline and backend unreachable";
            }
        }
        raw.Report.InternetReachable = online;
        raw.InternetDetail = internetDetail;

        Log.Info(LogCat,
            $"Preflight: os={raw.Report.OsVersion} secureBoot={raw.Report.SipEnabled} mdm={raw.Report.MdmEnrolled} " +
            $"account={raw.Report.AccountType} displays={raw.Report.DisplayCount} sharing={raw.Report.ScreenSharingActive} " +
            $"assignedAccessHint={raw.Report.AssignedAccessHint} cam={AuthorizationStateConverter.Instance.ToWire(raw.Report.CameraAuthorized)} " +
            $"mic={AuthorizationStateConverter.Instance.ToWire(raw.Report.MicrophoneAuthorized)} online={online} " +
            $"vm={raw.Extras.VirtualMachine} tpm={raw.Extras.TpmPresent} rdp={raw.Extras.RdpSession} edition={raw.Extras.Edition} webview2={raw.WebView2Available} " +
            $"hookTimeout={raw.LowLevelHooksTimeoutMs} debugger={raw.DebuggerAttached} (all client-reported)");
        return raw;
    }

    // ---- Local checks ----------------------------------------------------------------

    private static PreflightRaw RunLocalChecks()
    {
        var raw = new PreflightRaw();
        var report = raw.Report;
        var extras = raw.Extras;

        // OS version / edition
        var v = Environment.OSVersion.Version;
        report.OsVersion = DeviceIdentity.OsVersion();
        raw.OsVersionOk = v.Major >= 10 && v.Build >= Constants.MinimumWindowsBuild;
        var edition = ReadEdition();
        extras.Edition = edition.editionId;
        raw.OsDetail = $"{edition.productName} {edition.displayVersion} (build {v.Build}; minimum build {Constants.MinimumWindowsBuild})".Trim();

        // Secure Boot (== sipEnabled on Windows)
        var secureBoot = ReadSecureBoot();
        report.SipEnabled = secureBoot.enabled;
        extras.SecureBoot = secureBoot.enabled;
        raw.SecureBootDetail = secureBoot.detail;

        // MDM / domain / AAD
        var mdm = ReadMdm();
        report.MdmEnrolled = mdm.enrolled;
        raw.MdmDetail = mdm.detail;

        // Account type
        report.AccountType = IsAdministrator() ? "admin" : "standard";

        // Displays
        report.DisplayCount = DisplayMonitor.CurrentCount();

        // Screen sharing / remote control
        var rdp = IsRdpSession();
        extras.RdpSession = rdp;
        var sharingProcs = ProcessMonitor.ScanOnce();
        report.ScreenSharingActive = rdp || sharingProcs.Count > 0;
        if (rdp)
        {
            raw.ScreenSharingDetail = "Remote Desktop session (" + (Environment.GetEnvironmentVariable("SESSIONNAME") ?? "RDP") + ")" +
                                      (sharingProcs.Count > 0 ? "; running: " + string.Join(", ", sharingProcs) : string.Empty);
        }
        else if (sharingProcs.Count > 0)
        {
            raw.ScreenSharingDetail = "Running: " + string.Join(", ", sharingProcs);
        }
        else
        {
            raw.ScreenSharingDetail = "No remote-control or capture processes detected";
        }

        // Assigned Access hint (§10.4): HKLM signal only; false unless present. ADVISORY.
        var aa = AssignedAccessDetector.Detect();
        report.AacEntitlementPresent = aa.HklmSignal;
        report.AssignedAccessHint = aa.HklmSignal;
        extras.AssignedAccessHint = aa.HklmSignal;
        raw.AssignedAccessDetail = aa.Detail;

        // Low-level hook timeout (W-19): a student could set it very low so Windows drops our hook.
        var hookTimeout = ReadLowLevelHooksTimeout();
        raw.LowLevelHooksTimeoutMs = hookTimeout;
        extras.LowLevelHooksTimeoutMs = hookTimeout;
        raw.HookTimeoutDetail = hookTimeout == 0
            ? "Not set (Windows default; the hook liveness probe still runs)"
            : "LowLevelHooksTimeout = " + hookTimeout + " ms" + (hookTimeout < 1000 ? " — below 1000 ms, Windows may drop the keyboard hook" : string.Empty);

        // Debugger (§10 preflight item): managed or native debugger attached to this process.
        var dbg = ReadDebugger();
        raw.DebuggerAttached = dbg.attached;
        raw.DebuggerDetail = dbg.detail;
        extras.DebuggerAttached = dbg.attached;

        // Camera / microphone consent
        report.CameraAuthorized = ReadConsent("webcam");
        report.MicrophoneAuthorized = ReadConsent("microphone");

        // Extras
        extras.TpmPresent = ReadTpmPresent();
        var vm = ReadVirtualMachine();
        extras.VirtualMachine = vm.isVm;
        raw.VirtualMachineDetail = vm.detail;
        // WDA_EXCLUDEFROMCAPTURE requires Windows 10 2004 (build 19041); the real result is
        // only known once the window applies it (KioskFallback reports the outcome).
        extras.ScreenCaptureProtection = v.Build >= 19041;

        // WebView2 runtime
        var wv = ReadWebView2();
        raw.WebView2Available = wv.available;
        raw.WebView2Detail = wv.detail;

        report.ClientVersion = Constants.ClientVersion;
        return raw;
    }

    private static RegistryKey? OpenHklm(string path)
    {
        try
        {
            // Sub-keys own their handles, so the base key can be disposed right away.
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return hklm.OpenSubKey(path, writable: false);
        }
        catch
        {
            return null;
        }
    }

    private static RegistryKey? OpenHkcu(string path)
    {
        try
        {
            return Registry.CurrentUser.OpenSubKey(path, writable: false);
        }
        catch
        {
            return null;
        }
    }

    private static (string productName, string editionId, string displayVersion) ReadEdition()
    {
        try
        {
            using var key = OpenHklm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key == null) return ("Windows", "Unknown", string.Empty);
            var product = key.GetValue("ProductName") as string ?? "Windows";
            var edition = key.GetValue("EditionID") as string ?? "Unknown";
            var display = key.GetValue("DisplayVersion") as string ?? string.Empty;
            var build = key.GetValue("CurrentBuild") as string;
            // ProductName still says "Windows 10" on Windows 11; use the build to label it.
            if (int.TryParse(build, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) && b >= 22000 && product.Contains("Windows 10"))
            {
                product = product.Replace("Windows 10", "Windows 11");
            }
            return (product, edition, display);
        }
        catch
        {
            return ("Windows", "Unknown", string.Empty);
        }
    }

    private static (bool enabled, string detail) ReadSecureBoot()
    {
        try
        {
            using var key = OpenHklm(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (key == null) return (false, "Secure Boot state unavailable (legacy BIOS or no access) — treated as disabled");
            var value = key.GetValue("UEFISecureBootEnabled");
            if (value is int i) return (i == 1, i == 1 ? "Secure Boot enabled (UEFI)" : "Secure Boot disabled");
            return (false, "Secure Boot state not readable — treated as disabled");
        }
        catch (Exception ex)
        {
            return (false, "Secure Boot check failed: " + ex.Message);
        }
    }

    private static (bool enrolled, string detail) ReadMdm()
    {
        var parts = new List<string>();
        var enrolled = false;
        try
        {
            using var key = OpenHklm(@"SOFTWARE\Microsoft\Enrollments");
            if (key != null)
            {
                foreach (var name in key.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = key.OpenSubKey(name, writable: false);
                        var provider = sub?.GetValue("ProviderID") as string;
                        if (!string.IsNullOrWhiteSpace(provider))
                        {
                            enrolled = true;
                            parts.Add("MDM provider " + provider);
                            break;
                        }
                    }
                    catch
                    {
                        // ignore this enrollment
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            using var join = OpenHklm(@"SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo");
            if (join != null && join.GetSubKeyNames().Length > 0)
            {
                enrolled = true;
                parts.Add("Azure AD joined");
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            if (!string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                enrolled = true;
                parts.Add("Domain " + Environment.UserDomainName);
            }
        }
        catch
        {
            // ignore
        }

        return (enrolled, enrolled ? string.Join("; ", parts) : "Not MDM-enrolled, not domain/Azure AD joined");
    }

    /// <summary>
    /// True when the user is a member of BUILTIN\Administrators, even when running un-elevated
    /// (UAC filtered token lists the group as deny-only, which still counts as an admin account).
    /// </summary>
    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            if (principal.IsInRole(WindowsBuiltInRole.Administrator)) return true;
            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            if (identity.Groups != null)
            {
                foreach (var group in identity.Groups)
                {
                    if (group.Equals(adminSid)) return true;
                }
            }
            return false;
        }
        catch
        {
            return true; // conservative
        }
    }

    public static bool IsRdpSession()
    {
        try
        {
            if (NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTESESSION) != 0) return true;
        }
        catch
        {
            // ignore
        }
        var session = Environment.GetEnvironmentVariable("SESSIONNAME");
        return session != null && session.StartsWith("RDP-", StringComparison.OrdinalIgnoreCase);
    }

    private static AuthorizationState ReadConsent(string capability)
    {
        try
        {
            using var key = OpenHkcu(@"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\" + capability);
            var value = key?.GetValue("Value") as string;
            if (string.IsNullOrEmpty(value)) return AuthorizationState.NotDetermined;
            if (string.Equals(value, "Allow", StringComparison.OrdinalIgnoreCase)) return AuthorizationState.Authorized;
            if (string.Equals(value, "Deny", StringComparison.OrdinalIgnoreCase)) return AuthorizationState.Denied;
            return AuthorizationState.NotDetermined;
        }
        catch
        {
            return AuthorizationState.NotDetermined;
        }
    }

    private static bool ReadTpmPresent()
    {
        // Best effort: TPM 2.0 exposes ACPI device MSFT0101; TPM 1.2 devices vary.
        try
        {
            using var acpi = OpenHklm(@"SYSTEM\CurrentControlSet\Enum\ACPI\MSFT0101");
            if (acpi != null) return true;
        }
        catch
        {
            // ignore
        }
        try
        {
            using var tpm = OpenHklm(@"SYSTEM\CurrentControlSet\Services\TPM");
            // The service key exists on every install; only count it when it has a Parameters
            // subkey populated by the driver (weak signal, documented as best-effort).
            if (tpm != null)
            {
                using var p = tpm.OpenSubKey("Parameters", writable: false);
                return p != null;
            }
        }
        catch
        {
            // ignore
        }
        return false;
    }

    private static (bool isVm, string detail) ReadVirtualMachine()
    {
        try
        {
            using var bios = OpenHklm(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (bios == null) return (false, "BIOS information unavailable");
            var manufacturer = bios.GetValue("SystemManufacturer") as string ?? string.Empty;
            var product = bios.GetValue("SystemProductName") as string ?? string.Empty;
            var vendor = bios.GetValue("BIOSVendor") as string ?? string.Empty;
            var haystack = (manufacturer + " " + product + " " + vendor).ToLowerInvariant();
            string[] markers = { "vmware", "virtualbox", "qemu", "hyper-v", "virtual machine", "parallels", "xen", "kvm", "innotek", "bochs" };
            foreach (var m in markers)
            {
                if (haystack.Contains(m))
                {
                    return (true, $"Virtual machine detected ({manufacturer} {product})".Trim());
                }
            }
            return (false, $"{manufacturer} {product}".Trim());
        }
        catch (Exception ex)
        {
            return (false, "VM check failed: " + ex.Message);
        }
    }

    /// <summary>HKCU\Control Panel\Desktop\LowLevelHooksTimeout (REG_DWORD ms, or REG_SZ); 0 when absent.</summary>
    private static int ReadLowLevelHooksTimeout()
    {
        try
        {
            using var key = OpenHkcu(@"Control Panel\Desktop");
            var value = key?.GetValue("LowLevelHooksTimeout");
            if (value is int i) return Math.Max(0, i);
            if (value is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return Math.Max(0, parsed);
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static (bool attached, string detail) ReadDebugger()
    {
        var managed = false;
        var native = false;
        var remote = false;
        try { managed = Debugger.IsAttached; } catch { /* ignore */ }
        try { native = NativeMethods.IsDebuggerPresent(); } catch { /* ignore */ }
        try
        {
            if (NativeMethods.CheckRemoteDebuggerPresent(NativeMethods.GetCurrentProcess(), out var r)) remote = r;
        }
        catch
        {
            // ignore
        }
        var attached = managed || native || remote;
        var kinds = new List<string>();
        if (managed) kinds.Add("managed");
        if (native) kinds.Add("native");
        if (remote) kinds.Add("remote");
        var detail = attached ? "Debugger attached (" + string.Join(", ", kinds) + ")" : "No debugger attached";
        return (attached, detail);
    }

    private static (bool available, string detail) ReadWebView2()
    {
        try
        {
            var version = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrWhiteSpace(version)) return (false, "WebView2 Runtime not found");
            return (true, "WebView2 Runtime " + version);
        }
        catch (Exception ex)
        {
            return (false, "WebView2 Runtime not found (" + ex.GetType().Name + "). Install the Evergreen WebView2 Runtime.");
        }
    }

    // ---- Checklist -------------------------------------------------------------------

    /// <summary>Turns the raw results into checklist rows; required flags come from the policy.</summary>
    public static List<PreflightItem> Items(PreflightRaw raw, Policy policy, string? minClientVersion)
    {
        var r = raw.Report;
        var items = new List<PreflightItem>();

        items.Add(new PreflightItem(PreflightKind.OsVersion, "Windows version",
            raw.OsVersionOk ? PreflightStatus.Pass : PreflightStatus.Fail,
            raw.OsDetail, isRequired: true));

        items.Add(new PreflightItem(PreflightKind.SecureBoot, "Secure Boot",
            r.SipEnabled ? PreflightStatus.Pass : (policy.RequireSIP ? PreflightStatus.Fail : PreflightStatus.Warning),
            raw.SecureBootDetail, isRequired: policy.RequireSIP));

        items.Add(new PreflightItem(PreflightKind.Mdm, "Device management (MDM)",
            r.MdmEnrolled ? PreflightStatus.Pass : (policy.RequireMDM ? PreflightStatus.Fail : PreflightStatus.NotApplicable),
            raw.MdmDetail, isRequired: policy.RequireMDM));

        var standard = r.AccountType == "standard";
        items.Add(new PreflightItem(PreflightKind.AccountType, "Account type",
            standard ? PreflightStatus.Pass : (policy.RequireStandardAccount ? PreflightStatus.Fail : PreflightStatus.Warning),
            standard ? "Standard user account" : "Administrator account",
            isRequired: policy.RequireStandardAccount));

        var displaysRequired = policy.BlockExternalDisplay && policy.ExternalDisplayAction == ExternalDisplayAction.BlockStart;
        var displaysOk = r.DisplayCount <= 1;
        items.Add(new PreflightItem(PreflightKind.Displays, "Displays",
            displaysOk ? PreflightStatus.Pass : (displaysRequired ? PreflightStatus.Fail : PreflightStatus.Warning),
            displaysOk ? "1 display" : $"{r.DisplayCount} displays connected — disconnect external displays" +
                                        (policy.BlockExternalDisplay && !displaysRequired ? $" (policy action during the exam: {policy.ExternalDisplayAction.Wire()})" : string.Empty),
            isRequired: displaysRequired));

        items.Add(new PreflightItem(PreflightKind.ScreenSharing, "Screen sharing / remote control",
            r.ScreenSharingActive ? PreflightStatus.Fail : PreflightStatus.Pass,
            raw.ScreenSharingDetail, isRequired: true));

        items.Add(new PreflightItem(PreflightKind.AssignedAccess, "Assigned Access hint (client-reported)",
            r.AssignedAccessHint ? PreflightStatus.Pass : (policy.RequireAAC ? PreflightStatus.Fail : PreflightStatus.Warning),
            r.AssignedAccessHint
                ? raw.AssignedAccessDetail + " — advisory; the server decides"
                : raw.AssignedAccessDetail + " — kiosk fallback will be used" + (policy.RequireAAC ? " (policy requires Assigned Access)" : string.Empty),
            isRequired: policy.RequireAAC));

        var hookOk = raw.LowLevelHooksTimeoutMs == 0 || raw.LowLevelHooksTimeoutMs >= 1000;
        items.Add(new PreflightItem(PreflightKind.HookTimeout, "Low-level hook timeout (client-reported)",
            hookOk ? PreflightStatus.Pass : PreflightStatus.Fail,
            raw.HookTimeoutDetail, isRequired: true));

#if DEBUG
        const bool debuggerRequired = false; // a developer's debugger must not block a debug run
#else
        const bool debuggerRequired = true;
#endif
        items.Add(new PreflightItem(PreflightKind.Debugger, "Debugger attached (client-reported)",
            raw.DebuggerAttached ? PreflightStatus.Fail : PreflightStatus.Pass,
            raw.DebuggerDetail, isRequired: debuggerRequired));

        items.Add(new PreflightItem(PreflightKind.Camera, "Camera permission",
            r.CameraAuthorized == AuthorizationState.Authorized ? PreflightStatus.Pass : PreflightStatus.Warning,
            AuthorizationStateConverter.Instance.ToWire(r.CameraAuthorized), isRequired: false));

        items.Add(new PreflightItem(PreflightKind.Microphone, "Microphone permission",
            r.MicrophoneAuthorized == AuthorizationState.Authorized ? PreflightStatus.Pass : PreflightStatus.Warning,
            AuthorizationStateConverter.Instance.ToWire(r.MicrophoneAuthorized), isRequired: false));

        items.Add(new PreflightItem(PreflightKind.Internet, "Internet / backend",
            r.InternetReachable ? PreflightStatus.Pass : PreflightStatus.Fail,
            raw.InternetDetail, isRequired: true));

        var minVersion = string.IsNullOrWhiteSpace(minClientVersion) ? policy.MinClientVersion : minClientVersion!;
        var versionOk = SemVer.IsAtLeast(Constants.ClientVersion, minVersion);
        items.Add(new PreflightItem(PreflightKind.ClientVersion, "Client version",
            versionOk ? PreflightStatus.Pass : PreflightStatus.Fail,
            $"{Constants.ClientVersion} (minimum {minVersion})", isRequired: true));

        items.Add(new PreflightItem(PreflightKind.WebView2, "WebView2 Runtime",
            raw.WebView2Available ? PreflightStatus.Pass : PreflightStatus.Fail,
            raw.WebView2Detail, isRequired: true));

        items.Add(new PreflightItem(PreflightKind.VirtualMachine, "Physical machine",
            raw.Extras.VirtualMachine ? PreflightStatus.Warning : PreflightStatus.Pass,
            raw.VirtualMachineDetail, isRequired: false));

        items.Add(new PreflightItem(PreflightKind.RdpSession, "Local session",
            raw.Extras.RdpSession ? PreflightStatus.Fail : PreflightStatus.Pass,
            raw.Extras.RdpSession ? "Running inside a Remote Desktop session" : "Local console session",
            isRequired: true));

        return items;
    }
}
