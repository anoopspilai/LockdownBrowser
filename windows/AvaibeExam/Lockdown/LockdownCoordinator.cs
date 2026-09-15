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
///   * reads the ADVISORY HKLM kiosk hint -> mode "assigned-access" (client-reported);
///   * otherwise mode "kiosk-fallback" — refused when policy.requireAAC (LOCKDOWN_FAILED);
///   * KioskFallback controls + ProcessMonitor are applied in BOTH modes.
/// Release(ReleaseAuthority): the ONLY way to reverse the controls; the authority type can only be
/// built from a verified command, the offline failsafe or a start backout (W-03).
/// Emits LOCKDOWN_ENGAGED / LOCKDOWN_FALLBACK / LOCKDOWN_FAILED and forwards the kiosk callbacks as
/// BLOCKED_SHORTCUT / APP_DEACTIVATED / WINDOW_RESIZED / SESSION_END_BLOCKED /
/// SCREEN_CAPTURE_PROTECTION_UNAVAILABLE / CAPTURE_PROTECTION_LOST / LOCKDOWN_INTERRUPTED /
/// PROCESS_DETECTED telemetry.
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

        Kiosk.OnBlockedShortcut = (name, injected) =>
            _events.Record(EventType.BlockedShortcut, injected ? EventSeverity.High : EventSeverity.Medium,
                new Dictionary<string, object?> { ["shortcut"] = name, ["injected"] = injected });

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

        Kiosk.OnCaptureProtectionLost = reapplied =>
            _events.RecordNow(EventType.CaptureProtectionLost, EventSeverity.High,
                new Dictionary<string, object?> { ["reapplied"] = reapplied, ["level"] = Kiosk.CaptureProtectionLevel });

        Kiosk.OnKeyboardHookLost = reinstalled =>
            _events.RecordNow(EventType.LockdownInterrupted, EventSeverity.High,
                new Dictionary<string, object?> { ["component"] = "keyboard-hook", ["reinstalled"] = reinstalled });

        Kiosk.OnDisplayChange = () => OnDisplayChange?.Invoke();

        Processes.OnDetected = (process, action, signal) =>
        {
            _events.RecordNow(EventType.ProcessDetected, EventSeverity.Medium,
                new Dictionary<string, object?> { ["process"] = process, ["action"] = action, ["signal"] = signal });
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

        if (policy.RequireAAC && !aa.HklmSignal)
        {
            Mode = LockdownMode.None;
            _events.RecordNow(EventType.LockdownFailed, EventSeverity.High,
                Meta("reason", "requireAAC and no HKLM kiosk configuration (client-reported): " + aa.Detail));
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
            _events.RecordNow(EventType.LockdownFailed, EventSeverity.High, Meta("reason", "kiosk engage failed: " + ex.GetType().Name));
            return LockdownMode.None;
        }

        Processes.Start(_dispatcher);

        if (aa.HklmSignal)
        {
            Mode = LockdownMode.AssignedAccess;
            _events.RecordNow(EventType.LockdownEngaged, EventSeverity.Info, new Dictionary<string, object?>
            {
                ["mode"] = Mode.Wire(),
                ["detail"] = aa.Detail,
                ["assignedAccessHint"] = true,
                ["captureProtection"] = Kiosk.CaptureProtectionLevel,
            });
            Log.Info(LogCat, "Lockdown engaged: assigned-access hint (" + aa.Detail + ")");
        }
        else
        {
            Mode = LockdownMode.KioskFallback;
            _events.RecordNow(EventType.LockdownFallback, EventSeverity.Medium, new Dictionary<string, object?>
            {
                ["mode"] = Mode.Wire(),
                ["reason"] = aa.Detail,
                ["assignedAccessHint"] = false,
                ["captureProtection"] = Kiosk.CaptureProtectionLevel,
            });
            Log.Warn(LogCat, "Lockdown engaged: kiosk-fallback (" + aa.Detail + ")");
        }
        return Mode;
    }

    /// <summary>
    /// Reverses every kiosk control. Idempotent. Never throws: each step is guarded so a failure
    /// in one control cannot leave the others engaged (W-09).
    /// </summary>
    public void Release(ReleaseAuthority authority)
    {
        if (authority == null) throw new ArgumentNullException(nameof(authority));
        if (Mode == LockdownMode.None && !Kiosk.IsEngaged && !Processes.IsRunning) return;
        Log.Info(LogCat, "Releasing lockdown (authority=" + authority.Wire() + ": " + authority.Detail + ")");
        try
        {
            Processes.Stop();
        }
        catch (Exception ex)
        {
            Log.Error(LogCat, "Process monitor stop failed", ex);
        }
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
