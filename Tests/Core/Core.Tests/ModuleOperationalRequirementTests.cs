using System.Reflection;
using Poseidon.Core.Modules;
using Xunit;

namespace Core.Tests;

public sealed partial class ModuleRuntimeTests
{
    [Fact]
    public async Task BrokenPackage_IsRestagedFromLastKnownGoodShadowDuringAnotherUpdate()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "base");
        test.Package("other", "other", feature: "test.other");
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.Equal("base:1", await test.Read());

        File.WriteAllText(Path.Combine(test.Root, "base", "module.json"), "{");
        test.Package("other", "updated", feature: "test.other");

        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Equal(2, test.Runtime.Status.Generation);
        Assert.Equal("base:2", await test.Read());
        Assert.Equal("updated:1", await test.Read("test.other"));
        var retained = test.State("base");
        Assert.Equal(PackageState.Retained, retained.State);
        Assert.Contains("last-known-good", retained.Detail);
    }

    [Fact]
    public async Task RetiredShadowDirectory_IsReapedAfterItsContextIsCollected()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "first");
        Assert.True(await test.Runtime.ReloadAsync());
        var firstShadow = Assert.Single(Directory.GetDirectories(test.Runtime.CacheDirectory));

        test.Package("base", "second");
        Assert.True(await test.Runtime.ReloadAsync());
        for (var attempt = 0; attempt < 20 && test.Runtime.RetiredContexts.Any(context => context.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(25);
        }

        Assert.All(test.Runtime.RetiredContexts, context => Assert.False(context.IsAlive));
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.False(Directory.Exists(firstShadow));
    }

    [Fact]
    public async Task PeriodicReconciliation_RecoversWhenWatcherEventsAreMissed()
    {
        await using var test = new RuntimeTest();
        await test.Runtime.StartAsync();
        DisableWatcher(test.Runtime);

        test.Package("base", "periodic");

        await WaitUntil(() => test.Runtime.Status.Modules.Count == 1);
        Assert.Equal("periodic:1", await test.Read());
    }

    [Fact]
    public async Task AutomaticReconciliation_DoesNotRetryAnUnchangedRejectedPackage()
    {
        await using var test = new RuntimeTest();
        test.Package("bad", "bad", failStart: true);
        var log = Path.Combine(test.Root, "lifecycle.log");
        ConfigureFixture(test, "bad", "EventLog", System.Text.Json.Nodes.JsonValue.Create(log)!);

        await test.Runtime.StartAsync();
        DisableWatcher(test.Runtime);
        Assert.Equal(1, StartCount(log));

        await Task.Delay(TimeSpan.FromSeconds(6));
        Assert.Equal(1, StartCount(log));

        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Equal(2, StartCount(log));
    }

    private static void DisableWatcher(Poseidon.Core.Runtime.ModuleRuntime runtime)
    {
        var field = typeof(Poseidon.Core.Runtime.ModuleRuntime).GetField(
            "watcher",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var watcher = Assert.IsType<FileSystemWatcher>(field?.GetValue(runtime));
        watcher.EnableRaisingEvents = false;
    }

    private static int StartCount(string log)
        => File.ReadAllLines(log).Count(line => line.EndsWith(":start", StringComparison.Ordinal));
}
