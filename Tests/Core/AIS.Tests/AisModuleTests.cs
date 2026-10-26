using Poseidon.Core.Modules;
using Poseidon.Modules.Ais;
using Poseidon.Tests.Shared;
using Xunit;

namespace AIS.Tests;

public static class AisTestSuite
{
    // Identity lives in module.json, so the cross-module smoke check only verifies the entry point.
    public static void RunSmoke()
    {
        IModule module = new AisModule();
        Assert.All(module.Capabilities, c => Assert.False(string.IsNullOrWhiteSpace(c.CapabilityId)));
    }
}

public sealed class AisModuleTests
{
    [Fact]
    public void EntryPoint_IsConstructible() => AisTestSuite.RunSmoke();

    [Fact]
    public void Manifest_DeclaresWellKnownId()
        => Assert.Equal("Core.AIS", ModuleDeclarationTests.DeployedManifest().Id);
}
