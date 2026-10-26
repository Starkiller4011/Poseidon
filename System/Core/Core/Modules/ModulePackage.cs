using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Poseidon.Core.Modules;

namespace Poseidon.Core.Runtime;

// Directory is the deployed package location reported to users. ContentDirectory is where files
// are copied from; it differs only when a last-known-good shadow copy is retained.
internal sealed record PackageSource(string Directory, string Fingerprint, ModuleManifest Manifest)
{
    public string ContentDirectory { get; init; } = Directory;
    public Version Version => System.Version.Parse(Manifest.Version);
}

internal sealed class ModulePackage : IDisposable
{
    private readonly PluginLoadContext loadContext;
    private readonly string shadowDirectory;
    public PackageSource Source { get; }
    public IModule Instance { get; }
    public IReadOnlyDictionary<string, object> Features { get; }
    public WeakReference ContextReference { get; }

    private ModulePackage(PackageSource source, string shadow, PluginLoadContext context, IModule instance)
    {
        Source = source;
        shadowDirectory = shadow;
        loadContext = context;
        Instance = instance;
        Features = new Dictionary<string, object>(instance.Features, StringComparer.Ordinal);
        ContextReference = new WeakReference(context);
    }

    public static PackageSource Inspect(string directory)
    {
        // Unknown members are rejected so a misspelled or obsolete declaration fails loudly.
        var manifest = JsonSerializer.Deserialize<ModuleManifest>(
            File.ReadAllText(Path.Combine(directory, "module.json")),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
            })
            ?? throw new InvalidDataException($"Empty manifest in {directory}.");
        if (string.IsNullOrWhiteSpace(manifest.Id) || !Version.TryParse(manifest.Version, out _) ||
            manifest.ContractVersion != 1 || manifest.Dependencies is null ||
            manifest.Provides is null || manifest.Contributions is null)
            throw new InvalidDataException($"Invalid identity/version or incompatible contract in {directory}.");
        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly) ||
            manifest.EntryAssembly.IndexOfAny(['\\', '/']) >= 0 ||
            Path.GetFileName(manifest.EntryAssembly) != manifest.EntryAssembly ||
            !manifest.EntryAssembly.EndsWith(".dll", StringComparison.Ordinal))
            throw new InvalidDataException($"Entry assembly must be a package-local DLL in {directory}.");
        foreach (var dependency in manifest.Dependencies)
            ValidateDependency(dependency);
        foreach (var point in manifest.Provides)
        {
            if (point is null || string.IsNullOrWhiteSpace(point.Id) || !Version.TryParse(point.Version, out _) ||
                !Enum.IsDefined(point.Cardinality) || point.Contract is not null && string.IsNullOrWhiteSpace(point.Contract))
                throw new InvalidDataException($"Invalid extension point declaration in {directory}.");
        }
        foreach (var contribution in manifest.Contributions)
        {
            if (contribution is null || string.IsNullOrWhiteSpace(contribution.Point) ||
                string.IsNullOrWhiteSpace(contribution.Id) || !Version.TryParse(contribution.Version, out _))
                throw new InvalidDataException($"Invalid contribution in {directory}.");
            if (contribution.Priority != 0 && !contribution.Replaces)
                throw new InvalidDataException(
                    $"Contribution {contribution.Id} in {directory} sets a priority but does not replace anything.");
        }
        return new PackageSource(directory, Fingerprint(directory), manifest);
    }

    private static void ValidateDependency(ModuleDependency? dependency)
    {
        if (dependency is null || string.IsNullOrWhiteSpace(dependency.Id) ||
            !Version.TryParse(dependency.Version, out _))
            throw new InvalidDataException("Dependencies require an ID and a version.");
    }

    public static string Fingerprint(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in EnumeratePackageFiles(directory))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(directory, path)));
            hash.AppendData([0]);
            using var stream = File.OpenRead(path);
            hash.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static IEnumerable<string> EnumeratePackageFiles(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Symbolic links are not supported in module packages: {directory}");

        foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Symbolic links are not supported in module packages: {path}");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var child in EnumeratePackageFiles(path))
                    yield return child;
            }
            else
            {
                yield return path;
            }
        }
    }

    public static ModulePackage Load(PackageSource source, string cacheRoot,
        Action<WeakReference, string> onFailure)
    {
        var shadow = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(shadow);
        PluginLoadContext? context = null;
        try
        {
            var content = source.ContentDirectory;
            foreach (var path in Directory.EnumerateFiles(content, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(shadow, Path.GetRelativePath(content, path));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(path, destination);
            }
            if (Fingerprint(shadow) != source.Fingerprint ||
                Fingerprint(content) != source.Fingerprint ||
                !File.Exists(Path.Combine(content, ".ready")))
                throw new IOException($"Package changed during staging: {source.Directory}");

            var entry = Path.Combine(shadow, source.Manifest.EntryAssembly);
            if (!File.Exists(entry))
                throw new FileNotFoundException(
                    $"Entry assembly {source.Manifest.EntryAssembly} is not present in {source.Directory}.");
            context = new PluginLoadContext(entry);
            var assembly = context.LoadFromAssemblyPath(entry);
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                throw new InvalidDataException(
                    $"Cannot load types from {source.Manifest.EntryAssembly}: " +
                    string.Join("; ", ex.LoaderExceptions.Where(e => e is not null).Select(e => e!.Message).Distinct()), ex);
            }
            var entryPoints = types.Where(t =>
                typeof(IModule).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface).ToArray();
            if (entryPoints.Length != 1)
                throw new InvalidDataException(
                    $"Expected exactly one IModule implementation in {source.Manifest.EntryAssembly}; found {entryPoints.Length}.");
            if (entryPoints[0].GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidDataException($"{entryPoints[0].FullName} requires a public parameterless constructor.");
            var instance = (IModule?)Activator.CreateInstance(entryPoints[0])
                ?? throw new InvalidDataException($"Cannot create module in {entry}.");
            return new ModulePackage(source, shadow, context, instance);
        }
        catch
        {
            context?.Unload();
            onFailure(new WeakReference(context), shadow);
            // The runtime reports this package directory and the original error.
            throw;
        }
    }

    public void Dispose() => loadContext.Unload();

    public string ShadowDirectory => shadowDirectory;
}

internal sealed class PluginLoadContext(string entryAssembly)
    : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver resolver = new(entryAssembly);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name == typeof(IModule).Assembly.GetName().Name ||
            assemblyName.Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true)
            return Default.LoadFromAssemblyName(assemblyName);
        var path = resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
        => throw new NotSupportedException(
            $"In-process native module dependency '{unmanagedDllName}' is not reload-safe. Use a worker process.");
}
