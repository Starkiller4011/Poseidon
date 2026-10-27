using Poseidon.Core.Modules;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CoreAIS.Tests")]

namespace Poseidon.Modules.Ais;

public sealed class AisModule : IModule, IAisInput
{
    internal const string SourceId = "Core.AIS";
    private ObservationRelay? relay;

    public IReadOnlyList<ModuleCapability> Capabilities =>
    [
        new ModuleCapability("cms.ais.tracking", "AIS Tracking")
    ];

    public IReadOnlyDictionary<string, object> Features => new Dictionary<string, object>
    {
        [SensorPoints.AisInputFeatureId] = this
    };

    internal RelayStatistics? Statistics => relay?.Statistics;

    public async ValueTask StartAsync(IModuleHost host, ModuleState? state, CancellationToken cancellationToken)
    {
        var created = new ObservationRelay(host, SourceId);
        created.Restore(state);
        if (relay is not null)
            await relay.StopAsync();
        relay = created;
        created.Start();
    }

    public ValueTask<ModuleState?> CaptureStateAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(relay?.Capture());

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (relay is not null)
            await relay.StopAsync();
    }

    public SubmissionResult Submit(AisPositionReport report)
    {
        if (relay is not { } current)
            return SubmissionResult.Rejected(RejectionReason.Unavailable, "The AIS adapter is not running.");
        return AisNormalizer.TryNormalize(report, out var observation, out var problem)
            ? current.Enqueue(observation)
            : SubmissionResult.Rejected(RejectionReason.Invalid, problem);
    }

    public void ReportSourceHealth(SourceHealth health, string? detail = null)
        => relay?.ReportHealth(health, detail);
}
