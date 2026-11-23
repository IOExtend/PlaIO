using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static ReadOnlySpan<byte> SlabOutlineFrame => [2, 0, 2, 1];

    private static PlaBoundingBox? ReadSavedWallBox(ReadOnlySpan<byte> settings)
    {
        if (!HasWallBoxFrame(settings)) return null;
        return MakeBoundingBox((ReadDouble(settings, 515), ReadDouble(settings, 523),
            ReadDouble(settings, 531), ReadDouble(settings, 539)), ReadDouble(settings, 547), ReadDouble(settings, 555));
    }

    private static bool HasWallBoxFrame(ReadOnlySpan<byte> settings) =>
        settings.Length >= 563 && settings[489..].StartsWith(new byte[] { 1, 0, 1, 0, 0, 0 }) &&
        settings[495..511].IndexOfAnyExcept((byte)0) < 0;

    private static bool HasEmptyWallPlanCache(ReadOnlySpan<byte> settings) =>
        HasWallBoxFrame(settings) && settings[515..547].IndexOfAnyExcept((byte)0) < 0;

    private static PlaBoundingBox? ReadWallBoxWithoutPlanCache(ReadOnlySpan<byte> settings, ElementLocation location,
        (double X0, double Y0, double X1, double Y1)? footprint, PlaWallProfile? profile, Elevations planes, double home)
    {
        if (!HasEmptyWallPlanCache(settings)) return null;
        if (footprint is { } rectangle)
        {
            if (settings[547..563].IndexOfAnyExcept((byte)0) < 0) return ProjectBoundingBox(rectangle, planes, home);
            double lower = ReadDouble(settings, 547), upper = ReadDouble(settings, 555);
            return Math.Abs(lower - (home + planes.Bottom)) <= JunctionTolerance &&
                   Math.Abs(upper - (home + planes.Top)) <= JunctionTolerance
                ? MakeBoundingBox(rectangle, lower, upper) : null;
        }

        if (profile is null || location.TopStoryPartition != Guid.Empty || settings[184] != 0 ||
            settings[238] > 1 || BinaryPrimitives.ReadUInt32LittleEndian(settings[239..]) != 0 ||
            ReadDouble(settings, 243) != 0 || settings[547..563].IndexOfAnyExcept((byte)0) >= 0 ||
            ProjectBeamSection(default, profile.Components, 1, 0) is not { } width ||
            ProjectBeamSection(default, profile.Components, 0, 1) is not { } height ||
            Math.Abs(width.Max - width.Min - ReadDouble(settings, 77)) > JunctionTolerance ||
            ReadDouble(settings, 77) != ReadDouble(settings, 85) ||
            Math.Abs(height.Max - height.Min - (planes.Top - planes.Bottom)) > JunctionTolerance)
            return null;

        var start = new PlaPlanPoint(ReadDouble(settings, 0), ReadDouble(settings, 8));
        var end = new PlaPlanPoint(ReadDouble(settings, 16), ReadDouble(settings, 24));
        var direct = ReadProfileWallRectangle(settings, start, end, width.Min, width.Max, allowPartial: true);
        var reflected = ReadProfileWallRectangle(settings, start, end, -width.Max, -width.Min, allowPartial: true);
        if (direct.HasValue && reflected.HasValue && direct != reflected) return null;
        return (direct ?? reflected) is { } plan
            ? MakeBoundingBox(plan, home + planes.Bottom + height.Min, home + planes.Bottom + height.Max) : null;
    }

    private static PlaBoundingBox? ReadFloorBox(ReadOnlySpan<byte> record, string type, Elevations planes, double? story)
    {
        ReadOnlySpan<byte> polygon;
        if (type == "Slab")
        {
            var settings = AfterCountedString(record, ClassElementIdOffset(type));
            polygon = ReadSlabOutline(settings);
        }
        else
        {
            var settings = ReadZoneSettings(record);
            if (settings.Length < 56 || settings[52] != 0 || settings[53] is not (0 or 1) ||
                settings[54] != 2 || settings[55] != 1)
                return null;
            polygon = settings[52..];
        }

        return ReadOutlineBounds(polygon) is { } plan ? ProjectBoundingBox(plan, planes, story) : null;
    }

    private static ReadOnlySpan<byte> ReadSlabOutline(ReadOnlySpan<byte> settings)
    {
        if (settings.Length >= 166 && settings[166..].StartsWith(SlabOutlineFrame))
            return settings[166..];
        if (settings.Length >= 270 && settings[165..].StartsWith(new byte[] { 20, 0, 0, 0 }) &&
            settings[270..].StartsWith(SlabOutlineFrame))
            return settings[270..];
        return default;
    }

    private static (double X0, double Y0, double X1, double Y1)? ReadOutlineBounds(ReadOnlySpan<byte> polygon)
    {
        const int headerSize = 71;
        if (polygon.Length < headerSize || !polygon[8..].StartsWith(new byte[] { 1, 31, 0 }))
            return null;

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(polygon[4..]);
        uint points = BinaryPrimitives.ReadUInt32LittleEndian(polygon[43..]);
        uint arcs = BinaryPrimitives.ReadUInt32LittleEndian(polygon[47..]);
        uint rings = BinaryPrimitives.ReadUInt32LittleEndian(polygon[51..]);
        if (size < headerSize || size > polygon.Length || points < 4 || points > (size - headerSize) / 16 ||
            rings == 0 || rings > points / 4 || arcs > points - rings)
            return null;

        var (x0, y0, x1, y1) = (ReadDouble(polygon, 11), ReadDouble(polygon, 19), ReadDouble(polygon, 27), ReadDouble(polygon, 35));
        int footer = headerSize + 16 * (int)points;
        for (int offset = 0; offset < points; offset++)
        {
            double x = ReadDouble(polygon, headerSize + 16 * offset), y = ReadDouble(polygon, headerSize + 16 * offset + 8);
            if (!(x >= x0 - JunctionTolerance && x <= x1 + JunctionTolerance &&
                y >= y0 - JunctionTolerance && y <= y1 + JunctionTolerance))
                return null;
        }

        if (arcs > 0)
        {
            if (size - footer < 16 || arcs > (size - footer - 16) / 16 ||
                polygon.Slice(footer, 16).IndexOfAnyExcept((byte)0) >= 0)
                return null;
            footer += 16;
            for (int i = 0; i < arcs; i++, footer += 16)
            {
                uint start = BinaryPrimitives.ReadUInt32LittleEndian(polygon[footer..]);
                uint end = BinaryPrimitives.ReadUInt32LittleEndian(polygon[(footer + 4)..]);
                if (start == 0 || start >= points || end > points || end != start + 1 ||
                    !double.IsFinite(ReadDouble(polygon, footer + 8)))
                    return null;
            }
        }

        if (size - footer < 4 * (rings + 1) || BinaryPrimitives.ReadUInt32LittleEndian(polygon[footer..]) != 0)
            return null;
        uint first = 0;
        for (int ring = 1; ring <= rings; ring++)
        {
            uint last = BinaryPrimitives.ReadUInt32LittleEndian(polygon[(footer + 4 * ring)..]);
            if (last > points || last < first + 4 ||
                !polygon.Slice(headerSize + 16 * (int)first, 16).SequenceEqual(polygon.Slice(headerSize + 16 * ((int)last - 1), 16)))
                return null;
            first = last;
        }

        return first == points ? (x0, y0, x1, y1) : null;
    }

    private static PlaBoundingBox? ProjectBoundingBox((double X0, double Y0, double X1, double Y1) plan,
        Elevations planes, double? story) =>
        story is { } level ? MakeBoundingBox(plan, planes.Bottom + level, planes.Top + level) : null;

    private static PlaBoundingBox? MakeBoundingBox((double X0, double Y0, double X1, double Y1) plan, double low, double high)
    {
        var (x0, y0, x1, y1) = plan;
        return double.IsFinite(x0) && double.IsFinite(y0) && double.IsFinite(x1) && double.IsFinite(y1) &&
            double.IsFinite(low) && double.IsFinite(high) && x0 < x1 && y0 < y1 && low <= high
            ? new PlaBoundingBox(x0, y0, low, x1, y1, high) : null;
    }
}
