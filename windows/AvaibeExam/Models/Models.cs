using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using AvaibeExam.Core;

namespace AvaibeExam.Models;

// ======================================================================================
// Lockdown mode (CONTRACT §6 / §9.3)
// ======================================================================================

[JsonConverter(typeof(LockdownModeConverter))]
public enum LockdownMode
{
    None,
    AssignedAccess,
    KioskFallback,
}

public sealed class LockdownModeConverter : MappedEnumConverter<LockdownMode>
{
    public static readonly LockdownModeConverter Instance = new LockdownModeConverter();

    private static readonly Dictionary<LockdownMode, string> Wire = new Dictionary<LockdownMode, string>
    {
        [LockdownMode.None] = "none",
        [LockdownMode.AssignedAccess] = "assigned-access",
        [LockdownMode.KioskFallback] = "kiosk-fallback",
    };

    protected override IReadOnlyDictionary<LockdownMode, string> Map => Wire;
    protected override LockdownMode Fallback => LockdownMode.None;
}

public static class LockdownModeExtensions
{
    public static string Wire(this LockdownMode mode) => LockdownModeConverter.Instance.ToWire(mode);

    public static string DisplayName(this LockdownMode mode)
    {
        switch (mode)
        {
            case LockdownMode.AssignedAccess: return "Assigned Access (client-reported)";
            case LockdownMode.KioskFallback: return "Kiosk fallback";
            default: return "Not locked";
        }
    }
}

// ======================================================================================
// Enrollment (POST /api/v1/devices/enroll) — CONTRACT §10.1
// ======================================================================================

public sealed class EnrollRequest
{
    [JsonPropertyName("enrollmentToken")] public string EnrollmentToken { get; set; } = string.Empty;
    [JsonPropertyName("platform")] public string Platform { get; set; } = Constants.Platform;
    [JsonPropertyName("osVersion")] public string OsVersion { get; set; } = string.Empty;
    [JsonPropertyName("clientVersion")] public string ClientVersion { get; set; } = Constants.ClientVersion;
    [JsonPropertyName("hardwareId")] public string HardwareId { get; set; } = string.Empty;
    [JsonPropertyName("deviceName")] public string DeviceName { get; set; } = string.Empty;
}

public sealed class EnrollResponse
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("deviceToken")] public string DeviceToken { get; set; } = string.Empty;
    [JsonPropertyName("mode")] public string Mode { get; set; } = "byod";
    [JsonPropertyName("minClientVersion")] public string? MinClientVersion { get; set; }
    /// <summary>base64 SPKI DER of the server's ECDSA P-256 signing key (§10.1). Pinned at enrollment.</summary>
    [JsonPropertyName("serverPublicKey")] public string? ServerPublicKey { get; set; }
    [JsonPropertyName("keyId")] public string? KeyId { get; set; }
    [JsonPropertyName("serverTime")] public string? ServerTime { get; set; }
}

/// <summary>Persisted enrollment (DPAPI-protected file, CONTRACT §9.1 / §10.1). Contains the pinned server key.</summary>
public sealed class Enrollment
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("deviceToken")] public string DeviceToken { get; set; } = string.Empty;
    [JsonPropertyName("mode")] public string Mode { get; set; } = "byod";
    /// <summary>Trust-on-first-use pinned signing key (base64 SPKI). Blank => pre-v2 enrollment, must re-enroll.</summary>
    [JsonPropertyName("serverPublicKey")] public string ServerPublicKey { get; set; } = string.Empty;
    [JsonPropertyName("keyId")] public string KeyId { get; set; } = string.Empty;
    [JsonPropertyName("enrolledAt")] public string EnrolledAt { get; set; } = string.Empty;
}

// ======================================================================================
// Preflight (CONTRACT §3 + §9.2 + §10.4)
// ======================================================================================

[JsonConverter(typeof(AuthorizationStateConverter))]
public enum AuthorizationState
{
    NotDetermined,
    Authorized,
    Denied,
    Restricted,
}

public sealed class AuthorizationStateConverter : MappedEnumConverter<AuthorizationState>
{
    public static readonly AuthorizationStateConverter Instance = new AuthorizationStateConverter();

    private static readonly Dictionary<AuthorizationState, string> Wire = new Dictionary<AuthorizationState, string>
    {
        [AuthorizationState.NotDetermined] = "notDetermined",
        [AuthorizationState.Authorized] = "authorized",
        [AuthorizationState.Denied] = "denied",
        [AuthorizationState.Restricted] = "restricted",
    };

    protected override IReadOnlyDictionary<AuthorizationState, string> Map => Wire;
    protected override AuthorizationState Fallback => AuthorizationState.NotDetermined;
}

/// <summary>Wire format sent in POST /api/v1/sessions. Everything here is CLIENT-REPORTED (§10.2).</summary>
public sealed class PreflightReport
{
    [JsonPropertyName("osVersion")] public string OsVersion { get; set; } = string.Empty;
    /// <summary>Windows: Secure Boot enabled (CONTRACT §9.2).</summary>
    [JsonPropertyName("sipEnabled")] public bool SipEnabled { get; set; }
    [JsonPropertyName("mdmEnrolled")] public bool MdmEnrolled { get; set; }
    /// <summary>"admin" | "standard"</summary>
    [JsonPropertyName("accountType")] public string AccountType { get; set; } = "admin";
    [JsonPropertyName("displayCount")] public int DisplayCount { get; set; } = 1;
    [JsonPropertyName("screenSharingActive")] public bool ScreenSharingActive { get; set; }
    /// <summary>
    /// Windows: true ONLY when a machine-wide (HKLM) Shell Launcher / Assigned Access configuration is
    /// present (§10.4). Advisory: the server decides requireAAC from its device registry.
    /// </summary>
    [JsonPropertyName("aacEntitlementPresent")] public bool AacEntitlementPresent { get; set; }
    /// <summary>Same HKLM signal under its honest name (§10.4). Advisory only.</summary>
    [JsonPropertyName("assignedAccessHint")] public bool AssignedAccessHint { get; set; }
    [JsonPropertyName("cameraAuthorized")] public AuthorizationState CameraAuthorized { get; set; } = AuthorizationState.NotDetermined;
    [JsonPropertyName("microphoneAuthorized")] public AuthorizationState MicrophoneAuthorized { get; set; } = AuthorizationState.NotDetermined;
    [JsonPropertyName("internetReachable")] public bool InternetReachable { get; set; }
    [JsonPropertyName("clientVersion")] public string ClientVersion { get; set; } = Constants.ClientVersion;
}

/// <summary>Windows-only extras (CONTRACT §9.2). The server ignores unknown fields.</summary>
public sealed class PreflightExtras
{
    [JsonPropertyName("edition")] public string Edition { get; set; } = string.Empty;
    [JsonPropertyName("secureBoot")] public bool SecureBoot { get; set; }
    [JsonPropertyName("tpmPresent")] public bool TpmPresent { get; set; }
    [JsonPropertyName("virtualMachine")] public bool VirtualMachine { get; set; }
    /// <summary>HKLM kiosk configuration present (advisory, see PreflightReport.AssignedAccessHint).</summary>
    [JsonPropertyName("assignedAccessHint")] public bool AssignedAccessHint { get; set; }
    [JsonPropertyName("rdpSession")] public bool RdpSession { get; set; }
    [JsonPropertyName("screenCaptureProtection")] public bool ScreenCaptureProtection { get; set; }
    /// <summary>HKCU LowLevelHooksTimeout in ms (0 = not set, Windows default applies).</summary>
    [JsonPropertyName("lowLevelHooksTimeoutMs")] public int LowLevelHooksTimeoutMs { get; set; }
    [JsonPropertyName("debuggerAttached")] public bool DebuggerAttached { get; set; }
}

public enum PreflightStatus
{
    Pass,
    Fail,
    Warning,
    NotApplicable,
}

public enum PreflightKind
{
    OsVersion,
    SecureBoot,
    Mdm,
    AccountType,
    Displays,
    ScreenSharing,
    AssignedAccess,
    Camera,
    Microphone,
    Internet,
    ClientVersion,
    WebView2,
    VirtualMachine,
    RdpSession,
    HookTimeout,
    Debugger,
}

/// <summary>UI row on the Preflight screen.</summary>
public sealed class PreflightItem
{
    public PreflightItem(PreflightKind kind, string name, PreflightStatus status, string detail, bool isRequired)
    {
        Kind = kind;
        Name = name;
        Status = status;
        Detail = detail;
        Required = isRequired;
    }

    public PreflightKind Kind { get; }
    public string Name { get; }
    public PreflightStatus Status { get; }
    public string Detail { get; }
    public bool Required { get; }

    public bool BlocksStart => Required && Status == PreflightStatus.Fail;

    public string RequiredText => Required ? "required" : "optional";

    public string StatusGlyph
    {
        get
        {
            switch (Status)
            {
                case PreflightStatus.Pass: return "✓";      // ✓
                case PreflightStatus.Fail: return "✗";      // ✗
                case PreflightStatus.Warning: return "!";
                default: return "–";                        // –
            }
        }
    }
}

// ======================================================================================
// Session (POST /api/v1/sessions) — §10.2
// ======================================================================================

public sealed class SessionStartRequest
{
    [JsonPropertyName("studentCode")] public string StudentCode { get; set; } = string.Empty;
    [JsonPropertyName("examCode")] public string ExamCode { get; set; } = string.Empty;
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("preflight")] public PreflightReport Preflight { get; set; } = new PreflightReport();
    [JsonPropertyName("preflightExtras")] public PreflightExtras? PreflightExtras { get; set; }

    /// <summary>Only for exams where the teacher set an access code. Left out of the JSON when null.</summary>
    [JsonPropertyName("accessCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccessCode { get; set; }

    /// <summary>Trims the code and treats blank as "none", so a blank box is never sent as a wrong guess.</summary>
    public static string? NormalizeAccessCode(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0) return null;
        return trimmed.Length > 64 ? trimmed.Substring(0, 64) : trimmed;
    }
}

public sealed class SessionStudent
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

public sealed class SessionExam
{
    [JsonPropertyName("code")] public string Code { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("durationMinutes")] public int? DurationMinutes { get; set; }
}

public sealed class SessionStartResponse
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = string.Empty;
    [JsonPropertyName("sessionToken")] public string SessionToken { get; set; } = string.Empty;
    [JsonPropertyName("expiresAt")] public string ExpiresAt { get; set; } = string.Empty;
    // Nullable on purpose: the server may omit them; Normalize() replaces null with empty objects.
    [JsonPropertyName("student")] public SessionStudent? Student { get; set; }
    [JsonPropertyName("exam")] public SessionExam? Exam { get; set; }
    [JsonPropertyName("examUrl")] public string ExamUrl { get; set; } = string.Empty;
    [JsonPropertyName("policy")] public Policy? Policy { get; set; }
    [JsonPropertyName("serverTime")] public string? ServerTime { get; set; }
    [JsonPropertyName("nextBeatInSeconds")] public int? NextBeatInSeconds { get; set; }

    [JsonIgnore] public DateTimeOffset? ExpiresAtDate => Iso8601.Parse(ExpiresAt);

    /// <summary>Non-null student/exam/policy after this call.</summary>
    [JsonIgnore] public SessionStudent StudentOrEmpty => Student ??= new SessionStudent();
    [JsonIgnore] public SessionExam ExamOrEmpty => Exam ??= new SessionExam();
    [JsonIgnore] public Policy PolicyOrDefault => Policy ??= new Policy();

    /// <summary>
    /// Null-guards nested objects and rejects unusable responses (blank sessionId / examUrl,
    /// blank token). Returns an error text, or null when the response is usable. (W-15)
    /// </summary>
    public string? Normalize()
    {
        Student ??= new SessionStudent();
        Exam ??= new SessionExam();
        Policy = (Policy ?? new Policy()).Normalized();
        Student.Id ??= string.Empty;
        Student.Name ??= string.Empty;
        Exam.Code ??= string.Empty;
        Exam.Title ??= string.Empty;
        SessionId = (SessionId ?? string.Empty).Trim();
        SessionToken = (SessionToken ?? string.Empty).Trim();
        ExamUrl = (ExamUrl ?? string.Empty).Trim();
        ExpiresAt ??= string.Empty;
        if (SessionId.Length == 0 || SessionId.Length > 128) return "The server returned no session id.";
        if (SessionToken.Length == 0 || SessionToken.Length > 512) return "The server returned no session token.";
        if (ExamUrl.Length == 0 || ExamUrl.Length > 4096) return "The server returned no exam URL.";
        if (!Uri.TryCreate(ExamUrl, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
        {
            return "The server returned an invalid exam URL.";
        }
        return null;
    }
}

// ======================================================================================
// Policy (CONTRACT §4 + §10.3)
// ======================================================================================

[JsonConverter(typeof(ExternalDisplayActionConverter))]
public enum ExternalDisplayAction
{
    Warn,
    BlockStart,
    Pause,
    Terminate,
    Flag,
}

public sealed class ExternalDisplayActionConverter : MappedEnumConverter<ExternalDisplayAction>
{
    public static readonly ExternalDisplayActionConverter Instance = new ExternalDisplayActionConverter();

    private static readonly Dictionary<ExternalDisplayAction, string> Wire = new Dictionary<ExternalDisplayAction, string>
    {
        [ExternalDisplayAction.Warn] = "WARN",
        [ExternalDisplayAction.BlockStart] = "BLOCK_START",
        [ExternalDisplayAction.Pause] = "PAUSE",
        [ExternalDisplayAction.Terminate] = "TERMINATE",
        [ExternalDisplayAction.Flag] = "FLAG",
    };

    protected override IReadOnlyDictionary<ExternalDisplayAction, string> Map => Wire;
    protected override ExternalDisplayAction Fallback => ExternalDisplayAction.Warn;
}

public static class ExternalDisplayActionExtensions
{
    public static string Wire(this ExternalDisplayAction a) => ExternalDisplayActionConverter.Instance.ToWire(a);
}

public sealed class AllowedApp
{
    [JsonPropertyName("bundleId")] public string BundleId { get; set; } = string.Empty;
    [JsonPropertyName("teamId")] public string? TeamId { get; set; }
}

/// <summary>§10.3 allowedLinks entry: a resource the student may open from the "Resources" menu.</summary>
public sealed class AllowedLink
{
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;

    /// <summary>Absolute http(s) URL, no userinfo, no "..", label non-empty (falls back to the host).</summary>
    public bool TryNormalize()
    {
        Url = (Url ?? string.Empty).Trim();
        Label = (Label ?? string.Empty).Trim();
        if (Url.Length == 0 || Url.Length > 2048) return false;
        if (Url.Contains("..", StringComparison.Ordinal)) return false;
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(u.UserInfo)) return false;
        if (string.IsNullOrEmpty(u.Host)) return false;
        if (Label.Length == 0) Label = u.Host;
        if (Label.Length > 60) Label = Label.Substring(0, 60);
        return true;
    }
}

/// <summary>
/// Every field has a conservative default so a partial policy from the server never makes
/// the client less strict than intended. Call <see cref="Normalized"/> after deserializing.
/// </summary>
public sealed class Policy
{
    public const int MinHeartbeatSeconds = 2;
    public const int MaxHeartbeatSeconds = 120;
    public const int MinFlushSeconds = 1;
    public const int MaxFlushSeconds = 300;
    public const int MinOfflineGraceSeconds = 60;
    public const int MaxOfflineGraceSeconds = 3600;

    [JsonPropertyName("mode")] public string Mode { get; set; } = "school";
    [JsonPropertyName("allowedDomains")] public List<string> AllowedDomains { get; set; } = new List<string> { "localhost", "127.0.0.1" };
    [JsonPropertyName("allowedLinks")] public List<AllowedLink> AllowedLinks { get; set; } = new List<AllowedLink>();
    [JsonPropertyName("allowedApps")] public List<AllowedApp> AllowedApps { get; set; } = new List<AllowedApp>();
    [JsonPropertyName("blockExternalDisplay")] public bool BlockExternalDisplay { get; set; } = true;
    [JsonPropertyName("externalDisplayAction")] public ExternalDisplayAction ExternalDisplayAction { get; set; } = AvaibeExam.Models.ExternalDisplayAction.Warn;
    [JsonPropertyName("requireSIP")] public bool RequireSIP { get; set; } = true;
    [JsonPropertyName("requireMDM")] public bool RequireMDM { get; set; }
    [JsonPropertyName("requireStandardAccount")] public bool RequireStandardAccount { get; set; }
    [JsonPropertyName("requireAAC")] public bool RequireAAC { get; set; }
    [JsonPropertyName("allowClipboard")] public bool AllowClipboard { get; set; }
    [JsonPropertyName("allowPrinting")] public bool AllowPrinting { get; set; }
    [JsonPropertyName("allowDownloads")] public bool AllowDownloads { get; set; }
    [JsonPropertyName("allowDevTools")] public bool AllowDevTools { get; set; }
    [JsonPropertyName("heartbeatIntervalSeconds")] public int HeartbeatIntervalSeconds { get; set; } = 10;
    [JsonPropertyName("eventFlushIntervalSeconds")] public int EventFlushIntervalSeconds { get; set; } = 5;
    /// <summary>§10.6 failsafe: release after this many seconds without any successful heartbeat.</summary>
    [JsonPropertyName("offlineGraceSeconds")] public int OfflineGraceSeconds { get; set; } = 600;
    /// <summary>"teacher" (default) | "auto" (§10.3 / §10.5).</summary>
    [JsonPropertyName("releaseOnSubmit")] public string ReleaseOnSubmit { get; set; } = "teacher";
    [JsonPropertyName("minClientVersion")] public string MinClientVersion { get; set; } = "0.1.0";
    [JsonPropertyName("allowStudentReleaseCode")] public bool AllowStudentReleaseCode { get; set; } = true;

    /// <summary>Conservative defaults used before the server has sent a policy.</summary>
    public static Policy ConservativeDefault => new Policy();

    public bool ReleaseOnSubmitIsAuto => string.Equals(ReleaseOnSubmit, "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>Clamps every number, null-guards lists and drops malformed entries (server data is untrusted). (W-07)</summary>
    public Policy Normalized()
    {
        AllowedDomains ??= new List<string>();
        AllowedApps ??= new List<AllowedApp>();
        AllowedLinks ??= new List<AllowedLink>();
        Mode ??= "school";
        MinClientVersion = string.IsNullOrWhiteSpace(MinClientVersion) ? "0.1.0" : MinClientVersion;
        ReleaseOnSubmit = string.Equals(ReleaseOnSubmit, "auto", StringComparison.OrdinalIgnoreCase) ? "auto" : "teacher";
        HeartbeatIntervalSeconds = Math.Clamp(HeartbeatIntervalSeconds, MinHeartbeatSeconds, MaxHeartbeatSeconds);
        EventFlushIntervalSeconds = Math.Clamp(EventFlushIntervalSeconds, MinFlushSeconds, MaxFlushSeconds);
        OfflineGraceSeconds = Math.Clamp(OfflineGraceSeconds, MinOfflineGraceSeconds, MaxOfflineGraceSeconds);

        var domains = new List<string>();
        foreach (var d in AllowedDomains)
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            var t = d.Trim().ToLowerInvariant();
            if (t.Length > 253) continue;
            if (!domains.Contains(t)) domains.Add(t);
            if (domains.Count >= 200) break;
        }
        AllowedDomains = domains;

        var links = new List<AllowedLink>();
        foreach (var l in AllowedLinks)
        {
            if (l == null) continue;
            if (l.TryNormalize()) links.Add(l);
            if (links.Count >= 20) break;
        }
        AllowedLinks = links;

        var apps = new List<AllowedApp>();
        foreach (var a in AllowedApps)
        {
            if (a == null) continue;
            a.BundleId ??= string.Empty;
            apps.Add(a);
        }
        AllowedApps = apps;
        return this;
    }
}

// ======================================================================================
// Heartbeat (POST /api/v1/sessions/:id/heartbeat) — §10.4 signed commands
// ======================================================================================

[JsonConverter(typeof(HeartbeatStatusConverter))]
public enum HeartbeatStatus
{
    Unlocked,
    Locked,
    Warning,
}

public sealed class HeartbeatStatusConverter : MappedEnumConverter<HeartbeatStatus>
{
    private static readonly Dictionary<HeartbeatStatus, string> Wire = new Dictionary<HeartbeatStatus, string>
    {
        [HeartbeatStatus.Unlocked] = "unlocked",
        [HeartbeatStatus.Locked] = "locked",
        [HeartbeatStatus.Warning] = "warning",
    };

    protected override IReadOnlyDictionary<HeartbeatStatus, string> Map => Wire;
    protected override HeartbeatStatus Fallback => HeartbeatStatus.Unlocked;
}

public sealed class HeartbeatRequest
{
    [JsonPropertyName("status")] public HeartbeatStatus Status { get; set; }
    [JsonPropertyName("displayCount")] public int DisplayCount { get; set; } = 1;
    [JsonPropertyName("lockdownMode")] public LockdownMode LockdownMode { get; set; }
    [JsonPropertyName("uptimeSeconds")] public int UptimeSeconds { get; set; }
}

[JsonConverter(typeof(HeartbeatCommandTypeConverter))]
public enum HeartbeatCommandType
{
    Unknown,
    Release,
    Terminate,
    Warn,
}

public sealed class HeartbeatCommandTypeConverter : MappedEnumConverter<HeartbeatCommandType>
{
    public static readonly HeartbeatCommandTypeConverter Instance = new HeartbeatCommandTypeConverter();

    private static readonly Dictionary<HeartbeatCommandType, string> Wire = new Dictionary<HeartbeatCommandType, string>
    {
        [HeartbeatCommandType.Unknown] = "UNKNOWN",
        [HeartbeatCommandType.Release] = "RELEASE",
        [HeartbeatCommandType.Terminate] = "TERMINATE",
        [HeartbeatCommandType.Warn] = "WARN",
    };

    protected override IReadOnlyDictionary<HeartbeatCommandType, string> Map => Wire;
    protected override HeartbeatCommandType Fallback => HeartbeatCommandType.Unknown;
}

/// <summary>
/// §10.4 signed authorization. The signature (ECDSA P-256 / SHA-256, IEEE P1363 r‖s) covers
/// "avaibe-cmd-v1\n{type}\n{sessionId}\n{deviceId}\n{nonce}\n{issuedAt}\n{expiresAt}" using the
/// exact strings received. Verified by Security/CommandVerifier; never logged in full.
/// </summary>
public sealed class CommandAuthorization
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = string.Empty;
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = string.Empty;
    [JsonPropertyName("issuedAt")] public string IssuedAt { get; set; } = string.Empty;
    [JsonPropertyName("expiresAt")] public string ExpiresAt { get; set; } = string.Empty;
    [JsonPropertyName("keyId")] public string KeyId { get; set; } = string.Empty;
    [JsonPropertyName("signature")] public string Signature { get; set; } = string.Empty;
}

public sealed class HeartbeatCommand
{
    [JsonPropertyName("type")] public HeartbeatCommandType Type { get; set; } = HeartbeatCommandType.Unknown;
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    /// <summary>Required for RELEASE / TERMINATE (§10.4); WARN is unsigned.</summary>
    [JsonPropertyName("authorization")] public CommandAuthorization? Authorization { get; set; }
}

public sealed class HeartbeatResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
    [JsonPropertyName("serverTime")] public string? ServerTime { get; set; }
    [JsonPropertyName("remainingSeconds")] public int? RemainingSeconds { get; set; }
    [JsonPropertyName("nextBeatInSeconds")] public int? NextBeatInSeconds { get; set; }
    /// <summary>null, absent, or a command; unknown command types deserialize as Unknown and are ignored.</summary>
    [JsonPropertyName("command")] public HeartbeatCommand? Command { get; set; }
}

// ======================================================================================
// Telemetry (POST /api/v1/sessions/:id/events) — §10.7
// ======================================================================================

[JsonConverter(typeof(EventSeverityConverter))]
public enum EventSeverity
{
    Info,
    Low,
    Medium,
    High,
}

public sealed class EventSeverityConverter : MappedEnumConverter<EventSeverity>
{
    public static readonly EventSeverityConverter Instance = new EventSeverityConverter();

    private static readonly Dictionary<EventSeverity, string> Wire = new Dictionary<EventSeverity, string>
    {
        [EventSeverity.Info] = "info",
        [EventSeverity.Low] = "low",
        [EventSeverity.Medium] = "medium",
        [EventSeverity.High] = "high",
    };

    protected override IReadOnlyDictionary<EventSeverity, string> Map => Wire;
    protected override EventSeverity Fallback => EventSeverity.Info;

    public static EventSeverity FromWire(string? s)
    {
        if (s == null) return EventSeverity.Info;
        foreach (var kv in Wire)
        {
            if (string.Equals(kv.Value, s, StringComparison.OrdinalIgnoreCase)) return kv.Key;
        }
        return EventSeverity.Info;
    }
}

/// <summary>Event type string constants from CONTRACT §3, §9.5 and §10.7.</summary>
public static class EventType
{
    public const string SessionStart = "SESSION_START";
    public const string LockdownEngaged = "LOCKDOWN_ENGAGED";
    public const string LockdownFallback = "LOCKDOWN_FALLBACK";
    public const string LockdownFailed = "LOCKDOWN_FAILED";
    public const string LockdownInterrupted = "LOCKDOWN_INTERRUPTED";
    public const string DisplayChanged = "DISPLAY_CHANGED";
    public const string ExternalDisplay = "EXTERNAL_DISPLAY";
    public const string ScreenSharingDetected = "SCREEN_SHARING_DETECTED";
    public const string BlockedNavigation = "BLOCKED_NAVIGATION";
    public const string BlockedShortcut = "BLOCKED_SHORTCUT";
    public const string AppDeactivated = "APP_DEACTIVATED";
    public const string WindowResized = "WINDOW_RESIZED";
    public const string NetworkOffline = "NETWORK_OFFLINE";
    public const string NetworkOnline = "NETWORK_ONLINE";
    public const string UnlockRequested = "UNLOCK_REQUESTED";
    public const string UnlockDenied = "UNLOCK_DENIED";
    public const string UnlockCompleted = "UNLOCK_COMPLETED";
    public const string ExamSubmitted = "EXAM_SUBMITTED";
    public const string SessionEnd = "SESSION_END";
    public const string WebEvent = "WEB_EVENT";

    // Windows-only (§9.5)
    public const string ProcessDetected = "PROCESS_DETECTED";
    public const string RdpSession = "RDP_SESSION";
    public const string ScreenCaptureProtectionUnavailable = "SCREEN_CAPTURE_PROTECTION_UNAVAILABLE";
    public const string SessionEndBlocked = "SESSION_END_BLOCKED";
    public const string VirtualMachineDetected = "VIRTUAL_MACHINE_DETECTED";

    // Protocol v2 (§10.7)
    public const string CommandRejected = "COMMAND_REJECTED";
    public const string OfflineGraceRelease = "OFFLINE_GRACE_RELEASE";
    public const string ReleaseCodeLocked = "RELEASE_CODE_LOCKED";
    public const string SessionConflict = "SESSION_CONFLICT";
    public const string PolicyMismatch = "POLICY_MISMATCH";
    public const string CaptureProtectionLost = "CAPTURE_PROTECTION_LOST";
}

public sealed class TelemetryEvent
{
    public TelemetryEvent()
    {
    }

    public TelemetryEvent(string type, EventSeverity severity, Dictionary<string, object?>? metadata)
    {
        Type = type;
        Severity = severity;
        Timestamp = Iso8601.Now();
        Metadata = metadata ?? new Dictionary<string, object?>();
    }

    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("severity")] public EventSeverity Severity { get; set; } = EventSeverity.Info;
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = Iso8601.Now();
    /// <summary>Values are string/bool/int/double/JsonElement/nested dictionaries; STJ serializes by runtime type.</summary>
    [JsonPropertyName("metadata")] public Dictionary<string, object?> Metadata { get; set; } = new Dictionary<string, object?>();
}

public sealed class EventsRequest
{
    [JsonPropertyName("events")] public List<TelemetryEvent> Events { get; set; } = new List<TelemetryEvent>();
}

public sealed class EventsResponse
{
    [JsonPropertyName("accepted")] public int? Accepted { get; set; }
}

// ======================================================================================
// Submit / Unlock / Errors / Health — §10.5
// ======================================================================================

public sealed class EmptyBody
{
}

public sealed class SubmitResponse
{
    [JsonPropertyName("ok")] public bool? Ok { get; set; }
    [JsonPropertyName("submittedAt")] public string? SubmittedAt { get; set; }
    /// <summary>"auto" => a signed RELEASE follows; "teacher" (default) => wait for the teacher.</summary>
    [JsonPropertyName("release")] public string? Release { get; set; }

    [JsonIgnore] public bool ReleaseIsAuto => string.Equals(Release, "auto", StringComparison.OrdinalIgnoreCase);
}

public sealed class UnlockRequest
{
    [JsonPropertyName("releaseCode")] public string ReleaseCode { get; set; } = string.Empty;
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
}

public sealed class UnlockResponse
{
    [JsonPropertyName("authorized")] public bool Authorized { get; set; }
    /// <summary>§10.4: the same signed authorization object a RELEASE command carries.</summary>
    [JsonPropertyName("authorization")] public CommandAuthorization? Authorization { get; set; }
    [JsonPropertyName("expiresAt")] public string? ExpiresAt { get; set; }
}

/// <summary>{ "error": { "code", "message" }, "failures": [...] } (CONTRACT §2).</summary>
public sealed class ApiError
{
    [JsonPropertyName("code")] public string Code { get; set; } = "ERROR";
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("failures")] public List<string>? Failures { get; set; }
}

public sealed class ApiErrorEnvelope
{
    [JsonPropertyName("error")] public ApiError? Error { get; set; }
    [JsonPropertyName("failures")] public List<string>? Failures { get; set; }
}

public sealed class HealthResponse
{
    [JsonPropertyName("ok")] public bool? Ok { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
}

/// <summary>Local settings file (%LOCALAPPDATA%\AvaibeExam\settings.json). Never contains secrets.</summary>
public sealed class AppSettings
{
    [JsonPropertyName("baseUrl")] public string? BaseUrl { get; set; }
    [JsonPropertyName("lastStudentCode")] public string? LastStudentCode { get; set; }
    [JsonPropertyName("lastExamCode")] public string? LastExamCode { get; set; }
    [JsonPropertyName("fallbackHardwareId")] public string? FallbackHardwareId { get; set; }
}

/// <summary>Persisted set of used command nonces (DPAPI-protected file nonces.dat, §10.4).</summary>
public sealed class NonceStoreFile
{
    [JsonPropertyName("entries")] public List<NonceEntry> Entries { get; set; } = new List<NonceEntry>();
}

public sealed class NonceEntry
{
    [JsonPropertyName("n")] public string Nonce { get; set; } = string.Empty;
    [JsonPropertyName("t")] public string SeenAt { get; set; } = string.Empty;
}
