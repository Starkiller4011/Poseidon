using Poseidon.Modules.Comms;
using Xunit;

namespace CmsComms.Tests;

public static class CmsCommsTestSuite
{
    public static void RunSmoke()
    {
        Poseidon.Core.Modules.IModule module = new CmsCommsModule();
        Assert.NotNull(module.Capabilities);
    }
}

public sealed class CmsCommsModuleTests
{
    [Fact]
    public void CmsCommsModuleId_IsStable()
    {
        CmsCommsTestSuite.RunSmoke();
    }
}
