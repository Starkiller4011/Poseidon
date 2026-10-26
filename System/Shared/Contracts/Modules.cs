using System.Text.Json.Serialization;

namespace Poseidon.Core.Modules;

public sealed record ModuleCapability(string CapabilityId, string DisplayName);

// Module identity, version and dependencies are discovered from the package's module.json;
// entry points deliberately do not restate them, so a deployed manifest cannot drift from code.
public interface IModule
{
    IReadOnlyList<ModuleCapability> Capabilities { get; }

    // Registration must be side-effect free; activation owns external resources.
    // Keys are the contribution IDs declared in the manifest, and every one must be declared.
    IReadOnlyDictionary<string, object> Features => new Dictionary<string, object>();
    ValueTask StartAsync(IModuleHost host, ModuleState? state, CancellationToken cancellationToken)
    {
        if (state is not null)
            throw new InvalidDataException("This module does not support state migration.");
        return ValueTask.CompletedTask;
    }
    ValueTask<ModuleState?> CaptureStateAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<ModuleState?>(null);
    // Stop must also clean up a partially failed Start and must be idempotent.
    ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed record ModuleState(int SchemaVersion, byte[] Data);
public sealed record ModuleStatus(string ModuleId, string Version, string PackagePath);
// An effective contribution: FeatureId is the contribution ID and PointId the extension point it targets.
public sealed record FeatureStatus(string FeatureId, string ProviderId, string? PointId = null);

public enum PackageState
{
    // Loaded and running from the currently deployed package.
    Active,
    // Running a last-known-good copy because the deployed package is incomplete or was rejected.
    Retained,
    // Discovered without a .ready marker and not yet loaded.
    Pending,
    // The package itself is invalid or failed to load/start.
    Failed,
    // The package is valid but cannot run because a dependency or override target is unavailable.
    Blocked
}

public sealed record PackageStatus(
    string PackagePath, string? ModuleId, string? Version, PackageState State, string? Detail = null);

public sealed record RuntimeStatus(
    long Generation,
    IReadOnlyList<ModuleStatus> Modules,
    IReadOnlyList<FeatureStatus> Features,
    IReadOnlyList<string> Diagnostics)
{
    public int RetainedLoadContexts { get; init; }
    public IReadOnlyList<PackageStatus> Packages { get; init; } = [];

    public bool HasContribution(string pointId)
        => Features.Any(f => string.Equals(f.PointId, pointId, StringComparison.Ordinal));
}

public interface IModuleHost
{
    RuntimeStatus Status { get; }
    void RequestReload();
    void RequestShutdown();
    Task<TResult> InvokeAsync<TFeature, TResult>(
        string featureId, Func<TFeature, TResult> action, CancellationToken cancellationToken = default)
        where TFeature : class;
}

public sealed record ModuleDependency
{
    public required string Id { get; init; }
    // Minimum compatible version: satisfied by the same major version at or above this value.
    public required string Version { get; init; }

    public bool IsSatisfiedBy(Version installed)
    {
        var required = System.Version.Parse(Version);
        return installed.Major == required.Major && installed >= required;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PointCardinality
{
    // At most one contribution ID may target the point (overrides of that ID are still allowed).
    Single,
    // Any number of independent contributions may target the point.
    Multiple
}

// A seam that a module (or the host) opens for other modules to extend. Contract is the full name of
// a host-shared interface that every contribution to the point must implement.
public sealed record ExtensionPoint
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public PointCardinality Cardinality { get; init; } = PointCardinality.Multiple;
    public string? Contract { get; init; }
}

// A contribution adds a new feature to an extension point, or with Replaces overrides the feature
// with the same ID that another module already contributes. Version is the point contract version
// the contribution targets. Among several replacements of one feature the highest Priority wins.
public sealed record Contribution
{
    public required string Point { get; init; }
    public required string Id { get; init; }
    public required string Version { get; init; }
    public bool Replaces { get; init; }
    public int Priority { get; init; }
}

public sealed record ModuleManifest
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string EntryAssembly { get; init; }
    public int ContractVersion { get; init; } = 1;
    public ModuleDependency[] Dependencies { get; init; } = [];
    public ExtensionPoint[] Provides { get; init; } = [];
    public Contribution[] Contributions { get; init; } = [];
}

// A small shared feature used by the shell and loader integration fixtures.
public interface ITextFeature
{
    string Text { get; }
}
