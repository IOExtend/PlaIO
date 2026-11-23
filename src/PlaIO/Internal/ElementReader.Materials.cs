using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    internal static PlaMaterialCatalog ReadMaterialCatalog(PlaDocument document, IReadOnlyList<PlaObjectClass> classes)
    {
        var kinds = classes.Where(c => c.Guid == BuildingMaterialClass || c.Guid == CompositeClass)
            .ToDictionary(c => c.Number, c => c.Guid);
        var materials = new Dictionary<int, PlaBuildingMaterial>();
        var composites = new Dictionary<int, PlaComposite>();
        var seen = new HashSet<Guid>();
        foreach (var block in document.Blocks)
        {
            if (block.Tag != "BL" || block.Version != 1 || block.Guid is not { } id || id == Guid.Empty ||
                !kinds.TryGetValue(block.Kind, out Guid type)) continue;
            var payload = block.Mode == PlaBlockMode.Lz ? document.ReadStandalonePayload(block) :
                document.PayloadStartsWithClass(block, type) ? document.ReadPayload(block) : null;
            if (payload is not null) Add(id, type, payload);
        }

        if (kinds.Count != 0)
            foreach (var (record, block) in FindUnindexedAttributeBlocks(document, kinds, seen))
                if (document.ReadDirectoryPayload(block) is { } payload)
                    Add(record.ObjectId, kinds[record.ClassNumber], payload);

        return new PlaMaterialCatalog(materials.Values.OrderBy(m => m.Index).ToList().AsReadOnly(),
            composites.Values.OrderBy(c => c.Index).ToList().AsReadOnly());

        void Add(Guid id, Guid type, byte[] payload)
        {
            if (payload.Length < 16 || new Guid(payload.AsSpan(0, 16)) != type) return;
            var data = ReadNamedAttribute(payload, out string? name, out int index);
            if (data.IsEmpty) return;
            if (type == BuildingMaterialClass)
            {
                uint? raw = ReadBuildingMaterialPriority(data, out string? materialId);
                int? priority = raw switch
                {
                    4200 => 0,
                    >= 4300 and <= 5909 => (int)(((raw.Value - 4298) * 31 + 25) / 50),
                    _ => null,
                };
                var material = new PlaBuildingMaterial(id, index, name!, materialId, priority);
                if (!materials.TryAdd(index, material))
                {
                    if (materials[index] != material) throw new InvalidDataException($"Building material {index} has conflicting records.");
                    return;
                }
            }
            else
            {
                if (ReadComposite(id, index, name!, data) is not { } composite) return;
                if (!composites.TryAdd(index, composite))
                {
                    var prior = composites[index];
                    if ((prior.Guid, prior.Name, prior.TotalThickness) != (composite.Guid, composite.Name, composite.TotalThickness) ||
                        !prior.Skins.SequenceEqual(composite.Skins) ||
                        (prior.UseWith is null) != (composite.UseWith is null) ||
                        !(prior.UseWith ?? []).SequenceEqual(composite.UseWith ?? []))
                        throw new InvalidDataException($"Composite {index} has conflicting records.");
                    return;
                }
            }
            if (!seen.Add(id)) throw new InvalidDataException($"Material attribute {id:D} has conflicting indices or classes.");
        }
    }

    private static ReadOnlySpan<byte> ReadNamedAttribute(ReadOnlySpan<byte> payload, out string? name, out int index,
        int frame = 47)
    {
        name = null;
        index = 0;
        int start = frame + LayerNameFrame.Length;
        if (payload.Length < start + sizeof(int) || !payload[frame..].StartsWith(LayerNameFrame) ||
            ReadElementId(payload[start..], out int consumed) is not { } value) return default;
        var data = payload[(start + consumed)..];
        if (data.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 1) return default;
        index = BinaryPrimitives.ReadInt32LittleEndian(data[2..]);
        if (index <= 0) return default;
        name = value;
        return data;
    }

    private static uint? ReadBuildingMaterialPriority(ReadOnlySpan<byte> data, out string? id)
    {
        id = null;
        if (data.Length < 41) return null;
        id = ReadElementId(data[37..], out int consumed);
        return id is not null && data.Length - 37 - consumed >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(data[(37 + consumed)..]) : null;
    }

    private const int CompositeSkinSize = 23;

    private static ReadOnlySpan<byte> ReadCompositeSkinRows(ReadOnlySpan<byte> data)
    {
        if (data.Length < 45) return default;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data[43..]);
        return count > 0 && count <= (data.Length - 45) / CompositeSkinSize
            ? data.Slice(45, count * CompositeSkinSize) : default;
    }

    private static PlaComposite? ReadComposite(Guid id, int index, string name, ReadOnlySpan<byte> data)
    {
        var rows = ReadCompositeSkinRows(data);
        if (rows.IsEmpty) return null;
        double total = ReadDouble(data, 35);
        var skins = new PlaCompositeSkin[rows.Length / CompositeSkinSize];
        for (int i = 0; i < skins.Length; i++)
        {
            var row = rows[(i * CompositeSkinSize)..];
            int material = BinaryPrimitives.ReadUInt16LittleEndian(row[8..]);
            double thickness = ReadDouble(row, 15);
            if (ReadCompositeSkinFunction(row) is not { } function)
                return null;
            skins[i] = new PlaCompositeSkin(material, thickness, function == 1, function == 5);
        }
        if (!(Math.Abs(skins.Sum(skin => skin.Thickness) - total) <= JunctionTolerance)) return null;

        ushort usage = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        List<string>? useWith = null;
        if ((usage & ~60) == 0 && (usage & 48) is 0 or 48)
        {
            useWith = new List<string>(4);
            if ((usage & 4) != 0) useWith.Add("Wall");
            if ((usage & 8) != 0) useWith.Add("Slab");
            if ((usage & 48) != 0) useWith.AddRange(["Roof", "Shell"]);
        }
        return new PlaComposite(id, index, name, total, Array.AsReadOnly(skins), useWith?.AsReadOnly());
    }

    private static byte? ReadCompositeSkinFunction(ReadOnlySpan<byte> row) =>
        row.StartsWith(new byte[] { 5, 1, 17, 0, 0, 0, 1, 0 }) &&
        BinaryPrimitives.ReadUInt16LittleEndian(row[8..]) > 0 &&
        row[14] is 1 or 5 or 6 && ReadDouble(row, 15) is >= 0 and < double.PositiveInfinity
            ? row[14] : null;

    private static (int? Material, int? Composite) ReadMaterialReference(ReadOnlySpan<byte> record, bool wall)
    {
        var settings = AfterCountedString(record, wall ? 197 : 185);
        int structure = wall ? 225 : 83;
        if (settings.Length < structure + 6 || BinaryPrimitives.ReadUInt16LittleEndian(settings[(structure + 2)..]) != 1)
            return default;

        return BinaryPrimitives.ReadUInt16LittleEndian(settings[structure..]) switch
        {
            1 when BinaryPrimitives.ReadInt32LittleEndian(settings[(structure - 10)..]) is > 0 and { } material =>
                (material, null),
            2 when BinaryPrimitives.ReadUInt16LittleEndian(settings[(structure + 4)..]) is > 0 and { } composite =>
                (null, composite),
            _ => default,
        };
    }
}
