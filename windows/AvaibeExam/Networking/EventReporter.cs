using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AvaibeExam.Models;
using AvaibeExam.Util;

namespace AvaibeExam.Networking;

/// <summary>
/// Thread-safe telemetry queue. Events are flushed in batches of up to 100 every
/// policy.eventFlushIntervalSeconds, or on demand. A failed batch stays at the front of the
/// queue and is retried on the next flush. Events recorded before <see cref="Start"/> are held.
/// </summary>
public sealed class EventReporter
{
    private const string LogCat = "events";
    private const int MaxQueue = 2000;
    private const int BatchSize = 100;

    private readonly ApiClient _api;
    private readonly object _gate = new object();
    private readonly List<TelemetryEvent> _queue = new List<TelemetryEvent>();
    private readonly SemaphoreSlim _flushLock = new SemaphoreSlim(1, 1);
    private string? _sessionId;
    private int _intervalSeconds = 5;
    private CancellationTokenSource? _cts;

    public EventReporter(ApiClient api)
    {
        _api = api;
    }

    public int PendingCount
    {
        get { lock (_gate) { return _queue.Count; } }
    }

    public void Start(string sessionId, int intervalSeconds)
    {
        StopLoop();
        lock (_gate)
        {
            _sessionId = sessionId;
            _intervalSeconds = Math.Max(1, intervalSeconds);
        }
        var cts = new CancellationTokenSource();
        _cts = cts;
        var interval = _intervalSeconds;
        _ = Task.Run(() => LoopAsync(interval, cts.Token));
        Log.Info(LogCat, $"EventReporter started for {sessionId} (every {interval}s)");
    }

    private async Task LoopAsync(int intervalSeconds, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await FlushAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
        catch (Exception ex)
        {
            Log.Error(LogCat, "Flush loop crashed", ex);
        }
    }

    private void StopLoop()
    {
        var cts = _cts;
        _cts = null;
        if (cts != null)
        {
            try { cts.Cancel(); } catch { /* ignore */ }
            cts.Dispose();
        }
    }

    /// <summary>Stops the periodic flush and flushes whatever is left.</summary>
    public async Task StopAsync()
    {
        StopLoop();
        await FlushAsync().ConfigureAwait(false);
    }

    public void Record(string type, EventSeverity severity = EventSeverity.Info, Dictionary<string, object?>? metadata = null)
    {
        var evt = new TelemetryEvent(type, severity, metadata);
        Log.Info("security", $"event {type} [{EventSeverityConverter.Instance.ToWire(severity)}] {DescribeMetadata(metadata)}");
        lock (_gate)
        {
            _queue.Add(evt);
            if (_queue.Count > MaxQueue)
            {
                _queue.RemoveRange(0, _queue.Count - MaxQueue);
            }
        }
    }

    /// <summary>Record and flush immediately (lockdown state changes and other high-value events).</summary>
    public void RecordNow(string type, EventSeverity severity = EventSeverity.Info, Dictionary<string, object?>? metadata = null)
    {
        Record(type, severity, metadata);
        _ = FlushSafeAsync();
    }

    private async Task FlushSafeAsync()
    {
        try
        {
            await FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(LogCat, "Flush failed unexpectedly", ex);
        }
    }

    public async Task FlushAsync()
    {
        if (!await _flushLock.WaitAsync(0).ConfigureAwait(false))
        {
            return; // a flush is already running; it will pick up new events next time
        }
        try
        {
            string? sessionId;
            List<TelemetryEvent> batch;
            lock (_gate)
            {
                if (_sessionId == null || _queue.Count == 0) return;
                sessionId = _sessionId;
                batch = _queue.Take(BatchSize).ToList();
            }

            try
            {
                var resp = await _api.PostEventsAsync(sessionId, batch).ConfigureAwait(false);
                lock (_gate)
                {
                    _queue.RemoveRange(0, Math.Min(batch.Count, _queue.Count));
                }
                Log.Debug(LogCat, $"Flushed {batch.Count} events, accepted={resp.Accepted?.ToString() ?? "?"}");
            }
            catch (Exception ex)
            {
                // Keep the batch in the queue for retry.
                Log.Error(LogCat, $"Event flush failed ({batch.Count} kept): {ex.Message}");
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private static string DescribeMetadata(Dictionary<string, object?>? metadata)
    {
        if (metadata == null || metadata.Count == 0) return string.Empty;
        try
        {
            return Json.Serialize(metadata);
        }
        catch
        {
            return "{...}";
        }
    }
}
