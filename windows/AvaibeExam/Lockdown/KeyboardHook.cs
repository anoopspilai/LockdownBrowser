using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AvaibeExam.Core;
using AvaibeExam.Util;

namespace AvaibeExam.Lockdown;

/// <summary>
/// WH_KEYBOARD_LL hook (CONTRACT §9.3). Installed on the UI thread (a low-level hook needs a
/// thread with a message loop; the callback runs on that thread). Swallows:
///   Alt+Tab, Alt+Shift+Tab, Alt+Esc, Alt+F4, Alt+Space, Ctrl+Esc, Ctrl+Shift+Esc, Ctrl+Alt+Tab,
///   the Windows keys themselves (so Win+D/G/R/L/Shift+S/... never reach the shell),
///   PrintScreen / Alt+PrintScreen, F11, F12, the Apps (menu) key, Sleep, browser/launch keys,
///   Ctrl+N/T/W/O/S/U/H/J, Ctrl+Shift+I/J/C/N, Ctrl+F4,
///   and — policy dependent — Ctrl+P (printing), Ctrl+C/X/V, Ctrl+Insert, Shift+Insert,
///   Shift+Delete (clipboard).
///
/// What CANNOT be blocked from user mode (honestly logged, see README):
///   * Ctrl+Alt+Del (secure attention sequence) is handled by winlogon before any hook.
///   * Win+L: Windows processes the lock combination before low-level hooks see it on most
///     builds. Swallowing the Win key makes it *usually* not fire, but the only reliable block
///     is the DisableLockWorkstation registry policy, which this client deliberately does NOT set.
///   * Secure desktop / UAC prompts, hardware capture devices, second keyboards on other sessions.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const string LogCat = "keyhook";

    // Keep the delegate in a field so the GC never collects it while the hook is installed.
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private readonly Dictionary<string, DateTime> _lastBlocked = new Dictionary<string, DateTime>(StringComparer.Ordinal);
    private IntPtr _hook = IntPtr.Zero;
    private bool _winDown;
    private bool _disposed;

    public bool AllowClipboard { get; set; }
    public bool AllowPrinting { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>(shortcut name) — already throttled to one report per name per 2 s. UI thread.</summary>
    public Action<string>? OnBlocked { get; set; }

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
        Log.Info(LogCat, "Low-level keyboard hook installed");
        return true;
    }

    /// <summary>
    /// Windows silently removes a low-level hook whose callback exceeds LowLevelHooksTimeout
    /// (default 300 ms) a few times, e.g. when the UI thread was busy. Re-installing periodically
    /// heals that without any notification from the OS.
    /// </summary>
    public void Reinstall()
    {
        if (_disposed) return;
        Uninstall();
        Install();
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        if (!NativeMethods.UnhookWindowsHookEx(_hook))
        {
            Log.Warn(LogCat, "UnhookWindowsHookEx failed, error " + Marshal.GetLastWin32Error());
        }
        _hook = IntPtr.Zero;
        Log.Info(LogCat, "Low-level keyboard hook removed");
    }

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

            // Windows keys: swallow completely so no Win+X combination reaches the shell.
            if (vk == NativeMethods.VK_LWIN || vk == NativeMethods.VK_RWIN)
            {
                if (down) { _winDown = true; Report("Win"); }
                if (up) _winDown = false;
                return new IntPtr(1);
            }

            var alt = (info.flags & NativeMethods.LLKHF_ALTDOWN) != 0 || NativeMethods.IsKeyDown(NativeMethods.VK_MENU);
            var ctrl = NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL);
            var shift = NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT);
            var win = _winDown || NativeMethods.IsKeyDown(NativeMethods.VK_LWIN) || NativeMethods.IsKeyDown(NativeMethods.VK_RWIN);

            var name = Classify(vk, alt, ctrl, shift, win);
            if (name != null)
            {
                if (down) Report(name);
                return new IntPtr(1);
            }
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
        catch (Exception ex)
        {
            // Never let an exception escape into the hook chain.
            Log.Error(LogCat, "Hook callback error", ex);
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
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

    private void Report(string name)
    {
        var now = DateTime.UtcNow;
        if (_lastBlocked.TryGetValue(name, out var last) && (now - last).TotalSeconds < Constants.BlockedShortcutThrottleSeconds)
        {
            return;
        }
        _lastBlocked[name] = now;
        Log.Warn("security", "Blocked shortcut: " + name);
        try
        {
            OnBlocked?.Invoke(name);
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
