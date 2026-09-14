using System;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using AvaibeExam.Core;
using AvaibeExam.Networking;
using AvaibeExam.Util;

namespace AvaibeExam.Security;

/// <summary>
/// NetworkChange.NetworkAvailabilityChanged plus a periodic GET /healthz probe. Reports
/// connectivity transitions (UI thread) which the app turns into NETWORK_OFFLINE / NETWORK_ONLINE
/// events and the status-strip dot. "Online" = an interface is up OR the backend answered.
/// </summary>
public sealed class NetworkMonitor
{
    private const string LogCat = "network";
    private readonly Dispatcher _dispatcher;
    private readonly ApiClient _api;
    private CancellationTokenSource? _cts;
    private bool _started;

    public bool IsOnline { get; private set; } = SafeIsNetworkAvailable();

    /// <summary>Called on the UI thread when connectivity changes.</summary>
    public Action<bool>? OnChange { get; set; }

    public NetworkMonitor(Dispatcher dispatcher, ApiClient api)
    {
        _dispatcher = dispatcher;
        _api = api;
    }

    private static bool SafeIsNetworkAvailable()
    {
        try
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch
        {
            return true;
        }
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        IsOnline = SafeIsNetworkAvailable();
        try
        {
            NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "NetworkChange unavailable: " + ex.Message);
        }
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = Task.Run(() => ProbeLoopAsync(cts.Token));
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        try
        {
            NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        }
        catch
        {
            // ignore
        }
        var cts = _cts;
        _cts = null;
        if (cts != null)
        {
            try { cts.Cancel(); } catch { /* ignore */ }
            cts.Dispose();
        }
    }

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        var online = e.IsAvailable;
        _dispatcher.BeginInvoke(new Action(() => Apply(online)), DispatcherPriority.Normal);
    }

    private async Task ProbeLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Constants.NetworkProbeIntervalSeconds));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                bool online;
                try
                {
                    await _api.HealthzAsync().ConfigureAwait(false);
                    online = true;
                }
                catch
                {
                    online = SafeIsNetworkAvailable();
                }
                await _dispatcher.InvokeAsync(() => Apply(online));
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "Network probe loop ended: " + ex.Message);
        }
    }

    private void Apply(bool online)
    {
        if (!_started) return;
        if (online == IsOnline) return;
        IsOnline = online;
        Log.Info(LogCat, online ? "Network online" : "Network offline");
        OnChange?.Invoke(online);
    }

    /// <summary>One-shot snapshot for the preflight report.</summary>
    public static bool Snapshot() => SafeIsNetworkAvailable();
}
