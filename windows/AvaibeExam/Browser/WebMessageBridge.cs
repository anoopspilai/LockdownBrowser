using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using AvaibeExam.Core;
using AvaibeExam.Models;
using AvaibeExam.Util;

namespace AvaibeExam.Browser;

/// <summary>
/// The web ⇄ native bridge (CONTRACT §5 / §9.4). Incoming { type, id, payload } messages from
/// window.chrome.webview.postMessage (via the injected window.webkit.messageHandlers.lockdown
/// shim) are parsed strictly; unknown types are ignored. Outgoing messages call
/// window.LockdownBridge.onMessage(msg) when the page defines it. The bridge never exposes a
/// way for JavaScript to end the assessment session, change policy, or read device secrets.
/// </summary>
public sealed class WebMessageBridge
{
    private const string LogCat = "web";

    public enum IncomingKind
    {
        Ready,
        ReportEvent,
        GetSessionStatus,
        RequestExit,
        SubmitComplete,
        HeartbeatPing,
    }

    public sealed class Incoming
    {
        public Incoming(IncomingKind kind, string? id)
        {
            Kind = kind;
            Id = id;
        }

        public IncomingKind Kind { get; }
        public string? Id { get; }
        /// <summary>REPORT_EVENT: the page's event type.</summary>
        public string EventType { get; set; } = string.Empty;
        public EventSeverity Severity { get; set; } = EventSeverity.Info;
        /// <summary>REPORT_EVENT: metadata object (cloned JsonElement) or null.</summary>
        public JsonElement? Metadata { get; set; }
        /// <summary>REQUEST_EXIT: reason.</summary>
        public string Reason { get; set; } = "requested";
    }

    /// <summary>Executes JavaScript in the exam page (set by ExamWebView; null when no page).</summary>
    public Func<string, Task<string>>? ScriptExecutor { get; set; }

    /// <summary>Parsed messages from the page (UI thread).</summary>
    public Action<Incoming>? OnMessage { get; set; }

    // ---- Shim (§9.4) -----------------------------------------------------------------

    /// <summary>
    /// Injected at document creation. Makes the unchanged exam page (which talks to
    /// window.webkit.messageHandlers.lockdown) work on WebView2 and publishes window.LockdownNative.
    /// </summary>
    public static string ShimScript(LockdownMode mode)
    {
        var native = "{ platform: 'windows', clientVersion: '" + Constants.ClientVersion + "', lockdownMode: '" + mode.Wire() + "' }";
        return
            "(function () {\n" +
            "  try {\n" +
            "    window.webkit = window.webkit || {};\n" +
            "    window.webkit.messageHandlers = window.webkit.messageHandlers || {};\n" +
            "    window.webkit.messageHandlers.lockdown = { postMessage: function (m) { window.chrome.webview.postMessage(m); } };\n" +
            "  } catch (e) { console.error('[lockdown-shim] handler install failed', e); }\n" +
            "  try {\n" +
            "    Object.defineProperty(window, 'LockdownNative', { value: Object.freeze(" + native + "), writable: false, configurable: false, enumerable: true });\n" +
            "  } catch (e) { window.LockdownNative = " + native + "; }\n" +
            "})();";
    }

    // ---- Native -> web ---------------------------------------------------------------

    /// <summary>Delivers a message to the page. Silently does nothing when there is no page yet.</summary>
    public void Send(string type, string? id, Dictionary<string, object?> payload)
    {
        var exec = ScriptExecutor;
        if (exec == null) return;
        var message = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["payload"] = payload,
        };
        if (!string.IsNullOrEmpty(id)) message["id"] = id;

        string json;
        try
        {
            json = Json.Serialize(message);
        }
        catch (Exception ex)
        {
            Log.Error(LogCat, "bridge: could not serialize " + type, ex);
            return;
        }
        var js = "(function(){ if (window.LockdownBridge && typeof window.LockdownBridge.onMessage === 'function') { window.LockdownBridge.onMessage(" + json + "); } })();";
        Log.Debug(LogCat, "bridge -> web: " + type);
        _ = RunScriptAsync(exec, js, type);
    }

    private static async Task RunScriptAsync(Func<string, Task<string>> exec, string js, string type)
    {
        try
        {
            await exec(js);
        }
        catch (Exception ex)
        {
            Log.Error(LogCat, "bridge send " + type + " failed: " + ex.Message);
        }
    }

    // ---- Web -> native ---------------------------------------------------------------

    /// <summary>Called by ExamWebView with e.WebMessageAsJson.</summary>
    public void Receive(string json)
    {
        var incoming = Parse(json);
        if (incoming == null) return;
        try
        {
            OnMessage?.Invoke(incoming);
        }
        catch (Exception ex)
        {
            Log.Error(LogCat, "bridge handler failed for " + incoming.Kind, ex);
        }
    }

    /// <summary>Strict parser: returns null for anything that is not a known { type, id?, payload? } object.</summary>
    public static Incoming? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                Log.Warn(LogCat, "bridge: non-object message ignored");
                return null;
            }
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            {
                Log.Warn(LogCat, "bridge: malformed message (no type)");
                return null;
            }
            var type = typeEl.GetString() ?? string.Empty;
            string? id = null;
            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            {
                id = idEl.GetString();
            }
            JsonElement payload = default;
            var hasPayload = root.TryGetProperty("payload", out payload) && payload.ValueKind == JsonValueKind.Object;

            switch (type)
            {
                case "READY":
                    return new Incoming(IncomingKind.Ready, id);
                case "GET_SESSION_STATUS":
                    return new Incoming(IncomingKind.GetSessionStatus, id);
                case "HEARTBEAT_PING":
                    return new Incoming(IncomingKind.HeartbeatPing, id);
                case "SUBMIT_COMPLETE":
                    return new Incoming(IncomingKind.SubmitComplete, id);
                case "REQUEST_EXIT":
                    {
                        var reason = "requested";
                        if (hasPayload && payload.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
                        {
                            reason = r.GetString() ?? reason;
                        }
                        if (reason.Length > 200) reason = reason.Substring(0, 200);
                        return new Incoming(IncomingKind.RequestExit, id) { Reason = reason };
                    }
                case "REPORT_EVENT":
                    {
                        if (!hasPayload || !payload.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String ||
                            string.IsNullOrEmpty(t.GetString()))
                        {
                            Log.Warn(LogCat, "bridge: REPORT_EVENT without type ignored");
                            return null;
                        }
                        var webType = t.GetString() ?? string.Empty;
                        if (webType.Length > 64) webType = webType.Substring(0, 64);
                        var severity = EventSeverity.Info;
                        if (payload.TryGetProperty("severity", out var s) && s.ValueKind == JsonValueKind.String)
                        {
                            severity = EventSeverityConverter.FromWire(s.GetString());
                        }
                        JsonElement? metadata = null;
                        if (payload.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object)
                        {
                            metadata = m.Clone(); // outlives the JsonDocument
                        }
                        return new Incoming(IncomingKind.ReportEvent, id)
                        {
                            EventType = webType,
                            Severity = severity,
                            Metadata = metadata,
                        };
                    }
                default:
                    Log.Warn(LogCat, "bridge: unknown message type " + type + " ignored");
                    return null;
            }
        }
        catch (JsonException ex)
        {
            Log.Warn(LogCat, "bridge: invalid JSON ignored: " + ex.Message);
            return null;
        }
    }
}
