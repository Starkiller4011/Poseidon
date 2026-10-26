using Poseidon.Core.Modules;

namespace Poseidon.Modules.Gun;

public sealed class CmsGunModule : IModule
{
    public IReadOnlyList<ModuleCapability> Capabilities =>
    [
        new ModuleCapability("cms.weapon.gun-control", "Gun Control")
    ];
}
