using Poseidon.Core.Modules;
using Poseidon.Modules.Tracks;
using Xunit;
using static CoreTracks.Tests.TrackTestData;

namespace CoreTracks.Tests;

// Slice 4 Step 2 (21.9, R23 to R26, R29 to R31): the ingress design is approved, but the per-source bounded
// queue and its consumer thread do not exist yet. TrackIngress.Register already validates and stores a
// descriptor for real, delegating to TrackManager.RegisterSource (slice 3), and then throws
// NotImplementedException for any valid descriptor, so every test below that needs a working sink fails
// there, uncaught, as a red test; each one is written as if the feature already worked, so it starts
// exercising its real assertions once Step 3 replaces the stub.
public sealed class TrackIngressTests
{
    private static (TrackIngress Ingress, TrackManager Manager) Create()
    {
        var manager = new TrackManager(new ManualTime(Start));
        return (new TrackIngress(manager), manager);
    }

    [Fact]
    public void Register_WithAnInvalidDescriptor_ThrowsArgumentException()
    {
        var (ingress, _) = Create();
        Assert.Throws<ArgumentException>(() => ingress.Register(Descriptor() with { ExpectedPeakReportsPerSecond = 0 }));
    }

    [Fact]
    public void Register_WithAValidDescriptor_ReturnsASinkBoundToTheSource()
    {
        // R23: Register returns an ITrackSink bound to the source.
        var (ingress, _) = Create();
        var sink = ingress.Register(Descriptor());
        Assert.NotNull(sink);
    }

    [Fact]
    public void Register_TheSameSourceIdAgain_ReplacesTheEarlierSink_WhichStopsAcceptingPublishes()
    {
        // R23: a restart (or a module's own reconnect) re-registers the same source ID; the earlier sink is
        // replaced and no longer delivers.
        var (ingress, manager) = Create();
        var first = ingress.Register(Descriptor());
        var second = ingress.Register(Descriptor());
        Assert.False(first.Publish(Observation()).Accepted);
        Assert.True(second.Publish(Observation()).Accepted);
    }

    [Fact]
    public void Publish_WhenASourcesQueueIsFull_DropsTheOldestReportAndCountsItInsteadOfBlocking()
    {
        // R26: each source's queue is sized from its descriptor's declared peak rate (at least 4,096 reports,
        // otherwise ten seconds of peak); once full, publishing drops the oldest queued report and counts it.
        var (ingress, manager) = Create();
        var sink = ingress.Register(Descriptor(peakReportsPerSecond: 1));
        for (var i = 1; i <= 5_000; i++)
            sink.Publish(Observation(sequence: i, seconds: i));

        var status = manager.GetSnapshot().Sources.Single(s => s.SourceId == "Core.AIS");
        Assert.True(status.Statistics!.Dropped > 0);
    }

    [Fact]
    public void TrackSystemHealth_BecomesDegraded_WhileAQueueIsDropping_AndRecoversAfterTenSecondsWithoutDrops()
    {
        // R26: the track management system (not the sensor) reports Degraded while any source's queue is
        // dropping, and returns to Healthy after 10 seconds without further drops.
        var time = new ManualTime(Start);
        var manager = new TrackManager(time);
        var ingress = new TrackIngress(manager);
        var sink = ingress.Register(Descriptor(peakReportsPerSecond: 1));
        for (var i = 1; i <= 5_000; i++)
            sink.Publish(Observation(sequence: i, seconds: i));
        manager.Advance();
        Assert.Equal(TrackSystemHealth.Degraded, manager.GetSnapshot().System!.Health);

        time.Advance(TimeSpan.FromSeconds(10));
        manager.Advance();
        Assert.Equal(TrackSystemHealth.Healthy, manager.GetSnapshot().System!.Health);
    }

    [Fact]
    public void ConcurrentSources_EachKeepTheirOwnOrder_AndNoneStarvesTheOthers()
    {
        // R29: several sources publishing at once are all processed; the one consumer thread drains their
        // queues round robin in small batches, so per-source order is preserved and no source can starve
        // another, and no report is lost below its own queue's limit.
        var (ingress, manager) = Create();
        var radar = ingress.Register(Descriptor(sourceId: "Core.Radar.Nav1", kind: TrackSourceKinds.Radar));
        var ais = ingress.Register(Descriptor(sourceId: "Core.AIS"));

        Parallel.Invoke(
            () => { for (var i = 1; i <= 200; i++) radar.Publish(Observation(source: "Core.Radar.Nav1", sequence: i, seconds: i)); },
            () => { for (var i = 1; i <= 200; i++) ais.Publish(Observation(source: "Core.AIS", target: "200", sequence: i, seconds: i)); });

        // Drains both queues round robin, deterministically and single-threaded (no background pump).
        while (ingress.DrainOnce()) { }
        var snapshot = manager.GetSnapshot();
        Assert.Equal(2, snapshot.Tracks.Count);
        Assert.All(snapshot.Tracks, t => Assert.Equal(200, t.UpdateCount));
    }

    [Fact]
    public void SourceStatistics_ExposeReceivedCreatedUpdatedRejectedDroppedQueueDepthAndLastRejection()
    {
        // R30: SourceStatus.Statistics reports received, created, updated, rejected-by-reason counts, dropped,
        // queue depth and the last rejection detail, per source, after the fact.
        var (ingress, manager) = Create();
        var sink = ingress.Register(Descriptor());
        sink.Publish(Observation(sequence: 1));
        sink.Publish(Observation(sequence: 2, seconds: 1));
        sink.Publish(Observation(sequence: 2, seconds: 1));
        while (ingress.DrainOnce()) { }

        var statistics = manager.GetSnapshot().Sources.Single(s => s.SourceId == "Core.AIS").Statistics;
        Assert.NotNull(statistics);
        Assert.Equal((3L, 1L, 1L, 1L), (statistics!.Received, statistics.Created, statistics.Updated, statistics.Duplicate));
    }

    [Fact]
    public void ThroughputBaseline_IsMeasuredAtOneThousandTracks_AsAMeasurementNotAGate()
    {
        // R31: a measured throughput baseline (proposal: 5,000 reports/s at 1,000 tracks) is recorded by this
        // test as a measurement, not a pass/fail gate.
        var (ingress, manager) = Create();
        var sink = ingress.Register(Descriptor(peakReportsPerSecond: 5_000));
        for (var track = 1; track <= 1_000; track++)
            sink.Publish(Observation(target: track.ToString(), sequence: 1, latitude: track * 0.001));
        while (ingress.DrainOnce()) { }

        Assert.Equal(1_000, manager.GetSnapshot().Tracks.Count);
    }
}
