using System.Buffers.Binary;
using PlaIO.Model;
using Xunit;

namespace PlaIO.Tests;

public partial class ElementReaderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void ReadsDoorWallHoleDimensionsAcrossPoolsOnlyWithVerifiedOwners(int variation)
    {
        Guid doorClass = new("d8f07689-9cfa-4fbe-aeb4-0a60b8e667ee");
        Guid libraryClass = new("2bfca33d-bd38-49e3-bf6d-52ac8b850b9f");
        Guid tableClass = new("11db227d-dbe4-45af-88b6-6fddea993f71");
        Guid libraryRole = new("672f1c46-64f3-41f5-8a4a-ffdfd86bcbfd");
        Guid tableRole = new("76ebf0a4-c2ef-483f-a1a8-28d3adde99a0");
        Guid door = Guid.NewGuid(), descriptor = Guid.NewGuid(), table = Guid.NewGuid();
        Guid family = Guid.NewGuid(), revision = Guid.NewGuid();
        var doorBody = WithGeometryRelations(FramedElementId(doorClass, 286, "Door"), GeometryRelation(0x11, libraryRole, descriptor));
        byte[] descriptorData = new byte[89];
        libraryClass.TryWriteBytes(descriptorData);
        new byte[] { 1, 0, 131, 0, 3, 0, 0, 0, 1, 0, 2, 0 }.CopyTo(descriptorData, 39);
        family.TryWriteBytes(descriptorData.AsSpan(51));
        revision.TryWriteBytes(descriptorData.AsSpan(67));
        var descriptorBody = WithGeometryRelations(descriptorData, GeometryRelation(0x12, libraryRole, door),
            GeometryRelation(0x11, tableRole, variation == 6 ? Guid.NewGuid() : table));

        const int tableSize = 8 + 2 * 72;
        byte[] tableData = new byte[57 + tableSize + 24];
        tableClass.TryWriteBytes(tableData);
        new byte[] { 1, 0, 8, 0, 131, 0, 3, 0, 0, 0, 1, 0, 1, 0 }.CopyTo(tableData, 39);
        BinaryPrimitives.WriteUInt32LittleEndian(tableData.AsSpan(35), (uint)tableData.Length - 39);
        BinaryPrimitives.WriteUInt32LittleEndian(tableData.AsSpan(53), tableSize);
        BinaryPrimitives.WriteUInt32LittleEndian(tableData.AsSpan(57), variation == 4 ? uint.MaxValue : 2);
        WriteLengthParameter(tableData.AsSpan(65, 72), "ac_wallhole_width"u8, 1.125);
        WriteLengthParameter(tableData.AsSpan(137, 72), variation == 5 ? "ac_wallhole_width"u8 : "ac_wallhole_height"u8,
            variation == 3 ? double.NaN : 2.25);
        var tableBody = WithGeometryRelations(tableData, variation == 0 ? GeometryRelation(0x12, tableRole, descriptor) :
            GeometryRelationList(2, tableRole, variation == 2 ? Guid.NewGuid() : descriptor, Guid.NewGuid()));

        byte[] doorPool = new byte[2048], tablePool = new byte[512];
        Synthetic.Block(PlaBlockMode.Raw, doorBody, ver: 1, guid: door, extra: 1, kind: 1).CopyTo(doorPool, 0);
        Synthetic.Block(PlaBlockMode.Raw, descriptorBody, ver: 1, guid: descriptor, extra: 1, kind: 2).CopyTo(doorPool, 1024);
        Synthetic.Block(PlaBlockMode.Raw, tableBody, ver: 1, guid: table, extra: 1, kind: 3).CopyTo(tablePool, 0);
        byte[] tableDirectory = new byte[40];
        BinaryPrimitives.WriteUInt64LittleEndian(tableDirectory, 32);
        new Guid("34d77468-1d98-45b9-958d-1eea37423cb5").TryWriteBytes(tableDirectory.AsSpan(24));
        using var doc = Open(ClassTable((1, doorClass), (2, libraryClass), (3, tableClass)),
            OwnedDirectory(false, Guid.Empty, 128, new(1, 0, 64, door), new(2, 64, 64, descriptor)),
            Synthetic.Block(PlaBlockMode.Raw,
                Synthetic.Concat(tableDirectory, Synthetic.Record(new(3, 0, 32, table))), ver: 1),
            Pool(tablePool), Pool(doorPool));

        var element = Assert.Single(doc.ReadElements());
        Assert.Equal(new PlaLibraryPartReference(family, revision), element.LibraryPartReference);
        Assert.Equal(variation is 0 or 1 ? new PlaDoorWallHoleDimensions(1.125, 2.25) : (PlaDoorWallHoleDimensions?)null,
            element.DoorWallHoleDimensions);

    }

    private static void WriteLengthParameter(Span<byte> row, ReadOnlySpan<byte> name, double value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(row[8..], 2);
        BinaryPrimitives.WriteDoubleLittleEndian(row[24..], value);
        name.CopyTo(row[32..]);
        BinaryPrimitives.WriteUInt32LittleEndian(row[68..], sizeof(double));
    }

    private static byte[] PlainDoorPassageTable(Guid tableClass, Guid role, Guid descriptor, bool positiveOversize, int revealType,
        bool pocket, bool centeredPocket)
    {
        (string Name, ushort Type, int Value)[] settings =
        [
            ("AC_WallType", 0, 1),
            ("gs_leaf_optype_m", 0, pocket ? 25 : 1),
            ("gs_wallhole_nominal", 13, 1),
            ("gs_reveal_type_int", 0, revealType),
            ("gs_closure_type_int", 0, 1),
            ("gs_trimtype_out_int", 0, 1),
            ("gs_trimtype_in_int", 0, 1),
            ("iThresholdType", 0, pocket ? -1 : 2),
            ("gs_sunShade_iType", 0, 1),
            ("gs_sidelight_left", 13, 0),
            ("gs_sidelight_right", 13, 0),
            ("gs_sill_outside", 13, 0),
            ("gs_board_inside", 13, 0),
            ("gs_shutRoll_cutWallBehind", 13, 0),
            ("gs_shutLam_cutWallBehind", 13, 0),
            ("bMasonryArch", 13, 0),
        ];
        (string Name, double Value)[] lengths = [];
        if (pocket)
        {
            settings = [.. settings, ("gs_pocket_door_frame", 13, 1), ("gs_pocket_frame", 13, 0),
                ("gs_leaf_center", 13, centeredPocket ? 1 : 0), ("bThreshold", 13, 0)];
            lengths = [("gs_leaf_offset", 0.01), ("gs_leaf_undercut", 0), ("gs_leaf_thk", 0.04),
                ("ac_leaf_height", 2.05), ("ac_leaf_width", 0.6), ("gs_leaf_panel_width", 0.6),
                ("gs_frame_thk", 0.06), ("gs_frame_width", 0.05), ("gs_frame_width_left", 0.04),
                ("gs_frame_width_right", 0.04), ("gs_frame_width_upper", 0.04)];
        }
        int count = settings.Length + lengths.Length + 6;
        int size = 8 + 72 * count;
        byte[] bytes = new byte[57 + size + 24];
        tableClass.TryWriteBytes(bytes);
        new byte[] { 1, 0, 8, 0, 131, 0, 3, 0, 0, 0, 1, 0, 1, 0 }.CopyTo(bytes, 39);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(35), (uint)bytes.Length - 39);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(53), (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(57), (uint)count);
        WriteLengthParameter(bytes.AsSpan(65, 72), "ac_wallhole_width"u8, pocket ? 0.7 : 0.5);
        WriteLengthParameter(bytes.AsSpan(137, 72), "ac_wallhole_height"u8, pocket ? 2.1 : 2);
        WriteLengthParameter(bytes.AsSpan(209, 72), "ac_left_oversize"u8, positiveOversize ? 0.125 : 0);
        WriteLengthParameter(bytes.AsSpan(281, 72), "ac_right_oversize"u8, 0);
        WriteLengthParameter(bytes.AsSpan(353, 72), "ac_lower_oversize"u8, 0);
        WriteLengthParameter(bytes.AsSpan(425, 72), "ac_upper_oversize"u8, 0);
        for (int i = 0; i < settings.Length; i++)
        {
            var (name, type, value) = settings[i];
            var row = bytes.AsSpan(497 + i * 72, 72);
            BinaryPrimitives.WriteUInt16LittleEndian(row[8..], type);
            BinaryPrimitives.WriteInt32LittleEndian(row[24..], value);
            System.Text.Encoding.ASCII.GetBytes(name, row[32..64]);
        }
        for (int i = 0; i < lengths.Length; i++)
        {
            var (name, value) = lengths[i];
            WriteLengthParameter(bytes.AsSpan(497 + (settings.Length + i) * 72, 72),
                System.Text.Encoding.ASCII.GetBytes(name), value);
        }
        return WithGeometryRelations(bytes, GeometryRelation(0x12, role, descriptor));
    }
}
