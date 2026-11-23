using System.Buffers.Binary;
using PlaIO.Model;
using Xunit;

namespace PlaIO.Tests;

public partial class ElementReaderTests
{
    [Theory]
    [InlineData(false, 0, 0, true, 0.25, 2.5)]
    [InlineData(true, 0, 0, true, 1.0, 3.25)]
    [InlineData(false, 0.02, 0, true, 0.25, 3.25)]
    [InlineData(false, 2e-7, 0, true, 0.25, 3.25)]
    [InlineData(false, 0, 0.125, true, 0.25, 3.25)]
    [InlineData(false, 0, 0, false, null, null)]
    public void CoplanarCompositeSlabsCanShareACompleteWallEndCut(bool lower, double gap, double heightDifference,
        bool reciprocal, double? bottom, double? top)
    {
        Guid story = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid material = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5");
        Guid layer = new("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d");
        Guid composite = new("c7ecb629-3cf3-4c9c-a222-11082672e04b");
        Guid junction = new("324477ff-7a26-4d53-a38e-8f39f678c2b8");
        Guid wallRole = new("88f6f041-4ce9-43cd-b0c1-056bf842bf5a");
        Guid slabRole = new("ae1816f7-dc76-4857-b0e9-fbb3378a3800");
        Guid wall = Guid.NewGuid(), left = Guid.NewGuid(), right = Guid.NewGuid();
        Guid leftLink = Guid.NewGuid(), rightLink = Guid.NewGuid();
        const int slotSize = 1024;
        byte[] pool = new byte[5 * slotSize];
        var entries = new List<ObjectDirectoryRecord>();
        var target = WallWithPlanes(1, -3.75);
        WriteWallStrip(target.AsSpan(209), alongX: true, 0.125);
        Add(1, wall, WithGeometryRelations(target, GeometryRelationList(1, wallRole, leftLink, rightLink)));
        double elevation = lower ? 1.25 : 3.25;
        AddSlab(left, leftLink, -1, 0, elevation, reciprocal: true);
        AddSlab(right, rightLink, gap, 1, elevation + heightDifference, reciprocal);

        using var doc = Open(ClassTable((1, WallClass), (2, SlabClass), (3, junction), (4, story),
                (7, material), (8, layer), (10, composite)),
            OwnedDirectory(false, Guid.Empty, (ulong)pool.Length / 16, entries.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(story), ver: 1, kind: 4),
            JunctionAttribute(material, 7, 1, 1000), JunctionAttribute(material, 7, 2, 1200),
            JunctionAttribute(material, 7, 3, 800), JunctionAttribute(layer, 8, 1, 1),
            CompositeForWallTest(composite, splitCuts: false), Pool(pool));
        var result = Assert.Single(doc.ReadElements(), item => item.Guid == wall);
        Assert.Equal(bottom, result.BottomElevationToHomeStory);
        Assert.Equal(bottom - 4, result.BottomElevationToProjectZero);
        Assert.Equal(top, result.TopElevationToHomeStory);
        Assert.Equal(top - 4, result.TopElevationToProjectZero);

        void AddSlab(Guid id, Guid link, double start, double end, double slabTop, bool reciprocal)
        {
            var body = OpeningSlab(0, slabTop - 4, 1);
            var settings = body.AsSpan(191);
            BinaryPrimitives.WriteUInt16LittleEndian(settings[83..], 2);
            BinaryPrimitives.WriteUInt16LittleEndian(settings[87..], 1);
            WriteRectangularPlan(settings[270..], start, -1, end, 1, slab: true);
            Add(2, id, WithGeometryRelations(body, GeometryRelation(0x11, slabRole, reciprocal ? link : Guid.NewGuid())));
            Add(3, link, WithGeometryRelations(junction.ToByteArray(), HostRelation(wallRole, wall), HostRelation(slabRole, id)));
        }

        void Add(ushort kind, Guid id, byte[] body)
        {
            int slot = entries.Count;
            Synthetic.Block(PlaBlockMode.Raw, body, ver: 1, guid: id, extra: 1, kind: kind).CopyTo(pool, slot * slotSize);
            entries.Add(new(kind, (ulong)slot * (slotSize / 16), slotSize / 16, id));
        }
    }
}
