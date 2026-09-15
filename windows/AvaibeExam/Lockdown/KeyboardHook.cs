using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using AvaibeExam.Core;
using AvaibeExam.Util;

namespace AvaibeExam.Lockdown;

/// <summary>
/// WH_KEYBOARD_LL hook (CONTRACT §9.3). Installed on the UI thread (a low-level hook needs a
/// thread with a message loop; the callback runs on that thread). Swallows:
///   Alt+Tab, Alt+Shift+Tab, Alt+Esc, Alt+F4, Alt+Space, Ctrl+Esc, Ctrl+Shift+Esc, Ctrl+Alt+Tab,
///   the Windows keys themselves (so Win+D/G/R/L/Shift+S/... never reach the shell),
///   PrintScreen / Alt+PrintScreen, F11, F12, the Apps (menu) key, Sleep, browser/launch keys,
///   Ctrl+N/T/W/O/S/U/H/J, Ctrl+Shift+I/J/C/N, Ctrl+F4, the fifth Shift press within 5 s
///   (Sticky Keys prompt, W-20), every INJECTED key event except our own liveness probe (W-31),
///   and — policy dependent — Ctrl+P (printing), Ctrl+C/X/V, Ctrl+Insert, Shift+Insert,
///   Shift+Delete (clipboard).
///
/// Design (W-10): the hook callback ONLY classifies and enqueues. Windows silently removes a
/// low-level hook whose callback exceeds LowLevelHooksTimeout, so nothing slow (logging, event
/// recording, handler invocation) runs inside it. A DispatcherTimer on the UI thread drains the
/// queue every 100 ms and raises <see cref="OnBlocked"/> (throttled per name).
///
/// Liveness (W-19): every 5 s <see cref="Probe"/> injects a key-up of a reserved virtual key
/// tagged with a marker in dwExtraInfo; if the callback has not seen it by the next watchdog
/// check the hook was dropped by Windows — it is reinstalled and <see cref="OnHookLost"/> fires.
///
/// What CANNOT be blocked from user mode (honestly logged, see README):
///   * Ctrl+Alt+Del (secure attention sequence) is handled by winlogon before any hook.
///   * Win+L: Windows processes the lock combination before low-level hooks see it on most
///     builds. Swallowing the Win key makes it *usually* not fire, but the only reliable block
///     is the DisableLockWorkstation registry policy, which this client deliberately does NOT set.
///   * Secure desktop / UAC prompts, hardware capture devices, second keyboards on other sessions.
///   * Swallowing injected input also blocks assistive tools that use SendInput (documented limit).
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const string LogCat = "keyhook";
    /// <summary>"AVBI" — dwExtraInfo tag of our own probe events.</summary>
    private static readonly IntPtr ProbeMarker = new IntPtr(0x41564249);
    private const int StickyKeysPresses = 5;
    private const double StickyKeysWindowSeconds = 5.0;

    private sealed class Blocked
    {
        public Blocked(string name, bool injected)
        {
            Name = name;
            Injected = injected;
        }

        public string Name { get; }
        public bool Injected { get; }
    }

    // Keep the delegate in a field so the GC never collects it while the hook is installed.
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private readonly ConcurrentQueue<Blocked> _pending = new ConcurrentQueue<Blocked>();
    private readonly Dictionary<string, DateTime> _lastBlocked = new Dictionary<string, DateTime>(StringComparer.Ordinal);
    private readonly Queue<DateTime> _shiftPresses = new Queue<DateTime>();
    private IntPtr _hook = IntPtr.Zero;
    private bool _winDown;
    private bool _disposed;
    private DispatcherTimer? _drain;
    private int _probeOutstanding;       // 1 while a probe was sent and not yet seen (Interlocked)
    private DateTime _probeSentAt = DateTime.MinValue;
    private DateTime _lastProbeAt = DateTime.MinValue;
    private long _callbackErrors;

    public bool AllowClipboard { get; set; }
    public bool AllowPrinting { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>(shortcut name, injected) — already throttled to one report per name per 2 s. UI thread.</summary>
    public Action<string, bool>? OnBlocked { get; set; }
    /// <summary>The OS dropped the hook (liveness probe unanswered); it was reinstalled (or not: bool). UI thread.</summary>
    public Action<bool>? OnHookLost { get; set; }

    public bool IsInstalled => _hook != IntPtr.Zero;

    public KeyboardHook()
    {
        _proc = Callback;
    }

    public bool Install()
    {
        if (_hook != IntPtr.Zero) return true;
        _winDown = false;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Error(LogCat, "SetWindowsHookEx failed, error " + Marshal.GetLastWin32Error());
            return false;
        }
        Interlocked.Exchange(ref _probeOutstanding, 0);
        StartDrain();
        Log.Info(LogCat, "Low-level keyboard hook installed");
        return true;
    }

    public void Uninstall()
    {
        StopDrain();
        if (_hook == IntPtr.Zero) return;
        if (!NativeMethods.UnhookWindowsHookEx(_hook))
        {
            Log.Warn(LogCat, "UnhookWindowsHookEx failed, error " + Marshal.GetLastWin32Error());
        }
        _hook = IntPtr.Zero;
        Log.Info(LogCat, "Low-level keyboard hook removed");
    }

    private bool Reinstall()
    {
        if (_disposed) return false;
        Uninstall();
        return Install();
    }

    // ---- Liveness probe (W-19) --------------------------------------------------------

    /// <summary>
    /// Called by the kiosk watchdog every 500 ms. Sends a probe every 5 s and checks whether the
    /// previous one arrived within 1.5 s; when it did not, the hook is gone: reinstall + report.
    /// </summary>
    public void CheckLiveness()
    {
        if (_disposed || _hook == IntPtr.Zero) return;
        var now = DateTime.UtcNow;
        if (Interlocked.CompareExchange(ref _probeOutstanding, 0, 0) == 1 &&
            (now - _probeSentAt).TotalMilliseconds > Constants.HookProbeTimeoutMs)
        {
            Interlocked.Exchange(ref _probeOutstanding, 0);
            Log.Error("security", "Keyboard hook liveness probe unanswered — hook was dropped by Windows; reinstalling");
            var ok = Reinstall();
            try { OnHookLost?.Invoke(ok); } catch (Exception ex) { Log.Error(LogCat, "OnHookLost handler failed", ex); }
            _lastProbeAt = now;
            return;
        }
        if ((now - _lastProbeAt).TotalMilliseconds >= Constants.HookProbeIntervalMs)
        {
            _lastProbeAt = now;
            if (Interlocked.CompareExchange(ref _probeOutstanding, 1, 0) == 0)
            {
                _probeSentAt = now;
                if (!NativeMethods.SendProbeKeyUp(ProbeMarker))
                {
                    // Could not inject (e.g. secure desktop active): do not count as a lost hook.
                    Interlocked.Exchange(ref _probeOutstanding, 0);
                }
            }
        }
    }

    // ---- Hook callback: classify + enqueue ONLY ----------------------------------------

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode < 0 || !Enabled)
            {
                return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            var msg = (int)wParam.ToInt64();
            var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            var vk = (int)info.vkCode;
            var down = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
            var up = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;
            var injected = (info.flags & NativeMethods.LLKHF_INJECTED) != 0;

            // Our own liveness probe: mark seen, swallow (it must never reach the page).
            if (injected && vk == NativeMethods.VK_PROBE && info.dwExtraInfo.ToUInt64() == (ulong)ProbeMarker.ToInt64())
            {
                Interlocked.Exchange(ref _probeOutstanding, 0);
                return new IntPtr(1);
            }

            // W-31: any other injected key event (SendInput / keybd_event from another process).
            if (injected)
            {
                if (down) _pending.Enqueue(new Blocked("Injected+" + KeyName(vk), true));
                return new IntPtr(1);
            }

            // Windows keys: swallow completely so no Win+X combination reaches the shell.
            if (vk == NativeMethods.VK_LWIN || vk == NativeMethods.VK_RWIN)
            {
                if (down) { _winDown = true; _pending.Enqueue(new Blocked("Win", false)); }
                if (up) _winDown = false;
                return new IntPtr(1);
            }

            var alt = (info.flags & NativeMethods.LLKHF_ALTDOWN) != 0 || NativeMethods.IsKeyDown(NativeMethods.VK_MENU);
            var ctrl = NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL);
            var shift = NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT);
            var win = _winDown || NativeMethods.IsKeyDown(NativeMethods.VK_LWIN) || NativeMethods.IsKeyDown(NativeMethods.VK_RWIN);

            // W-20: Sticky Keys — five Shift presses within 5 s opens the accessibility prompt.
            if (down && IsShiftKey(vk) && !ctrl && !alt && !win)
            {
                if (CountShiftPress())
                {
                    _pending.Enqueue(new Blocked("StickyKeys", false));
                    return new IntPtr(1);
                }
            }
            else if (down)
            {
                _shiftPresses.Clear();
            }

            var name = Classify(vk, alt, ctrl, shift, win);
            if (name != null)
            {
                if (down) _pending.Enqueue(new Blocked(name, false));
                return new IntPtr(1);
            }
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
        catch
        {
            // Never let an exception escape into the hook chain, and never log from here.
            Interlocked.Increment(ref _callbackErrors);
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
    }

    private static bool IsShiftKey(int vk) =>
        vk == NativeMethods.VK_SHIFT || vk == NativeMethods.VK_LSHIFT || vk == NativeMethods.VK_RSHIFT;

    /// <summary>Returns true on the fifth Shift press within the window (that press must be swallowed).</summary>
    private bool CountShiftPress()
    {
        var now = DateTime.UtcNow;
        _shiftPresses.Enqueue(now);
        while (_shiftPresses.Count > 0 && (now - _shiftPresses.Peek()).TotalSeconds > StickyKeysWindowSeconds)
        {
            _shiftPresses.Dequeue();
        }
        if (_shiftPresses.Count >= StickyKeysPresses)
        {
            _shiftPresses.Clear();
            return true;
        }
        return false;
    }

    /// <summary>Returns a description when the key event must be swallowed, else null.</summary>
    public string? Classify(int vk, bool alt, bool ctrl, bool shift, bool win)
    {
        // Keys blocked regardless of modifiers.
        switch (vk)
        {
            case NativeMethods.VK_SNAPSHOT: return alt ? "Alt+PrintScreen" : "PrintScreen";
            case NativeMethods.VK_F11: return "F11";
            case NativeMethods.VK_F12: return "F12";
            case NativeMethods.VK_APPS: return "Menu";
            case NativeMethods.VK_SLEEP: return "Sleep";
        }
        if (vk >= NativeMethods.VK_BROWSER_BACK && vk <= NativeMethods.VK_LAUNCH_APP2)
        {
            return "MediaKey(" + vk + ")";
        }

        if (win)
        {
            return "Win+" + KeyName(vk);
        }

        if (alt)
        {
            switch (vk)
            {
                case NativeMethods.VK_TAB: return shift ? "Alt+Shift+Tab" : (ctrl ? "Ctrl+Alt+Tab" : "Alt+Tab");
                case NativeMethods.VK_ESCAPE: return "Alt+Esc";
                case NativeMethods.VK_F4: return "Alt+F4";
                case NativeMethods.VK_SPACE: return "Alt+Space";
            }
        }

        if (ctrl)
        {
            switch (vk)
            {
                case NativeMethods.VK_ESCAPE: return shift ? "Ctrl+Shift+Esc" : "Ctrl+Esc";
                case NativeMethods.VK_F4: return "Ctrl+F4";
                case 0x4E: return shift ? "Ctrl+Shift+N" : "Ctrl+N";   // new window
                case 0x54: return shift ? "Ctrl+Shift+T" : "Ctrl+T";   // new tab
                case 0x57: return "Ctrl+W";                            // close
                case 0x4F: return "Ctrl+O";                            // open file
                case 0x53: return "Ctrl+S";                            // save page
                case 0x55: return "Ctrl+U";                            // view source
                case 0x48: return "Ctrl+H";                            // history
                case 0x4A: return "Ctrl+J";                            // downloads / console
                case 0x49: if (shift) return "Ctrl+Shift+I"; break;    // dev tools
                case 0x50: if (!AllowPrinting) return "Ctrl+P"; break;
                case 0x43: if (shift) return "Ctrl+Shift+C"; if (!AllowClipboard) return "Ctrl+C"; break;
                case 0x58: if (!AllowClipboard) return "Ctrl+X"; break;
                case 0x56: if (!AllowClipboard) return shift ? "Ctrl+Shift+V" : "Ctrl+V"; break;
                case NativeMethods.VK_INSERT: if (!AllowClipboard) return "Ctrl+Insert"; break;
            }
        }

        if (shift && !ctrl && !alt)
        {
            if (vk == NativeMethods.VK_INSERT && !AllowClipboard) return "Shift+Insert";
            if (vk == NativeMethods.VK_DELETE && !AllowClipboard) return "Shift+Delete";
        }

        return null;
    }

    private static string KeyName(int vk)
    {
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();          // 0-9
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();          // A-Z
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);          // F1-F24
        switch (vk)
        {
            case NativeMethods.VK_TAB: return "Tab";
            case NativeMethods.VK_ESCAPE: return "Esc";
            case NativeMethods.VK_SPACE: return "Space";
            case NativeMethods.VK_RETURN: return "Enter";
            case NativeMethods.VK_SHIFT:
            case NativeMethods.VK_LSHIFT:
            case NativeMethods.VK_RSHIFT: return "Shift";
            case NativeMethods.VK_CONTROL:
            case NativeMethods.VK_LCONTROL:
            case NativeMethods.VK_RCONTROL: return "Ctrl";
            case NativeMethods.VK_MENU:
            case NativeMethods.VK_LMENU:
            case NativeMethods.VK_RMENU: return "Alt";
            default: return "VK" + vk.ToString("X2");
        }
    }

    // ---- Drain (UI thread) -------------------------------------------------------------

    private void StartDrain()
    {
        if (_drain != null) return;
        _drain = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _drain.Tick += DrainTick;
        _drain.Start();
    }

    private void StopDrain()
    {
        if (_drain == null) return;
        _drain.Stop();
        _drain.Tick -= DrainTick;
        _drain = null;
        DrainTick(null, EventArgs.Empty);
    }

    private void DrainTick(object? sender, EventArgs e)
    {
        var errors = Interlocked.Exchange(ref _callbackErrors, 0);
        if (errors > 0) Log.Error(LogCat, errors + " hook callback error(s) swallowed");
        var guard = 0;
        while (guard++ < 200 && _pending.TryDequeue(out var item))
        {
            Report(item);
        }
    }

    private void Report(Blocked item)
    {
        var now = DateTime.UtcNow;
        if (_lastBlocked.TryGetValue(item.Name, out var last) && (now - last).TotalSeconds < Constants.BlockedShortcutThrottleSeconds)
        {
            return;
        }
        _lastBlocked[item.Name] = now;
        Log.Warn("security", "Blocked shortcut: " + item.Name + (item.Injected ? " (injected)" : string.Empty));
        try
        {
            OnBlocked?.Invoke(item.Name, item.Injected);
        }
        catch (Exception ex)
        {
            Log.Error(LogCat, "OnBlocked handler failed", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Uninstall();
    }
}
