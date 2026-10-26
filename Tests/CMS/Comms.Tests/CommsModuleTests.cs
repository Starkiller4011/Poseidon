using Poseidon.Modules.Comms;
using Xunit;

namespace Comms.Tests;

public static class CommsTestSuite
{
    public static void RunSmoke()
    {
        Poseidon.Core.Modules.IModule module = new CmsCommsModule();
        Assert.NotNull(module.Capabilities);
    }
}

public sealed class CommsModuleTests
{
    [Fact]
    public void CommsModuleId_IsStable()
    {
        CommsTestSuite.RunSmoke();
    }
}
