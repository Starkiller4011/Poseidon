using Poseidon.Core.Modules;
using Poseidon.Modules.Tracks;
using Xunit;
using static CoreTracks.Tests.TrackTestData;

namespace CoreTracks.Tests;

// Slice 3 (manager): bearing association, refinement and kind changes, R33 to R36 (m1-e-track-fusion.md
// section 21.1, amendment 21.12). Everything else the manager does is in TrackManagerTests.cs.
public sealed class TrackBearingTests
{
    private static readonly TrackPolicy ShortPolicy = new(
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));

    private static (TrackManager Manager, ManualTime Time) Create(TrackPolicy? policy = null)
    {
        var time = new ManualTime(Start);
        return (new TrackManager(time, policy ?? ShortPolicy), time);
    }

    private static Track Only(TrackManager manager) => Assert.Single(manager.GetSnapshot().Tracks);

    // True bearing in [0, 360) from one position to another, independent of the manager's own frame math.
    private static double TrueBearingDegrees(GeodeticPosition from, GeodeticPosition to)
    {
        var lat1 = from.LatitudeDegrees * Math.PI / 180;
        var lat2 = to.LatitudeDegrees * Math.PI / 180;
        var dLon = (to.LongitudeDegrees - from.LongitudeDegrees) * Math.PI / 180;
        var bearing = Math.Atan2(
            Math.Sin(dLon) * Math.Cos(lat2),
            Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon));
        return ((bearing * 180 / Math.PI) + 360) % 360;
    }

    #region R33: a standalone bearing-only report creates a Bearing track

    [Fact]
    public void BearingOnlyReport_ThatCorrelatesWithNothing_CreatesABearingTrack()
    {
        var (manager, _) = Create();
        var ownShip = new GeodeticPosition(25.0, 55.0);
        var result = manager.Submit(BearingObservation(ownShipLatitude: ownShip.LatitudeDegrees,
            ownShipLongitude: ownShip.LongitudeDegrees, bearingDegrees: 90, bearingUncertaintyDegrees: 5));

        Assert.Equal(ObservationOutcome.Created, result.Outcome);
        var track = Only(manager);
        Assert.Equal(TrackKind.Bearing, track.Kind);
        Assert.Null(track.Position);
        Assert.NotNull(track.Bearing);
        Assert.Equal((90.0, 5.0, ownShip), (track.Bearing!.TrueBearingDegrees, track.Bearing.UncertaintyDegrees, track.Bearing.OwnShipPosition));
    }

    [Fact]
    public void BearingTrack_AgesAndIsRemoved_LikeAnyOtherTrack_AndItsNumberIsRetired()
    {
        var (manager, time) = Create();
        manager.RegisterSource(Descriptor(sourceId: "Core.EW", kind: "ew",
            intervalSeconds: 1, staleSeconds: 2, lostSeconds: 4, removeSeconds: 6, bearingUncertaintyDegrees: 5));
        var first = manager.Submit(BearingObservation(source: "Core.EW", target: "1"));

        time.Advance(TimeSpan.FromSeconds(7));
        manager.Advance();
        Assert.Empty(manager.GetSnapshot().Tracks);

        var second = manager.Submit(BearingObservation(source: "Core.EW", target: "2", sequence: 1, seconds: 7));
        Assert.NotEqual(first.TrackId, second.TrackId);
    }

    #endregion

    #region R34: bearing association with Point/Hybrid tracks, never with another Bearing track

    [Fact]
    public void BearingReport_ThatCorrelatesWithExactlyOnePointTrack_JoinsItAsAContribution()
    {
        var (manager, _) = Create();
        var target = new GeodeticPosition(25.1, 55.1);
        var pointResult = manager.Submit(Observation(source: "Core.AIS", target: "7",
            latitude: target.LatitudeDegrees, longitude: target.LongitudeDegrees));

        var ownShip = new GeodeticPosition(25.0, 55.0);
        var bearing = TrueBearingDegrees(ownShip, target);
        var result = manager.Submit(BearingObservation(source: "Core.EW", target: "1", sequence: 1, seconds: 1,
            ownShipLatitude: ownShip.LatitudeDegrees, ownShipLongitude: ownShip.LongitudeDegrees,
            bearingDegrees: bearing));

        Assert.Equal(ObservationOutcome.Updated, result.Outcome);
        Assert.Equal(pointResult.TrackId, result.TrackId);
        var track = Only(manager);
        Assert.Contains(track.Contributions, c => c.SourceId == "Core.EW" && !c.IsRanged);
    }

    [Fact]
    public void BearingReport_AmbiguousBetweenTwoTracks_StaysStandalone_AndJoinsOnceTheAmbiguityClears()
    {
        var (manager, time) = Create();
        var ownShip = new GeodeticPosition(0, 0);
        var near = Geodesy.Project(ownShip, 90, 1_000);
        var far = Geodesy.Project(ownShip, 90, 1_010); // almost exactly the same bearing from own ship

        var nearTrack = manager.Submit(Observation(source: "Core.AIS", target: "near",
            latitude: near.LatitudeDegrees, longitude: near.LongitudeDegrees));
        manager.Submit(Observation(source: "Core.AIS", target: "far",
            latitude: far.LatitudeDegrees, longitude: far.LongitudeDegrees));

        var bearing = manager.Submit(BearingObservation(source: "Core.EW", target: "1", sequence: 1, seconds: 1,
            ownShipLatitude: ownShip.LatitudeDegrees, ownShipLongitude: ownShip.LongitudeDegrees,
            bearingDegrees: TrueBearingDegrees(ownShip, near)));
        Assert.Equal(ObservationOutcome.Created, bearing.Outcome);
        Assert.Equal(TrackKind.Bearing, manager.GetSnapshot().Tracks.Single(t => t.Id == bearing.TrackId).Kind);

        // The far track is removed, clearing the ambiguity; the next bearing from the same source track joins
        // the sole remaining candidate.
        manager.ReportSourceHealth("Core.AIS", SourceHealth.Disconnected);
        time.Advance(TimeSpan.FromSeconds(1000));
        manager.Advance();
        manager.ReportSourceHealth("Core.AIS", SourceHealth.Healthy);
        var revived = manager.Submit(Observation(source: "Core.AIS", target: "near", sequence: 1, seconds: 1001,
            latitude: near.LatitudeDegrees, longitude: near.LongitudeDegrees));

        var rejoin = manager.Submit(BearingObservation(source: "Core.EW", target: "1", sequence: 2, seconds: 1002,
            ownShipLatitude: ownShip.LatitudeDegrees, ownShipLongitude: ownShip.LongitudeDegrees,
            bearingDegrees: TrueBearingDegrees(ownShip, near)));
        Assert.Equal(revived.TrackId, rejoin.TrackId);
    }

    [Fact]
    public void BearingTracks_NeverCorrelateWithEachOther()
    {
        var (manager, _) = Create();
        var first = manager.Submit(BearingObservation(source: "Core.EW", target: "1", bearingDegrees: 45));
        var second = manager.Submit(BearingObservation(source: "Core.Camera", target: "1", sequence: 1, seconds: 1,
            bearingDegrees: 45));

        Assert.Equal(ObservationOutcome.Created, second.Outcome);
        Assert.NotEqual(first.TrackId, second.TrackId);
        Assert.Equal(2, manager.GetSnapshot().Tracks.Count);
    }

    #endregion

    #region R35: a bearing contribution refines the point only with an Active AIS-only ranged contribution

    [Fact]
    public void BearingContribution_RefinesThePoint_WhenTheOnlyActiveRangedContributionIsAis()
    {
        var (manager, _) = Create();
        var ownShip = new GeodeticPosition(0, 0);
        var aisPosition = Geodesy.Project(ownShip, 40, 2_000);
        manager.Submit(Observation(source: "Core.AIS", target: "7",
            latitude: aisPosition.LatitudeDegrees, longitude: aisPosition.LongitudeDegrees,
            positionUncertaintyMetres: 150));

        var before = Only(manager).Position!;
        manager.Submit(BearingObservation(source: "Core.EW", target: "1", sequence: 1, seconds: 1,
            ownShipLatitude: ownShip.LatitudeDegrees, ownShipLongitude: ownShip.LongitudeDegrees,
            bearingDegrees: 45, bearingUncertaintyDegrees: 2));

        var after = Only(manager).Position!;
        var beforeBearing = TrueBearingDegrees(ownShip, before);
        var afterBearing = TrueBearingDegrees(ownShip, after);
        Assert.True(Math.Abs(afterBearing - 45) < Math.Abs(beforeBearing - 45),
            "An AIS-only ranged track should be refined toward the bearing.");
    }

    [Fact]
    public void BearingContribution_DoesNotRefineThePoint_WhileARadarContributionIsActive()
    {
        var (manager, _) = Create();
        var ownShip = new GeodeticPosition(0, 0);
        var position = Geodesy.Project(ownShip, 40, 2_000);
        manager.Submit(Observation(source: "Core.AIS", target: "7",
            latitude: position.LatitudeDegrees, longitude: position.LongitudeDegrees));
        manager.Submit(Observation(source: "Core.Radar", target: "7",
            latitude: position.LatitudeDegrees, longitude: position.LongitudeDegrees));

        var before = Only(manager).Position!;
        manager.Submit(BearingObservation(source: "Core.EW", target: "1", sequence: 1, seconds: 1,
            ownShipLatitude: ownShip.LatitudeDegrees, ownShipLongitude: ownShip.LongitudeDegrees,
            bearingDegrees: 45, bearingUncertaintyDegrees: 2));
        var after = Only(manager).Position!;

        Assert.Equal((before.LatitudeDegrees, before.LongitudeDegrees), (after.LatitudeDegrees, after.LongitudeDegrees));
        Assert.Contains(Only(manager).Contributions, c => c.SourceId == "Core.EW" && !c.IsRanged);
    }

    [Fact]
    public void BearingContribution_ThatStopsAgreeingForTheConfirmationCount_SplitsOffAsABearingTrack()
    {
        var (manager, _) = Create();
        var ownShip = new GeodeticPosition(0, 0);
        var position = Geodesy.Project(ownShip, 45, 2_000);
        var trackId = manager.Submit(Observation(source: "Core.AIS", target: "7",
            latitude: position.LatitudeDegrees, longitude: position.LongitudeDegrees)).TrackId!;
        manager.Submit(BearingObservation(source: "Core.EW", target: "1", sequence: 1, seconds: 1,
            ownShipLatitude: ownShip.LatitudeDegrees, ownShipLongitude: ownShip.LongitudeDegrees, bearingDegrees: 45));

        for (var i = 0; i < 3; i++)
            manager.Submit(BearingObservation(source: "Core.EW", target: "1", sequence: 2 + i, seconds: 2 + i,
                ownShipLatitude: ownShip.LatitudeDegrees, ownShipLongitude: ownShip.LongitudeDegrees,
                bearingDegrees: 200)); // consistently well away from the track's true bearing (45)

        var snapshot = manager.GetSnapshot();
        Assert.Equal(2, snapshot.Tracks.Count);
        var survivor = snapshot.Tracks.Single(t => t.Id == trackId);
        Assert.DoesNotContain(survivor.Contributions, c => c.SourceId == "Core.EW");
        var split = snapshot.Tracks.Single(t => t.Id != trackId);
        Assert.Equal(TrackKind.Bearing, split.Kind);
        Assert.Contains(split.Contributions, c => c.SourceId == "Core.EW");
    }

    #endregion

    #region R36: kind changes keep the track number

    [Fact]
    public void ABearingTrackWhoseSourceReportsARange_BecomesAPointTrack_KeepingItsNumber()
    {
        var (manager, _) = Create();
        var bearing = manager.Submit(BearingObservation(source: "Core.EW", target: "1"));
        var position = new GeodeticPosition(25.0, 55.0);
        var ranged = manager.Submit(Observation(source: "Core.EW", target: "1", sequence: 2, seconds: 1,
            latitude: position.LatitudeDegrees, longitude: position.LongitudeDegrees));

        Assert.Equal(bearing.TrackId, ranged.TrackId);
        var track = Only(manager);
        Assert.Equal(TrackKind.Point, track.Kind);
        Assert.NotNull(track.Position);
    }

    [Fact]
    public void ATrackWhoseRangedContributionsAllStop_WhileABearingContributionIsActive_BecomesHybrid()
    {
        var (manager, time) = Create();
        manager.RegisterSource(Descriptor(sourceId: "Core.AIS", intervalSeconds: 1,
            staleSeconds: 2, lostSeconds: 4, removeSeconds: 1000));
        var ownShip = new GeodeticPosition(0, 0);
        var position = Geodesy.Project(ownShip, 45, 2_000);
        var trackId = manager.Submit(Observation(source: "Core.AIS", target: "7",
            latitude: position.LatitudeDegrees, longitude: position.LongitudeDegrees)).TrackId!;
        manager.Submit(BearingObservation(source: "Core.EW", target: "1", sequence: 1, seconds: 1,
            ownShipLatitude: ownShip.LatitudeDegrees, ownShipLongitude: ownShip.LongitudeDegrees, bearingDegrees: 45));

        // The only ranged contribution (AIS) is lost; the bearing contribution is kept alive by its own,
        // much longer, unregistered-default timings.
        time.Advance(TimeSpan.FromSeconds(5));
        manager.Advance();

        var track = manager.GetSnapshot().Tracks.Single(t => t.Id == trackId);
        Assert.Equal(TrackKind.Hybrid, track.Kind);
        Assert.NotNull(track.Position);
        Assert.NotNull(track.Bearing);
    }

    [Fact]
    public void AHybridTrack_BecomesPointAgain_WhenARangedReportArrives()
    {
        var (manager, time) = Create();
        manager.RegisterSource(Descriptor(sourceId: "Core.AIS", intervalSeconds: 1,
            staleSeconds: 2, lostSeconds: 4, removeSeconds: 1000));
        var ownShip = new GeodeticPosition(0, 0);
        var position = Geodesy.Project(ownShip, 45, 2_000);
        var trackId = manager.Submit(Observation(source: "Core.AIS", target: "7",
            latitude: position.LatitudeDegrees, longitude: position.LongitudeDegrees)).TrackId!;
        manager.Submit(BearingObservation(source: "Core.EW", target: "1", sequence: 1, seconds: 1,
            ownShipLatitude: ownShip.LatitudeDegrees, ownShipLongitude: ownShip.LongitudeDegrees, bearingDegrees: 45));
        time.Advance(TimeSpan.FromSeconds(5));
        manager.Advance();
        Assert.Equal(TrackKind.Hybrid, manager.GetSnapshot().Tracks.Single(t => t.Id == trackId).Kind);

        manager.Submit(Observation(source: "Core.AIS", target: "7", sequence: 2, seconds: 6,
            latitude: position.LatitudeDegrees, longitude: position.LongitudeDegrees));
        Assert.Equal(TrackKind.Point, manager.GetSnapshot().Tracks.Single(t => t.Id == trackId).Kind);
    }

    #endregion
}
