using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AvaibeExam.Core;
using AvaibeExam.Util;

namespace AvaibeExam;

/// <summary>
/// Composition root (no DI container): single-instance mutex, global exception handlers,
/// AppState + MainWindow, dev-only auto-run and smoke test.
/// </summary>
public partial class App : System.Windows.Application
{
    private const string LogCat = "app";
    private Mutex? _singleInstance;
    private AppState? _state;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Prune(14);
        Log.Info(LogCat, $"{Constants.ProductName} {Constants.ClientVersion} launching (pid {Environment.ProcessId}, {Environment.OSVersion.VersionString}, .NET {Environment.Version})");

        bool createdNew;
        _singleInstance = new Mutex(true, Constants.SingleInstanceMutexName, out createdNew);
        if (!createdNew)
        {
            Log.Warn(LogCat, "Another instance is already running; exiting");
            System.Windows.MessageBox.Show("Avaibe Exam is already running.", Constants.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var state = new AppState();
        _state = state;
        var window = new MainWindow(state);
        state.AttachWindow(window);
        MainWindow = window;
        window.Show();

        state.AutoRunIfRequested();

        if (Constants.EnvIsOne(Constants.EnvSmokeTest))
        {
            Log.Info(LogCat, "SMOKE_OK");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                Log.Info(LogCat, "Smoke test complete; exiting 0");
                Shutdown(0);
            };
            timer.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info(LogCat, "Exiting with code " + e.ApplicationExitCode);
        try
        {
            _singleInstance?.ReleaseMutex();
            _singleInstance?.Dispose();
        }
        catch
        {
            // ignore
        }
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(LogCat, "Unhandled UI exception", e.Exception);
        // Keep running: crashing while locked would leave the student on a half-restored desktop
        // (and the kiosk state would not be reversed cleanly). When not locked, tell the user.
        e.Handled = true;
        if (_state == null || !_state.IsLocked)
        {
            try
            {
                System.Windows.MessageBox.Show("An unexpected error occurred:\n\n" + e.Exception.Message + "\n\nSee the log in " + Log.LogDirectory,
                    Constants.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                // ignore
            }
        }
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Log.Error(LogCat, "Unhandled domain exception (terminating=" + e.IsTerminating + "): " + (ex?.ToString() ?? e.ExceptionObject?.ToString() ?? "?"));
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(LogCat, "Unobserved task exception", e.Exception);
        e.SetObserved();
    }
}
