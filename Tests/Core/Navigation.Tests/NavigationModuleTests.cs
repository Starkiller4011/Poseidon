using Poseidon.Core.Modules;
using Poseidon.Modules.Navigation;
using Poseidon.Tests.Shared;
using Xunit;

namespace Navigation.Tests;

public static class NavigationTestSuite
{
    // Identity lives in module.json, so the cross-module smoke check only verifies the entry point.
    public static void RunSmoke()
    {
        IModule module = new NavigationModule();
        Assert.All(module.Capabilities, c => Assert.False(string.IsNullOrWhiteSpace(c.CapabilityId)));
    }
}

public sealed class NavigationModuleTests
{
    [Fact]
    public void EntryPoint_IsConstructible() => NavigationTestSuite.RunSmoke();

    [Fact]
    public void Manifest_DeclaresWellKnownId()
        => Assert.Equal("Core.Navigation", ModuleDeclarationTests.DeployedManifest().Id);
}
