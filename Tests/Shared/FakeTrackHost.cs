using Poseidon.Core.Modules;

namespace Poseidon.Tests.Shared;

// A test double for the publisher side of the ingress design (21.9): stands in for TrackIngress so adapter
// tests can exercise registration and publishing without a real TrackManager/queue.
public sealed class RecordingPublisher : ITrackPublisher
{
    private readonly object gate = new();
    private readonly List<string> calls = [];
    private readonly List<TrackObservation> observations = [];
    private readonly Dictionary<string, TrackSourceDescriptor> lastDescriptors = new(StringComparer.Ordinal);

    // Decides the result for each observation; by default every observation is accepted.
    public Func<TrackObservation, SubmissionResult> Respond { get; set; } = _ => SubmissionResult.Success;

    public IReadOnlyList<string> Calls { get { lock (gate) return [.. calls]; } }

    public IReadOnlyList<TrackObservation> Observations { get { lock (gate) return [.. observations]; } }

    public TrackSourceDescriptor? LastDescriptor(string sourceId)
    {
        lock (gate)
            return lastDescriptors.GetValueOrDefault(sourceId);
    }

    public ITrackSink Register(TrackSourceDescriptor descriptor)
    {
        if (descriptor.Validate() is { } problem)
            throw new ArgumentException(problem, nameof(descriptor));
        lock (gate)
            lastDescriptors[descriptor.SourceId] = descriptor;
        return new RecordingSink(this, descriptor.SourceId);
    }

    private SubmissionResult Publish(TrackObservation observation)
    {
        lock (gate)
        {
            observations.Add(observation);
            calls.Add($"obs:{observation.Sequence}");
        }
        return Respond(observation);
    }

    private void ReportHealth(string sourceId, SourceHealth health, string? detail)
    {
        lock (gate)
            calls.Add($"health:{sourceId}:{health}");
    }

    private void UpdateDescriptor(string sourceId, TrackSourceDescriptor descriptor)
    {
        if (descriptor.Validate() is { } problem)
            throw new ArgumentException(problem, nameof(descriptor));
        lock (gate)
            lastDescriptors[sourceId] = descriptor;
    }

    public async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Timed out waiting for " + description);
            await Task.Delay(10);
        }
    }

    private sealed class RecordingSink(RecordingPublisher publisher, string sourceId) : ITrackSink
    {
        public SubmissionResult Publish(TrackObservation observation) => publisher.Publish(observation);

        public void ReportHealth(SourceHealth health, string? detail = null) => publisher.ReportHealth(sourceId, health, detail);

        public void UpdateDescriptor(TrackSourceDescriptor descriptor) => publisher.UpdateDescriptor(sourceId, descriptor);

        public void Dispose()
        {
        }
    }
}

// Registration now happens on a background task (21.3, R23), so a Submit right after StartAsync can race it;
// this polls past that window without changing what a test asserts, the same way other adapter tests already
// poll for delivery.
public static class SubmissionPolling
{
    public static async Task<SubmissionResult> UntilRegisteredAsync(Func<SubmissionResult> submit)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var result = submit();
            if (result.Reason != RejectionReason.Unavailable || DateTime.UtcNow >= deadline)
                return result;
            await Task.Delay(10);
        }
    }
}

// A minimal feed whose content is replaced wholesale; any consumer behind the head simply resynchronizes.
public sealed class StaticTrackFeed : ITrackFeed
{
    private readonly object gate = new();
    private Track[] tracks = [];
    private long revision;

    public string Epoch { get; } = Guid.NewGuid().ToString("N");

    public void Set(params Track[] next)
    {
        lock (gate)
        {
            tracks = next;
            revision++;
        }
    }

    public TrackSnapshot GetSnapshot()
    {
        lock (gate)
            return new TrackSnapshot(Epoch, revision, tracks, []);
    }

    public TrackChanges GetChanges(string? epoch, long afterRevision, int maxChanges = 1000)
    {
        lock (gate)
            return new TrackChanges(Epoch, revision, [], epoch != Epoch || afterRevision != revision);
    }
}

// Stands in for the runtime's InvokeAsync: calls are rejected inside lifecycle code, exactly like ModuleRuntime.
public sealed class FakeTrackHost : IModuleHost
{
    public static readonly AsyncLocal<bool> InvocationBlocked = new();

    public RecordingPublisher Publisher { get; } = new();

    // While set and incomplete, calls wait as if a reload held the runtime gate.
    public Task? Block { get; set; }

    public Exception? Failure { get; set; }

    public RuntimeStatus Status { get; } = new(0, [], [], []);

    // The Tracks feed provider; leave null to model a missing Tracks module.
    public ITrackFeed? Feed { get; set; }

    public void RequestReload()
    {
    }

    public void RequestShutdown()
    {
    }

    public async Task<TResult> InvokeAsync<TFeature, TResult>(
        string featureId, Func<TFeature, TResult> action, CancellationToken cancellationToken = default)
        where TFeature : class
    {
        if (InvocationBlocked.Value)
            throw new InvalidOperationException("Feature calls cannot be nested or made from lifecycle methods.");
        if (Block is { IsCompleted: false } block)
            await block.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (Failure is not null)
            throw Failure;
        if (featureId == TrackPoints.FeedFeatureId && Feed is TFeature feed)
            return action(feed);
        if (featureId != TrackPoints.PublisherFeatureId || Publisher is not TFeature feature)
            throw new InvalidOperationException($"Feature is unavailable: {featureId}");
        return action(feature);
    }
}
