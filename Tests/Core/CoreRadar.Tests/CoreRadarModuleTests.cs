using Poseidon.Core.Modules;
using Poseidon.Modules.Radar;
using Poseidon.Tests.Shared;
using Xunit;

namespace CoreRadar.Tests;

public static class CoreRadarTestSuite
{
    // Identity lives in module.json, so the cross-module smoke check only verifies the entry point.
    public static void RunSmoke()
    {
        IModule module = new RadarModule();
        Assert.All(module.Capabilities, c => Assert.False(string.IsNullOrWhiteSpace(c.CapabilityId)));
    }
}

public sealed class CoreRadarModuleTests
{
    [Fact]
    public void EntryPoint_IsConstructible() => CoreRadarTestSuite.RunSmoke();

    [Fact]
    public void Manifest_DeclaresWellKnownId()
        => Assert.Equal("Core.Radar", ModuleDeclarationTests.DeployedManifest().Id);
}
