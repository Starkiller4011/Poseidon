using Xunit;

namespace Core.Tests;

public static class CoreTestSuite
{
    public static void RunSmoke()
    {
        Assert.NotEmpty(Poseidon.Core.Modules.ExtensionPoints.Host);
    }
}

public sealed class CoreModuleTests
{
    [Fact]
    public void CoreModuleId_IsStable()
    {
        CoreTestSuite.RunSmoke();
    }
}
