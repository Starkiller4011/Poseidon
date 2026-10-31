using Poseidon.Core.Modules;
using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("CoreAIS.Tests")]

namespace Poseidon.Modules.Ais;

public sealed class AisModule : IModule, IAisInput
{
    internal const string SourceId = "Core.AIS";
    private const int CheckpointSchema = 1;

    private static readonly TrackSourceDescriptor Descriptor = new(
        SourceId, TrackSourceKinds.Ais,
        ExpectedUpdateInterval: TimeSpan.FromSeconds(10),
        StaleAfter: TimeSpan.FromMinutes(4),
        LostAfter: TimeSpan.FromMinutes(6),
        RemoveAfter: TimeSpan.FromMinutes(10),
        PositionUncertaintyMetres: 10,
        VelocityUncertaintyMetresPerSecond: 0.5,
        ExpectedPeakReportsPerSecond: 1000);

    private TrackSourceConnector? connector;
    private long lastSequence;

    public IReadOnlyList<ModuleCapability> Capabilities =>
    [
        new ModuleCapability("cms.ais.tracking", "AIS Tracking")
    ];

    public IReadOnlyDictionary<string, object> Features => new Dictionary<string, object>
    {
        [SensorPoints.AisInputFeatureId] = this
    };

    public async ValueTask StartAsync(IModuleHost host, ModuleState? state, CancellationToken cancellationToken)
    {
        if (state is not null)
        {
            if (state.SchemaVersion != CheckpointSchema || state.Data.Length != sizeof(long))
                throw new InvalidDataException("Unsupported AIS checkpoint.");
            lastSequence = BitConverter.ToInt64(state.Data);
        }

        var created = new TrackSourceConnector(host, Descriptor);
        if (connector is { } previous)
            await previous.StopAsync();
        connector = created;
        created.Start();
    }

    public ValueTask<ModuleState?> CaptureStateAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(lastSequence == 0 ? null : new ModuleState(CheckpointSchema, BitConverter.GetBytes(lastSequence)));

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (connector is { } current)
            await current.StopAsync();
        connector = null;
    }

    public SubmissionResult Submit(AisPositionReport report)
    {
        // Checked before the sequence counter advances, so a report rejected as Unavailable (not yet
        // registered with Tracks) never burns a sequence number it was never actually assigned to.
        if (connector is not { Sink: not null } current)
            return SubmissionResult.Rejected(RejectionReason.Unavailable, "The AIS adapter is not running.");
        if (!AisNormalizer.TryNormalize(report, out var observation, out var problem))
            return SubmissionResult.Rejected(RejectionReason.Invalid, problem);
        var stamped = observation with { Sequence = ++lastSequence, ReceivedUtc = DateTimeOffset.UtcNow };
        return current.Publish(stamped);
    }

    public void ReportSourceHealth(SourceHealth health, string? detail = null)
        => connector?.ReportHealth(health, detail);
}
