using Poseidon.Core.Modules;

namespace Poseidon.Modules.Sonar;

public sealed class CmsSonarModule : IModule
{
    public IReadOnlyList<ModuleCapability> Capabilities =>
    [
        new ModuleCapability("cms.sonar.operations", "Sonar Operations")
    ];
}
