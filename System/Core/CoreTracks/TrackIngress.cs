using Poseidon.Core.Modules;

namespace Poseidon.Modules.Tracks;

// The publisher side of the ingress design (m1-e-track-fusion.md 21.9, D5): sources register once and
// publish into a bounded queue Tracks owns for them; one consumer thread drains every source's queue round
// robin in small batches, so no source can block another or the manager. A full queue drops its oldest
// report and counts it (R26); the drop also marks the manager's system-wide health as Degraded for 10
// seconds (R26). Tests drive draining deterministically with DrainOnce instead of starting the background
// pump, so they need no polling or waiting.
internal sealed class TrackIngress(TrackManager manager) : ITrackPublisher
{
    private const int BatchSize = 128;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(5);

    private readonly object sync = new();
    private readonly Dictionary<string, SourceQueueState> queues = new(StringComparer.Ordinal);
    private CancellationTokenSource? cancellation;
    private Task? pump;

    public ITrackSink Register(TrackSourceDescriptor descriptor)
    {
        // Real since slice 3: validates the descriptor and stores it, independent of the queue below.
        manager.RegisterSource(descriptor);

        var capacity = Math.Max(4096, descriptor.ExpectedPeakReportsPerSecond * 10);
        var queue = new BoundedDropOldestQueue<Item>(capacity);
        lock (sync)
        {
            if (queues.TryGetValue(descriptor.SourceId, out var previous))
                previous.Sink.Deactivate();
            var sink = new QueuedTrackSink(this, descriptor.SourceId, queue);
            queues[descriptor.SourceId] = new SourceQueueState(queue, sink);
            return sink;
        }
    }

    public void Start()
    {
        lock (sync)
        {
            if (pump is not null)
                return;
            var source = new CancellationTokenSource();
            cancellation = source;
            using (ExecutionContext.SuppressFlow())
                pump = Task.Run(() => PumpAsync(source.Token));
        }
    }

    public async Task StopAsync()
    {
        Task? stopping;
        CancellationTokenSource? source;
        lock (sync)
        {
            stopping = pump;
            source = cancellation;
            pump = null;
            cancellation = null;
        }
        if (stopping is null)
            return;
        source!.Cancel();
        try { await stopping; }
        catch (OperationCanceledException) { }
        source.Dispose();
    }

    private async Task PumpAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!DrainOnce())
            {
                try { await Task.Delay(IdleDelay, token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    // One round-robin pass: up to BatchSize items per source. Returns whether anything was processed, so
    // callers (the pump, or a test) can tell an empty round from a busy one. Internal so tests can drain
    // deterministically without starting the background pump.
    internal bool DrainOnce()
    {
        List<KeyValuePair<string, SourceQueueState>> snapshot;
        lock (sync)
            snapshot = [.. queues];

        var processedAny = false;
        foreach (var (sourceId, state) in snapshot)
        {
            var batch = new List<TrackObservation>();
            for (var i = 0; i < BatchSize && state.Queue.TryDequeue(out var item); i++)
            {
                processedAny = true;
                if (item is ObservationItem observation)
                    batch.Add(observation.Observation);
                else if (item is HealthItem health)
                {
                    if (batch.Count > 0)
                    {
                        manager.SubmitBatch(batch);
                        batch.Clear();
                    }
                    manager.ReportSourceHealth(sourceId, health.Health, health.Detail);
                }
            }
            if (batch.Count > 0)
                manager.SubmitBatch(batch);
            manager.RecordQueueMetrics(sourceId, state.Queue.Count, droppedDelta: 0);
        }
        return processedAny;
    }

    private void OnPublished(string sourceId, int queueDepth, bool dropped)
        => manager.RecordQueueMetrics(sourceId, queueDepth, dropped ? 1 : 0);

    private void ApplyDescriptorUpdate(TrackSourceDescriptor descriptor) => manager.RegisterSource(descriptor);

    private sealed record SourceQueueState(BoundedDropOldestQueue<Item> Queue, QueuedTrackSink Sink);

    private abstract record Item;
    private sealed record ObservationItem(TrackObservation Observation) : Item;
    private sealed record HealthItem(SourceHealth Health, string? Detail) : Item;

    private sealed class QueuedTrackSink(TrackIngress ingress, string sourceId, BoundedDropOldestQueue<Item> queue) : ITrackSink
    {
        private volatile bool active = true;

        public void Deactivate() => active = false;

        public SubmissionResult Publish(TrackObservation observation)
        {
            if (!active)
                return SubmissionResult.Rejected(RejectionReason.Unavailable, "This sink has been replaced.");
            var dropped = queue.Enqueue(new ObservationItem(observation with { SourceId = sourceId }));
            ingress.OnPublished(sourceId, queue.Count, dropped);
            return SubmissionResult.Success;
        }

        public void ReportHealth(SourceHealth health, string? detail = null)
        {
            if (!active)
                return;
            var dropped = queue.Enqueue(new HealthItem(health, detail));
            ingress.OnPublished(sourceId, queue.Count, dropped);
        }

        public void UpdateDescriptor(TrackSourceDescriptor descriptor)
        {
            if (active)
                ingress.ApplyDescriptorUpdate(descriptor);
        }

        public void Dispose() => active = false;
    }

    // A thread-safe queue bounded to a fixed capacity: once full, enqueuing drops the oldest item to make
    // room for the new one instead of blocking or refusing it (R26), because the latest report matters most.
    private sealed class BoundedDropOldestQueue<T>(int capacity)
    {
        private readonly Queue<T> items = new();
        private readonly object sync = new();

        public int Count
        {
            get { lock (sync) return items.Count; }
        }

        // Returns true when this enqueue forced the oldest queued item out.
        public bool Enqueue(T item)
        {
            lock (sync)
            {
                var dropped = items.Count >= capacity;
                if (dropped)
                    items.Dequeue();
                items.Enqueue(item);
                return dropped;
            }
        }

        public bool TryDequeue(out T item)
        {
            lock (sync)
            {
                if (items.Count == 0)
                {
                    item = default!;
                    return false;
                }
                item = items.Dequeue();
                return true;
            }
        }
    }
}
