using Poseidon.Core.Modules;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Poseidon.Modules.Radar;

// Converts a radar target report to a normalized observation. A geographic position is derived only when the
// sensor pose (and, for relative bearings, the heading) is known; otherwise the report is rejected.
internal static class RadarNormalizer
{
    public static bool TryNormalize(RadarTargetReport report,
        [NotNullWhen(true)] out TrackObservation? observation, [NotNullWhen(false)] out string? problem)
    {
        observation = null;
        if (report.TargetNumber < 0)
        {
            problem = $"Target number {report.TargetNumber} is invalid.";
            return false;
        }
        if (!double.IsFinite(report.RangeMetres) || report.RangeMetres <= 0)
        {
            problem = $"Range {report.RangeMetres} m is invalid.";
            return false;
        }
        if (!double.IsFinite(report.BearingDegrees) || report.BearingDegrees is < 0 or >= 360)
        {
            problem = $"Bearing {report.BearingDegrees} degrees is invalid.";
            return false;
        }
        if (report.Pose is { } pose &&
            (!double.IsFinite(pose.Position.LatitudeDegrees) || Math.Abs(pose.Position.LatitudeDegrees) > 90 ||
             !double.IsFinite(pose.Position.LongitudeDegrees) || Math.Abs(pose.Position.LongitudeDegrees) > 180 ||
             pose.HeadingTrueDegrees is { } heading && (!double.IsFinite(heading) || heading is < 0 or >= 360)))
        {
            problem = "The sensor pose is invalid.";
            return false;
        }

        GeodeticPosition? position = Geodesy.TryProject(report.Pose, report.RangeMetres, report.BearingDegrees,
            report.BearingReference, out var projected) ? projected : null;
        var candidate = new TrackObservation
        {
            SourceId = RadarModule.SourceId,
            SourceTargetId = report.TargetNumber.ToString(CultureInfo.InvariantCulture),
            MeasuredUtc = report.MeasuredUtc,
            Position = position,
            PositionUncertaintyMetres = report.PositionUncertaintyMetres,
            SpeedOverGroundMetresPerSecond = report.SpeedOverGroundMetresPerSecond,
            CourseOverGroundDegrees = report.CourseOverGroundDegrees,
            Measurement = new PolarMeasurement(report.RangeMetres, report.BearingDegrees, report.BearingReference)
        };
        problem = TrackObservationRules.Validate(candidate);
        if (problem is not null)
            return false;
        observation = candidate;
        return true;
    }
}
