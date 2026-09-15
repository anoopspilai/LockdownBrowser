using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using AvaibeExam.Core;
using AvaibeExam.Models;
using AvaibeExam.Util;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AvaibeExam.Browser;

/// <summary>A JavaScript dialog the page opened; rendered by the app as a WPF overlay (W-24).</summary>
public sealed class ScriptDialogRequest
{
    public ScriptDialogRequest(string kind, string message, string? defaultText)
    {
        Kind = kind;
        Message = message;
        DefaultText = defaultText;
    }

    /// <summary>"alert" | "confirm" | "prompt" | "beforeunload"</summary>
    public string Kind { get; }
    public string Message { get; }
    public string? DefaultText { get; }
    public bool HasCancel => Kind != "alert";
    public bool HasInput => Kind == "prompt";
}

public sealed class ScriptDialogResult
{
    public ScriptDialogResult(bool accepted, string? text)
    {
        Accepted = accepted;
        Text = text;
    }

    public bool Accepted { get; }
    public string? Text { get; }

    public static ScriptDialogResult Cancelled => new ScriptDialogResult(false, null);
}

/// <summary>
/// Owns the WebView2 control for the exam, its navigation policy and the message bridge.
/// Restrictions per CONTRACT §9.3 / §10.3: no context menu, no dev tools, no downloads (ever),
/// no new windows (ever), no zoom, no print (accelerators off), no drag-drop, status bar off,
/// autofill off, PDF toolbar stripped, all permission prompts denied.
///
/// Navigation rule (§10.3, top-level AND frames): allowed only when the target origin equals the
/// exam page origin or the URL starts with one allowedLinks entry; javascript:/data:/blob:/file:
/// are always blocked; about:blank only before the first load. Subresources (fetch/XHR/WS/images/
/// scripts/frames) are refused with a 403 unless their host is in allowedDomains (W-12).
/// The bridge accepts messages only from documents whose origin equals the exam origin (W-05).
///
/// Lifecycle: CreateControl(policy, mode, sessionId) -> (host in the visual tree) -> InitializeAsync()
/// -> Load(url) -> [NavigateToAllowedLink / BackToExam] -> ClearBrowsingDataAsync() -> Teardown().
/// Each session uses its own user-data folder, deleted on teardown and swept at startup (W-18).
/// </summary>
public sealed class ExamWebView
{
    private const string LogCat = "web";

    private Policy _policy = Policy.ConservativeDefault;
    private bool _reloadedAfterFailure;
    private bool _initialized;
    private bool _firstNavigationSucceeded;
    private string _userDataFolder = string.Empty;
    private CoreWebView2Environment? _environment;
    private Uri? _examUrl;
    private string? _examOrigin;
    private string? _lastExamPageUrl;
    private string _sessionId = string.Empty;
    /// <summary>
    /// True while the top-level page is the exam origin. Subresource filtering (W-12) applies only
    /// then: an allowed resource page needs its own images, styles and scripts from other hosts.
    /// Navigation to other sites stays refused by <see cref="IsNavigationAllowed"/>, frames included.
    /// </summary>
    private bool _topLevelOnExam = true;
    private readonly List<string> _linkPrefixes = new List<string>();
    private readonly HashSet<string> _extraAllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastBlockedHost = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

    public WebView2? Control { get; private set; }
    public WebMessageBridge Bridge { get; } = new WebMessageBridge();
    public LockdownMode LockdownMode { get; private set; } = AvaibeExam.Models.LockdownMode.None;

    /// <summary>(url, reason) — a navigation, popup, subresource or download was refused.</summary>
    public Action<string, string>? OnBlockedNavigation { get; set; }
    /// <summary>(kind, fatal) — the browser/render process failed.</summary>
    public Action<string, bool>? OnProcessFailed { get; set; }
    /// <summary>CoreWebView2 is ready (used to re-apply capture protection to the top-level window).</summary>
    public Action? OnInitialized { get; set; }
    /// <summary>Renders alert/confirm/prompt/beforeunload natively; null => every dialog is cancelled. UI thread, async.</summary>
    public Func<ScriptDialogRequest, Task<ScriptDialogResult>>? ScriptDialogHandler { get; set; }
    /// <summary>The bridge received a message from a document that is NOT the exam origin (W-05).</summary>
    public Action<string>? OnForeignMessage { get; set; }
    /// <summary>A top-level navigation finished (success or failure); lets the UI refresh "Back to exam".</summary>
    public Action? OnNavigated { get; set; }
    /// <summary>(host) — a page the student tried to open at top level was refused (for the on-screen notice).</summary>
    public Action<string>? OnTopLevelBlocked { get; set; }

    public bool IsInitialized => _initialized;
    /// <summary>"scheme://host:port" of the exam page (lower-case host, explicit port).</summary>
    public string? ExamOrigin => _examOrigin;

    // ---- Startup hygiene (W-18) ------------------------------------------------------------

    /// <summary>Deletes every leftover per-session WebView2 profile folder (best effort, background).</summary>
    public static void SweepStaleUserDataFolders()
    {
        var root = Constants.WebView2UserDataRoot;
        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(root)) return;
                var removed = 0;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (TryDeleteDirectory(dir, attempts: 2)) removed++;
                }
                // Legacy single-profile files directly under the root (pre-W-18 builds).
                foreach (var file in Directory.GetFiles(root))
                {
                    try { File.Delete(file); } catch { /* locked */ }
                }
                if (removed > 0) Log.Info(LogCat, "Swept " + removed + " stale WebView2 profile folder(s)");
            }
            catch (Exception ex)
            {
                Log.Warn(LogCat, "Stale profile sweep failed: " + ex.GetType().Name);
            }
        });
    }

    private static bool TryDeleteDirectory(string path, int attempts)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                if (!Directory.Exists(path)) return true;
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch
            {
                if (i + 1 < attempts) System.Threading.Thread.Sleep(1000);
            }
        }
        return false;
    }

    // ---- Lifecycle -------------------------------------------------------------------

    /// <summary>Creates the WPF control (must be added to the visual tree before InitializeAsync completes).</summary>
    public WebView2 CreateControl(Policy policy, LockdownMode mode, string sessionId)
    {
        Teardown();
        _policy = policy;
        LockdownMode = mode;
        _reloadedAfterFailure = false;
        _initialized = false;
        _firstNavigationSucceeded = false;
        _examUrl = null;
        _examOrigin = null;
        _lastExamPageUrl = null;
        _sessionId = sessionId ?? string.Empty;
        _topLevelOnExam = true;
        _userDataFolder = Constants.WebView2UserDataFolderFor(sessionId);
        _linkPrefixes.Clear();
        _extraAllowedHosts.Clear();
        _lastBlockedHost.Clear();
        foreach (var link in policy.AllowedLinks)
        {
            if (Uri.TryCreate(link.Url, UriKind.Absolute, out var lu))
            {
                var host = lu.Host.ToLowerInvariant();
                _linkPrefixes.Add(NormalizedPrefix(lu));
                _extraAllowedHosts.Add(host);
                // Sites redirect between example.com and www.example.com; a link to either means both.
                var twin = WwwTwin(host);
                if (twin != null)
                {
                    _linkPrefixes.Add(lu.Scheme + "://" + twin + ":" + lu.Port + lu.PathAndQuery);
                    _extraAllowedHosts.Add(twin);
                }
            }
        }

        var control = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.Black,
        };
        Control = control;
        Log.Info(LogCat, "WebView2 control created (mode=" + mode.Wire() + ", devTools=" + policy.AllowDevTools + ", links=" + _linkPrefixes.Count + ")");
        return control;
    }

    /// <summary>Creates the environment, initializes CoreWebView2 and applies every restriction.</summary>
    public async Task InitializeAsync()
    {
        var control = Control;
        if (control == null) throw new InvalidOperationException("CreateControl must be called first");

        Directory.CreateDirectory(_userDataFolder);
        // Keep the browser arguments minimal on purpose; every restriction is applied through
        // the documented Settings API below.
        var options = new CoreWebView2EnvironmentOptions();
        // Positional arguments: (browserExecutableFolder, userDataFolder, options).
        var environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder, options);
        _environment = environment;

        await control.EnsureCoreWebView2Async(environment);
        var core = control.CoreWebView2;
        if (core == null) throw new InvalidOperationException("CoreWebView2 failed to initialize");

        // Core restrictions (present in every runtime the SDK supports) — must not fail silently.
        var s = core.Settings;
#if DEBUG
        s.AreDevToolsEnabled = _policy.AllowDevTools;
        s.AreDefaultContextMenusEnabled = _policy.AllowDevTools;
#else
        // §10.3: dev tools are compiled off in release regardless of policy.
        s.AreDevToolsEnabled = false;
        s.AreDefaultContextMenusEnabled = false;
#endif
        s.AreDefaultScriptDialogsEnabled = false;      // we render alert/confirm natively
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;    // Ctrl+P/F/R, F5, F12, ... (editing keys unaffected)
        s.IsBuiltInErrorPageEnabled = true;
        s.IsWebMessageEnabled = true;
        s.IsScriptEnabled = true;

        // Newer settings: the SDK is newer than some installed Evergreen runtimes, and accessing a
        // setting the runtime does not implement throws. None of these is security-critical on its
        // own (downloads, popups, navigation and dialogs are enforced by the handlers below), so a
        // failure is logged instead of aborting the exam start.
        TrySetting("IsGeneralAutofillEnabled", () => s.IsGeneralAutofillEnabled = false);
        TrySetting("IsPasswordAutosaveEnabled", () => s.IsPasswordAutosaveEnabled = false);
        TrySetting("IsPinchZoomEnabled", () => s.IsPinchZoomEnabled = false);
        TrySetting("IsSwipeNavigationEnabled", () => s.IsSwipeNavigationEnabled = false);
        TrySetting("HiddenPdfToolbarItems", () =>
            s.HiddenPdfToolbarItems = CoreWebView2PdfToolbarItems.Save | CoreWebView2PdfToolbarItems.Print |
                                      CoreWebView2PdfToolbarItems.SaveAs | CoreWebView2PdfToolbarItems.FullScreen |
                                      CoreWebView2PdfToolbarItems.MoreSettings);
        TrySetting("UserAgent", () =>
            s.UserAgent = "AvaibeExam/" + Constants.ClientVersion + " (Windows; lockdown=" + LockdownMode.Wire() + ") " + s.UserAgent);
        TrySetting("AllowExternalDrop", () => control.AllowExternalDrop = false);

        control.ZoomFactor = 1.0;

        core.NavigationStarting += OnNavigationStarting;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.WebMessageReceived += OnWebMessageReceived;
        core.ContextMenuRequested += OnContextMenuRequested;
        core.PermissionRequested += OnPermissionRequested;
        core.ScriptDialogOpening += OnScriptDialogOpening;
        core.ProcessFailed += OnProcessFailedHandler;
        core.WindowCloseRequested += OnWindowCloseRequested;

        // W-12: every request (documents, frames, scripts, XHR, fetch, WebSocket, images, media, ...)
        // passes through OnWebResourceRequested, which answers 403 for hosts outside allowedDomains.
        // The 3-argument overload (SDK 1.0.2365+) also covers requests issued by iframes and
        // workers; the 2-argument one is documented as "does not behave as expected for iframes".
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnWebResourceRequested;

        // Note: WebView2 injects document-created scripts into EVERY frame; there is no
        // main-frame-only option in this SDK. The origin check in OnWebMessageReceived is the
        // real control (W-05).
        await core.AddScriptToExecuteOnDocumentCreatedAsync(WebMessageBridge.ShimScript(LockdownMode, _policy.AllowPrinting));
        Bridge.ScriptExecutor = js => core.ExecuteScriptAsync(js);

        _initialized = true;
        Log.Info(LogCat, "CoreWebView2 ready (runtime " + environment.BrowserVersionString + ")");
        OnInitialized?.Invoke();
    }

    private static void TrySetting(string name, Action apply)
    {
        try
        {
            apply();
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "WebView2 setting " + name + " not applied (runtime too old?): " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// True when the exam URL may be loaded at all: http(s), a host, and the host inside
    /// policy.allowedDomains (the server derives allowedDomains from examUrl, §10.3). Checked
    /// BEFORE lockdown engages so a refused URL never leaves the student locked (W-30).
    /// </summary>
    public static bool IsExamUrlAcceptable(Uri url, Policy policy, out string reason)
    {
        if (!url.IsAbsoluteUri) { reason = "not-absolute"; return false; }
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) { reason = "scheme"; return false; }
        if (string.IsNullOrEmpty(url.Host)) { reason = "no-host"; return false; }
        if (!string.IsNullOrEmpty(url.UserInfo)) { reason = "userinfo"; return false; }
        if (url.Scheme == Uri.UriSchemeHttp && !url.IsLoopback && !string.Equals(url.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            reason = "https-required";
            return false;
        }
        var host = url.Host.ToLowerInvariant();
        foreach (var pattern in policy.AllowedDomains)
        {
            if (HostMatches(host, pattern)) { reason = string.Empty; return true; }
        }
        reason = "host-not-in-allowedDomains";
        return false;
    }

    /// <summary>Loads the exam page. Returns false (and reports) when the URL is refused.</summary>
    public bool Load(Uri url)
    {
        var core = Control?.CoreWebView2;
        if (core == null)
        {
            Log.Error(LogCat, "Load called before CoreWebView2 is ready");
            return false;
        }
        if (!IsExamUrlAcceptable(url, _policy, out var reason))
        {
            Log.Error(LogCat, "Refusing to load exam URL (" + reason + "): " + Redact(url));
            OnBlockedNavigation?.Invoke(Redact(url), "exam-url-" + reason);
            return false;
        }
        _examUrl = url;
        _examOrigin = OriginOf(url);
        _lastExamPageUrl = null;
        Log.Info(LogCat, "Loading exam page at origin " + _examOrigin);
        core.Navigate(url.ToString());
        return true;
    }

    /// <summary>"Resources" menu: navigates the exam view to an allowed link (§10.3).</summary>
    public bool NavigateToAllowedLink(AllowedLink link)
    {
        var core = Control?.CoreWebView2;
        if (core == null || link == null) return false;
        if (!IsNavigationAllowed(link.Url, out var reason))
        {
            Block(link.Url, "resource-" + reason);
            return false;
        }
        Log.Info(LogCat, "Opening allowed link: " + link.Label);
        try { core.Navigate(link.Url); } catch (Exception ex) { Log.Warn(LogCat, "Navigate failed: " + ex.GetType().Name); return false; }
        return true;
    }

    /// <summary>
    /// "Back to exam": returns to the real exam page. The launch URL carries a one-time token the
    /// server refuses after its first use, so it is never reused: the last /exam/&lt;sessionId&gt; page
    /// seen is preferred, otherwise that address is built from the exam origin and session id.
    /// Answers are autosaved on the server and restored when the page loads.
    /// </summary>
    public bool BackToExam()
    {
        var core = Control?.CoreWebView2;
        if (core == null) return false;
        var target = _lastExamPageUrl ?? ExamPageUrl();
        if (string.IsNullOrEmpty(target)) return false;
        Log.Info(LogCat, "Back to exam page");
        try { core.Navigate(target); } catch (Exception ex) { Log.Warn(LogCat, "Navigate failed: " + ex.GetType().Name); return false; }
        return true;
    }

    private string? ExamPageUrl()
    {
        if (_examUrl == null || _sessionId.Length == 0) return null;
        return _examUrl.GetLeftPart(UriPartial.Authority) + "/exam/" + Uri.EscapeDataString(_sessionId);
    }

    /// <summary>example.com &lt;-&gt; www.example.com; null for IP addresses and single-label hosts.</summary>
    public static string? WwwTwin(string host)
    {
        var h = (host ?? string.Empty).Trim().ToLowerInvariant();
        if (h.Length == 0 || h.Contains(':') || Uri.CheckHostName(h) == UriHostNameType.IPv4) return null;
        var labels = h.Split('.');
        if (labels.Length < 2) return null;
        if (h.StartsWith("www.", StringComparison.Ordinal) && labels.Length > 2) return h.Substring(4);
        return "www." + h;
    }

    /// <summary>Whether the view currently shows a non-exam-origin page (drives the "Back to exam" button).</summary>
    public bool IsOnResourcePage
    {
        get
        {
            var src = Control?.CoreWebView2?.Source;
            if (string.IsNullOrEmpty(src) || _examOrigin == null) return false;
            return Uri.TryCreate(src, UriKind.Absolute, out var u) && OriginOf(u) != _examOrigin;
        }
    }

    /// <summary>§10.3: wipes cookies, storage and cache of this session's profile. Guarded; never throws.</summary>
    public async Task ClearBrowsingDataAsync()
    {
        var core = Control?.CoreWebView2;
        if (core == null) return;
        try
        {
            var clear = core.Profile.ClearBrowsingDataAsync();
            var finished = await Task.WhenAny(clear, Task.Delay(TimeSpan.FromSeconds(4)));
            if (finished == clear)
            {
                await clear; // surface exceptions
                Log.Info(LogCat, "Browsing data cleared");
            }
            else
            {
                Log.Warn(LogCat, "ClearBrowsingDataAsync timed out; profile folder is deleted on teardown anyway");
            }
        }
        catch (Exception ex)
        {
            // Older runtime without Profile / ClearBrowsingData, or the browser is already gone.
            Log.Warn(LogCat, "ClearBrowsingDataAsync unavailable: " + ex.GetType().Name);
        }
    }

    public void Teardown()
    {
        var control = Control;
        Control = null;
        Bridge.ScriptExecutor = null;
        _initialized = false;
        _environment = null;
        var folder = _userDataFolder;
        if (control == null) return;
        try
        {
            var core = control.CoreWebView2;
            if (core != null)
            {
                core.NavigationStarting -= OnNavigationStarting;
                core.FrameNavigationStarting -= OnFrameNavigationStarting;
                core.NavigationCompleted -= OnNavigationCompleted;
                core.NewWindowRequested -= OnNewWindowRequested;
                core.DownloadStarting -= OnDownloadStarting;
                core.WebMessageReceived -= OnWebMessageReceived;
                core.ContextMenuRequested -= OnContextMenuRequested;
                core.PermissionRequested -= OnPermissionRequested;
                core.ScriptDialogOpening -= OnScriptDialogOpening;
                core.ProcessFailed -= OnProcessFailedHandler;
                core.WindowCloseRequested -= OnWindowCloseRequested;
                core.WebResourceRequested -= OnWebResourceRequested;
                try { core.Stop(); } catch { /* ignore */ }
            }
            control.Dispose();
            Log.Info(LogCat, "WebView2 disposed");
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "Teardown failed: " + ex.Message);
        }
        // W-18: the browser process exits asynchronously; delete the profile with retries off-thread.
        if (!string.IsNullOrEmpty(folder))
        {
            _ = Task.Run(() =>
            {
                if (TryDeleteDirectory(folder, attempts: 8)) Log.Info(LogCat, "Session profile folder deleted");
                else Log.Warn(LogCat, "Session profile folder still locked; the startup sweep will retry");
            });
        }
    }

    // ---- Navigation policy -----------------------------------------------------------

    /// <summary>"scheme://host:port" with a lower-case host and an explicit port; null for non-http(s).</summary>
    public static string? OriginOf(Uri u)
    {
        if (!u.IsAbsoluteUri) return null;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
        return u.Scheme + "://" + u.Host.ToLowerInvariant() + ":" + u.Port;
    }

    private static string NormalizedPrefix(Uri u)
    {
        // Scheme/host lower-case, explicit port, then path+query exactly as given.
        return u.Scheme + "://" + u.Host.ToLowerInvariant() + ":" + u.Port + u.PathAndQuery;
    }

    /// <summary>§10.3 navigation rule for top-level and frame navigations.</summary>
    public bool IsNavigationAllowed(string? uriText, out string reason)
    {
        if (string.IsNullOrEmpty(uriText)) { reason = "empty"; return false; }
        if (uriText.Length > 8192) { reason = "too-long"; return false; }
        if (string.Equals(uriText, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            // Allowed only before the first successful load (W-16).
            reason = _firstNavigationSucceeded ? "about-blank-after-load" : string.Empty;
            return !_firstNavigationSucceeded;
        }
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var u)) { reason = "unparsable"; return false; }
        var scheme = u.Scheme.ToLowerInvariant();
        if (scheme == "javascript" || scheme == "data" || scheme == "blob" || scheme == "file") { reason = "scheme-" + scheme; return false; }
        if (scheme != "http" && scheme != "https") { reason = "scheme-" + scheme; return false; }
        if (!string.IsNullOrEmpty(u.UserInfo)) { reason = "userinfo"; return false; }
        if (uriText.Contains("..", StringComparison.Ordinal)) { reason = "dot-segments"; return false; }
        if (_examOrigin == null) { reason = "no-exam-origin"; return false; }
        if (OriginOf(u) == _examOrigin) { reason = string.Empty; return true; }
        var candidate = NormalizedPrefix(u);
        foreach (var prefix in _linkPrefixes)
        {
            if (candidate.StartsWith(prefix, StringComparison.Ordinal)) { reason = string.Empty; return true; }
        }
        reason = "origin-not-allowed";
        return false;
    }

    /// <summary>Subresource rule (W-12): host in allowedDomains (plus the hosts of allowedLinks).</summary>
    public bool IsHostAllowed(string host)
    {
        var h = (host ?? string.Empty).ToLowerInvariant();
        if (h.Length == 0) return false;
        if (_extraAllowedHosts.Contains(h)) return true;
        foreach (var pattern in _policy.AllowedDomains)
        {
            if (HostMatches(h, pattern)) return true;
        }
        return false;
    }

    /// <summary>
    /// Bare public suffixes that must never be used as a wildcard base ("*.com" would allow the
    /// whole internet). Small built-in list (W-29); the server should reject these too.
    /// </summary>
    private static readonly HashSet<string> PublicSuffixes = new HashSet<string>(StringComparer.Ordinal)
    {
        "com", "net", "org", "edu", "gov", "mil", "int", "info", "biz", "io", "co", "app", "dev", "me",
        "ae", "sa", "uk", "us", "au", "in", "de", "fr", "ca", "nl", "eu", "qa", "om", "kw", "bh", "eg", "jo", "pk",
        "co.uk", "org.uk", "ac.uk", "gov.uk", "sch.uk", "com.au", "net.au", "edu.au", "gov.au",
        "ac.ae", "co.ae", "gov.ae", "sch.ae", "com.sa", "edu.sa", "gov.sa", "sch.sa", "co.in", "ac.in", "edu.in",
        "com.br", "co.jp", "co.za", "ac.za", "com.cn", "com.tr", "edu.tr", "com.qa", "edu.qa", "com.eg", "edu.eg",
        "github.io", "herokuapp.com", "azurewebsites.net", "cloudfront.net", "amazonaws.com", "appspot.com",
        "web.app", "firebaseapp.com", "netlify.app", "vercel.app", "pages.dev", "workers.dev", "blob.core.windows.net",
    };

    public static bool HostMatches(string host, string pattern)
    {
        var p = (pattern ?? string.Empty).Trim().ToLowerInvariant();
        if (p.Length == 0 || p.Length > 253) return false;
        if (p.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = p.Substring(2);
            if (!IsAcceptableWildcardSuffix(suffix)) return false;
            return host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal);
        }
        if (p.Contains('*')) return false; // no other wildcard forms
        return host == p;
    }

    /// <summary>W-29: at least two non-empty labels and not a bare public suffix.</summary>
    public static bool IsAcceptableWildcardSuffix(string suffix)
    {
        if (string.IsNullOrEmpty(suffix)) return false;
        var labels = suffix.Split('.');
        if (labels.Length < 2) return false;
        foreach (var l in labels)
        {
            if (l.Length == 0) return false;
        }
        if (PublicSuffixes.Contains(suffix)) return false;
        return true;
    }

    private static string Redact(Uri u)
    {
        // Query strings can carry one-time launch tokens: log origin + path only.
        try { return u.GetLeftPart(UriPartial.Path); } catch { return "?"; }
    }

    private static string Redact(string? uriText)
    {
        if (string.IsNullOrEmpty(uriText)) return "about:invalid";
        return Uri.TryCreate(uriText, UriKind.Absolute, out var u) ? Redact(u) : (uriText.Length > 120 ? uriText.Substring(0, 120) : uriText);
    }

    private void Block(string? uri, string reason)
    {
        var u = Redact(uri);
        Log.Warn(LogCat, "Blocked navigation (" + reason + "): " + u);
        OnBlockedNavigation?.Invoke(u, reason);
    }

    // ---- CoreWebView2 event handlers -------------------------------------------------

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsNavigationAllowed(e.Uri, out var reason))
        {
            e.Cancel = true;
            Block(e.Uri, "navigation-" + reason);
            string host = string.Empty;
            try { if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var bu)) host = bu.Host.ToLowerInvariant(); } catch { /* ignore */ }
            try { OnTopLevelBlocked?.Invoke(host); } catch (Exception ex) { Log.Warn(LogCat, "OnTopLevelBlocked handler failed: " + ex.GetType().Name); }
            return;
        }
        // Allowed top-level navigation: subresource filtering follows the page being opened.
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var nu) && (nu.Scheme == Uri.UriSchemeHttp || nu.Scheme == Uri.UriSchemeHttps))
        {
            _topLevelOnExam = _examOrigin == null || OriginOf(nu) == _examOrigin;
        }
    }

    /// <summary>W-04: frames obey the same rule as the top-level document.</summary>
    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsNavigationAllowed(e.Uri, out var reason))
        {
            e.Cancel = true;
            Block(e.Uri, "frame-" + reason);
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var source = Control?.CoreWebView2?.Source;
        if (e.IsSuccess)
        {
            _firstNavigationSucceeded = true;
            if (!string.IsNullOrEmpty(source) && _examOrigin != null &&
                Uri.TryCreate(source, UriKind.Absolute, out var u) && OriginOf(u) == _examOrigin)
            {
                _lastExamPageUrl = source;
            }
            Log.Info(LogCat, "Loaded " + Redact(source));
        }
        else
        {
            Log.Error(LogCat, "Navigation failed: " + e.WebErrorStatus + " for " + Redact(source));
        }
        try { OnNavigated?.Invoke(); } catch (Exception ex) { Log.Warn(LogCat, "OnNavigated handler failed: " + ex.GetType().Name); }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // W-16: never create a new window and never retarget — popups are simply refused.
        e.Handled = true;
        Block(e.Uri, "popup-blocked");
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        // §10.3: downloads are always refused (policy.allowDownloads is reported, not honoured).
        e.Cancel = true;
        e.Handled = true;
        string? uri = null;
        try { uri = e.DownloadOperation?.Uri; } catch { /* ignore */ }
        Block(uri, _policy.AllowDownloads ? "download-blocked-policy-ignored" : "download-blocked");
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        string? uriText = null;
        try
        {
            uriText = e.Request?.Uri;
            if (string.IsNullOrEmpty(uriText)) return;
            if (!Uri.TryCreate(uriText, UriKind.Absolute, out var u)) { Refuse(e, uriText, "unparsable"); return; }
            var scheme = u.Scheme.ToLowerInvariant();
            if (scheme != "http" && scheme != "https" && scheme != "ws" && scheme != "wss") { Refuse(e, uriText, "scheme-" + scheme); return; }
            // On an allowed resource page (not the exam page) its own assets may come from any host.
            if (!_topLevelOnExam) return;
            if (!IsHostAllowed(u.Host)) { Refuse(e, uriText, "host-not-allowed"); return; }
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "WebResourceRequested handler failed: " + ex.GetType().Name);
            Refuse(e, uriText, "handler-error");
        }
    }

    private void Refuse(CoreWebView2WebResourceRequestedEventArgs e, string? uriText, string reason)
    {
        var env = _environment;
        try
        {
            if (env != null)
            {
                var body = new MemoryStream(Encoding.UTF8.GetBytes("Blocked by exam policy."));
                e.Response = env.CreateWebResourceResponse(body, 403, "Forbidden", "Content-Type: text/plain\r\nCache-Control: no-store");
            }
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "CreateWebResourceResponse failed: " + ex.GetType().Name);
        }
        // Throttle reports per host (a page can fire hundreds of subresource requests).
        var host = "?";
        try { if (Uri.TryCreate(uriText, UriKind.Absolute, out var u)) host = u.Host.ToLowerInvariant(); } catch { /* ignore */ }
        var now = DateTime.UtcNow;
        if (_lastBlockedHost.TryGetValue(host, out var last) && (now - last).TotalSeconds < 60)
        {
            return;
        }
        _lastBlockedHost[host] = now;
        Block(uriText, "subresource-" + reason + " (" + ContextName(e) + ")");
    }

    private static string ContextName(CoreWebView2WebResourceRequestedEventArgs e)
    {
        try { return e.ResourceContext.ToString(); } catch { return "?"; }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // W-05: only the exam origin may talk to the bridge (frames of other origins get the shim
        // too, because document-created scripts run in every frame).
        string? source = null;
        try { source = e.Source; } catch { /* ignore */ }
        var origin = source != null && Uri.TryCreate(source, UriKind.Absolute, out var su) ? OriginOf(su) : null;
        if (_examOrigin == null || origin == null || origin != _examOrigin)
        {
            Log.Warn(LogCat, "bridge: message from non-exam origin ignored (" + (origin ?? "unknown") + ")");
            OnForeignMessage?.Invoke(origin ?? "unknown");
            return;
        }

        string json;
        try
        {
            json = e.WebMessageAsJson;
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "bridge: unreadable message: " + ex.GetType().Name);
            return;
        }
        if (json.Length > 64 * 1024)
        {
            Log.Warn(LogCat, "bridge: oversized message ignored (" + json.Length + " chars)");
            return;
        }
        Bridge.Receive(json);
    }

    private void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
#if DEBUG
        if (!_policy.AllowDevTools) e.Handled = true;
#else
        e.Handled = true; // suppress the menu entirely
#endif
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        // Camera, microphone, geolocation, notifications, clipboard read, ... all denied (§10.3).
        Log.Warn(LogCat, "Permission denied: " + e.PermissionKind + " for " + Redact(e.Uri));
        e.State = CoreWebView2PermissionState.Deny;
    }

    private void OnScriptDialogOpening(object? sender, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        // W-24: rendered as an in-window WPF overlay by the app; the deferral keeps the page waiting.
        var deferral = e.GetDeferral();
        _ = HandleScriptDialogAsync(e, deferral);
    }

    private async Task HandleScriptDialogAsync(CoreWebView2ScriptDialogOpeningEventArgs e, CoreWebView2Deferral deferral)
    {
        try
        {
            var kind = e.Kind;
            var kindName = kind == CoreWebView2ScriptDialogKind.Alert ? "alert"
                : kind == CoreWebView2ScriptDialogKind.Confirm ? "confirm"
                : kind == CoreWebView2ScriptDialogKind.Prompt ? "prompt"
                : "beforeunload";
            var message = e.Message ?? string.Empty;
            if (message.Length > 2000) message = message.Substring(0, 2000);
            var defaultText = kindName == "prompt" ? (e.DefaultText ?? string.Empty) : null;

            var handler = ScriptDialogHandler;
            ScriptDialogResult result;
            if (handler == null)
            {
                result = ScriptDialogResult.Cancelled;
            }
            else
            {
                // Runs on the UI thread (the event is raised there); the continuation stays there.
                result = await handler(new ScriptDialogRequest(kindName, message, defaultText));
            }

            if (result.Accepted)
            {
                if (kindName == "prompt") e.ResultText = result.Text ?? string.Empty;
                e.Accept();
            }
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "Script dialog failed: " + ex.GetType().Name);
        }
        finally
        {
            try { deferral.Complete(); } catch { /* ignore */ }
        }
    }

    private void OnProcessFailedHandler(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        var kind = e.ProcessFailedKind.ToString();
        var details = string.Empty;
        try { details = " reason=" + e.Reason + " exit=" + e.ExitCode; } catch { /* older runtime */ }
        Log.Error(LogCat, "WebView2 process failed: " + kind + details);
        var fatal = e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited;
        var alreadyReloaded = _reloadedAfterFailure;
        if (!fatal && !alreadyReloaded)
        {
            _reloadedAfterFailure = true;
            var dispatcher = Control?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(() =>
            {
                try { Control?.CoreWebView2?.Reload(); } catch (Exception ex) { Log.Warn(LogCat, "Reload failed: " + ex.Message); }
            }), DispatcherPriority.Normal);
        }
        // Fatal when the browser process died or this is the second failure (we only reload once).
        OnProcessFailed?.Invoke(kind, fatal || alreadyReloaded);
    }

    private void OnWindowCloseRequested(object? sender, object e)
    {
        // window.close() from the page: ignored (the native exit flow is the only way out).
        Log.Warn(LogCat, "window.close() ignored");
    }
}
