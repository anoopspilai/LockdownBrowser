using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AvaibeExam.Core;
using AvaibeExam.Models;
using AvaibeExam.Util;

namespace AvaibeExam.Networking;

/// <summary>
/// Thread-safe telemetry queue. Events are flushed in batches of up to 100 every
/// policy.eventFlushIntervalSeconds (clamped 1..300), or on demand. A failed batch stays at the
/// front of the queue and is retried on the next flush. Events recorded before <see cref="Start"/>
/// are held. §10.7: metadata larger than 4 KiB is dropped (counted, replaced by a marker) and the
/// log line for an event contains only type, severity and size — never the metadata (W-25 / W-26).
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
    private int _droppedMetadata;

    public EventReporter(ApiClient api)
    {
        _api = api;
    }

    public int PendingCount
    {
        get { lock (_gate) { return _queue.Count; } }
    }

    /// <summary>Number of events whose metadata exceeded the 4 KiB limit and was dropped.</summary>
    public int DroppedMetadataCount => Volatile.Read(ref _droppedMetadata);

    public bool IsRunning => _cts != null;

    public void Start(string sessionId, int intervalSeconds)
    {
        StopLoop();
        lock (_gate)
        {
            _sessionId = sessionId;
            // W-07: PeriodicTimer throws on a non-positive period; clamp before constructing it.
            _intervalSeconds = Math.Clamp(intervalSeconds, Policy.MinFlushSeconds, Policy.MaxFlushSeconds);
        }
        var cts = new CancellationTokenSource();
        _cts = cts;
        var interval = _intervalSeconds;
        _ = Task.Run(() => LoopAsync(interval, cts.Token));
        Log.Info(LogCat, $"EventReporter started (every {interval}s)");
    }

    private async Task LoopAsync(int intervalSeconds, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(intervalSeconds, 1, 300)));
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
        var size = MetadataSize(metadata);
        if (size > Constants.MaxEventMetadataBytes)
        {
            var dropped = Interlocked.Increment(ref _droppedMetadata);
            metadata = new Dictionary<string, object?>
            {
                ["metadataDropped"] = true,
                ["metadataBytes"] = size,
                ["droppedCount"] = dropped,
            };
            Log.Warn(LogCat, $"event {type}: metadata {size} B > {Constants.MaxEventMetadataBytes} B dropped (#{dropped})");
            size = MetadataSize(metadata);
        }
        var evt = new TelemetryEvent(type, severity, metadata);
        // W-26: type + severity + size only. Metadata may contain page-supplied text.
        Log.Info("security", $"event {type} [{EventSeverityConverter.Instance.ToWire(severity)}] {size}B");
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

    /// <summary>Flushes one batch; returns true when the queue is empty afterwards (or there was nothing to send).</summary>
    public async Task<bool> FlushAsync()
    {
        if (!await _flushLock.WaitAsync(0).ConfigureAwait(false))
        {
            return false; // a flush is already running; it will pick up new events next time
        }
        try
        {
            string? sessionId;
            List<TelemetryEvent> batch;
            lock (_gate)
            {
                if (_sessionId == null || _queue.Count == 0) return _sessionId != null;
                sessionId = _sessionId;
                batch = _queue.Take(BatchSize).ToList();
            }

            try
            {
                var resp = await _api.PostEventsAsync(sessionId, batch).ConfigureAwait(false);
                int remaining;
                lock (_gate)
                {
                    _queue.RemoveRange(0, Math.Min(batch.Count, _queue.Count));
                    remaining = _queue.Count;
                }
                Log.Debug(LogCat, $"Flushed {batch.Count} events, accepted={resp.Accepted?.ToString() ?? "?"}, remaining={remaining}");
                return remaining == 0;
            }
            catch (Exception ex)
            {
                // Keep the batch in the queue for retry.
                Log.Error(LogCat, $"Event flush failed ({batch.Count} kept): {ex.GetType().Name}");
                return false;
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private static int MetadataSize(Dictionary<string, object?>? metadata)
    {
        if (metadata == null || metadata.Count == 0) return 0;
        try
        {
            return Encoding.UTF8.GetByteCount(Json.Serialize(metadata));
        }
        catch
        {
            return int.MaxValue; // unserializable => treated as oversized and dropped
        }
    }
}
