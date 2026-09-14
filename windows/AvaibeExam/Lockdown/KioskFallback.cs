using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AvaibeExam.Core;
using AvaibeExam.Models;
using AvaibeExam.Util;
using WinForms = System.Windows.Forms;

namespace AvaibeExam.Lockdown;

/// <summary>
/// Best-effort lockdown controls per CONTRACT §9.3 (applied in BOTH assigned-access and
/// kiosk-fallback modes):
///   * exam window: borderless, maximized (covers the taskbar), Topmost, not resizable/minimizable;
///   * black cover windows (with a label) on every non-primary monitor when policy.blockExternalDisplay;
///   * WH_KEYBOARD_LL hook (KeyboardHook) swallowing dangerous shortcuts -> BLOCKED_SHORTCUT;
///   * SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE), falling back to WDA_MONITOR, else
///     SCREEN_CAPTURE_PROTECTION_UNAVAILABLE;
///   * focus watchdog (500 ms): re-activates the window when another process is in the
///     foreground and raises APP_DEACTIVATED (once per 2 s) with the foreground process name;
///   * clipboard cleared on engage and on focus return;
///   * WM_QUERYENDSESSION refused (ShutdownBlockReasonCreate) -> SESSION_END_BLOCKED;
///   * WM_SYSCOMMAND close/minimize/move/size refused.
///
/// What it CANNOT guarantee (reported honestly as kiosk-fallback): Ctrl+Alt+Del, the secure
/// desktop / UAC, Win+L on some builds, Task Manager once reached via Ctrl+Alt+Del, hardware
/// capture, virtual machines, remote control that started before the exam (detected, not
/// prevented). Only a real Assigned Access kiosk account makes the OS itself enforce these.
/// </summary>
public sealed class KioskFallback
{
    private const string LogCat = "lockdown";

    private sealed class SavedWindowState
    {
        public WindowStyle WindowStyle;
        public ResizeMode ResizeMode;
        public WindowState WindowState;
        public bool Topmost;
        public bool ShowInTaskbar;
        public double Left, Top, Width, Height;
    }

    private Window? _window;
    private IntPtr _hwnd = IntPtr.Zero;
    private HwndSource? _source;
    private HwndSourceHook? _wndProcHook;
    private Policy _policy = Policy.ConservativeDefault;
    private SavedWindowState? _saved;
    private KeyboardHook? _hook;
    private DispatcherTimer? _watchdog;
    private readonly List<Window> _covers = new List<Window>();
    private readonly uint _ourPid = (uint)Environment.ProcessId;
    private DateTime _lastDeactivatedReport = DateTime.MinValue;
    private DateTime _lastSessionEndReport = DateTime.MinValue;
    private DateTime _lastResizeReport = DateTime.MinValue;
    private DateTime _lastHookRefresh = DateTime.MinValue;
    private bool _shutdownBlockActive;
    private bool _reassertingState;

    public bool IsEngaged { get; private set; }

    /// <summary>true when WDA_EXCLUDEFROMCAPTURE or WDA_MONITOR was accepted by the OS.</summary>
    public bool CaptureProtectionActive { get; private set; }
    /// <summary>"exclude-from-capture", "monitor", or "none".</summary>
    public string CaptureProtectionLevel { get; private set; } = "none";

    /// <summary>(shortcut name) — throttled.</summary>
    public Action<string>? OnBlockedShortcut { get; set; }
    /// <summary>(foreground process name) — throttled to once per 2 s.</summary>
    public Action<string>? OnDeactivated { get; set; }
    /// <summary>(width, height) in physical pixels — throttled.</summary>
    public Action<int, int>? OnWindowResized { get; set; }
    /// <summary>The user tried to log off / shut down while locked.</summary>
    public Action? OnSessionEndBlocked { get; set; }
    /// <summary>Display topology changed (WM_DISPLAYCHANGE); covers were already refreshed.</summary>
    public Action? OnDisplayChange { get; set; }
    /// <summary>Screen capture protection could not be enabled at all.</summary>
    public Action<string>? OnCaptureProtectionUnavailable { get; set; }

    // ---- Engage / disengage ----------------------------------------------------------

    public void Engage(Window window, Policy policy)
    {
        if (IsEngaged) return;
        _window = window;
        _policy = policy;
        IsEngaged = true;
        Log.Info(LogCat, $"KioskFallback engaging (blockExternalDisplay={policy.BlockExternalDisplay}, clipboard={policy.AllowClipboard}, printing={policy.AllowPrinting})");

        _hwnd = new WindowInteropHelper(window).EnsureHandle();

        _saved = new SavedWindowState
        {
            WindowStyle = window.WindowStyle,
            ResizeMode = window.ResizeMode,
            WindowState = window.WindowState,
            Topmost = window.Topmost,
            ShowInTaskbar = window.ShowInTaskbar,
            Left = window.Left,
            Top = window.Top,
            Width = window.Width,
            Height = window.Height,
        };

        PresentFullscreen(window);
        ApplyCaptureProtection();
        InstallKeyboardHook();
        _lastHookRefresh = DateTime.UtcNow;
        InstallWndProcHook();
        BlockSessionEnd();
        UpdateCoverWindows();
        ClearClipboard();
        StartWatchdog();

        ForceForeground();
    }

    public void Disengage()
    {
        if (!IsEngaged) return;
        IsEngaged = false;
        Log.Info(LogCat, "KioskFallback disengaging");

        StopWatchdog();
        CloseCoverWindows();

        if (_hook != null)
        {
            _hook.Dispose();
            _hook = null;
        }

        UnblockSessionEnd();
        RemoveWndProcHook();

        if (_hwnd != IntPtr.Zero)
        {
            try
            {
                NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_NONE);
            }
            catch
            {
                // ignore
            }
        }
        CaptureProtectionActive = false;
        CaptureProtectionLevel = "none";

        var window = _window;
        var saved = _saved;
        if (window != null && saved != null)
        {
            try
            {
                window.Topmost = saved.Topmost;
                window.WindowState = WindowState.Normal;
                window.WindowStyle = saved.WindowStyle;
                window.ResizeMode = saved.ResizeMode;
                window.ShowInTaskbar = saved.ShowInTaskbar;
                window.Left = saved.Left;
                window.Top = saved.Top;
                window.Width = saved.Width;
                window.Height = saved.Height;
                window.WindowState = saved.WindowState == WindowState.Minimized ? WindowState.Normal : saved.WindowState;
                window.Activate();
            }
            catch (Exception ex)
            {
                Log.Warn(LogCat, "Restoring window state failed: " + ex.Message);
            }
        }
        _saved = null;
        _window = null;
        _hwnd = IntPtr.Zero;
    }

    // ---- Window presentation ---------------------------------------------------------

    private void PresentFullscreen(Window window)
    {
        _reassertingState = true;
        try
        {
            // Style first, then maximize: a borderless maximized WPF window covers the whole
            // primary monitor including the taskbar.
            window.WindowState = WindowState.Normal;
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.ShowInTaskbar = true;
            window.Topmost = true;
            window.WindowState = WindowState.Maximized;
        }
        finally
        {
            _reassertingState = false;
        }
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
        }
    }

    /// <summary>Called by the watchdog and by MainWindow.StateChanged.</summary>
    public void ReassertWindow()
    {
        if (!IsEngaged || _window == null || _reassertingState) return;
        var window = _window;
        _reassertingState = true;
        try
        {
            if (window.WindowState != WindowState.Maximized)
            {
                Log.Warn(LogCat, "Window left the maximized state (" + window.WindowState + "); re-asserting");
                ReportResize();
                window.WindowState = WindowState.Maximized;
            }
            if (!window.Topmost) window.Topmost = true;
            if (window.WindowStyle != WindowStyle.None) window.WindowStyle = WindowStyle.None;
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "ReassertWindow failed: " + ex.Message);
        }
        finally
        {
            _reassertingState = false;
        }
    }

    private void ReportResize()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastResizeReport).TotalSeconds < 1) return;
        _lastResizeReport = now;
        if (_hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            OnWindowResized?.Invoke(rect.Width, rect.Height);
        }
    }

    // ---- Screen capture protection ---------------------------------------------------

    /// <summary>
    /// Applies WDA_EXCLUDEFROMCAPTURE (Windows 10 2004+), falling back to WDA_MONITOR. Can be
    /// called again after the WebView2 child window exists (see ExamWebView) — the affinity is a
    /// property of the top-level window and is re-applied idempotently.
    /// </summary>
    public void ApplyCaptureProtection()
    {
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            if (NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE))
            {
                CaptureProtectionActive = true;
                CaptureProtectionLevel = "exclude-from-capture";
                Log.Info(LogCat, "Screen capture protection: WDA_EXCLUDEFROMCAPTURE");
                return;
            }
            var err1 = Marshal.GetLastWin32Error();
            if (NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_MONITOR))
            {
                CaptureProtectionActive = true;
                CaptureProtectionLevel = "monitor";
                Log.Warn(LogCat, $"WDA_EXCLUDEFROMCAPTURE failed ({err1}); using WDA_MONITOR");
                return;
            }
            var err2 = Marshal.GetLastWin32Error();
            CaptureProtectionActive = false;
            CaptureProtectionLevel = "none";
            var reason = $"SetWindowDisplayAffinity failed (exclude={err1}, monitor={err2})";
            Log.Warn(LogCat, "Screen capture protection unavailable: " + reason);
            OnCaptureProtectionUnavailable?.Invoke(reason);
        }
        catch (Exception ex)
        {
            CaptureProtectionActive = false;
            CaptureProtectionLevel = "none";
            Log.Warn(LogCat, "SetWindowDisplayAffinity threw: " + ex.Message);
            OnCaptureProtectionUnavailable?.Invoke(ex.Message);
        }
    }

    // ---- Keyboard hook ---------------------------------------------------------------

    private void InstallKeyboardHook()
    {
        _hook = new KeyboardHook
        {
            AllowClipboard = _policy.AllowClipboard,
            AllowPrinting = _policy.AllowPrinting,
            OnBlocked = name => OnBlockedShortcut?.Invoke(name),
        };
        if (!_hook.Install())
        {
            Log.Error(LogCat, "Keyboard hook could not be installed; shortcuts are NOT blocked");
        }
    }

    // ---- WndProc: session end, sys commands, display change --------------------------

    private void InstallWndProcHook()
    {
        if (_hwnd == IntPtr.Zero) return;
        _source = HwndSource.FromHwnd(_hwnd);
        if (_source == null)
        {
            Log.Warn(LogCat, "HwndSource unavailable; WM_QUERYENDSESSION will not be intercepted");
            return;
        }
        _wndProcHook = WndProc;
        _source.AddHook(_wndProcHook);
    }

    private void RemoveWndProcHook()
    {
        if (_source != null && _wndProcHook != null)
        {
            try { _source.RemoveHook(_wndProcHook); } catch { /* ignore */ }
        }
        _source = null;
        _wndProcHook = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!IsEngaged) return IntPtr.Zero;
        switch (msg)
        {
            case NativeMethods.WM_QUERYENDSESSION:
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastSessionEndReport).TotalSeconds >= 2)
                    {
                        _lastSessionEndReport = now;
                        Log.Warn("security", "Log off / shutdown attempt blocked while locked");
                        OnSessionEndBlocked?.Invoke();
                    }
                    handled = true;
                    return IntPtr.Zero; // FALSE = refuse to end the session
                }
            case NativeMethods.WM_ENDSESSION:
                // If Windows ends the session anyway (forced), there is nothing we can do.
                if (wParam != IntPtr.Zero) Log.Error("security", "Session is ending despite the block");
                break;
            case NativeMethods.WM_SYSCOMMAND:
                {
                    var command = (int)(wParam.ToInt64() & 0xFFF0);
                    if (command == NativeMethods.SC_CLOSE || command == NativeMethods.SC_MINIMIZE ||
                        command == NativeMethods.SC_MOVE || command == NativeMethods.SC_SIZE ||
                        command == NativeMethods.SC_KEYMENU || command == NativeMethods.SC_MONITORPOWER)
                    {
                        Log.Warn("security", "System command blocked: 0x" + command.ToString("X4"));
                        if (command == NativeMethods.SC_CLOSE || command == NativeMethods.SC_MINIMIZE)
                        {
                            OnBlockedShortcut?.Invoke(command == NativeMethods.SC_CLOSE ? "SysMenu+Close" : "SysMenu+Minimize");
                        }
                        handled = true;
                        return IntPtr.Zero;
                    }
                    break;
                }
            case NativeMethods.WM_DISPLAYCHANGE:
                // Let the monitor list settle, then re-cover and re-assert.
                _window?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!IsEngaged) return;
                    UpdateCoverWindows();
                    ReassertWindow();
                    OnDisplayChange?.Invoke();
                }), DispatcherPriority.Background);
                break;
        }
        return IntPtr.Zero;
    }

    private void BlockSessionEnd()
    {
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            _shutdownBlockActive = NativeMethods.ShutdownBlockReasonCreate(_hwnd, "An exam is in progress. Ask your teacher to release the exam first.");
            if (!_shutdownBlockActive)
            {
                Log.Warn(LogCat, "ShutdownBlockReasonCreate failed, error " + Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "ShutdownBlockReasonCreate threw: " + ex.Message);
        }
    }

    private void UnblockSessionEnd()
    {
        if (!_shutdownBlockActive || _hwnd == IntPtr.Zero) return;
        try
        {
            NativeMethods.ShutdownBlockReasonDestroy(_hwnd);
        }
        catch
        {
            // ignore
        }
        _shutdownBlockActive = false;
    }

    // ---- Cover windows for secondary monitors ----------------------------------------

    /// <summary>Re-covers every non-primary monitor (no-op unless policy.blockExternalDisplay).</summary>
    public void UpdateCoverWindows()
    {
        if (!IsEngaged) return;
        CloseCoverWindows();
        if (!_policy.BlockExternalDisplay) return;

        WinForms.Screen[] screens;
        try
        {
            screens = WinForms.Screen.AllScreens;
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "Screen enumeration failed: " + ex.Message);
            return;
        }

        foreach (var screen in screens)
        {
            if (screen.Primary) continue;
            try
            {
                var cover = CreateCoverWindow();
                cover.Show();
                var hwnd = new WindowInteropHelper(cover).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    // Hide from Alt+Tab and never take focus from the exam window.
                    var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                    ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
                    NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
                    // Bounds are physical pixels for that monitor (PerMonitorV2), which is what
                    // SetWindowPos expects — no DPI conversion needed.
                    var b = screen.Bounds;
                    NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height,
                        NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
                }
                _covers.Add(cover);
            }
            catch (Exception ex)
            {
                Log.Warn(LogCat, "Could not cover display " + screen.DeviceName + ": " + ex.Message);
            }
        }
        if (_covers.Count > 0)
        {
            Log.Info(LogCat, $"Covered {_covers.Count} secondary display(s)");
        }
    }

    private Window CreateCoverWindow()
    {
        var label = new TextBlock
        {
            Text = "This display is blocked during the exam.\nDisconnect it to continue.",
            Foreground = Brushes.White,
            FontSize = 24,
            FontWeight = FontWeights.Medium,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var cover = new Window
        {
            Title = "Avaibe Exam — display blocked",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.Black,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = 400,
            Height = 300,
            Content = new Grid { Background = Brushes.Black, Children = { label } },
        };
        cover.Closing += CoverClosing;
        return cover;
    }

    private void CoverClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Covers may only be closed by Disengage.
        if (IsEngaged) e.Cancel = true;
    }

    private void CloseCoverWindows()
    {
        foreach (var cover in _covers)
        {
            try
            {
                cover.Closing -= CoverClosing;
                cover.Close();
            }
            catch
            {
                // ignore
            }
        }
        _covers.Clear();
    }

    // ---- Focus watchdog --------------------------------------------------------------

    private void StartWatchdog()
    {
        StopWatchdog();
        _watchdog = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(Constants.FocusWatchdogIntervalMs),
        };
        _watchdog.Tick += WatchdogTick;
        _watchdog.Start();
    }

    private void StopWatchdog()
    {
        if (_watchdog == null) return;
        _watchdog.Stop();
        _watchdog.Tick -= WatchdogTick;
        _watchdog = null;
    }

    private void WatchdogTick(object? sender, EventArgs e)
    {
        if (!IsEngaged || _hwnd == IntPtr.Zero) return;
        try
        {
            var fg = NativeMethods.GetForegroundWindow();
            if (fg != _hwnd)
            {
                var ours = false;
                var processName = "unknown";
                if (fg != IntPtr.Zero)
                {
                    NativeMethods.GetWindowThreadProcessId(fg, out var pid);
                    if (pid == _ourPid)
                    {
                        ours = true; // cover window, script dialog, message box: fine
                    }
                    else
                    {
                        processName = ProcessNameOf(pid);
                    }
                }
                if (!ours)
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastDeactivatedReport).TotalSeconds >= Constants.DeactivatedThrottleSeconds)
                    {
                        _lastDeactivatedReport = now;
                        Log.Warn("security", $"Exam window lost foreground to '{processName}'; re-activating");
                        OnDeactivated?.Invoke(processName);
                    }
                    ForceForeground();
                    ClearClipboard();
                }
            }
            ReassertWindow();

            // Heal a silently-removed low-level hook (see KeyboardHook.Reinstall).
            var nowUtc = DateTime.UtcNow;
            if (_hook != null && (nowUtc - _lastHookRefresh).TotalSeconds >= 30)
            {
                _lastHookRefresh = nowUtc;
                _hook.Reinstall();
            }
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "Watchdog tick failed: " + ex.Message);
        }
    }

    private static string ProcessNameOf(uint pid)
    {
        if (pid == 0) return "unknown";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "pid:" + pid;
        }
    }

    /// <summary>Best-effort SetForegroundWindow (Windows deliberately makes this hard).</summary>
    private void ForceForeground()
    {
        if (_hwnd == IntPtr.Zero || _window == null) return;
        try
        {
            if (NativeMethods.IsIconic(_hwnd))
            {
                NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_RESTORE);
            }
            var fg = NativeMethods.GetForegroundWindow();
            var ourThread = NativeMethods.GetCurrentThreadId();
            var fgThread = fg != IntPtr.Zero ? NativeMethods.GetWindowThreadProcessId(fg, out _) : 0u;
            var attached = false;
            if (fg != IntPtr.Zero && fgThread != 0 && fgThread != ourThread && !NativeMethods.IsHungAppWindow(fg))
            {
                attached = NativeMethods.AttachThreadInput(ourThread, fgThread, true);
            }
            try
            {
                NativeMethods.BringWindowToTop(_hwnd);
                NativeMethods.SetForegroundWindow(_hwnd);
            }
            finally
            {
                if (attached) NativeMethods.AttachThreadInput(ourThread, fgThread, false);
            }
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
            _window.Activate();
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "ForceForeground failed: " + ex.Message);
        }
    }

    // ---- Clipboard -------------------------------------------------------------------

    public void ClearClipboard()
    {
        if (_policy.AllowClipboard) return;
        try
        {
            System.Windows.Clipboard.Clear();
        }
        catch (Exception ex)
        {
            // Another process may hold the clipboard open; harmless.
            Log.Debug(LogCat, "Clipboard.Clear failed: " + ex.Message);
        }
    }
}
