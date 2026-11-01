using System.Security.Cryptography;
using System.Text.Json;
using Poseidon.Core.Modules;

namespace Poseidon.Modules.Charts.Store;

// One file as received, kept by content hash. Chain entries are replayed in order to rebuild a cell.
internal sealed record StoredFile(string FileName, string Sha256);

// Coverage holds the catalogue's coverage polygons, each flattened as latitude, longitude pairs.
internal sealed record StoredCell(
    ChartCellSummary Summary,
    string? NotUpToDateReason,
    IReadOnlyList<StoredFile> Chain,
    IReadOnlyList<double[]> Coverage)
{
    // Identifies the cell's decoded content: equal chains decode to equal datasets.
    public string ContentKey => string.Join('+', Chain.Select(f => f.Sha256));
}

internal enum SupportFileStatus
{
    Stored,
    Missing,
    SignatureInvalid
}

// Sha256 is set only when Status is Stored.
internal sealed record StoredSupportFile(string FileName, SupportFileStatus Status, string? Sha256);

internal sealed record Manifest(
    int Format,
    long Generation,
    IReadOnlyList<StoredCell> Cells,
    IReadOnlyList<StoredSupportFile> SupportFiles);

// The managed embedded store (C31). The host refuses native libraries in modules, so instead of an
// embedded database engine the store is content-addressed files (datasets and support files exactly as
// received) beside a manifest that is replaced atomically on each publish. Cells are re-decoded from
// their stored chain on load. One ChartLibrary owns a store at a time.
internal sealed class ChartStore(string databasePath)
{
    public const int Format = 1;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string manifestPath = Path.GetFullPath(databasePath);
    private readonly string blobDirectory = Path.GetFullPath(databasePath) + ".blobs";

    public string ManifestPath => manifestPath;

    public Manifest? Load()
    {
        if (!File.Exists(manifestPath))
            return null;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(manifestPath), Json)
            ?? throw new InvalidDataException("The chart store manifest is empty.");
        if (manifest.Format != Format)
            throw new InvalidDataException($"The chart store manifest has format {manifest.Format}; this version reads format {Format}.");
        return manifest;
    }

    public void Save(Manifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var temp = manifestPath + ".tmp";
        WriteDurably(temp, JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
        File.Move(temp, manifestPath, overwrite: true);
    }

    // Returns the content hash under which the bytes are stored; hash, when given, is Hash(data).
    public string Put(byte[] data, string? hash = null)
    {
        hash ??= Hash(data);
        var path = BlobPath(hash);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(blobDirectory);
            var temp = path + ".tmp";
            WriteDurably(temp, data);
            File.Move(temp, path, overwrite: true);
        }
        return hash;
    }

    public byte[] Read(string hash)
    {
        var data = File.ReadAllBytes(BlobPath(hash));
        if (!Hash(data).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The stored file {hash} no longer matches its hash.");
        return data;
    }

    // Removes stored files no published manifest refers to: leftovers of rejected or cancelled imports,
    // and chains superseded by a new edition. Only safe before any snapshot is handed out.
    public void CollectGarbage(Manifest manifest)
    {
        if (!Directory.Exists(blobDirectory))
            return;
        var keep = manifest.Cells.SelectMany(c => c.Chain).Select(f => f.Sha256)
            .Concat(manifest.SupportFiles.Select(s => s.Sha256).OfType<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(blobDirectory))
        {
            if (keep.Contains(Path.GetFileName(file)))
                continue;
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    // The hash comes from the manifest, so it is checked before it becomes part of a path.
    private string BlobPath(string hash)
    {
        if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
            throw new InvalidDataException($"'{hash}' is not a stored file hash.");
        return Path.Combine(blobDirectory, hash.ToUpperInvariant());
    }

    private static void WriteDurably(string path, byte[] data)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough);
        stream.Write(data);
        stream.Flush(flushToDisk: true);
    }
}
