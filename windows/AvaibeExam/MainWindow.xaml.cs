using System;
using System.ComponentModel;
using System.Windows;
using AvaibeExam.Core;
using AvaibeExam.Util;
using AvaibeExam.Views;

namespace AvaibeExam;

/// <summary>
/// The single application window. Hosts one of Login / Preflight / Exam / Exit according to
/// AppState.Screen. Closing is refused while locked (BLOCKED_SHORTCUT "quit"); minimizing while
/// locked is reverted by KioskFallback.
/// </summary>
public partial class MainWindow : Window
{
    private const string LogCat = "window";
    private readonly AppState _state;
    private AppScreen? _renderedScreen;

    public MainWindow(AppState state)
    {
        InitializeComponent();
        _state = state;
        DataContext = state;
        state.PropertyChanged += OnStatePropertyChanged;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        Deactivated += OnDeactivated;
        Loaded += (_, __) => RenderScreen();
        RenderScreen();
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.Screen))
        {
            RenderScreen();
        }
    }

    private void RenderScreen()
    {
        var screen = _state.Screen;
        if (_renderedScreen == screen) return;
        _renderedScreen = screen;
        Log.Info(LogCat, "Screen -> " + screen);
        switch (screen)
        {
            case AppScreen.Login:
                Host.Content = new LoginView(_state);
                break;
            case AppScreen.Preflight:
                Host.Content = new PreflightView(_state);
                break;
            case AppScreen.Exam:
                Host.Content = new ExamView(_state);
                break;
            case AppScreen.Exit:
                Host.Content = new ExitView(_state);
                break;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_state.IsLocked)
        {
            Log.Warn("security", "Close attempt blocked while locked");
            _state.ReportBlockedQuit();
            e.Cancel = true;
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_state.IsLocked && WindowState != WindowState.Maximized)
        {
            _state.Coordinator.Kiosk.ReassertWindow();
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_state.IsLocked)
        {
            // The kiosk focus watchdog re-activates and reports; this is just for the log.
            Log.Debug(LogCat, "Window deactivated while locked");
        }
    }
}
