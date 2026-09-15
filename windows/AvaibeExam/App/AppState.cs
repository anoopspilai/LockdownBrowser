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

/// <summary>State of the native Exit / hold overlay (CONTRACT §7.5, §10.4, §10.5).</summary>
public sealed class ExitOverlayState : ObservableObject
{
    private string _title = "Exam locked";
    private string _message = string.Empty;
    private bool _allowReleaseCode;
    private bool _canGoBack;
    private string? _errorText;
    private bool _verifying;
    private string _statusText = "Waiting for your teacher to release the exam…";

    public string Title { get => _title; set => Set(ref _title, value); }
    public string Message { get => _message; set => Set(ref _message, value); }
    public bool AllowReleaseCode { get => _allowReleaseCode; set => Set(ref _allowReleaseCode, value); }
    /// <summary>"Back to exam" is offered only while nothing has been submitted and no release is pending (W-17).</summary>
    public bool CanGoBack { get => _canGoBack; set => Set(ref _canGoBack, value); }
    public string? ErrorText { get => _errorText; set => Set(ref _errorText, value); }
    public bool Verifying { get => _verifying; set => Set(ref _verifying, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
}

/// <summary>A JavaScript dialog waiting for the student (rendered by ScriptDialogOverlay, W-24).</summary>
public sealed class ScriptDialogState : ObservableObject
{
    private readonly TaskCompletionSource<ScriptDialogResult> _completion =
        new TaskCompletionSource<ScriptDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _inputText;

    public ScriptDialogState(ScriptDialogRequest request)
    {
        Request = request;
        _inputText = request.DefaultText ?? string.Empty;
    }

    public ScriptDialogRequest Request { get; }
    public string InputText { get => _inputText; set => Set(ref _inputText, value); }
    public Task<ScriptDialogResult> Completion => _completion.Task;

    public void Complete(bool accepted)
    {
        _completion.TrySetResult(new ScriptDialogResult(accepted, accepted ? InputText : null));
    }
}

/// <summary>
/// Single source of truth for the UI and the orchestrator of the exam flow
/// (Login → (enroll) → Preflight → Exam (locked) → Exit). Mirrors macos/App/AppState.swift.
/// Every member must be used from the UI thread; services marshal back via the Dispatcher.
///
/// Release invariant (§10.4): lockdown ends ONLY through FinishRelease(ReleaseAuthority), and a
/// ReleaseAuthority can only be built from a verified signed command, the offline failsafe, or a
/// start backout. TERMINATE submits and HOLDS; it never releases.
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
    public CommandVerifier Verifier { get; } = new CommandVerifier();
    public ExamWebView Web { get; } = new ExamWebView();

    private Window? _window;
    private PreflightRaw? _preflightRaw;
    private string? _minClientVersionFromEnroll;
    private DispatcherTimer? _countdown;
    private DispatcherTimer? _releaseWatchdog;
    private DateTimeOffset? _expiresAt;
    private bool _timeUpHandled;
    private bool _released;
    private bool _terminated;
    private ReleaseAuthority? _releaseAuthority;
    /// <summary>The page said SUBMIT_COMPLETE (telemetry only, W-06).</summary>
    private bool _webReportedSubmit;
    /// <summary>POST /submit succeeded (the only submit that counts, W-06).</summary>
    private bool _serverSubmitDone;
    private bool _submitInFlight;
    private bool _submitRetryPending;
    private bool _recoveryLoopRunning;
    private bool _autoRunPending;
    private int _lastServerRemaining = -1;
    private DateTime _webEventWindowStart = DateTime.MinValue;
    private int _webEventWindowCount;
    private int _webEventsDropped;

    // ---- Observable state ------------------------------------------------------------

    private AppScreen _screen = AppScreen.Login;
    private string _baseUrlText;
    private readonly bool _baseUrlLocked;
    private string _enrollmentTokenText = string.Empty;
    private string _studentCode;
    private string _examCode;
    private string _accessCode = string.Empty;
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
    private int _offlineSeconds;
    private int _displayCount = AvaibeExam.Security.DisplayMonitor.CurrentCount();
    private int _remainingSeconds;
    private string? _warningMessage;
    private string? _pauseMessage;
    private ExitOverlayState? _exitOverlay;
    private ScriptDialogState? _scriptDialog;
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
    /// <summary>true when IT preset the base URL in HKLM (§10.9): the login field is read-only.</summary>
    public bool BaseUrlLocked => _baseUrlLocked;
    public string EnrollmentTokenText { get => _enrollmentTokenText; set => Set(ref _enrollmentTokenText, value); }
    public string StudentCode { get => _studentCode; set => Set(ref _studentCode, value); }
    public string ExamCode { get => _examCode; set => Set(ref _examCode, value); }
    /// <summary>Only needed when the teacher set an access code on the exam. Never saved to settings.</summary>
    public string AccessCode { get => _accessCode; set => Set(ref _accessCode, value ?? string.Empty); }

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
            Raise(nameof(AllowedLinks));
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
    /// <summary>Seconds since the last successful heartbeat (0 while online) — shown in the status strip (§10.6).</summary>
    public int OfflineSeconds
    {
        get => _offlineSeconds;
        private set
        {
            if (Set(ref _offlineSeconds, value)) Raise(nameof(OfflineText));
        }
    }
    public int DisplayCount { get => _displayCount; private set => Set(ref _displayCount, value); }
    public int RemainingSeconds { get => _remainingSeconds; private set => Set(ref _remainingSeconds, value); }
    public string? WarningMessage { get => _warningMessage; private set => Set(ref _warningMessage, value); }
    public string? PauseMessage { get => _pauseMessage; private set => Set(ref _pauseMessage, value); }
    public ExitOverlayState? ExitOverlay { get => _exitOverlay; private set => Set(ref _exitOverlay, value); }
    public ScriptDialogState? ScriptDialog { get => _scriptDialog; private set => Set(ref _scriptDialog, value); }
    public string ExitReason { get => _exitReason; private set => Set(ref _exitReason, value); }

    // ---- Derived ---------------------------------------------------------------------

    public bool IsLocked => Screen == AppScreen.Exam && LockdownMode != LockdownMode.None;

    /// <summary>ADVISORY HKLM kiosk hint (§10.4). Never a proof of OS enforcement.</summary>
    public bool AssignedAccessHint => _preflightRaw?.Report.AssignedAccessHint ?? false;

    public IReadOnlyList<AllowedLink> AllowedLinks => Policy.AllowedLinks;

    /// <summary>true while the exam view shows an allowed resource rather than the exam page.</summary>
    public bool IsOnResourcePage => Screen == AppScreen.Exam && Web.IsOnResourcePage;

    public string OfflineText
    {
        get
        {
            if (OfflineSeconds <= 0) return string.Empty;
            var grace = Math.Max(0, Policy.OfflineGraceSeconds - OfflineSeconds);
            return "Offline " + FormatCountdown(OfflineSeconds) + " · auto-release in " + FormatCountdown(grace);
        }
    }

    public bool CanStartExam
    {
        get
        {
            if (Session == null || !HasServerPolicy || PreflightServerFailures.Count > 0 || IsBusy) return false;
            if (PreflightItems.Any(i => i.BlocksStart)) return false;
            if (Policy.RequireAAC && !AssignedAccessHint) return false;
            return true;
        }
    }

    public bool RequireAACButUnavailable => HasServerPolicy && Policy.RequireAAC && !AssignedAccessHint;

    /// <summary>Plain reasons why Start Exam is disabled, shown above the button. Empty while busy.</summary>
    public List<string> StartBlockers
    {
        get
        {
            var reasons = new List<string>();
            if (IsBusy) return reasons;
            if (Session == null || !HasServerPolicy)
                reasons.Add("The server has not started your exam session yet. Fix the message above, then press Re-run checks or Back.");
            if (PreflightServerFailures.Count > 0)
                reasons.Add("The server rejected the readiness check: " + string.Join(", ", PreflightServerFailures) + ".");
            var failing = PreflightItems.Where(i => i.BlocksStart).Select(i => i.Name.Replace(" (client-reported)", string.Empty)).ToList();
            if (failing.Count > 0)
                reasons.Add("Fix these required checks, then press Re-run checks: " + string.Join(", ", failing) + ".");
            if (RequireAACButUnavailable)
                reasons.Add("This exam requires a Windows Assigned Access (kiosk) setup, which this PC does not have.");
            return reasons;
        }
    }

    public string RemainingTimeText => FormatCountdown(RemainingSeconds);

    public static string FormatCountdown(int seconds)
    {
        var s = Math.Clamp(seconds, 0, 86400);
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
        var preset = Constants.ReadPresetBaseUrl();
        if (preset != null && ApiClient.ParseBaseUrl(preset) != null)
        {
            _baseUrlText = preset;
            _baseUrlLocked = true;
            Log.Info(LogCat, "Base URL preset by IT (HKLM); login field locked");
        }
        else
        {
            if (preset != null) Log.Warn(LogCat, "HKLM BaseUrl preset is not a valid https URL; ignored");
            _baseUrlLocked = false;
            string? envUrl = null;
#if DEBUG
            envUrl = Constants.Env(Constants.EnvBaseUrl);
#endif
            _baseUrlText = envUrl
                           ?? (string.IsNullOrWhiteSpace(settings.BaseUrl) ? Constants.DefaultBaseUrl : settings.BaseUrl!);
        }
        _studentCode = settings.LastStudentCode ?? string.Empty;
        _examCode = settings.LastExamCode ?? string.Empty;
        _enrollment = DeviceIdentity.LoadEnrollment();

        Api = new ApiClient(_baseUrlText);
        ApplyEnrollment(_enrollment);
        Events = new EventReporter(Api);
        Heartbeat = new HeartbeatService(Api, Dispatcher);
        Coordinator = new LockdownCoordinator(Events, Dispatcher);
        DisplayMonitor = new DisplayMonitor(Dispatcher);
        NetworkMonitor = new NetworkMonitor(Dispatcher, Api);

        PreflightItems.CollectionChanged += (_, __) => Raise(nameof(CanStartExam));
        WireCallbacks();
    }

    private void ApplyEnrollment(Enrollment? enrollment)
    {
        Api.SetDeviceId(enrollment?.DeviceId);
        Api.SetDeviceToken(enrollment?.DeviceToken);
        Verifier.SetPinnedKey(enrollment?.ServerPublicKey, enrollment?.KeyId);
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
        Heartbeat.OnServerTime = t => Verifier.UpdateServerTime(t);
        Heartbeat.OnResult = ok =>
        {
            BackendReachable = ok;
            if (ok) OfflineSeconds = 0;
        };
        Heartbeat.OnOffline = HandleHeartbeatOffline;
        Heartbeat.OnBackOnline = () =>
        {
            OfflineSeconds = 0;
            if (_submitRetryPending && !_serverSubmitDone) Fire(() => RetrySubmitAsync("reconnect"), "RetrySubmit");
            Fire(() => Events.FlushAsync(), "Flush after reconnect");
        };

        Coordinator.OnWarning = ShowWarning;
        Coordinator.OnDisplayChange = () => DisplayMonitor.Poke();

        DisplayMonitor.OnChange = HandleDisplayChange;
        NetworkMonitor.OnChange = HandleNetworkChange;

        Web.Bridge.OnMessage = HandleBridge;
        Web.OnBlockedNavigation = (url, reason) =>
            Events.Record(EventType.BlockedNavigation, EventSeverity.Medium,
                new Dictionary<string, object?> { ["url"] = url, ["reason"] = reason });
        Web.OnForeignMessage = origin =>
            Events.Record(EventType.BlockedNavigation, EventSeverity.Medium,
                new Dictionary<string, object?> { ["url"] = origin, ["reason"] = "bridge-foreign-origin" });
        Web.OnNavigated = () => Raise(nameof(IsOnResourcePage));
        Web.ScriptDialogHandler = ShowScriptDialogAsync;
        Web.OnProcessFailed = (kind, fatal) =>
        {
            Events.RecordNow(EventType.LockdownInterrupted, EventSeverity.High,
                new Dictionary<string, object?> { ["component"] = "webview2", ["kind"] = kind, ["fatal"] = fatal });
            if (fatal)
            {
                ShowExitOverlay("Exam locked", "The exam page stopped responding (" + kind + "). The exam stays locked until your teacher releases it.", canGoBack: false);
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

    // ---- Dev-only auto run (DEBUG builds only, §10.9) --------------------------------

    /// <summary>
    /// AVAIBE_AUTO_RUN=1: fill the login form from AVAIBE_AUTO_TOKEN / _STUDENT / _EXAM, continue,
    /// and press Start Exam as soon as the preflight allows it. AVAIBE_AUTO_EXIT_AFTER=&lt;s&gt;
    /// force-exits the process after that many seconds (safety net for unattended tests).
    /// Compiled out of Release builds entirely.
    /// </summary>
    public void AutoRunIfRequested()
    {
#if DEBUG
        if (!Constants.EnvIsOne(Constants.EnvAutoRun)) return;
        Log.Warn(LogCat, "AUTO RUN enabled (debug build only)");
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
#endif
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
            LoginError = ApiClient.BaseUrlRule;
            return;
        }
        var student = (StudentCode ?? string.Empty).Trim();
        var exam = (ExamCode ?? string.Empty).Trim().ToUpperInvariant();
        // Field-specific messages: a combined "both are required" message would wrongly tell a
        // student their code is missing when only the OTHER field was left blank or too long.
        if (student.Length == 0 && exam.Length == 0)
        {
            LoginError = "Student code and exam code are required.";
            return;
        }
        if (student.Length == 0)
        {
            LoginError = "Student code is required.";
            return;
        }
        if (exam.Length == 0)
        {
            LoginError = "Exam code is required.";
            return;
        }
        if (student.Length > 64)
        {
            LoginError = "Student code must be 64 characters or fewer.";
            return;
        }
        if (exam.Length > 64)
        {
            LoginError = "Exam code must be 64 characters or fewer.";
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

    /// <summary>§10.1 enrollment: pins the server signing key (TOFU) and the device token.</summary>
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
        var deviceId = (response.DeviceId ?? string.Empty).Trim();
        var deviceToken = (response.DeviceToken ?? string.Empty).Trim();
        var spki = (response.ServerPublicKey ?? string.Empty).Trim();
        var keyId = (response.KeyId ?? string.Empty).Trim();
        if (deviceId.Length == 0 || deviceToken.Length == 0)
        {
            throw new ApiException("ENROLL_INCOMPLETE", "The server did not return a device id and token.", 0, null);
        }
        if (spki.Length == 0)
        {
            throw new ApiException("ENROLL_NO_KEY", "The server did not send its signing key (protocol v2 is required).", 0, null);
        }
        if (!CommandVerifier.IsValidP256Spki(spki))
        {
            throw new ApiException("ENROLL_BAD_KEY", "The server's signing key is not a valid ECDSA P-256 key.", 0, null);
        }
        if (Constants.PinnedServerPublicKey.Length > 0 && !string.Equals(Constants.PinnedServerPublicKey, spki, StringComparison.Ordinal))
        {
            Log.Error("security", "Server signing key differs from the compile-time pin; enrollment refused");
            throw new ApiException("SERVER_KEY_MISMATCH", "This server's signing key does not match the key built into this client. Contact your administrator.", 0, null);
        }
        Verifier.UpdateServerTime(response.ServerTime);

        var enrollment = new Enrollment
        {
            DeviceId = deviceId,
            DeviceToken = deviceToken,
            Mode = string.IsNullOrWhiteSpace(response.Mode) ? "byod" : response.Mode,
            ServerPublicKey = spki,
            KeyId = keyId,
            EnrolledAt = Iso8601.Now(),
        };
        DeviceIdentity.SaveEnrollment(enrollment);
        Enrollment = enrollment;
        _minClientVersionFromEnroll = response.MinClientVersion;
        ApplyEnrollment(enrollment);
        EnrollmentTokenText = string.Empty;
        Log.Info(LogCat, "Enrolled as " + enrollment.DeviceId + " (" + enrollment.Mode + ", keyId=" + keyId + ")");
    }

    public void ResetEnrollment()
    {
        if (IsLocked || IsBusy) return;
        DeviceIdentity.ResetEnrollment();
        Enrollment = null;
        ApplyEnrollment(null);
    }

    private async Task RunPreflightAndStartSessionAsync(string student, string exam)
    {
        PreflightError = null;
        PreflightServerFailures = new List<string>();
        var raw = await Preflight.RunAsync(Api);
        _preflightRaw = raw;
        DisplayCount = raw.Report.DisplayCount;
        ReplacePreflightItems(Preflight.Items(raw, Policy, _minClientVersionFromEnroll));
        Raise(nameof(AssignedAccessHint));
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
            AccessCode = SessionStartRequest.NormalizeAccessCode(this.AccessCode),
        };
        try
        {
            var response = await Api.StartSessionAsync(request);
            var problem = response.Normalize();   // W-15
            if (problem != null)
            {
                throw new ApiException("SESSION_INVALID", problem, 0, null);
            }
            Verifier.UpdateServerTime(response.ServerTime);
            var policy = response.PolicyOrDefault;
            Session = response;
            Policy = policy;
            HasServerPolicy = true;
            Api.SetSessionToken(response.SessionToken);
            AccessCode = string.Empty;   // accepted; do not keep it longer than needed
            ReplacePreflightItems(Preflight.Items(raw, Policy, _minClientVersionFromEnroll));
            _expiresAt = response.ExpiresAtDate;
            RemainingSeconds = ClampedRemaining(_expiresAt);
            Log.Info(LogCat, "Session started (" + response.ExamOrEmpty.Code + "); requireAAC=" + policy.RequireAAC +
                             " releaseOnSubmit=" + policy.ReleaseOnSubmit + " offlineGrace=" + policy.OfflineGraceSeconds + "s links=" + policy.AllowedLinks.Count);
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
            else if (ex.Code == "SESSION_ALREADY_ACTIVE")
            {
                Events.Record(EventType.SessionConflict, EventSeverity.High, new Dictionary<string, object?> { ["examCode"] = exam });
                PreflightError = "Another session for this exam is already active. Ask your teacher to end it first.";
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
                Raise(nameof(AssignedAccessHint));
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
            _terminated = false;
            _releaseAuthority = null;
            _webReportedSubmit = false;
            _serverSubmitDone = false;
            _submitInFlight = false;
            _submitRetryPending = false;
            _timeUpHandled = false;
            _lastServerRemaining = -1;
            OfflineSeconds = 0;

            Events.Start(session.SessionId, Policy.EventFlushIntervalSeconds);

            // W-30: validate the exam URL BEFORE anything is locked.
            string urlReason = "unparsable";
            if (!Uri.TryCreate(session.ExamUrl, UriKind.Absolute, out var examUrl) ||
                !ExamWebView.IsExamUrlAcceptable(examUrl, Policy, out urlReason))
            {
                Events.RecordNow(EventType.LockdownFailed, EventSeverity.High,
                    new Dictionary<string, object?> { ["reason"] = "exam-url-refused: " + urlReason });
                PreflightError = "The exam URL from the server is not allowed by the policy (" + urlReason + "). Ask your teacher.";
                return;
            }

            Events.Record(EventType.SessionStart, EventSeverity.Info, new Dictionary<string, object?>
            {
                ["examCode"] = session.ExamOrEmpty.Code,
                ["clientVersion"] = Constants.ClientVersion,
                ["platform"] = Constants.Platform,
                ["assignedAccessHint"] = AssignedAccessHint,
                ["virtualMachine"] = _preflightRaw?.Extras.VirtualMachine ?? false,
                ["debuggerAttached"] = _preflightRaw?.Extras.DebuggerAttached ?? false,
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
                    ? "This exam requires a Windows Assigned Access (kiosk) configuration, which was not detected on this device (client-reported)."
                    : "Lockdown could not be engaged.";
                await Events.FlushAsync();
                return;
            }
            LockdownMode = mode;

            Web.CreateControl(Policy, mode, session.SessionId);
            Screen = AppScreen.Exam;     // ExamView hosts the control so it gets an HWND

            try
            {
                await Web.InitializeAsync();
            }
            catch (Exception ex)
            {
                Log.Error(LogCat, "WebView2 initialization failed", ex);
                BackOutOfStart("webview2: " + ex.GetType().Name, "The exam browser (WebView2) could not start: " + ex.Message);
                return;
            }

            if (!Web.Load(examUrl))
            {
                BackOutOfStart("exam-url-refused", "The exam page address was refused by the policy. Ask your teacher.");
                return;
            }
            Web.Bridge.Send("LOCKDOWN_STATE", null, LockdownStatePayload());

            DisplayMonitor.Start();
            NetworkMonitor.Start();
            Heartbeat.Start(session.SessionId, Policy.HeartbeatIntervalSeconds, session.NextBeatInSeconds);
            StartCountdown();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Start failed after lockdown engaged: release (start backout) and return to the preflight screen.</summary>
    private void BackOutOfStart(string reason, string userText)
    {
        Events.RecordNow(EventType.LockdownFailed, EventSeverity.High, new Dictionary<string, object?> { ["reason"] = reason });
        try
        {
            Coordinator.Release(ReleaseAuthority.StartBackout(reason));
        }
        finally
        {
            LockdownMode = LockdownMode.None;
            Web.Teardown();
            Screen = AppScreen.Preflight;
            PreflightError = userText;
        }
    }

    // ---- Countdown (W-14) ------------------------------------------------------------

    private static int ClampedRemaining(DateTimeOffset? expiresAt)
    {
        if (!expiresAt.HasValue) return 0;
        var seconds = (expiresAt.Value - DateTimeOffset.UtcNow).TotalSeconds;
        if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return 0;
        return (int)Math.Clamp(Math.Round(seconds), 0, 86400);
    }

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
        var remaining = ClampedRemaining(_expiresAt);
        if (remaining != RemainingSeconds)
        {
            RemainingSeconds = remaining;
            Raise(nameof(RemainingTimeText));
        }
        if (remaining == 0 && !_timeUpHandled && Screen == AppScreen.Exam && !_terminated)
        {
            _timeUpHandled = true;
            TimeUp();
        }
    }

    /// <summary>
    /// Adopts the server's remaining time. Clamped 0..86400; the countdown may be extended freely
    /// but is never shortened by more than one heartbeat interval per beat, so a single bad value
    /// cannot end the exam instantly (W-14).
    /// </summary>
    private void ResyncCountdown(int serverRemaining)
    {
        var clamped = Math.Clamp(serverRemaining, 0, 86400);
        _lastServerRemaining = clamped;
        var local = ClampedRemaining(_expiresAt);
        var maxDrop = Math.Max(Policy.HeartbeatIntervalSeconds, 2);
        var target = clamped;
        if (_expiresAt.HasValue && clamped < local - maxDrop)
        {
            target = local - maxDrop;
            Log.Warn(LogCat, "Server remaining " + clamped + "s is far below local " + local + "s; shortening by at most " + maxDrop + "s this beat");
        }
        var serverExpiry = DateTimeOffset.UtcNow.AddSeconds(target);
        if (_expiresAt.HasValue && Math.Abs((_expiresAt.Value - serverExpiry).TotalSeconds) < 3) return;
        _expiresAt = serverExpiry;
        TickCountdown();
    }

    // ---- Submit (§10.5, W-06) --------------------------------------------------------

    private void TimeUp()
    {
        Log.Warn(LogCat, "Exam time is up; submitting");
        Web.Bridge.Send("WARNING", null, new Dictionary<string, object?> { ["message"] = "Time is up. Your exam is being submitted." });
        ShowExitOverlay("Time is up", "Time is up. Your exam is being submitted…", canGoBack: false);
        Fire(async () =>
        {
            // The native client ALWAYS posts /submit at time-up, regardless of any page message.
            var response = await SubmitExamAsync("time-up");
            AfterSubmit(response, "Time is up. Your exam has been submitted.");
        }, "TimeUp");
    }

    /// <summary>POST /submit once; returns the response, or null when it failed (a retry is scheduled).</summary>
    private async Task<SubmitResponse?> SubmitExamAsync(string reason)
    {
        var session = Session;
        if (session == null) return null;
        if (_serverSubmitDone || _submitInFlight) return null;
        _submitInFlight = true;
        try
        {
            var response = await Api.SubmitAsync(session.SessionId);
            _serverSubmitDone = true;
            _submitRetryPending = false;
            Events.RecordNow(EventType.ExamSubmitted, EventSeverity.Info, new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["submittedAt"] = response.SubmittedAt ?? string.Empty,
                ["release"] = response.Release ?? "teacher",
                ["webReported"] = _webReportedSubmit,
            });
            return response;
        }
        catch (Exception ex)
        {
            _submitRetryPending = true;
            Log.Error(LogCat, "Submit failed (" + reason + "): " + ex.GetType().Name);
            Events.RecordNow(EventType.ExamSubmitted, EventSeverity.Medium, new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["error"] = (ex as ApiException)?.Code ?? ex.GetType().Name,
                ["pendingRetry"] = true,
            });
            return null;
        }
        finally
        {
            _submitInFlight = false;
        }
    }

    private async Task RetrySubmitAsync(string reason)
    {
        var response = await SubmitExamAsync("retry-" + reason);
        if (response != null && ExitOverlay != null)
        {
            AfterSubmit(response, "Your exam has been submitted.");
        }
    }

    /// <summary>Updates the hold overlay after a submit attempt (§10.5 wording).</summary>
    private void AfterSubmit(SubmitResponse? response, string doneText)
    {
        if (_released) return;
        if (response == null)
        {
            ShowExitOverlay(ExitOverlay?.Title ?? "Exam locked",
                "Your exam could not be sent to the server yet. It will be retried automatically; stay on this screen.",
                canGoBack: false, statusText: "Retrying the submission… Waiting for your teacher to release the exam.");
            Heartbeat.BeatNow();
            return;
        }
        if (response.ReleaseIsAuto)
        {
            ShowExitOverlay("Exam submitted", doneText, canGoBack: false, statusText: "Submitted. Waiting for the release…");
        }
        else
        {
            ShowExitOverlay("Exam submitted", "Submitted. Waiting for your teacher to release you.", canGoBack: false,
                statusText: "Your teacher will release the exam. Stay on this screen.");
        }
        Heartbeat.BeatNow();
    }

    // ---- Heartbeat commands (§10.4) --------------------------------------------------

    private VerifiedAuthorization? VerifyOrReject(CommandAuthorization? auth, string type, string source)
    {
        var session = Session;
        var enrollment = Enrollment;
        var verified = Verifier.Verify(auth, type, session?.SessionId ?? string.Empty, enrollment?.DeviceId ?? string.Empty, out var reason);
        if (verified == null)
        {
            Log.Error("security", type + " command from " + source + " REJECTED: " + reason);
            Events.RecordNow(EventType.CommandRejected, EventSeverity.High, new Dictionary<string, object?>
            {
                ["type"] = type,
                ["source"] = source,
                ["reason"] = reason,
                ["keyId"] = auth?.KeyId ?? string.Empty,
            });
        }
        return verified;
    }

    private void HandleCommand(HeartbeatCommand cmd)
    {
        if (Screen != AppScreen.Exam || _released) return;
        switch (cmd.Type)
        {
            case HeartbeatCommandType.Release:
                {
                    var verified = VerifyOrReject(cmd.Authorization, "RELEASE", "heartbeat");
                    if (verified == null) return;
                    Events.Record(EventType.UnlockCompleted, EventSeverity.Info, new Dictionary<string, object?>
                    {
                        ["source"] = "heartbeat",
                        ["keyId"] = verified.KeyId,
                    });
                    FinishRelease(cmd.Reason ?? "Released by teacher", ReleaseAuthority.VerifiedCommand(verified, "heartbeat"));
                    break;
                }
            case HeartbeatCommandType.Terminate:
                {
                    var verified = VerifyOrReject(cmd.Authorization, "TERMINATE", "heartbeat");
                    if (verified == null) return;
                    Terminate("Your session was terminated by your teacher" + (string.IsNullOrWhiteSpace(cmd.Reason) ? "." : ": " + cmd.Reason), "teacher");
                    break;
                }
            case HeartbeatCommandType.Warn:
                ShowWarning(cmd.Message ?? cmd.Reason ?? "Warning from your teacher");
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// §10.4 TERMINATE (teacher or external display): submit, then HOLD a locked overlay.
    /// Lockdown is NOT released here — only a verified RELEASE (heartbeat or release code) or the
    /// offline failsafe can do that (W-03).
    /// </summary>
    private void Terminate(string message, string source)
    {
        if (_released || _terminated) return;
        _terminated = true;
        _timeUpHandled = true; // time-up must not double-submit / repaint the overlay
        Log.Warn("security", "Session terminated (" + source + "); holding lockdown until a verified release");
        Events.RecordNow(EventType.SessionEnd, EventSeverity.High,
            new Dictionary<string, object?> { ["reason"] = "terminated", ["source"] = source, ["held"] = true });
        Web.Bridge.Send("WARNING", null, new Dictionary<string, object?> { ["message"] = "Session terminated. Wait for your teacher." });
        WarningMessage = null;
        PauseMessage = null;
        ShowExitOverlay("Session terminated", message + "\n\nSession terminated. Wait for your teacher.", canGoBack: false,
            statusText: "The exam stays locked until your teacher releases it.");
        Fire(async () =>
        {
            await SubmitExamAsync("terminated-" + source);
            Heartbeat.BeatNow();
        }, "TerminateSubmit");
    }

    // ---- Offline-grace failsafe (§10.6, W-08) ----------------------------------------

    private void HandleHeartbeatOffline(int consecutiveFailures, int offlineSeconds)
    {
        BackendReachable = false;
        OfflineSeconds = offlineSeconds;
        if (Screen != AppScreen.Exam || _released) return;
        var grace = Policy.OfflineGraceSeconds;
        if (offlineSeconds < grace) return;

        Log.Error("security", "Offline for " + offlineSeconds + "s (>= grace " + grace + "s, " + consecutiveFailures + " failed beats); releasing via the offline failsafe");
        Events.Record(EventType.SessionEnd, EventSeverity.High, new Dictionary<string, object?>
        {
            ["reason"] = "offline-grace",
            ["offlineSeconds"] = offlineSeconds,
            ["consecutiveFailures"] = consecutiveFailures,
        });
        Events.Record(EventType.OfflineGraceRelease, EventSeverity.High, new Dictionary<string, object?>
        {
            ["offlineSeconds"] = offlineSeconds,
            ["graceSeconds"] = grace,
            ["submitted"] = _serverSubmitDone,
        });
        if (!_serverSubmitDone) _submitRetryPending = true;
        FinishRelease("Released by the offline failsafe: no server contact for " + FormatCountdown(offlineSeconds) +
                      ". Your work will be sent when the connection returns.", ReleaseAuthority.OfflineGrace(offlineSeconds));
        StartOfflineRecoveryLoop();
    }

    /// <summary>After an offline-grace release: keep trying to submit and flush events until the server returns.</summary>
    private void StartOfflineRecoveryLoop()
    {
        if (_recoveryLoopRunning) return;
        _recoveryLoopRunning = true;
        Fire(async () =>
        {
            var deadline = DateTime.UtcNow.AddMinutes(30);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                if (Screen != AppScreen.Exit) break;
                if (_submitRetryPending && !_serverSubmitDone)
                {
                    await SubmitExamAsync("retry-offline-recovery");
                }
                var flushed = await Events.FlushAsync();
                if (!_submitRetryPending && flushed)
                {
                    Log.Info(LogCat, "Offline recovery complete: submission and events delivered");
                    break;
                }
            }
            _recoveryLoopRunning = false;
        }, "OfflineRecovery");
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

    public void ShowExitOverlay(string title, string message, bool canGoBack, string? statusText = null)
    {
        var goBack = canGoBack && !_serverSubmitDone && !_webReportedSubmit && !_terminated && !_timeUpHandled;
        if (ExitOverlay == null)
        {
            ExitOverlay = new ExitOverlayState
            {
                Title = title,
                Message = message,
                AllowReleaseCode = Policy.AllowStudentReleaseCode,
                CanGoBack = goBack,
                StatusText = statusText ?? "Waiting for your teacher to release the exam…",
            };
        }
        else
        {
            ExitOverlay.Title = title;
            ExitOverlay.Message = message;
            ExitOverlay.CanGoBack = goBack;
            if (statusText != null) ExitOverlay.StatusText = statusText;
        }
    }

    /// <summary>"Back to exam" on the exit overlay (W-17): only while nothing is submitted/terminated/pending.</summary>
    public void DismissExitOverlay()
    {
        var state = ExitOverlay;
        if (state == null || !state.CanGoBack || state.Verifying) return;
        if (_serverSubmitDone || _webReportedSubmit || _terminated || _timeUpHandled || _released) return;
        Log.Info(LogCat, "Exit overlay dismissed by the student (back to exam)");
        ExitOverlay = null;
    }

    /// <summary>W-24: renders a page dialog as a WPF overlay; one at a time, extra dialogs are cancelled.</summary>
    private Task<ScriptDialogResult> ShowScriptDialogAsync(ScriptDialogRequest request)
    {
        if (Screen != AppScreen.Exam || ScriptDialog != null || ExitOverlay != null || PauseMessage != null)
        {
            return Task.FromResult(ScriptDialogResult.Cancelled);
        }
        var state = new ScriptDialogState(request);
        ScriptDialog = state;
        return AwaitScriptDialogAsync(state);
    }

    private async Task<ScriptDialogResult> AwaitScriptDialogAsync(ScriptDialogState state)
    {
        try
        {
            return await state.Completion;
        }
        finally
        {
            if (ReferenceEquals(ScriptDialog, state)) ScriptDialog = null;
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
            var verified = VerifyOrReject(response.Authorization, "RELEASE", "release-code");
            if (verified == null)
            {
                throw new ApiException("RELEASE_NOT_VERIFIED", "The server's release could not be verified. Ask your teacher to release you from the console.", 0, null);
            }
            Events.Record(EventType.UnlockCompleted, EventSeverity.Info, new Dictionary<string, object?>
            {
                ["source"] = "release-code",
                ["keyId"] = verified.KeyId,
            });
            FinishRelease("Release code accepted", ReleaseAuthority.VerifiedCommand(verified, "release-code"));
        }
        catch (Exception ex)
        {
            var apiEx = ex as ApiException;
            var errorCode = apiEx?.Code ?? "ERROR";
            var text = apiEx?.DisplayText ?? ex.Message;
            // W-26: the code itself is never logged or reported.
            Events.RecordNow(EventType.UnlockDenied, EventSeverity.Medium, new Dictionary<string, object?> { ["code"] = errorCode });
            if (errorCode == "RELEASE_CODE_LOCKED")
            {
                Events.RecordNow(EventType.ReleaseCodeLocked, EventSeverity.High, null);
                state.AllowReleaseCode = false;
                text = "Too many wrong codes. The release-code path is locked; wait for your teacher.";
            }
            state.Verifying = false;
            state.ErrorText = text;
        }
    }

    // ---- Release / exit (W-09) -------------------------------------------------------

    /// <summary>
    /// The ONLY exit from lockdown. Order matters: the kiosk controls are reversed FIRST inside
    /// try/finally (so a failure elsewhere can never leave the student locked), then flags and
    /// services are updated, then a 15 s watchdog verifies the controls really are gone.
    /// </summary>
    private void FinishRelease(string reason, ReleaseAuthority authority)
    {
        if (authority == null) throw new ArgumentNullException(nameof(authority));
        if (_released) return;
        _released = true;
        _releaseAuthority = authority;
        Log.Info(LogCat, "Finishing release (" + authority.Wire() + "): " + reason);
        try
        {
            try { Web.Bridge.Send("LOCKDOWN_STATE", null, new Dictionary<string, object?> { ["lockdownMode"] = "none", ["engaged"] = false }); }
            catch (Exception ex) { Log.Warn(LogCat, "LOCKDOWN_STATE send failed: " + ex.GetType().Name); }
            Coordinator.Release(authority);
        }
        finally
        {
            LockdownMode = LockdownMode.None;
            try { Heartbeat.Stop(); } catch (Exception ex) { Log.Warn(LogCat, "Heartbeat stop failed: " + ex.GetType().Name); }
            StopCountdown();
            try { DisplayMonitor.Stop(); } catch (Exception ex) { Log.Warn(LogCat, "DisplayMonitor stop failed: " + ex.GetType().Name); }
            try { NetworkMonitor.Stop(); } catch (Exception ex) { Log.Warn(LogCat, "NetworkMonitor stop failed: " + ex.GetType().Name); }
            Events.Record(EventType.SessionEnd, EventSeverity.Info, new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["authority"] = authority.Wire(),
                ["submitted"] = _serverSubmitDone,
            });

            ExitReason = reason;
            ExitOverlay = null;
            WarningMessage = null;
            PauseMessage = null;
            var dialog = ScriptDialog;
            ScriptDialog = null;
            dialog?.Complete(false);
            Screen = AppScreen.Exit;

            Fire(ClearAndTeardownWebAsync, "Web teardown");
            if (authority.Type != ReleaseAuthority.Kind.OfflineGrace)
            {
                Fire(() => Events.StopAsync(), "Events.Stop");
            }
            StartReleaseWatchdog();
        }
    }

    private async Task ClearAndTeardownWebAsync()
    {
        try
        {
            await Web.ClearBrowsingDataAsync();
        }
        finally
        {
            Web.Teardown();
        }
    }

    /// <summary>W-09: 15 s after release, if any kiosk control is still engaged, release again and log loudly.</summary>
    private void StartReleaseWatchdog()
    {
        StopReleaseWatchdog();
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(Constants.ReleaseWatchdogSeconds) };
        timer.Tick += (_, __) =>
        {
            timer.Stop();
            _releaseWatchdog = null;
            var authority = _releaseAuthority;
            if (!_released || authority == null) return;
            if (Coordinator.IsEngaged || Coordinator.Kiosk.IsEngaged || Coordinator.Processes.IsRunning || LockdownMode != LockdownMode.None)
            {
                Log.Error("security", "Release watchdog: kiosk controls still engaged after release; releasing again");
                Events.RecordNow(EventType.LockdownInterrupted, EventSeverity.High,
                    new Dictionary<string, object?> { ["component"] = "release-watchdog", ["action"] = "re-release" });
                try { Coordinator.Release(authority); } catch (Exception ex) { Log.Error(LogCat, "Watchdog re-release failed", ex); }
                LockdownMode = LockdownMode.None;
                if (Screen != AppScreen.Exit) Screen = AppScreen.Exit;
            }
            else
            {
                Log.Info(LogCat, "Release watchdog: clean");
            }
        };
        _releaseWatchdog = timer;
        timer.Start();
    }

    private void StopReleaseWatchdog()
    {
        if (_releaseWatchdog == null) return;
        _releaseWatchdog.Stop();
        _releaseWatchdog = null;
    }

    /// <summary>Only reachable from the Exit screen.</summary>
    public void Quit()
    {
        if (IsLocked) return;
        Log.Info(LogCat, "Quit requested");
        Fire(async () =>
        {
            // Last chance to deliver queued telemetry (bounded).
            var flush = Events.StopAsync();
            await Task.WhenAny(flush, Task.Delay(TimeSpan.FromSeconds(3)));
            System.Windows.Application.Current.Shutdown();
        }, "Quit");
    }

    /// <summary>MainWindow reports a close/quit attempt while locked.</summary>
    public void ReportBlockedQuit()
    {
        Events.RecordNow(EventType.BlockedShortcut, EventSeverity.Medium, new Dictionary<string, object?> { ["shortcut"] = "quit", ["injected"] = false });
    }

    // ---- Allowed links (§10.3) -------------------------------------------------------

    public void OpenAllowedLink(AllowedLink link)
    {
        if (Screen != AppScreen.Exam || _released || ExitOverlay != null || PauseMessage != null || link == null) return;
        if (!Policy.AllowedLinks.Contains(link)) return; // only entries from the current policy
        Events.Record(EventType.WebEvent, EventSeverity.Info, new Dictionary<string, object?>
        {
            ["webType"] = "RESOURCE_OPENED",
            ["label"] = link.Label,
        });
        Web.NavigateToAllowedLink(link);
        Raise(nameof(IsOnResourcePage));
    }

    public void BackToExam()
    {
        if (Screen != AppScreen.Exam || _released) return;
        Web.BackToExam();
        Raise(nameof(IsOnResourcePage));
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
                    // W-03: same hold behaviour as a teacher TERMINATE — never a release.
                    Terminate("Your session was terminated because an external display was connected.", "external-display");
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

    /// <summary>§10.7: at most 20 REPORT_EVENT per second; the rest are dropped and counted (W-25).</summary>
    private bool AllowWebEvent()
    {
        var now = DateTime.UtcNow;
        if ((now - _webEventWindowStart).TotalSeconds >= 1)
        {
            if (_webEventsDropped > 0)
            {
                Log.Warn(LogCat, "bridge: " + _webEventsDropped + " REPORT_EVENT message(s) dropped (rate limit)");
                _webEventsDropped = 0;
            }
            _webEventWindowStart = now;
            _webEventWindowCount = 0;
        }
        if (_webEventWindowCount >= Constants.MaxWebEventsPerSecond)
        {
            _webEventsDropped++;
            return false;
        }
        _webEventWindowCount++;
        return true;
    }

    private void HandleBridge(WebMessageBridge.Incoming incoming)
    {
        if (Screen != AppScreen.Exam) return;
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
                if (!AllowWebEvent()) break;
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
                if (!Policy.AllowStudentReleaseCode)
                {
                    // W-17: deny and stop; a teacher RELEASE over the heartbeat is the only way out.
                    Web.Bridge.Send("EXIT_DENIED", incoming.Id, new Dictionary<string, object?>
                    {
                        ["reason"] = "Only a teacher release can end the exam.",
                    });
                    return;
                }
                ShowExitOverlay("Exam locked", "Exam locked. Ask your teacher for a release code.", canGoBack: true);
                break;
            case WebMessageBridge.IncomingKind.SubmitComplete:
                // W-06 / §10.5: telemetry only. The native /submit call is what counts.
                if (!_webReportedSubmit)
                {
                    _webReportedSubmit = true;
                    Events.RecordNow(EventType.ExamSubmitted, EventSeverity.Info,
                        new Dictionary<string, object?> { ["reason"] = "web-submit-complete", ["source"] = "page", ["telemetryOnly"] = true });
                }
                ShowExitOverlay("Exam submitted", "Your exam has been submitted. Waiting for release…", canGoBack: false,
                    statusText: "Confirming the submission with the server…");
                Fire(async () =>
                {
                    var response = await SubmitExamAsync("web-submit-complete");
                    AfterSubmit(response, "Your exam has been submitted.");
                }, "SubmitAfterPage");
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
            ["studentName"] = session.StudentOrEmpty.Name,
            ["examTitle"] = session.ExamOrEmpty.Title,
            ["lockdownMode"] = LockdownMode.Wire(),
            ["remainingSeconds"] = RemainingSeconds,
            ["displayCount"] = DisplayCount,
            ["online"] = IsOnline,
        });
    }
}
