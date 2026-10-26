using Poseidon.Core.Modules;

namespace Poseidon.Modules.Charts;

public sealed class ChartsModule : IModule
{
    public IReadOnlyList<ModuleCapability> Capabilities =>
    [
        new ModuleCapability("charts.display", "Chart Display"),
        new ModuleCapability("charts.route-planning", "Route Planning")
    ];
}
