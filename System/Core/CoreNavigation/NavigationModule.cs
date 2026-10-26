using Poseidon.Core.Modules;

namespace Poseidon.Modules.Navigation;

public sealed class NavigationModule : IModule
{
    public IReadOnlyList<ModuleCapability> Capabilities =>
    [
        new ModuleCapability("cms.navigation.activity", "Navigation Activity")
    ];
}
