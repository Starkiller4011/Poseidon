using Poseidon.Core.Modules;

namespace Poseidon.Modules.Ais;

public sealed class AisModule : IModule
{
    public IReadOnlyList<ModuleCapability> Capabilities =>
    [
        new ModuleCapability("cms.ais.tracking", "AIS Tracking")
    ];
}
