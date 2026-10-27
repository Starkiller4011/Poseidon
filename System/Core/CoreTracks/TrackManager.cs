using Poseidon.Core.Modules;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Poseidon.Modules.Tracks;

internal sealed record TrackPolicy(
    TimeSpan StaleAfter, TimeSpan LostAfter, TimeSpan RemoveAfter, int MaxTracks = 10_000, int MaxChangeLog = 10_000)
{
    public static TrackPolicy Default { get; } =
        new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(600));
}

// The authoritative track table. Every mutation takes one lock and produces at most one revision per
// change, so the change log is gapless and a consumer cursor is simply the last revision it applied.
internal sealed class TrackManager : ITrackIngestion, ITrackFeed
{
    private const int CheckpointSchema = 1;
    private static readonly JsonSerializerOptions CheckpointOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed class SourceState(SourceStatus status, DateTimeOffset resumedUtc)
    {
        public SourceStatus Status { get; set; } = status;
        // Tracks not heard from since this moment are held at Stale and age from here.
        public DateTimeOffset ResumedUtc { get; set; } = resumedUtc;
    }

    private sealed record SourceCheckpoint(SourceStatus Status, DateTimeOffset ResumedUtc);

    private sealed record Checkpoint(
        string Epoch, long Revision, long NextTrackNumber, Track[] Tracks, SourceCheckpoint[] Sources);

    private readonly object gate = new();
    private readonly TimeProvider time;
    private readonly TrackPolicy policy;
    private readonly Dictionary<string, Track> tracks = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Source, string Target), string> index = [];
    private readonly Dictionary<string, SourceState> sources = new(StringComparer.Ordinal);
    private readonly List<TrackChange> log = [];
    private int logHead;
    private string epoch = Guid.NewGuid().ToString("N");
    private long revision;
    private long nextTrackNumber = 1;

    public TrackManager(TimeProvider time, TrackPolicy? policy = null)
    {
        this.time = time;
        this.policy = policy ?? TrackPolicy.Default;
        if (this.policy.StaleAfter <= TimeSpan.Zero || this.policy.LostAfter <= this.policy.StaleAfter ||
            this.policy.RemoveAfter <= this.policy.LostAfter || this.policy.MaxTracks <= 0 || this.policy.MaxChangeLog <= 0)
            throw new ArgumentException("Stale, lost and removal intervals must increase and the limits must be positive.",
                nameof(policy));
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
        if (string.IsNullOrWhiteSpace(observation.SourceTargetId))
            return ObservationResult.Rejected(RejectionReason.MissingTargetId,
                "Observations need a source-local target ID so they can be associated.");
        if (TrackObservationRules.Validate(observation) is { } problem)
            return ObservationResult.Rejected(RejectionReason.Invalid, problem);

        var key = (observation.SourceId, observation.SourceTargetId);
        Track? previous = null;
        if (index.TryGetValue(key, out var existingId))
        {
            previous = tracks[existingId];
            if (observation.Sequence == previous.LastSequence && observation.MeasuredUtc == previous.MeasuredUtc)
                return ObservationResult.Rejected(RejectionReason.Duplicate,
                    $"Observation {observation.Sequence} was already applied.");
            if (observation.MeasuredUtc <= previous.MeasuredUtc)
                return ObservationResult.Rejected(RejectionReason.OutOfOrder,
                    $"Measured {observation.MeasuredUtc:O} is not newer than {previous.MeasuredUtc:O}.");
        }
        else if (tracks.Count >= policy.MaxTracks)
            return ObservationResult.Rejected(RejectionReason.CapacityExceeded,
                $"The track limit of {policy.MaxTracks} has been reached.");

        MarkSourceAlive(observation.SourceId, now);
        var id = previous?.Id ?? $"T{nextTrackNumber++:D8}";
        var track = new Track(
            id, observation.SourceId, observation.SourceTargetId, observation.Name ?? previous?.Name,
            TrackState.Active, observation.Position, observation.PositionUncertaintyMetres,
            observation.SpeedOverGroundMetresPerSecond, observation.CourseOverGroundDegrees, observation.HeadingDegrees,
            observation.Measurement, previous?.FirstSeenUtc ?? now, observation.MeasuredUtc, now,
            observation.Sequence, (previous?.UpdateCount ?? 0) + 1, ++revision);
        tracks[id] = track;
        index[key] = id;
        Append(new TrackChange(track.Revision, TrackChangeKind.Upsert, Track: track));
        return new ObservationResult(previous is null ? ObservationOutcome.Created : ObservationOutcome.Updated, id);
    }

    // An accepted observation proves the source is delivering data again.
    private void MarkSourceAlive(string sourceId, DateTimeOffset now)
    {
        if (sources.TryGetValue(sourceId, out var state))
        {
            if (state.Status.Health == SourceHealth.Disconnected)
                SetSource(state, SourceHealth.Healthy, null, now);
        }
        else
            AddSource(sourceId, SourceHealth.Healthy, null, now);
    }

    public void ReportSourceHealth(string sourceId, SourceHealth health, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        lock (gate)
        {
            var now = time.GetUtcNow();
            if (!sources.TryGetValue(sourceId, out var state))
                AddSource(sourceId, health, detail, now);
            else if (state.Status.Health == health && state.Status.Detail == detail)
                return;
            else
                SetSource(state, health, detail, now);

            if (health != SourceHealth.Disconnected)
                return;
            foreach (var track in tracks.Values.Where(t => t.SourceId == sourceId && t.State == TrackState.Active)
                         .OrderBy(t => t.Id, StringComparer.Ordinal).ToList())
                Replace(track with { State = TrackState.Stale });
        }
    }

    private void AddSource(string sourceId, SourceHealth health, string? detail, DateTimeOffset now)
    {
        var state = new SourceState(new SourceStatus(sourceId, health, detail, now), DateTimeOffset.MinValue);
        sources[sourceId] = state;
        Append(new TrackChange(++revision, TrackChangeKind.Source, Source: state.Status));
    }

    private void SetSource(SourceState state, SourceHealth health, string? detail, DateTimeOffset now)
    {
        if (state.Status.Health == SourceHealth.Disconnected && health != SourceHealth.Disconnected)
            state.ResumedUtc = now;
        state.Status = state.Status with { Health = health, Detail = detail, UpdatedUtc = now };
        Append(new TrackChange(++revision, TrackChangeKind.Source, Source: state.Status));
    }

    // Applies lifecycle ageing at the current time.
    public void Advance()
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            foreach (var track in tracks.Values.OrderBy(t => t.Id, StringComparer.Ordinal).ToList())
            {
                var source = sources[track.SourceId];
                if (source.Status.Health == SourceHealth.Disconnected)
                {
                    // A lost source says nothing about whether its targets still exist.
                    if (track.State == TrackState.Active)
                        Replace(track with { State = TrackState.Stale });
                    continue;
                }
                var age = now - (track.UpdatedUtc > source.ResumedUtc ? track.UpdatedUtc : source.ResumedUtc);
                if (age >= policy.RemoveAfter)
                {
                    tracks.Remove(track.Id);
                    index.Remove((track.SourceId, track.SourceTargetId));
                    Append(new TrackChange(++revision, TrackChangeKind.Remove, TrackId: track.Id));
                    continue;
                }
                var state = age >= policy.LostAfter ? TrackState.Lost
                    : age >= policy.StaleAfter || track.UpdatedUtc < source.ResumedUtc ? TrackState.Stale
                    : TrackState.Active;
                if (state != track.State)
                    Replace(track with { State = state });
            }
        }
    }

    private void Replace(Track track)
    {
        track = track with { Revision = ++revision };
        tracks[track.Id] = track;
        Append(new TrackChange(track.Revision, TrackChangeKind.Upsert, Track: track));
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
            return new TrackSnapshot(epoch, revision,
                [.. tracks.Values.OrderBy(t => t.Id, StringComparer.Ordinal)],
                [.. sources.Values.Select(s => s.Status).OrderBy(s => s.SourceId, StringComparer.Ordinal)]);
    }

    public TrackChanges GetChanges(string? epoch, long afterRevision, int maxChanges = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChanges);
        lock (gate)
        {
            if (!string.Equals(epoch, this.epoch, StringComparison.Ordinal) || afterRevision < 0 ||
                afterRevision > revision)
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

    // An empty manager has nothing worth carrying across a reload.
    public ModuleState? Capture()
    {
        lock (gate)
        {
            if (revision == 0 && tracks.Count == 0 && sources.Count == 0)
                return null;
            var checkpoint = new Checkpoint(epoch, revision, nextTrackNumber,
                [.. tracks.Values.OrderBy(t => t.Id, StringComparer.Ordinal)],
                [.. sources.Values.OrderBy(s => s.Status.SourceId, StringComparer.Ordinal)
                    .Select(s => new SourceCheckpoint(s.Status, s.ResumedUtc))]);
            return new ModuleState(CheckpointSchema, JsonSerializer.SerializeToUtf8Bytes(checkpoint, CheckpointOptions));
        }
    }

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
            checkpoint.Tracks.Any(t => t is null || string.IsNullOrEmpty(t.Id) || t.SourceId is null || t.SourceTargetId is null) ||
            checkpoint.Sources.Any(s => s?.Status is null || string.IsNullOrWhiteSpace(s.Status.SourceId)))
            throw new InvalidDataException("The track checkpoint is incomplete.");

        var restoredTracks = new Dictionary<string, Track>(StringComparer.Ordinal);
        var restoredIndex = new Dictionary<(string Source, string Target), string>();
        var restoredSources = new Dictionary<string, SourceState>(StringComparer.Ordinal);
        foreach (var source in checkpoint.Sources)
            if (!restoredSources.TryAdd(source.Status.SourceId, new SourceState(source.Status, source.ResumedUtc)))
                throw new InvalidDataException($"Duplicate source {source.Status.SourceId} in the track checkpoint.");
        foreach (var track in checkpoint.Tracks)
        {
            if (!restoredTracks.TryAdd(track.Id, track) ||
                !restoredIndex.TryAdd((track.SourceId, track.SourceTargetId), track.Id) ||
                !restoredSources.ContainsKey(track.SourceId))
                throw new InvalidDataException($"Inconsistent track {track.Id} in the track checkpoint.");
        }

        lock (gate)
        {
            tracks.Clear();
            index.Clear();
            sources.Clear();
            foreach (var (id, track) in restoredTracks)
                tracks[id] = track;
            foreach (var (key, id) in restoredIndex)
                index[key] = id;
            foreach (var (id, source) in restoredSources)
                sources[id] = source;
            log.Clear();
            logHead = 0;
            epoch = checkpoint.Epoch;
            revision = checkpoint.Revision;
            nextTrackNumber = checkpoint.NextTrackNumber;
        }
    }
}
