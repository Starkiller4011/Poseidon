using System.Buffers.Binary;
using Poseidon.Core.Modules;
using Poseidon.Modules.Charts;
using Poseidon.Modules.Charts.S101;
using Xunit;

namespace CoreCharts.Tests;

public sealed class S101DatasetDecoderTests
{
    private static readonly string[] SpatialTags = ["PRID", "MRID", "CRID", "CCID", "SRID"];

    private static string Released200 => Fixtures.DatasetFile(Fixtures.ReleasedPowerUp, "10100AA_X01SW.000");
    private static string Primary120 => Path.Combine(Fixtures.S101TestDatasets, "S-101_1.2", "000", "101AA00DS0001.000");
    private static string Soundings120 => Path.Combine(Fixtures.S101TestDatasets, "S-101_1.2", "000", "101AA00DS0011.000");
    private static string Oracle(string cell) => Path.Combine(Fixtures.S101TestDatasets, "S-101_1.2", "yaml", cell + ".yaml");

    // C10
    [Fact]
    public void Decode_Edition200BaseCell_ExposesIdentityVersionsCrsAndDistinctRecordCollections()
    {
        var data = File.ReadAllBytes(Released200);
        var records = Iso8211Probe.Walk(data).Skip(1).ToList();
        var dssi = Iso8211Probe.FirstField(data, "DSSI");

        var dataset = Decoding.Dataset(data, "10100AA_X01SW.000");

        var identity = dataset.Identity;
        Assert.Equal("10100AA_X01SW.000", identity.FileName);
        Assert.Equal("S-100 Part 10a", identity.EncodingSpecification);
        Assert.Equal("5.2", identity.EncodingEdition);
        Assert.StartsWith("INT.IHO.S-101", identity.ProductIdentifier, StringComparison.Ordinal);
        Assert.Equal("2.0.0", identity.ProductEdition);
        Assert.Equal(1, identity.ApplicationProfile);
        Assert.Equal(3, identity.Edition);
        Assert.Equal(0, identity.Update);
        Assert.Equal(new CoordinateFactors(
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(dssi.Start + 24)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(dssi.Start + 28)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(dssi.Start + 32))), dataset.Factors);
        var horizontal = Assert.Single(dataset.CoordinateReferenceSystems, c => c.Index == 1);
        Assert.Equal("4326", horizontal.Identifier);
        Assert.Equal(records.Count(r => r.Fields[0].Tag == "FRID"), dataset.Features.Count);
        Assert.Equal(records.Count(r => r.Fields[0].Tag == "IRID"), dataset.InformationTypes.Count);
        Assert.Equal(records.Count(r => SpatialTags.Contains(r.Fields[0].Tag)), dataset.Spatial.Count);
    }

    // C11
    [Fact]
    public void Decode_Features_KeepFoidTypeAttributesTreesAndAssociations()
    {
        var dataset = Decoding.Dataset(Released200);

        Assert.All(dataset.Features, f =>
        {
            Assert.NotNull(f.Foid);
            Assert.False(string.IsNullOrEmpty(f.TypeCode));
            Assert.Equal(f.Spatial.Count == 0, f.Primitive == PrimitiveKind.None);
        });
        Assert.Contains(dataset.Features.SelectMany(f => f.Attributes), a => a.Children.Count > 0 && a.Children.All(c => c.Code is not null));
        var associations = dataset.Features.SelectMany(f => f.InformationAssociations).ToList();
        Assert.NotEmpty(associations);
        Assert.All(associations, a =>
        {
            Assert.Equal(150, a.Target.Kind);
            Assert.False(string.IsNullOrEmpty(a.Name));
            Assert.False(string.IsNullOrEmpty(a.Role));
        });
    }

    // C12: every geometry reference resolves to a decoded spatial record, and shared records are shared.
    [Fact]
    public void Decode_SpatialReferences_AllResolveAndSharedRecordsAreShared()
    {
        var dataset = Decoding.Dataset(Released200);
        var spatial = dataset.Spatial.ToDictionary(s => s.Name);

        var references = dataset.Features.SelectMany(f => f.Spatial.Select(s => s.Target))
            .Concat(dataset.Spatial.Select(s => s.Geometry).OfType<SurfaceGeometry>().SelectMany(g => g.Rings.Select(r => r.Curve)))
            .Concat(dataset.Spatial.Select(s => s.Geometry).OfType<CompositeCurveGeometry>().SelectMany(g => g.Components.Select(c => c.Target)))
            .Concat(dataset.Spatial.Select(s => s.Geometry).OfType<CurveGeometry>().SelectMany(g => new[] { g.Start, g.End }.OfType<RecordName>()))
            .ToList();
        Assert.NotEmpty(references);
        Assert.All(references, r => Assert.True(spatial.ContainsKey(r), $"Unresolved spatial reference {r}"));
        Assert.Contains(references.GroupBy(r => r), g => g.Count() > 1);
        Assert.DoesNotContain(dataset.Diagnostics, d => d.Code == ChartDiagnosticCodes.UnresolvedReference);
    }

    // C12: references are resolved after the whole file is read, so record order does not matter.
    [Fact]
    public void Decode_FeaturesBeforeTheirGeometry_ResolveTheSameAsTheOriginalOrder()
    {
        var data = File.ReadAllBytes(Released200);
        var reordered = Iso8211Probe.Reorder(data, records => records.Take(2)
            .Concat(records.Skip(2).Where(r => r.Fields[0].Tag == "FRID"))
            .Concat(records.Skip(2).Where(r => r.Fields[0].Tag != "FRID")));

        var original = Decoding.Dataset(data, "10100AA_X01SW.000");
        var shuffled = Decoding.Dataset(reordered, "10100AA_X01SW.000");

        Assert.Equal(Canonical.Of(original.Features.OrderBy(f => f.Name.Id)), Canonical.Of(shuffled.Features.OrderBy(f => f.Name.Id)));
        Assert.Equal(Canonical.Of(original.Spatial.OrderBy(s => (s.Name.Kind, s.Name.Id))),
            Canonical.Of(shuffled.Spatial.OrderBy(s => (s.Name.Kind, s.Name.Id))));
        Assert.DoesNotContain(shuffled.Diagnostics, d => d.Code == ChartDiagnosticCodes.UnresolvedReference);
    }

    // C13: positions and depths use the dataset's own factors; depth stays apart from the horizontal
    // position, with its own vertical CRS.
    [Fact]
    public void Decode_Coordinates_MatchTheOracleUsingTheDatasetsOwnFactors()
    {
        var oracle = YamlOracle.Load(Oracle("101AA00DS0011"));
        var dataset = Decoding.Dataset(Soundings120);

        Assert.NotEmpty(oracle.Depths);
        foreach (var expected in oracle.Depths)
        {
            var geometry = ByFirstPosition(dataset, expected).Geometry;
            var positions = geometry is MultiPointGeometry m ? m.Positions : [Assert.IsType<PointGeometry>(geometry).Position];
            Assert.Equal(expected.Z.Count, positions.Count);
            for (var i = 0; i < positions.Count; i++)
            {
                Assert.Equal(expected.Locations[i].Lat, positions[i].Horizontal.LatitudeDegrees, 7);
                Assert.Equal(expected.Locations[i].Lon, positions[i].Horizontal.LongitudeDegrees, 7);
                var vertical = Assert.IsType<VerticalValue>(positions[i].Vertical);
                Assert.Equal(expected.Z[i], vertical.Metres, 9);
                Assert.Contains(dataset.CoordinateReferenceSystems, c => c.Index == vertical.VerticalCrsIndex && c.Index > 1);
            }
        }
        foreach (var expected in oracle.Points)
        {
            var point = Assert.IsType<PointGeometry>(ByFirstPosition(dataset, expected).Geometry);
            Assert.Equal(expected.Locations[0].Lat, point.Position.Horizontal.LatitudeDegrees, 7);
            Assert.Equal(expected.Locations[0].Lon, point.Position.Horizontal.LongitudeDegrees, 7);
        }
    }

    // C13: the factor is read from the dataset, not assumed: a CMFZ of 100 divides depths ten times more.
    [Fact]
    public void Decode_DifferentDepthFactor_ScalesDepthsByThatFactor()
    {
        var oracle = YamlOracle.Load(Oracle("101AA00DS0011"));
        var data = File.ReadAllBytes(Soundings120);
        var dssi = Iso8211Probe.FirstField(data, "DSSI");
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(dssi.Start + 32)));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(dssi.Start + 32), 100);

        var dataset = Decoding.Dataset(data, "101AA00DS0011.000");

        Assert.Equal(100, dataset.Factors.Z);
        var expected = oracle.Depths[0];
        var geometry = ByFirstPosition(dataset, expected).Geometry;
        var first = geometry is MultiPointGeometry m ? m.Positions[0] : Assert.IsType<PointGeometry>(geometry).Position;
        Assert.Equal(expected.Z[0] / 10, Assert.IsType<VerticalValue>(first.Vertical).Metres, 9);
    }

    // C14
    [Fact]
    public void Decode_UnknownFeatureTypeCode_IsKeptAndReported()
    {
        var data = File.ReadAllBytes(Released200);
        var frid = Iso8211Probe.FirstField(data, "FRID");
        var id = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(frid.Start + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(frid.Start + 5), 65534);
        var original = Decoding.Dataset(Released200);

        var dataset = Decoding.Dataset(data, "10100AA_X01SW.000");

        Assert.Equal(original.Features.Count, dataset.Features.Count);
        var feature = Assert.Single(dataset.Features, f => f.Name == new RecordName(100, id));
        Assert.Null(feature.TypeCode);
        Assert.Equal(65534, feature.NumericTypeCode);
        Assert.Contains(dataset.Diagnostics, d => d.Code == ChartDiagnosticCodes.UnresolvedCode);
    }

    // C14
    [Fact]
    public void Decode_UnknownGeometryReference_IsKeptAndReported()
    {
        var data = File.ReadAllBytes(Released200);
        var record = Iso8211Probe.Walk(data).Skip(1).First(r => r.Fields.Any(f => f.Tag == "SPAS"));
        var id = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(record.Fields[0].Start + 1));
        var spas = record.Fields.First(f => f.Tag == "SPAS");
        var kind = data[spas.Start];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(spas.Start + 1), 0x7FFFFF00);

        var dataset = Decoding.Dataset(data, "10100AA_X01SW.000");

        var feature = Assert.Single(dataset.Features, f => f.Name == new RecordName(100, id));
        Assert.Contains(feature.Spatial, s => s.Target == new RecordName(kind, 0x7FFFFF00));
        Assert.Contains(dataset.Diagnostics, d => d.Code == ChartDiagnosticCodes.UnresolvedReference);
    }

    // C15: the audited 1.2.0 base cell against its YAML sidecar (an oracle, not an authority).
    [Fact]
    public void Decode_PrimaryCell_MatchesItsYamlOracle()
    {
        var oracle = YamlOracle.Load(Oracle("101AA00DS0001"));
        Assert.NotEmpty(oracle.Features);

        var dataset = Decoding.Dataset(Primary120);

        Assert.Equal(oracle.CellName, dataset.Identity.FileName);
        foreach (var expected in oracle.Features)
        {
            var foid = expected.Foid.Split(':').Select(long.Parse).ToArray();
            var feature = Assert.Single(dataset.Features,
                f => f.Foid == new FeatureObjectIdentifier((int)foid[0], foid[1], (int)foid[2]));
            Assert.Equal(expected.Name, feature.TypeCode);
            Assert.Equal(expected.Prim, feature.Primitive.ToString());
            Assert.Equal(OracleAttributes(expected.Attributes, null), Canonical.Of(feature.Attributes.Select(Shape)));
            foreach (var association in expected.Associations)
            {
                // "150/21": the record kind is real, the number is the producer tool's own (see ByFirstPosition).
                var kind = int.Parse(association.To.Split('/')[0]);
                Assert.Contains(feature.InformationAssociations.Concat(feature.FeatureAssociations), a =>
                    a.Target.Kind == kind && a.Name == association.Name && a.Role == association.Role);
            }
            // The sidecar's geometry names are the producer tool's own, not encoded record IDs, so only
            // the number of spatial references is comparable (the primitive kind is asserted above).
            Assert.Equal(expected.Geometry.Count, feature.Spatial.Count);
        }
    }

    // C34: S-101 2.0 data decodes, whichever way its version string is written.
    [Theory]
    [MemberData(nameof(OperationalEditions))]
    public void Decode_Edition20_IsSupported(string path)
    {
        var result = Decoding.Result(path);

        Assert.Equal(DecodeStatus.Decoded, result.Status);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.PreOperationalEdition);
    }

    public static TheoryData<string> OperationalEditions() => new()
    {
        Released200,
        Directory.EnumerateFiles(Path.Combine(Fixtures.S101TestDatasets, "S-101_Test_DataSets", "cells", "101AA00DS0001"),
            "*.000", SearchOption.AllDirectories).Single()
    };

    // C34 and Q6: pre-operational 1.x editions decode at the record level, with a diagnostic saying so.
    [Theory]
    [MemberData(nameof(PreOperationalEditions))]
    public void Decode_Edition1x_IsDecodedWithAPreOperationalDiagnostic(string path)
    {
        var result = Decoding.Result(path);

        Assert.Equal(DecodeStatus.Decoded, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.PreOperationalEdition);
    }

    public static TheoryData<string> PreOperationalEditions() => new()
    {
        Primary120,
        Fixtures.DatasetFile(Path.Combine(Fixtures.S101TestDatasets, "S-158", "V1", "TestDataSets", "00AA_00010"), "10100AA_00010.000"),
        Fixtures.DatasetFile(Path.Combine(Fixtures.S101TestDatasets, "S-101_Test_DataSets", "exports", "1.0", "IHO_V10"), "101AA00DS0003.000")
    };

    // C34 and C29: other products and unknown editions are an explicit unsupported result, never a decode.
    [Theory]
    [MemberData(nameof(UnsupportedFiles))]
    public void Decode_UnsupportedProductOrEdition_IsReportedUnsupported(string path)
    {
        var result = Decoding.Result(path);

        Assert.Equal(DecodeStatus.UnsupportedVersion, result.Status);
        Assert.Null(result.Dataset);
        Assert.Contains(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.VersionUnsupported);
    }

    public static TheoryData<string> UnsupportedFiles() => new()
    {
        Fixtures.DatasetFile(Fixtures.Released("2.1_CatalogueLoading", "MultipleRevisions"), "10100AASPLIT01.000"),
        Path.Combine(Fixtures.S164SubGroup, "data", "Staging", "S-57", "AA3ARSPC.000"),
        Path.Combine(Fixtures.S100Tds, "upd", "2.1.0", "base", "UPDBASE.000")
    };

    // C34 and Q9: the test-only override decodes the 2.1.0-labelled s100tds data under the 2.0.0 rules.
    [Fact]
    public void Decode_S100TdsWithTestOverride_IsDecoded()
    {
        var dataset = Decoding.Dataset(Path.Combine(Fixtures.S100Tds, "upd", "2.1.0", "base", "UPDBASE.000"), Decoding.S100TdsOverride);

        Assert.Equal("2.1.0", dataset.Identity.ProductEdition);
        Assert.Equal(9, dataset.Features.Count);
    }

    // C35: declared DSSI record counts that do not match the records present are a warning only.
    [Fact]
    public void Decode_WrongDeclaredRecordCounts_IsAWarningNotARejection()
    {
        var result = Decoding.Result(Primary120);

        Assert.Equal(DecodeStatus.Decoded, result.Status);
        var warning = Assert.Single(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.RecordCountMismatch);
        Assert.Equal(ChartDiagnosticSeverity.Warning, warning.Severity);
    }

    // C9
    [Fact]
    public void Decode_SameFileTwice_ProducesStructurallyEqualOutput()
    {
        Assert.Equal(Canonical.Of(Decoding.Result(Released200)), Canonical.Of(Decoding.Result(Released200)));
    }

    // Sidecar names start with the record's three-digit RCNM ("P115…" is a multi point), but the rest is
    // the producer tool's own numbering, not the encoded RCID (115/1 is called "P11511"). So an entry is
    // found by its record kind and first coordinate instead.
    // Two records can sit at the same position (this cell has two points at one place), so the entry's
    // whole coordinate list must match, and any one such record will do.
    private static S101SpatialRecord ByFirstPosition(S101Dataset dataset, YamlOracle.Positions expected)
    {
        var kind = int.Parse(expected.Name.TrimStart('R', 'P', 'C', 'S')[..3]);
        var match = dataset.Spatial.FirstOrDefault(s => s.Name.Kind == kind && Horizontals(s.Geometry) is { } positions
            && positions.Count == expected.Locations.Count
            && positions.Zip(expected.Locations).All(p => Math.Abs(p.First.LatitudeDegrees - p.Second.Lat) < 1e-7
                && Math.Abs(p.First.LongitudeDegrees - p.Second.Lon) < 1e-7));
        Assert.True(match is not null, $"No {kind} record at the positions of sidecar entry {expected.Name}");
        return match;
    }

    private static List<GeodeticPosition>? Horizontals(S101Geometry geometry) => geometry switch
    {
        PointGeometry p => [p.Position.Horizontal],
        MultiPointGeometry m => m.Positions.Select(x => x.Horizontal).ToList(),
        _ => null
    };


    private static object Shape(S101Attribute a) => new { a.Code, Value = a.Value?.Trim(), Children = a.Children.Select(Shape).ToList() };

    // The sidecar flattens complex attributes with id/parent markers; rebuild the same tree shape.
    private static string OracleAttributes(IReadOnlyList<YamlOracle.Attribute> all, string? parent)
    {
        var level = all.Where(a => a.Parent == parent).Select(a => (object)new
        {
            Code = a.Name,
            Value = a.Id is null ? a.Value?.Trim() : null,
            Children = a.Id is null ? new List<object>() : ChildShapes(all, a.Id)
        });
        return Canonical.Of(level);
    }

    private static List<object> ChildShapes(IReadOnlyList<YamlOracle.Attribute> all, string parent)
        => all.Where(a => a.Parent == parent).Select(a => (object)new
        {
            Code = a.Name,
            Value = a.Id is null ? a.Value?.Trim() : null,
            Children = a.Id is null ? new List<object>() : ChildShapes(all, a.Id)
        }).ToList();
}
