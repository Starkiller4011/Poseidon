namespace Poseidon.Core.Modules;

// Spherical-earth helpers: adequate for civilian radar and sonar ranges, not for geodetic survey work.
public static class Geodesy
{
    public const double MeanEarthRadiusMetres = 6_371_008.8;

    // The point reached by travelling distanceMetres from origin along a true bearing.
    public static GeodeticPosition Project(GeodeticPosition origin, double trueBearingDegrees, double distanceMetres)
    {
        var angular = distanceMetres / MeanEarthRadiusMetres;
        var bearing = ToRadians(trueBearingDegrees);
        var latitude = ToRadians(origin.LatitudeDegrees);
        var longitude = ToRadians(origin.LongitudeDegrees);

        var sinLatitude = Math.Sin(latitude) * Math.Cos(angular) + Math.Cos(latitude) * Math.Sin(angular) * Math.Cos(bearing);
        var projectedLatitude = Math.Asin(Math.Clamp(sinLatitude, -1, 1));
        var projectedLongitude = longitude + Math.Atan2(
            Math.Sin(bearing) * Math.Sin(angular) * Math.Cos(latitude),
            Math.Cos(angular) - Math.Sin(latitude) * Math.Sin(projectedLatitude));

        var degrees = projectedLongitude * 180 / Math.PI;
        degrees = (degrees + 540) % 360 - 180;
        return new GeodeticPosition(projectedLatitude * 180 / Math.PI, degrees);
    }

    // Resolves the sensor's measurement to a position. Fails when the pose, range or bearing needed is missing,
    // including a relative bearing without a heading, so a position is never invented.
    public static bool TryProject(SensorPose? pose, double? rangeMetres, double? bearingDegrees,
        BearingReference reference, out GeodeticPosition position)
    {
        position = new GeodeticPosition(0, 0);
        if (pose is null || rangeMetres is not { } range || bearingDegrees is not { } bearing ||
            !double.IsFinite(range) || range < 0 || !IsBearing(bearing) ||
            !double.IsFinite(pose.Position.LatitudeDegrees) || Math.Abs(pose.Position.LatitudeDegrees) > 90 ||
            !double.IsFinite(pose.Position.LongitudeDegrees) || Math.Abs(pose.Position.LongitudeDegrees) > 180)
            return false;

        var trueBearing = bearing;
        if (reference == BearingReference.Relative)
        {
            if (pose.HeadingTrueDegrees is not { } heading || !IsBearing(heading))
                return false;
            trueBearing = (bearing + heading) % 360;
        }
        position = Project(pose.Position, trueBearing, range);
        return true;
    }

    private static bool IsBearing(double value) => double.IsFinite(value) && value >= 0 && value < 360;

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;
}
