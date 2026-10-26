using Poseidon.Core.Modules;

namespace Poseidon.Modules.CmsCore;

public sealed class CmsCoreModule : IModule
{
    public IReadOnlyList<ModuleCapability> Capabilities =>
    [
        new ModuleCapability("cms.activity-coordination", "CMS Activity Coordination")
    ];
}
