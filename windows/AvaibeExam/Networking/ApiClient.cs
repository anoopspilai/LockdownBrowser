using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AvaibeExam.Core;
using AvaibeExam.Models;
using AvaibeExam.Util;

namespace AvaibeExam.Networking;

/// <summary>Any failure talking to the backend: transport, HTTP status, or decoding.</summary>
public sealed class ApiException : Exception
{
    public ApiException(string code, string message, int status, IReadOnlyList<string>? failures, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Status = status;
        Failures = failures ?? Array.Empty<string>();
    }

    /// <summary>Server error code (e.g. PREFLIGHT_FAILED) or a client-side code (NETWORK_ERROR, DECODE_ERROR).</summary>
    public string Code { get; }
    /// <summary>HTTP status, 0 for transport errors.</summary>
    public int Status { get; }
    /// <summary>PREFLIGHT_FAILED failures list, otherwise empty.</summary>
    public IReadOnlyList<string> Failures { get; }

    /// <summary>"message (CODE)" like the macOS client's errorDescription.</summary>
    public string DisplayText => Status > 0 ? $"{Message} ({Code})" : Message;
}

/// <summary>
/// HttpClient wrapper for every endpoint in CONTRACT §3 / §10. Headers per §10.1: X-Device-Id AND
/// X-Device-Token on every request after enrollment, X-Client-Version always, and
/// Authorization: Bearer &lt;sessionToken&gt; after session start. No redirects, 15 s timeout,
/// response bodies capped at 1 MiB (W-22). Base URL must be https unless loopback (W-02).
/// Thread-safe; call from any thread. Tokens are never logged.
/// </summary>
public sealed class ApiClient : IDisposable
{
    private const string LogCat = "network";
    /// <summary>§10.1: clients cap response bodies at 1 MiB.</summary>
    public const long MaxResponseBytes = 1_000_000;

    private readonly HttpClient _http;
    private readonly object _gate = new object();
    private Uri _baseUrl;
    private string? _deviceId;
    private string? _deviceToken;
    private string? _sessionToken;

    public ApiClient(string baseUrl)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
        _baseUrl = ParseBaseUrl(baseUrl) ?? new Uri(Constants.DefaultBaseUrl);
    }

    public Uri BaseUrl
    {
        get { lock (_gate) { return _baseUrl; } }
    }

    /// <summary>
    /// §10.1: absolute http(s) URL, https REQUIRED unless the host is loopback (localhost /
    /// 127.0.0.1 / ::1), no userinfo. Returns null for anything else.
    /// </summary>
    public static Uri? ParseBaseUrl(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > 2048) return null;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;
        if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopback(uri)) return null;
        return uri;
    }

    public static bool IsLoopback(Uri uri)
    {
        if (uri.IsLoopback) return true;
        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Human text for the login screen when <see cref="ParseBaseUrl"/> rejects a value.</summary>
    public static string BaseUrlRule => "Enter a valid https:// backend URL (http:// is only allowed for localhost).";

    /// <summary>Returns false (and keeps the old URL) when the text is not an acceptable base URL.</summary>
    public bool SetBaseUrl(string? text)
    {
        var uri = ParseBaseUrl(text);
        if (uri == null) return false;
        lock (_gate) { _baseUrl = uri; }
        return true;
    }

    public void SetDeviceId(string? id)
    {
        lock (_gate) { _deviceId = string.IsNullOrWhiteSpace(id) ? null : id; }
    }

    /// <summary>§10.1: sent as X-Device-Token on every request once enrolled (W-11).</summary>
    public void SetDeviceToken(string? token)
    {
        lock (_gate) { _deviceToken = string.IsNullOrWhiteSpace(token) ? null : token; }
    }

    public void SetSessionToken(string? token)
    {
        lock (_gate) { _sessionToken = string.IsNullOrWhiteSpace(token) ? null : token; }
    }

    // ---- Endpoints -------------------------------------------------------------------

    public Task<HealthResponse> HealthzAsync() =>
        RequestAsync<HealthResponse>(HttpMethod.Get, "/healthz", null);

    public Task<EnrollResponse> EnrollAsync(EnrollRequest request) =>
        RequestAsync<EnrollResponse>(HttpMethod.Post, "/api/v1/devices/enroll", request);

    public Task<SessionStartResponse> StartSessionAsync(SessionStartRequest request) =>
        RequestAsync<SessionStartResponse>(HttpMethod.Post, "/api/v1/sessions", request);

    public Task<HeartbeatResponse> HeartbeatAsync(string sessionId, HeartbeatRequest request) =>
        RequestAsync<HeartbeatResponse>(HttpMethod.Post, $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/heartbeat", request);

    public Task<EventsResponse> PostEventsAsync(string sessionId, List<TelemetryEvent> events) =>
        RequestAsync<EventsResponse>(HttpMethod.Post, $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/events", new EventsRequest { Events = events });

    public Task<SubmitResponse> SubmitAsync(string sessionId) =>
        RequestAsync<SubmitResponse>(HttpMethod.Post, $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/submit", new EmptyBody());

    public Task<UnlockResponse> UnlockAsync(string sessionId, UnlockRequest request) =>
        RequestAsync<UnlockResponse>(HttpMethod.Post, $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/unlock", request);

    // ---- Core ------------------------------------------------------------------------

    private async Task<T> RequestAsync<T>(HttpMethod method, string path, object? body)
    {
        Uri baseUrl;
        string? deviceId;
        string? deviceToken;
        string? token;
        lock (_gate)
        {
            baseUrl = _baseUrl;
            deviceId = _deviceId;
            deviceToken = _deviceToken;
            token = _sessionToken;
        }

        var url = new Uri(baseUrl, path);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Client-Version", Constants.ClientVersion);
        if (deviceId != null) request.Headers.TryAddWithoutValidation("X-Device-Id", deviceId);
        if (deviceToken != null) request.Headers.TryAddWithoutValidation("X-Device-Token", deviceToken);
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null)
        {
            var json = JsonSerializer.Serialize(body, body.GetType(), Json.Options);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            // ResponseHeadersRead so the Content-Length check below runs before any body is buffered.
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is OperationCanceledException)
        {
            Log.Error(LogCat, $"{method} {path} transport error: {ex.GetType().Name}");
            throw new ApiException("NETWORK_ERROR", "Network error: " + ex.Message, 0, null, ex);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            Log.Debug(LogCat, $"{method} {path} -> {status}");

            // W-22: refuse oversized bodies up front; MaxResponseContentBufferSize is the backstop
            // for chunked responses (HttpClient then throws HttpRequestException while reading).
            var declared = response.Content.Headers.ContentLength;
            if (declared.HasValue && declared.Value > MaxResponseBytes)
            {
                throw new ApiException("RESPONSE_TOO_LARGE", "Server response too large (" + declared.Value + " bytes)", status, null);
            }

            string text;
            try
            {
                // Buffers at most MaxResponseBytes (throws HttpRequestException beyond that), which
                // also covers chunked responses that carry no Content-Length.
                await response.Content.LoadIntoBufferAsync(MaxResponseBytes).ConfigureAwait(false);
                text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is OperationCanceledException)
            {
                Log.Error(LogCat, $"{method} {path} body read error: {ex.GetType().Name}");
                throw new ApiException("NETWORK_ERROR", "Network error while reading the response: " + ex.Message, 0, null, ex);
            }

            if (status < 200 || status >= 300)
            {
                throw ParseError(status, text);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                if (typeof(T) == typeof(HealthResponse))
                {
                    return (T)(object)new HealthResponse { Ok = true };
                }
                throw new ApiException("DECODE_ERROR", "Could not read server response: empty body", status, null);
            }

            try
            {
                var result = JsonSerializer.Deserialize<T>(text, Json.Options);
                if (result == null)
                {
                    throw new ApiException("DECODE_ERROR", "Could not read server response: null", status, null);
                }
                return result;
            }
            catch (JsonException ex)
            {
                Log.Error(LogCat, $"{method} {path} decode error: {ex.GetType().Name}");
                throw new ApiException("DECODE_ERROR", "Could not read server response: " + ex.Message, status, null, ex);
            }
        }
    }

    private static ApiException ParseError(int status, string text)
    {
        string code = "HTTP_" + status;
        string message = "Server returned HTTP " + status;
        List<string>? failures = null;

        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<ApiErrorEnvelope>(text, Json.Options);
                if (envelope?.Error != null)
                {
                    code = string.IsNullOrWhiteSpace(envelope.Error.Code) ? code : envelope.Error.Code;
                    message = string.IsNullOrWhiteSpace(envelope.Error.Message) ? message : envelope.Error.Message;
                    failures = envelope.Error.Failures ?? envelope.Failures;
                }
                else
                {
                    var flat = JsonSerializer.Deserialize<ApiError>(text, Json.Options);
                    if (flat != null && !string.IsNullOrWhiteSpace(flat.Message))
                    {
                        code = string.IsNullOrWhiteSpace(flat.Code) ? code : flat.Code;
                        message = flat.Message;
                        failures = flat.Failures;
                    }
                }
            }
            catch (JsonException)
            {
                // keep generic
            }
        }
        if (code.Length > 64) code = code.Substring(0, 64);
        if (message.Length > 500) message = message.Substring(0, 500);
        return new ApiException(code, message, status, failures);
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
