using Poseidon.Core.Modules;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("CoreRadar.Tests")]

namespace Poseidon.Modules.Radar;

public sealed class RadarModule : IModule, IRadarInput
{
    internal const string SourceId = "Core.Radar";
    private const int CheckpointSchema = 2;
    private static readonly JsonSerializerOptions CheckpointOptions = new();

    // R38 to R40 (21.13): one connector per configured physical channel; "default" until a hardware
    // configuration file is found. Each channel keeps its own sequence counter for checkpointing.
    private readonly Dictionary<string, TrackSourceConnector> connectors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> antennaRotationRatesRpm = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> lastSequenceByChannel = new(StringComparer.Ordinal);
    private bool started;

    public IReadOnlyList<ModuleCapability> Capabilities =>
    [
        new ModuleCapability("cms.radar.navigation", "Navigation Radar")
    ];

    public IReadOnlyDictionary<string, object> Features => new Dictionary<string, object>
    {
        [SensorPoints.RadarInputFeatureId] = this
    };

    public async ValueTask StartAsync(IModuleHost host, ModuleState? state, CancellationToken cancellationToken)
    {
        if (state is not null)
        {
            if (state.SchemaVersion != CheckpointSchema)
                throw new InvalidDataException($"Unsupported radar checkpoint schema {state.SchemaVersion}.");
            Dictionary<string, long>? restored;
            try
            {
                restored = JsonSerializer.Deserialize<Dictionary<string, long>>(state.Data, CheckpointOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("The radar checkpoint is corrupt.", ex);
            }
            lastSequenceByChannel.Clear();
            foreach (var (channelId, sequence) in restored ?? [])
                lastSequenceByChannel[channelId] = sequence;
        }

        var directory = Path.GetDirectoryName(typeof(RadarModule).Assembly.Location)!;
        var channels = RadarHardwareConfiguration.LoadChannels(directory);

        foreach (var previous in connectors.Values)
            await previous.StopAsync();
        connectors.Clear();

        foreach (var channel in channels)
        {
            antennaRotationRatesRpm[channel.ChannelId] = channel.AntennaRotationRateRpm;
            var connector = new TrackSourceConnector(host, DescriptorFor(channel.ChannelId, channel.AntennaRotationRateRpm));
            connectors[channel.ChannelId] = connector;
            connector.Start();
        }
        started = true;
    }

    public ValueTask<ModuleState?> CaptureStateAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(lastSequenceByChannel.Count == 0
            ? null
            : new ModuleState(CheckpointSchema, JsonSerializer.SerializeToUtf8Bytes(lastSequenceByChannel, CheckpointOptions)));

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        foreach (var connector in connectors.Values)
            await connector.StopAsync();
        connectors.Clear();
        started = false;
    }

    public SubmissionResult Submit(RadarTargetReport report)
    {
        if (!started)
            return SubmissionResult.Rejected(RejectionReason.Unavailable, "The radar adapter is not running.");
        if (!connectors.TryGetValue(report.ChannelId, out var connector))
            return SubmissionResult.Rejected(RejectionReason.Invalid, $"Channel '{report.ChannelId}' is not configured.");
        // Checked before the sequence counter advances, so a report rejected as Unavailable (not yet
        // registered with Tracks) never burns a sequence number it was never actually assigned to.
        if (connector.Sink is null)
            return SubmissionResult.Rejected(RejectionReason.Unavailable, "The radar adapter is not running.");
        if (!RadarNormalizer.TryNormalize(report, out var observation, out var problem))
            return SubmissionResult.Rejected(RejectionReason.Invalid, problem);
        var sequence = lastSequenceByChannel.GetValueOrDefault(report.ChannelId) + 1;
        lastSequenceByChannel[report.ChannelId] = sequence;
        var stamped = observation with
        {
            SourceId = SourceIdFor(report.ChannelId), Sequence = sequence, ReceivedUtc = DateTimeOffset.UtcNow
        };
        return connector.Publish(stamped);
    }

    public void ReportSourceHealth(SourceHealth health, string? detail = null)
    {
        foreach (var connector in connectors.Values)
            connector.ReportHealth(health, detail);
    }

    // R25, R40: changing a channel's rotation rate at run time recomputes and re-registers its descriptor.
    public void SetAntennaRotationRate(string channelId, double revolutionsPerMinute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        if (!double.IsFinite(revolutionsPerMinute) || revolutionsPerMinute <= 0)
            throw new ArgumentOutOfRangeException(nameof(revolutionsPerMinute), "The rotation rate must be a positive number.");
        if (!connectors.TryGetValue(channelId, out var connector))
            throw new ArgumentException($"Channel '{channelId}' is not configured.", nameof(channelId));
        antennaRotationRatesRpm[channelId] = revolutionsPerMinute;
        connector.UpdateDescriptor(DescriptorFor(channelId, revolutionsPerMinute));
    }

    // 21.4 "Radar descriptor": interval 60/rpm; stale max(5 s, 2x interval); lost 2x stale; remove 3x stale.
    internal static TrackSourceDescriptor DescriptorFor(string channelId, double rotationRateRpm)
    {
        var interval = TimeSpan.FromSeconds(60 / rotationRateRpm);
        var stale = TimeSpan.FromSeconds(Math.Max(5, 2 * interval.TotalSeconds));
        var lost = stale * 2;
        var remove = stale * 3;
        return new TrackSourceDescriptor(
            SourceIdFor(channelId), TrackSourceKinds.Radar, interval, stale, lost, remove,
            PositionUncertaintyMetres: 30, VelocityUncertaintyMetresPerSecond: 2, ExpectedPeakReportsPerSecond: 500);
    }

    // 21.4 "Configured radar channel source ID": the default implicit channel keeps today's plain source ID;
    // a deployer-named channel gets its own, e.g. Core.Radar.Nav1 (R38 to R40).
    internal static string SourceIdFor(string channelId)
        => channelId == RadarHardwareConfiguration.DefaultChannelId ? SourceId : $"{SourceId}.{channelId}";
}
