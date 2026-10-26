using Poseidon.Core.Modules;

namespace Poseidon.Modules.Ew;

public sealed class CmsEwModule : IModule
{
    public IReadOnlyList<ModuleCapability> Capabilities =>
    [
        new ModuleCapability("cms.ew.operations", "Electronic Warfare")
    ];
}
