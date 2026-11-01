using System.Text.Json;
using Poseidon.Core.Modules;
using Poseidon.Modules.Charts.ExchangeSets;
using Poseidon.Modules.Charts.Lineage;
using Poseidon.Modules.Charts.S101;

namespace Poseidon.Modules.Charts.Store;

public sealed record ChartLibraryOptions
{
    // The embedded database file holding durable snapshots (C31).
    public required string DatabasePath { get; init; }

    // Decoded cells kept in memory beyond the current reference neighbourhood (C33).
    public int CacheCapacity { get; init; } = 64;

    public DecodeOptions Decode { get; init; } = new();

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public ILog? Log { get; init; }
}

public enum ImportStage
{
    CatalogueRead,
    DatasetDecoding,
    DatasetDecoded,
    LineageApplied,
    Published
}

public sealed record ImportProgress(ImportStage Stage, string? File);

public sealed record CacheStatistics(IReadOnlyCollection<string> WarmCells, int DatabaseLoads, int Evictions);

// Content is the stored support file's bytes when the reference resolves; otherwise Diagnostic says why.
public sealed record SupportFileResolution(byte[]? Content, ChartDiagnostic? Diagnostic);

// An immutable published view: its generation, active cells and their decoded content never change.
public sealed class ChartSnapshot
{
    private readonly ChartLibrary owner;
    private readonly Lazy<IReadOnlyDictionary<string, IReadOnlySet<string>>> adjacency;

    internal ChartSnapshot(ChartLibrary owner, long generation, IReadOnlyDictionary<string, StoredCell> cells,
        IReadOnlyDictionary<string, StoredSupportFile> supportFiles)
    {
        this.owner = owner;
        Generation = generation;
        AllCells = cells;
        SupportFiles = supportFiles;
        // A cancelled cell is kept so later updates to it are refused, but is never an active chart (C20).
        Active = cells.Where(c => c.Value.Summary.State != ChartLineageState.Cancelled)
            .ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
        Cells = Active.Values.Select(c => c.Summary).OrderBy(c => c.DatasetName, StringComparer.Ordinal).ToList();
        adjacency = new(() => Coverage.Adjacency(Active.ToDictionary(c => c.Key, c => c.Value.Coverage)));
    }

    public long Generation { get; }

    public IReadOnlyList<ChartCellSummary> Cells { get; }

    public S101Dataset? GetDataset(string datasetName)
        => Active.TryGetValue(datasetName, out var cell) ? owner.Load(cell) : null;

    internal IReadOnlyDictionary<string, StoredCell> AllCells { get; }

    internal IReadOnlyDictionary<string, StoredCell> Active { get; }

    internal IReadOnlyDictionary<string, StoredSupportFile> SupportFiles { get; }

    // The named active cells plus every active cell whose coverage touches one of them, in name order.
    internal IReadOnlyList<string> Neighbourhood(IEnumerable<string> referenceCells)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var reference in referenceCells)
        {
            if (!Active.ContainsKey(reference))
                continue;
            names.Add(reference);
            names.UnionWith(adjacency.Value[reference]);
        }
        return names.ToList();
    }
}

public sealed class ChartLibrary : IChartLibrary, IAsyncDisposable
{
    private readonly ChartLibraryOptions options;
    private readonly ChartStore store;
    private readonly DatasetCache cache;
    private readonly SemaphoreSlim importGate = new(1, 1);
    private readonly object openGate = new();
    private readonly IExchangeSetReader reader = new ExchangeSetReader();
    private readonly IS101DatasetDecoder decoder = new S101DatasetDecoder();
    private readonly IS101UpdateApplier applier = new S101UpdateApplier();
    private ChartSnapshot? current;
    private volatile bool disposed;

    // The store is opened on first use, so constructing the library (at module registration) touches no files.
    public ChartLibrary(ChartLibraryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        store = new ChartStore(options.DatabasePath);
        cache = new DatasetCache(Math.Max(0, options.CacheCapacity));
    }

    public ChartSnapshot Current => Volatile.Read(ref current) ?? Open();

    public CacheStatistics Statistics => cache.Statistics;

    public Task<ChartImportResult> ImportAsync(string exchangeSetRoot, CancellationToken cancellationToken = default)
        => ImportAsync(exchangeSetRoot, null, cancellationToken);

    // progress is reported synchronously from the importing thread.
    public Task<ChartImportResult> ImportAsync(
        string exchangeSetRoot, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(exchangeSetRoot);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(new ChartImportResult(ChartImportStatus.Cancelled, Current.Generation, [], []));
        // Decoding untrusted files is CPU-bound and never runs on the caller's thread (C26).
        return Task.Run(() => Import(exchangeSetRoot, progress, cancellationToken));
    }

    public ChartNeighbourhood QueryNeighbourhood(IReadOnlyList<string> referenceCells)
    {
        ArgumentNullException.ThrowIfNull(referenceCells);
        var snapshot = Current;
        var cells = snapshot.Neighbourhood(referenceCells).Select(n => snapshot.Active[n]).ToList();
        cache.Pin(cells.Select(c => (c.ContentKey, c.Summary.DatasetName, (Func<S101Dataset?>)(() => Replay(c)))).ToList());
        return new ChartNeighbourhood(snapshot.Generation, cells.Select(c => c.Summary).ToList());
    }

    public SupportFileResolution ResolveSupportFile(string datasetName, string reference)
    {
        var snapshot = Current;
        if (!snapshot.Active.ContainsKey(datasetName))
            return new SupportFileResolution(null, new ChartDiagnostic(ChartDiagnosticSeverity.Warning,
                ChartDiagnosticCodes.UnresolvedReference, $"No installed chart is named {datasetName}."));
        var key = SupportKey(reference);
        if (!snapshot.SupportFiles.TryGetValue(key, out var support))
            return new SupportFileResolution(null, new ChartDiagnostic(ChartDiagnosticSeverity.Warning,
                ChartDiagnosticCodes.SupportFileNotListed, $"{datasetName} refers to {reference}, which no imported exchange catalogue lists.", key));
        switch (support.Status)
        {
            case SupportFileStatus.Missing:
                return new SupportFileResolution(null, new ChartDiagnostic(ChartDiagnosticSeverity.Warning,
                    ChartDiagnosticCodes.SupportFileMissing, $"{reference} was listed in the exchange catalogue but missing from the exchange set.", key));
            case SupportFileStatus.SignatureInvalid:
                return new SupportFileResolution(null, new ChartDiagnostic(ChartDiagnosticSeverity.Error,
                    ChartDiagnosticCodes.SignatureInvalid, $"{reference} did not match its signature when imported and was not stored.", key));
        }
        try
        {
            return new SupportFileResolution(store.Read(support.Sha256!), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            options.Log?.Error($"Stored support file {key} cannot be read.", e);
            return new SupportFileResolution(null, new ChartDiagnostic(ChartDiagnosticSeverity.Error,
                ChartDiagnosticCodes.StoreUnreadable, $"The stored copy of {reference} cannot be read: {e.Message}", key));
        }
    }

    // Waits for an import in progress; the store needs no other cleanup.
    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        await importGate.WaitAsync().ConfigureAwait(false);
        disposed = true;
        importGate.Release();
    }

    internal S101Dataset? Load(StoredCell cell) => cache.Get(cell.ContentKey, cell.Summary.DatasetName, () => Replay(cell));

    private static string SupportKey(string reference) => Path.GetFileName(reference.Replace('\\', '/'));

    private ChartImportResult Import(string root, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            importGate.Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new ChartImportResult(ChartImportStatus.Cancelled, Current.Generation, [], []);
        }
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return new ImportRun(this, root, progress, cancellationToken).Run();
        }
        finally
        {
            importGate.Release();
        }
    }

    private ChartSnapshot Open()
    {
        lock (openGate)
        {
            if (Volatile.Read(ref current) is { } opened)
                return opened;
            ChartSnapshot snapshot;
            try
            {
                snapshot = store.Load() is { } manifest ? FromManifest(manifest) : Empty();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                          or InvalidDataException or NotSupportedException or ArgumentException)
            {
                options.Log?.Error($"The chart store manifest {store.ManifestPath} cannot be read; it is set aside and the library starts empty.", e);
                SetAsideUnreadableManifest();
                snapshot = Empty();
            }
            Volatile.Write(ref current, snapshot);
            return snapshot;
        }
    }

    private ChartSnapshot FromManifest(Manifest manifest)
    {
        var snapshot = new ChartSnapshot(this, manifest.Generation,
            manifest.Cells.ToDictionary(c => c.Summary.DatasetName, StringComparer.Ordinal),
            manifest.SupportFiles.ToDictionary(s => s.FileName, StringComparer.OrdinalIgnoreCase));
        // No snapshot has been handed out yet, so nothing can still need an unreferenced file.
        try
        {
            store.CollectGarbage(manifest);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            options.Log?.Warning("Unreferenced chart store files could not be removed.", e);
        }
        return snapshot;
    }

    private ChartSnapshot Empty() => new(this, 0, new Dictionary<string, StoredCell>(StringComparer.Ordinal),
        new Dictionary<string, StoredSupportFile>(StringComparer.OrdinalIgnoreCase));

    private void SetAsideUnreadableManifest()
    {
        try
        {
            if (File.Exists(store.ManifestPath))
                File.Move(store.ManifestPath, $"{store.ManifestPath}.unreadable-{options.Time.GetUtcNow():yyyyMMddHHmmss}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            options.Log?.Error("The unreadable chart store manifest could not be set aside; the next import replaces it.", e);
        }
    }

    // Rebuilds a cell's content by decoding its stored chain: the base (or latest edition or re-issue)
    // followed by each update applied since.
    private S101Dataset? Replay(StoredCell cell)
    {
        try
        {
            DatasetLineage? lineage = null;
            foreach (var file in cell.Chain)
            {
                var decoded = decoder.Decode(store.Read(file.Sha256), file.FileName, options.Decode);
                if (decoded.Dataset is not { } dataset)
                    throw new InvalidDataException($"Stored {file.FileName} no longer decodes ({decoded.Status}).");
                if (lineage is null)
                {
                    lineage = DatasetLineage.Start(dataset);
                    continue;
                }
                var applied = applier.Apply(lineage, dataset);
                if (applied.Outcome != UpdateOutcome.Applied)
                    throw new InvalidDataException($"Stored update {file.FileName} no longer applies ({applied.Outcome}).");
                lineage = applied.Lineage;
            }
            return lineage?.Current;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            options.Log?.Error($"Chart {cell.Summary.DatasetName} cannot be rebuilt from the chart store.", e);
            return null;
        }
    }

    private ChartSnapshot Publish(long generation, Dictionary<string, StoredCell> cells, Dictionary<string, StoredSupportFile> supportFiles)
    {
        store.Save(new Manifest(ChartStore.Format, generation,
            cells.Values.OrderBy(c => c.Summary.DatasetName, StringComparer.Ordinal).ToList(),
            supportFiles.Values.OrderBy(s => s.FileName, StringComparer.OrdinalIgnoreCase).ToList()));
        var snapshot = new ChartSnapshot(this, generation, cells, supportFiles);
        Volatile.Write(ref current, snapshot);
        return snapshot;
    }

    // One import: the catalogue, each S-101 dataset (signature, decode, lineage) in name and sequence
    // order, then the support files. Nothing is visible until Publish swaps in the new snapshot, so a
    // cancelled or failed import leaves the published chart as it was (C22, C25).
    private sealed class ImportRun
    {
        private readonly ChartLibrary library;
        private readonly string root;
        private readonly IProgress<ImportProgress>? progress;
        private readonly CancellationToken cancellationToken;
        private readonly ChartSnapshot baseline;
        private readonly DateTimeOffset importedAt;
        private readonly List<ChartDiagnostic> diagnostics = [];
        private readonly Dictionary<string, StoredCell> cells;
        private readonly Dictionary<string, StoredSupportFile> supportFiles;
        private readonly Dictionary<string, DatasetLineage> lineages = new(StringComparer.Ordinal);
        // Cells whose stored state changed, and the subset whose content changed.
        private readonly HashSet<string> touched = new(StringComparer.Ordinal);
        private readonly HashSet<string> changedContent = new(StringComparer.Ordinal);
        private bool supportChanged;

        public ImportRun(ChartLibrary library, string root, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
        {
            this.library = library;
            this.root = root;
            this.progress = progress;
            this.cancellationToken = cancellationToken;
            baseline = library.Current;
            importedAt = library.options.Time.GetUtcNow();
            cells = new Dictionary<string, StoredCell>(baseline.AllCells, StringComparer.Ordinal);
            supportFiles = new Dictionary<string, StoredSupportFile>(baseline.SupportFiles, StringComparer.OrdinalIgnoreCase);
        }

        public ChartImportResult Run()
        {
            var read = library.reader.Read(root);
            diagnostics.AddRange(read.Diagnostics);
            if (!Report(ImportStage.CatalogueRead, root))
                return Cancelled();
            if (read.ExchangeSet is not { } set)
                return new ChartImportResult(ChartImportStatus.Rejected, baseline.Generation, [], diagnostics);

            using (var signatures = new SignatureVerifier(set.Certificates))
            {
                var datasets = set.Entries.Where(e => e.Role == ExchangeEntryRole.Dataset && e.IsSupportedProduct)
                    .OrderBy(e => Path.GetFileNameWithoutExtension(e.ResolvedPath), StringComparer.Ordinal)
                    .ThenBy(e => Sequence(e.ResolvedPath));
                foreach (var entry in datasets)
                    if (!ImportDataset(entry, signatures))
                        return Cancelled();
                foreach (var entry in set.Entries.Where(e => e.Role == ExchangeEntryRole.SupportFile))
                    ImportSupportFile(entry, signatures);
            }
            CheckSupportReferences();

            var status = Status(set);
            if (touched.Count == 0 && !(supportChanged && status == ChartImportStatus.Loaded))
                return new ChartImportResult(status, baseline.Generation, [], diagnostics);
            if (cancellationToken.IsCancellationRequested)
                return Cancelled();
            if (status != ChartImportStatus.Loaded)
            {
                // Only a refusal that flags cells not up to date reaches here; support files are not kept.
                supportFiles.Clear();
                foreach (var support in baseline.SupportFiles)
                    supportFiles[support.Key] = support.Value;
            }

            var published = library.Publish(baseline.Generation + 1, cells, supportFiles);
            foreach (var name in changedContent)
                if (cells[name].Summary.State != ChartLineageState.Cancelled)
                    library.cache.Put(cells[name].ContentKey, name, lineages[name].Current);
            progress?.Report(new ImportProgress(ImportStage.Published, null));
            var affected = status == ChartImportStatus.Loaded
                ? touched.Order(StringComparer.Ordinal).Select(n => cells[n].Summary).ToList()
                : [];
            return new ChartImportResult(status, published.Generation, affected, diagnostics);
        }

        // Loaded when any cell was installed, updated, replaced or cancelled; otherwise the errors decide.
        private ChartImportStatus Status(ExchangeSet set)
        {
            if (changedContent.Count > 0 || touched.Any(n => cells[n].Summary.State == ChartLineageState.Cancelled))
                return ChartImportStatus.Loaded;
            var errors = diagnostics.Where(d => d.Severity == ChartDiagnosticSeverity.Error).ToList();
            if (errors.Count > 0 && errors.All(d => d.Code == ChartDiagnosticCodes.VersionUnsupported))
                return ChartImportStatus.UnsupportedVersion;
            var datasets = set.Entries.Where(e => e.Role == ExchangeEntryRole.Dataset).ToList();
            if (datasets.Count > 0 && datasets.All(e => !e.IsSupportedProduct))
                return ChartImportStatus.UnsupportedVersion;
            return errors.Count > 0 ? ChartImportStatus.Rejected : ChartImportStatus.Loaded;
        }

        // Returns false when the import was cancelled.
        private bool ImportDataset(ExchangeSetEntry entry, SignatureVerifier signatures)
        {
            var path = entry.ResolvedPath;
            var file = Path.GetFileName(path);
            if (!Report(ImportStage.DatasetDecoding, path))
                return false;
            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new ChartDiagnostic(ChartDiagnosticSeverity.Error, ChartDiagnosticCodes.DatasetMissing,
                    $"{file} is listed in the exchange catalogue but cannot be read: {e.Message}", file));
                return true;
            }

            var fileDiagnostics = new List<ChartDiagnostic>();
            try
            {
                var signature = signatures.Check(entry.Signature, data, file);
                if (signature.Diagnostic is { } found)
                    fileDiagnostics.Add(found);
                if (signature.Rejected)
                    return true;
                var decoded = Decode(data, file);
                fileDiagnostics.AddRange(decoded.Diagnostics);
                if (decoded.Dataset is not { } dataset)
                    return true;
                if (!Report(ImportStage.DatasetDecoded, path))
                    return false;
                CompareWithCatalogue(entry, dataset.Identity, file, fileDiagnostics);
                Apply(entry, dataset, data, fileDiagnostics);
                return Report(ImportStage.LineageApplied, path);
            }
            finally
            {
                diagnostics.AddRange(fileDiagnostics);
            }
        }

        private DecodeResult Decode(byte[] data, string file)
        {
            try
            {
                return library.decoder.Decode(data, file, library.options.Decode);
            }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
            {
                // The decoder reports malformed input itself; this is a defect, kept from failing the import.
                library.options.Log?.Error($"Decoding {file} failed unexpectedly.", e);
                return new DecodeResult(DecodeStatus.Rejected, null,
                [
                    new ChartDiagnostic(ChartDiagnosticSeverity.Error, ChartDiagnosticCodes.DatasetMalformed, $"{file} could not be decoded: {e.Message}", file)
                ]);
            }
        }

        private void Apply(ExchangeSetEntry entry, S101Dataset dataset, byte[] data, List<ChartDiagnostic> fileDiagnostics)
        {
            var file = dataset.Identity.FileName;
            var name = Path.GetFileNameWithoutExtension(file);
            var hash = ChartStore.Hash(data);
            cells.TryGetValue(name, out var cell);
            DatasetLineage next;
            IReadOnlyList<StoredFile> chain;
            if (cell is null)
            {
                if (dataset.Identity.ApplicationProfile != 1)
                {
                    fileDiagnostics.Add(new ChartDiagnostic(ChartDiagnosticSeverity.Error, ChartDiagnosticCodes.BaseNotInstalled,
                        $"{file} updates {name}, which is not installed; its base cell must be imported first.", file));
                    return;
                }
                next = DatasetLineage.Start(dataset);
                chain = [new StoredFile(file, library.store.Put(data, hash))];
            }
            else
            {
                if (Lineage(name, cell) is not { } lineage)
                {
                    fileDiagnostics.Add(new ChartDiagnostic(ChartDiagnosticSeverity.Error, ChartDiagnosticCodes.StoreUnreadable,
                        $"The installed {name} cannot be read from the chart store, so {file} cannot be applied; re-import its base cell.", file));
                    return;
                }
                var result = library.applier.Apply(lineage, dataset);
                fileDiagnostics.AddRange(result.Diagnostics);
                next = result.Lineage;
                switch (result.Outcome)
                {
                    case UpdateOutcome.Refused:
                        // The content stays; only a newly flagged not-up-to-date state is recorded.
                        if (next.State == lineage.State && next.NotUpToDateReason == lineage.NotUpToDateReason)
                            return;
                        lineages[name] = next;
                        cells[name] = cell with
                        {
                            Summary = cell.Summary with { State = next.State, Diagnostics = [.. cell.Summary.Diagnostics, .. result.Diagnostics] },
                            NotUpToDateReason = next.NotUpToDateReason
                        };
                        touched.Add(name);
                        return;
                    case UpdateOutcome.Applied:
                        chain = [.. cell.Chain, new StoredFile(file, library.store.Put(data, hash))];
                        break;
                    case UpdateOutcome.Cancelled:
                        chain = cell.Chain;
                        break;
                    default:
                        chain = [new StoredFile(file, library.store.Put(data, hash))];
                        break;
                }
            }

            lineages[name] = next;
            var coverage = Coverage.From(entry);
            cells[name] = new StoredCell(
                new ChartCellSummary(name, next.Edition, next.Update, next.State, dataset.Identity.ProductEdition,
                    dataset.Identity.EncodingEdition, entry.ResolvedPath, hash, importedAt, fileDiagnostics.ToList()),
                next.NotUpToDateReason, chain, coverage.Count > 0 ? coverage : cell?.Coverage ?? []);
            touched.Add(name);
            if (next.State != ChartLineageState.Cancelled)
                changedContent.Add(name);
        }

        private DatasetLineage? Lineage(string name, StoredCell cell)
        {
            if (lineages.TryGetValue(name, out var lineage))
                return lineage;
            if (library.Load(cell) is not { } dataset)
                return null;
            return lineages[name] = new DatasetLineage(name, dataset, cell.Summary.Edition, cell.Summary.Update,
                cell.Summary.State, cell.NotUpToDateReason);
        }

        private void ImportSupportFile(ExchangeSetEntry entry, SignatureVerifier signatures)
        {
            var key = SupportKey(entry.ResolvedPath);
            supportChanged = true;
            byte[] data;
            try
            {
                data = File.ReadAllBytes(entry.ResolvedPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new ChartDiagnostic(ChartDiagnosticSeverity.Warning, ChartDiagnosticCodes.SupportFileMissing,
                    $"Support file {key} is listed in the exchange catalogue but missing from the exchange set.", key));
                supportFiles[key] = new StoredSupportFile(key, SupportFileStatus.Missing, null);
                return;
            }
            var signature = signatures.Check(entry.Signature, data, key);
            if (signature.Diagnostic is { } found)
                diagnostics.Add(found);
            supportFiles[key] = signature.Rejected
                ? new StoredSupportFile(key, SupportFileStatus.SignatureInvalid, null)
                : new StoredSupportFile(key, SupportFileStatus.Stored, library.store.Put(data));
        }

        // Reports each fileReference or pictorialRepresentation in a changed cell that will not resolve (C37).
        private void CheckSupportReferences()
        {
            var reported = diagnostics.Where(d => d.Code == ChartDiagnosticCodes.SupportFileMissing)
                .Select(d => d.File).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in changedContent.Order(StringComparer.Ordinal))
            {
                var dataset = lineages[name].Current;
                var attributes = dataset.Features.SelectMany(f => f.Attributes).Concat(dataset.InformationTypes.SelectMany(i => i.Attributes));
                foreach (var reference in Flatten(attributes).Where(a => a.Code is "fileReference" or "pictorialRepresentation")
                             .Select(a => a.Value).OfType<string>().Where(v => v.Length > 0).Distinct(StringComparer.Ordinal))
                {
                    var key = SupportKey(reference);
                    if (!supportFiles.TryGetValue(key, out var support))
                        diagnostics.Add(new ChartDiagnostic(ChartDiagnosticSeverity.Warning, ChartDiagnosticCodes.SupportFileNotListed,
                            $"{name} refers to {reference}, which the exchange catalogue does not list; it will not resolve.", key));
                    else if (support.Status == SupportFileStatus.Missing && reported.Add(key))
                        diagnostics.Add(new ChartDiagnostic(ChartDiagnosticSeverity.Warning, ChartDiagnosticCodes.SupportFileMissing,
                            $"{name} refers to {reference}, which is listed in the exchange catalogue but missing.", key));
                }
            }
        }

        // A disagreement between catalogue and file is a warning; the file's DSID decides (C35).
        private static void CompareWithCatalogue(ExchangeSetEntry entry, DatasetIdentity identity, string file, List<ChartDiagnostic> into)
        {
            var cancellation = identity.ApplicationProfile == 2 && identity.Edition == 0;
            var differences = new List<string>();
            if (!cancellation && entry.Edition is { } edition && edition != identity.Edition)
                differences.Add($"edition {edition} (the file says {identity.Edition})");
            if (!cancellation && entry.Update is { } update && update != identity.Update)
                differences.Add($"update {update} (the file says {identity.Update})");
            string[] purposes = cancellation ? ["cancellation"]
                : identity.ApplicationProfile == 2 ? ["update"]
                : identity.Update > 0 ? ["reissue"]
                : ["newdataset", "newedition"];
            if (entry.Purpose is { } purpose && !purposes.Contains(purpose.Replace("-", "").ToLowerInvariant()))
                differences.Add($"purpose {purpose} (the file is {string.Join(" or ", purposes)})");
            if (differences.Count > 0)
                into.Add(new ChartDiagnostic(ChartDiagnosticSeverity.Warning, ChartDiagnosticCodes.CatalogueMismatch,
                    $"The exchange catalogue disagrees with {file} on {string.Join(", ", differences)}; the file's DSID is used.", file));
        }

        private static IEnumerable<S101Attribute> Flatten(IEnumerable<S101Attribute> attributes)
            => attributes.SelectMany(a => new[] { a }.Concat(Flatten(a.Children)));

        private static int Sequence(string path)
            => int.TryParse(Path.GetExtension(path).TrimStart('.'), out var sequence) ? sequence : int.MaxValue;

        private bool Report(ImportStage stage, string? file)
        {
            progress?.Report(new ImportProgress(stage, file));
            return !cancellationToken.IsCancellationRequested;
        }

        private ChartImportResult Cancelled()
            => new(ChartImportStatus.Cancelled, baseline.Generation, [], diagnostics);
    }
}
