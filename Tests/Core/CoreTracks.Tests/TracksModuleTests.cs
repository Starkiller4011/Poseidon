using Poseidon.Core.Modules;
using Poseidon.Modules.Tracks;
using Poseidon.Tests.Shared;
using Xunit;
using static CoreTracks.Tests.TrackTestData;

namespace CoreTracks.Tests;

public sealed class TracksModuleTests
{
    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }

    private static (T Feature, IModule Module) Create<T>(string featureId, ManualTime? time = null)
        where T : class
    {
        IModule module = new TracksModule(time ?? new ManualTime(Start), TimeSpan.FromMilliseconds(10),
            new TrackPolicy(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)));
        return ((T)module.Features[featureId], module);
    }

    [Fact]
    public void Manifest_DeclaresAllThreePointsAndContributesEachOfThem()
    {
        // Slice 5 (21.2, 21.6): tracks.ingestion/ITrackIngestion are removed entirely, no compatibility shim.
        var manifest = ModuleDeclarationTests.DeployedManifest();
        Assert.Empty(manifest.Dependencies);
        Assert.Equal(
            [
                (TrackPoints.Feed, typeof(ITrackFeed).FullName),
                (TrackPoints.Operations, typeof(ITrackOperations).FullName),
                (TrackPoints.Publisher, typeof(ITrackPublisher).FullName)
            ],
            manifest.Provides.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => (p.Id, p.Contract)));
        Assert.All(manifest.Provides, p => Assert.Equal(PointCardinality.Single, p.Cardinality));
        Assert.Equal(
            [
                (TrackPoints.Feed, TrackPoints.FeedFeatureId),
                (TrackPoints.Operations, TrackPoints.OperationsFeatureId),
                (TrackPoints.Publisher, TrackPoints.PublisherFeatureId)
            ],
            manifest.Contributions.OrderBy(c => c.Point, StringComparer.Ordinal).Select(c => (c.Point, c.Id)));
    }

    [Fact]
    public void Features_ImplementTheDeclaredSharedContracts()
    {
        IModule module = new TracksModule();
        Assert.IsAssignableFrom<ITrackFeed>(module.Features[TrackPoints.FeedFeatureId]);
        Assert.IsAssignableFrom<ITrackPublisher>(module.Features[TrackPoints.PublisherFeatureId]);
        Assert.IsAssignableFrom<ITrackOperations>(module.Features[TrackPoints.OperationsFeatureId]);
    }

    [Fact]
    public void Publisher_Register_ValidatesTheDescriptor_AndReturnsASinkBoundToTheSource()
    {
        // R23: registering delegates to TrackManager.RegisterSource, which already validates and stores the
        // descriptor for real (slice 3); the per-source queue and the sink Register returns are real since
        // slice 4.
        var (publisher, module) = Create<ITrackPublisher>(TrackPoints.PublisherFeatureId);
        Assert.Throws<ArgumentException>(() => publisher.Register(Descriptor() with { SourceId = "" }));
        var sink = publisher.Register(Descriptor());
        Assert.NotNull(sink);
    }

    [Fact]
    public async Task TracksModule_RunsCorrectly_WithNoSourceEverRegistering()
    {
        // R24 (partial): a reload picking up or dropping a source without restarting Core needs the real
        // ingress and is exercised by slice 5's real-runtime scenario (R27, R28), not here.
        var time = new ManualTime(Start);
        var (feed, module) = Create<ITrackFeed>(TrackPoints.FeedFeatureId, time);
        await module.StartAsync(null!, null, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(50);
        Assert.Empty(feed.GetSnapshot().Tracks);
        await module.StopAsync(CancellationToken.None);
    }

    // TrackIngress's background pump (started for real by TracksModule.StartAsync) drains asynchronously, so
    // a test that needs an immediate, deterministic result drains it itself, exactly like TrackIngressTests
    // does — redundant with the real pump racing it, never harmful (DrainOnce finds nothing once the pump
    // already consumed an item).
    private static void DrainSynchronously(ITrackPublisher publisher)
    {
        while (((TrackIngress)publisher).DrainOnce()) { }
    }

    [Fact]
    public async Task Module_AgesTracksOnItsOwnTimer_AndStopsIdempotently()
    {
        var time = new ManualTime(Start);
        var (publisher, module) = Create<ITrackPublisher>(TrackPoints.PublisherFeatureId, time);
        var feed = (ITrackFeed)module.Features[TrackPoints.FeedFeatureId];
        await module.StartAsync(null!, null, CancellationToken.None);
        // Short timings (matching the module's own TrackPolicy above) so a 60 s advance removes it, the same
        // way the pre-rewire test exercised the module's ageing timer with the unregistered-source defaults.
        var sink = publisher.Register(Descriptor(staleSeconds: 10, lostSeconds: 30, removeSeconds: 60));
        Assert.True(sink.Publish(Observation()).Accepted);
        DrainSynchronously(publisher);
        Assert.Single(feed.GetSnapshot().Tracks);

        time.Advance(TimeSpan.FromSeconds(60));
        await WaitUntil(() => feed.GetSnapshot().Tracks.Count == 0);

        await module.StopAsync(CancellationToken.None);
        await module.StopAsync(CancellationToken.None);
        Assert.True(sink.Publish(Observation(target: "9", seconds: 61)).Accepted);
        DrainSynchronously(publisher);
        time.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(100);
        Assert.Single(feed.GetSnapshot().Tracks);
    }

    [Fact]
    public async Task Module_CheckpointsItsTracks_AndRestoresThemAfterRestart()
    {
        var (publisher, module) = Create<ITrackPublisher>(TrackPoints.PublisherFeatureId);
        var feed = (ITrackFeed)module.Features[TrackPoints.FeedFeatureId];
        await module.StartAsync(null!, null, CancellationToken.None);
        var sink = publisher.Register(Descriptor());
        Assert.True(sink.Publish(Observation()).Accepted);
        DrainSynchronously(publisher);
        var createdId = Assert.Single(feed.GetSnapshot().Tracks).Id;
        var state = await module.CaptureStateAsync(CancellationToken.None);
        await module.StopAsync(CancellationToken.None);
        Assert.NotNull(state);

        var (_, restarted) = Create<ITrackPublisher>(TrackPoints.PublisherFeatureId);
        await restarted.StartAsync(null!, state, CancellationToken.None);
        var restartedFeed = (ITrackFeed)restarted.Features[TrackPoints.FeedFeatureId];
        Assert.Equal(createdId, Assert.Single(restartedFeed.GetSnapshot().Tracks).Id);
        await restarted.StopAsync(CancellationToken.None);

        var (_, rejecting) = Create<ITrackPublisher>(TrackPoints.PublisherFeatureId);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            rejecting.StartAsync(null!, new ModuleState(99, []), CancellationToken.None).AsTask());
    }
}
