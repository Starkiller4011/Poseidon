using Poseidon.Core.Modules;
using Poseidon.Modules.Charts.ExchangeSets;

namespace Poseidon.Modules.Charts.Store;

// Cell adjacency from catalogue coverage (C32): two cells are neighbours when any of their coverage
// polygons touch or overlap. Planar in degrees, which is exact for touching and overlapping tests; the
// antimeridian is not handled.
internal static class Coverage
{
    // Absorbs rounding in the orientation test only; far below any real gap between cells.
    private const double Epsilon = 1e-12;

    public static IReadOnlyList<double[]> From(ExchangeSetEntry entry)
    {
        if (entry.CoveragePolygons.Count > 0)
            return entry.CoveragePolygons.Select(p => p.SelectMany(v => new[] { v.LatitudeDegrees, v.LongitudeDegrees }).ToArray()).ToList();
        if (entry.BoundingBox is { } b)
            return [[b.South, b.West, b.South, b.East, b.North, b.East, b.North, b.West, b.South, b.West]];
        return [];
    }

    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Adjacency(IReadOnlyDictionary<string, IReadOnlyList<double[]>> cells)
    {
        var shapes = cells.ToDictionary(c => c.Key, c => c.Value.Select(Polygon.From).Where(p => p.Points.Length > 0).ToList());
        var adjacency = cells.Keys.ToDictionary(k => k, _ => new HashSet<string>(StringComparer.Ordinal));
        var names = shapes.Keys.Order(StringComparer.Ordinal).ToList();
        for (var i = 0; i < names.Count; i++)
        {
            for (var j = i + 1; j < names.Count; j++)
            {
                if (!shapes[names[i]].Any(a => shapes[names[j]].Any(b => a.Intersects(b))))
                    continue;
                adjacency[names[i]].Add(names[j]);
                adjacency[names[j]].Add(names[i]);
            }
        }
        return adjacency.ToDictionary(a => a.Key, a => (IReadOnlySet<string>)a.Value);
    }

    private readonly record struct Point(double X, double Y);

    private sealed class Polygon
    {
        private Polygon(Point[] points)
        {
            Points = points;
            if (points.Length == 0)
                return;
            (MinX, MaxX, MinY, MaxY) = (points.Min(p => p.X), points.Max(p => p.X), points.Min(p => p.Y), points.Max(p => p.Y));
        }

        public Point[] Points { get; }
        private double MinX { get; }
        private double MaxX { get; }
        private double MinY { get; }
        private double MaxY { get; }

        public static Polygon From(double[] latLon)
            => new(Enumerable.Range(0, latLon.Length / 2).Select(i => new Point(latLon[2 * i + 1], latLon[2 * i])).ToArray());

        public bool Intersects(Polygon other)
        {
            if (MaxX < other.MinX || other.MaxX < MinX || MaxY < other.MinY || other.MaxY < MinY)
                return false;
            foreach (var (a, b) in Edges())
                foreach (var (c, d) in other.Edges())
                    if (SegmentsIntersect(a, b, c, d))
                        return true;
            return Contains(other.Points[0]) || other.Contains(Points[0]);
        }

        private IEnumerable<(Point, Point)> Edges()
        {
            for (var i = 0; i < Points.Length; i++)
                yield return (Points[i], Points[(i + 1) % Points.Length]);
        }

        // Even-odd ray casting; boundary points are already caught by the edge test.
        private bool Contains(Point p)
        {
            var inside = false;
            foreach (var (a, b) in Edges())
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            return inside;
        }

        private static bool SegmentsIntersect(Point a, Point b, Point c, Point d)
        {
            int o1 = Orientation(a, b, c), o2 = Orientation(a, b, d), o3 = Orientation(c, d, a), o4 = Orientation(c, d, b);
            if (o1 != o2 && o3 != o4)
                return true;
            return (o1 == 0 && Within(a, b, c)) || (o2 == 0 && Within(a, b, d))
                || (o3 == 0 && Within(c, d, a)) || (o4 == 0 && Within(c, d, b));
        }

        private static int Orientation(Point a, Point b, Point c)
        {
            var cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            return cross > Epsilon ? 1 : cross < -Epsilon ? -1 : 0;
        }

        // Whether collinear point p lies on segment ab.
        private static bool Within(Point a, Point b, Point p)
            => p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X) && p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y);
    }
}
