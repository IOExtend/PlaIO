using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static readonly Guid CircularColumnSectionClass = new("d6c12939-d905-40e0-aa57-f7c8d687dc7b");

    private static Elevations? ReadCircularColumn(Guid memo, ReadOnlySpan<byte> section, ReadOnlySpan<byte> lower,
        ReadOnlySpan<byte> upper, PlaPlanPoint origin, ElementLocation location, ModelGeometry geometry,
        List<(Guid Key, Guid Id)>? junctions, out PlaBoundingBox? boundingBox, out PlaColumnExtrusion? extrusion)
    {
        boundingBox = null;
        extrusion = null;
        if (ReadSingleRelation(section, SectionProfileRelation, requireSection: true) != memo ||
            ReadCircularColumnSection(section, out int material) is not { } radius ||
            !IsHorizontal(lower) || !IsHorizontal(upper) || !(ReadDouble(lower, 40) < 0 && ReadDouble(upper, 40) > 0) ||
            CheckedElevations(ReadDouble(lower, 16), ReadDouble(upper, 16)) is not { } nominal || nominal.Bottom >= nominal.Top)
            return null;

        var plan = (origin.X - radius, origin.Y - radius, origin.X + radius, origin.Y + radius);
        bool solid = true;
        var heights = junctions is null ? nominal : ResolveColumnJunctions(geometry, memo, junctions, location, material,
            nominal, plan, nominal.Bottom, nominal.Top, profiled: false, checkSolid: true, out _, out solid);
        if (solid && heights is { } span && geometry.StoryElevation(location.HomeStoryIndex) is { } home &&
            ProjectBoundingBox(plan, span, home) is { } box && box.ZMin < box.ZMax &&
            Math.PI * radius * radius * (span.Top - span.Bottom) is > 0 and { } volume && double.IsFinite(volume))
        {
            var (capArea, perimeter) = CircularScheduleSection(radius);
            double lateralArea = perimeter * (span.Top - span.Bottom);
            boundingBox = box;
            extrusion = new PlaColumnExtrusion(origin, [], new(box.ZMin, 0, 0), new(box.ZMax, 0, 0), box, volume)
            {
                CircularRadius = radius,
                ScheduledCapArea = capArea,
                ScheduledLateralArea = double.IsFinite(lateralArea) ? lateralArea : null,
            };
        }
        return heights;
    }

    private static (double Area, double Perimeter) CircularScheduleSection(double radius)
    {
        // Listing quantities use equal subdivisions of each quadrant with a 1 mm sagitta.
        const double sagitta = 0.001;
        double halfStep = 2 * Math.Asin(Math.Sqrt(Math.Min(0.5, sagitta / radius / 2)));
        double quadrantSegments = Math.Max(2, Math.Ceiling(Math.PI / (4 * halfStep)));
        double step = Math.PI / (2 * quadrantSegments);
        return (Math.PI * radius * radius * (Math.Sin(step) / step),
            Math.Tau * radius * (Math.Sin(step / 2) / (step / 2)));
    }

    private static double? ReadCircularColumnSection(ReadOnlySpan<byte> section, out int material)
    {
        material = 0;
        var hatch = ReadSectionHatch(section, 141, 482);
        if (hatch.Length < 175 || BinaryPrimitives.ReadUInt32LittleEndian(hatch[55..]) != 4) return null;
        double diameter = ReadDouble(section, 91);
        double radius = diameter / 2;
        if (!double.IsFinite(radius) || radius <= 0 || diameter != ReadDouble(section, 99)) return null;

        var points = hatch[59..];
        if (points[..16].IndexOfAnyExcept((byte)0) >= 0 || !IsCorner(points[16..], -radius, 0) ||
            !IsCorner(points[32..], radius, 0) || !points.Slice(16, 16).SequenceEqual(points.Slice(48, 16))) return null;

        // Two semicircles close the only contour, between the two stored ends of a diameter.
        var arcs = points[64..];
        if (!arcs.StartsWith(new byte[] { 1, 0, 2, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0 }) ||
            ReadDouble(arcs, 14) != Math.PI || BinaryPrimitives.ReadUInt32LittleEndian(arcs[22..]) != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(arcs[26..]) != 3 || ReadDouble(arcs, 30) != Math.PI ||
            !arcs[38..].StartsWith(new byte[] { 1, 0, 2, 0, 0, 0, 0, 0, 0, 0, 3, 0, 0, 0 })) return null;

        material = BinaryPrimitives.ReadInt32LittleEndian(hatch[18..]);
        return radius;
    }
}
