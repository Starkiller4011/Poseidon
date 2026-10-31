using System.Text.Json;
using System.Text.Json.Serialization;

namespace Poseidon.Modules.Radar;

// One physical navigation-radar channel this module is responsible for (21.13, R38, R40): its own
// deployer-chosen channel ID (unique within the file) and antenna rotation rate.
internal sealed record RadarChannel(string ChannelId, double AntennaRotationRateRpm);

// R38: the hardware configuration stays with the module, found next to its own assembly, not supplied by the
// host (21.13, D10). A module with no such file keeps one implicit default channel, so single-radar installs
// and the existing adapter tests are unaffected. A malformed file (duplicate channel IDs, a non-positive
// rotation rate) is reported as InvalidDataException, the same way other module start failures are (settled
// while writing the slice 4 tests, 21.13 "still open" item 1).
internal static class RadarHardwareConfiguration
{
    public const string DefaultChannelId = "default";
    public const string FileName = "sensors.json";
    private const double DefaultRotationRateRpm = 24;

    private sealed record ChannelFile([property: JsonPropertyName("channels")] ChannelEntry[]? Channels);

    private sealed record ChannelEntry(
        [property: JsonPropertyName("channelId")] string? ChannelId,
        [property: JsonPropertyName("antennaRotationRateRpm")] double AntennaRotationRateRpm);

    public static IReadOnlyList<RadarChannel> LoadChannels(string moduleDirectory)
    {
        var path = Path.Combine(moduleDirectory, FileName);
        if (!File.Exists(path))
            return [new RadarChannel(DefaultChannelId, DefaultRotationRateRpm)];

        ChannelFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ChannelFile>(
                File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{FileName} is not valid JSON: {ex.Message}", ex);
        }

        if (file?.Channels is not { Length: > 0 } entries)
            throw new InvalidDataException($"{FileName} must list at least one channel.");

        var channels = new List<RadarChannel>(entries.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.ChannelId))
                throw new InvalidDataException($"{FileName} has a channel with no channel ID.");
            if (!seen.Add(entry.ChannelId))
                throw new InvalidDataException($"{FileName} configures channel '{entry.ChannelId}' more than once.");
            if (!double.IsFinite(entry.AntennaRotationRateRpm) || entry.AntennaRotationRateRpm <= 0)
                throw new InvalidDataException(
                    $"{FileName}'s channel '{entry.ChannelId}' has a non-positive antenna rotation rate.");
            channels.Add(new RadarChannel(entry.ChannelId, entry.AntennaRotationRateRpm));
        }
        return channels;
    }
}
