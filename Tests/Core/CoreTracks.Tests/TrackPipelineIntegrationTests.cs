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

    private static async Task<SubmissionResult> SubmitAis(ModuleRuntime runtime, AisPositionReport report)
        => await runtime.InvokeAsync<IAisInput, SubmissionResult>(SensorPoints.AisInputFeatureId, i => i.Submit(report));

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

    private static RadarTargetReport Radar(int target = 7, int seconds = 0)
        => new(target, Measured.AddSeconds(seconds), 50, 10, BearingReference.True,
            new SensorPose(new GeodeticPosition(25.25, 55.5), 0));

    private static Task<SubmissionResult> SubmitRadar(ModuleRuntime runtime, RadarTargetReport report)
        => runtime.InvokeAsync<IRadarInput, SubmissionResult>(SensorPoints.RadarInputFeatureId, i => i.Submit(report));

    [Fact]
    public async Task AisInput_BecomesAManagedTrack_AndRepeatReportsUpdateTheSameTrack()
    {
        await using var modules = new DeployedModules();
        modules.Deploy("CoreTracks", "CoreAIS", "CoreRadar", "CoreSonar");
        Assert.True(await modules.Runtime.ReloadAsync());
        Assert.All(modules.Runtime.Status.Packages, p => Assert.Equal(PackageState.Active, p.State));
        // tracks.ingestion/ITrackIngestion are removed entirely this slice (21.2, 21.6); only the three
        // publisher-era points remain.
        Assert.Equal(
            new[]
            {
                SensorPoints.AisInput, SensorPoints.RadarInput, TrackPoints.Feed,
                TrackPoints.Operations, TrackPoints.Publisher
            },
            modules.Runtime.Status.Features.Select(f => f.PointId!).Order(StringComparer.Ordinal));

        Assert.True((await SubmitAis(modules.Runtime, Ais())).Accepted);
        var first = await WaitForTracks(modules.Runtime, s => s.Tracks.Count == 1);
        var ais = Assert.Single(first.Tracks);
        Assert.Equal("MV EXAMPLE", ais.Name);
        Assert.Equal(("Core.AIS", "366123456"), (ais.Contributions.Single().SourceId, ais.Contributions.Single().SourceTargetId));
        Assert.Equal(new GeodeticPosition(25.25, 55.5), ais.Position);
        // Speed/course are reported only once the fused velocity has converged (R8); a single report does not
        // get there, so SpeedOverGroundMetresPerSecond is still null here rather than echoing the raw report.

        Assert.True((await SubmitAis(modules.Runtime, Ais(seconds: 10))).Accepted);
        var updated = await WaitForTracks(modules.Runtime, s => s.Tracks.Any(t => t.UpdateCount == 2));
        Assert.Equal(ais.Id, Assert.Single(updated.Tracks).Id);
    }

    [Fact]
    public async Task AisAndRadar_OfOneVessel_FuseIntoOneTrack_WhichSurvivesReload_AndOutlivesEitherSource()
    {
        // The real-runtime fusion scenario (R27, R28): one vessel, two real sensor modules registering through
        // the publisher, one fused track whose number survives a reload and whose life does not depend on
        // either single source.
        await using var modules = new DeployedModules();
        modules.Deploy("CoreTracks", "CoreAIS", "CoreRadar");
        Assert.True(await modules.Runtime.ReloadAsync());
        Assert.All(modules.Runtime.Status.Packages, p => Assert.Equal(PackageState.Active, p.State));

        Assert.True((await SubmitAis(modules.Runtime, Ais())).Accepted);
        var afterAis = await WaitForTracks(modules.Runtime, s => s.Tracks.Count == 1);
        var trackId = Assert.Single(afterAis.Tracks).Id;

        Assert.True((await SubmitRadar(modules.Runtime, Radar())).Accepted);
        var fused = await WaitForTracks(modules.Runtime, s => s.Tracks.Any(t => t.Contributions.Count == 2));
        var track = Assert.Single(fused.Tracks);
        Assert.Equal(trackId, track.Id);
        Assert.Equal(
            new[] { "Core.AIS", "Core.Radar" },
            track.Contributions.Select(c => c.SourceId).OrderBy(id => id, StringComparer.Ordinal));

        var generation = modules.Runtime.Status.Generation;
        modules.Touch("CoreTracks");
        Assert.True(await modules.Runtime.ReloadAsync());
        Assert.True(modules.Runtime.Status.Generation > generation);
        var afterReload = await WaitForTracks(modules.Runtime, s => s.Tracks.Count == 1);
        Assert.Equal(trackId, Assert.Single(afterReload.Tracks).Id);

        await modules.Runtime.InvokeAsync<IAisInput, bool>(SensorPoints.AisInputFeatureId, i =>
        {
            i.ReportSourceHealth(SourceHealth.Disconnected, "no signal");
            return true;
        });
        var aisDown = await WaitForTracks(modules.Runtime, s => s.Tracks.Any(t => t.Id == trackId));
        Assert.Equal(TrackState.Active, Assert.Single(aisDown.Tracks).State);

        Assert.True((await SubmitRadar(modules.Runtime, Radar(seconds: 10))).Accepted);
        var stillAlive = await WaitForTracks(modules.Runtime, s => s.Tracks.Any(t => t.UpdateCount == 3));
        Assert.Equal(trackId, Assert.Single(stillAlive.Tracks).Id);
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
        Assert.Equal(2, track.Contributions.Single().LastSequence);
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
