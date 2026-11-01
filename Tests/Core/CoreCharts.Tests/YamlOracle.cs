using System.Globalization;

namespace CoreCharts.Tests;

// Reads the S-101_1.2 YAML sidecars (an oracle, not an authority). Only the shape those files use is
// supported; anything else throws so a format surprise cannot silently pass a comparison.
internal sealed class YamlOracle
{
    public sealed record Attribute(string Name, string? Value, string? Id, string? Parent);

    public sealed record Association(string To, string Name, string Role);

    public sealed record Feature(
        string Name, string Foid, string Prim, IReadOnlyList<Attribute> Attributes,
        IReadOnlyList<Association> Associations, IReadOnlyList<string> Geometry);

    // Positions are (longitude, latitude) as the sidecars write them; Z holds depths in metres.
    public sealed record Positions(string Name, IReadOnlyList<(double Lon, double Lat)> Locations, IReadOnlyList<double> Z);

    public required string CellName { get; init; }
    public required string Edition { get; init; }
    public required IReadOnlyList<Feature> Features { get; init; }
    public required IReadOnlyList<Positions> Points { get; init; }
    public required IReadOnlyList<Positions> Depths { get; init; }

    public static YamlOracle Load(string path)
    {
        var header = new Dictionary<string, string>(StringComparer.Ordinal);
        var sections = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.Ordinal);
        List<Dictionary<string, object>>? items = null;
        Dictionary<string, object>? item = null;
        List<Dictionary<string, object>>? subList = null;
        Dictionary<string, object>? subItem = null;
        var lineNumber = 0;
        foreach (var raw in File.ReadLines(path))
        {
            lineNumber++;
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
                continue;
            var indent = line.Length - line.TrimStart().Length;
            var text = line.TrimStart();
            if (indent == 0)
            {
                var (key, value) = Split(text, path, lineNumber);
                if (value.Length == 0)
                    sections[key] = items = [];
                else
                    header[key] = value;
                item = null;
                subList = null;
                continue;
            }
            if (items is null)
                throw Unsupported(path, lineNumber, line);
            if (indent == 2 && text.StartsWith("- ", StringComparison.Ordinal))
            {
                items.Add(item = new Dictionary<string, object>(StringComparer.Ordinal));
                subList = null;
                Put(item, text[2..], path, lineNumber);
            }
            else if (indent == 4 && item is not null && !text.StartsWith("- ", StringComparison.Ordinal))
            {
                var (key, value) = Split(text, path, lineNumber);
                if (value.Length == 0)
                    item[key] = subList = [];
                else
                {
                    item[key] = value;
                    subList = null;
                }
            }
            else if (indent is 4 or 6 && text.StartsWith("- ", StringComparison.Ordinal) && subList is not null)
            {
                subList.Add(subItem = new Dictionary<string, object>(StringComparer.Ordinal));
                Put(subItem, text[2..], path, lineNumber);
            }
            else if (indent is 6 or 8 && subItem is not null && subList is not null)
                Put(subItem, text, path, lineNumber);
            else
                throw Unsupported(path, lineNumber, line);
        }

        return new YamlOracle
        {
            CellName = header["CellName"],
            Edition = header["Edition"],
            Features = Section(sections, "Features").Select(ToFeature).ToList(),
            Points = Section(sections, "Points").Select(ToPositions).ToList(),
            Depths = Section(sections, "Depths").Select(ToPositions).ToList()
        };
    }

    private static List<Dictionary<string, object>> Section(Dictionary<string, List<Dictionary<string, object>>> sections, string name)
        => sections.TryGetValue(name, out var list) ? list : [];

    private static Feature ToFeature(Dictionary<string, object> d) => new(
        Text(d, "Name")!, Text(d, "Foid")!, Text(d, "Prim")!,
        List(d, "Attributes").Select(a => new Attribute(Text(a, "Name")!, Text(a, "Value"), Text(a, "id"), Text(a, "parent"))).ToList(),
        List(d, "Association").Select(a => new Association(Text(a, "To")!, Text(a, "Name")!, Text(a, "Role")!)).ToList(),
        (Text(d, "Geometry") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static Positions ToPositions(Dictionary<string, object> d)
    {
        var numbers = Numbers(Text(d, "Location"));
        var locations = Enumerable.Range(0, numbers.Count / 2).Select(i => (numbers[2 * i], numbers[2 * i + 1])).ToList();
        return new Positions(Text(d, "Name")!, locations, Numbers(Text(d, "Z")));
    }

    private static List<double> Numbers(string? text)
        => (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToList();

    private static string? Text(Dictionary<string, object> d, string key)
        => d.TryGetValue(key, out var v) ? v as string : null;

    private static List<Dictionary<string, object>> List(Dictionary<string, object> d, string key)
        => d.TryGetValue(key, out var v) && v is List<Dictionary<string, object>> list ? list : [];

    private static void Put(Dictionary<string, object> target, string text, string path, int line)
    {
        var (key, value) = Split(text, path, line);
        target[key] = value;
    }

    private static (string Key, string Value) Split(string text, string path, int line)
    {
        var colon = text.IndexOf(':');
        if (colon <= 0)
            throw Unsupported(path, line, text);
        return (text[..colon].Trim(), text[(colon + 1)..].Trim());
    }

    private static FormatException Unsupported(string path, int line, string text)
        => new($"Unsupported YAML shape at {Path.GetFileName(path)}:{line}: '{text}'");
}
