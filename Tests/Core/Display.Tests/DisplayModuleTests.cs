using Poseidon.Core.Modules;
using Poseidon.Modules.Display;
using Poseidon.Tests.Shared;
using Xunit;

namespace Display.Tests;

public sealed class DisplayModuleTests
{
    [Fact]
    public void DisplayModule_IsIndependentOfHostAndChartImplementation()
    {
        IModule module = new DisplayModule();
        Assert.Equal("Core.Display", ModuleDeclarationTests.DeployedManifest().Id);
        Assert.DoesNotContain(module.GetType().Assembly.GetReferencedAssemblies(),
            a => a.Name is "Core" or "CoreCharts" or "CoreUI");
    }
}