using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using AvaibeExam.Core;

namespace AvaibeExam.Util;

/// <summary>
/// Minimal thread-safe rolling file logger:
///   %LOCALAPPDATA%\AvaibeExam\logs\avaibe-YYYYMMDD.log
/// Every line is also written to the debugger output. Everything security-relevant
/// (blocked shortcuts, deactivation, lockdown limitations) is logged here so the
/// limitations of the client are visible and auditable.
/// </summary>
public static class Log
{
    private static readonly object Gate = new object();
    private static string? _directory;

    public static string LogDirectory
    {
        get
        {
            _directory ??= Path.Combine(Constants.AppDataDirectory, "logs");
            return _directory;
        }
    }

    public static string CurrentLogFile =>
        Path.Combine(LogDirectory, "avaibe-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");

    public static void Debug(string category, string message) => Write("DEBUG", category, message);
    public static void Info(string category, string message) => Write("INFO", category, message);
    public static void Warn(string category, string message) => Write("WARN", category, message);
    public static void Error(string category, string message) => Write("ERROR", category, message);

    public static void Error(string category, string message, Exception ex) =>
        Write("ERROR", category, message + ": " + ex.GetType().Name + ": " + ex.Message);

    /// <summary>Deletes log files older than <paramref name="keepDays"/> days (best effort).</summary>
    public static void Prune(int keepDays)
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return;
            var cutoff = DateTime.Now.AddDays(-keepDays);
            foreach (var file in Directory.GetFiles(LogDirectory, "avaibe-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void Write(string level, string category, string message)
    {
        var line = string.Concat(
            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            "Z [", level, "] [", category, "] [t", Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture), "] ",
            message);
        System.Diagnostics.Debug.WriteLine(line);
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(CurrentLogFile, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never throw.
            }
        }
    }
}
