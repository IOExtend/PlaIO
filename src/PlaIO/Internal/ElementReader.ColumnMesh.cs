using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private readonly record struct ColumnBeamMask(double MinAcross, double MaxAcross, PlaPlanPoint[] Outline);

    private static PlaColumnCutMesh? BuildColumnCutMesh(PlaPlanPoint[] section, double minAcross, double maxAcross,
        List<ColumnBeamMask> masks, PlaColumnMeshPoint origin, Func<PlaPlanPoint, double, PlaColumnMeshPoint> pointAt,
        IReadOnlyList<PlaPlanPoint>? profile = null)
    {
        var points = section.ToList();
        List<int[]> cells = [[0, 1, 2, 3]];
        if (profile is not null && !SplitAlong(profile)) return null;
        foreach (var mask in masks)
            if (!SplitAlong(mask.Outline)) return null;

        bool SplitAlong(IReadOnlyList<PlaPlanPoint> boundary)
        {
            for (int j = 0; j < boundary.Count; j++)
            {
                var a = boundary[j];
                var b = boundary[(j + 1) % boundary.Count];
                double length = Distance(a, b);
                if (!(length > JunctionTolerance)) return false;
                SplitColumnCells(points, cells, a, b, length * JunctionTolerance);
                if (cells.Count > 1024) return false;
            }
            return true;
        }

        var stops = masks.SelectMany(m => new[] { m.MinAcross, m.MaxAcross }).Append(minAcross).Append(maxAcross)
            .Distinct().Order().ToArray();
        double tolerance = profile is null ? 0 : JunctionTolerance;
        if (tolerance > 0)
        {
            var levels = new List<double>(stops.Length);
            foreach (double stop in stops)
            {
                if (levels.Count == 0 || stop - levels[^1] > tolerance)
                    levels.Add(stop);
                else if (stop == maxAcross)
                    levels[^1] = stop;
            }
            // Saved caps and cutter faces at the same elevation can differ by roundoff.
            stops = levels.ToArray();
        }
        var centres = cells.Select(c => new PlaPlanPoint(c.Average(v => points[v].X), c.Average(v => points[v].Y))).ToArray();
        var areas = cells.Select(c => SignedRingArea(c.Select(v => points[v]).ToArray())).ToArray();
        if (areas.Any(a => !(a > 0))) return null;
        var material = centres.Select(p => profile is null || ContainsPoint(profile, p)).ToArray();
        if (profile is not null)
        {
            double area = areas.Where((_, i) => material[i]).Sum();
            double perimeter = profile.Select((p, i) => Distance(p, profile[(i + 1) % profile.Count])).Sum();
            if (Math.Abs(area - SignedRingArea(profile)) > JunctionTolerance * perimeter) return null;
        }
        var edges = new Dictionary<(int Start, int End), int>();
        for (int c = 0; c < cells.Count; c++)
            for (int j = 0; j < cells[c].Length; j++)
                if (!edges.TryAdd((cells[c][j], cells[c][(j + 1) % cells[c].Length]), c)) return null;

        var inside = new bool[stops.Length - 1, cells.Count];
        double volume = 0;
        for (int b = 0; b + 1 < stops.Length; b++)
        {
            var cutters = masks.Where(m =>
                m.MinAcross <= stops[b] + tolerance && m.MaxAcross >= stops[b + 1] - tolerance).ToArray();
            for (int c = 0; c < cells.Count; c++)
            {
                inside[b, c] = material[c] && !cutters.Any(mask => InsideConvex(mask.Outline, centres[c]));
                if (inside[b, c]) volume += areas[c] * (stops[b + 1] - stops[b]);
            }
        }
        if (!(volume > 0) || !double.IsFinite(volume)) return null;

        var vertices = new List<PlaColumnMeshPoint>();
        var keys = new Dictionary<(int Point, int Stop), int>();
        var triangles = new List<int>();
        for (int b = 0; b + 1 < stops.Length; b++)
        {
            for (int c = 0; c < cells.Count; c++)
            {
                if (!inside[b, c]) continue;
                var cell = cells[c];
                if (b == 0 || !inside[b - 1, c]) Cap(cell, centres[c], b, upper: false);
                if (b == stops.Length - 2 || !inside[b + 1, c]) Cap(cell, centres[c], b + 1, upper: true);
                for (int j = 0; j < cell.Length; j++)
                {
                    int a = cell[j], d = cell[(j + 1) % cell.Length];
                    if (edges.TryGetValue((d, a), out int neighbor) && inside[b, neighbor]) continue;
                    int a0 = Vertex(a, b), d0 = Vertex(d, b), a1 = Vertex(a, b + 1), d1 = Vertex(d, b + 1);
                    triangles.AddRange([a0, d0, d1, a0, d1, a1]);
                }
            }
        }
        if (vertices.Count == 0 || !IsClosed(triangles)) return null;
        var plan = (origin.X + vertices.Min(p => p.X), origin.Y + vertices.Min(p => p.Y),
            origin.X + vertices.Max(p => p.X), origin.Y + vertices.Max(p => p.Y));
        var bounds = MakeBoundingBox(plan, origin.Z + vertices.Min(p => p.Z), origin.Z + vertices.Max(p => p.Z));
        return bounds is not null ? new PlaColumnCutMesh(origin, vertices.AsReadOnly(), triangles.AsReadOnly(), bounds, volume) : null;

        int Vertex(int point, int level)
        {
            if (keys.TryGetValue((point, level), out int index)) return index;
            keys.Add((point, level), vertices.Count);
            vertices.Add(pointAt(points[point], stops[level]));
            return vertices.Count - 1;
        }

        void Cap(int[] cell, PlaPlanPoint centre, int level, bool upper)
        {
            // Every edge keeps its shared nodes, including collinear ones needed by adjacent faces.
            int fan = vertices.Count;
            vertices.Add(pointAt(centre, stops[level]));
            for (int j = 0; j < cell.Length; j++)
            {
                int a = Vertex(cell[j], level), d = Vertex(cell[(j + 1) % cell.Length], level);
                triangles.AddRange([fan, upper ? a : d, upper ? d : a]);
            }
        }
    }

    private static bool InsideConvex(PlaPlanPoint[] outline, PlaPlanPoint point)
    {
        for (int i = 0; i < outline.Length; i++)
            if (Cross(outline[i], outline[(i + 1) % outline.Length], point) < 0) return false;
        return true;
    }

    private static void SplitColumnCells(List<PlaPlanPoint> points, List<int[]> cells, PlaPlanPoint a, PlaPlanPoint b,
        double tolerance)
    {
        var signed = points.Select(p => Cross(a, b, p)).Select(d => Math.Abs(d) <= tolerance ? 0 : d).ToArray();
        var intersections = new Dictionary<(int, int), int>();
        int count = cells.Count;
        for (int c = 0; c < count; c++)
        {
            var polygon = cells[c];
            if (!polygon.Any(i => signed[i] > 0) || !polygon.Any(i => signed[i] < 0)) continue;
            var positive = new List<int>();
            var negative = new List<int>();
            for (int i = 0; i < polygon.Length; i++)
            {
                int first = polygon[i], next = polygon[(i + 1) % polygon.Length];
                double lo = signed[first], hi = signed[next];
                if (lo >= 0) positive.Add(first);
                if (lo <= 0) negative.Add(first);
                if (lo * hi >= 0) continue;
                var edge = (Math.Min(first, next), Math.Max(first, next));
                if (!intersections.TryGetValue(edge, out int cut))
                {
                    double t = lo / (lo - hi);
                    var p = points[first];
                    var q = points[next];
                    cut = points.Count;
                    points.Add(new PlaPlanPoint(p.X + (q.X - p.X) * t, p.Y + (q.Y - p.Y) * t));
                    intersections.Add(edge, cut);
                }
                positive.Add(cut);
                negative.Add(cut);
            }
            cells[c] = positive.ToArray();
            cells.Add(negative.ToArray());
        }
    }

    private static bool IsClosed(List<int> triangles)
    {
        var sides = new Dictionary<(int, int), (int Count, int Balance)>();
        for (int i = 0; i < triangles.Count; i += 3)
            for (int j = 0; j < 3; j++)
            {
                int a = triangles[i + j], b = triangles[i + (j + 1) % 3];
                var edge = (Math.Min(a, b), Math.Max(a, b));
                var tally = sides.GetValueOrDefault(edge);
                sides[edge] = (tally.Count + 1, tally.Balance + (a < b ? 1 : -1));
            }
        return sides.Values.All(s => s is (2, 0));
    }
}
