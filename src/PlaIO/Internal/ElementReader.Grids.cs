using System.Buffers.Binary;
using System.Text;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static readonly Guid GridLibraryFamily = new("b286e7ff-8856-42d5-b293-6084b1784db2");
    private static readonly Guid ObjectLibraryRelation = new("189f453e-ea2e-4e38-afe9-660d1d25afc6");

    private readonly record struct GridPlacement(Guid Descriptor, PlaPlanPoint Anchor, double Rotation, bool Mirrored,
        int? Story, double? ElevationToHomeStory);
    private readonly record struct GridLibraryLink(Guid Owner, Guid Table);
    private readonly record struct GridDefinition(string Label, double Extent, double Sweep, double? SegmentLength, bool IsHidden);
    private sealed record GridParameters(GridDefinition Definition, HashSet<Guid> Owners);

    internal static IReadOnlyList<PlaGridAxis> ReadGridAxes(PlaDocument document, IReadOnlyList<PlaObjectClass> classes)
    {
        var stores = ReadGridStores(document, classes);
        if (stores is null) throw new InvalidDataException("A grid store has an unsupported frame or relationship section.");
        var members = stores.Where(store => store.Key != Guid.Empty).SelectMany(store => store.Value).ToHashSet();
        if (members.Count == 0) return [];

        var classIds = classes.Where(c => c.Guid == ObjectClass || c.Guid == PlacedLibraryClass || c.Guid == LibraryParameterClass)
            .ToDictionary(c => c.Number, c => c.Guid);
        var directories = new Dictionary<ulong, List<ObjectDirectoryRecord>>();
        foreach (var block in document.Blocks)
        {
            if (!IsRawTable(block, DirectoryKind)) continue;
            var data = document.ReadPayload(block).AsSpan();
            int header = DirectoryHeaderSize(data, DirectoryLayout);
            bool parameterDirectory = header == 0;
            if (parameterDirectory) header = DirectoryHeaderSize(data, LibraryParameterDirectory);
            if (header == 0) continue;

            ulong units = BinaryPrimitives.ReadUInt64LittleEndian(data[(header - 40)..]);
            for (int offset = header; offset <= data.Length - DirectoryFinder.RecordSize; offset += DirectoryFinder.RecordSize)
            {
                var record = DirectoryFinder.ReadRecord(data[offset..]);
                if (record.ObjectId == Guid.Empty || record.Count == 0 || record.Position > units || record.Count > units - record.Position ||
                    !classIds.TryGetValue(record.ClassNumber, out Guid kind) ||
                    (parameterDirectory && kind != LibraryParameterClass) || (kind == ObjectClass && !members.Contains(record.ObjectId)))
                    continue;
                if (!directories.TryGetValue(units, out var entries)) directories.Add(units, entries = []);
                entries.Add(record);
            }
        }

        var placements = new Dictionary<Guid, GridPlacement?>();
        var descriptors = new Dictionary<Guid, GridLibraryLink?>();
        var parameters = new Dictionary<Guid, GridParameters?>();
        foreach (var pool in document.FindCompressedPools())
        {
            if (pool.TotalDecompressed % ExtentUnit != 0 ||
                !directories.TryGetValue((ulong)(pool.TotalDecompressed / ExtentUnit), out var entries)) continue;
            var data = pool.Decode();
            foreach (var record in entries)
            {
                if (ReadIndexedPayload(data, record) is not { } payload) continue;
                Guid id = record.ObjectId, kind = classIds[record.ClassNumber];
                if (kind == ObjectClass)
                    AddGridRecord(placements, id, ReadGridPlacement(payload, id, stores));
                else if (kind == PlacedLibraryClass)
                    AddGridRecord(descriptors, id, ReadGridLibraryLink(payload, members));
                else
                {
                    var table = ReadGridParameters(payload);
                    if (!parameters.TryAdd(id, table) && (table is null || parameters[id] is not { } previous ||
                        previous.Definition != table.Definition || !previous.Owners.SetEquals(table.Owners)))
                        parameters[id] = null;
                }
            }
        }

        var axes = new List<PlaGridAxis>();
        var stories = new Lazy<Dictionary<int, Story>>(() => ReadStories(document, classes));
        foreach (var (id, candidate) in placements.OrderBy(pair => pair.Key))
        {
            if (candidate is not { } placement ||
                descriptors.GetValueOrDefault(placement.Descriptor) is not { } link || link.Owner != id ||
                parameters.GetValueOrDefault(link.Table) is not { } table || !table.Owners.Contains(placement.Descriptor))
                continue;

            double? storyElevation = placement.Story is { } story && placement.ElevationToHomeStory.HasValue
                ? stories.Value.GetValueOrDefault(story).Elevation : null;
            if (ReadGridAxis(id, placement, table.Definition, storyElevation) is { } axis) axes.Add(axis);
        }
        return axes.AsReadOnly();
    }

    private static PlaGridAxis? ReadGridAxis(Guid id, GridPlacement placement, GridDefinition definition, double? storyElevation)
    {
        var (label, extent, sweep, segmentLength, isHidden) = definition;
        var start = placement.Anchor;
        PlaPlanPoint end;
        PlaGridArc? arc = null;
        if (sweep == 0)
        {
            end = PointFromAnchor(placement.Rotation, placement.Mirrored ? -extent : extent);
        }
        else
        {
            // The circular anchor is the center. Reflection reverses the arc, so use its other end first for CCW order.
            double startAngle = placement.Mirrored ? placement.Rotation + Math.PI - sweep : placement.Rotation;
            start = PointFromAnchor(startAngle, extent);
            end = sweep == Math.Tau ? start : PointFromAnchor(startAngle + sweep, extent);
            if (start == placement.Anchor || end == placement.Anchor) return null;
            arc = new PlaGridArc(placement.Anchor, extent, sweep);
        }

        if (!double.IsFinite(start.X) || !double.IsFinite(start.Y) || !double.IsFinite(end.X) || !double.IsFinite(end.Y) ||
            (end == start && sweep != Math.Tau)) return null;
        return new(id, label, start, end, placement.Story)
        {
            ElevationToHomeStory = placement.ElevationToHomeStory,
            ElevationToProjectZero = ToProjectZero(placement.ElevationToHomeStory, storyElevation),
            Arc = arc,
            FloorPlanSegmentLength = segmentLength,
            IsFloorPlanLineHidden = isHidden,
        };

        PlaPlanPoint PointFromAnchor(double angle, double distance)
        {
            var (sin, cos) = Math.SinCos(angle);
            return new(placement.Anchor.X + distance * cos, placement.Anchor.Y + distance * sin);
        }
    }

    private static void AddGridRecord<T>(Dictionary<Guid, T?> records, Guid id, T? value) where T : struct
    {
        if (!records.TryAdd(id, value) && !EqualityComparer<T?>.Default.Equals(records[id], value)) records[id] = null;
    }

    private static GridPlacement? ReadGridPlacement(ReadOnlySpan<byte> data, Guid id, Dictionary<Guid, HashSet<Guid>> stores)
    {
        if (!TryReadDeclaredRelations(data, out int end, out var relations) || new Guid(data[..16]) != ObjectClass) return null;
        Guid store = ReadGridRelation(relations, GridElementList, 0x12);
        if (store == Guid.Empty || !stores.TryGetValue(store, out var members) || !members.Contains(id) ||
            ReadGridRelation(relations, ElementPartitionRelation, 0x12) == Guid.Empty) return null;

        Guid descriptor = ReadGridRelation(relations, ObjectLibraryRelation, 0x11);
        int frame = FindClassFrame(data[..end], ObjectClass);
        if (descriptor == Guid.Empty || frame < 0 || end - frame < 254) return null;

        var body = data[frame..end];
        double x = ReadDouble(body, 206), y = ReadDouble(body, 214), rotation = ReadDouble(body, 222);
        double elevation = ReadDouble(body, 230), scaleX = ReadDouble(body, 238), scaleY = ReadDouble(body, 246);
        ushort placementFlags = BinaryPrimitives.ReadUInt16LittleEndian(body[204..]);
        // The zero-flags placement variant stores no explicit scale.
        bool unitScale = (scaleX, scaleY) is (1, 1) || (placementFlags, scaleX, scaleY) is (0, 0, 0);
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(rotation) ||
            body[201] > 1 || !unitScale) return null;
        return new(descriptor, new(x, y), rotation, body[201] == 1, ReadElementLocation(body, "Object", 0).HomeStoryIndex,
            double.IsFinite(elevation) ? elevation : null);
    }

    private static GridLibraryLink? ReadGridLibraryLink(ReadOnlySpan<byte> data, HashSet<Guid> members)
    {
        if (data.Length < 89 || new Guid(data[..16]) != PlacedLibraryClass || !data[39..].StartsWith(PlacedLibraryFrame) ||
            new Guid(data.Slice(51, 16)) != GridLibraryFamily || new Guid(data.Slice(67, 16)) == Guid.Empty ||
            !TryReadDeclaredRelations(data, out int end, out var relations) || end < 89) return null;
        Guid owner = ReadGridRelation(relations, ObjectLibraryRelation, 0x12);
        if (!members.Contains(owner)) return null;
        Guid table = ReadGridRelation(relations, LibraryParameterRelation, 0x11);
        return table != Guid.Empty ? new(owner, table) : null;
    }

    private static Guid ReadGridRelation(ReadOnlySpan<byte> relations, Guid role, byte expectedTag)
    {
        Guid result = Guid.Empty;
        while (relations is not [0])
        {
            if (!TryReadGeometryRelation(ref relations, out byte tag, out Guid key, out Guid target)) return Guid.Empty;
            if (key != role) continue;
            if (tag != expectedTag || target == Guid.Empty || result != Guid.Empty) return Guid.Empty;
            result = target;
        }
        return result;
    }

    private static GridParameters? ReadGridParameters(ReadOnlySpan<byte> data)
    {
        if (!TryReadLibraryParameterFrame(data, out var rows, out var table, out var relations)) return null;
        double? length = null, radius = null, angle = null, segmentLength = null;
        int? geometryType = null, lineVisibility = null;
        string? label = null;
        for (int i = 0; i < rows.Length; i += LibraryParameterRowSize)
        {
            var row = rows.Slice(i, LibraryParameterRowSize);
            var name = ReadLibraryParameterName(row);
            if (name.SequenceEqual("AC_Type_i"u8))
            {
                if (geometryType.HasValue || !IsScalarNumber(row, 0)) return null;
                geometryType = BinaryPrimitives.ReadInt32LittleEndian(row[24..]);
            }
            else if (name.SequenceEqual("AC_LineVisibility_i"u8))
            {
                if (lineVisibility.HasValue || !IsScalarNumber(row, 0)) return null;
                lineVisibility = BinaryPrimitives.ReadInt32LittleEndian(row[24..]);
            }
            else if (name.SequenceEqual("AC_Length"u8))
            {
                if (!ReadGridNumber(row, 2, ref length)) return null;
            }
            else if (name.SequenceEqual("AC_Radius"u8))
            {
                if (!ReadGridNumber(row, 2, ref radius)) return null;
            }
            else if (name.SequenceEqual("AC_Angle"u8))
            {
                if (!ReadGridNumber(row, 3, ref angle)) return null;
            }
            else if (name.SequenceEqual("AC_VisLength"u8))
            {
                if (!ReadGridNumber(row, 2, ref segmentLength)) return null;
            }
            else if (name.SequenceEqual("AC_MarkerText_1"u8))
            {
                if (label is not null || ReadGridLabel(row, table, 8 + rows.Length) is not { } text) return null;
                label = text;
            }
        }
        if (label is null || lineVisibility is not (1 or 2 or 3) ||
            (lineVisibility == 2 && (segmentLength is not >= 0 || !double.IsFinite(segmentLength.Value))) ||
            ReadLibraryParameterOwners(relations) is not { } owners) return null;

        double? visibleLength = lineVisibility == 2 ? segmentLength : null;
        bool isHidden = lineVisibility == 3;
        if (geometryType == 1 && length is > 0 and { } extent && double.IsFinite(extent))
            return new(new(label, extent, 0, visibleLength, isHidden), owners);
        if (geometryType == 2 && radius is > 0 and { } r && double.IsFinite(r) && angle is > 0 and <= 360 and { } degrees)
        {
            double sweep = degrees == 360 ? Math.Tau : degrees * (Math.PI / 180);
            if (sweep > 0) return new(new(label, r, sweep, visibleLength, isHidden), owners);
        }
        return null;
    }

    private static bool ReadGridNumber(ReadOnlySpan<byte> row, ushort type, ref double? value)
    {
        if (value.HasValue) return false;
        value = IsScalarNumber(row, type) ? ReadDouble(row, 24) : double.NaN;
        return true;
    }

    private static string? ReadGridLabel(ReadOnlySpan<byte> row, ReadOnlySpan<byte> table, int valuesStart)
    {
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(row[64..]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(row[68..]);
        if (BinaryPrimitives.ReadUInt16LittleEndian(row[8..]) != 12 || BinaryPrimitives.ReadUInt64LittleEndian(row[12..]) != 0 ||
            (offset & 1) != 0 || (length & 1) != 0 || length < 2 || offset < valuesStart ||
            offset > table.Length || length > table.Length - offset) return null;

        var value = table.Slice((int)offset, (int)length);
        for (int index = 0; index < value.Length; index += 2)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(value[index..]) != 0) continue;
            if (value[index..].IndexOfAnyExcept((byte)0) >= 0) return null;
            try { return Utf16.GetString(value[..index]); }
            catch (DecoderFallbackException) { return null; }
        }
        return null;
    }
}
