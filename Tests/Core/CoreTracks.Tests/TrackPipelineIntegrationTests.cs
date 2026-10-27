using Poseidon.Core.Modules;
using Poseidon.Core.Runtime;
using Xunit;

namespace CoreTracks.Tests;

// Deploys the real Tracks, AIS, Radar and Sonar packages into a real runtime, so the sensor-to-track
// pipeline is exercised through manifests, collectible load contexts, InvokeAsync and reload checkpoints.
public sealed class TrackPipelineIntegrationTests
{
    private static readonly DateTimeOffset Measured = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static AisPositionReport Ais(int seconds = 0)
        => new(366123456, Measured.AddSeconds(seconds), 25.25, 55.5, 10, 90, 91, "MV EXAMPLE@@");

    private static RadarTargetReport Radar(int seconds = 0)
        => new(12, Measured.AddSeconds(seconds), 1852, 0, BearingReference.True,
            new SensorPose(new GeodeticPosition(25.25, 55.5), 0));

    private static async Task<SubmissionResult> SubmitAis(ModuleRuntime runtime, AisPositionReport report)
        => await runtime.InvokeAsync<IAisInput, SubmissionResult>(SensorPoints.AisInputFeatureId, i => i.Submit(report));

    private static async Task<SubmissionResult> SubmitRadar(ModuleRuntime runtime, RadarTargetReport report)
        => await runtime.InvokeAsync<IRadarInput, SubmissionResult>(SensorPoints.RadarInputFeatureId, i => i.Submit(report));

    private static Task<TrackSnapshot> Snapshot(ModuleRuntime runtime)
        => runtime.InvokeAsync<ITrackFeed, TrackSnapshot>(TrackPoints.FeedFeatureId, f => f.GetSnapshot());

    private static async Task<TrackSnapshot> WaitForTracks(ModuleRuntime runtime, Func<TrackSnapshot, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var snapshot = await Snapshot(runtime);
            if (condition(snapshot))
                return snapshot;
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the track feed.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task AisAndRadarInput_BecomeManagedTracks_AndRepeatReportsUpdateTheSameTrack()
    {
        await using var modules = new DeployedModules();
        modules.Deploy("CoreTracks", "CoreAIS", "CoreRadar", "CoreSonar");
        Assert.True(await modules.Runtime.ReloadAsync());
        Assert.All(modules.Runtime.Status.Packages, p => Assert.Equal(PackageState.Active, p.State));
        Assert.Equal(
            new[] { SensorPoints.AisInput, SensorPoints.RadarInput, TrackPoints.Feed, TrackPoints.Ingestion },
            modules.Runtime.Status.Features.Select(f => f.PointId!).Order(StringComparer.Ordinal));

        Assert.True((await SubmitAis(modules.Runtime, Ais())).Accepted);
        var first = await WaitForTracks(modules.Runtime, s => s.Tracks.Count == 1);
        var ais = Assert.Single(first.Tracks);
        Assert.Equal(("Core.AIS", "366123456", "MV EXAMPLE"), (ais.SourceId, ais.SourceTargetId, ais.Name));
        Assert.Equal(new GeodeticPosition(25.25, 55.5), ais.Position);
        Assert.Equal(10 * 1852.0 / 3600, ais.SpeedOverGroundMetresPerSecond!.Value, 9);

        Assert.True((await SubmitAis(modules.Runtime, Ais(seconds: 10))).Accepted);
        var updated = await WaitForTracks(modules.Runtime, s => s.Tracks.Any(t => t.UpdateCount == 2));
        Assert.Equal(ais.Id, Assert.Single(updated.Tracks).Id);

        // A radar target at the same place is a separate track: sources are never merged on proximity.
        Assert.True((await SubmitRadar(modules.Runtime, Radar())).Accepted);
        var both = await WaitForTracks(modules.Runtime, s => s.Tracks.Count == 2);
        var radar = Assert.Single(both.Tracks, t => t.SourceId == "Core.Radar");
        Assert.NotEqual(ais.Id, radar.Id);
        Assert.Equal(25.25 + 1.0 / 60, radar.Position!.LatitudeDegrees, 3);
        Assert.Equal(new PolarMeasurement(1852, 0, BearingReference.True), radar.LatestMeasurement);
    }

    [Fact]
    public async Task SourceDisconnect_IsVisibleInTheFeed_AndAnObservationRestoresIt()
    {
        await using var modules = new DeployedModules();
        modules.Deploy("CoreTracks", "CoreAIS");
        Assert.True(await modules.Runtime.ReloadAsync());
        await SubmitAis(modules.Runtime, Ais());
        await WaitForTracks(modules.Runtime, s => s.Tracks.Count == 1);

        await modules.Runtime.InvokeAsync<IAisInput, bool>(SensorPoints.AisInputFeatureId, i =>
        {
            i.ReportSourceHealth(SourceHealth.Disconnected, "no signal");
            return true;
        });
        var down = await WaitForTracks(modules.Runtime,
            s => s.Sources.Any(x => x.Health == SourceHealth.Disconnected));
        Assert.Equal(TrackState.Stale, Assert.Single(down.Tracks).State);

        await SubmitAis(modules.Runtime, Ais(seconds: 5));
        var up = await WaitForTracks(modules.Runtime, s => s.Sources.All(x => x.Health == SourceHealth.Healthy));
        Assert.Equal(TrackState.Active, Assert.Single(up.Tracks).State);
    }

    [Fact]
    public async Task Reload_KeepsTrackIdsAndConsumerCursors_AndTheSensorKeepsUpdatingTheSameTrack()
    {
        await using var modules = new DeployedModules();
        modules.Deploy("CoreTracks", "CoreAIS");
        Assert.True(await modules.Runtime.ReloadAsync());
        await SubmitAis(modules.Runtime, Ais());
        var before = await WaitForTracks(modules.Runtime, s => s.Tracks.Count == 1);
        var generation = modules.Runtime.Status.Generation;

        modules.Touch("CoreTracks");
        Assert.True(await modules.Runtime.ReloadAsync());
        Assert.True(modules.Runtime.Status.Generation > generation);

        var changes = await modules.Runtime.InvokeAsync<ITrackFeed, TrackChanges>(TrackPoints.FeedFeatureId,
            f => f.GetChanges(before.Epoch, before.Revision));
        Assert.False(changes.ResyncRequired);
        Assert.Empty(changes.Changes);

        Assert.True((await SubmitAis(modules.Runtime, Ais(seconds: 10))).Accepted);
        var after = await WaitForTracks(modules.Runtime, s => s.Tracks.Any(t => t.UpdateCount == 2));
        var track = Assert.Single(after.Tracks);
        Assert.Equal((before.Tracks[0].Id, before.Epoch), (track.Id, after.Epoch));
        Assert.Equal(2, track.LastSequence);
    }

    [Fact]
    public async Task WithoutTracks_TheTrackSensorsAreBlockedAndSonarIsUnaffected_ThenTheyRecover()
    {
        await using var modules = new DeployedModules();
        modules.Deploy("CoreTracks", "CoreAIS", "CoreRadar", "CoreSonar");
        Assert.True(await modules.Runtime.ReloadAsync());
        await SubmitAis(modules.Runtime, Ais());
        var before = await WaitForTracks(modules.Runtime, s => s.Tracks.Count == 1);

        modules.Remove("CoreTracks");
        Assert.False(await modules.Runtime.ReloadAsync());
        var packages = modules.Runtime.Status.Packages;
        Assert.Equal(PackageState.Blocked, Assert.Single(packages, p => p.ModuleId == "Core.AIS").State);
        Assert.Equal(PackageState.Blocked, Assert.Single(packages, p => p.ModuleId == "Core.Radar").State);
        Assert.Contains("Core.Tracks", Assert.Single(packages, p => p.ModuleId == "Core.AIS").Detail);
        Assert.Equal(PackageState.Active, Assert.Single(packages, p => p.ModuleId == "Core.Sonar").State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SubmitAis(modules.Runtime, Ais(seconds: 1)));

        modules.Deploy("CoreTracks");
        Assert.True(await modules.Runtime.ReloadAsync());
        Assert.All(modules.Runtime.Status.Packages, p => Assert.Equal(PackageState.Active, p.State));
        Assert.True((await SubmitAis(modules.Runtime, Ais(seconds: 20))).Accepted);
        var after = await WaitForTracks(modules.Runtime, s => s.Tracks.Count == 1);

        // Tracks was removed, so its state was lost: consumers must resynchronize from a snapshot.
        Assert.NotEqual(before.Epoch, after.Epoch);
        var stale = await modules.Runtime.InvokeAsync<ITrackFeed, TrackChanges>(TrackPoints.FeedFeatureId,
            f => f.GetChanges(before.Epoch, before.Revision));
        Assert.True(stale.ResyncRequired);
    }

    private sealed class DeployedModules : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "Poseidon.Tests", Guid.NewGuid().ToString("N"));
        public ModuleRuntime Runtime { get; }

        public DeployedModules()
        {
            Directory.CreateDirectory(root);
            Runtime = new ModuleRuntime(root);
        }

        public void Deploy(params string[] names)
        {
            foreach (var name in names)
            {
                var (assembly, manifest) = name == "CoreTracks"
                    ? (Path.Combine(AppContext.BaseDirectory, "CoreTracks.dll"),
                       Path.Combine(AppContext.BaseDirectory, "module-under-test.json"))
                    : (Path.Combine(AppContext.BaseDirectory, "packages", name, name + ".dll"),
                       Path.Combine(AppContext.BaseDirectory, "packages", name, "module.json"));
                var package = Path.Combine(root, name);
                Directory.CreateDirectory(package);
                File.Copy(assembly, Path.Combine(package, name + ".dll"), overwrite: true);
                File.Copy(manifest, Path.Combine(package, "module.json"), overwrite: true);
                File.WriteAllText(Path.Combine(package, ".ready"), "complete");
            }
        }

        // Any content change makes the package a new deployment, which reloads the generation.
        public void Touch(string name) => File.WriteAllText(Path.Combine(root, name, "redeployed.txt"), Guid.NewGuid().ToString());

        public void Remove(string name) => Directory.Delete(Path.Combine(root, name), recursive: true);

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            for (var attempt = 0; attempt < 10; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                try
                {
                    Directory.Delete(root, recursive: true);
                    return;
                }
                catch (IOException) { await Task.Delay(50); }
                catch (UnauthorizedAccessException) { await Task.Delay(50); }
            }
        }
    }
}
