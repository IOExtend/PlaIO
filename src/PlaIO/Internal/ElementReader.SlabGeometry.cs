using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static readonly Guid AlternateSlabWallRelation = new("29bce22c-b910-400a-b84a-0b85fb92610e");

    private readonly record struct SlabOpeningSources(Guid[] Openings, bool CanReconstructSolid);

    private static bool HasSupportedSlabReferencePlane(ReadOnlySpan<byte> settings) =>
        settings.Length >= 25 && (settings[8] is 0 or 3 || settings[8] == 1 && settings.Length >= 89 &&
            BinaryPrimitives.ReadUInt16LittleEndian(settings[83..]) == 2 &&
            BinaryPrimitives.ReadUInt16LittleEndian(settings[85..]) == 1);

    private static PlaSlabGeometry? ReadSlabGeometry(ReadOnlySpan<byte> record, Guid slab, ModelGeometry geometry, PlaBoundingBox box,
        out SlabOpeningSources? sources)
    {
        sources = null;
        if (box.ZMax <= box.ZMin) return null;
        var settings = AfterCountedString(record, ClassElementIdOffset("Slab"));
        var polygon = ReadSlabOutline(settings);
        // The box reader has already validated the frame, extents and contour table.
        if (BinaryPrimitives.ReadUInt32LittleEndian(polygon[47..]) > 0)
            return ReadCurvedSlabGeometry(polygon, box) is { } curved ? WithFloorPlanPerimeters(curved) : null;
        if (ReadStraightRings(polygon, out double area) is not { } rings) return null;
        int points = (int)BinaryPrimitives.ReadUInt32LittleEndian(polygon[43..]);
        int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(polygon[4..]);
        double volume = area * (box.ZMax - box.ZMin);
        bool vertical = HasVerticalSlabEdges(polygon[size..], points - rings.Count);
        var openings = vertical ? ReadSlabOpeningSources(record, slab, geometry) : null;
        bool hasVerifiedOpenings = openings is { Length: > 0 };
        bool solid = double.IsFinite(volume) && volume > 0 && vertical &&
            SlabContactsPreservePrism(record, settings, slab, geometry, hasVerifiedOpenings);
        if (openings is not null) sources = new SlabOpeningSources(openings, solid);
        double? verifiedVolume = solid && !hasVerifiedOpenings ? volume : null;
        bool uncut = solid && openings is { Length: 0 };
        IReadOnlyList<PlaSlabPrism>? prisms = uncut ? Array.AsReadOnly(new[] { new PlaSlabPrism(rings) }) : null;
        if (verifiedVolume is null && vertical && openings is { Length: 0 } &&
            double.IsFinite(volume) &&
            ReadSlabStructuralLoss(record, settings, slab, geometry, box, rings, out var evaluated) is { } removed &&
            removed >= 0 && volume > removed)
        {
            verifiedVolume = volume - removed;
            prisms = evaluated;
        }
        return WithFloorPlanPerimeters(new PlaSlabGeometry(rings, box.ZMin, box.ZMax, area, verifiedVolume)
        {
            IsUncutVerticalPrism = uncut,
            VerticalPrisms = prisms,
        });
    }

    private static PlaSlabGeometry WithFloorPlanPerimeters(PlaSlabGeometry slab)
    {
        double total = 0, holes = 0;
        for (int i = 0; i < slab.Rings.Count; i++)
        {
            var ring = slab.Rings[i];
            double length = 0;
            for (int j = 0; j < ring.Count; j++)
                length += Distance(ring[j], ring[(j + 1) % ring.Count]);
            total += length;
            if (i != 0) holes += length;
        }
        foreach (var arc in slab.Arcs ?? [])
        {
            var ring = slab.Rings[arc.RingIndex];
            int i = arc.StartVertexIndex;
            double chord = Distance(ring[i], ring[(i + 1) % ring.Count]);
            double halfSweep = Math.Abs(arc.SweepRadians) / 2;
            double extra = chord * (halfSweep / Math.Sin(halfSweep) - 1);
            total += extra;
            if (arc.RingIndex != 0) holes += extra;
        }
        return double.IsFinite(total) && double.IsFinite(holes)
            ? slab with { FloorPlanPerimeter = total, FloorPlanHolesPerimeter = holes }
            : slab;
    }

    private static double Distance(PlaPlanPoint a, PlaPlanPoint b) => double.Hypot(b.X - a.X, b.Y - a.Y);

    // The caller validates the contour frame before reading its rings.
    private static IReadOnlyList<IReadOnlyList<PlaPlanPoint>>? ReadStraightRings(ReadOnlySpan<byte> polygon, out double area)
    {
        area = 0;
        if (BinaryPrimitives.ReadUInt32LittleEndian(polygon[47..]) != 0) return null;
        int points = (int)BinaryPrimitives.ReadUInt32LittleEndian(polygon[43..]);
        int ringCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(polygon[51..]);
        if (points > 4096) return null;

        var rings = new List<IReadOnlyList<PlaPlanPoint>>(ringCount);
        int endTable = 71 + points * 16;
        int start = 0;
        for (int ringIndex = 1; ringIndex <= ringCount; ringIndex++)
        {
            int end = (int)BinaryPrimitives.ReadUInt32LittleEndian(polygon[(endTable + ringIndex * 4)..]);
            var ring = ReadSlabRing(polygon, start, end);
            start = end;

            double signedArea = 0;
            for (int i = 1; i < ring.Length - 1; i++)
                signedArea += Cross(ring[0], ring[i], ring[i + 1]) * 0.5;
            if (!double.IsFinite(signedArea) || signedArea == 0 || !IsSimpleRing(ring)) return null;
            bool outer = ringIndex == 1;
            if (!outer && !IsSeparateHole(rings, ring)) return null;
            area += outer ? Math.Abs(signedArea) : -Math.Abs(signedArea);
            if ((signedArea > 0) != outer) Array.Reverse(ring);
            rings.Add(Array.AsReadOnly(ring));
        }

        return double.IsFinite(area) && area > 0 ? rings.AsReadOnly() : null;
    }

    private static PlaPlanPoint[] ReadSlabRing(ReadOnlySpan<byte> polygon, int start, int end)
    {
        var ring = new PlaPlanPoint[end - start - 1];
        for (int i = 0; i < ring.Length; i++)
        {
            int offset = 71 + (start + i) * 16;
            ring[i] = new PlaPlanPoint(ReadDouble(polygon, offset), ReadDouble(polygon, offset + 8));
        }
        return ring;
    }

    private static bool SlabContactsPreservePrism(ReadOnlySpan<byte> record, ReadOnlySpan<byte> settings, Guid slab,
        ModelGeometry geometry, bool hasVerifiedOpenings)
    {
        ushort structure = BinaryPrimitives.ReadUInt16LittleEndian(settings[83..]);
        if (BinaryPrimitives.ReadUInt16LittleEndian(settings[85..]) != 1 || structure is not (1 or 2))
            return false;
        uint? priority = structure == 1
            ? geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(settings[73..]))
            : geometry.Attributes.Composite.GetValueOrDefault(BinaryPrimitives.ReadUInt16LittleEndian(settings[87..]))
                ?.Min(skin => skin.Priority);
        return HasSupportedSlabContacts(record, Guid.Empty, Guid.Empty,
            (key, id) => hasVerifiedOpenings && (key == CutElementRelation || key == OpeningHostRelation) ||
                SlabSolidContactKeepsShape(geometry, slab, key, id, priority));
    }

    private static Guid[]? ReadSlabOpeningSources(ReadOnlySpan<byte> slab, Guid id, ModelGeometry geometry)
    {
        if (!TryReadRelationTargets(slab, CutElementRelation, out var cuts) ||
            !TryReadRelationTargets(slab, OpeningHostRelation, out var hosted))
            return null;
        var sources = new SortedSet<Guid>();
        for (int i = 0; i < cuts.Length; i += 16)
        {
            var (owner, target) = ReadOpeningCut(geometry, new Guid(cuts.Slice(i, 16)));
            if (owner == Guid.Empty || target != id || !sources.Add(owner)) return null;
        }
        for (int i = 0; i < hosted.Length; i += 16)
            if (!sources.Contains(new Guid(hosted.Slice(i, 16)))) return null;
        return sources.ToArray();
    }

    private static PlaSlabGeometry ApplySlabOpenings(PlaSlabGeometry slab, Guid id, SlabOpeningSources? sources,
        Dictionary<Guid, OpeningLinks> links, Dictionary<Guid, PlaOpeningGeometry?> geometry,
        Dictionary<Guid, BasicParameters> parameters)
    {
        if (sources is not { } saved) return slab;
        double topArea = slab.OutlineArea, bottomArea = slab.OutlineArea;
        double removedVolume = 0;
        IReadOnlyList<IReadOnlyList<PlaPlanPoint>>? evaluated =
            saved.CanReconstructSolid && saved.Openings.Length > 0 ? slab.Rings : null;
        foreach (Guid opening in saved.Openings)
        {
            if (parameters.GetValueOrDefault(opening).OpeningProfile is null ||
                links.GetValueOrDefault(opening).Targets?.Contains(id) != true ||
                geometry.GetValueOrDefault(opening) is not { Cuts: { } cuts, CutVolume: { } volume } cutGeometry)
                return slab;

            double sectionArea = volume / cuts.Sum(c => c.Top - c.Bottom);
            if (!double.IsFinite(sectionArea) || sectionArea <= 0) return slab;
            double depth = 0;
            bool bottom = false, top = false, through = false;
            foreach (var cut in cuts)
            {
                if (cut.SlabGuid != id) continue;
                if (cut.Bottom < slab.Bottom - JunctionTolerance || cut.Top > slab.Top + JunctionTolerance)
                    return slab;
                depth += cut.Top - cut.Bottom;
                through |= cut.Bottom <= slab.Bottom && cut.Top >= slab.Top;
                bottom |= Math.Abs(cut.Bottom - slab.Bottom) <= JunctionTolerance;
                top |= Math.Abs(cut.Top - slab.Top) <= JunctionTolerance;
            }
            if (bottom) bottomArea -= sectionArea;
            if (top) topArea -= sectionArea;
            removedVolume += sectionArea * depth;
            if (depth > 0 && evaluated is not null)
                evaluated = through && cutGeometry.Rings is { Count: > 0 } rings
                    ? SubtractSlabFootprint(evaluated,
                        rings.Select(r => (IReadOnlyList<PlaPlanPoint>)r.Reverse().ToArray()).ToArray(), sectionArea)
                    : null;
        }
        if (!(bottomArea >= 0 && topArea >= 0)) return slab;
        double candidate = slab.OutlineArea * (slab.Top - slab.Bottom) - removedVolume;
        bool hasVolume = saved.CanReconstructSolid && double.IsFinite(candidate) && candidate > 0;
        return slab with
        {
            TopAreaWithOpenings = topArea,
            BottomAreaWithOpenings = bottomArea,
            Volume = hasVolume ? candidate : slab.Volume,
            VerticalPrisms = hasVolume && evaluated is not null ? GroupSlabPrisms(evaluated) : slab.VerticalPrisms,
        };
    }

    private static bool HasVerticalSlabEdges(ReadOnlySpan<byte> data, int count)
    {
        if (data.Length < 16 || !data[8..].StartsWith(new byte[] { 1, 0, 1, 0 }) ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[12..]) != count || count > (data.Length - 16) / 27)
            return false;
        for (int i = 0; i < count; i++)
        {
            var edge = data[(16 + i * 27)..];
            if (BinaryPrimitives.ReadUInt32LittleEndian(edge[4..]) != 2 ||
                ReadDouble(edge, 8) != Math.PI / 2 || BinaryPrimitives.ReadUInt16LittleEndian(edge[16..]) != 3)
                return false;
        }
        return true;
    }

    private static bool SlabSolidContactKeepsShape(ModelGeometry geometry, Guid slab, Guid key, Guid id, uint? priority)
    {
        if (key == SlabLevelDimensionRelation)
            return geometry.Read(id, SlabLevelDimensionClass) is { } dimension &&
                ReadSingleRelation(dimension, key, requireSection: true) == slab;
        if (priority is not > 0) return false;
        bool wallContact = key == SlabWallRelation || key == AlternateSlabWallRelation;
        var (type, peerRole) = wallContact ? (WallSlabJunctionClass, WallSlabRelation) :
            key == SlabBeamRelation ? (BeamSlabJunctionClass, BeamSlabRelation) : default;
        if (type == Guid.Empty || geometry.Read(id, type) is not { } joint ||
            ReadSingleRelation(joint, key, requireSection: true) != slab) return false;
        Guid peer = ReadSingleRelation(joint, peerRole, requireSection: true);
        if (wallContact)
            return geometry.Read(peer, WallClass) is { } wall && HasGeometryRelation(wall, peerRole, id) &&
                ReadWallPriority(geometry, peer, wall) is > 0 and { } wallPriority && wallPriority <= priority;
        if (geometry.Read(peer, BeamMemoClass) is not { } memo || !HasGeometryRelation(memo, peerRole, id))
            return false;
        var hatch = ReadBeamHatch(geometry, peer, memo);
        return !hatch.IsEmpty && geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(hatch[18..]))
            is > 0 and { } beamPriority && beamPriority < priority;
    }

    private static bool IsSimpleRing(IReadOnlyList<PlaPlanPoint> ring)
    {
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            var c = ring[(i + 2) % ring.Count];
            if (a == b || Cross(a, b, c) == 0 && ((a.X - b.X) * (c.X - b.X) + (a.Y - b.Y) * (c.Y - b.Y)) > 0)
                return false;
            int end = i == 0 ? ring.Count - 1 : ring.Count;
            for (int j = i + 2; j < end; j++)
                if (SegmentsIntersect(a, b, ring[j], ring[(j + 1) % ring.Count])) return false;
        }
        return true;
    }

    private static bool IsSeparateHole(IReadOnlyList<IReadOnlyList<PlaPlanPoint>> rings, IReadOnlyList<PlaPlanPoint> hole)
    {
        if (!ContainsPoint(rings[0], hole[0])) return false;
        for (int i = 0; i < rings.Count; i++)
        {
            var other = rings[i];
            if (i > 0 && (ContainsPoint(other, hole[0]) || ContainsPoint(hole, other[0]))) return false;
            for (int j = 0; j < hole.Count; j++)
                for (int k = 0; k < other.Count; k++)
                    if (SegmentsIntersect(hole[j], hole[(j + 1) % hole.Count], other[k], other[(k + 1) % other.Count]))
                        return false;
        }
        return true;
    }

    private static bool ContainsPoint(IReadOnlyList<PlaPlanPoint> ring, PlaPlanPoint p)
    {
        bool inside = false;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < a.X + (p.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y))
                inside = !inside;
        }
        return inside;
    }

    private static bool SegmentsIntersect(PlaPlanPoint a, PlaPlanPoint b, PlaPlanPoint c, PlaPlanPoint d)
    {
        if (Math.Max(a.X, b.X) < Math.Min(c.X, d.X) || Math.Max(c.X, d.X) < Math.Min(a.X, b.X) ||
            Math.Max(a.Y, b.Y) < Math.Min(c.Y, d.Y) || Math.Max(c.Y, d.Y) < Math.Min(a.Y, b.Y)) return false;
        double abC = Cross(a, b, c), abD = Cross(a, b, d);
        double cdA = Cross(c, d, a), cdB = Cross(c, d, b);
        return !double.IsFinite(abC) || !double.IsFinite(abD) || !double.IsFinite(cdA) || !double.IsFinite(cdB) ||
            (abC <= 0 && abD >= 0 || abD <= 0 && abC >= 0) && (cdA <= 0 && cdB >= 0 || cdB <= 0 && cdA >= 0);
    }

    private static double Cross(PlaPlanPoint a, PlaPlanPoint b, PlaPlanPoint c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
}
