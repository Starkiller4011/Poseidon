using Poseidon.Core.Modules;

namespace Poseidon.Modules.Comms;

public sealed class CmsCommsModule : IModule
{
    public IReadOnlyList<ModuleCapability> Capabilities =>
    [
        new ModuleCapability("cms.comms.control", "Communications Control")
    ];
}
