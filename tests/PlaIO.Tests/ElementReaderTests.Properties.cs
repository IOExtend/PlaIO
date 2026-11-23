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
    public void ReadsUserPropertyCatalogWithAvailabilityChoicesAndDefaults(bool framed)
    {
        var fixture = PropertyCatalogFixture(framed: framed);
        using var document = PlaDocument.Open(new MemoryStream(fixture.File));
        var catalog = document.ReadPropertyCatalog();
        Assert.Equal(new PlaPropertyGroup(fixture.Group, "Параметры"), Assert.Single(catalog.Groups));
        var byType = catalog.Definitions.ToDictionary(p => p.DataType!);
        Assert.Equal(5, byType.Count);
        Assert.All(byType.Values, definition => Assert.Equal(fixture.Group, definition.GroupGuid));
        var length = byType["length"];
        Assert.Equal(fixture.Length, length.Guid);
        Assert.Equal("Размер", length.Name);
        Assert.Equal("Две\r\nстроки", length.Description);
        Assert.Equal([fixture.FirstClass, fixture.SecondClass], length.AvailableClassificationItemGuids);
        Assert.Null(length.Choices);
        Assert.Equal(new PlaPropertyValue("normal", 0.125), length.DefaultValue);
        Assert.Equal([fixture.SecondClass], byType["integer"].AvailableClassificationItemGuids);
        Assert.Equal(new PlaPropertyValue("userUndefined", null), byType["integer"].DefaultValue);
        Assert.Empty(byType["string"].AvailableClassificationItemGuids);
        Assert.Equal(new PlaPropertyValue("expression", null), byType["string"].DefaultValue);
        var single = byType["singleEnum"];
        Assert.Equal([new PlaPropertyChoice(fixture.Choice, "Étage")], single.Choices);
        Assert.Equal(new PlaPropertyValue("normal", fixture.Choice), single.DefaultValue);
        Assert.Equal(new PlaPropertyValue("userUndefined", null), byType["multiEnum"].DefaultValue);
    }

    [Theory]
    [InlineData("wrong group")]
    [InlineData("short choice")]
    [InlineData("missing selected choice")]
    [InlineData("truncated availability")]
    [InlineData("truncated compressed chain")]
    public void RejectsMalformedUserPropertyCatalog(string error)
    {
        using var document = PlaDocument.Open(new MemoryStream(PropertyCatalogFixture(error).File));
        Assert.Throws<InvalidDataException>(document.ReadPropertyCatalog);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsOnlyOverridesLinkedByTheirDefinition(bool reciprocal)
    {
        var fixture = PropertyCatalogFixture(reciprocal ? null : "unlinked override", withOverride: true);
        using var document = PlaDocument.Open(new MemoryStream(fixture.File));
        var element = Assert.Single(document.ReadElements());
        Assert.Equal(fixture.Element, element.Guid);
        if (!reciprocal)
        {
            Assert.Null(element.PropertyOverrides);
            return;
        }
        var overrides = element.PropertyOverrides!.ToDictionary(entry => entry.DefinitionGuid);
        Assert.Equal(2, overrides.Count);
        Assert.Equal(new PlaPropertyValue("normal", 0.5), overrides[fixture.Length].Value);
        Assert.Equal(new PlaPropertyValue("normal", fixture.Choice), overrides[fixture.Single].Value);
    }

    internal static (byte[] File, Guid Group, Guid Length, Guid FirstClass, Guid SecondClass, Guid Choice,
        Guid Single, Guid Element) PropertyCatalogFixture(string? error = null, bool framed = false, bool withOverride = false,
        bool classified = false)
    {
        Guid groupType = new("e9cdf1dc-9117-46b6-bd32-5adba41a52c2");
        Guid propertyType = new("1f11a777-d5f5-4336-9b5a-963800153712");
        Guid groupLink = new("6f061922-6cfd-40c7-bb63-bcb4ed6c577a");
        Guid availability = new("b0b13d7f-53e9-444e-81aa-a11e9b3227fd");
        Guid overrideType = new("8dd49051-b081-4f20-a4a7-f6375145640d");
        Guid overrideLink = new("6d8a4425-a61e-4dd0-8978-9f90fac91e16");
        Guid elementLink = new("7306b00e-ff8d-4051-a8b3-f76cfc597704");
        Guid meshType = new("8f2fee46-6a3f-41ac-a32b-69e44dc6276d");
        Guid scalarType = new("512896ad-f784-49fb-b45e-b741e45ccc7a");
        Guid enumType = new("87fecb96-0931-4ca1-a911-ac6ba507502a");
        Guid realValue = new("e0bf337e-40c2-43c4-86e5-fb8f2186d7f1");
        Guid intValue = new("bea7c323-acfb-4de4-83fa-22f8439fb713");
        Guid textValue = new("b0485a4f-f7a8-4798-a06a-8efa6ec97e59");
        Guid singleValue = new("7f8ba35b-ed11-4752-aff1-d4bcf0a0317d");
        Guid multipleValue = new("deb5c8a0-7fd8-4428-a1d4-1ef876cbf0be");

        Guid group = Guid.NewGuid(), length = Guid.NewGuid(), integer = Guid.NewGuid(), text = Guid.NewGuid();
        Guid single = Guid.NewGuid(), multiple = Guid.NewGuid(), choice = Guid.NewGuid(), secondChoice = Guid.NewGuid();
        Guid firstClass = Guid.NewGuid(), secondClass = Guid.NewGuid();
        Guid systemType = new("420f4c27-70b8-43c2-9d9d-f74ef1ee9b47");
        Guid itemType = new("9be1f9d1-13d9-45ff-813e-01c7846cd0c2");
        Guid element = Guid.NewGuid(), lengthOverride = Guid.NewGuid(), choiceOverride = Guid.NewGuid();
        var lengthFrame = Value(realValue, 0, BitConverter.GetBytes(0.125));
        var intFrame = Value(intValue, 3, []);
        var formula = Synthetic.Concat([2, 0, 2, 0, 2, 0, 0, 0, 1, 0, 1, 1, 0], Guid.NewGuid().ToByteArray(),
            new byte[64]);
        var singleFrame = Value(singleValue, 0, (error == "missing selected choice" ? Guid.NewGuid() : choice).ToByteArray());
        var multiFrame = Value(multipleValue, 3, []);
        var list = Relation(1, availability, firstClass, secondClass);
        if (error == "truncated availability") list = list[..^16];

        bool linkOverrides = withOverride && error != "unlinked override";
        var blocks = new List<byte[]>
        {
            ClassTable((5, groupType), (6, propertyType), (7, overrideType), (8, meshType), (9, systemType), (10, itemType)),
            Record(group, groupType, 5, Synthetic.Concat(Text("Параметры"), Text(""), [0]),
                Relation(1, groupLink, length, integer, text, single, multiple)),
            Definition(length, "Размер", "Две\r\nстроки", Scalar(2, 1, lengthFrame), list,
                linkOverrides ? Relation(0x11, overrideLink, lengthOverride) : []),
            Definition(integer, "Count", "", Scalar(1, 0, intFrame), Relation(0x11, availability, secondClass)),
            Definition(text, "Formula", "", Scalar(3, 0, formula)),
            Definition(single, "Pick one", "", Enumeration(choice, false, singleFrame),
                classified ? Relation(0x11, availability, secondClass) : [],
                linkOverrides ? Relation(0x11, overrideLink, choiceOverride) : []),
            Definition(multiple, "Pick several", "", Enumeration(secondChoice, true, multiFrame))
        };
        if (withOverride)
        {
            var pool = new byte[3 * 512];
            Override(lengthOverride, length, realValue, BitConverter.GetBytes(0.5)).CopyTo(pool, 512);
            Override(choiceOverride, single, singleValue, choice.ToByteArray()).CopyTo(pool, 1024);
            blocks.Add(OwnedDirectory(false, Guid.Empty, 96, new(8, 0, 32, element), new(7, 32, 32, lengthOverride),
                new(7, 64, 32, choiceOverride)));
            blocks.Add(Pool(pool));
        }
        if (classified)
        {
            Guid system = Guid.NewGuid(), root = Guid.NewGuid();
            Guid rootLink = new("e88a0cef-00c8-45d1-ae13-80c6616ad0f8");
            Guid treeLink = new("8d79244e-35bb-412f-8148-2b4a8839159a");
            Guid assignment = new("9534032d-da8d-4c44-b42b-6363688cafc6");
            blocks.Add(ClassificationBlock(9, system, systemType, 1, false,
                [Text("Classes"), Text("1"), [0xE8, 7, 2, 0, 29, 0], Text(""), Text("")],
                [Relation(0x11, rootLink, root)]));
            blocks.Add(ClassificationBlock(10, root, itemType, 3, false, [Text("root"), Text(""), Text("")],
                [Relation(0x12, rootLink, system), Relation(0x11, treeLink, secondClass)]));
            blocks.Add(ClassificationBlock(10, secondClass, itemType, 3, false, [Text("B"), Text(""), Text("")],
                [Relation(0x12, treeLink, root), Relation(0x12, assignment, element)]));
        }
        return (Synthetic.Fdb(blocks.ToArray()), group, length, firstClass, secondClass, choice, single, element);

        byte[] Override(Guid id, Guid definition, Guid valueClass, byte[] value)
        {
            byte[] prefix = new byte[39];
            overrideType.TryWriteBytes(prefix);
            var fields = Synthetic.Concat([2, 0, 1, 0, 3, 0, 0], valueClass.ToByteArray(), [2, 0, 2, 0, 0, 0, 0, 0], value);
            BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(35), fields.Length);
            var links = Synthetic.Concat([1, 0], Relation(0x12, elementLink, element),
                Relation(0x12, overrideLink, definition), [0]);
            return Synthetic.Block(PlaBlockMode.Raw, Synthetic.Concat(prefix, fields, BitConverter.GetBytes(links.Length), links),
                ver: 1, kind: 7, guid: id, extra: 1);
        }

        byte[] Definition(Guid id, string name, string description, byte[] value, params byte[][] links) =>
            Record(id, propertyType, 6, Synthetic.Concat(Text(name), Text(description), value, [1]),
                [.. links, Relation(0x12, groupLink, error == "wrong group" ? Guid.NewGuid() : group)]);

        byte[] Scalar(byte kind, byte unit, byte[] value) =>
            Synthetic.Concat(scalarType.ToByteArray(), [1, 0, kind, 0, unit, 0, 0, 0, 1, 0], value);

        byte[] Enumeration(Guid key, bool multi, byte[] value)
        {
            var header = new byte[] { 2, 0, 2, 0, 1, 0, 3, 0, 1, 0, 1, 0, 0, 0 };
            if (error == "short choice") return Synthetic.Concat(enumType.ToByteArray(), header);
            return Synthetic.Concat(enumType.ToByteArray(), header, [2], key.ToByteArray(), textValue.ToByteArray(),
                [2, 0, 2, 0, 0, 0, 0, 0], Text("Étage"), [1, 0, 1, 0, 0, 0], key.ToByteArray(),
                [1, 0, 0, 0, 0, 0, multi ? (byte)1 : (byte)0, 0, 0, 0, 0, 1, 0], value);
        }

        static byte[] Value(Guid valueClass, byte status, byte[] bytes) =>
            Synthetic.Concat([1, 0], valueClass.ToByteArray(), [2, 0, 2, 0, status, 0, 0, 0], bytes);

        byte[] Record(Guid id, Guid type, ushort kind, byte[] fields, params byte[][] relations)
        {
            var envelope = new byte[] { 0x83, 0, 3, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
            var body = new byte[kind == 5 ? 61 : 57];
            type.TryWriteBytes(body);
            if (kind == 5)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(39), 0x0102);
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(41), fields.Length + envelope.Length);
            }
            else
                BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(39), 3);
            envelope.CopyTo(body, body.Length - envelope.Length);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(35), body.Length + fields.Length - 39);
            var tail = Synthetic.Concat([new byte[] { 1, 0 }, .. relations, new byte[] { 0 }]);
            var payload = Synthetic.Concat(body, fields, BitConverter.GetBytes(tail.Length), tail);
            if (kind == 6 && (framed || error == "truncated compressed chain"))
            {
                var first = Synthetic.QuickLzFrame(payload[..^16], payload.Length - 16, 0x46);
                var literalTail = Synthetic.Concat([0, 0, 0, 0x80], payload[^16..]);
                var second = Synthetic.QuickLzFrame(literalTail, 16, 0x45);
                if (error == "truncated compressed chain") second = second[..^1];
                return Synthetic.Block(PlaBlockMode.Lz, Synthetic.Concat(first, second), ver: 1, kind: kind, guid: id,
                    unpacked: (uint)payload.Length, extra: 1);
            }
            return Synthetic.Block(PlaBlockMode.Raw, payload, ver: 1, kind: kind, guid: id);
        }

        static byte[] Text(string value) =>
            Synthetic.Concat([1, 0], BitConverter.GetBytes(value.Length), Encoding.Unicode.GetBytes(value));

        static byte[] Relation(byte tag, Guid key, params Guid[] targets)
        {
            var header = Synthetic.Concat([tag], key.ToByteArray());
            if (tag == 1) header = Synthetic.Concat(header, BitConverter.GetBytes(targets.Length));
            return Synthetic.Concat([header, .. targets.Select(id => id.ToByteArray())]);
        }
    }
}
