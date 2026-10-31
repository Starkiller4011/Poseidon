using Poseidon.Core.Modules;
using Poseidon.Modules.Ais;
using Poseidon.Tests.Shared;
using Xunit;

namespace CoreAIS.Tests;

public sealed class AisNormalizerTests
{
    private static readonly DateTimeOffset Measured = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static AisPositionReport Report(
        long mmsi = 366123456, double latitude = 25.25, double longitude = 55.5, double speedKnots = 10,
        double course = 90, int heading = 91, string? name = "MV EXAMPLE@@@@")
        => new(mmsi, Measured, latitude, longitude, speedKnots, course, heading, name);

    private static TrackObservation Normalize(AisPositionReport report)
    {
        Assert.True(AisNormalizer.TryNormalize(report, out var observation, out var problem), problem);
        return observation!;
    }

    [Fact]
    public void ValidReport_BecomesAnObservationInSiUnits()
    {
        var observation = Normalize(Report());
        Assert.Equal(("Core.AIS", "366123456", "MV EXAMPLE"), (observation.SourceId, observation.SourceTargetId, observation.Name));
        Assert.Equal(Measured, observation.MeasuredUtc);
        Assert.Equal(new GeodeticPosition(25.25, 55.5), observation.Position);
        Assert.Equal(10 * 1852.0 / 3600, observation.SpeedOverGroundMetresPerSecond!.Value, 9);
        Assert.Equal((90.0, 91.0), (observation.CourseOverGroundDegrees, observation.HeadingDegrees));
        Assert.Null(observation.Measurement);
    }

    [Fact]
    public void NotAvailableSentinels_BecomeUnknownValuesWhileThePositionIsKept()
    {
        var observation = Normalize(Report(speedKnots: 102.3, course: 360, heading: 511, name: "@@@@"));
        Assert.Null(observation.SpeedOverGroundMetresPerSecond);
        Assert.Null(observation.CourseOverGroundDegrees);
        Assert.Null(observation.HeadingDegrees);
        Assert.Null(observation.Name);
        Assert.NotNull(observation.Position);
    }

    [Fact]
    public void BoundaryValues_AreAccepted()
    {
        Normalize(Report(mmsi: 1, latitude: -90, longitude: -180, speedKnots: 0, course: 0, heading: 0));
        Normalize(Report(mmsi: 999_999_999, latitude: 90, longitude: 180, speedKnots: 102.2, course: 359.9, heading: 359));
    }

    [Theory]
    [InlineData(0L, 25.0, 55.0, 10.0, 90.0, 91)]
    [InlineData(-5L, 25.0, 55.0, 10.0, 90.0, 91)]
    [InlineData(1_000_000_000L, 25.0, 55.0, 10.0, 90.0, 91)]
    [InlineData(366123456L, 91.0, 55.0, 10.0, 90.0, 91)]
    [InlineData(366123456L, 25.0, 181.0, 10.0, 90.0, 91)]
    [InlineData(366123456L, 95.0, 55.0, 10.0, 90.0, 91)]
    [InlineData(366123456L, double.NaN, 55.0, 10.0, 90.0, 91)]
    [InlineData(366123456L, 25.0, 55.0, -1.0, 90.0, 91)]
    [InlineData(366123456L, 25.0, 55.0, 150.0, 90.0, 91)]
    [InlineData(366123456L, 25.0, 55.0, 10.0, 361.0, 91)]
    [InlineData(366123456L, 25.0, 55.0, 10.0, -1.0, 91)]
    [InlineData(366123456L, 25.0, 55.0, 10.0, 90.0, 360)]
    [InlineData(366123456L, 25.0, 55.0, 10.0, 90.0, -1)]
    public void InvalidReports_AreRejectedWithAProblem(
        long mmsi, double latitude, double longitude, double speed, double course, int heading)
    {
        Assert.False(AisNormalizer.TryNormalize(
            new AisPositionReport(mmsi, Measured, latitude, longitude, speed, course, heading, null),
            out var observation, out var problem));
        Assert.Null(observation);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void DefaultMeasuredTime_IsRejected()
    {
        Assert.False(AisNormalizer.TryNormalize(Report() with { MeasuredUtc = default }, out _, out var problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }
}

public sealed class AisModuleTests
{
    private static readonly DateTimeOffset Measured = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static AisPositionReport Report(long mmsi = 366123456, int seconds = 0)
        => new(mmsi, Measured.AddSeconds(seconds), 25.25, 55.5, 10, 90, 91, "MV EXAMPLE");

    [Fact]
    public void Manifest_DeclaresItsInputPointAndDependsOnTracksOnly()
    {
        var manifest = ModuleDeclarationTests.DeployedManifest();
        var point = Assert.Single(manifest.Provides);
        Assert.Equal((SensorPoints.AisInput, PointCardinality.Single, typeof(IAisInput).FullName),
            (point.Id, point.Cardinality, point.Contract));
        var contribution = Assert.Single(manifest.Contributions);
        Assert.Equal((SensorPoints.AisInput, SensorPoints.AisInputFeatureId), (contribution.Point, contribution.Id));
        Assert.Equal("Core.Tracks", Assert.Single(manifest.Dependencies).Id);
        Assert.IsAssignableFrom<IAisInput>(new AisModule().Features[SensorPoints.AisInputFeatureId]);
    }

    [Fact]
    public void Submit_BeforeStart_IsUnavailable()
    {
        var result = new AisModule().Submit(Report());
        Assert.Equal((false, RejectionReason.Unavailable), (result.Accepted, result.Reason));
    }

    [Fact]
    public async Task RunningAdapter_NormalizesAndForwardsReportsAndHealthToTracks()
    {
        // Registration now happens on a background task (21.3, R23), so the first Submit after StartAsync
        // can race it; poll past that window rather than assume it has already completed.
        var host = new FakeTrackHost();
        var module = new AisModule();
        await module.StartAsync(host, null, CancellationToken.None);

        Assert.True((await SubmissionPolling.UntilRegisteredAsync(() => module.Submit(Report()))).Accepted);
        Assert.True(module.Submit(Report(seconds: 5)).Accepted);
        module.ReportSourceHealth(SourceHealth.Disconnected, "no signal");
        var invalid = module.Submit(Report(mmsi: 0));
        Assert.Equal((false, RejectionReason.Invalid), (invalid.Accepted, invalid.Reason));

        await host.Publisher.WaitUntilAsync(() => host.Publisher.Calls.Count == 3, "delivery");
        Assert.Equal(new[] { "obs:1", "obs:2", "health:Core.AIS:Disconnected" }, host.Publisher.Calls);
        var first = host.Publisher.Observations[0];
        Assert.Equal(("Core.AIS", "366123456"), (first.SourceId, first.SourceTargetId));
        Assert.Equal(10 * 1852.0 / 3600, first.SpeedOverGroundMetresPerSecond!.Value, 9);
        await module.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Adapter_StopsIdempotently_AndRefusesInputWhenStopped()
    {
        var module = new AisModule();
        await module.StartAsync(new FakeTrackHost(), null, CancellationToken.None);
        await module.StopAsync(CancellationToken.None);
        await module.StopAsync(CancellationToken.None);
        Assert.Equal(RejectionReason.Unavailable, module.Submit(Report()).Reason);
    }

    [Fact]
    public async Task Sequence_IsCheckpointedAcrossRestarts_AndUnsupportedStateIsRejected()
    {
        // AisModule owns its own sequence counter and checkpoint now (ObservationRelay owned this before).
        var host = new FakeTrackHost();
        var module = new AisModule();
        await module.StartAsync(host, null, CancellationToken.None);
        Assert.True((await SubmissionPolling.UntilRegisteredAsync(() => module.Submit(Report()))).Accepted);
        Assert.True(module.Submit(Report(seconds: 1)).Accepted);
        var state = await module.CaptureStateAsync(CancellationToken.None);
        await module.StopAsync(CancellationToken.None);
        Assert.NotNull(state);

        var restarted = new AisModule();
        await restarted.StartAsync(host, state, CancellationToken.None);
        Assert.True((await SubmissionPolling.UntilRegisteredAsync(() => restarted.Submit(Report(seconds: 2)))).Accepted);
        await host.Publisher.WaitUntilAsync(() => host.Publisher.Observations.Any(o => o.Sequence == 3), "delivery");
        await restarted.StopAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AisModule().StartAsync(host, new ModuleState(99, []), CancellationToken.None).AsTask());
    }
}
