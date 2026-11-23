using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static bool ReadSlabProfileWallCut(ModelGeometry geometry, Guid slab, int? slabLayer, Guid role, Guid contact,
        PlaBoundingBox slabBox, SlabSkin[] skins, IReadOnlyList<IReadOnlyList<PlaPlanPoint>> slabRings,
        out PlaBoundingBox? cut, out double volume)
    {
        cut = null;
        volume = 0;
        if (role != SlabWallRelation && role != AlternateSlabWallRelation ||
            geometry.Read(contact, WallSlabJunctionClass) is not { } joint ||
            ReadSingleRelation(joint, role, requireSection: true) != slab)
            return false;
        Guid wall = ReadSingleRelation(joint, WallSlabRelation, requireSection: true);
        if (geometry.Read(wall, WallClass) is not { } body || !HasOnlyProfileWallSlabContact(body, contact)) return false;
        var record = body.AsSpan(FindClassFrame(body, WallClass));
        var settings = AfterCountedString(record, 197);
        var location = ReadElementLocation(record, "Wall", 0);
        if (ReadSavedWallBox(settings) is not { } wallBox || settings[184] != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(settings[225..]) != 3 ||
            BinaryPrimitives.ReadUInt16LittleEndian(settings[227..]) != 1 ||
            !SameIntersectionGroup(geometry, slabLayer, location.LayerIndex) ||
            geometry.StoryElevation(location.HomeStoryIndex) is not { } story ||
            ReadWallReferencePlanes(settings, location, geometry) is not { } wallPlanes ||
            ReadWallCutRegions(ReadWallProfileVector(geometry, wall, body), geometry) is not { } regions)
            return false;

        double x = ReadDouble(settings, 0), y = ReadDouble(settings, 8);
        double dx = ReadDouble(settings, 16) - x, dy = ReadDouble(settings, 24) - y;
        double length = Math.Sqrt(dx * dx + dy * dy), baseZ = story + wallPlanes.Bottom;
        if (!double.IsFinite(length) || length <= 0) return false;
        double alongX = dx / length, alongY = dy / length;
        double min = regions.Min(r => r.Ring.Min(p => p.X)), max = regions.Max(r => r.Ring.Max(p => p.X));
        double minZ = baseZ + regions.Min(r => r.Ring.Min(p => p.Y)), maxZ = baseZ + regions.Max(r => r.Ring.Max(p => p.Y));
        if (!MatchesWallExtent(wallBox.XMin, wallBox.XMax, x, dx, alongY, min, max) ||
            !MatchesWallExtent(wallBox.YMin, wallBox.YMax, y, dy, -alongX, min, max) ||
            !(Math.Abs(minZ - wallBox.ZMin) <= JunctionTolerance && Math.Abs(maxZ - wallBox.ZMax) <= JunctionTolerance &&
                Math.Abs(maxZ - (story + wallPlanes.Top)) <= JunctionTolerance))
            return false;
        if (!CutBoxesOverlap(slabBox, wallBox)) return true;

        var plan = new List<List<PlaPlanPoint>>(slabRings.Count);
        foreach (var ring in slabRings)
        {
            var local = ring.Select(p => new PlaPlanPoint(
                (p.X - x) * alongY - (p.Y - y) * alongX,
                (p.X - x) * alongX + (p.Y - y) * alongY)).ToList();
            plan.Add(ClipHalfPlane(ClipHalfPlane(local, xAxis: false, 0, keepBelow: false), xAxis: false, length, keepBelow: true));
        }

        double top = slabBox.ZMax - baseZ, cutLow = double.PositiveInfinity, cutHigh = double.NegativeInfinity;
        foreach (var skin in skins)
        {
            double low = top - skin.Thickness;
            var filled = new List<(double X0, double Y0, double X1, double Y1)>();
            foreach (var region in regions)
            {
                if (region.Priority <= skin.Priority) continue;
                var part = ClipHalfPlane(ClipHalfPlane(region.Ring, xAxis: false, low, keepBelow: false),
                    xAxis: false, top, keepBelow: true);
                if (part.Count < 3 || SignedRingArea(part) == 0) continue;
                var bounds = (X0: part.Min(p => p.X), Y0: part.Min(p => p.Y),
                    X1: part.Max(p => p.X), Y1: part.Max(p => p.Y));
                // shortcut: overlapping material regions need a section union before volumes can be added.
                if (filled.Any(r => !OpeningPlansDisjoint(r, bounds))) return false;
                double overlap = IntegrateWallCut(plan, part, bounds.X0, bounds.X1);
                if (!double.IsFinite(overlap) || overlap < -JunctionTolerance) return false;
                if (overlap <= 0) continue;
                filled.Add(bounds);
                volume += overlap;
                cutLow = Math.Min(cutLow, bounds.Y0 + baseZ);
                cutHigh = Math.Max(cutHigh, bounds.Y1 + baseZ);
            }
            top = low;
        }
        if (volume > 0) cut = wallBox with { ZMin = cutLow, ZMax = cutHigh };
        return double.IsFinite(volume);
    }

    private static bool MatchesWallExtent(double min, double max, double origin, double along, double normal, double u0, double u1) =>
        Math.Abs(min - (origin + Math.Min(0, along) + Math.Min(normal * u0, normal * u1))) <= JunctionTolerance &&
        Math.Abs(max - (origin + Math.Max(0, along) + Math.Max(normal * u0, normal * u1))) <= JunctionTolerance;

    private static bool HasOnlyProfileWallSlabContact(ReadOnlySpan<byte> wall, Guid contact)
    {
        int start = FindRelationshipSection(wall, wall.Length);
        if (start < 0) return false;
        var data = wall[start..];
        bool found = false;
        while (data.Length >= 17)
        {
            if (!TryReadGeometryRelation(ref data, out byte tag, out Guid key, out Guid target)) return false;
            if (tag == 0x11 && key == WallSlabRelation && target == contact && !found) found = true;
            else if ((tag is 1 or 0x11) && key == DrawingRelation ||
                tag == 0x11 && (UnjoinedWallChildRelations.Contains(key) || key == WallProfileRelation || key == LinkedElementSettingsRelation) ||
                tag == 0x12 && (UnjoinedWallOwnerRelations.Contains(key) || key == DatabaseElementList ||
                    key == AttributeOwnerRelation || key == WallTopStoryRelation)) continue;
            else return false;
        }
        return found && data is [0];
    }

    private static List<(uint Priority, List<PlaPlanPoint> Ring)>? ReadWallCutRegions(ReadOnlySpan<byte> vector, ModelGeometry geometry)
    {
        var items = ReadFirstWallHatch(vector, out uint count);
        if (items.IsEmpty) return null;
        var regions = new List<(uint, List<PlaPlanPoint>)>();
        int totalPoints = 0;
        for (int i = 0; i < count; i++)
        {
            int size = ReadHatchSize(items, 2);
            if (size == 0) return null;
            var hatch = items[..size];
            var vertices = StraightSectionVertices(hatch);
            if (vertices.IsEmpty ||
                geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(hatch[18..])) is not (> 0 and { } priority))
                return null;
            totalPoints += vertices.Length / 16;
            if (totalPoints > 4096) return null;
            var ring = new List<PlaPlanPoint>(vertices.Length / 16);
            for (int j = 0; j < vertices.Length; j += 16)
            {
                double u = ReadDouble(vertices, j), z = ReadDouble(vertices, j + 8);
                if (!double.IsFinite(u) || !double.IsFinite(z)) return null;
                ring.Add(new PlaPlanPoint(u, z));
            }
            double area = SignedRingArea(ring);
            if (!double.IsFinite(area) || area == 0 || !IsSimpleRing(ring)) return null;
            if (area < 0) ring.Reverse();
            regions.Add((priority, ring));
            items = items[size..];
            if (i + 1 < count)
            {
                if (items.Length < 16) return null;
                items = items[16..];
            }
        }
        return items.IsEmpty ? regions : null;
    }

    private static double SignedRingArea(IReadOnlyList<PlaPlanPoint> ring)
    {
        double area = 0;
        for (int i = 1; i < ring.Count - 1; i++)
            area += Cross(ring[0], ring[i], ring[i + 1]) * 0.5;
        return area;
    }

    private static double IntegrateWallCut(List<List<PlaPlanPoint>> plan, List<PlaPlanPoint> section, double min, double max)
    {
        var stations = plan.SelectMany(r => r).Select(p => p.X).Where(u => u > min && u < max)
            .Concat(section.Select(p => p.X)).Distinct().Order().ToArray();
        double volume = 0;
        for (int i = 1; i < stations.Length; i++)
        {
            double half = (stations[i] - stations[i - 1]) * 0.5;
            double mid = stations[i - 1] + half, offset = half / Math.Sqrt(3);
            double left = mid - offset, right = mid + offset;
            // Each polygon span is linear between vertex stations, so two-point quadrature integrates their product exactly.
            volume += half * (plan.Sum(r => SectionSpan(r, left)) * SectionSpan(section, left) +
                plan.Sum(r => SectionSpan(r, right)) * SectionSpan(section, right));
        }
        return volume;
    }

    private static double SectionSpan(List<PlaPlanPoint> ring, double x)
    {
        double span = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            if ((a.X > x) == (b.X > x)) continue;
            double height = a.Y + (x - a.X) * (b.Y - a.Y) / (b.X - a.X);
            span += a.X > b.X ? height : -height;
        }
        return span;
    }
}
