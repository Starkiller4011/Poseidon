using System.Text.Json;
using Poseidon.Core.Modules;
using Poseidon.Core.Runtime;
using Xunit;

namespace Core.Tests;

public sealed partial class ModuleRuntimeTests
{
    [Fact]
    public async Task UnchangedPackages_DoNotRestartOrIncrementGeneration()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "text");
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.Equal("text:1", await test.Read());
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.Equal(1, test.Runtime.Status.Generation);
        Assert.Equal("text:2", await test.Read());
    }

    [Fact]
    public async Task IncompleteNewPackage_IsIgnoredUntilReady()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "text");
        var ready = Path.Combine(test.Root, "base", ".ready");
        File.Delete(ready);
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.Empty(test.Runtime.Status.Modules);
        File.WriteAllText(ready, "ready");
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.Single(test.Runtime.Status.Modules);
    }

    [Fact]
    public async Task FeatureTypeMismatch_DoesNotPoisonSubsequentCalls()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "text");
        Assert.True(await test.Runtime.ReloadAsync());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Runtime.InvokeAsync<IDisposable, bool>("test.text", _ => true));
        Assert.Contains("contract mismatch", error.Message);
        Assert.Equal("text:1", await test.Read());
    }

    [Fact]
    public async Task ThrowingFeatureCallback_ReleasesGenerationBarrier()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "text");
        Assert.True(await test.Runtime.ReloadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            test.Runtime.InvokeAsync<ITextFeature, string>("test.text", _ => throw new InvalidDataException("callback")));
        Assert.Equal("text:1", await test.Read());
        Assert.True(await test.Runtime.ReloadAsync());
    }

    [Fact]
    public async Task CancelledReloadAndInvocation_DoNotAlterActiveGeneration()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "text");
        Assert.True(await test.Runtime.ReloadAsync());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Runtime.ReloadAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            test.Runtime.InvokeAsync<ITextFeature, string>("test.text", f => f.Text, cancellation.Token));
        Assert.Equal(1, test.Runtime.Status.Generation);
        Assert.Equal("text:1", await test.Read());
    }

    [Fact]
    public async Task DisposedRuntime_RejectsCallsAndCannotRestart()
    {
        await using var test = new RuntimeTest();
        await test.Runtime.DisposeAsync();
        await test.Runtime.DisposeAsync();
        Assert.False(await test.Runtime.ReloadAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => test.Read());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => test.Runtime.StartAsync());
        test.Runtime.RequestReload();
    }

    [Fact]
    public async Task Dispose_WithdrawsPublicStatusAsWellAsFeatureCalls()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "text");
        Assert.True(await test.Runtime.ReloadAsync());
        await test.Runtime.DisposeAsync();
        Assert.Empty(test.Runtime.Status.Modules);
        Assert.Empty(test.Runtime.Status.Features);
    }

    [Fact]
    public async Task ShutdownTimeout_AllowsCleanupRetryAfterOperationDrains()
    {
        await using var test = new RuntimeTest(TimeSpan.FromMilliseconds(100));
        test.Package("base", "text");
        Assert.True(await test.Runtime.ReloadAsync());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var operation = Task.Run(() => test.Runtime.InvokeAsync<ITextFeature, string>("test.text", f =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return f.Text;
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => test.Runtime.DisposeAsync().AsTask());
        }
        finally { release.Set(); }
        Assert.Equal("text:1", await operation);
        await test.Runtime.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => test.Read());
        Assert.Empty(test.Runtime.Status.Modules);
    }

    [Fact]
    public async Task Watcher_CannotBeStartedTwice()
    {
        await using var test = new RuntimeTest();
        await test.Runtime.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Runtime.StartAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidLifecycleTimeout_IsRejected(int milliseconds)
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ModuleRuntime(lifecycleTimeout: TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public async Task ShutdownRequest_InvokesHostCallback()
    {
        var count = 0;
        await using var runtime = new ModuleRuntime(shutdown: () => count++);
        runtime.RequestShutdown();
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("missing-assembly")]
    [InlineData("invalid-assembly")]
    [InlineData("no-entry-point")]
    [InlineData("undeclared-feature")]
    [InlineData("duplicate-export")]
    [InlineData("missing-target-feature")]
    [InlineData("dependency-version")]
    public async Task InvalidCandidate_RetainsWorkingProvider(string kind)
    {
        await using var test = new RuntimeTest();
        test.Package("base", "good");
        Assert.True(await test.Runtime.ReloadAsync());
        test.Package("patch", "patch", replaces: true);
        var path = Path.Combine(test.Root, "patch", "module.json");
        var manifest = JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(path))!;
        switch (kind)
        {
            case "missing-assembly":
                manifest = manifest with { EntryAssembly = "Absent.dll" };
                break;
            case "invalid-assembly":
                File.WriteAllText(Path.Combine(test.Root, "patch", "RuntimeModule.dll"), "not a DLL");
                break;
            case "no-entry-point":
                File.Copy(typeof(IModule).Assembly.Location, Path.Combine(test.Root, "patch", "NoModule.dll"));
                manifest = manifest with { EntryAssembly = "NoModule.dll" };
                break;
            case "undeclared-feature":
                manifest = manifest with { Contributions = [] };
                break;
            case "duplicate-export":
                manifest = manifest with { Contributions = [manifest.Contributions[0], manifest.Contributions[0]] };
                break;
            case "missing-target-feature":
                manifest = manifest with { Contributions = [manifest.Contributions[0] with { Id = "absent" }] };
                break;
            case "dependency-version":
                manifest = manifest with { Dependencies = [new() { Id = "base", Version = "9.0.0" }] };
                break;
        }
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Equal("good:1", await test.Read());
        Assert.Equal(1, test.Runtime.Status.Generation);
        Assert.NotEmpty(test.Runtime.Status.Diagnostics);
    }

    [Fact]
    public async Task RemovingRequiredDependency_BlocksOnlyItsDependents()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "base");
        test.Package("patch", "patch", replaces: true);
        test.Package("other", "other", feature: "test.other");
        Assert.True(await test.Runtime.ReloadAsync());
        test.Remove("base");
        Assert.False(await test.Runtime.ReloadAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Read());
        Assert.StartsWith("other:", await test.Read("test.other"));
        var patch = test.State("patch");
        Assert.Equal(PackageState.Blocked, patch.State);
        Assert.Contains("test.text", patch.Detail);
        test.Remove("patch");
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.Equal("other", Assert.Single(test.Runtime.Status.Modules).ModuleId);
    }

    [Fact]
    public async Task BrokenPackage_DoesNotPreventHealthyModulesFromLoading()
    {
        await using var test = new RuntimeTest();
        test.Package("good", "good");
        test.Package("broken", "broken", feature: "test.broken");
        File.WriteAllText(Path.Combine(test.Root, "broken", "module.json"), "{");
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Equal("good:1", await test.Read());
        Assert.Equal(PackageState.Active, test.State("good").State);
        Assert.Equal(PackageState.Failed, test.State("broken").State);
    }

    [Fact]
    public async Task DependentOfUnavailableModule_IsBlockedWhileOthersRun()
    {
        await using var test = new RuntimeTest();
        test.Package("good", "good");
        test.Package("child", "child", dependency: "absent", feature: "test.child");
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Equal("good:1", await test.Read());
        var child = test.State("child");
        Assert.Equal(PackageState.Blocked, child.State);
        Assert.Equal("child", child.ModuleId);
        Assert.Contains("absent", child.Detail);
    }

    [Fact]
    public async Task StartFailureOnInitialLoad_StartsRemainingModulesAndBlocksDependents()
    {
        await using var test = new RuntimeTest();
        test.Package("good", "good");
        test.Package("bad", "bad", feature: "test.bad", failStart: true);
        test.Package("child", "child", dependency: "bad", feature: "test.child");
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Equal("good:1", await test.Read());
        Assert.Equal("good", Assert.Single(test.Runtime.Status.Modules).ModuleId);
        Assert.Equal(PackageState.Failed, test.State("bad").State);
        Assert.Contains("Fixture activation failure", test.State("bad").Detail);
        Assert.Equal(PackageState.Blocked, test.State("child").State);
    }

    [Fact]
    public async Task InvalidUpdate_RunsAndReportsLastKnownGoodVersion()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "good");
        Assert.True(await test.Runtime.ReloadAsync());
        File.WriteAllText(Path.Combine(test.Root, "base", "module.json"), "{");
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Equal("good:1", await test.Read());
        Assert.Equal(1, test.Runtime.Status.Generation);
        var status = test.State("base");
        Assert.Equal(PackageState.Retained, status.State);
        Assert.Contains("last-known-good", status.Detail);
        test.Package("base", "fixed");
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.Equal("fixed:2", await test.Read());
        Assert.Equal(PackageState.Active, test.State("base").State);
    }

    [Fact]
    public async Task ManifestIdentity_IsAuthoritativeForAnyIdAndVersion()
    {
        await using var test = new RuntimeTest();
        test.Package("Vendor.EchoSounder", "echo", version: "0.0.7", folder: "echo");
        Assert.True(await test.Runtime.ReloadAsync());
        var module = Assert.Single(test.Runtime.Status.Modules);
        Assert.Equal("Vendor.EchoSounder", module.ModuleId);
        Assert.Equal("0.0.7", module.Version);
        Assert.Equal("Vendor.EchoSounder", Assert.Single(test.Runtime.Status.Features).ProviderId);
    }

    [Theory]
    [InlineData("1.4.2", "1.2.0", true)]
    [InlineData("1.2.0", "1.2.0", true)]
    [InlineData("1.1.0", "1.2.0", false)]
    [InlineData("2.0.0", "1.2.0", false)]
    public async Task DependencyVersion_IsAMinimumWithinTheSameMajor(string installed, string required, bool loads)
    {
        await using var test = new RuntimeTest();
        test.Package("base", "base", version: installed);
        test.Package("child", "child", dependency: "base", dependencyVersion: required, feature: "test.child");
        Assert.Equal(loads, await test.Runtime.ReloadAsync());
        Assert.Equal(PackageState.Active, test.State("base").State);
        Assert.Equal(loads ? PackageState.Active : PackageState.Blocked, test.State("child").State);
    }

    [Fact]
    public async Task MissingModulesDirectory_StartsEmpty()
    {
        await using var test = new RuntimeTest();
        Directory.Delete(test.Root, recursive: true);
        try
        {
            Assert.True(await test.Runtime.ReloadAsync());
            Assert.Empty(test.Runtime.Status.Modules);
            Assert.Empty(test.Runtime.Status.Packages);
        }
        finally
        {
            Directory.CreateDirectory(test.Root);
        }
    }

    [Fact]
    public async Task ExplicitReload_RetriesPreviouslyRejectedPackages()
    {
        await using var test = new RuntimeTest();
        test.Package("bad", "bad", failStart: true);
        Assert.False(await test.Runtime.ReloadAsync());
        var first = test.Runtime.Status.Diagnostics.Count(d => d.Contains("Fixture activation failure"));
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.True(test.Runtime.Status.Diagnostics.Count(d => d.Contains("Fixture activation failure")) > first);
    }

    [Fact]
    public async Task FailedQuiesce_PreventsLaterActivation()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "unsafe", failStop: true);
        Assert.True(await test.Runtime.ReloadAsync());
        test.Package("base", "replacement");
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.False(await test.Runtime.ReloadAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Read());
    }

    [Fact]
    public async Task PublicStatus_IsASnapshotOfTheCommittedGeneration()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "one");
        Assert.True(await test.Runtime.ReloadAsync());
        var original = test.Runtime.Status;
        test.Remove("base");
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.Equal(1, original.Generation);
        Assert.Equal("base", Assert.Single(original.Modules).ModuleId);
        Assert.Equal("base", Assert.Single(original.Features).ProviderId);
        Assert.Equal(2, test.Runtime.Status.Generation);
    }
}
