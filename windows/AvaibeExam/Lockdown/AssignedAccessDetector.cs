using System;
using AvaibeExam.Util;
using Microsoft.Win32;

namespace AvaibeExam.Lockdown;

public sealed class AssignedAccessResult
{
    public AssignedAccessResult(bool hklmSignal, string detail)
    {
        HklmSignal = hklmSignal;
        Detail = detail;
    }

    /// <summary>
    /// true when a MACHINE-WIDE kiosk configuration exists (HKLM, admin-only writable). This is an
    /// advisory HINT (§10.4 "assignedAccessHint"): it proves an administrator configured Shell
    /// Launcher / Assigned Access on this PC, not that the current session is that kiosk.
    /// </summary>
    public bool HklmSignal { get; }
    public string Detail { get; }
}

/// <summary>
/// ADVISORY detection of a Windows Assigned Access / Shell Launcher kiosk configuration (W-01).
///
/// Only HKLM keys are read — they are writable by administrators only, so a student running as
/// a standard user cannot fake them:
///   1. HKLM\SOFTWARE\Microsoft\Windows Embedded\Shell Launcher
///        Present when Shell Launcher (Education/Enterprise) has been configured on this machine
///        (the WMI/CSP provider stores its per-user/default shell mappings here).
///   2. HKLM\SOFTWARE\Microsoft\Windows\AssignedAccessConfiguration
///        Present when a multi-app / restricted-user-experience Assigned Access profile has been
///        applied (provisioning package, Intune AssignedAccess CSP, PowerShell).
///   3. HKLM\SOFTWARE\Microsoft\Windows\AssignedAccessCsp
///        Present when the AssignedAccess CSP has ever written a configuration (Intune / MDM).
///
/// Deliberately NOT used (removed by the 2026-09-14 review): every HKCU key (the student's own
/// hive — trivially writable), the Winlogon "Shell" value (HKCU override is user-writable), and
/// "no explorer.exe in this session" (the student can simply kill Explorer). None of those can
/// prove OS enforcement. HKLM\...\Authentication\LogonUI\* is a logon-screen cache, not a kiosk
/// marker, and is not consulted either.
///
/// The result is reported to the server as assignedAccessHint / aacEntitlementPresent and shown
/// in the UI as "client-reported"; the server decides requireAAC from its device registry.
/// </summary>
public static class AssignedAccessDetector
{
    private const string LogCat = "assigned-access";

    private const string ShellLauncherKey = @"SOFTWARE\Microsoft\Windows Embedded\Shell Launcher";
    private const string AssignedAccessConfigurationKey = @"SOFTWARE\Microsoft\Windows\AssignedAccessConfiguration";
    private const string AssignedAccessCspKey = @"SOFTWARE\Microsoft\Windows\AssignedAccessCsp";

    public static AssignedAccessResult Detect()
    {
        try
        {
            if (HklmKeyExists(ShellLauncherKey))
            {
                return Positive("HKLM Shell Launcher configuration present (client-reported hint)");
            }
            if (HklmKeyExists(AssignedAccessConfigurationKey))
            {
                return Positive("HKLM AssignedAccessConfiguration present (client-reported hint)");
            }
            if (HklmKeyExists(AssignedAccessCspKey))
            {
                return Positive("HKLM AssignedAccessCsp configuration present (client-reported hint)");
            }
            return new AssignedAccessResult(false, "No machine-wide (HKLM) Shell Launcher / Assigned Access configuration found");
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

    /// <summary>Checks the 64-bit view first, then the 32-bit (WOW6432Node) view.</summary>
    private static bool HklmKeyExists(string path)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hklm.OpenSubKey(path, writable: false);
                if (key != null) return true;
            }
            catch
            {
                // no access: treat as absent
            }
        }
        return false;
    }
}
