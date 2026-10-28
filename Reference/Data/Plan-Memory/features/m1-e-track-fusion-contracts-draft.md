# M1-E fusion: contracts draft

This is the reworked `System/Shared/Contracts/Tracks.cs` that was written at the
start of slice 1 and then reverted, kept so the plan in
[m1-e-track-fusion.md](m1-e-track-fusion.md) can be picked up from it. It is a
draft, not approved code: under the workflow
([development-workflow.md](../development-workflow.md)) the tests for slice 1 are
written and approved first, and this file is only a starting point for Step 3.

Differences from the plan: the draft has no per-report sequence number and no
contribution last sequence (duplicates are detected by measured time and
position); it adds per-source statistics and the system health described in
section 21.2. Things the plan still needs and the draft lacks:
`TrackMotion.Predict`, the removal of `ObservationRelay`, `SetAntennaRotationRate`
on `IRadarInput`, and `PolarMeasurement` (removed here because range and bearing
are on the radar report itself).

```csharp
namespace Poseidon.Core.Modules;

// Points declared by the Tracks module. The IDs are shared so consumers can find the effective
// contribution in RuntimeStatus without referencing Tracks.
public static class TrackPoints
{
    public const string Publisher = "tracks.publisher";
    public const string Feed = "tracks.feed";
    public const string Operations = "tracks.operations";
    public const string PublisherFeatureId = "core.tracks.publisher";
    public const string FeedFeatureId = "core.tracks.feed";
    public const string OperationsFeatureId = "core.tracks.operations";
}

// WGS 84 geodetic position in decimal degrees.
public sealed record GeodeticPosition(double LatitudeDegrees, double LongitudeDegrees);

public enum BearingReference
{
    True,
    // Measured clockwise from the sensor platform's bow; needs a heading to become a true bearing.
    Relative
}

// The own-ship position (and true heading, when known) at the moment a sensor measured. Range and bearing
// reports are converted to a position from it.
public sealed record SensorPose(GeodeticPosition Position, double? HeadingTrueDegrees);

public enum SourceHealth
{
    Healthy,
    Degraded,
    // The feed from the source is gone; its targets have not necessarily disappeared.
    Disconnected
}

// String constants rather than an enum so that other modules (for example CMS sensors) can add kinds.
public static class TrackSourceKinds
{
    public const string Ais = "ais";
    public const string Radar = "radar";
}

// What a source tells Tracks when it registers: how often it reports, how quickly its targets go stale, are
// lost and are removed, how accurate its reports are by default, and its expected peak rate (which sizes
// its queue in Tracks).
public sealed record TrackSourceDescriptor(
    string SourceId,
    string Kind,
    TimeSpan ExpectedUpdateInterval,
    TimeSpan StaleAfter,
    TimeSpan LostAfter,
    TimeSpan RemoveAfter,
    double PositionUncertaintyMetres,
    double VelocityUncertaintyMetresPerSecond,
    int ExpectedPeakReportsPerSecond)
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
        return null;
    }
}

// A normalized, positioned report from a sensor adapter. Position and kinematics use SI units and true
// bearings. The source's own target ID (a radar's target number, an AIS MMSI) is not the managed track ID.
public sealed record TrackObservation
{
    public required string SourceId { get; init; }
    public string? SourceTargetId { get; init; }
    public DateTimeOffset MeasuredUtc { get; init; }
    // Stamped by the sink when the report is published; track ageing uses it.
    public DateTimeOffset ReceivedUtc { get; init; }
    public string? Name { get; init; }
    public required GeodeticPosition Position { get; init; }
    public double? PositionUncertaintyMetres { get; init; }
    public double? SpeedOverGroundMetresPerSecond { get; init; }
    public double? CourseOverGroundDegrees { get; init; }
    public double? HeadingDegrees { get; init; }
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
    // The adapter or the feature it relies on is not running or not yet connected.
    Unavailable
}

public sealed record ObservationResult(
    ObservationOutcome Outcome, string? TrackId, RejectionReason Reason = RejectionReason.None, string? Detail = null)
{
    public bool IsAccepted => Outcome != ObservationOutcome.Rejected;

    public static ObservationResult Rejected(RejectionReason reason, string? detail = null)
        => new(ObservationOutcome.Rejected, null, reason, detail);
}

// A source's handle for publishing into the queue Tracks keeps for it. Publishing never waits for the track
// manager. Dispose when the source stops.
public interface ITrackSink : IDisposable
{
    SubmissionResult Publish(TrackObservation observation);
    void ReportHealth(SourceHealth health, string? detail = null);
    void UpdateDescriptor(TrackSourceDescriptor descriptor);
}

// Sources register once Tracks is available and receive a sink. Registering a source ID again (after a
// restart) replaces the earlier sink; an invalid descriptor, or an ID claimed by another registration that is
// still live, throws ArgumentException.
public interface ITrackPublisher
{
    ITrackSink Register(TrackSourceDescriptor descriptor);
}

public enum TrackState
{
    Active,
    // No recent update, or the source is disconnected.
    Stale,
    // Not heard from for the lost interval; removed after a further interval.
    Lost
}

// One source's report stream (its own track) inside a managed track.
public sealed record TrackContribution(
    string SourceId,
    string SourceTargetId,
    TrackState State,
    DateTimeOffset JoinedUtc,
    DateTimeOffset MeasuredUtc,
    DateTimeOffset UpdatedUtc,
    int UpdateCount);

// An immutable managed track: the fused estimate and the contributions behind it.
public sealed record Track(
    string Id,
    string? Name,
    TrackState State,
    GeodeticPosition Position,
    double PositionUncertaintyMetres,
    double? SpeedOverGroundMetresPerSecond,
    double? CourseOverGroundDegrees,
    double? HeadingDegrees,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset MeasuredUtc,
    DateTimeOffset UpdatedUtc,
    int UpdateCount,
    long Revision,
    IReadOnlyList<TrackContribution> Contributions)
{
    // Contribution lists compare by content.
    public bool Equals(Track? other)
        => other is not null && Id == other.Id && Name == other.Name && State == other.State &&
           Position == other.Position && PositionUncertaintyMetres == other.PositionUncertaintyMetres &&
           SpeedOverGroundMetresPerSecond == other.SpeedOverGroundMetresPerSecond &&
           CourseOverGroundDegrees == other.CourseOverGroundDegrees && HeadingDegrees == other.HeadingDegrees &&
           FirstSeenUtc == other.FirstSeenUtc && MeasuredUtc == other.MeasuredUtc && UpdatedUtc == other.UpdatedUtc &&
           UpdateCount == other.UpdateCount && Revision == other.Revision &&
           Contributions.SequenceEqual(other.Contributions);

    public override int GetHashCode() => HashCode.Combine(Id, State, Position, MeasuredUtc, Revision, Contributions.Count);
}

// Counters kept by Tracks for one source since Tracks started (they are not checkpointed).
public sealed record SourceStatistics(
    long Received,
    long Created,
    long Updated,
    long Invalid,
    long Duplicate,
    long OutOfOrder,
    long CapacityExceeded,
    long Dropped,
    int QueueDepth,
    string? LastRejection);

public sealed record SourceStatus(
    string SourceId, SourceHealth Health, string? Detail, DateTimeOffset UpdatedUtc, SourceStatistics? Statistics = null);

public enum TrackSystemHealth
{
    Healthy,
    // The track management system is overloaded and is dropping reports; this is not a sensor fault.
    Degraded
}

public sealed record TrackSystemStatus(TrackSystemHealth Health, string? Detail, DateTimeOffset UpdatedUtc);

public sealed record TrackSnapshot(
    string Epoch,
    long Revision,
    IReadOnlyList<Track> Tracks,
    IReadOnlyList<SourceStatus> Sources,
    TrackSystemStatus? System = null);

public enum TrackChangeKind
{
    Upsert,
    Remove,
    Source,
    System
}

public sealed record TrackChange(
    long Revision,
    TrackChangeKind Kind,
    Track? Track = null,
    string? TrackId = null,
    SourceStatus? Source = null,
    TrackSystemStatus? System = null);

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

public sealed record TrackOperationResult(bool Succeeded, string? TrackId, string? Detail)
{
    public static TrackOperationResult Success(string trackId, string? detail = null) => new(true, trackId, detail);

    public static TrackOperationResult Failure(string detail) => new(false, null, detail);
}

// Operator corrections, called by other modules (for example a console). Both may be undone by automatic
// splitting if the contributions disagree beyond the split distance.
public interface ITrackOperations
{
    // Merges the other track into the older one, which keeps its number; returns the surviving track.
    TrackOperationResult Merge(string trackId, string otherTrackId);

    // Moves one source track out of a track into a new track with a new number.
    TrackOperationResult Split(string trackId, string sourceId, string sourceTargetId);
}
```
