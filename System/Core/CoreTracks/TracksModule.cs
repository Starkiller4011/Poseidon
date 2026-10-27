using Poseidon.Core.Modules;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CoreTracks.Tests")]

namespace Poseidon.Modules.Tracks;

public sealed class TracksModule : IModule
{
    private readonly TrackManager manager;
    private readonly TimeProvider time;
    private readonly TimeSpan tickInterval;
    private ITimer? timer;

    public TracksModule() : this(TimeProvider.System, TimeSpan.FromSeconds(1), null)
    {
    }

    internal TracksModule(TimeProvider time, TimeSpan tickInterval, TrackPolicy? policy)
    {
        this.time = time;
        this.tickInterval = tickInterval;
        manager = new TrackManager(time, policy);
    }

    public IReadOnlyList<ModuleCapability> Capabilities =>
    [
        new ModuleCapability("cms.tracks.management", "Track Management")
    ];

    public IReadOnlyDictionary<string, object> Features => new Dictionary<string, object>
    {
        [TrackPoints.IngestionFeatureId] = manager,
        [TrackPoints.FeedFeatureId] = manager
    };

    public async ValueTask StartAsync(IModuleHost host, ModuleState? state, CancellationToken cancellationToken)
    {
        if (state is not null)
            manager.Restore(state);
        if (timer is not null)
            await timer.DisposeAsync();
        timer = time.CreateTimer(_ => manager.Advance(), null, tickInterval, tickInterval);
    }

    public ValueTask<ModuleState?> CaptureStateAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(manager.Capture());

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        var stopping = timer;
        timer = null;
        if (stopping is not null)
            await stopping.DisposeAsync();
    }
}
