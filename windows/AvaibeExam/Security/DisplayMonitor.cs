using System;
using System.Windows.Threading;
using AvaibeExam.Util;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace AvaibeExam.Security;

/// <summary>
/// Observes display topology changes (SystemEvents.DisplaySettingsChanged; KioskFallback also
/// forwards WM_DISPLAYCHANGE) and reports the current monitor count. The app maps the count to
/// DISPLAY_CHANGED / EXTERNAL_DISPLAY events and applies policy.externalDisplayAction.
/// </summary>
public sealed class DisplayMonitor
{
    private const string LogCat = "display";
    private readonly Dispatcher _dispatcher;
    private bool _started;

    public int DisplayCount { get; private set; } = CurrentCount();

    /// <summary>(newCount, oldCount) on the UI thread.</summary>
    public Action<int, int>? OnChange { get; set; }

    public DisplayMonitor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public static int CurrentCount()
    {
        try
        {
            return Math.Max(1, WinForms.Screen.AllScreens.Length);
        }
        catch
        {
            return Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CMONITORS));
        }
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        DisplayCount = CurrentCount();
        try
        {
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "SystemEvents unavailable: " + ex.Message);
        }
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        try
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Called by KioskFallback on WM_DISPLAYCHANGE as a second signal source.</summary>
    public void Poke()
    {
        if (!_started) return;
        _dispatcher.BeginInvoke(new Action(HandleChange), DispatcherPriority.Background);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // SystemEvents raises on its own thread; marshal to the UI thread.
        _dispatcher.BeginInvoke(new Action(HandleChange), DispatcherPriority.Background);
    }

    private void HandleChange()
    {
        if (!_started) return;
        var newCount = CurrentCount();
        var old = DisplayCount;
        if (newCount == old) return;
        DisplayCount = newCount;
        Log.Info(LogCat, $"Display settings changed: {old} -> {newCount} monitors");
        OnChange?.Invoke(newCount, old);
    }
}
