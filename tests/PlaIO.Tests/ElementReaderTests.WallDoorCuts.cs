using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Tests;

public partial class ElementReaderTests
{
    internal static (byte[] File, Guid Wall) WallWithDoorCutsFixture(double sill, double secondStation, bool reverse,
        double oversize = 0, bool unsupportedRelation = false, double width = 1, double firstStation = 2)
    {
        Guid doorClass = new("d8f07689-9cfa-4fbe-aeb4-0a60b8e667ee");
        Guid libraryClass = new("2bfca33d-bd38-49e3-bf6d-52ac8b850b9f");
        Guid tableClass = new("11db227d-dbe4-45af-88b6-6fddea993f71");
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid libraryRole = new("672f1c46-64f3-41f5-8a4a-ffdfd86bcbfd");
        Guid parameterRole = new("76ebf0a4-c2ef-483f-a1a8-28d3adde99a0");
        Guid hostRole = new("1f6bfe02-d60c-40ae-9ac8-c3953ce12814");
        Guid family = new("f6fab810-3a56-4c74-8eb8-b58738a9ee9b");
        Guid revision = new("0adb90d2-efb3-4a46-ae2b-35a98d9eef91");
        Guid wall = Guid.NewGuid(), first = Guid.NewGuid(), second = Guid.NewGuid(), table = Guid.NewGuid();
        Guid firstPart = Guid.NewGuid(), secondPart = Guid.NewGuid();

        byte[] wallBody = WallWithPlanes(1, -3.75)[..609];
        var settings = wallBody.AsSpan(209);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[77..], 0.25);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[85..], 0.25);
        BinaryPrimitives.WriteDoubleLittleEndian(settings, reverse ? 6 : 0);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[8..], 2);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[16..], reverse ? 0 : 6);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[24..], 2);
        settings[238] = reverse ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteDoubleLittleEndian(settings[243..], 0.0625);
        new byte[] { 0, 0, 2, 1, 59, 0, 0, 0, 1, 31, 0 }.CopyTo(settings[256..]);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[267..], 1e35);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[275..], 1e35);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[283..], -1e35);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[291..], -1e35);
        wallBody = WithGeometryRelations(wallBody, GeometryRelationList(1, hostRole, first, second),
            unsupportedRelation ? GeometryRelation(0x11, Guid.NewGuid(), Guid.NewGuid()) : []);
        byte[] model = new byte[5 * 1024];
        var records = new List<ObjectDirectoryRecord>();
        Add(1, wall, wallBody);
        AddDoor(first, firstPart, firstStation);
        AddDoor(second, secondPart, secondStation);

        const int size = 8 + 6 * 72;
        byte[] tableBody = new byte[57 + size + 24];
        tableClass.TryWriteBytes(tableBody);
        new byte[] { 1, 0, 8, 0, 131, 0, 3, 0, 0, 0, 1, 0, 1, 0 }.CopyTo(tableBody, 39);
        BinaryPrimitives.WriteUInt32LittleEndian(tableBody.AsSpan(35), (uint)tableBody.Length - 39);
        BinaryPrimitives.WriteUInt32LittleEndian(tableBody.AsSpan(53), size);
        BinaryPrimitives.WriteUInt32LittleEndian(tableBody.AsSpan(57), 6);
        WriteLengthParameter(tableBody.AsSpan(65, 72), "ac_wallhole_width"u8, width);
        WriteLengthParameter(tableBody.AsSpan(137, 72), "ac_wallhole_height"u8, 2);
        WriteLengthParameter(tableBody.AsSpan(209, 72), "ac_left_oversize"u8, oversize);
        WriteLengthParameter(tableBody.AsSpan(281, 72), "ac_right_oversize"u8, 0);
        WriteLengthParameter(tableBody.AsSpan(353, 72), "ac_lower_oversize"u8, 0);
        WriteLengthParameter(tableBody.AsSpan(425, 72), "ac_upper_oversize"u8, 0);
        byte[] tablePool = new byte[1024];
        Synthetic.Block(PlaBlockMode.Raw, WithGeometryRelations(tableBody, GeometryRelationList(2, parameterRole, firstPart, secondPart)),
            ver: 1, guid: table, extra: 1, kind: 4).CopyTo(tablePool, 0);
        var file = Synthetic.Fdb(ClassTable((1, WallClass), (2, doorClass), (3, libraryClass), (4, tableClass), (5, storyClass)),
            OwnedDirectory(false, Guid.Empty, 320, records.ToArray()),
            OwnedDirectory(false, Guid.Empty, 64, new ObjectDirectoryRecord(4, 0, 64, table)),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 5), Pool(model), Pool(tablePool));
        return (file, wall);

        void Add(ushort kind, Guid id, byte[] body)
        {
            int slot = records.Count;
            Synthetic.Block(PlaBlockMode.Raw, body, ver: 1, guid: id, extra: 1, kind: kind).CopyTo(model, slot * 1024);
            records.Add(new(kind, (ulong)slot * 64, 64, id));
        }

        void AddDoor(Guid door, Guid part, double station)
        {
            byte[] descriptor = new byte[89];
            libraryClass.TryWriteBytes(descriptor);
            new byte[] { 1, 0, 131, 0, 3, 0, 0, 0, 1, 0, 2, 0 }.CopyTo(descriptor, 39);
            family.TryWriteBytes(descriptor.AsSpan(51));
            revision.TryWriteBytes(descriptor.AsSpan(67));
            Add(3, part, WithGeometryRelations(descriptor, GeometryRelation(0x12, libraryRole, door),
                GeometryRelation(0x11, parameterRole, table)));
            var framed = FramedElementId(doorClass, 286, "D");
            WriteLocation(framed.AsSpan(175), 1, -1);
            BinaryPrimitives.WriteUInt16LittleEndian(framed.AsSpan(185), 1);
            BinaryPrimitives.WriteDoubleLittleEndian(framed.AsSpan(202), width);
            BinaryPrimitives.WriteDoubleLittleEndian(framed.AsSpan(210), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(framed.AsSpan(268), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(framed.AsSpan(272), 83);
            BinaryPrimitives.WriteUInt16LittleEndian(framed.AsSpan(274), 2);
            byte[] placement = new byte[72];
            new byte[] { 0, 1, 0, 14, 0, 0, 0, 63, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0 }.CopyTo(placement, 0);
            BinaryPrimitives.WriteDoubleLittleEndian(placement.AsSpan(24), station);
            BinaryPrimitives.WriteDoubleLittleEndian(placement.AsSpan(48), 1);
            BinaryPrimitives.WriteDoubleLittleEndian(placement.AsSpan(64), sill);
            Add(2, door, WithGeometryRelations(Synthetic.Concat(framed, placement),
                GeometryRelation(0x11, libraryRole, part), GeometryRelation(0x12, hostRole, wall)));
        }
    }
}
