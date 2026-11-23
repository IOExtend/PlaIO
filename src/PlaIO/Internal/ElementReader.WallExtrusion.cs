using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static readonly Guid ZoneBoundaryCalculationClass = new("ea57dcc5-7231-45df-83dc-792083fc4699");
    private static readonly Guid ZoneCalculationRelation = new("cc4c5518-9f96-4272-8130-5d61c5a351e6");

    private static bool WallBoundsZones(ModelGeometry geometry, Guid wall, ReadOnlySpan<byte> pairs)
    {
        if (pairs.IsEmpty || pairs.Length % 32 != 0) return false;
        for (int i = 0; i < pairs.Length; i += 32)
        {
            var zone = new Guid(pairs.Slice(i, 16));
            var calculation = new Guid(pairs.Slice(i + 16, 16));
            if (geometry.Read(zone, ZoneClass) is null ||
                geometry.Read(calculation, ZoneBoundaryCalculationClass) is not { Length: >= 60 } body ||
                FindRelationshipSection(body, body.Length) != body.Length - 54)
                return false;
            var relationship = body.AsSpan(body.Length - 54);
            // The calculation row uses the same relation as the wall, with the ordered Zone and Wall owners.
            if (relationship[0] != 4 || new Guid(relationship[1..17]) != ZoneCalculationRelation ||
                BinaryPrimitives.ReadUInt32LittleEndian(relationship[17..]) != 2 || new Guid(relationship[21..37]) != zone ||
                new Guid(relationship[37..53]) != wall || relationship[53] != 0)
                return false;
        }
        return true;
    }

    private static PlaWallExtrusion? ReadWallExtrusion(ReadOnlySpan<byte> settings, PlaWallReferencePlan? plan,
        PlaWallProfile? profile, Elevations planes, double home, PlaBoundingBox box)
    {
        if (settings[184] != 0 || BinaryPrimitives.ReadUInt16LittleEndian(settings[227..]) != 1) return null;
        var start = new PlaPlanPoint(ReadDouble(settings, 0), ReadDouble(settings, 8));
        var end = new PlaPlanPoint(ReadDouble(settings, 16), ReadDouble(settings, 24));

        IReadOnlyList<IReadOnlyList<PlaPlanPoint>>? rings;
        if (plan is { } rectangle)
        {
            double low = Math.Min(rectangle.OutsideOffset, rectangle.InsideOffset);
            double high = Math.Max(rectangle.OutsideOffset, rectangle.InsideOffset);
            double height = planes.Top - planes.Bottom;
            rings = [[new(low, 0), new(high, 0), new(high, height), new(low, height)]];
        }
        else
        {
            rings = profile is not null ? ReadStraightProfileSection(profile.Components) : null;
        }
        return rings is null ? null : BuildWallExtrusion(start, end, planes.Bottom + home, rings, box);
    }

    private static PlaWallExtrusion? ReadDoorCutWallExtrusion(PlaWallReferencePlan plan, Elevations planes, double home,
        PlaBoundingBox box, List<(double Station, double Width, double Bottom, double Top)> apertures)
    {
        double dx = plan.End.X - plan.Start.X, dy = plan.End.Y - plan.Start.Y;
        double length = double.Hypot(dx, dy), height = planes.Top - planes.Bottom;
        if (!(length > JunctionTolerance) || !double.IsFinite(length) || !(height > JunctionTolerance)) return null;

        // Sweep the perforated elevation across the thickness to keep each cut face in the wall boundary.
        IReadOnlyList<IReadOnlyList<PlaPlanPoint>> rings = [[new(0, 0), new(length, 0), new(length, height), new(0, height)]];
        foreach (var (station, width, bottom, top) in apertures)
        {
            double left = station - width / 2, right = station + width / 2;
            double low = Math.Max(0, bottom - planes.Bottom), high = Math.Min(height, top - planes.Bottom);
            if (!(left > JunctionTolerance && right < length - JunctionTolerance && right > left &&
                high - low > JunctionTolerance) ||
                CombinePlanarContours(rings, [[new(left, low), new(left, high), new(right, high), new(right, low)]],
                    subtract: true) is not { } remainder)
                return null;
            rings = remainder;
        }

        double max = Math.Max(plan.OutsideOffset, plan.InsideOffset), min = Math.Min(plan.OutsideOffset, plan.InsideOffset);
        double rx = dy / length, ry = -dx / length;
        return BuildWallExtrusion(new(plan.Start.X + rx * max, plan.Start.Y + ry * max),
            new(plan.Start.X + rx * min, plan.Start.Y + ry * min), planes.Bottom + home, rings, box);
    }

    private static PlaWallExtrusion? BuildWallExtrusion(PlaPlanPoint start, PlaPlanPoint end, double baseElevation,
        IReadOnlyList<IReadOnlyList<PlaPlanPoint>> rings, PlaBoundingBox box)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y;
        double length = double.Hypot(dx, dy);
        if (!(length > JunctionTolerance) || !double.IsFinite(length) || GroupPlanarContours(rings) is not { } sections)
            return null;

        var points = rings.SelectMany(r => r).ToArray();
        double u0 = points.Min(p => p.X), u1 = points.Max(p => p.X);
        double rightX = dy / length, rightY = -dx / length;
        var xy = (Math.Min(start.X, end.X) + Math.Min(rightX * u0, rightX * u1),
            Math.Min(start.Y, end.Y) + Math.Min(rightY * u0, rightY * u1),
            Math.Max(start.X, end.X) + Math.Max(rightX * u0, rightX * u1),
            Math.Max(start.Y, end.Y) + Math.Max(rightY * u0, rightY * u1));
        if (!SamePlan(xy, box.XMin, box.YMin, box.XMax, box.YMax) ||
            !(Math.Abs(baseElevation + points.Min(p => p.Y) - box.ZMin) <= JunctionTolerance &&
              Math.Abs(baseElevation + points.Max(p => p.Y) - box.ZMax) <= JunctionTolerance))
            return null;

        double volume = rings.Sum(SignedRingArea) * length;
        if (!double.IsFinite(volume) || volume <= 0) return null;
        var result = new List<PlaWallExtrusionSection>(sections.Length);
        foreach (var section in sections)
        {
            var contours = new List<IReadOnlyList<PlaProfilePoint>>(section.Count);
            foreach (var ring in section)
                contours.Add(Array.AsReadOnly(ring.Select(p => new PlaProfilePoint(p.X, p.Y)).ToArray()));
            result.Add(new PlaWallExtrusionSection(contours.AsReadOnly()));
        }
        return new PlaWallExtrusion(start, end, baseElevation, result.AsReadOnly(), volume);
    }

    private static IReadOnlyList<IReadOnlyList<PlaPlanPoint>>? ReadStraightProfileSection(IReadOnlyList<PlaProfileComponent> components)
    {
        IReadOnlyList<IReadOnlyList<PlaPlanPoint>>? combined = null;
        double area = 0, perimeter = 0;
        foreach (var component in components)
        {
            if (component.Arcs is { Count: > 0 } || component.Rings.Count == 0) return null;
            var rings = new List<IReadOnlyList<PlaPlanPoint>>(component.Rings.Count);
            foreach (var saved in component.Rings)
            {
                var ring = saved.Select(p => new PlaPlanPoint(p.U, p.Z)).ToArray();
                double signedArea = SignedRingArea(ring);
                if (ring.Length < 3 || !double.IsFinite(signedArea) || signedArea == 0 || !IsSimpleRing(ring)) return null;
                if (rings.Count > 0 && !IsSeparateHole(rings, ring)) return null;
                if ((signedArea > 0) != (rings.Count == 0)) Array.Reverse(ring);
                rings.Add(ring);
                for (int i = 0; i < ring.Length; i++) perimeter += Distance(ring[i], ring[(i + 1) % ring.Length]);
            }
            area += rings.Sum(SignedRingArea);
            combined = combined is null ? rings : CombinePlanarContours(combined, rings, subtract: false);
            if (combined is null) return null;
        }
        // The saved material fills must partition the section.
        return combined is not null && Math.Abs(combined.Sum(SignedRingArea) - area) <= JunctionTolerance * Math.Max(1, perimeter)
            ? combined : null;
    }

    private static bool SameWallExtrusion(PlaWallExtrusion? a, PlaWallExtrusion? b) =>
        a == b || a is not null && b is not null && a.Start == b.Start && a.End == b.End &&
        a.BaseElevation == b.BaseElevation && a.Volume == b.Volume && a.Sections.Count == b.Sections.Count &&
        a.Sections.Zip(b.Sections).All(pair => pair.First.Rings.Count == pair.Second.Rings.Count &&
            pair.First.Rings.Zip(pair.Second.Rings).All(rings => rings.First.SequenceEqual(rings.Second)));
}
