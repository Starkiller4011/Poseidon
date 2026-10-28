using Poseidon.Core.Modules;

namespace Poseidon.Tests.Shared;

public sealed class RecordingIngestion : ITrackIngestion
{
    private readonly object gate = new();
    private readonly List<string> calls = [];
    private readonly List<TrackObservation> observations = [];

    // Decides the result for each observation; by default every observation creates a track.
    public Func<TrackObservation, ObservationResult> Respond { get; set; } =
        o => new ObservationResult(ObservationOutcome.Created, "T-" + o.SourceTargetId);

    public IReadOnlyList<string> Calls { get { lock (gate) return [.. calls]; } }

    public IReadOnlyList<TrackObservation> Observations { get { lock (gate) return [.. observations]; } }

    public ObservationResult Submit(TrackObservation observation) => SubmitBatch([observation])[0];

    public IReadOnlyList<ObservationResult> SubmitBatch(IReadOnlyList<TrackObservation> batch)
    {
        lock (gate)
        {
            var results = new List<ObservationResult>();
            foreach (var observation in batch)
            {
                observations.Add(observation);
                calls.Add($"obs:{observation.Sequence}");
                results.Add(Respond(observation));
            }
            return results;
        }
    }

    public void ReportSourceHealth(string sourceId, SourceHealth health, string? detail = null)
    {
        lock (gate)
            calls.Add($"health:{sourceId}:{health}");
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

    public RecordingIngestion Ingestion { get; } = new();

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
        if (featureId != TrackPoints.IngestionFeatureId || Ingestion is not TFeature feature)
            throw new InvalidOperationException($"Feature is unavailable: {featureId}");
        return action(feature);
    }
}
