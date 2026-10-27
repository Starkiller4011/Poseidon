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
        double latitude = 25.0, double longitude = 55.0, string? name = null)
        => new()
        {
            SourceId = source,
            SourceTargetId = target,
            Sequence = sequence,
            MeasuredUtc = Start.AddSeconds(seconds),
            ReceivedUtc = Start.AddSeconds(seconds),
            Name = name,
            Position = new GeodeticPosition(latitude, longitude),
            SpeedOverGroundMetresPerSecond = 5,
            CourseOverGroundDegrees = 90,
            HeadingDegrees = 91
        };
}
