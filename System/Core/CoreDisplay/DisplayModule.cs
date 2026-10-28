using Poseidon.Core.Modules;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CoreDisplay.Tests")]

namespace Poseidon.Modules.Display;

public sealed class DisplayModule : IModule
{
    private readonly TrackReplica tracks = new();
    private readonly ChartViewport viewport;
    private readonly TimeSpan trackPollInterval;
    private TrackReplicaPump? pump;

    public DisplayModule() : this(TimeSpan.FromMilliseconds(250))
    {
    }

    internal DisplayModule(TimeSpan trackPollInterval)
    {
        this.trackPollInterval = trackPollInterval;
        viewport = new ChartViewport(tracks);
        viewport.AddLayer(new ViewportLayer(ChartViewport.TracksLayerId, "Tracks", ViewportLayerKind.Overlay, true));
    }

    public IReadOnlyList<ModuleCapability> Capabilities => [];
    public IReadOnlyDictionary<string, object> Features =>
        new Dictionary<string, object> { ["core.display.viewport"] = viewport };

    // Tracks is an optional provider: Display follows its feed when present and reports it unavailable otherwise.
    public async ValueTask StartAsync(IModuleHost host, ModuleState? state, CancellationToken cancellationToken)
    {
        if (state is not null)
            throw new InvalidDataException("This module does not support state migration.");
        if (pump is not null)
            await pump.StopAsync();
        pump = new TrackReplicaPump(host, tracks, trackPollInterval);
        pump.Start();
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (pump is not null)
            await pump.StopAsync();
        pump = null;
        tracks.Reset();
    }
}
