using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Poseidon.Core.Modules;
using Poseidon.Modules.Charts;
using Poseidon.Modules.Charts.S101;
using Poseidon.Modules.Charts.Store;
using Xunit;

namespace CoreCharts.Tests;

public sealed class ChartLibraryTests
{
    private const string X0000 = "10100AA_X0000", X01NE = "10100AA_X01NE", X01NW = "10100AA_X01NW",
        X01SE = "10100AA_X01SE", X01SW = "10100AA_X01SW", X02SE = "10100AA_X02SE";

    // Computed from the released PowerUp catalogue's coverage polygons (touching counts as adjacent).
    public static TheoryData<string, string[]> PowerUpNeighbours() => new()
    {
        { X0000, [X01NE, X01NW, X01SE, X01SW, X02SE] },
        { X01NE, [X0000, X01NW, X01SW] },
        { X01NW, [X0000, X01NE, X01SW] },
        { X01SE, [X0000, X01SW, X02SE] },
        { X01SW, [X0000, X01NE, X01NW, X01SE] },
        { X02SE, [X0000, X01SE] }
    };

    // C18: a structurally corrupt update is rejected naming the file; the installed chart is undisturbed.
    [Fact]
    public async Task Import_TruncatedUpdate_IsRejectedAndLeavesTheInstalledCellUntouched()
    {
        using var store = new TempCopy();
        await using var library = Libraries.Create(store.Root);
        await Libraries.ImportLoaded(library, Fixtures.ReleasedPowerUp);
        var before = Canonical.Of(library.Current.GetDataset(X01SW));
        using var update = new TempCopy(Fixtures.SequentialUpdate(1));
        var file = update.PathOf("10100AA_X01SW.001");
        var bytes = File.ReadAllBytes(file);
        File.WriteAllBytes(file, bytes[..^20]);

        var result = await library.ImportAsync(update.Root);

        Assert.Equal(ChartImportStatus.Rejected, result.Status);
        // Truncation also breaks the signature, so either finding is a correct rejection of this file.
        Assert.Contains(result.Diagnostics, d => d.Severity == ChartDiagnosticSeverity.Error
            && d.Code is ChartDiagnosticCodes.DatasetMalformed or ChartDiagnosticCodes.SignatureInvalid
            && (d.File ?? "").EndsWith("10100AA_X01SW.001", StringComparison.Ordinal));
        Assert.Equal(0, Libraries.Cell(library.Current, X01SW).Update);
        Assert.Equal(before, Canonical.Of(library.Current.GetDataset(X01SW)));
    }

    // C21: the sub-group's corrupt base cell is rejected with its byte position and never installed.
    [Fact]
    public async Task Import_CorruptBaseCell_IsRejectedWithPositionAndNeverInstalled()
    {
        using var store = new TempCopy();
        await using var library = Libraries.Create(store.Root);

        var result = await library.ImportAsync(Fixtures.SubGroupSet("CorruptData"));

        Assert.Equal(ChartImportStatus.Rejected, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.DatasetMalformed);
        Assert.EndsWith("10100AA_X02SE.000", diagnostic.File, StringComparison.Ordinal);
        Assert.Equal(11705, diagnostic.Position);
        Assert.DoesNotContain(library.Current.Cells, c => c.DatasetName == X02SE);
    }

    // C21: other cells in the same exchange set still load.
    [Fact]
    public async Task Import_SetWithOneCorruptCell_LoadsTheOthers()
    {
        using var store = new TempCopy();
        using var copy = new TempCopy(Fixtures.ReleasedPowerUp);
        var file = copy.PathOf("10100AA_X02SE.000");
        File.WriteAllBytes(file, File.ReadAllBytes(file)[..^15]);
        await using var library = Libraries.Create(store.Root);

        var result = await library.ImportAsync(copy.Root);

        Assert.Equal(ChartImportStatus.Loaded, result.Status);
        // Truncation also breaks the signature, so either finding is a correct rejection of this cell.
        Assert.Contains(result.Diagnostics, d => d.Severity == ChartDiagnosticSeverity.Error
            && d.Code is ChartDiagnosticCodes.DatasetMalformed or ChartDiagnosticCodes.SignatureInvalid
            && (d.File ?? "").EndsWith("10100AA_X02SE.000", StringComparison.Ordinal));
        Assert.Equal([X0000, X01NE, X01NW, X01SE, X01SW], library.Current.Cells.Select(c => c.DatasetName).Order(StringComparer.Ordinal));
    }

    // C22: a published snapshot never changes, even while and after a newer import publishes.
    [Fact]
    public async Task Import_NewerImport_NeverChangesAnAlreadyPublishedSnapshot()
    {
        using var store = new TempCopy();
        await using var library = Libraries.Create(store.Root);
        await Libraries.ImportLoaded(library, Fixtures.ReleasedPowerUp);
        var published = library.Current;
        var generation = published.Generation;
        var cells = Canonical.Of(published.Cells);
        var content = Canonical.Of(published.GetDataset(X01SW));
        var seenDuringImport = new List<long>();

        var result = await library.ImportAsync(Fixtures.SequentialUpdate(1),
            new SyncProgress(p => { if (p.Stage != ImportStage.Published) seenDuringImport.Add(library.Current.Generation); }),
            CancellationToken.None);

        Assert.Equal(ChartImportStatus.Loaded, result.Status);
        Assert.All(seenDuringImport, g => Assert.Equal(generation, g));
        Assert.Equal(generation, published.Generation);
        Assert.Equal(cells, Canonical.Of(published.Cells));
        Assert.Equal(content, Canonical.Of(published.GetDataset(X01SW)));
        Assert.True(library.Current.Generation > generation);
        Assert.Equal(1, Libraries.Cell(library.Current, X01SW).Update);
    }

    // C23: provenance per installed cell.
    [Fact]
    public async Task Import_RecordsProvenancePerCell()
    {
        using var store = new TempCopy();
        var importedAt = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        await using var library = Libraries.Create(store.Root, time: new FixedTime(importedAt));
        var root = Fixtures.ReleasedPowerUp;

        await Libraries.ImportLoaded(library, root);

        Assert.Equal(6, library.Current.Cells.Count);
        foreach (var cell in library.Current.Cells)
        {
            var path = Fixtures.DatasetFile(root, cell.DatasetName + ".000");
            Assert.EndsWith(cell.DatasetName + ".000", cell.SourceFile, StringComparison.Ordinal);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), cell.SourceSha256, ignoreCase: true);
            Assert.Equal(Decoding.Dataset(path).Identity.Edition, cell.Edition);
            Assert.Equal(0, cell.Update);
            Assert.Equal("2.0.0", cell.ProductEdition);
            Assert.Equal("5.2", cell.EncodingEdition);
            Assert.Equal(importedAt, cell.ImportedAt);
            Assert.NotNull(cell.Diagnostics);
        }
    }

    // C24: every query answers from exactly one snapshot generation, even during an import.
    [Fact]
    public async Task Query_DuringAnImport_AlwaysAnswersFromOneGeneration()
    {
        using var store = new TempCopy();
        await using var library = Libraries.Create(store.Root);
        await Libraries.ImportLoaded(library, Fixtures.ReleasedPowerUp);
        var old = library.Current;
        var answers = new List<ChartNeighbourhood>();

        var import = Task.Run(() => library.ImportAsync(Fixtures.SequentialUpdate(1)));
        while (!import.IsCompleted)
            answers.Add(library.QueryNeighbourhood([X01SW]));
        Assert.Equal(ChartImportStatus.Loaded, (await import).Status);
        answers.Add(library.QueryNeighbourhood([X01SW]));
        var updated = library.Current;

        Assert.All(answers, answer =>
        {
            var snapshot = answer.Generation == old.Generation ? old
                : answer.Generation == updated.Generation ? updated
                : throw new Xunit.Sdk.XunitException($"Unknown generation {answer.Generation}");
            var names = answer.Cells.Select(c => c.DatasetName).ToHashSet();
            Assert.Equal(Canonical.Of(snapshot.Cells.Where(c => names.Contains(c.DatasetName)).OrderBy(c => c.DatasetName)),
                Canonical.Of(answer.Cells.OrderBy(c => c.DatasetName)));
        });
        Assert.Equal(1, answers[^1].Cells.Single(c => c.DatasetName == X01SW).Update);
    }

    // C25: cancelling mid-import leaves the installed chart and publishes nothing partial.
    [Fact]
    public async Task Import_CancelledMidFlight_LeavesTheInstalledChartAndNoPartialSnapshot()
    {
        using var store = new TempCopy();
        await using var library = Libraries.Create(store.Root);
        await Libraries.ImportLoaded(library, Fixtures.ReleasedPowerUp);
        var before = library.Current;
        var beforeCells = Canonical.Of(before.Cells);
        using var cancel = new CancellationTokenSource();

        var result = await library.ImportAsync(Fixtures.SequentialUpdate(1),
            new SyncProgress(p => { if (p.Stage == ImportStage.DatasetDecoded) cancel.Cancel(); }), cancel.Token);

        Assert.Equal(ChartImportStatus.Cancelled, result.Status);
        Assert.Equal(before.Generation, library.Current.Generation);
        Assert.Equal(beforeCells, Canonical.Of(library.Current.Cells));
        Assert.Equal(0, Libraries.Cell(library.Current, X01SW).Update);
    }

    // C26: ImportAsync returns to its caller before decoding, which happens off the calling thread.
    [Fact]
    public async Task Import_ReturnsBeforeDecodingAndDecodesOffTheCallingThread()
    {
        using var store = new TempCopy();
        await using var library = Libraries.Create(store.Root);
        using var gate = new ManualResetEventSlim();
        var callerThread = Environment.CurrentManagedThreadId;
        var decodingThreads = new List<int>();

        var import = library.ImportAsync(Fixtures.ReleasedPowerUp, new SyncProgress(p =>
        {
            if (p.Stage == ImportStage.DatasetDecoding)
            {
                lock (decodingThreads)
                    decodingThreads.Add(Environment.CurrentManagedThreadId);
                gate.Wait(TimeSpan.FromSeconds(30));
            }
        }), CancellationToken.None);

        Assert.False(import.IsCompleted);
        gate.Set();
        Assert.Equal(ChartImportStatus.Loaded, (await import).Status);
        Assert.NotEmpty(decodingThreads);
        Assert.DoesNotContain(callerThread, decodingThreads);
    }

    // C29: every outcome is explicit; nothing unsupported or rejected is reported as an empty success.
    [Fact]
    public async Task Import_Outcomes_AreExplicit()
    {
        using var store = new TempCopy();
        await using var library = Libraries.Create(store.Root);

        var rejected = await library.ImportAsync(Fixtures.SubGroupSet("CorruptData"));
        var unsupported = await library.ImportAsync(UnsupportedEditionSet(store));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledResult = await library.ImportAsync(Fixtures.ReleasedPowerUp, cancelled.Token);
        var loaded = await library.ImportAsync(Fixtures.ReleasedPowerUp);

        Assert.Equal(ChartImportStatus.Rejected, rejected.Status);
        Assert.Equal(ChartImportStatus.UnsupportedVersion, unsupported.Status);
        Assert.Contains(unsupported.Diagnostics, d => d.Code == ChartDiagnosticCodes.VersionUnsupported);
        Assert.Equal(ChartImportStatus.Cancelled, cancelledResult.Status);
        Assert.Equal(ChartImportStatus.Loaded, loaded.Status);
        Assert.Equal(6, loaded.Cells.Count);
        Assert.All(new[] { rejected, unsupported, cancelledResult }, r => Assert.Empty(r.Cells));
    }

    // C31: published data survives a restart without re-import.
    [Fact]
    public async Task Restart_ReopensPublishedChartsFromTheDatabase()
    {
        using var store = new TempCopy();
        string cells, content;
        await using (var first = Libraries.Create(store.Root))
        {
            await Libraries.ImportLoaded(first, Fixtures.ReleasedPowerUp);
            cells = Canonical.Of(first.Current.Cells);
            content = Canonical.Of(first.Current.GetDataset(X01SW));
        }

        await using var second = Libraries.Create(store.Root);

        Assert.Equal(cells, Canonical.Of(second.Current.Cells));
        Assert.Equal(content, Canonical.Of(second.Current.GetDataset(X01SW)));
        Assert.True(second.Statistics.DatabaseLoads >= 1);
    }

    // C32
    [Theory]
    [MemberData(nameof(PowerUpNeighbours))]
    public async Task Query_ReferenceCell_ReturnsItAndEveryCellItsCoverageTouches(string reference, string[] neighbours)
    {
        using var store = new TempCopy();
        await using var library = Libraries.Create(store.Root);
        await Libraries.ImportLoaded(library, Fixtures.ReleasedPowerUp);

        var answer = library.QueryNeighbourhood([reference]);

        Assert.Equal(neighbours.Append(reference).Order(StringComparer.Ordinal), answer.Cells.Select(c => c.DatasetName).Order(StringComparer.Ordinal));
        Assert.Equal(library.Current.Generation, answer.Generation);
    }

    // C32: a cold neighbourhood is decoded from the database and cached; a warm one is served from memory.
    [Fact]
    public async Task Query_ColdNeighbourhood_LoadsFromTheDatabaseOnceThenServesFromCache()
    {
        using var store = new TempCopy();
        await using (var importer = Libraries.Create(store.Root))
            await Libraries.ImportLoaded(importer, Fixtures.ReleasedPowerUp);
        await using var library = Libraries.Create(store.Root);

        library.QueryNeighbourhood([X02SE]);
        var afterCold = library.Statistics;
        library.QueryNeighbourhood([X02SE]);
        var afterWarm = library.Statistics;

        Assert.True(afterCold.DatabaseLoads >= 3);
        Assert.Superset(new HashSet<string> { X02SE, X0000, X01SE }, afterCold.WarmCells.ToHashSet());
        Assert.Equal(afterCold.DatabaseLoads, afterWarm.DatabaseLoads);
    }

    // C33: cells outside the current neighbourhood are evicted under the limit, and reload on demand.
    [Fact]
    public async Task Query_MovingNeighbourhood_EvictsCellsOutsideItAndReloadsThemOnDemand()
    {
        using var store = new TempCopy();
        await using var library = Libraries.Create(store.Root, cacheCapacity: 0);
        await Libraries.ImportLoaded(library, Fixtures.ReleasedPowerUp);

        library.QueryNeighbourhood([X02SE]);
        library.QueryNeighbourhood([X01NW]);
        var moved = library.Statistics;
        var reloaded = library.Current.GetDataset(X02SE);

        Assert.Equal(new[] { X0000, X01NE, X01NW, X01SW }, moved.WarmCells.Order(StringComparer.Ordinal));
        Assert.True(moved.Evictions >= 2);
        Assert.NotNull(reloaded);
        Assert.True(library.Statistics.DatabaseLoads > moved.DatabaseLoads);
    }

    // C35: a catalogue that disagrees with its file (the released updates say newDataset) is a warning;
    // the file's DSID decides and the update applies.
    [Fact]
    public async Task Import_CatalogueDisagreeingWithTheFile_WarnsAndUsesTheFile()
    {
        using var store = new TempCopy();
        await using var library = Libraries.Create(store.Root);
        await Libraries.ImportLoaded(library, Fixtures.ReleasedPowerUp);

        var result = await Libraries.ImportLoaded(library, Fixtures.SequentialUpdate(1));

        Assert.Contains(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.CatalogueMismatch && d.Severity == ChartDiagnosticSeverity.Warning
            && (d.File ?? "").EndsWith("10100AA_X01SW.001", StringComparison.Ordinal));
        Assert.Equal(1, Libraries.Cell(library.Current, X01SW).Update);
    }

    // C36: genuine signatures verify.
    [Fact]
    public async Task Import_GenuineSignatures_Verify()
    {
        using var store = new TempCopy();
        await using var library = Libraries.Create(store.Root);

        var powerUp = await Libraries.ImportLoaded(library, Fixtures.ReleasedPowerUp);
        var update = await Libraries.ImportLoaded(library, Fixtures.SequentialUpdate(1));

        Assert.DoesNotContain(powerUp.Diagnostics.Concat(update.Diagnostics), d => d.Code is ChartDiagnosticCodes.SignatureInvalid
            or ChartDiagnosticCodes.SignatureUndecodable or ChartDiagnosticCodes.Unsigned or ChartDiagnosticCodes.CertificateUnresolved);
    }

    // C36: a byte altered without breaking the structure is caught by the signature and rejected as corrupt.
    [Fact]
    public async Task Import_UpdateAlteredAfterSigning_IsRejectedAsCorrupt()
    {
        using var store = new TempCopy();
        await using var library = Libraries.Create(store.Root);
        await Libraries.ImportLoaded(library, Fixtures.ReleasedPowerUp);
        using var update = new TempCopy(Fixtures.SequentialUpdate(1));
        var file = update.PathOf("10100AA_X01SW.001");
        var bytes = File.ReadAllBytes(file);
        AlterTitle(bytes);
        File.WriteAllBytes(file, bytes);
        Assert.Equal(DecodeStatus.Decoded, Decoding.Result(bytes, "10100AA_X01SW.001").Status);

        var result = await library.ImportAsync(update.Root);

        Assert.Equal(ChartImportStatus.Rejected, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.SignatureInvalid && (d.File ?? "").EndsWith("10100AA_X01SW.001", StringComparison.Ordinal));
        Assert.Equal(0, Libraries.Cell(library.Current, X01SW).Update);
    }

    // C36: a placeholder that is not a signature at all counts as unsigned: a warning, not a rejection.
    [Fact]
    public async Task Import_PlaceholderSignature_IsAWarning()
    {
        using var store = new TempCopy();
        await using var library = Libraries.Create(store.Root);
        await Libraries.ImportLoaded(library, Fixtures.ReleasedPowerUp);
        using var update = new TempCopy(Fixtures.SequentialUpdate(1));
        EditSignatures(update, signature => signature.Value = "============A_MODIFIER_AVEC_SIGNATURE============");

        var result = await Libraries.ImportLoaded(library, update.Root);

        Assert.Contains(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.SignatureUndecodable && d.Severity == ChartDiagnosticSeverity.Warning);
        Assert.Equal(1, Libraries.Cell(library.Current, X01SW).Update);
    }

    // C36: an unresolvable certificate reference is a warning, not a rejection.
    [Fact]
    public async Task Import_UnresolvableCertificateReference_IsAWarning()
    {
        using var store = new TempCopy();
        await using var library = Libraries.Create(store.Root);
        await Libraries.ImportLoaded(library, Fixtures.ReleasedPowerUp);
        using var update = new TempCopy(Fixtures.SequentialUpdate(1));
        EditSignatures(update, signature => signature.SetAttributeValue("certificateRef", "urn:mrn:iho:XX00:00000"));

        var result = await Libraries.ImportLoaded(library, update.Root);

        Assert.Contains(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.CertificateUnresolved && d.Severity == ChartDiagnosticSeverity.Warning);
        Assert.Equal(1, Libraries.Cell(library.Current, X01SW).Update);
    }

    // C36: UKHO's toolchain writes S100_SE_DigitalSignature; every file verifies. The set's 28 S-101 files
    // are 27 cells (101GB005DEVRC also has update .001), beside one S-128 file that is not S-101.
    [LocalDataFact]
    public async Task Import_UkhoExchangeSet_VerifiesEverySignature()
    {
        using var store = new TempCopy();
        await using var library = Libraries.Create(store.Root);

        var result = await Libraries.ImportLoaded(library, Fixtures.LocalUkhoGb);

        Assert.Equal(27, library.Current.Cells.Count);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code is ChartDiagnosticCodes.SignatureInvalid
            or ChartDiagnosticCodes.SignatureUndecodable or ChartDiagnosticCodes.Unsigned or ChartDiagnosticCodes.CertificateUnresolved);
    }

    // C37: support files are stored with the snapshot and resolve from the referencing cell. The cell's
    // references are on information types (NauticalInformation). One, 10100AATESTPC.TI, is truncated in the
    // released cell's own bytes (the catalogue lists 10100AATESTPC.TIF), so it is reported and never resolves.
    [Fact]
    public async Task Import_SupportFiles_ResolveToTheirStoredContent()
    {
        const string truncated = "10100AATESTPC.TI";
        using var store = new TempCopy();
        await using var library = Libraries.Create(store.Root);
        var root = Fixtures.Released("2.2_DatasetLoading", "InitialPowerUp");
        var result = await Libraries.ImportLoaded(library, root);
        var references = SupportReferences(library.Current.GetDataset(X0000)!);

        Assert.Equal(["10100AACHAINS.TXT", "10100AADEVARE.TXT", "10100AAIECTMP.TXT", truncated, "10100AAX4000T.TIF"],
            references.Order(StringComparer.Ordinal));
        foreach (var reference in references.Where(r => r != truncated))
        {
            var resolution = library.ResolveSupportFile(X0000, reference);
            Assert.Null(resolution.Diagnostic);
            Assert.Equal(File.ReadAllBytes(Fixtures.DatasetFile(root, reference)), resolution.Content);
        }
        var unresolved = library.ResolveSupportFile(X0000, truncated);
        Assert.Null(unresolved.Content);
        Assert.Equal(ChartDiagnosticCodes.SupportFileNotListed, unresolved.Diagnostic?.Code);
        Assert.Contains(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.SupportFileNotListed && d.Message.Contains(truncated + ",", StringComparison.Ordinal));
    }

    // C37: a referenced support file missing from disk is reported, and does not resolve.
    [Fact]
    public async Task Import_ReferencedSupportFileMissing_IsReported()
    {
        using var store = new TempCopy();
        using var copy = new TempCopy(Fixtures.Released("2.2_DatasetLoading", "InitialPowerUp"));
        File.Delete(copy.PathOf("10100AADEVARE.TXT"));
        await using var library = Libraries.Create(store.Root);

        var result = await Libraries.ImportLoaded(library, copy.Root);

        Assert.Contains(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.SupportFileMissing && d.Message.Contains("10100AADEVARE.TXT", StringComparison.Ordinal));
        var resolution = library.ResolveSupportFile(X0000, "10100AADEVARE.TXT");
        Assert.Null(resolution.Content);
        Assert.Equal(ChartDiagnosticCodes.SupportFileMissing, resolution.Diagnostic?.Code);
    }

    // C37: a reference to a file the catalogue does not list is reported, even if the file is on disk.
    [Fact]
    public async Task Import_ReferencedSupportFileNotInTheCatalogue_IsReported()
    {
        using var store = new TempCopy();
        using var copy = new TempCopy(Fixtures.Released("2.2_DatasetLoading", "InitialPowerUp"));
        var document = XDocument.Load(copy.Catalogue);
        document.Descendants().Where(e => e.Name.LocalName == "S100_SupportFileDiscoveryMetadata"
            && e.Descendants().Any(c => c.Name.LocalName == "fileName" && c.Value.EndsWith("10100AADEVARE.TXT", StringComparison.Ordinal))).Single().Remove();
        document.Save(copy.Catalogue);
        await using var library = Libraries.Create(store.Root);

        await Libraries.ImportLoaded(library, copy.Root);

        var resolution = library.ResolveSupportFile(X0000, "10100AADEVARE.TXT");
        Assert.Null(resolution.Content);
        Assert.Equal(ChartDiagnosticCodes.SupportFileNotListed, resolution.Diagnostic?.Code);
    }

    // C37 with C36: a support file altered after signing is not stored.
    [Fact]
    public async Task Import_SupportFileAlteredAfterSigning_IsRejected()
    {
        using var store = new TempCopy();
        using var copy = new TempCopy(Fixtures.Released("2.2_DatasetLoading", "InitialPowerUp"));
        var file = copy.PathOf("10100AACHAINS.TXT");
        var bytes = File.ReadAllBytes(file);
        bytes[0] ^= 0x20;
        File.WriteAllBytes(file, bytes);
        await using var library = Libraries.Create(store.Root);

        var result = await Libraries.ImportLoaded(library, copy.Root);

        Assert.Contains(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.SignatureInvalid && (d.File ?? "").EndsWith("10100AACHAINS.TXT", StringComparison.Ordinal));
        Assert.Null(library.ResolveSupportFile(X0000, "10100AACHAINS.TXT").Content);
    }

    // C37: every reference in UKHO's 27 cells resolves except one producer typo: 101GB005DEVQG names
    // 101GB00A0193.TXT, but the set's file is 101GB001A0193.TXT. That one is reported at import.
    [LocalDataFact]
    public async Task Import_UkhoSupportFiles_ResolveExceptTheProducersOneTypo()
    {
        using var store = new TempCopy();
        await using var library = Libraries.Create(store.Root);
        var result = await Libraries.ImportLoaded(library, Fixtures.LocalUkhoGb);

        var unresolved = (from cell in library.Current.Cells
                          from reference in SupportReferences(library.Current.GetDataset(cell.DatasetName)!)
                          let resolution = library.ResolveSupportFile(cell.DatasetName, reference)
                          where resolution.Content is null
                          select $"{cell.DatasetName}:{reference}").ToList();

        Assert.Equal(["101GB005DEVQG:101GB00A0193.TXT"], unresolved);
        Assert.Contains(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.SupportFileNotListed
            && d.Message.Contains("101GB00A0193.TXT", StringComparison.Ordinal));
    }

    // Every distinct fileReference or pictorialRepresentation value, on features and information types.
    private static List<string> SupportReferences(S101Dataset dataset)
        => Decoding.Flatten(dataset.Features.SelectMany(f => f.Attributes).Concat(dataset.InformationTypes.SelectMany(i => i.Attributes)))
            .Where(a => a.Code is "fileReference" or "pictorialRepresentation").Select(a => a.Value!).Distinct().ToList();

    // The released MultipleRevisions S-101 2.8 cell (an edition with no decoder) behind a minimal catalogue,
    // since that set ships without one.
    private static string UnsupportedEditionSet(TempCopy store)
    {
        var root = Path.Combine(store.Root, "unsupported");
        var source = Fixtures.Released("2.1_CatalogueLoading", "MultipleRevisions");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(root, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        var catalogue = new XDocument(new XElement(XName.Get("S100_ExchangeCatalogue", "http://www.iho.int/s100/xc/5.2"),
            new XElement(XName.Get("datasetDiscoveryMetadata", "http://www.iho.int/s100/xc/5.2"),
                new XElement(XName.Get("S100_DatasetDiscoveryMetadata", "http://www.iho.int/s100/xc/5.2"),
                    new XElement(XName.Get("fileName", "http://www.iho.int/s100/xc/5.2"), "file:/S-101/DATASET_FILES/10100AASPLIT04.000"),
                    new XElement(XName.Get("purpose", "http://www.iho.int/s100/xc/5.2"), "newDataset"),
                    new XElement(XName.Get("editionNumber", "http://www.iho.int/s100/xc/5.2"), "1")))));
        catalogue.Save(Path.Combine(root, "CATALOG.XML"));
        return root;
    }

    private static void EditSignatures(TempCopy copy, Action<XElement> edit)
    {
        var document = XDocument.Load(copy.Catalogue);
        foreach (var signature in document.Descendants().Where(e => e.Name.LocalName is "S100_SE_SignatureOnData" or "S100_SE_DigitalSignature").ToList())
            edit(signature);
        document.Save(copy.Catalogue);
    }

    // Changes one letter of the DSID title (DSTL), leaving every length and the structure intact.
    private static void AlterTitle(byte[] data)
    {
        var dsid = Iso8211Probe.FirstField(data, "DSID");
        var position = dsid.Start + 5;
        for (var separators = 0; separators < 6; position++)
            if (data[position] == 0x1f)
                separators++;
        for (var i = position; data[i] != 0x1f; i++)
        {
            if (char.IsAsciiLetter((char)data[i]))
            {
                data[i] ^= 0x20;
                return;
            }
        }
        for (var i = position; i < dsid.Start + dsid.Length; i++)
        {
            if (data[i] is >= (byte)'0' and <= (byte)'8')
            {
                data[i]++;
                return;
            }
        }
        throw new InvalidOperationException("No title or date digit to alter in DSID.");
    }
}
