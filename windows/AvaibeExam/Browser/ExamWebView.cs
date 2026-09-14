using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AvaibeExam.Core;
using AvaibeExam.Models;
using AvaibeExam.Util;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AvaibeExam.Browser;

/// <summary>
/// Owns the WebView2 control for the exam, its navigation policy and the message bridge.
/// Restrictions per CONTRACT §9.3: no context menu, no dev tools, no downloads, no new windows,
/// no zoom, no print (accelerators off), no drag-drop, status bar off, autofill off, PDF toolbar
/// stripped. Navigation is default-deny per policy.allowedDomains (exact host or "*.suffix";
/// about:blank allowed). All permission prompts (camera, mic, geolocation, ...) are denied.
///
/// Lifecycle: CreateControl(policy, mode) -> (host in the visual tree) -> InitializeAsync() ->
/// Load(url) -> Teardown().
/// </summary>
public sealed class ExamWebView
{
    private const string LogCat = "web";

    private Policy _policy = Policy.ConservativeDefault;
    private bool _reloadedAfterFailure;
    private bool _initialized;

    public WebView2? Control { get; private set; }
    public WebMessageBridge Bridge { get; } = new WebMessageBridge();
    public LockdownMode LockdownMode { get; private set; } = AvaibeExam.Models.LockdownMode.None;

    /// <summary>(url, reason) — a navigation, popup or download was refused.</summary>
    public Action<string, string>? OnBlockedNavigation { get; set; }
    /// <summary>(kind, fatal) — the browser/render process failed.</summary>
    public Action<string, bool>? OnProcessFailed { get; set; }
    /// <summary>CoreWebView2 is ready (used to re-apply capture protection to the top-level window).</summary>
    public Action? OnInitialized { get; set; }

    public bool IsInitialized => _initialized;

    // ---- Lifecycle -------------------------------------------------------------------

    /// <summary>Creates the WPF control (must be added to the visual tree before InitializeAsync completes).</summary>
    public WebView2 CreateControl(Policy policy, LockdownMode mode)
    {
        Teardown();
        _policy = policy;
        LockdownMode = mode;
        _reloadedAfterFailure = false;
        _initialized = false;

        var control = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.Black,
        };
        Control = control;
        Log.Info(LogCat, "WebView2 control created (mode=" + mode.Wire() + ", devTools=" + policy.AllowDevTools + ")");
        return control;
    }

    /// <summary>Creates the environment, initializes CoreWebView2 and applies every restriction.</summary>
    public async Task InitializeAsync()
    {
        var control = Control;
        if (control == null) throw new InvalidOperationException("CreateControl must be called first");

        Directory.CreateDirectory(Constants.WebView2UserDataFolder);
        // Keep the browser arguments minimal on purpose; every restriction is applied through
        // the documented Settings API below.
        var options = new CoreWebView2EnvironmentOptions();
        // Positional arguments: (browserExecutableFolder, userDataFolder, options).
        var environment = await CoreWebView2Environment.CreateAsync(null, Constants.WebView2UserDataFolder, options);

        await control.EnsureCoreWebView2Async(environment);
        var core = control.CoreWebView2;
        if (core == null) throw new InvalidOperationException("CoreWebView2 failed to initialize");

        // Core restrictions (present in every runtime the SDK supports) — must not fail silently.
        var s = core.Settings;
        s.AreDevToolsEnabled = _policy.AllowDevTools;
        s.AreDefaultContextMenusEnabled = _policy.AllowDevTools;
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
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.WebMessageReceived += OnWebMessageReceived;
        core.ContextMenuRequested += OnContextMenuRequested;
        core.PermissionRequested += OnPermissionRequested;
        core.ScriptDialogOpening += OnScriptDialogOpening;
        core.ProcessFailed += OnProcessFailedHandler;
        core.WindowCloseRequested += OnWindowCloseRequested;

        await core.AddScriptToExecuteOnDocumentCreatedAsync(WebMessageBridge.ShimScript(LockdownMode));
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

    public void Load(Uri url)
    {
        var core = Control?.CoreWebView2;
        if (core == null)
        {
            Log.Error(LogCat, "Load called before CoreWebView2 is ready");
            return;
        }
        if (!IsAllowed(url))
        {
            Log.Error(LogCat, "Refusing to load exam URL not in allowedDomains: " + url);
            OnBlockedNavigation?.Invoke(url.ToString(), "exam-url-not-allowed");
            return;
        }
        Log.Info(LogCat, "Loading " + url);
        core.Navigate(url.ToString());
    }

    public void Teardown()
    {
        var control = Control;
        Control = null;
        Bridge.ScriptExecutor = null;
        _initialized = false;
        if (control == null) return;
        try
        {
            var core = control.CoreWebView2;
            if (core != null)
            {
                core.NavigationStarting -= OnNavigationStarting;
                core.NavigationCompleted -= OnNavigationCompleted;
                core.NewWindowRequested -= OnNewWindowRequested;
                core.DownloadStarting -= OnDownloadStarting;
                core.WebMessageReceived -= OnWebMessageReceived;
                core.ContextMenuRequested -= OnContextMenuRequested;
                core.PermissionRequested -= OnPermissionRequested;
                core.ScriptDialogOpening -= OnScriptDialogOpening;
                core.ProcessFailed -= OnProcessFailedHandler;
                core.WindowCloseRequested -= OnWindowCloseRequested;
                try { core.Stop(); } catch { /* ignore */ }
            }
            control.Dispose();
            Log.Info(LogCat, "WebView2 disposed");
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "Teardown failed: " + ex.Message);
        }
    }

    // ---- Navigation policy -----------------------------------------------------------

    public bool IsAllowed(Uri url)
    {
        if (string.Equals(url.OriginalString, "about:blank", StringComparison.OrdinalIgnoreCase)) return true;
        if (!url.IsAbsoluteUri) return false;
        var scheme = url.Scheme.ToLowerInvariant();
        if (scheme != "http" && scheme != "https") return false;
        var host = url.Host.ToLowerInvariant();
        if (string.IsNullOrEmpty(host)) return false;
        foreach (var pattern in _policy.AllowedDomains)
        {
            if (HostMatches(host, pattern)) return true;
        }
        return false;
    }

    public static bool HostMatches(string host, string pattern)
    {
        var p = (pattern ?? string.Empty).Trim().ToLowerInvariant();
        if (p.Length == 0) return false;
        if (p.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = p.Substring(2);
            return host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal);
        }
        return host == p;
    }

    private bool IsAllowedString(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return false;
        return Uri.TryCreate(uri, UriKind.Absolute, out var u) && IsAllowed(u);
    }

    private void Block(string? uri, string reason)
    {
        var u = uri ?? "about:invalid";
        Log.Warn(LogCat, "Blocked navigation (" + reason + "): " + u);
        OnBlockedNavigation?.Invoke(u, reason);
    }

    // ---- CoreWebView2 event handlers -------------------------------------------------

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsAllowedString(e.Uri))
        {
            e.Cancel = true;
            Block(e.Uri, "domain-not-allowed");
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            Log.Info(LogCat, "Loaded " + (Control?.CoreWebView2?.Source ?? "?"));
        }
        else
        {
            Log.Error(LogCat, "Navigation failed: " + e.WebErrorStatus);
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Never create a new window. If the target is allowed, load it in the same view.
        e.Handled = true;
        if (IsAllowedString(e.Uri))
        {
            Log.Info(LogCat, "Popup redirected into exam view: " + e.Uri);
            try { Control?.CoreWebView2?.Navigate(e.Uri); } catch (Exception ex) { Log.Warn(LogCat, "Popup navigate failed: " + ex.Message); }
        }
        else
        {
            Block(e.Uri, "popup-blocked");
        }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        if (_policy.AllowDownloads)
        {
            e.Handled = true; // no download UI even when allowed
            return;
        }
        e.Cancel = true;
        e.Handled = true;
        string? uri = null;
        try { uri = e.DownloadOperation?.Uri; } catch { /* ignore */ }
        Block(uri, "download-blocked");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try
        {
            json = e.WebMessageAsJson;
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "bridge: unreadable message: " + ex.Message);
            return;
        }
        Bridge.Receive(json);
    }

    private void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        if (!_policy.AllowDevTools)
        {
            e.Handled = true; // suppress the menu entirely
        }
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        // Camera, microphone, geolocation, notifications, clipboard read, ... all denied.
        Log.Warn(LogCat, "Permission denied: " + e.PermissionKind + " for " + e.Uri);
        e.State = CoreWebView2PermissionState.Deny;
    }

    private void OnScriptDialogOpening(object? sender, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        // Show the dialog outside the event callback (no nested message loop inside WebView2
        // callbacks) using a deferral.
        var deferral = e.GetDeferral();
        var owner = Control != null ? Window.GetWindow(Control) : null;
        var dispatcher = Control?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var kind = e.Kind;
                var message = e.Message ?? string.Empty;
                if (kind == CoreWebView2ScriptDialogKind.Alert)
                {
                    ShowMessage(owner, message, MessageBoxButton.OK);
                    e.Accept();
                }
                else if (kind == CoreWebView2ScriptDialogKind.Confirm || kind == CoreWebView2ScriptDialogKind.Beforeunload)
                {
                    var result = ShowMessage(owner, message, MessageBoxButton.OKCancel);
                    if (result == MessageBoxResult.OK) e.Accept();
                }
                else
                {
                    // Prompt: no native text prompt in WPF; OK returns the default text, Cancel returns null.
                    var result = ShowMessage(owner, message + "\n\n(Default: " + (e.DefaultText ?? string.Empty) + ")", MessageBoxButton.OKCancel);
                    if (result == MessageBoxResult.OK)
                    {
                        e.ResultText = e.DefaultText ?? string.Empty;
                        e.Accept();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(LogCat, "Script dialog failed: " + ex.Message);
            }
            finally
            {
                deferral.Complete();
            }
        }), DispatcherPriority.Normal);
    }

    private static MessageBoxResult ShowMessage(Window? owner, string message, MessageBoxButton buttons)
    {
        return owner != null
            ? System.Windows.MessageBox.Show(owner, message, "Exam", buttons, MessageBoxImage.Information)
            : System.Windows.MessageBox.Show(message, "Exam", buttons, MessageBoxImage.Information);
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
