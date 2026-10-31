using Poseidon.Core.Modules;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Poseidon.Modules.Tracks;

// Starting parameters (m1-e-track-fusion.md section 21.4), tunable. Per-source timings and default
// uncertainties come from each source's registered TrackSourceDescriptor (R11); the Unregistered* values here
// are the fallback for a source that never registered one.
internal sealed record TrackPolicy(
    TimeSpan UnregisteredStaleAfter,
    TimeSpan UnregisteredLostAfter,
    TimeSpan UnregisteredRemoveAfter,
    int MaxTracks = 10_000,
    int MaxChangeLog = 10_000,
    double ProcessNoiseAccelerationMetresPerSecondSquared = 0.3,
    double PositionGateChiSquare = 9.21,
    double VelocityGateChiSquare = 9.21,
    double DominanceThreshold = 0.75,
    double MaximumJoinDistanceMetres = 200,
    int SplitConfirmations = 3,
    double BearingGateChiSquare = 6.63,
    double VelocityReportedWhenSigmaAtMost = 3,
    double InitialVelocityUncertaintyMetresPerSecond = 10,
    double UnregisteredPositionUncertaintyMetres = 50,
    double UnregisteredVelocityUncertaintyMetresPerSecond = 5)
{
    public static TrackPolicy Default { get; } =
        new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(600));
}

// A source's registration: its descriptor (null until RegisterSource is called; the unregistered defaults
// apply until then), its health, and when it last resumed from Disconnected (ageing restarts from there).
internal sealed class SourceRegistration(SourceStatus status, DateTimeOffset resumedUtc)
{
    public SourceStatus Status { get; set; } = status;
    public DateTimeOffset ResumedUtc { get; set; } = resumedUtc;
    public TrackSourceDescriptor? Descriptor { get; set; }
}

// R30's per-source counters. Received/Created/Updated/Invalid/Duplicate/OutOfOrder/CapacityExceeded and
// LastRejection come from applying reports (SubmitLocked); QueueDepth and Dropped are pushed in from the
// source's own queue in TrackIngress (RecordQueueMetrics), which the manager otherwise knows nothing about.
internal sealed class SourceStatisticsAccumulator
{
    public long Received;
    public long Created;
    public long Updated;
    public long Invalid;
    public long Duplicate;
    public long OutOfOrder;
    public long CapacityExceeded;
    public long Dropped;
    public int QueueDepth;
    public string? LastRejection;
}

// One source's contribution to a track. LastReported* holds the contribution's own raw, unfused report (never
// what the shared estimator was pulled toward), used to detect disagreement for the automatic split (R7, R35)
// and to re-estimate a track from only its remaining contributions after a split or merge.
internal sealed class ContributionEntry
{
    public required string SourceId { get; init; }
    public required string SourceTargetId { get; init; }
    public bool IsRanged { get; set; }
    public TrackState State { get; set; } = TrackState.Active;
    public DateTimeOffset JoinedUtc { get; set; }
    public DateTimeOffset MeasuredUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public long LastSequence { get; set; }
    public int UpdateCount { get; set; }
    public int DisagreementStreak { get; set; }

    public GeodeticPosition? LastReportedPosition { get; set; }
    public double? LastReportedSpeed { get; set; }
    public double? LastReportedCourse { get; set; }
    public double? LastReportedPositionUncertaintyMetres { get; set; }

    public double? LastReportedBearingDegrees { get; set; }
    public GeodeticPosition? LastReportedBearingOwnShip { get; set; }
    public double? LastReportedBearingUncertaintyDegrees { get; set; }
}

// The mutable working state behind one managed track: its shared Kalman estimate (null only for a standalone
// Bearing track that has never had a ranged contribution), or a standalone bearing reading when there is no
// estimator yet. The public, immutable Track is rebuilt from this on every publish.
internal sealed class TrackEntry
{
    public required string Id { get; init; }
    public string? Name { get; set; }
    public TrackKind Kind { get; set; } = TrackKind.Bearing;
    public TrackEstimator? Estimator { get; set; }
    public TrackBearing? StandaloneBearing { get; set; }
    public double? LastHeadingDegrees { get; set; }
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public long Revision { get; set; }
    public List<ContributionEntry> Contributions { get; } = [];
}

// The authoritative track table: correlation, per-contribution lifecycle, the Kalman-filter estimate (via
// TrackEstimator), automatic split, operator merge/split, and checkpoint/restore (m1-e-track-fusion.md section
// 21). Every mutation takes one lock and produces at most one revision per change, so the change log is gapless
// and a consumer cursor is simply the last revision it applied.
//
// Simplifications documented as starting points, consistent with the plan's own D1/D3 "revisit if testing
// shows another method is needed": gating and the dominance score use each candidate's combined (DRMS)
// position uncertainty treated as isotropic (independent, equal in both axes) rather than its true covariance,
// and the dominance likelihood considers position only, not velocity. The correlator lives in this file rather
// than a separate TrackCorrelator.cs; nothing observes file layout.
internal sealed class TrackManager : ITrackFeed, ITrackOperations
{
    private const int CheckpointSchema = 2;
    private const double DefaultBearingUncertaintyDegrees = 5;
    private static readonly JsonSerializerOptions CheckpointOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object gate = new();
    private readonly TimeProvider time;
    private readonly TrackPolicy policy;
    private readonly Dictionary<string, TrackEntry> tracks = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Source, string Target), string> index = [];
    private readonly Dictionary<string, SourceRegistration> sources = new(StringComparer.Ordinal);
    // Per-source counters for R30 and the system health for R26 (21.9); fed partly by SubmitLocked itself and
    // partly by TrackIngress's queue (RecordQueueMetrics). Not checkpointed: they are runtime observability,
    // not track state.
    private readonly Dictionary<string, SourceStatisticsAccumulator> statistics = new(StringComparer.Ordinal);
    private DateTimeOffset? lastDropUtc;
    private readonly List<TrackChange> log = [];
    private int logHead;
    private string epoch = Guid.NewGuid().ToString("N");
    private long revision;
    private long nextTrackNumber = 1;

    public TrackManager(TimeProvider time, TrackPolicy? policy = null)
    {
        this.time = time;
        this.policy = policy ?? TrackPolicy.Default;
        if (this.policy.UnregisteredStaleAfter <= TimeSpan.Zero ||
            this.policy.UnregisteredLostAfter <= this.policy.UnregisteredStaleAfter ||
            this.policy.UnregisteredRemoveAfter <= this.policy.UnregisteredLostAfter ||
            this.policy.MaxTracks <= 0 || this.policy.MaxChangeLog <= 0)
            throw new ArgumentException(
                "The unregistered stale, lost and removal intervals must increase and the limits must be positive.",
                nameof(policy));
    }

    // R11: registers (or replaces) a source's descriptor; the new timings and defaults apply immediately.
    public void RegisterSource(TrackSourceDescriptor descriptor)
    {
        if (descriptor.Validate() is { } problem)
            throw new ArgumentException(problem, nameof(descriptor));
        lock (gate)
        {
            if (sources.TryGetValue(descriptor.SourceId, out var registration))
                registration.Descriptor = descriptor;
            else
                AddSourceRegistration(descriptor.SourceId, descriptor, SourceHealth.Healthy, null, time.GetUtcNow());
        }
    }

    public ObservationResult Submit(TrackObservation observation)
    {
        lock (gate)
            return SubmitLocked(observation, time.GetUtcNow());
    }

    public IReadOnlyList<ObservationResult> SubmitBatch(IReadOnlyList<TrackObservation> observations)
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            return [.. observations.Select(o => SubmitLocked(o, now))];
        }
    }

    private ObservationResult SubmitLocked(TrackObservation observation, DateTimeOffset now)
    {
        var result = SubmitLockedCore(observation, now);
        RecordStatistic(observation.SourceId, result);
        return result;
    }

    private void RecordStatistic(string sourceId, ObservationResult result)
    {
        var accumulator = statistics.TryGetValue(sourceId, out var existing) ? existing : statistics[sourceId] = new();
        accumulator.Received++;
        switch (result.Outcome)
        {
            case ObservationOutcome.Created:
                accumulator.Created++;
                break;
            case ObservationOutcome.Updated:
                accumulator.Updated++;
                break;
            case ObservationOutcome.Rejected:
                switch (result.Reason)
                {
                    case RejectionReason.Duplicate: accumulator.Duplicate++; break;
                    case RejectionReason.OutOfOrder: accumulator.OutOfOrder++; break;
                    case RejectionReason.CapacityExceeded: accumulator.CapacityExceeded++; break;
                    default: accumulator.Invalid++; break;
                }
                accumulator.LastRejection = result.Detail ?? result.Reason.ToString();
                break;
        }
    }

    // Pushed in from TrackIngress's own per-source queue (21.9): the manager has no other way to know a
    // queue's current depth or that it dropped a report. droppedDelta > 0 also marks the system-wide drop
    // time GetSnapshot uses for R26's Degraded/Healthy health.
    internal void RecordQueueMetrics(string sourceId, int queueDepth, long droppedDelta)
    {
        lock (gate)
        {
            var accumulator = statistics.TryGetValue(sourceId, out var existing) ? existing : statistics[sourceId] = new();
            accumulator.QueueDepth = queueDepth;
            if (droppedDelta <= 0)
                return;
            accumulator.Dropped += droppedDelta;
            lastDropUtc = time.GetUtcNow();
        }
    }

    private ObservationResult SubmitLockedCore(TrackObservation observation, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(observation.SourceTargetId))
            return ObservationResult.Rejected(RejectionReason.MissingTargetId,
                "Observations need a source-local target ID so they can be associated.");
        if (TrackObservationRules.Validate(observation) is { } problem)
            return ObservationResult.Rejected(RejectionReason.Invalid, problem);

        var isRanged = observation.Position is not null;
        var key = (Source: observation.SourceId, Target: observation.SourceTargetId!);

        if (index.TryGetValue(key, out var existingId))
        {
            var entry = tracks[existingId];
            var contribution = entry.Contributions.First(c => c.SourceId == key.Source && c.SourceTargetId == key.Target);
            if (observation.Sequence == contribution.LastSequence && observation.MeasuredUtc == contribution.MeasuredUtc)
                return ObservationResult.Rejected(RejectionReason.Duplicate,
                    $"Observation {observation.Sequence} was already applied.");
            if (observation.MeasuredUtc <= contribution.MeasuredUtc)
                return ObservationResult.Rejected(RejectionReason.OutOfOrder,
                    $"Measured {observation.MeasuredUtc:O} is not newer than {contribution.MeasuredUtc:O}.");

            MarkSourceAlive(observation.SourceId, now);
            var track = ApplyObservation(entry, observation, isRanged, now, isNewContribution: false);
            return new ObservationResult(ObservationOutcome.Updated, track.Id);
        }

        var candidate = isRanged ? FindRangedCandidate(observation) : FindBearingCandidate(observation);
        if (candidate is not null)
        {
            MarkSourceAlive(observation.SourceId, now);
            var track = ApplyObservation(candidate, observation, isRanged, now, isNewContribution: true);
            return new ObservationResult(ObservationOutcome.Updated, track.Id);
        }

        if (tracks.Count >= policy.MaxTracks)
            return ObservationResult.Rejected(RejectionReason.CapacityExceeded,
                $"The track limit of {policy.MaxTracks} has been reached.");

        MarkSourceAlive(observation.SourceId, now);
        var created = CreateTrack(observation, isRanged, now);
        return new ObservationResult(ObservationOutcome.Created, created.Id);
    }

    private Track CreateTrack(TrackObservation observation, bool isRanged, DateTimeOffset now)
    {
        var entry = new TrackEntry { Id = $"T{nextTrackNumber++:D8}", FirstSeenUtc = now, Name = observation.Name };
        if (observation.HeadingDegrees is { } heading)
            entry.LastHeadingDegrees = heading;
        entry.Contributions.Add(NewContributionEntry(observation, isRanged, now));
        tracks[entry.Id] = entry;
        index[(observation.SourceId, observation.SourceTargetId!)] = entry.Id;
        ApplyMeasurementToEstimator(entry, observation, isRanged);
        return Publish(entry, now);
    }

    private Track ApplyObservation(
        TrackEntry entry, TrackObservation observation, bool isRanged, DateTimeOffset now, bool isNewContribution)
    {
        if (isNewContribution)
        {
            // R15: a new source track from a source whose earlier contribution is Lost replaces it, rather
            // than sitting alongside it.
            var lost = entry.Contributions.FirstOrDefault(c => c.SourceId == observation.SourceId && c.State == TrackState.Lost);
            if (lost is not null)
            {
                entry.Contributions.Remove(lost);
                index.Remove((lost.SourceId, lost.SourceTargetId));
            }
            entry.Contributions.Add(NewContributionEntry(observation, isRanged, now));
            index[(observation.SourceId, observation.SourceTargetId!)] = entry.Id;
        }
        else
        {
            var contribution = entry.Contributions.First(
                c => c.SourceId == observation.SourceId && c.SourceTargetId == observation.SourceTargetId);
            contribution.IsRanged = isRanged;
            contribution.State = TrackState.Active;
            contribution.MeasuredUtc = observation.MeasuredUtc;
            contribution.UpdatedUtc = now;
            contribution.LastSequence = observation.Sequence;
            contribution.UpdateCount++;
            PopulateLastReported(contribution, observation, isRanged);
        }

        entry.Name = observation.Name ?? entry.Name;
        if (observation.HeadingDegrees is { } heading)
            entry.LastHeadingDegrees = heading;

        ApplyMeasurementToEstimator(entry, observation, isRanged);
        CheckRangedSplit(entry, now);
        CheckBearingSplit(entry, now);
        return Publish(entry, now);
    }

    // Applies the observation's measurement to the shared estimate: a ranged report as a position (and
    // velocity, if present) update, or a bearing-only report as the bearing update (R34, R35), gated on whether
    // every Active ranged contribution is AIS. A Bearing track's first ranged report creates its estimator here
    // (R36); Publish's kind recomputation picks that up.
    private void ApplyMeasurementToEstimator(TrackEntry entry, TrackObservation observation, bool isRanged)
    {
        if (isRanged)
        {
            var positionUncertainty = EffectivePositionUncertainty(observation);
            if (entry.Estimator is null)
            {
                entry.Estimator = new TrackEstimator(observation.Position!, positionUncertainty, observation.MeasuredUtc,
                    policy.InitialVelocityUncertaintyMetresPerSecond, policy.ProcessNoiseAccelerationMetresPerSecondSquared);
                entry.StandaloneBearing = null;
            }
            else
                entry.Estimator.UpdatePosition(observation.MeasuredUtc, observation.Position!, positionUncertainty);

            if (observation.SpeedOverGroundMetresPerSecond is { } speed && observation.CourseOverGroundDegrees is { } course)
                entry.Estimator.UpdateVelocity(observation.MeasuredUtc, speed, course, EffectiveVelocityUncertainty(observation.SourceId));
        }
        else
        {
            var measurement = observation.Measurement!;
            var trueBearing = ResolveTrueBearing(measurement);
            var bearingUncertainty = EffectiveBearingUncertainty(observation);
            if (entry.Estimator is null)
                entry.StandaloneBearing = new TrackBearing(trueBearing, bearingUncertainty, measurement.Pose!.Position, observation.MeasuredUtc);
            else
            {
                var refine = HasOnlyAisActiveRangedContributions(entry);
                entry.Estimator.UpdateBearing(observation.MeasuredUtc, measurement.Pose!.Position, trueBearing, bearingUncertainty, refine);
            }
        }
    }

    // R35: a bearing refines the fused point only while every Active ranged contribution is AIS.
    private bool HasOnlyAisActiveRangedContributions(TrackEntry entry)
    {
        var activeRanged = entry.Contributions.Where(c => c.IsRanged && c.State == TrackState.Active).ToList();
        return activeRanged.Count > 0 && activeRanged.All(c => SourceKind(c.SourceId) == TrackSourceKinds.Ais);
    }

    private string? SourceKind(string sourceId) => sources.TryGetValue(sourceId, out var registration) ? registration.Descriptor?.Kind : null;

    private ContributionEntry NewContributionEntry(TrackObservation observation, bool isRanged, DateTimeOffset now)
    {
        var contribution = new ContributionEntry
        {
            SourceId = observation.SourceId,
            SourceTargetId = observation.SourceTargetId!,
            IsRanged = isRanged,
            State = TrackState.Active,
            JoinedUtc = now,
            MeasuredUtc = observation.MeasuredUtc,
            UpdatedUtc = now,
            LastSequence = observation.Sequence,
            UpdateCount = 1
        };
        PopulateLastReported(contribution, observation, isRanged);
        return contribution;
    }

    private void PopulateLastReported(ContributionEntry contribution, TrackObservation observation, bool isRanged)
    {
        if (isRanged)
        {
            contribution.LastReportedPosition = observation.Position;
            contribution.LastReportedSpeed = observation.SpeedOverGroundMetresPerSecond;
            contribution.LastReportedCourse = observation.CourseOverGroundDegrees;
            contribution.LastReportedPositionUncertaintyMetres = EffectivePositionUncertainty(observation);
        }
        else
        {
            var measurement = observation.Measurement!;
            contribution.LastReportedBearingDegrees = ResolveTrueBearing(measurement);
            contribution.LastReportedBearingOwnShip = measurement.Pose!.Position;
            contribution.LastReportedBearingUncertaintyDegrees = EffectiveBearingUncertainty(observation);
        }
    }

    // An accepted observation proves the source is delivering data again.
    private void MarkSourceAlive(string sourceId, DateTimeOffset now)
    {
        if (sources.TryGetValue(sourceId, out var registration))
        {
            if (registration.Status.Health == SourceHealth.Disconnected)
                SetSourceHealth(registration, SourceHealth.Healthy, null, now);
        }
        else
            AddSourceRegistration(sourceId, null, SourceHealth.Healthy, null, now);
    }

    public void ReportSourceHealth(string sourceId, SourceHealth health, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        lock (gate)
        {
            var now = time.GetUtcNow();
            if (!sources.TryGetValue(sourceId, out var registration))
                registration = AddSourceRegistration(sourceId, null, health, detail, now);
            else if (registration.Status.Health == health && registration.Status.Detail == detail)
                return;
            else
                SetSourceHealth(registration, health, detail, now);

            if (health != SourceHealth.Disconnected)
                return;
            foreach (var entry in tracks.Values
                         .Where(e => e.Contributions.Any(c => c.SourceId == sourceId && c.State == TrackState.Active))
                         .OrderBy(e => e.Id, StringComparer.Ordinal).ToList())
            {
                foreach (var contribution in entry.Contributions.Where(c => c.SourceId == sourceId && c.State == TrackState.Active))
                    contribution.State = TrackState.Stale;
                Publish(entry, now);
            }
        }
    }

    private SourceRegistration AddSourceRegistration(
        string sourceId, TrackSourceDescriptor? descriptor, SourceHealth health, string? detail, DateTimeOffset now)
    {
        var registration = new SourceRegistration(new SourceStatus(sourceId, health, detail, now), now) { Descriptor = descriptor };
        sources[sourceId] = registration;
        Append(new TrackChange(++revision, TrackChangeKind.Source, Source: registration.Status));
        return registration;
    }

    private void SetSourceHealth(SourceRegistration registration, SourceHealth health, string? detail, DateTimeOffset now)
    {
        if (registration.Status.Health == SourceHealth.Disconnected && health != SourceHealth.Disconnected)
            registration.ResumedUtc = now;
        registration.Status = registration.Status with { Health = health, Detail = detail, UpdatedUtc = now };
        Append(new TrackChange(++revision, TrackChangeKind.Source, Source: registration.Status));
    }

    #region Correlation (R2 to R6, R15, R33, R34)

    // R3 to R6, R15: a ranged candidate must have a position (so a Bearing track is never a candidate), must not
    // already carry an Active or Stale contribution from this source (a Lost one may be rejoined), must sit
    // within the absolute join cap, and must pass the position (and, when both sides have one, velocity) gate.
    private TrackEntry? FindRangedCandidate(TrackObservation observation)
    {
        var reportVariance = Math.Pow(EffectivePositionUncertainty(observation), 2);
        var scored = new List<(TrackEntry Entry, double DistanceSquared)>();
        foreach (var entry in tracks.Values)
        {
            if (entry.Estimator is null)
                continue;
            if (entry.Contributions.Any(c => c.SourceId == observation.SourceId && c.State != TrackState.Lost))
                continue;

            var estimate = entry.Estimator.Peek(observation.MeasuredUtc);
            if (estimate.Position is null)
                continue;
            var distanceMetres = GreatCircleDistanceMetres(estimate.Position, observation.Position!);
            if (distanceMetres > policy.MaximumJoinDistanceMetres)
                continue;

            var candidateVariance = estimate.PositionUncertaintyMetres * estimate.PositionUncertaintyMetres / 2;
            var distanceSquared = distanceMetres * distanceMetres / (candidateVariance + reportVariance);
            if (distanceSquared > policy.PositionGateChiSquare)
                continue;

            if (observation.SpeedOverGroundMetresPerSecond is { } speed && observation.CourseOverGroundDegrees is { } course &&
                estimate.SpeedOverGroundMetresPerSecond is { } candidateSpeed && estimate.CourseOverGroundDegrees is { } candidateCourse &&
                estimate.VelocityUncertaintyMetresPerSecond is { } candidateVelocityUncertainty)
            {
                var reportVelocityVariance = Math.Pow(EffectiveVelocityUncertainty(observation.SourceId), 2);
                var candidateVelocityVariance = candidateVelocityUncertainty * candidateVelocityUncertainty / 2;
                var reportEast = speed * Math.Sin(course * Math.PI / 180);
                var reportNorth = speed * Math.Cos(course * Math.PI / 180);
                var candidateEast = candidateSpeed * Math.Sin(candidateCourse * Math.PI / 180);
                var candidateNorth = candidateSpeed * Math.Cos(candidateCourse * Math.PI / 180);
                var velocityDistanceSquared =
                    (Math.Pow(reportEast - candidateEast, 2) + Math.Pow(reportNorth - candidateNorth, 2)) /
                    (candidateVelocityVariance + reportVelocityVariance);
                if (velocityDistanceSquared > policy.VelocityGateChiSquare)
                    continue;
            }

            scored.Add((entry, distanceSquared));
        }
        return PickDominant(scored);
    }

    // R33, R34: a bearing candidate must be a Point or Hybrid track (never another Bearing track), must not
    // already carry an Active or Stale bearing contribution from this source, must be within the source's
    // declared maximum association range (if any), and must pass the bearing gate.
    private TrackEntry? FindBearingCandidate(TrackObservation observation)
    {
        var measurement = observation.Measurement!;
        var trueBearingRadians = ResolveTrueBearing(measurement) * Math.PI / 180;
        var bearingUncertaintyRadians = EffectiveBearingUncertainty(observation) * Math.PI / 180;
        var ownShip = measurement.Pose!.Position;
        var maximumRange = EffectiveMaximumAssociationRange(observation.SourceId);

        var scored = new List<(TrackEntry Entry, double DistanceSquared)>();
        foreach (var entry in tracks.Values)
        {
            if (entry.Kind == TrackKind.Bearing || entry.Estimator is null)
                continue;
            if (entry.Contributions.Any(c => c.SourceId == observation.SourceId && c.State != TrackState.Lost))
                continue;

            var estimate = entry.Estimator.Peek(observation.MeasuredUtc);
            if (estimate.Position is null)
                continue;
            var rangeMetres = GreatCircleDistanceMetres(ownShip, estimate.Position);
            if (rangeMetres < 1 || (maximumRange is { } cap && rangeMetres > cap))
                continue;

            var predictedBearingRadians = TrueBearingRadians(ownShip, estimate.Position);
            var diff = WrapToPi(trueBearingRadians - predictedBearingRadians);
            var crossBearingVariance = estimate.PositionUncertaintyMetres * estimate.PositionUncertaintyMetres / 2;
            var angleVariance = bearingUncertaintyRadians * bearingUncertaintyRadians + crossBearingVariance / (rangeMetres * rangeMetres);
            var distanceSquared = diff * diff / angleVariance;
            if (distanceSquared > policy.BearingGateChiSquare)
                continue;

            scored.Add((entry, distanceSquared));
        }
        return PickDominant(scored);
    }

    // R5: joins the single gated candidate outright, or the dominant one if its normalized likelihood is at
    // least the dominance threshold; otherwise ambiguous (including no candidates at all).
    private TrackEntry? PickDominant(List<(TrackEntry Entry, double DistanceSquared)> scored)
    {
        if (scored.Count == 0)
            return null;
        if (scored.Count == 1)
            return scored[0].Entry;

        var likelihoods = scored.Select(s => Math.Exp(-0.5 * s.DistanceSquared)).ToArray();
        var total = likelihoods.Sum();
        if (total <= 0)
            return null;
        var bestIndex = 0;
        for (var i = 1; i < likelihoods.Length; i++)
            if (likelihoods[i] > likelihoods[bestIndex])
                bestIndex = i;
        return likelihoods[bestIndex] / total >= policy.DominanceThreshold ? scored[bestIndex].Entry : null;
    }

    #endregion

    #region Automatic split (R7, R35)

    // Compares every ranged contribution's own last-reported position (dead-reckoned to now) against the first
    // contributor's; one that disagrees by more than the join/split distance for the confirmation count of
    // consecutive updates splits off as a new track (R7).
    private void CheckRangedSplit(TrackEntry entry, DateTimeOffset now)
    {
        var ranged = entry.Contributions.Where(c => c.IsRanged && c.State is TrackState.Active or TrackState.Stale).ToList();
        if (ranged.Count < 2)
        {
            foreach (var contribution in ranged)
                contribution.DisagreementStreak = 0;
            return;
        }

        var first = ranged.OrderBy(c => c.JoinedUtc).First();
        var firstPosition = ExtrapolatedPosition(first, now);
        if (firstPosition is null)
            return;

        foreach (var other in ranged.Where(c => c != first))
        {
            var otherPosition = ExtrapolatedPosition(other, now);
            if (otherPosition is null)
            {
                other.DisagreementStreak = 0;
                continue;
            }
            if (GreatCircleDistanceMetres(firstPosition, otherPosition) > policy.MaximumJoinDistanceMetres)
            {
                other.DisagreementStreak++;
                if (other.DisagreementStreak >= policy.SplitConfirmations)
                {
                    SplitOffContribution(entry, other, now);
                    return;
                }
            }
            else
                other.DisagreementStreak = 0;
        }
    }

    // As above, but for a bearing contribution against the track's current fused position (R35).
    private void CheckBearingSplit(TrackEntry entry, DateTimeOffset now)
    {
        if (entry.Estimator is null)
            return;
        var estimate = entry.Estimator.Estimate;
        if (estimate.Position is null)
            return;

        foreach (var bearing in entry.Contributions
                     .Where(c => !c.IsRanged && c.State is TrackState.Active or TrackState.Stale).ToList())
        {
            if (bearing.LastReportedBearingOwnShip is not { } ownShip || bearing.LastReportedBearingDegrees is not { } measuredBearing)
                continue;
            var rangeMetres = GreatCircleDistanceMetres(ownShip, estimate.Position);
            if (rangeMetres < 1)
            {
                bearing.DisagreementStreak = 0;
                continue;
            }
            var predictedBearingRadians = TrueBearingRadians(ownShip, estimate.Position);
            var diff = WrapToPi(measuredBearing * Math.PI / 180 - predictedBearingRadians);
            var bearingUncertaintyRadians = (bearing.LastReportedBearingUncertaintyDegrees ?? DefaultBearingUncertaintyDegrees) * Math.PI / 180;
            var crossBearingVariance = estimate.PositionUncertaintyMetres * estimate.PositionUncertaintyMetres / 2;
            var angleVariance = bearingUncertaintyRadians * bearingUncertaintyRadians + crossBearingVariance / (rangeMetres * rangeMetres);
            var distanceSquared = diff * diff / angleVariance;
            if (distanceSquared > policy.BearingGateChiSquare)
            {
                bearing.DisagreementStreak++;
                if (bearing.DisagreementStreak >= policy.SplitConfirmations)
                {
                    SplitOffContribution(entry, bearing, now);
                    return;
                }
            }
            else
                bearing.DisagreementStreak = 0;
        }
    }

    // Removes the contribution from entry and gives it a brand new track, re-estimating both from their
    // remaining contributions' latest reports (R7, R18). Used by both the automatic and the operator split.
    private Track SplitOffContribution(TrackEntry entry, ContributionEntry contribution, DateTimeOffset now)
    {
        entry.Contributions.Remove(contribution);
        var newEntry = new TrackEntry { Id = $"T{nextTrackNumber++:D8}", FirstSeenUtc = now };
        newEntry.Contributions.Add(contribution);
        tracks[newEntry.Id] = newEntry;
        index[(contribution.SourceId, contribution.SourceTargetId)] = newEntry.Id;

        RebuildEstimator(entry);
        RebuildEstimator(newEntry);
        Publish(entry, now);
        return Publish(newEntry, now);
    }

    // Rebuilds an entry's estimator (and standalone bearing) from scratch, from only its current contributions'
    // own last-reported values, in their measured order. Used after a split or merge changes which contributions
    // belong together, per the plan's "re-estimated from their remaining contributions' latest reports".
    private void RebuildEstimator(TrackEntry entry)
    {
        entry.Estimator = null;
        entry.StandaloneBearing = null;
        var ranged = entry.Contributions.Where(c => c.IsRanged && c.LastReportedPosition is not null)
            .OrderBy(c => c.MeasuredUtc).ToList();
        if (ranged.Count > 0)
        {
            var seed = ranged[0];
            entry.Estimator = new TrackEstimator(
                seed.LastReportedPosition!, seed.LastReportedPositionUncertaintyMetres ?? policy.UnregisteredPositionUncertaintyMetres,
                seed.MeasuredUtc, policy.InitialVelocityUncertaintyMetresPerSecond, policy.ProcessNoiseAccelerationMetresPerSecondSquared);
            if (seed.LastReportedSpeed is { } seedSpeed && seed.LastReportedCourse is { } seedCourse)
                entry.Estimator.UpdateVelocity(seed.MeasuredUtc, seedSpeed, seedCourse, EffectiveVelocityUncertainty(seed.SourceId));
            foreach (var next in ranged.Skip(1))
            {
                entry.Estimator.UpdatePosition(next.MeasuredUtc, next.LastReportedPosition!,
                    next.LastReportedPositionUncertaintyMetres ?? policy.UnregisteredPositionUncertaintyMetres);
                if (next.LastReportedSpeed is { } speed && next.LastReportedCourse is { } course)
                    entry.Estimator.UpdateVelocity(next.MeasuredUtc, speed, course, EffectiveVelocityUncertainty(next.SourceId));
            }
        }

        foreach (var bearing in entry.Contributions
                     .Where(c => !c.IsRanged && c.LastReportedBearingDegrees is not null).OrderBy(c => c.MeasuredUtc))
        {
            var uncertainty = bearing.LastReportedBearingUncertaintyDegrees ?? DefaultBearingUncertaintyDegrees;
            if (entry.Estimator is { } estimator)
                estimator.UpdateBearing(bearing.MeasuredUtc, bearing.LastReportedBearingOwnShip!,
                    bearing.LastReportedBearingDegrees!.Value, uncertainty, HasOnlyAisActiveRangedContributions(entry));
            else
                entry.StandaloneBearing = new TrackBearing(
                    bearing.LastReportedBearingDegrees!.Value, uncertainty, bearing.LastReportedBearingOwnShip!, bearing.MeasuredUtc);
        }
    }

    private static GeodeticPosition? ExtrapolatedPosition(ContributionEntry contribution, DateTimeOffset now)
    {
        if (contribution.LastReportedPosition is not { } position)
            return null;
        if (contribution.LastReportedSpeed is not { } speed || contribution.LastReportedCourse is not { } course || speed == 0)
            return position;
        var seconds = (now - contribution.MeasuredUtc).TotalSeconds;
        return seconds <= 0 ? position : Geodesy.Project(position, course, speed * seconds);
    }

    #endregion

    #region Operator merge and split (R17, R18)

    public TrackOperationResult Merge(string trackId, string otherTrackId)
    {
        lock (gate)
        {
            if (!tracks.TryGetValue(trackId, out var a))
                return TrackOperationResult.Failure($"Track {trackId} is unknown.");
            if (!tracks.TryGetValue(otherTrackId, out var b))
                return TrackOperationResult.Failure($"Track {otherTrackId} is unknown.");
            if (ReferenceEquals(a, b))
                return TrackOperationResult.Failure("A track cannot be merged with itself.");

            var (survivor, absorbed) = TrackNumber(a.Id) < TrackNumber(b.Id) ? (a, b) : (b, a);
            var now = time.GetUtcNow();

            foreach (var contribution in absorbed.Contributions)
                index[(contribution.SourceId, contribution.SourceTargetId)] = survivor.Id;
            survivor.Contributions.AddRange(absorbed.Contributions);
            tracks.Remove(absorbed.Id);
            Append(new TrackChange(++revision, TrackChangeKind.Remove, TrackId: absorbed.Id));

            RebuildEstimator(survivor);
            var track = Publish(survivor, now);
            return TrackOperationResult.Success(track.Id);
        }
    }

    public TrackOperationResult Split(string trackId, string sourceId, string sourceTargetId)
    {
        lock (gate)
        {
            if (!tracks.TryGetValue(trackId, out var entry))
                return TrackOperationResult.Failure($"Track {trackId} is unknown.");
            var contribution = entry.Contributions.FirstOrDefault(c => c.SourceId == sourceId && c.SourceTargetId == sourceTargetId);
            if (contribution is null)
                return TrackOperationResult.Failure($"Track {trackId} has no contribution from {sourceId}/{sourceTargetId}.");
            if (entry.Contributions.Count == 1)
                return TrackOperationResult.Failure("Splitting the last contribution would leave the track empty.");

            var newTrack = SplitOffContribution(entry, contribution, time.GetUtcNow());
            return TrackOperationResult.Success(newTrack.Id);
        }
    }

    private static long TrackNumber(string trackId) => long.Parse(trackId[1..]);

    #endregion

    // Applies lifecycle ageing at the current time: per-contribution stale/lost/removal (R12 to R14), extrapolates
    // a Hybrid track's point so its uncertainty keeps growing while unrefreshed (R36), and removes a track once
    // its last contribution is gone (R13).
    public void Advance()
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            foreach (var entry in tracks.Values.OrderBy(e => e.Id, StringComparer.Ordinal).ToList())
            {
                var changed = false;
                foreach (var contribution in entry.Contributions.ToList())
                {
                    var registration = sources.GetValueOrDefault(contribution.SourceId);
                    if (registration is not null && registration.Status.Health == SourceHealth.Disconnected)
                    {
                        if (contribution.State == TrackState.Active)
                        {
                            contribution.State = TrackState.Stale;
                            changed = true;
                        }
                        continue;
                    }

                    var (staleAfter, lostAfter, removeAfter) = TimingsFor(contribution.SourceId);
                    var since = registration is not null && contribution.UpdatedUtc < registration.ResumedUtc
                        ? registration.ResumedUtc : contribution.UpdatedUtc;
                    var age = now - since;
                    if (age >= removeAfter)
                    {
                        entry.Contributions.Remove(contribution);
                        index.Remove((contribution.SourceId, contribution.SourceTargetId));
                        changed = true;
                        continue;
                    }
                    var state = age >= lostAfter ? TrackState.Lost
                        : age >= staleAfter || contribution.UpdatedUtc < since ? TrackState.Stale
                        : TrackState.Active;
                    if (state != contribution.State)
                    {
                        contribution.State = state;
                        changed = true;
                    }
                }

                if (entry.Contributions.Count == 0)
                {
                    tracks.Remove(entry.Id);
                    Append(new TrackChange(++revision, TrackChangeKind.Remove, TrackId: entry.Id));
                    continue;
                }

                var isHybridNow = entry.Estimator is not null &&
                    !entry.Contributions.Any(c => c.IsRanged && c.State == TrackState.Active) &&
                    entry.Contributions.Any(c => !c.IsRanged && c.State == TrackState.Active);
                if (isHybridNow)
                {
                    entry.Estimator!.Predict(now);
                    changed = true;
                }

                if (changed)
                    Publish(entry, now);
            }
        }
    }

    private (TimeSpan Stale, TimeSpan Lost, TimeSpan Remove) TimingsFor(string sourceId)
        => sources.TryGetValue(sourceId, out var registration) && registration.Descriptor is { } descriptor
            ? (descriptor.StaleAfter, descriptor.LostAfter, descriptor.RemoveAfter)
            : (policy.UnregisteredStaleAfter, policy.UnregisteredLostAfter, policy.UnregisteredRemoveAfter);

    private double EffectivePositionUncertainty(TrackObservation observation)
    {
        if (observation.PositionUncertaintyMetres is { } given)
            return given;
        return sources.TryGetValue(observation.SourceId, out var registration) && registration.Descriptor is { } descriptor
            ? descriptor.PositionUncertaintyMetres : policy.UnregisteredPositionUncertaintyMetres;
    }

    private double EffectiveVelocityUncertainty(string sourceId)
        => sources.TryGetValue(sourceId, out var registration) && registration.Descriptor is { } descriptor
            ? descriptor.VelocityUncertaintyMetresPerSecond : policy.UnregisteredVelocityUncertaintyMetresPerSecond;

    private double EffectiveBearingUncertainty(TrackObservation observation)
    {
        if (observation.Measurement?.BearingUncertaintyDegrees is { } given)
            return given;
        if (sources.TryGetValue(observation.SourceId, out var registration) && registration.Descriptor?.BearingUncertaintyDegrees is { } fromDescriptor)
            return fromDescriptor;
        return DefaultBearingUncertaintyDegrees;
    }

    private double? EffectiveMaximumAssociationRange(string sourceId)
        => sources.TryGetValue(sourceId, out var registration) ? registration.Descriptor?.MaximumAssociationRangeMetres : null;

    private Track Publish(TrackEntry entry, DateTimeOffset now)
    {
        UpdateKind(entry);
        entry.UpdatedUtc = now;
        entry.Revision = ++revision;
        var track = BuildPublicTrack(entry);
        Append(new TrackChange(entry.Revision, TrackChangeKind.Upsert, Track: track));
        return track;
    }

    // R36: no estimator at all means Bearing; an Active bearing without any Active ranged contribution means
    // Hybrid; otherwise Point (including while everything is Lost, about to be removed).
    private static void UpdateKind(TrackEntry entry)
    {
        if (entry.Estimator is null)
        {
            entry.Kind = TrackKind.Bearing;
            return;
        }
        var hasActiveRanged = entry.Contributions.Any(c => c.IsRanged && c.State == TrackState.Active);
        var hasActiveBearing = entry.Contributions.Any(c => !c.IsRanged && c.State == TrackState.Active);
        entry.Kind = !hasActiveRanged && hasActiveBearing ? TrackKind.Hybrid : TrackKind.Point;
    }

    private Track BuildPublicTrack(TrackEntry entry)
    {
        GeodeticPosition? position = null;
        double? positionUncertainty = null, speed = null, course = null;
        var bearing = entry.StandaloneBearing;
        if (entry.Estimator is { } estimator)
        {
            var estimate = estimator.Estimate;
            position = estimate.Position;
            positionUncertainty = estimate.PositionUncertaintyMetres;
            if (estimate.VelocityUncertaintyMetresPerSecond is { } velocityUncertainty &&
                velocityUncertainty <= policy.VelocityReportedWhenSigmaAtMost)
            {
                speed = estimate.SpeedOverGroundMetresPerSecond;
                course = estimate.CourseOverGroundDegrees;
            }
            if (estimate.BearingDegrees is { } bearingDegrees)
                bearing = new TrackBearing(bearingDegrees, estimate.BearingUncertaintyDegrees ?? DefaultBearingUncertaintyDegrees,
                    estimate.BearingOwnShipPosition!, estimate.BearingMeasuredUtc!.Value);
        }

        var state = ComputeTrackState(entry);
        var measuredUtc = entry.Contributions.Count > 0 ? entry.Contributions.Max(c => c.MeasuredUtc) : entry.FirstSeenUtc;
        var updateCount = entry.Contributions.Sum(c => c.UpdateCount);
        return new Track(
            entry.Id, entry.Name, entry.Kind, state, position, positionUncertainty, speed, course, entry.LastHeadingDegrees,
            bearing, entry.FirstSeenUtc, measuredUtc, entry.UpdatedUtc, updateCount, entry.Revision,
            [.. entry.Contributions.Select(ToPublicContribution)]);
    }

    private static TrackContribution ToPublicContribution(ContributionEntry contribution) => new(
        contribution.SourceId, contribution.SourceTargetId, contribution.IsRanged, contribution.State,
        contribution.JoinedUtc, contribution.MeasuredUtc, contribution.UpdatedUtc, contribution.LastSequence, contribution.UpdateCount);

    private static TrackState ComputeTrackState(TrackEntry entry)
    {
        if (entry.Contributions.Any(c => c.State == TrackState.Active))
            return TrackState.Active;
        return entry.Contributions.Any(c => c.State == TrackState.Stale) ? TrackState.Stale : TrackState.Lost;
    }

    private void Append(TrackChange change)
    {
        log.Add(change);
        while (log.Count - logHead > policy.MaxChangeLog)
            logHead++;
        if (logHead > 1024 && logHead > log.Count / 2)
        {
            log.RemoveRange(0, logHead);
            logHead = 0;
        }
    }

    public TrackSnapshot GetSnapshot()
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            return new TrackSnapshot(epoch, revision,
                [.. tracks.Values.OrderBy(e => e.Id, StringComparer.Ordinal).Select(BuildPublicTrack)],
                [.. sources.Values.Select(s => s.Status with { Statistics = StatisticsFor(s.Status.SourceId) })
                    .OrderBy(s => s.SourceId, StringComparer.Ordinal)],
                System: new TrackSystemStatus(SystemHealth(now), SystemDetail(now), now));
        }
    }

    private SourceStatistics? StatisticsFor(string sourceId)
        => statistics.TryGetValue(sourceId, out var a)
            ? new SourceStatistics(a.Received, a.Created, a.Updated, a.Invalid, a.Duplicate, a.OutOfOrder,
                a.CapacityExceeded, a.Dropped, a.QueueDepth, a.LastRejection)
            : null;

    // R26: the system is Degraded while any source's queue has dropped a report in the last 10 seconds.
    private TrackSystemHealth SystemHealth(DateTimeOffset now)
        => lastDropUtc is { } dropped && now - dropped < TimeSpan.FromSeconds(10)
            ? TrackSystemHealth.Degraded : TrackSystemHealth.Healthy;

    private string? SystemDetail(DateTimeOffset now)
        => SystemHealth(now) == TrackSystemHealth.Degraded
            ? "One or more source queues are dropping reports; Tracks cannot keep up with the current load."
            : null;

    public TrackChanges GetChanges(string? epoch, long afterRevision, int maxChanges = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChanges);
        lock (gate)
        {
            if (!string.Equals(epoch, this.epoch, StringComparison.Ordinal) || afterRevision < 0 || afterRevision > revision)
                return new TrackChanges(this.epoch, revision, [], true);
            if (afterRevision == revision)
                return new TrackChanges(this.epoch, revision, [], false);

            var oldest = log.Count > logHead ? log[logHead].Revision : revision + 1;
            if (afterRevision + 1 < oldest)
                return new TrackChanges(this.epoch, revision, [], true);

            var start = logHead + (int)(afterRevision + 1 - oldest);
            var changes = log.GetRange(start, Math.Min(maxChanges, log.Count - start));
            return new TrackChanges(this.epoch, changes[^1].Revision, changes, false);
        }
    }

    #region Checkpoint (R21, schema 2)

    // P is flattened in TrackEstimatorState itself; everything else here is a direct mirror of the working
    // state, so restore can rebuild the index and re-correlate exactly as before a reload.
    private sealed record ContributionCheckpoint(
        string SourceId, string SourceTargetId, bool IsRanged, TrackState State, DateTimeOffset JoinedUtc,
        DateTimeOffset MeasuredUtc, DateTimeOffset UpdatedUtc, long LastSequence, int UpdateCount, int DisagreementStreak,
        GeodeticPosition? LastReportedPosition, double? LastReportedSpeed, double? LastReportedCourse,
        double? LastReportedPositionUncertaintyMetres, double? LastReportedBearingDegrees,
        GeodeticPosition? LastReportedBearingOwnShip, double? LastReportedBearingUncertaintyDegrees);

    private sealed record TrackCheckpoint(
        string Id, string? Name, TrackKind Kind, DateTimeOffset FirstSeenUtc, DateTimeOffset UpdatedUtc, long Revision,
        double? LastHeadingDegrees, TrackEstimatorState? Estimator, TrackBearing? StandaloneBearing,
        ContributionCheckpoint[] Contributions);

    private sealed record SourceCheckpoint(string SourceId, TrackSourceDescriptor? Descriptor, SourceStatus Status, DateTimeOffset ResumedUtc);

    private sealed record Checkpoint(string Epoch, long Revision, long NextTrackNumber, TrackCheckpoint[] Tracks, SourceCheckpoint[] Sources);

    public ModuleState? Capture()
    {
        lock (gate)
        {
            if (revision == 0 && tracks.Count == 0 && sources.Count == 0)
                return null;
            var checkpoint = new Checkpoint(epoch, revision, nextTrackNumber,
                [.. tracks.Values.OrderBy(e => e.Id, StringComparer.Ordinal).Select(ToTrackCheckpoint)],
                [.. sources.OrderBy(s => s.Key, StringComparer.Ordinal)
                    .Select(s => new SourceCheckpoint(s.Key, s.Value.Descriptor, s.Value.Status, s.Value.ResumedUtc))]);
            return new ModuleState(CheckpointSchema, JsonSerializer.SerializeToUtf8Bytes(checkpoint, CheckpointOptions));
        }
    }

    private static TrackCheckpoint ToTrackCheckpoint(TrackEntry entry) => new(
        entry.Id, entry.Name, entry.Kind, entry.FirstSeenUtc, entry.UpdatedUtc, entry.Revision, entry.LastHeadingDegrees,
        entry.Estimator?.Export(), entry.StandaloneBearing,
        [.. entry.Contributions.Select(c => new ContributionCheckpoint(
            c.SourceId, c.SourceTargetId, c.IsRanged, c.State, c.JoinedUtc, c.MeasuredUtc, c.UpdatedUtc, c.LastSequence,
            c.UpdateCount, c.DisagreementStreak, c.LastReportedPosition, c.LastReportedSpeed, c.LastReportedCourse,
            c.LastReportedPositionUncertaintyMetres, c.LastReportedBearingDegrees, c.LastReportedBearingOwnShip,
            c.LastReportedBearingUncertaintyDegrees))]);

    public void Restore(ModuleState state)
    {
        if (state.SchemaVersion != CheckpointSchema)
            throw new InvalidDataException($"Unsupported track checkpoint schema {state.SchemaVersion}.");
        Checkpoint checkpoint;
        try
        {
            checkpoint = JsonSerializer.Deserialize<Checkpoint>(state.Data, CheckpointOptions)
                ?? throw new InvalidDataException("The track checkpoint is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The track checkpoint is corrupt.", ex);
        }
        if (string.IsNullOrEmpty(checkpoint.Epoch) || checkpoint.Tracks is null || checkpoint.Sources is null ||
            checkpoint.Tracks.Any(t => t is null || string.IsNullOrEmpty(t.Id) || t.Contributions is null) ||
            checkpoint.Sources.Any(s => s is null || string.IsNullOrWhiteSpace(s.SourceId)))
            throw new InvalidDataException("The track checkpoint is incomplete.");

        var restoredTracks = new Dictionary<string, TrackEntry>(StringComparer.Ordinal);
        var restoredIndex = new Dictionary<(string Source, string Target), string>();
        var restoredSources = new Dictionary<string, SourceRegistration>(StringComparer.Ordinal);

        foreach (var source in checkpoint.Sources)
            if (!restoredSources.TryAdd(source.SourceId, new SourceRegistration(source.Status, source.ResumedUtc) { Descriptor = source.Descriptor }))
                throw new InvalidDataException($"Duplicate source {source.SourceId} in the track checkpoint.");

        foreach (var trackCheckpoint in checkpoint.Tracks)
        {
            var entry = new TrackEntry
            {
                Id = trackCheckpoint.Id, Name = trackCheckpoint.Name, Kind = trackCheckpoint.Kind,
                FirstSeenUtc = trackCheckpoint.FirstSeenUtc, UpdatedUtc = trackCheckpoint.UpdatedUtc, Revision = trackCheckpoint.Revision,
                LastHeadingDegrees = trackCheckpoint.LastHeadingDegrees,
                Estimator = trackCheckpoint.Estimator is { } estimatorState ? TrackEstimator.Restore(estimatorState) : null,
                StandaloneBearing = trackCheckpoint.StandaloneBearing
            };
            foreach (var contributionCheckpoint in trackCheckpoint.Contributions)
            {
                entry.Contributions.Add(new ContributionEntry
                {
                    SourceId = contributionCheckpoint.SourceId,
                    SourceTargetId = contributionCheckpoint.SourceTargetId,
                    IsRanged = contributionCheckpoint.IsRanged,
                    State = contributionCheckpoint.State,
                    JoinedUtc = contributionCheckpoint.JoinedUtc,
                    MeasuredUtc = contributionCheckpoint.MeasuredUtc,
                    UpdatedUtc = contributionCheckpoint.UpdatedUtc,
                    LastSequence = contributionCheckpoint.LastSequence,
                    UpdateCount = contributionCheckpoint.UpdateCount,
                    DisagreementStreak = contributionCheckpoint.DisagreementStreak,
                    LastReportedPosition = contributionCheckpoint.LastReportedPosition,
                    LastReportedSpeed = contributionCheckpoint.LastReportedSpeed,
                    LastReportedCourse = contributionCheckpoint.LastReportedCourse,
                    LastReportedPositionUncertaintyMetres = contributionCheckpoint.LastReportedPositionUncertaintyMetres,
                    LastReportedBearingDegrees = contributionCheckpoint.LastReportedBearingDegrees,
                    LastReportedBearingOwnShip = contributionCheckpoint.LastReportedBearingOwnShip,
                    LastReportedBearingUncertaintyDegrees = contributionCheckpoint.LastReportedBearingUncertaintyDegrees
                });
                if (!restoredIndex.TryAdd((contributionCheckpoint.SourceId, contributionCheckpoint.SourceTargetId), trackCheckpoint.Id))
                    throw new InvalidDataException($"Inconsistent contribution in the track checkpoint for track {trackCheckpoint.Id}.");
            }
            if (!restoredTracks.TryAdd(trackCheckpoint.Id, entry))
                throw new InvalidDataException($"Duplicate track {trackCheckpoint.Id} in the track checkpoint.");
        }

        lock (gate)
        {
            tracks.Clear();
            foreach (var (id, entry) in restoredTracks)
                tracks[id] = entry;
            index.Clear();
            foreach (var (key, id) in restoredIndex)
                index[key] = id;
            sources.Clear();
            foreach (var (id, registration) in restoredSources)
                sources[id] = registration;
            log.Clear();
            logHead = 0;
            epoch = checkpoint.Epoch;
            revision = checkpoint.Revision;
            nextTrackNumber = checkpoint.NextTrackNumber;
        }
    }

    #endregion

    private static double GreatCircleDistanceMetres(GeodeticPosition a, GeodeticPosition b)
    {
        var lat1 = a.LatitudeDegrees * Math.PI / 180;
        var lat2 = b.LatitudeDegrees * Math.PI / 180;
        var dLat = (b.LatitudeDegrees - a.LatitudeDegrees) * Math.PI / 180;
        var dLon = (b.LongitudeDegrees - a.LongitudeDegrees) * Math.PI / 180;
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * Geodesy.MeanEarthRadiusMetres * Math.Asin(Math.Sqrt(Math.Clamp(h, 0, 1)));
    }

    private static double TrueBearingRadians(GeodeticPosition from, GeodeticPosition to)
    {
        var lat1 = from.LatitudeDegrees * Math.PI / 180;
        var lat2 = to.LatitudeDegrees * Math.PI / 180;
        var dLon = (to.LongitudeDegrees - from.LongitudeDegrees) * Math.PI / 180;
        return Math.Atan2(Math.Sin(dLon) * Math.Cos(lat2), Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon));
    }

    private static double WrapToPi(double radians)
    {
        while (radians > Math.PI)
            radians -= 2 * Math.PI;
        while (radians < -Math.PI)
            radians += 2 * Math.PI;
        return radians;
    }

    private static double ResolveTrueBearing(PolarMeasurement measurement)
        => measurement.BearingReference == BearingReference.Relative
            ? ((measurement.BearingDegrees!.Value + measurement.Pose!.HeadingTrueDegrees!.Value) % 360 + 360) % 360
            : measurement.BearingDegrees!.Value;
}
