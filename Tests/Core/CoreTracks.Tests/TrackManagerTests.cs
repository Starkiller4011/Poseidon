using Poseidon.Core.Modules;
using Poseidon.Modules.Tracks;
using Xunit;
using static CoreTracks.Tests.TrackTestData;

namespace CoreTracks.Tests;

// Slice 3 (manager): correlation, contributions, lifecycle, automatic split, operator merge/split and
// checkpoint, against R1 to R21 (m1-e-track-fusion.md section 21.1). Bearing association, refinement and kind
// changes (R33 to R36) are in TrackBearingTests.cs. Rewritten for fusion: a track is no longer one (source,
// target) pair, so tests built on that assumption (previously E2, E5, E6) are not carried over; see 21.6.
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

    private static TrackContribution Contribution(Track track, string sourceId)
        => Assert.Single(track.Contributions, c => c.SourceId == sourceId);

    // 20 m east: comfortably inside the 200 m join/split cap and any reasonable uncertainty-based gate.
    private static GeodeticPosition Near(GeodeticPosition from) => Geodesy.Project(from, 90, 20);

    // 5,000 m east: comfortably outside the 200 m join/split cap regardless of uncertainty.
    private static GeodeticPosition Far(GeodeticPosition from) => Geodesy.Project(from, 90, 5_000);

    private static double DistanceMetres(GeodeticPosition a, GeodeticPosition b)
    {
        var lat1 = a.LatitudeDegrees * Math.PI / 180;
        var lat2 = b.LatitudeDegrees * Math.PI / 180;
        var dLat = (b.LatitudeDegrees - a.LatitudeDegrees) * Math.PI / 180;
        var dLon = (b.LongitudeDegrees - a.LongitudeDegrees) * Math.PI / 180;
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * Geodesy.MeanEarthRadiusMetres * Math.Asin(Math.Sqrt(Math.Clamp(h, 0, 1)));
    }

    #region R1, R10, R19: carried-over basics (invalid, duplicate/out-of-order, capacity)

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
    public void ExactRepeatIsDuplicate_AndOlderObservationIsOutOfOrder_WithoutChangingTheTrack()
    {
        var (manager, _) = Create();
        manager.Submit(Observation(sequence: 5, seconds: 10));
        var revision = manager.GetSnapshot().Revision;

        var duplicate = manager.Submit(Observation(sequence: 5, seconds: 10));
        var older = manager.Submit(Observation(sequence: 6, seconds: 4));

        Assert.Equal(RejectionReason.Duplicate, duplicate.Reason);
        Assert.Equal(RejectionReason.OutOfOrder, older.Reason);
        Assert.Equal(revision, manager.GetSnapshot().Revision);
        Assert.Equal(5L, Contribution(Only(manager), "Core.AIS").LastSequence);
    }

    [Fact]
    public void NewTracksBeyondCapacityAreRejected_ButExistingContributionsStillUpdate()
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

    #endregion

    #region R2 to R6, R15: correlation

    [Fact]
    public void UnknownSourceTrack_ThatCorrelatesWithNothing_CreatesATrackWithAGeneratedNumber()
    {
        var (manager, _) = Create();
        var result = manager.Submit(Observation(name: "MV Example"));
        Assert.Equal(ObservationOutcome.Created, result.Outcome);
        var track = Only(manager);
        Assert.Equal(track.Id, result.TrackId);
        Assert.NotEqual("100", track.Id);
        Assert.Equal(TrackKind.Point, track.Kind);
        Assert.Equal("MV Example", track.Name);
        var contribution = Contribution(track, "Core.AIS");
        Assert.Equal(("100", 1L, 1), (contribution.SourceTargetId, contribution.LastSequence, contribution.UpdateCount));
    }

    [Fact]
    public void KnownSourceTrack_UpdatesItsContributionAndTheTrack()
    {
        var (manager, _) = Create();
        var created = manager.Submit(Observation(name: "MV Example"));
        var updated = manager.Submit(Observation(sequence: 2, seconds: 5, latitude: 25.1, name: "MV Example"));
        Assert.Equal((ObservationOutcome.Updated, created.TrackId), (updated.Outcome, updated.TrackId));
        var track = Only(manager);
        Assert.Equal(25.1, track.Position!.LatitudeDegrees);
        Assert.Equal(Start.AddSeconds(5), track.MeasuredUtc);
        var contribution = Contribution(track, "Core.AIS");
        Assert.Equal((2L, 2), (contribution.LastSequence, contribution.UpdateCount));
    }

    [Fact]
    public void ANewSourceTrack_InsideTheGateOfExactlyOneCandidate_JoinsItAsAContribution()
    {
        var (manager, _) = Create();
        var ais = manager.Submit(Observation(source: "Core.AIS", target: "7"));
        var origin = Only(manager).Position!;

        var radar = manager.Submit(Observation(source: "Core.Radar", target: "7",
            latitude: Near(origin).LatitudeDegrees, longitude: Near(origin).LongitudeDegrees));

        Assert.Equal(ObservationOutcome.Updated, radar.Outcome);
        Assert.Equal(ais.TrackId, radar.TrackId);
        var track = Only(manager);
        Assert.Equal(2, track.Contributions.Count);
        Assert.NotNull(Contribution(track, "Core.AIS"));
        Assert.NotNull(Contribution(track, "Core.Radar"));
    }

    [Fact]
    public void ANewSourceTrack_OutsideEveryGate_CreatesANewTrack()
    {
        var (manager, _) = Create();
        var ais = manager.Submit(Observation(source: "Core.AIS", target: "7"));
        var origin = Only(manager).Position!;

        var radar = manager.Submit(Observation(source: "Core.Radar", target: "7",
            latitude: Far(origin).LatitudeDegrees, longitude: Far(origin).LongitudeDegrees));

        Assert.Equal(ObservationOutcome.Created, radar.Outcome);
        Assert.NotEqual(ais.TrackId, radar.TrackId);
        Assert.Equal(2, manager.GetSnapshot().Tracks.Count);
    }

    [Fact]
    public void ANewSourceTrack_WellInsideOneCandidateAndFarFromAnother_JoinsTheDominantOne()
    {
        var (manager, _) = Create();
        var farOrigin = new GeodeticPosition(25.0, 55.0);
        var closeOrigin = Geodesy.Project(farOrigin, 90, 190); // near the 200 m cap, but still gated
        manager.Submit(Observation(source: "Core.Radar", target: "far",
            latitude: farOrigin.LatitudeDegrees, longitude: farOrigin.LongitudeDegrees));
        var close = manager.Submit(Observation(source: "Core.Radar", target: "close",
            latitude: closeOrigin.LatitudeDegrees, longitude: closeOrigin.LongitudeDegrees));

        var report = Near(closeOrigin); // 20 m from "close", 170 m from "far": heavily dominated by "close"
        var ais = manager.Submit(Observation(source: "Core.AIS", target: "1",
            latitude: report.LatitudeDegrees, longitude: report.LongitudeDegrees));

        Assert.Equal(ObservationOutcome.Updated, ais.Outcome);
        Assert.Equal(close.TrackId, ais.TrackId);
    }

    [Fact]
    public void ANewSourceTrack_EquidistantBetweenTwoCandidates_IsAmbiguousAndCreatesANewTrack()
    {
        var (manager, _) = Create();
        var origin = new GeodeticPosition(25.0, 55.0);
        var east = manager.Submit(Observation(source: "Core.Radar", target: "east",
            latitude: Geodesy.Project(origin, 90, 30).LatitudeDegrees,
            longitude: Geodesy.Project(origin, 90, 30).LongitudeDegrees));
        var west = manager.Submit(Observation(source: "Core.Radar", target: "west",
            latitude: Geodesy.Project(origin, 270, 30).LatitudeDegrees,
            longitude: Geodesy.Project(origin, 270, 30).LongitudeDegrees));

        var ais = manager.Submit(Observation(source: "Core.AIS", target: "1",
            latitude: origin.LatitudeDegrees, longitude: origin.LongitudeDegrees));

        Assert.Equal(ObservationOutcome.Created, ais.Outcome);
        Assert.NotEqual(east.TrackId, ais.TrackId);
        Assert.NotEqual(west.TrackId, ais.TrackId);
        Assert.Equal(3, manager.GetSnapshot().Tracks.Count);
    }

    [Fact]
    public void ATrack_NeverGainsASecondActiveContributionFromTheSameSource()
    {
        var (manager, _) = Create();
        var origin = new GeodeticPosition(25.0, 55.0);
        var first = manager.Submit(Observation(source: "Core.Radar", target: "7",
            latitude: origin.LatitudeDegrees, longitude: origin.LongitudeDegrees));
        // A second, simultaneous report from the SAME source near the first is, by definition, a different
        // target: it must not join, even though it would otherwise be well inside the gate.
        var second = manager.Submit(Observation(source: "Core.Radar", target: "8",
            latitude: Near(origin).LatitudeDegrees, longitude: Near(origin).LongitudeDegrees));

        Assert.Equal(ObservationOutcome.Created, second.Outcome);
        Assert.NotEqual(first.TrackId, second.TrackId);
        Assert.Equal(2, manager.GetSnapshot().Tracks.Count);
    }

    [Fact]
    public void ANewSourceTrack_FromASourceWhoseEarlierContributionIsLost_MayRejoinTheSameTrack()
    {
        var (manager, time) = Create();
        var origin = new GeodeticPosition(25.0, 55.0);
        manager.Submit(Observation(source: "Core.AIS", target: "7",
            latitude: origin.LatitudeDegrees, longitude: origin.LongitudeDegrees));
        var radar = manager.Submit(Observation(source: "Core.Radar", target: "7",
            latitude: origin.LatitudeDegrees, longitude: origin.LongitudeDegrees));
        var trackId = radar.TrackId!;

        // The radar contribution ages out to Lost without affecting the AIS contribution.
        manager.ReportSourceHealth("Core.Radar", SourceHealth.Disconnected);
        time.Advance(TimeSpan.FromSeconds(1000));
        manager.Advance();
        manager.ReportSourceHealth("Core.Radar", SourceHealth.Healthy);

        // A brand new radar source track (a different target number) near the same place now may rejoin.
        var rejoined = manager.Submit(Observation(source: "Core.Radar", target: "9", sequence: 1, seconds: 1000,
            latitude: origin.LatitudeDegrees, longitude: origin.LongitudeDegrees));
        Assert.Equal(trackId, rejoined.TrackId);
        var contribution = Contribution(Only(manager), "Core.Radar");
        Assert.Equal(("9", TrackState.Active), (contribution.SourceTargetId, contribution.State));
    }

    [Fact]
    public void JoiningContributions_FuseIntoOneEstimate_WeightedByTheirReportedUncertainty()
    {
        var (manager, _) = Create();
        var origin = new GeodeticPosition(0, 0);
        var tight = Geodesy.Project(origin, 90, 10);
        manager.Submit(Observation(source: "Core.AIS", target: "7", positionUncertaintyMetres: 50,
            latitude: origin.LatitudeDegrees, longitude: origin.LongitudeDegrees));
        manager.Submit(Observation(source: "Core.Radar", target: "7", positionUncertaintyMetres: 5,
            latitude: tight.LatitudeDegrees, longitude: tight.LongitudeDegrees));

        // The tighter radar contribution should pull the fused position closer to it than to the looser AIS one.
        var fused = Only(manager).Position!;
        var distanceToRadar = DistanceMetres(fused, tight);
        var distanceToAis = DistanceMetres(fused, origin);
        Assert.True(distanceToRadar < distanceToAis,
            $"The fused position ({distanceToRadar} m from radar, {distanceToAis} m from AIS) should sit closer to the tighter report.");
    }

    [Fact]
    public void ALateReportFromAnotherSource_IsAppliedWithoutBreakingTheTrack()
    {
        var (manager, _) = Create();
        var origin = new GeodeticPosition(25.0, 55.0);
        manager.Submit(Observation(source: "Core.AIS", target: "7", seconds: 100,
            latitude: origin.LatitudeDegrees, longitude: origin.LongitudeDegrees));
        // A radar contribution measured earlier than the AIS contribution's current time.
        var result = manager.Submit(Observation(source: "Core.Radar", target: "7", seconds: 40,
            latitude: origin.LatitudeDegrees, longitude: origin.LongitudeDegrees));
        Assert.Equal(ObservationOutcome.Updated, result.Outcome);
        var track = Only(manager);
        Assert.True(double.IsFinite(track.Position!.LatitudeDegrees));
        Assert.Equal(2, track.Contributions.Count);
    }

    #endregion

    #region R11 to R14: descriptors and per-contribution lifecycle

    [Fact]
    public void RegisteredSourceTimings_ApplyImmediately_AndSupersedeTheUnregisteredDefaults()
    {
        var (manager, time) = Create();
        manager.RegisterSource(Descriptor(sourceId: "Core.Radar", kind: TrackSourceKinds.Radar,
            intervalSeconds: 1, staleSeconds: 5, lostSeconds: 10, removeSeconds: 15));
        manager.Submit(Observation(source: "Core.Radar", target: "7"));

        time.Advance(TimeSpan.FromSeconds(6));
        manager.Advance();
        Assert.Equal(TrackState.Stale, Only(manager).State);

        // Re-registering tightens the timings further and the new timings apply immediately.
        manager.RegisterSource(Descriptor(sourceId: "Core.Radar", kind: TrackSourceKinds.Radar,
            intervalSeconds: 1, staleSeconds: 2, lostSeconds: 3, removeSeconds: 4));
        manager.Advance();
        Assert.Equal(TrackState.Lost, Only(manager).State);
    }

    [Fact]
    public void UnregisteredSource_UsesThePolicyDefaults()
    {
        var (manager, time) = Create(TrackPolicy.Default with
        {
            UnregisteredStaleAfter = TimeSpan.FromSeconds(10),
            UnregisteredLostAfter = TimeSpan.FromSeconds(30),
            UnregisteredRemoveAfter = TimeSpan.FromSeconds(60)
        });
        manager.Submit(Observation(source: "Core.Unknown"));
        time.Advance(TimeSpan.FromSeconds(11));
        manager.Advance();
        Assert.Equal(TrackState.Stale, Only(manager).State);
    }

    [Fact]
    public void Track_IsActiveWhileAnyContributionIsActive_StaleWhenNoneAreActive_LostWhenAllAre()
    {
        var (manager, time) = Create();
        manager.RegisterSource(Descriptor(sourceId: "Core.AIS", intervalSeconds: 10,
            staleSeconds: 20, lostSeconds: 40, removeSeconds: 1000));
        manager.RegisterSource(Descriptor(sourceId: "Core.Radar", kind: TrackSourceKinds.Radar,
            intervalSeconds: 1, staleSeconds: 2, lostSeconds: 4, removeSeconds: 1000));
        var origin = new GeodeticPosition(25.0, 55.0);
        manager.Submit(Observation(source: "Core.AIS", target: "7",
            latitude: origin.LatitudeDegrees, longitude: origin.LongitudeDegrees));
        manager.Submit(Observation(source: "Core.Radar", target: "7",
            latitude: origin.LatitudeDegrees, longitude: origin.LongitudeDegrees));

        // The radar contribution goes stale first; AIS is still Active, so the track stays Active.
        time.Advance(TimeSpan.FromSeconds(3));
        manager.Advance();
        Assert.Equal(TrackState.Active, Only(manager).State);
        Assert.Equal(TrackState.Stale, Contribution(Only(manager), "Core.Radar").State);

        // The radar contribution is now Lost, AIS still Active: the track stays Active.
        time.Advance(TimeSpan.FromSeconds(2));
        manager.Advance();
        Assert.Equal(TrackState.Active, Only(manager).State);
        Assert.Equal(TrackState.Lost, Contribution(Only(manager), "Core.Radar").State);

        // Now AIS also goes stale: no contribution is Active, so the track is Stale.
        time.Advance(TimeSpan.FromSeconds(16));
        manager.Advance();
        Assert.Equal(TrackState.Stale, Only(manager).State);

        // Both contributions are now Lost: the track is Lost.
        time.Advance(TimeSpan.FromSeconds(20));
        manager.Advance();
        Assert.Equal(TrackState.Lost, Only(manager).State);
    }

    [Fact]
    public void ATrack_IsRemovedOnlyWhenItsLastContributionIsRemoved_AndKeepsItsNumberMeanwhile()
    {
        var (manager, time) = Create();
        manager.RegisterSource(Descriptor(sourceId: "Core.AIS", intervalSeconds: 10,
            staleSeconds: 20, lostSeconds: 40, removeSeconds: 1000));
        manager.RegisterSource(Descriptor(sourceId: "Core.Radar", kind: TrackSourceKinds.Radar,
            intervalSeconds: 1, staleSeconds: 2, lostSeconds: 4, removeSeconds: 8));
        var origin = new GeodeticPosition(25.0, 55.0);
        var ais = manager.Submit(Observation(source: "Core.AIS", target: "7",
            latitude: origin.LatitudeDegrees, longitude: origin.LongitudeDegrees));
        manager.Submit(Observation(source: "Core.Radar", target: "7",
            latitude: origin.LatitudeDegrees, longitude: origin.LongitudeDegrees));

        // The radar contribution is removed (8 s), the AIS contribution is not (removes at 1000 s): the track
        // survives with one contribution and the same number.
        time.Advance(TimeSpan.FromSeconds(9));
        manager.Advance();
        var track = Only(manager);
        Assert.Equal(ais.TrackId, track.Id);
        Assert.Single(track.Contributions);
        Assert.DoesNotContain(track.Contributions, c => c.SourceId == "Core.Radar");

        // Now the AIS contribution is also removed: the track itself is removed.
        time.Advance(TimeSpan.FromSeconds(1000));
        manager.Advance();
        Assert.Empty(manager.GetSnapshot().Tracks);
    }

    [Fact]
    public void DisconnectedSource_HoldsItsContributionsAtStale_NotLostOrRemoved_UntilItResumes()
    {
        var (manager, time) = Create();
        manager.Submit(Observation(source: "Core.AIS", target: "1"));

        manager.ReportSourceHealth("Core.AIS", SourceHealth.Disconnected, "cable unplugged");
        time.Advance(TimeSpan.FromSeconds(100_000));
        manager.Advance();

        var track = Only(manager);
        Assert.Equal(TrackState.Stale, track.State);
        Assert.Equal(TrackState.Stale, Contribution(track, "Core.AIS").State);
        var status = Assert.Single(manager.GetSnapshot().Sources, s => s.SourceId == "Core.AIS");
        Assert.Equal((SourceHealth.Disconnected, "cable unplugged"), (status.Health, status.Detail));

        manager.ReportSourceHealth("Core.AIS", SourceHealth.Healthy);
        manager.Advance();
        Assert.Equal(TrackState.Stale, Only(manager).State);
    }

    #endregion

    #region R7: automatic split

    [Fact]
    public void ContributionsThatStopAgreeing_SplitAfterTheConfirmationCount_NotOnATransientDisagreement()
    {
        var (manager, time) = Create();
        var origin = new GeodeticPosition(25.0, 55.0);
        var ais = manager.Submit(Observation(source: "Core.AIS", target: "7",
            latitude: origin.LatitudeDegrees, longitude: origin.LongitudeDegrees));
        manager.Submit(Observation(source: "Core.Radar", target: "7",
            latitude: origin.LatitudeDegrees, longitude: origin.LongitudeDegrees));
        var trackId = ais.TrackId!;

        var divergent = Far(origin);
        // One transient disagreement: does not split.
        manager.Submit(Observation(source: "Core.Radar", target: "7", sequence: 2, seconds: 1,
            latitude: divergent.LatitudeDegrees, longitude: divergent.LongitudeDegrees));
        Assert.Equal(2, manager.GetSnapshot().Tracks.Single(t => t.Id == trackId).Contributions.Count);

        // Back to agreeing resets the streak.
        manager.Submit(Observation(source: "Core.Radar", target: "7", sequence: 3, seconds: 2,
            latitude: origin.LatitudeDegrees, longitude: origin.LongitudeDegrees));

        // Three consecutive disagreeing updates: splits. The AIS contribution (the first contributor) keeps the
        // track number; the radar contribution becomes a new track.
        for (var i = 0; i < 3; i++)
            manager.Submit(Observation(source: "Core.Radar", target: "7", sequence: 4 + i, seconds: 3 + i,
                latitude: divergent.LatitudeDegrees, longitude: divergent.LongitudeDegrees));

        var snapshot = manager.GetSnapshot();
        Assert.Equal(2, snapshot.Tracks.Count);
        var survivor = snapshot.Tracks.Single(t => t.Id == trackId);
        Assert.Single(survivor.Contributions, c => c.SourceId == "Core.AIS");
        var split = snapshot.Tracks.Single(t => t.Id != trackId);
        Assert.Single(split.Contributions, c => c.SourceId == "Core.Radar");
    }

    #endregion

    #region R17, R18: operator merge and split

    [Fact]
    public void Merge_KeepsTheOlderNumber_MovesContributions_AndLaterReportsUpdateTheSurvivor()
    {
        var (manager, _) = Create();
        var older = manager.Submit(Observation(source: "Core.AIS", target: "7"));
        var newer = manager.Submit(Observation(source: "Core.Radar", target: "8", sequence: 1, seconds: 1,
            latitude: Far(new GeodeticPosition(25.0, 55.0)).LatitudeDegrees,
            longitude: Far(new GeodeticPosition(25.0, 55.0)).LongitudeDegrees));
        Assert.NotEqual(older.TrackId, newer.TrackId);

        var result = manager.Merge(older.TrackId!, newer.TrackId!);
        Assert.True(result.Succeeded);
        Assert.Equal(older.TrackId, result.TrackId);

        var snapshot = manager.GetSnapshot();
        Assert.Single(snapshot.Tracks);
        var merged = Only(manager);
        Assert.Equal(2, merged.Contributions.Count);

        var update = manager.Submit(Observation(source: "Core.Radar", target: "8", sequence: 2, seconds: 2));
        Assert.Equal((ObservationOutcome.Updated, older.TrackId), (update.Outcome, update.TrackId));
    }

    [Fact]
    public void Merge_FailsWithAReason_ForAnUnknownTrack()
    {
        var (manager, _) = Create();
        var only = manager.Submit(Observation());
        var result = manager.Merge(only.TrackId!, "T-does-not-exist");
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public void Split_MovesOneContributionToANewTrack_AndReEstimatesBoth()
    {
        var (manager, _) = Create();
        var origin = new GeodeticPosition(25.0, 55.0);
        var ais = manager.Submit(Observation(source: "Core.AIS", target: "7",
            latitude: origin.LatitudeDegrees, longitude: origin.LongitudeDegrees));
        manager.Submit(Observation(source: "Core.Radar", target: "7",
            latitude: origin.LatitudeDegrees, longitude: origin.LongitudeDegrees));

        var result = manager.Split(ais.TrackId!, "Core.Radar", "7");
        Assert.True(result.Succeeded);
        Assert.NotEqual(ais.TrackId, result.TrackId);

        var snapshot = manager.GetSnapshot();
        Assert.Equal(2, snapshot.Tracks.Count);
        var original = snapshot.Tracks.Single(t => t.Id == ais.TrackId);
        Assert.Single(original.Contributions, c => c.SourceId == "Core.AIS");
        var split = snapshot.Tracks.Single(t => t.Id == result.TrackId);
        Assert.Single(split.Contributions, c => c.SourceId == "Core.Radar");
    }

    [Fact]
    public void Split_FailsWithAReason_WhenItWouldRemoveTheLastContribution()
    {
        var (manager, _) = Create();
        var ais = manager.Submit(Observation(source: "Core.AIS", target: "7"));
        var result = manager.Split(ais.TrackId!, "Core.AIS", "7");
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Detail);
        Assert.Single(manager.GetSnapshot().Tracks);
    }

    [Fact]
    public void Split_FailsWithAReason_ForAnUnknownTrackOrContribution()
    {
        var (manager, _) = Create();
        var ais = manager.Submit(Observation(source: "Core.AIS", target: "7"));
        Assert.False(manager.Split("T-does-not-exist", "Core.AIS", "7").Succeeded);
        Assert.False(manager.Split(ais.TrackId!, "Core.Radar", "99").Succeeded);
    }

    #endregion

    #region R20: feed (reshaped Track, including contributions)

    [Fact]
    public void Feed_OffersGaplessOrderedChanges_IncludingContributions_ThatConvergeWithTheSnapshot()
    {
        var (manager, time) = Create();
        manager.Submit(Observation(target: "1"));
        manager.Submit(Observation(target: "2"));
        var baseline = manager.GetSnapshot();
        var local = baseline.Tracks.ToDictionary(t => t.Id);
        var cursor = baseline.Revision;

        manager.Submit(Observation(target: "1", sequence: 2, seconds: 1, latitude: 25.00001));
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

        var retained = manager.GetChanges(snapshot.Epoch, snapshot.Revision - 3);
        Assert.False(retained.ResyncRequired);
        Assert.Equal(3, retained.Changes.Count);
    }

    #endregion

    #region R21: checkpoint (schema 2)

    [Fact]
    public void Checkpoint_PreservesContributionsDescriptorsAndSourceHealth_AndRestoredTracksStillCorrelate()
    {
        var (original, _) = Create();
        Assert.Null(original.Capture());
        original.RegisterSource(Descriptor(sourceId: "Core.Radar", kind: TrackSourceKinds.Radar, intervalSeconds: 5));
        var origin = new GeodeticPosition(25.0, 55.0);
        var ais = original.Submit(Observation(source: "Core.AIS", target: "7",
            latitude: origin.LatitudeDegrees, longitude: origin.LongitudeDegrees));
        original.Submit(Observation(source: "Core.Radar", target: "7",
            latitude: origin.LatitudeDegrees, longitude: origin.LongitudeDegrees));
        original.ReportSourceHealth("Core.Radar", SourceHealth.Degraded, "noisy");
        var before = original.GetSnapshot();

        var (restored, _) = Create();
        restored.Restore(original.Capture()!);
        var after = restored.GetSnapshot();
        Assert.Equal((before.Epoch, before.Revision), (after.Epoch, after.Revision));
        Assert.Equal(before.Tracks, after.Tracks);
        Assert.Equal(before.Sources, after.Sources);

        // A later report for the same (source, target) pair updates the restored track, proving the
        // correlation index survived, not just the public snapshot.
        var update = restored.Submit(Observation(source: "Core.AIS", target: "7", sequence: 2, seconds: 3,
            latitude: origin.LatitudeDegrees, longitude: origin.LongitudeDegrees));
        Assert.Equal((ObservationOutcome.Updated, ais.TrackId), (update.Outcome, update.TrackId));
        Assert.Single(restored.GetSnapshot().Tracks);
    }

    [Fact]
    public void Restore_RejectsUnsupportedOrCorruptState()
    {
        var (manager, _) = Create();
        Assert.Throws<InvalidDataException>(() => manager.Restore(new ModuleState(99, [])));
        Assert.Throws<InvalidDataException>(() => manager.Restore(new ModuleState(2, [1, 2, 3])));
        Assert.Empty(manager.GetSnapshot().Tracks);
    }

    #endregion
}
