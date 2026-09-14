using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AvaibeExam.Browser;
using AvaibeExam.Lockdown;
using AvaibeExam.Models;
using AvaibeExam.Networking;
using AvaibeExam.Security;
using AvaibeExam.Util;

namespace AvaibeExam.Core;

public enum AppScreen
{
    Login,
    Preflight,
    Exam,
    Exit,
}

/// <summary>State of the native Exit overlay (CONTRACT §7.5).</summary>
public sealed class ExitOverlayState : ObservableObject
{
    private string _message = string.Empty;
    private bool _allowReleaseCode;
    private string? _errorText;
    private bool _verifying;
    private string _statusText = "Waiting for your teacher to release the exam…";

    public string Message { get => _message; set => Set(ref _message, value); }
    public bool AllowReleaseCode { get => _allowReleaseCode; set => Set(ref _allowReleaseCode, value); }
    public string? ErrorText { get => _errorText; set => Set(ref _errorText, value); }
    public bool Verifying { get => _verifying; set => Set(ref _verifying, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
}

/// <summary>
/// Single source of truth for the UI and the orchestrator of the exam flow
/// (Login → (enroll) → Preflight → Exam (locked) → Exit). Mirrors macos/App/AppState.swift.
/// Every member must be used from the UI thread; services marshal back via the Dispatcher.
/// </summary>
public sealed class AppState : ObservableObject
{
    private const string LogCat = "app";

    // ---- Services --------------------------------------------------------------------

    public Dispatcher Dispatcher { get; }
    public ApiClient Api { get; }
    public EventReporter Events { get; }
    public HeartbeatService Heartbeat { get; }
    public LockdownCoordinator Coordinator { get; }
    public DisplayMonitor DisplayMonitor { get; }
    public NetworkMonitor NetworkMonitor { get; }
    public ExamWebView Web { get; } = new ExamWebView();

    private Window? _window;
    private PreflightRaw? _preflightRaw;
    private string? _minClientVersionFromEnroll;
    private DispatcherTimer? _countdown;
    private DateTimeOffset? _expiresAt;
    private bool _timeUpHandled;
    private bool _released;
    private bool _submitted;
    private bool _autoRunPending;

    // ---- Observable state ------------------------------------------------------------

    private AppScreen _screen = AppScreen.Login;
    private string _baseUrlText;
    private string _enrollmentTokenText = string.Empty;
    private string _studentCode;
    private string _examCode;
    private Enrollment? _enrollment;
    private bool _isBusy;
    private string _busyText = string.Empty;
    private string? _loginError;
    private List<string> _preflightServerFailures = new List<string>();
    private string? _preflightError;
    private bool _hasServerPolicy;
    private SessionStartResponse? _session;
    private Policy _policy = new Policy();
    private LockdownMode _lockdownMode; // default: None
    private bool _isOnline = true;
    private bool _backendReachable = true;
    private int _displayCount = AvaibeExam.Security.DisplayMonitor.CurrentCount();
    private int _remainingSeconds;
    private string? _warningMessage;
    private string? _pauseMessage;
    private ExitOverlayState? _exitOverlay;
    private string _exitReason = string.Empty;

    public AppScreen Screen
    {
        get => _screen;
        private set
        {
            if (Set(ref _screen, value))
            {
                Raise(nameof(IsLocked));
                Raise(nameof(CanStartExam));
            }
        }
    }

    public string BaseUrlText { get => _baseUrlText; set => Set(ref _baseUrlText, value); }
    public string EnrollmentTokenText { get => _enrollmentTokenText; set => Set(ref _enrollmentTokenText, value); }
    public string StudentCode { get => _studentCode; set => Set(ref _studentCode, value); }
    public string ExamCode { get => _examCode; set => Set(ref _examCode, value); }

    public Enrollment? Enrollment
    {
        get => _enrollment;
        private set
        {
            if (Set(ref _enrollment, value)) Raise(nameof(IsEnrolled));
        }
    }

    public bool IsEnrolled => _enrollment != null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) Raise(nameof(CanStartExam));
        }
    }

    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }
    public string? LoginError { get => _loginError; private set => Set(ref _loginError, value); }

    public ObservableCollection<PreflightItem> PreflightItems { get; } = new ObservableCollection<PreflightItem>();

    public List<string> PreflightServerFailures
    {
        get => _preflightServerFailures;
        private set
        {
            _preflightServerFailures = value;
            Raise();
            Raise(nameof(CanStartExam));
        }
    }

    public string? PreflightError { get => _preflightError; private set => Set(ref _preflightError, value); }

    public bool HasServerPolicy
    {
        get => _hasServerPolicy;
        private set
        {
            if (Set(ref _hasServerPolicy, value))
            {
                Raise(nameof(CanStartExam));
                Raise(nameof(RequireAACButUnavailable));
            }
        }
    }

    public SessionStartResponse? Session
    {
        get => _session;
        private set
        {
            if (Set(ref _session, value)) Raise(nameof(CanStartExam));
        }
    }

    public Policy Policy
    {
        get => _policy;
        private set
        {
            _policy = value;
            Raise();
            Raise(nameof(CanStartExam));
            Raise(nameof(RequireAACButUnavailable));
        }
    }

    public LockdownMode LockdownMode
    {
        get => _lockdownMode;
        private set
        {
            if (Set(ref _lockdownMode, value)) Raise(nameof(IsLocked));
        }
    }

    public bool IsOnline { get => _isOnline; private set => Set(ref _isOnline, value); }
    public bool BackendReachable { get => _backendReachable; private set => Set(ref _backendReachable, value); }
    public int DisplayCount { get => _displayCount; private set => Set(ref _displayCount, value); }
    public int RemainingSeconds { get => _remainingSeconds; private set => Set(ref _remainingSeconds, value); }
    public string? WarningMessage { get => _warningMessage; private set => Set(ref _warningMessage, value); }
    public string? PauseMessage { get => _pauseMessage; private set => Set(ref _pauseMessage, value); }
    public ExitOverlayState? ExitOverlay { get => _exitOverlay; private set => Set(ref _exitOverlay, value); }
    public string ExitReason { get => _exitReason; private set => Set(ref _exitReason, value); }

    // ---- Derived ---------------------------------------------------------------------

    public bool IsLocked => Screen == AppScreen.Exam && LockdownMode != LockdownMode.None;

    /// <summary>Windows analogue of the AAC entitlement: an Assigned Access session was detected.</summary>
    public bool AssignedAccessDetected => _preflightRaw?.Report.AacEntitlementPresent ?? false;

    public bool CanStartExam
    {
        get
        {
            if (Session == null || !HasServerPolicy || PreflightServerFailures.Count > 0 || IsBusy) return false;
            if (PreflightItems.Any(i => i.BlocksStart)) return false;
            if (Policy.RequireAAC && !AssignedAccessDetected) return false;
            return true;
        }
    }

    public bool RequireAACButUnavailable => HasServerPolicy && Policy.RequireAAC && !AssignedAccessDetected;

    public string RemainingTimeText => FormatCountdown(RemainingSeconds);

    public static string FormatCountdown(int seconds)
    {
        var s = Math.Max(0, seconds);
        var h = s / 3600;
        var m = (s % 3600) / 60;
        var sec = s % 60;
        return h > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, sec)
            : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", m, sec);
    }

    // ---- Construction ----------------------------------------------------------------

    public AppState()
    {
        Dispatcher = Dispatcher.CurrentDispatcher;

        var settings = DeviceIdentity.LoadSettings();
        _baseUrlText = Constants.Env(Constants.EnvBaseUrl)
                       ?? (string.IsNullOrWhiteSpace(settings.BaseUrl) ? Constants.DefaultBaseUrl : settings.BaseUrl!);
        _studentCode = settings.LastStudentCode ?? string.Empty;
        _examCode = settings.LastExamCode ?? string.Empty;
        _enrollment = DeviceIdentity.LoadEnrollment();

        Api = new ApiClient(_baseUrlText);
        Api.SetDeviceId(_enrollment?.DeviceId);
        Events = new EventReporter(Api);
        Heartbeat = new HeartbeatService(Api, Dispatcher);
        Coordinator = new LockdownCoordinator(Events, Dispatcher);
        DisplayMonitor = new DisplayMonitor(Dispatcher);
        NetworkMonitor = new NetworkMonitor(Dispatcher, Api);

        PreflightItems.CollectionChanged += (_, __) => Raise(nameof(CanStartExam));
        WireCallbacks();
    }

    /// <summary>The main window (needed for lockdown). Called once by App.</summary>
    public void AttachWindow(Window window)
    {
        _window = window;
    }

    private void WireCallbacks()
    {
        Heartbeat.SnapshotProvider = () =>
        {
            HeartbeatStatus status;
            if (WarningMessage != null || PauseMessage != null) status = HeartbeatStatus.Warning;
            else status = IsLocked ? HeartbeatStatus.Locked : HeartbeatStatus.Unlocked;
            return new HeartbeatService.Snapshot(status, DisplayCount, LockdownMode);
        };
        Heartbeat.OnCommand = HandleCommand;
        Heartbeat.OnRemainingSeconds = ResyncCountdown;
        Heartbeat.OnResult = ok => BackendReachable = ok;

        Coordinator.OnWarning = ShowWarning;
        Coordinator.OnDisplayChange = () => DisplayMonitor.Poke();

        DisplayMonitor.OnChange = HandleDisplayChange;
        NetworkMonitor.OnChange = HandleNetworkChange;

        Web.Bridge.OnMessage = HandleBridge;
        Web.OnBlockedNavigation = (url, reason) =>
            Events.Record(EventType.BlockedNavigation, EventSeverity.Medium,
                new Dictionary<string, object?> { ["url"] = url, ["reason"] = reason });
        Web.OnProcessFailed = (kind, fatal) =>
        {
            Events.RecordNow(EventType.LockdownInterrupted, EventSeverity.High,
                new Dictionary<string, object?> { ["component"] = "webview2", ["kind"] = kind, ["fatal"] = fatal });
            if (fatal)
            {
                ShowExitOverlay("The exam page stopped responding (" + kind + "). The exam stays locked until your teacher releases it.");
            }
            else
            {
                ShowWarning("The exam page was reloaded after a problem. This has been reported.");
            }
        };
        Web.OnInitialized = () =>
        {
            // The WebView2 child window now exists; re-apply capture protection to the top-level window.
            Coordinator.Kiosk.ApplyCaptureProtection();
            if (Web.Control != null)
            {
                try { Web.Control.Focus(); } catch { /* ignore */ }
            }
        };
    }

    // ---- Async helpers ---------------------------------------------------------------

    /// <summary>Runs UI-thread async work and logs (never throws) — avoids async void.</summary>
    private void Fire(Func<Task> work, string what)
    {
        _ = RunSafeAsync(work, what);
    }

    private static async Task RunSafeAsync(Func<Task> work, string what)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Log.Error(LogCat, what + " failed", ex);
        }
    }

    // ---- Dev-only auto run -----------------------------------------------------------

    /// <summary>
    /// AVAIBE_AUTO_RUN=1: fill the login form from AVAIBE_AUTO_TOKEN / _STUDENT / _EXAM, continue,
    /// and press Start Exam as soon as the preflight allows it. AVAIBE_AUTO_EXIT_AFTER=&lt;s&gt;
    /// force-exits the process after that many seconds (safety net for unattended tests).
    /// Never active in production: driven purely by environment variables.
    /// </summary>
    public void AutoRunIfRequested()
    {
        if (!Constants.EnvIsOne(Constants.EnvAutoRun)) return;
        Log.Warn(LogCat, "AUTO RUN enabled (dev only)");
        var token = Constants.Env(Constants.EnvAutoToken);
        var student = Constants.Env(Constants.EnvAutoStudent);
        var exam = Constants.Env(Constants.EnvAutoExam);
        if (token != null) EnrollmentTokenText = token;
        if (student != null) StudentCode = student;
        if (exam != null) ExamCode = exam;

        var exitAfter = Constants.Env(Constants.EnvAutoExitAfter);
        if (exitAfter != null && double.TryParse(exitAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) && secs > 0)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(secs) };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                Log.Warn(LogCat, "AUTO EXIT after " + secs + "s");
                Environment.Exit(0);
            };
            timer.Start();
        }

        _autoRunPending = true;
        PropertyChanged += AutoRunObserver;
        ContinueFromLogin();
    }

    private void AutoRunObserver(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_autoRunPending) return;
        if (IsBusy || Screen != AppScreen.Preflight || !CanStartExam) return;
        _autoRunPending = false;
        PropertyChanged -= AutoRunObserver;
        Log.Warn(LogCat, "AUTO RUN: starting exam");
        Dispatcher.BeginInvoke(new Action(StartExam), DispatcherPriority.Background);
    }

    // ---- Login → Preflight -----------------------------------------------------------

    public void ContinueFromLogin()
    {
        if (IsBusy) return;
        LoginError = null;
        var baseUrl = (BaseUrlText ?? string.Empty).Trim();
        if (!Api.SetBaseUrl(baseUrl))
        {
            LoginError = "Enter a valid backend URL (e.g. http://localhost:4000).";
            return;
        }
        var student = (StudentCode ?? string.Empty).Trim();
        var exam = (ExamCode ?? string.Empty).Trim().ToUpperInvariant();
        if (student.Length == 0 || exam.Length == 0)
        {
            LoginError = "Student code and exam code are required.";
            return;
        }
        if (Enrollment == null && (EnrollmentTokenText ?? string.Empty).Trim().Length == 0)
        {
            LoginError = "This device is not enrolled yet. Enter the enrollment token.";
            return;
        }
        DeviceIdentity.UpdateSettings(s =>
        {
            s.BaseUrl = baseUrl;
            s.LastStudentCode = student;
            s.LastExamCode = exam;
        });

        Fire(() => ContinueFromLoginAsync(student, exam), "ContinueFromLogin");
    }

    private async Task ContinueFromLoginAsync(string student, string exam)
    {
        IsBusy = true;
        try
        {
            if (Enrollment == null)
            {
                BusyText = "Enrolling device…";
                try
                {
                    await EnrollAsync((EnrollmentTokenText ?? string.Empty).Trim());
                }
                catch (ApiException ex)
                {
                    LoginError = ex.DisplayText;
                    return;
                }
                catch (Exception ex)
                {
                    LoginError = ex.Message;
                    return;
                }
            }
            BusyText = "Running readiness checks…";
            await RunPreflightAndStartSessionAsync(student, exam);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task EnrollAsync(string token)
    {
        var request = new EnrollRequest
        {
            EnrollmentToken = token,
            OsVersion = DeviceIdentity.OsVersion(),
            HardwareId = DeviceIdentity.HardwareId(),
            DeviceName = DeviceIdentity.DeviceName(),
        };
        var response = await Api.EnrollAsync(request);
        var enrollment = new Enrollment { DeviceId = response.DeviceId, DeviceToken = response.DeviceToken, Mode = response.Mode };
        DeviceIdentity.SaveEnrollment(enrollment);
        Enrollment = enrollment;
        _minClientVersionFromEnroll = response.MinClientVersion;
        Api.SetDeviceId(enrollment.DeviceId);
        EnrollmentTokenText = string.Empty;
        Log.Info(LogCat, "Enrolled as " + enrollment.DeviceId + " (" + enrollment.Mode + ")");
    }

    public void ResetEnrollment()
    {
        if (IsLocked || IsBusy) return;
        DeviceIdentity.ResetEnrollment();
        Enrollment = null;
        Api.SetDeviceId(null);
    }

    private async Task RunPreflightAndStartSessionAsync(string student, string exam)
    {
        PreflightError = null;
        PreflightServerFailures = new List<string>();
        var raw = await Preflight.RunAsync(Api);
        _preflightRaw = raw;
        DisplayCount = raw.Report.DisplayCount;
        ReplacePreflightItems(Preflight.Items(raw, Policy, _minClientVersionFromEnroll));
        Raise(nameof(AssignedAccessDetected));
        Raise(nameof(RequireAACButUnavailable));

        var enrollment = Enrollment;
        if (enrollment == null)
        {
            LoginError = "Device is not enrolled.";
            return;
        }

        BusyText = "Starting session…";
        var request = new SessionStartRequest
        {
            StudentCode = student,
            ExamCode = exam,
            DeviceId = enrollment.DeviceId,
            Preflight = raw.Report,
            PreflightExtras = raw.Extras,
        };
        try
        {
            var response = await Api.StartSessionAsync(request);
            response.Policy = (response.Policy ?? new Policy()).Normalized();
            Session = response;
            Policy = response.Policy;
            HasServerPolicy = true;
            Api.SetSessionToken(response.SessionToken);
            ReplacePreflightItems(Preflight.Items(raw, Policy, _minClientVersionFromEnroll));
            _expiresAt = response.ExpiresAtDate;
            RemainingSeconds = _expiresAt.HasValue
                ? Math.Max(0, (int)Math.Round((_expiresAt.Value - DateTimeOffset.UtcNow).TotalSeconds))
                : 0;
            Log.Info(LogCat, "Session " + response.SessionId + " started; requireAAC=" + response.Policy.RequireAAC);
            Screen = AppScreen.Preflight;
        }
        catch (ApiException ex)
        {
            if (ex.Code == "PREFLIGHT_FAILED")
            {
                PreflightServerFailures = ex.Failures.Count > 0 ? ex.Failures.ToList() : new List<string> { "PREFLIGHT_FAILED" };
                PreflightError = string.IsNullOrEmpty(ex.Message) ? "The server rejected the readiness check." : ex.Message;
                Screen = AppScreen.Preflight;
            }
            else if (Screen == AppScreen.Preflight)
            {
                PreflightError = ex.DisplayText;
            }
            else
            {
                LoginError = ex.DisplayText;
            }
        }
        catch (Exception ex)
        {
            if (Screen == AppScreen.Preflight) PreflightError = ex.Message; else LoginError = ex.Message;
        }
    }

    private void ReplacePreflightItems(List<PreflightItem> items)
    {
        PreflightItems.Clear();
        foreach (var item in items) PreflightItems.Add(item);
        Raise(nameof(CanStartExam));
    }

    /// <summary>[Re-run checks] on the Preflight screen.</summary>
    public void RerunPreflight()
    {
        if (IsBusy) return;
        Fire(RerunPreflightAsync, "RerunPreflight");
    }

    private async Task RerunPreflightAsync()
    {
        IsBusy = true;
        BusyText = "Re-running readiness checks…";
        try
        {
            if (Session == null)
            {
                await RunPreflightAndStartSessionAsync(
                    (StudentCode ?? string.Empty).Trim(),
                    (ExamCode ?? string.Empty).Trim().ToUpperInvariant());
            }
            else
            {
                var raw = await Preflight.RunAsync(Api);
                _preflightRaw = raw;
                DisplayCount = raw.Report.DisplayCount;
                ReplacePreflightItems(Preflight.Items(raw, Policy, _minClientVersionFromEnroll));
                Raise(nameof(AssignedAccessDetected));
                Raise(nameof(RequireAACButUnavailable));
                PreflightError = null;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void BackToLogin()
    {
        if (IsLocked || IsBusy) return;
        Session = null;
        HasServerPolicy = false;
        Policy = Policy.ConservativeDefault;
        PreflightServerFailures = new List<string>();
        PreflightError = null;
        Api.SetSessionToken(null);
        Screen = AppScreen.Login;
    }

    // ---- Start exam ------------------------------------------------------------------

    public void StartExam()
    {
        if (!CanStartExam || Session == null || _window == null) return;
        Fire(StartExamAsync, "StartExam");
    }

    private async Task StartExamAsync()
    {
        var session = Session;
        var window = _window;
        if (session == null || window == null) return;

        IsBusy = true;
        BusyText = "Engaging lockdown…";
        try
        {
            _released = false;
            _submitted = false;
            _timeUpHandled = false;

            Events.Start(session.SessionId, Policy.EventFlushIntervalSeconds);
            Events.Record(EventType.SessionStart, EventSeverity.Info, new Dictionary<string, object?>
            {
                ["examCode"] = session.Exam.Code,
                ["clientVersion"] = Constants.ClientVersion,
                ["platform"] = Constants.Platform,
                ["assignedAccessDetected"] = AssignedAccessDetected,
                ["virtualMachine"] = _preflightRaw?.Extras.VirtualMachine ?? false,
            });
            if (_preflightRaw?.Extras.VirtualMachine == true)
            {
                Events.Record(EventType.VirtualMachineDetected, EventSeverity.Medium,
                    new Dictionary<string, object?> { ["detail"] = _preflightRaw.VirtualMachineDetail });
            }
            if (_preflightRaw?.Extras.RdpSession == true)
            {
                Events.Record(EventType.RdpSession, EventSeverity.High, null);
            }

            var mode = Coordinator.Engage(Policy, window);
            if (mode == LockdownMode.None)
            {
                PreflightError = Policy.RequireAAC
                    ? "This exam requires a Windows Assigned Access (kiosk) session, which was not detected on this device."
                    : "Lockdown could not be engaged.";
                await Events.FlushAsync();
                return;
            }
            LockdownMode = mode;

            if (!Uri.TryCreate(session.ExamUrl, UriKind.Absolute, out var examUrl))
            {
                PreflightError = "Invalid exam URL from server.";
                Coordinator.Release(null);
                LockdownMode = LockdownMode.None;
                return;
            }

            Web.CreateControl(Policy, mode);
            Screen = AppScreen.Exam;     // ExamView hosts the control so it gets an HWND

            try
            {
                await Web.InitializeAsync();
            }
            catch (Exception ex)
            {
                Log.Error(LogCat, "WebView2 initialization failed", ex);
                Events.RecordNow(EventType.LockdownFailed, EventSeverity.High,
                    new Dictionary<string, object?> { ["reason"] = "webview2: " + ex.Message });
                Web.Teardown();
                Coordinator.Release(null);
                LockdownMode = LockdownMode.None;
                Screen = AppScreen.Preflight;
                PreflightError = "The exam browser (WebView2) could not start: " + ex.Message;
                return;
            }

            Web.Load(examUrl);
            Web.Bridge.Send("LOCKDOWN_STATE", null, LockdownStatePayload());

            DisplayMonitor.Start();
            NetworkMonitor.Start();
            Heartbeat.Start(session.SessionId, Policy.HeartbeatIntervalSeconds);
            StartCountdown();
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Countdown -------------------------------------------------------------------

    private void StartCountdown()
    {
        StopCountdown();
        _countdown = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, __) => TickCountdown();
        _countdown.Start();
        TickCountdown();
    }

    private void StopCountdown()
    {
        if (_countdown == null) return;
        _countdown.Stop();
        _countdown = null;
    }

    private void TickCountdown()
    {
        if (!_expiresAt.HasValue) return;
        var remaining = Math.Max(0, (int)Math.Round((_expiresAt.Value - DateTimeOffset.UtcNow).TotalSeconds));
        if (remaining != RemainingSeconds)
        {
            RemainingSeconds = remaining;
            Raise(nameof(RemainingTimeText));
        }
        if (remaining == 0 && !_timeUpHandled && Screen == AppScreen.Exam)
        {
            _timeUpHandled = true;
            TimeUp();
        }
    }

    private void ResyncCountdown(int serverRemaining)
    {
        var serverExpiry = DateTimeOffset.UtcNow.AddSeconds(serverRemaining);
        if (_expiresAt.HasValue && Math.Abs((_expiresAt.Value - serverExpiry).TotalSeconds) < 3) return;
        _expiresAt = serverExpiry;
        TickCountdown();
    }

    private void TimeUp()
    {
        Log.Warn(LogCat, "Exam time is up; auto-submitting");
        Web.Bridge.Send("WARNING", null, new Dictionary<string, object?> { ["message"] = "Time is up. Your exam is being submitted." });
        Fire(async () =>
        {
            await SubmitExamAsync("time-up");
            ShowExitOverlay("Time is up. Your exam has been submitted.");
            Heartbeat.BeatNow();
        }, "TimeUp");
    }

    private async Task SubmitExamAsync(string reason)
    {
        var session = Session;
        if (session == null || _submitted) return;
        _submitted = true;
        try
        {
            var response = await Api.SubmitAsync(session.SessionId);
            Events.RecordNow(EventType.ExamSubmitted, EventSeverity.Info, new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["submittedAt"] = response.SubmittedAt ?? string.Empty,
            });
        }
        catch (Exception ex)
        {
            Log.Error(LogCat, "Submit failed: " + ex.Message);
            Events.RecordNow(EventType.ExamSubmitted, EventSeverity.Medium, new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["error"] = ex.Message,
            });
        }
    }

    // ---- Heartbeat commands ----------------------------------------------------------

    private void HandleCommand(HeartbeatCommand cmd)
    {
        switch (cmd.Type)
        {
            case HeartbeatCommandType.Release:
                Events.Record(EventType.UnlockCompleted, EventSeverity.Info, new Dictionary<string, object?>
                {
                    ["authorizationId"] = cmd.AuthorizationId ?? string.Empty,
                    ["source"] = "heartbeat",
                });
                FinishRelease(cmd.Reason ?? "Released by teacher", cmd.AuthorizationId);
                break;
            case HeartbeatCommandType.Terminate:
                Events.Record(EventType.SessionEnd, EventSeverity.High,
                    new Dictionary<string, object?> { ["reason"] = "terminated: " + (cmd.Reason ?? string.Empty) });
                FinishRelease("Terminated by teacher" + (cmd.Reason != null ? ": " + cmd.Reason : string.Empty), null);
                break;
            case HeartbeatCommandType.Warn:
                ShowWarning(cmd.Message ?? cmd.Reason ?? "Warning from your teacher");
                break;
            default:
                break;
        }
    }

    // ---- Warnings / overlays ---------------------------------------------------------

    public void ShowWarning(string message)
    {
        WarningMessage = message;
        Web.Bridge.Send("WARNING", null, new Dictionary<string, object?> { ["message"] = message });
    }

    public void DismissWarning()
    {
        WarningMessage = null;
    }

    public void ShowExitOverlay(string message)
    {
        if (ExitOverlay == null)
        {
            ExitOverlay = new ExitOverlayState { Message = message, AllowReleaseCode = Policy.AllowStudentReleaseCode };
        }
        else
        {
            ExitOverlay.Message = message;
        }
    }

    /// <summary>Student pressed "Verify code" on the exit overlay.</summary>
    public void VerifyReleaseCode(string code)
    {
        var state = ExitOverlay;
        var session = Session;
        var enrollment = Enrollment;
        if (state == null || state.Verifying || session == null || enrollment == null) return;
        var trimmed = (code ?? string.Empty).Trim();
        if (trimmed.Length != 6 || !trimmed.All(char.IsDigit))
        {
            state.ErrorText = "Enter the 6-digit release code.";
            return;
        }
        state.Verifying = true;
        state.ErrorText = null;
        Events.Record(EventType.UnlockRequested, EventSeverity.Info, new Dictionary<string, object?> { ["method"] = "release-code" });
        Fire(() => VerifyReleaseCodeAsync(state, session.SessionId, enrollment.DeviceId, trimmed), "VerifyReleaseCode");
    }

    private async Task VerifyReleaseCodeAsync(ExitOverlayState state, string sessionId, string deviceId, string code)
    {
        try
        {
            var response = await Api.UnlockAsync(sessionId, new UnlockRequest { ReleaseCode = code, DeviceId = deviceId });
            if (!response.Authorized)
            {
                throw new ApiException("UNLOCK_DENIED", "Not authorized", 403, null);
            }
            Events.Record(EventType.UnlockCompleted, EventSeverity.Info, new Dictionary<string, object?>
            {
                ["authorizationId"] = response.AuthorizationId ?? string.Empty,
                ["source"] = "release-code",
            });
            FinishRelease("Release code accepted", response.AuthorizationId);
        }
        catch (Exception ex)
        {
            var apiEx = ex as ApiException;
            var errorCode = apiEx?.Code ?? "ERROR";
            var text = apiEx?.DisplayText ?? ex.Message;
            Events.RecordNow(EventType.UnlockDenied, EventSeverity.Medium, new Dictionary<string, object?> { ["code"] = errorCode });
            state.Verifying = false;
            state.ErrorText = text;
        }
    }

    // ---- Release / exit --------------------------------------------------------------

    private void FinishRelease(string reason, string? authorizationId)
    {
        if (_released) return;
        _released = true;
        Log.Info(LogCat, "Finishing release: " + reason);
        Heartbeat.Stop();
        StopCountdown();
        DisplayMonitor.Stop();
        NetworkMonitor.Stop();

        Web.Bridge.Send("LOCKDOWN_STATE", null, new Dictionary<string, object?> { ["lockdownMode"] = "none", ["engaged"] = false });
        Coordinator.Release(authorizationId);
        LockdownMode = LockdownMode.None;
        Events.Record(EventType.SessionEnd, EventSeverity.Info, new Dictionary<string, object?> { ["reason"] = reason });

        ExitReason = reason;
        ExitOverlay = null;
        WarningMessage = null;
        PauseMessage = null;
        Screen = AppScreen.Exit;
        Web.Teardown();

        Fire(() => Events.StopAsync(), "Events.Stop");
    }

    /// <summary>Only reachable from the Exit screen.</summary>
    public void Quit()
    {
        if (IsLocked) return;
        Log.Info(LogCat, "Quit requested");
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>MainWindow reports a close/quit attempt while locked.</summary>
    public void ReportBlockedQuit()
    {
        Events.RecordNow(EventType.BlockedShortcut, EventSeverity.Medium, new Dictionary<string, object?> { ["shortcut"] = "quit" });
    }

    // ---- Security monitors -----------------------------------------------------------

    private void HandleDisplayChange(int newCount, int oldCount)
    {
        DisplayCount = newCount;
        if (Screen != AppScreen.Exam) return;
        Events.Record(EventType.DisplayChanged, EventSeverity.Low,
            new Dictionary<string, object?> { ["from"] = oldCount, ["to"] = newCount });
        Coordinator.HandleScreensChanged();

        if (newCount > 1 && Policy.BlockExternalDisplay)
        {
            Events.RecordNow(EventType.ExternalDisplay, EventSeverity.High, new Dictionary<string, object?>
            {
                ["displayCount"] = newCount,
                ["action"] = Policy.ExternalDisplayAction.Wire(),
            });
            switch (Policy.ExternalDisplayAction)
            {
                case ExternalDisplayAction.Warn:
                case ExternalDisplayAction.BlockStart:
                    ShowWarning("An external display was connected. Disconnect it now — this has been reported.");
                    break;
                case ExternalDisplayAction.Pause:
                    PauseMessage = "Exam paused: disconnect the external display to continue.";
                    Web.Bridge.Send("WARNING", null, new Dictionary<string, object?> { ["message"] = "Exam paused: external display connected." });
                    break;
                case ExternalDisplayAction.Terminate:
                    Events.Record(EventType.SessionEnd, EventSeverity.High, new Dictionary<string, object?> { ["reason"] = "external display" });
                    FinishRelease("Exam ended: an external display was connected", null);
                    break;
                case ExternalDisplayAction.Flag:
                default:
                    break;
            }
        }
        else if (newCount <= 1 && PauseMessage != null)
        {
            PauseMessage = null;
            Web.Bridge.Send("WARNING", null, new Dictionary<string, object?> { ["message"] = "External display removed. You may continue." });
        }
    }

    private void HandleNetworkChange(bool online)
    {
        IsOnline = online;
        if (Screen != AppScreen.Exam) return;
        Events.Record(online ? EventType.NetworkOnline : EventType.NetworkOffline, online ? EventSeverity.Info : EventSeverity.Medium, null);
        if (online) Fire(() => Events.FlushAsync(), "Flush after reconnect");
    }

    // ---- Bridge (web → native) -------------------------------------------------------

    private Dictionary<string, object?> LockdownStatePayload() => new Dictionary<string, object?>
    {
        ["lockdownMode"] = LockdownMode.Wire(),
        ["engaged"] = LockdownMode != LockdownMode.None,
    };

    private void HandleBridge(WebMessageBridge.Incoming incoming)
    {
        switch (incoming.Kind)
        {
            case WebMessageBridge.IncomingKind.Ready:
                SendSessionStatus(incoming.Id);
                Web.Bridge.Send("LOCKDOWN_STATE", null, LockdownStatePayload());
                break;
            case WebMessageBridge.IncomingKind.GetSessionStatus:
            case WebMessageBridge.IncomingKind.HeartbeatPing:
                SendSessionStatus(incoming.Id);
                break;
            case WebMessageBridge.IncomingKind.ReportEvent:
                Events.Record(EventType.WebEvent, incoming.Severity, new Dictionary<string, object?>
                {
                    ["webType"] = incoming.EventType,
                    ["metadata"] = incoming.Metadata.HasValue ? (object)incoming.Metadata.Value : new Dictionary<string, object?>(),
                });
                break;
            case WebMessageBridge.IncomingKind.RequestExit:
                Events.Record(EventType.UnlockRequested, EventSeverity.Info, new Dictionary<string, object?>
                {
                    ["method"] = "web-request-exit",
                    ["reason"] = incoming.Reason,
                });
                if (!Policy.AllowStudentReleaseCode && ExitOverlay == null)
                {
                    Web.Bridge.Send("EXIT_DENIED", incoming.Id, new Dictionary<string, object?>
                    {
                        ["reason"] = "Only a teacher release can end the exam.",
                    });
                }
                ShowExitOverlay("Exam locked. Ask your teacher for a release code.");
                break;
            case WebMessageBridge.IncomingKind.SubmitComplete:
                // The page may submit on its own as well as via our time-up path; record once.
                if (!_submitted)
                {
                    _submitted = true;
                    Events.RecordNow(EventType.ExamSubmitted, EventSeverity.Info, new Dictionary<string, object?> { ["reason"] = "web-submit-complete" });
                }
                ShowExitOverlay("Your exam has been submitted. Waiting for release…");
                Heartbeat.BeatNow();
                break;
        }
    }

    private void SendSessionStatus(string? id)
    {
        var session = Session;
        if (session == null) return;
        Web.Bridge.Send("SESSION_STATUS", id, new Dictionary<string, object?>
        {
            ["sessionId"] = session.SessionId,
            ["studentName"] = session.Student.Name,
            ["examTitle"] = session.Exam.Title,
            ["lockdownMode"] = LockdownMode.Wire(),
            ["remainingSeconds"] = RemainingSeconds,
            ["displayCount"] = DisplayCount,
            ["online"] = IsOnline,
        });
    }
}
