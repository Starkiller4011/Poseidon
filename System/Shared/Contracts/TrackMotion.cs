namespace Poseidon.Core.Modules;

public static class TrackMotion
{
    // Extrapolates a track's position to a later time along its fused course and speed. Returns null when the
    // track has no position (a bearing track) or its velocity is not known.
    public static GeodeticPosition? Predict(Track track, DateTimeOffset atUtc)
    {
        if (track.Position is not { } position ||
            track.SpeedOverGroundMetresPerSecond is not { } speed || track.CourseOverGroundDegrees is not { } course)
            return null;

        var distance = speed * (atUtc - track.MeasuredUtc).TotalSeconds;
        return distance == 0 ? position : Geodesy.Project(position, course, distance);
    }
}
