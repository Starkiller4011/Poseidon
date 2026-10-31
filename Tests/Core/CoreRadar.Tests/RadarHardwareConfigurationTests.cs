using Poseidon.Modules.Radar;
using Xunit;

namespace CoreRadar.Tests;

// Slice 4 Step 2 (21.13, R38, R40): RadarHardwareConfiguration.LoadChannels does not exist yet; every test
// below is written as if it already worked and fails, uncaught, at the stub's NotImplementedException.
public sealed class RadarHardwareConfigurationTests
{
    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "CoreRadar.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void LoadChannels_WithNoConfigurationFile_ReturnsOneImplicitDefaultChannel()
    {
        // R38: a module directory with no sensors.json yields exactly one implicit channel ("default"), so
        // today's single-radar behavior is preserved unchanged.
        var channel = Assert.Single(RadarHardwareConfiguration.LoadChannels(TempDirectory()));
        Assert.Equal(RadarHardwareConfiguration.DefaultChannelId, channel.ChannelId);
    }

    [Fact]
    public void LoadChannels_ParsesEachConfiguredChannel_WithItsOwnRotationRate()
    {
        // R40: two navigation-radar channels, each with its own deployer-chosen ID and rotation rate.
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, RadarHardwareConfiguration.FileName), """
            { "channels": [
                { "channelId": "nav1", "antennaRotationRateRpm": 24 },
                { "channelId": "nav2", "antennaRotationRateRpm": 48 }
            ] }
            """);
        var channels = RadarHardwareConfiguration.LoadChannels(directory);
        Assert.Equal(
            [("nav1", 24.0), ("nav2", 48.0)],
            channels.Select(c => (c.ChannelId, c.AntennaRotationRateRpm)).OrderBy(c => c.Item1, StringComparer.Ordinal));
    }

    [Fact]
    public void LoadChannels_RejectsDuplicateChannelIds_AsAMalformedFile()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, RadarHardwareConfiguration.FileName), """
            { "channels": [
                { "channelId": "nav1", "antennaRotationRateRpm": 24 },
                { "channelId": "nav1", "antennaRotationRateRpm": 48 }
            ] }
            """);
        Assert.Throws<InvalidDataException>(() => RadarHardwareConfiguration.LoadChannels(directory));
    }

    [Fact]
    public void LoadChannels_RejectsANonPositiveRotationRate_AsAMalformedFile()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, RadarHardwareConfiguration.FileName), """
            { "channels": [ { "channelId": "nav1", "antennaRotationRateRpm": 0 } ] }
            """);
        Assert.Throws<InvalidDataException>(() => RadarHardwareConfiguration.LoadChannels(directory));
    }
}
