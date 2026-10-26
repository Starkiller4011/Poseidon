using System.Text.Json;
using System.Text.Json.Nodes;
using Poseidon.Core.Modules;
using Xunit;

namespace Core.Tests;

public sealed partial class ModuleRuntimeTests
{
    private static readonly ExtensionPoint Layers = new() { Id = "chart.layer", Version = "1.0.0" };

    private static void EditManifest(RuntimeTest test, string folder, Func<ModuleManifest, ModuleManifest> change)
    {
        var path = Path.Combine(test.Root, folder, "module.json");
        var manifest = JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(path))!;
        File.WriteAllText(path, JsonSerializer.Serialize(change(manifest)));
    }

    private static void ProvidePoint(RuntimeTest test, string folder, ExtensionPoint point)
        => EditManifest(test, folder, m => m with { Provides = [point] });

    private static void Contribute(RuntimeTest test, string folder, string point, string? version = null)
        => EditManifest(test, folder, m => m with
        {
            Contributions = [m.Contributions[0] with { Point = point, Version = version ?? m.Contributions[0].Version }]
        });

    [Fact]
    public async Task ContributionToUnprovidedPoint_IsBlockedWithoutAffectingOthers()
    {
        await using var test = new RuntimeTest();
        test.Package("lonely", "lonely", feature: "lonely.feature");
        Contribute(test, "lonely", "nobody.provides");
        test.Package("other", "other", feature: "test.other");
        Assert.False(await test.Runtime.ReloadAsync());
        var lonely = test.State("lonely");
        Assert.Equal(PackageState.Blocked, lonely.State);
        Assert.Contains("nobody.provides", lonely.Detail);
        Assert.StartsWith("other:", await test.Read("test.other"));
    }

    [Fact]
    public async Task ModuleProvidedPoint_StartsProviderBeforeContributorsAndReportsThePoint()
    {
        await using var test = new RuntimeTest();
        var log = Path.Combine(test.Root, "lifecycle.log");
        test.Package("z-provider", "provider", feature: "provider.feature");
        ProvidePoint(test, "z-provider", Layers);
        test.Package("a-user", "user", feature: "user.feature");
        Contribute(test, "a-user", Layers.Id);
        ConfigureFixture(test, "z-provider", "EventLog", JsonValue.Create(log)!);
        ConfigureFixture(test, "a-user", "EventLog", JsonValue.Create(log)!);
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.Equal(["z-provider:start", "a-user:start"], File.ReadAllLines(log));
        Assert.Contains(test.Runtime.Status.Features, f => f.FeatureId == "user.feature" && f.PointId == Layers.Id);
        Assert.True(test.Runtime.Status.HasContribution(Layers.Id));
    }

    [Fact]
    public async Task RemovingPointProvider_BlocksItsContributors()
    {
        await using var test = new RuntimeTest();
        test.Package("provider", "provider", feature: "provider.feature");
        ProvidePoint(test, "provider", Layers);
        test.Package("user", "user", feature: "user.feature");
        Contribute(test, "user", Layers.Id);
        Assert.True(await test.Runtime.ReloadAsync());
        test.Remove("provider");
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Equal(PackageState.Blocked, test.State("user").State);
        Assert.False(test.Runtime.Status.HasContribution(Layers.Id));
    }

    [Fact]
    public async Task SinglePoint_RejectsDifferentFeaturesButAllowsAnOverrideOfTheSameFeature()
    {
        await using var test = new RuntimeTest();
        var single = Layers with { Cardinality = PointCardinality.Single };
        test.Package("provider", "provider", feature: "provider.feature");
        ProvidePoint(test, "provider", single);
        test.Package("base", "base", feature: "user.feature");
        Contribute(test, "base", single.Id);
        test.Package("patch", "patch", replaces: true, feature: "user.feature");
        Contribute(test, "patch", single.Id);
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.StartsWith("patch:", await test.Read("user.feature"));

        test.Package("second", "second", feature: "second.feature");
        Contribute(test, "second", single.Id);
        Assert.False(await test.Runtime.ReloadAsync());
        foreach (var folder in new[] { "base", "patch", "second" })
        {
            var state = test.State(folder);
            Assert.Equal(PackageState.Failed, state.State);
            Assert.Contains("accepts a single contribution", state.Detail);
        }
    }

    [Fact]
    public async Task ContributionTargetingAnIncompatiblePointVersion_IsBlocked()
    {
        await using var test = new RuntimeTest();
        test.Package("provider", "provider", feature: "provider.feature");
        ProvidePoint(test, "provider", Layers);
        test.Package("user", "user", feature: "user.feature");
        Contribute(test, "user", Layers.Id, version: "2.0.0");
        Assert.False(await test.Runtime.ReloadAsync());
        var user = test.State("user");
        Assert.Equal(PackageState.Blocked, user.State);
        Assert.Contains("1.0.0", user.Detail);
        Assert.Equal(PackageState.Active, test.State("provider").State);
    }

    [Fact]
    public async Task ContributionNotImplementingThePointContract_IsFailed()
    {
        await using var test = new RuntimeTest();
        test.Package("provider", "provider", feature: "provider.feature");
        ProvidePoint(test, "provider", Layers with { Contract = typeof(IShell).FullName });
        test.Package("user", "user", feature: "user.feature");
        Contribute(test, "user", Layers.Id);
        Assert.False(await test.Runtime.ReloadAsync());
        var user = test.State("user");
        Assert.Equal(PackageState.Failed, user.State);
        Assert.Contains(typeof(IShell).FullName!, user.Detail);
    }

    [Theory]
    [InlineData("module")]
    [InlineData("host")]
    public async Task DuplicatePointDeclarations_FailEveryClaimant(string kind)
    {
        await using var test = new RuntimeTest();
        var point = kind == "host" ? Layers with { Id = ExtensionPoints.Shell } : Layers;
        test.Package("first", "first", feature: "first.feature");
        ProvidePoint(test, "first", point);
        if (kind == "module")
        {
            test.Package("second", "second", feature: "second.feature");
            ProvidePoint(test, "second", point);
        }
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Equal(PackageState.Failed, test.State("first").State);
        Assert.Contains("declared more than once", test.State("first").Detail);
        if (kind == "module")
            Assert.Equal(PackageState.Failed, test.State("second").State);
    }

    [Fact]
    public async Task FeatureContributedToDifferentPoints_IsFailed()
    {
        await using var test = new RuntimeTest();
        test.Package("provider", "provider", feature: "provider.feature");
        ProvidePoint(test, "provider", Layers);
        test.Package("base", "base");
        test.Package("patch", "patch", replaces: true);
        Contribute(test, "patch", Layers.Id);
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Contains("different extension points", test.State("patch").Detail);
    }

    [Fact]
    public async Task ReplacementWithoutAnOriginal_IsBlocked()
    {
        await using var test = new RuntimeTest();
        test.Package("patch", "patch", replaces: true);
        Assert.False(await test.Runtime.ReloadAsync());
        var patch = test.State("patch");
        Assert.Equal(PackageState.Blocked, patch.State);
        Assert.Contains("test.text", patch.Detail);
    }

    [Fact]
    public async Task EqualPriorityReplacements_AreAmbiguousButALowerPriorityOneIsOutranked()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "base");
        test.Package("low", "low", replaces: true, priority: 1);
        test.Package("a", "first", replaces: true, priority: 5);
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.Equal("first:1", await test.Read());

        test.Package("b", "second", replaces: true, priority: 5);
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Contains(test.Runtime.Status.Diagnostics, d => d.Contains("Ambiguous override"));
        // The ambiguous leaders are excluded, so the next-ranked replacement takes over.
        Assert.Equal("low:1", await test.Read());
    }
}
