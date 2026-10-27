namespace Poseidon.Core.Modules;

// Points declared by the Tracks module. The IDs are shared so consumers can find the effective
// contribution in RuntimeStatus without referencing Tracks.
public static class TrackPoints
{
    public const string Ingestion = "tracks.ingestion";
    public const string Feed = "tracks.feed";
    public const string IngestionFeatureId = "core.tracks.ingestion";
    public const string FeedFeatureId = "core.tracks.feed";
}

// WGS 84 geodetic position in decimal degrees.
public sealed record GeodeticPosition(double LatitudeDegrees, double LongitudeDegrees);

public enum BearingReference
{
    True,
    // Measured clockwise from the sensor platform's bow; needs a heading to become a true bearing.
    Relative
}

// A range and/or bearing from a sensor. Either value may be unknown. Pose is the own-ship position (and
// heading) when the sensor measured; BearingUncertaintyDegrees is one sigma.
public sealed record PolarMeasurement(
    double? RangeMetres,
    double? BearingDegrees,
    BearingReference BearingReference,
    SensorPose? Pose = null,
    double? BearingUncertaintyDegrees = null);

// Where the sensor was when it measured; heading is the platform's true heading.
public sealed record SensorPose(GeodeticPosition Position, double? HeadingTrueDegrees);

// String constants rather than an enum so that other modules (for example CMS sensors) can add kinds.
public static class TrackSourceKinds
{
    public const string Ais = "ais";
    public const string Radar = "radar";
    public const string Camera = "camera";
    public const string DirectionFinder = "direction-finder";
}

// What a source tells Tracks when it registers: how often it reports, how quickly its targets go stale, are
// lost and are removed, how accurate its reports are by default, and its expected peak rate (which sizes its
// queue in Tracks). A null bearing uncertainty means the source does not report bearings; a null maximum
// association range means bearings are associated by angle alone.
public sealed record TrackSourceDescriptor(
    string SourceId,
    string Kind,
    TimeSpan ExpectedUpdateInterval,
    TimeSpan StaleAfter,
    TimeSpan LostAfter,
    TimeSpan RemoveAfter,
    double PositionUncertaintyMetres,
    double VelocityUncertaintyMetresPerSecond,
    int ExpectedPeakReportsPerSecond,
    double? BearingUncertaintyDegrees = null,
    double? MaximumAssociationRangeMetres = null)
{
    // Returns why the descriptor cannot be used, or null when it is acceptable.
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceId))
            return "The source ID is required.";
        if (string.IsNullOrWhiteSpace(Kind))
            return "The source kind is required.";
        if (ExpectedUpdateInterval <= TimeSpan.Zero)
            return "The expected update interval must be positive.";
        if (StaleAfter <= TimeSpan.Zero || LostAfter <= StaleAfter || RemoveAfter <= LostAfter)
            return "The stale, lost and removal times must be positive and increasing.";
        if (!double.IsFinite(PositionUncertaintyMetres) || PositionUncertaintyMetres <= 0)
            return "The default position uncertainty must be a positive number of metres.";
        if (!double.IsFinite(VelocityUncertaintyMetresPerSecond) || VelocityUncertaintyMetresPerSecond <= 0)
            return "The default velocity uncertainty must be a positive number of metres per second.";
        if (ExpectedPeakReportsPerSecond < 1)
            return "The expected peak report rate must be at least one report per second.";
        if (BearingUncertaintyDegrees is { } bearing && !TrackObservationRules.IsBearingUncertainty(bearing))
            return "The default bearing uncertainty must be above 0 and below 180 degrees.";
        if (MaximumAssociationRangeMetres is { } range && (!double.IsFinite(range) || range <= 0))
            return "The maximum association range must be a positive number of metres.";
        return null;
    }
}

public enum SourceHealth
{
    Healthy,
    Degraded,
    // The feed from the source is gone; its targets have not necessarily disappeared.
    Disconnected
}

// A normalized report from a sensor adapter. Position and kinematics use SI units and true bearings.
public sealed record TrackObservation
{
    public required string SourceId { get; init; }
    // The source's own identifier for the target; it is not the managed track ID.
    public string? SourceTargetId { get; init; }
    public long Sequence { get; init; }
    public DateTimeOffset MeasuredUtc { get; init; }
    public DateTimeOffset ReceivedUtc { get; init; }
    public string? Name { get; init; }
    public GeodeticPosition? Position { get; init; }
    public double? PositionUncertaintyMetres { get; init; }
    public double? SpeedOverGroundMetresPerSecond { get; init; }
    public double? CourseOverGroundDegrees { get; init; }
    public double? HeadingDegrees { get; init; }
    public PolarMeasurement? Measurement { get; init; }
}

public enum ObservationOutcome
{
    Created,
    Updated,
    Rejected
}

public enum RejectionReason
{
    None,
    Invalid,
    MissingTargetId,
    Duplicate,
    OutOfOrder,
    CapacityExceeded,
    // The adapter's bounded queue was full.
    QueueFull,
    // The adapter or the feature it relies on is not running.
    Unavailable
}

public sealed record ObservationResult(
    ObservationOutcome Outcome, string? TrackId, RejectionReason Reason = RejectionReason.None, string? Detail = null)
{
    public bool IsAccepted => Outcome != ObservationOutcome.Rejected;

    public static ObservationResult Rejected(RejectionReason reason, string? detail = null)
        => new(ObservationOutcome.Rejected, null, reason, detail);
}

// Only Tracks creates managed tracks; sensors submit observations and never call Display directly.
public interface ITrackIngestion
{
    ObservationResult Submit(TrackObservation observation);
    IReadOnlyList<ObservationResult> SubmitBatch(IReadOnlyList<TrackObservation> observations);
    void ReportSourceHealth(string sourceId, SourceHealth health, string? detail = null);
}

public enum TrackState
{
    Active,
    // No recent update, or the source is disconnected.
    Stale,
    // Not heard from for the lost interval; removed after a further interval.
    Lost
}

// An immutable managed track. Position is null for measurement-only tracks.
public sealed record Track(
    string Id,
    string SourceId,
    string SourceTargetId,
    string? Name,
    TrackState State,
    GeodeticPosition? Position,
    double? PositionUncertaintyMetres,
    double? SpeedOverGroundMetresPerSecond,
    double? CourseOverGroundDegrees,
    double? HeadingDegrees,
    PolarMeasurement? LatestMeasurement,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset MeasuredUtc,
    DateTimeOffset UpdatedUtc,
    long LastSequence,
    int UpdateCount,
    long Revision);

public sealed record SourceStatus(string SourceId, SourceHealth Health, string? Detail, DateTimeOffset UpdatedUtc);

public sealed record TrackSnapshot(
    string Epoch, long Revision, IReadOnlyList<Track> Tracks, IReadOnlyList<SourceStatus> Sources);

public enum TrackChangeKind
{
    Upsert,
    Remove,
    Source
}

public sealed record TrackChange(
    long Revision, TrackChangeKind Kind, Track? Track = null, string? TrackId = null, SourceStatus? Source = null);

// Changes strictly after the requested revision, in order and without gaps. Revision is the revision of the
// last returned change (or the unchanged cursor). When ResyncRequired is set the cursor is unusable and the
// consumer must take a new snapshot.
public sealed record TrackChanges(
    string Epoch, long Revision, IReadOnlyList<TrackChange> Changes, bool ResyncRequired);

// A pull feed: consumers keep only a cursor and so never pin a retired Tracks load context.
public interface ITrackFeed
{
    TrackSnapshot GetSnapshot();
    TrackChanges GetChanges(string? epoch, long afterRevision, int maxChanges = 1000);
}
