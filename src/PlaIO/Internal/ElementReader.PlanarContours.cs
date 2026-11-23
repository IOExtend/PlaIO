using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private sealed class PlanarEdge(PlaPlanPoint start, PlaPlanPoint end)
    {
        internal PlaPlanPoint Start { get; } = start;
        internal PlaPlanPoint End { get; } = end;
        internal List<(double Parameter, PlaPlanPoint Point)> Nodes { get; } = [(0, start), (1, end)];
        internal List<(double Start, double End, bool SameDirection)> Overlaps { get; } = [];
    }

    private static IReadOnlyList<IReadOnlyList<PlaPlanPoint>>? SubtractSlabFootprint(
        IReadOnlyList<IReadOnlyList<PlaPlanPoint>> rings, IReadOnlyList<IReadOnlyList<PlaPlanPoint>> footprint, double cutArea)
    {
        double footprintArea = footprint.Sum(SignedRingArea);
        if (!double.IsFinite(footprintArea) || footprintArea >= 0 || footprint.Any(r => !IsSimpleRing(r)) ||
            CombinePlanarContours(rings, footprint, subtract: true) is not { } result)
            return null;

        double area = result.Sum(SignedRingArea);
        double perimeter = rings.Concat(footprint).Sum(ring => ring.Select((p, i) => Distance(p, ring[(i + 1) % ring.Count])).Sum());
        return area > 0 && Math.Abs(rings.Sum(SignedRingArea) - area - cutArea) <= JunctionTolerance * Math.Max(1, perimeter)
            ? result : null;
    }

    private static IReadOnlyList<IReadOnlyList<PlaPlanPoint>>? CombinePlanarContours(
        IReadOnlyList<IReadOnlyList<PlaPlanPoint>> rings, IReadOnlyList<IReadOnlyList<PlaPlanPoint>> operand, bool subtract)
    {
        if (rings.Sum(r => r.Count) + operand.Sum(r => r.Count) > 4096) return null;
        var outline = rings.SelectMany(Edges).ToArray();
        var other = operand.SelectMany(Edges).ToArray();
        foreach (var a in outline)
            foreach (var b in other)
                SplitPlanarEdges(a, b);

        var next = new Dictionary<PlaPlanPoint, PlaPlanPoint>();
        var predecessors = new HashSet<PlaPlanPoint>();
        // Both kinds of boundary put the remaining material to their left.
        if (!KeepBoundaryEdges(outline, p => ContainsMaterial(operand, p), true) ||
            !KeepBoundaryEdges(other, p => ContainsMaterial(rings, p) != subtract, false) ||
            next.Count is < 3 or > 4096)
            return null;

        var result = new List<IReadOnlyList<PlaPlanPoint>>();
        while (next.Count != 0)
        {
            var start = next.First().Key;
            var ring = new List<PlaPlanPoint>();
            var current = start;
            do
            {
                ring.Add(current);
                if (!next.Remove(current, out current)) return null;
            } while (current != start);
            if (ring.Count < 3 || !IsSimpleRing(ring) || SignedRingArea(ring) == 0) return null;
            result.Add(ring.AsReadOnly());
        }

        return result.AsReadOnly();

        static IEnumerable<PlanarEdge> Edges(IReadOnlyList<PlaPlanPoint> ring) =>
            ring.Select((point, i) => new PlanarEdge(point, ring[(i + 1) % ring.Count]));

        static bool ContainsMaterial(IReadOnlyList<IReadOnlyList<PlaPlanPoint>> contours, PlaPlanPoint point) =>
            contours.Count(r => ContainsPoint(r, point)) % 2 != 0;

        bool KeepBoundaryEdges(PlanarEdge[] edges, Func<PlaPlanPoint, bool> isRemoved, bool isOutline)
        {
            foreach (var edge in edges)
            {
                edge.Nodes.Sort((a, b) => a.Parameter.CompareTo(b.Parameter));
                for (int i = 1; i < edge.Nodes.Count; i++)
                {
                    var (t0, start) = edge.Nodes[i - 1];
                    var (t1, end) = edge.Nodes[i];
                    if (start == end) continue;
                    if (!(t0 < t1)) return false;
                    double t = t0 + (t1 - t0) / 2;
                    int overlap = edge.Overlaps.FindIndex(r => r.Start < t && t < r.End);
                    var middle = new PlaPlanPoint(start.X + (end.X - start.X) / 2, start.Y + (end.Y - start.Y) / 2);
                    bool keep = overlap >= 0 ? isOutline && edge.Overlaps[overlap].SameDirection : !isRemoved(middle);
                    if (keep && (!next.TryAdd(start, end) || !predecessors.Add(end))) return false;
                }
            }
            return true;
        }
    }

    private static void SplitPlanarEdges(PlanarEdge a, PlanarEdge b)
    {
        if (!SegmentsIntersect(a.Start, a.End, b.Start, b.End)) return;
        double ax = a.End.X - a.Start.X, ay = a.End.Y - a.Start.Y;
        double bx = b.End.X - b.Start.X, by = b.End.Y - b.Start.Y;
        double x = b.Start.X - a.Start.X, y = b.Start.Y - a.Start.Y;
        double cross = ax * by - ay * bx;
        if (cross != 0)
        {
            // Prefer a saved vertex at a shared endpoint to give all its incident edges the same node.
            if (AddEndpoint(a, b, a.Start, 0) | AddEndpoint(a, b, a.End, 1) |
                AddEndpoint(b, a, b.Start, 0) | AddEndpoint(b, a, b.End, 1)) return;
            double t = (x * by - y * bx) / cross;
            double u = (x * ay - y * ax) / cross;
            if (t is > 0 and < 1 && u is > 0 and < 1)
            {
                var p = new PlaPlanPoint(a.Start.X + t * ax, a.Start.Y + t * ay);
                a.Nodes.Add((t, p));
                b.Nodes.Add((u, p));
            }
            return;
        }
        if (Cross(a.Start, a.End, b.Start) != 0 || Cross(a.Start, a.End, b.End) != 0) return;
        AddOverlap(a, b);
        AddOverlap(b, a);

        static double Parameter(PlanarEdge edge, PlaPlanPoint p)
        {
            double dx = edge.End.X - edge.Start.X, dy = edge.End.Y - edge.Start.Y;
            return Math.Abs(dx) >= Math.Abs(dy) ? (p.X - edge.Start.X) / dx : (p.Y - edge.Start.Y) / dy;
        }

        static bool AddEndpoint(PlanarEdge owner, PlanarEdge edge, PlaPlanPoint p, double position)
        {
            if (Cross(edge.Start, edge.End, p) != 0) return false;
            double t = Parameter(edge, p);
            if (t is < 0 or > 1) return false;
            owner.Nodes.Add((position, p));
            edge.Nodes.Add((t, p));
            return true;
        }

        static void AddOverlap(PlanarEdge edge, PlanarEdge other)
        {
            double t = Parameter(edge, other.Start), u = Parameter(edge, other.End);
            if (t is > 0 and < 1) edge.Nodes.Add((t, other.Start));
            if (u is > 0 and < 1) edge.Nodes.Add((u, other.End));
            double lo = Math.Max(0, Math.Min(t, u)), hi = Math.Min(1, Math.Max(t, u));
            if (lo < hi) edge.Overlaps.Add((lo, hi, t < u));
        }
    }

    private static IReadOnlyList<PlaSlabPrism>? GroupSlabPrisms(IReadOnlyList<IReadOnlyList<PlaPlanPoint>> rings)
    {
        return GroupPlanarContours(rings) is { } groups
            ? Array.AsReadOnly(groups.Select(g => new PlaSlabPrism(g.AsReadOnly())).ToArray()) : null;
    }

    private static List<IReadOnlyList<PlaPlanPoint>>[]? GroupPlanarContours(IReadOnlyList<IReadOnlyList<PlaPlanPoint>> rings)
    {
        var groups = rings.Where(r => SignedRingArea(r) > 0).Select(r => new List<IReadOnlyList<PlaPlanPoint>> { r }).ToArray();
        foreach (var hole in rings.Where(r => SignedRingArea(r) < 0))
        {
            var group = groups.Where(g => IsSeparateHole([g[0]], hole)).MinBy(g => SignedRingArea(g[0]));
            if (group is null || !IsSeparateHole(group, hole)) return null;
            group.Add(hole);
        }
        return groups.Length > 0 ? groups : null;
    }
}
