using System.Buffers.Binary;
using System.Text;
using PlaIO.Model;
using Xunit;

namespace PlaIO.Tests;

public partial class ElementReaderTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void ReadsMaterialCatalogIncludingPhysicalDirectoryEntries(bool hidden, bool validRoot)
    {
        Guid materialClass = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5");
        Guid compositeClass = new("c7ecb629-3cf3-4c9c-a222-11082672e04b");
        Guid materialId = Guid.NewGuid(), compositeId = Guid.NewGuid(), rootId = Guid.NewGuid();
        var material = NamedAttribute(materialClass, 'M', 8, 47);
        var materialBody = material.AsSpan(61);
        BinaryPrimitives.WriteUInt32LittleEndian(materialBody[37..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(materialBody[41..], 'X');
        BinaryPrimitives.WriteUInt32LittleEndian(materialBody[43..], 5508);
        var materialBlock = Align16(Synthetic.Block(PlaBlockMode.Raw, material, ver: 1, kind: 6, guid: materialId));

        var composite = NamedAttribute(compositeClass, 'C', 5, 45 + 2 * 23);
        var compositeBody = composite.AsSpan(61);
        BinaryPrimitives.WriteUInt16LittleEndian(compositeBody[6..], 12);
        BinaryPrimitives.WriteDoubleLittleEndian(compositeBody[35..], 0.2);
        BinaryPrimitives.WriteUInt16LittleEndian(compositeBody[43..], 2);
        for (int i = 0; i < 2; i++)
        {
            var skin = compositeBody[(45 + 23 * i)..];
            new byte[] { 5, 1, 17, 0, 0, 0, 1, 0 }.CopyTo(skin);
            BinaryPrimitives.WriteUInt16LittleEndian(skin[8..], 8);
            skin[14] = i == 0 ? (byte)5 : (byte)1;
            BinaryPrimitives.WriteDoubleLittleEndian(skin[15..], i == 0 ? 0.025 : 0.175);
        }
        var compositeBlock = Align16(Synthetic.Block(PlaBlockMode.ZeroMask, Synthetic.ZeroMask(composite), ver: 1,
            unpacked: (uint)composite.Length, kind: 7, guid: compositeId, extra: 1));
        var classes = Align16(ClassTable((6, materialClass), (7, compositeClass)));
        PlaDocument document;
        if (!hidden)
            document = Open(classes, compositeBlock, materialBlock);
        else
        {
            var root = Align16(Synthetic.Block(PlaBlockMode.Raw, new byte[8], ver: 1, kind: 8, guid: rootId));
            uint rootUnits = (uint)root.Length / 16, compositeUnits = (uint)compositeBlock.Length / 16;
            uint materialPosition = rootUnits + compositeUnits, materialUnits = (uint)materialBlock.Length / 16;
            var header = new byte[40];
            BinaryPrimitives.WriteUInt64LittleEndian(header, materialPosition + materialUnits);
            new Guid("90b1e208-3b15-45e1-854c-23f5368b97cc").TryWriteBytes(header.AsSpan(24));
            var directory = Align16(Synthetic.Block(PlaBlockMode.Raw, Synthetic.Concat(header,
                Synthetic.Record(new ObjectDirectoryRecord(8, 0, rootUnits, validRoot ? rootId : Guid.NewGuid())),
                Synthetic.Record(new ObjectDirectoryRecord(7, rootUnits, compositeUnits, compositeId)),
                Synthetic.Record(new ObjectDirectoryRecord(6, materialPosition, materialUnits, materialId))),
                ver: 1, kind: 0xFDEB));
            const ushort carrierKind = 9;
            int payloadLength = root.Length + compositeBlock.Length;
            int carrierHeader = Synthetic.Block(PlaBlockMode.Raw, new byte[payloadLength], kind: carrierKind).Length - payloadLength;
            var carrier = Synthetic.Block(PlaBlockMode.Raw, Synthetic.Concat(new byte[16 - carrierHeader], root, compositeBlock),
                kind: carrierKind);
            document = Open(classes, directory, carrier, materialBlock);
            Assert.DoesNotContain(document.Blocks, block => block.Guid == compositeId);
        }

        using (document)
        {
            var catalog = document.ReadMaterialCatalog();
            Assert.Equal(new PlaBuildingMaterial(materialId, 8, "M", "X", 750), Assert.Single(catalog.BuildingMaterials));
            if (!validRoot)
                Assert.Empty(catalog.Composites);
            else
            {
                var recovered = Assert.Single(catalog.Composites);
                Assert.Equal((compositeId, 5, "C", 0.2), (recovered.Guid, recovered.Index, recovered.Name, recovered.TotalThickness));
                Assert.Equal(["Wall", "Slab"], recovered.UseWith);
                Assert.Equal([new PlaCompositeSkin(8, 0.025, false, true), new PlaCompositeSkin(8, 0.175, true, false)], recovered.Skins);
            }
        }

        static byte[] Align16(byte[] block)
        {
            Array.Resize(ref block, (block.Length + 15) & ~15);
            return block;
        }
        static byte[] NamedAttribute(Guid type, char name, int index, int length)
        {
            var data = new byte[61 + length];
            type.TryWriteBytes(data);
            new byte[] { 0x83, 0, 3, 0, 0, 0, 1, 0 }.CopyTo(data, 47);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(55), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(59), name);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(61), 1);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(63), index);
            return data;
        }
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    public void ReadsMaterialReferenceForSupportedWallsAndSlabs(bool wall, ushort structure)
    {
        byte[] body = wall ? WallWithPlanes(8, -3.75) : RectangularSlab(true);
        var settings = body.AsSpan(wall ? 209 : 191);
        int typeOffset = wall ? 225 : 83;
        BinaryPrimitives.WriteUInt16LittleEndian(settings[typeOffset..], structure);
        BinaryPrimitives.WriteInt32LittleEndian(settings[(typeOffset - 10)..], 8);
        BinaryPrimitives.WriteUInt16LittleEndian(settings[(typeOffset + 4)..], 5);

        var element = ReadElementWithStory(wall ? WallClass : SlabClass, body);
        Assert.Equal(structure == 1 ? 8 : (int?)null, element.BuildingMaterialIndex);
        Assert.Equal(structure == 2 ? 5 : (int?)null, element.CompositeIndex);
    }
    private static readonly Guid WallClass = new("4f654582-33af-4e52-abfc-c35d46765ec4");
    private static readonly Guid BeamClass = new("a5c2d285-5a7c-4a68-9554-9eda9d4556ca");
    private static readonly Guid ObjectClass = new("dbefcec3-1bbf-4867-b8b3-c4f7c859df61");
    private static readonly Guid SlabClass = new("945c1275-7948-41d4-8744-9d22d72ca486");
    private static readonly Guid DatabaseClass = new("f6a0e521-443b-4eda-b20a-7160cbe09115");
    private static readonly Guid GridStoreClass = new("e9969c7d-ebe2-42ff-a7dc-ed77e7e274c0");
    private static readonly Guid ProfileAttributeClass = new("31ab5131-81ae-4181-a714-7e4d3dd1a91d");

    [Fact]
    public void ReadsProfileCatalogAndMeasuresArcExtrema()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid(), unsupported = Guid.NewGuid();
        using var doc = Open(ClassTable((7, ProfileAttributeClass)),
            CatalogProfileBlock(second, 9, flags: 32, material: 8, core: false),
            CatalogProfileBlock(first, 2, flags: 31, material: 0, core: true),
            CatalogProfileBlock(unsupported, 12, flags: 4, material: 0, core: false, withSection: false));
        Assert.Collection(doc.ReadProfileCatalog(), p =>
        {
            Assert.Equal((first, 2, "Профиль"), (p.Guid, p.Index, p.Name));
            Assert.Equal(["Wall", "Beam", "Column", "Railing", "Object"], p.UseWith);
            Assert.Equal(2, p.Width);
            Assert.Equal(2, p.Height);
            Assert.True(p.HasCoreSkin);
            var component = Assert.Single(p.Components!);
            Assert.Equal(0, component.BuildingMaterialIndex);
            Assert.Equal(2 + Math.PI / 2, component.CrossSectionArea, 12);
            Assert.Equal(new PlaProfileArc(0, 0, Math.PI), Assert.Single(component.Arcs!));
        }, p =>
        {
            Assert.Equal((second, 9), (p.Guid, p.Index));
            Assert.Null(p.UseWith);
            Assert.False(p.HasCoreSkin);
            Assert.Null(Assert.Single(p.Components!).Function);
        }, p =>
        {
            Assert.Equal((unsupported, 12), (p.Guid, p.Index));
            Assert.Equal(["Beam"], p.UseWith);
            Assert.Null(p.Components);
            Assert.Null(p.Width);
            Assert.Null(p.Height);
            Assert.Null(p.HasCoreSkin);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsConflictingProfileAttributeIndicesAndGuids(bool sameGuid)
    {
        Guid id = Guid.NewGuid();
        using var doc = Open(ClassTable((7, ProfileAttributeClass)),
            CatalogProfileBlock(id, 2, flags: 1, material: 0, core: true),
            CatalogProfileBlock(sameGuid ? id : Guid.NewGuid(), sameGuid ? 4 : 2, flags: 1, material: 0, core: true));
        Assert.Throws<InvalidDataException>(doc.ReadProfileCatalog);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsProfileStretchAndOnlyReciprocallyLinkedModifierNames(bool wrongOwner)
    {
        Guid modifierClass = new("09f84a8c-dcf6-45b4-96aa-e23380acf496");
        Guid definitionClass = new("7fd67a75-4b36-4be8-bf9a-6ce013ab9247");
        Guid ownerRelation = new("d98b1d57-b28c-44bf-92a3-a67c8881ed56");
        Guid definitionRelation = new("43f52d63-b8d0-4659-b981-4401a14823b1");
        Guid profile = Guid.NewGuid(), modifier = Guid.NewGuid(), definition = Guid.NewGuid();
        byte[] extension = new byte[6 + 8 + 128 + 68];
        BinaryPrimitives.WriteUInt16LittleEndian(extension, 1);
        BinaryPrimitives.WriteInt32LittleEndian(extension.AsSpan(2), 1);
        new byte[] { 2, 0, 9, 1 }.CopyTo(extension, 6);
        BinaryPrimitives.WriteInt32LittleEndian(extension.AsSpan(10), 128);
        BinaryPrimitives.WriteInt32LittleEndian(extension.AsSpan(14), 128);
        var stretch = extension.AsSpan(142);
        for (int i = 0; i <= 24; i += 6) stretch[i] = 1;
        stretch[26] = 1;
        BinaryPrimitives.WriteDoubleLittleEndian(stretch[27..], 0.25);
        BinaryPrimitives.WriteDoubleLittleEndian(stretch[35..], 1.75);
        stretch[60] = 2;
        stretch[63] = 4;
        stretch[67] = 1;
        extension = WithGeometryRelations(extension, GeometryRelation(0x11, ownerRelation, modifier));

        byte[] name = System.Text.Encoding.Unicode.GetBytes("Глубина");
        byte[] definitionRecord = new byte[53 + name.Length];
        definitionClass.TryWriteBytes(definitionRecord);
        new byte[] { 0x83, 0, 3, 0, 0, 0, 1, 0 }.CopyTo(definitionRecord, 41);
        BinaryPrimitives.WriteInt32LittleEndian(definitionRecord.AsSpan(49), name.Length / 2);
        name.CopyTo(definitionRecord, 53);
        using var doc = Open(ClassTable((7, ProfileAttributeClass), (8, modifierClass), (9, definitionClass)),
            CatalogProfileBlock(profile, 2, flags: 5, material: 8, core: true, extension: extension),
            Synthetic.Block(PlaBlockMode.Raw, WithGeometryRelations(modifierClass.ToByteArray(),
                HostRelation(ownerRelation, wrongOwner ? Guid.NewGuid() : profile), HostRelation(definitionRelation, definition)),
                ver: 1, kind: 8, guid: modifier, extra: 1),
            Synthetic.Block(PlaBlockMode.Raw, WithGeometryRelations(definitionRecord, GeometryRelation(0x11, definitionRelation, modifier)),
                ver: 1, kind: 9, guid: definition, extra: 1));
        var p = Assert.Single(doc.ReadProfileCatalog());
        Assert.Equal(new PlaProfileStretch(true, 0.25, 1.75, 0.5), p.WidthStretch);
        Assert.Equal(new PlaProfileStretch(false, null, null, 2), p.HeightStretch);
        if (wrongOwner)
            Assert.Null(p.Modifiers);
        else
            Assert.Equal(new PlaProfileModifier(modifier, definition, "Глубина"), Assert.Single(p.Modifiers!));
    }

    internal static byte[] ProfileCatalogFile(Guid id) => Synthetic.Fdb(ClassTable((7, ProfileAttributeClass)),
        CatalogProfileBlock(id, 3, flags: 5, material: 8, core: true));

    [Theory]
    [InlineData("valid", 1.0)]
    [InlineData("negative", -1.0)]
    [InlineData("unknown vertex", null)]
    [InlineData("conflicting dimensions", null)]
    public void MeasuresProfileModifierDefaultFromItsAnchors(string variation, double? expected)
    {
        Guid modifierClass = new("09f84a8c-dcf6-45b4-96aa-e23380acf496");
        Guid definitionClass = new("7fd67a75-4b36-4be8-bf9a-6ce013ab9247");
        Guid ownerRelation = new("d98b1d57-b28c-44bf-92a3-a67c8881ed56");
        Guid definitionRelation = new("43f52d63-b8d0-4659-b981-4401a14823b1");
        Guid profile = Guid.NewGuid(), modifier = Guid.NewGuid(), definition = Guid.NewGuid(), hatch = Guid.NewGuid();
        Guid key = Guid.NewGuid(), group1 = Guid.NewGuid(), group2 = Guid.NewGuid();
        Guid dimension1 = Guid.NewGuid(), dimension2 = Guid.NewGuid();
        Guid edge = Guid.NewGuid(), vertex = Guid.NewGuid(), point = Guid.NewGuid();
        byte[] modifierRecord = new byte[63];
        modifierClass.TryWriteBytes(modifierRecord);
        new byte[] { 1, 0, 0x83, 0, 3, 0, 0, 0 }.CopyTo(modifierRecord, 39);
        key.TryWriteBytes(modifierRecord.AsSpan(47));
        byte[] definitionRecord = new byte[55];
        definitionClass.TryWriteBytes(definitionRecord);
        new byte[] { 0x83, 0, 3, 0, 0, 0, 1, 0 }.CopyTo(definitionRecord, 41);
        BinaryPrimitives.WriteInt32LittleEndian(definitionRecord.AsSpan(49), 1);
        definitionRecord[53] = (byte)'A';

        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data);
        Count(1);
        writer.Write(key.ToByteArray());
        Count(2);
        writer.Write(group1.ToByteArray());
        writer.Write(group2.ToByteArray());
        Count(2);
        Group(group1, dimension1);
        Group(group2, dimension2);
        Count(3);
        Anchor(edge, 1, 1);
        Anchor(vertex, 2, variation == "unknown vertex" ? 99 : 5);
        Anchor(point, 3, 0, variation == "conflicting dimensions" ? 2 : 1);
        Count(2);
        Dimension(dimension1, vertex);
        Dimension(dimension2, point);

        PlaProfilePoint[] outside = [new(0, 0), new(5, 0), new(5, 5), new(0, 5), new(0, 0)];
        PlaProfilePoint[] hole = [new(1, 1), new(1, 3), new(3, 3), new(3, 1), new(1, 1)];
        using var doc = Open(ClassTable((7, ProfileAttributeClass), (8, modifierClass), (9, definitionClass)),
            CatalogProfileBlock(profile, 2, flags: 1, material: 8, core: true,
                extension: WithGeometryRelations(data.ToArray(), GeometryRelation(0x11, ownerRelation, modifier)),
                geometry: (hatch, [outside, hole])),
            Synthetic.Block(PlaBlockMode.Raw, WithGeometryRelations(modifierRecord,
                HostRelation(ownerRelation, profile), HostRelation(definitionRelation, definition)), ver: 1, kind: 8, guid: modifier),
            Synthetic.Block(PlaBlockMode.Raw, WithGeometryRelations(definitionRecord,
                GeometryRelation(0x11, definitionRelation, modifier)), ver: 1, kind: 9, guid: definition));
        var decoded = Assert.Single(Assert.Single(doc.ReadProfileCatalog()).Modifiers!);
        Assert.Equal("A", decoded.Name);
        if (expected is { } value)
            Assert.Equal(value, decoded.DefaultValue!.Value, 12);
        else
            Assert.Null(decoded.DefaultValue);

        void Count(int count)
        {
            writer.Write((ushort)1);
            writer.Write(count);
        }

        void Group(Guid group, Guid dimension)
        {
            writer.Write(group.ToByteArray());
            Count(2);
            writer.Write((ushort)1);
            Count(1);
            writer.Write(new byte[] { 1, 0, 1, 0 });
            writer.Write(hatch.ToByteArray());
            writer.Write(1);
            writer.Write(1);
            writer.Write(dimension.ToByteArray());
        }

        void Anchor(Guid id, int kind, int index, double z = 0)
        {
            var anchor = new byte[67];
            anchor[0] = anchor[6] = anchor[28] = anchor[50] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(anchor.AsSpan(2), kind);
            if (kind is 1 or 2)
            {
                int offset = kind == 1 ? 8 : 30;
                hatch.TryWriteBytes(anchor.AsSpan(offset));
                BinaryPrimitives.WriteInt32LittleEndian(anchor.AsSpan(offset + 16), index);
            }
            else
                BinaryPrimitives.WriteDoubleLittleEndian(anchor.AsSpan(59), z);
            writer.Write(id.ToByteArray());
            writer.Write(anchor);
        }

        void Dimension(Guid id, Guid target)
        {
            writer.Write(id.ToByteArray());
            writer.Write(new byte[] { 1, 1, 92, 0, 0, 0 });
            writer.Write(edge.ToByteArray());
            writer.Write(target.ToByteArray());
            writer.Write(edge.ToByteArray());
            writer.Write(key.ToByteArray());
            writer.Write(key.ToByteArray());
            writer.Write(2);
            writer.Write(variation == "negative" ? -Math.PI / 2 : Math.PI / 2);
        }
    }

    private static byte[] CatalogProfileBlock(Guid id, int index, ushort flags, int material, bool core,
        bool withSection = true, byte[]? extension = null, (Guid Id, PlaProfilePoint[][] Rings)? geometry = null)
    {
        byte[] name = System.Text.Encoding.Unicode.GetBytes("Профиль");
        const int frame = 37;
        int settings = 63 + name.Length;
        PlaProfilePoint[] ring = [new(0, 0), new(2, 0), new(2, 1), new(0, 1), new(0, 0)];
        byte[] hatch = SavedProfileHatch(material, core ? (byte)1 : (byte)2, geometry.HasValue ? 0 : 2,
            geometry?.Rings ?? [ring]);
        int hatchStart = 128 + (geometry.HasValue ? 16 : 0);
        int sectionLength = withSection ? 8 + hatchStart + hatch.Length : 0;
        var data = new byte[settings + frame + sectionLength + (extension?.Length ?? 0)];
        ProfileAttributeClass.TryWriteBytes(data);
        new byte[] { 0x83, 0, 3, 0, 0, 0, 1, 0 }.CopyTo(data, 51);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(59), name.Length / 2);
        name.CopyTo(data, 63);
        var body = data.AsSpan(settings);
        BinaryPrimitives.WriteUInt16LittleEndian(body, 1);
        BinaryPrimitives.WriteInt32LittleEndian(body[2..], index);
        BinaryPrimitives.WriteUInt16LittleEndian(body[35..], flags);
        if (withSection)
        {
            new byte[] { 2, 0, 9, 1 }.CopyTo(body[frame..]);
            BinaryPrimitives.WriteInt32LittleEndian(body[(frame + 4)..], hatchStart + hatch.Length);
            BinaryPrimitives.WriteInt32LittleEndian(body[(frame + 8)..], 128);
            BinaryPrimitives.WriteInt32LittleEndian(body[(frame + 8 + 69)..], 1);
            if (geometry is { } keyed)
                keyed.Id.TryWriteBytes(body[(frame + 8 + hatchStart - 16)..]);
            hatch.CopyTo(body[(frame + 8 + hatchStart)..]);
        }
        extension?.CopyTo(data, settings + frame + sectionLength);
        return Synthetic.Block(PlaBlockMode.Raw, data, ver: 1, kind: 7, guid: id, extra: 1);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(23, true)]
    public void ReadsTheClassGuidAndWallParametersThroughTheFileLocalNumber(ushort number, bool prefixed)
    {
        Guid wallId = Guid.NewGuid(), unknownClass = Guid.NewGuid();
        using var doc = Open(
            ClassTable((number, WallClass), (1, ObjectClass), (2, unknownClass)),
            ElementDirectory(prefixed, new(number, 0, 8, wallId), new(1, 8, 8, Guid.NewGuid()),
                new(2, 16, 8, Guid.NewGuid()), new(number, ulong.MaxValue, 8, Guid.NewGuid())),
            Pool(Subgroup(number, wallId, Wall("Стена-01"))));

        Assert.Collection(doc.ReadObjectClasses(),
            c => Assert.Equal(new PlaObjectClass(1, ObjectClass, "Object"), c),
            c => Assert.Equal(new PlaObjectClass(2, unknownClass, null), c),
            c => Assert.Equal(new PlaObjectClass(number, WallClass, "Wall"), c));
        Assert.Equal(new PlaElement(wallId, "Wall", "Стена-01", 0.25, 5), Assert.Single(doc.ReadElements()));
    }

    [Fact]
    public void IncompleteAndConflictingSubgroupsDoNotSupplyParameters()
    {
        Guid conflict = Guid.NewGuid(), truncated = Guid.NewGuid(), wrongClass = Guid.NewGuid();
        using var doc = Open(
            ClassTable((1, WallClass), (2, BeamClass)),
            ElementDirectory(false, new(1, 0, 8, conflict), new(2, 8, 8, truncated), new(1, 16, 8, wrongClass)),
            Pool(Subgroup(1, conflict, Wall("A")), Subgroup(1, conflict, Wall("B")),
                Subgroup(2, truncated, ElementId("B")[..^1]), Subgroup(2, wrongClass, ElementId("Wrong class"))));

        var elements = doc.ReadElements();
        Assert.Equal(3, elements.Count);
        Assert.All(elements, element => Assert.Null(element.ElementId));
        Assert.All(elements, element => Assert.Null(element.WallThickness));
    }

    [Fact]
    public void MissingClassTableIsReported()
    {
        using var doc = Open(ElementDirectory(false));
        Assert.Throws<InvalidDataException>(doc.ReadElements);
    }

    [Fact]
    public void ReadsSavedStoryCatalogWithoutElementRecords()
    {
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        byte[] table = StoryTable(storyClass);
        BinaryPrimitives.WriteDoubleLittleEndian(table.AsSpan(table.Length - 23), 3);
        using var doc = Open(ClassTable((7, storyClass)), Synthetic.Block(PlaBlockMode.Raw, table, ver: 1, kind: 7));

        Assert.Equal([new PlaStory(-1, "Подвал", -4), new PlaStory(0, "", 0)], doc.ReadStories());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoryCatalogRejectsInvalidRangesAndLeavesConflictingElevationsUnset(bool invalidRange)
    {
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        byte[] first = StoryTable(storyClass), second = StoryTable(storyClass);
        BinaryPrimitives.WriteDoubleLittleEndian(second.AsSpan(52), -6);
        if (invalidRange) BinaryPrimitives.WriteInt32LittleEndian(second.AsSpan(second.Length - 4), 1);
        using var doc = Open(ClassTable((1, storyClass)), Synthetic.Block(PlaBlockMode.Raw, first, ver: 1, kind: 1),
            Synthetic.Block(PlaBlockMode.Raw, second, ver: 1, kind: 1));

        if (invalidRange)
            Assert.Throws<InvalidDataException>(doc.ReadStories);
        else
            Assert.Equal([new PlaStory(-1, "Подвал", null), new PlaStory(0, "", 0)], doc.ReadStories());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsLayerCatalogAndReportsConflictingIdentities(bool conflictingGuid)
    {
        var layerClass = new Guid("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d");
        var builtIn = new Guid("4d22523e-c844-4209-a123-3608c8964de4");
        Guid userLayer = Guid.NewGuid();
        byte[] token = [1, 0, 0, 0, 0x14, 0];
        using var doc = Open(ClassTable((7, layerClass)),
            Layer(8, ElementId("Стены")[14..], 0, userLayer), Layer(1, token, 1, builtIn),
            conflictingGuid ? Layer(9, ElementId("Двери")[14..], 1, userLayer) : Layer(10, token, 1, Guid.NewGuid()));

        if (conflictingGuid)
            Assert.Throws<InvalidDataException>(doc.ReadLayers);
        else
            Assert.Equal([new PlaLayer(builtIn, 1, null, 1), new PlaLayer(userLayer, 8, "Стены", 0)], doc.ReadLayers());

        byte[] Layer(int index, byte[] name, uint group, Guid id)
        {
            byte[] prefix = new byte[55];
            layerClass.TryWriteBytes(prefix);
            new byte[] { 0x83, 0, 3, 0, 0, 0, 1, 0 }.CopyTo(prefix, 47);
            byte[] tail = new byte[39];
            tail[0] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(tail.AsSpan(2), index);
            BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(35), group);
            return Synthetic.Block(PlaBlockMode.Raw, Synthetic.Concat(prefix, name, tail), ver: 1, guid: id, kind: 7);
        }
    }

    [Fact]
    public void SlabElementIdUsesItsOwnClassFrame()
    {
        Guid id = Guid.NewGuid(), wrongFrame = Guid.NewGuid();
        byte[] slab = FramedElementId(SlabClass, 185, "SL-1");
        byte[] other = FramedElementId(WallClass, 185, "SL-1");
        using var doc = Open(ClassTable((1, SlabClass)),
            ElementDirectory(false, new(1, 0, 8, id), new(1, 8, 8, wrongFrame)),
            Pool(Subgroup(1, id, slab), Subgroup(1, wrongFrame, other)));

        Assert.Collection(doc.ReadElements(),
            element => Assert.Equal(new PlaElement(id, "Slab", "SL-1", null, null), element),
            element => Assert.Equal(new PlaElement(wrongFrame, "Slab", null, null, null), element));
    }

    [Theory]
    [InlineData("Opening", "6746aa79-7498-4bea-bad8-191cdad31d05", 185, "", PlaBlockMode.Raw)]
    [InlineData("Lamp", "342a8b55-7348-486a-850b-1eb1bed031fc", 289, "Свет-01", PlaBlockMode.ZeroMask)]
    [InlineData("Stair", "fa0f7f15-29b4-4905-80fa-c6769109847b", 185, "ST-01", PlaBlockMode.Lz)]
    public void ReadsElementIdsFromIndexedRawZeroMaskAndQuickLzRecords(string type, string classId, int offset,
        string elementId, PlaBlockMode mode)
    {
        Guid id = Guid.NewGuid(), classGuid = new(classId);
        byte[] plain = FramedElementId(classGuid, offset, elementId);
        byte[] packed = mode switch
        {
            PlaBlockMode.ZeroMask => Synthetic.ZeroMask(plain),
            PlaBlockMode.Lz => Synthetic.QuickLzFrame(plain.Chunk(31)
                .SelectMany(chunk => Synthetic.Concat([0, 0, 0, 0x80], chunk)).ToArray(), plain.Length, 0x47),
            _ => plain,
        };
        byte[] decodedPool = new byte[512];
        Synthetic.Block(mode, packed, ver: 1, unpacked: (uint)plain.Length, guid: id, extra: 0xFFFFFFFF, kind: 1)
            .CopyTo(decodedPool, 0);
        using var doc = Open(ClassTable((1, classGuid)), ElementDirectory(false, new ObjectDirectoryRecord(1, 0, 32, id)), Pool(decodedPool));

        Assert.Equal(new PlaElement(id, type, elementId, null, null), Assert.Single(doc.ReadElements()));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ZoneIdComesFromTheLinkedStamp(bool matchingGuid, bool validLength)
    {
        Guid zoneClass = new("3ad0332e-efe2-491a-92a6-5b14bfdd7229");
        Guid zoneId = Guid.NewGuid(), stampId = Guid.NewGuid();
        byte[] zone = FramedElementId(zoneClass, 185, "Room-304");
        byte[] relationship = new byte[4 + 19 + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(relationship, validLength ? 35u : 36u);
        relationship[4] = 1;
        relationship[6] = 0x11;
        Assert.True(new Guid("5bbc9f04-754d-408b-8e1b-4f20c33cd868").TryWriteBytes(relationship.AsSpan(7)));
        Assert.True((matchingGuid ? stampId : Guid.NewGuid()).TryWriteBytes(relationship.AsSpan(23)));
        zone = Synthetic.Concat(zone, relationship);

        byte[] decodedPool = new byte[1024];
        Synthetic.Block(PlaBlockMode.Raw, FramedElementId(ObjectClass, 289, "ZN-01"), ver: 1, guid: stampId, extra: 1, kind: 2)
            .CopyTo(decodedPool, 0);
        Synthetic.Block(PlaBlockMode.Raw, zone, ver: 1, guid: zoneId, extra: 1, kind: 1).CopyTo(decodedPool, 512);
        using var doc = Open(ClassTable((1, zoneClass), (2, ObjectClass)),
            OwnedDirectory(false, Guid.Empty, 64, new(2, 0, 32, stampId), new(1, 32, 32, zoneId)), Pool(decodedPool));

        Assert.Equal(new PlaElement(zoneId, "Zone", matchingGuid && validLength ? "ZN-01" : null, null, null),
            Assert.Single(doc.ReadElements()));
    }

    [Theory]
    [InlineData(true, 1, 0)]
    [InlineData(true, 2, 0)]
    [InlineData(false, 1, 0)]
    [InlineData(true, 0, 1)]
    [InlineData(true, 3, 2)]
    [InlineData(true, 1, 3)]
    [InlineData(true, 1, 0, 1)]
    [InlineData(true, 1, 0, 2)]
    [InlineData(true, 1, 0, 3)]
    [InlineData(true, 1, 0, 4)]
    public void ReadsHostedLocationsAndVerifiesDoorHostAndDimensions(bool matchingHost, int reciprocal, int invalidDimensions,
        int placementCase = 0)
    {
        var fixture = HostedElementsFixture(matchingHost, reciprocal, invalidDimensions, placementCase);
        var (wallId, doorId, openingId) = (fixture.Wall, fixture.Door, fixture.Opening);
        using var doc = PlaDocument.Open(new MemoryStream(fixture.File));
        var elements = doc.ReadElements().ToDictionary(e => e.Guid);
        Assert.Equal((17, "Несущие стены", -1, "Подвал"), Location(elements[wallId]));
        Assert.Equal((matchingHost ? 17 : (int?)null, matchingHost ? "Несущие стены" : null, 0, ""), Location(elements[doorId]));
        Assert.Equal(matchingHost && reciprocal is 1 or 2 ? wallId : (Guid?)null, elements[doorId].HostGuid);
        PlaDoorDimensions? dimensions = invalidDimensions is 1 or 2 ? null :
            new PlaDoorDimensions(0.9, 2.1) { SillHeightFromAnchor = invalidDimensions == 3 ? null : 0.1 };
        Assert.Equal(dimensions, elements[doorId].DoorDimensions);
        var point = placementCase == 1 ? new PlaPlanPoint(3.25, 5) : new PlaPlanPoint(1.75, 3);
        PlaDoorPlacement? placement = matchingHost && reciprocal is 1 or 2 && invalidDimensions == 0 && placementCase is 0 or 1
            ? new PlaDoorPlacement(point, 1.25, -3.65, 0.35) : null;
        Assert.Equal(placement, elements[doorId].DoorPlacement);
        Assert.Equal(matchingHost ? Location(elements[wallId]) : (null, null, null, null), Location(elements[openingId]));
    }

    internal static (byte[] File, Guid Wall, Guid Door, Guid Opening) HostedElementsFixture(bool matchingHost,
        int reciprocal, int invalidDimensions, int placementCase)
    {
        Guid doorClass = new("d8f07689-9cfa-4fbe-aeb4-0a60b8e667ee");
        Guid openingClass = new("6746aa79-7498-4bea-bad8-191cdad31d05");
        Guid layerClass = new("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d");
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid doorHost = new("1f6bfe02-d60c-40ae-9ac8-c3953ce12814");
        Guid wallId = Guid.NewGuid(), doorId = Guid.NewGuid(), openingId = Guid.NewGuid();
        Guid host = matchingHost ? wallId : Guid.NewGuid();

        byte[] wall = WallWithPlanes(1, -3.75);
        var wallSettings = wall.AsSpan(209);
        bool reverse = placementCase == 1;
        BinaryPrimitives.WriteDoubleLittleEndian(wallSettings, reverse ? 4 : 1);
        BinaryPrimitives.WriteDoubleLittleEndian(wallSettings[8..], reverse ? 6 : 2);
        BinaryPrimitives.WriteDoubleLittleEndian(wallSettings[16..], reverse ? 1 : 4);
        BinaryPrimitives.WriteDoubleLittleEndian(wallSettings[24..], reverse ? 2 : 6);
        if (placementCase == 2) wallSettings[184] = 1;
        WriteLocation(wall.AsSpan(163), 17, -1);
        if (reciprocal != 0)
        {
            wall = WithGeometryRelations(wall, reciprocal == 1 ? GeometryRelation(0x11, doorHost, doorId) :
                GeometryRelationList(1, doorHost, reciprocal == 2 ? Guid.NewGuid() : doorId, doorId));
        }
        byte[] door = FramedElementId(doorClass, 286, "DR-01");
        WriteLocation(door.AsSpan(175), 34, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(door.AsSpan(185), 1);
        BinaryPrimitives.WriteDoubleLittleEndian(door.AsSpan(202), invalidDimensions == 1 ? double.NaN : 0.9);
        BinaryPrimitives.WriteDoubleLittleEndian(door.AsSpan(210), 2.1);
        BinaryPrimitives.WriteUInt32LittleEndian(door.AsSpan(268), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(door.AsSpan(272), invalidDimensions == 2 ? (ushort)0 : (ushort)83);
        BinaryPrimitives.WriteUInt16LittleEndian(door.AsSpan(274), 2);
        var tail = new byte[72];
        new byte[] { 0, 1, 0, 14, 0, 0, 0, 63, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 255, 0, 0 }.CopyTo(tail, 0);
        if (invalidDimensions == 3) tail[15] = 0;
        BinaryPrimitives.WriteDoubleLittleEndian(tail.AsSpan(24), placementCase == 3 ? 5.1 : 1.25);
        BinaryPrimitives.WriteDoubleLittleEndian(tail.AsSpan(48), 1);
        if (placementCase == 4) BinaryPrimitives.WriteDoubleLittleEndian(tail.AsSpan(56), 0.25);
        BinaryPrimitives.WriteDoubleLittleEndian(tail.AsSpan(64), 0.1);
        door = WithGeometryRelations(Synthetic.Concat(door, tail), GeometryRelation(0x12, doorHost, host));
        byte[] opening = Synthetic.Concat(FramedElementId(openingClass, 185, "OP-01"),
            HostRelation(new Guid("49f5b35f-3e0e-4644-a659-d024c2c93228"), host));
        byte[] pool = new byte[3 * 1024];
        Synthetic.Block(PlaBlockMode.Raw, wall, ver: 1, guid: wallId, extra: 1, kind: 1).CopyTo(pool, 0);
        Synthetic.Block(PlaBlockMode.Raw, door, ver: 1, guid: doorId, extra: 1, kind: 2).CopyTo(pool, 1024);
        Synthetic.Block(PlaBlockMode.Raw, opening, ver: 1, guid: openingId, extra: 1, kind: 3).CopyTo(pool, 2048);

        byte[] layer = new byte[55];
        Assert.True(layerClass.TryWriteBytes(layer));
        byte[] layerNameFrame = [0x83, 0, 3, 0, 0, 0, 1, 0];
        layerNameFrame.CopyTo(layer, 47);
        layer = Synthetic.Concat(layer, ElementId("Несущие стены")[14..], [1, 0, 17, 0, 0, 0]);
        var file = Synthetic.Fdb(ClassTable((1, WallClass), (2, doorClass), (3, openingClass), (4, layerClass), (5, storyClass)),
            OwnedDirectory(false, Guid.Empty, 192, new(1, 0, 64, wallId), new(2, 64, 64, doorId), new(3, 128, 64, openingId)),
            Synthetic.Block(PlaBlockMode.ZeroMask, Synthetic.ZeroMask(layer), ver: 1, unpacked: (uint)layer.Length, kind: 0xFFDC),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 5), Pool(pool));
        return (file, wallId, doorId, openingId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void ReadsOpeningProfileAndReciprocalCutTargets(int variation)
    {
        Guid openingClass = new("6746aa79-7498-4bea-bad8-191cdad31d05");
        Guid cutClass = new("9a756127-8689-486d-8873-5c1d7b0f8785");
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid hostRole = new("49f5b35f-3e0e-4644-a659-d024c2c93228");
        Guid openingRole = new("219fcd20-e2ef-4e61-bb1b-a771897ff878");
        Guid targetRole = new("fc9598c9-7004-45ef-93e0-8fdcbc306acf");
        Guid openingId = Guid.NewGuid(), wallId = Guid.NewGuid(), slabId = Guid.NewGuid();
        Guid wallCut = Guid.NewGuid(), slabCut = Guid.NewGuid();

        var wall = WallWithPlanes(1, -3.75);
        WriteLocation(wall.AsSpan(163), 2, -1);
        wall = WithGeometryRelations(wall,
            GeometryRelationList(1, hostRole, variation == 3 ? Guid.NewGuid() : openingId),
            GeometryRelation(0x11, targetRole, variation == 4 ? Guid.NewGuid() : wallCut));
        var slab = WithGeometryRelations(RectangularSlab(true), GeometryRelation(0x11, targetRole, slabCut));

        var settings = OpeningSettings(variation == 1 ? 1u : 2u, 0.5, 0.75, linked: variation != 1);
        double center = variation == 7 ? 0.375 : 2;
        BinaryPrimitives.WriteDoubleLittleEndian(settings.AsSpan(270), center);
        if (variation == 5) settings[307] = 2;
        BinaryPrimitives.WriteDoubleLittleEndian(settings.AsSpan(variation == 6 ? 190 : 198), 1);
        var opening = WithGeometryRelations(Synthetic.Concat(FramedElementId(openingClass, 185, "OP"), settings),
            HostRelation(hostRole, wallId),
            variation == 2 ? GeometryRelationList(1, openingRole, slabCut, wallCut) : GeometryRelation(0x11, openingRole, wallCut));

        byte[] pool = new byte[5 * 1024];
        var records = new List<ObjectDirectoryRecord>();
        void Add(ushort kind, Guid id, byte[] body)
        {
            int block = records.Count;
            Synthetic.Block(PlaBlockMode.Raw, body, ver: 1, guid: id, extra: 1, kind: kind).CopyTo(pool, block * 1024);
            records.Add(new ObjectDirectoryRecord(kind, (ulong)block * 64, 64, id));
        }
        Add(1, openingId, opening);
        Add(2, wallId, wall);
        Add(3, slabId, slab);
        Add(4, wallCut, WithGeometryRelations(cutClass.ToByteArray(), HostRelation(openingRole, openingId), HostRelation(targetRole, wallId)));
        Add(4, slabCut, WithGeometryRelations(cutClass.ToByteArray(), HostRelation(openingRole, openingId), HostRelation(targetRole, slabId)));
        using var doc = Open(ClassTable((1, openingClass), (2, WallClass), (3, SlabClass), (4, cutClass), (5, storyClass)),
            OwnedDirectory(false, Guid.Empty, (ulong)pool.Length / 16, records.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 5), Pool(pool));
        var elements = doc.ReadElements();
        var element = Assert.Single(elements, item => item.Guid == openingId);
        Assert.Equal(variation == 3 ? null : (Guid?)wallId, element.HostGuid);
        if (variation is 3 or 4)
            Assert.Null(element.OpeningCutElementGuids);
        else
            Assert.Equal(variation == 2 ? new[] { wallId, slabId }.Order().ToArray() : [wallId], element.OpeningCutElementGuids);
        Assert.Equal(variation == 5 ? null : variation == 1 ? new PlaOpeningProfile("Rectangle", 0.5, 0.75) :
            new PlaOpeningProfile("Circle", 0.5, 0.5), element.OpeningProfile);

        double? halfHeight = variation is 0 or 7 ? 0.25 : variation == 1 ? 0.375 : null;
        Assert.Equal(center - halfHeight, element.BottomElevationToHomeStory);
        Assert.Equal(center + halfHeight, element.TopElevationToHomeStory);
        Assert.Equal(center - 4 - halfHeight, element.BottomElevationToProjectZero);
        Assert.Equal(center - 4 + halfHeight, element.TopElevationToProjectZero);

        var host = Assert.Single(elements, item => item.Guid == wallId);
        double? wallBottom = variation is 0 or 1 ? 0.25 : null;
        double? wallTop = wallBottom + 3;
        Assert.Equal(wallBottom, host.BottomElevationToHomeStory);
        Assert.Equal(wallTop, host.TopElevationToHomeStory);
        Assert.Equal(wallBottom - 4, host.BottomElevationToProjectZero);
        Assert.Equal(wallTop - 4, host.TopElevationToProjectZero);
    }

    [Theory]
    [InlineData(0, -0.5, 0.5)]
    [InlineData(1, 0.125, 0.5)]
    [InlineData(2, null, null)]
    [InlineData(3, null, null)]
    [InlineData(4, -0.5, 1.0)]
    [InlineData(5, -0.25, 0.5)]
    [InlineData(6, null, null)]
    [InlineData(7, -0.5, 0.5)]
    [InlineData(8, -0.5, 0.5)]
    [InlineData(9, -0.5, 0.5)]
    public void ResolvesSlabOpeningElevationsFromCutLimitsAndOverlaps(int variation, double? bottom, double? top)
    {
        var (file, opening, slab, other, secondSlab) = SlabOpeningFixture(variation);
        using var doc = PlaDocument.Open(new MemoryStream(file));
        var elements = doc.ReadElements();
        var element = Assert.Single(elements, e => e.Guid == opening);
        Assert.Equal(slab, element.HostGuid);
        Assert.Equal(bottom, element.BottomElevationToHomeStory);
        Assert.Equal(top, element.TopElevationToHomeStory);
        Assert.Equal(bottom - 4, element.BottomElevationToProjectZero);
        Assert.Equal(top - 4, element.TopElevationToProjectZero);
        var solid = Assert.IsType<PlaSlabGeometry>(Assert.Single(elements, e => e.Guid == slab).SlabGeometry);
        const double circleArea = 0.19684949293332902;
        Assert.Null(solid.VerticalPrisms);
        if (bottom is null || variation == 8)
        {
            Assert.Null(solid.TopAreaWithOpenings);
            Assert.Null(solid.BottomAreaWithOpenings);
            Assert.Null(solid.Volume);
        }
        else
        {
            Assert.Equal(4 - circleArea, solid.TopAreaWithOpenings!.Value, 12);
            double removedBelow = variation switch { 1 => 2.25, 5 => 0, _ => circleArea };
            Assert.Equal(4 - removedBelow, solid.BottomAreaWithOpenings!.Value, 12);
            double expectedVolume = 4 - circleArea * (0.5 - bottom.Value) - (variation == 1 ? 1.40625 : 0);
            if (variation == 9)
                Assert.Null(solid.Volume);
            else
                Assert.Equal(expectedVolume, solid.Volume!.Value, 12);
        }
        if (variation is 4 or 7)
        {
            var secondary = Assert.IsType<PlaSlabGeometry>(Assert.Single(elements, e => e.Guid == secondSlab).SlabGeometry);
            double area = variation == 7 ? 4 : 4 - circleArea;
            Assert.Equal(area, secondary.TopAreaWithOpenings!.Value, 12);
            Assert.Equal(area, secondary.BottomAreaWithOpenings!.Value, 12);
            Assert.Equal(area * 0.25, secondary.Volume!.Value, 12);
        }
        if (variation == 3)
        {
            Assert.Null(element.OpeningGeometry);
            return;
        }
        var geometry = Assert.IsType<PlaOpeningGeometry>(element.OpeningGeometry);
        Assert.Equal(new PlaPlanPoint(variation == 2 ? 0.875 : 0.25, -0.125), geometry.Origin);
        Assert.Null(geometry.Rings);
        if (bottom is null || variation == 8)
        {
            Assert.Null(geometry.Cuts);
            Assert.Null(geometry.CutVolume);
        }
        else
        {
            Assert.NotNull(geometry.Cuts);
            Assert.Equal(variation == 4 ? 2 : 1, geometry.Cuts.Count);
            Assert.Equal(new PlaOpeningCut(slab, bottom.Value - 4, (variation == 4 ? 0.5 : top!.Value) - 4), geometry.Cuts[0]);
            if (variation == 4) Assert.Equal(new PlaOpeningCut(secondSlab, -3.25, -3), geometry.Cuts[1]);
            Assert.Equal(circleArea * (variation == 4 ? 1.25 : top!.Value - bottom.Value), geometry.CutVolume!.Value, 12);
        }
        if (variation == 1)
        {
            var recess = Assert.IsType<PlaOpeningGeometry>(Assert.Single(elements, e => e.Guid == other).OpeningGeometry);
            Assert.NotNull(recess.Rings);
            Assert.Equal(new PlaPlanPoint(-0.5, -0.875), Assert.Single(recess.Rings)[0]);
            Assert.Equal(new PlaOpeningCut(slab, -4.5, -3.875), Assert.Single(recess.Cuts!));
            Assert.Equal(1.40625, recess.CutVolume);
        }
    }

    internal static (byte[] File, Guid Opening, Guid Slab, Guid Other, Guid SecondSlab) SlabOpeningFixture(
        int variation, bool rectangular = false, bool slabAlignedFrame = false)
    {
        Guid openingClass = new("6746aa79-7498-4bea-bad8-191cdad31d05");
        Guid cutClass = new("9a756127-8689-486d-8873-5c1d7b0f8785");
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid hostRole = new("49f5b35f-3e0e-4644-a659-d024c2c93228");
        Guid openingRole = new("219fcd20-e2ef-4e61-bb1b-a771897ff878");
        Guid targetRole = new("fc9598c9-7004-45ef-93e0-8fdcbc306acf");
        Guid opening = Guid.NewGuid(), slab = Guid.NewGuid(), cut = Guid.NewGuid();
        Guid other = Guid.NewGuid(), secondSlab = Guid.NewGuid(), secondCut = Guid.NewGuid();

        const int slotSize = 2048;
        byte[] pool = new byte[5 * slotSize];
        var records = new List<ObjectDirectoryRecord>();
        void Add(ushort kind, Guid id, byte[] body)
        {
            int block = records.Count;
            Synthetic.Block(PlaBlockMode.Raw, body, ver: 1, guid: id, extra: 1, kind: kind).CopyTo(pool, block * slotSize);
            records.Add(new ObjectDirectoryRecord(kind, (ulong)block * (slotSize / 16), slotSize / 16, id));
        }
        void AddCut(Guid id, Guid source, Guid target) => Add(3, id,
            WithGeometryRelations(cutClass.ToByteArray(), HostRelation(openingRole, source), HostRelation(targetRole, target)));

        var settings = OpeningSettings(rectangular ? 1u : 2u, 0.5, 0.5);
        BinaryPrimitives.WriteDoubleLittleEndian(settings.AsSpan(134), variation switch { 2 => 0.625, 10 => 0.5, _ => 0 });
        BinaryPrimitives.WriteDoubleLittleEndian(settings.AsSpan(158), 1);
        BinaryPrimitives.WriteDoubleLittleEndian(settings.AsSpan(190), 1);
        BinaryPrimitives.WriteDoubleLittleEndian(settings.AsSpan(222), slabAlignedFrame ? -1 : 1);
        if (slabAlignedFrame) BinaryPrimitives.WriteUInt32LittleEndian(settings.AsSpan(266), 2);
        if (variation == 5)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(settings.AsSpan(278), 2);
            BinaryPrimitives.WriteDoubleLittleEndian(settings.AsSpan(282), -0.75);
        }

        bool multipleSlabs = variation is 4 or 7;
        Add(1, opening, WithGeometryRelations(Synthetic.Concat(FramedElementId(openingClass, 185, "OP"), settings),
            HostRelation(hostRole, slab),
            multipleSlabs ? GeometryRelationList(1, openingRole, cut, secondCut) : GeometryRelation(0x11, openingRole, cut)));
        AddCut(cut, opening, slab);
        bool overlapping = variation is 1 or 2 or 6 or 8;
        byte[] slabBody = OpeningSlab(-1, 0.5, 1, variation == 3);
        if (slabAlignedFrame)
        {
            slabBody[191 + 8] = 1;
            BinaryPrimitives.WriteDoubleLittleEndian(slabBody.AsSpan(191), 0.375);
            BinaryPrimitives.WriteDoubleLittleEndian(slabBody.AsSpan(191 + 9), 0.125);
            BinaryPrimitives.WriteUInt16LittleEndian(slabBody.AsSpan(191 + 83), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(slabBody.AsSpan(191 + 87), 1);
        }
        Add(2, slab, WithGeometryRelations(slabBody,
            overlapping ? GeometryRelationList(1, hostRole, opening, other) : GeometryRelation(0x11, hostRole, opening),
            overlapping ? GeometryRelationList(1, targetRole, cut, secondCut) : GeometryRelation(0x11, targetRole, cut),
            variation == 9 ? GeometryRelation(0x11, Guid.NewGuid(), Guid.NewGuid()) : []));

        if (overlapping)
        {
            var recess = OpeningSettings(3, 1.5, 1.5);
            Array.Resize(ref recess, 304 + 247);
            WriteRectangularPlan(recess.AsSpan(304), -0.75, -0.75, 0.75, 0.75, slab: false);
            recess[312] = 1;
            BinaryPrimitives.WriteDoubleLittleEndian(recess.AsSpan(158), 1);
            BinaryPrimitives.WriteDoubleLittleEndian(recess.AsSpan(190), 1);
            BinaryPrimitives.WriteDoubleLittleEndian(recess.AsSpan(222), -1);
            BinaryPrimitives.WriteUInt32LittleEndian(recess.AsSpan(278), variation == 8 ? 0u : 2u);
            BinaryPrimitives.WriteDoubleLittleEndian(recess.AsSpan(282), 0.375);
            Add(1, other, WithGeometryRelations(Synthetic.Concat(FramedElementId(openingClass, 185, "R"), recess),
                HostRelation(hostRole, slab), GeometryRelation(0x11, openingRole, secondCut)));
            AddCut(secondCut, variation == 6 ? Guid.NewGuid() : other, slab);
        }
        if (multipleSlabs)
        {
            var second = OpeningSlab(0, -3, 0.25);
            if (variation == 7) WriteRectangularPlan(second.AsSpan(191 + 270), 2, 2, 4, 4, slab: true);
            Add(2, secondSlab, WithGeometryRelations(second, GeometryRelation(0x11, targetRole, secondCut)));
            AddCut(secondCut, opening, secondSlab);
        }

        var file = Synthetic.Fdb(ClassTable((1, openingClass), (2, SlabClass), (3, cutClass), (4, storyClass)),
            OwnedDirectory(false, Guid.Empty, (ulong)pool.Length / 16, records.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 4), Pool(pool));
        return (file, opening, slab, other, secondSlab);
    }

    [Fact]
    public void ReadsTheTopCenteredCircularOpeningWithoutInferringItsCutVolume()
    {
        var (file, opening, slab, _, _) = SlabOpeningFixture(0, slabAlignedFrame: true);
        using var doc = PlaDocument.Open(new MemoryStream(file));
        var elements = doc.ReadElements();
        var geometry = Assert.IsType<PlaOpeningGeometry>(Assert.Single(elements, e => e.Guid == opening).OpeningGeometry);
        Assert.Equal(new PlaPlanPoint(0.25, -0.375), geometry.Origin);
        Assert.Equal(new PlaOpeningCut(slab, -4.5, -3.5), Assert.Single(geometry.Cuts!));
        Assert.Null(geometry.CutVolume);
        var solid = Assert.IsType<PlaSlabGeometry>(Assert.Single(elements, e => e.Guid == slab).SlabGeometry);
        Assert.Null(solid.Volume);
        Assert.Null(solid.VerticalPrisms);
    }

    private static byte[] OpeningSettings(uint shape, double width, double height, bool linked = false)
    {
        var settings = new byte[363];
        new byte[] { 1, 3, 70, 0, 0, 0 }.CopyTo(settings, 230);
        BinaryPrimitives.WriteUInt32LittleEndian(settings.AsSpan(236), shape);
        BinaryPrimitives.WriteDoubleLittleEndian(settings.AsSpan(240), width);
        BinaryPrimitives.WriteDoubleLittleEndian(settings.AsSpan(248), height);
        new byte[] { 2, 0, 0, 0, 1, 0, 1, 0, 0, 0, 1, 0, 0, 0 }.CopyTo(settings, 256);
        BinaryPrimitives.WriteUInt32LittleEndian(settings.AsSpan(298), linked ? 0u : 1u);
        new byte[] { 0, 0, 2, 1, 59, 0, 0, 0, 1, 31, 0 }.CopyTo(settings, 304);
        return settings;
    }

    private static byte[] OpeningSlab(short story, double reference, double thickness, bool beveled = false)
    {
        byte[] slab = RectangularSlab(true);
        const int edgesOffset = 191 + 270 + 247;
        const int tailOffset = edgesOffset + 16 + 4 * 27;
        Array.Resize(ref slab, tailOffset + 87);
        WriteLocation(slab.AsSpan(163), 1, story);
        slab[191 + 165] = 20;
        BinaryPrimitives.WriteDoubleLittleEndian(slab.AsSpan(191), reference);
        BinaryPrimitives.WriteDoubleLittleEndian(slab.AsSpan(191 + 17), thickness);
        WriteSlabEdges(slab.AsSpan(edgesOffset), 4, beveled ? 1 : -1);
        var tail = slab.AsSpan(tailOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(tail, 2);
        BinaryPrimitives.WriteDoubleLittleEndian(tail[4..], Math.PI / 2);
        new byte[] { 1, 0, 1, 0, 1, 0, 0, 0 }.CopyTo(tail[23..]);
        BinaryPrimitives.WriteDoubleLittleEndian(tail[63..], 0.25);
        BinaryPrimitives.WriteDoubleLittleEndian(tail[71..], -0.125);
        return slab;
    }

    [Theory]
    [InlineData(false, PlaBlockMode.Raw, true, true, true)]
    [InlineData(true, PlaBlockMode.ZeroMask, true, true, true)]
    [InlineData(false, PlaBlockMode.Lz, true, true, true)]
    [InlineData(true, PlaBlockMode.Lz, false, true, true)]
    [InlineData(false, PlaBlockMode.Raw, true, false, true)]
    [InlineData(true, PlaBlockMode.Raw, true, true, false)]
    public void ResolvesTheTopStoryThroughTheTypedPartitionDescriptor(bool column, PlaBlockMode mode,
        bool matchingPartition, bool validDescriptor, bool validElementSection)
    {
        Guid elementClass = column ? new Guid("13e8eb85-5a33-47a3-a4d4-1e05296aec7e") : WallClass;
        Guid topStoryRelation = column ? new Guid("5ebebc2d-cf0b-4751-8f55-7c2353c3fd14") : new Guid("7b48cc47-7267-4cfd-9133-9214ea18fb99");
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid descriptorClass = new("bc046b25-27d6-461a-a6e5-6fa0e8594a95");
        Guid elementId = Guid.NewGuid(), partitionId = Guid.NewGuid();
        byte[] body = column ? FramedElementId(elementClass, 735, "WC") : WallWithPlanes(1, 0.25);
        if (!column)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(209 + 145), 5);
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(209 + 153), 0);
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(209 + 555), 1);
        }
        WriteLocation(body.AsSpan(column ? 701 : 163), 1, 0);
        body = Synthetic.Concat(body, [(byte)(validElementSection ? 36 : 35), 0, 0, 0, 1, 0],
            HostRelation(topStoryRelation, matchingPartition ? partitionId : Guid.NewGuid()), [0]);

        byte[] descriptor = new byte[49];
        Assert.True(descriptorClass.TryWriteBytes(descriptor));
        byte[] frame = [5, 0, 0x83, 0, 3, 0, 0, 0, 1, 0];
        frame.CopyTo(descriptor, 39);
        byte[] tail = [0xFF, 0xFF, 1, 0, 0, (byte)(validDescriptor ? 36 : 35), 0, 0, 0, 1, 0];
        var relation = HostRelation(new Guid("baf95b80-f256-4932-b891-03ff66848591"), partitionId);
        relation[0] = 0x11;
        descriptor = Synthetic.Concat(descriptor, ElementId("Подвал")[14..], tail, relation, [0]);
        var packed = mode switch
        {
            PlaBlockMode.Lz => Synthetic.QuickLzFrame(descriptor, descriptor.Length, 0x46),
            PlaBlockMode.ZeroMask => Synthetic.ZeroMask(descriptor),
            _ => descriptor,
        };

        byte[] pool = new byte[1024];
        Synthetic.Block(PlaBlockMode.Raw, body, ver: 1, guid: elementId, extra: 1, kind: 1).CopyTo(pool, 0);
        using var doc = Open(ClassTable((1, elementClass), (2, storyClass), (3, descriptorClass)),
            OwnedDirectory(false, Guid.Empty, 64, new ObjectDirectoryRecord(1, 0, 64, elementId)), Pool(pool),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 2),
            Synthetic.Block(mode, packed, ver: 1, unpacked: (uint)descriptor.Length, kind: 3));
        var element = Assert.Single(doc.ReadElements());
        Assert.Equal(0, element.HomeStoryIndex);
        Assert.Equal(matchingPartition && validDescriptor && validElementSection ? -1 : (int?)null, element.TopLinkedStoryIndex);
        double? top = !column && matchingPartition && validDescriptor && validElementSection ? 1 : null;
        Assert.Equal(top, element.TopElevationToHomeStory);
        Assert.Equal(top, element.TopElevationToProjectZero);
    }

    [Theory]
    [InlineData(0, -1, 1.25, 0.0, 0.5, true)]
    [InlineData(3, -1, 0.75, 0.5, 0.5, true)]
    [InlineData(3, 2, 0.75, 0.5, 0.5, true)]
    [InlineData(7, -1, 0.75, 0.5, 0.5, false)]
    [InlineData(0, -1, 1.25, 0.0, -double.Epsilon, false)]
    public void ReadsSlabPlanesInMetresInBothOrigins(byte plane, short story, double reference, double offset,
        double thickness, bool supported)
    {
        byte[] slab = FramedElementId(SlabClass, 185, "SL-1");
        WriteLocation(slab.AsSpan(163), 1, story);
        byte[] planes = new byte[25];
        BinaryPrimitives.WriteDoubleLittleEndian(planes, reference);
        planes[8] = plane;
        BinaryPrimitives.WriteDoubleLittleEndian(planes.AsSpan(9), offset);
        BinaryPrimitives.WriteDoubleLittleEndian(planes.AsSpan(17), thickness);

        var element = ReadElementWithStory(SlabClass, Synthetic.Concat(slab, planes));

        Assert.Equal("SL-1", element.ElementId);
        Assert.Equal(supported ? 0.75 : null, element.BottomElevationToHomeStory);
        Assert.Equal(supported ? 1.25 : null, element.TopElevationToHomeStory);
        Assert.Equal(supported && story == -1 ? -3.25 : null, element.BottomElevationToProjectZero);
        Assert.Equal(supported && story == -1 ? -2.75 : null, element.TopElevationToProjectZero);
    }

    [Theory]
    [InlineData(1, 2.5, 24, true)]
    [InlineData(2, 2.5, 24, false)]
    [InlineData(1, 2.5, 23, false)]
    [InlineData(1, -2.5, 24, false)]
    [InlineData(1, double.PositiveInfinity, 24, false)]
    public void ReadsZoneBoundsAfterTheTwoCountedRoomStrings(byte roomNumberTag, double height, int length, bool supported)
    {
        Guid zoneClass = new("3ad0332e-efe2-491a-92a6-5b14bfdd7229");
        byte[] zone = FramedElementId(zoneClass, 253, "Холл");
        WriteLocation(zone.AsSpan(163), 1, -1);
        byte[] levels = new byte[52 + 247];
        BinaryPrimitives.WriteDoubleLittleEndian(levels, -0.25);
        BinaryPrimitives.WriteDoubleLittleEndian(levels.AsSpan(8), height);
        BinaryPrimitives.WriteDoubleLittleEndian(levels.AsSpan(16), 0.125);

        WriteRectangularPlan(levels.AsSpan(52), 1, 2, 4, 6, slab: true);
        levels[52] = 0;
        levels[53] = 1;
        var element = ReadElementWithStory(zoneClass, Synthetic.Concat(zone, [roomNumberTag, 0], ElementId("Б-01")[14..],
            length == 24 ? levels : levels[..length]));

        Assert.Equal("Подвал", element.HomeStoryName);
        Assert.Equal(supported ? -0.125 : null, element.BottomElevationToHomeStory);
        Assert.Equal(supported ? 2.375 : null, element.TopElevationToHomeStory);
        Assert.Equal(supported ? -4.125 : null, element.BottomElevationToProjectZero);
        Assert.Equal(supported ? -1.625 : null, element.TopElevationToProjectZero);
        Assert.Equal(supported ? new PlaBoundingBox(1, 2, -4.125, 4, 6, -1.625) : null, element.BoundingBox);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public void ReadsSlabBoxesFromFramedOutlines(bool compact, bool curved, bool damaged)
    {
        byte[] slab = RectangularSlab(true);
        var settings = slab.AsSpan(191);
        settings[165] = 20;
        if (compact) settings[270..].CopyTo(settings[166..]);
        var polygon = settings[(compact ? 166 : 270)..];
        if (curved)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(polygon[47..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(polygon[155..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(polygon[167..], damaged ? uint.MaxValue : 1);
            BinaryPrimitives.WriteUInt32LittleEndian(polygon[171..], 2);
            BinaryPrimitives.WriteDoubleLittleEndian(polygon[175..], Math.PI / 2);
            BinaryPrimitives.WriteUInt32LittleEndian(polygon[187..], 5);
            BinaryPrimitives.WriteDoubleLittleEndian(polygon[19..], -Math.Sqrt(2));
        }
        else if (damaged)
            BinaryPrimitives.WriteUInt32LittleEndian(polygon[155..], 6);

        var slabElement = ReadElementWithStory(SlabClass, slab);
        Assert.Equal(damaged ? null : new PlaBoundingBox(-1, curved ? -Math.Sqrt(2) : -1, -4.5, 1, 1, -3.5),
            slabElement.BoundingBox);
        if (curved && !damaged)
        {
            var geometry = Assert.IsType<PlaSlabGeometry>(slabElement.SlabGeometry);
            Assert.Equal(new PlaSlabArc(0, 0, Math.PI / 2), Assert.Single(geometry.Arcs!));
            Assert.Equal(3 + Math.PI / 2, geometry.OutlineArea, 12);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ReadsClosingSlabArcAndRejectsIntersectingEdges(bool clockwise, bool crossing)
    {
        var slab = RectangularSlab(true);
        slab[191 + 165] = 20;
        var polygon = slab.AsSpan(191 + 270);
        BinaryPrimitives.WriteUInt32LittleEndian(polygon[47..], 1);
        polygon[151..191].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(polygon[167..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(polygon[171..], 5);
        BinaryPrimitives.WriteDoubleLittleEndian(polygon[175..], crossing ? -1.5 * Math.PI : clockwise ? -Math.PI : Math.PI);
        BinaryPrimitives.WriteUInt32LittleEndian(polygon[187..], 5);
        double span = Math.Sqrt(2);
        BinaryPrimitives.WriteDoubleLittleEndian(polygon[11..], crossing ? -1 : -2);
        if (clockwise)
        {
            Span<byte> start = stackalloc byte[16];
            polygon.Slice(71, 16).CopyTo(start);
            polygon.Slice(119, 16).CopyTo(polygon[71..]);
            start.CopyTo(polygon[119..]);
            polygon.Slice(103, 16).CopyTo(start);
            polygon.Slice(87, 16).CopyTo(polygon[103..]);
            start.CopyTo(polygon[87..]);
            polygon.Slice(71, 16).CopyTo(polygon[135..]);
        }
        if (crossing)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(polygon[19..], -span);
            BinaryPrimitives.WriteDoubleLittleEndian(polygon[27..], span);
            BinaryPrimitives.WriteDoubleLittleEndian(polygon[35..], span);
        }
        var element = ReadElementWithStory(SlabClass, slab);
        Assert.NotNull(element.BoundingBox);
        if (crossing)
        {
            Assert.Null(element.SlabGeometry);
            return;
        }
        var geometry = Assert.IsType<PlaSlabGeometry>(element.SlabGeometry);
        Assert.Equal(new PlaSlabArc(0, 3, Math.PI), Assert.Single(geometry.Arcs!));
        Assert.Equal(4 + Math.PI / 2, geometry.OutlineArea, 12);
        Assert.Equal(6 + Math.PI, geometry.FloorPlanPerimeter!.Value, 12);
        Assert.Equal(0, geometry.FloorPlanHolesPerimeter);
        Assert.Null(geometry.TopAreaWithOpenings);
        Assert.Null(geometry.BottomAreaWithOpenings);
        Assert.Null(geometry.Volume);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TestsSlabHoleContainmentAgainstTheArcRatherThanItsChord(bool inward)
    {
        var slab = RectangularSlab(true);
        slab[191 + 165] = 20;
        var polygon = slab.AsSpan(191 + 270);
        BinaryPrimitives.WriteUInt32LittleEndian(polygon[4..], 275);
        BinaryPrimitives.WriteUInt32LittleEndian(polygon[43..], 10);
        BinaryPrimitives.WriteUInt32LittleEndian(polygon[47..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(polygon[51..], 2);
        BinaryPrimitives.WriteDoubleLittleEndian(polygon[11..], inward ? -1 : -2);
        double x = inward ? -0.5 : -1.5;
        var hole = new PlaPlanPoint[] { new(-1, -0.25), new(-1, 0.25), new(x, 0.25), new(x, -0.25), new(-1, -0.25) };
        for (int i = 0; i < hole.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(polygon[(151 + i * 16)..], hole[i].X);
            BinaryPrimitives.WriteDoubleLittleEndian(polygon[(159 + i * 16)..], hole[i].Y);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(polygon[247..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(polygon[251..], 5);
        BinaryPrimitives.WriteDoubleLittleEndian(polygon[255..], inward ? -Math.PI : Math.PI);
        BinaryPrimitives.WriteUInt32LittleEndian(polygon[267..], 5);
        BinaryPrimitives.WriteUInt32LittleEndian(polygon[271..], 10);
        var element = ReadElementWithStory(SlabClass, slab);
        Assert.NotNull(element.BoundingBox);
        if (inward)
            Assert.Null(element.SlabGeometry);
        else
        {
            var geometry = Assert.IsType<PlaSlabGeometry>(element.SlabGeometry);
            Assert.Equal(3.75 + Math.PI / 2, geometry.OutlineArea, 12);
            Assert.Equal(8 + Math.PI, geometry.FloorPlanPerimeter!.Value, 12);
            Assert.Equal(2, geometry.FloorPlanHolesPerimeter);
        }
    }

    [Theory]
    [InlineData(0, true, true)]
    [InlineData(1, true, false)]
    [InlineData(2, true, false)]
    [InlineData(3, false, false)]
    [InlineData(4, false, false)]
    public void ReadsConcaveSlabOutlinesAndVerifiesExtrusionsWithHoles(int variation, bool readsOutline, bool readsVolume)
    {
        (double X, double Y)[] outer = [(0, 0), (0, 4), (2, 4), (2, 2), (4, 2), (4, 0), (0, 0)];
        if (variation == 4) (outer[1], outer[2]) = (outer[2], outer[1]);
        double h = variation == 3 ? 3 : 0.5;
        (double X, double Y)[] hole = [(h, h), (h + 1, h), (h + 1, h + 1), (h, h + 1), (h, h)];
        var slab = SlabWithStraightRings(variation == 2 ? 1 : -1, outer, hole);
        var element = ReadElementWithStory(SlabClass, WithGeometryRelations(slab,
            variation == 1 ? GeometryRelation(0x11, Guid.NewGuid(), Guid.NewGuid()) : []));
        var geometry = element.SlabGeometry;
        if (!readsOutline)
        {
            Assert.Null(geometry);
            return;
        }

        Assert.NotNull(geometry);
        Assert.Equal([6, 4], geometry.Rings.Select(ring => ring.Count));
        Assert.Equal(new PlaPlanPoint(4, 0), geometry.Rings[0][0]);
        Assert.Equal(new PlaPlanPoint(0.5, 1.5), geometry.Rings[1][0]);
        Assert.Equal(-4.5, geometry.Bottom);
        Assert.Equal(-3.5, geometry.Top);
        Assert.Equal(11, geometry.OutlineArea);
        Assert.Equal(20, geometry.FloorPlanPerimeter);
        Assert.Equal(4, geometry.FloorPlanHolesPerimeter);
        Assert.Equal(variation == 2 ? null : 11.0, geometry.TopAreaWithOpenings);
        Assert.Equal(variation == 2 ? null : 11.0, geometry.BottomAreaWithOpenings);
        Assert.Equal(readsVolume ? 11.0 : null, geometry.Volume);
        Assert.Equal(readsVolume, geometry.VerticalPrisms is { Count: 1 });
    }

    private static void WriteSlabEdges(Span<byte> edges, int edgeCount, int beveledEdge)
    {
        new byte[] { 1, 0, 1, 0 }.CopyTo(edges[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(edges[12..], edgeCount);
        for (int i = 0; i < edgeCount; i++)
        {
            var edge = edges[(16 + i * 27)..];
            BinaryPrimitives.WriteUInt32LittleEndian(edge[4..], 2);
            BinaryPrimitives.WriteDoubleLittleEndian(edge[8..], i == beveledEdge ? Math.PI / 4 : Math.PI / 2);
            BinaryPrimitives.WriteUInt16LittleEndian(edge[16..], 3);
        }
    }

    [Theory]
    [InlineData(900, false, false, 3.953125)]
    [InlineData(1000, false, false, 3.953125)]
    [InlineData(1100, false, false, 4.0)]
    [InlineData(900, true, false, null)]
    [InlineData(900, false, true, 3.96875)]
    public void ComputesSlabVolumeFromTheIntersectionWithARotatedColumn(int slabPriority, bool wrongTarget, bool composite,
        double? volume)
    {
        var (file, slab) = SlabColumnSceneFixture(slabPriority, wrongTarget, composite);
        using var doc = PlaDocument.Open(new MemoryStream(file));
        var geometry = Assert.IsType<PlaSlabGeometry>(Assert.Single(doc.ReadElements(), e => e.Guid == slab).SlabGeometry);
        Assert.Equal(volume, geometry.Volume);
        Assert.Equal(4, geometry.TopAreaWithOpenings);
        Assert.Equal(4, geometry.BottomAreaWithOpenings);
        Assert.Equal(volume == 4, geometry.VerticalPrisms is { Count: 1 });
    }

    internal enum SlabColumnContact { PartialEdge, PartialInterior, Interior, Edge, Split, TrimOuterEdge }

    [Theory]
    [InlineData(5, 5, true)]
    [InlineData(5, 6, true)]
    [InlineData(1, 1, true)]
    [InlineData(5, 1, false)]
    [InlineData(5, 0, false)]
    public void ResolvesColumnSolidWhenCompositeWallCannotWrap(byte outer, byte middle, bool hasSolid)
    {
        var (file, _) = SlabColumnSceneFixture(wallSkinFunctions: [outer, middle, outer]);
        using var doc = PlaDocument.Open(new MemoryStream(file));
        var column = Assert.Single(doc.ReadElements(), e => e.Type == "Column");
        Assert.Equal(-0.25, column.BottomElevationToHomeStory);
        Assert.Equal(1, column.TopElevationToHomeStory);
        Assert.Equal(-4.25, column.BottomElevationToProjectZero);
        Assert.Equal(-3, column.TopElevationToProjectZero);
        if (hasSolid)
        {
            var solid = Assert.IsType<PlaColumnExtrusion>(column.ColumnExtrusion);
            Assert.Equal(0.15625, solid.Volume, 12);
            Assert.Equal(-4.25, solid.BoundingBox.ZMin);
            Assert.Equal(-3, solid.BoundingBox.ZMax);
        }
        else
        {
            Assert.Null(column.ColumnExtrusion);
        }
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    public void PreservesColumnGeometryWithReciprocalAnnotation(bool reciprocal, bool circular, bool angularDimension)
    {
        var (file, _) = SlabColumnSceneFixture(validCircularSection: circular ? true : null, validColumnAnnotation: reciprocal,
            angularDimension: angularDimension);
        using var doc = PlaDocument.Open(new MemoryStream(file));
        var element = Assert.Single(doc.ReadElements(), e => e.Type == "Column");
        if (!reciprocal)
        {
            Assert.Null(element.ColumnExtrusion);
            Assert.Null(element.BottomElevationToProjectZero);
            Assert.Null(element.TopElevationToHomeStory);
            return;
        }
        var solid = Assert.IsType<PlaColumnExtrusion>(element.ColumnExtrusion);
        Assert.Equal(circular ? Math.PI * 0.25 * 0.25 * 1.25 : 0.15625, solid.Volume, 12);
        Assert.Equal((-4.25, -3.0, -0.25, 1.0), (element.BottomElevationToProjectZero,
            element.TopElevationToProjectZero, element.BottomElevationToHomeStory, element.TopElevationToHomeStory));
    }

    internal static (byte[] File, Guid Element) SlabColumnSceneFixture(int slabPriority = 900, bool wrongTarget = false,
        bool composite = false, SlabColumnContact contact = SlabColumnContact.PartialEdge, bool slopeUpperCap = false,
        bool? validCircularSection = null, double circularRadius = 0.25, byte[]? wallSkinFunctions = null,
        bool? validColumnAnnotation = null, bool angularDimension = false)
    {
        Guid columnClass = new("13e8eb85-5a33-47a3-a4d4-1e05296aec7e");
        Guid memoClass = new("7a1a82ab-69cd-40e6-a7d8-76564f981cf9");
        Guid sectionClass = new(validCircularSection.HasValue ? "d6c12939-d905-40e0-aa57-f7c8d687dc7b" : "c4191955-87c6-4fb6-b074-5f094fe9f6a6");
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid jointClass = new("d5381783-6a19-4de8-9e52-2e04a3978db0");
        Guid materialClass = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5");
        Guid layerClass = new("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d");
        Guid compositeClass = new("c7ecb629-3cf3-4c9c-a222-11082672e04b");
        Guid memoRelation = new("27448621-9adc-43e6-b5aa-fc76e4cd62ce");
        Guid sectionRelation = new("63b86c30-a221-421c-84fb-926c694bf611");
        Guid slabRelation = new("f6ee0d9c-27ad-44d0-824f-682969bb171e");
        Guid columnRelation = new("87811dc4-1686-45f4-b1a6-dfaa81da44e4");
        Guid wallJointClass = new("a9e0daf5-0932-4054-b976-d8f207bb865a");
        Guid wallRelation = new("199f13d8-5fa0-4894-8adf-761a1442a7f7");
        Guid columnWallRelation = new("27c70d06-a928-499c-a005-5307c9f10da0");
        Guid annotationClass = new(angularDimension ? "2120198c-d317-4cea-ab4e-c7b6ceb5156e" : "1601b50c-a61f-447c-8b74-0f578c003d88");
        Guid annotationRelation = new(angularDimension ? "1ff9df7b-3f2d-4f16-855a-b863af9ca26d" : "f73ad2dc-973b-4e70-9787-a3c6fee4a2bf");
        Guid slab = Guid.NewGuid(), column = Guid.NewGuid(), memo = Guid.NewGuid(), section = Guid.NewGuid(), joint = Guid.NewGuid();
        Guid wall = Guid.NewGuid(), wallJoint = Guid.NewGuid();
        Guid annotation = Guid.NewGuid();

        var columnBody = Synthetic.Concat(FramedElementId(columnClass, 735, "C"), new byte[95]);
        var data = columnBody.AsSpan();
        ElementId("C").CopyTo(data[721..]);
        WriteLocation(data[701..], 1, -1);
        new byte[] { 1, 0, 2, 0, 0, 0, 1, 0 }.CopyTo(data[304..]);
        BinaryPrimitives.WriteUInt16LittleEndian(data[368..], 1);
        double x = contact is SlabColumnContact.PartialEdge or SlabColumnContact.Edge or SlabColumnContact.TrimOuterEdge ? 1 : 0;
        bool through = contact is not (SlabColumnContact.PartialEdge or SlabColumnContact.PartialInterior);
        (double halfWidth, double halfHeight, double angle) = contact switch
        {
            SlabColumnContact.Split => (1, 0.125, 0),
            SlabColumnContact.TrimOuterEdge => (0.125, 1.25, 0),
            _ => (0.25, 0.125, Math.PI / 4),
        };
        BinaryPrimitives.WriteDoubleLittleEndian(data[312..], x);
        BinaryPrimitives.WriteDoubleLittleEndian(data[328..], through ? -1 : -0.25);
        BinaryPrimitives.WriteDoubleLittleEndian(data[352..], -1);
        BinaryPrimitives.WriteDoubleLittleEndian(data[370..], x);
        BinaryPrimitives.WriteDoubleLittleEndian(data[386..], 1);
        if (slopeUpperCap) BinaryPrimitives.WriteDoubleLittleEndian(data[402..], 0.5);
        BinaryPrimitives.WriteDoubleLittleEndian(data[410..], 1);
        BinaryPrimitives.WriteDoubleLittleEndian(data[(741 + 87)..], angle);

        int slots = wallSkinFunctions is null ? 5 : 7;
        byte[] pool = new byte[(slots + (angularDimension && validColumnAnnotation.HasValue ? 1 : 0)) * 1024];
        var entries = new List<ObjectDirectoryRecord>();
        void Add(ushort kind, Guid id, byte[] bytes)
        {
            int offset = entries.Count;
            Synthetic.Block(PlaBlockMode.Raw, bytes, ver: 1, guid: id, extra: 1, kind: kind).CopyTo(pool, offset * 1024);
            entries.Add(new ObjectDirectoryRecord(kind, (ulong)offset * 64, 64, id));
        }
        var slabBody = OpeningSlab(-1, 0.5, 1);
        if (composite)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(slabBody.AsSpan(191 + 83), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(slabBody.AsSpan(191 + 87), 1);
        }
        Add(1, slab, WithGeometryRelations(slabBody, GeometryRelation(0x11, slabRelation, joint)));
        byte[] annotationLink = !validColumnAnnotation.HasValue ? [] : angularDimension
            ? GeometryRelationList(1, annotationRelation, annotation) : GeometryRelation(0x11, annotationRelation, annotation);
        Add(2, column, WithGeometryRelations(columnBody, GeometryRelation(0x11, memoRelation, memo), annotationLink));
        var memoRelations = new List<byte[]> { HostRelation(memoRelation, column), GeometryRelation(0x11, sectionRelation, section),
            GeometryRelation(0x11, columnRelation, joint) };
        if (wallSkinFunctions is not null) memoRelations.Add(GeometryRelation(0x11, columnWallRelation, wallJoint));
        Add(3, memo, WithGeometryRelations(memoClass.ToByteArray(), memoRelations.ToArray()));
        var sectionBody = validCircularSection is { } valid
            ? CircularColumnSection(sectionClass, circularRadius, valid)
            : RectangularSection(sectionClass, 1, halfWidth, -halfHeight, halfHeight);
        Add(4, section, WithGeometryRelations(sectionBody, HostRelation(sectionRelation, memo)));
        Add(5, joint, WithGeometryRelations(jointClass.ToByteArray(), HostRelation(columnRelation, memo),
            HostRelation(slabRelation, wrongTarget ? Guid.NewGuid() : slab)));
        if (wallSkinFunctions is not null)
        {
            var wallBody = WallWithPlanes(2, -4.25);
            BinaryPrimitives.WriteUInt16LittleEndian(wallBody.AsSpan(209 + 225), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(wallBody.AsSpan(209 + 229), 1);
            Add(10, wall, WithGeometryRelations(wallBody, GeometryRelation(0x11, wallRelation, wallJoint)));
            Add(11, wallJoint, WithGeometryRelations(wallJointClass.ToByteArray(),
                HostRelation(columnWallRelation, memo), HostRelation(wallRelation, wall)));
        }

        byte[] labelDirectory = [], labelPool = [];
        if (validColumnAnnotation is { } reciprocal)
        {
            var body = WithGeometryRelations(annotationClass.ToByteArray(), HostRelation(annotationRelation, reciprocal ? column : Guid.NewGuid()));
            if (angularDimension) Add(12, annotation, body);
            else
            {
                byte[] labelData = new byte[1024];
                Synthetic.Block(PlaBlockMode.Raw, body, ver: 1, guid: annotation, extra: 1, kind: 12).CopyTo(labelData, 0);
                labelDirectory = OwnedDirectory(false, Guid.Empty, 64, new ObjectDirectoryRecord(12, 0, 64, annotation));
                labelPool = Pool(labelData);
            }
        }

        var file = Synthetic.Fdb(ClassTable((1, SlabClass), (2, columnClass), (3, memoClass), (4, sectionClass),
                (5, jointClass), (6, storyClass), (7, materialClass), (8, layerClass), (9, compositeClass),
                (10, WallClass), (11, wallJointClass), (12, annotationClass)),
            OwnedDirectory(false, Guid.Empty, (ulong)pool.Length / 16, entries.ToArray()), labelDirectory,
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 6),
            JunctionAttribute(materialClass, 7, 1, 1000), JunctionAttribute(materialClass, 7, 2, (uint)slabPriority),
            JunctionAttribute(materialClass, 7, 3, wallSkinFunctions is null ? 1100U : 950U),
            JunctionAttribute(layerClass, 8, 1, 1),
            CompositeForWallTest(compositeClass, splitCuts: false, kind: 9, skinFunctions: wallSkinFunctions), Pool(pool), labelPool);

        return (file, slab);
    }

    private static byte[] CircularColumnSection(Guid sectionClass, double radius, bool valid)
    {
        const int frame = 141, vector = 149, hatchOffset = 482, hatchLength = 175;
        var section = new byte[vector + hatchOffset + hatchLength];
        sectionClass.TryWriteBytes(section);
        BinaryPrimitives.WriteDoubleLittleEndian(section.AsSpan(91), 2 * radius);
        BinaryPrimitives.WriteDoubleLittleEndian(section.AsSpan(99), 2 * radius);
        new byte[] { 2, 0, 9, 1 }.CopyTo(section, frame);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(frame + 4), hatchOffset + hatchLength);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(vector), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(vector + 69), 1);
        var hatch = section.AsSpan(vector + hatchOffset);
        hatch[0] = 3;
        hatch[1] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(hatch[2..], hatchLength - 6);
        BinaryPrimitives.WriteUInt16LittleEndian(hatch[8..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(hatch[18..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(hatch[55..], 4);
        BinaryPrimitives.WriteDoubleLittleEndian(hatch[75..], -radius);
        BinaryPrimitives.WriteDoubleLittleEndian(hatch[91..], radius);
        BinaryPrimitives.WriteDoubleLittleEndian(hatch[107..], -radius);
        var arcs = hatch[123..];
        BinaryPrimitives.WriteUInt16LittleEndian(arcs, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(arcs[2..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(arcs[6..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(arcs[10..], 2);
        BinaryPrimitives.WriteDoubleLittleEndian(arcs[14..], Math.PI);
        BinaryPrimitives.WriteUInt32LittleEndian(arcs[22..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(arcs[26..], 3);
        BinaryPrimitives.WriteDoubleLittleEndian(arcs[30..], valid ? Math.PI : -Math.PI);
        BinaryPrimitives.WriteUInt16LittleEndian(arcs[38..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(arcs[40..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(arcs[48..], 3);
        return section;
    }

    [Theory]
    [InlineData(1100, false, 4.0)]
    [InlineData(900, false, null)]
    [InlineData(1100, true, null)]
    public void ChecksPriorityAndOwnershipForTheAlternateSlabWallRelation(int slabPriority, bool wrongTarget, double? volume)
    {
        Guid contactClass = new("324477ff-7a26-4d53-a38e-8f39f678c2b8");
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid materialClass = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5");
        Guid slabRole = new("29bce22c-b910-400a-b84a-0b85fb92610e");
        Guid wallRole = new("88f6f041-4ce9-43cd-b0c1-056bf842bf5a");
        Guid slab = Guid.NewGuid(), wall = Guid.NewGuid(), contact = Guid.NewGuid();
        byte[] pool = new byte[3 * 1024];
        var slabBody = WithGeometryRelations(OpeningSlab(-1, 0.5, 1), GeometryRelation(0x11, slabRole, contact));
        var wallBody = WithGeometryRelations(WallWithPlanes(1, -3.75), GeometryRelation(0x11, wallRole, contact));
        var jointBody = WithGeometryRelations(contactClass.ToByteArray(),
            HostRelation(slabRole, wrongTarget ? Guid.NewGuid() : slab), HostRelation(wallRole, wall));
        Synthetic.Block(PlaBlockMode.Raw, slabBody, ver: 1, guid: slab, extra: 1, kind: 1).CopyTo(pool, 0);
        Synthetic.Block(PlaBlockMode.Raw, wallBody, ver: 1, guid: wall, extra: 1, kind: 2).CopyTo(pool, 1024);
        Synthetic.Block(PlaBlockMode.Raw, jointBody, ver: 1, guid: contact, extra: 1, kind: 3).CopyTo(pool, 2048);
        using var doc = Open(ClassTable((1, SlabClass), (2, WallClass), (3, contactClass), (4, storyClass), (7, materialClass)),
            OwnedDirectory(false, Guid.Empty, 192, new(1, 0, 64, slab), new(2, 64, 64, wall), new(3, 128, 64, contact)),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 4),
            JunctionAttribute(materialClass, 7, 1, 1000), JunctionAttribute(materialClass, 7, 2, (uint)slabPriority), Pool(pool));
        var geometry = Assert.IsType<PlaSlabGeometry>(Assert.Single(doc.ReadElements(), e => e.Guid == slab).SlabGeometry);
        Assert.Equal(volume, geometry.Volume);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("unknown role")]
    [InlineData("wrong owner")]
    [InlineData("arc crosses rings")]
    [InlineData("open ring")]
    [InlineData("hole outside")]
    public void ReadsSavedWallProfileComponents(string variation)
    {
        Guid profileClass = new("462b6598-57f0-41a5-ae81-739c8f487e04");
        Guid relation = new("e63e7632-0efd-412d-8077-d3f3906b638a");
        Guid wall = Guid.NewGuid(), profile = Guid.NewGuid();
        var wallBody = WallWithPlanes(1, -4.5);
        BinaryPrimitives.WriteUInt16LittleEndian(wallBody.AsSpan(209 + 225), 3);

        double holeX = variation == "hole outside" ? 5 : 1;
        PlaProfilePoint[] exterior = [new(0, 0), new(4, 0), new(4, 4), new(0, 4), new(0, 0)];
        PlaProfilePoint[] hole = [new(holeX, 1), new(holeX + 1, 1), new(holeX + 1, 2), new(holeX, 2), new(holeX, 1)];
        if (variation == "open ring") exterior[^1] = new(0, 1);
        var core = SavedProfileHatch(14, variation == "unknown role" ? (byte)7 : (byte)1,
            variation == "arc crosses rings" ? 6 : 5, exterior, hole);
        PlaProfilePoint[] otherRing = [new(5, 0), new(6, 0), new(6, 1), new(5, 1), new(5, 0)];
        var other = SavedProfileHatch(6, 0, 0, otherRing);
        byte[] profileBody = SavedWallProfileRecord(profileClass, core, other);

        const int extent = 2048;
        var pool = new byte[2 * extent];
        Synthetic.Block(PlaBlockMode.Raw, WithGeometryRelations(wallBody, GeometryRelation(0x11, relation, profile)),
            ver: 1, guid: wall, extra: 1, kind: 1).CopyTo(pool, 0);
        Synthetic.Block(PlaBlockMode.Raw,
            WithGeometryRelations(profileBody, HostRelation(relation, variation == "wrong owner" ? Guid.NewGuid() : wall)),
            ver: 1, guid: profile, extra: 1, kind: 2).CopyTo(pool, extent);
        using var doc = Open(ClassTable((1, WallClass), (2, profileClass)),
            OwnedDirectory(false, Guid.Empty, 256, new ObjectDirectoryRecord(1, 0, 128, wall),
                new ObjectDirectoryRecord(2, 128, 128, profile)), Pool(pool));
        var profileResult = Assert.Single(doc.ReadElements()).WallProfile;
        if (variation is not ("valid" or "unknown role"))
        {
            Assert.Null(profileResult);
            return;
        }

        var decoded = Assert.IsType<PlaWallProfile>(profileResult);
        Assert.Equal(9, decoded.ProfileIndex);
        Assert.Collection(decoded.Components, part =>
        {
            Assert.Equal(14, part.BuildingMaterialIndex);
            Assert.Equal(variation == "valid" ? "Core" : null, part.Function);
            Assert.Equal(15 + 2 * Math.PI, part.CrossSectionArea, 12);
            Assert.Collection(part.Rings,
                ring => Assert.Equal(exterior[..^1], ring),
                ring => Assert.Equal(hole[..^1], ring));
            Assert.Equal(new PlaProfileArc(0, 3, Math.PI), Assert.Single(part.Arcs!));
        }, part =>
        {
            Assert.Equal(6, part.BuildingMaterialIndex);
            Assert.Equal("Other", part.Function);
            Assert.Equal(1, part.CrossSectionArea);
            Assert.Single(part.Rings);
            Assert.Null(part.Arcs);
        });

    }

    private static byte[] SavedWallProfileRecord(Guid profileClass, params byte[][] hatches)
    {
        int size = 128 + hatches.Sum(h => h.Length) + (hatches.Length - 1) * 16;
        var profile = new byte[402 + size];
        profileClass.TryWriteBytes(profile);
        BinaryPrimitives.WriteUInt16LittleEndian(profile.AsSpan(49), 1);
        BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(51), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(profile.AsSpan(55), 2);
        new byte[] { 2, 0, 9, 1 }.CopyTo(profile, 394);
        BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(398), size);
        BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(402), 128);
        BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(402 + 69), hatches.Length);
        int offset = 402 + 128;
        foreach (var hatch in hatches)
        {
            hatch.CopyTo(profile, offset);
            offset += hatch.Length + 16;
        }
        return profile;
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ReadsProfiledWallBoxFromAnInlineSlice(bool reflected, bool verifiedSlice)
    {
        var (file, _) = ProfiledWallSceneFixture(overlapping: false, reflected, verifiedSlice);
        using var document = PlaDocument.Open(new MemoryStream(file));
        var wall = Assert.Single(document.ReadElements());
        Assert.Equal(verifiedSlice ? new PlaBoundingBox(-1, reflected ? 0 : -5, -6.75,
            1, reflected ? 5 : 0, -3.75) : null, wall.BoundingBox);
        Assert.Null(wall.BottomElevationToProjectZero);
    }

    internal static (byte[] File, Guid Wall) ProfiledWallSceneFixture(bool overlapping, bool reflected = false, bool? verifiedSlice = null)
    {
        Guid profileClass = new("462b6598-57f0-41a5-ae81-739c8f487e04");
        Guid relation = new("e63e7632-0efd-412d-8077-d3f3906b638a");
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid wall = Guid.NewGuid(), profile = Guid.NewGuid();
        var body = WallWithPlanes(1, -3.75);
        var settings = body.AsSpan(209);
        BinaryPrimitives.WriteUInt16LittleEndian(settings[225..], 3);
        BinaryPrimitives.WriteDoubleLittleEndian(settings, -1);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[16..], 1);
        new byte[] { 1, 0, 1, 0, 0, 0 }.CopyTo(settings[489..]);
        double[] box = [-1, -5, 1, 0, -3.75, -0.75];
        if (verifiedSlice is { } verified)
        {
            settings[515..563].Clear();
            BinaryPrimitives.WriteDoubleLittleEndian(settings[77..], 5);
            BinaryPrimitives.WriteDoubleLittleEndian(settings[85..], 5);
            double edgeMin = reflected ? 4 : -5;
            double edgeMax = reflected ? 5 : -4;
            WriteRectangularPlan(settings[256..], -1, edgeMin, verified ? 1 : 0.5, edgeMax, slab: false);
        }
        else
            for (int i = 0; i < box.Length; i++)
                BinaryPrimitives.WriteDoubleLittleEndian(settings[(515 + i * 8)..], box[i]);
        double z = verifiedSlice.HasValue ? -3 : 0;
        var core = SavedProfileHatch(14, 1, 0,
            [new(0, z), new(4, z), new(4, z + 3), new(0, z + 3), new(0, z)],
            [new(1, z + 1), new(2, z + 1), new(2, z + 2), new(1, z + 2), new(1, z + 1)]);
        double edge = overlapping ? 3.5 : 4;
        PlaProfilePoint[] jacketRing = [new(edge, z + 1), new(5, z + 1), new(5, z + 2), new(edge, z + 2), new(edge, z + 1)];
        var jacket = SavedProfileHatch(6, 0, 0, jacketRing);
        const int extent = 2048;
        var pool = new byte[2 * extent];
        Synthetic.Block(PlaBlockMode.Raw, WithGeometryRelations(body, GeometryRelation(0x11, relation, profile)),
            ver: 1, guid: wall, extra: 1, kind: 1).CopyTo(pool, 0);
        Synthetic.Block(PlaBlockMode.Raw, WithGeometryRelations(SavedWallProfileRecord(profileClass, core, jacket), HostRelation(relation, wall)),
            ver: 1, guid: profile, extra: 1, kind: 2).CopyTo(pool, extent);
        return (Synthetic.Fdb(ClassTable((1, WallClass), (2, profileClass), (3, storyClass)),
            OwnedDirectory(false, Guid.Empty, 256, new ObjectDirectoryRecord(1, 0, 128, wall),
                new ObjectDirectoryRecord(2, 128, 128, profile)),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 3), Pool(pool)), wall);
    }

    private static byte[] SavedProfileHatch(int material, byte role, int arcEnd, params PlaProfilePoint[][] rings)
    {
        int points = 1 + rings.Sum(ring => ring.Length);
        int arcs = 59 + points * 16;
        int ends = arcs + 6 + (arcEnd == 0 ? 0 : 16);
        int footer = ends + 6 + (rings.Length + 1) * 4;
        var buffer = new byte[footer + 109];
        buffer[0] = 3;
        buffer[1] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(2), buffer.Length - 6);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8), 2);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(18), material);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(55), points);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(arcs), 1);
        if (arcEnd != 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(arcs + 2), 1);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(arcs + 6), arcEnd - 1);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(arcs + 10), arcEnd);
            BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(arcs + 14), Math.PI);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(ends), 1);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(ends + 2), rings.Length + 1);
        int vertex = 1;
        for (int i = 0; i < rings.Length; i++)
        {
            foreach (var point in rings[i])
            {
                int offset = 59 + vertex * 16;
                BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(offset), point.U);
                BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(offset + 8), point.Z);
                vertex++;
            }
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(ends + 10 + i * 4), vertex - 1);
        }
        buffer[footer + 108] = role;
        return buffer;
    }

    [Theory]
    [InlineData(0.65, true, true, null)]
    [InlineData(0.59, true, false, null)]
    [InlineData(0.65, false, false, null)]
    [InlineData(0.65, true, true, -0.5)]
    [InlineData(0.65, true, false, 0.4)]
    [InlineData(0.65, false, false, -0.5)]
    public void ChecksJoinedBeamCapAgainstTheRotatedColumnOutline(double centerX, bool reciprocal, bool hasSolid, double? collisionX)
    {
        Guid columnClass = new("13e8eb85-5a33-47a3-a4d4-1e05296aec7e"), memoClass = new("7a1a82ab-69cd-40e6-a7d8-76564f981cf9");
        Guid sectionClass = new("c4191955-87c6-4fb6-b074-5f094fe9f6a6"), storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid materialClass = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5"), layerClass = new("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d");
        Guid jointClass = new("92ee3161-7e81-4cb2-a403-b814edb2ae37"), columnJointClass = new("f7f60005-35d0-4346-87ab-29e087ac0bbf");
        Guid memoKey = new("27448621-9adc-43e6-b5aa-fc76e4cd62ce"), sectionKey = new("63b86c30-a221-421c-84fb-926c694bf611");
        Guid beamKey = new("6346a816-1d7c-4e46-9609-67624e99d0fb"), endKey = new("1dae4518-b353-42bc-bcb1-d037bcc603d6");
        Guid peerKey = new("59655252-b20b-47a7-9d4a-27a2f2bf2c73");
        if (collisionX.HasValue)
        {
            endKey = new("89d11bdb-3a78-48ad-96e7-5b69836c591d");
            peerKey = new("1d381463-2edc-4aad-91b0-027291e8da94");
        }
        Guid column = Guid.NewGuid(), memo = Guid.NewGuid(), section = Guid.NewGuid(), contact = Guid.NewGuid(), join = Guid.NewGuid();
        byte[] pool = new byte[14 * 1024];
        var records = new List<ObjectDirectoryRecord>();
        void Add(ushort kind, Guid id, byte[] bytes, int slot)
        {
            Synthetic.Block(PlaBlockMode.Raw, bytes, ver: 1, guid: id, extra: 1, kind: kind).CopyTo(pool, slot * 1024);
            records.Add(new ObjectDirectoryRecord(kind, (ulong)slot * 64, 64, id));
        }

        var body = Synthetic.Concat(FramedElementId(columnClass, 735, "C"), new byte[95]);
        ElementId("C").CopyTo(body.AsSpan(721));
        new byte[] { 1, 0, 2, 0, 0, 0, 1, 0 }.CopyTo(body, 304);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(368), 1);
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(312), centerX);
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(320), 0.34);
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(352), -1);
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(370), centerX);
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(378), 0.34);
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(386), 4);
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(410), 1);
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(741 + 87), Math.PI / 4);
        WriteLocation(body.AsSpan(701), 1, -1);
        Add(1, column, WithGeometryRelations(body, GeometryRelation(0x11, memoKey, memo)), 0);
        Add(2, memo, WithGeometryRelations(memoClass.ToByteArray(), HostRelation(memoKey, column),
            GeometryRelation(0x11, sectionKey, section), GeometryRelation(0x11, beamKey, contact)), 1);
        Add(3, section, WithGeometryRelations(RectangularSection(sectionClass, 1, 0.25, -0.25, 0.25),
            HostRelation(sectionKey, memo)), 2);

        Guid branch = AddBeamCutFixture(pool, records, memo, contact, 3 * 1024, 0, 2.5, 0.1, 0,
            GeometryRelation(0x11, endKey, join), endX: 0.4);
        Guid peer = AddBeamCutFixture(pool, records, Guid.Empty, Guid.Empty, 8 * 1024, collisionX ?? 0.4, 2.5, 0.1, 0,
            reciprocal ? GeometryRelation(0x11, peerKey, join) : [], transverse: true, startY: collisionX.HasValue ? -2 : 0);
        var joint = new byte[67];
        jointClass.TryWriteBytes(joint);
        new byte[] { 28, 0, 0, 0, 1, 2, 22, 0, 0, 0, 2, 0, 3, 0, 0 }.CopyTo(joint, 35);
        if (!collisionX.HasValue)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(joint.AsSpan(50), 0.4);
            joint[66] = 1;
        }
        Add(12, join, WithGeometryRelations(joint, HostRelation(endKey, branch), HostRelation(peerKey, peer)), 13);

        using var doc = Open(ClassTable((1, columnClass), (2, memoClass), (3, sectionClass), (6, storyClass), (7, materialClass),
                (8, layerClass), (9, new("a5c2d285-5a7c-4a68-9554-9eda9d4556ca")), (10, new("cbab7bc6-e03a-4133-9161-c28ed3368672")),
                (11, columnJointClass), (12, jointClass)),
            OwnedDirectory(false, Guid.Empty, (ulong)pool.Length / 16, records.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 6),
            JunctionAttribute(materialClass, 7, 1, 1000), JunctionAttribute(materialClass, 7, 2, 1200),
            JunctionAttribute(layerClass, 8, 1, 1), Pool(pool));
        var element = Assert.Single(doc.ReadElements(), e => e.Guid == column);
        Assert.Equal(0, element.BottomElevationToHomeStory);
        Assert.Equal(4, element.TopElevationToHomeStory);
        Assert.Equal(-4, element.BottomElevationToProjectZero);
        Assert.Equal(0, element.TopElevationToProjectZero);
        Assert.Equal(hasSolid ? 1.0 : (double?)null, element.ColumnExtrusion?.Volume);
    }

    [Theory]
    [InlineData(0.75, 0.125, true, 0.375)]
    [InlineData(0, 0.125, true, 0.4375)]
    [InlineData(0, 0.25, true, null)]
    [InlineData(0, 0.125, false, null)]
    public void ReadsRectangularColumnCutByCoextensiveBeams(double end, double profileHalfWidth, bool reciprocal, double? volume)
    {
        Guid columnClass = new("13e8eb85-5a33-47a3-a4d4-1e05296aec7e"), columnMemoClass = new("7a1a82ab-69cd-40e6-a7d8-76564f981cf9");
        Guid beamClass = new("a5c2d285-5a7c-4a68-9554-9eda9d4556ca"), beamMemoClass = new("cbab7bc6-e03a-4133-9161-c28ed3368672");
        Guid sectionClass = new("c4191955-87c6-4fb6-b074-5f094fe9f6a6"), complexClass = new("8825ae40-f827-4a57-a1c5-49d04c687c46");
        Guid shapeClass = new("f4cd2bd7-d4b1-4abf-b13c-59001907bbbd"), columnContactClass = new("f7f60005-35d0-4346-87ab-29e087ac0bbf");
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea"), jointClass = new("92ee3161-7e81-4cb2-a403-b814edb2ae37");
        Guid materialClass = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5"), layerClass = new("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d");
        Guid columnMemoKey = new("27448621-9adc-43e6-b5aa-fc76e4cd62ce"), beamMemoKey = new("d5762ca7-d920-455d-a373-28ba6d47152a");
        Guid sectionKey = new("63b86c30-a221-421c-84fb-926c694bf611"), shapeKey = new("ca073b44-0655-407d-9715-1a019e628139");
        Guid columnContactKey = new("6346a816-1d7c-4e46-9609-67624e99d0fb"), beamContactKey = new("cd0b0837-0b34-4e96-8d66-9fe7d926478c");
        Guid endKey = new("1dae4518-b353-42bc-bcb1-d037bcc603d6"), peerKey = new("59655252-b20b-47a7-9d4a-27a2f2bf2c73");
        Guid column = Guid.NewGuid(), memo = Guid.NewGuid(), section = Guid.NewGuid(), shape = Guid.NewGuid();
        Guid startJoint = Guid.NewGuid(), endJoint = Guid.NewGuid(), mainContact = Guid.NewGuid(), profileContact = Guid.NewGuid();
        byte[] pool = new byte[14 * 2048];
        var entries = new List<ObjectDirectoryRecord>();
        void Add(ushort kind, Guid id, byte[] body)
        {
            int slot = entries.Count;
            Synthetic.Block(PlaBlockMode.Raw, body, ver: 1, guid: id, extra: 1, kind: kind).CopyTo(pool, slot * 2048);
            entries.Add(new ObjectDirectoryRecord(kind, (ulong)slot * 128, 128, id));
        }

        var body = Synthetic.Concat(FramedElementId(columnClass, 735, "C"), new byte[95]);
        ElementId("C").CopyTo(body.AsSpan(721));
        new byte[] { 1, 0, 2, 0, 0, 0, 1, 0 }.CopyTo(body, 304);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(368), 1);
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(320), 0.125);
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(352), -1);
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(378), 0.125);
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(386), 2);
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(410), 1);
        WriteLocation(body.AsSpan(701), 1, -1);
        Add(1, column, WithGeometryRelations(body, GeometryRelation(0x11, columnMemoKey, memo)));
        Add(2, memo, WithGeometryRelations(columnMemoClass.ToByteArray(), HostRelation(columnMemoKey, column),
            GeometryRelation(0x11, sectionKey, section), GeometryRelationList(1, columnContactKey, mainContact, profileContact)));
        Add(3, section, WithGeometryRelations(RectangularSection(sectionClass, 1, 0.25, -0.25, 0.25), HostRelation(sectionKey, memo)));

        Guid main = AddBeam(false, mainContact, endKey), weaker = AddBeam(true, profileContact, peerKey);
        AddJoin(startJoint, -1, weaker);
        AddJoin(endJoint, end, reciprocal ? weaker : Guid.NewGuid());

        using var doc = Open(ClassTable((1, columnClass), (2, columnMemoClass), (3, sectionClass), (4, complexClass), (5, shapeClass),
                (6, storyClass), (7, materialClass), (8, layerClass), (9, beamClass), (10, beamMemoClass), (11, columnContactClass), (12, jointClass)),
            OwnedDirectory(false, Guid.Empty, (ulong)pool.Length / 16, entries.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 6),
            JunctionAttribute(materialClass, 7, 1, 1000), JunctionAttribute(materialClass, 7, 2, 1200),
            JunctionAttribute(materialClass, 7, 3, 1100), JunctionAttribute(layerClass, 8, 1, 1), Pool(pool));
        var element = Assert.Single(doc.ReadElements(), e => e.Guid == column);
        if (volume is null)
        {
            Assert.Null(element.ColumnCutMesh);
            return;
        }
        var mesh = Assert.IsType<PlaColumnCutMesh>(element.ColumnCutMesh);
        Assert.Equal(volume.Value, mesh.Volume, 12);
        Assert.Equal(new PlaBoundingBox(-0.25, -0.125, -4, 0.25, 0.375, -2), element.BoundingBox);
        Assert.Equal(0, element.BottomElevationToHomeStory);
        Assert.Equal(2, element.TopElevationToHomeStory);
        Assert.Equal(-4, element.BottomElevationToProjectZero);
        Assert.Equal(-2, element.TopElevationToProjectZero);

        Guid AddBeam(bool profiled, Guid contact, Guid jointRole)
        {
            Guid owner = Guid.NewGuid(), beamMemo = Guid.NewGuid(), beamSection = Guid.NewGuid();
            Add(9, owner, WithGeometryRelations(HorizontalBeam(beamClass, -1, 0, end, 0, 1, 0, 1),
                GeometryRelation(0x11, beamMemoKey, beamMemo)));
            Add(10, beamMemo, WithGeometryRelations(beamMemoClass.ToByteArray(), HostRelation(beamMemoKey, owner),
                GeometryRelation(0x11, sectionKey, beamSection), GeometryRelation(0x11, beamContactKey, contact),
                GeometryRelationList(1, jointRole, startJoint, endJoint)));
            Add(11, contact, WithGeometryRelations(columnContactClass.ToByteArray(), HostRelation(beamContactKey, beamMemo),
                HostRelation(columnContactKey, memo)));
            if (profiled)
            {
                Add(4, beamSection, WithGeometryRelations(complexClass.ToByteArray(), HostRelation(sectionKey, beamMemo),
                    GeometryRelation(0x11, shapeKey, shape)));
                double w = profileHalfWidth;
                PlaProfilePoint[] ring = [new(-w, -1), new(w, -1), new(w, -0.5), new(0, -0.5), new(0, 0), new(-w, 0), new(-w, -1)];
                Add(5, shape, WithGeometryRelations(SavedMemberProfileShape(shapeClass, 1, SavedProfileHatch(3, 1, 0, ring)),
                    HostRelation(shapeKey, beamSection)));
            }
            else
                Add(3, beamSection, WithGeometryRelations(RectangularSection(sectionClass, 2, 0.125, -1, 0), HostRelation(sectionKey, beamMemo)));
            return beamMemo;
        }

        void AddJoin(Guid id, double x, Guid other)
        {
            var join = new byte[67];
            jointClass.TryWriteBytes(join);
            new byte[] { 28, 0, 0, 0, 1, 2, 22, 0, 0, 0, 2, 0, 3, 0, 0 }.CopyTo(join, 35);
            BinaryPrimitives.WriteDoubleLittleEndian(join.AsSpan(50), x);
            join[66] = 1;
            Add(12, id, WithGeometryRelations(join, HostRelation(endKey, main), HostRelation(peerKey, other)));
        }
    }

    internal enum ColumnBeamEnd
    {
        Butt, EqualTee, WeakerTee, RepeatedWeakerTee, DifferentHeightTee, TrimmedThroughTee, AngledTee,
        AngledTeeNearCap, AngledWeakerTee,
        MiterAway, MiterAtFace, WeakerOverlap, EqualOverlap, SeparateEqualOverlap,
        EqualMiter, EqualMiterWithWeakPeer, EqualMiterWithDifferentWeakPeer,
    }

    internal static byte[] ColumnBeamBandsFixture(bool partialCut, bool otherColumn, bool wrongOtherOwner, bool oblique = false,
        bool? validWallLabel = null, bool concaveProfile = false, bool skewedBeam = false, bool clearBeams = false,
        ColumnBeamEnd beamEnd = ColumnBeamEnd.Butt)
    {
        Guid columnClass = new("13e8eb85-5a33-47a3-a4d4-1e05296aec7e"), columnMemoClass = new("7a1a82ab-69cd-40e6-a7d8-76564f981cf9");
        Guid sectionClass = new("8825ae40-f827-4a57-a1c5-49d04c687c46"), shapeClass = new("f4cd2bd7-d4b1-4abf-b13c-59001907bbbd");
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea"), materialClass = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5");
        Guid layerClass = new("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d"), beamJoinClass = new("92ee3161-7e81-4cb2-a403-b814edb2ae37");
        Guid memoKey = new("27448621-9adc-43e6-b5aa-fc76e4cd62ce"), sectionKey = new("63b86c30-a221-421c-84fb-926c694bf611");
        Guid shapeKey = new("ca073b44-0655-407d-9715-1a019e628139"), beamKey = new("6346a816-1d7c-4e46-9609-67624e99d0fb");
        Guid firstEnd = new("59655252-b20b-47a7-9d4a-27a2f2bf2c73"), secondEnd = new("1dae4518-b353-42bc-bcb1-d037bcc603d6");
        Guid throughRole = new("89d11bdb-3a78-48ad-96e7-5b69836c591d");
        Guid secondCollision = new("1d381463-2edc-4aad-91b0-027291e8da94");
        Guid column = Guid.NewGuid(), memo = Guid.NewGuid(), section = Guid.NewGuid(), shape = Guid.NewGuid();
        Guid lowerContact = Guid.NewGuid(), middleContact = Guid.NewGuid(), beamJoin = Guid.NewGuid();
        Guid throughContact = Guid.NewGuid(), repeatedJoin = Guid.NewGuid(), throughTrim = Guid.NewGuid();
        bool trimmed = beamEnd == ColumnBeamEnd.TrimmedThroughTee;
        bool nearCap = beamEnd == ColumnBeamEnd.AngledTeeNearCap;
        bool angledTee = beamEnd is ColumnBeamEnd.AngledTee or ColumnBeamEnd.AngledWeakerTee || nearCap;
        bool weakMiterPeer = beamEnd is ColumnBeamEnd.EqualMiterWithWeakPeer or ColumnBeamEnd.EqualMiterWithDifferentWeakPeer;
        bool activeMiter = beamEnd == ColumnBeamEnd.EqualMiter || weakMiterPeer;
        bool miter = beamEnd is ColumnBeamEnd.MiterAway or ColumnBeamEnd.MiterAtFace || activeMiter;
        bool overlap = beamEnd is ColumnBeamEnd.WeakerOverlap or ColumnBeamEnd.EqualOverlap or ColumnBeamEnd.SeparateEqualOverlap;
        bool tee = beamEnd != ColumnBeamEnd.Butt && !miter && !overlap;
        bool equal = beamEnd is ColumnBeamEnd.EqualTee or ColumnBeamEnd.AngledTee || trimmed || nearCap || activeMiter;
        bool repeated = beamEnd == ColumnBeamEnd.RepeatedWeakerTee;
        Guid wallColumn = new("199f13d8-5fa0-4894-8adf-761a1442a7f7"), columnWall = new("27c70d06-a928-499c-a005-5307c9f10da0");
        Guid wallProfileClass = new("462b6598-57f0-41a5-ae81-739c8f487e04"), wallColumnClass = new("a9e0daf5-0932-4054-b976-d8f207bb865a");
        Guid labelClass = new("1601b50c-a61f-447c-8b74-0f578c003d88"), wallContact = Guid.NewGuid();
        var body = Synthetic.Concat(FramedElementId(columnClass, 735, "C"), new byte[95]);
        ElementId("C").CopyTo(body.AsSpan(721));
        body[741] = 1;
        new byte[] { 1, 0, 2, 0, 0, 0, 1, 0 }.CopyTo(body, 304);
        if (nearCap) BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(328), 1.5 - 1e-12);
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(352), -1);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(368), 1);
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(386), 4);
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(410), 1);
        WriteLocation(body.AsSpan(701), 1, -1);
        if (oblique)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(336), 1 / Math.Sqrt(5));
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(352), -2 / Math.Sqrt(5));
        }

        var pool = new byte[(validWallLabel.HasValue || trimmed || weakMiterPeer ? 31 : otherColumn ? 28 : 24) * 1024];
        var records = new List<ObjectDirectoryRecord>();
        void Add(ushort kind, Guid id, byte[] bytes, int? position = null)
        {
            int offset = position ?? records.Count * 1024;
            Synthetic.Block(PlaBlockMode.Raw, bytes, ver: 1, guid: id, extra: 1, kind: kind).CopyTo(pool, offset);
            records.Add(new ObjectDirectoryRecord(kind, (ulong)offset / 16, 64, id));
        }

        PlaProfilePoint[] ring = concaveProfile
            ? [new(-0.25, -0.25), new(0.25, -0.25), new(0.25, 0), new(0, 0), new(0, 0.25), new(-0.25, 0.25), new(-0.25, -0.25)]
            : [new(-0.25, -0.25), new(0.25, -0.25), new(0.25, 0.25), new(-0.25, 0.25), new(-0.25, -0.25)];
        Add(1, column, WithGeometryRelations(body, GeometryRelation(0x11, memoKey, memo)));
        Add(2, memo, WithGeometryRelations(columnMemoClass.ToByteArray(), HostRelation(memoKey, column),
            GeometryRelation(0x11, sectionKey, section), equal
                ? GeometryRelationList(1, beamKey, lowerContact, middleContact, throughContact)
                : GeometryRelationList(1, beamKey, lowerContact, middleContact),
            validWallLabel.HasValue ? GeometryRelation(0x11, columnWall, wallContact) : []));
        Add(4, section, WithGeometryRelations(sectionClass.ToByteArray(), HostRelation(sectionKey, memo), GeometryRelation(0x11, shapeKey, shape)));
        Add(5, shape, WithGeometryRelations(SavedMemberProfileShape(shapeClass, 1, SavedProfileHatch(1, 1, 0, ring)), HostRelation(shapeKey, section)));

        var contact = new byte[67];
        beamJoinClass.TryWriteBytes(contact);
        new byte[] { 28, 0, 0, 0, 1, 2, 22, 0, 0, 0, 2, 0, 3, 0, 0 }.CopyTo(contact, 35);
        double jointX = activeMiter ? 0 : tee || beamEnd == ColumnBeamEnd.MiterAtFace ? 0.25 : miter ? 0.5 : 2;
        double beamY = clearBeams ? 1 : 0;
        double skew = skewedBeam ? 0.5 : 0;
        if (!overlap)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(contact.AsSpan(50), jointX);
            BinaryPrimitives.WriteDoubleLittleEndian(contact.AsSpan(58), beamY + skew);
            contact[66] = 1;
        }
        Guid extraContact = Guid.NewGuid();
        Guid beamColumn = new("cd0b0837-0b34-4e96-8d66-9fe7d926478c");
        Guid branchRole = overlap ? secondCollision : firstEnd;
        Guid nextRole = tee || overlap ? throughRole : secondEnd;
        byte[] JointLinks(Guid role) => repeated || (weakMiterPeer && role == branchRole)
            ? GeometryRelationList(1, role, beamJoin, repeatedJoin) : GeometryRelation(0x11, role, beamJoin);
        Guid middle = AddBeamCutFixture(pool, records, memo, middleContact, 6144, beamY - skew, 2.5, partialCut ? 0.125 : 0.25, 0,
            oblique ? [] : JointLinks(branchRole), endElevation: oblique ? 3.5 : null,
            additionalColumnContact: otherColumn ? extraContact : null, endY: beamY + skew, endX: jointX);
        AddBeamCutFixture(pool, records, memo, lowerContact, 16384, beamY, 0.75, 0.25, 0, []);
        if (!oblique)
        {
            const double throughStart = -0.375;
            var throughLinks = trimmed
                ? Synthetic.Concat(JointLinks(throughRole), GeometryRelation(0x11, firstEnd, throughTrim))
                : JointLinks(nextRole);
            Guid next = activeMiter
                ? AddBeamCutFixture(pool, records, memo, throughContact, 11264, jointX, 2.5, 0.25, 0, throughLinks,
                    transverse: true, startY: beamY)
                : tee
                ? AddBeamCutFixture(pool, records, equal ? memo : Guid.Empty, throughContact, 11264, angledTee ? jointX - 0.5 : jointX,
                    beamEnd == ColumnBeamEnd.DifferentHeightTee ? 2.625 : 2.5, 0.125, 0, throughLinks,
                    transverse: true, material: equal ? 2u : 3u, startY: trimmed ? throughStart : -2,
                    concaveSection: !equal, endX: angledTee ? jointX + 0.5 : null)
                : AddBeamCutFixture(pool, records, Guid.Empty, Guid.Empty, 11264,
                    beamEnd == ColumnBeamEnd.SeparateEqualOverlap ? 1 : beamY + skew, 2.5, 0.25, 0,
                    JointLinks(nextRole), startX: overlap ? -2 : jointX, endX: overlap ? 2 : 4,
                    endY: overlap ? null : miter ? 1 : beamY + 2 * skew,
                    material: beamEnd == ColumnBeamEnd.WeakerOverlap ? 3u : 2u);
            var connection = WithGeometryRelations(contact, HostRelation(branchRole, middle), HostRelation(nextRole, next));
            Add(12, beamJoin, connection, 22528);
            if (repeated) Add(12, repeatedJoin, connection, 23552);
            if (weakMiterPeer)
            {
                double weakEndX = beamEnd == ColumnBeamEnd.EqualMiterWithDifferentWeakPeer ? jointX + 0.25 : jointX;
                Guid weak = AddBeamCutFixture(pool, records, Guid.Empty, Guid.Empty, 24576, jointX, 2.5, 0.25, 0,
                    GeometryRelation(0x11, nextRole, repeatedJoin), transverse: true, startY: beamY,
                    endX: weakEndX, material: 3, concaveSection: true);
                Add(12, repeatedJoin, WithGeometryRelations(contact, HostRelation(branchRole, middle), HostRelation(nextRole, weak)), 29696);
            }
            if (trimmed)
            {
                Guid weak = AddBeamCutFixture(pool, records, Guid.Empty, Guid.Empty, 24576, throughStart, 2.5, 0.25, 0,
                    GeometryRelation(0x11, throughRole, throughTrim), material: 3, concaveSection: true);
                BinaryPrimitives.WriteDoubleLittleEndian(contact.AsSpan(58), throughStart);
                Add(12, throughTrim, WithGeometryRelations(contact, HostRelation(firstEnd, next), HostRelation(throughRole, weak)), 29696);
            }
        }
        Guid beamColumnClass = new("f7f60005-35d0-4346-87ab-29e087ac0bbf");
        if (otherColumn)
        {
            Guid placed = Guid.NewGuid(), otherMemo = Guid.NewGuid(), otherSection = Guid.NewGuid();
            Add(1, placed, WithGeometryRelations(body, GeometryRelation(0x11, memoKey, otherMemo)), 24576);
            Add(2, otherMemo, WithGeometryRelations(columnMemoClass.ToByteArray(), HostRelation(memoKey, placed),
                GeometryRelation(0x11, sectionKey, otherSection), GeometryRelation(0x11, beamKey, extraContact)), 25600);
            Add(3, otherSection, WithGeometryRelations(RectangularSection(new("c4191955-87c6-4fb6-b074-5f094fe9f6a6"), 2, 0.25, -0.25, 0.25),
                HostRelation(sectionKey, otherMemo)), 26624);
            Add(11, extraContact, WithGeometryRelations(beamColumnClass.ToByteArray(),
                HostRelation(beamColumn, middle), HostRelation(beamKey, wrongOtherOwner ? Guid.NewGuid() : otherMemo)), 27648);
        }

        byte[] labelDirectory = [], labelPool = [];
        if (validWallLabel is { } valid)
        {
            Guid wall = Guid.NewGuid(), wallProfile = Guid.NewGuid(), label = Guid.NewGuid();
            Guid profileKey = new("e63e7632-0efd-412d-8077-d3f3906b638a"), labelKey = new("5dd68b40-d2b2-4e13-9238-8343cdb6c423");
            Guid columnLabelKey = new("f73ad2dc-973b-4e70-9787-a3c6fee4a2bf");
            var wallBody = WallWithPlanes(2, 0);
            var settings = wallBody.AsSpan(209);
            BinaryPrimitives.WriteDoubleLittleEndian(settings[8..], -2);
            BinaryPrimitives.WriteDoubleLittleEndian(settings[24..], 2);
            BinaryPrimitives.WriteUInt16LittleEndian(settings[225..], 3);
            WriteRectangularPlan(settings[256..], 0.5, -2, 1, 2, slab: false);
            Add(13, wall, WithGeometryRelations(wallBody, GeometryRelation(0x11, profileKey, wallProfile),
                GeometryRelation(0x11, wallColumn, wallContact), GeometryRelation(0x11, labelKey, label)), 28672);
            PlaProfilePoint[] wallRing = [new(0, 5), new(1, 5), new(1, 6), new(0, 6), new(0, 5)];
            var placedProfile = SavedProfileHatch(2, 1, 0, wallRing);
            Add(14, wallProfile, WithGeometryRelations(SavedWallProfileRecord(wallProfileClass, placedProfile), HostRelation(profileKey, wall)), 29696);
            Add(15, wallContact, WithGeometryRelations(wallColumnClass.ToByteArray(), HostRelation(wallColumn, wall),
                HostRelation(columnWall, memo)), 30720);
            labelDirectory = OwnedDirectory(false, Guid.Empty, 64, new ObjectDirectoryRecord(16, 0, 64, label));
            var labelData = new byte[1024];
            Synthetic.Block(PlaBlockMode.Raw, WithGeometryRelations(labelClass.ToByteArray(),
                HostRelation(labelKey, valid ? wall : Guid.NewGuid()), HostRelation(columnLabelKey, column)),
                ver: 1, guid: label, extra: 1, kind: 16).CopyTo(labelData, 0);
            labelPool = Pool(labelData);
        }

        return Synthetic.Fdb(ClassTable((1, columnClass), (2, columnMemoClass), (3, new("c4191955-87c6-4fb6-b074-5f094fe9f6a6")),
                (4, sectionClass), (5, shapeClass), (6, storyClass), (7, materialClass), (8, layerClass),
                (9, new("a5c2d285-5a7c-4a68-9554-9eda9d4556ca")), (10, new("cbab7bc6-e03a-4133-9161-c28ed3368672")),
                (11, beamColumnClass), (12, beamJoinClass), (13, WallClass), (14, wallProfileClass), (15, wallColumnClass), (16, labelClass)),
            OwnedDirectory(false, Guid.Empty, (ulong)pool.Length / 16, records.ToArray()), labelDirectory,
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 6),
            JunctionAttribute(materialClass, 7, 1, 1000), JunctionAttribute(materialClass, 7, 2, 1200),
            JunctionAttribute(materialClass, 7, 3, 900),
            JunctionAttribute(layerClass, 8, 1, 1), Pool(pool), labelPool);
    }

    private static byte[] SavedMemberProfileShape(Guid shapeClass, int index, byte[] hatch)
    {
        var profile = new byte[400 + 128 + hatch.Length];
        shapeClass.TryWriteBytes(profile);
        BinaryPrimitives.WriteUInt16LittleEndian(profile.AsSpan(47), 1);
        BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(49), index);
        byte[] frame = [2, 0, 9, 1];
        frame.CopyTo(profile, 53);
        frame.CopyTo(profile, 392);
        BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(396), 128 + hatch.Length);
        BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(400), 128);
        BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(400 + 69), 1);
        hatch.CopyTo(profile, 400 + 128);
        return profile;
    }

    [Theory]
    [InlineData(true, "valid")]
    [InlineData(false, "valid")]
    [InlineData(false, "hole")]
    [InlineData(false, "curved")]
    [InlineData(false, "crosshair")]
    [InlineData(false, "invalid crosshair")]
    [InlineData(false, "single annotation")]
    [InlineData(false, "annotation list")]
    [InlineData(false, "annotation owner")]
    [InlineData(true, "memo owner")]
    [InlineData(false, "section owner")]
    [InlineData(true, "shape owner")]
    [InlineData(false, "invalid index")]
    public void ReadsSavedBeamAndColumnProfiles(bool beam, string variation)
    {
        Guid elementClass = new(beam ? "a5c2d285-5a7c-4a68-9554-9eda9d4556ca" : "13e8eb85-5a33-47a3-a4d4-1e05296aec7e");
        Guid memoClass = new(beam ? "cbab7bc6-e03a-4133-9161-c28ed3368672" : "7a1a82ab-69cd-40e6-a7d8-76564f981cf9");
        Guid memoKey = new(beam ? "d5762ca7-d920-455d-a373-28ba6d47152a" : "27448621-9adc-43e6-b5aa-fc76e4cd62ce");
        Guid sectionClass = new("8825ae40-f827-4a57-a1c5-49d04c687c46"), sectionKey = new("63b86c30-a221-421c-84fb-926c694bf611");
        Guid shapeClass = new("f4cd2bd7-d4b1-4abf-b13c-59001907bbbd"), shapeKey = new("ca073b44-0655-407d-9715-1a019e628139");
        Guid element = Guid.NewGuid(), memo = Guid.NewGuid(), section = Guid.NewGuid(), shape = Guid.NewGuid();

        bool crosshair = variation is "crosshair" or "invalid crosshair";
        PlaProfilePoint[] ring = crosshair
            ? [new(-1, -1.5), new(1, -1.5), new(1, 1.5), new(-1, 1.5), new(-1, -1.5)]
            : [new(-1, 0), new(1, 0), new(1, 3), new(-1, 3), new(-1, 0)];
        PlaProfilePoint[] hole = [new(-0.5, 1), new(-0.5, 2), new(0.5, 2), new(0.5, 1), new(-0.5, 1)];
        var hatch = SavedProfileHatch(17, 1, variation == "curved" ? 2 : 0, variation == "hole" ? [ring, hole] : [ring]);
        var profile = SavedMemberProfileShape(shapeClass, variation == "invalid index" ? -1 : 12, hatch);

        Guid annotationClass = new("5793eecb-32e2-4fdd-b216-739ff6c806c4"), annotationKey = new("386bc4fb-ea34-4287-8067-4f16b966b7af");
        Guid annotation = Guid.NewGuid(), secondAnnotation = Guid.NewGuid();
        byte[] annotationLink = variation switch
        {
            "single annotation" => GeometryRelation(0x11, annotationKey, annotation),
            "annotation list" or "annotation owner" => GeometryRelationList(1, annotationKey, annotation, secondAnnotation),
            _ => [],
        };
        const int extent = 2048, units = extent / 16;
        var pool = new byte[(annotationLink.Length == 0 ? 4 : 6) * extent];
        var entries = new List<ObjectDirectoryRecord>();
        void Add(ushort kind, Guid id, byte[] bytes)
        {
            Synthetic.Block(PlaBlockMode.Raw, bytes, ver: 1, guid: id, extra: 1, kind: kind).CopyTo(pool, entries.Count * extent);
            entries.Add(new ObjectDirectoryRecord(kind, (ulong)entries.Count * units, units, id));
        }
        byte[] body = Synthetic.Concat(FramedElementId(elementClass, 735, "P"), new byte[95]);
        if (!beam)
        {
            ElementId("P").CopyTo(body.AsSpan(721));
            WriteLocation(body.AsSpan(701), 1, -1);
            new byte[] { 1, 0, 2, 0, 0, 0, 1, 0 }.CopyTo(body, 304);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(368), 1);
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(312), 10);
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(320), 20);
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(352), -1);
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(370), 10);
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(378), 20);
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(386), 2);
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(410), 1);
            body[741] = crosshair ? (byte)4 : (byte)1;
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(741 + 69), variation == "invalid crosshair" ? double.NaN : 1.25);
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(741 + 87), crosshair ? Math.PI / 6 : Math.PI / 2);
        }
        Add(1, element, WithGeometryRelations(body, GeometryRelation(0x11, memoKey, memo), annotationLink));
        Add(2, memo, WithGeometryRelations(memoClass.ToByteArray(),
            HostRelation(memoKey, variation == "memo owner" ? Guid.NewGuid() : element), GeometryRelation(0x11, sectionKey, section)));
        Add(3, section, WithGeometryRelations(sectionClass.ToByteArray(),
            HostRelation(sectionKey, variation == "section owner" ? Guid.NewGuid() : memo), GeometryRelation(0x11, shapeKey, shape)));
        Add(4, shape, WithGeometryRelations(profile, HostRelation(shapeKey, variation == "shape owner" ? Guid.NewGuid() : section)));
        if (annotationLink.Length != 0)
        {
            Add(6, annotation, WithGeometryRelations(annotationClass.ToByteArray(), HostRelation(annotationKey, element)));
            Add(6, secondAnnotation, WithGeometryRelations(annotationClass.ToByteArray(),
                HostRelation(annotationKey, variation == "annotation owner" ? Guid.NewGuid() : element)));
        }

        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        using var doc = Open(ClassTable((1, elementClass), (2, memoClass), (3, sectionClass), (4, shapeClass), (5, storyClass),
                (6, annotationClass)), OwnedDirectory(false, Guid.Empty, (ulong)pool.Length / 16, entries.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 5), Pool(pool));
        var item = Assert.Single(doc.ReadElements());
        var result = item.MemberProfile;
        if (variation is "memo owner" or "section owner" or "shape owner" or "invalid index")
        {
            Assert.Null(result);
            Assert.Null(item.ColumnExtrusion);
            return;
        }
        var decoded = Assert.IsType<PlaMemberProfile>(result);
        Assert.Equal(12, decoded.ProfileIndex);
        var component = Assert.Single(decoded.Components);
        Assert.Equal(17, component.BuildingMaterialIndex);
        Assert.Equal("Core", component.Function);
        Assert.Equal(ring[..^1], component.Rings[0]);
        if (variation == "curved")
        {
            Assert.Single(component.Arcs!);
            Assert.Null(item.ColumnExtrusion);
            return;
        }
        double area = variation == "hole" ? 5 : 6;
        Assert.Equal(area, component.CrossSectionArea);
        if (beam) return;
        if (variation == "annotation owner")
        {
            Assert.Null(item.BottomElevationToProjectZero);
            Assert.Null(item.ColumnExtrusion);
            return;
        }
        var solid = Assert.IsType<PlaColumnExtrusion>(item.ColumnExtrusion);
        Assert.Equal(new PlaPlanPoint(10, 20), solid.Origin);
        if (crosshair)
        {
            double cos = Math.Sqrt(3) / 2;
            Assert.Equal(10 + cos + 0.75, solid.BoundingBox.XMax, 12);
            Assert.Equal(20 + 0.5 + 1.5 * cos, solid.BoundingBox.YMax, 12);
            if (variation == "invalid crosshair")
                Assert.Null(item.BoundingBox);
            else
            {
                var box = Assert.IsType<PlaBoundingBox>(item.BoundingBox);
                Assert.Equal(10 - 2.25 * cos, box.XMin, 12);
                Assert.Equal(10 + 2.25 * cos, box.XMax, 12);
                Assert.Equal(20 - 2.75 * cos, box.YMin, 12);
                Assert.Equal(20 + 2.75 * cos, box.YMax, 12);
                Assert.Equal((-4d, -2d), (box.ZMin, box.ZMax));
            }
        }
        else
        {
            Assert.Equal(new PlaBoundingBox(7, 19, -4, 10, 21, -2), solid.BoundingBox);
            Assert.Equal(solid.BoundingBox, item.BoundingBox);
        }
        Assert.Equal(2 * area, solid.Volume);
        Assert.Equal(variation == "hole" ? 2 : 1, Assert.Single(solid.Sections).Rings.Count);
    }

    [Theory]
    [InlineData(900, false, false, false, 3.7)]
    [InlineData(1000, false, false, false, 4.0)]
    [InlineData(900, true, false, false, null)]
    [InlineData(900, false, true, false, null)]
    [InlineData(900, false, false, true, 3.825)]
    public void ComputesSlabVolumeFromTheIntersectionWithAPrismaticWallProfile(int slabPriority, bool staleEnvelope,
        bool otherContact, bool composite, double? volume)
    {
        Guid profileClass = new("462b6598-57f0-41a5-ae81-739c8f487e04");
        Guid contactClass = new("324477ff-7a26-4d53-a38e-8f39f678c2b8");
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid materialClass = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5");
        Guid layerClass = new("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d");
        Guid compositeClass = new("c7ecb629-3cf3-4c9c-a222-11082672e04b");
        Guid profileRole = new("e63e7632-0efd-412d-8077-d3f3906b638a");
        Guid slabRole = new("29bce22c-b910-400a-b84a-0b85fb92610e");
        Guid wallRole = new("88f6f041-4ce9-43cd-b0c1-056bf842bf5a");
        Guid slab = Guid.NewGuid(), wall = Guid.NewGuid(), profile = Guid.NewGuid(), contact = Guid.NewGuid();

        var slabBody = OpeningSlab(-1, 0.5, 1);
        if (composite)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(slabBody.AsSpan(191 + 83), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(slabBody.AsSpan(191 + 87), 1);
        }
        var wallBody = WallWithPlanes(1, -4.5);
        var wallSettings = wallBody.AsSpan(209);
        BinaryPrimitives.WriteUInt16LittleEndian(wallSettings[225..], 3);
        BinaryPrimitives.WriteDoubleLittleEndian(wallSettings[153..], 1.5);
        BinaryPrimitives.WriteDoubleLittleEndian(wallSettings[161..], -0.5);
        double[] line = [0.8, -2, 0.8, 2];
        for (int i = 0; i < line.Length; i++)
            BinaryPrimitives.WriteDoubleLittleEndian(wallSettings[(8 * i)..], line[i]);
        new byte[] { 1, 0, 1, 0, 0, 0 }.CopyTo(wallSettings[489..]);
        double[] envelope = [staleEnvelope ? 0.7 : 0.8, -2, 1.2, 2, -4.5, -3];
        for (int i = 0; i < envelope.Length; i++)
            BinaryPrimitives.WriteDoubleLittleEndian(wallSettings[(515 + 8 * i)..], envelope[i]);

        const int hatchSize = 59 + 6 * 16 + 6;
        byte[] profileBody = new byte[402 + 128 + hatchSize];
        profileClass.TryWriteBytes(profileBody);
        new byte[] { 2, 0, 9, 1 }.CopyTo(profileBody, 394);
        BinaryPrimitives.WriteUInt32LittleEndian(profileBody.AsSpan(398), 128 + hatchSize);
        BinaryPrimitives.WriteUInt32LittleEndian(profileBody.AsSpan(402), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(profileBody.AsSpan(402 + 69), 1);
        var hatch = profileBody.AsSpan(402 + 128);
        hatch[0] = 3;
        hatch[1] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(hatch[2..], hatchSize - 6);
        BinaryPrimitives.WriteUInt16LittleEndian(hatch[8..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(hatch[18..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(hatch[55..], 6);
        (double U, double Z)[] points = [(0, 0), (0.4, 0), (0.4, 1.5), (0, 0.5), (0, 0)];
        for (int i = 0; i < points.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(hatch[(75 + 16 * i)..], points[i].U);
            BinaryPrimitives.WriteDoubleLittleEndian(hatch[(83 + 16 * i)..], points[i].Z);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(hatch[(59 + 6 * 16)..], 1);

        byte[] pool = new byte[4 * 1024];
        var entries = new List<ObjectDirectoryRecord>();
        void Add(ushort kind, Guid id, byte[] bytes)
        {
            int offset = entries.Count;
            Synthetic.Block(PlaBlockMode.Raw, bytes, ver: 1, guid: id, extra: 1, kind: kind).CopyTo(pool, offset * 1024);
            entries.Add(new ObjectDirectoryRecord(kind, (ulong)offset * 64, 64, id));
        }
        Add(1, slab, WithGeometryRelations(slabBody, GeometryRelation(0x11, slabRole, contact)));
        Add(2, wall, WithGeometryRelations(wallBody, GeometryRelation(0x11, wallRole, contact),
            GeometryRelation(0x11, profileRole, profile),
            otherContact ? GeometryRelation(0x11, Guid.NewGuid(), Guid.NewGuid()) : []));
        Add(3, profile, WithGeometryRelations(profileBody, HostRelation(profileRole, wall)));
        Add(4, contact, WithGeometryRelations(contactClass.ToByteArray(), HostRelation(slabRole, slab), HostRelation(wallRole, wall)));

        using var doc = Open(ClassTable((1, SlabClass), (2, WallClass), (3, profileClass), (4, contactClass),
                (5, storyClass), (7, materialClass), (8, layerClass), (9, compositeClass)),
            OwnedDirectory(false, Guid.Empty, 256, entries.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 5),
            JunctionAttribute(materialClass, 7, 1, 1000), JunctionAttribute(materialClass, 7, 2, (uint)slabPriority),
            JunctionAttribute(materialClass, 7, 3, 1100), JunctionAttribute(layerClass, 8, 1, 1),
            CompositeForWallTest(compositeClass, splitCuts: false, kind: 9), Pool(pool));
        var geometry = Assert.IsType<PlaSlabGeometry>(Assert.Single(doc.ReadElements(), e => e.Guid == slab).SlabGeometry);
        if (volume.HasValue)
            Assert.Equal(volume.Value, geometry.Volume!.Value, 12);
        else
            Assert.Null(geometry.Volume);
        Assert.Equal(4, geometry.TopAreaWithOpenings);
        Assert.Equal(4, geometry.BottomAreaWithOpenings);
    }

    private static PlaElement ReadElementWithStory(Guid elementClass, byte[] body)
    {
        var (file, _) = ElementWithStoryFile(elementClass, body);
        using var doc = PlaDocument.Open(new MemoryStream(file));
        return Assert.Single(doc.ReadElements());
    }

    private static (byte[] File, Guid Element) ElementWithStoryFile(Guid elementClass, byte[] body)
    {
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid id = Guid.NewGuid();
        var block = Synthetic.Block(PlaBlockMode.Raw, body, ver: 1, guid: id, extra: 1, kind: 1);
        byte[] pool = new byte[Math.Max(1024, (block.Length + 15) / 16 * 16)];
        uint paragraphs = (uint)pool.Length / 16;
        block.CopyTo(pool, 0);
        byte[] file = Synthetic.Fdb(ClassTable((1, elementClass), (2, storyClass)),
            OwnedDirectory(false, Guid.Empty, paragraphs, new ObjectDirectoryRecord(1, 0, paragraphs, id)),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 2), Pool(pool));
        return (file, id);
    }

    internal static (byte[] File, Guid Element) SlabSceneFixture(bool concave, bool beveled)
    {
        const int outlineOffset = 191 + 166;
        const int alternateOffset = 191 + 270;
        byte[] slab = RectangularSlab(true, concave ? 7 : 0);
        int polygonSize = BinaryPrimitives.ReadInt32LittleEndian(slab.AsSpan(alternateOffset + 4));
        slab.AsSpan(alternateOffset, polygonSize).CopyTo(slab.AsSpan(outlineOffset));
        int edgeCount = concave ? 6 : 4;
        int edgesOffset = outlineOffset + polygonSize;
        Array.Resize(ref slab, edgesOffset + 16 + edgeCount * 27);
        WriteSlabEdges(slab.AsSpan(edgesOffset), edgeCount, beveled ? 0 : -1);
        return ElementWithStoryFile(SlabClass, WithGeometryRelations(slab));
    }

    internal static (byte[] File, Guid Element) SlabWithHolesSceneFixture()
    {
        var slab = SlabWithStraightRings(-1,
            [(0, 0), (0, 4), (2, 4), (2, 2), (4, 2), (4, 0), (0, 0)],
            [(0.5, 0.5), (1.5, 0.5), (1.5, 1.5), (0.5, 1.5), (0.5, 0.5)],
            [(2.5, 0.5), (3.5, 0.5), (3.5, 1.5), (2.5, 1.5), (2.5, 0.5)]);
        return ElementWithStoryFile(SlabClass, WithGeometryRelations(slab));
    }

    private static byte[] SlabWithStraightRings(int beveledEdge, params (double X, double Y)[][] rings)
    {
        var points = rings.SelectMany(r => r).ToArray();
        int polygonSize = 71 + points.Length * 16 + (rings.Length + 1) * 4;
        int edgeCount = points.Length - rings.Length;
        const int outlineOffset = 191 + 166;
        var slab = RectangularSlab(true);
        Array.Resize(ref slab, outlineOffset + polygonSize + 16 + edgeCount * 27);
        var polygon = slab.AsSpan(outlineOffset);
        polygon.Clear();
        new byte[] { 2, 0, 2, 1 }.CopyTo(polygon);
        BinaryPrimitives.WriteInt32LittleEndian(polygon[4..], polygonSize);
        new byte[] { 1, 31, 0 }.CopyTo(polygon[8..]);
        BinaryPrimitives.WriteDoubleLittleEndian(polygon[27..], points.Max(p => p.X));
        BinaryPrimitives.WriteDoubleLittleEndian(polygon[35..], points.Max(p => p.Y));
        BinaryPrimitives.WriteInt32LittleEndian(polygon[43..], points.Length);
        BinaryPrimitives.WriteInt32LittleEndian(polygon[51..], rings.Length);
        for (int i = 0; i < points.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(polygon[(71 + i * 16)..], points[i].X);
            BinaryPrimitives.WriteDoubleLittleEndian(polygon[(79 + i * 16)..], points[i].Y);
        }
        int table = 71 + points.Length * 16;
        int end = 0;
        for (int i = 0; i < rings.Length; i++)
        {
            end += rings[i].Length;
            BinaryPrimitives.WriteInt32LittleEndian(polygon[(table + (i + 1) * 4)..], end);
        }
        WriteSlabEdges(polygon[polygonSize..], edgeCount, beveledEdge);
        return slab;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ReadsUnjoinedWallPlanesWhenTheStoredEnvelopeAgrees(bool joined, bool staleEnvelope)
    {
        byte[] wall = WallWithPlanes(1, staleEnvelope ? -2.75 : -3.75);
        byte[] relation = GeometryRelation(0x11, joined
            ? new Guid("0e7a0206-f853-4c86-9f9e-43266b0ab58a")
            : new Guid("3b42cf2e-0d77-4d3d-bf03-0b922103f576"), Guid.NewGuid());
        var element = ReadElementWithStory(WallClass, WithGeometryRelations(wall, relation));
        bool supported = !joined && !staleEnvelope;
        Assert.Equal(supported ? 0.25 : null, element.BottomElevationToHomeStory);
        Assert.Equal(supported ? 3.25 : null, element.TopElevationToHomeStory);
        Assert.Equal(supported ? -3.75 : null, element.BottomElevationToProjectZero);
        Assert.Equal(supported ? -0.75 : null, element.TopElevationToProjectZero);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ReadsTheFramedWallBoxIndependentlyOfItsSchedulePlanes(bool wrongFrame, bool invalidCoordinate)
    {
        byte[] wall = WallWithPlanes(1, -3.75);
        var settings = wall.AsSpan(209);
        new byte[] { 1, 0, 1, 0, 0, 0 }.CopyTo(settings[489..]);
        if (wrongFrame) settings[490] = 1;
        WriteWallStrip(settings, alongX: true, 0.125);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[547..], invalidCoordinate ? double.NaN : -3.5);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[555..], -0.75);
        var element = ReadElementWithStory(WallClass, wall);

        Assert.Equal(wrongFrame || invalidCoordinate ? null : new PlaBoundingBox(-0.5, -0.125, -3.5, 0.5, 0.125, -0.75),
            element.BoundingBox);
        Assert.Null(element.BottomElevationToProjectZero);
    }

    [Theory]
    [InlineData(0, false, 0, false, false)]
    [InlineData(1, false, 0.25, false, false)]
    [InlineData(3, true, 0.0625, false, false)]
    [InlineData(4, true, 0.1875, true, false)]
    [InlineData(0, true, 0, false, true)]
    public void ReadsStraightWallReferencePlacementFromItsInlineContour(uint mode, bool flipped, double offset,
        bool joined, bool inconsistentContour)
    {
        var (file, _) = WallReferenceSceneFixture(mode, flipped, offset, joined, inconsistentContour);
        using var document = PlaDocument.Open(new MemoryStream(file));
        var element = Assert.Single(document.ReadElements());
        var start = new PlaPlanPoint(4, -3);
        var end = new PlaPlanPoint(1, 1);
        double outside = flipped ? offset : -offset;
        double inside = flipped ? offset - 0.25 : 0.25 - offset;
        Assert.Equal(inconsistentContour ? null : new PlaWallReferencePlan(start, end, outside, inside), element.WallReferencePlan);
        if (joined || inconsistentContour)
        {
            Assert.Null(element.BoundingBox);
            Assert.Null(element.BottomElevationToProjectZero);
            Assert.Null(element.TopElevationToHomeStory);
            return;
        }
        double u0 = Math.Min(outside, inside), u1 = Math.Max(outside, inside);
        Assert.Equal(new PlaBoundingBox(end.X + 0.8 * u0, start.Y + 0.6 * u0, -3.75,
            start.X + 0.8 * u1, end.Y + 0.6 * u1, -0.75), element.BoundingBox);
        Assert.Equal(-3.75, element.BottomElevationToProjectZero);
        Assert.Equal(-0.75, element.TopElevationToProjectZero);
        Assert.Equal(0.25, element.BottomElevationToHomeStory);
        Assert.Equal(3.25, element.TopElevationToHomeStory);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(-3.75, true)]
    [InlineData(-3.5, false)]
    public void ReadsStraightWallBoxWithAnEmptyPlanCache(double? storedBottom, bool supported)
    {
        var (file, _) = WallReferenceSceneFixture(0, flipped: false, offset: 0, joined: true, inconsistentContour: false,
            hasEmptyPlanFrame: true, storedBottom: storedBottom);
        using var document = PlaDocument.Open(new MemoryStream(file));
        var wall = Assert.Single(document.ReadElements());
        Assert.Equal(supported ? new PlaBoundingBox(1, -3, -3.75, 4.2, 1.15, -0.75) : null, wall.BoundingBox);
        Assert.Null(wall.BottomElevationToProjectZero);
        Assert.Null(wall.TopElevationToHomeStory);
    }

    internal static (byte[] File, Guid Element) WallReferenceSceneFixture(uint mode, bool flipped, double offset,
        bool joined, bool inconsistentContour, bool? validZoneCalculation = null, bool hasEmptyPlanFrame = false,
        double? storedBottom = null)
    {
        byte[] wall = WallWithPlanes(1, -3.75);
        var settings = wall.AsSpan(209);
        settings[547..563].Clear();
        if (hasEmptyPlanFrame) new byte[] { 1, 0, 1, 0, 0, 0 }.CopyTo(settings[489..]);
        if (storedBottom is { } bottom)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(settings[547..], bottom);
            BinaryPrimitives.WriteDoubleLittleEndian(settings[555..], bottom + 3);
        }
        var start = new PlaPlanPoint(4, -3);
        var end = new PlaPlanPoint(1, 1);
        const double thickness = 0.25;
        BinaryPrimitives.WriteDoubleLittleEndian(settings, start.X);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[8..], start.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[16..], end.X);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[24..], end.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[77..], thickness);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[85..], thickness);
        if (mode >= 3) BinaryPrimitives.WriteUInt16LittleEndian(settings[225..], 2);
        settings[238] = flipped ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(settings[239..], mode);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[243..], offset);
        double outside = flipped ? offset : -offset;
        double inside = flipped ? offset - thickness : thickness - offset;
        double u0 = Math.Min(outside, inside), u1 = Math.Max(outside, inside);
        PlaPlanPoint Shift(PlaPlanPoint point, double u) => new(point.X + 0.8 * u, point.Y + 0.6 * u);
        PlaPlanPoint[] corners = [Shift(start, u1), Shift(end, u1), Shift(end, u0), Shift(start, u0)];
        double minX = corners.Min(p => p.X), minY = corners.Min(p => p.Y);
        double maxX = corners.Max(p => p.X), maxY = corners.Max(p => p.Y);
        var polygon = settings[256..];
        new byte[] { 0, 0, 2, 1, 247, 0, 0, 0, 0, 31, 0 }.CopyTo(polygon);
        BinaryPrimitives.WriteDoubleLittleEndian(polygon[11..], minX);
        BinaryPrimitives.WriteDoubleLittleEndian(polygon[19..], minY);
        BinaryPrimitives.WriteDoubleLittleEndian(polygon[27..], maxX);
        BinaryPrimitives.WriteDoubleLittleEndian(polygon[35..], maxY);
        BinaryPrimitives.WriteUInt32LittleEndian(polygon[43..], 5);
        BinaryPrimitives.WriteUInt32LittleEndian(polygon[51..], 1);
        for (int i = 0; i < 5; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(polygon[(71 + 16 * i)..], corners[i % 4].X);
            BinaryPrimitives.WriteDoubleLittleEndian(polygon[(79 + 16 * i)..], corners[i % 4].Y);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(polygon[155..], 5);
        if (inconsistentContour) BinaryPrimitives.WriteDoubleLittleEndian(polygon[87..], 2);

        var wallRecord = WithGeometryRelations(wall, GeometryRelation(0x11,
            new Guid(joined ? "0e7a0206-f853-4c86-9f9e-43266b0ab58a" : "3b42cf2e-0d77-4d3d-bf03-0b922103f576"), Guid.NewGuid()));
        return validZoneCalculation is { } valid ? WallFixtureWithZoneCalculation(wall, valid) : ElementWithStoryFile(WallClass, wallRecord);
    }

    private static (byte[] File, Guid Element) WallFixtureWithZoneCalculation(byte[] wall, bool valid)
    {
        Guid zoneClass = new("3ad0332e-efe2-491a-92a6-5b14bfdd7229");
        Guid calculationClass = new("ea57dcc5-7231-45df-83dc-792083fc4699");
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid relation = new("cc4c5518-9f96-4272-8130-5d61c5a351e6");
        Guid owner = Guid.NewGuid(), zone = Guid.NewGuid(), record = Guid.NewGuid();
        const int extent = 1024;
        var pool = new byte[extent * 3];
        var wallBody = WithGeometryRelations(wall, GeometryRelationList(2, relation, zone, record));
        var calculation = WithGeometryRelations(calculationClass.ToByteArray(),
            GeometryRelationList(4, relation, zone, valid ? owner : Guid.NewGuid()));
        Synthetic.Block(PlaBlockMode.Raw, wallBody, ver: 1, extra: 1, kind: 1, guid: owner).CopyTo(pool, 0);
        Synthetic.Block(PlaBlockMode.Raw, zoneClass.ToByteArray(), ver: 1, extra: 1, kind: 2, guid: zone).CopyTo(pool, extent);
        Synthetic.Block(PlaBlockMode.Raw, calculation, ver: 1, extra: 1, kind: 3, guid: record).CopyTo(pool, extent * 2);
        return (Synthetic.Fdb(ClassTable((1, WallClass), (2, zoneClass), (3, calculationClass), (4, storyClass)),
            OwnedDirectory(false, Guid.Empty, 192, new ObjectDirectoryRecord(1, 0, 64, owner),
                new ObjectDirectoryRecord(2, 64, 64, zone), new ObjectDirectoryRecord(3, 128, 64, record)),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 4), Pool(pool)), owner);
    }

    [Theory]
    [InlineData(0, 2.0, 2.0)]
    [InlineData(1, 0.25, 1.0)]
    [InlineData(2, -0.25, 0.875)]
    [InlineData(3, null, null)]
    [InlineData(4, null, null)]
    [InlineData(5, 2.0, null)]
    [InlineData(6, 2.0, 2.0)]
    public void ReadsBeamQuantitiesThroughSupportedJunctions(int shape, double? xMax, double? volume)
    {
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid materialClass = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5");
        Guid layerClass = new("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d");
        Guid junctionClass = new("92ee3161-7e81-4cb2-a403-b814edb2ae37");
        Guid firstEnd = new("59655252-b20b-47a7-9d4a-27a2f2bf2c73");
        Guid secondEnd = new("1dae4518-b353-42bc-bcb1-d037bcc603d6");
        Guid through = new("89d11bdb-3a78-48ad-96e7-5b69836c591d");
        Guid secondThrough = new("1d381463-2edc-4aad-91b0-027291e8da94");
        Guid junction = Guid.NewGuid();
        bool joined = shape != 0;
        Guid ownRole = shape >= 5 ? through : firstEnd;
        byte[] pool = new byte[11 * 1024];
        var entries = new List<ObjectDirectoryRecord>();
        Guid own = AddBeamCutFixture(pool, entries, Guid.Empty, Guid.Empty, 0, 0, 2, 0.25, 0,
            joined ? GeometryRelation(0x11, ownRole, junction) : [], endX: joined && shape < 5 ? 0 : null);
        if (joined)
        {
            Guid otherRole = shape switch { 1 => secondEnd, 5 => secondThrough, 6 => firstEnd, _ => through };
            Guid peer = AddBeamCutFixture(pool, entries, Guid.Empty, Guid.Empty, 5120, 0, shape == 3 ? 2.25 : 2, 0.25, 0,
                GeometryRelation(0x11, otherRole, shape == 4 ? Guid.NewGuid() : junction), transverse: true,
                startY: shape is 1 or 6 ? 0 : -2);
            var joint = new byte[67];
            junctionClass.TryWriteBytes(joint);
            new byte[] { 28, 0, 0, 0, 1, 2, 22, 0, 0, 0, 2, 0, 3, 0, 0 }.CopyTo(joint, 35);
            joint[66] = 1;
            byte[] contact = WithGeometryRelations(joint, HostRelation(ownRole, own), HostRelation(otherRole, peer));
            Synthetic.Block(PlaBlockMode.Raw, contact, ver: 1, guid: junction, extra: 1, kind: 12).CopyTo(pool, 10240);
            entries.Add(new(12, 640, 64, junction));
        }
        using var doc = Open(ClassTable((3, new Guid("c4191955-87c6-4fb6-b074-5f094fe9f6a6")), (4, storyClass),
                (7, materialClass), (8, layerClass), (9, BeamClass), (10, new Guid("cbab7bc6-e03a-4133-9161-c28ed3368672")),
                (12, junctionClass)),
            OwnedDirectory(false, Guid.Empty, (ulong)pool.Length / 16, entries.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 4),
            JunctionAttribute(materialClass, 7, 2, 1000),
            JunctionAttribute(layerClass, 8, 1, 1), Pool(pool));

        var beam = Assert.Single(doc.ReadElements(), e => e.Guid == entries[0].ObjectId);
        Assert.Equal(xMax is { } end ? new PlaBoundingBox(-2, -0.25, -3, end, 0.25, -2) : null, beam.BoundingBox);
        Assert.Equal(volume, beam.BeamNetVolume);
        bool hasElevations = xMax.HasValue || shape == 3;
        Assert.Equal(hasElevations ? -3.0 : null, beam.BottomElevationToProjectZero);
        Assert.Equal(hasElevations ? -2.0 : null, beam.TopElevationToProjectZero);
        Assert.Equal(hasElevations ? 1.0 : null, beam.BottomElevationToHomeStory);
        Assert.Equal(hasElevations ? 2.0 : null, beam.TopElevationToHomeStory);
    }

    [Theory]
    [InlineData("unjoined", true, true)]
    [InlineData("rolled", true, true)]
    [InlineData("transverse", true, false)]
    [InlineData("covers axis", false, false)]
    [InlineData("combined cuts", false, false)]
    [InlineData("missing reciprocal", false, false)]
    [InlineData("opposite ends", true, true)]
    [InlineData("overlapping ends", false, false)]
    public void ReadsBeamElevationsWhenAProfileSectionSurvives(string junctionCase, bool hasElevations, bool hasBox)
    {
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid materialClass = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5"), layerClass = new("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d");
        Guid memoClass = new("cbab7bc6-e03a-4133-9161-c28ed3368672"), memoKey = new("d5762ca7-d920-455d-a373-28ba6d47152a");
        Guid sectionClass = new("8825ae40-f827-4a57-a1c5-49d04c687c46"), sectionKey = new("63b86c30-a221-421c-84fb-926c694bf611");
        Guid shapeClass = new("f4cd2bd7-d4b1-4abf-b13c-59001907bbbd"), shapeKey = new("ca073b44-0655-407d-9715-1a019e628139");
        Guid junctionClass = new("92ee3161-7e81-4cb2-a403-b814edb2ae37");
        Guid own = Guid.NewGuid(), ownMemo = Guid.NewGuid(), segment = Guid.NewGuid(), profileId = Guid.NewGuid(), junction = Guid.NewGuid();
        Guid secondJunction = Guid.NewGuid();
        Guid peer = Guid.NewGuid(), peerMemo = Guid.NewGuid(), peerSegment = Guid.NewGuid(), peerShape = Guid.NewGuid();
        bool collinear = junctionCase is "opposite ends" or "overlapping ends";
        bool joined = junctionCase is not ("unjoined" or "rolled");
        bool twoCuts = junctionCase == "combined cuts";
        double roll = junctionCase == "rolled" ? Math.PI / 6 : 0;
        Guid ownRole = new(collinear ? "59655252-b20b-47a7-9d4a-27a2f2bf2c73" : "89d11bdb-3a78-48ad-96e7-5b69836c591d");
        Guid peerRole = new(collinear ? "1dae4518-b353-42bc-bcb1-d037bcc603d6" : "1d381463-2edc-4aad-91b0-027291e8da94");

        PlaProfilePoint[] lowerRing = [new(-1, -2), new(1, -2), new(1, -1), new(-1, -1), new(-1, -2)];
        PlaProfilePoint[] upperRing = [new(-0.5, -1), new(0.5, -1), new(0.5, 0.5), new(-0.5, 0.5), new(-0.5, -1)];
        var lower = SavedProfileHatch(2, 1, 2, lowerRing);
        var upper = SavedProfileHatch(3, 0, 0, upperRing);
        var profile = new byte[528 + lower.Length + 16 + upper.Length];
        shapeClass.TryWriteBytes(profile);
        BinaryPrimitives.WriteUInt16LittleEndian(profile.AsSpan(47), 1);
        BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(49), 12);
        byte[] frame = [2, 0, 9, 1];
        frame.CopyTo(profile, 53);
        frame.CopyTo(profile, 392);
        BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(396), profile.Length - 400);
        BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(400), 128);
        BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(469), 2);
        lower.CopyTo(profile, 528);
        upper.CopyTo(profile, 528 + lower.Length + 16);

        const int extent = 2048, units = extent / 16;
        var pool = new byte[12 * extent];
        var entries = new List<ObjectDirectoryRecord>();
        Add(9, own, WithGeometryRelations(HorizontalBeam(BeamClass, -2, 0, collinear ? 0 : 2, 0, 2, roll, 1),
            GeometryRelation(0x11, memoKey, ownMemo)));
        Add(10, ownMemo, WithGeometryRelations(memoClass.ToByteArray(), HostRelation(memoKey, own),
            GeometryRelation(0x11, sectionKey, segment), twoCuts ? GeometryRelationList(1, ownRole, junction, secondJunction) :
                joined ? GeometryRelation(0x11, ownRole, junction) : []));
        Add(13, segment, WithGeometryRelations(sectionClass.ToByteArray(), HostRelation(sectionKey, ownMemo), GeometryRelation(0x11, shapeKey, profileId)));
        Add(14, profileId, WithGeometryRelations(profile, HostRelation(shapeKey, segment)));
        if (joined)
        {
            if (collinear)
            {
                Add(9, peer, WithGeometryRelations(HorizontalBeam(BeamClass, 0, 0, junctionCase == "opposite ends" ? 2 : -2, 0, 2, 0, 1),
                    GeometryRelation(0x11, memoKey, peerMemo)));
                Add(10, peerMemo, WithGeometryRelations(memoClass.ToByteArray(), HostRelation(memoKey, peer),
                    GeometryRelation(0x11, sectionKey, peerSegment), GeometryRelation(0x11, peerRole, junction)));
                Add(13, peerSegment, WithGeometryRelations(sectionClass.ToByteArray(), HostRelation(sectionKey, peerMemo),
                    GeometryRelation(0x11, shapeKey, peerShape)));
                Add(14, peerShape, WithGeometryRelations(profile, HostRelation(shapeKey, peerSegment)));
            }
            else
            {
                double halfWidth = junctionCase == "covers axis" ? 2.25 : twoCuts ? 1.1 : 0.25;
                peerMemo = AddBeamCutFixture(pool, entries, Guid.Empty, Guid.Empty, 4 * extent, twoCuts ? -1 : 0, 2, halfWidth, 0,
                    GeometryRelation(0x11, peerRole, junctionCase == "missing reciprocal" ? Guid.NewGuid() : junction), transverse: true);
            }
            AddJunction(junction, peerMemo, twoCuts ? -1 : 0, 11);
            if (twoCuts)
            {
                Guid second = AddBeamCutFixture(pool, entries, Guid.Empty, Guid.Empty, 7 * extent, 1, 2, 1.1, 0,
                    GeometryRelation(0x11, peerRole, secondJunction), transverse: true);
                AddJunction(secondJunction, second, 1, 10);
            }
        }
        using var document = Open(ClassTable((3, new Guid("c4191955-87c6-4fb6-b074-5f094fe9f6a6")), (4, storyClass),
                (7, materialClass), (8, layerClass), (9, BeamClass), (10, memoClass), (12, junctionClass), (13, sectionClass), (14, shapeClass)),
            OwnedDirectory(false, Guid.Empty, 12 * units, entries.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 4),
            JunctionAttribute(materialClass, 7, 2, 1000), JunctionAttribute(materialClass, 7, 3, 800),
            JunctionAttribute(layerClass, 8, 1, 1), Pool(pool));
        var beam = Assert.Single(document.ReadElements(), e => e.Guid == own);
        Assert.Equal(2, Assert.IsType<PlaMemberProfile>(beam.MemberProfile).Components.Count);
        if (roll == 0)
        {
            Assert.Equal(hasBox ? new PlaBoundingBox(-2, -1, -5, collinear ? 0 : 2, 1, -1.5) : null, beam.BoundingBox);
            Assert.Equal(hasElevations ? -5.0 : null, beam.BottomElevationToProjectZero);
            Assert.Equal(hasElevations ? -1.5 : null, beam.TopElevationToProjectZero);
            Assert.Equal(hasElevations ? -1.0 : null, beam.BottomElevationToHomeStory);
            Assert.Equal(hasElevations ? 2.5 : null, beam.TopElevationToHomeStory);
        }
        else
        {
            var box = Assert.IsType<PlaBoundingBox>(beam.BoundingBox);
            double low = -3 - Math.Sqrt(3), high = -2 + (1 + Math.Sqrt(3)) / 4;
            Assert.Equal(-2, box.YMin, 12);
            Assert.Equal(high + 2, box.YMax, 12);
            Assert.Equal(low, box.ZMin, 12);
            Assert.Equal(high, box.ZMax, 12);
            Assert.Equal(low + 4, Assert.IsType<double>(beam.BottomElevationToHomeStory), 12);
            Assert.Equal(high + 4, Assert.IsType<double>(beam.TopElevationToHomeStory), 12);
        }
        Assert.Null(beam.BeamNetVolume);

        void AddJunction(Guid id, Guid neighbor, double x, int slot)
        {
            var contact = new byte[67];
            junctionClass.TryWriteBytes(contact);
            new byte[] { 28, 0, 0, 0, 1, 2, 22, 0, 0, 0, 2, 0, 3, 0, 0 }.CopyTo(contact, 35);
            BinaryPrimitives.WriteDoubleLittleEndian(contact.AsSpan(50), x);
            contact[66] = 1;
            Add(12, id, WithGeometryRelations(contact, HostRelation(ownRole, ownMemo), HostRelation(peerRole, neighbor)), slot);
        }

        void Add(ushort kind, Guid id, byte[] payload, int? slot = null)
        {
            int position = slot ?? entries.Count;
            Synthetic.Block(PlaBlockMode.Raw, payload, ver: 1, guid: id, extra: 1, kind: kind).CopyTo(pool, position * extent);
            entries.Add(new ObjectDirectoryRecord(kind, (ulong)position * units, units, id));
        }
    }

    [Theory]
    [InlineData(5, 1, 0, false, -3.25, 1.0, 2.0, 2.5)]
    [InlineData(-1, 1, 0, false, -6.25, -2.0, 2.0, 2.5)]
    [InlineData(5, 0.8, 0.6, false, -3.25, 1.0, 2.6, null)]
    [InlineData(2, 0.8, 0.6, false, -3.0, -2.0, 2.75, null)]
    [InlineData(5, -1, 0, false, null, null, null, null)]
    [InlineData(5, 1, 0, true, null, null, null, null)]
    public void ReadsBeamBoundsFromInclinationAndStoredCutPlanes(double endElevation, double normalX, double normalZ,
        bool unresolvedContact, double? bottom, double? top, double? xMax, double? volume)
    {
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid materialClass = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5");
        Guid rectangularSection = new("c4191955-87c6-4fb6-b074-5f094fe9f6a6");
        Guid beamMemoClass = new("cbab7bc6-e03a-4133-9161-c28ed3368672");
        var pool = new byte[5 * 1024];
        var entries = new List<ObjectDirectoryRecord>();
        AddBeamCutFixture(pool, entries, Guid.Empty, Guid.Empty, 0, 0, 2, 0.25, 0,
            unresolvedContact ? GeometryRelation(0x11, Guid.NewGuid(), Guid.NewGuid()) : [],
            endElevation: endElevation, endNormal: (normalX, normalZ));
        using var document = Open(ClassTable((3, rectangularSection), (4, storyClass), (7, materialClass),
                (9, BeamClass), (10, beamMemoClass)),
            OwnedDirectory(false, Guid.Empty, (ulong)pool.Length / 16, entries.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 4),
            JunctionAttribute(materialClass, 7, 2, 1000), Pool(pool));
        var beam = Assert.Single(document.ReadElements());
        var reference = Assert.IsType<PlaBeamReferenceAxis>(beam.BeamReferenceAxis);
        Assert.Equal(new PlaPlanPoint(-2, 0), reference.Start);
        Assert.Equal(new PlaPlanPoint(2, 0), reference.End);
        Assert.Equal(-2, reference.StartElevationToProjectZero);
        Assert.Equal(endElevation - 4, reference.EndElevationToProjectZero);
        Assert.Equal(0, reference.PlanSweepRadians);
        Assert.Equal(Math.Atan2(endElevation - 2, 4), reference.InclinationRadians, 12);
        Assert.Equal(double.Hypot(4, endElevation - 2), reference.Length, 12);
        Assert.Equal(volume, beam.BeamNetVolume);
        if (bottom is { } low && top is { } high && xMax is { } end)
        {
            var box = Assert.IsType<PlaBoundingBox>(beam.BoundingBox);
            Assert.Equal(-2.0, box.XMin, 12);
            Assert.Equal(end, box.XMax, 12);
            Assert.Equal(-0.25, box.YMin, 12);
            Assert.Equal(0.25, box.YMax, 12);
            Assert.Equal(low, box.ZMin, 12);
            Assert.Equal(high, box.ZMax, 12);
            Assert.Equal(low, Assert.IsType<double>(beam.BottomElevationToProjectZero), 12);
            Assert.Equal(high, Assert.IsType<double>(beam.TopElevationToProjectZero), 12);
            Assert.Equal(low + 4, Assert.IsType<double>(beam.BottomElevationToHomeStory), 12);
            Assert.Equal(high + 4, Assert.IsType<double>(beam.TopElevationToHomeStory), 12);
        }
        else
        {
            Assert.Null(beam.BoundingBox);
            Assert.Null(beam.BottomElevationToProjectZero);
            Assert.Null(beam.TopElevationToProjectZero);
            Assert.Null(beam.BottomElevationToHomeStory);
            Assert.Null(beam.TopElevationToHomeStory);
        }
    }

    [Theory]
    [InlineData(1000, 0.25, 1.75, true, 0, null, null, true)]
    [InlineData(700, 0.25, 1.75, true, 0, null, null, false)]
    [InlineData(1000, 0.10, 1.75, true, 0, null, null, false)]
    [InlineData(1000, 0.25, 2.125, true, 0, null, null, false)]
    [InlineData(1000, 0.25, 1.75, false, 0, null, null, false)]
    [InlineData(1000, 0.25, 1.75, true, 800, null, null, true)]
    [InlineData(1000, 0.25, 1.75, true, 1100, null, null, false)]
    [InlineData(1000, 0.25, 1.75, true, 0, 0.0, null, true)]
    [InlineData(1000, 0.25, 1.75, true, 0, 0.01, null, false)]
    [InlineData(1000, 0.25, 1.75, true, 0, null, "end under cover", true)]
    [InlineData(1000, 0.25, 1.75, true, 0, null, "end outside cover", false)]
    [InlineData(1000, 0.25, 1.75, true, 0, null, "slab above profile", true)]
    [InlineData(1000, 0.25, 1.75, true, 0, null, "slab within profile", false)]
    [InlineData(1000, 0.25, 1.75, true, 0, null, "angular dimension", true)]
    [InlineData(1000, 0.25, 1.75, true, 0, null, "wrong angular dimension owner", false)]
    public void ReadsZeroNetVolumeWhenRectangularBeamsCoverTheWholeProfile(
        uint coveringPriority, double halfWidth, double elevation, bool reciprocal, uint competingPriority,
        double? secondCoverStart, string? extraContact, bool hasZeroVolume)
    {
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid materialClass = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5"), layerClass = new("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d");
        Guid memoClass = new("cbab7bc6-e03a-4133-9161-c28ed3368672"), memoKey = new("d5762ca7-d920-455d-a373-28ba6d47152a");
        Guid sectionClass = new("8825ae40-f827-4a57-a1c5-49d04c687c46"), sectionKey = new("63b86c30-a221-421c-84fb-926c694bf611");
        Guid shapeClass = new("f4cd2bd7-d4b1-4abf-b13c-59001907bbbd"), shapeKey = new("ca073b44-0655-407d-9715-1a019e628139");
        Guid junctionClass = new("92ee3161-7e81-4cb2-a403-b814edb2ae37");
        Guid mine = new("89d11bdb-3a78-48ad-96e7-5b69836c591d"), other = new("1d381463-2edc-4aad-91b0-027291e8da94");
        Guid endRole = new("59655252-b20b-47a7-9d4a-27a2f2bf2c73");
        Guid beamSlab = new("4df5ee09-8368-4f3b-ad64-056115b4c0ab"), slabBeam = new("d50bcd56-a71c-40f8-a3f1-551d14a4762b");
        Guid slabJunctionClass = new("7259c45f-97e3-434a-bc9e-fe3bcf980947");
        Guid owner = Guid.NewGuid(), memo = Guid.NewGuid(), section = Guid.NewGuid(), shape = Guid.NewGuid(), joint = Guid.NewGuid();
        Guid competingJoint = Guid.NewGuid();
        Guid secondCoverJoint = Guid.NewGuid();
        Guid additionalJoint = Guid.NewGuid();
        Guid dimension = Guid.NewGuid();
        Guid dimensionClass = new("2120198c-d317-4cea-ab4e-c7b6ceb5156e");
        Guid dimensionRole = new("b3ffaf20-1c37-4090-b8f3-9e65bd5fa244");
        bool dimensionAttached = extraContact is "angular dimension" or "wrong angular dimension owner";
        bool endJoint = extraContact is "end under cover" or "end outside cover";
        bool slabCut = extraContact is "slab above profile" or "slab within profile";
        const int extent = 2048, units = extent / 16;
        var pool = new byte[12 * extent];
        var entries = new List<ObjectDirectoryRecord>();
        Add(0, 9, owner, WithGeometryRelations(HorizontalBeam(BeamClass, 1, 0, -1, 0, elevation, 0, 1),
            GeometryRelation(0x11, memoKey, memo), dimensionAttached ? GeometryRelation(0x11, dimensionRole, dimension) : []));
        Add(1, 10, memo, WithGeometryRelations(memoClass.ToByteArray(), HostRelation(memoKey, owner),
            GeometryRelation(0x11, sectionKey, section), secondCoverStart.HasValue
                ? GeometryRelationList(1, mine, joint, secondCoverJoint) : GeometryRelation(0x11, mine, joint),
            endJoint ? GeometryRelation(0x11, endRole, additionalJoint) : []));
        Add(2, 13, section, WithGeometryRelations(sectionClass.ToByteArray(), HostRelation(sectionKey, memo),
            GeometryRelation(0x11, shapeKey, shape)));

        PlaProfilePoint[] ring = [new(-0.125, -0.5), new(0.15, -0.5), new(0.15, 0), new(-0.125, 0), new(-0.125, -0.5)];
        var hatch = SavedProfileHatch(1, 1, 0, ring);
        var profile = new byte[528 + hatch.Length];
        shapeClass.TryWriteBytes(profile);
        BinaryPrimitives.WriteUInt16LittleEndian(profile.AsSpan(47), 1);
        BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(49), 1);
        byte[] frame = [2, 0, 9, 1];
        frame.CopyTo(profile, 53);
        frame.CopyTo(profile, 392);
        BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(396), profile.Length - 400);
        BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(400), 128);
        BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(469), 1);
        hatch.CopyTo(profile, 528);
        Add(3, 14, shape, WithGeometryRelations(profile, HostRelation(shapeKey, section)));

        Guid savedJoint = reciprocal ? joint : Guid.NewGuid();
        byte[] relations = competingPriority == 0 ? GeometryRelation(0x11, other, savedJoint)
            : GeometryRelationList(1, other, savedJoint, competingJoint);
        if (slabCut) relations = Synthetic.Concat(relations, GeometryRelation(0x11, beamSlab, additionalJoint));
        Guid peerMemo = AddBeamCutFixture(pool, entries, Guid.Empty, Guid.Empty, 4 * extent, 0, 2, halfWidth, 0, relations,
            endX: secondCoverStart.HasValue ? 0 : extraContact == "end outside cover" ? 1.1 : null);
        var contact = new byte[67];
        junctionClass.TryWriteBytes(contact);
        new byte[] { 28, 0, 0, 0, 1, 2, 22, 0, 0, 0, 2, 0, 3, 0, 0 }.CopyTo(contact, 35);
        Add(7, 11, joint, WithGeometryRelations(contact, HostRelation(mine, memo), HostRelation(other, peerMemo)));
        if (competingPriority > 0)
        {
            Guid competitor = AddBeamCutFixture(pool, entries, Guid.Empty, Guid.Empty, 8 * extent, 0, 2, 0.5, 0,
                GeometryRelation(0x11, mine, competingJoint), material: 3);
            Add(11, 11, competingJoint, WithGeometryRelations(contact, HostRelation(mine, competitor), HostRelation(other, peerMemo)));
        }
        if (secondCoverStart is { } start)
        {
            Guid second = AddBeamCutFixture(pool, entries, Guid.Empty, Guid.Empty, 8 * extent, 0, 2, halfWidth, 0,
                GeometryRelation(0x11, other, secondCoverJoint), startX: start);
            Add(11, 11, secondCoverJoint, WithGeometryRelations(contact, HostRelation(mine, memo), HostRelation(other, second)));
        }
        if (endJoint)
        {
            Guid branch = AddBeamCutFixture(pool, entries, Guid.Empty, Guid.Empty, 8 * extent, 1, 2, 0.25, 0,
                GeometryRelation(0x11, mine, additionalJoint), transverse: true);
            BinaryPrimitives.WriteDoubleLittleEndian(contact.AsSpan(50), 1);
            contact[66] = 1;
            Add(11, 11, additionalJoint, WithGeometryRelations(contact, HostRelation(endRole, memo), HostRelation(mine, branch)));
        }
        if (slabCut)
        {
            Guid slab = Guid.NewGuid();
            var body = OpeningSlab(-1, extraContact == "slab above profile" ? 1.95 : 1.7, 0.1);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(191 + 73), 3);
            WriteRectangularPlan(body.AsSpan(191 + 270), -0.125, -0.5, 0.125, 0.5, slab: true);
            Add(8, 6, slab, WithGeometryRelations(body, GeometryRelation(0x11, slabBeam, additionalJoint)));
            Add(9, 15, additionalJoint, WithGeometryRelations(slabJunctionClass.ToByteArray(),
                HostRelation(beamSlab, peerMemo), HostRelation(slabBeam, slab)));
        }
        if (dimensionAttached)
            Add(11, 16, dimension, WithGeometryRelations(dimensionClass.ToByteArray(),
                HostRelation(dimensionRole, extraContact == "angular dimension" ? owner : Guid.NewGuid())));

        using var document = Open(ClassTable((3, new Guid("c4191955-87c6-4fb6-b074-5f094fe9f6a6")), (4, storyClass),
                (6, SlabClass), (7, materialClass), (8, layerClass), (9, BeamClass), (10, memoClass), (11, junctionClass),
                (13, sectionClass), (14, shapeClass), (15, slabJunctionClass), (16, dimensionClass)),
            OwnedDirectory(false, Guid.Empty, 12 * units, entries.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 4),
            JunctionAttribute(layerClass, 8, 1, 1), JunctionAttribute(materialClass, 7, 1, 700),
            JunctionAttribute(materialClass, 7, 2, coveringPriority), JunctionAttribute(materialClass, 7, 3, slabCut ? 1100 : competingPriority),
            Pool(pool));
        var beam = Assert.Single(document.ReadElements(), e => e.Guid == owner);
        Assert.NotNull(beam.MemberProfile);
        Assert.NotNull(beam.BeamReferenceAxis);
        Assert.Null(beam.BoundingBox);
        Assert.Null(beam.BottomElevationToHomeStory);
        Assert.Equal(hasZeroVolume ? 0 : (double?)null, beam.BeamNetVolume);

        void Add(int index, ushort kind, Guid id, byte[] data)
        {
            Synthetic.Block(PlaBlockMode.Raw, data, ver: 1, guid: id, extra: 1, kind: kind).CopyTo(pool, index * extent);
            entries.Add(new ObjectDirectoryRecord(kind, (ulong)index * units, units, id));
        }
    }

    [Theory]
    [InlineData(Math.PI / 2, true, true)]
    [InlineData(-Math.PI / 2, true, true)]
    [InlineData(Math.PI / 2, false, true)]
    [InlineData(Math.PI / 2, true, false)]
    public void ReadsDirectedCircularBeamAxis(double sweep, bool matchingCaps, bool reciprocal)
    {
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid memoClass = new("cbab7bc6-e03a-4133-9161-c28ed3368672"), memoKey = new("d5762ca7-d920-455d-a373-28ba6d47152a");
        Guid id = Guid.NewGuid(), memo = Guid.NewGuid();
        var placement = HorizontalBeam(BeamClass, -2, 0, 2, 0, 2, 0, 1);
        BinaryPrimitives.WriteDoubleLittleEndian(placement.AsSpan(790), sweep);
        double c = Math.Cos(sweep / 2), s = Math.Sin(sweep / 2);
        BinaryPrimitives.WriteDoubleLittleEndian(placement.AsSpan(336), -c);
        BinaryPrimitives.WriteDoubleLittleEndian(placement.AsSpan(344), matchingCaps ? s : -s);
        BinaryPrimitives.WriteDoubleLittleEndian(placement.AsSpan(352), 1e-32);
        BinaryPrimitives.WriteDoubleLittleEndian(placement.AsSpan(394), c);
        BinaryPrimitives.WriteDoubleLittleEndian(placement.AsSpan(402), s);
        var pool = new byte[3 * 1024];
        var body = WithGeometryRelations(placement, GeometryRelation(0x11, memoKey, memo));
        Synthetic.Block(PlaBlockMode.Raw, body, ver: 1, guid: id, extra: 1, kind: 9).CopyTo(pool, 0);
        var linked = WithGeometryRelations(memoClass.ToByteArray(), HostRelation(memoKey, reciprocal ? id : Guid.NewGuid()));
        Synthetic.Block(PlaBlockMode.Raw, linked, ver: 1, guid: memo, extra: 1, kind: 10).CopyTo(pool, 2048);
        using var document = Open(ClassTable((4, storyClass), (9, BeamClass), (10, memoClass)),
            OwnedDirectory(false, Guid.Empty, 192, new ObjectDirectoryRecord(9, 0, 128, id),
                new ObjectDirectoryRecord(10, 128, 64, memo)),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 4), Pool(pool));
        var beam = Assert.Single(document.ReadElements());
        Assert.Null(beam.BoundingBox);
        if (!matchingCaps || !reciprocal)
        {
            Assert.Null(beam.BeamReferenceAxis);
            return;
        }
        var reference = Assert.IsType<PlaBeamReferenceAxis>(beam.BeamReferenceAxis);
        Assert.Equal(new PlaPlanPoint(-2, 0), reference.Start);
        Assert.Equal(new PlaPlanPoint(2, 0), reference.End);
        Assert.Equal(-2, reference.StartElevationToProjectZero);
        Assert.Equal(-2, reference.EndElevationToProjectZero);
        Assert.Equal(sweep, reference.PlanSweepRadians);
        Assert.Equal(Math.PI * Math.Sqrt(2), reference.Length, 12);
        Assert.Equal(0, reference.InclinationRadians);
    }

    [Theory]
    [InlineData("free")]
    [InlineData("clockwise")]
    [InlineData("major arc")]
    [InlineData("straight joint")]
    [InlineData("curved joint")]
    [InlineData("near tangency")]
    [InlineData("stale node")]
    [InlineData("missing reciprocal")]
    [InlineData("interior slab")]
    [InlineData("slab at bottom")]
    [InlineData("assembly")]
    [InlineData("transformed member")]
    public void ReadsEvaluatedCircularBeamBounds(string scenario)
    {
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid materialClass = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5"), layerClass = new("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d");
        Guid memoClass = new("cbab7bc6-e03a-4133-9161-c28ed3368672"), memoKey = new("d5762ca7-d920-455d-a373-28ba6d47152a");
        Guid sectionClass = new("8825ae40-f827-4a57-a1c5-49d04c687c46"), sectionKey = new("63b86c30-a221-421c-84fb-926c694bf611");
        Guid shapeClass = new("f4cd2bd7-d4b1-4abf-b13c-59001907bbbd"), shapeKey = new("ca073b44-0655-407d-9715-1a019e628139");
        Guid junctionClass = new("92ee3161-7e81-4cb2-a403-b814edb2ae37"), slabJunctionClass = new("7259c45f-97e3-434a-bc9e-fe3bcf980947");
        Guid ownRole = new("59655252-b20b-47a7-9d4a-27a2f2bf2c73"), peerRole = new("1dae4518-b353-42bc-bcb1-d037bcc603d6");
        Guid beamSlab = new("4df5ee09-8368-4f3b-ad64-056115b4c0ab"), slabBeam = new("d50bcd56-a71c-40f8-a3f1-551d14a4762b");
        Guid groupClass = new("8f2fee46-6a3f-41ac-a32b-69e44dc6276d"), memberClass = new("47a28b23-f655-49fa-b219-1511ca52c49a");
        Guid groupKey = new("68236820-e176-4e8c-be39-d5d56a7a71c0");
        Guid own = Guid.NewGuid(), memo = Guid.NewGuid(), peer = Guid.NewGuid(), peerMemo = Guid.NewGuid();
        Guid joint = Guid.NewGuid(), duplicate = Guid.NewGuid(), group = Guid.NewGuid(), member = Guid.NewGuid();
        bool clockwise = scenario == "clockwise";
        bool major = scenario == "major arc";
        bool tangency = scenario is "near tangency" or "stale node";
        bool joined = tangency || scenario is "straight joint" or "curved joint" or "missing reciprocal";
        bool slabCut = scenario is "interior slab" or "slab at bottom";
        bool grouped = scenario is "assembly" or "transformed member";
        double sweep = clockwise ? -Math.PI / 2 : Math.PI / 2;
        const int extent = 2048, units = extent / 16;
        var pool = new byte[14 * extent];
        var entries = new List<ObjectDirectoryRecord>();
        double corner = 3 / Math.Sqrt(2);
        var placement = major ? ArcBeam(new(-corner, -corner), new(-corner, corner), 3 * Math.PI / 2)
            : ArcBeam(new(3, 0), new(0, clockwise ? -3 : 3), sweep);
        AddProfiledBeam(own, memo, placement,
            joined ? GeometryRelationList(1, ownRole, joint, duplicate) : slabCut ? GeometryRelation(0x11, beamSlab, joint) : [],
            grouped ? GeometryRelationList(2, groupKey, group, member) : []);
        if (grouped)
        {
            Add(17, group, WithGeometryRelations(groupClass.ToByteArray(), GeometryRelationList(1, groupKey, own)));
            var body = new byte[84];
            memberClass.TryWriteBytes(body);
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(60), scenario == "assembly" ? 0 : 0.2);
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(76), 1);
            Add(18, member, WithGeometryRelations(body, GeometryRelationList(4, groupKey, group, own)));
        }
        if (joined)
        {
            double c = Math.Sqrt(3) / 2;
            bool curve = scenario == "curved joint";
            var end = curve ? new PlaPlanPoint(1.5 - 3 * c, 4.5 + 3 * c)
                : tangency ? new(-4 * Math.Cos(1e-6), 3 - 4 * Math.Sin(1e-6)) : new(-4 * c, 5);
            AddProfiledBeam(peer, peerMemo, ArcBeam(new(0, 3), end, curve ? -Math.PI / 2 : 0),
                GeometryRelationList(1, peerRole, joint, scenario == "missing reciprocal" ? Guid.NewGuid() : duplicate), []);
            AddJoint(joint);
            AddJoint(duplicate);
        }
        if (slabCut)
        {
            Guid slab = Guid.NewGuid();
            var body = OpeningSlab(-1, scenario == "interior slab" ? 1.5 : 1.1, 0.2);
            var settings = body.AsSpan(191);
            WriteRectangularPlan(settings[270..], -1, -1, 4, 4, slab: true);
            Add(6, slab, WithGeometryRelations(body, GeometryRelation(0x11, slabBeam, joint)));
            Add(16, joint, WithGeometryRelations(slabJunctionClass.ToByteArray(), HostRelation(beamSlab, memo), HostRelation(slabBeam, slab)));
        }

        using var document = Open(ClassTable((4, storyClass), (6, SlabClass), (7, materialClass), (8, layerClass),
                (9, BeamClass), (10, memoClass), (12, junctionClass), (13, sectionClass), (14, shapeClass),
                (16, slabJunctionClass), (17, groupClass), (18, memberClass)),
            OwnedDirectory(false, Guid.Empty, 14 * units, entries.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 4),
            JunctionAttribute(materialClass, 7, 1, 100), JunctionAttribute(materialClass, 7, 2, 200),
            JunctionAttribute(layerClass, 8, 1, 1), Pool(pool));
        var beam = Assert.Single(document.ReadElements(), e => e.Guid == own);
        Assert.NotNull(beam.BeamReferenceAxis);
        Assert.NotNull(beam.MemberProfile);
        Assert.Null(beam.BeamNetVolume);
        if (scenario is "missing reciprocal" or "slab at bottom" or "transformed member" or "stale node")
        {
            Assert.Null(beam.BoundingBox);
            Assert.Null(beam.BottomElevationToProjectZero);
            Assert.Null(beam.TopElevationToHomeStory);
            return;
        }

        double minX = major ? -corner : 0;
        if (joined && !tangency)
        {
            // At the inner radius the end is an intersection of the two offset curves.
            double c = Math.Sqrt(3) / 2;
            if (scenario == "curved joint")
            {
                double dx = 1.5, dy = 3 + 3 * c, centerDistance = double.Hypot(dx, dy);
                double along = (centerDistance * centerDistance - 12) / (2 * centerDistance);
                minX = (dx * along - dy * Math.Sqrt(4 - along * along)) / centerDistance;
            }
            else
            {
                double along = 1 - 3 * c;
                minX = -along / 2 - c * Math.Sqrt(4 - along * along);
            }
        }
        var box = Assert.IsType<PlaBoundingBox>(beam.BoundingBox);
        Assert.Equal(minX, box.XMin, 10);
        Assert.Equal(clockwise ? -4 : major ? -3 : 0, box.YMin, 10);
        Assert.Equal(clockwise ? 4 : 3, box.XMax, 10);
        Assert.Equal(clockwise ? 0 : 3, box.YMax, 10);
        Assert.Equal(-3, box.ZMin, 10);
        Assert.Equal(-1.5, box.ZMax, 10);
        Assert.Equal(-3, beam.BottomElevationToProjectZero);
        Assert.Equal(-1.5, beam.TopElevationToProjectZero);
        Assert.Equal(1, beam.BottomElevationToHomeStory);
        Assert.Equal(2.5, beam.TopElevationToHomeStory);

        static byte[] ArcBeam(PlaPlanPoint a, PlaPlanPoint b, double sweep)
        {
            var body = HorizontalBeam(BeamClass, a.X, a.Y, b.X, b.Y, 2, 0, 1);
            double heading = Math.Atan2(b.Y - a.Y, b.X - a.X), half = sweep / 2;
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(336), -Math.Cos(heading - half));
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(344), -Math.Sin(heading - half));
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(394), Math.Cos(heading + half));
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(402), Math.Sin(heading + half));
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(790), sweep);
            return body;
        }

        void AddProfiledBeam(Guid owner, Guid id, byte[] body, byte[] contact, byte[] membership)
        {
            Guid section = Guid.NewGuid(), shape = Guid.NewGuid();
            Add(9, owner, WithGeometryRelations(body, GeometryRelation(0x11, memoKey, id), membership));
            Add(10, id, WithGeometryRelations(memoClass.ToByteArray(), HostRelation(memoKey, owner),
                GeometryRelation(0x11, sectionKey, section), contact));
            Add(13, section, WithGeometryRelations(sectionClass.ToByteArray(), HostRelation(sectionKey, id),
                GeometryRelation(0x11, shapeKey, shape)));
            PlaProfilePoint[] ring = [new(0, -1), new(1, -1), new(1, 0.5), new(0, 0.5), new(0, -1)];
            byte[] hatch = SavedProfileHatch(1, 1, 0, ring);
            var profile = new byte[528 + hatch.Length];
            shapeClass.TryWriteBytes(profile);
            BinaryPrimitives.WriteUInt16LittleEndian(profile.AsSpan(47), 1);
            BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(49), 12);
            byte[] header = [2, 0, 9, 1];
            header.CopyTo(profile, 53);
            header.CopyTo(profile, 392);
            BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(396), profile.Length - 400);
            BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(400), 128);
            BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(469), 1);
            hatch.CopyTo(profile, 528);
            Add(14, shape, WithGeometryRelations(profile, HostRelation(shapeKey, section)));
        }

        void AddJoint(Guid id)
        {
            var body = new byte[67];
            junctionClass.TryWriteBytes(body);
            new byte[] { 28, 0, 0, 0, 1, 2, 22, 0, 0, 0, 2, 0, 3, 0, 0 }.CopyTo(body, 35);
            double travel = tangency ? 6 * Math.Sin(1e-6) : 0;
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(50), -travel * Math.Cos(1e-6));
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(58), 3 - travel * Math.Sin(1e-6) +
                (scenario == "stale node" ? 1e-7 : 0));
            body[66] = 1;
            Add(12, id, WithGeometryRelations(body, HostRelation(ownRole, memo), HostRelation(peerRole, peerMemo)));
        }

        void Add(ushort kind, Guid id, byte[] data)
        {
            int index = entries.Count;
            Synthetic.Block(PlaBlockMode.Raw, data, ver: 1, guid: id, extra: 1, kind: kind).CopyTo(pool, index * extent);
            entries.Add(new ObjectDirectoryRecord(kind, (ulong)index * units, units, id));
        }
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void PreservesSlantedBeamBoundsWhenAProfiledWallMissesBothCaps(bool curvedCut, bool stalePlan, bool retainsBounds)
    {
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid materialClass = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5"), layerClass = new("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d");
        Guid sectionClass = new("c4191955-87c6-4fb6-b074-5f094fe9f6a6"), memoClass = new("cbab7bc6-e03a-4133-9161-c28ed3368672");
        Guid wallProfileClass = new("462b6598-57f0-41a5-ae81-739c8f487e04"), profileRole = new("e63e7632-0efd-412d-8077-d3f3906b638a");
        Guid junctionClass = new("9eb0036e-2b99-4db1-b043-051fb1817f47"), wallBeam = new("b7a142a1-2e5f-4b17-9b52-40604fbc3828");
        Guid wall = Guid.NewGuid(), profile = Guid.NewGuid(), junction = Guid.NewGuid();
        byte[] pool = new byte[7 * 1024];
        var entries = new List<ObjectDirectoryRecord>();
        AddBeamCutFixture(pool, entries, wall, junction, 0, 0, 2, 0.25, 0, [], wallTarget: true, endElevation: 5);

        var wallBody = WallWithPlanes(1, -4);
        var settings = wallBody.AsSpan(209);
        BinaryPrimitives.WriteDoubleLittleEndian(settings, -2);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[8..], -0.5);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[16..], 2);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[24..], -0.5);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[161..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(settings[225..], 3);
        WriteRectangularPlan(settings[256..], -2, -0.5, 2, stalePlan ? 0.125 : 0, slab: false);
        Add(5, 1, wall, WithGeometryRelations(wallBody, GeometryRelation(0x11, wallBeam, junction),
            GeometryRelation(0x11, profileRole, profile)));

        PlaProfilePoint[] ring = [new(0, 2.5), new(0.5, 2.5), new(0.5, 3.6), new(0, 3.6), new(0, 2.5)];
        byte[] hatch = SavedProfileHatch(1, 1, curvedCut ? 4 : 0, ring);
        byte[] saved = new byte[530 + hatch.Length];
        wallProfileClass.TryWriteBytes(saved);
        BinaryPrimitives.WriteUInt16LittleEndian(saved.AsSpan(49), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(saved.AsSpan(51), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(saved.AsSpan(55), 2);
        new byte[] { 2, 0, 9, 1 }.CopyTo(saved, 394);
        BinaryPrimitives.WriteInt32LittleEndian(saved.AsSpan(398), 128 + hatch.Length);
        BinaryPrimitives.WriteInt32LittleEndian(saved.AsSpan(402), 128);
        BinaryPrimitives.WriteInt32LittleEndian(saved.AsSpan(471), 1);
        hatch.CopyTo(saved, 530);
        Add(6, 2, profile, WithGeometryRelations(saved, HostRelation(profileRole, wall)));

        using var document = Open(ClassTable((1, WallClass), (2, wallProfileClass), (3, sectionClass), (4, storyClass),
                (7, materialClass), (8, layerClass), (9, BeamClass), (10, memoClass), (11, junctionClass)),
            OwnedDirectory(false, Guid.Empty, 7 * 64, entries.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 4),
            JunctionAttribute(materialClass, 7, 1, 1100), JunctionAttribute(materialClass, 7, 2, 1000),
            JunctionAttribute(layerClass, 8, 1, 1), Pool(pool));
        var beam = Assert.Single(document.ReadElements(), e => e.Type == "Beam");
        Assert.Equal(retainsBounds ? new PlaBoundingBox(-2, -0.25, -3.25, 2, 0.25, 1) : null, beam.BoundingBox);
        Assert.Equal(retainsBounds ? -3.25 : null, beam.BottomElevationToProjectZero);
        Assert.Equal(retainsBounds ? 1.0 : null, beam.TopElevationToProjectZero);
        Assert.Null(beam.BeamNetVolume);

        void Add(int index, ushort kind, Guid id, byte[] data)
        {
            Synthetic.Block(PlaBlockMode.Raw, data, ver: 1, guid: id, extra: 1, kind: kind).CopyTo(pool, index * 1024);
            entries.Add(new ObjectDirectoryRecord(kind, (ulong)index * 64, 64, id));
        }
    }

    [Theory]
    [InlineData(0.25, true)]
    [InlineData(2.25, false)]
    public void VerifiesTheInclinedBeamButtPlaneAgainstTheFarJunction(double farBeamWidth, bool retainsBounds)
    {
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid materialClass = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5"), layerClass = new("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d");
        Guid sectionClass = new("c4191955-87c6-4fb6-b074-5f094fe9f6a6"), memoClass = new("cbab7bc6-e03a-4133-9161-c28ed3368672");
        Guid junctionClass = new("92ee3161-7e81-4cb2-a403-b814edb2ae37");
        Guid firstEnd = new("59655252-b20b-47a7-9d4a-27a2f2bf2c73"), secondEnd = new("1dae4518-b353-42bc-bcb1-d037bcc603d6");
        Guid through = new("89d11bdb-3a78-48ad-96e7-5b69836c591d");
        Guid nearJoint = Guid.NewGuid(), duplicate = Guid.NewGuid(), farJoint = Guid.NewGuid();
        var pool = new byte[18 * 1024];
        var entries = new List<ObjectDirectoryRecord>();
        Guid slanted = AddBeamCutFixture(pool, entries, Guid.Empty, Guid.Empty, 0, 0, 2, 0.25, 0,
            GeometryRelationList(1, secondEnd, nearJoint, duplicate), endX: 0, endElevation: 3.5);
        Guid beamId = entries[0].ObjectId;
        Guid flat = AddBeamCutFixture(pool, entries, Guid.Empty, Guid.Empty, 5 * 1024, 0, 3.625, 0.25, 0,
            GeometryRelationList(1, firstEnd, nearJoint, duplicate, farJoint), startX: 0);
        Guid far = AddBeamCutFixture(pool, entries, Guid.Empty, Guid.Empty, 10 * 1024, 2, 3.625, farBeamWidth, 0,
            GeometryRelation(0x11, through, farJoint), transverse: true);
        AddJoint(15, nearJoint, 0, firstEnd, flat, secondEnd, slanted);
        AddJoint(16, duplicate, 0, firstEnd, flat, secondEnd, slanted);
        AddJoint(17, farJoint, 2, firstEnd, flat, through, far);

        using var document = Open(ClassTable((3, sectionClass), (4, storyClass), (7, materialClass), (8, layerClass),
                (9, BeamClass), (10, memoClass), (12, junctionClass)),
            OwnedDirectory(false, Guid.Empty, 18 * 64, entries.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 4),
            JunctionAttribute(materialClass, 7, 2, 1000), JunctionAttribute(layerClass, 8, 1, 1), Pool(pool));
        var beam = Assert.Single(document.ReadElements(), e => e.Guid == beamId);
        Assert.Equal(retainsBounds ? new PlaBoundingBox(-2, -0.25, -3.25, 0, 0.25, -0.5) : null, beam.BoundingBox);
        Assert.Null(beam.BottomElevationToProjectZero);
        Assert.Null(beam.TopElevationToProjectZero);
        Assert.Null(beam.BeamNetVolume);

        void AddJoint(int slot, Guid id, double x, Guid firstRole, Guid first, Guid secondRole, Guid second)
        {
            byte[] node = new byte[67];
            junctionClass.TryWriteBytes(node);
            new byte[] { 28, 0, 0, 0, 1, 2, 22, 0, 0, 0, 2, 0, 3, 0, 0 }.CopyTo(node, 35);
            BinaryPrimitives.WriteDoubleLittleEndian(node.AsSpan(50), x);
            node[66] = 1;
            var payload = WithGeometryRelations(node, HostRelation(firstRole, first), HostRelation(secondRole, second));
            Synthetic.Block(PlaBlockMode.Raw, payload, ver: 1, guid: id, extra: 1, kind: 12).CopyTo(pool, slot * 1024);
            entries.Add(new ObjectDirectoryRecord(12, (ulong)slot * 64, 64, id));
        }
    }

    [Theory]
    [InlineData(false, 800, true, true)]
    [InlineData(true, 800, true, true)]
    [InlineData(true, 1000, true, false)]
    [InlineData(false, 1200, true, false)]
    [InlineData(false, 800, false, false)]
    [InlineData(true, 1000, true, true, 1)]
    [InlineData(true, 1000, true, false, 2)]
    [InlineData(false, 1200, true, true, 1)]
    [InlineData(false, 1200, true, true, 3)]
    [InlineData(false, 1200, true, true, 4)]
    [InlineData(false, 1000, true, true, 4)]
    [InlineData(true, 1000, true, true, 5)]
    [InlineData(true, 1000, true, false, 6)]
    [InlineData(false, 1200, true, true, 7)]
    [InlineData(false, 1200, true, false, 8)]
    [InlineData(false, 1200, true, true, 9)]
    [InlineData(false, 1200, true, false, 10)]
    [InlineData(false, 800, true, true, 11)]
    [InlineData(false, 1200, true, true, 12)]
    [InlineData(false, 1200, true, false, 13)]
    [InlineData(true, 1200, true, true, 14)]
    [InlineData(true, 1200, false, false, 14)]
    [InlineData(true, 1200, true, false, 15)]
    [InlineData(true, 1200, true, true, 16)]
    [InlineData(true, 1200, true, false, 17)]
    [InlineData(true, 1200, true, false, 18)]
    [InlineData(true, 1200, true, false, 19)]
    [InlineData(true, 1200, true, false, 20)]
    public void ReadsWallPlanesWhoseJunctionCannotCutThem(bool attachedWall, int neighborPriority, bool reciprocal, bool supported,
        int stripCase = 0)
    {
        Guid materialClass = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5");
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid layerClass = new("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d");
        Guid junctionClass = new(attachedWall ? "68122649-d152-4046-8c2d-8880dfd880bf" : "324477ff-7a26-4d53-a38e-8f39f678c2b8");
        Guid wallJunction = new(attachedWall ? "0e7a0206-f853-4c86-9f9e-43266b0ab58a" : "88f6f041-4ce9-43cd-b0c1-056bf842bf5a");
        Guid neighborJunction = new(attachedWall ? "df61b698-e5ca-45bf-99fd-0e574a5e54d5" : "ae1816f7-dc76-4857-b0e9-fbb3378a3800");
        Guid contourClass = new("5f7c9d0d-a84b-4240-a67d-f1458abafd0e");
        Guid compositeClass = new("c7ecb629-3cf3-4c9c-a222-11082672e04b");
        Guid contourRelation = new("e6e5e667-9577-41ca-bf4a-da75b917513f");
        Guid wallId = Guid.NewGuid(), neighborId = Guid.NewGuid(), junctionId = Guid.NewGuid();
        Guid contourId = Guid.NewGuid(), neighborContour = Guid.NewGuid(), pointId = Guid.NewGuid();
        Guid pointClass = new("3367fbd4-7f06-4cef-bd01-09c83d399855");
        Guid pointRelation = new("90b557ed-5803-411b-af47-ef97d9d3e31e");
        Guid annotationClass = new("5793eecb-32e2-4fdd-b216-739ff6c806c4");
        Guid annotationRelation = new("386bc4fb-ea34-4287-8067-4f16b966b7af");
        Guid annotation = Guid.NewGuid();
        bool inlinePlan = stripCase is 14 or 15;
        bool collinear = stripCase is 5 or 6;
        bool butt = stripCase is 16 or 17;
        bool overlap = stripCase is 18 or 19 or 20;
        bool needsContour = stripCase == 3 || collinear;
        byte[] pool = new byte[(collinear || butt ? 6 : 4) * 1024];
        var wall = WithGeometryRelations(WallWithPlanes(1, -3.75), GeometryRelation(0x11, wallJunction, junctionId),
            needsContour ? GeometryRelation(0x11, contourRelation, contourId) : [],
            inlinePlan ? GeometryRelationList(1, annotationRelation, annotation) : [],
            inlinePlan ? GeometryRelation(0x11, new("7306b00e-ff8d-4051-a8b3-f76cfc597704"), Guid.NewGuid()) : []);
        var neighbor = WithGeometryRelations(attachedWall ? WallWithPlanes(2, -3.75) : RectangularSlab(stripCase != 12, stripCase),
            GeometryRelation(0x11, neighborJunction, reciprocal ? junctionId : Guid.NewGuid()),
            collinear ? GeometryRelation(0x11, contourRelation, neighborContour) : []);
        if (stripCase != 0)
        {
            WriteWallStrip(wall.AsSpan(209), alongX: true, 0.125);
            if (attachedWall)
                WriteWallStrip(neighbor.AsSpan(209), alongX: collinear || butt || overlap, stripCase == 2 ? 0.75 : 0.125);
        }
        if (needsContour)
        {
            wall.AsSpan(209 + 515, 48).Clear();
            WriteContour(-0.5, 0.5, wallId, contourId, 3072);
        }
        if (inlinePlan)
        {
            WriteInlinePlan(wall.AsSpan(209), alongX: true);
            WriteInlinePlan(neighbor.AsSpan(209), alongX: false);
            var body = WithGeometryRelations(annotationClass.ToByteArray(),
                HostRelation(annotationRelation, stripCase == 14 ? wallId : neighborId));
            Synthetic.Block(PlaBlockMode.Raw, body, ver: 1, guid: annotation, extra: 1, kind: 12).CopyTo(pool, 3072);
        }
        if (collinear)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(neighbor.AsSpan(209), -0.25);
            BinaryPrimitives.WriteDoubleLittleEndian(neighbor.AsSpan(225), 0.75);
            neighbor.AsSpan(209 + 515, 48).Clear();
            WriteContour(-0.25, 0.75, neighborId, neighborContour, 4096);
        }
        if (butt)
        {
            const double offset = 1.5e-7;
            var settings = neighbor.AsSpan(209);
            BinaryPrimitives.WriteDoubleLittleEndian(settings, 0.5);
            BinaryPrimitives.WriteDoubleLittleEndian(settings[16..], 1.5);
            BinaryPrimitives.WriteDoubleLittleEndian(settings[8..], offset);
            BinaryPrimitives.WriteDoubleLittleEndian(settings[24..], offset);
            BinaryPrimitives.WriteDoubleLittleEndian(settings[515..], 0.5);
            BinaryPrimitives.WriteDoubleLittleEndian(settings[531..], 1.5);
            BinaryPrimitives.WriteDoubleLittleEndian(settings[523..], -0.125 + offset);
            BinaryPrimitives.WriteDoubleLittleEndian(settings[539..], 0.125 + offset);
        }
        if (overlap)
        {
            var settings = neighbor.AsSpan(209);
            double peerBottom = stripCase == 20 ? 2.25 : -0.75;
            BinaryPrimitives.WriteDoubleLittleEndian(settings[153..], 2);
            BinaryPrimitives.WriteDoubleLittleEndian(settings[161..], peerBottom);
            BinaryPrimitives.WriteDoubleLittleEndian(settings[547..], peerBottom - 4);
            BinaryPrimitives.WriteDoubleLittleEndian(settings[555..], peerBottom - 2);
            if (stripCase != 19) new byte[] { 1, 0, 1, 0, 0, 0 }.CopyTo(settings[489..]);
        }
        if (collinear || butt)
        {
            var point = new byte[62];
            pointClass.TryWriteBytes(point);
            BinaryPrimitives.WriteDoubleLittleEndian(point.AsSpan(46), stripCase switch { 5 => -0.25, 16 => 0.5, 17 => 0.25, _ => -0.5 });
            Synthetic.Block(PlaBlockMode.Raw, point, ver: 1, guid: pointId, extra: 1, kind: 11).CopyTo(pool, 5120);
        }

        void WriteContour(double from, double to, Guid owner, Guid id, int offset)
        {
            byte[] contour = new byte[44 + 161];
            contourClass.TryWriteBytes(contour);
            WriteRectangularPlan(contour.AsSpan(44), from, -0.125, to, 0.125, slab: false);
            contour = WithGeometryRelations(contour, HostRelation(contourRelation, owner));
            Synthetic.Block(PlaBlockMode.Raw, contour, ver: 1, guid: id, extra: 1, kind: 9).CopyTo(pool, offset);
        }

        static void WriteInlinePlan(Span<byte> settings, bool alongX)
        {
            new byte[] { 1, 0, 1, 0, 0, 0 }.CopyTo(settings[489..]);
            settings[515..563].Clear();
            BinaryPrimitives.WriteDoubleLittleEndian(settings[243..], 0.125);
            var polygon = settings[256..];
            WriteRectangularPlan(polygon, alongX ? -0.5 : -0.125, alongX ? -0.125 : -0.5,
                alongX ? 0.5 : 0.125, alongX ? 0.125 : 0.5, slab: false);
            if (!alongX)
            {
                var first = polygon.Slice(71, 16).ToArray();
                polygon.Slice(87, 64).CopyTo(polygon[71..]);
                first.CopyTo(polygon[119..]);
                polygon.Slice(71, 16).CopyTo(polygon[135..]);
            }
        }

        bool compositeSlab = stripCase is 4 or (>= 9 and <= 13);
        if (compositeSlab)
        {
            var settings = neighbor.AsSpan(191);
            BinaryPrimitives.WriteDoubleLittleEndian(settings, stripCase == 9 ? 1 : 3.25);
            if (stripCase == 10) BinaryPrimitives.WriteDoubleLittleEndian(settings[17..], 0.875);
            BinaryPrimitives.WriteUInt16LittleEndian(settings[83..], 2);
            BinaryPrimitives.WriteUInt16LittleEndian(settings[87..], 1);
        }
        byte[] contact = new byte[62];
        junctionClass.TryWriteBytes(contact);
        contact = WithGeometryRelations(contact, HostRelation(wallJunction, wallId), HostRelation(neighborJunction, neighborId),
            collinear || butt ? HostRelation(pointRelation, pointId) : []);
        Synthetic.Block(PlaBlockMode.Raw, wall, ver: 1, guid: wallId, extra: 1, kind: 1).CopyTo(pool, 0);
        Synthetic.Block(PlaBlockMode.Raw, neighbor, ver: 1, guid: neighborId, extra: 1, kind: attachedWall ? (ushort)1 : (ushort)2).CopyTo(pool, 1024);
        Synthetic.Block(PlaBlockMode.Raw, contact, ver: 1, guid: junctionId, extra: 1, kind: 3).CopyTo(pool, 2048);
        var records = new List<ObjectDirectoryRecord>
        {
            new(1, 0, 64, wallId), new(attachedWall ? (ushort)1 : (ushort)2, 64, 64, neighborId), new(3, 128, 64, junctionId),
        };
        if (needsContour) records.Add(new(9, 192, 64, contourId));
        if (collinear) records.Add(new(9, 256, 64, neighborContour));
        if (collinear || butt) records.Add(new(11, 320, 64, pointId));
        if (inlinePlan) records.Add(new(12, 192, 64, annotation));
        using var doc = Open(ClassTable((1, WallClass), (2, SlabClass), (3, junctionClass), (4, storyClass),
                (7, materialClass), (8, layerClass), (9, contourClass), (10, compositeClass), (11, pointClass), (12, annotationClass)),
            OwnedDirectory(false, Guid.Empty, (ulong)pool.Length / 16, records.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 4),
            JunctionAttribute(materialClass, 7, 1, 1000),
            JunctionAttribute(materialClass, 7, 2, (uint)neighborPriority),
            JunctionAttribute(materialClass, 7, 3, 800),
            JunctionAttribute(layerClass, 8, 1, 1),
            CompositeForWallTest(compositeClass, splitCuts: stripCase == 13),
            Pool(pool));

        var element = Assert.Single(doc.ReadElements(), x => x.Guid == wallId);
        double bottom = stripCase switch { 9 => 0.75, 1 or 3 or 7 when !attachedWall => 0.5, _ => 0.25 };
        double top = stripCase == 4 ? 2.5 : 3.25;
        bool readsBottom = supported || stripCase == 20;
        bool readsTop = supported || stripCase == 18;
        Assert.Equal(readsBottom ? bottom : null, element.BottomElevationToHomeStory);
        Assert.Equal(readsTop ? top : null, element.TopElevationToHomeStory);
        Assert.Equal(readsBottom ? -4 + bottom : null, element.BottomElevationToProjectZero);
        Assert.Equal(readsTop ? -4 + top : null, element.TopElevationToProjectZero);
        if (attachedWall && stripCase is 5 or 6)
            Assert.Equal(supported ? new PlaBoundingBox(-0.5, -0.125, -3.75, 0.5, 0.125, -0.75) : null,
                element.BoundingBox);
    }

    private static byte[] CompositeForWallTest(Guid compositeClass, bool splitCuts, ushort kind = 10,
        byte[]? skinFunctions = null, (double Outer, double Core)? thickness = null)
    {
        byte[] attribute = new byte[61 + 45 + 3 * 23];
        compositeClass.TryWriteBytes(attribute);
        new byte[] { 0x83, 0, 3, 0, 0, 0, 1, 0 }.CopyTo(attribute, 47);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(55), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(59), 'A');
        var body = attribute.AsSpan(61);
        BinaryPrimitives.WriteUInt16LittleEndian(body, 1);
        BinaryPrimitives.WriteInt32LittleEndian(body[2..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(body[43..], 3);
        ushort outerMaterial = splitCuts ? (ushort)2 : (ushort)3;
        ushort innerMaterial = splitCuts ? (ushort)3 : (ushort)2;
        BinaryPrimitives.WriteUInt16LittleEndian(body[53..], outerMaterial);
        BinaryPrimitives.WriteDoubleLittleEndian(body[60..], thickness?.Outer ?? 0.25);
        BinaryPrimitives.WriteUInt16LittleEndian(body[76..], innerMaterial);
        BinaryPrimitives.WriteDoubleLittleEndian(body[83..], thickness?.Core ?? 0.5);
        BinaryPrimitives.WriteUInt16LittleEndian(body[99..], outerMaterial);
        BinaryPrimitives.WriteDoubleLittleEndian(body[106..], thickness?.Outer ?? 0.25);
        if (skinFunctions is not null)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(body[35..], 1);
            for (int i = 0; i < skinFunctions.Length; i++)
            {
                var skin = body[(45 + i * 23)..];
                new byte[] { 5, 1, 17, 0, 0, 0, 1, 0 }.CopyTo(skin);
                skin[14] = skinFunctions[i];
            }
        }
        return Synthetic.Block(PlaBlockMode.Raw, attribute, ver: 1, kind: kind);
    }

    [Theory]
    [InlineData(0, 2.5)]
    [InlineData(1, null)]
    [InlineData(2, null)]
    [InlineData(3, null)]
    [InlineData(4, null)]
    [InlineData(5, 2.125)]
    [InlineData(6, null)]
    [InlineData(7, 2.5)]
    [InlineData(8, 2.125)]
    [InlineData(9, null)]
    [InlineData(10, 2.25)]
    [InlineData(11, 2.125)]
    [InlineData(12, null)]
    [InlineData(13, null)]
    [InlineData(14, 3.25, 1)]
    [InlineData(15, 2.5)]
    [InlineData(16, null)]
    [InlineData(17, null)]
    [InlineData(18, 2.5)]
    [InlineData(19, 2.5)]
    [InlineData(20, null)]
    [InlineData(21, 2.5, 0.5)]
    [InlineData(22, null)]
    [InlineData(23, null)]
    [InlineData(24, 2.5)]
    [InlineData(25, 2.5)]
    [InlineData(26, null)]
    [InlineData(27, 2.5)]
    [InlineData(28, null)]
    [InlineData(29, null)]
    [InlineData(30, 2.5)]
    [InlineData(31, null)]
    [InlineData(32, null)]
    [InlineData(33, 2.5)]
    [InlineData(34, null)]
    [InlineData(35, null)]
    [InlineData(36, 2.5)]
    public void CutsJoinedWallOnlyWhereCompositeSlabKeepsCoverage(int variation, double? top, double bottom = 0.25)
    {
        Guid story = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid material = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5");
        Guid layer = new("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d");
        Guid composite = new("c7ecb629-3cf3-4c9c-a222-11082672e04b");
        Guid wallSlabClass = new("324477ff-7a26-4d53-a38e-8f39f678c2b8");
        Guid wallJoinClass = new("68122649-d152-4046-8c2d-8880dfd880bf");
        Guid pointClass = new("3367fbd4-7f06-4cef-bd01-09c83d399855");
        Guid beamSlabClass = new("7259c45f-97e3-434a-bc9e-fe3bcf980947");
        Guid beamPairClass = new("92ee3161-7e81-4cb2-a403-b814edb2ae37");
        Guid firstBeamEnd = new("59655252-b20b-47a7-9d4a-27a2f2bf2c73");
        Guid secondBeamEnd = new("1dae4518-b353-42bc-bcb1-d037bcc603d6");
        Guid wallSlab = new("88f6f041-4ce9-43cd-b0c1-056bf842bf5a");
        Guid slabWall = new("ae1816f7-dc76-4857-b0e9-fbb3378a3800");
        Guid wallEnd = new("0e7a0206-f853-4c86-9f9e-43266b0ab58a");
        Guid peerEnd = new("df61b698-e5ca-45bf-99fd-0e574a5e54d5");
        if (variation == 7) peerEnd = wallEnd;
        Guid junctionPoint = new("90b557ed-5803-411b-af47-ef97d9d3e31e");
        Guid slabBeam = new("d50bcd56-a71c-40f8-a3f1-551d14a4762b");
        Guid beamSlab = new("4df5ee09-8368-4f3b-ad64-056115b4c0ab");
        Guid wallBeam = new("b7a142a1-2e5f-4b17-9b52-40604fbc3828");
        Guid operationClass = new("47a28b23-f655-49fa-b219-1511ca52c49a");
        Guid solidRole = new("68236820-e176-4e8c-be39-d5d56a7a71c0");
        Guid operationId = Guid.NewGuid();
        Guid levelDimensionClass = new("690878fa-b11f-46c9-953c-3864d85bdee6");
        Guid slabDimension = new("9176a8c6-f502-440f-8ef3-f0a700178e4a");
        Guid wall = Guid.NewGuid(), peer = Guid.NewGuid(), slab = Guid.NewGuid(), point = Guid.NewGuid();
        Guid targetCut = Guid.NewGuid(), peerCut = Guid.NewGuid(), wallJoin = Guid.NewGuid(), beamJoin = Guid.NewGuid();
        Guid beamCut = Guid.NewGuid(), secondBeamCut = Guid.NewGuid(), secondBeamJoin = Guid.NewGuid(), pair = Guid.NewGuid();
        Guid foundation = Guid.NewGuid(), foundationCut = Guid.NewGuid();
        Guid additionalCut = Guid.NewGuid();
        bool repeatedSlab = variation is >= 33 and <= 35;
        bool sharedBeams = variation is >= 21 and <= 23;
        bool twoBeams = variation is >= 8 and <= 14 || sharedBeams, corner = variation is 11 or 12 or 13, cutsBottom = variation == 14;
        bool remoteBeamJoin = variation is >= 15 and <= 20 or 24;
        bool beamCutsWall = variation is 5 or 6 || twoBeams;
        Guid dimension = Guid.NewGuid();
        byte[] pool = new byte[(sharedBeams ? 25 : twoBeams || remoteBeamJoin ? 23 : 18) * 1024];
        var entries = new List<ObjectDirectoryRecord>();
        var target = WallWithPlanes(1, -3.75);
        WriteWallStrip(target.AsSpan(209), alongX: true, 0.125);
        bool peerOperation = variation is >= 30 and <= 32;
        var neighbor = WallWithPlanes(variation == 4 || peerOperation ? 4 : 1, peerOperation ? -0.5 : -3.75);
        var peerSettings = neighbor.AsSpan(209);
        bool oblique = variation is >= 27 and <= 32;
        if (peerOperation) BinaryPrimitives.WriteDoubleLittleEndian(peerSettings[161..], 3.5);
        if (variation is 25 or 26 or 36)
        {
            WriteWallStrip(peerSettings, alongX: true, 0.25);
            double start = variation == 26 ? 0.25 : 0.5;
            BinaryPrimitives.WriteDoubleLittleEndian(peerSettings, start);
            BinaryPrimitives.WriteDoubleLittleEndian(peerSettings[16..], 1.5);
            BinaryPrimitives.WriteDoubleLittleEndian(peerSettings[515..], start);
            BinaryPrimitives.WriteDoubleLittleEndian(peerSettings[531..], 1.5);
            if (variation == 36)
            {
                const double transverseOffset = 1.5e-7;
                BinaryPrimitives.WriteDoubleLittleEndian(peerSettings[8..], transverseOffset);
                BinaryPrimitives.WriteDoubleLittleEndian(peerSettings[24..], transverseOffset);
                BinaryPrimitives.WriteDoubleLittleEndian(peerSettings[523..], -0.25 + transverseOffset);
                BinaryPrimitives.WriteDoubleLittleEndian(peerSettings[539..], 0.25 + transverseOffset);
            }
        }
        else if (oblique)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(peerSettings, 0.5);
            BinaryPrimitives.WriteDoubleLittleEndian(peerSettings[16..], 0.8);
            BinaryPrimitives.WriteDoubleLittleEndian(peerSettings[24..], 0.4);
            BinaryPrimitives.WriteDoubleLittleEndian(peerSettings[77..], 0.25);
            BinaryPrimitives.WriteDoubleLittleEndian(peerSettings[85..], 0.25);
            BinaryPrimitives.WriteDoubleLittleEndian(peerSettings[194..], Math.PI / 2);
            BinaryPrimitives.WriteDoubleLittleEndian(peerSettings[243..], 0.125);
            var polygon = peerSettings[256..];
            WriteRectangularPlan(polygon, 0.4, -0.075, 0.9, 0.475, slab: false);
            (double X, double Y)[] vertices = [(0.6, -0.075), (0.9, 0.325), (0.7, 0.475), (0.4, 0.075), (0.6, -0.075)];
            for (int i = 0; i < vertices.Length; i++)
            {
                BinaryPrimitives.WriteDoubleLittleEndian(polygon[(71 + 16 * i)..], vertices[i].X);
                BinaryPrimitives.WriteDoubleLittleEndian(polygon[(79 + 16 * i)..], vertices[i].Y);
            }
        }
        else
        {
            WriteWallStrip(peerSettings, alongX: false, 0.125);
            BinaryPrimitives.WriteDoubleLittleEndian(peerSettings, 0.5);
            BinaryPrimitives.WriteDoubleLittleEndian(peerSettings[16..], variation == 7 ? 0.5 + 1e-12 : 0.5);
            BinaryPrimitives.WriteDoubleLittleEndian(peerSettings[515..], 0.375);
            BinaryPrimitives.WriteDoubleLittleEndian(peerSettings[531..], 0.625);
        }
        var floor = sharedBeams ? OpeningSlab(0, -0.75, 1) : RectangularSlab(true);
        if (sharedBeams) Array.Resize(ref floor, 191 + 270 + 247 + 16 + 4 * 27);
        WriteLocation(floor.AsSpan(163), 1, 0);
        var settings = floor.AsSpan(191);
        BinaryPrimitives.WriteDoubleLittleEndian(settings, cutsBottom ? -3 : -0.75);
        BinaryPrimitives.WriteUInt16LittleEndian(settings[83..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(settings[87..], 1);
        double slabEnd = remoteBeamJoin ? 3 : variation == 1 ? 0.5 : oblique && variation != 28 ? 1 : 0.625;
        WriteRectangularPlan(settings[270..], -1, -1, slabEnd, 1, slab: true);
        var xy = new byte[62];
        pointClass.TryWriteBytes(xy);
        BinaryPrimitives.WriteDoubleLittleEndian(xy.AsSpan(46), variation == 2 ? 0.75 : 0.5);
        if (variation == 29) BinaryPrimitives.WriteDoubleLittleEndian(xy.AsSpan(54), 0.01);
        Add(0, 1, wall, WithGeometryRelations(target,
            twoBeams && variation != 22 ? GeometryRelationList(1, wallBeam, beamCut, secondBeamCut) :
                beamCutsWall ? GeometryRelation(0x11, wallBeam, beamCut) : [],
            sharedBeams ? GeometryRelationList(1, wallSlab, targetCut, foundationCut) :
                repeatedSlab ? GeometryRelationList(1, wallSlab, targetCut, additionalCut) : GeometryRelation(0x11, wallSlab, targetCut),
            GeometryRelation(0x11, wallEnd, wallJoin)));
        Add(1, 1, peer, WithGeometryRelations(neighbor, GeometryRelation(0x11, peerEnd, wallJoin),
            GeometryRelation(0x11, wallSlab, peerCut),
            peerOperation ? GeometryRelationList(1, solidRole, slab, operationId) : []));
        Add(2, 2, slab, WithGeometryRelations(floor, repeatedSlab && variation != 34 ?
                GeometryRelationList(1, slabWall, targetCut, peerCut, additionalCut) : GeometryRelationList(1, slabWall, targetCut, peerCut),
            twoBeams ? GeometryRelationList(1, slabBeam, beamJoin, secondBeamJoin) : GeometryRelation(0x11, slabBeam, beamJoin),
            GeometryRelation(0x11, slabDimension, dimension),
            peerOperation && variation != 32 ? GeometryRelationList(2, solidRole, peer, operationId) : []));
        Add(3, 5, targetCut, WithGeometryRelations(wallSlabClass.ToByteArray(), HostRelation(wallSlab, wall), HostRelation(slabWall, slab)));
        Add(4, 5, peerCut, WithGeometryRelations(wallSlabClass.ToByteArray(), HostRelation(wallSlab, peer), HostRelation(slabWall, slab)));
        Add(5, 6, wallJoin, WithGeometryRelations(wallJoinClass.ToByteArray(),
            variation == 7 ? GeometryRelationList(2, wallEnd, wall, peer) : HostRelation(wallEnd, wall),
            variation == 7 ? [] : HostRelation(peerEnd, peer), HostRelation(junctionPoint, point)));
        Add(6, 12, point, xy);
        if (repeatedSlab)
            Add(7, 5, additionalCut, WithGeometryRelations(wallSlabClass.ToByteArray(),
                HostRelation(wallSlab, variation == 35 ? peer : wall), HostRelation(slabWall, slab)));
        if (peerOperation)
        {
            var memo = new byte[84];
            operationClass.TryWriteBytes(memo);
            BinaryPrimitives.WriteUInt32LittleEndian(memo.AsSpan(52), variation == 31 ? 5u : 1u);
            BinaryPrimitives.WriteUInt32LittleEndian(memo.AsSpan(56), 1);
            Add(7, 17, operationId, WithGeometryRelations(memo, GeometryRelationList(4, solidRole, peer, slab)));
        }
        if (sharedBeams)
        {
            var lower = OpeningSlab(0, -3.5, 1);
            Add(23, 2, foundation, WithGeometryRelations(lower, GeometryRelation(0x11, slabWall, foundationCut)));
            Add(24, 5, foundationCut, WithGeometryRelations(wallSlabClass.ToByteArray(),
                HostRelation(wallSlab, wall), HostRelation(slabWall, foundation)));
        }
        double offset = variation == 9 ? 0.1 : 0.0625;
        double cornerY = variation == 13 ? -0.0625 : -0.125;
        double beamY = sharedBeams ? -0.25 : corner ? cornerY :
            twoBeams ? -offset : variation == 3 || beamCutsWall || remoteBeamJoin ? 0 : 0.75;
        double beamTop = cutsBottom ? 1 : 3.125;
        double halfWidth = sharedBeams ? variation == 23 ? 0.26 : 0.1 :
            corner ? 0.25 : twoBeams ? 0.075 : variation == 6 ? 0.1 : 0.125;
        double startX = remoteBeamJoin ? variation == 17 ? 0.75 : 1.5 : -2;
        Guid beam = AddBeamCutFixture(pool, entries, beamCutsWall ? wall : Guid.Empty, beamCut, 8 * 1024, beamY, beamTop,
            halfWidth, 0, Synthetic.Concat(GeometryRelation(0x11, beamSlab, beamJoin),
                corner || remoteBeamJoin ? GeometryRelation(0x11, firstBeamEnd, pair) : []),
            wallTarget: beamCutsWall, transverse: sharedBeams, endX: corner ? 0 : null, startX: startX);
        Add(13, 13, beamJoin, WithGeometryRelations(beamSlabClass.ToByteArray(), HostRelation(slabBeam, slab), HostRelation(beamSlab, beam)));
        Add(14, 14, dimension, WithGeometryRelations(levelDimensionClass.ToByteArray(), HostRelation(slabDimension, slab)));
        if (twoBeams)
        {
            Guid second = AddBeamCutFixture(pool, entries, wall, secondBeamCut, 15 * 1024, sharedBeams ? 0.25 : corner ? 0 : offset,
                variation == 10 ? 3.25 : beamTop, sharedBeams ? halfWidth : corner ? 0.75 : 0.075, 0,
                Synthetic.Concat(GeometryRelation(0x11, beamSlab, secondBeamJoin),
                    corner ? GeometryRelation(0x11, secondBeamEnd, pair) : []),
                wallTarget: true, transverse: corner || sharedBeams, material: variation == 12 ? 4u : 2u, startY: corner ? cornerY : -2);
            Add(20, 13, secondBeamJoin, WithGeometryRelations(beamSlabClass.ToByteArray(), HostRelation(slabBeam, slab),
                HostRelation(beamSlab, second)));
            if (corner) AddPair(second, 0, cornerY);
        }
        if (remoteBeamJoin)
        {
            double peerWidth = variation >= 18 ? halfWidth : 0.25;
            double? peerX = variation switch { 18 or 24 => startX + 1, 19 => startX - 0.5, 20 => startX + 0.5, _ => null };
            double peerTop = variation == 24 ? beamTop + 2e-8 : beamTop;
            Guid second = AddBeamCutFixture(pool, entries, Guid.Empty, Guid.Empty, 15 * 1024, peerX is null ? startX : beamY, peerTop,
                peerWidth, 0, variation == 16 ? [] : GeometryRelation(0x11, secondBeamEnd, pair),
                transverse: peerX is null, startY: beamY, startX: startX, endX: peerX,
                endY: variation is 19 or 20 ? beamY : 2);
            AddPair(second, startX, beamY);
        }
        using var doc = Open(ClassTable((1, WallClass), (2, SlabClass), (3, new Guid("c4191955-87c6-4fb6-b074-5f094fe9f6a6")),
                (4, story), (5, wallSlabClass), (6, wallJoinClass), (7, material), (8, layer),
                (9, new Guid("a5c2d285-5a7c-4a68-9554-9eda9d4556ca")), (10, new Guid("cbab7bc6-e03a-4133-9161-c28ed3368672")),
                (11, new Guid("9eb0036e-2b99-4db1-b043-051fb1817f47")), (12, pointClass), (13, beamSlabClass),
                (14, levelDimensionClass), (15, composite), (16, beamPairClass), (17, operationClass)),
            OwnedDirectory(false, Guid.Empty, (ulong)pool.Length / 16, entries.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(story), ver: 1, kind: 4),
            JunctionAttribute(material, 7, 1, 1000), JunctionAttribute(material, 7, 2, 1200),
            JunctionAttribute(material, 7, 3, 800), JunctionAttribute(material, 7, 4, 1300),
            JunctionAttribute(layer, 8, 1, 1), CompositeForWallTest(composite, splitCuts: false, kind: 15),
            Pool(pool));
        var result = Assert.Single(doc.ReadElements(), x => x.Guid == wall);
        Assert.Equal(top.HasValue ? bottom : null, result.BottomElevationToHomeStory);
        Assert.Equal(top, result.TopElevationToHomeStory);
        Assert.Equal(top.HasValue ? bottom - 4 : null, result.BottomElevationToProjectZero);
        Assert.Equal(top - 4, result.TopElevationToProjectZero);

        void AddPair(Guid second, double x, double y)
        {
            var junction = new byte[67];
            beamPairClass.TryWriteBytes(junction);
            byte[] frame = [28, 0, 0, 0, 1, 2, 22, 0, 0, 0, 2, 0, 3, 0, 0];
            frame.CopyTo(junction, 35);
            BinaryPrimitives.WriteDoubleLittleEndian(junction.AsSpan(50), x);
            BinaryPrimitives.WriteDoubleLittleEndian(junction.AsSpan(58), y);
            junction[66] = 1;
            Add(21, 16, pair, WithGeometryRelations(junction, HostRelation(firstBeamEnd, beam), HostRelation(secondBeamEnd, second)));
        }

        void Add(int slot, ushort kind, Guid id, byte[] body)
        {
            Synthetic.Block(PlaBlockMode.Raw, body, ver: 1, guid: id, extra: 1, kind: kind).CopyTo(pool, slot * 1024);
            entries.Add(new(kind, (ulong)slot * 64, 64, id));
        }
    }

    [Theory]
    [InlineData(false, false, 2.75)]
    [InlineData(true, false, 2.75)]
    [InlineData(false, true, null)]
    [InlineData(false, false, null, false)]
    [InlineData(false, false, 2.75, true, 1)]
    [InlineData(false, false, null, true, 2)]
    [InlineData(false, false, null, true, 3)]
    [InlineData(false, false, 2.75, true, 4)]
    [InlineData(false, false, 2.75, true, 5)]
    [InlineData(false, false, null, true, 6)]
    [InlineData(false, false, null, true, 7)]
    [InlineData(true, false, null, true, 2)]
    [InlineData(true, false, null, true, 3)]
    [InlineData(false, false, 2.75, true, 0, 1)]
    [InlineData(false, false, 2.75, true, 0, 2)]
    [InlineData(true, false, 2.75, true, 0, 0, 1)]
    [InlineData(true, false, null, true, 0, 0, 2)]
    [InlineData(true, false, null, true, 0, 0, 3)]
    [InlineData(true, false, 2.75, true, 0, 0, 4)]
    public void CompositeSlabsCutThroughAllWallSkinsAtEachEnd(bool cutBothEnds, bool beveled, double? top,
        bool labelMatches = true, int openingCase = 0, int labelStorage = 0, int columnCase = 0)
    {
        Guid story = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid material = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5");
        Guid layer = new("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d");
        Guid composite = new("c7ecb629-3cf3-4c9c-a222-11082672e04b");
        Guid junctionClass = new("324477ff-7a26-4d53-a38e-8f39f678c2b8");
        Guid wallSlab = new("88f6f041-4ce9-43cd-b0c1-056bf842bf5a");
        Guid slabWall = new("ae1816f7-dc76-4857-b0e9-fbb3378a3800");
        Guid labelClass = new("1601b50c-a61f-447c-8b74-0f578c003d88");
        Guid labelRole = new("5dd68b40-d2b2-4e13-9238-8343cdb6c423");
        Guid openingClass = new("6746aa79-7498-4bea-bad8-191cdad31d05");
        Guid cutClass = new("9a756127-8689-486d-8873-5c1d7b0f8785");
        Guid hostRole = new("49f5b35f-3e0e-4644-a659-d024c2c93228");
        Guid openingRole = new("219fcd20-e2ef-4e61-bb1b-a771897ff878");
        Guid targetRole = new("fc9598c9-7004-45ef-93e0-8fdcbc306acf");
        Guid columnClass = new("13e8eb85-5a33-47a3-a4d4-1e05296aec7e");
        Guid columnMemoClass = new("7a1a82ab-69cd-40e6-a7d8-76564f981cf9");
        Guid columnSectionClass = new("d6c12939-d905-40e0-aa57-f7c8d687dc7b");
        Guid slabColumnClass = new("d5381783-6a19-4de8-9e52-2e04a3978db0");
        Guid columnMemoRole = new("27448621-9adc-43e6-b5aa-fc76e4cd62ce");
        Guid sectionRole = new("63b86c30-a221-421c-84fb-926c694bf611");
        Guid slabColumnRole = new("f6ee0d9c-27ad-44d0-824f-682969bb171e");
        Guid columnSlabRole = new("87811dc4-1686-45f4-b1a6-dfaa81da44e4");
        Guid wall = Guid.NewGuid(), upper = Guid.NewGuid(), lower = Guid.NewGuid();
        Guid upperJoint = Guid.NewGuid(), lowerJoint = Guid.NewGuid(), label = Guid.NewGuid();
        Guid opening = Guid.NewGuid(), cut = Guid.NewGuid();
        Guid slabColumnJoint = Guid.NewGuid();
        var target = WallWithPlanes(columnCase == 4 ? 2 : 1, -3.75);
        var settings = target.AsSpan(209);
        WriteWallStrip(settings, alongX: true, 0.5);
        if (columnCase != 4) BinaryPrimitives.WriteUInt16LittleEndian(settings[225..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(settings[229..], 1);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[243..], 0.5);
        WriteRectangularPlan(settings[256..], -0.5, -0.5, 0.5, 0.5, slab: false);
        const int slotSize = 2048;
        var pool = new byte[(columnCase == 0 ? 8 : 12) * slotSize];
        var entries = new List<ObjectDirectoryRecord>();
        Add(1, wall, WithGeometryRelations(target, cutBothEnds ? GeometryRelationList(1, wallSlab, lowerJoint, upperJoint) :
            GeometryRelation(0x11, wallSlab, upperJoint)));
        AddSlab(upper, upperJoint, -0.5, beveled ? 0 : -1);
        if (cutBothEnds) AddSlab(lower, lowerJoint, columnCase == 0 ? -3 : -3.25, -1);
        var labelBody = WithGeometryRelations(labelClass.ToByteArray(), HostRelation(labelRole, labelMatches ? upper : lower));
        if (labelStorage == 0) Add(10, label, labelBody);
        if (labelStorage == 2) entries.Add(new(10, (ulong)entries.Count * (slotSize / 16), slotSize / 16, label));
        if (openingCase != 0)
        {
            var openingSettings = OpeningSettings(openingCase >= 5 ? 2u : 1u, 0.2, 0.2);
            double offset = openingCase switch { 1 or 3 or 5 => 0.5, 7 => 0.3502, _ => 0 };
            BinaryPrimitives.WriteDoubleLittleEndian(openingSettings.AsSpan(134), offset);
            BinaryPrimitives.WriteDoubleLittleEndian(openingSettings.AsSpan(158), 1);
            BinaryPrimitives.WriteDoubleLittleEndian(openingSettings.AsSpan(190), 1);
            BinaryPrimitives.WriteDoubleLittleEndian(openingSettings.AsSpan(222), 1);
            if (openingCase == 4)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(openingSettings.AsSpan(278), 2);
                BinaryPrimitives.WriteDoubleLittleEndian(openingSettings.AsSpan(282), -0.125);
            }
            Add(11, opening, WithGeometryRelations(Synthetic.Concat(FramedElementId(openingClass, 185, "OP"), openingSettings),
                HostRelation(hostRole, upper), GeometryRelation(0x11, openingRole, openingCase == 3 ? Guid.NewGuid() : cut)));
            Add(12, cut, WithGeometryRelations(cutClass.ToByteArray(), HostRelation(openingRole, opening), HostRelation(targetRole, upper)));
        }
        if (columnCase != 0)
        {
            Guid column = Guid.NewGuid(), memo = Guid.NewGuid(), section = Guid.NewGuid();
            var body = Synthetic.Concat(FramedElementId(columnClass, 735, "C"), new byte[95]);
            ElementId("C").CopyTo(body.AsSpan(721));
            WriteLocation(body.AsSpan(701), 1, -1);
            byte[] frame = [1, 0, 2, 0, 0, 0, 1, 0];
            frame.CopyTo(body, 304);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(368), 1);
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(328), -2);
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(352), -1);
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(386), columnCase == 2 ? 0.5 : 0.25);
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(410), 1);
            Add(13, column, WithGeometryRelations(body, GeometryRelation(0x11, columnMemoRole, memo)));
            Add(14, memo, WithGeometryRelations(columnMemoClass.ToByteArray(), HostRelation(columnMemoRole, column),
                GeometryRelation(0x11, sectionRole, section), GeometryRelation(0x11, columnSlabRole, slabColumnJoint)));
            var circle = CircularColumnSection(columnSectionClass, 0.25, valid: true);
            BinaryPrimitives.WriteUInt32LittleEndian(circle.AsSpan(149 + 482 + 18), 2);
            Add(15, section, WithGeometryRelations(circle, HostRelation(sectionRole, memo)));
            Add(16, slabColumnJoint, WithGeometryRelations(slabColumnClass.ToByteArray(), HostRelation(columnSlabRole, memo),
                HostRelation(slabColumnRole, columnCase == 3 ? upper : lower)));
        }

        using var doc = Open(ClassTable((1, WallClass), (2, SlabClass), (3, junctionClass), (4, story),
                (7, material), (8, layer), (9, composite), (10, labelClass), (11, openingClass), (12, cutClass),
                (13, columnClass), (14, columnMemoClass), (15, columnSectionClass), (16, slabColumnClass)),
            OwnedDirectory(false, Guid.Empty, (ulong)pool.Length / 16, entries.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(story), ver: 1, kind: 4),
            JunctionAttribute(material, 7, 2, 1200), JunctionAttribute(material, 7, 3, 800),
            JunctionAttribute(layer, 8, 1, 1), CompositeForWallTest(composite, splitCuts: false, kind: 9), Pool(pool),
            labelStorage == 1 ? Synthetic.Block(PlaBlockMode.ZeroMask, Synthetic.ZeroMask(labelBody), ver: 1, guid: label,
                unpacked: (uint)labelBody.Length, kind: 10) : []);
        var result = Assert.Single(doc.ReadElements(), e => e.Guid == wall);
        double? bottom = cutBothEnds ? top.HasValue || openingCase == 2 ? 0.75 : null : beveled ? null : 0.25;
        Assert.Equal(bottom, result.BottomElevationToHomeStory);
        Assert.Equal(top, result.TopElevationToHomeStory);
        Assert.Equal(bottom - 4, result.BottomElevationToProjectZero);
        Assert.Equal(top - 4, result.TopElevationToProjectZero);

        void AddSlab(Guid slab, Guid intersection, double topPlane, int beveledEdge)
        {
            var body = OpeningSlab(0, topPlane, 1, beveledEdge >= 0);
            var floor = body.AsSpan(191);
            floor[8] = columnCase == 4 ? (byte)0 : (byte)3;
            bool foundation = slab == lower && columnCase != 0;
            BinaryPrimitives.WriteUInt16LittleEndian(floor[83..], foundation ? (ushort)1 : (ushort)2);
            BinaryPrimitives.WriteUInt16LittleEndian(floor[87..], 1);
            if (foundation) BinaryPrimitives.WriteInt32LittleEndian(floor[73..], 2);
            bool hosts = slab == upper && openingCase != 0;
            Add(2, slab, WithGeometryRelations(body, GeometryRelation(0x11, slabWall, intersection),
                slab == upper ? GeometryRelation(0x11, labelRole, label) : [],
                hosts ? GeometryRelation(0x11, hostRole, opening) : [], hosts ? GeometryRelation(0x11, targetRole, cut) : [],
                foundation ? GeometryRelation(0x11, slabColumnRole, slabColumnJoint) : []));
            Add(3, intersection, WithGeometryRelations(junctionClass.ToByteArray(), HostRelation(wallSlab, wall), HostRelation(slabWall, slab)));
        }

        void Add(ushort kind, Guid id, byte[] body)
        {
            int slot = entries.Count;
            Synthetic.Block(PlaBlockMode.Raw, body, ver: 1, guid: id, extra: 1, kind: kind).CopyTo(pool, slot * slotSize);
            entries.Add(new(kind, (ulong)slot * (slotSize / 16), slotSize / 16, id));
        }
    }

    [Theory]
    [InlineData(0, 0, true, 1.0)]
    [InlineData(3, 0, true, 1.0)]
    [InlineData(1, 0, true, 0.25)]
    [InlineData(3, 0.125, true, 0.25)]
    [InlineData(3, 0, false, null)]
    [InlineData(5, 0, true, null)]
    [InlineData(3, 0, true, 0.25, true)]
    [InlineData(0, 0, true, 0.25, false, true)]
    [InlineData(3, -0.2, true, 0.25, true, false, 1)]
    [InlineData(3, -0.2, true, null, true, false, 2)]
    [InlineData(3, -0.2, true, null, true, false, 3)]
    [InlineData(0, 0.125, true, 0.25, false, false, 0, true)]
    [InlineData(3, 0.125, true, 0.25, false, false, 0, false, 1)]
    [InlineData(3, 0, true, null, false, false, 0, false, 1)]
    [InlineData(3, 0.000001, true, null, false, false, 0, false, 1)]
    [InlineData(3, 0.125, true, null, false, false, 0, false, 2)]
    [InlineData(0, -1, true, 1.0, false, false, 0, false, 3)]
    [InlineData(0, 0, true, null, false, false, 0, false, 3)]
    [InlineData(0, -1, true, 1.0, false, false, 0, false, 4)]
    [InlineData(0, -1, true, null, false, false, 0, false, 5)]
    public void ResolvesWallCapFromTheUnionOfSlabCuts(uint operation, double gap, bool reciprocal, double? bottom,
        bool upper = false, bool withHole = false, int nestedCase = 0, bool roundoff = false, int wallContactCase = 0)
    {
        Guid story = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid material = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5");
        Guid layer = new("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d");
        Guid junction = new("324477ff-7a26-4d53-a38e-8f39f678c2b8");
        Guid operationClass = new("47a28b23-f655-49fa-b219-1511ca52c49a");
        Guid wallRole = new("88f6f041-4ce9-43cd-b0c1-056bf842bf5a");
        Guid slabRole = new("ae1816f7-dc76-4857-b0e9-fbb3378a3800");
        Guid solidRole = new("68236820-e176-4e8c-be39-d5d56a7a71c0");
        Guid wall = Guid.NewGuid(), left = Guid.NewGuid(), right = Guid.NewGuid();
        Guid leftLink = Guid.NewGuid(), rightLink = Guid.NewGuid();
        Guid peer = Guid.NewGuid(), peerLink = Guid.NewGuid();
        bool peerCutsLeft = wallContactCase is 3 or 5;
        bool bothCaps = wallContactCase is 4 or 5;
        var target = WallWithPlanes(1, -3.75);
        WriteWallStrip(target.AsSpan(209), alongX: true, 0.125);
        const int slotSize = 2048;
        var pool = new byte[(nestedCase == 3 ? 8 : nestedCase != 0 || wallContactCase != 0 ? 7 : 5) * slotSize];
        var entries = new List<ObjectDirectoryRecord>();
        if (nestedCase != 0)
        {
            Guid nestedLink = Guid.NewGuid(), reverseLink = Guid.NewGuid();
            Add(1, wall, WithGeometryRelations(target, GeometryRelationList(1, solidRole, left, leftLink, right, rightLink)));
            Add(2, left, WithGeometryRelations(Slab(-1, 0, fullDepth: true), GeometryRelationList(1, solidRole, right, nestedLink),
                nestedCase == 3 ? GeometryRelationList(2, solidRole, wall, leftLink, right, reverseLink) :
                GeometryRelationList(2, solidRole, wall, leftLink)));
            Add(2, right, WithGeometryRelations(Slab(gap, 1, fullDepth: false), GeometryRelationList(2, solidRole, wall, rightLink, left, nestedLink),
                nestedCase == 3 ? GeometryRelationList(1, solidRole, left, reverseLink) : []));
            AddSubtraction(leftLink, wall, left, operation, 1);
            AddSubtraction(rightLink, wall, right, operation, 1);
            AddSubtraction(nestedLink, left, right, operation, nestedCase == 2 ? -1 : 1);
            if (nestedCase == 3) AddSubtraction(reverseLink, right, left, operation, 1);
        }
        else
        {
            Add(1, wall, operation == 0 ? WithGeometryRelations(target, GeometryRelationList(1, wallRole, leftLink, rightLink)) :
                WithGeometryRelations(target, GeometryRelation(0x11, wallRole, leftLink), GeometryRelationList(1, solidRole, right, rightLink)));
            Add(2, left, WithGeometryRelations(Slab(-1, bothCaps ? 1 : 0, fullDepth: true), peerCutsLeft ?
                GeometryRelationList(1, slabRole, leftLink, peerLink) : GeometryRelation(0x11, slabRole, leftLink)));
            Add(3, leftLink, WithGeometryRelations(junction.ToByteArray(), HostRelation(wallRole, wall), HostRelation(slabRole, left)));
            if (operation == 0)
            {
                Add(2, right, WithGeometryRelations(Slab(gap, 1, fullDepth: true, topOperand: bothCaps), wallContactCase == 4 ?
                    GeometryRelationList(1, slabRole, rightLink, peerLink) : GeometryRelation(0x11, slabRole, rightLink)));
                Add(3, rightLink, WithGeometryRelations(junction.ToByteArray(), HostRelation(wallRole, wall), HostRelation(slabRole, right)));
            }
            else
            {
                Add(2, right, WithGeometryRelations(Slab(gap, 1, fullDepth: false),
                    GeometryRelationList(2, solidRole, reciprocal ? wall : Guid.NewGuid(), rightLink),
                    wallContactCase != 0 ? GeometryRelation(0x11, slabRole, peerLink) : []));
                AddSubtraction(rightLink, wall, right, operation, operation == 3 ? upper ? 1 : -1 : 0);
            }
        }
        if (wallContactCase != 0)
        {
            var neighbor = WallWithPlanes(4, -3.75);
            WriteWallStrip(neighbor.AsSpan(209), alongX: true, 0.125);
            Add(1, peer, WithGeometryRelations(neighbor, GeometryRelation(0x11, wallRole,
                wallContactCase == 2 ? Guid.NewGuid() : peerLink)));
            Add(3, peerLink, WithGeometryRelations(junction.ToByteArray(), HostRelation(wallRole, peer),
                HostRelation(slabRole, peerCutsLeft ? left : right)));
        }

        using var doc = Open(ClassTable((1, WallClass), (2, SlabClass), (3, junction), (4, story),
                (5, operationClass), (7, material), (8, layer)),
            OwnedDirectory(false, Guid.Empty, (ulong)pool.Length / 16, entries.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(story), ver: 1, kind: 4),
            JunctionAttribute(material, 7, 1, 1000), JunctionAttribute(material, 7, 2, 1200),
            JunctionAttribute(material, 7, 3, 800), JunctionAttribute(material, 7, 4, 1400),
            JunctionAttribute(layer, 8, 1, 1), Pool(pool));
        var result = Assert.Single(doc.ReadElements(), e => e.Guid == wall);
        Assert.Equal(bottom, result.BottomElevationToHomeStory);
        Assert.Equal(bottom - 4, result.BottomElevationToProjectZero);
        double? top = wallContactCase switch
        {
            4 => null,
            5 => 2.5,
            1 or 3 => 3.25,
            _ => bottom.HasValue ? upper ? 2.5 : 3.25 : null,
        };
        Assert.Equal(top, result.TopElevationToHomeStory);
        Assert.Equal(top - 4, result.TopElevationToProjectZero);

        byte[] Slab(double minX, double maxX, bool fullDepth, bool topOperand = false)
        {
            var body = RectangularSlab(true);
            Array.Resize(ref body, 191 + 270 + 247 + 16 + 4 * 27);
            WriteLocation(body.AsSpan(163), 1, 0);
            var floor = body.AsSpan(191);
            double reference = topOperand ? -0.5 : upper ? fullDepth ? -0.5 : -1.25 : -3;
            if (roundoff) reference = Math.BitIncrement(-2.75);
            BinaryPrimitives.WriteDoubleLittleEndian(floor, reference);
            BinaryPrimitives.WriteDoubleLittleEndian(floor[17..], fullDepth ? 1 : 0.25);
            BinaryPrimitives.WriteInt32LittleEndian(floor[73..], fullDepth ? 2 : 3);
            BinaryPrimitives.WriteUInt32LittleEndian(floor[165..], 20);
            WriteRectangularPlan(floor[270..], minX, -1, maxX, 1, slab: true);
            WriteSlabEdges(floor[(270 + 247)..], 4, -1);
            if (withHole && minX == gap)
            {
                var rings = SlabWithStraightRings(-1, [(minX, -1), (maxX, -1), (maxX, 1), (minX, 1), (minX, -1)],
                    [(0.2, -0.0625), (0.2, 0.0625), (0.4, 0.0625), (0.4, -0.0625), (0.2, -0.0625)]);
                Array.Resize(ref body, rings.Length + 104);
                var polygon = body.AsSpan(191 + 270);
                rings.AsSpan(191 + 166).CopyTo(polygon);
                BinaryPrimitives.WriteDoubleLittleEndian(polygon[11..], minX);
                BinaryPrimitives.WriteDoubleLittleEndian(polygon[19..], -1);
            }
            return body;
        }

        void AddSubtraction(Guid id, Guid owner, Guid cutter, uint type, double direction)
        {
            var memo = new byte[84];
            operationClass.TryWriteBytes(memo);
            BinaryPrimitives.WriteUInt32LittleEndian(memo.AsSpan(52), type);
            BinaryPrimitives.WriteUInt32LittleEndian(memo.AsSpan(56), 1);
            BinaryPrimitives.WriteDoubleLittleEndian(memo.AsSpan(76), direction);
            Add(5, id, WithGeometryRelations(memo, GeometryRelationList(4, solidRole, owner, cutter)));
        }

        void Add(ushort kind, Guid id, byte[] body)
        {
            int slot = entries.Count;
            Synthetic.Block(PlaBlockMode.Raw, body, ver: 1, guid: id, extra: 1, kind: kind).CopyTo(pool, slot * slotSize);
            entries.Add(new(kind, (ulong)slot * (slotSize / 16), slotSize / 16, id));
        }
    }

    private static void WriteWallStrip(Span<byte> settings, bool alongX, double halfWidth)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(settings[(alongX ? 0 : 8)..], -0.5);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[(alongX ? 16 : 24)..], 0.5);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[77..], 2 * halfWidth);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[85..], 2 * halfWidth);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[194..], Math.PI / 2);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[515..], alongX ? -0.5 : -halfWidth);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[523..], alongX ? -halfWidth : -0.5);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[531..], alongX ? 0.5 : halfWidth);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[539..], alongX ? halfWidth : 0.5);
    }

    [Theory]
    [InlineData(false, false, true, true, true)]
    [InlineData(true, false, true, true, true)]
    [InlineData(false, true, true, true, false)]
    [InlineData(true, true, true, true, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, true, true, true, 1)]
    [InlineData(false, false, true, true, true, 2)]
    [InlineData(false, false, true, true, false, 3)]
    [InlineData(false, false, true, true, true, 4)]
    [InlineData(false, false, true, true, true, 5)]
    [InlineData(true, false, true, true, true, 5)]
    [InlineData(false, false, true, true, false, 6)]
    [InlineData(false, false, true, true, false, 7)]
    [InlineData(false, false, true, true, true, 8)]
    [InlineData(false, false, true, true, true, 9)]
    [InlineData(true, false, true, true, true, 9)]
    [InlineData(false, false, true, true, false, 10)]
    [InlineData(false, false, true, true, true, 11)]
    [InlineData(false, false, true, true, false, 12)]
    [InlineData(false, false, true, true, true, 0, 1)]
    [InlineData(true, false, true, true, true, 0, 2)]
    [InlineData(false, false, true, true, false, 0, 3)]
    [InlineData(false, false, true, true, false, 0, 4)]
    [InlineData(false, false, true, true, false, 0, 5)]
    [InlineData(false, false, true, true, true, 0, 6)]
    [InlineData(true, false, true, true, true, 0, 6)]
    [InlineData(false, false, true, true, false, 0, 7)]
    [InlineData(false, false, true, true, false, 0, 8)]
    [InlineData(false, false, true, true, false, 13, 6)]
    public void PreservesWallCapsOutsideVerifiedRectangularDoorCuts(bool reverse, bool bothHalves,
        bool reciprocal, bool knownRevision, bool readsPlanes, int slabCase = 0, int libraryCase = 0)
    {
        Guid doorClass = new("d8f07689-9cfa-4fbe-aeb4-0a60b8e667ee");
        Guid libraryClass = new("2bfca33d-bd38-49e3-bf6d-52ac8b850b9f");
        Guid libraryRole = new("672f1c46-64f3-41f5-8a4a-ffdfd86bcbfd");
        Guid tableClass = new("11db227d-dbe4-45af-88b6-6fddea993f71");
        Guid tableRole = new("76ebf0a4-c2ef-483f-a1a8-28d3adde99a0");
        Guid hostRole = new("1f6bfe02-d60c-40ae-9ac8-c3953ce12814");
        Guid story = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid material = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5");
        Guid layer = new("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d");
        Guid junctionClass = new("324477ff-7a26-4d53-a38e-8f39f678c2b8");
        Guid wallSlab = new("88f6f041-4ce9-43cd-b0c1-056bf842bf5a");
        Guid slabWall = new("ae1816f7-dc76-4857-b0e9-fbb3378a3800");
        Guid composite = new("c7ecb629-3cf3-4c9c-a222-11082672e04b");
        bool pocket = libraryCase >= 6;
        var (family, revision) = libraryCase switch
        {
            0 => (new Guid("ffd54fba-1ad6-4cf7-87cc-918accd4e480"), new Guid("6ebb67a2-5c74-4640-ab79-86e54703485a")),
            2 => (new Guid("e155d0a0-7435-41af-8668-b4bae047f2b2"), new Guid("460fe002-f8e8-4d2c-859e-e99587473cef")),
            >= 6 => (new Guid("e4b25630-169a-4820-8bd9-962ed253b6c1"), new Guid("318e68fb-15e0-4e0b-92ed-bbf67a14b2eb")),
            _ => (new Guid("13d8efa7-3fee-4993-b7a5-d826a6830657"), new Guid("a4ce2f76-b4d0-4b26-a920-4a84fbe513dc")),
        };
        if (!knownRevision) revision = Guid.NewGuid();
        Guid wall = Guid.NewGuid(), door = Guid.NewGuid(), other = Guid.NewGuid();
        Guid joint = Guid.NewGuid(), upperJoint = Guid.NewGuid();
        bool twoSlabs = slabCase is 6 or 8 or 9 or 10 or 13;
        var target = WallWithPlanes(1, -3.75);
        var settings = target.AsSpan(209);
        WriteWallStrip(settings, alongX: true, pocket ? 0.05 : 0.125);
        double halfLength = pocket ? 0.75 : 0.5;
        BinaryPrimitives.WriteDoubleLittleEndian(settings, reverse ? halfLength : -halfLength);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[16..], reverse ? -halfLength : halfLength);
        if (pocket)
        {
            // The aperture bounds span the wall length. The cap needs a strip outside the pocket depth.
            BinaryPrimitives.WriteDoubleLittleEndian(settings[515..], -halfLength);
            BinaryPrimitives.WriteDoubleLittleEndian(settings[531..], halfLength);
            BinaryPrimitives.WriteUInt16LittleEndian(settings[225..], 2);
            BinaryPrimitives.WriteUInt16LittleEndian(settings[229..], 1);
        }
        const int slotSize = 1024;
        const int tableSlotSize = 4096;
        var pool = new byte[7 * slotSize];
        var entries = new List<ObjectDirectoryRecord>();
        var tables = new List<ObjectDirectoryRecord>();
        var tableBlocks = new List<byte[]>();
        Add(1, wall, WithGeometryRelations(target, bothHalves ? GeometryRelationList(1, hostRole, door, other) :
            GeometryRelation(0x11, hostRole, door), twoSlabs ? GeometryRelationList(1, wallSlab, joint, upperJoint) :
            slabCase != 0 ? GeometryRelation(0x11, wallSlab, joint) : []));
        AddDoor(door, pocket ? halfLength : 0.25);
        if (bothHalves) AddDoor(other, 0.75);
        if (slabCase == 13)
        {
            // At the bottom the slabs leave only the path occupied by the passage and pocket envelopes.
            AddSlab(joint, -1, [(-1, -1), (1, -1), (1, -0.02), (-1, -0.02), (-1, -1)], 0.5);
            AddSlab(upperJoint, -1, [(-1, 0.02), (1, 0.02), (1, 1), (-1, 1), (-1, 0.02)], 0.5);
        }
        else if (slabCase is 11 or 12)
        {
            AddSlab(joint, slabCase == 12 ? 4 : -1, [(-0.75, -1), (0.75, -1), (0.75, 1), (-0.75, 1), (-0.75, -1)],
                3.25, [(0.125, -0.0625), (0.125, 0.0625), (0.375, 0.0625), (0.375, -0.0625), (0.125, -0.0625)]);
        }
        else if (slabCase >= 5)
        {
            double edge = slabCase == 7 ? 0.125 - 1e-6 : -0.03125;
            AddSlab(joint, slabCase == 10 ? 0 : -1, [(-0.75, -1), (0.75, -1), (0.75, edge), (-0.75, edge), (-0.75, -1)]);
            if (twoSlabs)
            {
                double lower = slabCase == 8 ? 0.0625 : -0.0625;
                AddSlab(upperJoint, -1, [(-0.75, lower), (0.75, lower), (0.75, 1), (-0.75, 1), (-0.75, lower)],
                    slabCase is 9 or 10 ? 1 : 3.25);
            }
        }
        else if (slabCase != 0)
        {
            double left = slabCase == 4 ? 5e-8 : -0.125;
            double farEdge = slabCase switch { 2 => 0, 4 => 0.75, _ => -0.1 };
            double nearEdge = slabCase is 2 or 4 ? 1.5 : 0.6;
            AddSlab(joint, slabCase == 3 ? 0 : -1,
                [(left, -1), (nearEdge, -1), (farEdge, 1), (left, 1), (left, -1)]);
        }

        void AddSlab(Guid contact, int beveledEdge, (double X, double Y)[] ring, double top = 3.25,
            params (double X, double Y)[][] holes)
        {
            Guid slab = Guid.NewGuid();
            var floor = SlabWithStraightRings(beveledEdge, [ring, .. holes]);
            const int smallOutline = 191 + 166, largeOutline = 191 + 270;
            Array.Resize(ref floor, floor.Length + largeOutline - smallOutline);
            floor.AsSpan(smallOutline, floor.Length - largeOutline).CopyTo(floor.AsSpan(largeOutline));
            BinaryPrimitives.WriteDoubleLittleEndian(floor.AsSpan(largeOutline + 11), ring.Min(p => p.X));
            BinaryPrimitives.WriteDoubleLittleEndian(floor.AsSpan(largeOutline + 19), ring.Min(p => p.Y));
            BinaryPrimitives.WriteDoubleLittleEndian(floor.AsSpan(191), top);
            if (slabCase == 13) BinaryPrimitives.WriteInt32LittleEndian(floor.AsSpan(191 + 73), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(floor.AsSpan(191 + 165), 20);
            Add(5, slab, WithGeometryRelations(floor, GeometryRelation(0x11, slabWall, contact)));
            Add(6, contact, WithGeometryRelations(junctionClass.ToByteArray(), HostRelation(wallSlab, wall), HostRelation(slabWall, slab)));
        }

        using var doc = Open(ClassTable((1, WallClass), (2, doorClass), (3, libraryClass), (4, story),
                (5, SlabClass), (6, junctionClass), (7, material), (8, layer), (9, tableClass), (10, composite)),
            OwnedDirectory(false, Guid.Empty, (ulong)pool.Length / 16, entries.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(story), ver: 1, kind: 4),
            JunctionAttribute(material, 7, 1, 1000), JunctionAttribute(material, 7, 2, 1200),
            pocket ? JunctionAttribute(material, 7, 3, 1000) : [],
            slabCase == 13 ? JunctionAttribute(material, 7, 4, 1400) : [],
            pocket ? CompositeForWallTest(composite, splitCuts: false, thickness: libraryCase == 8 ? (0.02, 0.06) : (0.024, 0.052)) : [],
            JunctionAttribute(layer, 8, 1, 1), Pool(pool),
            tables.Count == 0 ? [] : OwnedDirectory(false, Guid.Empty, (ulong)tables.Count * (tableSlotSize / 16), tables.ToArray()),
            tables.Count == 0 ? [] : Pool(tableBlocks.ToArray()));
        var elements = doc.ReadElements();
        var result = Assert.Single(elements, e => e.Guid == wall);
        var placedDoor = Assert.Single(elements, e => e.Guid == door);
        Assert.Equal(reciprocal ? new PlaLibraryPartReference(family, revision) : (PlaLibraryPartReference?)null,
            placedDoor.LibraryPartReference);
        double? bottom = readsPlanes || slabCase is 6 or 7 ? 0.25 : null;
        double? top = readsPlanes || bothHalves || slabCase == 13 ? 3.25 : slabCase == 6 ? 2.25 : null;
        Assert.Equal(bottom, result.BottomElevationToHomeStory);
        Assert.Equal(top, result.TopElevationToHomeStory);
        Assert.Equal(bottom - 4, result.BottomElevationToProjectZero);
        Assert.Equal(top - 4, result.TopElevationToProjectZero);
        Assert.Null(result.WallExtrusion);

        void AddDoor(Guid id, double position)
        {
            Guid library = Guid.NewGuid();
            Guid table = Guid.NewGuid();
            var descriptor = new byte[89];
            libraryClass.TryWriteBytes(descriptor);
            new byte[] { 1, 0, 131, 0, 3, 0, 0, 0, 1, 0, 2, 0 }.CopyTo(descriptor, 39);
            family.TryWriteBytes(descriptor.AsSpan(51));
            revision.TryWriteBytes(descriptor.AsSpan(67));
            Add(3, library, WithGeometryRelations(descriptor, HostRelation(libraryRole, reciprocal ? id : Guid.NewGuid()),
                libraryCase == 0 ? [] : GeometryRelation(0x11, tableRole, table)));
            if (libraryCase != 0)
            {
                byte[] body = PlainDoorPassageTable(tableClass, tableRole, libraryCase == 5 ? Guid.NewGuid() : library,
                    positiveOversize: libraryCase == 3, revealType: libraryCase == 4 ? 2 : 1,
                    pocket: pocket, centeredPocket: libraryCase != 7);
                byte[] storage = new byte[tableSlotSize];
                Synthetic.Block(PlaBlockMode.Raw, body, ver: 1, guid: table, extra: 1, kind: 9).CopyTo(storage, 0);
                tables.Add(new(9, (ulong)tables.Count * (tableSlotSize / 16), tableSlotSize / 16, table));
                tableBlocks.Add(storage);
            }

            var framed = FramedElementId(doorClass, 286, "D");
            WriteLocation(framed.AsSpan(175), 1, -1);
            BinaryPrimitives.WriteUInt16LittleEndian(framed.AsSpan(185), 1);
            BinaryPrimitives.WriteDoubleLittleEndian(framed.AsSpan(202), pocket ? 0.7 : 0.5);
            BinaryPrimitives.WriteDoubleLittleEndian(framed.AsSpan(210), pocket ? 2.1 : 2);
            BinaryPrimitives.WriteUInt32LittleEndian(framed.AsSpan(268), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(framed.AsSpan(272), 83);
            BinaryPrimitives.WriteUInt16LittleEndian(framed.AsSpan(274), libraryCase == 0 ? (ushort)1 : (ushort)2);
            var placement = new byte[72];
            new byte[] { 0, 1, 0, 14, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0 }.CopyTo(placement, 0);
            BinaryPrimitives.WriteDoubleLittleEndian(placement.AsSpan(24), position);
            BinaryPrimitives.WriteDoubleLittleEndian(placement.AsSpan(48), 1);
            Add(2, id, WithGeometryRelations(Synthetic.Concat(framed, placement),
                GeometryRelation(0x11, libraryRole, library), HostRelation(hostRole, wall)));
        }

        void Add(ushort kind, Guid id, byte[] body)
        {
            int slot = entries.Count;
            Synthetic.Block(PlaBlockMode.Raw, body, ver: 1, guid: id, extra: 1, kind: kind).CopyTo(pool, slot * slotSize);
            entries.Add(new(kind, (ulong)slot * (slotSize / 16), slotSize / 16, id));
        }
    }

    private static byte[] WallWithPlanes(int material, double boxBottom)
    {
        byte[] wall = Synthetic.Concat(FramedElementId(WallClass, 197, "WL-1"), new byte[563]);
        ElementId("WL-1").CopyTo(wall, 183);
        WriteLocation(wall.AsSpan(163), 1, -1);
        var settings = wall.AsSpan(209);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[153..], 3);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[161..], 0.25);
        BinaryPrimitives.WriteInt32LittleEndian(settings[215..], material);
        BinaryPrimitives.WriteUInt16LittleEndian(settings[225..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(settings[227..], 1);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[547..], boxBottom);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[555..], boxBottom + 3);
        return wall;
    }

    [Theory]
    [InlineData(3, 1, true, true, true)]
    [InlineData(1, 0, true, true, true)]
    [InlineData(3, 1, false, true, false)]
    [InlineData(3, 1, true, false, false)]
    [InlineData(5, 1, true, true, false)]
    public void ReadsWallPlanesWhenTheWallIsASubtractionOperator(uint type, double z, bool reverseLink,
        bool matchingOperand, bool readsPlanes)
    {
        Guid story = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid assembly = new("8f2fee46-6a3f-41ac-a32b-69e44dc6276d");
        Guid member = new("47a28b23-f655-49fa-b219-1511ca52c49a");
        Guid operation = new("68236820-e176-4e8c-be39-d5d56a7a71c0");
        Guid wall = Guid.NewGuid(), target = Guid.NewGuid(), link = Guid.NewGuid();
        var body = WithGeometryRelations(WallWithPlanes(1, -3.75), GeometryRelationList(2, operation, target, link));
        var operand = WithGeometryRelations(assembly.ToByteArray(), GeometryRelationList(1, operation, wall,
            reverseLink ? link : Guid.NewGuid()));
        var memo = new byte[84];
        member.TryWriteBytes(memo);
        BinaryPrimitives.WriteUInt32LittleEndian(memo.AsSpan(52), type);
        BinaryPrimitives.WriteUInt32LittleEndian(memo.AsSpan(56), 1);
        BinaryPrimitives.WriteDoubleLittleEndian(memo.AsSpan(76), z);
        memo = WithGeometryRelations(memo, GeometryRelationList(4, operation, target, matchingOperand ? wall : Guid.NewGuid()));
        var pool = new byte[3072];
        Synthetic.Block(PlaBlockMode.Raw, body, ver: 1, guid: wall, extra: 1, kind: 1).CopyTo(pool, 0);
        Synthetic.Block(PlaBlockMode.Raw, operand, ver: 1, guid: target, extra: 1, kind: 2).CopyTo(pool, 1024);
        Synthetic.Block(PlaBlockMode.Raw, memo, ver: 1, guid: link, extra: 1, kind: 3).CopyTo(pool, 2048);
        using var doc = Open(ClassTable((1, WallClass), (2, assembly), (3, member), (4, story)),
            OwnedDirectory(false, Guid.Empty, 192, new ObjectDirectoryRecord(1, 0, 64, wall),
                new ObjectDirectoryRecord(2, 64, 64, target), new ObjectDirectoryRecord(3, 128, 64, link)),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(story), ver: 1, kind: 4), Pool(pool));

        var result = Assert.Single(doc.ReadElements(), e => e.Guid == wall);
        Assert.Equal(readsPlanes ? 0.25 : null, result.BottomElevationToHomeStory);
        Assert.Equal(readsPlanes ? 3.25 : null, result.TopElevationToHomeStory);
        Assert.Equal(readsPlanes ? -3.75 : null, result.BottomElevationToProjectZero);
        Assert.Equal(readsPlanes ? -0.75 : null, result.TopElevationToProjectZero);
    }

    [Theory]
    [InlineData(1200, true, 0.5)]
    [InlineData(800, true, 0.25)]
    [InlineData(1200, false, null)]
    [InlineData(1200, true, 0.5, 2, 1)]
    [InlineData(1200, true, null, 2, 2)]
    public void ReadsWallEndCutByRectangularBeam(uint beamPriority, bool reciprocal, double? expectedBottom,
        int beamLayer = 1, uint beamGroup = 1)
    {
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid materialClass = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5");
        Guid layerClass = new("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d");
        Guid sectionClass = new("c4191955-87c6-4fb6-b074-5f094fe9f6a6");
        Guid wallBeam = new("b7a142a1-2e5f-4b17-9b52-40604fbc3828");
        Guid wallId = Guid.NewGuid(), junctionId = Guid.NewGuid();

        byte[] pool = new byte[5 * 1024 + 1024];
        var wall = WithGeometryRelations(WallWithPlanes(1, -3.75),
            GeometryRelation(0x11, wallBeam, reciprocal ? junctionId : Guid.NewGuid()));
        WriteWallStrip(wall.AsSpan(209), alongX: true, halfWidth: 0.125);
        Synthetic.Block(PlaBlockMode.Raw, wall, ver: 1, guid: wallId, extra: 1, kind: 1).CopyTo(pool, 0);
        var entries = new List<ObjectDirectoryRecord> { new(1, 0, 64, wallId) };
        AddBeamCutFixture(pool, entries, wallId, junctionId, 1024, 0, 0.5, 1, 0, [], wallTarget: true, layer: beamLayer);
        using var doc = Open(ClassTable((1, WallClass), (3, sectionClass), (4, storyClass), (7, materialClass), (8, layerClass),
                (9, new Guid("a5c2d285-5a7c-4a68-9554-9eda9d4556ca")), (10, new Guid("cbab7bc6-e03a-4133-9161-c28ed3368672")),
                (11, new Guid("9eb0036e-2b99-4db1-b043-051fb1817f47"))),
            OwnedDirectory(false, Guid.Empty, (ulong)pool.Length / 16, entries.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 4),
            JunctionAttribute(materialClass, 7, 1, 1000),
            JunctionAttribute(materialClass, 7, 2, beamPriority),
            JunctionAttribute(layerClass, 8, 1, 1),
            JunctionAttribute(layerClass, 8, 2, beamGroup),
            Pool(pool));

        var element = Assert.Single(doc.ReadElements(), x => x.Guid == wallId);
        Assert.Equal(expectedBottom, element.BottomElevationToHomeStory);
        Assert.Equal(expectedBottom - 4, element.BottomElevationToProjectZero);
        Assert.Equal(expectedBottom.HasValue ? 3.25 : null, element.TopElevationToHomeStory);
        Assert.Equal(expectedBottom.HasValue ? -0.75 : null, element.TopElevationToProjectZero);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, false, false, 1200, true, 0.5)]
    [InlineData(false, false, false, false, 1200, false, 0)]
    [InlineData(false, false, false, false, 800, true, 0)]
    [InlineData(false, false, false, false, 1000, true, 0)]
    [InlineData(false, false, false, false, 0, false, 0.5, 1)]
    [InlineData(false, false, false, false, 0, false, 0, 2)]
    [InlineData(false, false, false, false, 0, false, 0, 3)]
    [InlineData(false, false, false, false, 0, false, 0, 4)]
    [InlineData(false, false, false, false, 0, false, 0.5, 5)]
    [InlineData(false, false, false, false, 0, false, 0, 6)]
    [InlineData(false, false, false, false, 0, false, 1.5, 7)]
    [InlineData(false, false, false, false, 0, false, 0.5, 8)]
    [InlineData(false, false, false, false, 0, false, 0, 9)]
    [InlineData(false, false, false, false, 0, false, 0.5, 10)]
    [InlineData(false, false, false, false, 0, false, 0, 11)]
    [InlineData(false, false, false, false, 0, false, 0.5, 12)]
    [InlineData(false, false, false, false, 0, false, 0.5, 13)]
    [InlineData(false, false, false, false, 0, false, 0, 14)]
    [InlineData(false, false, false, false, 0, false, 0.5, 15)]
    [InlineData(false, false, false, false, 0, false, 0, 16)]
    [InlineData(false, false, false, false, 0, false, 0, 0, Math.PI / 2)]
    public void ProjectsTheColumnEndCutsOverItsStoredRectangle(bool connected, bool curved, bool wrongOwner, bool unknownOperation,
        int slabPriority = 0, bool coversColumn = false, double expectedBottom = 0, int beamCase = 0, double planAngle = 0)
    {
        Guid columnClass = new("13e8eb85-5a33-47a3-a4d4-1e05296aec7e");
        Guid memoClass = new("7a1a82ab-69cd-40e6-a7d8-76564f981cf9");
        Guid sectionClass = new("c4191955-87c6-4fb6-b074-5f094fe9f6a6");
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        Guid memoRelation = new("27448621-9adc-43e6-b5aa-fc76e4cd62ce");
        Guid sectionRelation = new("63b86c30-a221-421c-84fb-926c694bf611");
        Guid slabJunctionClass = new("d5381783-6a19-4de8-9e52-2e04a3978db0");
        Guid materialClass = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5");
        Guid layerClass = new("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d");
        Guid columnBeam = new("6346a816-1d7c-4e46-9609-67624e99d0fb");
        Guid columnSlab = new("87811dc4-1686-45f4-b1a6-dfaa81da44e4");
        Guid slabColumn = new("f6ee0d9c-27ad-44d0-824f-682969bb171e");
        Guid contactId = Guid.NewGuid(), secondContactId = Guid.NewGuid(), slabId = Guid.NewGuid();
        Guid columnId = Guid.NewGuid(), memoId = Guid.NewGuid(), sectionId = Guid.NewGuid();
        bool multipleBeams = beamCase is >= 5 and <= 9 or >= 12 and <= 16;
        bool endJoin = beamCase >= 10;
        byte[] column = Synthetic.Concat(FramedElementId(columnClass, 735, "C-1"), new byte[95]);
        var record = column.AsSpan();
        ElementId("C-1").CopyTo(record[721..]);
        byte[] planes = [1, 0, 2, 0, 0, 0, 1, 0];
        planes.CopyTo(record[304..]);
        BinaryPrimitives.WriteDoubleLittleEndian(record[352..], -1);
        BinaryPrimitives.WriteUInt16LittleEndian(record[368..], 1);
        BinaryPrimitives.WriteDoubleLittleEndian(record[386..], 3);
        bool horizontalTop = multipleBeams || planAngle != 0;
        BinaryPrimitives.WriteDoubleLittleEndian(record[402..], horizontalTop ? 0 : 0.6);
        BinaryPrimitives.WriteDoubleLittleEndian(record[410..], horizontalTop ? 1 : 0.8);
        BinaryPrimitives.WriteDoubleLittleEndian(record[(745 + 87)..], planAngle);
        WriteLocation(record[701..], 1, -1);
        column = WithGeometryRelations(column, GeometryRelation(0x11, memoRelation, memoId),
            unknownOperation ? GeometryRelation(0x12, Guid.NewGuid(), Guid.NewGuid()) : []);

        var memo = new byte[40];
        memoClass.TryWriteBytes(memo);
        memo = WithGeometryRelations(memo,
            HostRelation(memoRelation, wrongOwner ? Guid.NewGuid() : columnId),
            GeometryRelation(0x11, sectionRelation, sectionId),
            connected || beamCase != 0 ? GeometryRelation(0x11, columnBeam, connected ? Guid.NewGuid() : contactId) : [],
            multipleBeams ? GeometryRelation(0x11, columnBeam, secondContactId) : [],
            slabPriority != 0 ? GeometryRelation(0x11, columnSlab, contactId) : []);

        double halfDepth = planAngle == 0 ? 0.25 : 0.125;
        byte[] section = WithGeometryRelations(RectangularSection(sectionClass, 1, 0.25, -halfDepth, halfDepth, curved),
            HostRelation(sectionRelation, memoId));
        bool joinedBeams = beamCase is 8 or 9 || endJoin;
        byte[] pool = new byte[(joinedBeams ? 14 : multipleBeams ? 13 : beamCase == 0 ? 5 : 8) * 1024];
        Synthetic.Block(PlaBlockMode.Raw, column, ver: 1, guid: columnId, extra: 1, kind: 1).CopyTo(pool, 0);
        Synthetic.Block(PlaBlockMode.Raw, memo, ver: 1, guid: memoId, extra: 1, kind: 2).CopyTo(pool, 1024);
        Synthetic.Block(PlaBlockMode.Raw, section, ver: 1, guid: sectionId, extra: 1, kind: 3).CopyTo(pool, 2048);
        if (slabPriority != 0)
        {
            byte[] contact = new byte[48];
            slabJunctionClass.TryWriteBytes(contact);
            contact = WithGeometryRelations(contact, HostRelation(slabColumn, slabId), HostRelation(columnSlab, memoId));
            var slab = WithGeometryRelations(RectangularSlab(coversColumn), GeometryRelation(0x11, slabColumn, contactId));
            Synthetic.Block(PlaBlockMode.Raw, slab, ver: 1, guid: slabId, extra: 1, kind: 5).CopyTo(pool, 3072);
            Synthetic.Block(PlaBlockMode.Raw, contact, ver: 1, guid: contactId, extra: 1, kind: 6).CopyTo(pool, 4096);
        }
        var entries = new List<ObjectDirectoryRecord> { new(1, 0, 64, columnId), new(2, 64, 64, memoId), new(3, 128, 64, sectionId) };
        var classes = new List<(ushort, Guid)>
        {
            (1, columnClass), (2, memoClass), (3, sectionClass), (4, storyClass),
            (5, SlabClass), (6, slabJunctionClass), (7, materialClass), (8, layerClass),
        };
        if (slabPriority != 0)
            entries.AddRange([new(5, 192, 64, slabId), new(6, 256, 64, contactId)]);
        if (beamCase != 0)
        {
            classes.AddRange([(9, new Guid("a5c2d285-5a7c-4a68-9554-9eda9d4556ca")),
                (10, new Guid("cbab7bc6-e03a-4133-9161-c28ed3368672")), (11, new Guid("f7f60005-35d0-4346-87ab-29e087ac0bbf"))]);
            double half = beamCase is 3 or 5 or 6 or 8 or 9 ? 0.125 : 1;
            double y = beamCase is 5 or 6 or 8 or 9 ? -0.125 : 0;
            Guid collision = Guid.NewGuid();
            bool branchStronger = beamCase is 13 or 14 or 16;
            Guid firstKey = new(branchStronger ? "1dae4518-b353-42bc-bcb1-d037bcc603d6" :
                endJoin ? "59655252-b20b-47a7-9d4a-27a2f2bf2c73" : "89d11bdb-3a78-48ad-96e7-5b69836c591d");
            Guid secondKey = new(branchStronger || !endJoin ? "1d381463-2edc-4aad-91b0-027291e8da94" :
                "89d11bdb-3a78-48ad-96e7-5b69836c591d");
            Guid firstMemo = AddBeamCutFixture(pool, entries, memoId, contactId, 3072, y, beamCase == 2 ? 3.25 : 0.5, half,
                beamCase == 4 ? Math.PI / 6 : 0, joinedBeams ? GeometryRelation(0x11, firstKey, collision) : [],
                endX: beamCase >= 12 ? 0 : null, material: beamCase == 15 ? 1u : 2u);
            if (multipleBeams || endJoin)
            {
                double elevation = beamCase == 7 ? 1.5 : joinedBeams ? 0.5 : 0.75;
                Guid secondMemo = AddBeamCutFixture(pool, entries, multipleBeams ? memoId : Guid.Empty, secondContactId, 8192,
                    beamCase >= 12 ? 0 : endJoin ? 2 : beamCase == 6 ? 0.1875 : -y,
                    beamCase == 16 ? 0.75 : elevation, beamCase == 14 ? 0.125 : half, 0,
                    joinedBeams ? GeometryRelation(0x11, secondKey, collision) : [], transverse: endJoin,
                    material: branchStronger ? 1u : 2u);
                if (joinedBeams)
                {
                    Guid collisionClass = new("92ee3161-7e81-4cb2-a403-b814edb2ae37");
                    var contact = new byte[67];
                    collisionClass.TryWriteBytes(contact);
                    if (endJoin)
                    {
                        byte[] pointFrame = [28, 0, 0, 0, 1, 2, 22, 0, 0, 0, 2, 0, 3, 0, 0];
                        pointFrame.CopyTo(contact, 35);
                        BinaryPrimitives.WriteDoubleLittleEndian(contact.AsSpan(50), beamCase >= 12 ? 0 : beamCase == 10 ? 2 : 1);
                        contact[66] = 1;
                    }
                    contact = WithGeometryRelations(contact, HostRelation(firstKey, firstMemo),
                        HostRelation(secondKey, beamCase == 9 ? Guid.NewGuid() : secondMemo));
                    Synthetic.Block(PlaBlockMode.Raw, contact, ver: 1, guid: collision, extra: 1, kind: 12).CopyTo(pool, 13312);
                    classes.Add((12, collisionClass));
                    entries.Add(new(12, 832, 64, collision));
                }
            }
        }
        using var doc = Open(ClassTable(classes.ToArray()),
            OwnedDirectory(false, Guid.Empty, (ulong)pool.Length / 16, entries.ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, StoryTable(storyClass), ver: 1, kind: 4),
            JunctionAttribute(materialClass, 7, 1, 1000),
            JunctionAttribute(materialClass, 7, 2, beamCase != 0 ? 1200u : (uint)slabPriority),
            JunctionAttribute(layerClass, 8, 1, 1),
            Pool(pool));

        var element = Assert.Single(doc.ReadElements(), x => x.Type == "Column");
        bool supported = !connected && !curved && !wrongOwner && !unknownOperation && beamCase is not (9 or 11 or 16);
        if (beamCase == 4) expectedBottom = 0.5 - 0.25 * Math.Tan(Math.PI / 6);
        double expectedTop = horizontalTop ? 3 : beamCase == 2 ? 2.25 : 3.1875;
        Assert.Equal(-1, element.HomeStoryIndex);
        Assert.Equal("Подвал", element.HomeStoryName);
        Assert.Equal(supported ? expectedBottom : null, element.BottomElevationToHomeStory);
        Assert.Equal(supported ? expectedTop : null, element.TopElevationToHomeStory);
        Assert.Equal(supported ? -4 + expectedBottom : null, element.BottomElevationToProjectZero);
        Assert.Equal(supported ? -4 + expectedTop : null, element.TopElevationToProjectZero);
        bool hasSolid = supported && (beamCase is 1 or 2 or 7 or 8 or 10 or 12 or 13 or 15 ||
            beamCase == 0 && (slabPriority is 0 or 800 or 1000 || coversColumn));
        if (hasSolid)
        {
            var solid = Assert.IsType<PlaColumnExtrusion>(element.ColumnExtrusion);
            var box = solid.BoundingBox;
            Assert.Equal(box, element.BoundingBox);
            Assert.Equal(planAngle == 0 ? -0.25 : -halfDepth, box.XMin, 12);
            Assert.Equal(planAngle == 0 ? 0.25 : halfDepth, box.XMax, 12);
            Assert.Equal(-0.25, box.YMin, 12);
            Assert.Equal(0.25, box.YMax, 12);
            Assert.Equal(expectedBottom - 4, box.ZMin);
            Assert.Equal(expectedTop - 4, box.ZMax);
            double upperAtCenter = horizontalTop || beamCase == 2 ? expectedTop : 3;
            Assert.Equal(upperAtCenter - 4, solid.UpperPlane.Elevation);
            Assert.Equal(0, solid.UpperPlane.SlopeX);
            Assert.Equal(horizontalTop || beamCase == 2 ? 0 : -0.75, solid.UpperPlane.SlopeY, 12);
            Assert.Equal(halfDepth * (upperAtCenter - expectedBottom), solid.Volume, 12);
        }
        else
            Assert.Null(element.ColumnExtrusion);
    }

    private static byte[] RectangularSection(Guid sectionClass, uint material, double halfWidth, double bottom, double top,
        bool curved = false, bool concave = false)
    {
        (double X, double Y)[] vertices = concave
            ? [(-halfWidth, bottom), (halfWidth, bottom), (halfWidth, top), (0, top),
                (0, (bottom + top) / 2), (-halfWidth, (bottom + top) / 2), (-halfWidth, bottom)]
            : [(-halfWidth, bottom), (halfWidth, bottom), (halfWidth, top), (-halfWidth, top), (-halfWidth, bottom)];
        int hatchLength = 59 + 16 * (vertices.Length + 1) + 6;
        byte[] section = new byte[149 + 482 + hatchLength];
        sectionClass.TryWriteBytes(section);
        BinaryPrimitives.WriteDoubleLittleEndian(section.AsSpan(91), 2 * halfWidth);
        BinaryPrimitives.WriteDoubleLittleEndian(section.AsSpan(99), top - bottom);
        new byte[] { 2, 0, 9, 1 }.CopyTo(section, 141);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(145), (uint)(482 + hatchLength));
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(149), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(149 + 69), 1);
        var hatch = section.AsSpan(149 + 482);
        hatch[0] = 3;
        hatch[1] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(hatch[2..], (uint)(hatchLength - 6));
        BinaryPrimitives.WriteUInt16LittleEndian(hatch[8..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(hatch[18..], material);
        BinaryPrimitives.WriteUInt32LittleEndian(hatch[55..], (uint)(vertices.Length + 1));
        for (int i = 0; i < vertices.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(hatch[(75 + 16 * i)..], vertices[i].X);
            BinaryPrimitives.WriteDoubleLittleEndian(hatch[(83 + 16 * i)..], vertices[i].Y);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(hatch[(hatchLength - 6)..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(hatch[(hatchLength - 4)..], curved ? 1u : 0u);
        return section;
    }

    private static Guid AddBeamCutFixture(byte[] pool, List<ObjectDirectoryRecord> entries,
        Guid targetId, Guid contactId, int offset, double y, double elevation, double halfWidth, double roll, byte[] join,
        bool wallTarget = false, bool transverse = false, double? endX = null, int layer = 1, uint material = 2, double startY = -2,
        double? endElevation = null, (double X, double Z)? endNormal = null, double startX = -2, Guid? additionalColumnContact = null,
        double? endY = null, bool concaveSection = false)
    {
        Guid beamClass = new("a5c2d285-5a7c-4a68-9554-9eda9d4556ca");
        Guid beamMemoClass = new("cbab7bc6-e03a-4133-9161-c28ed3368672");
        Guid contactClass = new(wallTarget ? "9eb0036e-2b99-4db1-b043-051fb1817f47" : "f7f60005-35d0-4346-87ab-29e087ac0bbf");
        Guid sectionClass = new("c4191955-87c6-4fb6-b074-5f094fe9f6a6");
        Guid beamMemo = new("d5762ca7-d920-455d-a373-28ba6d47152a");
        Guid beamTarget = new(wallTarget ? "b6d5f795-c750-4a2f-9bb0-e659fb07513a" : "cd0b0837-0b34-4e96-8d66-9fe7d926478c");
        Guid targetBeam = new(wallTarget ? "b7a142a1-2e5f-4b17-9b52-40604fbc3828" : "6346a816-1d7c-4e46-9609-67624e99d0fb");
        Guid sectionRelation = new("63b86c30-a221-421c-84fb-926c694bf611");
        Guid beamId = Guid.NewGuid(), memoId = Guid.NewGuid(), sectionId = Guid.NewGuid();

        double x0 = transverse ? y : startX, yy0 = transverse ? startY : y;
        double x1 = endX ?? (transverse ? y : 2), yy1 = endY ?? (transverse ? 2 : y);
        byte[] placement = HorizontalBeam(beamClass, x0, yy0, x1, yy1, elevation, roll, layer);
        if (endElevation is { } end) BinaryPrimitives.WriteDoubleLittleEndian(placement.AsSpan(386), end);
        if (endNormal is { } normal)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(placement.AsSpan(394), normal.X);
            BinaryPrimitives.WriteDoubleLittleEndian(placement.AsSpan(410), normal.Z);
        }
        byte[] beam = WithGeometryRelations(placement, GeometryRelation(0x11, beamMemo, memoId));
        var memo = WithGeometryRelations(beamMemoClass.ToByteArray(), HostRelation(beamMemo, beamId),
            GeometryRelation(0x11, sectionRelation, sectionId),
            additionalColumnContact is { } extra ? GeometryRelationList(1, beamTarget, contactId, extra) :
            targetId != Guid.Empty ? GeometryRelation(0x11, beamTarget, contactId) : [], join);
        var contact = WithGeometryRelations(contactClass.ToByteArray(), HostRelation(beamTarget, memoId), HostRelation(targetBeam, targetId));
        var section = WithGeometryRelations(RectangularSection(sectionClass, material, halfWidth, -1, 0, concave: concaveSection),
            HostRelation(sectionRelation, memoId));
        Synthetic.Block(PlaBlockMode.Raw, beam, ver: 1, guid: beamId, extra: 1, kind: 9).CopyTo(pool, offset);
        Synthetic.Block(PlaBlockMode.Raw, memo, ver: 1, guid: memoId, extra: 1, kind: 10).CopyTo(pool, offset + 2048);
        ulong start = (ulong)offset / 16;
        entries.AddRange([new(9, start, 128, beamId), new(10, start + 128, 64, memoId)]);
        if (targetId != Guid.Empty)
        {
            Synthetic.Block(PlaBlockMode.Raw, contact, ver: 1, guid: contactId, extra: 1, kind: 11).CopyTo(pool, offset + 3072);
            entries.Add(new(11, start + 192, 64, contactId));
        }
        Synthetic.Block(PlaBlockMode.Raw, section, ver: 1, guid: sectionId, extra: 1, kind: 3).CopyTo(pool, offset + 4096);
        entries.Add(new(3, start + 256, 64, sectionId));
        return memoId;
    }

    private static byte[] HorizontalBeam(Guid beamClass, double x0, double y0, double x1, double y1,
        double elevation, double roll, int layer)
    {
        byte[] beam = Synthetic.Concat(FramedElementId(beamClass, 735, "B"), new byte[254]);
        ElementId("B").CopyTo(beam, 721);
        byte[] planes = [1, 0, 2, 0, 0, 0, 1, 0];
        planes.CopyTo(beam, 304);
        BinaryPrimitives.WriteUInt16LittleEndian(beam.AsSpan(368), 1);
        double length = double.Hypot(x1 - x0, y1 - y0);
        double dx = (x1 - x0) / length, dy = (y1 - y0) / length;
        BinaryPrimitives.WriteDoubleLittleEndian(beam.AsSpan(312), x0);
        BinaryPrimitives.WriteDoubleLittleEndian(beam.AsSpan(320), y0);
        BinaryPrimitives.WriteDoubleLittleEndian(beam.AsSpan(328), elevation);
        BinaryPrimitives.WriteDoubleLittleEndian(beam.AsSpan(336), -dx);
        BinaryPrimitives.WriteDoubleLittleEndian(beam.AsSpan(344), -dy);
        BinaryPrimitives.WriteDoubleLittleEndian(beam.AsSpan(370), x1);
        BinaryPrimitives.WriteDoubleLittleEndian(beam.AsSpan(378), y1);
        BinaryPrimitives.WriteDoubleLittleEndian(beam.AsSpan(386), elevation);
        BinaryPrimitives.WriteDoubleLittleEndian(beam.AsSpan(394), dx);
        BinaryPrimitives.WriteDoubleLittleEndian(beam.AsSpan(402), dy);
        WriteLocation(beam.AsSpan(701), layer, -1);
        var settings = beam.AsSpan(741);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[17..], x0);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[25..], y0);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[33..], x1);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[41..], y1);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[133..], 1);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[238..], 1);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[155..], roll);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[246..], roll);
        return beam;
    }

    private static byte[] RectangularSlab(bool coversColumn, int polygonCase = 0)
    {
        byte[] slab = Synthetic.Concat(FramedElementId(SlabClass, 185, "S"), new byte[575]);
        WriteLocation(slab.AsSpan(163), 1, -1);
        var settings = slab.AsSpan(191);
        BinaryPrimitives.WriteDoubleLittleEndian(settings, 0.5);
        BinaryPrimitives.WriteDoubleLittleEndian(settings[17..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(settings[73..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(settings[83..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(settings[85..], 1);
        WriteRectangularPlan(settings[270..], -1, -1, coversColumn ? 1 : 0, 1, slab: true);
        if (polygonCase is 7 or 8)
        {
            var polygon = settings[270..];
            double notchX = polygonCase == 7 ? 0.75 : 0;
            double notchY = polygonCase == 7 ? 0.75 : -0.05;
            var vertices = new (double X, double Y)[] { (-1, -1), (1, -1), (1, notchY), (notchX, notchY), (notchX, 1), (-1, 1), (-1, -1) };
            BinaryPrimitives.WriteUInt32LittleEndian(polygon[4..], 295);
            BinaryPrimitives.WriteInt32LittleEndian(polygon[43..], vertices.Length);
            for (int i = 0; i < vertices.Length; i++)
            {
                BinaryPrimitives.WriteDoubleLittleEndian(polygon[(71 + i * 16)..], vertices[i].X);
                BinaryPrimitives.WriteDoubleLittleEndian(polygon[(79 + i * 16)..], vertices[i].Y);
            }
            BinaryPrimitives.WriteInt32LittleEndian(polygon[(75 + 16 * vertices.Length)..], vertices.Length);
        }
        return slab;
    }

    private static void WriteRectangularPlan(Span<byte> polygon, double left, double bottom, double right, double top, bool slab)
    {
        new byte[] { slab ? (byte)2 : (byte)0, 0, 2, 1, 247, 0, 0, 0, slab ? (byte)1 : (byte)0, 31, 0 }.CopyTo(polygon);
        double[] envelope = [left, bottom, right, top];
        for (int i = 0; i < envelope.Length; i++)
            BinaryPrimitives.WriteDoubleLittleEndian(polygon[(11 + 8 * i)..], envelope[i]);
        BinaryPrimitives.WriteInt32LittleEndian(polygon[43..], 5);
        BinaryPrimitives.WriteInt32LittleEndian(polygon[51..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(polygon[155..], 5);
        var rectangle = new (double X, double Y)[] { (left, bottom), (right, bottom), (right, top), (left, top), (left, bottom) };
        for (int i = 0; i < rectangle.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(polygon[(71 + 16 * i)..], rectangle[i].X);
            BinaryPrimitives.WriteDoubleLittleEndian(polygon[(79 + 16 * i)..], rectangle[i].Y);
        }
    }

    private static byte[] JunctionAttribute(Guid type, ushort kind, int index, uint priority)
    {
        byte[] attribute = new byte[61 + 45];
        type.TryWriteBytes(attribute);
        new byte[] { 0x83, 0, 3, 0, 0, 0, 1, 0 }.CopyTo(attribute, 47);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(55), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(59), 'A');
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(61), 1);
        BinaryPrimitives.WriteInt32LittleEndian(attribute.AsSpan(63), index);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(61 + (kind == 7 ? 41 : 35)), priority);
        return Synthetic.Block(PlaBlockMode.Raw, attribute, ver: 1, kind: kind);
    }

    private static byte[] WithGeometryRelations(byte[] data, params byte[][] items)
    {
        byte[] header = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(3 + items.Sum(r => r.Length)));
        header[4] = 1;
        return Synthetic.Concat(data, header, Synthetic.Concat(items), [0]);
    }

    private static byte[] GeometryRelation(byte tag, Guid key, Guid target) =>
        Synthetic.Concat([tag], key.ToByteArray(), target.ToByteArray());

    private static byte[] GeometryRelationList(byte tag, Guid key, params Guid[] targets)
    {
        var count = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(count, targets.Length);
        return Synthetic.Concat([tag], key.ToByteArray(), count, Synthetic.Concat(targets.Select(id => id.ToByteArray()).ToArray()));
    }

    private static (int? Layer, string? LayerName, int? Story, string? StoryName) Location(PlaElement element) =>
        (element.LayerIndex, element.LayerName, element.HomeStoryIndex, element.HomeStoryName);

    private static void WriteLocation(Span<byte> record, int layer, short story)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(record, 1);
        BinaryPrimitives.WriteInt32LittleEndian(record[2..], layer);
        BinaryPrimitives.WriteInt16LittleEndian(record[6..], story);
    }

    private static byte[] HostRelation(Guid relation, Guid host) =>
        Synthetic.Concat([0x12], relation.ToByteArray(), host.ToByteArray());

    private static byte[] StoryTable(Guid storyClass)
    {
        byte[] header = new byte[50];
        Assert.True(storyClass.TryWriteBytes(header));
        byte[] frame = [1, 0, 3, 0, 0, 1, 0];
        frame.CopyTo(header, 39);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(46), 3);
        byte[] row = [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0];
        byte[] endOfRow = [0, 0, 1];
        byte[] basement = Synthetic.Concat(row, ElementId("Подвал")[14..], endOfRow);
        BinaryPrimitives.WriteDoubleLittleEndian(basement.AsSpan(2), -4);
        byte[] unnamed = Synthetic.Concat(row, ElementId("")[14..], endOfRow);
        return Synthetic.Concat(header, basement, unnamed, unnamed, [0xFF, 0xFF, 0, 0, 0, 0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsTheFlaggedZeroMaskBodyAtItsDirectoryExtent(bool wrongGuid)
    {
        Guid id = Guid.NewGuid();
        byte[] body = Wall("Wall-1");
        byte[] subgroup = Synthetic.Block(PlaBlockMode.ZeroMask, Synthetic.ZeroMask(body), ver: 1,
            unpacked: (uint)body.Length, guid: wrongGuid ? Guid.NewGuid() : id, extra: 0xFFFFFFFF, kind: 1);
        byte[] decodedPool = new byte[32 * 16];
        subgroup.CopyTo(decodedPool, 2 * 16);
        using var doc = Open(ClassTable((1, WallClass)), ElementDirectory(false, new ObjectDirectoryRecord(1, 2, 8, id)),
            Pool(decodedPool));

        var wall = Assert.Single(doc.ReadElements());
        Assert.Equal(wrongGuid ? new PlaElement(id, "Wall", null, null, null) : new PlaElement(id, "Wall", "Wall-1", 0.25, 5), wall);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public void ObjectsMustBeInTheDatabaseListAndOutsideTheGridList(bool storedFrame, bool truncatedGridList, bool olderLayout)
    {
        Guid owner = Guid.NewGuid(), placed = Guid.NewGuid(), grid = Guid.NewGuid(), orphan = Guid.NewGuid();
        byte[] root = Relationship(DatabaseClass, olderLayout ? 137 : 101,
            new Guid("bd9b270b-0347-4dcf-92cf-d2d872117ba1"), placed, grid);
        byte[] gridList = Relationship(GridStoreClass, 59, new Guid("d940e8f4-4d66-4faa-86a1-6bf94c703228"), grid);
        if (truncatedGridList) BinaryPrimitives.WriteUInt32LittleEndian(gridList.AsSpan(82), 2);

        byte[] decodedPool = new byte[4 * 512];
        if (storedFrame)
        {
            byte[] frame = Synthetic.QuickLzFrame(root, root.Length, 0x46);
            Synthetic.Block(PlaBlockMode.Lz, frame, ver: 1, unpacked: (uint)root.Length, guid: owner, extra: 5, kind: 2)
                .CopyTo(decodedPool, 0);
        }
        else
            Synthetic.Block(PlaBlockMode.Raw, root, ver: 1, guid: owner, extra: 5, kind: 2).CopyTo(decodedPool, 0);

        byte[] body = new byte[olderLayout ? 212 : 338];
        Assert.True(ObjectClass.TryWriteBytes(body));
        byte[] marker = [1, 0, 9, 0, 0x83, 0, 3, 0, 0, 1];
        if (olderLayout) marker[4] = 0x82;
        marker.CopyTo(body, 49);
        Assert.True(ObjectClass.TryWriteBytes(body.AsSpan(59)));
        int layer = 49 + (olderLayout ? 45 : 171);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(layer - 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(layer), 13);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(layer + 4), -2);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(body.Length - 2), 1);
        byte[] plain = Synthetic.Concat(body, ElementId("МЕБ - 001")[14..]);
        byte[] packed = Synthetic.ZeroMask(plain);
        Synthetic.Block(PlaBlockMode.ZeroMask, packed, ver: 1, unpacked: (uint)plain.Length, guid: placed, extra: 5, kind: 1)
            .CopyTo(decodedPool, 512);
        Synthetic.Block(PlaBlockMode.ZeroMask, packed, ver: 1, unpacked: (uint)plain.Length, guid: grid, extra: 5, kind: 1)
            .CopyTo(decodedPool, 1024);
        Synthetic.Block(PlaBlockMode.ZeroMask, packed, ver: 1, unpacked: (uint)plain.Length, guid: orphan, extra: 5, kind: 1)
            .CopyTo(decodedPool, 1536);

        using var doc = Open(ClassTable((1, ObjectClass), (2, DatabaseClass), (3, GridStoreClass)),
            OwnedDirectory(false, owner, 128, new(2, 0, 32, owner), new(1, 32, 32, placed), new(1, 64, 32, grid), new(1, 96, 32, orphan)),
            Synthetic.Block(PlaBlockMode.Raw, gridList, ver: 1, kind: 3), Pool(decodedPool));

        if (truncatedGridList) Assert.Throws<InvalidDataException>(doc.ReadElements);
        else Assert.Equal(new PlaElement(placed, "Object", "МЕБ - 001", null, null) { LayerIndex = 13, HomeStoryIndex = -2 },
            Assert.Single(doc.ReadElements()));
    }

    private static byte[] Relationship(Guid recordClass, int sizeOffset, Guid listKind, params Guid[] members)
    {
        int offset = sizeOffset + 4;
        byte[] data = new byte[offset + 23 + 16 * members.Length];
        Assert.True(recordClass.TryWriteBytes(data));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(35), (uint)(sizeOffset - 39));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(sizeOffset), (uint)(data.Length - offset));
        data[offset] = 1;
        data[offset + 2] = 1;
        Assert.True(listKind.TryWriteBytes(data.AsSpan(offset + 3)));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 19), (uint)members.Length);
        for (int i = 0; i < members.Length; i++) Assert.True(members[i].TryWriteBytes(data.AsSpan(offset + 23 + 16 * i)));
        return data;
    }

    private static byte[] FramedElementId(Guid classGuid, int offset, string elementId)
    {
        var header = new byte[offset];
        byte[] marker = [1, 0, 9, 0, 0x83, 0, 3, 0, 0, 1];
        marker.CopyTo(header, 0);
        Assert.True(classGuid.TryWriteBytes(header.AsSpan(marker.Length)));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(offset - 2), 1);
        return Synthetic.Concat(header, ElementId(elementId)[14..]);
    }

    internal static byte[] WallFile(Guid id) => Synthetic.Fdb(
        ClassTable((1, WallClass)), ElementDirectory(false, new ObjectDirectoryRecord(1, 0, 8, id)),
        Pool(Subgroup(1, id, Wall("Wall-1"))));

    private static PlaDocument Open(params byte[][] parts) => PlaDocument.Open(new MemoryStream(Synthetic.Fdb(parts)));

    private static byte[] ClassTable(params (ushort Number, Guid Class)[] classes)
    {
        var payload = new byte[2 + 16 * (classes.Max(entry => entry.Number) + 1)];
        foreach (var (number, guid) in classes)
            Assert.True(guid.TryWriteBytes(payload.AsSpan(2 + 16 * number)));
        return Synthetic.Block(PlaBlockMode.Raw, payload, ver: 1, kind: 0xFDE9);
    }

    private static byte[] ElementDirectory(bool prefixed, params ObjectDirectoryRecord[] records) =>
        OwnedDirectory(prefixed, Guid.Empty, 32, records);

    private static byte[] OwnedDirectory(bool prefixed, Guid owner, ulong length, params ObjectDirectoryRecord[] records)
    {
        int start = prefixed ? 2 : 0;
        byte[] header = new byte[start + 40];
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(start), length);
        Assert.True(owner.TryWriteBytes(header.AsSpan(start + 8)));
        Assert.True(new Guid("1f9b53da-bb9f-4f97-b5d9-532843923eac").TryWriteBytes(header.AsSpan(start + 24)));
        var data = Synthetic.Concat(header, records.SelectMany(Synthetic.Record).ToArray());
        return Synthetic.Block(PlaBlockMode.Raw, data, ver: 1);
    }

    private static byte[] Subgroup(ushort kind, Guid id, byte[] data) =>
        Synthetic.Block(PlaBlockMode.Raw, data, ver: 1, guid: id, kind: kind);

    private static byte[] Pool(params byte[][] subgroups)
    {
        byte[] data = Synthetic.Concat(subgroups);
        return Synthetic.QuickLzPool(((uint)data.Length, Synthetic.QuickLzFrame(data, data.Length, 0x46)));
    }

    private static byte[] ElementId(string id) =>
        Synthetic.Concat([1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, (byte)id.Length, 0, 0, 0],
            Encoding.Unicode.GetBytes(id));

    private static byte[] Wall(string id)
    {
        byte[] tail = new byte[93];
        BinaryPrimitives.WriteDoubleLittleEndian(tail.AsSpan(16), 3);
        BinaryPrimitives.WriteDoubleLittleEndian(tail.AsSpan(24), 4);
        BinaryPrimitives.WriteDoubleLittleEndian(tail.AsSpan(77), 0.25);
        BinaryPrimitives.WriteDoubleLittleEndian(tail.AsSpan(85), 0.25);
        return Synthetic.Concat(ElementId(id), tail);
    }
}
