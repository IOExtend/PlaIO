using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static PlaWallReferencePlan? ReadWallReferencePlan(ReadOnlySpan<byte> settings,
        out (double X0, double Y0, double X1, double Y1)? footprint, bool allowEmptyOutline = false)
    {
        footprint = null;
        if (settings.Length < 251 || settings[184] != 0 || settings[238] > 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(settings[225..]) is not (1 or 2) ||
            BinaryPrimitives.ReadUInt16LittleEndian(settings[227..]) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(settings[239..]) > 4)
            return null;

        double x = ReadDouble(settings, 0), y = ReadDouble(settings, 8);
        double x1 = ReadDouble(settings, 16), y1 = ReadDouble(settings, 24);
        double length = double.Hypot(x1 - x, y1 - y);
        double thickness = ReadDouble(settings, 77), offset = ReadDouble(settings, 243);
        if (!double.IsFinite(length) || length <= JunctionTolerance ||
            !double.IsFinite(thickness) || thickness <= JunctionTolerance || thickness != ReadDouble(settings, 85) ||
            !(offset >= 0 && offset <= thickness))
            return null;

        bool flipped = settings[238] == 1;
        double outside = flipped ? offset : -offset;
        double inside = flipped ? offset - thickness : thickness - offset;
        double rightX = (y1 - y) / length, rightY = (x - x1) / length;
        double min = Math.Min(outside, inside), max = Math.Max(outside, inside);
        var plan = (Math.Min(x, x1) + Math.Min(rightX * min, rightX * max),
            Math.Min(y, y1) + Math.Min(rightY * min, rightY * max),
            Math.Max(x, x1) + Math.Max(rightX * min, rightX * max),
            Math.Max(y, y1) + Math.Max(rightY * min, rightY * max));
        if (!(allowEmptyOutline && HasEmptyWallOutline(settings)) && !MatchesInlineWallRectangle(settings,
                new(x + rightX * max, y + rightY * max), new(x1 + rightX * max, y1 + rightY * max),
                new(x1 + rightX * min, y1 + rightY * min), new(x + rightX * min, y + rightY * min), plan))
            return null;
        footprint = plan;
        return new PlaWallReferencePlan(new PlaPlanPoint(x, y), new PlaPlanPoint(x1, y1), outside, inside);
    }

    private static bool HasEmptyWallOutline(ReadOnlySpan<byte> settings)
    {
        if (settings.Length < 400) return false;
        var outline = settings[256..323];
        return outline.StartsWith(new byte[] { 0, 0, 2, 1, 59, 0, 0, 0, 1, 31, 0 }) &&
            ReadDouble(outline, 11) == 1e35 && ReadDouble(outline, 19) == 1e35 &&
            ReadDouble(outline, 27) == -1e35 && ReadDouble(outline, 35) == -1e35 &&
            outline[43..].IndexOfAnyExcept((byte)0) < 0;
    }

    private static bool HasUncachedDoorWallOutline(ReadOnlySpan<byte> wall, ReadOnlySpan<byte> settings) =>
        !HasWallBoxFrame(settings) && HasEmptyWallOutline(settings) &&
        FindRelationshipSection(wall, wall.Length) - 6 - (wall.Length - settings.Length) == 400 &&
        !ReadOpeningRelation(wall, DoorHostRelation).IsEmpty;

    private static (double X0, double Y0, double X1, double Y1)? ReadProfileWallRectangle(ReadOnlySpan<byte> settings,
        PlaPlanPoint start, PlaPlanPoint end, double min, double max, bool allowPartial)
    {
        double length = double.Hypot(end.X - start.X, end.Y - start.Y);
        if (!double.IsFinite(length) || length <= JunctionTolerance) return null;
        double rx = (end.Y - start.Y) / length, ry = (start.X - end.X) / length;
        var plan = (Math.Min(start.X, end.X) + Math.Min(rx * min, rx * max), Math.Min(start.Y, end.Y) + Math.Min(ry * min, ry * max),
            Math.Max(start.X, end.X) + Math.Max(rx * min, rx * max), Math.Max(start.Y, end.Y) + Math.Max(ry * min, ry * max));
        if (MatchesInlineWallRectangle(settings, new(start.X + rx * max, start.Y + ry * max),
                new(end.X + rx * max, end.Y + ry * max), new(end.X + rx * min, end.Y + ry * min),
                new(start.X + rx * min, start.Y + ry * min), plan))
            return plan;

        if (!allowPartial || start.X != end.X && start.Y != end.Y ||
            !settings[256..].StartsWith(new byte[] { 0, 0, 2, 1, 247, 0, 0, 0, 0, 31, 0 }) ||
            ReadRectangularPlan(settings[256..]) is not { } rectangle) return null;
        var along = ProjectRectangle(rectangle, start.X, start.Y, -ry, rx);
        var across = ProjectRectangle(rectangle, start.X, start.Y, rx, ry);
        // A horizontal profile slice can fix the transverse orientation by sharing an exterior face.
        return Math.Abs(along.Min) <= JunctionTolerance && Math.Abs(along.Max - length) <= JunctionTolerance &&
            across.Min >= min - JunctionTolerance && across.Max <= max + JunctionTolerance &&
            (Math.Abs(across.Min - min) <= JunctionTolerance || Math.Abs(across.Max - max) <= JunctionTolerance) ? plan : null;
    }

    private static bool MatchesInlineWallRectangle(ReadOnlySpan<byte> settings, PlaPlanPoint a, PlaPlanPoint b,
        PlaPlanPoint c, PlaPlanPoint d, (double X0, double Y0, double X1, double Y1) plan)
    {
        if (settings.Length < 503) return false;
        var polygon = settings[256..];
        ReadOnlySpan<byte> rectangle = [0, 0, 2, 1, 247, 0, 0, 0, 0, 31, 0];
        if (!polygon.StartsWith(rectangle) ||
            BinaryPrimitives.ReadUInt32LittleEndian(polygon[43..]) != 5 ||
            BinaryPrimitives.ReadUInt32LittleEndian(polygon[47..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(polygon[51..]) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(polygon[151..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(polygon[155..]) != 5 ||
            !polygon.Slice(71, 16).SequenceEqual(polygon.Slice(135, 16)) ||
            !SamePlan(plan, ReadDouble(polygon, 11), ReadDouble(polygon, 19), ReadDouble(polygon, 27), ReadDouble(polygon, 35)))
            return false;
        return MatchesCorner(polygon, 71, a) && MatchesCorner(polygon, 87, b) &&
            MatchesCorner(polygon, 103, c) && MatchesCorner(polygon, 119, d);

        static bool MatchesCorner(ReadOnlySpan<byte> record, int offset, PlaPlanPoint point) =>
            Math.Abs(ReadDouble(record, offset) - point.X) <= JunctionTolerance &&
            Math.Abs(ReadDouble(record, offset + 8) - point.Y) <= JunctionTolerance;
    }

    private static bool SamePlan((double X0, double Y0, double X1, double Y1) plan, double x0, double y0, double x1, double y1) =>
        Math.Abs(plan.X0 - x0) <= JunctionTolerance && Math.Abs(plan.Y0 - y0) <= JunctionTolerance &&
        Math.Abs(plan.X1 - x1) <= JunctionTolerance && Math.Abs(plan.Y1 - y1) <= JunctionTolerance;
}
