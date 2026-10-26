using System.Reflection;
using System.Text.Json;
using Poseidon.Core.Modules;
using Xunit;

namespace Poseidon.Tests.Shared;

// Compiled independently into each module's test project.
public sealed class ModuleDeclarationTests
{
    public static ModuleManifest DeployedManifest()
        => JsonSerializer.Deserialize<ModuleManifest>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "module-under-test.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static (IModule Module, ModuleManifest Manifest) Load()
    {
        var manifest = DeployedManifest();
        var assembly = Assembly.Load(Path.GetFileNameWithoutExtension(manifest.EntryAssembly));
        var type = Assert.Single(assembly.GetTypes(), t => typeof(IModule).IsAssignableFrom(t) &&
            !t.IsInterface && !t.IsAbstract);
        return ((IModule)Activator.CreateInstance(type)!, manifest);
    }

    [Fact]
    public void EntryPoint_MatchesDeploymentManifest()
    {
        var (module, manifest) = Load();
        Assert.False(string.IsNullOrWhiteSpace(manifest.Id));
        Assert.True(Version.TryParse(manifest.Version, out _));
        Assert.Equal(1, manifest.ContractVersion);
        Assert.Equal(module.GetType().Assembly.GetName().Name + ".dll", manifest.EntryAssembly);
        Assert.Null(module.GetType().Assembly.EntryPoint);
    }

    [Fact]
    public void EntryPoint_DoesNotRestateManifestIdentity()
    {
        var (module, _) = Load();
        Assert.Null(module.GetType().GetProperty("ModuleId"));
        Assert.Null(module.GetType().GetProperty("ModuleVersion"));
    }

    [Fact]
    public void FeaturesAndCapabilities_AreUniqueAndWellFormed()
    {
        var (module, manifest) = Load();
        Assert.Equal(module.Capabilities.Count,
            module.Capabilities.Select(c => c.CapabilityId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(module.Capabilities, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.CapabilityId));
            Assert.False(string.IsNullOrWhiteSpace(c.DisplayName));
        });
        Assert.Equal(manifest.Contributions.Select(c => c.Id).Order(), module.Features.Keys.Order());
    }

    [Fact]
    public void Module_HasNoReferenceToHostOrOtherModuleImplementations()
    {
        var (module, _) = Load();
        string[] implementations = ["Core", "CoreUI", "CoreDisplay", "CoreCharts", "CoreTracks",
            "CoreNavigation", "CoreAIS", "CoreRadar", "CoreSonar"];
        Assert.DoesNotContain(module.GetType().Assembly.GetReferencedAssemblies(),
            a => implementations.Contains(a.Name, StringComparer.Ordinal));
    }

    [Fact]
    public void SensorDependency_IsTracksOnly()
    {
        var (module, manifest) = Load();
        if (module.GetType().Assembly.GetName().Name is "CoreAIS" or "CoreRadar" or "CoreSonar")
        {
            var dependency = Assert.Single(manifest.Dependencies);
            Assert.Equal("Core.Tracks", dependency.Id);
            Assert.True(Version.TryParse(dependency.Version, out _));
        }
        else
            Assert.Empty(manifest.Dependencies);
    }

    [Fact]
    public async Task StopBeforeStart_IsSafeAndIdempotent()
    {
        var (module, manifest) = Load();
        // A shell's dispatcher-bound lifecycle is exercised by its headless UI tests.
        if (manifest.Contributions.Any(c => c.Point == ExtensionPoints.Shell))
            return;
        await module.StopAsync(CancellationToken.None);
        await module.StopAsync(CancellationToken.None);
        await module.StartAsync(null!, null, CancellationToken.None);
        Assert.Null(await module.CaptureStateAsync(CancellationToken.None));
        await module.StopAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            module.StartAsync(null!, new ModuleState(99, []), CancellationToken.None).AsTask());
    }
}
