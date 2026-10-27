using Poseidon.Core.Modules;
using Poseidon.Modules.Radar;
using Poseidon.Tests.Shared;
using Xunit;

namespace CoreRadar.Tests;

public sealed class RadarNormalizerTests
{
    private const double OneDegreeMetres = Geodesy.MeanEarthRadiusMetres * Math.PI / 180;
    private static readonly DateTimeOffset Measured = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly SensorPose OriginHeadingEast = new(new GeodeticPosition(0, 0), 90);

    private static RadarTargetReport Report(
        double range = OneDegreeMetres, double bearing = 0, BearingReference reference = BearingReference.True,
        SensorPose? pose = null, int target = 12)
        => new(target, Measured, range, bearing, reference, pose);

    private static TrackObservation Normalize(RadarTargetReport report)
    {
        Assert.True(RadarNormalizer.TryNormalize(report, out var observation, out var problem), problem);
        return observation!;
    }

    [Fact]
    public void TrueBearingWithPose_ProjectsAPositionAndKeepsTheMeasurement()
    {
        var observation = Normalize(Report(pose: OriginHeadingEast) with
        {
            SpeedOverGroundMetresPerSecond = 4, CourseOverGroundDegrees = 270, PositionUncertaintyMetres = 30
        });
        Assert.Equal(("Core.Radar", "12", Measured), (observation.SourceId, observation.SourceTargetId, observation.MeasuredUtc));
        Assert.Equal(1, observation.Position!.LatitudeDegrees, 6);
        Assert.Equal(0, observation.Position.LongitudeDegrees, 6);
        Assert.Equal(new PolarMeasurement(OneDegreeMetres, 0, BearingReference.True), observation.Measurement);
        Assert.Equal((4.0, 270.0, 30.0),
            (observation.SpeedOverGroundMetresPerSecond, observation.CourseOverGroundDegrees, observation.PositionUncertaintyMetres));
    }

    [Fact]
    public void RelativeBearing_UsesThePlatformHeading()
    {
        var observation = Normalize(Report(bearing: 0, reference: BearingReference.Relative, pose: OriginHeadingEast));
        Assert.Equal(0, observation.Position!.LatitudeDegrees, 6);
        Assert.Equal(1, observation.Position.LongitudeDegrees, 6);
    }

    [Fact]
    public void WithoutAPoseOrWithARelativeBearingAndNoHeading_TheReportIsRejectedWithAProblem()
    {
        // R16a and R32: no position can be calculated, so the report is not used.
        Assert.False(RadarNormalizer.TryNormalize(Report(), out var noPose, out var noPoseProblem));
        Assert.Null(noPose);
        Assert.False(string.IsNullOrWhiteSpace(noPoseProblem));

        Assert.False(RadarNormalizer.TryNormalize(Report(reference: BearingReference.Relative,
            pose: new SensorPose(new GeodeticPosition(0, 0), null)), out var noHeading, out var noHeadingProblem));
        Assert.Null(noHeading);
        Assert.False(string.IsNullOrWhiteSpace(noHeadingProblem));
    }

    [Theory]
    [InlineData(0.0, 10.0, 12)]
    [InlineData(-5.0, 10.0, 12)]
    [InlineData(double.NaN, 10.0, 12)]
    [InlineData(double.PositiveInfinity, 10.0, 12)]
    [InlineData(1000.0, 360.0, 12)]
    [InlineData(1000.0, -1.0, 12)]
    [InlineData(1000.0, double.NaN, 12)]
    [InlineData(1000.0, 10.0, -1)]
    public void InvalidReports_AreRejectedWithAProblem(double range, double bearing, int target)
    {
        Assert.False(RadarNormalizer.TryNormalize(Report(range, bearing, target: target), out var observation, out var problem));
        Assert.Null(observation);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void InvalidKinematicsAndPoseAreRejected()
    {
        Assert.False(RadarNormalizer.TryNormalize(Report() with { SpeedOverGroundMetresPerSecond = -1 }, out _, out var speed));
        Assert.False(string.IsNullOrWhiteSpace(speed));
        Assert.False(RadarNormalizer.TryNormalize(
            Report(pose: new SensorPose(new GeodeticPosition(120, 0), 0)), out _, out var pose));
        Assert.False(string.IsNullOrWhiteSpace(pose));
    }
}

public sealed class RadarModuleTests
{
    private static readonly DateTimeOffset Measured = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static RadarTargetReport Report(int target = 12, int seconds = 0)
        => new(target, Measured.AddSeconds(seconds), 5000, 45, BearingReference.True,
            new SensorPose(new GeodeticPosition(25, 55), 0));

    [Fact]
    public void Manifest_DeclaresItsInputPointAndDependsOnTracksOnly()
    {
        var manifest = ModuleDeclarationTests.DeployedManifest();
        var point = Assert.Single(manifest.Provides);
        Assert.Equal((SensorPoints.RadarInput, PointCardinality.Single, typeof(IRadarInput).FullName),
            (point.Id, point.Cardinality, point.Contract));
        var contribution = Assert.Single(manifest.Contributions);
        Assert.Equal((SensorPoints.RadarInput, SensorPoints.RadarInputFeatureId), (contribution.Point, contribution.Id));
        Assert.Equal("Core.Tracks", Assert.Single(manifest.Dependencies).Id);
        Assert.IsAssignableFrom<IRadarInput>(new RadarModule().Features[SensorPoints.RadarInputFeatureId]);
    }

    [Fact]
    public async Task Adapter_IsUnavailableUntilStarted_ThenForwardsReportsAndHealth()
    {
        var module = new RadarModule();
        Assert.Equal(RejectionReason.Unavailable, module.Submit(Report()).Reason);

        var host = new FakeTrackHost();
        await module.StartAsync(host, null, CancellationToken.None);
        Assert.True(module.Submit(Report()).Accepted);
        module.ReportSourceHealth(SourceHealth.Degraded, "clutter");
        Assert.Equal(RejectionReason.Invalid, module.Submit(Report() with { RangeMetres = 0 }).Reason);

        await host.Ingestion.WaitUntilAsync(() => host.Ingestion.Calls.Count == 2, "delivery");
        Assert.Equal(new[] { "obs:1", "health:Core.Radar:Degraded" }, host.Ingestion.Calls);
        Assert.NotNull(host.Ingestion.Observations[0].Position);
        await module.StopAsync(CancellationToken.None);
        await module.StopAsync(CancellationToken.None);
        Assert.Equal(RejectionReason.Unavailable, module.Submit(Report()).Reason);
    }

    [Fact]
    public async Task Sequence_IsCheckpointedAcrossRestarts_AndUnsupportedStateIsRejected()
    {
        var host = new FakeTrackHost();
        var module = new RadarModule();
        await module.StartAsync(host, null, CancellationToken.None);
        Assert.True(module.Submit(Report()).Accepted);
        var state = await module.CaptureStateAsync(CancellationToken.None);
        await module.StopAsync(CancellationToken.None);
        Assert.NotNull(state);

        var restarted = new RadarModule();
        await restarted.StartAsync(host, state, CancellationToken.None);
        Assert.True(restarted.Submit(Report(seconds: 2)).Accepted);
        await host.Ingestion.WaitUntilAsync(() => host.Ingestion.Observations.Any(o => o.Sequence == 2), "delivery");
        await restarted.StopAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new RadarModule().StartAsync(host, new ModuleState(99, []), CancellationToken.None).AsTask());
    }
}
