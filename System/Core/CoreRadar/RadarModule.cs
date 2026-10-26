using Poseidon.Core.Modules;

namespace Poseidon.Modules.Radar;

public sealed class RadarModule : IModule
{
    public IReadOnlyList<ModuleCapability> Capabilities =>
    [
        new ModuleCapability("cms.radar.surveillance", "Surveillance Radar"),
        new ModuleCapability("cms.radar.navigation", "Navigation Radar")
    ];
}
