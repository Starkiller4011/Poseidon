using Poseidon.Core.Modules;
using Poseidon.Tests.Shared;
using Xunit;

namespace Contracts.Tests;

public sealed class TrackReplicaTests
{
    private static readonly DateTimeOffset Time = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private sealed class ScriptedFeed : ITrackFeed
    {
        public Func<TrackSnapshot> OnSnapshot { get; set; } = () => throw new InvalidOperationException("No snapshot scripted.");

        public Func<string?, long, int, TrackChanges> OnChanges { get; set; } =
            (_, _, _) => throw new InvalidOperationException("No changes scripted.");

        public int SnapshotCalls { get; private set; }

        public int ChangeCalls { get; private set; }

        public TrackSnapshot GetSnapshot()
        {
            SnapshotCalls++;
            return OnSnapshot();
        }

        public TrackChanges GetChanges(string? epoch, long afterRevision, int maxChanges = 1000)
        {
            ChangeCalls++;
            return OnChanges(epoch, afterRevision, maxChanges);
        }
    }

    private static Track Track(string id, long revision, double latitude = 10)
        => new(id, null, TrackKind.Point, TrackState.Active, new GeodeticPosition(latitude, 20), null, null, null,
            null, null, Time, Time, Time, 1, revision, []);

    private static TrackSnapshot Snapshot(string epoch, long revision, params Track[] tracks)
        => new(epoch, revision, tracks, []);

    private static TrackChange Upsert(Track track) => new(track.Revision, TrackChangeKind.Upsert, track);

    private static (TrackReplica Replica, FakeTrackHost Host, ScriptedFeed Feed, List<int> Notifications) Create()
    {
        var feed = new ScriptedFeed();
        var host = new FakeTrackHost { Feed = feed };
        var replica = new TrackReplica();
        var notifications = new List<int>();
        replica.Changed += () => notifications.Add(notifications.Count);
        return (replica, host, feed, notifications);
    }

    [Fact]
    public async Task FirstPoll_TakesASnapshotAndBecomesLive()
    {
        var (replica, host, feed, notifications) = Create();
        Assert.Equal(TrackReplicaState.NotSynchronized, replica.State);
        feed.OnSnapshot = () => Snapshot("e1", 4, Track("T1", 3), Track("T2", 4));

        await replica.PollAsync(host, CancellationToken.None);

        Assert.Equal((TrackReplicaState.Live, "e1", 4L), (replica.State, replica.Epoch, replica.Revision));
        Assert.Equal(new[] { "T1", "T2" }, replica.Tracks.Select(t => t.Id));
        Assert.Single(notifications);
        Assert.Equal(1, feed.SnapshotCalls);
    }

    [Fact]
    public async Task LaterPolls_ApplyUpsertsRemovalsAndSourceChangesFromTheCursor()
    {
        var (replica, host, feed, notifications) = Create();
        feed.OnSnapshot = () => Snapshot("e1", 2, Track("T1", 1), Track("T2", 2));
        await replica.PollAsync(host, CancellationToken.None);
        var before = replica.Tracks;

        var source = new SourceStatus("Core.AIS", SourceHealth.Degraded, "noisy", Time);
        feed.OnChanges = (epoch, after, _) =>
        {
            Assert.Equal(("e1", 2L), (epoch, after));
            return new TrackChanges("e1", 6,
            [
                Upsert(Track("T1", 3, latitude: 11)),
                Upsert(Track("T3", 4)),
                new TrackChange(5, TrackChangeKind.Remove, TrackId: "T2"),
                new TrackChange(6, TrackChangeKind.Source, Source: source)
            ], false);
        };
        await replica.PollAsync(host, CancellationToken.None);

        Assert.Equal(6, replica.Revision);
        Assert.Equal(new[] { "T1", "T3" }, replica.Tracks.Select(t => t.Id));
        Assert.Equal(11, replica.Tracks[0].Position!.LatitudeDegrees);
        Assert.Equal(source, Assert.Single(replica.Sources));
        Assert.Equal(2, notifications.Count);
        // Lists handed out earlier are not modified in place.
        Assert.Equal(new[] { "T1", "T2" }, before.Select(t => t.Id));

        feed.OnChanges = (_, _, _) => new TrackChanges("e1", 6, [], false);
        await replica.PollAsync(host, CancellationToken.None);
        Assert.Equal(2, notifications.Count);
    }

    [Fact]
    public async Task ResyncRequired_ReplacesEverythingWithAFreshSnapshot()
    {
        var (replica, host, feed, _) = Create();
        feed.OnSnapshot = () => Snapshot("e1", 1, Track("T1", 1));
        await replica.PollAsync(host, CancellationToken.None);

        feed.OnChanges = (_, _, _) => new TrackChanges("e2", 9, [], true);
        feed.OnSnapshot = () => Snapshot("e2", 9, Track("T7", 9));
        await replica.PollAsync(host, CancellationToken.None);

        Assert.Equal(("e2", 9L), (replica.Epoch, replica.Revision));
        Assert.Equal("T7", Assert.Single(replica.Tracks).Id);
    }

    [Fact]
    public async Task AGapInTheChanges_IsNotApplied_AndTheNextPollResynchronizes()
    {
        var (replica, host, feed, _) = Create();
        feed.OnSnapshot = () => Snapshot("e1", 2, Track("T1", 2));
        await replica.PollAsync(host, CancellationToken.None);

        feed.OnChanges = (_, _, _) => new TrackChanges("e1", 5, [Upsert(Track("T1", 5, latitude: 50))], false);
        await replica.PollAsync(host, CancellationToken.None);
        Assert.Equal(10, Assert.Single(replica.Tracks).Position!.LatitudeDegrees);
        Assert.Null(replica.Epoch);

        feed.OnSnapshot = () => Snapshot("e1", 5, Track("T1", 5, latitude: 50));
        await replica.PollAsync(host, CancellationToken.None);
        Assert.Equal((TrackReplicaState.Live, 5L), (replica.State, replica.Revision));
        Assert.Equal(50, Assert.Single(replica.Tracks).Position!.LatitudeDegrees);
    }

    [Fact]
    public async Task DrainsEveryBatchInOnePoll()
    {
        var (replica, host, feed, _) = Create();
        feed.OnSnapshot = () => Snapshot("e1", 0);
        await replica.PollAsync(host, CancellationToken.None);

        feed.OnChanges = (_, after, max) =>
        {
            var revisions = Enumerable.Range((int)after + 1, 5 - (int)after).Take(max).Select(i => (long)i).ToList();
            return new TrackChanges("e1", revisions.Count == 0 ? after : revisions[^1],
                [.. revisions.Select(r => Upsert(Track("T" + r, r)))], false);
        };
        await replica.PollAsync(host, CancellationToken.None, maxChanges: 2);

        Assert.Equal(5, replica.Revision);
        Assert.Equal(5, replica.Tracks.Count);
        Assert.Equal(3, feed.ChangeCalls);
    }

    [Fact]
    public async Task MissingOrFailingProvider_MakesTheReplicaUnavailable_AndItRecovers()
    {
        var (replica, host, feed, notifications) = Create();
        feed.OnSnapshot = () => Snapshot("e1", 1, Track("T1", 1));
        await replica.PollAsync(host, CancellationToken.None);

        host.Feed = null;
        await replica.PollAsync(host, CancellationToken.None);
        Assert.Equal(TrackReplicaState.Unavailable, replica.State);
        Assert.Contains("unavailable", replica.UnavailableReason);
        Assert.Empty(replica.Tracks);
        Assert.Null(replica.Epoch);
        var count = notifications.Count;
        await replica.PollAsync(host, CancellationToken.None);
        Assert.Equal(count, notifications.Count);

        host.Feed = feed;
        await replica.PollAsync(host, CancellationToken.None);
        Assert.Equal(TrackReplicaState.Live, replica.State);
        Assert.Null(replica.UnavailableReason);
        Assert.Single(replica.Tracks);

        feed.OnChanges = (_, _, _) => throw new InvalidOperationException("boom");
        await replica.PollAsync(host, CancellationToken.None);
        Assert.Equal(("boom", TrackReplicaState.Unavailable), (replica.UnavailableReason, replica.State));
    }

    [Fact]
    public async Task Cancellation_Propagates_WithoutChangingTheReplica()
    {
        var (replica, host, feed, _) = Create();
        feed.OnSnapshot = () => Snapshot("e1", 1, Track("T1", 1));
        await replica.PollAsync(host, CancellationToken.None);

        host.Block = new TaskCompletionSource().Task;
        using var cancellation = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replica.PollAsync(host, cancellation.Token));
        Assert.Equal((TrackReplicaState.Live, 1L), (replica.State, replica.Revision));
    }

    [Fact]
    public async Task Reset_ReturnsToTheUnsynchronizedEmptyState()
    {
        var (replica, host, feed, notifications) = Create();
        feed.OnSnapshot = () => Snapshot("e1", 1, Track("T1", 1));
        await replica.PollAsync(host, CancellationToken.None);
        var count = notifications.Count;

        replica.Reset();
        Assert.Equal(TrackReplicaState.NotSynchronized, replica.State);
        Assert.Empty(replica.Tracks);
        Assert.Null(replica.Epoch);
        Assert.Equal(count + 1, notifications.Count);
    }

    [Fact]
    public async Task Pump_KeepsTheReplicaCurrent_StopsPromptly_AndRestarts()
    {
        var (replica, host, feed, _) = Create();
        var revision = 1L;
        feed.OnSnapshot = () => Snapshot("e1", revision, Track("T1", revision));
        feed.OnChanges = (_, after, _) => new TrackChanges("e1", revision,
            after == revision ? [] : [Upsert(Track("T1", revision))], false);
        var pump = new TrackReplicaPump(host, replica, TimeSpan.FromMilliseconds(10));

        pump.Start();
        pump.Start();
        await host.Ingestion.WaitUntilAsync(() => replica.Revision == 1, "the first snapshot");
        revision = 2;
        await host.Ingestion.WaitUntilAsync(() => replica.Revision == 2, "the update");

        host.Block = new TaskCompletionSource().Task;
        await Task.Delay(50);
        await pump.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await pump.StopAsync();

        host.Block = null;
        replica.Reset();
        pump.Start();
        await host.Ingestion.WaitUntilAsync(() => replica.State == TrackReplicaState.Live, "restart");
        await pump.StopAsync();
    }

    [Fact]
    public async Task Pump_IsNotBlockedByTheLifecycleFlagOfTheCallerThatStartedIt()
    {
        var (replica, host, feed, _) = Create();
        feed.OnSnapshot = () => Snapshot("e1", 1, Track("T1", 1));
        var pump = new TrackReplicaPump(host, replica, TimeSpan.FromMilliseconds(10));
        FakeTrackHost.InvocationBlocked.Value = true;
        try { pump.Start(); }
        finally { FakeTrackHost.InvocationBlocked.Value = false; }

        await host.Ingestion.WaitUntilAsync(() => replica.State == TrackReplicaState.Live, "the snapshot");
        await pump.StopAsync();
    }
}
