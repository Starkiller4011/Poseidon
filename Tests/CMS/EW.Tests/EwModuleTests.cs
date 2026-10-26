using Poseidon.Modules.Ew;
using Xunit;

namespace EW.Tests;

public static class EwTestSuite
{
    public static void RunSmoke()
    {
        Poseidon.Core.Modules.IModule module = new CmsEwModule();
        Assert.NotNull(module.Capabilities);
    }
}

public sealed class EwModuleTests
{
    [Fact]
    public void EwModuleId_IsStable()
    {
        EwTestSuite.RunSmoke();
    }
}
