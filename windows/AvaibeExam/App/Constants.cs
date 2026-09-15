using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AvaibeExam.Core;

/// <summary>Build-time constants and environment overrides (mirrors macos/App/Constants.swift).</summary>
public static class Constants
{
    /// <summary>Sent as X-Client-Version and compared with policy.minClientVersion.</summary>
    public const string ClientVersion = "0.1.0";
    public const string Platform = "windows";
    public const string ProductName = "Avaibe Exam";

    /// <summary>
    /// Default backend for development. Protocol v2 (§10.1) requires https except for
    /// localhost / 127.0.0.1, which ApiClient enforces on every URL.
    /// </summary>
    public const string DefaultBaseUrl = "http://localhost:4000";

    /// <summary>
    /// Optional compile-time pin (§10.1): base64 SPKI DER of the production server's ECDSA P-256
    /// signing key. When non-empty, enrollment is refused unless the server presents exactly this
    /// key, and command verification uses it in preference to the trust-on-first-use pin stored
    /// with the enrollment. Leave empty for development against a self-generated server key.
    /// </summary>
    public const string PinnedServerPublicKey = "";

    /// <summary>
    /// IT-preset base URL (§10.9): HKLM\SOFTWARE\Avaibe\Exam, REG_SZ "BaseUrl". HKLM is writable
    /// only by administrators (Intune / GPO), so a student cannot repoint the client. When present
    /// the login field is read-only.
    /// </summary>
    public const string PresetRegistryKey = @"SOFTWARE\Avaibe\Exam";
    public const string PresetRegistryValue = "BaseUrl";

#if DEBUG
    // Developer switches — DEBUG BUILDS ONLY (§10.9). In Release builds these constants do not exist,
    // so no code path can read them (every use site is inside #if DEBUG as well).
    public const bool DevSwitchesEnabled = true;
    public const string EnvBaseUrl = "AVAIBE_BASE_URL";
    public const string EnvAutoRun = "AVAIBE_AUTO_RUN";
    public const string EnvAutoToken = "AVAIBE_AUTO_TOKEN";
    public const string EnvAutoStudent = "AVAIBE_AUTO_STUDENT";
    public const string EnvAutoExam = "AVAIBE_AUTO_EXAM";
    public const string EnvAutoExitAfter = "AVAIBE_AUTO_EXIT_AFTER";
    /// <summary>When "1": write SMOKE_OK to the log after the window is shown and exit 0 after ~1 s.</summary>
    public const string EnvSmokeTest = "AVAIBE_SMOKE_TEST";
#else
    public const bool DevSwitchesEnabled = false;
#endif

    public const string SingleInstanceMutexName = @"Local\AvaibeExam.SingleInstance";
    public const string ProcessName = "AvaibeExam";

    /// <summary>Minimum seconds between two identical BLOCKED_SHORTCUT events.</summary>
    public const double BlockedShortcutThrottleSeconds = 2.0;
    /// <summary>Minimum seconds between two APP_DEACTIVATED events.</summary>
    public const double DeactivatedThrottleSeconds = 2.0;
    /// <summary>Minimum seconds between two PROCESS_DETECTED events for the same process.</summary>
    public const double ProcessDetectedThrottleSeconds = 60.0;
    public const int ProcessScanIntervalSeconds = 5;
    public const int FocusWatchdogIntervalMs = 500;
    public const int NetworkProbeIntervalSeconds = 20;
    /// <summary>Seconds after FinishRelease at which the release watchdog re-checks the kiosk state (W-09).</summary>
    public const int ReleaseWatchdogSeconds = 15;
    /// <summary>§10.7 limits.</summary>
    public const int MaxEventMetadataBytes = 4096;
    public const int MaxWebEventsPerSecond = 20;
    /// <summary>§10.4 clock tolerance around [issuedAt, expiresAt].</summary>
    public const int CommandTimeSkewSeconds = 60;
    /// <summary>Keyboard-hook liveness probe period (W-19).</summary>
    public const int HookProbeIntervalMs = 5000;
    public const int HookProbeTimeoutMs = 1500;

    /// <summary>Minimum Windows build we consider supported (Windows 10 2004 = 19041).</summary>
    public const int MinimumWindowsBuild = 19041;

    private static string? _appData;

    /// <summary>%LOCALAPPDATA%\AvaibeExam</summary>
    public static string AppDataDirectory
    {
        get
        {
            if (_appData == null)
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
                _appData = Path.Combine(local, "AvaibeExam");
            }
            return _appData;
        }
    }

    public static string EnrollmentFile => Path.Combine(AppDataDirectory, "enrollment.dat");
    public static string NonceFile => Path.Combine(AppDataDirectory, "nonces.dat");
    public static string SettingsFile => Path.Combine(AppDataDirectory, "settings.json");
    public static string WebView2UserDataRoot => Path.Combine(AppDataDirectory, "WebView2");

    private static readonly Regex UnsafePathChars = new Regex("[^A-Za-z0-9_-]", RegexOptions.CultureInvariant);

    /// <summary>Per-session WebView2 profile folder (W-18): WebView2\&lt;sessionId&gt;, sanitized.</summary>
    public static string WebView2UserDataFolderFor(string sessionId)
    {
        var safe = UnsafePathChars.Replace(sessionId ?? string.Empty, "_");
        if (safe.Length == 0) safe = "session";
        if (safe.Length > 64) safe = safe.Substring(0, 64);
        return Path.Combine(WebView2UserDataRoot, safe);
    }

#if DEBUG
    public static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    public static bool EnvIsOne(string name) => Env(name) == "1";
#endif

    /// <summary>
    /// Reads the IT-preset base URL from HKLM (64-bit view first, then 32-bit). Returns null when
    /// absent or unreadable. Validation of the value is done by ApiClient.ParseBaseUrl.
    /// </summary>
    public static string? ReadPresetBaseUrl()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hklm.OpenSubKey(PresetRegistryKey, writable: false);
                var value = key?.GetValue(PresetRegistryValue) as string;
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            catch
            {
                // no access / missing: fall through
            }
        }
        return null;
    }
}
