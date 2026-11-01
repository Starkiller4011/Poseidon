using Poseidon.Core.Modules;
using Xunit;

namespace Core.Tests;

// I1, I2, I8: IModuleHost.GetLog still inherits the interface's throwing default (ModuleRuntime does
// not implement it for real yet), and ModuleRuntime.Report does not route through the logging facility
// yet either. Every test below is written as if both already worked.
public sealed partial class ModuleRuntimeTests
{
    [Fact]
    public async Task GetLog_WorksFromStartAsyncAndStopAsync_WhereFeatureCallsAreRejected()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "text", useLogDuringStartAndStop: true);
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.Equal(PackageState.Active, test.State("base").State);

        var logFile = Assert.Single(Directory.GetFiles(test.LogDirectory, "poseidon-*.log"));
        var textAfterStart = await File.ReadAllTextAsync(logFile);
        Assert.Contains("base", textAfterStart);
        Assert.Contains("starting", textAfterStart);

        test.Remove("base");
        Assert.True(await test.Runtime.ReloadAsync());
        var textAfterStop = await File.ReadAllTextAsync(logFile);
        Assert.Contains("stopping", textAfterStop);
    }

    [Fact]
    public async Task Report_RoutesThroughTheSameLoggingFacility_NotJustConsoleError()
    {
        // A malformed feature value makes ModuleRuntime.Report a known diagnostic (see
        // ModuleLifecycleTests.InvalidFeatureValue_IsReportedWithoutActivation); this proves that same
        // text also reaches the log file, not only RuntimeStatus.Diagnostics and Console.Error.
        await using var test = new RuntimeTest();
        test.Package("base", "text");
        ConfigureFixture(test, "base", "FeatureMode", System.Text.Json.Nodes.JsonValue.Create("null")!);
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Contains(test.Runtime.Status.Diagnostics, d => d.Contains("Null feature"));

        var logFile = Assert.Single(Directory.GetFiles(test.LogDirectory, "poseidon-*.log"));
        Assert.Contains("Null feature", await File.ReadAllTextAsync(logFile));
    }
}
