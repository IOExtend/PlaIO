using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static readonly Guid OpeningCutClass = new("9a756127-8689-486d-8873-5c1d7b0f8785");
    private static readonly Guid OpeningCutRelation = new("219fcd20-e2ef-4e61-bb1b-a771897ff878");
    private static readonly Guid CutElementRelation = new("fc9598c9-7004-45ef-93e0-8fdcbc306acf");
    private static ReadOnlySpan<byte> CenteredOpeningFrame => [2, 0, 0, 0, 1, 0, 1, 0, 0, 0, 1, 0, 0, 0];

    private readonly record struct OpeningLinks(Guid Host, IReadOnlyList<Guid>? Targets = null);

    private static PlaOpeningProfile? ReadOpeningProfile(ReadOnlySpan<byte> settings)
    {
        if (settings.Length < 363 || !settings[230..].StartsWith(new byte[] { 1, 3, 70, 0, 0, 0 }))
            return null;
        var polygon = settings[304..];
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(polygon[4..]);
        if (!polygon.StartsWith(new byte[] { 0, 0, 2, 1 }) ||
            !polygon[8..].StartsWith(new byte[] { 1, 31, 0 }) || size < 59 || size > polygon.Length)
            return null;

        uint kind = BinaryPrimitives.ReadUInt32LittleEndian(settings[236..]);
        uint dimensions = BinaryPrimitives.ReadUInt32LittleEndian(settings[298..]);
        if (kind is < 1 or > 3 || dimensions > 1) return null;
        double width = ReadDouble(settings, 240);
        double height = kind != 3 && dimensions == 0 ? width : ReadDouble(settings, 248);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return null;
        if (kind == 3 && (ReadOutlineBounds(polygon) is not { } bounds ||
            Math.Abs(bounds.X1 - bounds.X0 - width) > JunctionTolerance ||
            Math.Abs(bounds.Y1 - bounds.Y0 - height) > JunctionTolerance))
            return null;

        string shape = kind switch
        {
            1 => width == height ? "Square" : "Rectangle",
            2 => width == height ? "Circle" : "Ellipse",
            _ => "Custom",
        };
        return new PlaOpeningProfile(shape, width, height);
    }

    private static OpeningLinks ReadOpeningLinks(ReadOnlySpan<byte> opening, Guid id,
        ModelGeometry geometry, Dictionary<Guid, PlaObjectClass> elements)
    {
        var owner = ReadOpeningRelation(opening, OpeningHostRelation, owner: true);
        if (owner.IsEmpty) return default;

        var host = new Guid(owner);
        if (ReadOpeningTarget(host, geometry, elements) is not { } hostRecord ||
            !HasGeometryRelation(hostRecord, OpeningHostRelation, id))
            return default;

        var cuts = ReadOpeningRelation(opening, OpeningCutRelation);
        if (cuts.IsEmpty) return new OpeningLinks(host);
        var targets = new SortedSet<Guid>();
        for (int i = 0; i < cuts.Length; i += 16)
        {
            var cutId = new Guid(cuts.Slice(i, 16));
            var (source, target) = ReadOpeningCut(geometry, cutId);
            if (source != id || target == Guid.Empty) return new OpeningLinks(host);
            var targetRecord = target == host ? hostRecord : ReadOpeningTarget(target, geometry, elements);
            if (targetRecord is null || !HasGeometryRelation(targetRecord, CutElementRelation, cutId) || !targets.Add(target))
                return new OpeningLinks(host);
        }
        return targets.Contains(host) ? new OpeningLinks(host, Array.AsReadOnly(targets.ToArray())) : new OpeningLinks(host);
    }

    private static (Guid Opening, Guid Target) ReadOpeningCut(ModelGeometry geometry, Guid id)
    {
        if (geometry.Read(id, OpeningCutClass) is not { } cut) return default;
        var opening = ReadOpeningRelation(cut, OpeningCutRelation, owner: true);
        var target = ReadOpeningRelation(cut, CutElementRelation, owner: true);
        return opening.IsEmpty || target.IsEmpty ? default : (new Guid(opening), new Guid(target));
    }

    private static byte[]? ReadOpeningTarget(Guid id, ModelGeometry geometry, Dictionary<Guid, PlaObjectClass> elements) =>
        elements.TryGetValue(id, out var element) && (element.Guid == SlabClass || element.Guid == WallClass)
            ? geometry.Read(id, element.Guid) : null;

    private static ReadOnlySpan<byte> ReadOpeningRelation(ReadOnlySpan<byte> record, Guid relation, bool owner = false) =>
        TryReadRelationTargets(record, relation, out var ids, owner) ? ids : default;

    private static bool TryReadRelationTargets(ReadOnlySpan<byte> record, Guid relation, out ReadOnlySpan<byte> ids, bool owner = false)
    {
        ids = default;
        int start = FindRelationshipSection(record, record.Length);
        if (start < 0) return false;
        var data = record[start..];
        ReadOnlySpan<byte> result = default;
        bool found = false;
        while (data.Length >= 17)
        {
            var entry = data;
            if (!TryReadGeometryRelation(ref data, out byte tag, out Guid key, out _)) return false;
            if (key != relation) continue;
            if (found || (owner ? tag != 0x12 : tag is not (1 or 0x11))) return false;
            found = true;
            result = ReadRelationIds(entry, data, tag);
        }
        if (data is not [0] || found && result.IsEmpty) return false;
        ids = result;
        return true;
    }

    private static Elevations? ReadOpeningElevations(ReadOnlySpan<byte> opening, Guid id, PlaOpeningProfile? profile,
        OpeningLinks links, ModelGeometry geometry, Dictionary<Guid, PlaObjectClass> elements, out PlaOpeningGeometry? cutGeometry)
    {
        cutGeometry = null;
        if (profile is null || links.Host == Guid.Empty || links.Targets is null) return null;
        var settings = AfterCountedString(opening, ClassElementIdOffset("Opening"));
        if (elements[links.Host].Guid == SlabClass)
            return ReadSlabOpeningElevations(settings, id, profile, links, geometry, out cutGeometry);
        return links.Targets.Count == 1 ? ReadVerticalWallOpeningElevations(settings, profile) : null;
    }

    private static Elevations? ReadVerticalWallOpeningElevations(ReadOnlySpan<byte> settings, PlaOpeningProfile profile)
    {
        if (settings.Length < 290 || !settings[256..].StartsWith(CenteredOpeningFrame) ||
            BinaryPrimitives.ReadUInt32LittleEndian(settings[278..]) is not (0 or 2) ||
            profile.Shape == "Custom")
            return null;

        // The center-anchored profile has its local Y axis along project Z.
        if (!(Math.Abs(ReadDouble(settings, 174)) <= JunctionTolerance &&
            Math.Abs(ReadDouble(settings, 182)) <= JunctionTolerance &&
            Math.Abs(ReadDouble(settings, 190)) <= JunctionTolerance &&
            Math.Abs(Math.Abs(ReadDouble(settings, 198)) - 1) <= JunctionTolerance &&
            Math.Abs(ReadDouble(settings, 222)) <= JunctionTolerance))
            return null;

        double center = ReadDouble(settings, 270);
        return CheckedElevations(center - profile.Height / 2, center + profile.Height / 2);
    }

    private static Elevations? ReadWallOpeningContact(ModelGeometry geometry, ReadOnlySpan<byte> wallRecord,
        Guid wall, Guid role, Guid id)
    {
        var (opening, target) = role == CutElementRelation ? ReadOpeningCut(geometry, id) : (id, wall);
        if (target != wall || geometry.Read(opening, OpeningClass) is not { } body ||
            ReadSingleRelation(body, OpeningHostRelation, 0x12, requireSection: true) != wall ||
            !HasGeometryRelation(wallRecord, OpeningHostRelation, opening))
            return null;
        var cuts = ReadOpeningRelation(body, OpeningCutRelation);
        if (cuts.Length != 16) return null;
        var operation = new Guid(cuts);
        if (role == CutElementRelation ? operation != id : ReadOpeningCut(geometry, operation) != (opening, wall) ||
            !HasGeometryRelation(wallRecord, CutElementRelation, operation))
            return null;
        var settings = AfterCountedString(body.AsSpan(FindClassFrame(body, OpeningClass)), ClassElementIdOffset("Opening"));
        return ReadOpeningProfile(settings) is { } profile ? ReadVerticalWallOpeningElevations(settings, profile) : null;
    }
}
