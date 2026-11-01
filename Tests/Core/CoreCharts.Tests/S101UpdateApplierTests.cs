using Poseidon.Core.Modules;
using Poseidon.Modules.Charts;
using Poseidon.Modules.Charts.Lineage;
using Poseidon.Modules.Charts.S101;
using Xunit;

namespace CoreCharts.Tests;

public sealed class S101UpdateApplierTests
{
    private static readonly S101UpdateApplier Applier = new();

    private static S101Dataset Released(string section, string set, string file)
        => Decoding.Dataset(Fixtures.DatasetFile(Fixtures.Released(section, set), file));

    private static S101Dataset Updates(string set, string file) => Released("2.3_AutomaticUpdates", set, file);

    private static S101Dataset Edition3Base => Released("2.2_DatasetLoading", "PowerUp", "10100AA_X01SW.000");

    private static S101Dataset Old(string scenario, string file) => Decoding.Dataset(Fixtures.DatasetFile(Fixtures.Old164(scenario), file));

    private static DatasetLineage Apply(DatasetLineage lineage, S101Dataset incoming, UpdateOutcome expected)
    {
        var result = Applier.Apply(lineage, incoming);
        Assert.True(result.Outcome == expected,
            $"Expected {expected} for {incoming.Identity.FileName} but got {result.Outcome}: {Libraries.Describe(result.Diagnostics)}");
        return result.Lineage;
    }

    // C16
    [Fact]
    public void Apply_SequentialUpdates_AdvanceTheLineageToEdition3Update5()
    {
        var lineage = DatasetLineage.Start(Edition3Base);
        Assert.Equal((3, 0, ChartLineageState.Current), (lineage.Edition, lineage.Update, lineage.State));
        var baseContent = Canonical.Of(lineage.Current.Features);

        for (var n = 1; n <= 5; n++)
        {
            lineage = Apply(lineage, Updates($"SequentialUpdate{n}", $"10100AA_X01SW.00{n}"), UpdateOutcome.Applied);
            Assert.Equal((3, n, ChartLineageState.Current), (lineage.Edition, lineage.Update, lineage.State));
        }
        Assert.NotEqual(baseContent, Canonical.Of(lineage.Current.Features));
        Assert.Equal("10100AA_X01SW", lineage.DatasetName);
    }

    // C16: real producer data, base and updates delivered in one exchange set.
    [LocalDataFact]
    public void Apply_ShomEdition1Updates_AdvanceToUpdate2()
    {
        var root = Fixtures.LocalShomSet("FR00369400_ed1");
        var lineage = DatasetLineage.Start(Decoding.Dataset(Fixtures.DatasetFile(root, "101FR00369400.000")));

        lineage = Apply(lineage, Decoding.Dataset(Fixtures.DatasetFile(root, "101FR00369400.001")), UpdateOutcome.Applied);
        lineage = Apply(lineage, Decoding.Dataset(Fixtures.DatasetFile(root, "101FR00369400.002")), UpdateOutcome.Applied);

        Assert.Equal((1, 2, ChartLineageState.Current), (lineage.Edition, lineage.Update, lineage.State));
    }

    // C16
    [LocalDataFact]
    public void Apply_UkhoUpdate_AdvancesToUpdate1()
    {
        var lineage = DatasetLineage.Start(Decoding.Dataset(Fixtures.DatasetFile(Fixtures.LocalUkhoGb, "101GB005DEVRC.000")));

        lineage = Apply(lineage, Decoding.Dataset(Fixtures.DatasetFile(Fixtures.LocalUkhoGb, "101GB005DEVRC.001")), UpdateOutcome.Applied);

        Assert.Equal((1, 1), (lineage.Edition, lineage.Update));
    }

    // C17: a gap in the sequence is refused; the lineage stays at its last valid state, not up to date.
    [Fact]
    public void Apply_UpdateAfterAGap_IsRefusedAndLeavesTheLastValidState()
    {
        var lineage = DatasetLineage.Start(Edition3Base);
        lineage = Apply(lineage, Updates("InvalidSequence001", "10100AA_X01SW.001"), UpdateOutcome.Applied);
        lineage = Apply(lineage, Updates("InvalidSequence002", "10100AA_X01SW.002"), UpdateOutcome.Applied);
        var lastValid = Canonical.Of(lineage.Current);

        var result = Applier.Apply(lineage, Updates("InvalidSequence003", "10100AA_X01SW.004"));

        Assert.Equal(UpdateOutcome.Refused, result.Outcome);
        Assert.Equal((3, 2, ChartLineageState.NotUpToDate), (result.Lineage.Edition, result.Lineage.Update, result.Lineage.State));
        Assert.Equal(lastValid, Canonical.Of(result.Lineage.Current));
        Assert.NotNull(result.Lineage.NotUpToDateReason);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.UpdateOutOfSequence);
        Assert.Contains("3", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("4", diagnostic.Message, StringComparison.Ordinal);
    }

    // C17: an update for a newer edition than the one installed is refused (S-164 2.3.4).
    [Fact]
    public void Apply_UpdateForANewerEdition_IsRefusedAndFlagsNotUpToDate()
    {
        var lineage = DatasetLineage.Start(Edition3Base);
        var before = Canonical.Of(lineage.Current);

        var result = Applier.Apply(lineage, Updates("NewUpdate", "10100AA_X01SW.001"));

        Assert.Equal(UpdateOutcome.Refused, result.Outcome);
        Assert.Equal((3, 0, ChartLineageState.NotUpToDate), (result.Lineage.Edition, result.Lineage.Update, result.Lineage.State));
        Assert.Equal(before, Canonical.Of(result.Lineage.Current));
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.UpdateEditionNewer);
        Assert.Contains("4", diagnostic.Message, StringComparison.Ordinal);
    }

    // C17: an update for an older edition is refused, naming the edition expected (S-164 2.3.5). The
    // installed edition is complete, so this does not flag the cell not up to date (see the plan's C17 note).
    [Fact]
    public void Apply_UpdateForAnOlderEdition_IsRefusedNamingTheExpectedEdition()
    {
        var lineage = DatasetLineage.Start(Edition3Base);
        lineage = Apply(lineage, Updates("GoodBaseCells", "10100AA_X01SW.000"), UpdateOutcome.NewEdition);
        var before = Canonical.Of(lineage.Current);

        var result = Applier.Apply(lineage, Updates("OldUpdate", "10100AA_X01SW.001"));

        Assert.Equal(UpdateOutcome.Refused, result.Outcome);
        Assert.Equal((4, 0, ChartLineageState.Current), (result.Lineage.Edition, result.Lineage.Update, result.Lineage.State));
        Assert.Equal(before, Canonical.Of(result.Lineage.Current));
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.UpdateEditionOlder);
        Assert.Contains("3", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("4", diagnostic.Message, StringComparison.Ordinal);
    }

    // C17: real producer data — an edition 1 update after edition 2 is installed.
    [LocalDataFact]
    public void Apply_ShomEdition1UpdateAfterEdition2_IsRefused()
    {
        var edition1 = Fixtures.LocalShomSet("FR00369400_ed1");
        var lineage = DatasetLineage.Start(Decoding.Dataset(Fixtures.DatasetFile(edition1, "101FR00369400.000")));
        lineage = Apply(lineage, Decoding.Dataset(Fixtures.DatasetFile(Fixtures.LocalShomSet("FR00369400_Ed2"), "101FR00369400.000")), UpdateOutcome.NewEdition);

        var result = Applier.Apply(lineage, Decoding.Dataset(Fixtures.DatasetFile(edition1, "101FR00369400.001")));

        Assert.Equal(UpdateOutcome.Refused, result.Outcome);
        Assert.Contains(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.UpdateEditionOlder);
        Assert.Equal((2, 0), (result.Lineage.Edition, result.Lineage.Update));
    }

    // C19: a new edition replaces the content, resets the update number and clears not-up-to-date.
    [Fact]
    public void Apply_NewEdition_ReplacesContentResetsUpdateAndClearsNotUpToDate()
    {
        var lineage = DatasetLineage.Start(Edition3Base);
        lineage = Apply(lineage, Updates("NewUpdate", "10100AA_X01SW.001"), UpdateOutcome.Refused);
        Assert.Equal(ChartLineageState.NotUpToDate, lineage.State);
        var edition4 = Updates("GoodBaseCells", "10100AA_X01SW.000");

        lineage = Apply(lineage, edition4, UpdateOutcome.NewEdition);

        Assert.Equal((4, 0, ChartLineageState.Current), (lineage.Edition, lineage.Update, lineage.State));
        Assert.Null(lineage.NotUpToDateReason);
        Assert.Equal(Canonical.Of(edition4.Features), Canonical.Of(lineage.Current.Features));
        lineage = Apply(lineage, Updates("NewUpdate", "10100AA_X01SW.001"), UpdateOutcome.Applied);
        Assert.Equal((4, 1), (lineage.Edition, lineage.Update));
    }

    // C19: a re-issue replaces the content at the same edition and update, distinct from a new edition.
    [Fact]
    public void Apply_Reissue_ReplacesContentKeepingEditionAndTakesItsUpdateNumber()
    {
        var lineage = DatasetLineage.Start(Old("2.1.1 Power Up", "10100AA_X01SW.000"));
        lineage = Apply(lineage, Old("2.2.2 Loading of Updates", "10100AA_X01SW.001"), UpdateOutcome.Applied);
        var reissue = Old("2.2.6 Re-issue", "10100AA_X01SW.000");

        lineage = Apply(lineage, reissue, UpdateOutcome.Reissued);

        Assert.Equal((1, 3, ChartLineageState.Current), (lineage.Edition, lineage.Update, lineage.State));
        Assert.Equal(Canonical.Of(reissue.Features), Canonical.Of(lineage.Current.Features));
        lineage = Apply(lineage, Old("2.2.2 Loading of Updates", "10100AA_X01SW.004"), UpdateOutcome.Applied);
        Assert.Equal((1, 4), (lineage.Edition, lineage.Update));
    }

    // C19: real producer data — SHOM's edition 2 after edition 1 update 2.
    [LocalDataFact]
    public void Apply_ShomEdition2_IsANewEdition()
    {
        var edition1 = Fixtures.LocalShomSet("FR00369400_ed1");
        var lineage = DatasetLineage.Start(Decoding.Dataset(Fixtures.DatasetFile(edition1, "101FR00369400.000")));
        lineage = Apply(lineage, Decoding.Dataset(Fixtures.DatasetFile(edition1, "101FR00369400.001")), UpdateOutcome.Applied);
        lineage = Apply(lineage, Decoding.Dataset(Fixtures.DatasetFile(edition1, "101FR00369400.002")), UpdateOutcome.Applied);

        lineage = Apply(lineage, Decoding.Dataset(Fixtures.DatasetFile(Fixtures.LocalShomSet("FR00369400_Ed2"), "101FR00369400.000")), UpdateOutcome.NewEdition);
        lineage = Apply(lineage, Decoding.Dataset(Fixtures.DatasetFile(Fixtures.LocalShomSet("FR00369400_ER1_ed2"), "101FR00369400.001")), UpdateOutcome.Applied);

        Assert.Equal((2, 1), (lineage.Edition, lineage.Update));
    }

    // C20: a cancellation file (DSED "0", S-101 Annex B-7) cancels the lineage.
    [Fact]
    public void Apply_Cancellation_MarksTheLineageCancelled()
    {
        var lineage = DatasetLineage.Start(Old("2.1.1 Power Up", "10100AA_X0000.000"));

        var result = Applier.Apply(lineage, Old("2.2.7 Cancellation", "10100AA_X0000.001"));

        Assert.Equal(UpdateOutcome.Cancelled, result.Outcome);
        Assert.Equal(ChartLineageState.Cancelled, result.Lineage.State);
        Assert.Contains(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.DatasetCancelled);
    }

    // C16 and C19 (Q9): every update mechanism in S-101 Annex B-6, one s100tds case each.
    [Theory]
    [MemberData(nameof(UpdateCases))]
    public void Apply_UpdatePermutationCase_ChangesExactlyTheRecordsTheUpdateAddresses(string caseId)
    {
        var folder = Path.Combine(Fixtures.S100Tds, "upd", "2.1.0", "cases", caseId);
        var before = Decoding.Dataset(Directory.EnumerateFiles(folder, "*.000").Single(), Decoding.S100TdsOverride);
        var update = Decoding.Dataset(Directory.EnumerateFiles(folder, "*.001").Single(), Decoding.S100TdsOverride);

        var result = Applier.Apply(DatasetLineage.Start(before), update);

        Assert.True(result.Outcome == UpdateOutcome.Applied, $"{caseId}: {result.Outcome} {Libraries.Describe(result.Diagnostics)}");
        Assert.Equal(1, result.Lineage.Update);
        var after = result.Lineage.Current;
        var touched = Records(update).GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.ToList());
        var beforeRecords = Records(before).ToDictionary(r => r.Name);
        var afterRecords = Records(after).ToDictionary(r => r.Name);
        foreach (var (name, instructions) in touched)
        {
            var last = instructions[^1];
            switch (last.Instruction)
            {
                case RecordInstruction.Insert:
                    Assert.True(afterRecords.ContainsKey(name), $"{caseId}: inserted {name} missing");
                    break;
                case RecordInstruction.Delete:
                    Assert.False(afterRecords.ContainsKey(name), $"{caseId}: deleted {name} still present");
                    break;
                case RecordInstruction.Modify:
                    Assert.True(afterRecords.TryGetValue(name, out var modified), $"{caseId}: modified {name} missing");
                    Assert.Equal(instructions.Max(i => i.Version), modified!.Version);
                    break;
            }
        }
        foreach (var (name, record) in beforeRecords.Where(r => !touched.ContainsKey(r.Key)))
            Assert.Equal(Canonical.Of(record.Value), Canonical.Of(afterRecords[name].Value));
        Targeted(caseId, before, update, after);
    }

    public static TheoryData<string> UpdateCases()
    {
        var data = new TheoryData<string>();
        foreach (var folder in Directory.EnumerateDirectories(Path.Combine(Fixtures.S100Tds, "upd", "2.1.0", "cases")).Order(StringComparer.Ordinal))
            if (Directory.EnumerateFiles(folder, "*.000").Any() && Directory.EnumerateFiles(folder, "*.001").Any())
                data.Add(Path.GetFileName(folder));
        return data;
    }

    // DS-NEWEDITION carries only a base cell, one edition above the s100tds update base (its title says 1 to 2,
    // but the bases it builds on are already edition 2, so the bytes say 3).
    [Fact]
    public void Decode_S100TdsNewEditionCase_IsTheNextEditionAsABase()
    {
        var updateBase = Decoding.Dataset(Path.Combine(Fixtures.S100Tds, "upd", "2.1.0", "base", "UPDBASE.000"), Decoding.S100TdsOverride);
        var dataset = Decoding.Dataset(Directory.EnumerateFiles(Path.Combine(Fixtures.S100Tds, "upd", "2.1.0", "cases", "DS-NEWEDITION"), "*.000").Single(),
            Decoding.S100TdsOverride);

        Assert.Equal((1, updateBase.Identity.Edition + 1, 0), (dataset.Identity.ApplicationProfile, dataset.Identity.Edition, dataset.Identity.Update));
    }

    private sealed record AnyRecord(RecordName Name, int Version, RecordInstruction Instruction, object Value);

    private static IEnumerable<AnyRecord> Records(S101Dataset d)
        => d.Features.Select(f => new AnyRecord(f.Name, f.Version, f.Instruction, f))
            .Concat(d.InformationTypes.Select(i => new AnyRecord(i.Name, i.Version, i.Instruction, i)))
            .Concat(d.Spatial.Select(s => new AnyRecord(s.Name, s.Version, s.Instruction, s)));

    private static S101Feature Of(S101Dataset d, string type) => Assert.Single(d.Features, f => f.TypeCode == type);

    private static S101Attribute? Top(S101Feature f, string code) => f.Attributes.FirstOrDefault(a => a.Code == code);

    // The effect each documented case must have, from docs/case-index.md (F1 is the LightAllAround,
    // F3 the DepthArea, F4 the LandArea and F5 the UnsurveyedArea).
    private static void Targeted(string caseId, S101Dataset before, S101Dataset update, S101Dataset after)
    {
        S101SpatialRecord Modified() => Assert.Single(after.Spatial, s => update.Spatial.Any(u => u.Name == s.Name && u.Instruction == RecordInstruction.Modify));
        S101SpatialRecord Original(RecordName name) => Assert.Single(before.Spatial, s => s.Name == name);
        switch (caseId)
        {
            case "PT-MOD-POS":
            {
                var point = Modified();
                Assert.Equal(Canonical.Of(update.Spatial.Single(u => u.Name == point.Name).Geometry), Canonical.Of(point.Geometry));
                break;
            }
            case "CV-MOD-SEGCOORD-ADD" or "CV-MOD-SEGCOORD-DEL" or "CV-MOD-SEGCOORD-MOD":
            {
                var curve = Modified();
                var delta = caseId.EndsWith("ADD") ? 1 : caseId.EndsWith("DEL") ? -1 : 0;
                Assert.Equal(((CurveGeometry)Original(curve.Name).Geometry).Vertices.Count + delta, ((CurveGeometry)curve.Geometry).Vertices.Count);
                if (delta == 0)
                    Assert.NotEqual(Canonical.Of(Original(curve.Name).Geometry), Canonical.Of(curve.Geometry));
                break;
            }
            case "MP-MOD-VTX-ADD" or "MP-MOD-VTX-DEL" or "MP-MOD-VTX-MOD":
            {
                var multi = Modified();
                var delta = caseId.EndsWith("ADD") ? 1 : caseId.EndsWith("DEL") ? -1 : 0;
                Assert.Equal(((MultiPointGeometry)Original(multi.Name).Geometry).Positions.Count + delta, ((MultiPointGeometry)multi.Geometry).Positions.Count);
                if (delta == 0)
                    Assert.NotEqual(Canonical.Of(Original(multi.Name).Geometry), Canonical.Of(multi.Geometry));
                break;
            }
            case "CC-MOD-COMP-ADD" or "CC-MOD-COMP-DEL":
            {
                var composite = Modified();
                Assert.Equal(((CompositeCurveGeometry)Original(composite.Name).Geometry).Components.Count + (caseId.EndsWith("ADD") ? 1 : -1),
                    ((CompositeCurveGeometry)composite.Geometry).Components.Count);
                break;
            }
            case "CC-MOD-COMP-MOD":
            {
                var composite = Modified();
                var was = ((CompositeCurveGeometry)Original(composite.Name).Geometry).Components;
                var now = ((CompositeCurveGeometry)composite.Geometry).Components;
                Assert.Equal(was.Select(c => c.Target), now.Select(c => c.Target));
                Assert.All(was.Zip(now), p => Assert.NotEqual(p.First.Orientation, p.Second.Orientation));
                break;
            }
            case "SF-MOD-RING-ADD" or "SF-MOD-RING-DEL":
            {
                var surface = Modified();
                Assert.Equal(((SurfaceGeometry)Original(surface.Name).Geometry).Rings.Count + (caseId.EndsWith("ADD") ? 1 : -1),
                    ((SurfaceGeometry)surface.Geometry).Rings.Count);
                break;
            }
            case "FT-MOD-ATTR-ADD":
                Assert.Null(Top(Of(before, "UnsurveyedArea"), "interoperabilityIdentifier"));
                Assert.NotNull(Top(Of(after, "UnsurveyedArea"), "interoperabilityIdentifier"));
                break;
            case "FT-MOD-ATTR-DEL":
                Assert.NotNull(Top(Of(before, "DepthArea"), "depthRangeMinimumValue"));
                Assert.Null(Top(Of(after, "DepthArea"), "depthRangeMinimumValue"));
                break;
            case "FT-MOD-ATTR-MOD":
                Assert.NotEqual(Top(Of(before, "DepthArea"), "depthRangeMaximumValue")!.Value, Top(Of(after, "DepthArea"), "depthRangeMaximumValue")!.Value);
                break;
            case "FT-MOD-ATTRCHILD-ADD":
                Assert.DoesNotContain(Decoding.Flatten(Of(before, "LandArea").Attributes), a => a.Code == "nameUsage");
                Assert.Contains(Top(Of(after, "LandArea"), "featureName")!.Children, a => a.Code == "nameUsage");
                break;
            case "FT-MOD-SPAS-ADD":
                Assert.Equal(Of(before, "LandArea").Spatial.Count + 1, Of(after, "LandArea").Spatial.Count);
                break;
            case "FT-MOD-SPAS-DEL":
                Assert.Equal(Of(before, "LightAllAround").Spatial.Count - 1, Of(after, "LightAllAround").Spatial.Count);
                break;
            case "FT-MOD-FASC-ADD":
                Assert.Equal(Of(before, "LightAllAround").FeatureAssociations.Count + 1, Of(after, "LightAllAround").FeatureAssociations.Count);
                break;
            case "FT-MOD-FASC-DEL":
                Assert.Equal(Of(before, "DepthArea").FeatureAssociations.Count - 1, Of(after, "DepthArea").FeatureAssociations.Count);
                break;
            case "FT-MOD-INAS-ADD":
                Assert.Equal(Of(before, "DepthArea").InformationAssociations.Count + 1, Of(after, "DepthArea").InformationAssociations.Count);
                break;
            case "FT-MOD-INAS-DEL":
                Assert.Equal(Of(before, "LandArea").InformationAssociations.Count - 1, Of(after, "LandArea").InformationAssociations.Count);
                break;
            case "FT-MOD-MASK-ADD":
                Assert.Equal(Of(before, "LandArea").Masks.Count + 1, Of(after, "LandArea").Masks.Count);
                break;
            case "FT-MOD-MASK-DEL":
                Assert.Equal(Of(before, "DepthArea").Masks.Count - 1, Of(after, "DepthArea").Masks.Count);
                break;
        }
    }
}
