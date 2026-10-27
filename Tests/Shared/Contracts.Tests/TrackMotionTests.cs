using Poseidon.Core.Modules;
using Xunit;

namespace Contracts.Tests;

// R22: TrackMotion.Predict extrapolates along the fused velocity and returns nothing without one.
// R36: it returns nothing for a bearing track, which has no position.
public sealed class TrackMotionTests
{
    private const double OneDegreeMetres = Geodesy.MeanEarthRadiusMetres * Math.PI / 180;
    private static readonly DateTimeOffset Measured = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    // The only place a Track is built, so the reshape in slice 3 changes one helper.
    private static Track Track(GeodeticPosition? position, double? speed, double? course) => new(
        "T1", "Core.Radar", "12", null, TrackState.Active, position, 30, speed, course, null, null,
        Measured, Measured, Measured, 1, 1, 1);

    [Theory]
    [InlineData(0.0, 1.0, 0.0)]
    [InlineData(90.0, 0.0, 1.0)]
    [InlineData(180.0, -1.0, 0.0)]
    public void Predict_MovesAlongTheCourseBySpeedTimesElapsedTime(double course, double expectedLatitude, double expectedLongitude)
    {
        // One degree of arc in 1,000 seconds.
        var track = Track(new GeodeticPosition(0, 0), OneDegreeMetres / 1000, course);
        var predicted = TrackMotion.Predict(track, Measured.AddSeconds(1000));
        Assert.NotNull(predicted);
        Assert.Equal(expectedLatitude, predicted.LatitudeDegrees, 6);
        Assert.Equal(expectedLongitude, predicted.LongitudeDegrees, 6);
    }

    [Fact]
    public void Predict_AtTheMeasuredTimeOrWithoutSpeed_ReturnsTheTrackPosition()
    {
        var position = new GeodeticPosition(25.25, 55.5);
        Assert.Equal(position, TrackMotion.Predict(Track(position, 10, 45), Measured));

        var stopped = TrackMotion.Predict(Track(position, 0, 45), Measured.AddMinutes(10));
        Assert.NotNull(stopped);
        Assert.Equal(25.25, stopped.LatitudeDegrees, 9);
        Assert.Equal(55.5, stopped.LongitudeDegrees, 9);
    }

    [Fact]
    public void Predict_ReturnsNothing_WhenTheVelocityIsUnknown()
    {
        var position = new GeodeticPosition(25.25, 55.5);
        Assert.Null(TrackMotion.Predict(Track(position, null, 45), Measured.AddSeconds(10)));
        Assert.Null(TrackMotion.Predict(Track(position, 10, null), Measured.AddSeconds(10)));
    }

    [Fact]
    public void Predict_ReturnsNothing_ForABearingTrack()
    {
        Assert.Null(TrackMotion.Predict(Track(null, 10, 45), Measured.AddSeconds(10)));
    }
}
