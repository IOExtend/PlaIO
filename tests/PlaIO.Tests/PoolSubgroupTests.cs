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
        // and the token stream emits the literals XY, then a match of length 4. The subgroup's own 17-byte header is
        // already part of the window when its stream decodes, so the window ends at 21 + 17 + 2 = 40 and the distance
        // 40 - 13 = 27 reaches "ABCD" of pool 1.
        byte[] tokens = [0x20, (byte)'X', (byte)'Y', 0x00, 0x1B, 0x04];
        byte[] payload = PoolLzPayload(tokens, unpacked: 6);
        byte[] second = Synthetic.Block(PlaBlockMode.Lz, payload, unpacked: 6);
        using var doc = OpenPools(first, second);

        var record = Assert.Single(doc.FindCompressedPools()[1].ReadSubgroups());
        Assert.Equal(PlaBlockMode.Lz, record.Mode);
        Assert.Equal("XYABCD"u8.ToArray(), record.Data);
        Assert.False(record.Partial);
    }

    [Fact]
    public void ALaterLzSubgroupMatchesTheDecodedOutputOfAnEarlierOneExactlyOnce()
    {
        // Pool 1: raw "ABCDEFGH" (13-byte header + 8 payload = 21 bytes of window).
        byte[] first = Synthetic.Block(PlaBlockMode.Raw, "ABCDEFGH"u8.ToArray());
        // Pool 2, subgroup 1: mode 2 emitting the literals WXYZ. Its 17-byte header plus the 4 produced bytes end
        // the window at 21 + 17 + 4 = 42, with WXYZ at 38..41.
        byte[] firstTokens = [0x40, (byte)'W', (byte)'X', (byte)'Y', (byte)'Z'];
        byte[] firstLz = Synthetic.Block(PlaBlockMode.Lz, PoolLzPayload(firstTokens, unpacked: 4), unpacked: 4);
        // Pool 2, subgroup 2: mode 2 matching length 4. The window at its match is 42 bytes of content plus the
        // subgroup's own 17-byte header (the header joins the window before the stream decodes), so the distance
        // 59 - 38 = 21 lands on WXYZ only when the output of subgroup 1 entered the window exactly once. A
        // duplicated contribution shifts the distance onto other bytes, and the match returns wrong data while
        // still reporting a complete decode.
        byte[] secondTokens = [0x00, 0x00, 0x15, 0x04];
        byte[] secondLz = Synthetic.Block(PlaBlockMode.Lz, PoolLzPayload(secondTokens, unpacked: 4), unpacked: 4);
        using var doc = OpenPools(first, Synthetic.Concat(firstLz, secondLz));

        var records = doc.FindCompressedPools()[1].ReadSubgroups();
        Assert.Equal(2, records.Count);
        Assert.Equal("WXYZ"u8.ToArray(), records[0].Data);
        Assert.Equal("WXYZ"u8.ToArray(), records[1].Data);
        Assert.False(records[1].Partial);
    }

    [Fact]
    public void ATruncatedZeroMaskSubgroupContributesItsRawBytesToTheWindow()
    {
        // Pool 1: raw "ABCD" (13 + 4 = 17 bytes of window, "ABCD" at 13..16).
        byte[] first = Synthetic.Block(PlaBlockMode.Raw, "ABCD"u8.ToArray());
        // Pool 2, subgroup 1: a zero-mask subgroup whose single mask byte 0x80 starts a literal that the payload
        // never delivers — the decode reports partial and the window takes the one raw payload byte, not the
        // zero-filled remainder of the declared buffer.
        byte[] truncated = Synthetic.Block(PlaBlockMode.ZeroMask, [0x80], unpacked: 8);
        // Pool 2, subgroup 2: a match of length 4 at distance 52 - 13 = 39 (35 bytes of content plus the
        // subgroup's own 17-byte header), which reaches "ABCD" of pool 1 only when the truncated subgroup
        // contributed its 1 raw byte (17 + 17 + 1 = 35); a zero-filled contribution of 8 bytes shifts the
        // distance onto zeros.
        byte[] tokens = [0x00, 0x00, 0x27, 0x04];
        byte[] match = Synthetic.Block(PlaBlockMode.Lz, PoolLzPayload(tokens, unpacked: 4), unpacked: 4);
        using var doc = OpenPools(first, Synthetic.Concat(truncated, match));

        var records = doc.FindCompressedPools()[1].ReadSubgroups();
        Assert.Equal(2, records.Count);
        Assert.True(records[0].Partial);
        Assert.Equal("ABCD"u8.ToArray(), records[1].Data);
        Assert.False(records[1].Partial);
    }

    [Fact]
    public void APoolThatDoesNotDecodeDoesNotBlockLaterPools()
    {
        // Pool 1: structurally valid framing whose compressed frame fails inside the QuickLZ decode, so the pool is
        // found but does not decode. Pool 2 is a plain raw pool and must still be readable.
        byte[] brokenFrameBody = [0x01, 0x00, 0x00, 0x80, 0xF1, 0xFF]; // control word with a match at an unregistered slot
        byte[] brokenPool = Synthetic.QuickLzPool((8, Synthetic.QuickLzFrame(brokenFrameBody, 8, flags: 0x45)));
        byte[] goodContent = Synthetic.Block(PlaBlockMode.Raw, "sample"u8.ToArray());
        byte[] goodPool = Synthetic.QuickLzPool(
            ((uint)goodContent.Length, Synthetic.QuickLzFrame(goodContent, goodContent.Length, flags: 0x46)));
        using var doc = PlaDocument.Open(new MemoryStream(Synthetic.Fdb(brokenPool, goodPool)));

        var pools = doc.FindCompressedPools();
        Assert.Equal(2, pools.Count);
        Assert.Throws<InvalidDataException>(() => pools[0].ReadSubgroups());
        var record = Assert.Single(pools[1].ReadSubgroups());
        Assert.Equal("sample"u8.ToArray(), record.Data);
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
