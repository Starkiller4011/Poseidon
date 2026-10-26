using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Poseidon.Core.Modules;
using Poseidon.Core.Runtime;
using Xunit;
using static CoreUI.Tests.ShellViewportTests;

namespace CoreUI.Tests;

// Deploys the real CoreUI and Display packages into a real runtime, so the viewport boundary is
// exercised through manifests, collectible load contexts and InvokeAsync rather than test doubles.
public sealed class ShellViewportIntegrationTests
{
    [AvaloniaFact]
    public async Task Display_IsActiveAndProvidesTheViewportWithoutCoreUi()
    {
        await using var modules = new DeployedModules();
        modules.Deploy("CoreDisplay");
        Assert.True(await modules.Runtime.ReloadAsync());
        Assert.Equal(PackageState.Active, Assert.Single(modules.Runtime.Status.Packages).State);
        var feature = Assert.Single(modules.Runtime.Status.Features);
        Assert.Equal((DisplayPoints.Viewport, "Core.Display"), (feature.PointId, feature.ProviderId));
    }

    [AvaloniaFact]
    public async Task CoreUi_HostsDisplaysViewportAndFallsBackWhenDisplayIsRemoved()
    {
        var opened = new List<Window>();
        using var subscription = Window.WindowOpenedEvent.AddClassHandler(typeof(Window),
            (sender, _) => opened.Add((Window)sender!), RoutingStrategies.Direct);
        await using var modules = new DeployedModules();
        modules.Deploy("CoreUI");
        modules.Deploy("CoreDisplay");
        Assert.True(await modules.Runtime.ReloadAsync());
        Assert.All(modules.Runtime.Status.Packages, p => Assert.Equal(PackageState.Active, p.State));

        var window = Assert.Single(opened, w => w.IsVisible);
        await WaitUntil(() => Text(window).Contains("No chart data loaded"));
        Assert.DoesNotContain("Chart viewport unavailable", Text(window));

        modules.Remove("CoreDisplay");
        Assert.True(await modules.Runtime.ReloadAsync());
        var replacement = Assert.Single(opened, w => w.IsVisible);
        Assert.NotSame(window, replacement);
        await WaitUntil(() => Text(replacement).Contains("Chart viewport unavailable"));
        Assert.DoesNotContain("No chart data loaded", Text(replacement));
    }

    private sealed class DeployedModules : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "Poseidon.Tests", Guid.NewGuid().ToString("N"));
        public ModuleRuntime Runtime { get; }

        public DeployedModules()
        {
            Directory.CreateDirectory(root);
            Runtime = new ModuleRuntime(root);
        }

        public void Deploy(string name)
        {
            var (assembly, manifest) = name == "CoreUI"
                ? (Path.Combine(AppContext.BaseDirectory, "CoreUI.dll"),
                   Path.Combine(AppContext.BaseDirectory, "module-under-test.json"))
                : (Path.Combine(AppContext.BaseDirectory, "packages", name, name + ".dll"),
                   Path.Combine(AppContext.BaseDirectory, "packages", name, "module.json"));
            var package = Path.Combine(root, name);
            Directory.CreateDirectory(package);
            File.Copy(assembly, Path.Combine(package, name + ".dll"), overwrite: true);
            File.Copy(manifest, Path.Combine(package, "module.json"), overwrite: true);
            File.WriteAllText(Path.Combine(package, ".ready"), "complete");
        }

        public void Remove(string name) => Directory.Delete(Path.Combine(root, name), recursive: true);

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            for (var attempt = 0; attempt < 10; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                try
                {
                    Directory.Delete(root, recursive: true);
                    return;
                }
                catch (IOException) { await Task.Delay(50); }
                catch (UnauthorizedAccessException) { await Task.Delay(50); }
            }
        }
    }
}
