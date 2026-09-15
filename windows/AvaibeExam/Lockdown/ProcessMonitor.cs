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
/// Every 5 s (a) enumerates running processes and raises PROCESS_DETECTED (CONTRACT §9.5) for
/// known remote-control / screen-capture / accessibility-bypass tools, and (b) enumerates visible
/// TOPMOST top-level windows of other processes (W-13b) — an overlay drawn above the exam window
/// is how cheat-sheets and remote viewers usually appear — raising PROCESS_DETECTED with
/// signal "topmost-window".
///
/// Both are WEAK signals, detection only: process names can be renamed and a topmost window can
/// belong to a legitimate tool (IME candidate list, screen-reader UI, toast notifications). Known
/// shell/system windows are ignored by class and process name; everything else is reported once
/// per process name per 60 s for a human to judge. This client never kills processes or windows.
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
        // Accessibility / system UI that can be used to escape a kiosk (W-20); flag only, an
        // accommodation may legitimately need them.
        ["osk"] = "flag",
        ["tabtip"] = "flag",
        ["magnify"] = "flag",
        ["narrator"] = "flag",
        ["taskmgr"] = "flag",
    };

    /// <summary>Processes whose topmost windows are normal on every desktop.</summary>
    private static readonly HashSet<string> IgnoredTopmostProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "shellexperiencehost", "startmenuexperiencehost", "searchhost", "searchapp", "searchui",
        "textinputhost", "ctfmon", "dwm", "sihost", "runtimebroker", "applicationframehost", "lockapp",
        "securityhealthsystray", "systemsettings", "shellhost", "wininit", "winlogon", "csrss", "fontdrvhost",
        "chsime", "imebroker", "msedgewebview2", "inputapp", "windowsinternal.composableshell.experiences.textinput.inputapp",
        "microsoft.notes", "ms-teams", "wsaclient", "nvcontainer", "audiodg",
    };

    /// <summary>Window classes that are topmost by design (shell, IME, tooltips, toasts).</summary>
    private static readonly HashSet<string> IgnoredTopmostClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "tooltips_class32", "Windows.UI.Core.CoreWindow",
        "Xaml_WindowedPopupClass", "ForegroundStaging", "MultitaskingViewFrame", "TaskListThumbnailWnd", "IME",
        "MSCTFIME UI", "Windows.Internal.Shell.TabProxyWindow", "ApplicationManager_DesktopShellWindow",
        "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow", "SysShadow", "DummyDWMListenerWindow",
        "EdgeUiInputTopWndClass", "EdgeUiInputWndClass", "ImmersiveLauncher", "NativeHWNDHost", "CEF-OSC-WIDGET",
    };

    private readonly object _gate = new object();
    private readonly Dictionary<string, DateTime> _lastReported = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private readonly uint _ourPid = (uint)Environment.ProcessId;
    private CancellationTokenSource? _cts;
    private Dispatcher? _dispatcher;

    /// <summary>(processName, action, signal) on the UI thread; already throttled per process name. signal = "process-name" | "topmost-window".</summary>
    public Action<string, string, string>? OnDetected { get; set; }

    public bool IsRunning => _cts != null;

    public void Start(Dispatcher dispatcher)
    {
        Stop();
        _dispatcher = dispatcher;
        var cts = new CancellationTokenSource();
        _cts = cts;
        lock (_gate) { _lastReported.Clear(); }
        _ = Task.Run(() => LoopAsync(cts.Token));
        Log.Info(LogCat, $"Process monitor started (every {Constants.ProcessScanIntervalSeconds}s, {Blacklist.Count} names, topmost-window scan)");
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
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(Constants.ProcessScanIntervalSeconds, 1, 300)));
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
        foreach (var name in found)
        {
            if (ct.IsCancellationRequested) return;
            Report(name, Blacklist.TryGetValue(name, out var a) ? a : "flag", "process-name");
        }

        var topmost = ScanTopmostWindows();
        foreach (var name in topmost)
        {
            if (ct.IsCancellationRequested) return;
            Report(name, "flag", "topmost-window");
        }
    }

    private void Report(string name, string action, string signal)
    {
        var now = DateTime.UtcNow;
        var key = signal + ":" + name;
        bool report;
        lock (_gate)
        {
            report = !_lastReported.TryGetValue(key, out var last) ||
                     (now - last).TotalSeconds >= Constants.ProcessDetectedThrottleSeconds;
            if (report) _lastReported[key] = now;
        }
        if (!report) return;
        Log.Warn(LogCat, $"Detected: {name} (signal={signal}, action={action})");
        var handler = OnDetected;
        var dispatcher = _dispatcher;
        if (handler != null && dispatcher != null)
        {
            dispatcher.BeginInvoke(new Action(() => handler(name, action, signal)), DispatcherPriority.Background);
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

    /// <summary>
    /// W-13b: distinct process names owning a visible, non-trivial (&gt; 50×50 px), WS_EX_TOPMOST
    /// top-level window that is not ours and not a known shell/IME/toast class.
    /// </summary>
    public List<string> ScanTopmostWindows()
    {
        var result = new List<string>();
        var pids = new HashSet<uint>();
        try
        {
            NativeMethods.EnumWindowsProc callback = (hwnd, _) =>
            {
                try
                {
                    if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                    var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                    if ((exStyle & NativeMethods.WS_EX_TOPMOST) == 0) return true;
                    if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true; // tool windows: palettes, our covers
                    NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                    if (pid == 0 || pid == _ourPid) return true;
                    if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return true;
                    if (rect.Width < 50 || rect.Height < 50) return true;
                    var cls = NativeMethods.ClassNameOf(hwnd);
                    if (IgnoredTopmostClasses.Contains(cls)) return true;
                    pids.Add(pid);
                }
                catch
                {
                    // ignore this window
                }
                return true;
            };
            NativeMethods.EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "EnumWindows failed: " + ex.Message);
            return result;
        }

        foreach (var pid in pids)
        {
            string name;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                name = p.ProcessName;
            }
            catch
            {
                continue;
            }
            if (string.IsNullOrEmpty(name) || IgnoredTopmostProcesses.Contains(name)) continue;
            if (!result.Contains(name)) result.Add(name);
        }
        return result;
    }

    public static string ActionFor(string processName) =>
        Blacklist.TryGetValue(processName, out var a) ? a : "flag";
}
