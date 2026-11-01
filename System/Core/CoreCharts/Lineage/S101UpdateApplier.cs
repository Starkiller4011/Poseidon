using Poseidon.Core.Modules;
using Poseidon.Modules.Charts.Iso8211;
using Poseidon.Modules.Charts.S101;

namespace Poseidon.Modules.Charts.Lineage;

// NotUpToDateReason explains the refused update that left the lineage NotUpToDate.
public sealed record DatasetLineage(
    string DatasetName,
    S101Dataset Current,
    int Edition,
    int Update,
    ChartLineageState State,
    string? NotUpToDateReason)
{
    public static DatasetLineage Start(S101Dataset baseDataset)
    {
        if (baseDataset.Identity.ApplicationProfile != 1)
            throw new ArgumentException("A lineage starts from a base dataset (application profile 1).", nameof(baseDataset));
        return new DatasetLineage(Path.GetFileNameWithoutExtension(baseDataset.Identity.FileName), baseDataset,
            baseDataset.Identity.Edition, baseDataset.Identity.Update, ChartLineageState.Current, null);
    }
}

public enum UpdateOutcome
{
    Applied,
    Refused,
    NewEdition,
    Reissued,
    Cancelled
}

// On Refused, Lineage is the unchanged previous content (flagged NotUpToDate where S-164 requires it).
public sealed record UpdateResult(UpdateOutcome Outcome, DatasetLineage Lineage, IReadOnlyList<ChartDiagnostic> Diagnostics);

public interface IS101UpdateApplier
{
    // incoming is a decoded update, re-issue, new edition or cancellation of the lineage's dataset.
    UpdateResult Apply(DatasetLineage lineage, S101Dataset incoming);
}

// Edition/update sequencing per S-101 §11.3.3 and S-164 §2.3; record changes per S-101 Annex B-6 and
// S-100 Part 10a §10a-4.5.
public sealed class S101UpdateApplier : IS101UpdateApplier
{
    public UpdateResult Apply(DatasetLineage lineage, S101Dataset incoming)
    {
        var identity = incoming.Identity;
        var (edition, update, file) = (identity.Edition, identity.Update, identity.FileName);
        var diagnostics = new List<ChartDiagnostic>();
        UpdateResult Refused(string code, string message, bool notUpToDate)
        {
            diagnostics.Add(new ChartDiagnostic(ChartDiagnosticSeverity.Error, code, message, file));
            var kept = notUpToDate ? lineage with { State = ChartLineageState.NotUpToDate, NotUpToDateReason = message } : lineage;
            return new UpdateResult(UpdateOutcome.Refused, kept, diagnostics);
        }

        if (lineage.State == ChartLineageState.Cancelled)
            return Refused(ChartDiagnosticCodes.DatasetCancelled, $"{lineage.DatasetName} is cancelled; {file} is not applied.", false);

        if (identity.ApplicationProfile == 2 && edition == 0)
        {
            diagnostics.Add(new ChartDiagnostic(ChartDiagnosticSeverity.Warning, ChartDiagnosticCodes.DatasetCancelled,
                $"{lineage.DatasetName} is cancelled by {file} and is no longer an active chart.", file));
            return new UpdateResult(UpdateOutcome.Cancelled,
                lineage with { State = ChartLineageState.Cancelled, NotUpToDateReason = null }, diagnostics);
        }

        if (identity.ApplicationProfile == 1)
        {
            if (edition > lineage.Edition)
                return new UpdateResult(UpdateOutcome.NewEdition,
                    new DatasetLineage(lineage.DatasetName, incoming, edition, update, ChartLineageState.Current, null), diagnostics);
            if (edition == lineage.Edition && update >= lineage.Update)
                return new UpdateResult(UpdateOutcome.Reissued,
                    new DatasetLineage(lineage.DatasetName, incoming, edition, update, ChartLineageState.Current, null), diagnostics);
            return edition < lineage.Edition
                ? Refused(ChartDiagnosticCodes.UpdateEditionOlder,
                    $"{file} is edition {edition}; the installed edition is {lineage.Edition}.", false)
                : Refused(ChartDiagnosticCodes.UpdateOutOfSequence,
                    $"Re-issue {file} is at update {update}, older than the installed update {lineage.Update}.", false);
        }

        if (edition > lineage.Edition)
            return Refused(ChartDiagnosticCodes.UpdateEditionNewer,
                $"Update {edition}.{update} ({file}) is for edition {edition}, newer than the installed edition {lineage.Edition}; " +
                "that edition must be installed first.", true);
        if (edition < lineage.Edition)
            return Refused(ChartDiagnosticCodes.UpdateEditionOlder,
                $"Update {edition}.{update} ({file}) is for edition {edition}; expecting edition {lineage.Edition}.", false);
        if (update != lineage.Update + 1)
            return Refused(ChartDiagnosticCodes.UpdateOutOfSequence,
                $"Update {edition}.{update} ({file}) is out of sequence: expected update {lineage.Update + 1} of edition {edition}.",
                update > lineage.Update + 1);

        var applied = new RecordChanges(lineage.Current, incoming, diagnostics).Apply();
        return new UpdateResult(UpdateOutcome.Applied,
            new DatasetLineage(lineage.DatasetName, applied, edition, update, ChartLineageState.Current, null), diagnostics);
    }

    private sealed class RecordChanges
    {
        private static readonly int[] KindOrder = [150, 110, 115, 120, 125, 130, 100];

        private readonly S101Dataset current;
        private readonly S101Dataset incoming;
        private readonly List<ChartDiagnostic> diagnostics;
        private readonly S101Fields.Context context;
        private readonly OrderedRecords<S101Feature> features;
        private readonly OrderedRecords<S101InformationType> information;
        private readonly OrderedRecords<S101SpatialRecord> spatial;

        public RecordChanges(S101Dataset current, S101Dataset incoming, List<ChartDiagnostic> diagnostics)
        {
            this.current = current;
            this.incoming = incoming;
            this.diagnostics = diagnostics;
            var source = incoming.Source ?? new UpdateSource([], new S101CodeTables(
                new Dictionary<long, string>(), new Dictionary<long, string>(), new Dictionary<long, string>(),
                new Dictionary<long, string>(), new Dictionary<long, string>(), new Dictionary<long, string>()), (0, 0, 0));
            context = new S101Fields.Context(source.Codes, incoming.Factors, source.Origin, diagnostics, incoming.Identity.FileName);
            features = new(current.Features, f => f.Name);
            information = new(current.InformationTypes, i => i.Name);
            spatial = new(current.Spatial, s => s.Name);
        }

        public S101Dataset Apply()
        {
            var records = incoming.Source?.Records ?? [];
            var ordered = records.Select((r, i) => (r.Name, r.Record, Index: i, Instruction: S101Fields.Instruction(r.Record.Fields[0].Values, "RUIN")))
                .OrderBy(r => r.Instruction == RecordInstruction.Delete ? 2 : r.Instruction == RecordInstruction.Modify ? 1 : 0)
                .ThenBy(r => r.Instruction == RecordInstruction.Delete ? -Array.IndexOf(KindOrder, r.Name.Kind) : Array.IndexOf(KindOrder, r.Name.Kind))
                .ThenBy(r => r.Index);
            foreach (var (name, record, _, instruction) in ordered)
            {
                switch (instruction)
                {
                    case RecordInstruction.Insert:
                        Insert(name, record);
                        break;
                    case RecordInstruction.Delete:
                        if (!features.Remove(name) && !information.Remove(name) && !spatial.Remove(name))
                            Report($"Delete of record {name.Kind}/{name.Id}, which is not in the dataset.");
                        break;
                    case RecordInstruction.Modify:
                        Modify(name, record);
                        break;
                }
            }
            return current with
            {
                Identity = current.Identity with { Edition = incoming.Identity.Edition, Update = incoming.Identity.Update },
                Features = features.Values,
                InformationTypes = information.Values,
                Spatial = spatial.Values
            };
        }

        private void Report(string message)
            => diagnostics.Add(new ChartDiagnostic(ChartDiagnosticSeverity.Warning, ChartDiagnosticCodes.UnresolvedReference,
                message, incoming.Identity.FileName));

        private void Insert(RecordName name, Iso8211Record record)
        {
            switch (record.Fields[0].Tag)
            {
                case "FRID":
                    features.Set(name, S101Fields.Feature(record, context) with { Instruction = RecordInstruction.Insert });
                    break;
                case "IRID":
                    information.Set(name, S101Fields.Information(record, context) with { Instruction = RecordInstruction.Insert });
                    break;
                default:
                    spatial.Set(name, S101Fields.Spatial(record, context) with { Instruction = RecordInstruction.Insert });
                    break;
            }
        }

        private void Modify(RecordName name, Iso8211Record record)
        {
            var head = record.Fields[0];
            var version = (int)S101Fields.Long(head.Values, "RVER");
            switch (head.Tag)
            {
                case "FRID" when features.TryGet(name, out var feature):
                    features.Set(name, ModifyFeature(feature!, record) with { Version = version });
                    break;
                case "IRID" when information.TryGet(name, out var info):
                    information.Set(name, info! with
                    {
                        Version = version,
                        Attributes = ApplyAttributes(info.Attributes, Rows(record, "ATTR")),
                        InformationAssociations = ApplyAssociations(info.InformationAssociations, record, "INAS")
                    });
                    break;
                case "PRID" or "MRID" or "CRID" or "CCID" or "SRID" when spatial.TryGet(name, out var item):
                    spatial.Set(name, item! with
                    {
                        Version = version,
                        Geometry = ModifyGeometry(item.Geometry, record),
                        InformationAssociations = ApplyAssociations(item.InformationAssociations, record, "INAS")
                    });
                    break;
                default:
                    Report($"Modify of record {name.Kind}/{name.Id}, which is not in the dataset.");
                    break;
            }
        }

        private S101Feature ModifyFeature(S101Feature feature, Iso8211Record record)
        {
            var spatialRefs = feature.Spatial.ToList();
            foreach (var row in Rows(record, "SPAS"))
            {
                var reference = new S101SpatialReference(S101Fields.Target(row), (int)S101Fields.Long(row, "ORNT"));
                var at = spatialRefs.FindIndex(s => s.Target == reference.Target);
                switch (S101Fields.Instruction(row, "SAUI"))
                {
                    case RecordInstruction.Insert:
                        spatialRefs.Add(reference);
                        break;
                    case RecordInstruction.Delete when at >= 0:
                        spatialRefs.RemoveAt(at);
                        break;
                    case RecordInstruction.Modify when at >= 0:
                        spatialRefs[at] = reference;
                        break;
                    default:
                        Report($"Spatial association to {reference.Target.Kind}/{reference.Target.Id} not found on feature {feature.Name.Id}.");
                        break;
                }
            }
            var masks = feature.Masks.ToList();
            foreach (var row in Rows(record, "MASK"))
            {
                var target = S101Fields.Target(row);
                if (S101Fields.Instruction(row, "MUIN") == RecordInstruction.Delete)
                {
                    if (!masks.Remove(target))
                        Report($"Mask of {target.Kind}/{target.Id} not found on feature {feature.Name.Id}.");
                }
                else if (!masks.Contains(target))
                    masks.Add(target);
            }
            return feature with
            {
                Attributes = ApplyAttributes(feature.Attributes, Rows(record, "ATTR")),
                Spatial = spatialRefs,
                Primitive = spatialRefs.Count == 0 ? PrimitiveKind.None : S101Fields.Primitive(spatialRefs[0].Target.Kind),
                FeatureAssociations = ApplyAssociations(feature.FeatureAssociations, record, "FASC"),
                InformationAssociations = ApplyAssociations(feature.InformationAssociations, record, "INAS"),
                Masks = masks
            };
        }

        // IUIN/FAUI: insert adds; delete removes the association with that target; modify updates the one with
        // that target, or re-points the one with the same association code (S-101 Annex B-6.1.11, B-6.1.31).
        private List<S101Association> ApplyAssociations(IReadOnlyList<S101Association> existing, Iso8211Record record, string tag)
        {
            var list = existing.ToList();
            foreach (var field in record.Fields.Where(f => f.Tag == tag))
            {
                var built = tag == "FASC" ? S101Fields.FeatureAssociation(field, context) : S101Fields.InformationAssociation(field, context);
                var instruction = S101Fields.Instruction(field.Values, tag == "FASC" ? "FAUI" : "IUIN");
                var at = list.FindIndex(a => a.Target == built.Target && (instruction == RecordInstruction.Delete || a.Name == built.Name));
                if (at < 0)
                    at = list.FindIndex(a => a.Target == built.Target);
                if (at < 0 && instruction == RecordInstruction.Modify && list.Count(a => a.Name == built.Name) == 1)
                    at = list.FindIndex(a => a.Name == built.Name);
                switch (instruction)
                {
                    case RecordInstruction.Insert:
                        list.Add(built);
                        break;
                    case RecordInstruction.Delete when at >= 0:
                        list.RemoveAt(at);
                        break;
                    case RecordInstruction.Modify when at >= 0:
                        list[at] = built with { Attributes = ApplyAttributes(list[at].Attributes, field.Repeats) };
                        break;
                    default:
                        Report($"{tag} association to {built.Target.Kind}/{built.Target.Id} not found.");
                        break;
                }
            }
            return list;
        }

        // ATIN per row; PAIX is the 1-based pre-order position of the parent among the target's existing
        // attributes, 0 for top level. Codes are matched by name, since an update numbers codes its own way.
        private IReadOnlyList<S101Attribute> ApplyAttributes(IReadOnlyList<S101Attribute> existing, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        {
            if (rows.Count == 0)
                return existing;
            var root = new S101Fields.AttributeNode(null, 0, 0, null);
            root.Children.AddRange(existing.Select(S101Fields.AttributeNode.From));
            var flat = PreOrder(root.Children).ToList();
            var referencedAsParent = rows.Select(r => S101Fields.Long(r, "PAIX")).ToHashSet();
            foreach (var row in rows)
            {
                var numeric = (int)S101Fields.Long(row, "NATC");
                var code = context.Lookup(context.Codes.Attributes, numeric, "attribute");
                var index = (int)S101Fields.Long(row, "ATIX");
                var parentIndex = S101Fields.Long(row, "PAIX");
                var parent = parentIndex >= 1 && parentIndex <= flat.Count ? flat[(int)parentIndex - 1] : root;
                var at = parent.Children.FindIndex(c => (code is not null ? c.Code == code : c.NumericCode == numeric) && c.Index == index);
                switch (S101Fields.Instruction(row, "ATIN"))
                {
                    case RecordInstruction.Insert:
                        var node = new S101Fields.AttributeNode(code, numeric, index, S101Fields.Value(row));
                        var after = parent.Children.FindLastIndex(c => c.Code == code);
                        parent.Children.Insert(after >= 0 ? after + 1 : parent.Children.Count, node);
                        break;
                    case RecordInstruction.Delete when at >= 0:
                        parent.Children.RemoveAt(at);
                        break;
                    case RecordInstruction.Modify when at >= 0:
                        var target = parent.Children[at];
                        var isContext = target.Children.Count > 0 && S101Fields.Value(row) is null;
                        if (!isContext)
                            target.Value = S101Fields.Value(row);
                        break;
                    default:
                        Report($"Attribute {code ?? numeric.ToString()} index {index} not found for update.");
                        break;
                }
            }
            return root.Children.Select(n => n.Build()).ToList();
        }

        private static IEnumerable<S101Fields.AttributeNode> PreOrder(IEnumerable<S101Fields.AttributeNode> nodes)
            => nodes.SelectMany(n => new[] { n }.Concat(PreOrder(n.Children)));

        private S101Geometry ModifyGeometry(S101Geometry geometry, Iso8211Record record)
        {
            switch (geometry)
            {
                case PointGeometry point:
                    return S101Fields.Positions(record, context) is { Count: > 0 } replaced ? new PointGeometry(replaced[0]) : point;
                case MultiPointGeometry multi:
                    return new MultiPointGeometry(EditList(multi.Positions, record, "COCC", "COUI", "COIX", "NCOR",
                        field => S101Fields.Coordinates(field, context).ToList(), "C2IL", "C3IL"));
                case CurveGeometry curve:
                    return ModifyCurve(curve, record);
                case CompositeCurveGeometry composite:
                    return new CompositeCurveGeometry(EditList(composite.Components, record, "CCOC", "CCUI", "CCIX", "NCCO",
                        field => field.Repeats.Select(r => new S101SpatialReference(S101Fields.Target(r), (int)S101Fields.Long(r, "ORNT"))).ToList(),
                        "CUCO"));
                case SurfaceGeometry surface:
                    var rings = surface.Rings.ToList();
                    foreach (var row in Rows(record, "RIAS"))
                    {
                        var ring = S101Fields.Ring(row);
                        var at = rings.FindIndex(r => r.Curve == ring.Curve);
                        switch (S101Fields.Instruction(row, "RAUI"))
                        {
                            case RecordInstruction.Insert:
                                rings.Add(ring);
                                break;
                            case RecordInstruction.Delete when at >= 0:
                                rings.RemoveAt(at);
                                break;
                            case RecordInstruction.Modify when at >= 0:
                                rings[at] = ring;
                                break;
                            default:
                                Report($"Ring {ring.Curve.Kind}/{ring.Curve.Id} not found for update.");
                                break;
                        }
                    }
                    return new SurfaceGeometry(rings);
                default:
                    return geometry;
            }
        }

        // Every curve in the corpora has one segment; a segment control addressing any other is refused.
        private CurveGeometry ModifyCurve(CurveGeometry curve, Iso8211Record record)
        {
            var (start, end) = (curve.Start, curve.End);
            if (record.Fields.Any(f => f.Tag == "PTAS"))
            {
                var replaced = S101Fields.Curve(record, context);
                (start, end) = (replaced.Start, replaced.End);
            }
            var vertices = curve.Vertices.ToList();
            var segment = record.Fields.FirstOrDefault(f => f.Tag == "SECC");
            if (segment is not null && S101Fields.Long(segment.Values, "SEIX") != 1)
            {
                Report($"Segment control for segment {S101Fields.Long(segment.Values, "SEIX")}; only single-segment curves are supported.");
                return curve with { Start = start, End = end };
            }
            if (segment is not null && S101Fields.Instruction(segment.Values, "SEUI") == RecordInstruction.Delete)
                vertices.Clear();
            else if (record.Fields.Any(f => f.Tag == "COCC"))
                vertices = EditList(vertices.Select(v => new SpatialPosition(v, null)).ToList(), record, "COCC", "COUI", "COIX", "NCOR",
                    field => S101Fields.Coordinates(field, context).ToList(), "C2IL", "C3IL").Select(p => p.Horizontal).ToList();
            else if (record.Fields.Any(f => f.Tag is "C2IL" or "C3IL"))
                vertices = S101Fields.Curve(record, context).Vertices.ToList();
            return new CurveGeometry(start, end, vertices);
        }

        // A control field (COCC/CCOC) addresses NCOR items at the 1-based index; the following data field carries
        // the inserted or replacement items. Data without any control field replaces the whole list.
        private static List<T> EditList<T>(IReadOnlyList<T> items, Iso8211Record record, string controlTag, string instructionLabel,
            string indexLabel, string countLabel, Func<Iso8211Field, List<T>> read, params string[] dataTags)
        {
            var list = items.ToList();
            var fields = record.Fields;
            var sawControl = false;
            for (var i = 0; i < fields.Count; i++)
            {
                if (fields[i].Tag != controlTag)
                    continue;
                sawControl = true;
                var control = fields[i].Values;
                var index = (int)S101Fields.Long(control, indexLabel) - 1;
                var count = (int)S101Fields.Long(control, countLabel);
                var data = i + 1 < fields.Count && dataTags.Contains(fields[i + 1].Tag) ? read(fields[i + 1]) : [];
                index = Math.Clamp(index, 0, list.Count);
                switch (S101Fields.Instruction(control, instructionLabel))
                {
                    case RecordInstruction.Insert:
                        list.InsertRange(index, data);
                        break;
                    case RecordInstruction.Delete:
                        list.RemoveRange(index, Math.Min(count, list.Count - index));
                        break;
                    case RecordInstruction.Modify:
                        for (var n = 0; n < data.Count && index + n < list.Count; n++)
                            list[index + n] = data[n];
                        break;
                }
            }
            if (!sawControl && fields.Any(f => dataTags.Contains(f.Tag)))
                return fields.Where(f => dataTags.Contains(f.Tag)).SelectMany(read).ToList();
            return list;
        }

        private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(Iso8211Record record, string tag)
            => S101Fields.Rows(record, tag).ToList();
    }

    // Keeps the dataset's record order: replacements stay in place, insertions go to the end.
    private sealed class OrderedRecords<T>(IEnumerable<T> items, Func<T, RecordName> name) where T : class
    {
        private readonly List<T?> items = items.Cast<T?>().ToList();
        private readonly Dictionary<RecordName, int> index = items.Select((item, i) => (name(item), i))
            .GroupBy(p => p.Item1).ToDictionary(g => g.Key, g => g.Last().i);

        public IReadOnlyList<T> Values => items.OfType<T>().ToList();

        public bool TryGet(RecordName key, out T? value)
        {
            value = index.TryGetValue(key, out var at) ? items[at] : null;
            return value is not null;
        }

        public void Set(RecordName key, T value)
        {
            if (index.TryGetValue(key, out var at))
                items[at] = value;
            else
            {
                index[key] = items.Count;
                items.Add(value);
            }
        }

        public bool Remove(RecordName key)
        {
            if (!index.Remove(key, out var at))
                return false;
            items[at] = null;
            return true;
        }
    }
}
