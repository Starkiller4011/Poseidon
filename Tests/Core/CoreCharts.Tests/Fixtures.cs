using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace CoreCharts.Tests;

internal static class Fixtures
{
    private static readonly object ExtractGate = new();

    public static string RepoRoot { get; } = FindRepoRoot();
    public static string Data => Path.Combine(RepoRoot, "Reference", "Data");
    public static string S101TestDatasets => Path.Combine(Data, "S-101-Test-Datasets");
    public static string S164SubGroup => Path.Combine(Data, "S-164-Sub-Group");
    public static string S100Tds => Path.Combine(Data, "s100tds");

    // UKHO-licensed data (UKHO and SHOM S-101): git-ignored, present only on machines that downloaded it.
    public static string LocalUkhoS101 => Path.Combine(Data, "S-101", "S-101");
    public static bool HasLocalUkhoData => Directory.Exists(LocalUkhoS101);

    // S-101 1.1.0 scenario sets in S-101-Test-Datasets/S-164 (pre-operational edition).
    public static string Old164(string scenario)
        => Path.Combine(S101TestDatasets, "S-164", "TestDataSets", scenario, "S100_ROOT");

    // Unreleased sub-group exchange sets kept unzipped in the repository.
    public static string SubGroupSet(string name)
        => Path.Combine(S164SubGroup, "data", "ExchangeSets", "S-100", name, "S100_ROOT");

    // Released S-164 2.0.0 Part 2 exchange sets ship as zips; each is extracted once into a folder named
    // after the zip's hash, so repeated runs reuse it and a changed zip gets a fresh copy.
    public static string Released(string section, string name)
    {
        var zip = Path.Combine(S164SubGroup, "data", "ExchangeSets", "S-164", "Part2", section, name, name + ".zip");
        Assert.True(File.Exists(zip), $"Missing fixture {zip}: is the S-164-Sub-Group submodule checked out?");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)))[..16];
        var target = Path.Combine(Path.GetTempPath(), "Poseidon.Tests", "s164-" + hash);
        lock (ExtractGate)
        {
            if (!File.Exists(Path.Combine(target, ".complete")))
            {
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
                ExtractNormalised(zip, target);
                File.WriteAllText(Path.Combine(target, ".complete"), "complete");
            }
        }
        return FindS100Root(target);
    }

    public static string SequentialUpdate(int n) => Released("2.3_AutomaticUpdates", $"SequentialUpdate{n}");

    public static string ReleasedPowerUp => Released("2.2_DatasetLoading", "PowerUp");

    public static string LocalShomSet(string folderPattern)
    {
        var root = Directory.EnumerateDirectories(LocalUkhoS101, "S100_ROOT", SearchOption.AllDirectories)
            .Single(d => d.Replace('\\', '/').Contains(folderPattern, StringComparison.Ordinal));
        return root;
    }

    public static string LocalUkhoGb => Path.Combine(LocalUkhoS101, "S-101_GB_Apr26", "S100_ROOT");

    public static string DatasetFile(string root, string fileName)
        => Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).Single();

    private static string FindS100Root(string directory)
        => Directory.EnumerateDirectories(directory, "S100_ROOT", SearchOption.AllDirectories).Single();

    // Some IHO zips store entry names with backslashes, which Linux would extract as literal file names.
    private static void ExtractNormalised(string zip, string target)
    {
        var fullTarget = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/'))
                continue;
            var path = Path.GetFullPath(Path.Combine(target, name));
            if (!path.StartsWith(fullTarget, StringComparison.Ordinal))
                throw new InvalidDataException($"Zip entry escapes the extraction folder: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Poseidon.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Repository root (Poseidon.slnx) not found above " + AppContext.BaseDirectory);
    }
}

// A writable copy of a fixture folder for tests that alter files; deleted on dispose.
internal sealed class TempCopy : IDisposable
{
    public TempCopy(string source)
    {
        Root = Path.Combine(Path.GetTempPath(), "Poseidon.Tests", "copy-" + Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(Root, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            System.IO.File.Copy(file, target);
        }
    }

    public TempCopy() => Directory.CreateDirectory(Root = Path.Combine(Path.GetTempPath(), "Poseidon.Tests",
        "temp-" + Guid.NewGuid().ToString("N")));

    public string Root { get; }

    public string PathOf(string fileName) => Fixtures.DatasetFile(Root, fileName);

    public string Catalogue => Directory.EnumerateFiles(Root, "*", SearchOption.TopDirectoryOnly)
        .Single(f => Path.GetFileName(f).Equals("CATALOG.XML", StringComparison.OrdinalIgnoreCase));

    public void EditCatalogue(Func<string, string> edit)
        => System.IO.File.WriteAllText(Catalogue, edit(System.IO.File.ReadAllText(Catalogue, Encoding.UTF8)), new UTF8Encoding(false));

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

// Tests over the git-ignored UKHO/SHOM data skip, naming the folder, where it has not been downloaded.
public sealed class LocalDataFactAttribute : FactAttribute
{
    public LocalDataFactAttribute()
    {
        if (!Fixtures.HasLocalUkhoData)
            Skip = $"Local-only UKHO data not present at {Fixtures.LocalUkhoS101}";
    }
}

public sealed class LocalDataTheoryAttribute : TheoryAttribute
{
    public LocalDataTheoryAttribute()
    {
        if (!Fixtures.HasLocalUkhoData)
            Skip = $"Local-only UKHO data not present at {Fixtures.LocalUkhoS101}";
    }
}

// An independent minimal ISO 8211 walker used only to locate bytes for patching and reordering fixtures.
// It never interprets subfields, so it cannot share a bug with the production reader.
internal static class Iso8211Probe
{
    public sealed record Field(string Tag, int Start, int Length);

    public sealed record Record(int Start, int Length, IReadOnlyList<Field> Fields);

    public static IReadOnlyList<Record> Walk(byte[] data)
    {
        var records = new List<Record>();
        var pos = 0;
        while (pos < data.Length)
        {
            var length = Number(data, pos, 5);
            var baseAddress = Number(data, pos + 12, 5);
            int sizeLen = data[pos + 20] - '0', sizePos = data[pos + 21] - '0', sizeTag = data[pos + 23] - '0';
            var fields = new List<Field>();
            for (var d = pos + 24; data[d] != 0x1e; d += sizeTag + sizeLen + sizePos)
            {
                var tag = Encoding.ASCII.GetString(data, d, sizeTag);
                var fieldLength = Number(data, d + sizeTag, sizeLen);
                var fieldPos = Number(data, d + sizeTag + sizeLen, sizePos);
                fields.Add(new Field(tag, pos + baseAddress + fieldPos, fieldLength));
            }
            records.Add(new Record(pos, length, fields));
            pos += length;
        }
        return records;
    }

    public static Field FirstField(byte[] data, string tag, int fromRecord = 1)
        => Walk(data).Skip(fromRecord).SelectMany(r => r.Fields).First(f => f.Tag == tag);

    // Rebuilds the file with its data records in a new order (the DDR always stays first).
    public static byte[] Reorder(byte[] data, Func<IReadOnlyList<Record>, IEnumerable<Record>> order)
    {
        var records = Walk(data);
        using var output = new MemoryStream();
        output.Write(data, records[0].Start, records[0].Length);
        foreach (var record in order(records.Skip(1).ToList()))
            output.Write(data, record.Start, record.Length);
        return output.ToArray();
    }

    private static int Number(byte[] data, int start, int count)
        => int.Parse(Encoding.ASCII.GetString(data, start, count));
}
