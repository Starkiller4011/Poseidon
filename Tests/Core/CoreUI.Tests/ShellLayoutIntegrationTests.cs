using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Poseidon.Core.Modules;
using Poseidon.Core.Runtime;
using Xunit;
using static CoreUI.Tests.ShellViewportTests;

namespace CoreUI.Tests;

// H1, H2, H4, H5: deploys the real CoreUI package into an isolated temp directory per test, so each
// test can write its own console.json without racing the project's shared default (see
// ConsoleHardwareConfigurationTests and the csproj's default console.json for the fake-host unit
// tests).
public sealed class ShellLayoutIntegrationTests
{
    [AvaloniaFact]
    public async Task SingleDisplayMode_OpensOneMainWindowPlusTheTid_AndTheChartRegionIsVisible()
    {
        var opened = new List<Window>();
        using var subscription = Window.WindowOpenedEvent.AddClassHandler(typeof(Window),
            (sender, _) => opened.Add((Window)sender!), RoutingStrategies.Direct);
        await using var modules = new DeployedModules();
        modules.Deploy("CoreUI", """{ "displayMode": "Single" }""");
        Assert.True(await modules.Runtime.ReloadAsync());
        Assert.All(modules.Runtime.Status.Packages, p => Assert.Equal(PackageState.Active, p.State));

        await WaitUntil(() => opened.Count(w => w.IsVisible) == 2 &&
            opened.Any(w => w.IsVisible && Text(w).Contains("Chart viewport unavailable")));
    }

    [AvaloniaFact]
    public async Task DualDisplayMode_OpensTwoMainWindowsPlusTheTid_OneWithOnlyTheChartAndSidePanels()
    {
        var opened = new List<Window>();
        using var subscription = Window.WindowOpenedEvent.AddClassHandler(typeof(Window),
            (sender, _) => opened.Add((Window)sender!), RoutingStrategies.Direct);
        await using var modules = new DeployedModules();
        modules.Deploy("CoreUI", """{ "displayMode": "Dual" }""");
        Assert.True(await modules.Runtime.ReloadAsync());
        Assert.All(modules.Runtime.Status.Packages, p => Assert.Equal(PackageState.Active, p.State));

        await WaitUntil(() => opened.Count(w => w.IsVisible) == 3 &&
            opened.Any(w => w.IsVisible && Text(w).Contains("Chart viewport unavailable")));
    }

    [AvaloniaFact]
    public async Task MissingConfigurationFile_BlocksTheCoreUiPackageWithAClearDiagnostic()
    {
        await using var modules = new DeployedModules();
        modules.Deploy("CoreUI", consoleJson: null);
        Assert.False(await modules.Runtime.ReloadAsync());
        var package = Assert.Single(modules.Runtime.Status.Packages, p => p.ModuleId == "Core.UI");
        Assert.Equal(PackageState.Failed, package.State);
        Assert.Contains("console.json", package.Detail ?? "");
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

        public void Deploy(string name, string? consoleJson)
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
            if (consoleJson is not null)
                File.WriteAllText(Path.Combine(package, "console.json"), consoleJson);
            File.WriteAllText(Path.Combine(package, ".ready"), "complete");
        }

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
