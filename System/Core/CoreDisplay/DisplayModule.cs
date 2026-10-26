using Poseidon.Core.Modules;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CoreDisplay.Tests")]

namespace Poseidon.Modules.Display;

public sealed class DisplayModule : IModule
{
    private readonly ChartViewport viewport = new();

    public IReadOnlyList<ModuleCapability> Capabilities => [];
    public IReadOnlyDictionary<string, object> Features =>
        new Dictionary<string, object> { ["core.display.viewport"] = viewport };
}
