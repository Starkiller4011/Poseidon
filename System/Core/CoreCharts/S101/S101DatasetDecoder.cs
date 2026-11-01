using Poseidon.Core.Modules;
using Poseidon.Modules.Charts.Iso8211;

namespace Poseidon.Modules.Charts.S101;

public enum DecodeStatus
{
    Decoded,
    Rejected,
    UnsupportedVersion
}

public sealed record DecodeOptions
{
    public Iso8211Limits Limits { get; init; } = Iso8211Limits.Default;

    // Maps a declared product edition to the edition whose rules decode it. Test-only use (Q9): lets the
    // 2.1.0-labelled s100tds cases decode under the 2.0.0 rules. Empty in production.
    public IReadOnlyDictionary<string, string> ProductEditionOverrides { get; init; } =
        new Dictionary<string, string>();
}

public sealed record DecodeResult(DecodeStatus Status, S101Dataset? Dataset, IReadOnlyList<ChartDiagnostic> Diagnostics);

public interface IS101DatasetDecoder
{
    DecodeResult Decode(ReadOnlyMemory<byte> data, string fileName, DecodeOptions? options = null);
}

public sealed class S101DatasetDecoder : IS101DatasetDecoder
{
    private const string Encoding = "S-100 Part 10a";
    private const string Product = "INT.IHO.S-101";

    public DecodeResult Decode(ReadOnlyMemory<byte> data, string fileName, DecodeOptions? options = null)
    {
        options ??= new DecodeOptions();
        var diagnostics = new List<ChartDiagnostic>();
        Iso8211Document document;
        try
        {
            document = Iso8211Reader.Read(data.Span, options.Limits, S101Fields.AnnexBLayouts, tag =>
                diagnostics.Add(new ChartDiagnostic(ChartDiagnosticSeverity.Warning, ChartDiagnosticCodes.DdrDefinitionMissing,
                    $"The file uses field {tag} without defining it in its DDR; decoded with the S-101 Annex B layout.", fileName)));
        }
        catch (Iso8211FormatException e)
        {
            diagnostics.Add(new ChartDiagnostic(ChartDiagnosticSeverity.Error,
                e.Limit is null ? ChartDiagnosticCodes.DatasetMalformed : ChartDiagnosticCodes.LimitExceeded,
                e.Message, fileName, e.Position));
            return new DecodeResult(DecodeStatus.Rejected, null, diagnostics);
        }

        var dsid = document.Records.Count == 0 ? null : document.Records[0].Fields.FirstOrDefault(f => f.Tag == "DSID");
        if (dsid is null || !dsid.Values.ContainsKey("ENSP") || S101Fields.Text(dsid.Values, "ENSP") != Encoding
            || !(S101Fields.Text(dsid.Values, "PRSP") ?? "").StartsWith(Product, StringComparison.Ordinal))
            return Unsupported(diagnostics, fileName,
                "Not an S-101 dataset in the S-100 Part 10a encoding (for example an S-57 file or another S-100 product).");

        var productEdition = S101Fields.Text(dsid.Values, "PRED") ?? "";
        var normalized = Normalize(productEdition);
        var rulesEdition = options.ProductEditionOverrides.TryGetValue(normalized, out var mapped) ? mapped : normalized;
        if (!Version.TryParse(rulesEdition, out var version) || !(version.Major == 2 && version.Minor == 0 || version.Major == 1))
            return Unsupported(diagnostics, fileName, $"S-101 product edition '{productEdition}' is not supported (2.0 and pre-operational 1.x are).");
        if (version.Major == 1)
            diagnostics.Add(new ChartDiagnostic(ChartDiagnosticSeverity.Warning, ChartDiagnosticCodes.PreOperationalEdition,
                $"S-101 {productEdition} is a pre-operational edition; it is decoded at the record and field level.", fileName));

        var (edition, update) = S101Fields.EditionAndUpdate(S101Fields.Text(dsid.Values, "DSED"));
        var identity = new DatasetIdentity(
            fileName,
            Encoding,
            S101Fields.Text(dsid.Values, "ENED") ?? "",
            S101Fields.Text(dsid.Values, "PRSP") ?? "",
            productEdition,
            int.TryParse(S101Fields.Text(dsid.Values, "PROF"), out var profile) ? profile : 1,
            edition,
            update,
            S101Fields.Text(dsid.Values, "DSTL") ?? "",
            S101Fields.Text(dsid.Values, "DSRD") ?? "",
            S101Fields.Text(dsid.Values, "DSLG") ?? "");

        var general = document.Records[0].Fields;
        var dssi = general.FirstOrDefault(f => f.Tag == "DSSI");
        var factors = new CoordinateFactors(Factor(dssi, "CMFX"), Factor(dssi, "CMFY"), Factor(dssi, "CMFZ"));
        var origin = (X: S101Fields.Double(dssi?.Values, "DCOX"), Y: S101Fields.Double(dssi?.Values, "DCOY"), Z: S101Fields.Double(dssi?.Values, "DCOZ"));
        var codes = new S101CodeTables(Table(general, "ATCS", "ATCD", "ANCD"), Table(general, "ITCS", "ITCD", "ITNC"),
            Table(general, "FTCS", "FTCD", "FTNC"), Table(general, "IACS", "IACD", "IANC"),
            Table(general, "FACS", "FACD", "FANC"), Table(general, "ARCS", "ARCD", "ARNC"));
        var context = new S101Fields.Context(codes, factors, origin, diagnostics, fileName);

        var crs = new List<CrsComponent>();
        var features = new List<S101Feature>();
        var information = new List<S101InformationType>();
        var spatial = new List<S101SpatialRecord>();
        var raw = new List<(RecordName, Iso8211Record)>();
        foreach (var record in document.Records.Skip(1))
        {
            var head = record.Fields[0];
            if (head.Tag == "CSID")
            {
                crs.AddRange(S101Fields.CoordinateReferenceSystems(record));
                continue;
            }
            var name = S101Fields.Name(head);
            if (S101Fields.FeatureTags.Contains(head.Tag) || head.Tag is "IRID" || S101Fields.SpatialTags.Contains(head.Tag))
                raw.Add((name, record));
            switch (head.Tag)
            {
                case "FRID":
                    features.Add(S101Fields.Feature(record, context));
                    break;
                case "IRID":
                    information.Add(S101Fields.Information(record, context));
                    break;
                case "PRID" or "MRID" or "CRID" or "CCID" or "SRID":
                    spatial.Add(S101Fields.Spatial(record, context));
                    break;
            }
        }

        CheckCounts(dssi, features.Count, information.Count, spatial, diagnostics, fileName);
        // References in an update point into the base dataset, so they are checked only for base datasets.
        if (identity.ApplicationProfile == 1)
            CheckReferences(features, information, spatial, diagnostics, fileName);

        var dataset = new S101Dataset(identity, factors, crs, features, information, spatial, diagnostics)
        {
            Source = identity.ApplicationProfile == 2 ? new UpdateSource(raw, codes, origin) : null
        };
        return new DecodeResult(DecodeStatus.Decoded, dataset, diagnostics);
    }

    private static DecodeResult Unsupported(List<ChartDiagnostic> diagnostics, string fileName, string message)
    {
        diagnostics.Add(new ChartDiagnostic(ChartDiagnosticSeverity.Error, ChartDiagnosticCodes.VersionUnsupported, message, fileName));
        return new DecodeResult(DecodeStatus.UnsupportedVersion, null, diagnostics);
    }

    // "2.0" and "2.0.0" are the same edition.
    private static string Normalize(string edition)
    {
        var parts = edition.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count < 3)
            parts.Add("0");
        return string.Join('.', parts);
    }

    private static double Factor(Iso8211Field? dssi, string label)
    {
        var value = S101Fields.Double(dssi?.Values, label);
        return value == 0 ? 1 : value;
    }

    private static IReadOnlyDictionary<long, string> Table(IReadOnlyList<Iso8211Field> fields, string tag, string code, string number)
    {
        var table = new Dictionary<long, string>();
        foreach (var row in fields.Where(f => f.Tag == tag).SelectMany(f => f.Repeats))
            if (S101Fields.Text(row, code) is { } text)
                table[S101Fields.Long(row, number)] = text;
        return table;
    }

    private static void CheckCounts(Iso8211Field? dssi, int features, int information, List<S101SpatialRecord> spatial,
        List<ChartDiagnostic> diagnostics, string fileName)
    {
        if (dssi is null)
            return;
        var actual = new (string Label, long Count)[]
        {
            ("NOIR", information), ("NOPN", spatial.Count(s => s.Name.Kind == 110)), ("NOMN", spatial.Count(s => s.Name.Kind == 115)),
            ("NOCN", spatial.Count(s => s.Name.Kind == 120)), ("NOXN", spatial.Count(s => s.Name.Kind == 125)),
            ("NOSN", spatial.Count(s => s.Name.Kind == 130)), ("NOFR", features)
        };
        var wrong = actual.Where(a => S101Fields.Long(dssi.Values, a.Label) != a.Count)
            .Select(a => $"{a.Label} declares {S101Fields.Long(dssi.Values, a.Label)}, found {a.Count}").ToList();
        if (wrong.Count > 0)
            diagnostics.Add(new ChartDiagnostic(ChartDiagnosticSeverity.Warning, ChartDiagnosticCodes.RecordCountMismatch,
                "Declared record counts differ from the records present: " + string.Join("; ", wrong) + ".", fileName));
    }

    private static void CheckReferences(List<S101Feature> features, List<S101InformationType> information,
        List<S101SpatialRecord> spatial, List<ChartDiagnostic> diagnostics, string fileName)
    {
        var spatialNames = spatial.Select(s => s.Name).ToHashSet();
        var featureNames = features.Select(f => f.Name).ToHashSet();
        var informationNames = information.Select(i => i.Name).ToHashSet();
        void Check(RecordName from, RecordName to, HashSet<RecordName> known)
        {
            if (!known.Contains(to))
                diagnostics.Add(new ChartDiagnostic(ChartDiagnosticSeverity.Warning, ChartDiagnosticCodes.UnresolvedReference,
                    $"Record {from.Kind}/{from.Id} refers to {to.Kind}/{to.Id}, which is not in the dataset.", fileName));
        }
        foreach (var f in features)
        {
            foreach (var s in f.Spatial)
                Check(f.Name, s.Target, spatialNames);
            foreach (var m in f.Masks)
                Check(f.Name, m, spatialNames);
            foreach (var a in f.FeatureAssociations)
                Check(f.Name, a.Target, featureNames);
            foreach (var a in f.InformationAssociations)
                Check(f.Name, a.Target, informationNames);
        }
        foreach (var i in information)
            foreach (var a in i.InformationAssociations)
                Check(i.Name, a.Target, informationNames);
        foreach (var s in spatial)
        {
            foreach (var a in s.InformationAssociations)
                Check(s.Name, a.Target, informationNames);
            switch (s.Geometry)
            {
                case CurveGeometry c:
                    foreach (var end in new[] { c.Start, c.End }.OfType<RecordName>())
                        Check(s.Name, end, spatialNames);
                    break;
                case CompositeCurveGeometry cc:
                    foreach (var component in cc.Components)
                        Check(s.Name, component.Target, spatialNames);
                    break;
                case SurfaceGeometry sg:
                    foreach (var ring in sg.Rings)
                        Check(s.Name, ring.Curve, spatialNames);
                    break;
            }
        }
    }
}
