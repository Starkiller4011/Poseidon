namespace Poseidon.Core.Modules;

// Points declared by the sensor adapters that feed Tracks (AIS and radar). A transport (serial, network,
// replay) pushes native reports to the adapter's input feature through IModuleHost.InvokeAsync; the adapter
// normalizes them and forwards them to Tracks.
public static class SensorPoints
{
    public const string AisInput = "ais.input";
    public const string RadarInput = "radar.input";
    public const string AisInputFeatureId = "core.ais.input";
    public const string RadarInputFeatureId = "core.radar.input";
}

public sealed record SubmissionResult(bool Accepted, RejectionReason Reason = RejectionReason.None, string? Detail = null)
{
    public static SubmissionResult Success { get; } = new(true);

    public static SubmissionResult Rejected(RejectionReason reason, string? detail = null) => new(false, reason, detail);
}

// A position report in the AIS wire encoding: 91 degrees latitude, 181 degrees longitude, 102.3 knots,
// 360 degrees course and 511 degrees heading all mean "not available".
public sealed record AisPositionReport(
    long Mmsi,
    DateTimeOffset MeasuredUtc,
    double LatitudeDegrees,
    double LongitudeDegrees,
    double SpeedOverGroundKnots,
    double CourseOverGroundDegrees,
    int TrueHeadingDegrees,
    string? ShipName);

public interface IAisInput
{
    SubmissionResult Submit(AisPositionReport report);
    void ReportSourceHealth(SourceHealth health, string? detail = null);
}

// A radar target as reported by the radar's own tracker (for example an ARPA target number). Without a pose
// (and a heading, for a relative bearing) no geographic position can be derived, so the report is rejected.
public sealed record RadarTargetReport(
    int TargetNumber,
    DateTimeOffset MeasuredUtc,
    double RangeMetres,
    double BearingDegrees,
    BearingReference BearingReference,
    SensorPose? Pose = null,
    double? SpeedOverGroundMetresPerSecond = null,
    double? CourseOverGroundDegrees = null,
    double? PositionUncertaintyMetres = null);

public interface IRadarInput
{
    SubmissionResult Submit(RadarTargetReport report);
    void ReportSourceHealth(SourceHealth health, string? detail = null);
}
