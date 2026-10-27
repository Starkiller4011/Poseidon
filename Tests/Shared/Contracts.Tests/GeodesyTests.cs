using Poseidon.Core.Modules;
using Xunit;

namespace Contracts.Tests;

public sealed class GeodesyTests
{
    private const double OneDegreeMetres = Geodesy.MeanEarthRadiusMetres * Math.PI / 180;

    [Theory]
    [InlineData(0, 0, 0, 1, 1, 0)]
    [InlineData(0, 0, 90, 1, 0, 1)]
    [InlineData(10, 20, 180, 1, 9, 20)]
    [InlineData(0, 0, 270, 1, 0, -1)]
    public void Project_MovesOneDegreeOfArcAlongTheBearing(
        double latitude, double longitude, double bearing, double degrees, double expectedLatitude, double expectedLongitude)
    {
        var result = Geodesy.Project(new GeodeticPosition(latitude, longitude), bearing, degrees * OneDegreeMetres);
        Assert.Equal(expectedLatitude, result.LatitudeDegrees, 6);
        Assert.Equal(expectedLongitude, result.LongitudeDegrees, 6);
    }

    [Fact]
    public void Project_WrapsLongitudeAcrossTheAntimeridian()
    {
        var result = Geodesy.Project(new GeodeticPosition(0, 179.5), 90, OneDegreeMetres);
        Assert.Equal(-179.5, result.LongitudeDegrees, 6);
    }

    [Fact]
    public void Project_ByZeroDistanceReturnsTheOrigin()
    {
        var result = Geodesy.Project(new GeodeticPosition(25.25, 55.5), 123, 0);
        Assert.Equal(25.25, result.LatitudeDegrees, 9);
        Assert.Equal(55.5, result.LongitudeDegrees, 9);
    }

    [Fact]
    public void TryProject_UsesATrueBearingDirectly_AndAddsTheHeadingToARelativeBearing()
    {
        var pose = new SensorPose(new GeodeticPosition(0, 0), 90);
        Assert.True(Geodesy.TryProject(pose, OneDegreeMetres, 0, BearingReference.True, out var north));
        Assert.Equal(1, north.LatitudeDegrees, 6);

        // Dead ahead of a vessel heading east is east.
        Assert.True(Geodesy.TryProject(pose, OneDegreeMetres, 0, BearingReference.Relative, out var east));
        Assert.Equal(0, east.LatitudeDegrees, 6);
        Assert.Equal(1, east.LongitudeDegrees, 6);

        // The sum wraps past 360 degrees.
        Assert.True(Geodesy.TryProject(pose, OneDegreeMetres, 300, BearingReference.Relative, out var wrapped));
        Assert.Equal(Math.Cos(30 * Math.PI / 180), wrapped.LatitudeDegrees, 3);
    }

    [Fact]
    public void TryProject_FailsWithoutEnoughInformation_InsteadOfInventingAPosition()
    {
        var withHeading = new SensorPose(new GeodeticPosition(0, 0), 0);
        var withoutHeading = new SensorPose(new GeodeticPosition(0, 0), null);
        Assert.False(Geodesy.TryProject(null, 1000, 10, BearingReference.True, out _));
        Assert.False(Geodesy.TryProject(withHeading, null, 10, BearingReference.True, out _));
        Assert.False(Geodesy.TryProject(withHeading, 1000, null, BearingReference.True, out _));
        Assert.False(Geodesy.TryProject(withoutHeading, 1000, 10, BearingReference.Relative, out _));
        Assert.True(Geodesy.TryProject(withoutHeading, 1000, 10, BearingReference.True, out _));
    }
}
