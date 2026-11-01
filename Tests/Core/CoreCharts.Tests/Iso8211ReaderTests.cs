using System.Text;
using Poseidon.Modules.Charts.Iso8211;
using Xunit;

namespace CoreCharts.Tests;

public sealed class Iso8211ReaderTests
{
    private static string PrimaryCell => Path.Combine(Fixtures.S101TestDatasets, "S-101_1.2", "000", "101AA00DS0001.000");

    // S-101 2.0.0: repeating groups nested in the format controls, text fields marked UTF-8 (%/G).
    private static string Edition200Cell => Fixtures.DatasetFile(Fixtures.ReleasedPowerUp, "10100AA_X01SW.000");

    // S-101 1.1.0 / Part 10a 1.1: repeating groups written flat, text fields unmarked. Chosen because it
    // carries both ATTR and INAS fields.
    private static string Edition110Cell => Fixtures.DatasetFile(
        Path.Combine(Fixtures.S101TestDatasets, "S-158", "V1", "TestDataSets", "00AA_00004"), "10100AA_00004.000");

    // C6
    [Fact]
    public void Read_PrimaryBaseCell_ReturnsDdrDefinitionsAndEveryDataRecordInOrder()
    {
        var data = File.ReadAllBytes(PrimaryCell);
        var probe = Iso8211Probe.Walk(data);

        var document = Iso8211Reader.Read(data);

        Assert.Equal(probe.Skip(1).Select(r => (long)r.Start), document.Records.Select(r => r.Position));
        Assert.Equal(probe.Skip(1).Select(r => string.Join(",", r.Fields.Select(f => f.Tag))),
            document.Records.Select(r => string.Join(",", r.Fields.Select(f => f.Tag))));
        var dsid = Assert.Single(document.Definitions, d => d.Tag == "DSID");
        Assert.Contains("DSED", dsid.Labels);
        var first = document.Records[0].Fields[0];
        Assert.Equal("DSID", first.Tag);
        Assert.Equal("S-100 Part 10a", first.Values["ENSP"]);
        Assert.Equal("INT.IHO.S-101.1.2.0", first.Values["PRSP"]);
    }

    // C6: the subfield layout comes from each file's own DDR, so both format-control styles decode.
    [Theory]
    [MemberData(nameof(BothFormatStyles))]
    public void Read_RepeatingGroups_DecodeFromEitherFormatControlStyle(string path, bool nested)
    {
        var document = Iso8211Reader.Read(File.ReadAllBytes(path));

        Assert.True(Assert.Single(document.Definitions, d => d.Tag == "ATTR").Repeating);
        // INAS has a fixed part followed by a repeating group: nested "(...,(...))" in 2.0.0, flat in 1.x.
        var inasDefinition = Assert.Single(document.Definitions, d => d.Tag == "INAS");
        Assert.Equal(nested, inasDefinition.FormatControls.Contains(",(", StringComparison.Ordinal));
        var attributes = document.Records.SelectMany(r => r.Fields).Where(f => f.Tag == "ATTR").ToList();
        Assert.NotEmpty(attributes);
        Assert.All(attributes, field =>
        {
            Assert.Empty(field.Values);
            Assert.NotEmpty(field.Repeats);
            for (var i = 0; i < field.Repeats.Count; i++)
            {
                var row = field.Repeats[i];
                Assert.Equal(["ATIN", "ATIX", "ATVL", "NATC", "PAIX"], row.Keys.Order(StringComparer.Ordinal));
                Assert.IsType<long>(row["NATC"]);
                Assert.True((long)row["NATC"]! > 0);
                // Attributes are encoded in pre-order, so a parent always precedes its children.
                Assert.InRange((long)row["PAIX"]!, 0, i);
                Assert.True(row["ATVL"] is null or string);
            }
        });
        var inas = document.Records.SelectMany(r => r.Fields).First(f => f.Tag == "INAS");
        Assert.Equal(["IUIN", "NARC", "NIAC", "RRID", "RRNM"], inas.Values.Keys.Order(StringComparer.Ordinal));
        Assert.All(inas.Repeats, row => Assert.Equal(["ATIN", "ATIX", "ATVL", "NATC", "PAIX"], row.Keys.Order(StringComparer.Ordinal)));
    }

    public static TheoryData<string, bool> BothFormatStyles() => new()
    {
        { Edition200Cell, true },
        { Edition110Cell, false }
    };

    // C6: a field the DDR marks UTF-8 decodes multi-byte characters.
    [Fact]
    public void Read_Utf8MarkedField_DecodesMultiByteText()
    {
        var data = File.ReadAllBytes(Edition200Cell);
        var attr = Iso8211Probe.Walk(data).Skip(1).SelectMany(r => r.Fields).Where(f => f.Tag == "ATTR");
        var (offset, original) = FindTextRun(data, attr);
        data[offset] = 0xC3;
        data[offset + 1] = 0xA9;

        var document = Iso8211Reader.Read(data);

        Assert.True(Assert.Single(document.Definitions, d => d.Tag == "ATTR").Utf8);
        var expected = "é" + original[2..];
        Assert.Contains(document.Records.SelectMany(r => r.Fields).Where(f => f.Tag == "ATTR")
            .SelectMany(f => f.Repeats), row => row["ATVL"] as string == expected);
    }

    // C7
    [Fact]
    public void Read_RecordLongerThanLimit_IsRejectedNamingTheLimitAndPosition()
    {
        var data = File.ReadAllBytes(Edition200Cell);
        var probe = Iso8211Probe.Walk(data);
        var limit = probe.Max(r => r.Length) - 1;
        var offender = probe.First(r => r.Length > limit);

        var error = Assert.Throws<Iso8211FormatException>(() =>
            Iso8211Reader.Read(data, new Iso8211Limits { MaxRecordLength = limit }));

        Assert.Equal(nameof(Iso8211Limits.MaxRecordLength), error.Limit);
        Assert.Equal(offender.Start, error.Position);
    }

    // C7
    [Fact]
    public void Read_TooManyFieldsInARecord_IsRejectedNamingTheLimitAndPosition()
    {
        var data = File.ReadAllBytes(Edition200Cell);
        var offender = Iso8211Probe.Walk(data).First(r => r.Fields.Count > 1);

        var error = Assert.Throws<Iso8211FormatException>(() =>
            Iso8211Reader.Read(data, new Iso8211Limits { MaxFieldsPerRecord = 1 }));

        Assert.Equal(nameof(Iso8211Limits.MaxFieldsPerRecord), error.Limit);
        Assert.Equal(offender.Start, error.Position);
    }

    // C7: nested repeating groups are bounded too.
    [Fact]
    public void Read_FormatNestingDeeperThanLimit_IsRejectedNamingTheLimit()
    {
        var data = File.ReadAllBytes(Edition200Cell);
        var ddr = Iso8211Probe.Walk(data)[0];

        var error = Assert.Throws<Iso8211FormatException>(() =>
            Iso8211Reader.Read(data, new Iso8211Limits { MaxFormatNesting = 1 }));

        Assert.Equal(nameof(Iso8211Limits.MaxFormatNesting), error.Limit);
        Assert.InRange(error.Position, ddr.Start, ddr.Start + ddr.Length - 1);
    }

    // C7
    [Fact]
    public void Read_MoreRecordsThanLimit_IsRejectedAtTheFirstRecordOverTheLimit()
    {
        var data = File.ReadAllBytes(Edition200Cell);
        var probe = Iso8211Probe.Walk(data);

        var error = Assert.Throws<Iso8211FormatException>(() =>
            Iso8211Reader.Read(data, new Iso8211Limits { MaxRecords = 3 }));

        Assert.Equal(nameof(Iso8211Limits.MaxRecords), error.Limit);
        Assert.Equal(probe[4].Start, error.Position);
    }

    // C8
    [Fact]
    public void Read_BadLeaderLength_IsRejectedAtPositionZero()
    {
        var data = File.ReadAllBytes(PrimaryCell);
        data[0] = (byte)'X';

        var error = Assert.Throws<Iso8211FormatException>(() => Iso8211Reader.Read(data));

        Assert.Null(error.Limit);
        Assert.Equal(0, error.Position);
    }

    // C8
    [Fact]
    public void Read_DirectoryPointingPastItsRecord_IsRejectedWithinThatRecord()
    {
        var data = File.ReadAllBytes(PrimaryCell);
        var record = Iso8211Probe.Walk(data)[1];
        int sizeLen = data[record.Start + 20] - '0', sizePos = data[record.Start + 21] - '0', sizeTag = data[record.Start + 23] - '0';
        var positionDigits = record.Start + 24 + sizeTag + sizeLen;
        for (var i = 0; i < sizePos; i++)
            data[positionDigits + i] = (byte)'9';

        var error = Assert.Throws<Iso8211FormatException>(() => Iso8211Reader.Read(data));

        Assert.Null(error.Limit);
        Assert.InRange(error.Position, record.Start, record.Start + record.Length - 1);
    }

    // C8: the sub-group's CorruptData cell; its 79th record (at byte 11705) claims 310 bytes but only
    // 295 remain (found by the structural survey recorded in the plan).
    [Fact]
    public void Read_TruncatedFile_IsRejectedAtTheTruncatedRecord()
    {
        var data = File.ReadAllBytes(Fixtures.DatasetFile(Fixtures.SubGroupSet("CorruptData"), "10100AA_X02SE.000"));

        var error = Assert.Throws<Iso8211FormatException>(() => Iso8211Reader.Read(data));

        Assert.Null(error.Limit);
        Assert.Equal(11705, error.Position);
    }

    // C9
    [Fact]
    public void Read_SameStreamTwice_ProducesStructurallyEqualOutput()
    {
        var data = File.ReadAllBytes(Edition200Cell);

        Assert.Equal(Canonical.Of(Iso8211Reader.Read(data)), Canonical.Of(Iso8211Reader.Read(data)));
    }

    // A run of at least four ASCII letters ending at a unit terminator lies inside an ATVL text value.
    private static (int Offset, string Text) FindTextRun(byte[] data, IEnumerable<Iso8211Probe.Field> fields)
    {
        foreach (var field in fields)
        {
            for (var i = field.Start; i < field.Start + field.Length; i++)
            {
                if (data[i] != 0x1f)
                    continue;
                var start = i;
                while (start > field.Start && char.IsAsciiLetter((char)data[start - 1]))
                    start--;
                if (i - start >= 4 && start > field.Start && data[start - 1] is < 0x20 or 0x1f)
                    return (start, Encoding.ASCII.GetString(data, start, i - start));
            }
        }
        throw new InvalidOperationException("No ATVL text run found in the fixture.");
    }
}
