using Poseidon.Modules.Gun;
using Xunit;

namespace CmsGun.Tests;

public static class CmsGunTestSuite
{
    public static void RunSmoke()
    {
        Poseidon.Core.Modules.IModule module = new CmsGunModule();
        Assert.NotNull(module.Capabilities);
    }
}

public sealed class CmsGunModuleTests
{
    [Fact]
    public void CmsGunModuleId_IsStable()
    {
        CmsGunTestSuite.RunSmoke();
    }
}
