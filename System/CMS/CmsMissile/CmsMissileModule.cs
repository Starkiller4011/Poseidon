using Poseidon.Core.Modules;

namespace Poseidon.Modules.Missile;

public sealed class CmsMissileModule : IModule
{
    public IReadOnlyList<ModuleCapability> Capabilities =>
    [
        new ModuleCapability("cms.weapon.missile-control", "Missile Control")
    ];
}
