using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AvaibeExam.Core;
using AvaibeExam.Models;

namespace AvaibeExam.Views;

/// <summary>CONTRACT §7.3 — status strip + WebView2 + overlays (pause, warning, exit).</summary>
public partial class ExamView : UserControl
{
    private readonly AppState _state;

    public ExamView(AppState state)
    {
        InitializeComponent();
        _state = state;
        DataContext = state;
        Exit.Attach(state);
        Warning.Dismissed += () => _state.DismissWarning();

        state.PropertyChanged += OnStateChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Render();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HostWebView();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _state.PropertyChanged -= OnStateChanged;
        WebHost.Content = null;
    }

    private void HostWebView()
    {
        var control = _state.Web.Control;
        if (control == null)
        {
            WebHost.Content = null;
            LoadingText.Visibility = Visibility.Visible;
            return;
        }
        if (!ReferenceEquals(WebHost.Content, control))
        {
            WebHost.Content = control;
        }
        LoadingText.Visibility = Visibility.Collapsed;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.Session):
            case nameof(AppState.RemainingSeconds):
            case nameof(AppState.RemainingTimeText):
            case nameof(AppState.LockdownMode):
            case nameof(AppState.IsOnline):
            case nameof(AppState.BackendReachable):
            case nameof(AppState.DisplayCount):
            case nameof(AppState.WarningMessage):
            case nameof(AppState.PauseMessage):
            case nameof(AppState.ExitOverlay):
                Render();
                break;
        }
    }

    private void Render()
    {
        HostWebView();

        var session = _state.Session;
        StudentText.Text = session?.Student.Name ?? string.Empty;
        ExamText.Text = session?.Exam.Title ?? string.Empty;

        CountdownText.Text = AppState.FormatCountdown(_state.RemainingSeconds);
        CountdownText.Foreground = _state.RemainingSeconds < 300
            ? (Brush)FindResource("DangerBrush")
            : Brushes.White;

        RenderBadge(_state.LockdownMode);

        var online = _state.IsOnline;
        var backend = _state.BackendReachable;
        OnlineDot.Fill = (Brush)FindResource(online && backend ? "SuccessBrush" : "DangerBrush");
        OnlineText.Text = online ? (backend ? "Online" : "Backend unreachable") : "Offline";

        if (_state.DisplayCount > 1)
        {
            DisplaysText.Text = _state.DisplayCount + " displays";
            DisplaysText.Visibility = Visibility.Visible;
        }
        else
        {
            DisplaysText.Visibility = Visibility.Collapsed;
        }

        // Overlays
        var pause = _state.PauseMessage;
        Pause.Message = pause ?? string.Empty;
        Pause.Visibility = pause != null ? Visibility.Visible : Visibility.Collapsed;

        var warning = _state.WarningMessage;
        var warningWasHidden = Warning.Visibility != Visibility.Visible;
        Warning.Message = warning ?? string.Empty;
        Warning.Visibility = warning != null ? Visibility.Visible : Visibility.Collapsed;

        var exitWasHidden = Exit.Visibility != Visibility.Visible;
        var exit = _state.ExitOverlay;
        Exit.Render();
        Exit.Visibility = exit != null ? Visibility.Visible : Visibility.Collapsed;

        var anyOverlay = pause != null || warning != null || exit != null;
        // Airspace: the WebView2 HWND would paint over WPF overlays, so hide it while one is up.
        WebHost.Visibility = anyOverlay ? Visibility.Hidden : Visibility.Visible;

        if (exit != null && exitWasHidden) Exit.FocusCode();
        else if (warning != null && warningWasHidden && exit == null) Warning.FocusButton();
    }

    private void RenderBadge(LockdownMode mode)
    {
        string text;
        Brush fg;
        Color bg;
        switch (mode)
        {
            case LockdownMode.AssignedAccess:
                text = "Assigned Access";
                fg = (Brush)FindResource("SuccessBrush");
                bg = Color.FromArgb(0x33, 0x16, 0xA3, 0x4A);
                BadgeBorder.ToolTip = "Running inside a Windows Assigned Access kiosk session (OS-enforced single-app shell) plus client controls.";
                break;
            case LockdownMode.KioskFallback:
                text = "Kiosk fallback";
                fg = (Brush)FindResource("WarningBrush");
                bg = Color.FromArgb(0x33, 0xD9, 0x77, 0x06);
                BadgeBorder.ToolTip = "Best-effort kiosk mode: not an Assigned Access session. Ctrl+Alt+Del cannot be blocked; all attempts are reported.";
                break;
            default:
                text = "Not locked";
                fg = (Brush)FindResource("DangerBrush");
                bg = Color.FromArgb(0x33, 0xDC, 0x26, 0x26);
                BadgeBorder.ToolTip = "Not locked.";
                break;
        }
        BadgeText.Text = text;
        BadgeText.Foreground = fg;
        BadgeBorder.Background = new SolidColorBrush(bg);
    }
}
