namespace Poseidon.Core.Modules;

public enum TrackReplicaState
{
    // No snapshot has been taken yet.
    NotSynchronized,
    Live,
    // The feed cannot be reached (no Tracks provider, or the call failed); the replica holds no tracks.
    Unavailable
}

// A consumer-side copy of the Tracks feed (snapshot plus ordered changes). It keeps only a cursor, takes a new
// snapshot whenever the feed demands a resync, and never retains the provider, so Tracks can be reloaded.
public sealed class TrackReplica
{
    private readonly object sync = new();
    private TrackReplicaState state = TrackReplicaState.NotSynchronized;
    private string? unavailableReason;
    private string? epoch;
    private long revision;
    private Track[] tracks = [];
    private SourceStatus[] sources = [];

    public TrackReplicaState State
    {
        get
        {
            lock (sync)
                return state;
        }
    }

    public string? UnavailableReason
    {
        get
        {
            lock (sync)
                return unavailableReason;
        }
    }

    public string? Epoch
    {
        get
        {
            lock (sync)
                return epoch;
        }
    }

    public long Revision
    {
        get
        {
            lock (sync)
                return revision;
        }
    }

    // Copy-on-write: a list handed out earlier is never modified.
    public IReadOnlyList<Track> Tracks
    {
        get
        {
            lock (sync)
                return tracks;
        }
    }

    public IReadOnlyList<SourceStatus> Sources
    {
        get
        {
            lock (sync)
                return sources;
        }
    }

    // Raised after the replica's content or state changed, on the thread that polled.
    public event Action? Changed;

    // One synchronization step: takes a snapshot when unsynchronized or after a resync request, otherwise
    // applies the changes after the cursor (draining batches of maxChanges). Provider failures make the replica
    // Unavailable rather than throwing; only cancellation propagates.
    public async Task PollAsync(IModuleHost host, CancellationToken cancellationToken, int maxChanges = 1000)
    {
        try
        {
            while (true)
            {
                string? currentEpoch;
                long currentRevision;
                lock (sync)
                {
                    currentEpoch = epoch;
                    currentRevision = revision;
                }
                if (currentEpoch is null)
                {
                    await TakeSnapshotAsync(host, cancellationToken);
                    return;
                }

                var changes = await host.InvokeAsync<ITrackFeed, TrackChanges>(TrackPoints.FeedFeatureId,
                    f => f.GetChanges(currentEpoch, currentRevision, maxChanges), cancellationToken);
                if (changes.ResyncRequired)
                {
                    await TakeSnapshotAsync(host, cancellationToken);
                    return;
                }
                if (!Apply(changes, currentEpoch, currentRevision) || changes.Changes.Count < maxChanges)
                    return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetUnavailable(ex.Message);
        }
    }

    public void Reset()
    {
        lock (sync)
        {
            state = TrackReplicaState.NotSynchronized;
            unavailableReason = null;
            epoch = null;
            revision = 0;
            tracks = [];
            sources = [];
        }
        Changed?.Invoke();
    }

    private async Task TakeSnapshotAsync(IModuleHost host, CancellationToken cancellationToken)
    {
        var snapshot = await host.InvokeAsync<ITrackFeed, TrackSnapshot>(
            TrackPoints.FeedFeatureId, f => f.GetSnapshot(), cancellationToken);
        lock (sync)
        {
            state = TrackReplicaState.Live;
            unavailableReason = null;
            epoch = snapshot.Epoch;
            revision = snapshot.Revision;
            tracks = [.. snapshot.Tracks];
            sources = [.. snapshot.Sources];
        }
        Changed?.Invoke();
    }

    // Returns false when the changes cannot be applied and a new snapshot is needed.
    private bool Apply(TrackChanges changes, string expectedEpoch, long cursor)
    {
        var changed = false;
        lock (sync)
        {
            var expected = cursor + 1;
            if (changes.Epoch != expectedEpoch || changes.Changes.Any(c => c.Revision != expected++))
            {
                epoch = null;
                return false;
            }
            if (changes.Changes.Count == 0)
                return true;

            var byId = tracks.ToDictionary(t => t.Id, StringComparer.Ordinal);
            var bySource = sources.ToDictionary(s => s.SourceId, StringComparer.Ordinal);
            foreach (var change in changes.Changes)
            {
                switch (change.Kind)
                {
                    case TrackChangeKind.Upsert when change.Track is { } track:
                        byId[track.Id] = track;
                        break;
                    case TrackChangeKind.Remove when change.TrackId is { } id:
                        byId.Remove(id);
                        break;
                    case TrackChangeKind.Source when change.Source is { } source:
                        bySource[source.SourceId] = source;
                        break;
                }
            }
            tracks = [.. byId.Values.OrderBy(t => t.Id, StringComparer.Ordinal)];
            sources = [.. bySource.Values.OrderBy(s => s.SourceId, StringComparer.Ordinal)];
            revision = changes.Changes[^1].Revision;
            changed = true;
        }
        if (changed)
            Changed?.Invoke();
        return true;
    }

    private void SetUnavailable(string reason)
    {
        lock (sync)
        {
            if (state == TrackReplicaState.Unavailable && unavailableReason == reason)
                return;
            state = TrackReplicaState.Unavailable;
            unavailableReason = reason;
            epoch = null;
            revision = 0;
            tracks = [];
            sources = [];
        }
        Changed?.Invoke();
    }
}

// Polls a replica on a background task. Start suppresses execution-context flow so the task does not inherit
// the lifecycle "feature calls blocked" flag of the module that started it.
public sealed class TrackReplicaPump(IModuleHost host, TrackReplica replica, TimeSpan interval)
{
    private readonly object sync = new();
    private CancellationTokenSource? cancellation;
    private Task? loop;

    public void Start()
    {
        lock (sync)
        {
            if (loop is not null)
                return;
            var source = new CancellationTokenSource();
            cancellation = source;
            using (ExecutionContext.SuppressFlow())
                loop = Task.Run(() => RunAsync(source.Token));
        }
    }

    public async Task StopAsync()
    {
        Task? stopping;
        CancellationTokenSource? source;
        lock (sync)
        {
            stopping = loop;
            source = cancellation;
            loop = null;
            cancellation = null;
        }
        if (stopping is null)
            return;
        source!.Cancel();
        await stopping;
        source.Dispose();
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await replica.PollAsync(host, token);
                await Task.Delay(interval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }
}
