using Poseidon.Core.Modules;
using Poseidon.Modules.CoreUI;
using Poseidon.Tests.Shared;
using Xunit;

namespace CoreUI.Tests;

public sealed class CoreUiModuleTests
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

    [Theory]
    [InlineData(PackageState.Active, null, "Active: Core.Tracks 0.0.1")]
    [InlineData(PackageState.Blocked, "Requires X.", "Blocked: Core.Tracks 0.0.1 - Requires X.")]
    public void PackageStatus_IsFormattedWithStateAndReason(PackageState state, string? detail, string expected)
        => Assert.Equal(expected, CoreUiModule.FormatPackage(
            new PackageStatus("modules/Tracks", "Core.Tracks", "0.0.1", state, detail)));

    [Fact]
    public void UnidentifiedPackage_IsFormattedByDirectoryName()
        => Assert.Equal("Pending: Tracks - Waiting.", CoreUiModule.FormatPackage(
            new PackageStatus(Path.Combine("modules", "Tracks"), null, null, PackageState.Pending, "Waiting.")));
}