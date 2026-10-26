using Poseidon.Core.Modules;

namespace Poseidon.Modules.Tracks;

public sealed class TracksModule : IModule
{
    public IReadOnlyList<ModuleCapability> Capabilities =>
    [
        new ModuleCapability("cms.tracks.management", "Track Management")
    ];
}
