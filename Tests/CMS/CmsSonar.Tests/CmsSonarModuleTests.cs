using Poseidon.Modules.Sonar;
using Xunit;

namespace CmsSonar.Tests;

public static class CmsSonarTestSuite
{
    public static void RunSmoke()
    {
        Poseidon.Core.Modules.IModule module = new CmsSonarModule();
        Assert.NotNull(module.Capabilities);
    }
}

public sealed class CmsSonarModuleTests
{
    [Fact]
    public void CmsSonarModuleId_IsStable()
    {
        CmsSonarTestSuite.RunSmoke();
    }
}
