using System.Xml.Linq;
using Poseidon.Core.Modules;
using Poseidon.Modules.Charts;
using Poseidon.Modules.Charts.ExchangeSets;
using Xunit;

namespace CoreCharts.Tests;

public sealed class ExchangeSetReaderTests
{
    private static ExchangeSetReadResult Read(string root) => new ExchangeSetReader().Read(root);

    private static XDocument Catalogue(string root) => XDocument.Load(Directory.EnumerateFiles(root)
        .Single(f => Path.GetFileName(f).Equals("CATALOG.XML", StringComparison.OrdinalIgnoreCase)));

    private static IEnumerable<XElement> ByLocalName(XContainer container, string name)
        => container.Descendants().Where(e => e.Name.LocalName == name);

    private static string? Child(XElement element, string name)
        => ByLocalName(element, name).FirstOrDefault()?.Value.Trim();

    private static void EditXml(TempCopy copy, Action<XDocument> edit)
    {
        var document = XDocument.Load(copy.Catalogue);
        edit(document);
        document.Save(copy.Catalogue);
    }

    // C1
    [Fact]
    public void Read_ReleasedPowerUp_ListsEveryEntryWithItsRoleMetadataAndResolvedFile()
    {
        var root = Fixtures.ReleasedPowerUp;
        var catalogue = Catalogue(root);
        var datasets = ByLocalName(catalogue, "S100_DatasetDiscoveryMetadata").ToList();
        var supportFiles = ByLocalName(catalogue, "S100_SupportFileDiscoveryMetadata").ToList();

        var result = Read(root);

        var set = Assert.IsType<ExchangeSet>(result.ExchangeSet);
        Assert.Equal("http://www.iho.int/s100/xc/5.2", set.CatalogueNamespace);
        Assert.Equal(datasets.Count + supportFiles.Count, set.Entries.Count);
        Assert.Equal(supportFiles.Count, set.Entries.Count(e => e.Role == ExchangeEntryRole.SupportFile));
        foreach (var expected in datasets)
        {
            var fileName = Child(expected, "fileName")!;
            var entry = Assert.Single(set.Entries, e => e.FileName == fileName);
            Assert.Equal(ExchangeEntryRole.Dataset, entry.Role);
            Assert.Equal("S-101", entry.ProductIdentifier);
            Assert.True(entry.IsSupportedProduct);
            Assert.Equal(Child(expected, "purpose"), entry.Purpose);
            Assert.Equal(int.Parse(Child(expected, "editionNumber")!), entry.Edition);
            Assert.True(File.Exists(entry.ResolvedPath), entry.ResolvedPath);
            Assert.NotNull(entry.Signature);
            Assert.NotNull(entry.OptimumDisplayScale);
            Assert.NotNull(entry.MinimumDisplayScale);
            var box = Assert.IsType<GeoBounds>(entry.BoundingBox);
            Assert.True(box.West < box.East && box.South < box.North);
            Assert.NotEmpty(entry.CoveragePolygons);
            // posList is latitude-first (EPSG:4326 axis order); every vertex must sit inside the bounding box.
            Assert.All(entry.CoveragePolygons.SelectMany(p => p), p =>
            {
                Assert.InRange(p.LatitudeDegrees, box.South - 1e-6, box.North + 1e-6);
                Assert.InRange(p.LongitudeDegrees, box.West - 1e-6, box.East + 1e-6);
            });
        }
    }

    // C1: elements are matched by namespace URI across all three catalogue versions in the corpora,
    // whatever prefix a producer chose.
    [Theory]
    [MemberData(nameof(CatalogueVersions))]
    public void Read_EachCatalogueVersion_ReadsItsDatasets(string root, string expectedNamespace)
    {
        var result = Read(root);

        var set = Assert.IsType<ExchangeSet>(result.ExchangeSet);
        Assert.Equal(expectedNamespace, set.CatalogueNamespace);
        Assert.Contains(set.Entries, e => e.Role == ExchangeEntryRole.Dataset && File.Exists(e.ResolvedPath));
    }

    public static TheoryData<string, string> CatalogueVersions() => new()
    {
        { Fixtures.Old164("2.1.1 Power Up"), "http://www.iho.int/s100/xc/5.0" },
        { Fixtures.SubGroupSet("GoodBaseCells"), "http://www.iho.int/s100/xc/5.1" },
        { Fixtures.ReleasedPowerUp, "http://www.iho.int/s100/xc/5.2" }
    };

    // C1: other S-100 products are listed and reported unsupported; they never fail the exchange set.
    [Fact]
    public void Read_OtherProducts_AreListedAsUnsupportedWithoutFailingTheSet()
    {
        var result = Read(Fixtures.Released("2.10_OverlayExchangeSets", "Overlays"));

        var set = Assert.IsType<ExchangeSet>(result.ExchangeSet);
        var others = set.Entries.Where(e => e.Role == ExchangeEntryRole.Dataset && e.ProductIdentifier != "S-101").ToList();
        Assert.NotEmpty(others);
        Assert.All(others, e => Assert.False(e.IsSupportedProduct));
        Assert.Contains(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.ProductUnsupported);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == ChartDiagnosticSeverity.Error);
    }

    // C2: every file-name form observed in real catalogues resolves inside the root.
    [Theory]
    [MemberData(nameof(FileNameConventions))]
    public void Read_ObservedFileNameConventions_ResolveInsideTheRoot(string root, string convention)
    {
        var result = Read(root);

        var set = Assert.IsType<ExchangeSet>(result.ExchangeSet);
        var datasets = set.Entries.Where(e => e.Role == ExchangeEntryRole.Dataset).ToList();
        Assert.NotEmpty(datasets);
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        Assert.All(datasets, e =>
        {
            Assert.StartsWith(fullRoot, Path.GetFullPath(e.ResolvedPath), StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(e.ResolvedPath), $"{convention}: {e.FileName} -> {e.ResolvedPath}");
        });
    }

    public static TheoryData<string, string> FileNameConventions() => new()
    {
        { Fixtures.ReleasedPowerUp, "file:/S-101/..." },
        { Path.Combine(Fixtures.S101TestDatasets, "S-158", "V1", "TestDataSets", "00AA_00001"), "file:/S100_ROOT/... beside S100_ROOT" },
        { Path.Combine(Fixtures.S101TestDatasets, "S-101_Test_DataSets", "exports", "1.0", "IHO_V10", "S100_ROOT"), @"S-101\DATASET_FILES\<cell>\..." }
    };

    // C2: SHOM keeps each cell in its own folder under DATASET_FILES.
    [LocalDataFact]
    public void Read_ShomPerCellFolders_ResolveInsideTheRoot()
    {
        var root = Fixtures.LocalShomSet("FR00369400_ed1");

        var set = Assert.IsType<ExchangeSet>(Read(root).ExchangeSet);

        Assert.Equal(3, set.Entries.Count(e => e.Role == ExchangeEntryRole.Dataset));
        Assert.All(set.Entries, e => Assert.True(File.Exists(e.ResolvedPath), e.ResolvedPath));
    }

    // C2
    [Fact]
    public void Read_FileNamesEscapingTheRoot_AreRejectedWithDiagnostics()
    {
        using var copy = new TempCopy(Fixtures.ReleasedPowerUp);
        // A real file at the escape target, so a reader that followed the path would find something.
        using var outside = new TempCopy();
        var decoy = Path.Combine(Path.GetDirectoryName(copy.Root)!, Path.GetFileName(outside.Root) + ".000");
        File.WriteAllText(decoy, "not a chart");
        string[] escapes = [$"file:/../{Path.GetFileName(decoy)}", $"file:/S-101/../../{Path.GetFileName(decoy)}"];
        try
        {
        EditXml(copy, document =>
        {
            var names = ByLocalName(document, "S100_DatasetDiscoveryMetadata")
                .Select(e => ByLocalName(e, "fileName").First()).Take(escapes.Length).ToList();
            for (var i = 0; i < escapes.Length; i++)
                names[i].Value = escapes[i];
        });

        var result = Read(copy.Root);

        var set = Assert.IsType<ExchangeSet>(result.ExchangeSet);
        var fullRoot = Path.GetFullPath(copy.Root) + Path.DirectorySeparatorChar;
        Assert.All(set.Entries, e => Assert.StartsWith(fullRoot, Path.GetFullPath(e.ResolvedPath), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(set.Entries, e => escapes.Contains(e.FileName));
        foreach (var escape in escapes)
            Assert.Contains(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.PathOutsideRoot && d.Message.Contains(escape, StringComparison.Ordinal));
        Assert.Equal(Catalogue(Fixtures.ReleasedPowerUp).Descendants().Count(e => e.Name.LocalName == "S100_DatasetDiscoveryMetadata") - escapes.Length,
            set.Entries.Count(e => e.Role == ExchangeEntryRole.Dataset));
        }
        finally
        {
            File.Delete(decoy);
        }
    }

    // C3
    [Fact]
    public void Read_MissingCatalogue_IsRejectedNamingTheFile()
    {
        using var empty = new TempCopy();

        var result = Read(empty.Root);

        Assert.Null(result.ExchangeSet);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.CatalogueMissing);
        Assert.Equal(ChartDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("CATALOG.XML", diagnostic.File ?? diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    // C3
    [Fact]
    public void Read_MalformedCatalogue_IsRejectedNamingTheFile()
    {
        using var copy = new TempCopy(Fixtures.ReleasedPowerUp);
        copy.EditCatalogue(text => text[..(text.Length / 2)]);

        var result = Read(copy.Root);

        Assert.Null(result.ExchangeSet);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.CatalogueMalformed);
        Assert.EndsWith("CATALOG.XML", diagnostic.File, StringComparison.OrdinalIgnoreCase);
    }

    // C3
    [Fact]
    public void Read_DatasetEntryWithoutFileName_IsRejectedNamingTheElement()
    {
        using var copy = new TempCopy(Fixtures.ReleasedPowerUp);
        EditXml(copy, document => ByLocalName(ByLocalName(document, "S100_DatasetDiscoveryMetadata").First(), "fileName").First().Remove());

        var result = Read(copy.Root);

        Assert.Null(result.ExchangeSet);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.CatalogueElementMissing);
        Assert.Contains("fileName", diagnostic.Message, StringComparison.Ordinal);
        Assert.EndsWith("CATALOG.XML", diagnostic.File, StringComparison.OrdinalIgnoreCase);
    }

    // C3: an exchange set with nothing in it is a rejection, never an empty success.
    [Fact]
    public void Read_CatalogueWithoutAnyEntries_IsRejected()
    {
        using var copy = new TempCopy(Fixtures.ReleasedPowerUp);
        EditXml(copy, document =>
        {
            foreach (var entry in ByLocalName(document, "S100_DatasetDiscoveryMetadata")
                         .Concat(ByLocalName(document, "S100_SupportFileDiscoveryMetadata"))
                         .Concat(ByLocalName(document, "S100_CatalogueDiscoveryMetadata")).ToList())
                entry.Remove();
        });

        var result = Read(copy.Root);

        Assert.Null(result.ExchangeSet);
        Assert.Contains(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.CatalogueEmpty);
    }

    // C4: an external entity is never resolved; the catalogue fails closed.
    [Fact]
    public async Task Read_CatalogueWithExternalEntity_IsRefusedWithoutAnyNetworkRequest()
    {
        using var counter = new ConnectionCounter();
        using var copy = new TempCopy(Fixtures.ReleasedPowerUp);
        copy.EditCatalogue(text =>
        {
            var rootStart = text.IndexOf('<', text.IndexOf("?>", StringComparison.Ordinal) + 2);
            var rootName = text[(rootStart + 1)..].Split([' ', '>', '\r', '\n'])[0];
            var rootEnd = text.IndexOf('>', rootStart) + 1;
            var doctype = $"<!DOCTYPE {rootName} [<!ENTITY ext SYSTEM \"{counter.BaseUrl}ext.txt\">]>\n";
            return text[..rootStart] + doctype + text[rootStart..rootEnd] + "&ext;" + text[rootEnd..];
        });

        var result = Read(copy.Root);
        await Task.Delay(200);

        Assert.Null(result.ExchangeSet);
        Assert.Contains(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.ExternalEntityRefused);
        Assert.Equal(0, counter.Connections);
    }

    // C4: a schema hint is never followed. "Fails closed" here means the retrieval never happens; the hint
    // itself is not needed to read the catalogue, so reading continues.
    [Fact]
    public async Task Read_CatalogueWithRemoteSchemaHint_NeverFetchesIt()
    {
        using var counter = new ConnectionCounter();
        using var copy = new TempCopy(Fixtures.ReleasedPowerUp);
        EditXml(copy, document =>
        {
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
            document.Root!.SetAttributeValue(XNamespace.Xmlns + "xsi", xsi.NamespaceName);
            document.Root.SetAttributeValue(xsi + "schemaLocation",
                $"{document.Root.Name.NamespaceName} {counter.BaseUrl}S100_ExchangeCatalogue.xsd");
        });

        var result = Read(copy.Root);
        await Task.Delay(200);

        Assert.NotNull(result.ExchangeSet);
        Assert.Equal(0, counter.Connections);
    }

    // C5
    [Fact]
    public async Task Import_SupportedPackage_SucceedsWithoutNetworkAccess()
    {
        using var counter = new ConnectionCounter();
        using var copy = new TempCopy(Fixtures.ReleasedPowerUp);
        using var store = new TempCopy();
        EditXml(copy, document =>
        {
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
            document.Root!.SetAttributeValue(XNamespace.Xmlns + "xsi", xsi.NamespaceName);
            document.Root.SetAttributeValue(xsi + "schemaLocation",
                $"{document.Root.Name.NamespaceName} {counter.BaseUrl}S100_ExchangeCatalogue.xsd");
        });
        await using var library = Libraries.Create(store.Root);

        var result = await library.ImportAsync(copy.Root);
        await Task.Delay(200);

        Assert.Equal(ChartImportStatus.Loaded, result.Status);
        Assert.Equal(6, result.Cells.Count);
        Assert.Equal(0, counter.Connections);
    }

    // C9
    [Fact]
    public void Read_SameExchangeSetTwice_ProducesStructurallyEqualOutput()
    {
        Assert.Equal(Canonical.Of(Read(Fixtures.ReleasedPowerUp)), Canonical.Of(Read(Fixtures.ReleasedPowerUp)));
    }
}
