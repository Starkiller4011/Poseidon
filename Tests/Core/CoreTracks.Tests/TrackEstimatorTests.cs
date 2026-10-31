using Poseidon.Core.Modules;
using Xunit;

namespace Poseidon.Modules.Tracks.Tests;

// Slice 2 (estimator): frame conversion and the Kalman filter alone, numerically, per m1-e-track-fusion.md
// section 21.5 item 2. R8 (fused estimate, weighting), R9 (late reports), R34/R35 (scalar bearing update) and
// R36 (hybrid extrapolation). The correlator, contributions and kind changes are slice 3 and are not tested here.
public sealed class TrackEstimatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    // Great-circle distance between two geodetic positions, independent of the estimator's own frame math.
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

    // True bearing in [0, 360) from one position to another, independent of the estimator's own frame math.
    private static double TrueBearingDegrees(GeodeticPosition from, GeodeticPosition to)
    {
        var lat1 = from.LatitudeDegrees * Math.PI / 180;
        var lat2 = to.LatitudeDegrees * Math.PI / 180;
        var dLon = (to.LongitudeDegrees - from.LongitudeDegrees) * Math.PI / 180;
        var bearing = Math.Atan2(
            Math.Sin(dLon) * Math.Cos(lat2),
            Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon));
        var degrees = bearing * 180 / Math.PI;
        return (degrees + 360) % 360;
    }

    [Fact]
    public void UpdatePosition_RepeatedConsistentMeasurements_ConvergeNearTheMeasurementWithShrinkingUncertainty()
    {
        var measured = new GeodeticPosition(25.0, 55.0);
        var estimator = new TrackEstimator(measured, positionUncertaintyMetres: 30, Start);
        var initialUncertainty = estimator.Estimate.PositionUncertaintyMetres;

        for (var i = 1; i <= 5; i++)
            estimator.UpdatePosition(Start.AddSeconds(i * 10), measured, positionUncertaintyMetres: 10);
        var afterFive = estimator.Estimate.PositionUncertaintyMetres;

        for (var i = 6; i <= 30; i++)
            estimator.UpdatePosition(Start.AddSeconds(i * 10), measured, positionUncertaintyMetres: 10);
        var afterThirty = estimator.Estimate.PositionUncertaintyMetres;

        Assert.True(afterFive < initialUncertainty,
            $"Five consistent reports should already tighten the estimate below the initial {initialUncertainty} m.");
        // PositionUncertaintyMetres combines both axes (DRMS, sqrt(east variance + north variance)), so the
        // converged figure is compared against a single report's own uncertainty expressed the same way.
        var singleReportDrms = 10 * Math.Sqrt(2);
        Assert.True(afterThirty < singleReportDrms,
            $"Thirty consistent 10 m reports should converge tighter than any one report's own uncertainty ({singleReportDrms:F2} m), not {afterThirty} m.");
        var distance = DistanceMetres(measured, estimator.Estimate.Position);
        Assert.True(distance < 1, $"The converged position drifted {distance} m from the repeated measurement.");
    }

    [Fact]
    public void UpdatePosition_WeightsByUncertainty_ATighterMeasurementPullsTheEstimateHarder()
    {
        var prior = new GeodeticPosition(0, 0);
        var measured = Geodesy.Project(prior, 90, 100);

        var tight = new TrackEstimator(prior, positionUncertaintyMetres: 50, Start);
        tight.UpdatePosition(Start.AddSeconds(1), measured, positionUncertaintyMetres: 5);

        var loose = new TrackEstimator(prior, positionUncertaintyMetres: 50, Start);
        loose.UpdatePosition(Start.AddSeconds(1), measured, positionUncertaintyMetres: 100);

        var tightDistance = DistanceMetres(prior, tight.Estimate.Position);
        var looseDistance = DistanceMetres(prior, loose.Estimate.Position);

        Assert.True(tightDistance > looseDistance,
            $"A measurement ten times tighter than the prior should move the estimate further (got {tightDistance} m vs {looseDistance} m).");
        Assert.True(tightDistance > 80, $"The tight update should dominate the estimate (moved only {tightDistance} m of 100).");
        Assert.True(looseDistance < 50, $"The loose update should pull the estimate only part of the way (moved {looseDistance} m of 100).");
    }

    [Fact]
    public void UpdatePosition_ALateMeasurement_IsAppliedAtTheCurrentTime_WithoutBreakingTheFilter()
    {
        var origin = new GeodeticPosition(0, 0);
        var estimator = new TrackEstimator(origin, positionUncertaintyMetres: 30, Start);

        estimator.UpdatePosition(Start.AddSeconds(100), origin, positionUncertaintyMetres: 10);
        var afterOnTime = estimator.Estimate;

        // A different source's report measured earlier than the estimate's current time.
        var elsewhere = Geodesy.Project(origin, 90, 20);
        estimator.UpdatePosition(Start.AddSeconds(40), elsewhere, positionUncertaintyMetres: 10);
        var afterLate = estimator.Estimate;

        Assert.Equal(afterOnTime.AtUtc, afterLate.AtUtc);
        Assert.True(double.IsFinite(afterLate.Position.LatitudeDegrees) && double.IsFinite(afterLate.Position.LongitudeDegrees));
        Assert.True(afterLate.PositionUncertaintyMetres < afterOnTime.PositionUncertaintyMetres,
            "A late report should still tighten the estimate even though it was measured earlier than the current state.");

        var later = estimator.Predict(Start.AddSeconds(200));
        Assert.True(double.IsFinite(later.Position.LatitudeDegrees) && double.IsFinite(later.Position.LongitudeDegrees));
        Assert.True(later.PositionUncertaintyMetres > afterLate.PositionUncertaintyMetres,
            "The filter should keep predicting normally (growing uncertainty) after a late report.");
    }

    [Fact]
    public void Estimate_StaysAccurate_AsTheTrackDriftsWellBeyondTheFramesRebaseDistance()
    {
        var position = new GeodeticPosition(10.0, 20.0);
        var estimator = new TrackEstimator(position, positionUncertaintyMetres: 10, Start);

        // 50 legs of 1 km each, due east: 50 km total, crossing the ~20 km rebase distance (21.7) more than once.
        var time = Start;
        for (var leg = 1; leg <= 50; leg++)
        {
            position = Geodesy.Project(position, 90, 1_000);
            time = time.AddSeconds(leg * 10);
            estimator.UpdatePosition(time, position, positionUncertaintyMetres: 10);
        }

        var distance = DistanceMetres(position, estimator.Estimate.Position);
        Assert.True(distance < 50, $"The estimate drifted {distance} m from the true track 50 km from its starting origin.");
    }

    [Fact]
    public void UpdateBearing_WithAnActiveRangedContribution_NudgesThePositionTowardTheBearingLine()
    {
        var ownShip = new GeodeticPosition(0, 0);
        // The prior sits on the same range ring but 5 degrees off the bearing that is about to be measured.
        var priorPosition = Geodesy.Project(ownShip, 40, 2_000);
        var estimator = new TrackEstimator(priorPosition, positionUncertaintyMetres: 150, Start);

        estimator.UpdateBearing(Start.AddSeconds(1), ownShip, trueBearingDegrees: 45,
            bearingUncertaintyDegrees: 2, hasActiveRangedContribution: true);

        var priorBearing = TrueBearingDegrees(ownShip, priorPosition);
        var updatedBearing = TrueBearingDegrees(ownShip, estimator.Estimate.Position);
        Assert.True(Math.Abs(updatedBearing - 45) < Math.Abs(priorBearing - 45),
            $"The bearing update should move the estimate's bearing from {priorBearing} closer to the measured 45 degrees, not to {updatedBearing}.");
        Assert.Equal(45, estimator.Estimate.BearingDegrees);
        Assert.Equal(ownShip, estimator.Estimate.BearingOwnShipPosition);
    }

    [Fact]
    public void UpdateBearing_WithoutAnActiveRangedContribution_RecordsTheBearingButNeverMovesThePosition()
    {
        var ownShip = new GeodeticPosition(0, 0);
        var priorPosition = Geodesy.Project(ownShip, 40, 2_000);
        var estimator = new TrackEstimator(priorPosition, positionUncertaintyMetres: 150, Start);

        estimator.UpdateBearing(Start.AddSeconds(1), ownShip, trueBearingDegrees: 45,
            bearingUncertaintyDegrees: 2, hasActiveRangedContribution: false);

        var distance = DistanceMetres(priorPosition, estimator.Estimate.Position);
        Assert.True(distance < 0.01, $"A bearing without an active ranged contribution must not move the point (moved {distance} m).");
        Assert.Equal(45, estimator.Estimate.BearingDegrees);
        Assert.Equal(ownShip, estimator.Estimate.BearingOwnShipPosition);
    }

    [Fact]
    public void Predict_WithoutMeasurements_GrowsPositionUncertaintyMonotonically_ForHybridExtrapolation()
    {
        var position = new GeodeticPosition(0, 0);
        var estimator = new TrackEstimator(position, positionUncertaintyMetres: 10, Start);
        // Several consistent reports of a target actually moving at 5 m/s east, as a real Hybrid track would
        // have had before losing its range, so the fused velocity is close to converged before extrapolation is
        // checked against dead reckoning.
        var time = Start;
        for (var i = 1; i <= 10; i++)
        {
            position = Geodesy.Project(position, 90, 5);
            time = Start.AddSeconds(i);
            estimator.UpdatePosition(time, position, positionUncertaintyMetres: 10);
            estimator.UpdateVelocity(time, speedMetresPerSecond: 5, courseDegrees: 90, velocityUncertaintyMetresPerSecond: 1);
        }

        var after10 = estimator.Predict(time.AddSeconds(10));
        var after100 = estimator.Predict(time.AddSeconds(100));
        var after1000 = estimator.Predict(time.AddSeconds(1000));

        Assert.True(after10.PositionUncertaintyMetres < after100.PositionUncertaintyMetres);
        Assert.True(after100.PositionUncertaintyMetres < after1000.PositionUncertaintyMetres);

        // It keeps moving along the known course and speed while unrefreshed, like a Hybrid track's point (R36).
        var expected = Geodesy.Project(position, 90, 5 * 1000);
        var drift = DistanceMetres(expected, after1000.Position);
        Assert.True(drift < 50, $"The extrapolated point drifted {drift} m from the expected dead-reckoned position.");
    }

    [Fact]
    public void Predict_ReturnsNothingNewWhenCalledAtOrBeforeTheCurrentTime_NeverRollingBackward()
    {
        var position = new GeodeticPosition(0, 0);
        var estimator = new TrackEstimator(position, positionUncertaintyMetres: 10, Start);
        estimator.UpdatePosition(Start.AddSeconds(100), position, positionUncertaintyMetres: 10);
        var atOneHundred = estimator.Estimate;

        var predictedEarlier = estimator.Predict(Start.AddSeconds(50));
        Assert.Equal(atOneHundred.AtUtc, predictedEarlier.AtUtc);
        Assert.Equal(atOneHundred.PositionUncertaintyMetres, predictedEarlier.PositionUncertaintyMetres, 6);
    }
}
