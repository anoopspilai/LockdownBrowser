using System;
using System.Diagnostics;
using AvaibeExam.Util;
using Microsoft.Win32;

namespace AvaibeExam.Lockdown;

public sealed class AssignedAccessResult
{
    public AssignedAccessResult(bool isAssignedAccess, string detail)
    {
        IsAssignedAccess = isAssignedAccess;
        Detail = detail;
    }

    public bool IsAssignedAccess { get; }
    public string Detail { get; }
}

/// <summary>
/// Best-effort detection of a Windows Assigned Access / Shell Launcher kiosk session
/// (CONTRACT §9.2 "aacEntitlementPresent" and §9.3 "assigned-access"). Signals, any of which
/// counts as positive:
///   1. HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\AssignedAccessConfiguration exists
///      (written by Windows for the kiosk account).
///   2. The Winlogon shell for this user/machine is not explorer.exe (Shell Launcher).
///   3. No explorer.exe is running in this logon session (single-app kiosk shells never start it).
///      (Only counted when every process' session id could be read — never on an enumeration error.)
///
/// Deliberately NOT used: machine-wide HKLM AssignedAccess keys, whose presence on ordinary desktops
/// is not documented well enough; a false "assigned-access" would over-claim OS enforcement.
///
/// TODO (documented): the authoritative source is the MDM bridge WMI class
/// root\cimv2\mdm\dmmap MDM_AssignedAccess, which needs System.Management (an extra package)
/// and usually administrator rights. It is intentionally not used in this build.
/// </summary>
public static class AssignedAccessDetector
{
    private const string LogCat = "assigned-access";

    public static AssignedAccessResult Detect()
    {
        try
        {
            if (KeyExists(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\AssignedAccessConfiguration"))
            {
                return Positive("Assigned Access configuration present for this user");
            }
            var shell = ReadShell();
            if (shell != null && !shell.Contains("explorer.exe", StringComparison.OrdinalIgnoreCase))
            {
                return Positive("Custom Winlogon shell (" + shell + ") — Shell Launcher");
            }

            if (!ExplorerRunningInThisSession())
            {
                return Positive("No Explorer shell in this session — kiosk shell");
            }

            return new AssignedAccessResult(false, "Not running in an Assigned Access / Shell Launcher session");
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "Detection failed: " + ex.Message);
            return new AssignedAccessResult(false, "Assigned Access detection failed: " + ex.Message);
        }
    }

    private static AssignedAccessResult Positive(string detail)
    {
        Log.Info(LogCat, detail);
        return new AssignedAccessResult(true, detail);
    }

    private static bool KeyExists(RegistryKey root, string path)
    {
        try
        {
            using var key = root.OpenSubKey(path, writable: false);
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Winlogon "Shell" value: HKCU overrides HKLM. Null when unset (=> explorer.exe).</summary>
    private static string? ReadShell()
    {
        try
        {
            using (var user = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", writable: false))
            {
                var s = user?.GetValue("Shell") as string;
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var machine = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", writable: false);
            var m = machine?.GetValue("Shell") as string;
            return string.IsNullOrWhiteSpace(m) ? null : m;
        }
        catch
        {
            return null;
        }
    }

    private static bool ExplorerRunningInThisSession()
    {
        try
        {
            int session;
            using (var self = Process.GetCurrentProcess())
            {
                session = self.SessionId;
            }
            var explorers = Process.GetProcessesByName("explorer");
            try
            {
                var unknown = false;
                foreach (var p in explorers)
                {
                    try
                    {
                        if (p.SessionId == session) return true;
                    }
                    catch
                    {
                        unknown = true;
                    }
                }
                // Fail safe: if any session id could not be read, assume a normal desktop.
                return unknown;
            }
            finally
            {
                foreach (var p in explorers) p.Dispose();
            }
        }
        catch
        {
            return true; // unknown => assume a normal desktop
        }
    }
}
