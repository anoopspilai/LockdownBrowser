using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using AvaibeExam.Models;
using AvaibeExam.Util;

namespace AvaibeExam.Networking;

/// <summary>
/// Sends POST /sessions/:id/heartbeat every policy.heartbeatIntervalSeconds from a background
/// task and hands any server command (RELEASE / TERMINATE / WARN) to the app on the UI thread.
/// A RELEASE is delivered exactly once by the server, so each response is fully processed on
/// the dispatcher before the next beat is sent (beats never overlap).
/// </summary>
public sealed class HeartbeatService
{
    private const string LogCat = "heartbeat";

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
    private CancellationTokenSource? _cts;
    private string? _sessionId;

    /// <summary>Supplies the current client state for each beat (invoked on the UI thread).</summary>
    public Func<Snapshot>? SnapshotProvider { get; set; }
    /// <summary>Receives commands from the server (UI thread).</summary>
    public Action<HeartbeatCommand>? OnCommand { get; set; }
    /// <summary>Receives the server-side remaining seconds (UI thread).</summary>
    public Action<int>? OnRemainingSeconds { get; set; }
    /// <summary>true/false when a heartbeat succeeds/fails (UI thread).</summary>
    public Action<bool>? OnResult { get; set; }

    public int ConsecutiveFailures { get; private set; }

    public HeartbeatService(ApiClient api, Dispatcher dispatcher)
    {
        _api = api;
        _dispatcher = dispatcher;
    }

    public void Start(string sessionId, int intervalSeconds)
    {
        Stop();
        _sessionId = sessionId;
        var interval = Math.Max(2, intervalSeconds);
        var cts = new CancellationTokenSource();
        _cts = cts;
        Log.Info(LogCat, $"Heartbeat started for {sessionId} every {interval}s");
        _ = Task.Run(() => LoopAsync(sessionId, interval, cts.Token));
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

    private async Task LoopAsync(string sessionId, int intervalSeconds, CancellationToken ct)
    {
        try
        {
            // First beat immediately so the teacher console sees the session quickly.
            await BeatAsync(sessionId).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
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
                UptimeSeconds = (int)(DateTime.UtcNow - _startedAt).TotalSeconds,
            };

            HeartbeatResponse response;
            try
            {
                response = await _api.HeartbeatAsync(sessionId, request).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ConsecutiveFailures++;
                Log.Error(LogCat, $"Heartbeat failed (#{ConsecutiveFailures}): {ex.Message}");
                await _dispatcher.InvokeAsync(() => { OnResult?.Invoke(false); });
                return;
            }

            ConsecutiveFailures = 0;
            await _dispatcher.InvokeAsync(() =>
            {
                OnResult?.Invoke(true);
                if (response.RemainingSeconds.HasValue)
                {
                    OnRemainingSeconds?.Invoke(response.RemainingSeconds.Value);
                }
                var cmd = response.Command;
                if (cmd != null && cmd.Type != HeartbeatCommandType.Unknown)
                {
                    Log.Info(LogCat, $"Heartbeat command: {cmd.Type}");
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
