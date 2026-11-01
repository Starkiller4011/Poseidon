using Poseidon.Core.Modules;
using Poseidon.Modules.Charts.Store;

namespace Poseidon.Modules.Charts;

public sealed class ChartsModule : IModule
{
    private readonly ChartLibrary library = new(new ChartLibraryOptions
    {
        DatabasePath = Path.Combine(AppContext.BaseDirectory, "charts", "charts.db")
    });

    public IReadOnlyList<ModuleCapability> Capabilities =>
    [
        new ModuleCapability("charts.display", "Chart Display"),
        new ModuleCapability("charts.route-planning", "Route Planning")
    ];

    public IReadOnlyDictionary<string, object> Features => new Dictionary<string, object>
    {
        [ChartPoints.LibraryFeatureId] = library
    };
}
