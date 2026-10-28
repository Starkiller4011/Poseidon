using Poseidon.Core.Modules;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Poseidon.Modules.Ais;

// Converts the AIS wire encoding to a normalized observation: SI units, true bearings and explicit unknowns.
internal static class AisNormalizer
{
    private const double KnotsToMetresPerSecond = 1852.0 / 3600;
    private const double SpeedNotAvailable = 102.3;
    private const double CourseNotAvailable = 360;
    private const int HeadingNotAvailable = 511;

    public static bool TryNormalize(AisPositionReport report,
        [NotNullWhen(true)] out TrackObservation? observation, [NotNullWhen(false)] out string? problem)
    {
        observation = null;
        if (report.Mmsi is < 1 or > 999_999_999)
        {
            problem = $"{report.Mmsi} is not a valid 9-digit MMSI.";
            return false;
        }
        // The 91/181 degree "not available" values fall outside the valid range, so a report without a position
        // cannot become a track.
        if (!double.IsFinite(report.LatitudeDegrees) || Math.Abs(report.LatitudeDegrees) > 90 ||
            !double.IsFinite(report.LongitudeDegrees) || Math.Abs(report.LongitudeDegrees) > 180)
        {
            problem = "The report has no valid position.";
            return false;
        }

        double? speed = null;
        if (report.SpeedOverGroundKnots != SpeedNotAvailable)
        {
            if (!double.IsFinite(report.SpeedOverGroundKnots) || report.SpeedOverGroundKnots is < 0 or > 102.2)
            {
                problem = $"Speed over ground {report.SpeedOverGroundKnots} knots is invalid.";
                return false;
            }
            speed = report.SpeedOverGroundKnots * KnotsToMetresPerSecond;
        }

        double? course = null;
        if (report.CourseOverGroundDegrees != CourseNotAvailable)
        {
            if (!double.IsFinite(report.CourseOverGroundDegrees) || report.CourseOverGroundDegrees is < 0 or >= 360)
            {
                problem = $"Course over ground {report.CourseOverGroundDegrees} degrees is invalid.";
                return false;
            }
            course = report.CourseOverGroundDegrees;
        }

        double? heading = null;
        if (report.TrueHeadingDegrees != HeadingNotAvailable)
        {
            if (report.TrueHeadingDegrees is < 0 or >= 360)
            {
                problem = $"True heading {report.TrueHeadingDegrees} degrees is invalid.";
                return false;
            }
            heading = report.TrueHeadingDegrees;
        }

        // AIS pads names with '@'.
        var name = report.ShipName?.Trim().TrimEnd('@').Trim();
        var candidate = new TrackObservation
        {
            SourceId = AisModule.SourceId,
            SourceTargetId = report.Mmsi.ToString(CultureInfo.InvariantCulture),
            MeasuredUtc = report.MeasuredUtc,
            Name = string.IsNullOrEmpty(name) ? null : name,
            Position = new GeodeticPosition(report.LatitudeDegrees, report.LongitudeDegrees),
            SpeedOverGroundMetresPerSecond = speed,
            CourseOverGroundDegrees = course,
            HeadingDegrees = heading
        };
        problem = TrackObservationRules.Validate(candidate);
        if (problem is not null)
            return false;
        observation = candidate;
        return true;
    }
}
