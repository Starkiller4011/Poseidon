using Poseidon.Core.Modules;

namespace CoreTracks.Tests;

internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset now;

    public ManualTime(DateTimeOffset start) => now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}

internal static class TrackTestData
{
    public static readonly DateTimeOffset Start = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    public static TrackObservation Observation(
        string source = "Core.AIS", string? target = "100", long sequence = 1, int seconds = 0,
        double latitude = 25.0, double longitude = 55.0, string? name = null,
        double positionUncertaintyMetres = 10, double? speed = 5, double? course = 90)
        => new()
        {
            SourceId = source,
            SourceTargetId = target,
            Sequence = sequence,
            MeasuredUtc = Start.AddSeconds(seconds),
            ReceivedUtc = Start.AddSeconds(seconds),
            Name = name,
            Position = new GeodeticPosition(latitude, longitude),
            PositionUncertaintyMetres = positionUncertaintyMetres,
            SpeedOverGroundMetresPerSecond = speed,
            CourseOverGroundDegrees = course,
            HeadingDegrees = 91
        };

    // A bearing-only report (no range): own ship at the given position, a true bearing and its uncertainty.
    public static TrackObservation BearingObservation(
        string source = "Core.EW", string? target = "1", long sequence = 1, int seconds = 0,
        double ownShipLatitude = 25.0, double ownShipLongitude = 55.0,
        double bearingDegrees = 45, double bearingUncertaintyDegrees = 5)
        => new()
        {
            SourceId = source,
            SourceTargetId = target,
            Sequence = sequence,
            MeasuredUtc = Start.AddSeconds(seconds),
            ReceivedUtc = Start.AddSeconds(seconds),
            Measurement = new PolarMeasurement(
                null, bearingDegrees, BearingReference.True,
                new SensorPose(new GeodeticPosition(ownShipLatitude, ownShipLongitude), null), bearingUncertaintyDegrees)
        };

    public static TrackSourceDescriptor Descriptor(
        string sourceId = "Core.AIS", string kind = TrackSourceKinds.Ais,
        int intervalSeconds = 10, int staleSeconds = 240, int lostSeconds = 360, int removeSeconds = 600,
        double positionUncertaintyMetres = 10, double velocityUncertaintyMetresPerSecond = 0.5,
        int peakReportsPerSecond = 1000, double? bearingUncertaintyDegrees = null,
        double? maximumAssociationRangeMetres = null)
        => new(
            sourceId, kind, TimeSpan.FromSeconds(intervalSeconds), TimeSpan.FromSeconds(staleSeconds),
            TimeSpan.FromSeconds(lostSeconds), TimeSpan.FromSeconds(removeSeconds), positionUncertaintyMetres,
            velocityUncertaintyMetresPerSecond, peakReportsPerSecond, bearingUncertaintyDegrees,
            maximumAssociationRangeMetres);
}
