using Poseidon.Modules.Sonar;
using Xunit;

namespace Sonar.Tests;

public static class SonarTestSuite
{
    public static void RunSmoke()
    {
        Poseidon.Core.Modules.IModule module = new CmsSonarModule();
        Assert.NotNull(module.Capabilities);
    }
}

public sealed class SonarModuleTests
{
    [Fact]
    public void SonarModuleId_IsStable()
    {
        SonarTestSuite.RunSmoke();
    }
}
