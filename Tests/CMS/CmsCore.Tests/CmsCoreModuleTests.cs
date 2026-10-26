using Poseidon.Modules.CmsCore;
using Xunit;

namespace CmsCore.Tests;

public static class CmsCoreTestSuite
{
    public static void RunSmoke()
    {
        Poseidon.Core.Modules.IModule module = new CmsCoreModule();
        Assert.NotNull(module.Capabilities);
    }
}

public sealed class CmsCoreModuleTests
{
    [Fact]
    public void CmsCoreModuleId_IsStable()
    {
        CmsCoreTestSuite.RunSmoke();
    }
}
