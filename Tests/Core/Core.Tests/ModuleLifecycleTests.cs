using System.Text.Json;
using System.Text.Json.Nodes;
using Poseidon.Core.Modules;
using Xunit;

namespace Core.Tests;

public sealed partial class ModuleRuntimeTests
{
    private static void ConfigureFixture(RuntimeTest test, string id, string property, JsonNode value)
    {
        var path = Path.Combine(test.Root, id, "fixture.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json[property] = value;
        File.WriteAllText(path, json.ToJsonString());
    }

    [Theory]
    [InlineData("null", "Null feature")]
    [InlineData("private", "host-shared interface")]
    public async Task InvalidFeatureValue_IsReportedWithoutActivation(string mode, string diagnostic)
    {
        await using var test = new RuntimeTest();
        test.Package("base", "text");
        ConfigureFixture(test, "base", "FeatureMode", JsonValue.Create(mode)!);
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Contains(test.Runtime.Status.Diagnostics, d => d.Contains(diagnostic));
        Assert.Empty(test.Runtime.Status.Modules);
    }

    [Fact]
    public async Task CaptureFailure_PreservesActiveInstanceWithoutStoppingIt()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "good");
        ConfigureFixture(test, "base", "FailCapture", JsonValue.Create(true)!);
        Assert.True(await test.Runtime.ReloadAsync());
        test.Package("base", "new");
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Equal("good:1", await test.Read());
        Assert.Contains(test.Runtime.Status.Diagnostics, d => d.Contains("Fixture capture failure"));
    }

    [Fact]
    public async Task RollbackFailure_WithdrawsProvidersAndRejectsFurtherReloads()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "old");
        ConfigureFixture(test, "base", "FailRollback", JsonValue.Create(true)!);
        Assert.True(await test.Runtime.ReloadAsync());
        test.Package("base", "bad", failStart: true);
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Empty(test.Runtime.Status.Modules);
        Assert.Empty(test.Runtime.Status.Features);
        Assert.Contains(test.Runtime.Status.Diagnostics, d => d.Contains("Rollback failed"));
        var package = Assert.Single(test.Runtime.Status.Packages);
        Assert.Equal(PackageState.Failed, package.State);
        Assert.Contains("restart required", package.Detail);
        Assert.False(await test.Runtime.ReloadAsync());
    }

    [Fact]
    public async Task PartialStartCleanupFailure_IsExplicitAndDoesNotAttemptUnsafeRollback()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "good");
        Assert.True(await test.Runtime.ReloadAsync());
        test.Package("base", "bad", failStart: true, failStop: true);
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Empty(test.Runtime.Status.Features);
        Assert.Contains(test.Runtime.Status.Diagnostics, d => d.Contains("Cleanup failed"));
    }

    [Fact]
    public async Task FeatureInvocationFromLifecycle_IsRejectedWithoutDeadlock()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "text");
        ConfigureFixture(test, "base", "InvokeOnStart", JsonValue.Create(true)!);
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Contains(test.Runtime.Status.Diagnostics, d => d.Contains("lifecycle methods"));
    }

    [Fact]
    public async Task DependencyOrder_DeterminesStartAndReverseStopOrder()
    {
        await using var test = new RuntimeTest();
        test.Package("z-base", "base");
        test.Package("a-patch", "patch", replaces: true);
        var log = Path.Combine(test.Root, "lifecycle.log");
        ConfigureFixture(test, "z-base", "EventLog", JsonValue.Create(log)!);
        ConfigureFixture(test, "a-patch", "EventLog", JsonValue.Create(log)!);
        Assert.True(await test.Runtime.ReloadAsync());
        Assert.Equal(["z-base:start", "a-patch:start"], File.ReadAllLines(log));
        await test.Runtime.DisposeAsync();
        Assert.Equal(["z-base:start", "a-patch:start", "a-patch:stop", "z-base:stop"], File.ReadAllLines(log));
    }

    [Fact]
    public async Task MissingReplacementFeature_IsDistinctFromMissingModule()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "base");
        test.Package("patch", "patch", replaces: true);
        ConfigureFixture(test, "patch", "FeatureId", JsonValue.Create("another.feature")!);
        var path = Path.Combine(test.Root, "patch", "module.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!;
        manifest["Contributions"]![0]!["Id"] = "another.feature";
        File.WriteAllText(path, manifest.ToJsonString());
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Contains(test.Runtime.Status.Diagnostics, d => d.Contains("Replacement target does not exist"));
    }

    [Fact]
    public async Task NativeDependency_IsRejectedByReloadableContext()
    {
        await using var test = new RuntimeTest();
        test.Package("base", "native-dependency");
        Assert.False(await test.Runtime.ReloadAsync());
        Assert.Contains(test.Runtime.Status.Diagnostics, d => d.Contains("not reload-safe"));
        Assert.Empty(test.Runtime.Status.Modules);
    }
}
