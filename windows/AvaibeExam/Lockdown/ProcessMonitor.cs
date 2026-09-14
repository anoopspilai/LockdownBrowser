using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using AvaibeExam.Core;
using AvaibeExam.Util;

namespace AvaibeExam.Lockdown;

/// <summary>
/// Every 5 s enumerates running processes and raises PROCESS_DETECTED (CONTRACT §9.5) for
/// known remote-control / screen-capture tools. Detection only — this client never kills
/// processes. Names are matched case-insensitively against Process.ProcessName (no ".exe").
/// </summary>
public sealed class ProcessMonitor
{
    private const string LogCat = "process";

    /// <summary>process name (lower case) => action ("warn" or "flag").</summary>
    private static readonly Dictionary<string, string> Blacklist = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // Remote control
        ["teamviewer"] = "warn",
        ["teamviewer_desktop"] = "warn",
        ["anydesk"] = "warn",
        ["vncserver"] = "warn",
        ["winvnc"] = "warn",
        ["tvnserver"] = "warn",
        ["chrome_remote_desktop_host"] = "warn",
        ["remoting_host"] = "warn",
        ["msrdc"] = "warn",
        ["mstsc"] = "warn",
        ["rustdesk"] = "warn",
        ["ultraviewer"] = "warn",
        ["ultraviewer_desktop"] = "warn",
        ["ammyy"] = "warn",
        ["supremo"] = "warn",
        // Screen capture / recording / streaming
        ["obs64"] = "warn",
        ["obs32"] = "warn",
        ["obs"] = "warn",
        ["sharex"] = "warn",
        ["snagit32"] = "warn",
        ["snagiteditor"] = "warn",
        ["bandicam"] = "warn",
        ["camtasia"] = "warn",
        ["camtasiastudio"] = "warn",
        ["xsplit"] = "warn",
        ["xsplit.core"] = "warn",
        ["streamlabs obs"] = "warn",
        ["streamlabs"] = "warn",
        ["loom"] = "warn",
        ["screenrec"] = "warn",
        ["nvidia share"] = "flag",
        // Conferencing (may be legitimately open; flag only)
        ["zoom"] = "flag",
        ["discord"] = "flag",
        ["teams"] = "flag",
        ["ms-teams"] = "flag",
        ["skype"] = "flag",
    };

    private readonly object _gate = new object();
    private readonly Dictionary<string, DateTime> _lastReported = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Dispatcher? _dispatcher;

    /// <summary>(processName, action) on the UI thread; already throttled per process name.</summary>
    public Action<string, string>? OnDetected { get; set; }

    public bool IsRunning => _cts != null;

    public void Start(Dispatcher dispatcher)
    {
        Stop();
        _dispatcher = dispatcher;
        var cts = new CancellationTokenSource();
        _cts = cts;
        lock (_gate) { _lastReported.Clear(); }
        _ = Task.Run(() => LoopAsync(cts.Token));
        Log.Info(LogCat, $"Process monitor started (every {Constants.ProcessScanIntervalSeconds}s, {Blacklist.Count} names)");
    }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        if (cts != null)
        {
            try { cts.Cancel(); } catch { /* ignore */ }
            cts.Dispose();
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            // Scan once right away, then periodically.
            Scan(ct);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Constants.ProcessScanIntervalSeconds));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                Scan(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
        catch (Exception ex)
        {
            Log.Error(LogCat, "Process monitor loop crashed", ex);
        }
    }

    private void Scan(CancellationToken ct)
    {
        var found = ScanOnce();
        if (found.Count == 0 || ct.IsCancellationRequested) return;
        var now = DateTime.UtcNow;
        var dispatcher = _dispatcher;
        foreach (var name in found)
        {
            bool report;
            lock (_gate)
            {
                report = !_lastReported.TryGetValue(name, out var last) ||
                         (now - last).TotalSeconds >= Constants.ProcessDetectedThrottleSeconds;
                if (report) _lastReported[name] = now;
            }
            if (!report) continue;
            var action = Blacklist.TryGetValue(name, out var a) ? a : "flag";
            Log.Warn(LogCat, $"Blacklisted process running: {name} (action={action})");
            var handler = OnDetected;
            if (handler != null && dispatcher != null)
            {
                dispatcher.BeginInvoke(new Action(() => handler(name, action)), DispatcherPriority.Background);
            }
        }
    }

    /// <summary>Synchronous scan; returns the distinct blacklisted process names currently running.</summary>
    public static List<string> ScanOnce()
    {
        var result = new List<string>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "Process enumeration failed: " + ex.Message);
            return result;
        }
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in processes)
            {
                string name;
                try
                {
                    name = p.ProcessName;
                }
                catch
                {
                    continue;
                }
                if (string.IsNullOrEmpty(name)) continue;
                if (Blacklist.ContainsKey(name) && seen.Add(name))
                {
                    result.Add(name);
                }
            }
        }
        finally
        {
            foreach (var p in processes)
            {
                try { p.Dispose(); } catch { /* ignore */ }
            }
        }
        return result;
    }

    public static string ActionFor(string processName) =>
        Blacklist.TryGetValue(processName, out var a) ? a : "flag";
}
