using System.Text.Json;
using Poseidon.Core.Modules;
using Xunit;

namespace Contracts.Tests;

public sealed class ModuleContractTests
{
    [Fact]
    public async Task DefaultLifecycle_AcceptsFreshStartAndIdempotentStop()
    {
        IModule module = new MinimalModule();
        Assert.Empty(module.Features);
        await module.StartAsync(null!, null, CancellationToken.None);
        Assert.Null(await module.CaptureStateAsync(CancellationToken.None));
        await module.StopAsync(CancellationToken.None);
        await module.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DefaultLifecycle_RejectsStateItCannotMigrate()
    {
        IModule module = new MinimalModule();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            module.StartAsync(null!, new ModuleState(1, [1]), CancellationToken.None).AsTask());
    }

    [Fact]
    public void Manifest_SerializesPointsAndContributionsWithoutLoss()
    {
        var original = new ModuleManifest
        {
            Id = "patch", Version = "2.0.0", EntryAssembly = "Patch.dll",
            Dependencies = [new() { Id = "base", Version = "1.0.0" }],
            Provides = [new() { Id = "point", Version = "1.0.0", Cardinality = PointCardinality.Single, Contract = "C" }],
            Contributions = [new()
            {
                Point = "point", Id = "feature", Version = "1.0.0", Replaces = true, Priority = 2
            }]
        };
        var json = JsonSerializer.Serialize(original);
        Assert.Contains("\"Single\"", json);
        var decoded = JsonSerializer.Deserialize<ModuleManifest>(json)!;
        Assert.Equal(original.Id, decoded.Id);
        Assert.Equal(original.Version, decoded.Version);
        Assert.Equal(original.EntryAssembly, decoded.EntryAssembly);
        Assert.Equal(original.ContractVersion, decoded.ContractVersion);
        Assert.Equal(original.Dependencies, decoded.Dependencies);
        Assert.Equal(original.Provides, decoded.Provides);
        Assert.Equal(original.Contributions, decoded.Contributions);
    }

    [Fact]
    public void RuntimeStatus_ReportsWhetherAnyContributionTargetsAPoint()
    {
        var status = new RuntimeStatus(1, [], [new("feature", "module", "point")], []);
        Assert.True(status.HasContribution("point"));
        Assert.False(status.HasContribution(ExtensionPoints.Shell));
        Assert.False(new RuntimeStatus(0, [], [], []).HasContribution(ExtensionPoints.Shell));
    }

    [Fact]
    public void HostPoints_DeclareTheShellAsAContractBackedPoint()
    {
        var shell = Assert.Single(ExtensionPoints.Host, p => p.Id == ExtensionPoints.Shell);
        Assert.Equal(typeof(IShell).FullName, shell.Contract);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"Id":"module","Version":"1.0.0"}""")]
    public void Manifest_RequiresIdentityVersionAndEntryAssembly(string json)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ModuleManifest>(json));

    [Fact]
    public void PublicStatus_ReportsGenerationProvidersAndDiagnostics()
    {
        var status = new RuntimeStatus(7, [new("module", "1.0.0", "package")],
            [new("feature", "module")], ["diagnostic"])
        {
            RetainedLoadContexts = 2,
            Packages = [new("package", "module", "1.0.0", PackageState.Blocked, "Requires base 1.0.0.")]
        };
        var decoded = JsonSerializer.Deserialize<RuntimeStatus>(JsonSerializer.Serialize(status))!;
        Assert.Equal(7, decoded.Generation);
        Assert.Equal(2, decoded.RetainedLoadContexts);
        Assert.Equal(status.Modules, decoded.Modules);
        Assert.Equal(status.Features, decoded.Features);
        Assert.Equal(status.Diagnostics, decoded.Diagnostics);
        Assert.Equal(status.Packages, decoded.Packages);
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0", true)]
    [InlineData("1.2.0", "1.5.3", true)]
    [InlineData("1.2.0", "1.1.9", false)]
    [InlineData("1.2.0", "2.0.0", false)]
    [InlineData("0.0.1", "0.0.1", true)]
    public void Dependency_AcceptsSameMajorAtOrAboveMinimum(string required, string installed, bool satisfied)
        => Assert.Equal(satisfied,
            new ModuleDependency { Id = "base", Version = required }.IsSatisfiedBy(Version.Parse(installed)));

    [Fact]
    public void Contracts_DoNotReferenceAUiFramework()
        => Assert.DoesNotContain(typeof(IModule).Assembly.GetReferencedAssemblies(),
            a => a.Name!.StartsWith("Avalonia", StringComparison.Ordinal));

    [Fact]
    public void Capability_PreservesStableIdAndDisplayName()
    {
        var capability = new ModuleCapability("charts.display", "Chart Display");
        Assert.Equal("charts.display", capability.CapabilityId);
        Assert.Equal("Chart Display", capability.DisplayName);
    }

    private sealed class MinimalModule : IModule
    {
        public IReadOnlyList<ModuleCapability> Capabilities => [];
    }
}
