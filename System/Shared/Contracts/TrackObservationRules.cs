namespace Poseidon.Core.Modules;

public static class TrackObservationRules
{
    // Returns why an observation cannot be used, or null when it is acceptable.
    public static string? Validate(TrackObservation observation)
    {
        if (string.IsNullOrWhiteSpace(observation.SourceId))
            return "The source ID is required.";
        if (string.IsNullOrWhiteSpace(observation.SourceTargetId))
            return "A source-local target ID is required.";
        if (observation.MeasuredUtc == default)
            return "The measured time is required.";

        if (observation.Position is { } position)
        {
            if (!double.IsFinite(position.LatitudeDegrees) || Math.Abs(position.LatitudeDegrees) > 90)
                return $"Latitude {position.LatitudeDegrees} is outside [-90, 90] degrees.";
            if (!double.IsFinite(position.LongitudeDegrees) || Math.Abs(position.LongitudeDegrees) > 180)
                return $"Longitude {position.LongitudeDegrees} is outside [-180, 180] degrees.";
        }
        if (observation.PositionUncertaintyMetres is { } uncertainty && (!double.IsFinite(uncertainty) || uncertainty < 0))
            return "The position uncertainty must be a non-negative number of metres.";
        if (observation.SpeedOverGroundMetresPerSecond is { } speed && (!double.IsFinite(speed) || speed < 0))
            return "The speed over ground must be a non-negative number of metres per second.";
        if (observation.CourseOverGroundDegrees is { } course && !IsBearing(course))
            return "The course over ground must be in [0, 360) degrees.";
        if (observation.HeadingDegrees is { } heading && !IsBearing(heading))
            return "The heading must be in [0, 360) degrees.";

        if (observation.Measurement is { } measurement)
        {
            if (measurement.RangeMetres is null && measurement.BearingDegrees is null)
                return "The measurement has neither a range nor a bearing.";
            if (measurement.RangeMetres is { } range && (!double.IsFinite(range) || range < 0))
                return "The measured range must be a non-negative number of metres.";
            if (measurement.BearingDegrees is { } bearing && !IsBearing(bearing))
                return "The measured bearing must be in [0, 360) degrees.";
            if (measurement.BearingUncertaintyDegrees is { } bearingUncertainty && !IsBearingUncertainty(bearingUncertainty))
                return "The bearing uncertainty must be above 0 and below 180 degrees.";
            if (measurement.Pose is { } pose)
            {
                if (!IsPosition(pose.Position))
                    return "The own-ship position is outside the valid latitude and longitude range.";
                if (pose.HeadingTrueDegrees is { } ownHeading && !IsBearing(ownHeading))
                    return "The own-ship heading must be in [0, 360) degrees.";
            }

            // A positioned report may keep the measurement it was converted from; otherwise the measurement must
            // be placeable: a bearing, the own-ship position, and the heading for a relative bearing.
            if (observation.Position is null)
            {
                if (measurement.BearingDegrees is null)
                    return "A measurement without a position needs a bearing.";
                if (measurement.Pose is null)
                    return "A range or bearing needs the own-ship position at the time of measurement.";
                if (measurement.BearingReference == BearingReference.Relative && measurement.Pose.HeadingTrueDegrees is null)
                    return "A relative bearing needs the own-ship heading.";
            }
        }
        else if (observation.Position is null)
            return "The observation carries neither a position nor a measurement.";
        return null;
    }

    // One-sigma bearing uncertainty: positive and below a half circle.
    public static bool IsBearingUncertainty(double degrees) => double.IsFinite(degrees) && degrees > 0 && degrees < 180;

    private static bool IsBearing(double value) => double.IsFinite(value) && value >= 0 && value < 360;

    private static bool IsPosition(GeodeticPosition position)
        => double.IsFinite(position.LatitudeDegrees) && Math.Abs(position.LatitudeDegrees) <= 90 &&
           double.IsFinite(position.LongitudeDegrees) && Math.Abs(position.LongitudeDegrees) <= 180;
}
