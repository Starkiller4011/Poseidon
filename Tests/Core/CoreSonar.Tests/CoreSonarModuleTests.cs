using Poseidon.Core.Modules;
using Poseidon.Modules.Sonar;
using Poseidon.Tests.Shared;
using Xunit;

namespace CoreSonar.Tests;

public sealed class CoreSonarModuleTests
{
    // Core.Sonar carries civilian sonar inputs (echo sounder, NDS); CMS.Sonar is a separate module.
    [Fact]
    public void CoreSonar_IsAnIndependentModule()
    {
        IModule module = new SonarModule();
        Assert.Equal("Core.Sonar", ModuleDeclarationTests.DeployedManifest().Id);
        Assert.DoesNotContain(module.GetType().Assembly.GetReferencedAssemblies(), a => a.Name == "Core");
    }
}