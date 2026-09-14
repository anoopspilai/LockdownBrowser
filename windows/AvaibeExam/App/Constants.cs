using System;
using System.IO;

namespace AvaibeExam.Core;

/// <summary>Build-time constants and environment overrides (mirrors macos/App/Constants.swift).</summary>
public static class Constants
{
    /// <summary>Sent as X-Client-Version and compared with policy.minClientVersion.</summary>
    public const string ClientVersion = "0.1.0";
    public const string Platform = "windows";
    public const string ProductName = "Avaibe Exam";

    /// <summary>Default mock backend. Override with AVAIBE_BASE_URL or on the login screen.</summary>
    public const string DefaultBaseUrl = "http://localhost:4000";

    // Environment variables (dev only; a normal Explorer/Start-menu launch does not set them).
    public const string EnvBaseUrl = "AVAIBE_BASE_URL";
    public const string EnvAutoRun = "AVAIBE_AUTO_RUN";
    public const string EnvAutoToken = "AVAIBE_AUTO_TOKEN";
    public const string EnvAutoStudent = "AVAIBE_AUTO_STUDENT";
    public const string EnvAutoExam = "AVAIBE_AUTO_EXAM";
    public const string EnvAutoExitAfter = "AVAIBE_AUTO_EXIT_AFTER";
    /// <summary>When "1": write SMOKE_OK to the log after the window is shown and exit 0 after ~1 s.</summary>
    public const string EnvSmokeTest = "AVAIBE_SMOKE_TEST";

    public const string SingleInstanceMutexName = @"Local\AvaibeExam.SingleInstance";

    /// <summary>Minimum seconds between two identical BLOCKED_SHORTCUT events.</summary>
    public const double BlockedShortcutThrottleSeconds = 2.0;
    /// <summary>Minimum seconds between two APP_DEACTIVATED events.</summary>
    public const double DeactivatedThrottleSeconds = 2.0;
    /// <summary>Minimum seconds between two PROCESS_DETECTED events for the same process.</summary>
    public const double ProcessDetectedThrottleSeconds = 60.0;
    public const int ProcessScanIntervalSeconds = 5;
    public const int FocusWatchdogIntervalMs = 500;
    public const int NetworkProbeIntervalSeconds = 20;

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
    public static string SettingsFile => Path.Combine(AppDataDirectory, "settings.json");
    public static string WebView2UserDataFolder => Path.Combine(AppDataDirectory, "WebView2");

    public static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    public static bool EnvIsOne(string name) => Env(name) == "1";
}
