using System.Text.Json;
using PlaIO.Internal;
using Xunit;

namespace PlaIO.Tests;

/// <summary>Checks against the private vectors (PLAIO_TEST_VECTORS). Each test loops over the manifest, so it is skipped as a
/// whole when the folder is not available; failure messages name the vector file read from the manifest.</summary>
public class VectorTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [SkippableFact]
    public void EveryManifestFileMatchesItsSha256()
    {
        var entries = TestVectors.Entries();
        Assert.NotEmpty(entries);
        foreach (var entry in entries) TestVectors.Read(TestVectors.Name(entry));
    }

    [SkippableFact]
    public void HeaderVectorsParseToTheConfirmedFields()
    {
        var kinds = new HashSet<PlaFileKind>();
        foreach (var entry in TestVectors.OfKind("header"))
        {
            string name = TestVectors.Name(entry);
            byte[] bytes = TestVectors.Read(name);
            var kind = PlaFile.DetectKind(bytes);
            Assert.True(kind != PlaFileKind.Unknown, $"{name}: unknown kind");
            kinds.Add(kind);
            if (kind == PlaFileKind.Bimx)
            {
                Assert.True(bytes.AsSpan(4, 8).SequenceEqual(PngSignature), $"{name}: no PNG signature at offset 4");
                continue;
            }
            using var document = PlaDocument.Open(new MemoryStream(bytes));
            Assert.True(document.Header.LayoutVersion == 0x0010, $"{name}: layout version {document.Header.LayoutVersion:x}");
            Assert.True(document.Header.FormatVersion == 0x0002, $"{name}: format version {document.Header.FormatVersion:x}");
            Assert.True(document.Header.Field12 == 0x01010000, $"{name}: field 12 {document.Header.Field12:x}");
            Assert.True(bytes.AsSpan(16, 16).IndexOfAnyExcept((byte)0) < 0, $"{name}: bytes 16..31 are not zero");
            Assert.True(document.Header.Raw.Span.SequenceEqual(bytes), $"{name}: raw header differs from the file");
        }
        Assert.Contains(PlaFileKind.Fdb, kinds);
        Assert.Contains(PlaFileKind.Bimx, kinds);
    }

    [SkippableFact]
    public void BlockVectorsDecodeToTheExpectedPayload()
    {
        var entries = TestVectors.Entries();
        var blocks = entries.Where(e => e.GetProperty("kind").GetString() == "block").ToList();
        Assert.NotEmpty(blocks);
        foreach (var entry in blocks)
        {
            string name = TestVectors.Name(entry);
            byte[] input = TestVectors.Read(name);
            long offset = entry.GetProperty("offset").GetInt64();
            Assert.True(BlockHeaderParser.TryParse(input, offset, offset + input.Length, out var block), $"{name}: header rejected");
            int mode = entry.GetProperty("mode").GetInt32();
            long packed = entry.GetProperty("packed").GetInt64();
            long unpacked = entry.GetProperty("unpacked").GetInt64();
            int payloadStart = entry.GetProperty("payload_start").GetInt32();
            Assert.True(block.Flags == entry.GetProperty("flags").GetInt32(), $"{name}: flags {block.Flags:x}");
            Assert.True(block.Mode == (PlaBlockMode)mode, $"{name}: mode {block.Mode}");
            Assert.True(block.PackedSize == packed, $"{name}: packed {block.PackedSize}");
            Assert.True(block.UnpackedSize == unpacked, $"{name}: unpacked {block.UnpackedSize}");
            Assert.True(block.HeaderLength == payloadStart, $"{name}: header length {block.HeaderLength}");
            Assert.True(SizeClass.Of((ulong)packed) == (block.Flags & 0xF), $"{name}: size class");

            var expectedEntry = entries.Single(e => e.TryGetProperty("of", out var of) && of.GetString() == name);
            byte[] expected = TestVectors.Read(TestVectors.Name(expectedEntry));
            // The public path: the block alone in a synthetic container, indexed and decoded by PlaDocument.
            using (var document = PlaDocument.Open(new MemoryStream(Synthetic.Concat(Synthetic.Header(), input))))
            {
                Assert.True(document.Blocks.Count == 1, $"{name}: {document.Blocks.Count} blocks indexed");
                Assert.True(document.ReadPayload(document.Blocks[0]).AsSpan().SequenceEqual(expected), $"{name}: ReadPayload differs");
            }

            var payload = input.AsSpan(payloadStart, (int)packed);
            if (block.Mode == PlaBlockMode.Raw)
            {
                Assert.True(payload[..(int)unpacked].SequenceEqual(expected), $"{name}: raw payload differs");
                continue;
            }
            var output = new byte[unpacked];
            int consumed = PayloadDecoder.DecodeZeroMask(payload, output);
            Assert.True(output.AsSpan().SequenceEqual(expected), $"{name}: decoded payload differs");
            Assert.True(consumed == entry.GetProperty("consumed").GetInt32(), $"{name}: consumed {consumed}");
        }
    }

    [SkippableFact]
    public void DirectoryVectorParsesToTheExpectedRecords()
    {
        var entry = Assert.Single(TestVectors.OfKind("directory"));
        string name = TestVectors.Name(entry);
        byte[] input = TestVectors.Read(name);
        int count = entry.GetProperty("records").GetInt32();
        // The expected parse sits next to the input; it is hash-checked when the manifest lists it. When it is not listed,
        // every record is still compared with the hash-checked input below and the typed rows are counted against the manifest.
        string expectedName = Path.ChangeExtension(Path.GetFileNameWithoutExtension(name), ".expected.json");
        byte[] expectedBytes = TestVectors.Entries().Any(e => TestVectors.Name(e) == expectedName)
            ? TestVectors.Read(expectedName)
            : File.ReadAllBytes(Path.Combine(TestVectors.Folder(), expectedName));
        using var expectedJson = JsonDocument.Parse(expectedBytes);
        var expected = expectedJson.RootElement.EnumerateArray().ToList();
        Assert.True(expected.Count == count, $"{name}: expected file lists {expected.Count} records");

        var records = DirectoryFinder.ReadRecords(input, 0, count);
        for (int i = 0; i < count; i++)
        {
            var e = expected[i];
            var want = new ObjectDirectoryRecord(e.GetProperty("class_").GetUInt16(), e.GetProperty("pos").GetUInt64(),
                e.GetProperty("count").GetUInt32(), Guid.Parse(e.GetProperty("guid").GetString()!));
            Assert.True(records[i] == want, $"{name}: record {i} is {records[i]}, expected {want}");
        }

        var sorted = records.OrderBy(r => r.Position).ToArray();
        for (int i = 0; i + 1 < sorted.Length; i++)
        {
            Assert.True(sorted[i].Position != sorted[i + 1].Position, $"{name}: repeated pos");
            Assert.True(sorted[i + 1].Position - sorted[i].Position >= sorted[i].Count, $"{name}: overlapping extents");
        }

        var classesByType = expected
            .Select((e, i) => (Type: e.GetProperty("api_type"), records[i].ClassNumber))
            .Where(x => x.Type.ValueKind == JsonValueKind.String)
            .GroupBy(x => x.Type.GetString()!, x => x.ClassNumber);
        Assert.NotEmpty(classesByType);
        int typed = expected.Count(e => e.GetProperty("api_type").ValueKind == JsonValueKind.String);
        Assert.True(typed == entry.GetProperty("api_typed").GetInt32(), $"{name}: expected file has {typed} typed records");
        foreach (var group in classesByType)
            Assert.True(group.Distinct().Count() == 1, $"{name}: a type maps to {group.Distinct().Count()} class numbers");

        var best = DirectoryFinder.BestPhase(input);
        Assert.NotNull(best);
        Assert.Equal(0, best.Value.Phase);
        Assert.Equal(0, best.Value.Score.Overlapping);
        Assert.Equal(0, best.Value.Score.Duplicates);
    }
}
