using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Poseidon.Core.Modules;

namespace Poseidon.Modules.Charts.ExchangeSets;

public enum ExchangeEntryRole
{
    Dataset,
    SupportFile,
    Catalogue
}

public sealed record GeoBounds(double West, double East, double South, double North);

public sealed record DatasetSignature(string? CertificateRef, string Value);

public sealed record ExchangeSetEntry(
    string FileName,
    string ResolvedPath,
    ExchangeEntryRole Role,
    string ProductIdentifier,
    bool IsSupportedProduct,
    string? Purpose,
    int? Edition,
    int? Update,
    string? IssueDate,
    GeoBounds? BoundingBox,
    IReadOnlyList<IReadOnlyList<GeodeticPosition>> CoveragePolygons,
    int? OptimumDisplayScale,
    int? MinimumDisplayScale,
    int? MaximumDisplayScale,
    DatasetSignature? Signature);

// Certificates maps certificate id to its base64 DER body as written in the catalogue.
public sealed record ExchangeSet(
    string Root,
    string CatalogueNamespace,
    IReadOnlyList<ExchangeSetEntry> Entries,
    IReadOnlyDictionary<string, string> Certificates);

// ExchangeSet is null when the catalogue is rejected; Diagnostics says why.
public sealed record ExchangeSetReadResult(ExchangeSet? ExchangeSet, IReadOnlyList<ChartDiagnostic> Diagnostics);

public interface IExchangeSetReader
{
    // exchangeSetRoot is the folder holding CATALOG.XML (S100_ROOT).
    ExchangeSetReadResult Read(string exchangeSetRoot);
}

public sealed partial class ExchangeSetReader : IExchangeSetReader
{
    public const string SupportedProduct = "S-101";

    private static readonly (string Name, ExchangeEntryRole Role)[] EntryKinds =
    [
        ("S100_DatasetDiscoveryMetadata", ExchangeEntryRole.Dataset),
        ("S100_SupportFileDiscoveryMetadata", ExchangeEntryRole.SupportFile),
        ("S100_CatalogueDiscoveryMetadata", ExchangeEntryRole.Catalogue)
    ];

    public ExchangeSetReadResult Read(string exchangeSetRoot)
    {
        var root = Path.GetFullPath(exchangeSetRoot);
        var diagnostics = new List<ChartDiagnostic>();
        var cataloguePath = Directory.Exists(root)
            ? Directory.EnumerateFiles(root).FirstOrDefault(f => Path.GetFileName(f).Equals("CATALOG.XML", StringComparison.OrdinalIgnoreCase))
            : null;
        if (cataloguePath is null)
            return Rejected(diagnostics, ChartDiagnosticCodes.CatalogueMissing, "No CATALOG.XML in the exchange-set root.",
                Path.Combine(root, "CATALOG.XML"));

        XDocument document;
        try
        {
            // DTDs are prohibited and nothing is resolved, so no entity or schema is ever fetched (C4, C5).
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(cataloguePath, settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException e)
        {
            var refusedDtd = File.ReadAllText(cataloguePath).Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
            return Rejected(diagnostics,
                refusedDtd ? ChartDiagnosticCodes.ExternalEntityRefused : ChartDiagnosticCodes.CatalogueMalformed,
                refusedDtd ? "The catalogue declares a DTD; entity resolution is refused." : "The catalogue is not well-formed XML: " + e.Message,
                cataloguePath, e.LineNumber);
        }

        var catalogueNamespace = document.Root!.Name.NamespaceName;
        var certificates = ByLocalName(document.Root, "certificate")
            .Where(c => c.Attribute("id") is not null)
            .GroupBy(c => c.Attribute("id")!.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Whitespace().Replace(g.First().Value, ""), StringComparer.Ordinal);

        var entries = new List<ExchangeSetEntry>();
        var found = 0;
        foreach (var (name, role) in EntryKinds)
        {
            foreach (var element in document.Root.Descendants(XName.Get(name, catalogueNamespace)))
            {
                found++;
                var fileName = Text(element, "fileName");
                if (string.IsNullOrEmpty(fileName))
                    return Rejected(diagnostics, ChartDiagnosticCodes.CatalogueElementMissing,
                        $"A {name} entry has no fileName element.", cataloguePath);
                var resolved = Resolve(root, fileName);
                if (resolved is null)
                {
                    diagnostics.Add(new ChartDiagnostic(ChartDiagnosticSeverity.Error, ChartDiagnosticCodes.PathOutsideRoot,
                        $"File name '{fileName}' resolves outside the exchange-set root; the entry is ignored.", cataloguePath));
                    continue;
                }
                var entry = ReadEntry(element, role, fileName, resolved);
                if (role == ExchangeEntryRole.Dataset && !entry.IsSupportedProduct)
                    diagnostics.Add(new ChartDiagnostic(ChartDiagnosticSeverity.Information, ChartDiagnosticCodes.ProductUnsupported,
                        $"{entry.ProductIdentifier} dataset '{fileName}' is listed but not read; only {SupportedProduct} is supported.", resolved));
                entries.Add(entry);
            }
        }
        if (found == 0)
            return Rejected(diagnostics, ChartDiagnosticCodes.CatalogueEmpty, "The catalogue lists no datasets, support files or catalogues.",
                cataloguePath);
        return new ExchangeSetReadResult(new ExchangeSet(root, catalogueNamespace, entries, certificates), diagnostics);
    }

    private static ExchangeSetReadResult Rejected(List<ChartDiagnostic> diagnostics, string code, string message, string file, long? position = null)
    {
        diagnostics.Add(new ChartDiagnostic(ChartDiagnosticSeverity.Error, code, message, file, position));
        return new ExchangeSetReadResult(null, diagnostics);
    }

    // Every observed form: "file:/S-101/…", "file:/S100_ROOT/…" (catalogue beside S100_ROOT), bare relative
    // paths and backslash separators. All resolve against the catalogue's own folder, never outside it.
    private static string? Resolve(string root, string fileName)
    {
        var relative = fileName.Trim();
        if (relative.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            relative = relative[5..];
        relative = Uri.UnescapeDataString(relative).Replace('\\', '/').TrimStart('/');
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Contains(':'))
            return null;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? full
            : null;
    }

    private static ExchangeSetEntry ReadEntry(XElement element, ExchangeEntryRole role, string fileName, string resolved)
    {
        var product = ProductOf(Text(element.Descendants().FirstOrDefault(e => e.Name.LocalName == "productSpecification"), "productIdentifier"))
            ?? ProductOf(fileName.Replace('\\', '/').Split('/').FirstOrDefault(s => ProductPattern().IsMatch(s)))
            ?? ProductFromName(Path.GetFileName(resolved));
        var box = Bounds(element);
        var coverages = ByLocalName(element, "dataCoverage").ToList();
        var polygons = ByLocalName(element, "posList")
            .Select(p => Polygon(p.Value, box))
            .Where(p => p.Count >= 3)
            .ToList();
        var signature = element.Descendants().FirstOrDefault(e => e.Name.LocalName is "S100_SE_SignatureOnData" or "S100_SE_DigitalSignature");
        return new ExchangeSetEntry(
            fileName,
            resolved,
            role,
            product,
            product == SupportedProduct,
            Text(element, "purpose"),
            Int(Text(element, "editionNumber")),
            Int(Text(element, "updateNumber")),
            Text(element, "issueDate"),
            box,
            polygons,
            coverages.Select(c => Int(Text(c, "optimumDisplayScale"))).FirstOrDefault(v => v is not null),
            coverages.Select(c => Int(Text(c, "minimumDisplayScale"))).FirstOrDefault(v => v is not null),
            coverages.Select(c => Int(Text(c, "maximumDisplayScale"))).FirstOrDefault(v => v is not null),
            signature is null ? null : new DatasetSignature(signature.Attribute("certificateRef")?.Value, signature.Value.Trim()));
    }

    private static string? ProductOf(string? text)
    {
        if (text is null)
            return null;
        var match = ProductPattern().Match(text);
        return match.Success ? match.Value : null;
    }

    // S-100 file names begin with the product number, e.g. "101…" for S-101.
    private static string ProductFromName(string name)
        => name.Length >= 3 && name[..3].All(char.IsAsciiDigit) ? "S-" + name[..3] : "unknown";

    private static GeoBounds? Bounds(XElement element)
    {
        var box = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "boundingBox");
        if (box is null)
            return null;
        double? Value(string name) => Double(Text(box, name));
        var (west, east, south, north) = (Value("westBoundLongitude"), Value("eastBoundLongitude"),
            Value("southBoundLatitude"), Value("northBoundLatitude"));
        return west is null || east is null || south is null || north is null ? null : new GeoBounds(west.Value, east.Value, south.Value, north.Value);
    }

    // S-100 5.2 writes latitude first (EPSG:4326 axis order); older catalogues wrote longitude first.
    // The order that fits the entry's bounding box wins; latitude first when there is no box.
    private static List<GeodeticPosition> Polygon(string posList, GeoBounds? box)
    {
        var numbers = posList.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToList();
        var pairs = Enumerable.Range(0, numbers.Count / 2).Select(i => (A: numbers[2 * i], B: numbers[2 * i + 1])).ToList();
        var latFirst = pairs.Select(p => new GeodeticPosition(p.A, p.B)).ToList();
        if (box is null)
            return latFirst;
        var lonFirst = pairs.Select(p => new GeodeticPosition(p.B, p.A)).ToList();
        int Inside(List<GeodeticPosition> points) => points.Count(p =>
            p.LatitudeDegrees >= box.South - 1e-6 && p.LatitudeDegrees <= box.North + 1e-6 &&
            p.LongitudeDegrees >= box.West - 1e-6 && p.LongitudeDegrees <= box.East + 1e-6);
        return Inside(lonFirst) > Inside(latFirst) ? lonFirst : latFirst;
    }

    private static IEnumerable<XElement> ByLocalName(XElement element, string name)
        => element.Descendants().Where(e => e.Name.LocalName == name);

    private static string? Text(XElement? element, string name)
    {
        var value = element?.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static int? Int(string? text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? Double(string? text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    [GeneratedRegex(@"S-\d+")]
    private static partial Regex ProductPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
