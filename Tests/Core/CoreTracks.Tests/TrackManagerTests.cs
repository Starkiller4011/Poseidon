using Poseidon.Core.Modules;
using Poseidon.Modules.Tracks;
using Xunit;
using static CoreTracks.Tests.TrackTestData;

namespace CoreTracks.Tests;

public sealed class TrackManagerTests
{
    private static readonly TrackPolicy ShortPolicy = new(
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));

    private static (TrackManager Manager, ManualTime Time) Create(TrackPolicy? policy = null)
    {
        var time = new ManualTime(Start);
        return (new TrackManager(time, policy ?? ShortPolicy), time);
    }

    private static Track Only(TrackManager manager) => Assert.Single(manager.GetSnapshot().Tracks);

    [Fact]
    public void InvalidObservations_AreRejectedWithoutCreatingTracks()
    {
        var (manager, _) = Create();
        var missingTarget = manager.Submit(Observation(target: null));
        Assert.Equal((ObservationOutcome.Rejected, RejectionReason.MissingTargetId),
            (missingTarget.Outcome, missingTarget.Reason));
        var invalid = manager.Submit(Observation(latitude: 123));
        Assert.Equal((ObservationOutcome.Rejected, RejectionReason.Invalid), (invalid.Outcome, invalid.Reason));
        Assert.NotNull(invalid.Detail);
        Assert.Empty(manager.GetSnapshot().Tracks);
        Assert.Equal(0, manager.GetSnapshot().Revision);
    }

    [Fact]
    public void FirstObservation_CreatesTrackWithGeneratedId()
    {
        var (manager, _) = Create();
        var result = manager.Submit(Observation(name: "MV Example"));
        Assert.Equal(ObservationOutcome.Created, result.Outcome);
        var track = Only(manager);
        Assert.Equal(track.Id, result.TrackId);
        Assert.NotEqual("100", track.Id);
        Assert.Equal(("Core.AIS", "100", "MV Example"), (track.SourceId, track.SourceTargetId, track.Name));
        Assert.Equal(TrackState.Active, track.State);
        Assert.Equal(new GeodeticPosition(25.0, 55.0), track.Position);
        Assert.Equal((1, 1L), (track.UpdateCount, track.LastSequence));
        Assert.Equal(Start, track.FirstSeenUtc);
    }

    [Fact]
    public void RepeatedObservation_UpdatesTheSameTrackAndKeepsTheName()
    {
        var (manager, _) = Create();
        var created = manager.Submit(Observation(name: "MV Example"));
        var updated = manager.Submit(Observation(sequence: 2, seconds: 5, latitude: 25.1));
        Assert.Equal((ObservationOutcome.Updated, created.TrackId), (updated.Outcome, updated.TrackId));
        var track = Only(manager);
        Assert.Equal((2, 2L, 25.1), (track.UpdateCount, track.LastSequence, track.Position!.LatitudeDegrees));
        Assert.Equal("MV Example", track.Name);
        Assert.Equal(Start, track.FirstSeenUtc);
        Assert.Equal(Start.AddSeconds(5), track.MeasuredUtc);
    }

    [Fact]
    public void SameTargetIdFromDifferentSources_OrNearbyPositions_StaySeparateTracks()
    {
        var (manager, _) = Create();
        var ais = manager.Submit(Observation(source: "Core.AIS", target: "7"));
        var radar = manager.Submit(Observation(source: "Core.Radar", target: "7"));
        var other = manager.Submit(Observation(source: "Core.Radar", target: "8"));
        Assert.Equal(3, new[] { ais.TrackId, radar.TrackId, other.TrackId }.Distinct().Count());
        Assert.Equal(3, manager.GetSnapshot().Tracks.Count);
    }

    [Fact]
    public void ExactRepeatIsDuplicate_AndOlderObservationIsOutOfOrder_WithoutChangingTheTrack()
    {
        var (manager, _) = Create();
        manager.Submit(Observation(sequence: 5, seconds: 10));
        var revision = manager.GetSnapshot().Revision;

        var duplicate = manager.Submit(Observation(sequence: 5, seconds: 10));
        var older = manager.Submit(Observation(sequence: 6, seconds: 4));
        var sameTime = manager.Submit(Observation(sequence: 7, seconds: 10));

        Assert.Equal(RejectionReason.Duplicate, duplicate.Reason);
        Assert.Equal(RejectionReason.OutOfOrder, older.Reason);
        Assert.Equal(RejectionReason.OutOfOrder, sameTime.Reason);
        Assert.Equal(revision, manager.GetSnapshot().Revision);
        Assert.Equal((1, 5L), (Only(manager).UpdateCount, Only(manager).LastSequence));
    }

    [Fact]
    public void SequenceReset_WithNewerMeasuredTime_IsAccepted()
    {
        var (manager, _) = Create();
        manager.Submit(Observation(sequence: 57, seconds: 10));
        var result = manager.Submit(Observation(sequence: 1, seconds: 20));
        Assert.Equal(ObservationOutcome.Updated, result.Outcome);
    }

    [Fact]
    public void BearingOnlyObservation_CreatesPositionlessTrackRetainingTheMeasurement()
    {
        var (manager, _) = Create();
        var measurement = new PolarMeasurement(null, 42.5, BearingReference.True, new SensorPose(new GeodeticPosition(25, 55), null));
        var result = manager.Submit(Observation(source: "Core.Radar", target: "3") with
        {
            Position = null,
            SpeedOverGroundMetresPerSecond = null,
            CourseOverGroundDegrees = null,
            HeadingDegrees = null,
            Measurement = measurement
        });
        Assert.Equal(ObservationOutcome.Created, result.Outcome);
        var track = Only(manager);
        Assert.Null(track.Position);
        Assert.Equal(measurement, track.LatestMeasurement);
    }

    [Fact]
    public void Tracks_AgeToStaleAndLostThenAreRemoved_AndUpdatesReviveThem()
    {
        var (manager, time) = Create();
        manager.Submit(Observation());
        time.Advance(TimeSpan.FromSeconds(9));
        manager.Advance();
        Assert.Equal(TrackState.Active, Only(manager).State);
        time.Advance(TimeSpan.FromSeconds(1));
        manager.Advance();
        Assert.Equal(TrackState.Stale, Only(manager).State);
        time.Advance(TimeSpan.FromSeconds(20));
        manager.Advance();
        Assert.Equal(TrackState.Lost, Only(manager).State);

        manager.Submit(Observation(sequence: 2, seconds: 30));
        Assert.Equal(TrackState.Active, Only(manager).State);

        time.Advance(TimeSpan.FromSeconds(60));
        manager.Advance();
        Assert.Empty(manager.GetSnapshot().Tracks);
    }

    [Fact]
    public void RemovalIsAFeedChange_AndReusedTargetIdAfterRemovalGetsANewTrackId()
    {
        var (manager, time) = Create();
        var first = manager.Submit(Observation());
        time.Advance(TimeSpan.FromSeconds(60));
        manager.Advance();
        var snapshot = manager.GetSnapshot();
        Assert.Empty(snapshot.Tracks);
        var changes = manager.GetChanges(snapshot.Epoch, 0);
        Assert.Contains(changes.Changes, c => c.Kind == TrackChangeKind.Remove && c.TrackId == first.TrackId);

        var second = manager.Submit(Observation(sequence: 2, seconds: 61));
        Assert.Equal(ObservationOutcome.Created, second.Outcome);
        Assert.NotEqual(first.TrackId, second.TrackId);
    }

    [Fact]
    public void DisconnectedSource_HoldsItsTracksAtStaleUntilItResumes()
    {
        var (manager, time) = Create();
        manager.Submit(Observation(source: "Core.AIS", target: "1"));
        manager.Submit(Observation(source: "Core.Radar", target: "1"));

        manager.ReportSourceHealth("Core.AIS", SourceHealth.Disconnected, "cable unplugged");
        time.Advance(TimeSpan.FromSeconds(1000));
        manager.Advance();

        var snapshot = manager.GetSnapshot();
        var ais = Assert.Single(snapshot.Tracks, t => t.SourceId == "Core.AIS");
        Assert.Equal(TrackState.Stale, ais.State);
        Assert.DoesNotContain(snapshot.Tracks, t => t.SourceId == "Core.Radar");
        var status = Assert.Single(snapshot.Sources, s => s.SourceId == "Core.AIS");
        Assert.Equal((SourceHealth.Disconnected, "cable unplugged"), (status.Health, status.Detail));

        manager.ReportSourceHealth("Core.AIS", SourceHealth.Healthy);
        manager.Advance();
        Assert.Equal(TrackState.Stale, Only(manager).State);
        time.Advance(TimeSpan.FromSeconds(29));
        manager.Advance();
        Assert.Equal(TrackState.Stale, Only(manager).State);
        time.Advance(TimeSpan.FromSeconds(1));
        manager.Advance();
        Assert.Equal(TrackState.Lost, Only(manager).State);
    }

    [Fact]
    public void ObservationFromADisconnectedSource_ReconnectsItAndRevivesTheTrack()
    {
        var (manager, time) = Create();
        manager.Submit(Observation());
        manager.ReportSourceHealth("Core.AIS", SourceHealth.Disconnected);
        Assert.Equal(TrackState.Stale, Only(manager).State);
        time.Advance(TimeSpan.FromSeconds(5));
        manager.Submit(Observation(sequence: 2, seconds: 5));
        Assert.Equal(TrackState.Active, Only(manager).State);
        Assert.Equal(SourceHealth.Healthy, Assert.Single(manager.GetSnapshot().Sources).Health);
    }

    [Fact]
    public void NewTracksBeyondCapacityAreRejected_ButExistingTracksStillUpdate()
    {
        var (manager, time) = Create(ShortPolicy with { MaxTracks = 2 });
        manager.Submit(Observation(target: "1"));
        manager.Submit(Observation(target: "2"));
        var rejected = manager.Submit(Observation(target: "3"));
        Assert.Equal((ObservationOutcome.Rejected, RejectionReason.CapacityExceeded),
            (rejected.Outcome, rejected.Reason));
        var update = manager.Submit(Observation(target: "1", sequence: 2, seconds: 1));
        Assert.Equal(ObservationOutcome.Updated, update.Outcome);

        time.Advance(TimeSpan.FromSeconds(60));
        manager.Advance();
        Assert.Equal(ObservationOutcome.Created, manager.Submit(Observation(target: "3", seconds: 61)).Outcome);
    }

    [Fact]
    public void Feed_OffersGaplessOrderedChanges_ThatConvergeWithTheSnapshot()
    {
        var (manager, time) = Create();
        manager.Submit(Observation(target: "1"));
        manager.Submit(Observation(target: "2"));
        var baseline = manager.GetSnapshot();
        var local = baseline.Tracks.ToDictionary(t => t.Id);
        var cursor = baseline.Revision;

        manager.Submit(Observation(target: "1", sequence: 2, seconds: 1, latitude: 26));
        manager.Submit(Observation(target: "3", seconds: 2));
        time.Advance(TimeSpan.FromSeconds(60));
        manager.Submit(Observation(target: "3", sequence: 2, seconds: 62));
        manager.Advance();

        var changes = manager.GetChanges(baseline.Epoch, cursor);
        Assert.False(changes.ResyncRequired);
        Assert.Equal(Enumerable.Range((int)cursor + 1, changes.Changes.Count).Select(i => (long)i),
            changes.Changes.Select(c => c.Revision));
        foreach (var change in changes.Changes)
        {
            if (change.Kind == TrackChangeKind.Upsert)
                local[change.Track!.Id] = change.Track;
            else if (change.Kind == TrackChangeKind.Remove)
                local.Remove(change.TrackId!);
        }
        var current = manager.GetSnapshot();
        Assert.Equal(current.Revision, changes.Revision);
        Assert.Equal(current.Tracks.OrderBy(t => t.Id), local.Values.OrderBy(t => t.Id));

        var none = manager.GetChanges(current.Epoch, current.Revision);
        Assert.Empty(none.Changes);
        Assert.False(none.ResyncRequired);
        Assert.Equal(current.Revision, none.Revision);
    }

    [Fact]
    public void Feed_RequiresResyncForUnknownEpochFutureRevisionOrTrimmedLog()
    {
        var (manager, _) = Create(ShortPolicy with { MaxChangeLog = 3 });
        for (var i = 1; i <= 6; i++)
            manager.Submit(Observation(target: i.ToString()));
        var snapshot = manager.GetSnapshot();

        Assert.True(manager.GetChanges("other-epoch", snapshot.Revision).ResyncRequired);
        Assert.True(manager.GetChanges(null, snapshot.Revision).ResyncRequired);
        Assert.True(manager.GetChanges(snapshot.Epoch, snapshot.Revision + 5).ResyncRequired);
        Assert.True(manager.GetChanges(snapshot.Epoch, 0).ResyncRequired);

        var retained = manager.GetChanges(snapshot.Epoch, snapshot.Revision - 3);
        Assert.False(retained.ResyncRequired);
        Assert.Equal(3, retained.Changes.Count);
    }

    [Fact]
    public void Feed_LimitsBatchSize_AndTheCursorContinuesWithoutGaps()
    {
        var (manager, _) = Create();
        for (var i = 1; i <= 5; i++)
            manager.Submit(Observation(target: i.ToString()));
        var epoch = manager.GetSnapshot().Epoch;

        var first = manager.GetChanges(epoch, 0, maxChanges: 2);
        Assert.Equal(2, first.Changes.Count);
        Assert.Equal(2, first.Revision);
        var second = manager.GetChanges(epoch, first.Revision, maxChanges: 10);
        Assert.Equal(new long[] { 3, 4, 5, 6 }, second.Changes.Select(c => c.Revision));
    }

    [Fact]
    public void Checkpoint_PreservesEpochRevisionAndTrackIds_AndReobservationUpdatesTheRestoredTrack()
    {
        var (original, _) = Create();
        Assert.Null(original.Capture());
        var created = original.Submit(Observation());
        original.ReportSourceHealth("Core.Radar", SourceHealth.Degraded, "noisy");
        var before = original.GetSnapshot();

        var (restored, _) = Create();
        restored.Restore(original.Capture()!);
        var after = restored.GetSnapshot();
        Assert.Equal((before.Epoch, before.Revision), (after.Epoch, after.Revision));
        Assert.Equal(before.Tracks, after.Tracks);
        Assert.Equal(before.Sources, after.Sources);
        var resumed = restored.GetChanges(before.Epoch, before.Revision);
        Assert.False(resumed.ResyncRequired);
        Assert.Empty(resumed.Changes);

        var update = restored.Submit(Observation(sequence: 2, seconds: 3));
        Assert.Equal((ObservationOutcome.Updated, created.TrackId), (update.Outcome, update.TrackId));
        var next = restored.Submit(Observation(target: "200"));
        Assert.Equal(ObservationOutcome.Created, next.Outcome);
        Assert.NotEqual(created.TrackId, next.TrackId);
        Assert.Equal(2, restored.GetSnapshot().Tracks.Count);
    }

    [Fact]
    public void Restore_RejectsUnsupportedOrCorruptState()
    {
        var (manager, _) = Create();
        Assert.Throws<InvalidDataException>(() => manager.Restore(new ModuleState(99, [])));
        Assert.Throws<InvalidDataException>(() => manager.Restore(new ModuleState(1, [1, 2, 3])));
        Assert.Empty(manager.GetSnapshot().Tracks);
    }
}
