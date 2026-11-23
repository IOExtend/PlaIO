using System.Buffers.Binary;
using System.Text;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static readonly Guid LibraryParameterClass = new("11db227d-dbe4-45af-88b6-6fddea993f71");
    private static readonly Guid LibraryParameterDirectory = new("34d77468-1d98-45b9-958d-1eea37423cb5");
    private static readonly Guid LibraryParameterRelation = new("76ebf0a4-c2ef-483f-a1a8-28d3adde99a0");
    private static ReadOnlySpan<byte> LibraryParameterFrame => [1, 0, 8, 0, 0x83, 0, 3, 0, 0, 0, 1, 0, 1, 0];
    private static readonly (string Name, ushort Type, double? Plain, double Pocket)[] PassageSettings =
    [
        ("AC_WallType", 0, 1, 1),
        ("gs_leaf_optype_m", 0, 1, 25),
        ("gs_wallhole_nominal", 13, 1, 1),
        ("gs_reveal_type_int", 0, 1, 1),
        ("gs_closure_type_int", 0, 1, 1),
        ("gs_trimtype_out_int", 0, 1, 1),
        ("gs_trimtype_in_int", 0, 1, 1),
        ("iThresholdType", 0, 2, -1),
        ("gs_sunShade_iType", 0, 1, 1),
        ("gs_sidelight_left", 13, 0, 0),
        ("gs_sidelight_right", 13, 0, 0),
        ("gs_sill_outside", 13, 0, 0),
        ("gs_board_inside", 13, 0, 0),
        ("gs_shutRoll_cutWallBehind", 13, 0, 0),
        ("gs_shutLam_cutWallBehind", 13, 0, 0),
        ("bMasonryArch", 13, 0, 0),
        ("gs_pocket_door_frame", 13, null, 1),
        ("gs_pocket_frame", 13, null, 0),
        ("gs_leaf_center", 13, null, 1),
        ("bThreshold", 13, null, 0),
        ("gs_leaf_offset", 2, null, 0.01),
        ("gs_leaf_undercut", 2, null, 0),
        ("gs_leaf_thk", 2, null, 0.04),
        ("ac_leaf_height", 2, null, 2.05),
        ("ac_leaf_width", 2, null, PocketLeafWidth),
        ("gs_leaf_panel_width", 2, null, PocketLeafWidth),
        ("gs_frame_thk", 2, null, PocketFrameDepth),
        ("gs_frame_width", 2, null, 0.05),
        ("gs_frame_width_left", 2, null, 0.04),
        ("gs_frame_width_right", 2, null, 0.04),
        ("gs_frame_width_upper", 2, null, 0.04),
    ];
    private static readonly Dictionary<string, (ushort Type, double? Plain, double Pocket, uint Bit)> PassageSettingValues =
        PassageSettings.Select((field, index) => (field, index))
            .ToDictionary(pair => pair.field.Name, pair => (pair.field.Type, pair.field.Plain, pair.field.Pocket, 1u << pair.index));
    private static readonly uint PlainPassageMask = PassageSettingValues.Values.Where(s => s.Plain.HasValue)
        .Aggregate(0u, (bits, setting) => bits | setting.Bit);
    private static readonly uint PocketPassageMask = (1u << PassageSettings.Length) - 1;

    private readonly record struct DoorParameterLink(Guid Descriptor, Guid Table);
    private sealed record DoorParameterTable(PlaDoorWallHoleDimensions Dimensions, HashSet<Guid> Descriptors,
        bool NoOversize, bool PlainPassage, bool CenteredPocketPassage);

    private static Dictionary<Guid, DoorParameterTable?> ReadDoorParameterTables(IReadOnlyList<CompressedPool> pools,
        Dictionary<ulong, List<ObjectDirectoryRecord>> entries)
    {
        var tables = new Dictionary<Guid, DoorParameterTable?>();
        foreach (var pool in pools)
        {
            // Door and library parameter records can be saved in separate pools.
            ulong units = (ulong)(pool.TotalDecompressed / ExtentUnit);
            if (pool.TotalDecompressed % ExtentUnit != 0 || !entries.TryGetValue(units, out var records)) continue;
            byte[] data = pool.Decode();
            foreach (var record in records)
            {
                if (ReadIndexedPayload(data, record) is not { } payload) continue;
                var table = ReadDoorParameterTable(payload);
                if (!tables.TryAdd(record.ObjectId, table) &&
                    (table is null || tables[record.ObjectId] is not { } previous ||
                        table.Dimensions != previous.Dimensions || table.NoOversize != previous.NoOversize ||
                        table.PlainPassage != previous.PlainPassage || table.CenteredPocketPassage != previous.CenteredPocketPassage ||
                        !table.Descriptors.SetEquals(previous.Descriptors)))
                    tables[record.ObjectId] = null;
            }
        }
        return tables;
    }

    private static DoorParameterTable? ResolveDoorParameterTable(Dictionary<Guid, DoorParameterTable?> tables, DoorParameterLink link)
    {
        var table = tables.GetValueOrDefault(link.Table);
        return table is not null && table.Descriptors.Contains(link.Descriptor) ? table : null;
    }

    private static DoorParameterTable? ReadDoorParameterTable(ReadOnlySpan<byte> data)
    {
        const int bodyOffset = 39;
        const int tableOffset = 57;
        const int trailerSize = 24;
        const int rowStart = 65;
        const int rowSize = 72;
        if (data.Length < rowStart || new Guid(data[..16]) != LibraryParameterClass ||
            !data[bodyOffset..].StartsWith(LibraryParameterFrame)) return null;
        uint bodySize = BinaryPrimitives.ReadUInt32LittleEndian(data[35..]);
        uint tableSize = BinaryPrimitives.ReadUInt32LittleEndian(data[53..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data[tableOffset..]);
        if (bodySize < rowStart - bodyOffset || bodySize > data.Length - bodyOffset - 7 ||
            bodySize != (long)tableSize + tableOffset + trailerSize - bodyOffset ||
            tableSize < rowStart - tableOffset || count > (tableSize - (rowStart - tableOffset)) / rowSize)
            return null;
        int end = bodyOffset + (int)bodySize;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[end..]) != data.Length - end - 4 ||
            BinaryPrimitives.ReadUInt16LittleEndian(data[(end + 4)..]) != 1) return null;

        double? width = null, height = null;
        int oversizes = 0;
        bool noOversize = true;
        uint passageSettings = 0, plainSettings = 0, pocketSettings = 0;
        for (int i = 0; i < count; i++)
        {
            var row = data.Slice(rowStart + i * rowSize, rowSize);
            var name = row.Slice(32, 32);
            int terminator = name.IndexOf((byte)0);
            if (terminator < 0) continue;
            name = name[..terminator];
            bool isWidth = name.SequenceEqual("ac_wallhole_width"u8);
            if (!isWidth && !name.SequenceEqual("ac_wallhole_height"u8))
            {
                int bit = name.SequenceEqual("ac_left_oversize"u8) ? 1 :
                    name.SequenceEqual("ac_right_oversize"u8) ? 2 :
                    name.SequenceEqual("ac_lower_oversize"u8) ? 4 :
                    name.SequenceEqual("ac_upper_oversize"u8) ? 8 : 0;
                if (bit != 0)
                {
                    noOversize &= (oversizes & bit) == 0 && IsScalarLength(row) && ReadDouble(row, 24) == 0;
                    oversizes |= bit;
                }
                else if (PassageSettingValues.TryGetValue(Encoding.ASCII.GetString(name), out var setting))
                {
                    bool valid = (passageSettings & setting.Bit) == 0 &&
                        BinaryPrimitives.ReadUInt16LittleEndian(row[8..]) == setting.Type &&
                        BinaryPrimitives.ReadUInt64LittleEndian(row[12..]) == 0 && (setting.Type != 2 || IsScalarLength(row));
                    double actual = setting.Type == 2 ? ReadDouble(row, 24) : BinaryPrimitives.ReadInt32LittleEndian(row[24..]);
                    if (!valid)
                    {
                        plainSettings &= ~setting.Bit;
                        pocketSettings &= ~setting.Bit;
                    }
                    else
                    {
                        if (actual == setting.Plain) plainSettings |= setting.Bit;
                        if (Math.Abs(actual - setting.Pocket) <= JunctionTolerance) pocketSettings |= setting.Bit;
                    }
                    passageSettings |= setting.Bit;
                }
                continue;
            }

            ref double? value = ref (isWidth ? ref width : ref height);
            if (value.HasValue || !IsScalarLength(row)) return null;
            double dimension = ReadDouble(row, 24);
            if (!(dimension > 0) || !double.IsFinite(dimension)) return null;
            value = dimension;
        }
        if (width is not double w || height is not double h) return null;

        var relation = data[(end + 6)..];
        var entry = relation;
        if (!TryReadGeometryRelation(ref relation, out byte tag, out Guid key, out _) ||
            key != LibraryParameterRelation || tag is not (2 or 0x12) || relation is not [0])
            return null;
        var descriptors = new HashSet<Guid>();
        var ids = ReadRelationIds(entry, relation, tag);
        for (int i = 0; i < ids.Length; i += 16)
        {
            Guid id = new(ids.Slice(i, 16));
            if (id == Guid.Empty || !descriptors.Add(id)) return null;
        }
        return descriptors.Count == 0 ? null : new DoorParameterTable(new(w, h), descriptors,
            noOversize && oversizes == 15, plainSettings == PlainPassageMask, pocketSettings == PocketPassageMask);

        static bool IsScalarLength(ReadOnlySpan<byte> row) =>
            BinaryPrimitives.ReadUInt16LittleEndian(row[8..]) == 2 &&
            BinaryPrimitives.ReadUInt64LittleEndian(row[12..]) == 0 &&
            BinaryPrimitives.ReadUInt32LittleEndian(row[68..]) == sizeof(double);
    }
}
