using Poseidon.Core.Modules;

namespace Poseidon.Modules.Tracks;

// The estimator's current output: the fused position and its uncertainty, the fused velocity once it is known,
// and the latest bearing contribution if any (R34 to R36). AtUtc is the time the state was last advanced to.
internal sealed record TrackEstimate(
    DateTimeOffset AtUtc,
    GeodeticPosition Position,
    double PositionUncertaintyMetres,
    double? SpeedOverGroundMetresPerSecond,
    double? CourseOverGroundDegrees,
    double? VelocityUncertaintyMetresPerSecond,
    double? BearingDegrees,
    double? BearingUncertaintyDegrees,
    GeodeticPosition? BearingOwnShipPosition,
    DateTimeOffset? BearingMeasuredUtc);

// One track's constant-velocity Kalman filter in a local east/north frame (m1-e-track-fusion.md section 21,
// "Estimate"). State is [east, north, veast, vnorth] with a full 4x4 covariance, so a bearing measurement can
// move both axes from one scalar update; position and velocity measurements are applied as sequential scalar
// updates (north, then east) rather than one joint vector update. A bearing measurement is always recorded, but
// only refines the fused position while the caller says the track has an active ranged contribution (R35);
// otherwise it keeps the track's bearing current without moving its point. TrackEstimator always holds a
// position: a purely bearing-only track (R33) has none yet and is not built from this type until a ranged report
// arrives (slice 3). The frame re-anchors when the estimate drifts about 20 km from its origin (21.7), invisibly
// to callers.
internal sealed class TrackEstimator
{
    private const double RebaseDistanceMetres = 20_000;

    private readonly double processNoiseAcceleration;
    private GeodeticPosition origin;
    private DateTimeOffset stateUtc;
    // [east, north, veast, vnorth]
    private readonly double[] x = new double[4];
    private readonly double[,] p = new double[4, 4];

    private double? bearingDegrees;
    private double? bearingUncertainty;
    private GeodeticPosition? bearingOwnShipPosition;
    private DateTimeOffset? bearingMeasuredUtc;

    public TrackEstimator(
        GeodeticPosition initialPosition, double positionUncertaintyMetres, DateTimeOffset atUtc,
        double initialVelocityUncertaintyMetresPerSecond = 10,
        double processNoiseAccelerationMetresPerSecondSquared = 0.3)
    {
        origin = initialPosition;
        stateUtc = atUtc;
        processNoiseAcceleration = processNoiseAccelerationMetresPerSecondSquared;
        p[0, 0] = positionUncertaintyMetres * positionUncertaintyMetres;
        p[1, 1] = positionUncertaintyMetres * positionUncertaintyMetres;
        p[2, 2] = initialVelocityUncertaintyMetresPerSecond * initialVelocityUncertaintyMetresPerSecond;
        p[3, 3] = initialVelocityUncertaintyMetresPerSecond * initialVelocityUncertaintyMetresPerSecond;
    }

    public TrackEstimate Estimate => BuildEstimate();

    // Time-updates to atUtc (never backward) and returns the resulting estimate; used for hybrid extrapolation
    // and internally before every measurement update (R9, R36).
    public TrackEstimate Predict(DateTimeOffset atUtc)
    {
        AdvanceTo(atUtc);
        return BuildEstimate();
    }

    // R8, R9: advances to the later of measuredUtc and the current state time (a late report never rolls the
    // filter backward), then applies the position as sequential scalar updates (north, then east).
    public void UpdatePosition(DateTimeOffset measuredUtc, GeodeticPosition position, double positionUncertaintyMetres)
    {
        AdvanceTo(measuredUtc);
        var (east, north) = ToLocal(position);
        var r = positionUncertaintyMetres * positionUncertaintyMetres;
        ApplyScalarUpdate([0, 1, 0, 0], north - x[1], r);
        ApplyScalarUpdate([1, 0, 0, 0], east - x[0], r);
    }

    // As above, then applies speed/course as sequential scalar velocity updates (north, then east).
    public void UpdateVelocity(
        DateTimeOffset measuredUtc, double speedMetresPerSecond, double courseDegrees, double velocityUncertaintyMetresPerSecond)
    {
        AdvanceTo(measuredUtc);
        var courseRadians = courseDegrees * Math.PI / 180;
        var east = speedMetresPerSecond * Math.Sin(courseRadians);
        var north = speedMetresPerSecond * Math.Cos(courseRadians);
        var r = velocityUncertaintyMetresPerSecond * velocityUncertaintyMetresPerSecond;
        ApplyScalarUpdate([0, 0, 0, 1], north - x[3], r);
        ApplyScalarUpdate([0, 0, 1, 0], east - x[2], r);
    }

    // R34, R35: always records the latest bearing; refines the position by one scalar update, linearized at the
    // current predicted position, only when hasActiveRangedContribution is true (the AIS-only nuance of R35 is
    // the manager's decision, not the estimator's).
    public void UpdateBearing(
        DateTimeOffset measuredUtc, GeodeticPosition ownShipPosition, double trueBearingDegrees,
        double bearingUncertaintyDegrees, bool hasActiveRangedContribution)
    {
        AdvanceTo(measuredUtc);
        if (hasActiveRangedContribution)
        {
            var (ownEast, ownNorth) = ToLocal(ownShipPosition);
            var dx = x[0] - ownEast;
            var dy = x[1] - ownNorth;
            var rangeSquared = dx * dx + dy * dy;
            if (rangeSquared > 1e-6)
            {
                var predictedBearing = Math.Atan2(dx, dy);
                var measuredBearing = trueBearingDegrees * Math.PI / 180;
                var innovation = WrapToPi(measuredBearing - predictedBearing);
                var r = Math.Pow(bearingUncertaintyDegrees * Math.PI / 180, 2);
                ApplyScalarUpdate([dy / rangeSquared, -dx / rangeSquared, 0, 0], innovation, r);
            }
        }

        this.bearingDegrees = ((trueBearingDegrees % 360) + 360) % 360;
        bearingUncertainty = bearingUncertaintyDegrees;
        bearingOwnShipPosition = ownShipPosition;
        bearingMeasuredUtc = measuredUtc;
    }

    private void AdvanceTo(DateTimeOffset atUtc)
    {
        var dt = (atUtc - stateUtc).TotalSeconds;
        if (dt <= 0)
            return;

        var pe = x[0] + dt * x[2];
        var pn = x[1] + dt * x[3];
        x[0] = pe;
        x[1] = pn;

        var f = new double[4, 4]
        {
            { 1, 0, dt, 0 },
            { 0, 1, 0, dt },
            { 0, 0, 1, 0 },
            { 0, 0, 0, 1 }
        };
        var q = processNoiseAcceleration * processNoiseAcceleration;
        var noise = new double[4, 4];
        noise[0, 0] = q * dt * dt * dt / 3;
        noise[0, 2] = noise[2, 0] = q * dt * dt / 2;
        noise[2, 2] = q * dt;
        noise[1, 1] = q * dt * dt * dt / 3;
        noise[1, 3] = noise[3, 1] = q * dt * dt / 2;
        noise[3, 3] = q * dt;

        var fp = Multiply(f, p);
        var fpft = Multiply(fp, Transpose(f));
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                p[i, j] = fpft[i, j] + noise[i, j];

        stateUtc = atUtc;

        var radius = Math.Sqrt(x[0] * x[0] + x[1] * x[1]);
        if (radius > RebaseDistanceMetres)
        {
            var newOrigin = ToGeodetic(x[0], x[1]);
            origin = newOrigin;
            x[0] = 0;
            x[1] = 0;
        }
    }

    // A single scalar Kalman update: h is the measurement's row vector into [east, north, veast, vnorth],
    // innovation is the measurement minus its prediction (already angle-wrapped for a bearing), r is the
    // measurement's variance.
    private void ApplyScalarUpdate(double[] h, double innovation, double r)
    {
        var ph = new double[4];
        for (var i = 0; i < 4; i++)
        {
            double sum = 0;
            for (var j = 0; j < 4; j++)
                sum += p[i, j] * h[j];
            ph[i] = sum;
        }

        double s = r;
        for (var i = 0; i < 4; i++)
            s += h[i] * ph[i];

        var k = new double[4];
        for (var i = 0; i < 4; i++)
            k[i] = ph[i] / s;

        for (var i = 0; i < 4; i++)
            x[i] += k[i] * innovation;

        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                p[i, j] -= k[i] * ph[j];

        // Floating-point error can break symmetry very slightly; restore it.
        for (var i = 0; i < 4; i++)
            for (var j = i + 1; j < 4; j++)
            {
                var average = (p[i, j] + p[j, i]) / 2;
                p[i, j] = average;
                p[j, i] = average;
            }
    }

    private TrackEstimate BuildEstimate()
    {
        var position = ToGeodetic(x[0], x[1]);
        var positionUncertainty = Math.Sqrt(Math.Max(0, p[0, 0] + p[1, 1]));
        var speed = Math.Sqrt(x[2] * x[2] + x[3] * x[3]);
        var course = (Math.Atan2(x[2], x[3]) * 180 / Math.PI + 360) % 360;
        var velocityUncertainty = Math.Sqrt(Math.Max(0, p[2, 2] + p[3, 3]));
        return new TrackEstimate(
            stateUtc, position, positionUncertainty, speed, course, velocityUncertainty,
            bearingDegrees, bearingUncertainty, bearingOwnShipPosition, bearingMeasuredUtc);
    }

    private (double East, double North) ToLocal(GeodeticPosition position)
    {
        var (metresPerDegreeLatitude, metresPerDegreeLongitude) = DegreeScale();
        var longitudeDiff = ((position.LongitudeDegrees - origin.LongitudeDegrees + 540) % 360) - 180;
        var east = longitudeDiff * metresPerDegreeLongitude;
        var north = (position.LatitudeDegrees - origin.LatitudeDegrees) * metresPerDegreeLatitude;
        return (east, north);
    }

    private GeodeticPosition ToGeodetic(double east, double north)
    {
        var (metresPerDegreeLatitude, metresPerDegreeLongitude) = DegreeScale();
        var latitude = origin.LatitudeDegrees + north / metresPerDegreeLatitude;
        var longitude = origin.LongitudeDegrees + east / metresPerDegreeLongitude;
        longitude = ((longitude + 540) % 360) - 180;
        return new GeodeticPosition(latitude, longitude);
    }

    // Equirectangular approximation about the current origin: adequate within the frame's rebase distance, in
    // keeping with Geodesy's own stated scope (civilian radar and sonar ranges, not geodetic survey work).
    private (double Latitude, double Longitude) DegreeScale()
    {
        var metresPerDegreeLatitude = Geodesy.MeanEarthRadiusMetres * Math.PI / 180;
        var metresPerDegreeLongitude = Geodesy.MeanEarthRadiusMetres * Math.Cos(origin.LatitudeDegrees * Math.PI / 180) * Math.PI / 180;
        return (metresPerDegreeLatitude, metresPerDegreeLongitude);
    }

    private static double WrapToPi(double radians)
    {
        while (radians > Math.PI)
            radians -= 2 * Math.PI;
        while (radians < -Math.PI)
            radians += 2 * Math.PI;
        return radians;
    }

    private static double[,] Multiply(double[,] a, double[,] b)
    {
        var result = new double[4, 4];
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
            {
                double sum = 0;
                for (var k = 0; k < 4; k++)
                    sum += a[i, k] * b[k, j];
                result[i, j] = sum;
            }
        return result;
    }

    private static double[,] Transpose(double[,] a)
    {
        var result = new double[4, 4];
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                result[j, i] = a[i, j];
        return result;
    }
}
