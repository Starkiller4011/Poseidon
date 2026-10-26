using Poseidon.Modules.Ew;
using Xunit;

namespace CmsEW.Tests;

public static class CmsEWTestSuite
{
    public static void RunSmoke()
    {
        Poseidon.Core.Modules.IModule module = new CmsEwModule();
        Assert.NotNull(module.Capabilities);
    }
}

public sealed class CmsEWModuleTests
{
    [Fact]
    public void CmsEWModuleId_IsStable()
    {
        CmsEWTestSuite.RunSmoke();
    }
}
