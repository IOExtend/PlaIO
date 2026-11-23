using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static Dictionary<Guid, double>? ReadProfileModifierValues(ReadOnlySpan<byte> section,
        IReadOnlyList<PlaProfileComponent> components, ReadOnlySpan<byte> tables)
    {
        if (ReadProfileModifierGroups(ref tables) is not { } modifiers ||
            ReadProfileDimensionGroups(ref tables) is not { } groups ||
            ReadProfileHatchVertices(section, components) is not { } vertices ||
            ReadProfileDimensionAnchors(ref tables, vertices) is not { } anchors ||
            !ReadProfileTableCount(ref tables, 114, out int count))
            return null;

        var dimensions = new Dictionary<Guid, (Guid Modifier, double Value)>();
        var seen = new HashSet<Guid>();
        for (int i = 0; i < count; i++, tables = tables[114..])
        {
            var row = tables[..114];
            Guid id = new(row[..16]);
            if (!seen.Add(id) || !row[16..].StartsWith(new byte[] { 1, 1, 92, 0, 0, 0 })) return null;
            var owner = new Guid(row.Slice(70, 16));
            double angle = ReadDouble(row, 106);
            int kind = BinaryPrimitives.ReadInt32LittleEndian(row[102..]);
            if (owner == Guid.Empty || owner != new Guid(row.Slice(86, 16)) || kind is not (2 or 3) ||
                !double.IsFinite(angle) || !anchors.TryGetValue(new Guid(row.Slice(22, 16)), out var a) ||
                !anchors.TryGetValue(new Guid(row.Slice(38, 16)), out var b)) continue;
            double value = (b.U - a.U) * Math.Cos(angle) + (b.Z - a.Z) * Math.Sin(angle);
            if (double.IsFinite(value)) dimensions.Add(id, (owner, value));
        }

        var result = new Dictionary<Guid, double>();
        foreach (var (owner, groupIds) in modifiers)
        {
            double? value = null;
            foreach (Guid group in groupIds)
            {
                if (!groups.TryGetValue(group, out Guid id) || !dimensions.TryGetValue(id, out var dimension) ||
                    dimension.Modifier != owner || (value is { } known && Math.Abs(dimension.Value - known) > 1e-7))
                {
                    value = null;
                    break;
                }
                value ??= dimension.Value;
            }
            if (value is { } confirmed) result.Add(owner, confirmed);
        }
        return result;
    }

    private static Dictionary<Guid, Guid[]>? ReadProfileModifierGroups(ref ReadOnlySpan<byte> data)
    {
        if (!ReadProfileTableCount(ref data, 22, out int count)) return null;
        var groups = new Dictionary<Guid, Guid[]>();
        for (int i = 0; i < count; i++)
        {
            if (data.Length < 22) return null;
            Guid key = new(data[..16]);
            data = data[16..];
            if (!ReadProfileTableCount(ref data, 16, out int length)) return null;
            var ids = new Guid[length];
            for (int j = 0; j < length; j++)
                ids[j] = new Guid(data.Slice(16 * j, 16));
            if (!groups.TryAdd(key, ids)) return null;
            data = data[(16 * length)..];
        }
        return groups;
    }

    private static Dictionary<Guid, Guid>? ReadProfileDimensionGroups(ref ReadOnlySpan<byte> data)
    {
        if (!ReadProfileTableCount(ref data, 46, out int count)) return null;
        var dimensions = new Dictionary<Guid, Guid>();
        for (int i = 0; i < count; i++)
        {
            if (data.Length < 46 || !data[16..].StartsWith(new byte[] { 1, 0, 2, 0, 0, 0, 1, 0, 1, 0 }))
                return null;
            Guid key = new(data[..16]);
            int edges = BinaryPrimitives.ReadInt32LittleEndian(data[26..]);
            if (edges <= 0 || edges > (data.Length - 46) / 28) return null;
            int target = 30 + edges * 28;
            for (int j = 30; j < target; j += 28)
                if (!data[j..].StartsWith(new byte[] { 1, 0, 1, 0 }) ||
                    BinaryPrimitives.ReadUInt32LittleEndian(data[(j + 24)..]) > 1) return null;
            if (!dimensions.TryAdd(key, new Guid(data.Slice(target, 16)))) return null;
            data = data[(target + 16)..];
        }
        return dimensions;
    }

    private static Dictionary<Guid, PlaProfilePoint[]>? ReadProfileHatchVertices(ReadOnlySpan<byte> vector,
        IReadOnlyList<PlaProfileComponent> components)
    {
        var items = ReadFirstWallHatch(vector, out uint count);
        int offset = vector.Length - items.Length;
        if (count != components.Count || offset < 16) return null;
        var points = new Dictionary<Guid, PlaProfilePoint[]>();
        foreach (var component in components)
        {
            int length = ReadHatchSize(vector[offset..], 2);
            // Constraint vertex indices count across rings without their stored closing vertices.
            if (length == 0 || !points.TryAdd(new Guid(vector.Slice(offset - 16, 16)),
                component.Rings.SelectMany(ring => ring).ToArray())) return null;
            offset += length;
            if (offset == vector.Length) break;
            if (vector.Length - offset < 16) return null;
            offset += 16;
        }
        return offset == vector.Length && points.Count == components.Count ? points : null;
    }

    private static Dictionary<Guid, PlaProfilePoint>? ReadProfileDimensionAnchors(ref ReadOnlySpan<byte> data,
        Dictionary<Guid, PlaProfilePoint[]> vertices)
    {
        if (!ReadProfileTableCount(ref data, 83, out int count)) return null;
        var seen = new HashSet<Guid>();
        var result = new Dictionary<Guid, PlaProfilePoint>();
        for (int i = 0; i < count; i++, data = data[83..])
        {
            Guid id = new(data[..16]);
            var anchor = data.Slice(16, 67);
            if (!seen.Add(id) || BinaryPrimitives.ReadUInt16LittleEndian(anchor) != 1 ||
                BinaryPrimitives.ReadUInt16LittleEndian(anchor[6..]) != 1 ||
                BinaryPrimitives.ReadUInt16LittleEndian(anchor[28..]) != 1 || anchor[50] > 1) return null;
            int kind = BinaryPrimitives.ReadInt32LittleEndian(anchor[2..]);
            if (kind == 3 && anchor[50] == 1)
            {
                double u = ReadDouble(anchor, 51), z = ReadDouble(anchor, 59);
                if (double.IsFinite(u) && double.IsFinite(z)) result.Add(id, new(u, z));
            }
            if (kind is 1 or 2)
            {
                int reference = kind == 1 ? 8 : 30;
                Guid hatch = new(anchor.Slice(reference, 16));
                int vertex = BinaryPrimitives.ReadInt32LittleEndian(anchor[(reference + 16)..]);
                if (vertices.TryGetValue(hatch, out var points) && vertex > 0 && vertex <= points.Length)
                    result.Add(id, points[vertex - 1]);
            }
        }
        return result;
    }

    private static bool ReadProfileTableCount(ref ReadOnlySpan<byte> data, int rowSize, out int count)
    {
        count = 0;
        if (data.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 1) return false;
        count = BinaryPrimitives.ReadInt32LittleEndian(data[2..]);
        data = data[6..];
        return count >= 0 && count <= data.Length / rowSize;
    }
}
