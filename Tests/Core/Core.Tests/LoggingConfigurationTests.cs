using Poseidon.Core.Modules;
using Poseidon.Core.Runtime.Logging;
using Xunit;

namespace Core.Tests;

// I3, I11: LoggingConfiguration.ResolveMinimumLevel does not exist yet; every test below is written
// as if it already worked and fails, uncaught, at the stub's NotImplementedException.
public sealed class LoggingConfigurationTests
{
    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void ResolveMinimumLevel_WithNoFileAndNoOverride_DefaultsToInformation()
        => Assert.Equal(LogLevel.Information,
            LoggingConfiguration.ResolveMinimumLevel(TempDirectory(), environmentOverride: null));

    [Theory]
    [InlineData("""{ "minimumLevel": "Debug" }""", LogLevel.Debug)]
    [InlineData("""{ "minimumLevel": "Trace" }""", LogLevel.Trace)]
    [InlineData("""{ "minimumLevel": "Warning" }""", LogLevel.Warning)]
    [InlineData("""{ "minimumLevel": "Error" }""", LogLevel.Error)]
    public void ResolveMinimumLevel_WithAValidFile_UsesItsMinimumLevel(string json, LogLevel expected)
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, LoggingConfiguration.FileName), json);
        Assert.Equal(expected, LoggingConfiguration.ResolveMinimumLevel(directory, environmentOverride: null));
    }

    [Theory]
    [InlineData("""{ "minimumLevel": "Verbose" }""")]
    [InlineData("""{ "minimumLevel": "debug" }""")]
    [InlineData("""{ }""")]
    [InlineData("not json")]
    public void ResolveMinimumLevel_WithAMalformedOrUnrecognizedFile_FallsBackToInformation_NeverThrows(string json)
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, LoggingConfiguration.FileName), json);
        Assert.Equal(LogLevel.Information, LoggingConfiguration.ResolveMinimumLevel(directory, environmentOverride: null));
    }

    [Fact]
    public void ResolveMinimumLevel_WithAnEnvironmentOverride_TakesPrecedenceOverTheFile()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, LoggingConfiguration.FileName),
            """{ "minimumLevel": "Warning" }""");
        Assert.Equal(LogLevel.Debug,
            LoggingConfiguration.ResolveMinimumLevel(directory, environmentOverride: "Debug"));
    }

    [Fact]
    public void ResolveMinimumLevel_WithAnUnrecognizedEnvironmentOverride_FallsBackToTheFile()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, LoggingConfiguration.FileName),
            """{ "minimumLevel": "Warning" }""");
        Assert.Equal(LogLevel.Warning,
            LoggingConfiguration.ResolveMinimumLevel(directory, environmentOverride: "Verbose"));
    }
}
