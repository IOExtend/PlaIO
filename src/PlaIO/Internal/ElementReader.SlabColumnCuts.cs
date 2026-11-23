using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static double? ReadSlabStructuralLoss(ReadOnlySpan<byte> slab, ReadOnlySpan<byte> settings, Guid id,
        ModelGeometry geometry, PlaBoundingBox box, IReadOnlyList<IReadOnlyList<PlaPlanPoint>> rings,
        out IReadOnlyList<PlaSlabPrism>? prisms)
    {
        prisms = null;
        if (BinaryPrimitives.ReadUInt16LittleEndian(settings[85..]) != 1) return null;
        var skins = BinaryPrimitives.ReadUInt16LittleEndian(settings[83..]) switch
        {
            1 => geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(settings[73..]))
                is > 0 and { } priority ? [new SlabSkin(box.ZMax - box.ZMin, priority)] : null,
            2 => geometry.Attributes.Composite.GetValueOrDefault(BinaryPrimitives.ReadUInt16LittleEndian(settings[87..])),
            _ => null,
        };
        if (skins is null || skins.Any(s => !double.IsFinite(s.Thickness) || s.Thickness <= 0 || s.Priority == 0) ||
            Math.Abs(skins.Sum(s => s.Thickness) - (box.ZMax - box.ZMin)) > JunctionTolerance)
            return null;
        uint minimum = skins.Min(s => s.Priority);
        int? layer = ReadElementLocation(slab, "Slab", 0).LayerIndex;
        var cuts = new List<PlaBoundingBox>();
        IReadOnlyList<IReadOnlyList<PlaPlanPoint>>? evaluated = rings;
        bool resolvedContact = false;
        double removed = 0;
        if (!HasSupportedSlabContacts(slab, Guid.Empty, Guid.Empty, (key, joint) =>
        {
            if (key != SlabColumnRelation && SlabSolidContactKeepsShape(geometry, id, key, joint, minimum))
                return true;
            IReadOnlyList<PlaPlanPoint>? footprint = null;
            bool resolved = key == SlabColumnRelation
                ? ReadSlabColumnCut(geometry, id, layer, joint, box, skins, minimum, rings, out var cut, out double volume, out footprint)
                : ReadSlabProfileWallCut(geometry, id, layer, key, joint, box, skins, rings, out cut, out volume);
            if (!resolved) return false;
            resolvedContact = true;
            if (cut is not null)
            {
                // shortcut: overlapping structural cuts require a union before their volumes can be deducted.
                if (cuts.Any(p => CutBoxesOverlap(p, cut))) return false;
                cuts.Add(cut);
                removed += volume;
                evaluated = evaluated is not null && footprint is not null
                    ? SubtractSlabFootprint(evaluated, [footprint], volume / (box.ZMax - box.ZMin))
                    : null;
            }
            return true;
        })) return null;
        if (resolvedContact && evaluated is not null) prisms = GroupSlabPrisms(evaluated);
        return resolvedContact ? removed : null;
    }

    private static bool ReadSlabColumnCut(ModelGeometry geometry, Guid slabId, int? slabLayer, Guid junction,
        PlaBoundingBox slabBox, SlabSkin[] skins, uint minimum, IReadOnlyList<IReadOnlyList<PlaPlanPoint>> rings,
        out PlaBoundingBox? cut, out double volume, out IReadOnlyList<PlaPlanPoint>? footprint)
    {
        cut = null;
        volume = 0;
        footprint = null;
        if (geometry.Read(junction, SlabColumnJunctionClass) is not { } joint ||
            ReadSingleRelation(joint, SlabColumnRelation, requireSection: true) != slabId)
            return false;
        Guid memoId = ReadSingleRelation(joint, ColumnSlabRelation, requireSection: true);
        var hatch = ReadColumnContactSection(geometry, memoId, ColumnSlabRelation, junction, out var column);
        if (hatch.IsEmpty || geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(hatch[18..]))
            is not (> 0 and { } priority)) return false;
        if (priority < minimum) return true;

        var location = ReadElementLocation(column, "Column", 0);
        if (!SameIntersectionGroup(geometry, location.LayerIndex, slabLayer) ||
            geometry.StoryElevation(location.HomeStoryIndex) is not { } level ||
            !column.Slice(304, ColumnEndPlaneFrame.Length).SequenceEqual(ColumnEndPlaneFrame) ||
            BinaryPrimitives.ReadUInt16LittleEndian(column[368..]) != 1 ||
            !IsHorizontal(column[312..]) || !IsHorizontal(column[370..]) ||
            !(ReadDouble(column, 352) < 0 && ReadDouble(column, 410) > 0) ||
            !IsCorner(column[370..], ReadDouble(column, 312), ReadDouble(column, 320)))
            return false;
        double low = ReadDouble(column, 328) + level, high = ReadDouble(column, 386) + level;
        if (!double.IsFinite(low) || !double.IsFinite(high) || high <= low) return false;
        if (high <= slabBox.ZMin || low >= slabBox.ZMax) return true;
        double thickness = 0, top = slabBox.ZMax;
        foreach (var skin in skins)
        {
            double bottom = top - skin.Thickness;
            if (priority >= skin.Priority)
                thickness += Math.Max(0, Math.Min(top, high) - Math.Max(bottom, low));
            top = bottom;
        }
        if (thickness <= JunctionTolerance) return true;

        if (geometry.Read(memoId, ColumnMemoClass) is not { } memo ||
            ReadColumnSection(memo, ReadSingleRelation(memo, ColumnMemoRelation, requireSection: true), out var contacts) is not { } sectionId ||
            geometry.Read(sectionId, RectangularSectionClass) is not { } section ||
            ReadCenteredRectangle(section, out _) is not { } halfSize)
            return false;
        var settings = AfterCountedString(column, 735);
        if (settings.Length < 95 || ReadDouble(settings, 28) != 0 || !double.IsFinite(ReadDouble(settings, 87)))
            return false;
        double centerX = ReadDouble(column, 312), centerY = ReadDouble(column, 320), angle = ReadDouble(settings, 87);
        if (!double.IsFinite(centerX) || !double.IsFinite(centerY)) return false;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        double x = Math.Abs(cos) * halfSize.X + Math.Abs(sin) * halfSize.Y;
        double y = Math.Abs(sin) * halfSize.X + Math.Abs(cos) * halfSize.Y;
        var envelope = new PlaBoundingBox(centerX - x, centerY - y, low, centerX + x, centerY + y, high);
        if (!CutBoxesOverlap(slabBox, envelope)) return true;

        double area = RectangleIntersectionArea(rings, new PlaPlanPoint(centerX, centerY), angle, halfSize);
        if (!double.IsFinite(area) || area < -JunctionTolerance) return false;
        if (area <= 0) return true;
        var band = new Elevations(Math.Max(low, slabBox.ZMin), Math.Min(high, slabBox.ZMax));
        if (contacts is not null && contacts.Any(c => !ColumnContactKeepsBand(geometry, memoId, junction, c, priority, band)))
            return false;
        cut = envelope with { ZMin = band.Bottom, ZMax = band.Top };
        volume = area * thickness;
        if (low <= slabBox.ZMin && high >= slabBox.ZMax && skins.All(s => s.Priority <= priority))
            footprint = Array.AsReadOnly(new[]
            {
                Corner(-halfSize.X, -halfSize.Y), Corner(-halfSize.X, halfSize.Y),
                Corner(halfSize.X, halfSize.Y), Corner(halfSize.X, -halfSize.Y),
            });
        return double.IsFinite(volume);

        PlaPlanPoint Corner(double u, double v) => new(centerX + (cos * u - sin * v), centerY + (sin * u + cos * v));
    }

    private static bool ColumnContactKeepsBand(ModelGeometry geometry, Guid column, Guid activeJunction,
        (Guid Key, Guid Id) contact, uint priority, Elevations band)
    {
        if (contact.Key == ColumnSlabRelation && contact.Id == activeJunction) return true;
        var (kind, relation) = contact.Key == ColumnBeamRelation ? (BeamColumnJunctionClass, BeamColumnRelation) :
            contact.Key == ColumnWallRelation ? (WallColumnJunctionClass, WallColumnRelation) : (SlabColumnJunctionClass, SlabColumnRelation);
        if (geometry.Read(contact.Id, kind) is not { } joint ||
            ReadSingleRelation(joint, contact.Key, requireSection: true) != column) return false;
        Guid peer = ReadSingleRelation(joint, relation, requireSection: true);
        if (contact.Key == ColumnBeamRelation)
        {
            if (ReadBeamPriority(geometry, peer, contact.Id) is > 0 and { } beamPriority && beamPriority <= priority)
                return true;
            return geometry.Read(peer, BeamMemoClass) is { } memo && HasGeometryRelation(memo, relation, contact.Id) &&
                ReadBeamPrism(geometry, peer, memo, 0) is { } beam &&
                BeamSectionElevations(beam, ReadBeamHatch(geometry, peer, memo)) is { } height &&
                DisjointHeights(height.Bottom, height.Top, band);
        }
        bool isWall = contact.Key == ColumnWallRelation;
        if (geometry.Read(peer, isWall ? WallClass : SlabClass) is not { } body ||
            !HasGeometryRelation(body, relation, contact.Id)) return false;
        var record = body.AsSpan(FindClassFrame(body, isWall ? WallClass : SlabClass));
        var location = ReadElementLocation(record, isWall ? "Wall" : "Slab", 0);
        if (isWall && ReadWallPriority(geometry, peer, body) is > 0 and { } wallPriority && wallPriority <= priority)
            return true;
        if (!isWall && ReadSlabPriority(AfterCountedString(record, ClassElementIdOffset("Slab")), geometry.Attributes)
            is > 0 and { } slabPriority && slabPriority <= priority) return true;
        if (isWall) return false;
        return ReadElevations(record, "Slab") is { } slab && geometry.StoryElevation(location.HomeStoryIndex) is { } level &&
            DisjointHeights(slab.Bottom + level, slab.Top + level, band);
    }

    private static bool CutBoxesOverlap(PlaBoundingBox a, PlaBoundingBox b) =>
        a.XMin < b.XMax && a.XMax > b.XMin && a.YMin < b.YMax && a.YMax > b.YMin && a.ZMin < b.ZMax && a.ZMax > b.ZMin;

    private static double RectangleIntersectionArea(IReadOnlyList<IReadOnlyList<PlaPlanPoint>> rings, PlaPlanPoint center,
        double angle, (double X, double Y) halfSize)
    {
        double sin = Math.Sin(angle), cos = Math.Cos(angle), area = 0;
        foreach (var ring in rings)
        {
            var points = new List<PlaPlanPoint>(ring.Count);
            foreach (var p in ring)
            {
                double x = p.X - center.X, y = p.Y - center.Y;
                points.Add(new PlaPlanPoint(cos * x + sin * y, cos * y - sin * x));
            }
            points = ClipHalfPlane(points, xAxis: true, halfSize.X, keepBelow: true);
            points = ClipHalfPlane(points, xAxis: true, -halfSize.X, keepBelow: false);
            points = ClipHalfPlane(points, xAxis: false, halfSize.Y, keepBelow: true);
            points = ClipHalfPlane(points, xAxis: false, -halfSize.Y, keepBelow: false);
            for (int i = 1; i < points.Count - 1; i++)
                area += Cross(points[0], points[i], points[i + 1]) / 2;
        }
        return area;
    }

    private static List<PlaPlanPoint> ClipHalfPlane(List<PlaPlanPoint> points, bool xAxis, double limit, bool keepBelow)
    {
        if (points.Count == 0) return points;
        var result = new List<PlaPlanPoint>(points.Count + 2);
        var last = points[^1];
        double a = (xAxis ? last.X : last.Y) - limit;
        foreach (var point in points)
        {
            double b = (xAxis ? point.X : point.Y) - limit;
            bool aInside = keepBelow ? a <= 0 : a >= 0, bInside = keepBelow ? b <= 0 : b >= 0;
            if (aInside != bInside)
            {
                double t = a / (a - b);
                result.Add(xAxis ? new PlaPlanPoint(limit, last.Y + t * (point.Y - last.Y)) :
                    new PlaPlanPoint(last.X + t * (point.X - last.X), limit));
            }
            if (bInside) result.Add(point);
            last = point;
            a = b;
        }
        return result;
    }
}
