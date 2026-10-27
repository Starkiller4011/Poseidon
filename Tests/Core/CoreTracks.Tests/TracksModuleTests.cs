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
    public void Manifest_DeclaresTheIngestionAndFeedPointsAndContributesBoth()
    {
        var manifest = ModuleDeclarationTests.DeployedManifest();
        Assert.Empty(manifest.Dependencies);
        Assert.Equal(
            [(TrackPoints.Feed, typeof(ITrackFeed).FullName), (TrackPoints.Ingestion, typeof(ITrackIngestion).FullName)],
            manifest.Provides.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => (p.Id, p.Contract)));
        Assert.All(manifest.Provides, p => Assert.Equal((PointCardinality.Single, "1.0.0"), (p.Cardinality, p.Version)));
        Assert.Equal(
            [(TrackPoints.Feed, TrackPoints.FeedFeatureId), (TrackPoints.Ingestion, TrackPoints.IngestionFeatureId)],
            manifest.Contributions.OrderBy(c => c.Point, StringComparer.Ordinal).Select(c => (c.Point, c.Id)));
        Assert.All(manifest.Contributions, c => Assert.Equal("1.0.0", c.Version));
    }

    [Fact]
    public void Features_ImplementTheDeclaredSharedContracts()
    {
        IModule module = new TracksModule();
        Assert.IsAssignableFrom<ITrackIngestion>(module.Features[TrackPoints.IngestionFeatureId]);
        Assert.IsAssignableFrom<ITrackFeed>(module.Features[TrackPoints.FeedFeatureId]);
    }

    [Fact]
    public async Task Module_AgesTracksOnItsOwnTimer_AndStopsIdempotently()
    {
        var time = new ManualTime(Start);
        var (ingestion, module) = Create<ITrackIngestion>(TrackPoints.IngestionFeatureId, time);
        var feed = (ITrackFeed)module.Features[TrackPoints.FeedFeatureId];
        await module.StartAsync(null!, null, CancellationToken.None);
        ingestion.Submit(Observation());
        Assert.Single(feed.GetSnapshot().Tracks);

        time.Advance(TimeSpan.FromSeconds(60));
        await WaitUntil(() => feed.GetSnapshot().Tracks.Count == 0);

        await module.StopAsync(CancellationToken.None);
        await module.StopAsync(CancellationToken.None);
        ingestion.Submit(Observation(target: "9", seconds: 61));
        time.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(100);
        Assert.Single(feed.GetSnapshot().Tracks);
    }

    [Fact]
    public async Task Module_CheckpointsItsTracks_AndRestoresThemAfterRestart()
    {
        var (ingestion, module) = Create<ITrackIngestion>(TrackPoints.IngestionFeatureId);
        await module.StartAsync(null!, null, CancellationToken.None);
        var created = ingestion.Submit(Observation());
        var state = await module.CaptureStateAsync(CancellationToken.None);
        await module.StopAsync(CancellationToken.None);
        Assert.NotNull(state);

        var (_, restarted) = Create<ITrackIngestion>(TrackPoints.IngestionFeatureId);
        await restarted.StartAsync(null!, state, CancellationToken.None);
        var feed = (ITrackFeed)restarted.Features[TrackPoints.FeedFeatureId];
        Assert.Equal(created.TrackId, Assert.Single(feed.GetSnapshot().Tracks).Id);
        await restarted.StopAsync(CancellationToken.None);

        var (_, rejecting) = Create<ITrackIngestion>(TrackPoints.IngestionFeatureId);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            rejecting.StartAsync(null!, new ModuleState(99, []), CancellationToken.None).AsTask());
    }
}
