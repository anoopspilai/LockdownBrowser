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
            case LockdownMode.AssignedAccess: return "Assigned Access";
            case LockdownMode.KioskFallback: return "Kiosk fallback";
            default: return "Not locked";
        }
    }
}

// ======================================================================================
// Enrollment (POST /api/v1/devices/enroll)
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
}

/// <summary>Persisted enrollment (DPAPI-protected file, CONTRACT §9.1).</summary>
public sealed class Enrollment
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("deviceToken")] public string DeviceToken { get; set; } = string.Empty;
    [JsonPropertyName("mode")] public string Mode { get; set; } = "byod";
}

// ======================================================================================
// Preflight (CONTRACT §3 + §9.2)
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

/// <summary>Wire format sent in POST /api/v1/sessions.</summary>
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
    /// <summary>Windows: Assigned Access / kiosk session detected (CONTRACT §9.2).</summary>
    [JsonPropertyName("aacEntitlementPresent")] public bool AacEntitlementPresent { get; set; }
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
    [JsonPropertyName("assignedAccess")] public bool AssignedAccess { get; set; }
    [JsonPropertyName("rdpSession")] public bool RdpSession { get; set; }
    [JsonPropertyName("screenCaptureProtection")] public bool ScreenCaptureProtection { get; set; }
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
// Session (POST /api/v1/sessions)
// ======================================================================================

public sealed class SessionStartRequest
{
    [JsonPropertyName("studentCode")] public string StudentCode { get; set; } = string.Empty;
    [JsonPropertyName("examCode")] public string ExamCode { get; set; } = string.Empty;
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("preflight")] public PreflightReport Preflight { get; set; } = new PreflightReport();
    [JsonPropertyName("preflightExtras")] public PreflightExtras? PreflightExtras { get; set; }
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
    [JsonPropertyName("student")] public SessionStudent Student { get; set; } = new SessionStudent();
    [JsonPropertyName("exam")] public SessionExam Exam { get; set; } = new SessionExam();
    [JsonPropertyName("examUrl")] public string ExamUrl { get; set; } = string.Empty;
    [JsonPropertyName("policy")] public Policy Policy { get; set; } = new Policy();

    [JsonIgnore] public DateTimeOffset? ExpiresAtDate => Iso8601.Parse(ExpiresAt);
}

// ======================================================================================
// Policy (CONTRACT §4)
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

/// <summary>
/// Every field has a conservative default so a partial policy from the server never makes
/// the client less strict than intended. Call <see cref="Normalized"/> after deserializing.
/// </summary>
public sealed class Policy
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "school";
    [JsonPropertyName("allowedDomains")] public List<string> AllowedDomains { get; set; } = new List<string> { "localhost", "127.0.0.1" };
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
    [JsonPropertyName("minClientVersion")] public string MinClientVersion { get; set; } = "0.1.0";
    [JsonPropertyName("allowStudentReleaseCode")] public bool AllowStudentReleaseCode { get; set; } = true;

    /// <summary>Conservative defaults used before the server has sent a policy.</summary>
    public static Policy ConservativeDefault => new Policy();

    /// <summary>Clamps intervals and null-guards lists (server data is untrusted).</summary>
    public Policy Normalized()
    {
        AllowedDomains ??= new List<string>();
        AllowedApps ??= new List<AllowedApp>();
        Mode ??= "school";
        MinClientVersion = string.IsNullOrWhiteSpace(MinClientVersion) ? "0.1.0" : MinClientVersion;
        HeartbeatIntervalSeconds = Math.Max(2, HeartbeatIntervalSeconds);
        EventFlushIntervalSeconds = Math.Max(1, EventFlushIntervalSeconds);
        return this;
    }
}

// ======================================================================================
// Heartbeat (POST /api/v1/sessions/:id/heartbeat)
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

public sealed class HeartbeatCommand
{
    [JsonPropertyName("type")] public HeartbeatCommandType Type { get; set; } = HeartbeatCommandType.Unknown;
    [JsonPropertyName("authorizationId")] public string? AuthorizationId { get; set; }
    [JsonPropertyName("expiresAt")] public string? ExpiresAt { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class HeartbeatResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
    [JsonPropertyName("serverTime")] public string? ServerTime { get; set; }
    [JsonPropertyName("remainingSeconds")] public int? RemainingSeconds { get; set; }
    /// <summary>null, absent, or a command; unknown command types deserialize as Unknown and are ignored.</summary>
    [JsonPropertyName("command")] public HeartbeatCommand? Command { get; set; }
}

// ======================================================================================
// Telemetry (POST /api/v1/sessions/:id/events)
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

/// <summary>Event type string constants from CONTRACT §3 and §9.5.</summary>
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
// Submit / Unlock / Errors / Health
// ======================================================================================

public sealed class EmptyBody
{
}

public sealed class SubmitResponse
{
    [JsonPropertyName("ok")] public bool? Ok { get; set; }
    [JsonPropertyName("submittedAt")] public string? SubmittedAt { get; set; }
}

public sealed class UnlockRequest
{
    [JsonPropertyName("releaseCode")] public string ReleaseCode { get; set; } = string.Empty;
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
}

public sealed class UnlockResponse
{
    [JsonPropertyName("authorized")] public bool Authorized { get; set; }
    [JsonPropertyName("authorizationId")] public string? AuthorizationId { get; set; }
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
