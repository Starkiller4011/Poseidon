using Poseidon.Core.Modules;
using Poseidon.Modules.Tracks;
using Poseidon.Tests.Shared;
using Xunit;

namespace CoreTracks.Tests;

public static class CoreTracksTestSuite
{
    // Identity lives in module.json, so the cross-module smoke check only verifies the entry point.
    public static void RunSmoke()
    {
        IModule module = new TracksModule();
        Assert.All(module.Capabilities, c => Assert.False(string.IsNullOrWhiteSpace(c.CapabilityId)));
    }
}

public sealed class CoreTracksModuleTests
{
    [Fact]
    public void EntryPoint_IsConstructible() => CoreTracksTestSuite.RunSmoke();

    [Fact]
    public void Manifest_DeclaresWellKnownId()
        => Assert.Equal("Core.Tracks", ModuleDeclarationTests.DeployedManifest().Id);
}
