using IOScene;
using PlaIO.Model;

namespace PlaIO.Scene;

internal static class PlaExtrusionMesh
{
    internal static void Add(SceneDocument scene, long elementId, IReadOnlyList<PlaSlabPrism> prisms,
        double bottom, double top) =>
        Add(scene, elementId, prisms.Select(p => p.Rings), new(bottom, 0, 0), new(top, 0, 0), Transform3d.Identity);

    internal static void Add(SceneDocument scene, long elementId, PlaColumnExtrusion column)
    {
        var frame = Transform3d.Identity with { Translation = new Vector3d(column.Origin.X, column.Origin.Y, 0) };
        if (column.CircularRadius is { } radius)
        {
            if (CircularBoundary(radius) is { } boundary)
                Add(scene, elementId, [[boundary]], column.LowerPlane, column.UpperPlane, frame, convex: true);
            return;
        }
        var contours = column.Sections.Select(section => section.Rings);
        if (column.Spans is null)
            Add(scene, elementId, contours, column.LowerPlane, column.UpperPlane, frame);
        else
            foreach (var span in column.Spans)
                Add(scene, elementId, contours, new(span.Bottom, 0, 0), new(span.Top, 0, 0), frame);
    }

    internal static void Add(SceneDocument scene, long elementId, PlaWallExtrusion wall)
    {
        double dx = wall.End.X - wall.Start.X, dy = wall.End.Y - wall.Start.Y;
        double length = double.Hypot(dx, dy);
        var along = new Vector3d(-dx / length, -dy / length, 0);
        var right = new Vector3d(dy / length, -dx / length, 0);
        // Right and up form a right-handed frame when the cap sweep goes from the wall end to its start.
        var frame = new Transform3d(right, Vector3d.UnitZ, along, new Vector3d(wall.End.X, wall.End.Y, wall.BaseElevation));
        Add(scene, elementId, wall.Sections.Select(section => (IReadOnlyList<IReadOnlyList<PlaPlanPoint>>)
            section.Rings.Select(ring => (IReadOnlyList<PlaPlanPoint>)ring.Select(p => new PlaPlanPoint(p.U, p.Z)).ToArray()).ToArray()),
            new(0, 0, 0), new(length, 0, 0), frame);
    }

    private static void Add(SceneDocument scene, long elementId, IEnumerable<IReadOnlyList<IReadOnlyList<PlaPlanPoint>>> contours,
        PlaColumnEndPlane lower, PlaColumnEndPlane upper, Transform3d frame, bool convex = false)
    {
        var parts = new List<(Vector3d Origin, Vector3d[] Vertices, List<int> Indices)>();
        foreach (var rings in contours)
        {
            if (Tessellate(rings, lower, upper, convex) is not { } part) return;
            parts.Add(part);
        }

        foreach (var (origin, vertices, indices) in parts)
        {
            int id = scene.Geometries.Count + 1;
            scene.Geometries.Add(new SceneGeometry { Id = id, Vertices = vertices, Indices = indices });
            scene.Instances.Add(new SceneInstance
            {
                ElementId = elementId,
                GeometryId = id,
                Transform = frame with { Translation = frame.Apply(origin) },
            });
        }
    }

    private static (Vector3d Origin, Vector3d[] Vertices, List<int> Indices)? Tessellate(
        IReadOnlyList<IReadOnlyList<PlaPlanPoint>> rings, PlaColumnEndPlane lower, PlaColumnEndPlane upper, bool convex)
    {
        var origin = rings[0][0];
        var points = rings.SelectMany(r => r.Select(p => new PlaPlanPoint(p.X - origin.X, p.Y - origin.Y))).ToArray();
        var ends = new int[rings.Count + 1];
        for (int i = 0; i < rings.Count; i++)
            ends[i + 1] = ends[i] + rings[i].Count;

        var boundary = Enumerable.Range(0, ends[1]).ToList();
        var holes = Enumerable.Range(1, rings.Count - 1).Select(ring =>
            (Ring: ring, Vertex: Enumerable.Range(ends[ring], rings[ring].Count).MaxBy(i => points[i].X)));
        foreach (var (ring, vertex) in holes.OrderByDescending(h => points[h.Vertex].X))
            if (!BridgeHole(points, ends, boundary, ring, vertex)) return null;
        if ((convex ? TriangleFan(points.Length) : Triangulate(points, boundary)) is not { } cap) return null;

        int count = points.Length;
        double bottom = HeightAt(lower, origin);
        double height = HeightAt(upper, origin) - bottom;
        var vertices = new Vector3d[2 * count];
        var indices = new List<int>(cap.Count * 2 + count * 6);
        for (int i = 0; i < count; i++)
        {
            var point = points[i];
            vertices[i] = new Vector3d(point.X, point.Y, lower.SlopeX * point.X + lower.SlopeY * point.Y);
            vertices[i + count] = new Vector3d(point.X, point.Y, height + upper.SlopeX * point.X + upper.SlopeY * point.Y);
        }

        for (int ring = 0; ring < rings.Count; ring++)
        {
            for (int i = ends[ring]; i < ends[ring + 1]; i++)
            {
                int next = i + 1 < ends[ring + 1] ? i + 1 : ends[ring];
                indices.Add(i);
                indices.Add(next);
                indices.Add(next + count);
                indices.Add(i);
                indices.Add(next + count);
                indices.Add(i + count);
            }
        }
        for (int i = 0; i < cap.Count; i += 3)
        {
            indices.Add(cap[i] + count);
            indices.Add(cap[i + 1] + count);
            indices.Add(cap[i + 2] + count);
            indices.Add(cap[i + 2]);
            indices.Add(cap[i + 1]);
            indices.Add(cap[i]);
        }

        return (new Vector3d(origin.X, origin.Y, bottom), vertices, indices);
    }

    private static PlaPlanPoint[]? CircularBoundary(double radius)
    {
        const double maximumSagitta = 1e-7;
        // Keep the four axis extrema exact. Use the half-angle form for narrow chords.
        double quarters = Math.Max(4, Math.Ceiling(Math.PI / (8 * Math.Asin(Math.Sqrt(Math.Min(0.5, maximumSagitta / radius / 2))))));
        if (!(quarters <= 4096)) return null;
        int steps = (int)quarters;
        var ring = new PlaPlanPoint[4 * steps];
        for (int i = 0; i < steps; i++)
        {
            var (sin, cos) = Math.SinCos(Math.PI / 2 * i / steps);
            double x = radius * cos, y = radius * sin;
            ring[i] = new PlaPlanPoint(x, y);
            ring[steps + i] = new PlaPlanPoint(-y, x);
            ring[2 * steps + i] = new PlaPlanPoint(-x, -y);
            ring[3 * steps + i] = new PlaPlanPoint(y, -x);
        }
        return ring;
    }

    private static List<int> TriangleFan(int count)
    {
        var triangles = new List<int>((count - 2) * 3);
        for (int i = 1; i + 1 < count; i++)
        {
            triangles.Add(0);
            triangles.Add(i);
            triangles.Add(i + 1);
        }
        return triangles;
    }

    // Open each hole with two opposed edges. They share vertex IDs and disappear inside the triangulated cap.
    private static bool BridgeHole(PlaPlanPoint[] points, int[] ends, List<int> boundary, int ring, int vertex)
    {
        var hole = points[vertex];
        int best = -1;
        double bestDistance = double.PositiveInfinity;
        for (int i = 0; i < boundary.Count; i++)
        {
            var target = points[boundary[i]];
            double distance = double.Hypot(target.X - hole.X, target.Y - hole.Y);
            if (distance >= bestDistance) continue;

            // A bridge endpoint can occur twice in the boundary. Only one occurrence faces the new hole.
            var previous = points[boundary[(i + boundary.Count - 1) % boundary.Count]];
            var next = points[boundary[(i + 1) % boundary.Count]];
            double left = Cross(previous, target, hole), right = Cross(target, next, hole);
            bool facesHole = Cross(previous, target, next) >= 0 ? left >= 0 && right >= 0 : left > 0 || right > 0;
            if (facesHole && IsVisible(points, ends, boundary, hole, target))
            {
                best = i;
                bestDistance = distance;
            }
        }
        if (best < 0) return false;

        int length = ends[ring + 1] - ends[ring];
        var path = new int[length + 2];
        for (int i = 0; i <= length; i++)
            path[i] = ends[ring] + (vertex - ends[ring] + i) % length;
        path[^1] = boundary[best];
        boundary.InsertRange(best + 1, path);
        return true;
    }

    private static bool IsVisible(PlaPlanPoint[] points, int[] ends, List<int> boundary, PlaPlanPoint a, PlaPlanPoint b)
    {
        var midpoint = new PlaPlanPoint(a.X + (b.X - a.X) / 2, a.Y + (b.Y - a.Y) / 2);
        for (int ring = 0; ring < ends.Length - 1; ring++)
        {
            bool inside = false;
            for (int i = ends[ring], last = ends[ring + 1] - 1; i < ends[ring + 1]; last = i++)
            {
                var c = points[last];
                var d = points[i];
                if (BlocksBridge(a, b, c, d)) return false;
                if ((c.Y > midpoint.Y) != (d.Y > midpoint.Y) &&
                    midpoint.X < c.X + (midpoint.Y - c.Y) * (d.X - c.X) / (d.Y - c.Y))
                    inside = !inside;
            }
            if (inside != (ring == 0)) return false;
        }
        // The joined boundary also contains the previously added bridges.
        for (int i = 0; i < boundary.Count; i++)
            if (BlocksBridge(a, b, points[boundary[i]], points[boundary[(i + 1) % boundary.Count]])) return false;
        return true;
    }

    private static bool BlocksBridge(PlaPlanPoint a, PlaPlanPoint b, PlaPlanPoint c, PlaPlanPoint d)
    {
        if (a == c || a == d) return SharesRay(a, b, a == c ? d : c);
        if (b == c || b == d) return SharesRay(b, a, b == c ? d : c);
        if (Math.Max(a.X, b.X) < Math.Min(c.X, d.X) || Math.Max(c.X, d.X) < Math.Min(a.X, b.X) ||
            Math.Max(a.Y, b.Y) < Math.Min(c.Y, d.Y) || Math.Max(c.Y, d.Y) < Math.Min(a.Y, b.Y)) return false;
        return Opposite(Cross(a, b, c), Cross(a, b, d)) && Opposite(Cross(c, d, a), Cross(c, d, b));

        static bool SharesRay(PlaPlanPoint origin, PlaPlanPoint p, PlaPlanPoint q) =>
            Cross(origin, p, q) == 0 && (p.X - origin.X) * (q.X - origin.X) + (p.Y - origin.Y) * (q.Y - origin.Y) > 0;

        static bool Opposite(double x, double y) => (x <= 0 && y >= 0) || (x >= 0 && y <= 0);
    }

    private static List<int>? Triangulate(PlaPlanPoint[] points, List<int> remaining)
    {
        var triangles = new List<int>((remaining.Count - 2) * 3);
        int i = 0, rejected = 0;
        while (remaining.Count > 2)
        {
            int previous = remaining[(i + remaining.Count - 1) % remaining.Count];
            int current = remaining[i];
            int next = remaining[(i + 1) % remaining.Count];
            var a = points[previous];
            var b = points[current];
            var c = points[next];
            if (Cross(a, b, c) > 0 && !remaining.Any(p => p != previous && p != current && p != next &&
                Cross(a, b, points[p]) >= 0 && Cross(b, c, points[p]) >= 0 && Cross(c, a, points[p]) >= 0))
            {
                triangles.Add(previous);
                triangles.Add(current);
                triangles.Add(next);
                remaining.RemoveAt(i);
                rejected = 0;
            }
            else
            {
                if (++rejected == remaining.Count) return null;
                i++;
            }
            i %= remaining.Count;
        }
        return triangles;
    }

    private static double Cross(PlaPlanPoint a, PlaPlanPoint b, PlaPlanPoint p) =>
        (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

    private static double HeightAt(PlaColumnEndPlane plane, PlaPlanPoint point) =>
        plane.Elevation + plane.SlopeX * point.X + plane.SlopeY * point.Y;
}
