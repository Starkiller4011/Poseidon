using Poseidon.Modules.CoreUI;
using Xunit;

namespace CoreUI.Tests;

// H1: ConsoleHardwareConfiguration.Load does not exist yet; every test below is written as if it
// already worked and fails, uncaught, at the stub's NotImplementedException.
public sealed class ConsoleHardwareConfigurationTests
{
    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "CoreUI.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Load_WithNoConfigurationFile_ThrowsInvalidDataException()
    {
        // Unlike Radar's "no file -> one implicit default channel", which hardware console this
        // deployment is built for is not something to guess a default for.
        Assert.Throws<InvalidDataException>(() => ConsoleHardwareConfiguration.Load(TempDirectory()));
    }

    [Fact]
    public void Load_ParsesSingleDisplayMode()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, ConsoleHardwareConfiguration.FileName),
            """{ "displayMode": "Single" }""");
        Assert.Equal(DisplayMode.Single, ConsoleHardwareConfiguration.Load(directory).DisplayMode);
    }

    [Fact]
    public void Load_ParsesDualDisplayMode()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, ConsoleHardwareConfiguration.FileName),
            """{ "displayMode": "Dual" }""");
        Assert.Equal(DisplayMode.Dual, ConsoleHardwareConfiguration.Load(directory).DisplayMode);
    }

    [Theory]
    [InlineData("""{ "displayMode": "Triple" }""")]
    [InlineData("""{ "displayMode": "single" }""")]
    [InlineData("""{ "displayMode": "" }""")]
    [InlineData("""{ }""")]
    public void Load_RejectsAnUnrecognizedOrMissingDisplayMode_AsAMalformedFile(string json)
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, ConsoleHardwareConfiguration.FileName), json);
        Assert.Throws<InvalidDataException>(() => ConsoleHardwareConfiguration.Load(directory));
    }

    [Fact]
    public void Load_RejectsMalformedJson()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, ConsoleHardwareConfiguration.FileName), "{ not json");
        Assert.Throws<InvalidDataException>(() => ConsoleHardwareConfiguration.Load(directory));
    }
}
