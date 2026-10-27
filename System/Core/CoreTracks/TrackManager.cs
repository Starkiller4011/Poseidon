using Poseidon.Core.Modules;

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

// The authoritative track table: correlation, per-contribution lifecycle, the Kalman-filter estimate (via
// TrackEstimator), automatic split, operator merge/split, and checkpoint/restore. Every mutation takes one lock
// and produces at most one revision per change, so the change log is gapless and a consumer cursor is simply the
// last revision it applied. Rewritten for fusion (m1-e-track-fusion.md section 21); behavior is added in Step 3.
internal sealed class TrackManager : ITrackIngestion, ITrackFeed, ITrackOperations
{
    private readonly object gate = new();
    private readonly TimeProvider time;
    private readonly TrackPolicy policy;

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

    // R11: a source registers its descriptor (kind, expected interval, stale/lost/remove timings, default
    // uncertainties) before its reports are associated with anything but the unregistered defaults. Registering
    // the same source ID again replaces its descriptor and the new timings apply immediately.
    public void RegisterSource(TrackSourceDescriptor descriptor) => throw new NotImplementedException();

    public ObservationResult Submit(TrackObservation observation) => throw new NotImplementedException();

    public IReadOnlyList<ObservationResult> SubmitBatch(IReadOnlyList<TrackObservation> observations)
        => throw new NotImplementedException();

    public void ReportSourceHealth(string sourceId, SourceHealth health, string? detail = null)
        => throw new NotImplementedException();

    public TrackOperationResult Merge(string trackId, string otherTrackId) => throw new NotImplementedException();

    public TrackOperationResult Split(string trackId, string sourceId, string sourceTargetId)
        => throw new NotImplementedException();

    // Applies lifecycle ageing at the current time. Invoked by TracksModule's own background timer, so it never
    // throws: an exception here would crash that timer rather than fail a single test. It is a genuine no-op
    // until Step 3, consistent with nothing having been submitted (Submit also throws until then).
    public void Advance()
    {
    }

    public TrackSnapshot GetSnapshot()
    {
        lock (gate)
            return new TrackSnapshot(StubEpoch, 0, [], []);
    }

    public TrackChanges GetChanges(string? epoch, long afterRevision, int maxChanges = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChanges);
        lock (gate)
            return new TrackChanges(StubEpoch, 0, [], true);
    }

    public ModuleState? Capture() => throw new NotImplementedException();

    public void Restore(ModuleState state) => throw new NotImplementedException();

    private const string StubEpoch = "stub";
}
