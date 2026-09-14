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
/// HttpClient wrapper for every endpoint in CONTRACT §3. Headers per §2:
/// X-Device-Id, X-Client-Version, and Authorization: Bearer &lt;sessionToken&gt; after session start.
/// Thread-safe; call from any thread.
/// </summary>
public sealed class ApiClient : IDisposable
{
    private const string LogCat = "network";

    private readonly HttpClient _http;
    private readonly object _gate = new object();
    private Uri _baseUrl;
    private string? _deviceId;
    private string? _sessionToken;

    public ApiClient(string baseUrl)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _baseUrl = ParseBaseUrl(baseUrl) ?? new Uri(Constants.DefaultBaseUrl);
    }

    public Uri BaseUrl
    {
        get { lock (_gate) { return _baseUrl; } }
    }

    private static Uri? ParseBaseUrl(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;
        return uri;
    }

    /// <summary>Returns false (and keeps the old URL) when the text is not a valid http(s) URL.</summary>
    public bool SetBaseUrl(string text)
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
        string? token;
        lock (_gate)
        {
            baseUrl = _baseUrl;
            deviceId = _deviceId;
            token = _sessionToken;
        }

        var url = new Uri(baseUrl, path);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Client-Version", Constants.ClientVersion);
        if (deviceId != null) request.Headers.TryAddWithoutValidation("X-Device-Id", deviceId);
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null)
        {
            var json = JsonSerializer.Serialize(body, body.GetType(), Json.Options);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is OperationCanceledException)
        {
            Log.Error(LogCat, $"{method} {path} transport error: {ex.Message}");
            throw new ApiException("NETWORK_ERROR", "Network error: " + ex.Message, 0, null, ex);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Log.Debug(LogCat, $"{method} {path} -> {status}");

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
                Log.Error(LogCat, $"{method} {path} decode error: {ex.Message}");
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
        return new ApiException(code, message, status, failures);
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
