using Poseidon.Core.Modules;
using Poseidon.Modules.Charts;
using Poseidon.Tests.Shared;
using Xunit;

namespace Charts.Tests;

public static class ChartsTestSuite
{
    // Identity lives in module.json, so the cross-module smoke check only verifies the entry point.
    public static void RunSmoke()
    {
        IModule module = new ChartsModule();
        Assert.All(module.Capabilities, c => Assert.False(string.IsNullOrWhiteSpace(c.CapabilityId)));
    }
}

public sealed class ChartsModuleTests
{
    [Fact]
    public void EntryPoint_IsConstructible() => ChartsTestSuite.RunSmoke();

    [Fact]
    public void Manifest_DeclaresWellKnownId()
        => Assert.Equal("Core.Charts", ModuleDeclarationTests.DeployedManifest().Id);
}
