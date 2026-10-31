namespace Poseidon.Core.Modules;

// Replaces ObservationRelay's role for a single source under the publisher design (m1-e-track-fusion.md 21.3,
// 21.9, R23): once Tracks is available, a background task registers with ITrackPublisher and holds the
// resulting sink until the source stops. Sources have no queue of their own; TrackIngress owns it. Publishing
// never waits for Tracks to be reachable: before registration, Publish is refused as Unavailable.
public sealed class TrackSourceConnector
{
    private readonly IModuleHost host;
    private readonly TimeSpan retryDelay;
    private readonly object sync = new();
    private TrackSourceDescriptor descriptor;
    private ITrackSink? sink;
    private CancellationTokenSource? cancellation;
    private Task? loop;

    public TrackSourceConnector(IModuleHost host, TrackSourceDescriptor descriptor, TimeSpan? retryDelay = null)
    {
        if (descriptor.Validate() is { } problem)
            throw new ArgumentException(problem, nameof(descriptor));
        this.host = host;
        this.descriptor = descriptor;
        this.retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(200);
    }

    public ITrackSink? Sink
    {
        get { lock (sync) return sink; }
    }

    public SubmissionResult Publish(TrackObservation observation)
    {
        var current = Sink;
        return current is null
            ? SubmissionResult.Rejected(RejectionReason.Unavailable, "This source has not registered with Tracks yet.")
            : current.Publish(observation);
    }

    public void ReportHealth(SourceHealth health, string? detail = null) => Sink?.ReportHealth(health, detail);

    public void UpdateDescriptor(TrackSourceDescriptor descriptor)
    {
        if (descriptor.Validate() is { } problem)
            throw new ArgumentException(problem, nameof(descriptor));
        lock (sync)
        {
            this.descriptor = descriptor;
            sink?.UpdateDescriptor(descriptor);
        }
    }

    public void Start()
    {
        lock (sync)
        {
            if (loop is not null)
                return;
            var source = new CancellationTokenSource();
            cancellation = source;
            // Lifecycle code runs with feature calls blocked; the loop must not inherit that flag.
            using (ExecutionContext.SuppressFlow())
                loop = Task.Run(() => RegisterLoopAsync(source.Token));
        }
    }

    public async Task StopAsync()
    {
        Task? stopping;
        CancellationTokenSource? source;
        ITrackSink? current;
        lock (sync)
        {
            stopping = loop;
            source = cancellation;
            loop = null;
            cancellation = null;
            current = sink;
            sink = null;
        }
        if (stopping is null)
            return;
        source!.Cancel();
        try { await stopping; }
        catch (OperationCanceledException) { }
        source.Dispose();
        current?.Dispose();
    }

    // Retries Register against tracks.publisher every retryDelay until it succeeds (21.9, R23). Runs on a
    // background task because lifecycle methods cannot make feature calls; stops retrying once registered or
    // once StopAsync cancels it.
    private async Task RegisterLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TrackSourceDescriptor current;
            lock (sync)
                current = descriptor;
            try
            {
                var registered = await host.InvokeAsync<ITrackPublisher, ITrackSink>(
                    TrackPoints.PublisherFeatureId, p => p.Register(current), token);
                lock (sync)
                {
                    if (token.IsCancellationRequested)
                    {
                        registered.Dispose();
                        return;
                    }
                    sink = registered;
                    // UpdateDescriptor may have run between the read above and this registration completing;
                    // reconcile so that change is never silently lost (R25: "the new timings apply immediately").
                    if (descriptor != current)
                        registered.UpdateDescriptor(descriptor);
                }
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                try { await Task.Delay(retryDelay, token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
