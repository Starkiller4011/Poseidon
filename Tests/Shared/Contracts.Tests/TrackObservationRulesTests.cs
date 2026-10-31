using Poseidon.Core.Modules;
using Xunit;

namespace Contracts.Tests;

// R1: invalid reports are rejected with a reason. R16a: a report gives a position, a range and bearing, or a
// bearing only; a polar report needs the own-ship position, and a relative bearing also needs the heading.
public sealed class TrackObservationRulesTests
{
    private static readonly SensorPose Pose = new(new GeodeticPosition(25, 55), null);
    private static readonly SensorPose PoseWithHeading = new(new GeodeticPosition(25, 55), 90);

    private static TrackObservation Polar(PolarMeasurement measurement) => Valid() with
    {
        Position = null,
        Measurement = measurement
    };

    private static TrackObservation Valid() => new()
    {
        SourceId = "Core.AIS",
        SourceTargetId = "100",
        Sequence = 1,
        MeasuredUtc = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero),
        ReceivedUtc = new DateTimeOffset(2026, 10, 26, 12, 0, 1, TimeSpan.Zero),
        Position = new GeodeticPosition(25, 55),
        SpeedOverGroundMetresPerSecond = 5,
        CourseOverGroundDegrees = 359.9,
        HeadingDegrees = 0
    };

    public static TheoryData<string, Func<TrackObservation, TrackObservation>> Invalid => new()
    {
        { "blank source", o => o with { SourceId = " " } },
        { "blank target", o => o with { SourceTargetId = "" } },
        { "default measured time", o => o with { MeasuredUtc = default } },
        { "latitude above range", o => o with { Position = new GeodeticPosition(90.1, 0) } },
        { "latitude below range", o => o with { Position = new GeodeticPosition(-90.1, 0) } },
        { "longitude above range", o => o with { Position = new GeodeticPosition(0, 180.1) } },
        { "NaN latitude", o => o with { Position = new GeodeticPosition(double.NaN, 0) } },
        { "infinite longitude", o => o with { Position = new GeodeticPosition(0, double.PositiveInfinity) } },
        { "negative speed", o => o with { SpeedOverGroundMetresPerSecond = -0.1 } },
        { "NaN speed", o => o with { SpeedOverGroundMetresPerSecond = double.NaN } },
        { "course of 360", o => o with { CourseOverGroundDegrees = 360 } },
        { "negative heading", o => o with { HeadingDegrees = -1 } },
        { "negative uncertainty", o => o with { PositionUncertaintyMetres = -1 } },
        { "neither position nor measurement", o => o with { Position = null } },
        { "empty measurement", _ => Polar(new(null, null, BearingReference.True, Pose)) },
        { "range without a bearing", _ => Polar(new(1000, null, BearingReference.True, Pose)) },
        { "negative range", _ => Polar(new(-1, 10, BearingReference.True, Pose)) },
        { "bearing of 360", _ => Polar(new(100, 360, BearingReference.True, Pose)) },
        { "range and bearing without own-ship position", _ => Polar(new(1000, 10, BearingReference.True)) },
        { "bearing only without own-ship position", _ => Polar(new(null, 10, BearingReference.True)) },
        { "relative bearing without heading", _ => Polar(new(null, 10, BearingReference.Relative, Pose)) },
        { "relative range and bearing without heading", _ => Polar(new(1000, 10, BearingReference.Relative, Pose)) },
        { "own-ship latitude out of range", _ => Polar(new(null, 10, BearingReference.True, new(new(91, 0), null))) },
        { "own-ship longitude NaN", _ => Polar(new(null, 10, BearingReference.True, new(new(0, double.NaN), null))) },
        { "own-ship heading of 360", _ => Polar(new(null, 10, BearingReference.Relative, new(new(0, 0), 360))) },
        { "zero bearing uncertainty", _ => Polar(new(null, 10, BearingReference.True, Pose, 0)) },
        { "bearing uncertainty of 180", _ => Polar(new(null, 10, BearingReference.True, Pose, 180)) },
        { "NaN bearing uncertainty", _ => Polar(new(null, 10, BearingReference.True, Pose, double.NaN)) }
    };

    [Fact]
    public void PositionedObservations_AreAccepted_IncludingBoundaryValues()
    {
        Assert.Null(TrackObservationRules.Validate(Valid()));
        Assert.Null(TrackObservationRules.Validate(Valid() with { Position = new GeodeticPosition(-90, 180) }));
    }

    [Fact]
    public void APositionedObservation_MayCarryTheMeasurementItCameFrom()
    {
        // A radar adapter that already converted range and bearing keeps the measurement for reference.
        Assert.Null(TrackObservationRules.Validate(Valid() with
        {
            Measurement = new PolarMeasurement(1852, 0, BearingReference.True)
        }));
    }

    [Fact]
    public void RangeAndBearing_IsAcceptedWithTheOwnShipPosition_AndRelativeBearingsWithItsHeading()
    {
        Assert.Null(TrackObservationRules.Validate(Polar(new(1852, 0, BearingReference.True, Pose))));
        Assert.Null(TrackObservationRules.Validate(Polar(new(0, 359.9, BearingReference.True, Pose))));
        Assert.Null(TrackObservationRules.Validate(Polar(new(1852, 10, BearingReference.Relative, PoseWithHeading))));
    }

    [Fact]
    public void BearingOnly_IsAcceptedWithTheOwnShipPosition_AndRelativeBearingsWithItsHeading()
    {
        Assert.Null(TrackObservationRules.Validate(Polar(new(null, 42.5, BearingReference.True, Pose))));
        Assert.Null(TrackObservationRules.Validate(Polar(new(null, 42.5, BearingReference.True, Pose, 0.5))));
        Assert.Null(TrackObservationRules.Validate(Polar(new(null, 0, BearingReference.Relative, PoseWithHeading, 5))));
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void InvalidObservations_AreRejectedWithAReason(string label, Func<TrackObservation, TrackObservation> mutate)
    {
        var reason = TrackObservationRules.Validate(mutate(Valid()));
        Assert.False(string.IsNullOrWhiteSpace(reason), label);
    }
}
