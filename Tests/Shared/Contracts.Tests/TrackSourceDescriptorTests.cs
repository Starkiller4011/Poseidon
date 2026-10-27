using Poseidon.Core.Modules;
using Xunit;

namespace Contracts.Tests;

// R11: a source registers a descriptor; an unusable one is refused with a reason.
public sealed class TrackSourceDescriptorTests
{
    private static TrackSourceDescriptor Valid() => new(
        "Core.Radar",
        TrackSourceKinds.Radar,
        ExpectedUpdateInterval: TimeSpan.FromSeconds(2.5),
        StaleAfter: TimeSpan.FromSeconds(5),
        LostAfter: TimeSpan.FromSeconds(10),
        RemoveAfter: TimeSpan.FromSeconds(15),
        PositionUncertaintyMetres: 30,
        VelocityUncertaintyMetresPerSecond: 2,
        ExpectedPeakReportsPerSecond: 500);

    public static TheoryData<string, Func<TrackSourceDescriptor, TrackSourceDescriptor>> Invalid => new()
    {
        { "blank source", d => d with { SourceId = " " } },
        { "blank kind", d => d with { Kind = "" } },
        { "zero interval", d => d with { ExpectedUpdateInterval = TimeSpan.Zero } },
        { "negative interval", d => d with { ExpectedUpdateInterval = TimeSpan.FromSeconds(-1) } },
        { "zero stale", d => d with { StaleAfter = TimeSpan.Zero } },
        { "lost equal to stale", d => d with { LostAfter = d.StaleAfter } },
        { "lost before stale", d => d with { LostAfter = TimeSpan.FromSeconds(4) } },
        { "remove equal to lost", d => d with { RemoveAfter = d.LostAfter } },
        { "remove before lost", d => d with { RemoveAfter = TimeSpan.FromSeconds(9) } },
        { "zero position uncertainty", d => d with { PositionUncertaintyMetres = 0 } },
        { "NaN position uncertainty", d => d with { PositionUncertaintyMetres = double.NaN } },
        { "infinite position uncertainty", d => d with { PositionUncertaintyMetres = double.PositiveInfinity } },
        { "negative velocity uncertainty", d => d with { VelocityUncertaintyMetresPerSecond = -1 } },
        { "NaN velocity uncertainty", d => d with { VelocityUncertaintyMetresPerSecond = double.NaN } },
        { "zero peak rate", d => d with { ExpectedPeakReportsPerSecond = 0 } },
        { "zero bearing uncertainty", d => d with { BearingUncertaintyDegrees = 0 } },
        { "bearing uncertainty of 180", d => d with { BearingUncertaintyDegrees = 180 } },
        { "NaN bearing uncertainty", d => d with { BearingUncertaintyDegrees = double.NaN } },
        { "zero association range", d => d with { MaximumAssociationRangeMetres = 0 } },
        { "infinite association range", d => d with { MaximumAssociationRangeMetres = double.PositiveInfinity } }
    };

    [Fact]
    public void ValidDescriptors_AreAccepted_WithAndWithoutTheOptionalBearingValues()
    {
        Assert.Null(Valid().Validate());
        Assert.Null((Valid() with { ExpectedPeakReportsPerSecond = 1 }).Validate());
        Assert.Null((Valid() with
        {
            SourceId = "Test.Camera",
            Kind = TrackSourceKinds.Camera,
            BearingUncertaintyDegrees = 0.5,
            MaximumAssociationRangeMetres = 5000
        }).Validate());
    }

    [Fact]
    public void Kinds_AreOpenStrings_SoOtherModulesCanDeclareTheirOwn()
    {
        Assert.Null((Valid() with { Kind = "cms.ew" }).Validate());
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void InvalidDescriptors_AreRefusedWithAReason(string label, Func<TrackSourceDescriptor, TrackSourceDescriptor> mutate)
    {
        var reason = mutate(Valid()).Validate();
        Assert.False(string.IsNullOrWhiteSpace(reason), label);
    }
}
