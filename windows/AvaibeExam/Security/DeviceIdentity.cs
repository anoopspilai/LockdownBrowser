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
///   * enrollment  = %LOCALAPPDATA%\AvaibeExam\enrollment.dat, DPAPI (CurrentUser scope):
///                   deviceId, deviceToken (secret), pinned server signing key + keyId (§10.1)
///   * nonces      = %LOCALAPPDATA%\AvaibeExam\nonces.dat, DPAPI: used command nonces (§10.4)
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

    // ---- DPAPI file helpers (enrollment.dat, nonces.dat) -------------------------------

    /// <summary>Writes UTF-8 text DPAPI-protected (CurrentUser scope). Throws on failure.</summary>
    public static void ProtectToFile(string path, string plainText)
    {
        Directory.CreateDirectory(Constants.AppDataDirectory);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
        // Write-then-rename so a crash mid-write never leaves a truncated file.
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, protectedBytes);
        File.Copy(tmp, path, overwrite: true);
        try { File.Delete(tmp); } catch { /* ignore */ }
    }

    /// <summary>Reads and unprotects a file written by <see cref="ProtectToFile"/>; null when missing. Throws on tamper/other-user.</summary>
    public static string? UnprotectFromFile(string path)
    {
        if (!File.Exists(path)) return null;
        var protectedBytes = File.ReadAllBytes(path);
        var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    // ---- Enrollment (DPAPI) ---------------------------------------------------------

    /// <summary>
    /// Returns null when not enrolled OR when the stored enrollment predates protocol v2 (no
    /// pinned server key): without a pinned key no RELEASE could ever be verified, so the device
    /// must re-enroll (the login screen asks for the token again).
    /// </summary>
    public static Enrollment? LoadEnrollment()
    {
        try
        {
            var json = UnprotectFromFile(Constants.EnrollmentFile);
            if (json == null) return null;
            var e = Json.Deserialize<Enrollment>(json);
            if (e == null || string.IsNullOrWhiteSpace(e.DeviceId) || string.IsNullOrWhiteSpace(e.DeviceToken)) return null;
            if (string.IsNullOrWhiteSpace(e.ServerPublicKey))
            {
                Log.Warn(LogCat, "Stored enrollment has no pinned server key (pre-v2); re-enrollment required");
                return null;
            }
            return e;
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "Could not load enrollment (treating as not enrolled): " + ex.GetType().Name);
            return null;
        }
    }

    public static void SaveEnrollment(Enrollment enrollment)
    {
        try
        {
            ProtectToFile(Constants.EnrollmentFile, Json.Serialize(enrollment));
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
            if (File.Exists(Constants.NonceFile)) File.Delete(Constants.NonceFile);
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
