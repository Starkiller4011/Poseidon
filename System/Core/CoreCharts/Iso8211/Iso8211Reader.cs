using System.Buffers.Binary;
using System.Text;

namespace Poseidon.Modules.Charts.Iso8211;

// Explicit bounds so malformed or hostile input is rejected rather than exhausting memory or the stack.
public sealed record Iso8211Limits
{
    public static Iso8211Limits Default { get; } = new();

    public int MaxRecordLength { get; init; } = 16 * 1024 * 1024;
    public int MaxFieldsPerRecord { get; init; } = 100_000;
    // Parenthesis depth of a field's format controls, e.g. "(b11,(3b12,A))" is depth 2.
    public int MaxFormatNesting { get; init; } = 4;
    public int MaxRecords { get; init; } = 10_000_000;
}

// Limit names the bound that was exceeded (an Iso8211Limits property name), or null for malformed input.
public sealed class Iso8211FormatException(string message, long position, string? limit = null) : Exception(message)
{
    public long Position { get; } = position;
    public string? Limit { get; } = limit;
}

public sealed record Iso8211FieldDefinition(
    string Tag,
    string Name,
    IReadOnlyList<string> Labels,
    string FormatControls,
    bool Repeating,
    bool Utf8);

// Values holds the field's non-repeating subfields; Repeats holds one row per repetition of the
// repeating group (all subfields for a fully repeating field). Values are long, double, string or byte[].
public sealed record Iso8211Field(
    string Tag,
    IReadOnlyDictionary<string, object?> Values,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Repeats,
    byte[] Raw);

public sealed record Iso8211Record(long Position, IReadOnlyList<Iso8211Field> Fields);

public sealed record Iso8211Document(
    IReadOnlyList<Iso8211FieldDefinition> Definitions,
    IReadOnlyList<Iso8211Record> Records);

public static class Iso8211Reader
{
    private const byte UnitTerminator = 0x1f;
    private const byte FieldTerminator = 0x1e;

    public static Iso8211Document Read(ReadOnlySpan<byte> data, Iso8211Limits? limits = null)
        => Read(data, limits, null, null);

    // fallback supplies (labels, format controls) for tags a file uses without defining them in its DDR;
    // undefinedTag is told once per such tag. Without a fallback such fields keep only their raw bytes.
    internal static Iso8211Document Read(ReadOnlySpan<byte> data, Iso8211Limits? limits,
        IReadOnlyDictionary<string, (string Labels, string Formats)>? fallback, Action<string>? undefinedTag)
    {
        limits ??= Iso8211Limits.Default;
        var pos = 0;
        var ddr = ReadRecord(data, ref pos, limits);
        var layouts = new Dictionary<string, Layout>(StringComparer.Ordinal);
        var definitions = new List<Iso8211FieldDefinition>();
        foreach (var (tag, start, length) in ddr.Entries)
        {
            if (tag.StartsWith("000", StringComparison.Ordinal))
                continue;
            var layout = Layout.Parse(data.Slice(start, length), ddr.FieldControlLength, start, limits);
            layouts[tag] = layout;
            definitions.Add(new Iso8211FieldDefinition(tag, layout.Name, layout.FixedLabels.Concat(layout.RepeatingLabels).ToList(),
                layout.FormatControls, layout.RepeatingLabels.Count > 0, layout.Encoding == TextEncoding.Utf8));
        }

        var records = new List<Iso8211Record>();
        while (pos < data.Length)
        {
            var recordStart = pos;
            if (records.Count >= limits.MaxRecords)
                throw new Iso8211FormatException(
                    $"More than {limits.MaxRecords} data records.", recordStart, nameof(Iso8211Limits.MaxRecords));
            var record = ReadRecord(data, ref pos, limits);
            var fields = new List<Iso8211Field>(record.Entries.Count);
            foreach (var (tag, start, length) in record.Entries)
            {
                var raw = data.Slice(start, length);
                if (!layouts.TryGetValue(tag, out var layout) && fallback is not null && fallback.TryGetValue(tag, out var known))
                {
                    layouts[tag] = layout = Layout.Create(known.Labels, known.Formats, limits);
                    undefinedTag?.Invoke(tag);
                }
                fields.Add(layout is not null
                    ? layout.Decode(tag, raw, start)
                    : new Iso8211Field(tag, Empty, [], raw.ToArray()));
            }
            records.Add(new Iso8211Record(recordStart, fields));
        }
        return new Iso8211Document(definitions, records);
    }

    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>();

    private sealed record RawRecord(int FieldControlLength, List<(string Tag, int Start, int Length)> Entries);

    private static RawRecord ReadRecord(ReadOnlySpan<byte> data, ref int pos, Iso8211Limits limits)
    {
        var start = pos;
        if (data.Length - start < 24)
            throw new Iso8211FormatException($"Truncated leader: {data.Length - start} bytes left.", start);
        var leader = data.Slice(start, 24);
        var length = Digits(leader, 0, 5, start, "record length");
        if (length > limits.MaxRecordLength)
            throw new Iso8211FormatException(
                $"Record of {length} bytes exceeds the {limits.MaxRecordLength}-byte limit.", start, nameof(Iso8211Limits.MaxRecordLength));
        if (length < 25 || start + length > data.Length)
            throw new Iso8211FormatException(
                $"Record claims {length} bytes but {data.Length - start} remain.", start);
        var fieldControlLength = leader[10] == (byte)' ' ? 0 : Digits(leader, 10, 2, start, "field control length");
        var baseAddress = Digits(leader, 12, 5, start, "field area address");
        var sizeLength = Digits(leader, 20, 1, start, "size of field length");
        var sizePosition = Digits(leader, 21, 1, start, "size of field position");
        var sizeTag = Digits(leader, 23, 1, start, "size of field tag");
        var entrySize = sizeTag + sizeLength + sizePosition;
        if (baseAddress <= 24 || baseAddress > length || entrySize == 0 || sizeLength == 0 || sizePosition == 0 || sizeTag == 0)
            throw new Iso8211FormatException("Inconsistent leader.", start);
        if (data[start + baseAddress - 1] != FieldTerminator)
            throw new Iso8211FormatException("Directory is not terminated before the field area.", start + baseAddress - 1);

        var entries = new List<(string, int, int)>();
        for (var d = start + 24; d < start + baseAddress - 1; d += entrySize)
        {
            if (entries.Count >= limits.MaxFieldsPerRecord)
                throw new Iso8211FormatException(
                    $"More than {limits.MaxFieldsPerRecord} fields in a record.", start, nameof(Iso8211Limits.MaxFieldsPerRecord));
            if (d + entrySize > start + baseAddress - 1)
                throw new Iso8211FormatException("Directory entry overruns the directory.", d);
            var tag = Encoding.ASCII.GetString(data.Slice(d, sizeTag));
            var fieldLength = Digits(data, d + sizeTag, sizeLength, d, "field length");
            var fieldPosition = Digits(data, d + sizeTag + sizeLength, sizePosition, d, "field position");
            if (fieldLength < 1 || baseAddress + (long)fieldPosition + fieldLength > length)
                throw new Iso8211FormatException($"Field {tag} lies outside its record.", d);
            entries.Add((tag, start + baseAddress + fieldPosition, fieldLength));
        }
        pos = start + length;
        return new RawRecord(fieldControlLength, entries);
    }

    private static int Digits(ReadOnlySpan<byte> data, int offset, int count, long position, string what)
    {
        var value = 0;
        for (var i = 0; i < count; i++)
        {
            var c = data[offset + i];
            if (c is < (byte)'0' or > (byte)'9')
                throw new Iso8211FormatException($"Non-numeric {what}.", position);
            value = value * 10 + (c - '0');
        }
        return value;
    }

    private enum TextEncoding { Latin1, Utf8, Ucs2 }

    private enum AtomKind { Text, Integer, Unsigned, Float, Bits }

    // Width 0 means variable length, ended by a unit or field terminator.
    private readonly record struct Atom(AtomKind Kind, int Width);

    private sealed class Layout
    {
        public required string Name { get; init; }
        public required string FormatControls { get; init; }
        public required List<string> FixedLabels { get; init; }
        public required List<string> RepeatingLabels { get; init; }
        public required List<Atom> FixedAtoms { get; init; }
        public required List<Atom> RepeatingAtoms { get; init; }
        public required TextEncoding Encoding { get; init; }

        public static Layout Create(string labels, string formats, Iso8211Limits limits)
        {
            const string controls = "1600;&%/G";
            var description = System.Text.Encoding.Latin1.GetBytes(
                controls + "fallback" + (char)UnitTerminator + labels + (char)UnitTerminator + formats + (char)FieldTerminator);
            return Parse(description, controls.Length, 0, limits);
        }

        public static Layout Parse(ReadOnlySpan<byte> description, int controlLength, long position, Iso8211Limits limits)
        {
            var body = description;
            if (body.Length > 0 && body[^1] == FieldTerminator)
                body = body[..^1];
            controlLength = Math.Min(controlLength, body.Length);
            var controls = System.Text.Encoding.ASCII.GetString(body[..controlLength]);
            var parts = System.Text.Encoding.Latin1.GetString(body[controlLength..]).Split((char)UnitTerminator);
            var labels = parts.Length > 1 ? parts[1] : "";
            // Some S-101 1.x producers wrote repeating groups in braces, "{3b12,b11,A}", rather than parentheses.
            var formats = parts.Length > 2 ? parts[2].Trim().Replace('{', '(').Replace('}', ')') : "";

            var star = labels.IndexOf('*');
            var fixedText = (star < 0 ? labels : labels[..star]).TrimEnd('\\', '!');
            var repeatingText = star < 0 ? "" : labels[(star + 1)..];
            var fixedLabels = Split(fixedText);
            var repeatingLabels = Split(repeatingText);

            var depth = 0;
            var maxDepth = 0;
            foreach (var c in formats)
            {
                if (c == '(')
                    maxDepth = Math.Max(maxDepth, ++depth);
                else if (c == ')')
                    depth--;
            }
            if (maxDepth > limits.MaxFormatNesting)
                throw new Iso8211FormatException(
                    $"Format controls nested {maxDepth} deep exceed the limit of {limits.MaxFormatNesting}.", position,
                    nameof(Iso8211Limits.MaxFormatNesting));

            var atoms = Expand(formats, position);
            var fixedCount = Math.Min(fixedLabels.Count, atoms.Count);
            var fixedAtoms = atoms.Take(fixedCount).ToList();
            var repeatingAtoms = atoms.Skip(fixedCount).ToList();
            if (repeatingLabels.Count > 0 && repeatingAtoms.Count == 0 && atoms.Count > 0)
                repeatingAtoms = [.. atoms];
            if (fixedAtoms.Count != fixedLabels.Count || (repeatingLabels.Count > 0 && repeatingAtoms.Count == 0))
                throw new Iso8211FormatException("Format controls do not cover the field's subfields.", position);
            while (repeatingAtoms.Count < repeatingLabels.Count)
                repeatingAtoms.Add(repeatingAtoms[repeatingAtoms.Count % Math.Max(1, repeatingAtoms.Count)]);

            var escape = controls.Length >= 9 ? controls[6..9] : "";
            return new Layout
            {
                Name = parts[0],
                FormatControls = formats,
                FixedLabels = fixedLabels,
                RepeatingLabels = repeatingLabels,
                FixedAtoms = fixedAtoms,
                RepeatingAtoms = repeatingAtoms.Take(repeatingLabels.Count).ToList(),
                Encoding = escape switch
                {
                    "%/G" => TextEncoding.Utf8,
                    "%/@" => TextEncoding.Ucs2,
                    _ => TextEncoding.Latin1
                }
            };
        }

        private static List<string> Split(string text)
            => text.Split('!', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim('\\', '*')).Where(s => s.Length > 0).ToList();

        // Flattens "(b11,2b12,(3b12,A),A(8))" into atomic formats; nested groups only mark repetition.
        private static List<Atom> Expand(string formats, long position)
        {
            var text = formats.Trim();
            if (text.StartsWith('(') && text.EndsWith(')'))
                text = text[1..^1];
            var atoms = new List<Atom>();
            foreach (var item in TopLevel(text))
            {
                var i = 0;
                while (i < item.Length && char.IsAsciiDigit(item[i]))
                    i++;
                var count = i == 0 ? 1 : int.Parse(item[..i]);
                var rest = item[i..];
                if (count > 10_000)
                    throw new Iso8211FormatException($"Unreasonable format repeat count {count}.", position);
                var expanded = rest.StartsWith('(') ? Expand(rest, position) : [ParseAtom(rest, position)];
                for (var n = 0; n < count; n++)
                    atoms.AddRange(expanded);
            }
            return atoms;
        }

        private static IEnumerable<string> TopLevel(string text)
        {
            var depth = 0;
            var start = 0;
            for (var i = 0; i <= text.Length; i++)
            {
                if (i == text.Length || (text[i] == ',' && depth == 0))
                {
                    var item = text[start..i].Trim();
                    if (item.Length > 0)
                        yield return item;
                    start = i + 1;
                }
                else if (text[i] == '(')
                    depth++;
                else if (text[i] == ')')
                    depth--;
            }
        }

        private static Atom ParseAtom(string item, long position)
        {
            int Width(string s) => s.Length > 3 && s[1] == '(' && s[^1] == ')' ? int.Parse(s[2..^1]) : 0;
            if (item.Length == 0)
                throw new Iso8211FormatException("Empty format control.", position);
            switch (item[0])
            {
                case 'A':
                case 'I':
                case 'R':
                    return new Atom(AtomKind.Text, Width(item));
                case 'B':
                    return new Atom(AtomKind.Bits, Width(item) / 8);
                case 'b' when item.Length == 3 && char.IsAsciiDigit(item[1]) && char.IsAsciiDigit(item[2]):
                    var width = item[2] - '0';
                    return item[1] switch
                    {
                        '1' => new Atom(AtomKind.Unsigned, width),
                        '2' => new Atom(AtomKind.Integer, width),
                        '4' => new Atom(AtomKind.Float, width),
                        _ => throw new Iso8211FormatException($"Unsupported binary format {item}.", position)
                    };
                default:
                    throw new Iso8211FormatException($"Unsupported format control '{item}'.", position);
            }
        }

        public Iso8211Field Decode(string tag, ReadOnlySpan<byte> raw, long position)
        {
            var end = raw.Length > 0 && raw[^1] == FieldTerminator ? raw.Length - 1 : raw.Length;
            var at = 0;
            var values = new Dictionary<string, object?>(FixedLabels.Count, StringComparer.Ordinal);
            for (var i = 0; i < FixedLabels.Count; i++)
                values[FixedLabels[i]] = ReadValue(raw, ref at, end, FixedAtoms[i], position);
            var repeats = new List<IReadOnlyDictionary<string, object?>>();
            if (RepeatingLabels.Count > 0)
            {
                while (at < end)
                {
                    var before = at;
                    var row = new Dictionary<string, object?>(RepeatingLabels.Count, StringComparer.Ordinal);
                    for (var i = 0; i < RepeatingLabels.Count; i++)
                        row[RepeatingLabels[i]] = ReadValue(raw, ref at, end, RepeatingAtoms[i], position);
                    if (at == before)
                        throw new Iso8211FormatException($"Field {tag} repeating group consumes no data.", position);
                    repeats.Add(row);
                }
            }
            return new Iso8211Field(tag, values, repeats, raw.ToArray());
        }

        private object? ReadValue(ReadOnlySpan<byte> raw, ref int at, int end, Atom atom, long position)
        {
            if (atom.Kind == AtomKind.Text)
                return ReadText(raw, ref at, end, atom.Width, position);
            if (at + atom.Width > end)
                throw new Iso8211FormatException("Subfield runs past the end of its field.", position);
            var bytes = raw.Slice(at, atom.Width);
            at += atom.Width;
            return atom.Kind switch
            {
                AtomKind.Unsigned => atom.Width switch
                {
                    1 => (long)bytes[0],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
                    4 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
                    8 => (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes),
                    _ => throw new Iso8211FormatException($"Unsupported unsigned width {atom.Width}.", position)
                },
                AtomKind.Integer => atom.Width switch
                {
                    1 => (long)(sbyte)bytes[0],
                    2 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
                    4 => BinaryPrimitives.ReadInt32LittleEndian(bytes),
                    8 => BinaryPrimitives.ReadInt64LittleEndian(bytes),
                    _ => throw new Iso8211FormatException($"Unsupported integer width {atom.Width}.", position)
                },
                AtomKind.Float => atom.Width switch
                {
                    4 => (double)BinaryPrimitives.ReadSingleLittleEndian(bytes),
                    8 => BinaryPrimitives.ReadDoubleLittleEndian(bytes),
                    _ => throw new Iso8211FormatException($"Unsupported float width {atom.Width}.", position)
                },
                _ => bytes.ToArray()
            };
        }

        private string ReadText(ReadOnlySpan<byte> raw, ref int at, int end, int width, long position)
        {
            if (width > 0)
            {
                if (at + width > end)
                    throw new Iso8211FormatException("Fixed-width text runs past the end of its field.", position);
                var fixedText = Decode(raw.Slice(at, width));
                at += width;
                return fixedText;
            }
            var start = at;
            if (Encoding == TextEncoding.Ucs2)
            {
                while (at + 1 < end && !(raw[at] == UnitTerminator && raw[at + 1] == 0))
                    at += 2;
                var text = Decode(raw[start..Math.Min(at, end)]);
                at = Math.Min(at + 2, end);
                return text;
            }
            while (at < end && raw[at] != UnitTerminator)
                at++;
            var value = Decode(raw[start..at]);
            if (at < end)
                at++;
            return value;
        }

        private string Decode(ReadOnlySpan<byte> bytes) => Encoding switch
        {
            TextEncoding.Utf8 => System.Text.Encoding.UTF8.GetString(bytes),
            TextEncoding.Ucs2 => System.Text.Encoding.Unicode.GetString(bytes),
            _ => System.Text.Encoding.Latin1.GetString(bytes)
        };
    }
}
