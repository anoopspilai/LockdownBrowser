using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AvaibeExam.Core;
using AvaibeExam.Models;
using AvaibeExam.Util;
using Microsoft.Win32;

namespace AvaibeExam.Security;

/// <summary>
/// Hardware identity + persisted enrollment + settings (CONTRACT §9.1).
///   * hardwareId  = HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid (fallback: generated GUID in settings.json)
///   * enrollment  = %LOCALAPPDATA%\AvaibeExam\enrollment.dat, DPAPI (CurrentUser scope)
///   * settings    = %LOCALAPPDATA%\AvaibeExam\settings.json (never contains secrets)
/// </summary>
public static class DeviceIdentity
{
    private const string LogCat = "identity";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AvaibeExam.enrollment.v1");
    private static readonly object SettingsGate = new object();
    private static AppSettings? _settings;

    // ---- Hardware --------------------------------------------------------------------

    public static string HardwareId()
    {
        var guid = ReadMachineGuid();
        if (!string.IsNullOrWhiteSpace(guid)) return guid!;

        var settings = LoadSettings();
        if (!string.IsNullOrWhiteSpace(settings.FallbackHardwareId)) return settings.FallbackHardwareId!;

        var generated = Guid.NewGuid().ToString();
        settings.FallbackHardwareId = generated;
        SaveSettings(settings);
        Log.Warn(LogCat, "MachineGuid unavailable; using generated hardware id");
        return generated;
    }

    private static string? ReadMachineGuid()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
            return key?.GetValue("MachineGuid") as string;
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "Could not read MachineGuid: " + ex.Message);
            return null;
        }
    }

    public static string DeviceName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch
        {
            return "Windows PC";
        }
    }

    /// <summary>"10.0.22631" (major.minor.build). .NET 5+ reports the true version.</summary>
    public static string OsVersion()
    {
        var v = Environment.OSVersion.Version;
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    // ---- Enrollment (DPAPI) ---------------------------------------------------------

    public static Enrollment? LoadEnrollment()
    {
        try
        {
            var path = Constants.EnrollmentFile;
            if (!File.Exists(path)) return null;
            var protectedBytes = File.ReadAllBytes(path);
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plain);
            var e = Json.Deserialize<Enrollment>(json);
            if (e == null || string.IsNullOrWhiteSpace(e.DeviceId) || string.IsNullOrWhiteSpace(e.DeviceToken)) return null;
            return e;
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "Could not load enrollment (treating as not enrolled): " + ex.Message);
            return null;
        }
    }

    public static void SaveEnrollment(Enrollment enrollment)
    {
        try
        {
            Directory.CreateDirectory(Constants.AppDataDirectory);
            var json = Json.Serialize(enrollment);
            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(Constants.EnrollmentFile, protectedBytes);
        }
        catch (Exception ex)
        {
            Log.Error(LogCat, "Could not save enrollment", ex);
        }
    }

    public static void ResetEnrollment()
    {
        try
        {
            if (File.Exists(Constants.EnrollmentFile)) File.Delete(Constants.EnrollmentFile);
            Log.Info(LogCat, "Enrollment reset by user");
        }
        catch (Exception ex)
        {
            Log.Error(LogCat, "Could not delete enrollment file", ex);
        }
    }

    // ---- Settings --------------------------------------------------------------------

    public static AppSettings LoadSettings()
    {
        lock (SettingsGate)
        {
            if (_settings != null) return _settings;
            try
            {
                if (File.Exists(Constants.SettingsFile))
                {
                    _settings = Json.Deserialize<AppSettings>(File.ReadAllText(Constants.SettingsFile));
                }
            }
            catch (Exception ex)
            {
                Log.Warn(LogCat, "Could not read settings.json: " + ex.Message);
            }
            _settings ??= new AppSettings();
            return _settings;
        }
    }

    public static void SaveSettings(AppSettings settings)
    {
        lock (SettingsGate)
        {
            _settings = settings;
            try
            {
                Directory.CreateDirectory(Constants.AppDataDirectory);
                File.WriteAllText(Constants.SettingsFile, Json.Serialize(settings));
            }
            catch (Exception ex)
            {
                Log.Warn(LogCat, "Could not write settings.json: " + ex.Message);
            }
        }
    }

    public static void UpdateSettings(Action<AppSettings> mutate)
    {
        var s = LoadSettings();
        mutate(s);
        SaveSettings(s);
    }
}
