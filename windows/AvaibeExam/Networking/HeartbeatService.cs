using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using AvaibeExam.Models;
using AvaibeExam.Util;

namespace AvaibeExam.Networking;

/// <summary>
/// Sends POST /sessions/:id/heartbeat from a background task and hands every server command
/// (RELEASE / TERMINATE / WARN) to the app on the UI thread, where it is verified (§10.4).
///
/// Timing (§10.6): the interval is the server's <c>nextBeatInSeconds</c> when present (clamped
/// 2..120), else the policy interval; ±20 % jitter is added to every wait. While beats fail the
/// wait backs off exponentially (base × 2^(failures−1), max 60 s). The service also tracks how long
/// the streak of failures has lasted so the app can apply the offline-grace failsafe (W-08) and
/// show the elapsed offline time. Beats never overlap. Tokens are never logged.
/// </summary>
public sealed class HeartbeatService
{
    private const string LogCat = "heartbeat";
    private const int MaxBackoffSeconds = 60;

    public sealed class Snapshot
    {
        public Snapshot(HeartbeatStatus status, int displayCount, LockdownMode lockdownMode)
        {
            Status = status;
            DisplayCount = displayCount;
            LockdownMode = lockdownMode;
        }

        public HeartbeatStatus Status { get; }
        public int DisplayCount { get; }
        public LockdownMode LockdownMode { get; }
    }

    private readonly ApiClient _api;
    private readonly Dispatcher _dispatcher;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly SemaphoreSlim _beatLock = new SemaphoreSlim(1, 1);
    private readonly Random _random = new Random();
    private readonly object _gate = new object();
    private CancellationTokenSource? _cts;
    private string? _sessionId;
    private int _baseIntervalSeconds = 10;
    private int _serverIntervalSeconds;      // 0 = none received yet
    private int _consecutiveFailures;
    private DateTime? _offlineSince;

    /// <summary>Supplies the current client state for each beat (invoked on the UI thread).</summary>
    public Func<Snapshot>? SnapshotProvider { get; set; }
    /// <summary>Receives commands from the server (UI thread). The app verifies signatures.</summary>
    public Action<HeartbeatCommand>? OnCommand { get; set; }
    /// <summary>Receives the server-side remaining seconds, raw (UI thread; the app clamps).</summary>
    public Action<int>? OnRemainingSeconds { get; set; }
    /// <summary>true/false when a heartbeat succeeds/fails (UI thread).</summary>
    public Action<bool>? OnResult { get; set; }
    /// <summary>The server's ISO-8601 serverTime from a successful beat (UI thread).</summary>
    public Action<string>? OnServerTime { get; set; }
    /// <summary>(consecutiveFailures, offlineSeconds) after every failed beat (UI thread).</summary>
    public Action<int, int>? OnOffline { get; set; }
    /// <summary>A beat succeeded after one or more failures (UI thread).</summary>
    public Action? OnBackOnline { get; set; }

    public int ConsecutiveFailures
    {
        get { lock (_gate) { return _consecutiveFailures; } }
    }

    /// <summary>
    /// Seconds without a successful heartbeat: the larger of (failures × base interval) and the
    /// wall-clock time since the first failure of the streak (back-off makes the former undercount).
    /// 0 when the last beat succeeded.
    /// </summary>
    public int OfflineSeconds
    {
        get
        {
            lock (_gate)
            {
                if (_consecutiveFailures == 0 || !_offlineSince.HasValue) return 0;
                var byCount = (long)_consecutiveFailures * _baseIntervalSeconds;
                var byClock = (long)Math.Round((DateTime.UtcNow - _offlineSince.Value).TotalSeconds);
                return (int)Math.Clamp(Math.Max(byCount, byClock), 0, 86400);
            }
        }
    }

    public HeartbeatService(ApiClient api, Dispatcher dispatcher)
    {
        _api = api;
        _dispatcher = dispatcher;
    }

    /// <param name="intervalSeconds">policy.heartbeatIntervalSeconds (already clamped by Policy.Normalized).</param>
    /// <param name="serverNextBeatSeconds">nextBeatInSeconds from the session-start response, if any.</param>
    public void Start(string sessionId, int intervalSeconds, int? serverNextBeatSeconds)
    {
        Stop();
        _sessionId = sessionId;
        lock (_gate)
        {
            _baseIntervalSeconds = Math.Clamp(intervalSeconds, Policy.MinHeartbeatSeconds, Policy.MaxHeartbeatSeconds);
            _serverIntervalSeconds = ClampServerInterval(serverNextBeatSeconds);
            _consecutiveFailures = 0;
            _offlineSince = null;
        }
        var cts = new CancellationTokenSource();
        _cts = cts;
        Log.Info(LogCat, $"Heartbeat started every {_baseIntervalSeconds}s (server hint {(_serverIntervalSeconds > 0 ? _serverIntervalSeconds.ToString() : "none")})");
        _ = Task.Run(() => LoopAsync(sessionId, cts.Token));
    }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        if (cts != null)
        {
            try { cts.Cancel(); } catch { /* ignore */ }
            cts.Dispose();
        }
    }

    /// <summary>Send one heartbeat right now (e.g. after submit, to pick up the RELEASE sooner).</summary>
    public void BeatNow()
    {
        var sessionId = _sessionId;
        if (sessionId == null) return;
        _ = Task.Run(() => BeatAsync(sessionId));
    }

    private static int ClampServerInterval(int? value)
    {
        if (!value.HasValue) return 0;
        return Math.Clamp(value.Value, Policy.MinHeartbeatSeconds, Policy.MaxHeartbeatSeconds);
    }

    /// <summary>Next wait in milliseconds: server/base interval or back-off, with ±20 % jitter.</summary>
    private int NextDelayMs()
    {
        double seconds;
        lock (_gate)
        {
            if (_consecutiveFailures > 0)
            {
                // base × 2^(failures−1), capped at 60 s (§10.6).
                var exponent = Math.Min(_consecutiveFailures - 1, 6);
                seconds = Math.Min(MaxBackoffSeconds, _baseIntervalSeconds * Math.Pow(2, exponent));
            }
            else
            {
                seconds = _serverIntervalSeconds > 0 ? _serverIntervalSeconds : _baseIntervalSeconds;
            }
        }
        var jitter = 0.8 + 0.4 * _random.NextDouble();
        var ms = seconds * jitter * 1000.0;
        return (int)Math.Clamp(ms, 1000, 150_000);
    }

    private async Task LoopAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            // First beat immediately so the teacher console sees the session quickly.
            await BeatAsync(sessionId).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(NextDelayMs(), ct).ConfigureAwait(false);
                await BeatAsync(sessionId).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
        catch (Exception ex)
        {
            Log.Error(LogCat, "Heartbeat loop crashed", ex);
        }
    }

    private async Task BeatAsync(string sessionId)
    {
        if (!await _beatLock.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var provider = SnapshotProvider;
            if (provider == null) return;

            Snapshot snapshot = await _dispatcher.InvokeAsync(provider);
            var request = new HeartbeatRequest
            {
                Status = snapshot.Status,
                DisplayCount = snapshot.DisplayCount,
                LockdownMode = snapshot.LockdownMode,
                UptimeSeconds = (int)Math.Clamp((DateTime.UtcNow - _startedAt).TotalSeconds, 0, int.MaxValue),
            };

            HeartbeatResponse response;
            try
            {
                response = await _api.HeartbeatAsync(sessionId, request).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                int failures;
                int offline;
                lock (_gate)
                {
                    _consecutiveFailures++;
                    if (!_offlineSince.HasValue) _offlineSince = DateTime.UtcNow;
                    failures = _consecutiveFailures;
                }
                offline = OfflineSeconds;
                Log.Error(LogCat, $"Heartbeat failed (#{failures}, offline {offline}s): {ex.GetType().Name}");
                await _dispatcher.InvokeAsync(() =>
                {
                    OnResult?.Invoke(false);
                    OnOffline?.Invoke(failures, offline);
                });
                return;
            }

            bool wasOffline;
            lock (_gate)
            {
                wasOffline = _consecutiveFailures > 0;
                _consecutiveFailures = 0;
                _offlineSince = null;
                var next = ClampServerInterval(response.NextBeatInSeconds);
                if (next > 0) _serverIntervalSeconds = next;
            }
            await _dispatcher.InvokeAsync(() =>
            {
                OnResult?.Invoke(true);
                if (wasOffline) OnBackOnline?.Invoke();
                if (!string.IsNullOrEmpty(response.ServerTime)) OnServerTime?.Invoke(response.ServerTime!);
                if (response.RemainingSeconds.HasValue)
                {
                    OnRemainingSeconds?.Invoke(response.RemainingSeconds.Value);
                }
                var cmd = response.Command;
                if (cmd != null && cmd.Type != HeartbeatCommandType.Unknown)
                {
                    Log.Info(LogCat, "Heartbeat command: " + HeartbeatCommandTypeConverter.Instance.ToWire(cmd.Type));
                    OnCommand?.Invoke(cmd);
                }
            });
        }
        catch (TaskCanceledException)
        {
            // dispatcher shutting down
        }
        catch (Exception ex)
        {
            Log.Error(LogCat, "Beat failed", ex);
        }
        finally
        {
            _beatLock.Release();
        }
    }
}
