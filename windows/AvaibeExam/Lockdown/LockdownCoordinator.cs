using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using AvaibeExam.Models;
using AvaibeExam.Networking;
using AvaibeExam.Util;

namespace AvaibeExam.Lockdown;

/// <summary>
/// Single entry point for engaging and releasing lockdown (mirrors macOS LockdownCoordinator).
///
/// Engage(policy, window):
///   * detects an Assigned Access / Shell Launcher session -> mode "assigned-access";
///   * otherwise mode "kiosk-fallback" — refused when policy.requireAAC (LOCKDOWN_FAILED);
///   * KioskFallback controls + ProcessMonitor are applied in BOTH modes.
/// Emits LOCKDOWN_ENGAGED / LOCKDOWN_FALLBACK / LOCKDOWN_FAILED and forwards the kiosk
/// callbacks as BLOCKED_SHORTCUT / APP_DEACTIVATED / WINDOW_RESIZED / SESSION_END_BLOCKED /
/// SCREEN_CAPTURE_PROTECTION_UNAVAILABLE / PROCESS_DETECTED telemetry.
/// </summary>
public sealed class LockdownCoordinator
{
    private const string LogCat = "lockdown";

    private readonly EventReporter _events;
    private readonly Dispatcher _dispatcher;

    public KioskFallback Kiosk { get; } = new KioskFallback();
    public ProcessMonitor Processes { get; } = new ProcessMonitor();

    public LockdownMode Mode { get; private set; } = LockdownMode.None;
    public string AssignedAccessDetail { get; private set; } = string.Empty;

    /// <summary>Show a warning overlay + WARNING bridge message (UI thread).</summary>
    public Action<string>? OnWarning { get; set; }
    /// <summary>Display topology changed while locked (UI thread).</summary>
    public Action? OnDisplayChange { get; set; }

    public bool IsEngaged => Mode != LockdownMode.None;

    public LockdownCoordinator(EventReporter events, Dispatcher dispatcher)
    {
        _events = events;
        _dispatcher = dispatcher;

        Kiosk.OnBlockedShortcut = name =>
            _events.Record(EventType.BlockedShortcut, EventSeverity.Medium, Meta("shortcut", name));

        Kiosk.OnDeactivated = process =>
        {
            _events.Record(EventType.AppDeactivated, EventSeverity.High, Meta("frontmostApp", process));
            OnWarning?.Invoke("Stay in the exam window. Leaving the exam app has been reported.");
        };

        Kiosk.OnWindowResized = (w, h) =>
            _events.Record(EventType.WindowResized, EventSeverity.Low, new Dictionary<string, object?> { ["width"] = w, ["height"] = h });

        Kiosk.OnSessionEndBlocked = () =>
            _events.RecordNow(EventType.SessionEndBlocked, EventSeverity.Medium, Meta("action", "logoff-or-shutdown"));

        Kiosk.OnCaptureProtectionUnavailable = reason =>
            _events.Record(EventType.ScreenCaptureProtectionUnavailable, EventSeverity.Low, Meta("reason", reason));

        Kiosk.OnDisplayChange = () => OnDisplayChange?.Invoke();

        Processes.OnDetected = (process, action) =>
        {
            _events.RecordNow(EventType.ProcessDetected, EventSeverity.Medium,
                new Dictionary<string, object?> { ["process"] = process, ["action"] = action });
            if (action == "warn")
            {
                OnWarning?.Invoke($"A remote-control or screen-capture program ({process}) is running. Close it now — this has been reported.");
            }
        };
    }

    private static Dictionary<string, object?> Meta(string key, object? value) =>
        new Dictionary<string, object?> { [key] = value };

    /// <summary>Engages lockdown and returns the resulting mode (None if refused/failed).</summary>
    public LockdownMode Engage(Policy policy, Window window)
    {
        var aa = AssignedAccessDetector.Detect();
        AssignedAccessDetail = aa.Detail;

        if (policy.RequireAAC && !aa.IsAssignedAccess)
        {
            Mode = LockdownMode.None;
            _events.RecordNow(EventType.LockdownFailed, EventSeverity.High,
                Meta("reason", "requireAAC (Assigned Access) and not in a kiosk session: " + aa.Detail));
            Log.Error(LogCat, "Lockdown refused: policy requires Assigned Access (" + aa.Detail + ")");
            return LockdownMode.None;
        }

        try
        {
            Kiosk.Engage(window, policy);
        }
        catch (Exception ex)
        {
            Log.Error(LogCat, "KioskFallback.Engage failed", ex);
            try { Kiosk.Disengage(); } catch { /* ignore */ }
            Mode = LockdownMode.None;
            _events.RecordNow(EventType.LockdownFailed, EventSeverity.High, Meta("reason", "kiosk engage failed: " + ex.Message));
            return LockdownMode.None;
        }

        Processes.Start(_dispatcher);

        if (aa.IsAssignedAccess)
        {
            Mode = LockdownMode.AssignedAccess;
            _events.RecordNow(EventType.LockdownEngaged, EventSeverity.Info, new Dictionary<string, object?>
            {
                ["mode"] = Mode.Wire(),
                ["detail"] = aa.Detail,
                ["captureProtection"] = Kiosk.CaptureProtectionLevel,
            });
            Log.Info(LogCat, "Lockdown engaged: assigned-access (" + aa.Detail + ")");
        }
        else
        {
            Mode = LockdownMode.KioskFallback;
            _events.RecordNow(EventType.LockdownFallback, EventSeverity.Medium, new Dictionary<string, object?>
            {
                ["mode"] = Mode.Wire(),
                ["reason"] = aa.Detail,
                ["captureProtection"] = Kiosk.CaptureProtectionLevel,
            });
            Log.Warn(LogCat, "Lockdown engaged: kiosk-fallback (" + aa.Detail + ")");
        }
        return Mode;
    }

    /// <summary>Reverses every kiosk control.</summary>
    public void Release(string? authorizationId)
    {
        if (Mode == LockdownMode.None) return;
        Log.Info(LogCat, "Releasing lockdown (authorization=" + (authorizationId ?? "none") + ")");
        Processes.Stop();
        try
        {
            Kiosk.Disengage();
        }
        catch (Exception ex)
        {
            Log.Error(LogCat, "Kiosk disengage failed", ex);
        }
        Mode = LockdownMode.None;
    }

    public void HandleScreensChanged()
    {
        Kiosk.UpdateCoverWindows();
        Kiosk.ReassertWindow();
    }
}
