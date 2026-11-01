using Poseidon.Core.Modules;
using Poseidon.Core.Runtime;
using Xunit;

namespace CoreCharts.Tests;

// Deploys the real CoreCharts package (its complete build output) into a real module runtime.
public sealed class ChartsRuntimeTests
{
    // C27
    [Fact]
    public async Task Runtime_WithCharts_ProvidesTheLibraryWhichImportsAnExchangeSet()
    {
        await using var modules = new DeployedModules();
        modules.DeployCharts();
        Assert.True(await modules.Runtime.ReloadAsync());
        Assert.Equal(PackageState.Active, Assert.Single(modules.Runtime.Status.Packages, p => p.ModuleId == "Core.Charts").State);
        Assert.True(modules.Runtime.Status.HasContribution(ChartPoints.Library));

        var import = await modules.Runtime.InvokeAsync<IChartLibrary, Task<ChartImportResult>>(
            ChartPoints.LibraryFeatureId, library => library.ImportAsync(Fixtures.ReleasedPowerUp));

        Assert.Equal(ChartImportStatus.Loaded, (await import).Status);
    }

    // C27: without Charts the host reports the feature unavailable; nothing else supplies a reader.
    [Fact]
    public async Task Runtime_WithoutCharts_ReportsTheLibraryUnavailable()
    {
        await using var modules = new DeployedModules();
        await modules.Runtime.ReloadAsync();

        Assert.False(modules.Runtime.Status.HasContribution(ChartPoints.Library));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            modules.Runtime.InvokeAsync<IChartLibrary, Task<ChartImportResult>>(
                ChartPoints.LibraryFeatureId, library => library.ImportAsync(Fixtures.ReleasedPowerUp)));
        Assert.Contains("unavailable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DeployedModules : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "Poseidon.Tests", Guid.NewGuid().ToString("N"));

        public DeployedModules()
        {
            Directory.CreateDirectory(root);
            Runtime = new ModuleRuntime(root);
        }

        public ModuleRuntime Runtime { get; }

        // The module's own build output, as the DeployModule target would package it.
        public void DeployCharts()
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var output = Path.Combine(Fixtures.RepoRoot, "System", "Core", "CoreCharts", "bin", configuration, "net10.0");
            Assert.True(File.Exists(Path.Combine(output, "CoreCharts.dll")), $"CoreCharts build output not found at {output}");
            var package = Path.Combine(root, "CoreCharts");
            foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(package, Path.GetRelativePath(output, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
            File.Copy(Path.Combine(AppContext.BaseDirectory, "module-under-test.json"), Path.Combine(package, "module.json"), overwrite: true);
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
