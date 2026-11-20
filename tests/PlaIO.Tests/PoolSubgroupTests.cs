using PlaIO.Model;
using Xunit;

namespace PlaIO.Tests;

public class PoolSubgroupTests
{
    [Fact]
    public void RawSubgroupUsesThePayloadImmediatelyAfterTheSizes()
    {
        var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        byte[] subgroup = Synthetic.Block(PlaBlockMode.Raw, "sample"u8.ToArray(), ver: 1, kind: 7, guid: guid);
        subgroup[10] |= 4; // In a pool this flag adds no file-level extra field.
        using var doc = OpenPool(subgroup);

        var record = Assert.Single(Assert.Single(doc.FindCompressedPools()).ReadSubgroups());
        Assert.Equal(0, record.Offset);
        Assert.Equal(1, record.Version);
        Assert.Equal(7, record.Kind);
        Assert.Equal(guid, record.ObjectId);
        Assert.Equal("sample"u8.ToArray(), record.Data);
        Assert.False(record.Partial);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, true)]
    public void ReportsTruncatedLiteralsSeparatelyFromValidBufferPadding(byte mask, bool partial)
    {
        byte[] subgroup = Synthetic.Block(PlaBlockMode.ZeroMask, [mask, 9], unpacked: 16);
        using var doc = OpenPool(subgroup);

        var record = Assert.Single(Assert.Single(doc.FindCompressedPools()).ReadSubgroups());
        byte[] expected = new byte[16];
        expected[0] = 9;
        Assert.Equal(expected, record.Data);
        Assert.Equal(partial, record.Partial);
    }

    [Fact]
    public void LzSubgroupReportsPartialWithEmptyData()
    {
        byte[] subgroup = Synthetic.Block(PlaBlockMode.Lz, [1, 2, 3, 4], unpacked: 64);
        using var doc = OpenPool(subgroup);

        var record = Assert.Single(Assert.Single(doc.FindCompressedPools()).ReadSubgroups());
        Assert.Equal(PlaBlockMode.Lz, record.Mode);
        Assert.Empty(record.Data);
        Assert.True(record.Partial);
    }

    [Fact]
    public void LzSubgroupDecodesAgainstAnEarlierPool()
    {
        // Pool 1: one raw subgroup "ABCDEFGH"; its reconstruction in the pool stream is the 13-byte
        // header followed by the payload, so "ABCD" starts at offset 13 of the window.
        byte[] first = Synthetic.Block(PlaBlockMode.Raw, "ABCDEFGH"u8.ToArray());
        // Pool 2: one mode-2 subgroup. Its payload starts with the pool header [u32][0x47][u32 packed][u32 unpacked]
        // and the token stream emits the literals XY, then a match of length 4 at distance 10: the window ends two
        // bytes past the 21-byte reconstruction of pool 1, and 23 - 13 = 10 reaches "ABCD".
        byte[] tokens = [0x20, (byte)'X', (byte)'Y', 0x00, 0x0A, 0x04];
        byte[] payload = PoolLzPayload(tokens, unpacked: 6);
        byte[] second = Synthetic.Block(PlaBlockMode.Lz, payload, unpacked: 6);
        using var doc = OpenPools(first, second);

        var record = Assert.Single(doc.FindCompressedPools()[1].ReadSubgroups());
        Assert.Equal(PlaBlockMode.Lz, record.Mode);
        Assert.Equal("XYABCD"u8.ToArray(), record.Data);
        Assert.False(record.Partial);
    }

    [Fact]
    public void LzSubgroupWithTheBlockStreamHeaderDecodesWithoutAPoolHeader()
    {
        // A mode-2 payload may instead start with the file block's 9-byte stream header, which the LZ decoder
        // recognises itself; here 6 literals stand alone, so nothing is referenced.
        byte[] tokens = [0x60, (byte)'A', (byte)'B', (byte)'C', (byte)'D', (byte)'E', (byte)'F'];
        byte[] payload = new byte[9 + tokens.Length];
        payload[0] = 0x47;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), (uint)payload.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(5), 6);
        tokens.CopyTo(payload, 9);
        byte[] subgroup = Synthetic.Block(PlaBlockMode.Lz, payload, unpacked: 6);
        using var doc = OpenPool(subgroup);

        var record = Assert.Single(Assert.Single(doc.FindCompressedPools()).ReadSubgroups());
        Assert.Equal("ABCDEF"u8.ToArray(), record.Data);
        Assert.False(record.Partial);
    }

    [Fact]
    public void ShortHeaderCandidateDoesNotHideLaterRecord()
    {
        byte[] badHeader = [0x42, 0x4C, 0, 0, 0, 0, 1, 0, 1, 0xA0, 0x80];
        byte[] good = Synthetic.Block(PlaBlockMode.Raw, [42]);
        using var doc = OpenPool(Synthetic.Concat(badHeader, good));

        var record = Assert.Single(Assert.Single(doc.FindCompressedPools()).ReadSubgroups());
        Assert.Equal(badHeader.Length, record.Offset);
        Assert.Equal(new byte[] { 42 }, record.Data);
    }

    [Fact]
    public void ReadSubgroupsAfterDisposeThrows()
    {
        var doc = OpenPool([]);
        var pool = Assert.Single(doc.FindCompressedPools());
        doc.Dispose();

        Assert.Throws<ObjectDisposedException>(pool.ReadSubgroups);
    }

    [SkippableFact]
    public void ReadsSubgroupsFromSavedFiles()
    {
        using var corpus = new SampleCorpus();
        int pools = 0;
        foreach (var sample in corpus.Fdb)
        {
            foreach (var pool in sample.Document.FindCompressedPools())
            {
                if (pool.TotalDecompressed == 0) continue;
                Assert.NotEmpty(pool.ReadSubgroups());
                pools++;
            }
        }
        if (pools == 0) throw TestFiles.Missing("FDB fixture with nonempty QuickLZ pools");
    }

    [SkippableFact]
    public void EveryLzSubgroupOfSavedFilesDecodes()
    {
        using var corpus = new SampleCorpus();
        long mode2 = 0, decoded = 0;
        foreach (var sample in corpus.Fdb)
        {
            foreach (var pool in sample.Document.FindCompressedPools())
            {
                foreach (var record in pool.ReadSubgroups())
                {
                    if (record.Mode != PlaBlockMode.Lz) continue;
                    mode2++;
                    if (!record.Partial && record.Data.Length > 0) decoded++;
                }
            }
        }
        if (mode2 == 0) throw TestFiles.Missing("FDB fixture with mode-2 pool subgroups");
        Assert.True(decoded >= 0.99 * mode2, $"{decoded} of {mode2} mode-2 subgroups decode");
    }

    private static PlaDocument OpenPool(byte[] data) => OpenPools(data);

    private static PlaDocument OpenPools(params byte[][] data)
    {
        byte[][] pools = data.Select(content => Synthetic.QuickLzPool(
            ((uint)content.Length, Synthetic.QuickLzFrame(content, content.Length, flags: 0x46)))).ToArray();
        return PlaDocument.Open(new MemoryStream(Synthetic.Fdb(pools)));
    }

    /// <summary>The 13-byte mode-2 pool header: a u32, the 0x47 marker, the packed payload length (including the
    /// header) and the unpacked size, followed by the token stream.</summary>
    private static byte[] PoolLzPayload(byte[] tokens, int unpacked)
    {
        byte[] payload = new byte[13 + tokens.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(5), (uint)payload.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(9), (uint)unpacked);
        payload[4] = 0x47;
        tokens.CopyTo(payload, 13);
        return payload;
    }
}
