using System.Globalization;
using Poseidon.Core.Modules;
using Poseidon.Modules.Charts.Iso8211;

namespace Poseidon.Modules.Charts.S101;

// Turns S-101 ISO 8211 fields (S-101 Annex B) into the typed model. Shared by the decoder and the
// update applier, which builds inserted list items from update fields the same way.
internal static class S101Fields
{
    // S-101 Annex B field layouts, used only for a field a file uses without defining it in its DDR.
    public static readonly IReadOnlyDictionary<string, (string Labels, string Formats)> AnnexBLayouts =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["FRID"] = ("RCNM!RCID!NFTC!RVER!RUIN", "(b11,b14,2b12,b11)"),
            ["IRID"] = ("RCNM!RCID!NITC!RVER!RUIN", "(b11,b14,2b12,b11)"),
            ["PRID"] = ("RCNM!RCID!RVER!RUIN", "(b11,b14,b12,b11)"),
            ["MRID"] = ("RCNM!RCID!RVER!RUIN", "(b11,b14,b12,b11)"),
            ["CRID"] = ("RCNM!RCID!RVER!RUIN", "(b11,b14,b12,b11)"),
            ["CCID"] = ("RCNM!RCID!RVER!RUIN", "(b11,b14,b12,b11)"),
            ["SRID"] = ("RCNM!RCID!RVER!RUIN", "(b11,b14,b12,b11)"),
            ["FOID"] = ("AGEN!FIDN!FIDS", "(b12,b14,b12)"),
            ["ATTR"] = ("*NATC!ATIX!PAIX!ATIN!ATVL", "(3b12,b11,A)"),
            ["INAS"] = ("RRNM!RRID!NIAC!NARC!IUIN\\*NATC!ATIX!PAIX!ATIN!ATVL", "(b11,b14,2b12,b11,(3b12,b11,A))"),
            ["FASC"] = ("RRNM!RRID!NFAC!NARC!FAUI\\*NATC!ATIX!PAIX!ATIN!ATVL", "(b11,b14,2b12,b11,(3b12,b11,A))"),
            ["SPAS"] = ("*RRNM!RRID!ORNT!SMIN!SMAX!SAUI", "(b11,b14,b11,2b14,b11)"),
            ["MASK"] = ("*RRNM!RRID!MIND!MUIN", "(b11,b14,2b11)"),
            ["PTAS"] = ("*RRNM!RRID!TOPI", "(b11,b14,b11)"),
            ["CUCO"] = ("*RRNM!RRID!ORNT", "(b11,b14,b11)"),
            ["RIAS"] = ("*RRNM!RRID!ORNT!USAG!RAUI", "(b11,b14,3b11)"),
            ["SEGH"] = ("INTP", "(b11)"),
            ["C2IT"] = ("YCOO!XCOO", "(2b24)"),
            ["C3IT"] = ("VCID!YCOO!XCOO!ZCOO", "(b11,3b24)"),
            ["C2IL"] = ("*YCOO!XCOO", "(2b24)"),
            ["C3IL"] = ("VCID\\*YCOO!XCOO!ZCOO", "(b11,(3b24))"),
            ["COCC"] = ("COUI!COIX!NCOR", "(b11,2b12)"),
            ["SECC"] = ("SEUI!SEIX!NSEG", "(b11,2b12)"),
            ["CCOC"] = ("CCUI!CCIX!NCCO", "(b11,2b12)")
        };

    public static readonly HashSet<string> FeatureTags = ["FRID"];
    public static readonly HashSet<string> SpatialTags = ["PRID", "MRID", "CRID", "CCID", "SRID"];

    public sealed class Context(
        S101CodeTables codes,
        CoordinateFactors factors,
        (double X, double Y, double Z) origin,
        List<ChartDiagnostic> diagnostics,
        string fileName)
    {
        private readonly HashSet<(string, long)> reported = [];

        public S101CodeTables Codes { get; } = codes;
        public CoordinateFactors Factors { get; } = factors;
        public (double X, double Y, double Z) Origin { get; } = origin;

        public string? Lookup(IReadOnlyDictionary<long, string> table, long code, string what)
        {
            if (table.TryGetValue(code, out var text))
                return text;
            if (reported.Add((what, code)))
                diagnostics.Add(new ChartDiagnostic(ChartDiagnosticSeverity.Warning, ChartDiagnosticCodes.UnresolvedCode,
                    $"Numeric {what} code {code} has no entry in the dataset's code table; kept unresolved.", fileName));
            return null;
        }
    }

    public static RecordName Name(Iso8211Field head) => new((int)Long(head.Values, "RCNM"), Long(head.Values, "RCID"));

    public static RecordName Target(IReadOnlyDictionary<string, object?> values)
        => new((int)Long(values, "RRNM"), Long(values, "RRID"));

    public static RecordInstruction Instruction(IReadOnlyDictionary<string, object?> values, string label)
        => Long(values, label) switch
        {
            2 => RecordInstruction.Delete,
            3 => RecordInstruction.Modify,
            _ => RecordInstruction.Insert
        };

    public static S101Feature Feature(Iso8211Record record, Context context)
    {
        var head = record.Fields[0];
        var spatial = Rows(record, "SPAS").Select(r => new S101SpatialReference(Target(r), (int)Long(r, "ORNT"))).ToList();
        var foid = record.Fields.FirstOrDefault(f => f.Tag == "FOID");
        var numeric = Long(head.Values, "NFTC");
        return new S101Feature(
            Name(head),
            (int)Long(head.Values, "RVER"),
            Instruction(head.Values, "RUIN"),
            context.Lookup(context.Codes.FeatureTypes, numeric, "feature type"),
            (int)numeric,
            foid is null ? null : new FeatureObjectIdentifier((int)Long(foid.Values, "AGEN"), Long(foid.Values, "FIDN"), (int)Long(foid.Values, "FIDS")),
            spatial.Count == 0 ? PrimitiveKind.None : Primitive(spatial[0].Target.Kind),
            Attributes(record.Fields.Where(f => f.Tag == "ATTR").SelectMany(f => f.Repeats).ToList(), context),
            spatial,
            record.Fields.Where(f => f.Tag == "FASC").Select(f => FeatureAssociation(f, context)).ToList(),
            record.Fields.Where(f => f.Tag == "INAS").Select(f => InformationAssociation(f, context)).ToList(),
            Rows(record, "MASK").Select(Target).ToList());
    }

    public static S101InformationType Information(Iso8211Record record, Context context)
    {
        var head = record.Fields[0];
        var numeric = Long(head.Values, "NITC");
        return new S101InformationType(
            Name(head),
            (int)Long(head.Values, "RVER"),
            Instruction(head.Values, "RUIN"),
            context.Lookup(context.Codes.InformationTypes, numeric, "information type"),
            (int)numeric,
            Attributes(record.Fields.Where(f => f.Tag == "ATTR").SelectMany(f => f.Repeats).ToList(), context),
            record.Fields.Where(f => f.Tag == "INAS").Select(f => InformationAssociation(f, context)).ToList());
    }

    public static S101SpatialRecord Spatial(Iso8211Record record, Context context)
    {
        var head = record.Fields[0];
        S101Geometry geometry = head.Tag switch
        {
            "PRID" => new PointGeometry(Positions(record, context).FirstOrDefault()
                ?? new SpatialPosition(new GeodeticPosition(double.NaN, double.NaN), null)),
            "MRID" => new MultiPointGeometry(Positions(record, context)),
            "CRID" => Curve(record, context),
            "CCID" => new CompositeCurveGeometry(Rows(record, "CUCO").Select(r => new S101SpatialReference(Target(r), (int)Long(r, "ORNT"))).ToList()),
            _ => new SurfaceGeometry(Rows(record, "RIAS").Select(Ring).ToList())
        };
        return new S101SpatialRecord(
            Name(head),
            (int)Long(head.Values, "RVER"),
            Instruction(head.Values, "RUIN"),
            geometry,
            record.Fields.Where(f => f.Tag == "INAS").Select(f => InformationAssociation(f, context)).ToList());
    }

    public static SurfaceRing Ring(IReadOnlyDictionary<string, object?> row)
        => new(Target(row), (int)Long(row, "ORNT"), Long(row, "USAG") != 2);

    public static CurveGeometry Curve(Iso8211Record record, Context context)
    {
        RecordName? start = null, end = null;
        foreach (var row in Rows(record, "PTAS"))
        {
            var topology = Long(row, "TOPI");
            if (topology is 1 or 3)
                start = Target(row);
            if (topology is 2 or 3)
                end = Target(row);
        }
        return new CurveGeometry(start, end, record.Fields.Where(f => f.Tag is "C2IL" or "C3IL")
            .SelectMany(f => Coordinates(f, context)).Select(p => p.Horizontal).ToList());
    }

    public static List<SpatialPosition> Positions(Iso8211Record record, Context context)
        => record.Fields.Where(f => f.Tag is "C2IT" or "C3IT" or "C2IL" or "C3IL").SelectMany(f => Coordinates(f, context)).ToList();

    // Integer coordinates scaled by the dataset's own factors and origin (Part 10a; S-101 §10.1).
    public static IEnumerable<SpatialPosition> Coordinates(Iso8211Field field, Context context)
    {
        var rows = field.Repeats.Count > 0 ? field.Repeats : [field.Values];
        var verticalCrs = field.Values.ContainsKey("VCID") ? (int)Long(field.Values, "VCID") : 0;
        foreach (var row in rows)
        {
            var position = new GeodeticPosition(
                Long(row, "YCOO") / context.Factors.Y + context.Origin.Y,
                Long(row, "XCOO") / context.Factors.X + context.Origin.X);
            VerticalValue? vertical = row.ContainsKey("ZCOO")
                ? new VerticalValue(Long(row, "ZCOO") / context.Factors.Z + context.Origin.Z,
                    row.ContainsKey("VCID") ? (int)Long(row, "VCID") : verticalCrs)
                : null;
            yield return new SpatialPosition(position, vertical);
        }
    }

    public static S101Association InformationAssociation(Iso8211Field field, Context context)
        => new(Target(field.Values),
            context.Lookup(context.Codes.InformationAssociations, Long(field.Values, "NIAC"), "information association"),
            context.Lookup(context.Codes.Roles, Long(field.Values, "NARC"), "association role"),
            Attributes(field.Repeats, context));

    public static S101Association FeatureAssociation(Iso8211Field field, Context context)
        => new(Target(field.Values),
            context.Lookup(context.Codes.FeatureAssociations, Long(field.Values, "NFAC"), "feature association"),
            context.Lookup(context.Codes.Roles, Long(field.Values, "NARC"), "association role"),
            Attributes(field.Repeats, context));

    // ATTR rows are encoded in pre-order: PAIX is the 1-based row of the parent within the same field,
    // 0 for a top-level attribute (Part 10a), so nesting and order rebuild exactly.
    public static IReadOnlyList<S101Attribute> Attributes(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, Context context)
    {
        var nodes = rows.Select(r => new AttributeNode(
            context.Lookup(context.Codes.Attributes, Long(r, "NATC"), "attribute"),
            (int)Long(r, "NATC"), (int)Long(r, "ATIX"), Value(r))).ToList();
        var top = new List<AttributeNode>();
        for (var i = 0; i < rows.Count; i++)
        {
            var parent = Long(rows[i], "PAIX");
            if (parent >= 1 && parent <= i)
                nodes[(int)parent - 1].Children.Add(nodes[i]);
            else
                top.Add(nodes[i]);
        }
        return top.Select(n => n.Build()).ToList();
    }

    public static string? Value(IReadOnlyDictionary<string, object?> row)
        => Text(row, "ATVL") is { Length: > 0 } text ? text : null;

    public sealed class AttributeNode(string? code, int numericCode, int index, string? value)
    {
        public string? Code { get; set; } = code;
        public int NumericCode { get; set; } = numericCode;
        public int Index { get; set; } = index;
        public string? Value { get; set; } = value;
        public List<AttributeNode> Children { get; } = [];

        public static AttributeNode From(S101Attribute a)
        {
            var node = new AttributeNode(a.Code, a.NumericCode, a.Index, a.Value);
            node.Children.AddRange(a.Children.Select(From));
            return node;
        }

        public S101Attribute Build() => new(Code, NumericCode, Index, Value, Children.Select(c => c.Build()).ToList());
    }

    public static IEnumerable<CrsComponent> CoordinateReferenceSystems(Iso8211Record record)
    {
        CrsComponentBuilder? current = null;
        var components = new List<CrsComponentBuilder>();
        foreach (var field in record.Fields)
        {
            if (field.Tag == "CRSH")
                components.Add(current = new CrsComponentBuilder(field.Values));
            else if (field.Tag == "VDAT" && current is not null)
                current.VerticalDatum = Text(field.Values, "DTNM");
        }
        return components.Select(c => c.Build());
    }

    private sealed class CrsComponentBuilder(IReadOnlyDictionary<string, object?> values)
    {
        public string? VerticalDatum { get; set; }

        public CrsComponent Build() => new(
            (int)Long(values, "CRIX"), (int)Long(values, "CRST"), (int)Long(values, "CSTY"),
            Text(values, "CRNM") ?? "", Text(values, "CRSI") is { Length: > 0 } id ? id : null, VerticalDatum);
    }

    public static PrimitiveKind Primitive(int kind) => kind switch
    {
        110 => PrimitiveKind.Point,
        115 => PrimitiveKind.MultiPoint,
        120 or 125 => PrimitiveKind.Curve,
        130 => PrimitiveKind.Surface,
        _ => PrimitiveKind.None
    };

    public static IEnumerable<IReadOnlyDictionary<string, object?>> Rows(Iso8211Record record, string tag)
        => record.Fields.Where(f => f.Tag == tag).SelectMany(f => f.Repeats);

    // DSED is "N" or "N.0" for a base, "N.M" for an update and "0" for a cancellation.
    public static (int Edition, int Update) EditionAndUpdate(string? dsed)
    {
        var parts = (dsed ?? "").Trim().Split('.');
        var edition = int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) ? e : 0;
        var update = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : 0;
        return (edition, update);
    }

    public static string? Text(IReadOnlyDictionary<string, object?>? values, string label)
        => values is not null && values.TryGetValue(label, out var v) && v is string s ? s.Trim() : null;

    public static long Long(IReadOnlyDictionary<string, object?>? values, string label)
        => values is not null && values.TryGetValue(label, out var v) ? v switch
        {
            long l => l,
            double d => (long)d,
            string s when long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        } : 0;

    public static double Double(IReadOnlyDictionary<string, object?>? values, string label)
        => values is not null && values.TryGetValue(label, out var v) ? v switch
        {
            double d => d,
            long l => l,
            _ => 0
        } : 0;
}
