using Poseidon.Core.Modules;
using Poseidon.Modules.Ais;
using Poseidon.Tests.Shared;
using Xunit;

namespace CoreAIS.Tests;

public static class CoreAISTestSuite
{
    // Identity lives in module.json, so the cross-module smoke check only verifies the entry point.
    public static void RunSmoke()
    {
        IModule module = new AisModule();
        Assert.All(module.Capabilities, c => Assert.False(string.IsNullOrWhiteSpace(c.CapabilityId)));
    }
}

public sealed class CoreAISModuleTests
{
    [Fact]
    public void EntryPoint_IsConstructible() => CoreAISTestSuite.RunSmoke();

    [Fact]
    public void Manifest_DeclaresWellKnownId()
        => Assert.Equal("Core.AIS", ModuleDeclarationTests.DeployedManifest().Id);
}
