using Poseidon.Core.Modules;

namespace Poseidon.Modules.Charts.S101;

// RCNM and RCID: the record name (100 feature, 110 point, 115 multi point, 120 curve, 125 composite
// curve, 130 surface, 150 information type) and the record identification number.
public readonly record struct RecordName(int Kind, long Id);

public enum RecordInstruction
{
    Insert = 1,
    Delete = 2,
    Modify = 3
}

public enum PrimitiveKind
{
    None,
    Point,
    MultiPoint,
    Curve,
    Surface
}

public sealed record FeatureObjectIdentifier(int Agency, long Number, int Subdivision);

// Code is null when the numeric code has no entry in the dataset's code table; Children holds the
// sub-attributes of a complex attribute, in encoded order.
public sealed record S101Attribute(
    string? Code,
    int NumericCode,
    int Index,
    string? Value,
    IReadOnlyList<S101Attribute> Children);

public sealed record S101Association(
    RecordName Target,
    string? Name,
    string? Role,
    IReadOnlyList<S101Attribute> Attributes);

public sealed record S101SpatialReference(RecordName Target, int Orientation);

public sealed record S101Feature(
    RecordName Name,
    int Version,
    RecordInstruction Instruction,
    string? TypeCode,
    int NumericTypeCode,
    FeatureObjectIdentifier? Foid,
    PrimitiveKind Primitive,
    IReadOnlyList<S101Attribute> Attributes,
    IReadOnlyList<S101SpatialReference> Spatial,
    IReadOnlyList<S101Association> FeatureAssociations,
    IReadOnlyList<S101Association> InformationAssociations,
    IReadOnlyList<RecordName> Masks);

public sealed record S101InformationType(
    RecordName Name,
    int Version,
    RecordInstruction Instruction,
    string? TypeCode,
    int NumericTypeCode,
    IReadOnlyList<S101Attribute> Attributes,
    IReadOnlyList<S101Association> InformationAssociations);

// A depth or height kept apart from the horizontal position, with the vertical CRS it is measured in.
public sealed record VerticalValue(double Metres, int VerticalCrsIndex);

public sealed record SpatialPosition(GeodeticPosition Horizontal, VerticalValue? Vertical);

public abstract record S101Geometry;

public sealed record PointGeometry(SpatialPosition Position) : S101Geometry;

public sealed record MultiPointGeometry(IReadOnlyList<SpatialPosition> Positions) : S101Geometry;

public sealed record CurveGeometry(RecordName? Start, RecordName? End, IReadOnlyList<GeodeticPosition> Vertices)
    : S101Geometry;

public sealed record CompositeCurveGeometry(IReadOnlyList<S101SpatialReference> Components) : S101Geometry;

public sealed record SurfaceRing(RecordName Curve, int Orientation, bool Exterior);

public sealed record SurfaceGeometry(IReadOnlyList<SurfaceRing> Rings) : S101Geometry;

public sealed record S101SpatialRecord(
    RecordName Name,
    int Version,
    RecordInstruction Instruction,
    S101Geometry Geometry,
    IReadOnlyList<S101Association> InformationAssociations);

// ApplicationProfile is 1 for a base dataset and 2 for an update or cancellation.
public sealed record DatasetIdentity(
    string FileName,
    string EncodingSpecification,
    string EncodingEdition,
    string ProductIdentifier,
    string ProductEdition,
    int ApplicationProfile,
    int Edition,
    int Update,
    string Title,
    string ReferenceDate,
    string Language);

public sealed record CoordinateFactors(double X, double Y, double Z);

public sealed record CrsComponent(
    int Index,
    int Type,
    int CoordinateSystemType,
    string Name,
    string? Identifier,
    string? VerticalDatum);

public sealed record S101Dataset(
    DatasetIdentity Identity,
    CoordinateFactors Factors,
    IReadOnlyList<CrsComponent> CoordinateReferenceSystems,
    IReadOnlyList<S101Feature> Features,
    IReadOnlyList<S101InformationType> InformationTypes,
    IReadOnlyList<S101SpatialRecord> Spatial,
    IReadOnlyList<ChartDiagnostic> Diagnostics)
{
    // For update files: the records as encoded, with their own code tables, which the update applier needs
    // for the per-list instructions (ATIN, IUIN, SAUI, COCC, ...) the public model does not carry.
    internal UpdateSource? Source { get; init; }
}

internal sealed record UpdateSource(
    IReadOnlyList<(RecordName Name, Iso8211.Iso8211Record Record)> Records,
    S101CodeTables Codes,
    (double X, double Y, double Z) Origin);

internal sealed record S101CodeTables(
    IReadOnlyDictionary<long, string> Attributes,
    IReadOnlyDictionary<long, string> InformationTypes,
    IReadOnlyDictionary<long, string> FeatureTypes,
    IReadOnlyDictionary<long, string> InformationAssociations,
    IReadOnlyDictionary<long, string> FeatureAssociations,
    IReadOnlyDictionary<long, string> Roles);
