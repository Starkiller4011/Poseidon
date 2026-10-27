using System.Threading.Channels;

namespace Poseidon.Core.Modules;

public sealed record RelayStatistics(
    long Enqueued, long Delivered, long RejectedByTracks, long Invalid, long Overflowed, long Dropped, int Pending,
    string? LastFailure, IReadOnlyDictionary<RejectionReason, long> RejectedByReason);

// A bounded, ordered queue between a sensor adapter and Tracks. Input never blocks the transport: when the
// queue is full the observation is refused with QueueFull. A background pump forwards batches through the
// host, so Tracks stays reachable only via InvokeAsync and a reload can stop the pump promptly.
public sealed class ObservationRelay
{
    private abstract record Item;

    private sealed record ObservationItem(TrackObservation Observation) : Item;

    private sealed record HealthItem(SourceHealth Health, string? Detail) : Item;

    private const int CheckpointSchema = 1;

    private readonly IModuleHost host;
    private readonly string sourceId;
    private readonly int capacity;
    private readonly int batchSize;
    private readonly TimeProvider time;
    private readonly TimeSpan retryDelay;
    private readonly object sync = new();
    private readonly Dictionary<RejectionReason, long> rejectedByReason = [];
    private Channel<Item>? channel;
    private CancellationTokenSource? cancellation;
    private Task? pump;
    private long lastSequence;
    private int pending;
    private long enqueued;
    private long delivered;
    private long rejectedByTracks;
    private long invalid;
    private long overflowed;
    private long dropped;
    private string? lastFailure;

    public ObservationRelay(IModuleHost host, string sourceId, int capacity = 1024, int batchSize = 128,
        TimeProvider? time = null, TimeSpan? retryDelay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        this.host = host;
        this.sourceId = sourceId;
        this.capacity = capacity;
        this.batchSize = batchSize;
        this.time = time ?? TimeProvider.System;
        this.retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(250);
    }

    public RelayStatistics Statistics
    {
        get
        {
            lock (sync)
                return new RelayStatistics(enqueued, delivered, rejectedByTracks, invalid, overflowed, dropped,
                    Volatile.Read(ref pending), lastFailure, new Dictionary<RejectionReason, long>(rejectedByReason));
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (sync)
                return pump is not null;
        }
    }

    // Stamps the source, sequence and receipt time; the caller supplies everything else.
    public SubmissionResult Enqueue(TrackObservation observation)
    {
        lock (sync)
        {
            if (channel is null)
                return SubmissionResult.Rejected(RejectionReason.Unavailable, "The adapter is not running.");
            var stamped = observation with { SourceId = sourceId, ReceivedUtc = time.GetUtcNow(), Sequence = lastSequence + 1 };
            if (TrackObservationRules.Validate(stamped) is { } problem)
            {
                invalid++;
                return SubmissionResult.Rejected(RejectionReason.Invalid, problem);
            }
            if (Volatile.Read(ref pending) >= capacity)
            {
                overflowed++;
                return SubmissionResult.Rejected(RejectionReason.QueueFull,
                    $"{sourceId} has {capacity} observations waiting for Tracks.");
            }
            if (!channel.Writer.TryWrite(new ObservationItem(stamped)))
                return SubmissionResult.Rejected(RejectionReason.Unavailable, "The adapter is stopping.");
            lastSequence++;
            enqueued++;
            Interlocked.Increment(ref pending);
            return SubmissionResult.Success;
        }
    }

    // Health reports are never refused: a disconnect must reach Tracks even when the queue is full.
    public void ReportHealth(SourceHealth health, string? detail = null)
    {
        lock (sync)
            channel?.Writer.TryWrite(new HealthItem(health, detail));
    }

    public void Start()
    {
        lock (sync)
        {
            if (pump is not null)
                return;
            var created = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
            var source = new CancellationTokenSource();
            channel = created;
            cancellation = source;
            // Lifecycle code runs with feature calls blocked; the pump must not inherit that flag.
            using (ExecutionContext.SuppressFlow())
                pump = Task.Run(() => PumpAsync(created.Reader, source.Token));
        }
    }

    public async Task StopAsync()
    {
        Task? stopping;
        Channel<Item>? closing;
        CancellationTokenSource? source;
        lock (sync)
        {
            stopping = pump;
            closing = channel;
            source = cancellation;
            pump = null;
            channel = null;
            cancellation = null;
        }
        if (stopping is null)
            return;

        closing!.Writer.TryComplete();
        source!.Cancel();
        try { await stopping; }
        catch (OperationCanceledException) { }
        source.Dispose();

        var discarded = 0;
        while (closing.Reader.TryRead(out var item))
            if (item is ObservationItem)
                discarded++;
        Drop(discarded);
        Interlocked.Exchange(ref pending, 0);
    }

    private async Task PumpAsync(ChannelReader<Item> reader, CancellationToken token)
    {
        var batch = new List<TrackObservation>(batchSize);
        while (await reader.WaitToReadAsync(token))
        {
            while (reader.TryRead(out var item))
            {
                if (item is ObservationItem observation)
                {
                    batch.Add(observation.Observation);
                    if (batch.Count >= batchSize)
                        await FlushAsync(batch, token);
                    continue;
                }
                await FlushAsync(batch, token);
                var health = (HealthItem)item;
                await SendAsync(i => i.ReportSourceHealth(sourceId, health.Health, health.Detail), token);
            }
            await FlushAsync(batch, token);
        }
    }

    private async Task FlushAsync(List<TrackObservation> batch, CancellationToken token)
    {
        if (batch.Count == 0)
            return;
        var observations = batch.ToArray();
        batch.Clear();
        try
        {
            var results = await host.InvokeAsync<ITrackIngestion, IReadOnlyList<ObservationResult>>(
                TrackPoints.IngestionFeatureId, i => i.SubmitBatch(observations), token);
            lock (sync)
            {
                foreach (var result in results)
                {
                    if (result.IsAccepted)
                        delivered++;
                    else
                    {
                        rejectedByTracks++;
                        rejectedByReason[result.Reason] = rejectedByReason.GetValueOrDefault(result.Reason) + 1;
                    }
                }
            }
            Interlocked.Add(ref pending, -observations.Length);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // StopAsync accounts for the observations still queued; this batch was already taken from the queue.
            Interlocked.Add(ref pending, -observations.Length);
            Drop(observations.Length, null);
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Add(ref pending, -observations.Length);
            Drop(observations.Length, ex.Message);
            await Task.Delay(retryDelay, token);
        }
    }

    private async Task SendAsync(Action<ITrackIngestion> action, CancellationToken token)
    {
        try
        {
            await host.InvokeAsync<ITrackIngestion, bool>(TrackPoints.IngestionFeatureId, i =>
            {
                action(i);
                return true;
            }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (sync)
                lastFailure = ex.Message;
            await Task.Delay(retryDelay, token);
        }
    }

    private void Drop(int count, string? failure = null)
    {
        if (count == 0 && failure is null)
            return;
        lock (sync)
        {
            dropped += count;
            if (failure is not null)
                lastFailure = failure;
        }
    }

    public ModuleState? Capture()
    {
        lock (sync)
            return lastSequence == 0 ? null : new ModuleState(CheckpointSchema, BitConverter.GetBytes(lastSequence));
    }

    public void Restore(ModuleState? state)
    {
        if (state is null)
            return;
        if (state.SchemaVersion != CheckpointSchema || state.Data.Length != sizeof(long))
            throw new InvalidDataException("Unsupported relay checkpoint.");
        lock (sync)
            lastSequence = BitConverter.ToInt64(state.Data);
    }
}
