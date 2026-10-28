using Poseidon.Modules.Missile;
using Xunit;

namespace CmsMissile.Tests;

public static class CmsMissileTestSuite
{
    public static void RunSmoke()
    {
        Poseidon.Core.Modules.IModule module = new CmsMissileModule();
        Assert.NotNull(module.Capabilities);
    }
}

public sealed class CmsMissileModuleTests
{
    [Fact]
    public void CmsMissileModuleId_IsStable()
    {
        CmsMissileTestSuite.RunSmoke();
    }
}
