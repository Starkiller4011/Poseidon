using Poseidon.Core.Modules;
using Poseidon.Modules.Charts;
using Poseidon.Tests.Shared;
using Xunit;

namespace CoreCharts.Tests;

public static class CoreChartsTestSuite
{
    // Identity lives in module.json, so the cross-module smoke check only verifies the entry point.
    public static void RunSmoke()
    {
        IModule module = new ChartsModule();
        Assert.All(module.Capabilities, c => Assert.False(string.IsNullOrWhiteSpace(c.CapabilityId)));
    }
}

public sealed class CoreChartsModuleTests
{
    [Fact]
    public void EntryPoint_IsConstructible() => CoreChartsTestSuite.RunSmoke();

    [Fact]
    public void Manifest_DeclaresWellKnownId()
        => Assert.Equal("Core.Charts", ModuleDeclarationTests.DeployedManifest().Id);
}
