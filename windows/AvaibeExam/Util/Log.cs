using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using AvaibeExam.Core;

namespace AvaibeExam.Util;

/// <summary>
/// Minimal thread-safe rolling file logger:
///   %LOCALAPPDATA%\AvaibeExam\logs\avaibe-YYYYMMDD.log
/// Lines are queued and written by ONE background thread (W-10), so callers — including the
/// low-level keyboard hook path and the UI thread — never block on disk I/O. Everything
/// security-relevant is logged here so the limitations of the client are visible and auditable.
/// Redaction rule (W-26): never pass tokens, release codes, signatures, authorization objects or
/// page-supplied metadata to Log.*; callers log types, counts and sizes instead.
/// </summary>
public static class Log
{
    private const int QueueCapacity = 20_000;
    private static readonly BlockingCollection<string> Queue = new BlockingCollection<string>(new ConcurrentQueue<string>(), QueueCapacity);
    private static readonly Thread Writer;
    private static string? _directory;
    private static int _dropped;

    static Log()
    {
        Writer = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "log-writer",
            Priority = ThreadPriority.BelowNormal,
        };
        Writer.Start();
    }

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

    /// <summary>Stops accepting lines and waits (bounded) for the writer to drain. Call once at exit.</summary>
    public static void Shutdown(int timeoutMs = 2000)
    {
        try
        {
            Queue.CompleteAdding();
        }
        catch
        {
            // already completed
        }
        try
        {
            Writer.Join(Math.Max(0, timeoutMs));
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
        try
        {
            if (!Queue.TryAdd(line))
            {
                Interlocked.Increment(ref _dropped);
            }
        }
        catch
        {
            // CompleteAdding already called (shutting down) — drop silently.
        }
    }

    private static void WriterLoop()
    {
        var batch = new List<string>(64);
        try
        {
            foreach (var first in Queue.GetConsumingEnumerable())
            {
                batch.Clear();
                batch.Add(first);
                // Drain whatever else is queued right now into the same file append.
                while (batch.Count < 500 && Queue.TryTake(out var more))
                {
                    batch.Add(more);
                }
                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0)
                {
                    batch.Add(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                              "Z [WARN] [log] [t0] " + dropped + " log line(s) dropped (queue full)");
                }
                Append(batch);
            }
        }
        catch
        {
            // Logging must never throw; if the loop dies, lines are simply lost.
        }
    }

    private static void Append(List<string> lines)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllLines(CurrentLogFile, lines);
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
