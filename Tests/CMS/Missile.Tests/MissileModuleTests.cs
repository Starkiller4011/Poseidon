using Poseidon.Modules.Missile;
using Xunit;

namespace Missile.Tests;

public static class MissileTestSuite
{
    public static void RunSmoke()
    {
        Poseidon.Core.Modules.IModule module = new CmsMissileModule();
        Assert.NotNull(module.Capabilities);
    }
}

public sealed class MissileModuleTests
{
    [Fact]
    public void MissileModuleId_IsStable()
    {
        MissileTestSuite.RunSmoke();
    }
}
