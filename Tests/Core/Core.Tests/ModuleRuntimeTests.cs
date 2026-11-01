using System.Text.Json;
using Poseidon.Core.Modules;
using Poseidon.Core.Runtime;
using Xunit;

namespace Core.Tests;

public sealed partial class ModuleRuntimeTests
{
    [Fact]
    public async Task EmptyHost_StartsWithoutFeatures()
    {
        await using var test = new RuntimeTest();
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.Empty(test.Runtime.Status.Modules);
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Read());
    }

    [Fact]
    public async Task Replacement_PreservesStateAndDoesNotLockDeployedDll()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "original");
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.Equal("original:1", await test.Read());
        // Replace the actual deployed assembly while its shadow copy is active.
        test.Package("base", "replacement");
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.Equal("replacement:2", await test.Read());
        Assert.Equal(2, test.Runtime.Status.Generation);
    }

    [Fact]
    public async Task ExplicitPatchAndRemoval_RestoreBaseProvider()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "base");
        Assert.True(await test.Runtime.ReloadAsync());
        test.Package("patch", "patched", replaces: true);
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.Equal("patched:1", await test.Read());
        Assert.Equal("patch", Assert.Single(test.Runtime.Status.Features).ProviderId);
        test.Remove("patch");
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.Equal("base:1", await test.Read());
    }

    [Fact]
    public async Task OrderedPatchChain_UsesLastExplicitReplacement()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "base");
        test.Package("patch", "patch", replaces: true, priority: 1);
        test.Package("patch2", "patch2", replaces: true, priority: 2);
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.Equal("patch2:1", await test.Read());
    }

    [Fact]
    public async Task AmbiguousPatch_IsRejectedWithoutChangingCurrentGeneration()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "base");
        Assert.True(await test.Runtime.ReloadAsync());
        test.Package("a", "first", replaces: true);
        test.Package("b", "second", replaces: true);
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Equal(1, test.Runtime.Status.Generation);
        Assert.Equal("base:1", await test.Read());
        Assert.Contains(test.Runtime.Status.Diagnostics, d => d.Contains("Ambiguous override"));
    }

    [Fact]
    public async Task FailedActivation_RestoresPriorStateAndProvider()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "good");
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.Equal("good:1", await test.Read());
        test.Package("base", "bad", failStart: true);
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Equal("good:2", await test.Read());
        Assert.Equal(1, test.Runtime.Status.Generation);
        Assert.Contains(test.Runtime.Status.Diagnostics, d => d.Contains("Fixture activation failure"));
    }

    [Fact]
    public async Task FailedQuiesce_MarksFeaturesUnavailable()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "unsafe", failStop: true);
        Assert.True(await test.Runtime.ReloadAsync());
        test.Package("base", "new");
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Empty(test.Runtime.Status.Features);
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Read());
        Assert.Contains(test.Runtime.Status.Diagnostics, d => d.Contains("Cannot safely quiesce"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("cycle")]
    [InlineData("duplicate")]
    [InlineData("contract")]
    public async Task InvalidGraph_IsRejected(string kind)
    {
        await using var test = new RuntimeTest();
        if (kind == "missing")
            test.Package("base", "text", dependency: "absent");
        else if (kind == "cycle")
        {
            test.Package("base", "text", dependency: "other");
            test.Package("other", "text", dependency: "base");
        }
        else if (kind == "duplicate")
        {
            test.Package("base", "text");
            test.Package("base", "text", folder: "duplicate");
        }
        else
            test.Package("base", "text", contractVersion: 99);
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Empty(test.Runtime.Status.Modules);
        Assert.NotEmpty(test.Runtime.Status.Diagnostics);
    }

    [Fact]
    public async Task IncompleteReplacement_RetainsCurrentGeneration()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "good");
        Assert.True(await test.Runtime.ReloadAsync());
        File.Delete(Path.Combine(test.Root, "base", ".ready"));
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Equal("good:1", await test.Read());
    }

    [Fact]
    public async Task Removal_WithdrawsFeature()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "good");
        Assert.True(await test.Runtime.ReloadAsync());
        test.Remove("base");
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.Empty(test.Runtime.Status.Modules);
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Read());
    }

    [Fact]
    public async Task Reload_WaitsForInFlightFeatureOperation()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "old");
        Assert.True(await test.Runtime.ReloadAsync());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var call = Task.Run(() => test.Runtime.InvokeAsync<ITextFeature, string>("test.text", f =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return f.Text;
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        test.Package("base", "new");
        var reload = test.Runtime.ReloadAsync();
        try { Assert.False(reload.IsCompleted); }
        finally { release.Set(); }
        Assert.Equal("old:1", await call);
        Assert.True(await reload);
        Assert.Equal("new:2", await test.Read());
    }

    [Fact]
    public async Task Watcher_AddsAndRemovesModuleWithoutManualReload()
    {
        await using var test = new RuntimeTest();
        await test.Runtime.StartAsync();
        test.Package("base", "watched");
        await WaitUntil(() => test.Runtime.Status.Modules.Count == 1);
        Assert.Equal("watched:1", await test.Read());
        test.Remove("base");
        await WaitUntil(() => test.Runtime.Status.Modules.Count == 0);
    }

    [Fact]
    public async Task RepeatedReloads_ReleaseOldCollectibleContexts()
    {
        await using var test = new RuntimeTest();
        for (var i = 0; i < 8; i++)
        {
            test.Package("base", $"generation-{i}");
            Assert.True(await test.Runtime.ReloadAsync());
            Assert.Equal($"generation-{i}:{i + 1}", await test.Read());
        }
        var contexts = test.Runtime.RetiredContexts;
        Assert.NotEmpty(contexts);
        for (var attempt = 0; attempt < 10 && contexts.Any(c => c.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(50);
        }
        Assert.All(contexts, context => Assert.False(context.IsAlive));
    }

    [Fact]
    public async Task IncompatibleFeatureVersion_RetainsBaseProvider()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "base");
        Assert.True(await test.Runtime.ReloadAsync());
        test.Package("patch", "patch", replaces: true);
        var path = Path.Combine(test.Root, "patch", "module.json");
        var manifest = JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(path))!;
        manifest = manifest with { Contributions = [manifest.Contributions[0] with { Version = "2.0.0" }] };
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Equal("base:1", await test.Read());
    }

    [Fact]
    public async Task MalformedManifest_IsAnExplicitFailure()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "base");
        File.WriteAllText(Path.Combine(test.Root, "base", "module.json"), "{");
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.NotEmpty(test.Runtime.Status.Diagnostics);
    }

    [Fact]
    public async Task UncooperativeLifecycle_IsBoundedAndReportedUnavailable()
    {
        await using var test = new RuntimeTest(TimeSpan.FromMilliseconds(80));
        test.Package("base", "slow", startDelayMilliseconds: 400);
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Empty(test.Runtime.Status.Features);
        Assert.Contains(test.Runtime.Status.Diagnostics, d => d.Contains("Lifecycle interrupted"));
        await Task.Delay(500);
    }

    [Fact]
    public async Task RecursiveFeatureInvocation_IsRejectedRatherThanDeadlocked()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "text");
        Assert.True(await test.Runtime.ReloadAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Runtime.InvokeAsync<ITextFeature, string>("test.text", _ => test.Read().GetAwaiter().GetResult()));
        Assert.Equal("text:1", await test.Read());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
            await Task.Delay(50, deadline.Token);
    }

    private sealed class RuntimeTest : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Poseidon.Tests", Guid.NewGuid().ToString("N"));
        // M1-I: an isolated directory for ModuleRuntime's log file, so logging tests never race the
        // shared test-output directory other tests' runs might also write to.
        public string LogDirectory { get; } = Path.Combine(Path.GetTempPath(), "Poseidon.Tests", Guid.NewGuid().ToString("N"));
        public static ExtensionPoint TestPoint { get; } = new() { Id = "test.point", Version = "1.0.0" };
        public ModuleRuntime Runtime { get; }

        public RuntimeTest(TimeSpan? lifecycleTimeout = null)
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(LogDirectory);
            Runtime = new ModuleRuntime(Root, lifecycleTimeout: lifecycleTimeout, additionalHostPoints: [TestPoint],
                logDirectory: LogDirectory);
        }

        public Task<string> Read(string feature = "test.text") =>
            Runtime.InvokeAsync<ITextFeature, string>(feature, f => f.Text);

        public PackageStatus State(string folder) =>
            Assert.Single(Runtime.Status.Packages, p => Path.GetFileName(p.PackagePath) == folder);

        public void Package(string id, string text, bool replaces = false, int priority = 0, string? dependency = null,
            bool failStart = false, bool failStop = false, string? folder = null,
            int contractVersion = 1, string version = "1.0.0", int startDelayMilliseconds = 0,
            string feature = "test.text", string dependencyVersion = "1.0.0", bool useLogDuringStartAndStop = false)
        {
            var directory = Path.Combine(Root, folder ?? id);
            Directory.CreateDirectory(directory);
            File.Delete(Path.Combine(directory, ".ready"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "RuntimeModule.dll"),
                Path.Combine(directory, "RuntimeModule.dll"), overwrite: true);
            File.WriteAllText(Path.Combine(directory, "fixture.json"), JsonSerializer.Serialize(new
            {
                Id = id, Text = text, FailStart = failStart, FailStop = failStop,
                StartDelayMilliseconds = startDelayMilliseconds, FeatureId = feature,
                UseLogDuringStartAndStop = useLogDuringStartAndStop
            }));
            var manifest = new ModuleManifest
            {
                Id = id, Version = version, EntryAssembly = "RuntimeModule.dll", ContractVersion = contractVersion,
                Dependencies = dependency is null ? [] : [new() { Id = dependency, Version = dependencyVersion }],
                Contributions = [new Contribution
                {
                    Point = TestPoint.Id, Id = feature, Version = "1.0.0", Replaces = replaces, Priority = priority
                }]
            };
            File.WriteAllText(Path.Combine(directory, "module.json"), JsonSerializer.Serialize(manifest));
            File.WriteAllText(Path.Combine(directory, ".ready"), "complete");
        }

        public void Remove(string id) => Directory.Delete(Path.Combine(Root, id), recursive: true);

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            Directory.Delete(Root, recursive: true);
            for (var attempt = 0; attempt < 10 && Runtime.RetiredContexts.Any(c => c.IsAlive); attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(20);
            }
            if (Directory.Exists(Runtime.CacheDirectory))
                Directory.Delete(Runtime.CacheDirectory, recursive: true);
            if (Directory.Exists(LogDirectory))
                Directory.Delete(LogDirectory, recursive: true);
        }
    }
}
