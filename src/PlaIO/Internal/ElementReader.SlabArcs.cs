using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private readonly record struct SlabEdge(PlaPlanPoint Start, PlaPlanPoint End, double Sweep,
        PlaPlanPoint Center = default, double Radius = 0)
    {
        public PlaPlanPoint AtAngle(double angle) => new(Center.X + Radius * Math.Cos(angle), Center.Y + Radius * Math.Sin(angle));

        public bool IncludesAngle(double angle)
        {
            double start = Math.Atan2(Start.Y - Center.Y, Start.X - Center.X);
            double travel = Math.IEEERemainder(Math.Sign(Sweep) * (angle - start), Math.Tau);
            if (travel < 0) travel += Math.Tau;
            double tolerance = JunctionTolerance / Radius;
            return travel <= Math.Abs(Sweep) + tolerance || Math.Tau - travel <= tolerance;
        }

        public bool IncludesDirection(PlaPlanPoint point) => IncludesAngle(Math.Atan2(point.Y - Center.Y, point.X - Center.X));
    }

    private static PlaSlabGeometry? ReadCurvedSlabGeometry(ReadOnlySpan<byte> polygon, PlaBoundingBox box)
    {
        int points = BinaryPrimitives.ReadInt32LittleEndian(polygon[43..]);
        int arcCount = BinaryPrimitives.ReadInt32LittleEndian(polygon[47..]);
        int ringCount = BinaryPrimitives.ReadInt32LittleEndian(polygon[51..]);
        if (points > 4096) return null;

        var sweeps = new Dictionary<int, double>(arcCount);
        int offsets = 71 + 16 * (points + 1);
        for (int i = 0; i < arcCount; i++, offsets += 16)
        {
            int start = BinaryPrimitives.ReadInt32LittleEndian(polygon[offsets..]) - 1;
            double sweep = ReadDouble(polygon, offsets + 8);
            if (!(Math.Abs(sweep) is > 0 and < Math.Tau) || !sweeps.TryAdd(start, sweep)) return null;
        }

        var rings = new List<IReadOnlyList<PlaPlanPoint>>(ringCount);
        var boundaries = new List<SlabEdge[]>(ringCount);
        var arcs = new List<PlaSlabArc>(arcCount);
        double area = 0;
        var bounds = (X0: double.PositiveInfinity, Y0: double.PositiveInfinity,
            X1: double.NegativeInfinity, Y1: double.NegativeInfinity);
        int first = 0;
        for (int ringIndex = 0; ringIndex < ringCount; ringIndex++)
        {
            int last = BinaryPrimitives.ReadInt32LittleEndian(polygon[(offsets + (ringIndex + 1) * 4)..]);
            var ring = ReadSlabRing(polygon, first, last);
            var edges = new SlabEdge[ring.Length];
            double signedArea = SignedRingArea(ring);
            var ringArcs = new List<PlaSlabArc>();
            for (int i = 0; i < ring.Length; i++)
            {
                var p = ring[i];
                var q = ring[(i + 1) % ring.Length];
                if (p == q) return null;
                Include(p);
                if (!sweeps.Remove(first + i, out double sweep))
                {
                    edges[i] = new SlabEdge(p, q, 0);
                    continue;
                }

                if (ReadCircularEdge(p, q, sweep, out double segmentArea) is not { } edge) return null;
                edges[i] = edge;
                for (int axis = 0; axis < 4; axis++)
                {
                    double angle = axis * (Math.PI / 2);
                    if (edge.IncludesAngle(angle)) Include(edge.AtAngle(angle));
                }
                signedArea += segmentArea;
                ringArcs.Add(new PlaSlabArc(ringIndex, i, sweep));
            }
            first = last;

            if (!double.IsFinite(signedArea) || signedArea == 0 || !SlabRingIsSimple(edges)) return null;
            bool outer = ringIndex == 0;
            if (!outer && !CurvedHoleFits(ring, edges, rings, boundaries)) return null;

            area += outer ? Math.Abs(signedArea) : -Math.Abs(signedArea);
            if ((signedArea > 0) != outer)
            {
                Array.Reverse(ring);
                ringArcs = ringArcs.Select(a => a with
                {
                    StartVertexIndex = ring.Length - 1 - (a.StartVertexIndex + 1) % ring.Length,
                    SweepRadians = -a.SweepRadians,
                }).OrderBy(a => a.StartVertexIndex).ToList();
            }
            arcs.AddRange(ringArcs);
            rings.Add(Array.AsReadOnly(ring));
            boundaries.Add(edges);
        }
        if (sweeps.Count != 0 || !double.IsFinite(area) || area <= 0 ||
            !SamePlan(bounds, box.XMin, box.YMin, box.XMax, box.YMax)) return null;
        // shortcut: curved face quantities and volumes require the verified Archicad segmentation and cut geometry.
        return new PlaSlabGeometry(rings.AsReadOnly(), box.ZMin, box.ZMax, area, null) { Arcs = arcs.AsReadOnly() };

        void Include(PlaPlanPoint p)
        {
            bounds = (Math.Min(p.X, bounds.X0), Math.Min(p.Y, bounds.Y0),
                Math.Max(p.X, bounds.X1), Math.Max(p.Y, bounds.Y1));
        }
    }

    private static SlabEdge? ReadCircularEdge(PlaPlanPoint p, PlaPlanPoint q, double sweep, out double segmentArea)
    {
        segmentArea = 0;
        double dx = q.X - p.X, dy = q.Y - p.Y;
        double chord = double.Hypot(dx, dy), half = sweep * 0.5;
        double radius = chord / (2 * Math.Abs(Math.Sin(half)));
        double offset = 0.5 / Math.Tan(half);
        var center = new PlaPlanPoint(p.X + 0.5 * dx - dy * offset, p.Y + 0.5 * dy + dx * offset);
        if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(center.X) || !double.IsFinite(center.Y))
            return null;
        // Near zero the series avoids subtracting almost equal sine and angle values.
        segmentArea = Math.Abs(sweep) < 1e-3
            ? chord * chord * (sweep / 12 + sweep * sweep * sweep / 360)
            : radius * radius * (sweep - Math.Sin(sweep)) * 0.5;
        return new SlabEdge(p, q, sweep, center, radius);
    }

    private static bool CurvedHoleFits(IReadOnlyList<PlaPlanPoint> ring, SlabEdge[] edges,
        List<IReadOnlyList<PlaPlanPoint>> rings, List<SlabEdge[]> boundaries)
    {
        if (!CurvedRingContainsPoint(rings[0], boundaries[0], ring[0])) return false;
        for (int i = 0; i < boundaries.Count; i++)
        {
            if (i > 0 && (CurvedRingContainsPoint(rings[i], boundaries[i], ring[0]) ||
                CurvedRingContainsPoint(ring, edges, rings[i][0]))) return false;
            foreach (var edge in edges)
                foreach (var other in boundaries[i])
                    if (SlabEdgesIntersect(edge, other, null)) return false;
        }
        return true;
    }

    private static bool SlabRingIsSimple(SlabEdge[] edges)
    {
        for (int i = 0; i < edges.Length; i++)
            for (int j = i + 1; j < edges.Length; j++)
            {
                PlaPlanPoint? shared = j == i + 1 ? edges[i].End : i == 0 && j == edges.Length - 1 ? edges[i].Start : null;
                if (SlabEdgesIntersect(edges[i], edges[j], shared)) return false;
            }
        return true;
    }

    private static bool CurvedRingContainsPoint(IReadOnlyList<PlaPlanPoint> ring, SlabEdge[] edges, PlaPlanPoint point)
    {
        bool inside = ContainsPoint(ring, point);
        foreach (var edge in edges)
        {
            if (edge.Sweep == 0 || double.Hypot(point.X - edge.Center.X, point.Y - edge.Center.Y) >= edge.Radius) continue;
            double side = Cross(edge.Start, edge.End, point);
            // The chord ray test assigns points on the chord to the right, then above for a horizontal edge.
            if (side == 0)
                side = edge.Start.Y == edge.End.Y ? edge.End.X - edge.Start.X : edge.Start.Y - edge.End.Y;
            if (Math.Sign(edge.Sweep) * side < 0) inside = !inside;
        }
        return inside;
    }

    private static bool SlabEdgesIntersect(SlabEdge a, SlabEdge b, PlaPlanPoint? shared)
    {
        if (a.Sweep == 0 && b.Sweep == 0)
        {
            if (shared is not { } vertex) return SegmentsIntersect(a.Start, a.End, b.Start, b.End);
            var x = a.Start == vertex ? a.End : a.Start;
            var y = b.Start == vertex ? b.End : b.Start;
            return Cross(x, vertex, y) == 0 && ((x.X - vertex.X) * (y.X - vertex.X) + (x.Y - vertex.Y) * (y.Y - vertex.Y)) > 0;
        }
        if (a.Sweep == 0 || b.Sweep == 0) return LineIntersectsSlabArc(a.Sweep == 0 ? a : b, a.Sweep == 0 ? b : a, shared);

        double dx = b.Center.X - a.Center.X, dy = b.Center.Y - a.Center.Y;
        double distance = double.Hypot(dx, dy);
        if (distance <= JunctionTolerance)
        {
            if (Math.Abs(a.Radius - b.Radius) > JunctionTolerance) return false;
            double start = Math.Atan2(a.Start.Y - a.Center.Y, a.Start.X - a.Center.X);
            return ArcContains(a, b.Start, shared) || ArcContains(a, b.End, shared) ||
                ArcContains(b, a.Start, shared) || ArcContains(b, a.End, shared) ||
                ArcContains(b, a.AtAngle(start + a.Sweep / 2), shared);
        }
        double ux = dx / distance, uy = dy / distance;
        if (shared is { } endpoint)
        {
            // The second circle intersection mirrors the known common endpoint across the line of centers.
            double projection = (endpoint.X - a.Center.X) * ux + (endpoint.Y - a.Center.Y) * uy;
            var reflected = new PlaPlanPoint(2 * (a.Center.X + projection * ux) - endpoint.X,
                2 * (a.Center.Y + projection * uy) - endpoint.Y);
            return ArcContains(a, reflected, shared) && ArcContains(b, reflected, shared);
        }
        if (distance > a.Radius + b.Radius + JunctionTolerance ||
            distance < Math.Abs(a.Radius - b.Radius) - JunctionTolerance) return false;
        double along = 0.5 * (distance + (a.Radius - b.Radius) * (a.Radius + b.Radius) / distance);
        double offset = Math.Sqrt(Math.Max(0, (a.Radius - along) * (a.Radius + along)));
        double x0 = a.Center.X + along * ux, y0 = a.Center.Y + along * uy;
        return Both(new PlaPlanPoint(x0 - offset * uy, y0 + offset * ux)) ||
            Both(new PlaPlanPoint(x0 + offset * uy, y0 - offset * ux));

        bool Both(PlaPlanPoint point) => a.IncludesDirection(point) && b.IncludesDirection(point);
    }

    private static bool LineIntersectsSlabArc(SlabEdge line, SlabEdge arc, PlaPlanPoint? shared)
    {
        double dx = line.End.X - line.Start.X, dy = line.End.Y - line.Start.Y;
        double length = double.Hypot(dx, dy);
        double ux = dx / length, uy = dy / length;
        double x = arc.Center.X - line.Start.X, y = arc.Center.Y - line.Start.Y;
        double along = x * ux + y * uy;
        if (shared is { } known)
            return WithinLineAndArc(2 * along - (known == line.Start ? 0 : length));

        double across = Math.Abs(x * uy - y * ux);
        if (across > arc.Radius + JunctionTolerance) return false;
        double offset = Math.Sqrt(Math.Max(0, (arc.Radius - across) * (arc.Radius + across)));
        return WithinLineAndArc(along - offset) || WithinLineAndArc(along + offset);

        bool WithinLineAndArc(double t) => t >= -JunctionTolerance && t <= length + JunctionTolerance &&
            ArcContains(arc, new PlaPlanPoint(line.Start.X + t * ux, line.Start.Y + t * uy), shared);
    }

    private static bool ArcContains(SlabEdge arc, PlaPlanPoint point, PlaPlanPoint? shared) =>
        (shared is not { } vertex || double.Hypot(point.X - vertex.X, point.Y - vertex.Y) > JunctionTolerance) &&
        arc.IncludesDirection(point);
}
