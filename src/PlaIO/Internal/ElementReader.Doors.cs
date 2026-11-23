using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static readonly Guid PlacedLibraryClass = new("2bfca33d-bd38-49e3-bf6d-52ac8b850b9f");
    private static readonly Guid DoorLibraryRelation = new("672f1c46-64f3-41f5-8a4a-ffdfd86bcbfd");
    private static readonly Guid SimpleDoorOpeningPart = new("f6fab810-3a56-4c74-8eb8-b58738a9ee9b");
    private static readonly Guid SimpleDoorOpeningRevision = new("0adb90d2-efb3-4a46-ae2b-35a98d9eef91");
    private static readonly Guid RectangularDoorOpeningPart = new("ffd54fba-1ad6-4cf7-87cc-918accd4e480");
    private static readonly Guid RectangularDoorOpeningRevision = new("6ebb67a2-5c74-4640-ab79-86e54703485a");
    private static readonly PlaLibraryPartReference SwingDoorPart = new(
        new Guid("13d8efa7-3fee-4993-b7a5-d826a6830657"), new Guid("a4ce2f76-b4d0-4b26-a920-4a84fbe513dc"));
    private static readonly PlaLibraryPartReference TransomDoorPart = new(
        new Guid("e155d0a0-7435-41af-8668-b4bae047f2b2"), new Guid("460fe002-f8e8-4d2c-859e-e99587473cef"));
    private static readonly PlaLibraryPartReference PocketDoorPart = new(
        new Guid("e4b25630-169a-4820-8bd9-962ed253b6c1"), new Guid("318e68fb-15e0-4e0b-92ed-bbf67a14b2eb"));
    private const double PocketLeafWidth = 0.6;
    private const double PocketFrameDepth = 0.06;

    private static PlaDoorDimensions? ReadDoorDimensions(ReadOnlySpan<byte> door, ReadOnlySpan<byte> settings)
    {
        if (door.Length < 286 || BinaryPrimitives.ReadUInt16LittleEndian(door[185..]) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(door[268..]) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(door[272..]) != 83 ||
            BinaryPrimitives.ReadUInt16LittleEndian(door[274..]) is not (1 or 2))
            return null;

        double width = ReadDouble(door, 202), height = ReadDouble(door, 210);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return null;
        var dimensions = new PlaDoorDimensions(width, height);
        if (settings.Length >= 72 &&
            settings[..24] is [0, 1, 0, 14, 0, 0, 0, 1 or 63, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 or 255, 0, 0] &&
            ReadDouble(settings, 64) is var sill && double.IsFinite(sill))
            dimensions = dimensions with { SillHeightFromAnchor = sill };
        return dimensions;
    }

    private static Guid ReadDoorHost(ReadOnlySpan<byte> door, Guid id, ModelGeometry geometry)
    {
        var owners = ReadOpeningRelation(door, DoorHostRelation, owner: true);
        if (owners.IsEmpty) return Guid.Empty;
        Guid wallId = new(owners);
        if (geometry.Read(wallId, WallClass) is not { } wall) return Guid.Empty;
        var doors = ReadOpeningRelation(wall, DoorHostRelation);
        bool found = false;
        for (int i = 0; i < doors.Length; i += 16)
        {
            if (new Guid(doors.Slice(i, 16)) != id) continue;
            if (found) return Guid.Empty;
            found = true;
        }
        return found ? wallId : Guid.Empty;
    }

    private static PlaLibraryPartReference? ReadDoorLibraryPart(ModelGeometry geometry, ReadOnlySpan<byte> door, Guid id,
        out DoorParameterLink parameterLink)
    {
        parameterLink = default;
        Guid part = ReadSingleRelation(door, DoorLibraryRelation, 0x11, requireSection: true);
        if (geometry.Read(part, PlacedLibraryClass) is not { Length: >= 89 } descriptor ||
            !descriptor.AsSpan(39).StartsWith(PlacedLibraryFrame) ||
            ReadSingleRelation(descriptor, DoorLibraryRelation, 0x12, requireSection: true) != id)
            return null;
        Guid family = new(descriptor.AsSpan(51, 16)), revision = new(descriptor.AsSpan(67, 16));
        if (family == Guid.Empty || revision == Guid.Empty) return null;
        Guid parameters = ReadSingleRelation(descriptor, LibraryParameterRelation, 0x11, requireSection: true);
        if (parameters != Guid.Empty) parameterLink = new DoorParameterLink(part, parameters);
        return new PlaLibraryPartReference(family, revision);
    }

    private static PlaDoorPlacement? ReadDoorPlacement(ModelGeometry geometry, ReadOnlySpan<byte> door, Guid host)
    {
        if (geometry.Read(host, WallClass) is not { } body || FindClassFrame(body, WallClass) is not (>= 0 and { } frame))
            return null;
        var wall = body.AsSpan(frame);
        var wallSettings = AfterCountedString(wall, 197);
        if (wallSettings.Length < 229 || wallSettings[184] != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(wallSettings[227..]) != 1) return null;

        var settings = AfterCountedString(door, ClassElementIdOffset("Door"));
        if (ReadDoorDimensions(door, settings) is not { SillHeightFromAnchor: { } sill } ||
            ReadDoorStation(settings) is not { } station) return null;
        double x = ReadDouble(wallSettings, 0), y = ReadDouble(wallSettings, 8);
        double dx = ReadDouble(wallSettings, 16) - x, dy = ReadDouble(wallSettings, 24) - y;
        double length = double.Hypot(dx, dy);
        if (!double.IsFinite(length) || length <= JunctionTolerance || station < 0 || station > length) return null;
        var point = new PlaPlanPoint(x + dx / length * station, y + dy / length * station);
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return null;
        int? hostStory = ReadElementLocation(wall, "Wall", 0).HomeStoryIndex;
        double height = ReadDouble(wallSettings, 161) + sill;
        double? toHostStory = hostStory.HasValue && double.IsFinite(height) ? height : null;
        return new PlaDoorPlacement(point, station, ToProjectZero(toHostStory, geometry.StoryElevation(hostStory)), toHostStory);
    }

    private static double? ReadDoorStation(ReadOnlySpan<byte> settings)
    {
        double station = ReadDouble(settings, 24);
        return double.IsFinite(station) && ReadDouble(settings, 32) == 0 && ReadDouble(settings, 40) == 0 &&
            ReadDouble(settings, 48) == 1 && ReadDouble(settings, 56) == 0 ? station : null;
    }

    private static bool ReadDoorCapObstacles(ModelGeometry geometry, Guid wall, Guid id,
        ReadOnlySpan<byte> wallSettings, WallStrip strip, List<WallCapObstacle> obstacles)
    {
        if (geometry.Read(id, DoorClass) is not { } body) return false;
        var door = body.AsSpan(FindClassFrame(body, DoorClass));
        if (ReadDoorHost(door, id, geometry) != wall ||
            ReadDoorLibraryPart(geometry, door, id, out var parameterLink) is not { } library)
            return false;
        var (family, revision) = library;

        var settings = AfterCountedString(door, ClassElementIdOffset("Door"));
        if (ReadDoorDimensions(door, settings) is not { SillHeightFromAnchor: { } sill } dimensions ||
            ReadDoorStation(settings) is not { } station) return false;
        bool pocket = library == PocketDoorPart;
        if (library == SwingDoorPart || library == TransomDoorPart || pocket)
        {
            if (geometry.DoorParameters(parameterLink) is not { NoOversize: true, Dimensions: var hole } table ||
                (pocket ? !table.CenteredPocketPassage || !HasSupportedPocketHost(geometry, wallSettings, hole) : !table.PlainPassage) ||
                Math.Abs(hole.Width - dimensions.Width) > JunctionTolerance ||
                Math.Abs(hole.Height - dimensions.Height) > JunctionTolerance)
                return false;
            dimensions = dimensions with { Width = hole.Width, Height = hole.Height };
        }
        else if (!(family == SimpleDoorOpeningPart && revision == SimpleDoorOpeningRevision ||
            family == RectangularDoorOpeningPart && revision == RectangularDoorOpeningRevision))
            return false;
        int axis = strip.AlongX ? 0 : 8;
        double origin = ReadDouble(wallSettings, axis), destination = ReadDouble(wallSettings, axis + 16);
        double center = origin + Math.Sign(destination - origin) * station;
        double start = center - dimensions.Width / 2, end = center + dimensions.Width / 2;
        // Only the straight, vertical host placement establishes the aperture's height.
        double bottom = ReadDouble(wallSettings, 161) + sill;
        var height = BinaryPrimitives.ReadUInt16LittleEndian(wallSettings[227..]) == 1
            ? CheckedElevations(bottom, bottom + dimensions.Height) : null;
        if (!(start >= strip.Start - JunctionTolerance && end <= strip.End + JunctionTolerance && end > start))
            return false;
        obstacles.Add(new((Math.Max(start, strip.Start), Math.Min(end, strip.End)), height));
        if (pocket)
        {
            // The verified recess fits within the leaf's width and the centered frame's depth.
            // Include both possible directions so the placement flags cannot leave a cut unaccounted for.
            AddPocket(Math.Max(start - PocketLeafWidth, strip.Start), start);
            AddPocket(end, Math.Min(end + PocketLeafWidth, strip.End));
        }
        return true;

        void AddPocket(double from, double to)
        {
            if (from >= to) return;
            double middle = strip.AcrossMin + (strip.AcrossMax - strip.AcrossMin) / 2;
            double low = middle - PocketFrameDepth / 2, high = middle + PocketFrameDepth / 2;
            obstacles.Add(new((from, to), height, RectangleRing(strip.AlongX ? (from, low, to, high) : (low, from, high, to))));
        }
    }

    private static bool HasSupportedPocketHost(ModelGeometry geometry, ReadOnlySpan<byte> settings, PlaDoorWallHoleDimensions hole) =>
        settings.Length >= 231 && BinaryPrimitives.ReadUInt16LittleEndian(settings[225..]) == 2 &&
        BinaryPrimitives.ReadUInt16LittleEndian(settings[227..]) == 1 &&
        geometry.Attributes.Composite.GetValueOrDefault(BinaryPrimitives.ReadUInt16LittleEndian(settings[229..]))
            is [var outside, var core, var inside] &&
        Math.Abs(outside.Thickness - 0.024) <= JunctionTolerance && Math.Abs(core.Thickness - 0.052) <= JunctionTolerance &&
        Math.Abs(inside.Thickness - 0.024) <= JunctionTolerance && Math.Abs(ReadDouble(settings, 77) - 0.1) <= JunctionTolerance &&
        Math.Abs(hole.Width - 0.7) <= JunctionTolerance && Math.Abs(hole.Height - 2.1) <= JunctionTolerance;
}
