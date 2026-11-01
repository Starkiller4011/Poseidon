using Poseidon.Core.Modules;
using Poseidon.Core.Runtime.Logging;
using Xunit;

namespace Core.Tests;

// I4 to I7, I10: LogSink.Write does not exist yet; every test below is written as if it already
// worked and fails, uncaught, at the stub's NotImplementedException.
public sealed class LogSinkTests : IDisposable
{
    private readonly TextWriter originalOut = Console.Out;
    private readonly StringWriter console = new();

    public LogSinkTests() => Console.SetOut(console);
    public void Dispose() => Console.SetOut(originalOut);

    private static string TempLogFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "poseidon-test.log");
    }

    [Fact]
    public void Write_AtOrAboveTheMinimumLevel_AppearsOnTheConsoleAndInTheFile()
    {
        var path = TempLogFile();
        var sink = new LogSink(LogLevel.Information, path);
        sink.Write("Core.Tracks", LogLevel.Warning, "queue is degraded", null);
        Assert.Contains("Core.Tracks", console.ToString());
        Assert.Contains("queue is degraded", console.ToString());
        Assert.Contains("Warning", console.ToString());
        var fileText = File.ReadAllText(path);
        Assert.Contains("Core.Tracks", fileText);
        Assert.Contains("queue is degraded", fileText);
        Assert.Contains("Warning", fileText);
    }

    [Fact]
    public void Write_BelowTheMinimumLevel_ProducesNoOutputAtAll()
    {
        var path = TempLogFile();
        var sink = new LogSink(LogLevel.Information, path);
        sink.Write("Core.Tracks", LogLevel.Debug, "very detailed", null);
        Assert.Equal("", console.ToString());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Write_IncludesATimestamp()
    {
        var path = TempLogFile();
        var sink = new LogSink(LogLevel.Information, path);
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        sink.Write("Core.Tracks", LogLevel.Information, "hello", null);
        var after = DateTimeOffset.UtcNow.AddSeconds(5);
        // The emitted line must contain a parseable timestamp within [before, after]; this is a loose
        // enough check to not depend on the exact chosen format.
        var line = console.ToString();
        var found = line.Split(' ', '\t').Any(token =>
            DateTimeOffset.TryParse(token.Trim('[', ']'), out var parsed) && parsed >= before && parsed <= after);
        Assert.True(found, $"No timestamp between {before:O} and {after:O} found in: {line}");
    }

    [Fact]
    public void Write_WithAnException_IncludesItsMessageAndStackTrace()
    {
        var path = TempLogFile();
        var sink = new LogSink(LogLevel.Information, path);
        Exception exception;
        try { throw new InvalidOperationException("boom"); }
        catch (InvalidOperationException ex) { exception = ex; }
        sink.Write("Core", LogLevel.Error, "something failed", exception);
        var fileText = File.ReadAllText(path);
        Assert.Contains("boom", fileText);
        Assert.Contains(nameof(LogSinkTests), fileText); // this test method appears in the stack trace
    }

    [Fact]
    public void Write_WhenTheFileCannotBeWritten_StillWritesTheConsoleAndDoesNotThrow()
    {
        // The parent directory does not exist and is never created, so the file write fails.
        var path = Path.Combine(Path.GetTempPath(), "Core.Tests", Guid.NewGuid().ToString("N"), "poseidon.log");
        var sink = new LogSink(LogLevel.Information, path);
        sink.Write("Core", LogLevel.Error, "disk is unhappy", null);
        Assert.Contains("disk is unhappy", console.ToString());
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }
}
