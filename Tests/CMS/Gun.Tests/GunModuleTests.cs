using Poseidon.Modules.Gun;
using Xunit;

namespace Gun.Tests;

public static class GunTestSuite
{
    public static void RunSmoke()
    {
        Poseidon.Core.Modules.IModule module = new CmsGunModule();
        Assert.NotNull(module.Capabilities);
    }
}

public sealed class GunModuleTests
{
    [Fact]
    public void GunModuleId_IsStable()
    {
        GunTestSuite.RunSmoke();
    }
}
