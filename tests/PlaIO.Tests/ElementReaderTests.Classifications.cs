using System.Buffers.Binary;
using System.Text;
using PlaIO.Model;
using Xunit;

namespace PlaIO.Tests;

public partial class ElementReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsClassificationTreesAndElementAssignments(bool olderFrame)
    {
        var fixture = ClassificationFixture(olderFrame: olderFrame);
        using var document = PlaDocument.Open(new MemoryStream(fixture.File));
        var systems = document.ReadClassificationSystems();
        Assert.Collection(systems, first =>
        {
            Assert.Equal((fixture.First, "System А", "Two\r\nlines", "", "3.1", new DateOnly(2024, 2, 29)),
                (first.Guid, first.Name, first.Description, first.Source, first.Version, first.Date!.Value));
            Assert.Equal([new PlaClassificationItem(fixture.Branch, null, "B", "", ""),
                new PlaClassificationItem(fixture.Leaf, fixture.Branch, "B.1", "Деталь", ""),
                new PlaClassificationItem(fixture.Sibling, null, "A", "", "")], first.Items);
        }, second =>
        {
            Assert.Equal(fixture.Second, second.Guid);
            Assert.Equal(fixture.Other, Assert.Single(second.Items).Guid);
        });

        var elements = document.ReadElements().ToDictionary(e => e.Guid);
        Assert.Equal(2, elements.Count);
        Assert.Equal([new PlaClassificationAssignment(fixture.First, fixture.Leaf),
            new PlaClassificationAssignment(fixture.Second, fixture.Other)], elements[fixture.Classified].Classifications);
        Assert.Empty(elements[fixture.Unclassified].Classifications!);
    }

    [Theory]
    [InlineData("missing child")]
    [InlineData("wrong parent")]
    [InlineData("truncated links")]
    [InlineData("conflicting assignments")]
    public void RejectsMalformedClassificationCatalog(string problem)
    {
        using var document = PlaDocument.Open(new MemoryStream(ClassificationFixture(problem).File));
        Assert.Throws<InvalidDataException>(document.ReadElements);
        if (problem != "conflicting assignments")
            Assert.Throws<InvalidDataException>(document.ReadClassificationSystems);
    }

    internal static (byte[] File, Guid First, Guid Second, Guid Branch, Guid Leaf, Guid Sibling, Guid Other,
        Guid Classified, Guid Unclassified) ClassificationFixture(string? problem = null, bool olderFrame = false)
    {
        Guid systemClass = new("420f4c27-70b8-43c2-9d9d-f74ef1ee9b47");
        Guid itemClass = new("9be1f9d1-13d9-45ff-813e-01c7846cd0c2");
        Guid meshClass = new("8f2fee46-6a3f-41ac-a32b-69e44dc6276d");
        Guid rootLink = new("e88a0cef-00c8-45d1-ae13-80c6616ad0f8");
        Guid treeLink = new("8d79244e-35bb-412f-8148-2b4a8839159a");
        Guid assignment = new("9534032d-da8d-4c44-b42b-6363688cafc6");
        Guid first = new("00000001-0000-0000-0000-000000000000"), second = new("00000002-0000-0000-0000-000000000000");
        Guid firstRoot = Guid.NewGuid(), secondRoot = Guid.NewGuid();
        Guid branch = Guid.NewGuid(), leaf = Guid.NewGuid(), sibling = Guid.NewGuid(), other = Guid.NewGuid();
        Guid classified = Guid.NewGuid(), unclassified = Guid.NewGuid(), outsideModel = Guid.NewGuid();
        byte[] leafAssignments = Relation(2, assignment, classified, outsideModel);
        if (problem == "truncated links") leafAssignments = leafAssignments[..^16];
        var blocks = new List<byte[]>
        {
            ClassTable((6, systemClass), (7, itemClass), (8, meshClass)),
            ElementDirectory(false, new ObjectDirectoryRecord(8, 2, 1, classified), new ObjectDirectoryRecord(8, 3, 1, unclassified)),
            System(second, secondRoot, "System B"),
            Item(secondRoot, "internal-b", "", Relation(0x12, rootLink, second), Relation(0x11, treeLink, other)),
            Item(other, "C", "", Relation(0x12, treeLink, secondRoot), Relation(0x12, assignment, classified)),
            System(first, firstRoot, "System А"),
            Item(firstRoot, "internal-a", "", Relation(0x12, rootLink, first), Relation(1, treeLink, branch, sibling)),
            Item(branch, "B", "", Relation(0x12, treeLink, firstRoot), Relation(0x11, treeLink, leaf)),
            Item(sibling, "A", "", Relation(0x12, treeLink, firstRoot),
                problem == "conflicting assignments" ? Relation(0x12, assignment, classified) : [])
        };
        if (problem != "missing child")
            blocks.Add(Item(leaf, "B.1", "Деталь", Relation(0x12, treeLink, problem == "wrong parent" ? sibling : branch),
                leafAssignments));
        return (Synthetic.Fdb(blocks.ToArray()), first, second, branch, leaf, sibling, other, classified, unclassified);

        byte[] System(Guid id, Guid root, string name) =>
            ClassificationBlock(6, id, systemClass, 1, olderFrame,
                [Text(name), Text("3.1"), [0xE8, 7, 2, 0, 29, 0], Text("Two\r\nlines"), Text("")],
                [Relation(0x11, rootLink, root)]);

        byte[] Item(Guid id, string code, string name, params byte[][] relations) =>
            ClassificationBlock(7, id, itemClass, 3, olderFrame, [Text(code), Text(name), Text("")], relations);

        static byte[] Text(string value)
        {
            var header = new byte[6];
            header[0] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(2), value.Length);
            return Synthetic.Concat(header, Encoding.Unicode.GetBytes(value));
        }

        static byte[] Relation(byte tag, Guid key, params Guid[] targets)
        {
            int headerSize = tag is 1 or 2 ? 21 : 17;
            var relation = new byte[headerSize + 16 * targets.Length];
            relation[0] = tag;
            key.TryWriteBytes(relation.AsSpan(1));
            if (headerSize == 21)
                BinaryPrimitives.WriteInt32LittleEndian(relation.AsSpan(17), targets.Length);
            for (int i = 0; i < targets.Length; i++)
                targets[i].TryWriteBytes(relation.AsSpan(headerSize + 16 * i));
            return relation;
        }
    }

    private static byte[] ClassificationBlock(ushort kind, Guid id, Guid type, byte fieldKind,
        bool olderFrame, byte[][] fields, byte[][] relations)
    {
        byte[] bodyFrame = [1, 0, olderFrame ? (byte)0x82 : (byte)0x83, 0, 3, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, fieldKind, 0];
        var body = Synthetic.Concat([bodyFrame, .. fields, new byte[] { olderFrame ? (byte)0 : (byte)1 }]);
        var links = Synthetic.Concat([new byte[] { 1, 0 }, .. relations, new byte[] { 0 }]);
        var prefix = new byte[39];
        type.TryWriteBytes(prefix);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(35), body.Length);
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, links.Length);
        var payload = Synthetic.Concat(prefix, body, length, links);
        return Synthetic.Block(PlaBlockMode.ZeroMask, Synthetic.ZeroMask(payload), ver: 1,
            kind: kind, guid: id, unpacked: (uint)payload.Length, extra: 1);
    }
}
