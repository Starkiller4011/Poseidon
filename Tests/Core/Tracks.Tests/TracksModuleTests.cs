using Poseidon.Core.Modules;
using Poseidon.Modules.Tracks;
using Poseidon.Tests.Shared;
using Xunit;

namespace Tracks.Tests;

public static class TracksTestSuite
{
    // Identity lives in module.json, so the cross-module smoke check only verifies the entry point.
    public static void RunSmoke()
    {
        IModule module = new TracksModule();
        Assert.All(module.Capabilities, c => Assert.False(string.IsNullOrWhiteSpace(c.CapabilityId)));
    }
}

public sealed class TracksModuleTests
{
    [Fact]
    public void EntryPoint_IsConstructible() => TracksTestSuite.RunSmoke();

    [Fact]
    public void Manifest_DeclaresWellKnownId()
        => Assert.Equal("Core.Tracks", ModuleDeclarationTests.DeployedManifest().Id);
}
