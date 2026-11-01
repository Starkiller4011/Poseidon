using Poseidon.Core.Modules;
using Poseidon.Modules.CoreUI;
using Poseidon.Tests.Shared;
using Xunit;

namespace CoreUI.Tests;

public sealed class CoreUIModuleTests
{
    [Fact]
    public void UiModule_UsesSharedContractWithoutHostReference()
    {
        IModule module = new CoreUiModule();
        Assert.Equal("Core.UI", ModuleDeclarationTests.DeployedManifest().Id);
        Assert.DoesNotContain(module.GetType().Assembly.GetReferencedAssemblies(), a => a.Name == "Core");
    }

    [Fact]
    public void UiModule_ContributesAShellDeclaredInItsManifest()
    {
        IModule module = new CoreUiModule();
        var contribution = Assert.Single(ModuleDeclarationTests.DeployedManifest().Contributions);
        Assert.Equal(ExtensionPoints.Shell, contribution.Point);
        Assert.IsAssignableFrom<IShell>(module.Features[contribution.Id]);
    }

}