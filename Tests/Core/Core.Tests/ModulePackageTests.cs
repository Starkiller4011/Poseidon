using System.Text.Json;
using System.Text.Json.Nodes;
using Poseidon.Core.Modules;
using Poseidon.Core.Runtime;
using Xunit;

namespace Core.Tests;

public sealed class ModulePackageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Poseidon.PackageTests", Guid.NewGuid().ToString("N"));

    public ModulePackageTests() => Directory.CreateDirectory(root);

    private void WriteManifest(Action<JsonObject>? change = null)
    {
        var json = JsonSerializer.SerializeToNode(new ModuleManifest
        {
            Id = "test", Version = "1.0.0", EntryAssembly = "Test.dll"
        })!.AsObject();
        change?.Invoke(json);
        File.WriteAllText(Path.Combine(root, "module.json"), json.ToJsonString());
    }

    [Fact]
    public void Inspect_AppliesDefaultsAndRecordsSourceIdentity()
    {
        WriteManifest();
        var source = ModulePackage.Inspect(root);
        Assert.Equal(root, source.Directory);
        Assert.Equal(ModulePackage.Fingerprint(root), source.Fingerprint);
        Assert.Equal("test", source.Manifest.Id);
        Assert.Equal(1, source.Manifest.ContractVersion);
        Assert.Empty(source.Manifest.Dependencies);
        Assert.Empty(source.Manifest.Provides);
        Assert.Empty(source.Manifest.Contributions);
    }

    [Fact]
    public void Inspect_AcceptsCamelCaseManifest()
    {
        File.WriteAllText(Path.Combine(root, "module.json"),
            """{"id":"test","version":"1.0.0","entryAssembly":"Test.dll"}""");
        Assert.Equal("test", ModulePackage.Inspect(root).Manifest.Id);
    }

    [Theory]
    [InlineData("Id", "")]
    [InlineData("Id", " ")]
    [InlineData("Version", "invalid")]
    [InlineData("EntryAssembly", "")]
    [InlineData("EntryAssembly", "Test.exe")]
    [InlineData("EntryAssembly", "../Test.dll")]
    [InlineData("EntryAssembly", "..\\Test.dll")]
    [InlineData("EntryAssembly", "/Test.dll")]
    public void Inspect_RejectsInvalidScalarFields(string field, string value)
    {
        WriteManifest(json => json[field] = value);
        Assert.Throws<InvalidDataException>(() => ModulePackage.Inspect(root));
    }

    [Theory]
    [InlineData("Dependencies")]
    [InlineData("Provides")]
    [InlineData("Contributions")]
    public void Inspect_RejectsNullCollections(string field)
    {
        WriteManifest(json => json[field] = null);
        Assert.Throws<InvalidDataException>(() => ModulePackage.Inspect(root));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{}")]
    public void Inspect_RejectsMissingOrMalformedManifest(string contents)
    {
        File.WriteAllText(Path.Combine(root, "module.json"), contents);
        if (contents == "null")
            Assert.Throws<InvalidDataException>(() => ModulePackage.Inspect(root));
        else
            Assert.Throws<JsonException>(() => ModulePackage.Inspect(root));
    }

    [Theory]
    [InlineData("""[null]""")]
    [InlineData("""[{"Id":"","Version":"1.0.0"}]""")]
    [InlineData("""[{"Id":"dependency","Version":"bad"}]""")]
    public void Inspect_RejectsInvalidDependencies(string json)
    {
        WriteManifest(manifest => manifest["Dependencies"] = JsonNode.Parse(json));
        Assert.Throws<InvalidDataException>(() => ModulePackage.Inspect(root));
    }

    [Theory]
    [InlineData("""[null]""")]
    [InlineData("""[{"Point":"","Id":"feature","Version":"1.0.0"}]""")]
    [InlineData("""[{"Point":"p","Id":"","Version":"1.0.0"}]""")]
    [InlineData("""[{"Point":"p","Id":"feature","Version":"bad"}]""")]
    [InlineData("""[{"Point":"p","Id":"feature","Version":"1.0.0","Priority":2}]""")]
    public void Inspect_RejectsInvalidContributions(string json)
    {
        WriteManifest(manifest => manifest["Contributions"] = JsonNode.Parse(json));
        Assert.Throws<InvalidDataException>(() => ModulePackage.Inspect(root));
    }

    [Theory]
    [InlineData("""[null]""")]
    [InlineData("""[{"Id":"","Version":"1.0.0"}]""")]
    [InlineData("""[{"Id":"p","Version":"bad"}]""")]
    [InlineData("""[{"Id":"p","Version":"1.0.0","Contract":" "}]""")]
    [InlineData("""[{"Id":"p","Version":"1.0.0","Cardinality":42}]""")]
    public void Inspect_RejectsInvalidExtensionPoints(string json)
    {
        WriteManifest(manifest => manifest["Provides"] = JsonNode.Parse(json));
        Assert.Throws<InvalidDataException>(() => ModulePackage.Inspect(root));
    }

    [Fact]
    public void Inspect_ReadsPointsAndContributionsFromCamelCaseManifest()
    {
        File.WriteAllText(Path.Combine(root, "module.json"), """
            {"id":"test","version":"1.0.0","entryAssembly":"Test.dll",
             "provides":[{"id":"chart.layer","version":"1.2.0","cardinality":"single","contract":"Some.Contract"}],
             "contributions":[{"point":"shell.host","id":"x","version":"1.0.0","replaces":true,"priority":3}]}
            """);
        var manifest = ModulePackage.Inspect(root).Manifest;
        var point = Assert.Single(manifest.Provides);
        Assert.Equal(PointCardinality.Single, point.Cardinality);
        Assert.Equal("Some.Contract", point.Contract);
        var contribution = Assert.Single(manifest.Contributions);
        Assert.True(contribution.Replaces);
        Assert.Equal(3, contribution.Priority);
    }

    [Theory]
    [InlineData("""{"id":"test","version":"1.0.0","entryAssembly":"Test.dll","exports":[]}""")]
    [InlineData("""{"id":"test","version":"1.0.0","entryAssembly":"Test.dll","changesUI":true}""")]
    public void Inspect_RejectsUnknownOrObsoleteFields(string contents)
    {
        File.WriteAllText(Path.Combine(root, "module.json"), contents);
        Assert.Throws<JsonException>(() => ModulePackage.Inspect(root));
    }

    [Fact]
    public void Fingerprint_IsStableAndIncludesResourceNamesAndContents()
    {
        WriteManifest();
        var resource = Path.Combine(root, "resource.txt");
        File.WriteAllText(resource, "one");
        var first = ModulePackage.Fingerprint(root);
        Assert.Equal(first, ModulePackage.Fingerprint(root));
        File.SetLastWriteTimeUtc(resource, DateTime.UtcNow.AddDays(-1));
        Assert.Equal(first, ModulePackage.Fingerprint(root));
        File.WriteAllText(resource, "two");
        var second = ModulePackage.Fingerprint(root);
        Assert.NotEqual(first, second);
        File.Move(resource, Path.Combine(root, "renamed.txt"));
        Assert.NotEqual(second, ModulePackage.Fingerprint(root));
    }

    [Fact]
    public void Staging_RejectsChangedPackagesAndReportsCleanupLocation()
    {
        WriteManifest();
        File.WriteAllText(Path.Combine(root, ".ready"), "ready");
        var package = Path.Combine(root, "package");
        Directory.CreateDirectory(package);
        File.Copy(Path.Combine(root, "module.json"), Path.Combine(package, "module.json"));
        File.Copy(Path.Combine(root, ".ready"), Path.Combine(package, ".ready"));
        var source = ModulePackage.Inspect(package);
        File.WriteAllText(Path.Combine(package, "changed.txt"), "changed");
        string? failedDirectory = null;
        Assert.Throws<IOException>(() => ModulePackage.Load(source, Path.Combine(root, "cache"),
            (_, path) => failedDirectory = path));
        Assert.NotNull(failedDirectory);
        Assert.True(Directory.Exists(failedDirectory));
    }

    [Fact]
    public void Staging_RequiresCompletionMarkerEvenWhenFingerprintMatches()
    {
        WriteManifest();
        var package = Path.Combine(root, "package");
        Directory.CreateDirectory(package);
        File.Copy(Path.Combine(root, "module.json"), Path.Combine(package, "module.json"));
        var source = ModulePackage.Inspect(package);
        Assert.Throws<IOException>(() => ModulePackage.Load(source, Path.Combine(root, "cache"), (_, _) => { }));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
