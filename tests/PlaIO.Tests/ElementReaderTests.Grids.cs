using System.Buffers.Binary;
using System.Text;
using PlaIO.Model;
using Xunit;

namespace PlaIO.Tests;

public partial class ElementReaderTests
{
    private const string GridFixtureLabel = "Ось-12";

    [Theory]
    [InlineData("verified")]
    [InlineData("shared parameter table")]
    [InlineData("implicit unit scale")]
    [InlineData("mirrored axis")]
    [InlineData("unsupported scale")]
    [InlineData("drawing copy")]
    [InlineData("missing membership")]
    [InlineData("wrong grid store")]
    [InlineData("wrong library family")]
    [InlineData("wrong library owner")]
    [InlineData("wrong parameter owner")]
    [InlineData("curved axis")]
    [InlineData("hidden arc")]
    [InlineData("mirrored arc")]
    [InlineData("full circle")]
    [InlineData("invalid arc angle")]
    [InlineData("invalid arc radius")]
    [InlineData("shortened line")]
    [InlineData("nonfinite segment length")]
    [InlineData("nonfinite length")]
    [InlineData("truncated marker")]
    public void ReadsGridAxesWithReciprocalPlacementAndParameterLinks(string variant)
    {
        var (file, id) = GridAxisFixture(variant);
        using var doc = PlaDocument.Open(new MemoryStream(file));
        bool segmented = variant is "shortened line" or "nonfinite segment length";
        double angle = variant switch { "mirrored arc" => 90, "full circle" => 360, _ => 270 };

        if (variant is "verified" or "shared parameter table" or "implicit unit scale" or "mirrored axis" or "shortened line")
        {
            var axis = Assert.Single(doc.ReadGridAxes());
            Assert.Equal(id, axis.Guid);
            Assert.Equal(GridFixtureLabel, axis.Label);
            Assert.Equal(-2, axis.HomeStoryIndex);
            Assert.Equal(2.5, axis.ElevationToHomeStory);
            Assert.Null(axis.ElevationToProjectZero);
            Assert.Equal(new PlaPlanPoint(-2, 3), axis.Start);
            double endX = variant == "mirrored axis" ? -2 - 6.25 * Math.Sqrt(3) / 2 : -2;
            double endY = variant == "mirrored axis" ? -0.125 : 9.25;
            Assert.Equal(endX, axis.End.X, precision: 12);
            Assert.Equal(endY, axis.End.Y, precision: 12);
            Assert.Null(axis.Arc);
            Assert.Equal(segmented ? 1.25 : (double?)null, axis.FloorPlanSegmentLength);
            Assert.False(axis.IsFloorPlanLineHidden);
        }
        else if (variant is "curved axis" or "mirrored arc" or "hidden arc" or "full circle")
        {
            var axis = Assert.Single(doc.ReadGridAxes());
            Assert.Equal(id, axis.Guid);
            Assert.Equal(GridFixtureLabel, axis.Label);
            Assert.Equal(-2, axis.HomeStoryIndex);
            Assert.Null(axis.FloorPlanSegmentLength);
            Assert.Equal(variant == "hidden arc", axis.IsFloorPlanLineHidden);
            var center = new PlaPlanPoint(-2, 3);
            var arc = Assert.IsType<PlaGridArc>(axis.Arc);
            Assert.Equal(new PlaGridArc(center, 2.75, angle * Math.PI / 180), arc);
            var expectedStart = variant == "mirrored arc" ? new PlaPlanPoint(-4.75, 3) : new PlaPlanPoint(-2, 5.75);
            var expectedEnd = variant switch
            {
                "mirrored arc" => new PlaPlanPoint(-2, 0.25),
                "full circle" => expectedStart,
                _ => new PlaPlanPoint(0.75, 3),
            };
            Assert.Equal(expectedStart.X, axis.Start.X, precision: 12);
            Assert.Equal(expectedStart.Y, axis.Start.Y, precision: 12);
            Assert.Equal(expectedEnd.X, axis.End.X, precision: 12);
            Assert.Equal(expectedEnd.Y, axis.End.Y, precision: 12);

            const double maxError = 0.001;
            var points = Assert.IsAssignableFrom<IReadOnlyList<PlaPlanPoint>>(axis.Tessellate(maxError));
            Assert.Equal(axis.Start, points[0]);
            Assert.Equal(axis.End, points[^1]);
            foreach (var (first, second) in points.Zip(points.Skip(1)))
            {
                double midpointRadius = double.Hypot((first.X + second.X) / 2 - center.X, (first.Y + second.Y) / 2 - center.Y);
                Assert.InRange(arc.Radius - midpointRadius, 0, maxError);
            }
            Assert.Null(axis.Tessellate(double.Epsilon));
            Assert.Throws<ArgumentOutOfRangeException>(() => axis.Tessellate(0));
        }
        else Assert.Empty(doc.ReadGridAxes());
    }

    internal static (byte[] File, Guid Id) GridAxisFixture(string variant, bool withStory = false)
    {
        Guid gridList = new("d940e8f4-4d66-4faa-86a1-6bf94c703228");
        Guid partitionRole = new("79b61203-ae84-4d88-a9c6-20ead705e0bc");
        Guid objectLibraryRole = new("189f453e-ea2e-4e38-afe9-660d1d25afc6");
        Guid libraryClass = new("2bfca33d-bd38-49e3-bf6d-52ac8b850b9f");
        Guid tableClass = new("11db227d-dbe4-45af-88b6-6fddea993f71");
        Guid tableRole = new("76ebf0a4-c2ef-483f-a1a8-28d3adde99a0");
        Guid id = Guid.NewGuid(), store = Guid.NewGuid(), descriptor = Guid.NewGuid(), table = Guid.NewGuid();

        // The model grid has a project partition; the drawing representation does not.
        var placementLinks = new List<byte[]>
        {
            GeometryRelation(0x12, gridList, variant == "wrong grid store" ? Guid.NewGuid() : store),
            GeometryRelation(0x11, objectLibraryRole, descriptor),
        };
        if (variant != "drawing copy") placementLinks.Add(GeometryRelation(0x12, partitionRole, Guid.NewGuid()));

        byte[] objectData = Synthetic.Concat(new byte[49], FramedElementId(ObjectClass, 289, "Grid"));
        ObjectClass.TryWriteBytes(objectData);
        var placement = objectData.AsSpan(49);
        WriteLocation(placement[169..], 3, -2);
        BinaryPrimitives.WriteDoubleLittleEndian(placement[206..], -2);
        BinaryPrimitives.WriteDoubleLittleEndian(placement[214..], 3);
        BinaryPrimitives.WriteDoubleLittleEndian(placement[222..], Math.PI / 2);
        if (variant == "mirrored arc") placement[201] = 1;
        BinaryPrimitives.WriteDoubleLittleEndian(placement[230..], variant == "nonfinite elevation" ? double.NaN : 2.5);
        if (variant != "implicit unit scale")
        {
            BinaryPrimitives.WriteUInt16LittleEndian(placement[204..], 2);
            BinaryPrimitives.WriteDoubleLittleEndian(placement[238..], variant == "unsupported scale" ? 0.75 : 1);
            BinaryPrimitives.WriteDoubleLittleEndian(placement[246..], 1);
        }
        if (variant == "mirrored axis")
        {
            placement[201] = 1;
            BinaryPrimitives.WriteDoubleLittleEndian(placement[222..], Math.PI / 6);
        }

        byte[] descriptorData = new byte[89];
        libraryClass.TryWriteBytes(descriptorData);
        new byte[] { 1, 0, 0x83, 0, 3, 0, 0, 0, 1, 0, 2, 0 }.CopyTo(descriptorData, 39);
        Guid family = variant == "wrong library family" ? Guid.NewGuid() : new("b286e7ff-8856-42d5-b293-6084b1784db2");
        family.TryWriteBytes(descriptorData.AsSpan(51));
        Guid.NewGuid().TryWriteBytes(descriptorData.AsSpan(67));

        byte[] marker = Encoding.Unicode.GetBytes(GridFixtureLabel + "\0\0");
        bool curved = variant is "curved axis" or "mirrored arc" or "hidden arc" or "full circle" or
            "invalid arc angle" or "invalid arc radius" or "excessive samples";
        bool segmented = variant is "shortened line" or "nonfinite segment length";
        const int stringOffset = 8 + 7 * 72;
        int tableSize = stringOffset + marker.Length;
        byte[] tableData = new byte[57 + tableSize + 24];
        tableClass.TryWriteBytes(tableData);
        new byte[] { 1, 0, 8, 0, 0x83, 0, 3, 0, 0, 0, 1, 0, 1, 0 }.CopyTo(tableData, 39);
        BinaryPrimitives.WriteUInt32LittleEndian(tableData.AsSpan(53), (uint)tableSize);
        BinaryPrimitives.WriteUInt32LittleEndian(tableData.AsSpan(57), 7);
        WriteInteger(tableData.AsSpan(65, 72), "AC_Type_i"u8, curved ? 2 : 1);
        int visibility = variant == "hidden arc" ? 3 : segmented ? 2 : 1;
        WriteInteger(tableData.AsSpan(137, 72), "AC_LineVisibility_i"u8, visibility);
        WriteLengthParameter(tableData.AsSpan(209, 72), "AC_Length"u8, variant == "nonfinite length" ? double.PositiveInfinity : 6.25);
        double radius = variant switch { "invalid arc radius" => 0, "excessive samples" => 1e9, _ => 2.75 };
        WriteLengthParameter(tableData.AsSpan(281, 72), "AC_Radius"u8, radius);
        var angleRow = tableData.AsSpan(353, 72);
        double angle = variant switch { "mirrored arc" => 90, "full circle" => 360, "invalid arc angle" => 361, _ => 270 };
        WriteLengthParameter(angleRow, "AC_Angle"u8, angle);
        BinaryPrimitives.WriteUInt16LittleEndian(angleRow[8..], 3);
        WriteLengthParameter(tableData.AsSpan(425, 72), "AC_VisLength"u8,
            variant == "nonfinite segment length" ? double.NaN : 1.25);
        var labelRow = tableData.AsSpan(497, 72);
        BinaryPrimitives.WriteUInt16LittleEndian(labelRow[8..], 12);
        "AC_MarkerText_1"u8.CopyTo(labelRow[32..]);
        BinaryPrimitives.WriteUInt32LittleEndian(labelRow[64..], stringOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(labelRow[68..], (uint)(variant == "truncated marker" ? marker.Length + 2 : marker.Length));
        marker.CopyTo(tableData, 57 + stringOffset);

        byte[] placementPool = new byte[2048], parameterPool = new byte[1024];
        var tableOwner = variant == "shared parameter table" ? GeometryRelationList(2, tableRole, Guid.NewGuid(), descriptor) :
            GeometryRelation(0x12, tableRole, variant == "wrong parameter owner" ? Guid.NewGuid() : descriptor);
        Add(parameterPool, 0, 4, table, tableData, tableOwner);
        Add(placementPool, 0, 1, id, objectData, placementLinks.ToArray());
        Add(placementPool, 1024, 3, descriptor, descriptorData,
            GeometryRelation(0x12, objectLibraryRole, variant == "wrong library owner" ? Guid.NewGuid() : id),
            GeometryRelation(0x11, tableRole, table));

        var gridStore = Relationship(GridStoreClass, 59, gridList, variant == "missing membership" ? Guid.NewGuid() : id);
        var tableDirectory = new byte[40];
        BinaryPrimitives.WriteUInt64LittleEndian(tableDirectory, 64);
        new Guid("34d77468-1d98-45b9-958d-1eea37423cb5").TryWriteBytes(tableDirectory.AsSpan(24));
        Guid storyClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
        var parts = new List<byte[]>
        {
            ClassTable((1, ObjectClass), (2, GridStoreClass), (3, libraryClass), (4, tableClass), (5, storyClass)),
            OwnedDirectory(false, Guid.Empty, 128, new(1, 0, 64, id), new(3, 64, 64, descriptor)),
            Synthetic.Block(PlaBlockMode.Raw,
                Synthetic.Concat(tableDirectory, Synthetic.Record(new(4, 0, 64, table))), ver: 1),
            Synthetic.Block(PlaBlockMode.Raw, gridStore, ver: 1, kind: 2, guid: store),
            Pool(parameterPool),
            Pool(placementPool),
        };
        if (withStory)
        {
            byte[] stories = StoryTable(storyClass);
            BinaryPrimitives.WriteInt16LittleEndian(stories.AsSpan(stories.Length - 6), -2);
            BinaryPrimitives.WriteInt32LittleEndian(stories.AsSpan(stories.Length - 4), -1);
            parts.Add(Synthetic.Block(PlaBlockMode.Raw, stories, ver: 1, kind: 5));
        }
        return (Synthetic.Fdb(parts.ToArray()), id);

        static void Add(byte[] pool, int offset, ushort kind, Guid guid, byte[] data, params byte[][] links)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(35), (uint)(data.Length - 39));
            Synthetic.Block(PlaBlockMode.Raw, WithGeometryRelations(data, links), ver: 1, guid: guid, extra: 1, kind: kind)
                .CopyTo(pool, offset);
        }

        static void WriteInteger(Span<byte> row, ReadOnlySpan<byte> name, int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(row[24..], value);
            name.CopyTo(row[32..]);
            BinaryPrimitives.WriteUInt32LittleEndian(row[68..], sizeof(double));
        }
    }

    [Fact]
    public void RejectsTruncatedGridStoreMembership()
    {
        var gridList = Relationship(GridStoreClass, 59, new Guid("d940e8f4-4d66-4faa-86a1-6bf94c703228"), Guid.NewGuid());
        BinaryPrimitives.WriteUInt32LittleEndian(gridList.AsSpan(82), 2);
        using var doc = Open(ClassTable((2, GridStoreClass)),
            Synthetic.Block(PlaBlockMode.Raw, gridList, ver: 1, kind: 2, guid: Guid.NewGuid()));
        Assert.Throws<InvalidDataException>(doc.ReadGridAxes);
    }
}
